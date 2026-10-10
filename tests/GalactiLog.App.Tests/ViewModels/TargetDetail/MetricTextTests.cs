using GalactiLog.App.ViewModels.TargetDetail;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// The table cell figures (spec.md items 3 and 6): a missing value is the dash, a real zero is 0.
public class MetricTextTests
{
    [Theory]
    [InlineData(null, MetricText.Missing)]
    [InlineData(double.NaN, MetricText.Missing)]
    [InlineData(0d, "0")]
    [InlineData(12345d, "12,345")]
    public void Cell_RendersTheDashForNoValue_AndAZeroAsZero(double? value, string expected)
        => Assert.Equal(expected, MetricText.Cell(value, "N0"));

    [Theory]
    [InlineData(null, MetricText.Missing)]
    [InlineData("", MetricText.Missing)]
    [InlineData("Ha", "Ha")]
    public void Cell_RendersTheDashForNoText(string? text, string expected)
        => Assert.Equal(expected, MetricText.Cell(text));

    [Theory]
    [InlineData(0d, "0.0")]
    [InlineData(3600d, "1.0")]
    [InlineData(44640d, "12.4")]
    [InlineData(4_500_000d, "1,250.0")]
    public void HourFigure_IsOneDecimalWithSeparatorsAndNoUnit(double seconds, string expected)
        => Assert.Equal(expected, MetricText.HourFigure(seconds));
}
