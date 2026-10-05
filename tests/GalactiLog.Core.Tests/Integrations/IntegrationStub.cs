using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GalactiLog.Core.Tests.Integrations;

/// <summary>
/// The in-process HTTP stub both protocol suites run against (task2-clients.md section 3). It
/// listens on a free loopback port the OS assigns, records every request's method, raw URL and
/// body, and answers from a delegate the test supplies. No real host is ever called and no test
/// names a real machine.
/// </summary>
internal sealed class IntegrationStub : IAsyncDisposable
{
    internal sealed record Recorded(string Method, string RawUrl, string Body)
    {
        public string Path => RawUrl.Split('?')[0];

        public string Query => RawUrl.Contains('?') ? RawUrl[(RawUrl.IndexOf('?') + 1)..] : "";
    }

    private readonly HttpListener _listener = new();
    private readonly List<Recorded> _requests = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<Recorded, CancellationToken, Task<(int Status, string Body)>> _respond;
    private readonly Task _loop;

    private IntegrationStub(
        int port, Func<Recorded, CancellationToken, Task<(int, string)>> respond)
    {
        _respond = respond;
        BaseAddress = "http://localhost:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _listener.Prefixes.Add(BaseAddress + "/");
        _listener.Start();
        _loop = AcceptAsync();
    }

    public string BaseAddress { get; }

    public IReadOnlyList<Recorded> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Starts a stub answering 200 with the given body for every request.</summary>
    public static IntegrationStub Ok(string body = "")
        => Start((_, _) => Task.FromResult((200, body)));

    public static IntegrationStub Start(
        Func<Recorded, CancellationToken, Task<(int Status, string Body)>> respond)
    {
        // Port 0 lets the OS pick, and the socket is released before HttpListener takes the same
        // number: the window is a test-local race no shared state can lose.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new IntegrationStub(port, respond);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
            // A listener torn down under an in-flight request is the normal end of the loop.
        }

        _listener.Close();
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            await HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(_stopping.Token);
        var recorded = new Recorded(
            context.Request.HttpMethod, context.Request.RawUrl ?? "", body);
        lock (_requests)
        {
            _requests.Add(recorded);
        }

        try
        {
            var (status, text) = await _respond(recorded, _stopping.Token);

            // A 3xx answer carries its Location in the body slot, which is all a redirect case needs.
            if (status is >= 300 and < 400)
            {
                context.Response.RedirectLocation = text;
                text = "";
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = status;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _stopping.Token);
            context.Response.Close();
        }
        catch (Exception)
        {
            // The client bound its own request and walked away, or the stub is shutting down.
            context.Response.Abort();
        }
    }
}
