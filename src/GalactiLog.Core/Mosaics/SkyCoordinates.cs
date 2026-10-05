using System.Globalization;
using GalactiLog.Core.Sessions;

namespace GalactiLog.Core.Mosaics;

/// <summary>A frame's stored sky position and the keyword each axis came from (spec 7.1).</summary>
public readonly record struct FramePosition(double RaDeg, double DecDeg, string RaSource, string DecSource);

/// <summary>
/// The coordinate rule of spec 7.1, port of <c>coordinates._parse_ra</c>, <c>_parse_coord</c> and
/// <c>mosaic_detection._frame_coords</c>. Shared by the extractor and the detection backfill (spec
/// 7.7 step 0) so both store the same position from the same headers.
/// </summary>
public static class SkyCoordinates
{
    /// <summary>A decimal value as degrees, else "h m s" as hours times 15; null otherwise.
    /// Colon-separated values are not parsed, as in the web (spec 7.1, the coordinate rule).</summary>
    public static double? ParseRa(string? value) => DecimalDegrees(value) ?? Sexagesimal(value, signed: false) * 15;

    /// <summary>A decimal value as degrees, else "[+-]d m s" as degrees, negated when the value
    /// begins with '-'; null otherwise (spec 7.1, the coordinate rule).</summary>
    public static double? ParseDec(string? value) => DecimalDegrees(value) ?? Sexagesimal(value, signed: true);

    /// <summary><c>RA</c> falls back to <c>OBJCTRA</c> and <c>DEC</c> to <c>OBJCTDEC</c>
    /// independently; the pair exists only when both values exist and the declination lies in
    /// -90 to 90. The right ascension is normalised into 0 to less than 360 (spec 7.1).</summary>
    public static FramePosition? FramePosition(string? ra, string? dec, string? objctRa, string? objctDec)
    {
        var (raDeg, raSource) = ParseRa(ra) is { } r ? (r, "RA") : (ParseRa(objctRa), "OBJCTRA");
        var (decDeg, decSource) = ParseDec(dec) is { } d ? (d, "DEC") : (ParseDec(objctDec), "OBJCTDEC");
        if (raDeg is not { } a || decDeg is not { } b || b < -90 || b > 90) return null;
        return new FramePosition(AstroNight.Mod360(a), b, raSource, decSource);
    }

    // Non-finite values ("nan", "inf") are rejected where Python's float() would accept them:
    // a NaN position would poison every separation.
    private static double? DecimalDegrees(string? value) =>
        value is not null
        && double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && double.IsFinite(d) ? d : null;

    // Three whitespace-separated numbers; for declination the leading signs are stripped first and
    // the trimmed value's leading '-' negates the result.
    private static double? Sexagesimal(string? value, bool signed)
    {
        if (value is null) return null;
        var s = value.Trim();
        var parts = (signed ? s.TrimStart('+', '-') : s).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var n = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !double.IsFinite(n[i]))
                return null;
        }
        var result = n[0] + n[1] / 60 + n[2] / 3600;
        return signed && s.StartsWith('-') ? -result : result;
    }
}
