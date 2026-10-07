using System.Text.RegularExpressions;

namespace GalactiLog.Core.Mosaics;

/// <summary>One parsed panel token (spec 7.7, the token rule).</summary>
/// <param name="BaseName">The name with the token removed, trimmed.</param>
/// <param name="Keyword">The keyword as <c>OBJECT</c> spells it, or null for a tile match.</param>
/// <param name="Number">The panel number as written: digits, or a row and column pair.</param>
public sealed record PanelMatch(string BaseName, string? Keyword, string Number);

/// <summary>
/// The configurable panel token rule of spec 7.7, port of <c>panel_tokens.py</c>. Pure functions.
/// <see cref="Targets.NameNormalizer.StripPanel"/> applies this rule with the DEFAULT keywords
/// only, never the stored list: resolution must not change when the reader edits
/// <c>general.mosaic_keywords</c> (spec 9.1).
/// </summary>
public static class PanelTokens
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex TilePattern = new(@"^(.+?)[\s_-]+(\d+-\d+)\s*$", Options | RegexOptions.Compiled);
    private static readonly Regex LetterSuffix = new(@"^(.*) \(([a-z])\)$", Options | RegexOptions.Compiled);

    /// <summary>The keyword pattern first, then the tile pattern; null when neither matches
    /// (spec 7.7). Port of <c>match_panel_token_full</c>.</summary>
    public static PanelMatch? Match(string? name, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (keywords.Count > 0)
        {
            var alternation = string.Join('|', keywords.Select(Regex.Escape));
            // The static Regex.Match caches the compiled pattern, so a run over one keyword list
            // builds it once.
            var m = Regex.Match(name, $@"^(.+?)\s*[-_\s]?\s*({alternation})\s*[-_\s]?\s*(\d+)\s*$", Options);
            if (m.Success) return new PanelMatch(m.Groups[1].Value.Trim(), m.Groups[2].Value, m.Groups[3].Value);
        }
        var t = TilePattern.Match(name);
        return t.Success ? new PanelMatch(t.Groups[1].Value.Trim(), null, t.Groups[2].Value) : null;
    }

    /// <summary>The base name, or null when the name carries no token (spec 7.7).</summary>
    public static string? Strip(string? name, IReadOnlyList<string> keywords) => Match(name, keywords)?.BaseName;

    /// <summary>The web's <c>OBJECT</c> pattern stored in <c>mosaic_suggestions</c>
    /// (spec 7.7, the pattern per entry). Port of <c>build_panel_pattern</c>.</summary>
    public static string BuildPattern(string baseName, string? keyword, string number) =>
        keyword is null ? $"%{baseName}%{number}%" : $"%{baseName}%{keyword}%{number}%";

    /// <summary>The panel label, "Panel " and the number (spec 7.7).</summary>
    public static string Label(string number) => $"Panel {number}";

    /// <summary>The number of a "Panel n" label; any other label is returned whole (spec 7.7, the
    /// panel label). Port of <c>panel_number_from_label</c>.</summary>
    public static string NumberFromLabel(string label) =>
        label.StartsWith("Panel ", StringComparison.Ordinal)
            ? label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1]
            : label;

    /// <summary>True when <paramref name="objectName"/> parses to this base, compared case
    /// insensitively, and exactly this number: how a candidate's frames are found (spec 7.7).</summary>
    public static bool ObjectMatchesPanel(string? objectName, string baseName, string number, IReadOnlyList<string> keywords) =>
        Match(objectName, keywords) is { } m
        && string.Equals(m.BaseName, baseName, StringComparison.OrdinalIgnoreCase)
        && m.Number == number;

    /// <summary>The next suffix for "As new panel" (spec 12.17): a label ending in a space and one
    /// letter in parentheses takes the following letter, any other label gains " (b)". A result
    /// already in <paramref name="existingLabels"/>, compared case insensitively, advances again
    /// until it is free.</summary>
    public static string NextLabelSuffix(string label, IEnumerable<string> existingLabels)
    {
        var taken = new HashSet<string>(existingLabels, StringComparer.OrdinalIgnoreCase);
        var next = label;
        do
        {
            var m = LetterSuffix.Match(next);
            next = m.Success ? $"{m.Groups[1].Value} ({(char)(m.Groups[2].Value[0] + 1)})" : $"{next} (b)";
        } while (taken.Contains(next));
        return next;
    }
}
