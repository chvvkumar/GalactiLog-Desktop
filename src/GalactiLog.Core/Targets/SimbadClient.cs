using System.Globalization;

namespace GalactiLog.Core.Targets;

// Port of simbad.py's `_query_simbad_raw` and `_fetch_tap_aliases` (design-spec 9.4.3). Two
// independent calls; the caller (Task 6/7) decides whether and when to make the second one.
//
// Synchronous by design (design-spec 9.4, coordinator ruling Q9): HttpClient.Send keeps the
// whole resolution pipeline (Tasks 5-8) one synchronous call chain, matching every other
// DB/IO-facing type already in this codebase. No retry here -- that belongs to Task 6's cache
// wrapper.
//
// IDisposable since Phase 10 Task 8 (TRACKING section 6 item 6): AppHost registers this as a
// factory, so the container constructs it and disposes it with the host.
public sealed class SimbadClient : IDisposable
{
    private const string ObjectQueryUrl = "https://simbad.cds.unistra.fr/simbad/sim-script";
    private const string AliasQueryUrl = "https://simbad.cds.unistra.fr/simbad/sim-tap/sync";

    private readonly CatalogHttpClient _http;
    private readonly HttpMessageHandler _handler;
    private readonly bool _ownsHandler;
    private bool _disposed;

    /// <param name="handler">The message handler this client's requests go through.</param>
    /// <param name="ownsHandler">True when this client created the handler and must dispose it;
    /// false when the caller supplied one (the httpHandlerOverride seam in AppHost.Build, which
    /// tests own and reuse across two clients). Disposing a handler a test still holds would make
    /// the second client's first request throw ObjectDisposedException.</param>
    public SimbadClient(HttpMessageHandler handler, bool ownsHandler = false)
    {
        _http = new CatalogHttpClient(handler, "SIMBAD");
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

    public sealed record ObjectResult(string MainId, string ObjectType, double? Ra, double? Dec);

    // Call 1 (spec 9.4.3): POST sim-script. Returns null when SIMBAD reports no match, when
    // sanitization empties the name, or when the response has fewer than 4 pipe-delimited
    // fields. Throws NonTransientCatalogException for a non-429 4xx; throws the underlying
    // HttpRequestException/TaskCanceledException for anything else (timeout, 5xx, 429,
    // connection failure) so Task 6's wrapper can retry it.
    public ObjectResult? QueryObject(string objectName, CancellationToken ct = default)
    {
        var sanitized = Sanitize(objectName);
        if (sanitized.Length == 0)
        {
            return null;
        }

        var script = $"format object \"%MAIN_ID|%OTYPELIST|%COO(d;A)|%COO(d;D)\"\nquery id {sanitized}";
        using var request = new HttpRequestMessage(HttpMethod.Post, ObjectQueryUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["script"] = script }),
        };
        var text = _http.Send(request, ct);
        return ParseObjectResponse(text);
    }

    // Pure parse, exposed static so a test can exercise it without a fake HttpMessageHandler.
    public static ObjectResult? ParseObjectResponse(string text)
    {
        if (text.Contains("::error::"))
        {
            return null;
        }

        const string marker = "::data::";
        var dataIndex = text.LastIndexOf(marker, StringComparison.Ordinal);
        var dataSection = (dataIndex >= 0 ? text[(dataIndex + marker.Length)..] : text).Trim();

        var lines = dataSection
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('~') && l.Trim(':').Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var parts = lines[0].Split('|');
        if (parts.Length < 4)
        {
            return null;
        }

        return new ObjectResult(
            MainId: parts[0].Trim(),
            ObjectType: parts[1].Trim(),
            Ra: ParseOptionalDouble(parts[2].Trim()),
            Dec: ParseOptionalDouble(parts[3].Trim()));
    }

    // Call 2 (spec 9.4.3): GET sim-tap/sync. The ADQL single-quote doubling in EscapeAdql is
    // the injection guard -- mandatory, never build this query by plain concatenation. The
    // order matters: call 1 supplies the canonical main_id used here, not the user's input.
    public IReadOnlyList<string> QueryAliases(string mainId, CancellationToken ct = default)
    {
        var query = $"SELECT id FROM ident JOIN basic ON ident.oidref = basic.oid WHERE basic.main_id = '{EscapeAdql(mainId)}'";
        var url = $"{AliasQueryUrl}?request=doQuery&lang=adql&format=tsv&query={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var tsv = _http.Send(request, ct);
        return ParseAliasResponse(tsv);
    }

    public static IReadOnlyList<string> ParseAliasResponse(string tsv)
    {
        var lines = tsv.Trim().Split('\n');
        if (lines.Length <= 1)
        {
            return [];
        }
        // First line is the "id" header; skip it.
        return lines.Skip(1)
            .Select(l => l.Trim().Trim('"'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    public static string EscapeAdql(string value) => value.Replace("'", "''");

    // Strips every character outside printable ASCII (0x20-0x7E), then trims. Tab, CR and LF
    // (0x09/0x0A/0x0D) all fall outside 0x20-0x7E and are removed by the same filter, so no
    // second pass is needed to satisfy the spec's separate "strip tab/CR/LF" instruction.
    private static string Sanitize(string name)
        => new string(name.Where(c => c is >= (char)0x20 and <= (char)0x7E).ToArray()).Trim();

    private static double? ParseOptionalDouble(string s)
        => s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
