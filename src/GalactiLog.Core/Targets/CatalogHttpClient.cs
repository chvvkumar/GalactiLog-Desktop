using System.Text;

namespace GalactiLog.Core.Targets;

// Shared HTTP spine for SimbadClient/SesameClient (design-spec 9.4/9.6): one HttpClient built
// from the caller's handler with the 15 second timeout, the 4xx-vs-5xx/429/timeout exception
// boundary, and a bounded response read. Both clients hold one of these and keep only URL
// building and response parsing to themselves.
//
// IDisposable since Phase 10 Task 8 (TRACKING section 6 item 6): it owns the HttpClient it built,
// so it disposes it. It does NOT dispose the handler. disposeHandler stays false because the
// handler's owner is decided one level up, by SimbadClient/SesameClient's ownsHandler flag: the
// same handler instance can be shared by both clients, and a test that supplied it keeps using it
// after the host is gone. This type is internal, so the change is visible only inside
// GalactiLog.Core.
internal sealed class CatalogHttpClient : IDisposable
{
    // ponytail: a real streaming cap (chunked read, stop past the limit), not just a
    // Content-Length check -- Content-Length is attacker-supplied and cannot be trusted
    // alone to bound memory use against a hostile/misbehaving server.
    private const int MaxResponseBytes = 5 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _source;
    private bool _disposed;

    public CatalogHttpClient(HttpMessageHandler handler, string source)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        _source = source;
    }

    // Idempotent, so a client disposed twice (the host's Dispose plus a caller's using) is not an
    // error. A Send after this throws ObjectDisposedException out of HttpClient, which is the
    // signal HostDisposalTests asserts on.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    // Sends `request` synchronously. Throws NonTransientCatalogException for a non-429 4xx
    // (spec 9.6); throws the underlying HttpRequestException/TaskCanceledException for
    // anything else (timeout, 5xx, 429, connection failure) so Task 6's cache wrapper can
    // retry it. Returns the bounded response body text on success. A cancelled `ct` aborts the
    // send and the body read with OperationCanceledException.
    public string Send(HttpRequestMessage request, CancellationToken ct = default)
    {
        // ResponseHeadersRead so the framework does not buffer an arbitrarily large body
        // itself before this method gets a chance to enforce the cap below.
        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
        ThrowIfNonTransient(response);
        response.EnsureSuccessStatusCode();
        return ReadBoundedContent(response, ct);
    }

    private void ThrowIfNonTransient(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            throw new NonTransientCatalogException($"{_source} returned {code} {response.ReasonPhrase}");
        }
    }

    // A declared Content-Length over the cap is rejected without reading anything (fast
    // path only). Regardless of what Content-Length claims -- or whether it is present at
    // all -- the body is then read in bounded chunks and rejected the moment the running
    // total exceeds the cap, so a chunked hostile response with no Content-Length is capped
    // just the same.
    private string ReadBoundedContent(HttpResponseMessage response, CancellationToken ct)
    {
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is long len && len > MaxResponseBytes)
        {
            throw new NonTransientCatalogException($"{_source} response too large ({len} bytes)");
        }

        using var stream = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new NonTransientCatalogException($"{_source} response too large (exceeds {MaxResponseBytes} bytes)");
            }
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
