using System.Text.RegularExpressions;

namespace GalactiLog.Core.Targets;

// Port of openngc.normalize_ngc_name (design-spec 9.3). This file gains Normalize,
// NormalizeDisplay, NormalizeCatalogId, StripPanel, and Compact in Phase 3 Task 2; only
// NormalizeNgcName exists here because StaticCatalogLoader.ParseOpenNgc (this task) needs
// it at load time.
public static partial class NameNormalizer
{
    [GeneratedRegex(@"^(NGC|IC)\s*0*(\d+)([A-Z]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex NgcIcRegex();

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // A comet designation's year ("C/2023" of "C/2023 P1"): its half-month letter and number
    // read as a "P" panel token, so the strip leaves such a name whole.
    private static readonly Regex CometYear = new(@"^\d*[CPDXI]/\d{4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "NGC0031" -> "NGC 31", "IC0002" -> "IC 2". Any non-matching input is returned trimmed,
    // unchanged.
    public static string NormalizeNgcName(string name)
    {
        var trimmed = name.Trim();
        var match = NgcIcRegex().Match(trimmed);
        if (!match.Success) return trimmed;

        var prefix = match.Groups[1].Value.ToUpperInvariant();
        var number = match.Groups[2].Value;
        var suffix = match.Groups[3].Value;
        return $"{prefix} {number}{suffix}";
    }

    // DB matching key: trim, collapse internal whitespace runs to one space, uppercase.
    public static string Normalize(string name) => CollapseWhitespace(name).ToUpperInvariant();

    // Display-quality: same, case preserved.
    public static string NormalizeDisplay(string name) => CollapseWhitespace(name);

    // Null-safe catalog identity key: null/blank in, null out.
    public static string? NormalizeCatalogId(string? catalogId)
        => string.IsNullOrWhiteSpace(catalogId) ? null : Normalize(catalogId);

    // Removes a trailing mosaic panel token ("M31 Panel 2" -> "M31", "IC 1396 P1" -> "IC 1396"),
    // then trims (spec 9.1). The token is spec 7.7's keyword rule (Mosaics.PanelTokens) over the
    // DEFAULT keywords only, never the stored list, so a custom keyword changes detection and not
    // resolution. Token-bounded: the keyword follows a space, hyphen or underscore, so "HIP 12345"
    // stays whole. A tile token ("2-1") is not stripped.
    public static string StripPanel(string name)
    {
        var trimmed = name.Trim();
        return Mosaics.PanelTokens.Match(trimmed, Settings.GeneralSettings.DefaultPanelKeywords) is { Keyword: not null } m
            && m.BaseName.Length < trimmed.Length
            && (char.IsWhiteSpace(trimmed[m.BaseName.Length]) || trimmed[m.BaseName.Length] is '-' or '_')
            && !CometYear.IsMatch(m.BaseName)
                ? m.BaseName
                : trimmed;
    }

    // openngc_catalog.messier's stored form ("M 057") to the bare number a catalog id or a
    // messier membership row needs ("57"). A stored value without the "M " prefix is returned
    // as-is; an all-zero number becomes "0". The two readers of that column --
    // OfflineCatalogLookup.BuildIdentity and CatalogMembershipMatcher.MatchForTarget -- share
    // this instead of each deriving it (review fix, item 5).
    public static string MessierNumber(string stored)
    {
        var number = stored.StartsWith("M ", StringComparison.Ordinal) ? stored[2..].TrimStart('0') : stored;
        return number.Length == 0 ? "0" : number;
    }

    // Uppercase, then remove spaces, hyphens, underscores: "SH 2-129" -> "SH2129".
    public static string Compact(string s) => s.ToUpperInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

    // Python collapses whitespace on the already-.strip()-ed string, so leading/trailing
    // whitespace of any width collapses to nothing, not to a single space at the edge.
    private static string CollapseWhitespace(string name) => WhitespaceRun.Replace(name.Trim(), " ");
}
