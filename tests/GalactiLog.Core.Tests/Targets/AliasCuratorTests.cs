using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

public class AliasCuratorTests
{
    [Fact]
    public void CurateAliases_KeepsNamePrefixedAliasTitleCased()
    {
        var result = AliasCurator.CurateAliases(["NAME Markarian Chain"]);

        Assert.Equal(["Markarian Chain"], result);
    }

    [Fact]
    public void CurateAliases_TitleCaseMatchesPythonQuirkOnApostrophe()
    {
        var result = AliasCurator.CurateAliases(["NAME robert's quartet"]);

        // Python's str.title() capitalizes the letter after every non-alpha character,
        // including an apostrophe: "robert's quartet".title() == "Robert'S Quartet".
        Assert.Equal(["Robert'S Quartet"], result);
    }

    [Fact]
    public void CurateAliases_KeepsCatalogPatternAlias()
    {
        // Catalog patterns other than Sharpless are matched case-sensitively (design-spec
        // 9.4.1, verbatim from simbad.CATALOG_PATTERNS): real SIMBAD/FITS designations are
        // already correctly cased, so this uses "NGC 7000" rather than a lowercase variant.
        var result = AliasCurator.CurateAliases(["NGC 7000"]);

        Assert.Equal(["NGC 7000"], result);
    }

    [Theory]
    [InlineData("2MASS J00424433+4116074")]
    [InlineData("[BFS98] 1")]
    [InlineData("TYC 2880-1234-1")]
    public void CurateAliases_DropsSurveyAndCoordinateIds(string alias)
    {
        var result = AliasCurator.CurateAliases([alias]);

        Assert.Empty(result);
    }

    [Fact]
    public void CurateAliases_AppendsFitsNamesAfterSimbadAliases()
    {
        var result = AliasCurator.CurateAliases(["NAME Test Object"], ["Custom Fits Name"]);

        Assert.Equal(["Test Object", "Custom Fits Name"], result);
    }

    [Fact]
    public void CurateAliases_DeduplicatesCaseAndSpaceInsensitively()
    {
        // The FITS-name path (unlike the raw-alias path) adds a normalized name unconditionally,
        // with no catalog-pattern check, so "ngc 7000" here reaches the dedup key regardless of
        // case -- this is what actually exercises the dedup key rather than being dropped by a
        // case-sensitive catalog-pattern mismatch beforehand.
        var result = AliasCurator.CurateAliases(["NGC 7000"], ["ngc 7000"]);

        Assert.Equal(["NGC 7000"], result);
    }

    [Fact]
    public void ExtractCommonName_PrefersNameAliasOverFitsName()
    {
        var result = AliasCurator.ExtractCommonName(["NAME North America Nebula"], ["Custom Fits Name"]);

        Assert.Equal("North America Nebula", result);
    }

    [Fact]
    public void ExtractCommonName_FallsBackToNonCatalogFitsName()
    {
        var result = AliasCurator.ExtractCommonName([], ["Horsehead Nebula Panel 2"]);

        Assert.Equal("Horsehead Nebula", result);
    }

    [Fact]
    public void ExtractCommonName_ReturnsNullWhenEverythingIsACatalogId()
    {
        var result = AliasCurator.ExtractCommonName([], ["NGC 7000"]);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("NGC 7000", "North America Nebula", "NGC 7000 - North America Nebula")]
    [InlineData("NGC 7000", null, "NGC 7000")]
    [InlineData(null, "North America Nebula", "North America Nebula")]
    [InlineData(null, null, "Unknown")]
    public void BuildPrimaryName_HandlesAllFourInputCombinations(string? catalogId, string? commonName, string expected)
    {
        Assert.Equal(expected, AliasCurator.BuildPrimaryName(catalogId, commonName));
    }

    [Fact]
    public void BuildPrimaryName_EmptyCatalogIdTreatedAsAbsent()
    {
        // Python's `if catalog_id and common_name` treats "" as falsy, same as None.
        Assert.Equal("North America Nebula", AliasCurator.BuildPrimaryName("", "North America Nebula"));
    }

    [Fact]
    public void BuildPrimaryName_BothEmptyReturnsUnknown()
    {
        Assert.Equal("Unknown", AliasCurator.BuildPrimaryName("", ""));
    }
}
