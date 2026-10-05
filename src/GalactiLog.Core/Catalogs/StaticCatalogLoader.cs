using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalactiLog.Core.Targets;
using GalactiLog.Core.Text;

namespace GalactiLog.Core.Catalogs;

// Pure CSV-to-record parsing for the seven bundled catalog files (design-spec 9.3, 17.2).
// No EF Core, no DbContext here -- GalactiLog.Core has neither. GalactiLog.Data.Repositories.
// CatalogSeeder is the DB-writing orchestrator that calls into this class.
public static class StaticCatalogLoader
{
    private static readonly Regex NgcIcRe = new(@"^(NGC|IC)\s*0*(\d+)([A-Z]?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MessierRe = new(@"^M\s*0*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // <assembly output dir>/Catalogs -- matches the csproj wiring (App and Data.Tests
    // projects) and design-spec 17.2.
    public static string ResolveCatalogsDirectory() => Path.Combine(AppContext.BaseDirectory, "Catalogs");

    public sealed record OpenNgcRow(
        string Name, string? Type, double? Ra, double? Dec, string? Constellation,
        double? MajorAxis, double? MinorAxis, double? PositionAngle, double? BMag,
        double? VMag, double? SurfaceBrightness, string? CommonNames, string? Messier);

    // Payload is already-serialized JSON text (System.Text.Json), built per-catalog below.
    public sealed record StaticCatalogRow(string CatalogName, string CatalogNumber, string? NgcName, string Payload);

    public static IReadOnlyList<OpenNgcRow> ParseOpenNgc(Stream stream)
    {
        var rows = new List<OpenNgcRow>();
        foreach (var (cells, index) in ReadDataRows(stream, ';'))
        {
            var rawName = Cell(cells, index, "Name")?.Trim();
            if (string.IsNullOrEmpty(rawName)) continue;

            // Zero-padded to three digits at load, because every reader of this column keys
            // on the padded form (OfflineCatalogLookup builds "M 031" to query it). The
            // bundled openngc.csv already pads its M column, so this only guards a future
            // catalog revision that does not (review fix, item 6).
            var messierRaw = Cell(cells, index, "M")?.Trim();
            var messier = string.IsNullOrEmpty(messierRaw) ? null : $"M {messierRaw.PadLeft(3, '0')}";

            rows.Add(new OpenNgcRow(
                Name: NameNormalizer.NormalizeNgcName(rawName),
                Type: BlankToNull(Cell(cells, index, "Type")),
                Ra: OpenNgcCatalog.ParseRaHms(Cell(cells, index, "RA")),
                Dec: OpenNgcCatalog.ParseDecDms(Cell(cells, index, "Dec")),
                Constellation: BlankToNull(Cell(cells, index, "Const")),
                MajorAxis: ParseDouble(Cell(cells, index, "MajAx")),
                MinorAxis: ParseDouble(Cell(cells, index, "MinAx")),
                PositionAngle: ParseDouble(Cell(cells, index, "PosAng")),
                BMag: ParseDouble(Cell(cells, index, "B-Mag")),
                VMag: ParseDouble(Cell(cells, index, "V-Mag")),
                SurfaceBrightness: ParseDouble(Cell(cells, index, "SurfBr")),
                CommonNames: BlankToNull(Cell(cells, index, "Common names")),
                Messier: messier));
        }
        return rows;
    }

    public static IReadOnlyList<StaticCatalogRow> ParseCaldwell(Stream stream)
    {
        var rows = new List<StaticCatalogRow>();
        foreach (var (cells, index) in ReadDataRows(stream, ','))
        {
            var catalogId = Cell(cells, index, "catalog_id")?.Trim();
            if (string.IsNullOrEmpty(catalogId)) continue;

            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["object_type"] = BlankToNull(Cell(cells, index, "object_type")),
                ["constellation"] = BlankToNull(Cell(cells, index, "constellation")),
                ["common_name"] = BlankToNull(Cell(cells, index, "common_name")),
            });

            rows.Add(new StaticCatalogRow("caldwell", catalogId, BlankToNull(Cell(cells, index, "ngc_ic_id")), payload));
        }
        return rows;
    }

    public static IReadOnlyList<StaticCatalogRow> ParseAbell(Stream stream)
    {
        var rows = new List<StaticCatalogRow>();
        foreach (var (cells, index) in ReadDataRows(stream, ','))
        {
            var abellId = Cell(cells, index, "abell_id")?.Trim();
            if (string.IsNullOrEmpty(abellId)) continue;

            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ra"] = ParseDouble(Cell(cells, index, "ra")),
                ["dec"] = ParseDouble(Cell(cells, index, "dec")),
                ["richness_class"] = ParseInt(Cell(cells, index, "richness_class")),
                ["distance_class"] = ParseInt(Cell(cells, index, "distance_class")),
                ["bm_type"] = BlankToNull(Cell(cells, index, "bm_type")),
                ["redshift"] = ParseDouble(Cell(cells, index, "redshift")),
            });

            // abell.csv has no NGC/IC crosswalk column -- NgcName is always null (deliberate,
            // per the brief; Abell membership can never populate through the generic 9.8
            // name-based matcher).
            rows.Add(new StaticCatalogRow("abell", abellId, null, payload));
        }
        return rows;
    }

    public static IReadOnlyList<StaticCatalogRow> ParseArp(Stream stream)
    {
        var rows = new List<StaticCatalogRow>();
        foreach (var (cells, index) in ReadDataRows(stream, ','))
        {
            var arpId = Cell(cells, index, "arp_id")?.Trim();
            if (string.IsNullOrEmpty(arpId)) continue;

            var ngcIcIds = Cell(cells, index, "ngc_ic_ids");
            // Defends against a hypothetical comma-separated value; the bundled file has
            // none today (verified by script), but a future CSV update could add one.
            var firstNgcId = ngcIcIds?
                .Split(',')
                .Select(s => s.Trim())
                .FirstOrDefault(s => s.Length > 0);

            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ngc_ic_ids"] = ngcIcIds,
                ["peculiarity_class"] = BlankToNull(Cell(cells, index, "peculiarity_class")),
                ["peculiarity_description"] = BlankToNull(Cell(cells, index, "peculiarity_description")),
            });

            rows.Add(new StaticCatalogRow("arp", arpId, string.IsNullOrEmpty(firstNgcId) ? null : firstNgcId, payload));
        }
        return rows;
    }

    public static IReadOnlyList<StaticCatalogRow> ParseSac(Stream stream)
    {
        var rows = new List<StaticCatalogRow>();
        foreach (var (cells, index) in ReadDataRows(stream, ','))
        {
            var rawObject = Cell(cells, index, "Object")?.Trim();
            if (string.IsNullOrEmpty(rawObject)) continue;

            // SAC has no separate numbering scheme: the normalized object designation is
            // both the catalog number and the NGC-name match key.
            var normalized = NormalizeSacObjectName(rawObject);

            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["other"] = BlankToNull(Cell(cells, index, "Other")),
                ["type"] = BlankToNull(Cell(cells, index, "Type")),
                ["constellation"] = BlankToNull(Cell(cells, index, "Con")),
                ["ra"] = BlankToNull(Cell(cells, index, "RA")),
                ["dec"] = BlankToNull(Cell(cells, index, "Dec")),
                ["mag"] = ParseDouble(Cell(cells, index, "Mag")),
                ["surface_brightness"] = ParseDouble(Cell(cells, index, "SBrightness")),
                ["size"] = BlankToNull(Cell(cells, index, "Size")),
                ["notes"] = BlankToNull(Cell(cells, index, "Notes")),
            });

            rows.Add(new StaticCatalogRow("sac", normalized, normalized, payload));
        }
        return rows;
    }

    public static IReadOnlyList<StaticCatalogRow> ParseHerschel400(Stream stream)
    {
        var rows = new List<StaticCatalogRow>();
        foreach (var (cells, index) in ReadDataRows(stream, ','))
        {
            var ngcId = Cell(cells, index, "ngc_id")?.Trim();
            if (string.IsNullOrEmpty(ngcId)) continue;

            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["object_type"] = BlankToNull(Cell(cells, index, "object_type")),
                ["constellation"] = BlankToNull(Cell(cells, index, "constellation")),
                ["magnitude"] = ParseDouble(Cell(cells, index, "magnitude")),
            });

            // The web app's per-target membership number is the constant "H400", which
            // cannot be this table's catalog_number because (catalog_name, catalog_number)
            // must be unique per row; ngc_id is the only value in this CSV unique per row.
            rows.Add(new StaticCatalogRow("herschel400", ngcId, ngcId, payload));
        }
        return rows;
    }

    // Port of sac._normalize_sac_name. Not NormalizeNgcName: SAC entries can be M-prefixed,
    // so this stays a private helper local to this file rather than a shared spine (single
    // occurrence).
    private static string NormalizeSacObjectName(string name)
    {
        var trimmed = name.Trim();

        var ngcMatch = NgcIcRe.Match(trimmed);
        if (ngcMatch.Success)
        {
            var prefix = ngcMatch.Groups[1].Value.ToUpperInvariant();
            var number = ngcMatch.Groups[2].Value;
            var suffix = ngcMatch.Groups[3].Value;
            return $"{prefix} {number}{suffix}";
        }

        var messierMatch = MessierRe.Match(trimmed);
        if (messierMatch.Success)
        {
            return $"M {messierMatch.Groups[1].Value}";
        }

        return trimmed;
    }

    // Shared row-reading loop: header line -> column index, then one (cells, index) tuple
    // per remaining non-empty line. Every Parse* method above builds its record from this.
    private static IEnumerable<(IReadOnlyList<string> Cells, Dictionary<string, int> Index)> ReadDataRows(Stream stream, char delimiter)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine();
        if (headerLine is null) yield break;

        var headerCells = CsvLine.Split(headerLine, delimiter);
        var index = new Dictionary<string, int>();
        for (var i = 0; i < headerCells.Count; i++)
        {
            index[headerCells[i]] = i;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            yield return (CsvLine.Split(line, delimiter), index);
        }
    }

    private static string? Cell(IReadOnlyList<string> cells, Dictionary<string, int> index, string columnName)
        => index.TryGetValue(columnName, out var i) && i < cells.Count ? cells[i] : null;

    // A value present as text becomes null only when the whole cell is empty after Trim()
    // (never treat "0" as blank).
    private static string? BlankToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }
}
