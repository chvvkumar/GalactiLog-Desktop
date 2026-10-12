using GalactiLog.Core.Text;
using Xunit;

namespace GalactiLog.Core.Tests.Text;

/// <summary>The two duration modes (ux-flow F12): a table figure and a prose figure, with no
/// leading zeros and no seconds above an hour.</summary>
public class DurationFormatTests
{
    [Theory]
    [InlineData(0d, "0.0 h")]
    [InlineData(471_960d, "131.1 h")]
    [InlineData(4_500_000d, "1,250.0 h")]
    [InlineData(-10d, "0.0 h")]
    [InlineData(double.NaN, "0.0 h")]
    [InlineData(double.PositiveInfinity, "0.0 h")]
    [InlineData(double.NegativeInfinity, "0.0 h")]
    public void Table_IsHoursWithOneDecimal(double seconds, string expected)
        => Assert.Equal(expected, DurationFormat.Table(seconds));

    [Theory]
    [InlineData(0d, "0 s")]
    [InlineData(-10d, "0 s")]
    [InlineData(double.NaN, "0 s")]
    [InlineData(18d, "18 s")]
    [InlineData(60d, "1 m")]
    [InlineData(2_718d, "45 m 18 s")]
    [InlineData(3_599.6d, "1 h")]
    [InlineData(3_600d, "1 h")]
    [InlineData(3_665d, "1 h 1 m")]
    [InlineData(24_318d, "6 h 45 m")]
    [InlineData(1_123_500d, "312 h 5 m")]
    public void Prose_DropsLeadingZerosAndSecondsAboveAnHour(double seconds, string expected)
        => Assert.Equal(expected, DurationFormat.Prose(seconds));
}
