using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Core.Metrics;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

// Task 4 section 8: the one stats card type (ruling A7), used by the Correlation tab and by
// Compare. The number format is StatsCard.tsx lines 12 to 16 ported exactly, and every row the
// web's own StatsCard.test.tsx asserts is ported below beside the boundary rows this port adds.
public class StatsCardTests
{
    // The web's StatsCard.test.tsx sampleStats, value for value.
    private static readonly SummaryStats WebSample = new(100, 1.23, 9.87, 5.0, 4.95, 1.5);

    [Fact]
    public void TheCard_RendersAllSixValues_TheWebsOwnCase()
    {
        // StatsCard.test.tsx, "renders all six stat values". N is the integer count and takes
        // neither the format nor the unit; the other five take two decimals below 10.
        var card = new StatsCardViewModel(WebSample);

        Assert.True(card.IsVisible);
        Assert.Equal("100", card.Count);
        Assert.Equal("1.23", card.Min);
        Assert.Equal("9.87", card.Max);
        Assert.Equal("5.00", card.Mean);
        Assert.Equal("4.95", card.Median);
        Assert.Equal("1.50", card.StdDev);
    }

    [Fact]
    public void TheLabel_IsRenderedOnlyWhenOneIsGiven_TheWebsOwnTwoCases()
    {
        // StatsCard.test.tsx, "renders the optional label when provided" and "does not render a
        // label element when label is omitted".
        var labelled = new StatsCardViewModel(WebSample, "HFR Distribution");
        Assert.True(labelled.HasLabel);
        Assert.Equal("HFR Distribution", labelled.Label);

        var bare = new StatsCardViewModel(WebSample);
        Assert.False(bare.HasLabel);
        Assert.Equal(string.Empty, bare.Label);
    }

    [Fact]
    public void LargeFigures_TakeFewerDecimals_TheWebsOwnCase()
    {
        // StatsCard.test.tsx, "abbreviates large numbers to one decimal place": max 12345.6 at or
        // above 1000 formats as toFixed(0) = "12346", and mean 10.5 at or above 10 formats as
        // toFixed(1) = "10.5".
        var card = new StatsCardViewModel(WebSample with { Max = 12345.6, Mean = 10.5 });

        Assert.Equal("12346", card.Max);
        Assert.Equal("10.5", card.Mean);
    }

    [Theory]
    [InlineData(0.0, "0.00")]
    [InlineData(9.99, "9.99")]          // below 10: two decimals
    [InlineData(9.994, "9.99")]
    [InlineData(10.0, "10.0")]          // AT 10: one decimal. A > instead of >= flips this row
    [InlineData(10.04, "10.0")]
    [InlineData(999.9, "999.9")]        // below 1000: still one decimal
    [InlineData(1000.0, "1000")]        // AT 1000: no decimals. A > instead of >= flips this row
    [InlineData(12345.6, "12346")]
    [InlineData(-9.99, "-9.99")]        // the threshold is on the absolute value
    [InlineData(-10.0, "-10.0")]
    [InlineData(-999.9, "-999.9")]
    [InlineData(-1000.4, "-1000")]      // task4.md section 8 names this one
    public void TheFormat_IsStatsCardTsxLines12To16(double value, string expected)
    {
        // Red proof: a > instead of a >= at either boundary flips the 10 and 1000 rows, and a
        // threshold on the signed value rather than on Math.Abs flips the four negative rows.
        Assert.Equal(expected, StatsCardViewModel.Format(value));
    }

    [Theory]
    [InlineData(" px", "2.31 px")]
    [InlineData(" arcsec", "2.31 arcsec")]
    [InlineData("%", "2.31%")]
    [InlineData(" hPa", "2.31 hPa")]
    [InlineData("\u00b0C", "2.31\u00b0C")]
    [InlineData("", "2.31")]
    public void TheUnitSuffix_IsAppendedWithNoSeparatorOfItsOwn(string unit, string expected)
    {
        // The leading space in " px", " hPa" and " arcsec" is part of the suffix and its absence
        // in "%" and the degree sign is too (spec 12.14's metric table, AnalysisMetricLabels).
        // Red against a format that inserts a space of its own: every row gains one.
        Assert.Equal(expected, StatsCardViewModel.Format(2.31, unit));
    }

    [Fact]
    public void TheUnit_IsAppendedToTheFiveFiguresAndNeverToN()
    {
        var card = new StatsCardViewModel(WebSample, "X: HFR (px)", " px");

        Assert.Equal("100", card.Count);
        Assert.Equal("1.23 px", card.Min);
        Assert.Equal("9.87 px", card.Max);
        Assert.Equal("5.00 px", card.Mean);
        Assert.Equal("4.95 px", card.Median);
        Assert.Equal("1.50 px", card.StdDev);
    }

    [Fact]
    public void AbsentStats_HideTheCardRatherThanShowingZeroes()
    {
        // Spec 12.14's Correlation subsection. Red against a card built from a non-nullable record
        // with a zero default: IsVisible reads true and the reader sees six zeroes that look like
        // measurements.
        var card = new StatsCardViewModel(null, "X: Humidity (%)", "%");

        Assert.False(card.IsVisible);
        Assert.Equal(string.Empty, card.Count);
        Assert.Equal(string.Empty, card.Min);
        Assert.Equal(string.Empty, card.Max);
        Assert.Equal(string.Empty, card.Mean);
        Assert.Equal(string.Empty, card.Median);
        Assert.Equal(string.Empty, card.StdDev);
    }
}
