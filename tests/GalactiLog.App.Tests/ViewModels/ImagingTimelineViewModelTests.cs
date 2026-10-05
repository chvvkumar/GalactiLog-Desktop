using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Stats;
using System.Globalization;
using GalactiLog.Core.Sessions;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.StatisticsViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.5's timeline controls and spec 8.4's efficiency rule. Plain xunit facts: the timeline
// view-model touches no window and no database (spec 18.3).
public class ImagingTimelineViewModelTests
{
    private static ImagingTimelineViewModel Create(
        StatsResponse? stats = null,
        bool coordinates = true)
    {
        var timeline = new ImagingTimelineViewModel(() => Factory.Today);
        timeline.SetData(
            stats ?? Factory.Sample(),
            coordinates ? Factory.WithCoordinates() : Factory.WithoutCoordinates());
        return timeline;
    }

    [Fact]
    public void Granularity_DefaultsToMonthly()
    {
        using var timeline = Create();

        Assert.Equal(TimelineGranularity.Monthly, timeline.Granularity);

        // The web's default preset is 1Y, which maps to zoom 2, which is monthly.
        Assert.Equal(TimelineRangePreset.OneYear, timeline.RangePreset);
    }

    [Theory]
    [InlineData(TimelineRangePreset.All, TimelineGranularity.Monthly)]
    [InlineData(TimelineRangePreset.OneYear, TimelineGranularity.Monthly)]
    [InlineData(TimelineRangePreset.Quarter, TimelineGranularity.Weekly)]
    [InlineData(TimelineRangePreset.Month, TimelineGranularity.Daily)]
    [InlineData(TimelineRangePreset.Week, TimelineGranularity.Daily)]
    public void SelectingAPreset_SetsBothTheRangeAndTheGranularity(
        TimelineRangePreset preset, TimelineGranularity expected)
    {
        using var timeline = Create();

        timeline.SelectPresetCommand.Execute(preset);

        Assert.Equal(preset, timeline.RangePreset);
        Assert.Equal(expected, timeline.Granularity);
    }

