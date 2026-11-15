using System.Diagnostics;
using System.Runtime.ExceptionServices;
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

    // Mirror fallback (port of the container's upstream.request_with_fallback): sends the same
    // request to each of `urls` in order and returns the first good response. `build` makes a
    // fresh request per mirror, since an HttpRequestMessage cannot be sent twice. When every
    // mirror fails, the last transient failure is thrown if there was one: one mirror answering
    // "bad request" proves nothing while another is down, so the combined failure is
    // non-transient only when every attempt was. A cancelled `ct` stops at once, without trying
    // the next mirror.
    public string Send(IReadOnlyList<string> urls, Func<string, HttpRequestMessage> build, CancellationToken ct = default)
    {
        Exception? transient = null;
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var request = build(url);
                return Send(request, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException or NonTransientCatalogException)
            {
                last = ex;
                if (ex is not NonTransientCatalogException)
                {
                    transient = ex;
                }
            }
        }

        ExceptionDispatchInfo.Throw(transient ?? last ?? new ArgumentException("No url given.", nameof(urls)));
        throw new UnreachableException();
    }

    // Sends `request` synchronously. Throws NonTransientCatalogException for a status that
    // rejects this one request (spec 9.6); throws the underlying
    // HttpRequestException/TaskCanceledException for anything else (timeout, 5xx, 429, 404,
    // connection failure) so Task 6's cache wrapper can retry it. Returns the bounded response
    // body text on success. A cancelled `ct` aborts the send and the body read with
    // OperationCanceledException.
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
        // Only the statuses that say "this particular request was rejected". Every other 4xx
        // (401, 403, 404, 410, ...) comes from a fixed query endpoint with the object in the
        // parameters, so it means the endpoint itself is missing or blocked: the CDS outage page
        // answers 404 on every path. Those fall through to EnsureSuccessStatusCode and take the
        // transient path, so an outage trips the scan's circuit breaker instead of failing the
        // scan, and is never negative-cached.
        var code = (int)response.StatusCode;
        if (code is 400 or 413 or 414 or 422)
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
