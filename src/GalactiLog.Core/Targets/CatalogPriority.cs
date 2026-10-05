using System.Text.RegularExpressions;

namespace GalactiLog.Core.Targets;

// Port of simbad.CATALOG_PATTERNS and simbad.extract_catalog_id (design-spec 9.4.1).
public static class CatalogPriority
{
    // Index = priority; a lower index wins. Matched with IsMatch (patterns are already
    // ^-anchored and $-terminated, so this is equivalent to Python's re.match + full-string
    // intent) against NameNormalizer.NormalizeDisplay(name).
    //
    // RegexOptions.Compiled is deliberately omitted here: these run at most a few dozen times
    // per resolved name, not in a hot loop, so JIT compilation overhead would exceed any
    // benefit. Index 4, the Sharpless pattern, needs RegexOptions.IgnoreCase for correctness,
    // not performance -- kept per the source.
    public static readonly Regex[] Patterns =
    [
        new(@"^M\s*\d+$"),                                     // 0  Messier
        new(@"^NGC\s*\d+$"),                                   // 1  NGC
        new(@"^IC\s*\d+[A-Z]?$"),                              // 2  IC
        new(@"^(Caldwell|C)\s+\d+$"),                          // 3  Caldwell
        new(@"^SH?\s*2[\s\-_]+\d+$", RegexOptions.IgnoreCase), // 4  Sharpless (all variants)
        new(@"^(PN\s+A66\s+\d+|Abell\s+\d+)$"),                // 5  Abell PN
        new(@"^Arp\s*\d+$"),                                   // 6  Arp
        new(@"^HCG\s*\d+$"),                                   // 7  HCG
        new(@"^B\s+\d+$"),                                     // 8  Barnard
        new(@"^vdB\s*\d+$"),                                   // 9  van den Bergh
        new(@"^LBN\s+[\d.+\-]+$"),                             // 10 LBN
        new(@"^LDN\s+\d+$"),                                   // 11 LDN
        new(@"^(Cr|Collinder)\s+\d+$"),                        // 12 Collinder
        new(@"^(Mel|Melotte)\s+\d+$"),                         // 13 Melotte
        new(@"^RCW\s+\d+$"),                                   // 14 RCW
        new(@"^Pal\s+\d+$"),                                   // 15 Palomar
        new(@"^(Tr|Trumpler)\s+\d+$"),                         // 16 Trumpler
        new(@"^Stock\s+\d+$"),                                 // 17 Stock
        new(@"^(Ced|Cederblad)\s+\d+$"),                       // 18 Cederblad
        new(@"^Simeis\s+\d+$"),                                // 19 Simeis
        new(@"^DWB\s+\d+$"),                                   // 20 DWB
        new(@"^SNR\s+G[\d.+\-]+$"),                            // 21 SNR G
        new(@"^Cl\s+Berkeley\s+\d+$"),                         // 22 Berkeley
        new(@"^Cl\s+King\s+\d+$"),                             // 23 King
        new(@"^Gum\s+\d+$"),                                   // 24 Gum
    ];

    // Priority index of `name` if it matches a known catalog pattern, else null.
    public static int? IndexOf(string name)
    {
        var n = NameNormalizer.NormalizeDisplay(name);
        for (var i = 0; i < Patterns.Length; i++)
        {
            if (Patterns[i].IsMatch(n)) return i;
        }
        return null;
    }

    // Best catalog ID from `aliases` + `mainId`. Falls back to NormalizeDisplay(mainId) with a
    // leading "NAME " marker stripped when nothing matches a catalog pattern.
    public static string ExtractCatalogId(IEnumerable<string> aliases, string mainId)
    {
        string? bestName = null;
        int? bestPriority = null;

        foreach (var raw in aliases.Append(mainId))
        {
            var n = NameNormalizer.NormalizeDisplay(raw);
            var priority = IndexOf(n);
            if (priority is not null && (bestPriority is null || priority < bestPriority))
            {
                bestPriority = priority;
                bestName = n;
            }
        }

        var fallback = NameNormalizer.NormalizeDisplay(mainId);
        if (fallback.StartsWith("NAME ", StringComparison.OrdinalIgnoreCase))
        {
            fallback = fallback[5..].Trim();
        }
        return bestName ?? fallback;
    }
}
