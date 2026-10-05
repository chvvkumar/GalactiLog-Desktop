using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace GalactiLog.Core.Targets;

// Port of sesame.py's `_query_sesame_raw` (design-spec 9.4.4). Fallback only, invoked when
// SimbadClient has already been tried and produced nothing. Always queries resolvers `NV`
// (NED + VizieR); SIMBAD is deliberately excluded from the resolver set here because it was
// already tried by the caller (Task 7's TargetResolver).
//
// IDisposable since Phase 10 Task 8 (TRACKING section 6 item 6), on the same terms as
// SimbadClient: AppHost registers this as a factory, so the container constructs it and disposes
// it with the host.
public sealed class SesameClient : IDisposable
{
    private const string BaseUrl = "https://cds.unistra.fr/cgi-bin/nph-sesame";

    private readonly CatalogHttpClient _http;
    private readonly HttpMessageHandler _handler;
    private readonly bool _ownsHandler;
    private bool _disposed;

    /// <param name="handler">The message handler this client's requests go through.</param>
    /// <param name="ownsHandler">True when this client created the handler and must dispose it;
    /// false when the caller supplied one (the httpHandlerOverride seam in AppHost.Build, which
    /// tests own and reuse across two clients). Disposing a handler a test still holds would make
    /// the second client's first request throw ObjectDisposedException.</param>
    public SesameClient(HttpMessageHandler handler, bool ownsHandler = false)
    {
        _http = new CatalogHttpClient(handler, "SESAME");
        _handler = handler;
        _ownsHandler = ownsHandler;
    }

    // Idempotent. The HttpClient always goes; the handler only when this client made it.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        if (_ownsHandler)
        {
            _handler.Dispose();
        }
    }

    public sealed record SesameResult(string MainId, double Ra, double Dec, string ObjectType, IReadOnlyList<string> Aliases, string ResolverName);

    public SesameResult? Query(string objectName, CancellationToken ct = default)
    {
        // objectName is user-controlled (an OBJECT/FITS-name string, not a fixed literal), so
        // it is percent-encoded before landing in the URL; SESAME's own convention represents
        // an encoded space as '+' rather than '%20', hence the swap after escaping everything
        // else (including '#', '&', and any other reserved character the name might contain).
        var url = $"{BaseUrl}/-ox/NV?{Uri.EscapeDataString(objectName).Replace("%20", "+")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var xml = _http.Send(request, ct);
        return ParseResponse(xml, objectName);
    }

    // Iterates <Resolver> elements in document order (spec 9.4.4: take the first that
    // carries both jradeg and jdedeg). XML is parsed with DTD processing prohibited and no
    // XmlResolver, since the response body is untrusted input over the network.
    //
    // Deliberate deviation from the Python source (coordinator ruling Q12): a resolver whose
    // jradeg/jdedeg text fails to parse as a double is skipped and the next resolver is
    // tried, rather than aborting the whole query the way the Python source's unguarded
    // `float(jradeg)` call does (a bare call inside its single outer try/except, so a parse
    // failure there returns None for the entire response instead of trying the next
    // resolver -- almost certainly an oversight, since SESAME's own XML always emits
    // well-formed numeric text; not worth reproducing a fail-the-whole-query quirk for a case
    // that cannot occur against a real server).
    public static SesameResult? ParseResponse(string xml, string queryName)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            doc = XDocument.Load(xmlReader);
        }
        catch (XmlException)
        {
            return null;
        }

        foreach (var resolver in doc.Descendants("Resolver"))
        {
            var jradeg = resolver.Element("jradeg")?.Value;
            var jdedeg = resolver.Element("jdedeg")?.Value;
            if (jradeg is null || jdedeg is null)
            {
                continue;
            }
            if (!double.TryParse(jradeg, NumberStyles.Float, CultureInfo.InvariantCulture, out var ra) ||
                !double.TryParse(jdedeg, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
            {
                continue;
            }

            // Python's `findtext("oname") or object_name` falls back on an empty string too
            // (an empty element still counts as "no usable oname"), not just a missing one.
            var oname = resolver.Element("oname")?.Value is { Length: > 0 } o ? o : queryName;
            var otype = (resolver.Element("otype")?.Value ?? "").Trim();
            var resolverName = resolver.Attribute("name")?.Value ?? "";
            var aliases = resolver.Elements("alias")
                .Select(e => e.Value)
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();

            return new SesameResult(oname, ra, dec, otype, aliases, resolverName);
        }

        return null;
    }
}
