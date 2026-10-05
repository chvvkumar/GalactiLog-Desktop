namespace GalactiLog.Core.Targets;

// Port of simbad.py's alias/primary-name curation helpers (design-spec 9.4.5). This file is
// shared across Phase 3 tasks: Task 4 adds only BuildPrimaryName; Task 5 adds CurateAliases
// and ExtractCommonName to this same file (same split pattern as NameNormalizer between
// Tasks 1 and 2).
public static class AliasCurator
{
    // Port of simbad.build_primary_name. Python's `if catalog_id and common_name` treats an
    // empty string as falsy, so an empty (not just null) catalogId/commonName must also be
    // treated as absent here -- `{ Length: > 0 }` rather than `{ }` on each side.
    public static string BuildPrimaryName(string? catalogId, string? commonName) => (catalogId, commonName) switch
    {
        ({ Length: > 0 } c, { Length: > 0 } n) => $"{c} - {n}",
        ({ Length: > 0 } c, _) => c,
        (_, { Length: > 0 } n) => n,
        _ => "Unknown",
    };

    // Port of simbad.curate_aliases (design-spec 9.4.5).
    public static IReadOnlyList<string> CurateAliases(IEnumerable<string> rawAliases, IEnumerable<string>? fitsNames = null)
    {
        var seenUpper = new HashSet<string>();
        var result = new List<string>();

        void Add(string value)
        {
            var key = value.ToUpperInvariant().Replace(" ", "");
            if (seenUpper.Add(key))
            {
                result.Add(value);
            }
        }

        foreach (var raw in rawAliases)
        {
            var n = NameNormalizer.NormalizeDisplay(raw);
            if (n.StartsWith("NAME ", StringComparison.OrdinalIgnoreCase))
            {
                Add(TitleCase(n[5..].Trim()));
                continue;
            }
            if (CatalogPriority.IndexOf(n) is not null)
            {
                Add(n);
            }
            // Everything else (2MASS, USNO, GSC, TYC, SDSS, WISE, GAIA, UCAC, IRAS,
            // bracket-prefixed survey ids, bare coordinate strings) is dropped by simply
            // never being added -- there is no explicit "coordinate ID" regex in this port
            // because CatalogPriority.IndexOf already rejects everything that is not one of
            // the 25 known catalog patterns, which is a strict superset of what needs
            // dropping here.
        }

        if (fitsNames is not null)
        {
            foreach (var fn in fitsNames)
            {
                var n = NameNormalizer.NormalizeDisplay(fn);
                if (n.Length > 0)
                {
                    Add(n);
                }
            }
        }

        return result;
    }

    // Port of simbad.extract_common_name (design-spec 9.4.5).
    public static string? ExtractCommonName(IEnumerable<string> rawAliases, IEnumerable<string>? fitsNames = null)
    {
        foreach (var raw in rawAliases)
        {
            var n = NameNormalizer.NormalizeDisplay(raw);
            if (n.StartsWith("NAME ", StringComparison.OrdinalIgnoreCase))
            {
                return TitleCase(n[5..].Trim());
            }
        }

        if (fitsNames is not null)
        {
            foreach (var fn in fitsNames)
            {
                var n = NameNormalizer.StripPanel(NameNormalizer.NormalizeDisplay(fn)).Trim();
                if (n.Length > 0 && CatalogPriority.IndexOf(n) is null)
                {
                    return n;
                }
            }
        }

        return null;
    }

    // Port of Python's str.title(), NOT CultureInfo.TextInfo.ToTitleCase. This is a
    // deliberate, load-bearing quirk: Python capitalizes the letter after EVERY non-alpha
    // character, including an apostrophe -- "robert's quartet".title() == "Robert'S
    // Quartet" (capital S), not "Robert's Quartet". A SIMBAD NAME alias containing an
    // apostrophe must title-case exactly this way for parity with the reference
    // implementation; do not substitute .NET's culture-aware title casing here.
    private static string TitleCase(string s)
    {
        var chars = s.ToCharArray();
        var previousWasLetter = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = previousWasLetter ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
                previousWasLetter = true;
            }
            else
            {
                previousWasLetter = false;
            }
        }
        return new string(chars);
    }
}
