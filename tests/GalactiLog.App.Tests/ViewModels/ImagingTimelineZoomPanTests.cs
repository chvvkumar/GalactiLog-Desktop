using System.Globalization;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.Data.Queries;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.StatisticsViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.5's "Wheel zoom and drag pan" (Phase 14C, UI layout ruling 13, questions.md Q7 and Q8).
// A new file rather than an append to the 26-case ImagingTimelineViewModelTests.cs, per the brief.
// No case here renders a chart: the pixel-to-period conversion lives in
// Views/StatisticsView.axaml.cs, and everything below is the period arithmetic it calls into.
public class ImagingTimelineZoomPanTests
{
    // Two years of consecutive months, so the default OneYear preset's 12-month window (the most
    // recent 12) leaves another 12 months of real data behind it for Pan to move into.
    private static IReadOnlyList<TimelineEntry> TwoYearsOfMonths()
    {
        var entries = new List<TimelineEntry>();
        var start = new DateOnly(2023, 7, 1);
        for (var i = 0; i < 24; i++)
        {
            entries.Add(new TimelineEntry(start.AddMonths(i).ToString("yyyy-MM", CultureInfo.InvariantCulture), 3_600d));
        }

        return entries;
    }

    // Two years of ISO weeks, for the granularity-step-with-anchor case, which resolves its window
    // against whichever granularity's own list it lands on.
    private static IReadOnlyList<TimelineEntry> TwoYearsOfWeeks()
    {
        var entries = new List<TimelineEntry>();
        var monday = new DateOnly(2023, 7, 3);
        for (var i = 0; i < 104; i++)
        {
            var date = monday.AddDays(7 * i).ToDateTime(TimeOnly.MinValue);
            var period = string.Create(
                CultureInfo.InvariantCulture,
                $"{ISOWeek.GetYear(date):D4}-W{ISOWeek.GetWeekOfYear(date):D2}");
            entries.Add(new TimelineEntry(period, 3_600d));
        }

        return entries;
    }

    private static ImagingTimelineViewModel Create(StatsResponse? stats = null)
    {
        var timeline = new ImagingTimelineViewModel(() => Factory.Today);
        timeline.SetData(stats ?? Factory.Sample(monthly: TwoYearsOfMonths()), Factory.WithCoordinates());
        return timeline;
    }

