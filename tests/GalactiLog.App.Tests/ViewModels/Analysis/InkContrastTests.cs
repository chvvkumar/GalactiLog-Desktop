using GalactiLog.App.ViewModels.Analysis;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

// The shared WCAG rule two Analysis charts pick their ink with: the Matrix grid for a cell label
// and the box plot for its median mark. Pure statics, so no Avalonia and no chart is built here.
// The two callers keep their own cases on what they pass in and what they publish; this file
// covers the arithmetic they both depend on.
public class InkContrastTests
{
    [Fact]
    public void RelativeLuminance_AnswersTheWcagEndsAndUsesTheLinearArmBelowTheKnee()
    {
        Assert.Equal(1d, InkContrast.RelativeLuminance(SKColors.White), 6);
        Assert.Equal(0d, InkContrast.RelativeLuminance(SKColors.Black), 6);

        // Channel 10 is 0.0392, under the 0.03928 knee, so the linear arm applies and the gamma
        // arm would answer a different number. A green-only colour isolates the one channel.
        var green = new SKColor(0, 10, 0);
        Assert.Equal(0.7152d * (10d / 255d / 12.92d), InkContrast.RelativeLuminance(green), 9);
    }

    [Fact]
    public void Contrast_SpansOneToTwentyOneAndIsSymmetric()
    {
        Assert.Equal(21d, InkContrast.Contrast(SKColors.White, SKColors.Black), 6);
        Assert.Equal(21d, InkContrast.Contrast(SKColors.Black, SKColors.White), 6);
        Assert.Equal(1d, InkContrast.Contrast(SKColors.White, SKColors.White), 6);
    }

    [Fact]
    public void Contrast_IgnoresAlpha()
    {
        // The callers force an ink opaque before they get here, and this is why: a translucent
        // white reads as white, not as whatever it would blend to.
        Assert.Equal(
            InkContrast.Contrast(SKColors.White, SKColors.Black),
            InkContrast.Contrast(SKColors.White.WithAlpha(0x20), SKColors.Black),
            6);
    }

    [Theory]
    // A dark background takes the light ink, a light background takes the dark ink, whichever
    // order the pair is given in.
    [InlineData(0x00, 0x00, 0x00, true)]
    [InlineData(0xFF, 0xFF, 0xFF, false)]
    // red-light's negative ramp end, the cell that made this rule necessary: near-white loses.
    [InlineData(0xD2, 0x4C, 0x33, false)]
    public void Choose_TakesTheHigherRatioOfTheTwoInks(byte r, byte g, byte b, bool lightWins)
    {
        var background = new SKColor(r, g, b);
        var light = new SKColor(0xF5, 0xF5, 0xF5);
        var dark = new SKColor(0x14, 0x14, 0x14);

        var chosen = InkContrast.Choose(background, light, dark);

        Assert.Equal(lightWins ? light : dark, chosen);
        Assert.Equal(
            Math.Max(InkContrast.Contrast(light, background), InkContrast.Contrast(dark, background)),
            InkContrast.Contrast(chosen, background),
            6);
    }

    [Fact]
    public void Choose_GivesATieToTheLightInk()
    {
        // Two inks the same distance from the background: the theme's own text ink stands rather
        // than the substitute.
        var light = new SKColor(0xF5, 0xF5, 0xF5);

        Assert.Equal(light, InkContrast.Choose(SKColors.Black, light, light));
    }
}