    [Fact]
    public void Granularity_CanBeChangedAfterAPreset()
    {
        using var timeline = Create();
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.Quarter);
        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);

        // questions.md Q15: the selector stays live, so the preset is a starting point and not a
        // lock.
        timeline.Granularity = TimelineGranularity.Daily;

        Assert.Equal(TimelineGranularity.Daily, timeline.Granularity);
        Assert.Equal(TimelineRangePreset.Quarter, timeline.RangePreset);
    }

    // Minor 18, ruled from review finding M14: the All preset spans the library's whole history and
    // the port has no zoom (questions.md Q15), so All plus Daily would emit one bar and one axis
    // label per calendar date over that span with no way out.
    [Fact]
    public void AllPreset_OffersMonthlyAndWeeklyOnly()
    {
        using var timeline = Create();

        Assert.Equal(
            new[] { TimelineGranularity.Monthly, TimelineGranularity.Weekly, TimelineGranularity.Daily },
            timeline.GranularityOptions.Select(option => option.Value));

        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);

        Assert.Equal(
            new[] { TimelineGranularity.Monthly, TimelineGranularity.Weekly },
            timeline.GranularityOptions.Select(option => option.Value));
    }

    [Fact]
    public void SelectingAll_WhileDailyIsChosen_SnapsToWeekly()
    {
        using var timeline = Create();
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.Month);
        Assert.Equal(TimelineGranularity.Daily, timeline.Granularity);

        timeline.RangePreset = TimelineRangePreset.All;

        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);
        Assert.DoesNotContain(
            timeline.GranularityOptions,
            option => option.Value == TimelineGranularity.Daily);
    }

    [Fact]
    public void UnderAll_SettingDailyDirectly_SnapsBackToWeekly()
    {
        using var timeline = Create();
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);

        timeline.Granularity = TimelineGranularity.Daily;

        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);
    }

    [Fact]
    public void LeavingAll_RestoresTheDailyOption()
    {
        using var timeline = Create();
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);

        timeline.SelectPresetCommand.Execute(TimelineRangePreset.Quarter);

        Assert.Equal(3, timeline.GranularityOptions.Count);
        timeline.Granularity = TimelineGranularity.Daily;
        Assert.Equal(TimelineGranularity.Daily, timeline.Granularity);
    }

    [Fact]
    public void ImagedOnly_DefaultsToFalse_SoGapsAreFilled()
    {
        using var timeline = Create(Factory.Sample(
            monthly: [new TimelineEntry("2025-01", 3_600d), new TimelineEntry("2025-04", 3_600d)]));
        Assert.False(timeline.ImagedOnly);

        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);

        Assert.Equal(
            new[] { "2025-01", "2025-02", "2025-03", "2025-04" },
            timeline.Bars.Select(bar => bar.Period));
        Assert.Equal(0d, timeline.Bars[1].IntegrationSeconds);
    }

    [Fact]
    public void ImagedOnly_True_DropsPeriodsWithNoIntegration()
    {
        using var timeline = Create(Factory.Sample(
            monthly: [new TimelineEntry("2025-01", 3_600d), new TimelineEntry("2025-04", 3_600d)]));
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);

        timeline.ImagedOnly = true;

        Assert.Equal(new[] { "2025-01", "2025-04" }, timeline.Bars.Select(bar => bar.Period));
    }

    [Fact]
    public void GapFilling_Monthly_InsertsEveryMonthBetweenTheBounds()
    {
        using var timeline = Create(Factory.Sample(
            monthly: [new TimelineEntry("2024-11", 3_600d), new TimelineEntry("2025-02", 3_600d)]));

        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);

        Assert.Equal(
            new[] { "2024-11", "2024-12", "2025-01", "2025-02" },
            timeline.Bars.Select(bar => bar.Period));
    }

    [Fact]
    public void GapFilling_Weekly_InsertsEveryIsoWeekBetweenTheBounds()
    {
        using var timeline = Create(Factory.Sample(
            weekly: [new TimelineEntry("2025-W10", 3_600d), new TimelineEntry("2025-W13", 3_600d)]));

        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);
        timeline.Granularity = TimelineGranularity.Weekly;

        Assert.Equal(
            new[] { "2025-W10", "2025-W11", "2025-W12", "2025-W13" },
            timeline.Bars.Select(bar => bar.Period));

        // Every bar spans a Monday to the Sunday after it, which is the same ISO rule Task 1's
        // AstroNight.IsoWeekMonday applies from the other direction.
        Assert.All(timeline.Bars, bar =>
        {
            Assert.Equal(DayOfWeek.Monday, bar.Start.DayOfWeek);
            Assert.Equal(bar.Start.AddDays(6), bar.End);
        });
    }

    [Fact]
    public void GapFilling_Daily_InsertsEveryDateBetweenTheBounds()
    {
        using var timeline = Create(Factory.Sample(
            daily: [new TimelineEntry("2025-03-01", 3_600d), new TimelineEntry("2025-03-04", 3_600d)]));

        // The W preset, not All plus Daily: minor 18 keeps Daily off the All selector.
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.Week);

        Assert.Equal(
            new[] { "2025-03-01", "2025-03-02", "2025-03-03", "2025-03-04" },
            timeline.Bars.Select(bar => bar.Period));
    }

    [Theory]
    [InlineData(TimelineGranularity.Daily, "All nights")]
    [InlineData(TimelineGranularity.Weekly, "All weeks")]
    [InlineData(TimelineGranularity.Monthly, "All months")]
    public void RightButtonLabel_FollowsTheGranularity(TimelineGranularity granularity, string expected)
    {
        using var timeline = Create();

        timeline.Granularity = granularity;

        Assert.Equal(expected, timeline.AllPeriodsLabel);
        Assert.Equal("Imaged only", ImagingTimelineViewModel.ImagedOnlyLabel);
    }

    [Fact]
    public void Efficiency_IsIntegrationHoursOverDarkHoursTimesRigs_ToOneDecimal()
    {
        // One night, one rig, so the expected figure is hand-computable from AstroNight itself:
        // 5 hours of exposure over that night's dark hours.
        var date = new DateOnly(2025, 3, 1);
        using var timeline = Create(Factory.Sample(
            daily: [new TimelineEntry("2025-03-01", 18_000d)],
            rigsPerNight: new Dictionary<DateOnly, int> { [date] = 1 }));
        // The W preset, not All plus Daily: minor 18 keeps Daily off the All selector.
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.Week);

        var dark = AstroNight.DarkHoursForNight(date, Factory.Latitude, Factory.Longitude);
        var expected = Math.Round(5d / dark * 100d, 1);

        Assert.Equal(expected, Assert.Single(timeline.Bars).EfficiencyPercent);
    }

    [Fact]
    public void Efficiency_UsesAFloorOfOneRigForAPeriodDateWithNoFrames()
    {
        // A whole ISO week, of which exactly one night carried frames. The Python's max(1, rigs)
        // makes the other six contribute a full night of darkness each, so the denominator is the
        // week's darkness and not just the imaged night's.
        var imaged = new DateOnly(2025, 3, 5);
        using var timeline = Create(Factory.Sample(
            weekly: [new TimelineEntry("2025-W10", 18_000d)],
            rigsPerNight: new Dictionary<DateOnly, int> { [imaged] = 2 }));
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);
        timeline.Granularity = TimelineGranularity.Weekly;

        var bar = Assert.Single(timeline.Bars);
        var floored = 0d;
        for (var date = bar.Start; date <= bar.End; date = date.AddDays(1))
        {
            var rigs = date == imaged ? 2 : 1;
            floored += AstroNight.DarkHoursForNight(date, Factory.Latitude, Factory.Longitude) * rigs;
        }

        var unfloored = AstroNight.DarkHoursForNight(imaged, Factory.Latitude, Factory.Longitude) * 2;
        Assert.True(floored > unfloored, "the six blank nights must contribute darkness");
        Assert.Equal(Math.Round(5d / floored * 100d, 1), bar.EfficiencyPercent);
        Assert.NotEqual(Math.Round(5d / unfloored * 100d, 1), bar.EfficiencyPercent);
    }

    [Fact]
    public void Efficiency_IsNull_WhenTotalDarkHoursAreZero()
    {
        // Midsummer above the Arctic circle: the sun never reaches -18 degrees, so there is no
        // denominator and the period has no efficiency rather than an infinite one.
        var timeline = new ImagingTimelineViewModel(() => new DateOnly(2025, 6, 30));
        timeline.SetData(
            Factory.Sample(daily: [new TimelineEntry("2025-06-21", 18_000d)]),
            new GeneralSettings
            {
                ObserverLatitude = 78d,
                ObserverLongitude = 15d,
            });
        // The W preset, not All plus Daily: minor 18 keeps Daily off the All selector.
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.Week);

        Assert.Null(Assert.Single(timeline.Bars).EfficiencyPercent);
        timeline.Dispose();
    }

    [Fact]
    public void Efficiency_SeriesIsAbsent_WhenObserverLatitudeIsNull()
    {
        using var timeline = Create(coordinates: false);

        Assert.False(timeline.HasEfficiency);
        Assert.Single(timeline.Series);
        Assert.Single(timeline.YAxes);
        Assert.All(timeline.Bars, bar => Assert.Null(bar.EfficiencyPercent));
    }

    [Fact]
    public void Efficiency_SeriesIsAbsent_WhenObserverLongitudeIsNull()
    {
        var timeline = new ImagingTimelineViewModel(() => Factory.Today);
        timeline.SetData(
            Factory.Sample(),
            new GeneralSettings { ObserverLatitude = Factory.Latitude });

        Assert.False(timeline.HasEfficiency);
        Assert.Single(timeline.Series);
        Assert.Single(timeline.YAxes);
        timeline.Dispose();
    }

    [Fact]
    public void Efficiency_SeriesIsPresent_WhenBothCoordinatesAreSet()
    {
        using var timeline = Create();

        Assert.True(timeline.HasEfficiency);
        Assert.Equal(2, timeline.Series.Count);
        Assert.IsType<ColumnSeries<double?>>(timeline.Series[0]);
        Assert.IsType<LineSeries<double?>>(timeline.Series[1]);

        // Spec 13: "a secondary 0-100 axis".
        Assert.Equal(2, timeline.YAxes.Count);
        var secondary = Assert.IsType<Axis>(timeline.YAxes[1]);
        Assert.Equal(0d, secondary.MinLimit);
        Assert.Equal(100d, secondary.MaxLimit);
        Assert.Equal(AxisPosition.End, secondary.Position);
        Assert.Equal(1, Assert.IsType<LineSeries<double?>>(timeline.Series[1]).ScalesYAt);
    }

    [Theory]
    [InlineData("2025-05", TimelineGranularity.Monthly, "May 2025")]
    [InlineData("2025-W03", TimelineGranularity.Weekly, "W3 '25")]
    [InlineData("2025-12-03", TimelineGranularity.Daily, "Dec 3")]
    public void PeriodLabel_Monthly_Weekly_AndDaily_MatchTheWebFormats(
        string period, TimelineGranularity granularity, string expected)
        => Assert.Equal(expected, ImagingTimelineViewModel.FormatLabel(period, granularity));

    [Theory]
    [InlineData(0d, "0.0h")]
    [InlineData(18_000d, "5.0h")]
    [InlineData(34_200d, "9.5h")]
    [InlineData(36_000d, "10h")]
    [InlineData(41_400d, "12h")]
    public void FormatHours_BelowTenHours_HasOneDecimal_AndAboveIsRounded(double seconds, string expected)
        => Assert.Equal(expected, ImagingTimelineViewModel.FormatHours(seconds));

    [Theory]
    [InlineData("2025-05", TimelineGranularity.Monthly, "2025-05-01", "2025-05-31")]
    [InlineData("2025-W03", TimelineGranularity.Weekly, "2025-01-13", "2025-01-19")]
    [InlineData("2025-12-03", TimelineGranularity.Daily, "2025-12-03", "2025-12-03")]
    public void PeriodToDateRange_MatchesTheWebRule(
        string period, TimelineGranularity granularity, string from, string to)
    {
        var (start, end) = ImagingTimelineViewModel.Bounds(period, granularity);

        Assert.Equal(DateOnly.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture), start);
        Assert.Equal(DateOnly.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture), end);
    }

    [Fact]
    public void BarClick_RaisesThePeriodsDateRange()
    {
        using var timeline = Create(Factory.Sample(
            monthly: [new TimelineEntry("2025-05", 3_600d)]));
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.Month);
        timeline.Granularity = TimelineGranularity.Monthly;

        (DateOnly From, DateOnly To)? raised = null;
        timeline.PeriodSelected += (_, range) => raised = range;

        timeline.SelectPeriodCommand.Execute(0);

        Assert.Equal((new DateOnly(2025, 5, 1), new DateOnly(2025, 5, 31)), raised);
    }

    [Fact]
    public void BarClick_OutsideTheBars_RaisesNothing()
    {
        using var timeline = Create();
        var raised = 0;
        timeline.PeriodSelected += (_, _) => raised++;

        timeline.SelectPeriodCommand.Execute(-1);
        timeline.SelectPeriodCommand.Execute(9999);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void EmptyData_PublishesOneAxisPerSide()
    {
        var timeline = new ImagingTimelineViewModel(() => Factory.Today);
        timeline.SetData(Factory.Empty(), Factory.WithCoordinates());

        // FIXER LIST item 10: rc5.4 throws "XAxes and YAxes must contain at least one element".
        Assert.True(timeline.IsEmpty);
        Assert.Empty(timeline.Series);
        Assert.Single(timeline.XAxes);
        Assert.Single(timeline.YAxes);
        timeline.Dispose();
    }

    [Fact]
    public async Task Load_RunsOffTheUiThread()
    {
        // The page reads the stats aggregate and pre-warms the dark-hours memo on a pool thread.
        // Awaited, never blocked on (TRACKING section 2 item 8, FIXER item 11).
        var loadThread = 0;
        var callerThread = Environment.CurrentManagedThreadId;
        using var page = new StatisticsViewModel(
            () =>
            {
                loadThread = Environment.CurrentManagedThreadId;
                return Factory.Sample();
            },
            (_, _) => [],
            Factory.WithCoordinates,
            Factory.NoAliases,
            post: action => action(),
            today: () => Factory.Today);

        await Factory.SettleAsync(page);

        Assert.NotEqual(0, loadThread);
        Assert.NotEqual(callerThread, loadThread);
    }
}
