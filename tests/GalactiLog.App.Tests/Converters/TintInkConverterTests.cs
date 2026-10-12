using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using GalactiLog.App.Converters;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Converters;

// Fix round 1, I2: a filter chip's label takes the filter colour only where it reads at 3:1 against
// the surface the chip sits on, else it falls back to the host's primary ink. The surfaces are read
// out of the two light theme dictionaries rather than restated, so a retuned surface moves the case.
public class TintInkConverterTests
{
    private static Color Surface(string theme)
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Theme", "Themes", theme + ".axaml"));
        var match = Regex.Match(source, @"x:Key=""ColorBgSurface"" Color=""(#[0-9A-Fa-f]{8})""");
        Assert.True(match.Success, $"{theme} declares no ColorBgSurface colour.");
        return Color.Parse(match.Groups[1].Value);
    }

    private static object? Ink(string tint, Color surface)
        => TintInkConverter.Instance.Convert(
            [new SolidColorBrush(Color.Parse(tint)), new SolidColorBrush(surface)],
            typeof(IBrush),
            null,
            CultureInfo.InvariantCulture);

    // The default L colour fails on both light surfaces; a saturated red passes on Atlas and a deep
    // blue on Logbook's darker paper.
    [Theory]
    [InlineData("Atlas", "#FF0000", true)]
    [InlineData("Atlas", "#e0e0e0", false)]
    [InlineData("Logbook", "#0050C0", true)]
    [InlineData("Logbook", "#e0e0e0", false)]
    public void TheLabel_TakesTheTint_OnlyWhereItReadsAtThreeToOne(string theme, string tint, bool reads)
    {
        var surface = Surface(theme);

        Assert.Equal(reads, TintInkConverter.Reads(Color.Parse(tint), surface));

        var ink = Ink(tint, surface);
        if (reads)
        {
            Assert.Equal(Color.Parse(tint), Assert.IsAssignableFrom<ISolidColorBrush>(ink).Color);
        }
        else
        {
            // No value: the label keeps the primary ink its host hands down.
            Assert.Same(AvaloniaProperty.UnsetValue, ink);
        }
    }

    [Fact]
    public void AMissingSurface_LeavesTheHostInk()
        => Assert.Same(
            AvaloniaProperty.UnsetValue,
            TintInkConverter.Instance.Convert(
                [new SolidColorBrush(Colors.Red), null], typeof(IBrush), null, CultureInfo.InvariantCulture));
}
