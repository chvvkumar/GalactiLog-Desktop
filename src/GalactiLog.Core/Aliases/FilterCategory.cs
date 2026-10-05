using System.Collections.Frozen;
using System.Text;

namespace GalactiLog.Core.Aliases;

/// <summary>
/// The web application's canonical filter category fold and its seeded default palette
/// (frontend/src/utils/filterStyles.ts and store/settings.ts::getFilterColorMap), ported so a
/// fresh profile renders Ha red and OIII blue before the user has stored anything (P13 R2).
/// </summary>
/// <remarks>
/// The seven hex literals here are the one documented exception to spec 14.5's "no colour literal
/// outside a theme dictionary", for the same reason <see cref="FilterColor.Fallback"/> is: a
/// filter's tint is user data, not a theme token, and GalactiLog.Core references no UI type.
/// </remarks>
public static class FilterCategory
{
    // The seeded palette, store/settings.ts:88-99 (getFilterColorMap), keyed by the category name
    // Of returns and compared ordinally: "Ha" is a key, "ha" is not.
    private static readonly FrozenDictionary<string, string> Palette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ha"] = "#c44040",
            ["OIII"] = "#3a8fd4",
            ["SII"] = "#d4a43a",
            ["L"] = "#e0e0e0",
            ["R"] = "#e05050",
            ["G"] = "#50b050",
            ["B"] = "#5070e0",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The seeded default palette: category name to colour. A stored colour outranks it
    /// (see <c>AliasMap.FilterColor</c>); this is what a filter nobody has coloured reads as.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Defaults => Palette;

    /// <summary>
    /// The web's <c>canonicalFilterCategory</c>: one of the seven standard category names, or null
    /// for a non-standard filter (IR, Duoband) and for a null or blank name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two details of the web source are load bearing and are ported rather than tidied.
    /// </para>
    /// <para>
    /// SII is tested before Ha, with the source comment "check before Ha to avoid 'sho' false
    /// matches". The seven sets are in fact disjoint, so the order is documentary here, but it is
    /// the order the web reads in and a later addition to either set would make it matter.
    /// </para>
    /// <para>
    /// The Ha set carries the literal "h alpha" although <see cref="Fold"/>, like the web's own
    /// normalizer, has already stripped every space by the time the sets are consulted, so that
    /// entry can never match. It is dead in the web too. It is kept verbatim rather than dropped
    /// or "fixed" into "halpha", which the set already holds, so the two lists stay identical to
    /// a reader comparing them.
    /// </para>
    /// <para>
    /// This deliberately does not call <c>NameNormalizer.Normalize</c>. That method is the alias
    /// map's key normalizer and its rules are its own; the web's fold is a different, documented
    /// normalization and must not silently inherit a change to the other.
    /// </para>
    /// </remarks>
    public static string? Of(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Fold(name) switch
        {
            // Luminance.
            "l" or "lum" or "luminance" or "luminosity" or "clear" => "L",
            // Red.
            "r" or "red" => "R",
            // Green.
            "g" or "green" => "G",
            // Blue.
            "b" or "blue" => "B",
            // Sulfur II, checked before Ha.
            "sii" or "s2" or "s" or "sulfur" or "sulphur" or "sulfurii" or "sulphurii" => "SII",
            // Hydrogen alpha. "h alpha" is the web's dead entry, kept as written.
            "ha" or "h" or "halpha" or "hydrogenalpha" or "hydrogen" or "h alpha" or "656nm" or "656" => "Ha",
            // Oxygen III.
            "oiii" or "o3" or "o" or "oxygen" or "oxygeniii" or "500nm" or "501nm" => "OIII",
            _ => null,
        };
    }

    /// <summary>The category <paramref name="canonical"/> folds to, else that of the first of
    /// <paramref name="aliases"/> that folds, in the order given, else null. The one alias sweep
    /// <see cref="FilterColor.Resolve"/> and <see cref="FilterOrder.Rank"/> share (ruling 1).</summary>
    public static string? Of(string? canonical, IEnumerable<string>? aliases)
    {
        if (Of(canonical) is { } direct)
        {
            return direct;
        }

        foreach (var alias in aliases ?? [])
        {
            if (Of(alias) is { } viaAlias)
            {
                return viaAlias;
            }
        }

        return null;
    }

    /// <summary>The seeded colour for the category <paramref name="name"/> folds to, or null when
    /// it folds to no category. The one call for a caller that holds no colour map.</summary>
    public static string? DefaultFor(string? name)
        => Of(name) is { } category && Palette.TryGetValue(category, out var color) ? color : null;

    // The web's `name.toLowerCase().replace(/[_\-\s]/g, "")`. A character loop rather than a
    // regex, because this runs once per filter per page load and per ledger row. char.IsWhiteSpace
    // covers every whitespace character a filter name can plausibly carry; it is not byte for byte
    // JavaScript's \s, which also matches U+FEFF and does not match U+0085, and neither code point
    // is reachable here (review P3-4).
    private static string Fold(string name)
    {
        var folded = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (character is '_' or '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            folded.Append(char.ToLowerInvariant(character));
        }

        return folded.ToString();
    }
}
