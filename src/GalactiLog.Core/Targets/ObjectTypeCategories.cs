namespace GalactiLog.Core.Targets;

// Port of target_helpers.SIMBAD_CATEGORY_MAP / categorize_object_type (design-spec 9.8).
public static class ObjectTypeCategories
{
    private static readonly Dictionary<string, string> SimbadCategoryMap = new(StringComparer.Ordinal)
    {
        ["HII"] = "Emission Nebula", ["sh"] = "Emission Nebula",
        ["GNe"] = "Reflection Nebula", ["RNe"] = "Reflection Nebula",
        ["DNe"] = "Dark Nebula", ["Cld"] = "Dark Nebula", ["MoC"] = "Dark Nebula",
        ["PN"] = "Planetary Nebula",
        ["SNR"] = "Supernova Remnant",
        ["G"] = "Galaxy", ["H2G"] = "Galaxy", ["GiG"] = "Galaxy", ["GiC"] = "Galaxy",
        ["GiP"] = "Galaxy", ["rG"] = "Galaxy", ["AGN"] = "Galaxy", ["Sy2"] = "Galaxy",
        ["LIN"] = "Galaxy", ["QSO"] = "Galaxy", ["PoG"] = "Galaxy", ["IG"] = "Galaxy",
        ["OpC"] = "Open Cluster", ["Cl*"] = "Open Cluster",
        ["GlC"] = "Globular Cluster",
        ["*"] = "Star", ["**"] = "Star", ["Ae*"] = "Star",

        // Coordinator ruling Q6: OpenNGC's Type column (design-spec 9.3, OfflineCatalogLookup.
        // BuildIdentity) uses a different vocabulary than SIMBAD's OTYPELIST, so these extra
        // rows let an offline-resolved target's raw Type categorize without a translation
        // layer. Verified against the actual distinct Type values in Catalogs/openngc.csv: *,
        // **, *Ass, Cl+N, Dup, EmN, G, GCl, GGroup, GPair, GTrpl, HII, Neb, NonEx, Nova, OCl,
        // Other, PN, RfN, SNR. G/HII/PN/SNR/*/** above already cover the overlap with SIMBAD.
        ["OCl"] = "Open Cluster",
        ["GCl"] = "Globular Cluster",
        ["EmN"] = "Emission Nebula",
        ["RfN"] = "Reflection Nebula",
        // "DrkN" does not appear in the bundled file today; kept for spec fidelity (Q6 names
        // it explicitly as one of the "nebula families" codes this method must accept).
        ["DrkN"] = "Dark Nebula",
        // OpenNGC's "Neb" is a generic/unspecified nebula type, genuinely ambiguous between
        // emission, reflection, and dark. Bucketed with Emission Nebula as the most common
        // real-world case absent a more specific type (ponytail: a judgment call, not a
        // spec-given mapping -- revisit if a better source turns up).
        ["Neb"] = "Emission Nebula",
        // Ast/Dup would fall through to "Other" via the unmapped-code path below anyway;
        // listed explicitly per Q6's literal enumeration ("Ast asterism, Dup duplicate,
        // Other"). "*Ass" is the code the bundled file actually uses for an asterism.
        ["Ast"] = "Other", ["*Ass"] = "Other", ["Dup"] = "Other",
    };

    // The nine SIMBAD-derived display categories of spec 9.8's table, in table order. The single
    // source of the dashboard's Object Type pills (Phase 5 Task 6), so that list cannot drift from
    // the map above.
    public static readonly string[] DisplayCategories =
    [
        "Emission Nebula",
        "Reflection Nebula",
        "Dark Nebula",
        "Planetary Nebula",
        "Supernova Remnant",
        "Galaxy",
        "Open Cluster",
        "Globular Cluster",
        "Star",
    ];

    // Not SIMBAD otypes; carried as the literal object_type of a user-defined target, which is
    // what SolarSystemNames.Classify assigns and what the pass-through below maps back.
    public static readonly string[] SolarSystemCategories = ["Planet", "Moon", "Sun", "Comet", "Asteroid"];

    // Port of target_helpers._CATEGORY_NAME_LOOKUP: a stored object_type that is already a
    // category display name categorizes as that category. Case-insensitive, unlike the code map
    // above, because a category name is a label rather than a catalogue code.
    private static readonly Dictionary<string, string> CategoryNames =
        DisplayCategories.Concat(SolarSystemCategories).Append("Other")
            .ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);

    // Splitting on ',' and taking index 0 is exactly spec 9.8's "equals the code exactly or
    // begins with the code followed by a comma" rule, restated. Frames with no resolved
    // target are "Unresolved" -- a caller-level decision made before ever reaching here (a
    // frame with resolved_target_id == null has no object_type to categorize at all); this
    // method never returns "Unresolved" itself.
    public static string Categorize(string? rawObjectType)
    {
        if (string.IsNullOrEmpty(rawObjectType))
        {
            return "Other";
        }
        var primary = rawObjectType.Split(',')[0].Trim();
        if (SimbadCategoryMap.TryGetValue(primary, out var mapped))
        {
            return mapped;
        }

        // The category-name pass-through, restored deliberately (coordinator ruling Q14). Phase
        // 6's review fix item 9 removed it for having "no caller and no spec basis"; FIXER LIST
        // item 4 is the caller. SolarSystemNames.Classify writes the literal category ("Planet",
        // "Comet") into object_type at target creation, because SIMBAD's OTYPELIST has no code
        // for a solar-system body, and without this branch those five dashboard pills can never
        // match, which is the defect item 4 names. The web source does the same thing through
        // target_helpers._CATEGORY_NAME_LOOKUP. Do not remove it again without removing its
        // producer.
        if (CategoryNames.TryGetValue(primary, out var category))
        {
            return category;
        }

        // Spec 9.8: "Anything mapped by no code is Other."
        return "Other";
    }
}
