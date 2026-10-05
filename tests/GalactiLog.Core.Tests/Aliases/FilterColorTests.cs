using GalactiLog.Core.Aliases;
using Xunit;

namespace GalactiLog.Core.Tests.Aliases;

// FIXER LIST F10: the spec 5.8.4 filter-colour fallback, previously duplicated as a private
// constant in two App view-models.
public class FilterColorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OrFallback_AbsentOrBlank_IsTheSpecDefault(string? color)
        => Assert.Equal("#808080", FilterColor.OrFallback(color));

    [Fact]
    public void OrFallback_ConfiguredValue_IsReturnedTrimmed()
        => Assert.Equal("#FF0000", FilterColor.OrFallback("  #FF0000 "));

    // ---- P13 review P3-5 and P2-1: the one stored-intent rule and the one resolution -----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#808080")]
    [InlineData("  #808080  ")]
    public void AsStored_CarriesNoIntent_IsNull(string? color)
        => Assert.Null(FilterColor.AsStored(color));

    [Theory]
    [InlineData("#7f7f7f", "#7f7f7f")]
    [InlineData("  #FF0000 ", "#FF0000")]
    [InlineData("#808081", "#808081")]
    public void AsStored_AnythingElse_IsKeptTrimmed(string color, string expected)
        => Assert.Equal(expected, FilterColor.AsStored(color));

    [Fact]
    public void Resolve_RunsTheFourStepsInOrder()
    {
        // 1. A stored colour that carries intent.
        Assert.Equal("#112233", FilterColor.Resolve("#112233", "Ha", ["OIII"]));
        // 2. The category the canonical name folds to.
        Assert.Equal("#c44040", FilterColor.Resolve(null, "H-alpha", []));
        // 3. The first alias that folds to a category, when the canonical does not.
        Assert.Equal("#3a8fd4", FilterColor.Resolve(null, "Duoband", ["IR", "O3"]));
        // 4. Spec 5.8.4's grey.
        Assert.Equal("#808080", FilterColor.Resolve(null, "Duoband", ["IR"]));
    }

    [Fact]
    public void Resolve_ToleratesNulls()
    {
        Assert.Equal("#808080", FilterColor.Resolve(null, null, null));
        Assert.Equal("#c44040", FilterColor.Resolve("   ", "Ha", null));
    }
}
