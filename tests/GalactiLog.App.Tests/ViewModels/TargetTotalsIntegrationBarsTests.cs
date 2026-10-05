using Avalonia.Media;
using Avalonia.Media.Immutable;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using Xunit;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// The header's per-filter integration line, one bar per filter in the swatch order, the longest bar being the largest integration.
public class TargetTotalsIntegrationBarsTests
{
    private static readonly Dictionary<string, string> Tints = new()
    {
        ["L"] = "#ffffff",
        ["Ha"] = "#ff3b3b",
        ["OIII"] = "#3ba7ff",
    };

    // One distinct tint per filter, so a bar paired with the wrong swatch's brush is visible.
    private static TargetTotalsViewModel Totals(IReadOnlyList<string> filters, Dictionary<string, double> seconds)
        => new(
            Page.PopulatedTotals() with { FiltersUsed = filters, IntegrationSecondsByFilter = seconds },
            name => new ImmutableSolidColorBrush(Color.Parse(Tints[name])));

    private static readonly Dictionary<string, double> ThreeFilters = new()
    {
        ["Ha"] = 7_200d,
        ["OIII"] = 3_600d,
        ["L"] = 1_800d,
    };

    [Fact]
    public void IntegrationBars_FollowTheSwatchOrder_NotTheQueryOrder()
    {
        // A failure is OIII, Ha, L in that order, or a bar count that disagrees with the swatches.
        var totals = Totals(["OIII", "Ha", "L"], ThreeFilters);

        Assert.Equal(["L", "Ha", "OIII"], totals.IntegrationBars.Select(bar => bar.FilterName));
        Assert.Equal(
            totals.FilterSwatches.Select(swatch => swatch.Brush.Color),
            totals.IntegrationBars.Select(bar => bar.Brush.Color));
        Assert.Equal(3, totals.IntegrationBars.Select(bar => bar.Brush.Color).Distinct().Count());
        Assert.True(totals.HasIntegrationBars);
    }

    [Fact]
    public void IntegrationBars_FractionIsOfTheLargestFilter_AndTheLargestIsOne()
    {
        // A failure is a fraction over the total or over the frame count rather than over the
        // longest filter, or a bar width that is not 160 at the longest.
        var bars = Totals(["Ha", "OIII", "L"], ThreeFilters).IntegrationBars.ToDictionary(bar => bar.FilterName);

        Assert.Equal(1d, bars["Ha"].Fraction);
        Assert.Equal(0.5d, bars["OIII"].Fraction);
        Assert.Equal(0.25d, bars["L"].Fraction);
        Assert.Equal(160d, bars["Ha"].BarWidth);
        Assert.Equal(40d, bars["L"].BarWidth);
    }

    [Fact]
    public void IntegrationBars_HoursText_IsOneDecimalWithTheUnit()
    {
        // A failure is a second decimal, a missing unit, or seconds where hours belong.
        var bars = Totals(["Ha", "OIII", "L"], ThreeFilters).IntegrationBars.ToDictionary(bar => bar.FilterName);

        Assert.Equal(2d, bars["Ha"].Hours);
        Assert.Equal("2.0 h", bars["Ha"].HoursText);
        Assert.Equal("1.0 h", bars["OIII"].HoursText);
        Assert.Equal("0.5 h", bars["L"].HoursText);
    }

    [Fact]
    public void IntegrationBars_AllZero_GiveZeroFractions_AndNoFilters_HideTheLine()
    {
        // A failure is a division by zero surfacing as NaN, or a visible empty row.
        var zero = Totals(["Ha", "OIII"], new Dictionary<string, double> { ["Ha"] = 0d, ["OIII"] = 0d });
        Assert.All(zero.IntegrationBars, bar => Assert.Equal(0d, bar.Fraction));
        Assert.All(zero.IntegrationBars, bar => Assert.Equal("0.0 h", bar.HoursText));
        Assert.True(zero.HasIntegrationBars);

        var none = Totals([], new Dictionary<string, double>());
        Assert.Empty(none.IntegrationBars);
        Assert.False(none.HasIntegrationBars);
    }

    [Fact]
    public void IntegrationBars_Segments_AreOnePerNightNewestFirst_OnTheLongestFiltersScale()
    {
        // A failure is the older night first, or a fraction over the filter's own hours.
        var older = new DateOnly(2025, 1, 1);
        var newer = new DateOnly(2025, 2, 1);
        NightFilterOverview Row(DateOnly night, string filter, double seconds)
            => new(night, filter, seconds, 1, [], null, null, null, null, null);
        var totals = new TargetTotalsViewModel(
            Page.PopulatedTotals() with { FiltersUsed = ["Ha", "OIII"], IntegrationSecondsByFilter = new Dictionary<string, double> { ["Ha"] = 7_200d, ["OIII"] = 3_600d } },
            name => new ImmutableSolidColorBrush(Color.Parse(Tints[name])),
            nightFilters: [Row(older, "OIII", 1_800d), Row(newer, "OIII", 1_800d), Row(older, "Ha", 7_200d)]);

        var oiii = totals.IntegrationBars.Single(bar => bar.FilterName == "OIII");
        Assert.Equal([newer, older], oiii.Segments.Select(segment => segment.Night));
        Assert.Equal([0.25d, 0.25d], oiii.Segments.Select(segment => segment.Fraction));
        Assert.Equal("2025-02-01: 0.5 h", oiii.Segments[0].Tip);
    }

    [Fact]
    public void IntegrationBars_Goal_IsTheGroupsLargest_AndShortByHidesUnderFiftyThousandthsOfAnHour()
    {
        // A failure is a goal on the largest filter, a goal fraction not over the longest filter,
        // or short-by text for a 0.003 h gap.
        var seconds = new Dictionary<string, double> { ["Ha"] = 36_000d, ["OIII"] = 32_400d, ["SII"] = 35_990d };
        var totals = new TargetTotalsViewModel(
            Page.PopulatedTotals() with { FiltersUsed = ["Ha", "OIII", "SII"], IntegrationSecondsByFilter = seconds },
            _ => new ImmutableSolidColorBrush(Colors.Gray));
        var bars = totals.IntegrationBars.ToDictionary(bar => bar.FilterName);

        Assert.Null(bars["Ha"].GoalFraction);
        Assert.Equal("", bars["Ha"].ShortByText);
        Assert.Equal(1d, bars["OIII"].GoalFraction);
        Assert.Equal("short by 1.0 h", bars["OIII"].ShortByText);
        Assert.Equal("", bars["SII"].ShortByText);
        Assert.Empty(bars["OIII"].Segments);
    }
}
