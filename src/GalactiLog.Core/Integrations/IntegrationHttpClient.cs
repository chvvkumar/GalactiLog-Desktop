namespace GalactiLog.Core.Integrations;

/// <summary>Owns the one <see cref="HttpClient"/> both integration clients share, registered as a
/// singleton so the DI container disposes it with the host instead of leaking it as an
/// unregistered local (fix-wave finding, `AppHost.cs`). Never the bare <see cref="HttpClient"/>
/// type itself: nothing may resolve it and rewrite the shared <c>Timeout</c>.</summary>
public sealed class IntegrationHttpClient : IDisposable
{
    public HttpClient Http { get; }

    /// <summary>Mirrors <c>AliasMapCache.IsDisposed</c>: observable so a test can assert the
    /// container actually disposed this.</summary>
    public bool IsDisposed { get; private set; }

    public IntegrationHttpClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        Http = http;
    }

    public void Dispose()
    {
        IsDisposed = true;
        Http.Dispose();
    }
}
