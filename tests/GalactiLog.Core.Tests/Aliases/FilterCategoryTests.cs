using GalactiLog.Core.Aliases;
using Xunit;

namespace GalactiLog.Core.Tests.Aliases;

// The web's canonicalFilterCategory fold and its seeded palette, ported (P13 R2a). Every case
// below pins one line of frontend/src/utils/filterStyles.ts:6-26 or of
// frontend/src/store/settings.ts:88-99, so a drift in either is a failure here rather than a
// filter that quietly renders grey.
public class FilterCategoryTests
{
    [Theory]
    // Luminance.
    [InlineData("l", "L")]
    [InlineData("lum", "L")]
    [InlineData("luminance", "L")]
    [InlineData("luminosity", "L")]
    [InlineData("clear", "L")]
    // Red, green, blue.
    [InlineData("r", "R")]
    [InlineData("red", "R")]
    [InlineData("g", "G")]
    [InlineData("green", "G")]
    [InlineData("b", "B")]
    [InlineData("blue", "B")]
    // Sulfur II, both spellings.
    [InlineData("sii", "SII")]
    [InlineData("s2", "SII")]
    [InlineData("s", "SII")]
    [InlineData("sulfur", "SII")]
    [InlineData("sulphur", "SII")]
    [InlineData("sulfurii", "SII")]
    [InlineData("sulphurii", "SII")]
    // Hydrogen alpha.
    [InlineData("ha", "Ha")]
    [InlineData("h", "Ha")]
    [InlineData("halpha", "Ha")]
    [InlineData("hydrogenalpha", "Ha")]
    [InlineData("hydrogen", "Ha")]
    [InlineData("656nm", "Ha")]
    [InlineData("656", "Ha")]
    // Oxygen III.
    [InlineData("oiii", "OIII")]
    [InlineData("o3", "OIII")]
    [InlineData("o", "OIII")]
    [InlineData("oxygen", "OIII")]
    [InlineData("oxygeniii", "OIII")]
    [InlineData("500nm", "OIII")]
    [InlineData("501nm", "OIII")]
    public void Of_FoldsEveryWebVariant(string raw, string? expected)
        => Assert.Equal(expected, FilterCategory.Of(raw));

    [Fact]
    public void Of_IgnoresCaseUnderscoresHyphensAndSpaces()
    {
        Assert.Equal("Ha", FilterCategory.Of("H-Alpha"));
        Assert.Equal("Ha", FilterCategory.Of("H_ALPHA"));
        Assert.Equal("Ha", FilterCategory.Of("  H alpha  "));
        Assert.Equal("Ha", FilterCategory.Of("Hydrogen-Alpha"));
        Assert.Equal("OIII", FilterCategory.Of("O III"));
        Assert.Equal("SII", FilterCategory.Of("S_II"));
        Assert.Equal("L", FilterCategory.Of("LUM"));
    }

    // The web's Ha set carries the literal "h alpha" although its own normalizer has already
    // stripped every space, so that entry can never match there. The port keeps the entry, and
    // this case records that the input it names still folds, through "halpha", which is in the
    // set twice over. Nothing depends on the dead entry; it is kept so a reader comparing the two
    // lists finds them identical.
    [Fact]
    public void Of_TheWebsDeadHSpaceAlphaEntry_StillFoldsThroughHalpha()
        => Assert.Equal("Ha", FilterCategory.Of("h alpha"));

    [Theory]
    [InlineData("IR")]
    [InlineData("Duoband")]
    [InlineData("L-eXtreme")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Of_ReturnsNullForANonStandardName(string? raw)
        => Assert.Null(FilterCategory.Of(raw));

    // The web checks SII before Ha with the comment "check before Ha to avoid 'sho' false
    // matches". The bare "s" is the entry that order exists for.
    [Fact]
    public void Of_TheBareS_FoldsToSII_NotToSomethingElse()
    {
        Assert.Equal("SII", FilterCategory.Of("s"));
        Assert.Equal("SII", FilterCategory.Of("S"));
    }

    [Fact]
    public void Of_TheBareH_FoldsToHa_AndTheBareO_ToOIII()
    {
        Assert.Equal("Ha", FilterCategory.Of("h"));
        Assert.Equal("OIII", FilterCategory.Of("O"));
    }

    [Fact]
    public void Defaults_CarryTheSevenSeededColours()
    {
        Assert.Equal(7, FilterCategory.Defaults.Count);
        Assert.Equal("#c44040", FilterCategory.Defaults["Ha"]);
        Assert.Equal("#3a8fd4", FilterCategory.Defaults["OIII"]);
        Assert.Equal("#d4a43a", FilterCategory.Defaults["SII"]);
        Assert.Equal("#e0e0e0", FilterCategory.Defaults["L"]);
        Assert.Equal("#e05050", FilterCategory.Defaults["R"]);
        Assert.Equal("#50b050", FilterCategory.Defaults["G"]);
        Assert.Equal("#5070e0", FilterCategory.Defaults["B"]);

        // Keyed by the category name the fold returns, ordinally: "ha" is not a key.
        Assert.False(FilterCategory.Defaults.ContainsKey("ha"));
    }

    [Fact]
    public void DefaultFor_IsOfThenDefaults()
    {
        Assert.Equal("#c44040", FilterCategory.DefaultFor("H-alpha"));
        Assert.Equal("#e0e0e0", FilterCategory.DefaultFor("lum"));
        Assert.Equal("#3a8fd4", FilterCategory.DefaultFor("O3"));
        Assert.Null(FilterCategory.DefaultFor("Duoband"));
        Assert.Null(FilterCategory.DefaultFor(null));
        Assert.Null(FilterCategory.DefaultFor("   "));
    }
}