    private static int CountRebuilds(ImagingTimelineViewModel timeline, Action act)
    {
        var count = 0;
        void Handler(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImagingTimelineViewModel.Bars))
            {
                count++;
            }
        }

        timeline.PropertyChanged += Handler;
        try
        {
            act();
        }
        finally
        {
            timeline.PropertyChanged -= Handler;
        }

        return count;
    }

    [Fact]
    public void StepGranularity_Up_GoesMonthlyToWeeklyToDaily()
    {
        using var timeline = Create();
        Assert.Equal(TimelineGranularity.Monthly, timeline.Granularity);

        timeline.StepGranularity(1);
        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);

        timeline.StepGranularity(1);
        Assert.Equal(TimelineGranularity.Daily, timeline.Granularity);
    }

    [Fact]
    public void StepGranularity_Down_GoesDailyToWeeklyToMonthly()
    {
        using var timeline = Create();
        timeline.Granularity = TimelineGranularity.Daily;

        timeline.StepGranularity(-1);
        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);

        timeline.StepGranularity(-1);
        Assert.Equal(TimelineGranularity.Monthly, timeline.Granularity);
    }

    // Fails when a wheel notch past either end wraps to the opposite granularity instead of
    // stopping.
    [Fact]
    public void StepGranularity_AtTheEnds_Stops_AndDoesNotWrap()
    {
        using var timeline = Create();
        Assert.Equal(TimelineGranularity.Monthly, timeline.Granularity);

        timeline.StepGranularity(-1);
        Assert.Equal(TimelineGranularity.Monthly, timeline.Granularity);

        timeline.Granularity = TimelineGranularity.Daily;
        timeline.StepGranularity(1);
        Assert.Equal(TimelineGranularity.Daily, timeline.Granularity);
    }

    // Fails when a second wheel notch under the All preset produces TimelineGranularity.Daily: the
    // case that catches stepping the raw enum rather than GranularityOptions.
    [Fact]
    public void StepGranularity_UnderTheAllPreset_NeverReachesDaily()
    {
        using var timeline = Create();
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);
        Assert.Equal(TimelineGranularity.Monthly, timeline.Granularity);

        timeline.StepGranularity(1);
        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);

        timeline.StepGranularity(1);
        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);
        Assert.DoesNotContain(
            timeline.GranularityOptions,
            option => option.Value == TimelineGranularity.Daily);
    }

    [Fact]
    public void StepGranularity_WithAnAnchor_KeepsTheAnchorPeriodInTheWindow()
    {
        using var timeline = Create(Factory.Sample(monthly: TwoYearsOfMonths(), weekly: TwoYearsOfWeeks()));
        // Comfortably inside both halves of the two year span, so no clamp at either edge is
        // needed to prove the centring.
        var anchor = new DateOnly(2024, 6, 15);

        timeline.StepGranularity(1, anchor);

        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);
        Assert.Contains(timeline.Bars, bar => anchor >= bar.Start && anchor <= bar.End);
    }

    [Fact]
    public void Pan_MovesTheFirstAndLastBar_ByThatManyPeriods()
    {
        using var timeline = Create();
        var firstStart = timeline.Bars.First().Start;
        var lastEnd = timeline.Bars.Last().End;

        var expectedFirstPeriod = firstStart.AddMonths(-2).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var (expectedFirstStart, _) = ImagingTimelineViewModel.Bounds(expectedFirstPeriod, TimelineGranularity.Monthly);
        var expectedLastPeriod = new DateOnly(lastEnd.Year, lastEnd.Month, 1)
            .AddMonths(-2)
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var (_, expectedLastEnd) = ImagingTimelineViewModel.Bounds(expectedLastPeriod, TimelineGranularity.Monthly);

        timeline.Pan(-2);

        Assert.Equal(expectedFirstStart, timeline.Bars.First().Start);
        Assert.Equal(expectedLastEnd, timeline.Bars.Last().End);
    }

    [Fact]
    public void Pan_StopsAtTheFirstPeriodInTheData()
    {
        using var timeline = Create();
        var (dataStart, _) = ImagingTimelineViewModel.Bounds(TwoYearsOfMonths()[0].Period, TimelineGranularity.Monthly);

        timeline.Pan(-100);

        Assert.Equal(dataStart, timeline.Bars.First().Start);
    }

    [Fact]
    public void Pan_StopsAtTheNewestPeriodInTheData()
    {
        using var timeline = Create();
        var unpannedLastEnd = timeline.Bars.Last().End;

        timeline.Pan(-3);
        Assert.NotEqual(unpannedLastEnd, timeline.Bars.Last().End);

        timeline.Pan(100);
        Assert.Equal(unpannedLastEnd, timeline.Bars.Last().End);
        Assert.Equal(0, timeline.PanPeriods);
    }

    [Fact]
    public void Pan_RebuildsOnce_PerCall()
    {
        using var timeline = Create();

        var rebuilds = CountRebuilds(timeline, () => timeline.Pan(-1));

        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public void SelectPreset_ResetsThePan()
    {
        using var timeline = Create();
        timeline.Pan(-2);
        Assert.NotEqual(0, timeline.PanPeriods);

        // Same preset the timeline already carries by default, so nothing about the range or the
        // granularity changes: only SelectPreset's own explicit reset can zero the pan here.
        timeline.SelectPresetCommand.Execute(TimelineRangePreset.OneYear);

        Assert.Equal(0, timeline.PanPeriods);
    }

    [Fact]
    public void AGranularityChange_ResetsThePan()
    {
        using var timeline = Create(Factory.Sample(monthly: TwoYearsOfMonths(), weekly: TwoYearsOfWeeks()));
        timeline.Pan(-2);
        Assert.NotEqual(0, timeline.PanPeriods);

        timeline.Granularity = TimelineGranularity.Weekly;

        Assert.Equal(0, timeline.PanPeriods);
    }

    // Fix pass, review P1: LiveCharts raises DataPointerDownCommand (and so PointClickedCommand)
    // on pointer down, not on a click or a release (verified against
    // LiveChartsCore.SkiaSharpView.Avalonia.xml's "Gets or sets a command to execute when the
    // pointer goes down on a data or data points"). A flag set on release to suppress the *next*
    // click always arrives one gesture late, so the guard is now a deferred click instead:
    // PointClicked records which bar the press landed on and raises nothing; the gesture's own
    // release path (StatisticsView.axaml.cs's HandleReleased in the shipped application, these
    // three cases directly here) commits it for a plain click or discards it for a drag. A real,
    // non-empty point is driven through PointClickedCommand in every case below, so a case that
    // deletes the defer-and-commit mechanism fails here rather than passing on a null no-op.
    private static ChartPoint FakePoint(int index)
    {
        // MappedChartEntity is LiveCharts' own helper for a caller that is not a series' internal
        // point type; its MetaData carries the EntityIndex ChartPoint.Index reads. The index this
        // gives is always 0 (EntityIndex has no public setter, only ever advanced by a series'
        // own internal draw loop), which is why every case below targets bar 0.
        _ = index;
        var entity = new MappedChartEntity { MetaData = new ChartEntityMetaData(_ => { }) };
        return new ChartPoint(null!, null!, entity);
    }

    [Fact]
    public void ADragThatMoved_DiscardsThePendingClick()
    {
        using var timeline = Create();
        var raised = 0;
        timeline.PeriodSelected += (_, _) => raised++;

        // The press.
        timeline.PointClickedCommand.Execute(new[] { FakePoint(0) });
        Assert.Equal(0, raised);

        // The release, for a gesture that moved past the drag threshold.
        timeline.DiscardPendingClick();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ADragThatDidNotMove_CommitsThePendingClick()
    {
        using var timeline = Create();
        var raised = 0;
        timeline.PeriodSelected += (_, _) => raised++;

        // The press.
        timeline.PointClickedCommand.Execute(new[] { FakePoint(0) });
        Assert.Equal(0, raised);

        // The release, for a gesture that never moved: SelectPeriodCommand.Execute
        // (ImagingTimelineViewModelTests' BarClick_RaisesThePeriodsDateRange) is the existing,
        // unmoved proof that SelectPeriod itself still raises PeriodSelected correctly.
        timeline.CommitPendingClick();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void TheClickAfterADrag_NavigatesNormally()
    {
        // The P1 finding's own scenario: a drag discards its own pending click, and the guard
        // must not also swallow the very next, unrelated click.
        using var timeline = Create();
        var raised = 0;
        timeline.PeriodSelected += (_, _) => raised++;

        timeline.PointClickedCommand.Execute(new[] { FakePoint(0) });
        timeline.DiscardPendingClick();
        Assert.Equal(0, raised);

        timeline.PointClickedCommand.Execute(new[] { FakePoint(0) });
        timeline.CommitPendingClick();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ThePresetsAndTheImagedOnlyToggle_BehaveExactlyAsBefore()
    {
        using var timeline = Create();

        timeline.SelectPresetCommand.Execute(TimelineRangePreset.Quarter);
        Assert.Equal(TimelineRangePreset.Quarter, timeline.RangePreset);
        Assert.Equal(TimelineGranularity.Weekly, timeline.Granularity);

        timeline.ImagedOnly = true;
        Assert.True(timeline.ImagedOnly);
        timeline.ImagedOnly = false;
        Assert.False(timeline.ImagedOnly);
    }
}
