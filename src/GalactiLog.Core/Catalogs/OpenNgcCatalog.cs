using System.Globalization;

namespace GalactiLog.Core.Catalogs;

// Port of openngc.py (design-spec 9.3). This task adds RA/Dec parsing only; Task 4 adds
// Lookup/Enrich members to this same class.
public static class OpenNgcCatalog
{
    // Port of openngc.parse_ra_hms: "HH:MM:SS.ss" to decimal degrees. Any parse failure
    // (wrong part count, non-numeric part) returns null, exactly like the Python
    // `except ValueError: return None`.
    public static double? ParseRaHms(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        var parts = val.Trim().Split(':');
        if (parts.Length != 3) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return null;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return null;
        return (h + m / 60 + s / 3600) * 15;
    }

    // Port of openngc.parse_dec_dms: "+DD:MM:SS.s" to decimal degrees, sign from a leading
    // '-'.
    public static double? ParseDecDms(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        var text = val.Trim();
        var sign = text.StartsWith('-') ? -1 : 1;
        text = text.TrimStart('+', '-');
        var parts = text.Split(':');
        if (parts.Length != 3) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return null;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return null;
        return sign * (d + m / 60 + s / 3600);
    }

    // Port of openngc.extract_openngc_common_name (Phase 3 Task 4): first semicolon-separated
    // entry of the "Common names" field, trimmed, or null if the field is null/blank/empty
    // after trimming.
    public static string? ExtractCommonName(string? commonNames)
    {
        if (string.IsNullOrWhiteSpace(commonNames)) return null;
        var first = commonNames.Split(';', 2)[0].Trim();
        return first.Length == 0 ? null : first;
    }
}
