using System.Text.RegularExpressions;

namespace GalactiLog.Core.Targets;

/// <summary>
/// Name-pattern classification into the five solar-system categories of spec 9.8, which SIMBAD's
/// OTYPELIST never produces and the offline catalogues never carry: SIMBAD and OpenNGC are deep-sky
/// catalogues, so "Jupiter" resolves to nothing and its frames group under <c>obj:Jupiter</c>
/// forever (FIXER LIST item 4).
/// </summary>
/// <remarks>
/// Consulted only after every catalogue has been asked and found nothing (see
/// <c>TargetResolver</c>), which is what keeps a name like "47 Tuc" out of the asteroid branch:
/// SIMBAD resolves it, so it never reaches this type. A classifier placed earlier in the pipeline
/// would have to out-guess the catalogues, which is exactly the trade spec 9.7 refuses elsewhere.
/// </remarks>
public static partial class SolarSystemNames
{
    /// <summary>The category, and the display name the created target should carry. Null when the
    /// name matches no pattern.</summary>
    /// <param name="objectName">The raw <c>OBJECT</c> string. Normalized internally with
    /// <see cref="NameNormalizer.Normalize"/> and <see cref="NameNormalizer.StripPanel"/>, so a
    /// mosaic panel suffix does not defeat a match.</param>
    public static (string Category, string PrimaryName)? Classify(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }

        // The panel-stripped, whitespace-collapsed name with its own case, which is what the
        // comet and asteroid rows of spec 9.8's table carry through as the display name, and its
        // upper-cased form, which is what every pattern below matches against (ordinal).
        var collapsed = NameNormalizer.NormalizeDisplay(NameNormalizer.StripPanel(objectName));
        if (collapsed.Length == 0)
        {
            return null;
        }

        var upper = collapsed.ToUpperInvariant();

        // Whole-string lookups, never substring: "HALF MOON NEBULA" is a nebula, not the Moon.
        if (CanonicalNames.TryGetValue(upper, out var canonical))
        {
            return canonical;
        }

        // A named minor planet keeps the incoming name, like every other asteroid form.
        if (NamedAsteroids.Contains(upper))
        {
            return ("Asteroid", collapsed);
        }

        if (CometDesignation().IsMatch(upper))
        {
            return ("Comet", collapsed);
        }

        var minorPlanet = NumberedMinorPlanet().Match(upper);
        if (minorPlanet.Success && !Constellations.Contains(minorPlanet.Groups[1].Value))
        {
            return ("Asteroid", collapsed);
        }

        return null;
    }

    // Sun, Moon and the eight planets, each mapping to its category and to the display name the
    // created target carries. Pluto is a Planet: spec 9.8 has five solar-system categories and no
    // dwarf-planet one, and inventing a sixth would add a pill the dashboard does not render.
    private static readonly Dictionary<string, (string Category, string PrimaryName)> CanonicalNames =
        new(StringComparer.Ordinal)
        {
            ["SUN"] = ("Sun", "Sun"), ["SOL"] = ("Sun", "Sun"), ["THE SUN"] = ("Sun", "Sun"),
            ["MOON"] = ("Moon", "Moon"), ["LUNA"] = ("Moon", "Moon"), ["THE MOON"] = ("Moon", "Moon"),
            ["MERCURY"] = ("Planet", "Mercury"),
            ["VENUS"] = ("Planet", "Venus"),
            ["MARS"] = ("Planet", "Mars"),
            ["JUPITER"] = ("Planet", "Jupiter"),
            ["SATURN"] = ("Planet", "Saturn"),
            ["URANUS"] = ("Planet", "Uranus"),
            ["NEPTUNE"] = ("Planet", "Neptune"),
            ["PLUTO"] = ("Planet", "Pluto"),
        };

    private static readonly HashSet<string> NamedAsteroids =
        new(StringComparer.Ordinal) { "CERES", "PALLAS", "JUNO", "VESTA" };

    // The 88 IAU constellation abbreviations, upper-cased. Not astronomy for its own sake: a
    // Flamsteed or variable-star designation has exactly the shape of a numbered minor planet
    // ("47 Tuc", "30 Dor", "61 Cyg"), and those are deep-sky targets people image. They normally
    // resolve online and never reach this type at all; this set is the second guard, because
    // misfiling one as an asteroid creates a user_defined target that suppresses catalog
    // enrichment for good (spec 5.3).
    private static readonly HashSet<string> Constellations = new(StringComparer.Ordinal)
    {
        "AND", "ANT", "APS", "AQR", "AQL", "ARA", "ARI", "AUR", "BOO", "CAE", "CAM", "CNC",
        "CVN", "CMA", "CMI", "CAP", "CAR", "CAS", "CEN", "CEP", "CET", "CHA", "CIR", "COL",
        "COM", "CRA", "CRB", "CRV", "CRT", "CRU", "CYG", "DEL", "DOR", "DRA", "EQU", "ERI",
        "FOR", "GEM", "GRU", "HER", "HOR", "HYA", "HYI", "IND", "LAC", "LEO", "LMI", "LEP",
        "LIB", "LUP", "LYN", "LYR", "MEN", "MIC", "MON", "MUS", "NOR", "OCT", "OPH", "ORI",
        "PAV", "PEG", "PER", "PHE", "PIC", "PSA", "PSC", "PUP", "PYX", "RET", "SGE", "SGR",
        "SCO", "SCL", "SCT", "SER", "SEX", "TAU", "TEL", "TRA", "TRI", "TUC", "UMA", "UMI",
        "VEL", "VIR", "VOL", "VUL",
    };

    // A comet designation, anchored at the start so "NGC 1P" is not a comet: the IAU orbit-type
    // prefixes C/, P/, D/, X/ and I/, with or without a leading periodic number ("1P/Halley",
    // "73P/Schwassmann"), plus the bare numbered periodic form ("73P").
    [GeneratedRegex(@"^(?:\d*[CPDXI]/|\d+P$)", RegexOptions.CultureInvariant)]
    private static partial Regex CometDesignation();

    // A numbered minor planet: the number first, optionally parenthesised, then one word of at
    // least two letters ("4 Vesta", "(4) Vesta", "243 Ida"). The number-first rule is what keeps
    // every catalogue designation out ("M 31", "NGC 7331", "Abell 21"), and anchoring the end at
    // one word keeps a described name out ("6 Panel Mosaic").
    [GeneratedRegex(@"^\(?\d+\)?\s+([A-Z]{2,})$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedMinorPlanet();
}
