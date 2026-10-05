using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Aliases;

// Polish ruling 5: L, R, G, B, SII, Ha, OIII first, then every other filter alphabetically. Each
// case pins one clause of frontend/src/components/FilterBadges.tsx filterSortKey.
public class FilterOrderTests
{
    [Theory]
    [InlineData("L", 0)]
    [InlineData("R", 1)]
    [InlineData("G", 2)]
    [InlineData("B", 3)]
    [InlineData("SII", 4)]
    [InlineData("Ha", 5)]
    [InlineData("OIII", 6)]
    public void Rank_TheSevenStandardNames_InWebOrder(string name, int expected)
    {
        // A failure is one of the seven names ranking out of place, so a broadband list would no
        // longer read L, R, G, B or a narrowband one SII, Ha, OIII.
        Assert.Equal(expected, FilterOrder.Rank(name));
    }

    [Fact]
    public void Rank_AnAliasRanksAsItsCategory()
    {
        // A failure is "Hydrogen alpha" sorting among the unknown filters instead of beside Ha.
        Assert.Equal(FilterOrder.Rank("Ha"), FilterOrder.Rank("Hydrogen alpha"));
        Assert.Equal(FilterOrder.Rank("L"), FilterOrder.Rank("Lum"));
        Assert.Equal(FilterOrder.Rank("SII"), FilterOrder.Rank("S2"));
    }

    [Theory]
    [InlineData("IR")]
    [InlineData("Duoband")]
    [InlineData("")]
    [InlineData(null)]
    public void Rank_AnUnknownName_SortsAfterEveryKnownOne(string? name)
        => Assert.True(FilterOrder.Rank(name) > FilterOrder.Rank("OIII"));

    [Fact]
    public void Comparer_KnownFirstInRankOrder_ThenUnknownAlphabetically()
    {
        // A failure is the list coming back alphabetical (B, Duoband, Ha, ir, L, OIII, SII), or
        // the unknown tail in its input order.
        string?[] names = ["OIII", "ir", "Ha", "B", "Duoband", "L", "SII"];

        Assert.Equal(
            ["L", "B", "SII", "Ha", "OIII", "Duoband", "ir"],
            names.Order(FilterOrder.Comparer(null)));
    }

    // Polish wave 2 ruling 1: a canonical name that folds to no category takes the category of
    // its first configured alias that does, in AliasMap.ExpandFilter order, the same fallback
    // FilterColor.Resolve uses.
    private static AliasMap Map(params (string Canonical, string[] Aliases)[] filters)
        => new(
            filters.ToDictionary(f => f.Canonical, f => new FilterSetting { Aliases = f.Aliases }),
            new EquipmentSettings());

    [Fact]
    public void Rank_ANonstandardCanonical_TakesItsFirstFoldingAliasCategory()
    {
        // A failure is "Chroma Ha 3nm" ranking 100 and sorting after OIII, while its dot is drawn
        // Ha red. "Chroma" is a non-folding alias ahead of "Ha" and must be skipped, not stop the sweep.
        var map = Map(("Chroma Ha 3nm", ["Chroma", "Ha"]), ("OIII", []));

        Assert.Equal(FilterOrder.Rank("Ha"), FilterOrder.Rank("Chroma Ha 3nm", map));
        Assert.Equal(
            ["Chroma Ha 3nm", "OIII"],
            new[] { "OIII", "Chroma Ha 3nm" }.Order(FilterOrder.Comparer(map)));
    }

    [Fact]
    public void Rank_ACanonicalThatFolds_OutranksAnAliasOfAnotherCategory()
    {
        // A failure is the alias sweep running before the canonical name, so "Ha" with a stray
        // "OIII" alias folds to OIII. Pinned at the shared fold as well as through the map, because
        // AliasMap.ExpandFilter leads with the canonical name and would mask the swap.
        var map = Map(("Ha", ["OIII"]));

        Assert.Equal("Ha", FilterCategory.Of("Ha", ["OIII"]));
        Assert.Equal(5, FilterOrder.Rank("Ha", map));
    }

    [Fact]
    public void Rank_NoFoldingAlias_StaysUnknown()
    {
        // A failure is a configured but non-folding alias ("dual") lifting Duoband into the seven.
        var map = Map(("Duoband", ["dual", "L-eNhance"]));

        Assert.Equal(100, FilterOrder.Rank("Duoband", map));
    }

    [Fact]
    public void Rank_WithoutAMap_FoldsTheNameAlone()
    {
        // A failure is a null map throwing, or a name that only an alias could place ranking
        // anywhere but 100.
        Assert.Equal(100, FilterOrder.Rank("Chroma Ha 3nm"));
        Assert.Equal(100, FilterOrder.Rank("Chroma Ha 3nm", null));
        Assert.Equal(5, FilterOrder.Rank("Ha", null));
    }

    [Fact]
    public void Comparer_TiesBreakAlphabetically_IgnoringCase()
    {
        // A failure is two spellings of one unknown filter sorting apart, or the unknown tail
        // ordered by case (DUOBAND before duoband) instead of by letter.
        Assert.Equal(0, FilterOrder.Comparer(null).Compare("duoband", "DUOBAND"));
        Assert.True(FilterOrder.Comparer(null).Compare("Duoband", "ir") < 0);
        Assert.True(FilterOrder.Comparer(null).Compare("ir", "Duoband") > 0);
    }
}
