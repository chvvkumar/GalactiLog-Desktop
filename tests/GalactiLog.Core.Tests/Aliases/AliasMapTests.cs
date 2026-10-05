using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Aliases;

public class AliasMapTests
{
    private static AliasMap BuildMap(
        Dictionary<string, FilterSetting>? filters = null,
        EquipmentSettings? equipment = null)
        => new(filters ?? new Dictionary<string, FilterSetting>(), equipment ?? new EquipmentSettings());

    [Fact]
    public void ExpandFilter_CanonicalOiii_IncludesEveryConfiguredAlias()
    {
        // "Oiii" is case-insensitively identical to the canonical "OIII" and collapses into
        // it per the dedup rule (see ExpandFilter_DeduplicatesCaseInsensitively below); the
        // spec 12.2 "matches stored Oiii" guarantee still holds because CanonicalFilter and
        // the SQL-side lower() comparison (Task 2) both fold on normalized/lowercased form,
        // not on this list's exact casing.
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new FilterSetting { Aliases = ["Oiii", "O3"] },
        });

        var expansion = map.ExpandFilter("OIII");

        Assert.Equal(new[] { "OIII", "O3" }, expansion);
        Assert.Equal("OIII", map.CanonicalFilter("Oiii"));
        Assert.Equal("OIII", map.CanonicalFilter("O3"));
    }

    [Fact]
    public void ExpandFilter_UnconfiguredName_ExpandsToItselfOnly()
    {
        var map = BuildMap();

        var expansion = map.ExpandFilter("Ha");

        Assert.Equal(new[] { "Ha" }, expansion);
    }

    [Fact]
    public void ExpandFilter_DeduplicatesCaseInsensitively()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new FilterSetting { Aliases = ["oiii", "O3"] },
        });

        var expansion = map.ExpandFilter("OIII");

        Assert.Equal(new[] { "OIII", "O3" }, expansion);
    }

    [Fact]
    public void CanonicalFilter_RawAlias_FoldsToCanonical()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new FilterSetting { Aliases = ["Oiii", "O3"] },
        });

        Assert.Equal("OIII", map.CanonicalFilter("Oiii"));
    }

    [Fact]
    public void CanonicalFilter_UnknownRaw_ReturnsItself()
    {
        var map = BuildMap();

        Assert.Equal("Luminance", map.CanonicalFilter("Luminance"));
    }

    [Fact]
    public void CanonicalFilter_NullOrWhitespace_ReturnsNull()
    {
        var map = BuildMap();

        Assert.Null(map.CanonicalFilter(null));
        Assert.Null(map.CanonicalFilter("   "));
    }

    [Fact]
    public void CanonicalFilter_IgnoresCaseAndSurroundingWhitespace()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new FilterSetting { Aliases = ["Oiii", "O3"] },
        });

        Assert.Equal("OIII", map.CanonicalFilter("  o3 "));
    }

    [Fact]
    public void CanonicalCamera_AndCanonicalTelescope_UseTheirOwnNamespaces()
    {
        var equipment = new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new EquipmentItemSettings { Aliases = ["Shared"] },
            },
            Telescopes = new Dictionary<string, EquipmentItemSettings>
            {
                ["RC8"] = new EquipmentItemSettings { Aliases = [] },
            },
        };
        var map = BuildMap(equipment: equipment);

        Assert.Equal("ASI2600MM", map.CanonicalCamera("Shared"));
        Assert.Equal("Shared", map.CanonicalTelescope("Shared"));
    }

    // P13 R2a re-pointed this case. "Ha" is no longer an unconfigured grey: it folds to the
    // seeded palette. A name with no category is what the terminal grey is now reached by.
    [Fact]
    public void FilterColor_Unconfigured_ReturnsDefaultGrey()
    {
        var map = BuildMap();

        Assert.Equal("#808080", map.FilterColor("Duoband"));
    }

    [Fact]
    public void FilterColor_Configured_ReturnsConfiguredValue()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#ff0000" },
        });

        Assert.Equal("#ff0000", map.FilterColor("Ha"));
    }

    // ---- P13 R2a: the four-step resolution order ----------------------------------------------
    //
    // One case per step of AliasMap.FilterColor's documented order, named so a reader sees which
    // step each pins:
    //   1 stored colour for this canonical name
    //   2 seeded palette entry for the category the canonical name folds to
    //   3 seeded palette entry for the category the first configured alias folds to
    //   4 spec 5.8.4's grey

    [Fact]
    public void FilterColor_AStoredColour_Wins()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#ff4d4d" },
        });

        Assert.Equal("#ff4d4d", map.FilterColor("Ha"));
    }

    [Fact]
    public void FilterColor_NoStoredColour_TakesTheCategoryDefault()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Aliases = ["H-alpha"] },
            ["OIII"] = new FilterSetting(),
        });

        Assert.Equal("#c44040", map.FilterColor("Ha"));
        Assert.Equal("#3a8fd4", map.FilterColor("OIII"));
    }

    [Fact]
    public void FilterColor_NoStoredColourAndNoCategory_TakesTheGrey()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Duoband"] = new FilterSetting(),
        });

        Assert.Equal("#808080", map.FilterColor("Duoband"));
    }

    // Review P3-5, coordinator ruling: a stored value equal to the grey fallback carries no
    // intent, because every build before Phase 13 wrote that exact string into every group it
    // created without the user asking. It falls through to the fold, so a profile that once saved
    // the Filters tab still gets the seeded palette.
    [Fact]
    public void FilterColor_AStoredGreyFallback_IsTreatedAsUnstored()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#808080" },
        });

        Assert.Equal("#c44040", map.FilterColor("Ha"));
    }

    // The other side of that rule: every value but the grey fallback is kept, so a user who wants
    // a filter drawn grey picks any grey but #808080.
    [Fact]
    public void FilterColor_AStoredGreyThatIsNotTheFallback_IsKept()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#7f7f7f" },
        });

        Assert.Equal("#7f7f7f", map.FilterColor("Ha"));
    }

    // Q1: the port's terminal grey stays #808080 and never the web's #666666.
    [Fact]
    public void FilterColor_AStoredGreyFallbackOnANameWithNoCategory_StillReadsTheGrey()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Duoband"] = new FilterSetting { Color = "#808080" },
        });

        Assert.Equal("#808080", map.FilterColor("Duoband"));
    }

    [Fact]
    public void FilterColor_ABlankStoredColour_IsTreatedAsUnset()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "   " },
        });

        Assert.Equal("#c44040", map.FilterColor("Ha"));
    }

    // Q3: the web's raw-name step has no port analogue, because FilterColor is only ever called
    // with an already-folded canonical name. What covers the same ground is the alias sweep: a
    // group named "Hydrogen Alpha Narrowband" whose alias list holds "Ha" still reads red.
    [Fact]
    public void FilterColor_AnAliasCarriesTheCategory_WhenTheCanonicalDoesNot()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Duoband"] = new FilterSetting { Aliases = ["IR", "OIII"] },
        });

        Assert.Equal("#3a8fd4", map.FilterColor("Duoband"));
    }

    [Fact]
    public void FilterColor_AnUnconfiguredCanonical_StillFolds()
    {
        var map = BuildMap();

        Assert.Equal("#c44040", map.FilterColor("H-alpha"));
        Assert.Equal("#e0e0e0", map.FilterColor("Lum"));
        Assert.Equal("#808080", map.FilterColor("IR"));
    }

    [Fact]
    public void ConfiguredFilters_PreservesConfiguredOrder()
    {
        var map = BuildMap(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting(),
            ["OIII"] = new FilterSetting(),
            ["SII"] = new FilterSetting(),
        });

        Assert.Equal(new[] { "Ha", "OIII", "SII" }, map.ConfiguredFilters);
    }

    [Fact]
    public void Constructor_EmptySettings_ProducesEmptyConfiguredListsAndIdentityFolds()
    {
        var map = BuildMap();

        Assert.Empty(map.ConfiguredFilters);
        Assert.Empty(map.ConfiguredCameras);
        Assert.Empty(map.ConfiguredTelescopes);
        Assert.Equal("Ha", map.CanonicalFilter("Ha"));
        Assert.Equal("ASI2600MM", map.CanonicalCamera("ASI2600MM"));
        Assert.Equal("RC8", map.CanonicalTelescope("RC8"));
    }

    // Review fix item 1: two canonical keys differing only by case (or whitespace) normalize
    // to the same lookup key. The constructor must not throw, and the first-configured entry
    // must win consistently across ConfiguredFilters and FilterColor.
    [Fact]
    public void Constructor_TwoCanonicalsDifferByCaseOnly_ConstructsWithoutThrowingAndFirstWins()
    {
        var filters = new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#111111" },
            ["ha"] = new FilterSetting { Color = "#222222" },
        };

        var map = BuildMap(filters);

        Assert.Equal(new[] { "Ha" }, map.ConfiguredFilters);
        Assert.Equal("#111111", map.FilterColor("Ha"));
    }

    // Review fix item 2: a null element in an aliases array must not throw in Normalize.
    [Fact]
    public void ExpandFilter_NullAlias_IsSkipped()
    {
        var filters = new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Aliases = [null!, "H-alpha"] },
        };

        var map = BuildMap(filters);

        Assert.Equal(new[] { "Ha", "H-alpha" }, map.ExpandFilter("Ha"));
    }

    // Review fix item 2: a whitespace-only alias must not land in the expansion list, since a
    // blank filter_used is reserved for the synthesized "Unknown" bucket (Q14).
    [Fact]
    public void ExpandFilter_WhitespaceOnlyAlias_IsSkipped()
    {
        var filters = new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Aliases = ["  ", "H-alpha"] },
        };

        var map = BuildMap(filters);

        Assert.Equal(new[] { "Ha", "H-alpha" }, map.ExpandFilter("Ha"));
    }

    // Review fix item 2: a blank canonical key must not throw and must not appear configured.
    [Fact]
    public void Constructor_BlankCanonical_IsSkipped()
    {
        var filters = new Dictionary<string, FilterSetting>
        {
            ["   "] = new FilterSetting(),
            ["Ha"] = new FilterSetting(),
        };

        var map = BuildMap(filters);

        Assert.Equal(new[] { "Ha" }, map.ConfiguredFilters);
    }
}
