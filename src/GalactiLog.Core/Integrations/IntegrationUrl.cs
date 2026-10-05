namespace GalactiLog.Core.Integrations;

/// <summary>Design-spec 12.16, "The URL rule". The one place a stored instance URL becomes a base
/// address, enforced at two points and no third: the External Tools tab before a URL is stored
/// (spec 12.7), and each client before it builds a request.</summary>
public static class IntegrationUrl
{
    private const string Http = "http";
    private const string Https = "https";

    /// <summary>True when <paramref name="url"/> parses as an absolute URI whose scheme is
    /// <c>http</c> or <c>https</c> compared ordinally and case-insensitively and whose host is
    /// non-empty. Loopback and the private ranges are accepted, which is spec 12.16's deliberate
    /// departure from the web's server-side-request-forgery guard: this is the reader's own
    /// desktop process and <c>http://localhost:1888</c> is the ordinary NINA setup.</summary>
    /// <param name="url">The stored URL, which may be null or blank.</param>
    /// <param name="baseAddress">On true, the accepted URL with every trailing <c>/</c> trimmed,
    /// so <c>http://host:1888/</c> and <c>http://host:1888</c> build the same request. On false it
    /// is <c>""</c>, never null.</param>
    public static bool TryParse(string? url, out string baseAddress)
    {
        baseAddress = "";
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // Trimmed before both the parse and the slash trim: Uri itself ignores surrounding
        // whitespace, so an untrimmed base address would carry a space into every request path.
        var text = url.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Http, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Https, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        baseAddress = text.TrimEnd('/');
        return true;
    }
}
