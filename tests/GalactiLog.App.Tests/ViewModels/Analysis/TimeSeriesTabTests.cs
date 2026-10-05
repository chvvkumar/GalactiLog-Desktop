using Avalonia.Headless.XUnit;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

/// <summary>
/// Spec 12.14's Time Series tab and its chart (<c>task6.md</c> section 2, required cases 13 to
/// 20d).
/// </summary>
/// <remarks>
/// <para>
/// Windowless throughout, so the inline post seam is the right one (the harness ruling in
/// <c>task6.md</c> section 4): nothing here binds a view, and the cases that SHOW one live in
/// <c>Views/Analysis/TimeSeriesTabViewTests</c> and take the UI-thread seam instead. They are
/// nonetheless <c>AvaloniaFact</c>, because every paint below is a resource lookup through
/// <c>ChartTheme.Read</c> and with no application every token falls back to one neutral grey,
/// which would make a colour assertion pass by comparing grey to grey.
/// </para>
/// <para>
/// Every assertion on a state, a query count or a series collection is preceded by an await on
/// <c>PendingLoad</c>, which is <c>task6.md</c> section 0a's first case rule: a counter alone
/// races the background increment and reads green with the defect present.
/// </para>
/// <para>No case here re-proves a base rule; <c>AnalysisTabBaseTests</c> covers those.</para>
/// </remarks>
public class TimeSeriesTabTests
{
    private static readonly AnalysisFilter AnyFilter =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    // Nine nights whose robust baseline is median 5 and MAD 1, so the three point bands are all
    // occupied: the four 4s are better, the three 5s are neutral, the 7 is watch (z 2.0) and the
    // 20 is reject (z 15). Both are computed by FrameQuality's own ladder, never restated here.
    private static readonly double[] ThreeBandNights = [4d, 4d, 4d, 4d, 5d, 5d, 5d, 7d, 20d];

    private static readonly AnalysisMetric[] PickerOrder = [.. AnalysisMetrics.X, .. AnalysisMetrics.Y];

    // ---- the metric picker -------------------------------------------------------------------

    [AvaloniaFact]
    public async Task TheMetricPicker_OffersTheTwentyMetricsInXThenYOrder_AndNoPhd2Metric()
    {
        var tab = await Loaded((_, _) => Result([1d]));

        var expected = AnalysisMetrics.X
            .Concat(AnalysisMetrics.Y)
            .Select(AnalysisMetricLabels.Label)
            .ToArray();

        // Red against a list built from Enum.GetValues, which offers twenty-five: the five PHD2
        // metrics are Correlation only and this tab's query would refuse them.
        Assert.Equal(expected, tab.MetricChoices.Select(choice => choice.Label));
        Assert.Equal(20, tab.MetricChoices.Count);
        Assert.All(
            AnalysisMetrics.Phd2X,
            metric => Assert.DoesNotContain(metric, tab.MetricChoices.Select(choice => choice.Metric)));

        Assert.Equal(AnalysisMetric.Hfr, tab.Metric);
        Assert.Equal(AnalysisMetric.Hfr, tab.SelectedMetricChoice!.Metric);

        // Every one of the twenty round-trips through the entry the picker shows.
        // Since the fold onto the shared choice shape the selection is carried by the
        // metric itself rather than by a label matched with Array.IndexOf, so a duplicate label can
        // no longer select the earlier metric; the distinctness of the labels is pinned in
        // AnalysisMetricLabelsTests. This arm goes red against a picker that maps back by label
        // text or by position.
        foreach (var metric in PickerOrder)
        {
            tab.SelectedMetricChoice = tab.MetricChoices.Single(choice => choice.Metric == metric);
            Assert.Equal(metric, tab.Metric);
            Assert.Equal(metric, tab.SelectedMetricChoice.Metric);
            Assert.Equal(AnalysisMetricLabels.Label(metric), tab.SelectedMetricChoice.Label);
        }

        // A cleared selection, which a ComboBox writes back on its own, moves nothing.
        var before = tab.Metric;
        tab.SelectedMetricChoice = null;
        Assert.Equal(before, tab.Metric);

        Assert.Equal(20, PickerOrder.Distinct().Count());
        await tab.PendingLoad!;
    }

    [AvaloniaFact]
    public async Task ChoosingAMetricFromThePicker_MovesTheMetricAndRequeries()
    {
        var asked = new List<AnalysisMetric>();
        var tab = await Loaded((metric, _) => { asked.Add(metric); return Result([1d]); });

        tab.SelectedMetricChoice = tab.MetricChoices.Single(
            choice => choice.Metric == AnalysisMetric.Fwhm);
        await tab.PendingLoad!;

        Assert.Equal(AnalysisMetric.Fwhm, tab.Metric);
        Assert.Equal(new[] { AnalysisMetric.Hfr, AnalysisMetric.Fwhm }, asked);
    }

    // ---- case 13, the five layers in one fixed series order -----------------------------------

    [AvaloniaFact]
    public async Task TheChart_DrawsTheFiveLayersInTheFixedSeriesOrder()
    {
        var tab = await Loaded((_, _) => Result(ThreeBandNights, ma7: Average(ThreeBandNights.Length)));
        tab.Smoothing = TimeSeriesSmoothing.Ma7;

        // Band, mask, baseline median, dashed bound, the three tinted point series, the average.
        // Red against a mask drawn before the band: the mask paints out everything below the lower
        // bound, so drawing it first leaves the band filled to the axis floor (spike section 3).
        Assert.Equal(
            new string?[]
            {
                "Baseline band", "Baseline mask", "Baseline median", "3 MAD bound",
                "Nightly median", "Nightly median", "Nightly median", "Moving average",
            },
            Names(tab));

        var series = Chart(tab).Series;
        Assert.IsType<LineSeries<DateTimePoint>>(series[0]);
        Assert.IsType<LineSeries<DateTimePoint>>(series[1]);
        Assert.IsType<ScatterSeries<DateTimePoint>>(series[4]);
        Assert.IsType<LineSeries<DateTimePoint>>(series[7]);

        // The band and the mask are strokeless fills, out of the tooltip, and the mask is opaque.
        Assert.Null(((LineSeries<DateTimePoint>)series[0]).Stroke);
        Assert.Null(((LineSeries<DateTimePoint>)series[1]).Stroke);
        Assert.False(series[0].IsHoverable);
        Assert.False(series[1].IsHoverable);
        // The band's 0x40 is spec 13's own alpha; the mask's 0xFF is ruling A23's "opaquely", and
        // it is pinned because deep-sky declares ColorBgElevated at alpha 0xE6.
        Assert.Equal(0x40, Fill(series[0]).Alpha);
        Assert.Equal(0xFF, Fill(series[1]).Alpha);

        // The band is median + MAD and the mask median - MAD, at the figures FrameQuality's own
        // median and MAD give for this fixture.
        Assert.All(Values(series[0]), value => Assert.Equal(6d, value));
        Assert.All(Values(series[1]), value => Assert.Equal(4d, value));
        Assert.All(Values(series[2]), value => Assert.Equal(5d, value));
    }

    // ---- case 14, three point series, one per band --------------------------------------------

    [AvaloniaFact]
    public async Task TheNightlyPoints_AreThreeSeriesOnePerBand_EachHoldingOnlyItsOwnNights()
    {
        var tab = await Loaded((_, _) => Result(ThreeBandNights));

        // Red against one series with a per-point paint callback, which does not survive a series
        // replacement at rc5.4 (spike trap 10): the count is 1 and the assertion names it.
        var points = Chart(tab).Series.OfType<ScatterSeries<DateTimePoint>>().ToList();
        Assert.Equal(3, points.Count);

        Assert.Equal(new double?[] { 4d, 4d, 4d, 4d, 5d, 5d, 5d }, Values(points[0]));
        Assert.Equal(new double?[] { 7d }, Values(points[1]));
        Assert.Equal(new double?[] { 20d }, Values(points[2]));

        // These three also pin the token READ: the inline post seam publishes from the query's own
        // thread-pool thread, so a chart that looked its tokens up inside its rebuild would take
        // ChartTheme.Read's documented off-thread fallback and paint every layer one neutral grey
        // with nothing else failing. The tokens are read at construction and on ChartTheme.Changed,
        // both on the UI thread (ruling A8, task6.md section 0).
        Assert.Equal(ChartTheme.Read(TimeSeriesChartViewModel.PointKey, ChartTheme.Fallback), Fill(points[0]));
        Assert.Equal(ChartTheme.Read(TimeSeriesChartViewModel.WatchKey, ChartTheme.Fallback), Fill(points[1]));
        Assert.Equal(ChartTheme.Read(TimeSeriesChartViewModel.RejectKey, ChartTheme.Fallback), Fill(points[2]));
        Assert.NotEqual(ChartTheme.Fallback, Fill(points[0]));
    }

    // ---- case 15, the band's two absences -----------------------------------------------------

    [AvaloniaFact]
    public async Task TheBaselineBand_IsAbsentBelowTheMinimumGroup_AbsentAtAZeroMad_AndPresentAtTheMinimum()
    {
        // The constant itself, never a literal 8: a case written against the literal passes after
        // FrameQuality.MinGroup moves and the band then disappears or appears without one failing.
        var sparse = Enumerable.Range(1, FrameQuality.MinGroup - 1).Select(value => (double)value).ToArray();
        var uniform = Enumerable.Repeat(3d, FrameQuality.MinGroup).ToArray();
        var full = Enumerable.Range(1, FrameQuality.MinGroup).Select(value => (double)value).ToArray();

        var sparseTab = await Loaded((_, _) => Result(sparse));
        Assert.False(Chart(sparseTab).HasBaselineBand);
        Assert.DoesNotContain("Baseline band", Names(sparseTab));
        Assert.DoesNotContain("Baseline median", Names(sparseTab));
        Assert.DoesNotContain("3 MAD bound", Names(sparseTab));

        var uniformTab = await Loaded((_, _) => Result(uniform));
        Assert.False(Chart(uniformTab).HasBaselineBand);
        Assert.DoesNotContain("Baseline band", Names(uniformTab));

        var fullTab = await Loaded((_, _) => Result(full));
        Assert.True(Chart(fullTab).HasBaselineBand);
        Assert.Contains("Baseline band", Names(fullTab));

        // The points still draw in all three, which is spec 12.14's own States table row.
        Assert.Equal(3, Chart(sparseTab).Series.OfType<ScatterSeries<DateTimePoint>>().Count());
        Assert.Equal(3, Chart(uniformTab).Series.OfType<ScatterSeries<DateTimePoint>>().Count());
    }

    // ---- the band's two areas close to one floor and never to the library's 0 ------------------

    [AvaloniaFact]
    public async Task TheBandAndItsMask_CloseToOneSharedFloorBelowEveryDrawnValue()
    {
        // A LiveCharts area fill closes to the series' Pivot, default 0. With every night below
        // zero the pixel for 0 sits above the plot, both areas fill UPWARD, and the mask covers the
        // band completely: no band at all, HasBaselineBand true, and no sentence to explain it.
        // Four of the picker's twenty metrics carry negative nightly medians on a winter library.
        // No pixel is asserted here (ruling A28); the series property that decides the direction is.
        double[] negative = [-12d, -11d, -11d, -10d, -10d, -9d, -9d, -8d];
        await AssertOneFloor(negative, expected: -12d);

        // Straddling zero, where the shipped default loses the band's sub-zero half instead.
        double[] crossing = [-4d, -3d, -2d, -1d, 1d, 2d, 3d, 4d];
        await AssertOneFloor(crossing, expected: -4d);

        // Positive data renders as before: the floor is the lower bound itself here, and the two
        // areas' own values are the ones case 13 already pins, unmoved.
        var positive = await AssertOneFloor(ThreeBandNights, expected: 4d);
        Assert.All(Values(positive.Series[0]), value => Assert.Equal(6d, value));
        Assert.All(Values(positive.Series[1]), value => Assert.Equal(4d, value));
        Assert.All(Values(positive.Series[2]), value => Assert.Equal(5d, value));
    }

    private static async Task<TimeSeriesChartViewModel> AssertOneFloor(double[] nights, double expected)
    {
        var chart = Chart(await Loaded((_, _) => Result(nights)));
        Assert.True(chart.HasBaselineBand);

        var band = (LineSeries<DateTimePoint>)chart.Series.Single(series => series.Name == "Baseline band");
        var mask = (LineSeries<DateTimePoint>)chart.Series.Single(series => series.Name == "Baseline mask");

        // One floor, shared, and at or below everything drawn. Red against the library's default of
        // 0, which the two areas carry when nothing sets Pivot.
        Assert.Equal(expected, band.Pivot);
        Assert.Equal(band.Pivot, mask.Pivot);
        Assert.True(
            band.Pivot <= nights.Min(),
            $"The floor {band.Pivot} is above the lowest drawn value {nights.Min()}.");
        Assert.All(Values(mask), value => Assert.True(band.Pivot <= value));

        return chart;
    }

    // ---- case 16, the mirrored 3 MAD bound ----------------------------------------------------

    [Theory]
    [MemberData(nameof(EveryMetric))]
    public async Task TheThreeMadBound_IsMirroredForTheTwoHigherIsBetterMetrics_AndNotForTheOtherEighteen(
        AnalysisMetric metric)
    {
        // One to eight: median 4.5, MAD 2.0, so the bound is 10.5 above and -1.5 mirrored.
        var nights = Enumerable.Range(1, FrameQuality.MinGroup).Select(value => (double)value).ToArray();
        var tab = await Loaded((_, _) => Result(nights));
        tab.Metric = metric;
        await tab.PendingLoad!;

        var mirrored = metric is AnalysisMetric.DetectedStars or AnalysisMetric.SkyQuality;
        Assert.Equal(mirrored, TimeSeriesChartViewModel.HigherIsBetter(metric));

        // Red against a hard-coded upper bound: the two mirrored metrics then read 10.5 and the
        // theory names them.
        var bound = Chart(tab).Series.Single(series => series.Name == "3 MAD bound");
        Assert.All(Values(bound), value => Assert.Equal(mirrored ? -1.5d : 10.5d, value));
    }

    public static TheoryData<AnalysisMetric> EveryMetric()
    {
        var data = new TheoryData<AnalysisMetric>();
        foreach (var metric in AnalysisMetrics.X.Concat(AnalysisMetrics.Y))
        {
            data.Add(metric);
        }

        return data;
    }

    // ---- case 17, the moving average ink ------------------------------------------------------

    [AvaloniaFact]
    public async Task TheMovingAverageLine_IsDrawnInAccent_AndNotInAMetricToken()
    {
        var tab = await Loaded((_, _) => Result(ThreeBandNights, ma7: Average(ThreeBandNights.Length)));
        tab.Smoothing = TimeSeriesSmoothing.Ma7;

        var average = (LineSeries<DateTimePoint>)Chart(tab).Series.Single(series => series.Name == "Moving average");
        var stroke = Assert.IsType<SolidColorPaint>(average.Stroke);

        // Tokens, not rendered colours (ruling A25): in deep-sky metric-hfr and warning are the
        // same hex, so a case that compared two resolved colours could pass in the wrong theme.
        Assert.Equal(ChartTheme.Read(TimeSeriesChartViewModel.AverageKey, ChartTheme.Fallback), stroke.Color);
        Assert.NotEqual(ChartTheme.Read("ColorMetricHfr", ChartTheme.Fallback), stroke.Color);
        Assert.Equal(2f, stroke.StrokeThickness);
        Assert.Null(average.Fill);
    }

    // ---- case 18, a window the library is too short for ---------------------------------------

    [AvaloniaFact]
    public async Task AnEmptyMovingAverage_DrawsNoLine_WithItsOwnSegmentSelected()
    {
        // Below seven nights the query answers an empty Ma7, and below thirty an empty Ma30. The
        // tab draws what it is handed, so both segments draw no line at all.
        var tab = await Loaded((_, _) => Result([1d, 2d, 3d, 4d, 5d]));

        tab.Smoothing = TimeSeriesSmoothing.Ma7;
        Assert.DoesNotContain("Moving average", Names(tab));
        Assert.True(tab.IsMa7);

        tab.Smoothing = TimeSeriesSmoothing.Ma30;
        Assert.DoesNotContain("Moving average", Names(tab));
        Assert.True(tab.IsMa30);
    }

    // ---- case 19, the two tooltip arms --------------------------------------------------------

    [AvaloniaFact]
    public async Task TheTooltip_NamesTheTargetOnASingleTargetNight_AndReadsMixedOtherwise()
    {
        var night = new DateOnly(2025, 6, 1);

        // A named night.
        Assert.Equal(
            "M 31: 1.36 (12 frames)",
            TimeSeriesChartViewModel.Tooltip(new TimeSeriesPoint(night, 1.3567d, "M 31", 1, 12)));

        // Two targets: the seam publishes no name and the view reads "Mixed". This is the common
        // path on the user's own library, where two of three nights carry two targets.
        Assert.Equal(
            "Mixed: 1.36 (12 frames)",
            TimeSeriesChartViewModel.Tooltip(new TimeSeriesPoint(night, 1.3567d, null, 2, 12)));

        // None resolved. Red against a third arm that prints the value with no name and no colon:
        // spec 12.14 states two arms and the pinned TimeSeriesChart.tsx line 165 has two.
        Assert.Equal(
            "Mixed: 1.36 (12 frames)",
            TimeSeriesChartViewModel.Tooltip(new TimeSeriesPoint(night, 1.3567d, null, 0, 12)));

        // Red against a missing F2: an unformatted value prints 1.3567 and a single frame still
        // reads "frames", which is what the pinned line writes.
        Assert.Equal(
            "Mixed: 2.00 (1 frames)",
            TimeSeriesChartViewModel.Tooltip(new TimeSeriesPoint(night, 2d, null, 0, 1)));

        // Every point series carries the formatter, so the strings above are what a hover reads.
        var tab = await Loaded((_, _) => Named(ThreeBandNights));
        Assert.All(
            Chart(tab).Series.OfType<ScatterSeries<DateTimePoint>>(),
            series => Assert.NotNull(series.YToolTipLabelFormatter));

        // The mapping from a hovered point to ITS OWN night. The three
        // series are published ordinary, watch, reject, and each holds only its band's nights, so
        // the watch series' index 0 is the 7 night and the reject series' index 0 is the 20 night.
        // Red against a formatter built over the whole result: index 0 there is the first night of
        // the nine, which names "Night 0" and prints its value, and every other case on this tab
        // stays green.
        var mappings = Chart(tab).PointTooltips;
        Assert.Equal(3, mappings.Count);
        Assert.Equal("Night 0: 4.00 (10 frames)", mappings[0](0));
        Assert.Equal("Night 7: 7.00 (10 frames)", mappings[1](0));
        Assert.Equal("Night 8: 20.00 (10 frames)", mappings[2](0));
    }

    // ---- case 20a, the moving average is plotted verbatim -------------------------------------

    [AvaloniaFact]
    public async Task ASeededMovingAverage_IsPlottedVerbatim_AndNoArithmeticHappensHere()
    {
        // Deliberately NOT the averages of the nightly values: the query sums medians already
        // rounded to 6 (seam ruling P2-8), so an average recomputed here would differ in the sixth
        // decimal on every point and ruling A4 allows no tolerance.
        var nights = new[] { 1d, 2d, 3d, 4d, 5d, 6d, 7d, 8d };
        MovingAveragePoint[] seeded =
        [
            new(new DateOnly(2025, 6, 7), 0.123456d),
            new(new DateOnly(2025, 6, 8), 99.987654d),
        ];

        var tab = await Loaded((_, _) => Result(nights, ma7: seeded));
        tab.Smoothing = TimeSeriesSmoothing.Ma7;

        var average = Chart(tab).Series.Single(series => series.Name == "Moving average");
        Assert.Equal(new double?[] { 0.123456d, 99.987654d }, Values(average));
        Assert.Equal(
            new[] { seeded[0].Date.ToDateTime(TimeOnly.MinValue), seeded[1].Date.ToDateTime(TimeOnly.MinValue) },
            Dates(average));
    }

    // ---- case 20b, the mixed plate scale warning ----------------------------------------------

    [AvaloniaFact]
    public async Task TheMixedPlateScaleWarning_NeedsTwoScalesAndAPixelMetric()
    {
        var scales = 2;
        var tab = Tab((_, _) => Result([1d, 2d], plateScales: scales));
        await tab.PendingLoad!;

        // Two scales on hfr: shown. Red against a tab that does not set the base's count, which is
        // the defect seam ruling S4 exists for: no arm warns at all.
        Assert.Equal(2, tab.DistinctPlateScales);
        Assert.True(tab.PlateScaleWarningVisible);

        // One scale on hfr: not shown.
        scales = 1;
        tab.Refresh();
        await tab.PendingLoad!;
        Assert.Equal(1, tab.DistinctPlateScales);
        Assert.False(tab.PlateScaleWarningVisible);

        // Two scales on a metric that is not measured in pixels: not shown. Red against a tab that
        // does not override IsPixelMetric.
        scales = 2;
        tab.Metric = AnalysisMetric.Fwhm;
        await tab.PendingLoad!;
        Assert.Equal(2, tab.DistinctPlateScales);
        Assert.False(tab.PlateScaleWarningVisible);
    }

    [AvaloniaFact]
    public async Task MovingTheMetricOffHfr_ReEvaluatesTheCallout_BeforeTheQueryLands()
    {
        // The second query is HELD until this case has finished asserting.
        // Without the gate the publish can land first: it clears DistinctPlateScales to 0 and the
        // mapper sets it back, which raises PlateScaleWarningVisible twice on its own, so the
        // assertion below would read green against a tab with no NotifyPixelMetricChanged call
        // whenever the pool thread won the race. It usually lost, which is worse than usually
        // winning.
        using var held = new ManualResetEventSlim(false);
        var queries = 0;
        var tab = Tab((_, _) =>
        {
            if (Interlocked.Increment(ref queries) > 1)
            {
                held.Wait(TimeSpan.FromSeconds(30));
            }

            return Result([1d, 2d], plateScales: 2);
        });

        await tab.PendingLoad!;
        Assert.True(tab.PlateScaleWarningVisible);

        var raised = 0;
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AnalysisTabViewModel.PlateScaleWarningVisible))
            {
                raised++;
            }
        };

        tab.Metric = AnalysisMetric.AduMean;

        // NotifyPixelMetricChanged, not a re-query: the count has not moved and the query that
        // would move it is still held, so nothing else could tell the view the callout's answer
        // changed.
        Assert.Equal(1, raised);
        Assert.False(tab.PlateScaleWarningVisible);
        Assert.Equal(2, tab.DistinctPlateScales);

        held.Set();
        await tab.PendingLoad!;
        Assert.False(tab.PlateScaleWarningVisible);
    }

    // ---- the two axis titles, and no unit on a tick -------------------------------------------

    [AvaloniaFact]
    public async Task TheValueAxisIsTitledWithTheMetricsFullLabel_TheTimeAxisWithDate_AndNoTickSpellsAUnit()
    {
        var tab = await Loaded((_, _) => Result(ThreeBandNights));

        // Two metrics whose unit suffixes differ, one of them empty. Every expectation is built
        // from AnalysisMetricLabels and never spelled here: a unit written into this file would
        // agree with a unit written into the chart and neither would be the table's.
        var ticks = new List<string>();
        foreach (var metric in new[] { AnalysisMetric.Hfr, AnalysisMetric.Eccentricity })
        {
            tab.Metric = metric;
            await tab.PendingLoad!;

            // The full label already carries the unit in its own parenthesis, so the title is
            // where a reader finds it. Red against the axis this chart shipped with, which was
            // titled with nothing at all.
            var value = Assert.IsType<Axis>(Chart(tab).YAxes.Single());
            Assert.Equal(AnalysisMetricLabels.Label(metric), value.Name);
            ticks.Add(value.Labeler(1.25d));

            Assert.Equal(
                TimeSeriesChartViewModel.DateAxisName,
                Assert.IsType<DateTimeAxis>(Chart(tab).XAxes.Single()).Name);
        }

        // Red against a labeler that appends the metric's unit: the two ticks would then differ
        // by the pixel suffix, and the first would carry it.
        Assert.Equal(ticks[0], ticks[1]);
        Assert.DoesNotContain(
            AnalysisMetricLabels.Unit(AnalysisMetric.Hfr).Trim(), ticks[0], StringComparison.Ordinal);

        // The two units are not the same string, so neither arm can pass by comparing a metric
        // with no unit against another metric with no unit.
        Assert.NotEqual(
            AnalysisMetricLabels.Unit(AnalysisMetric.Hfr),
            AnalysisMetricLabels.Unit(AnalysisMetric.Eccentricity));
        Assert.Equal("", AnalysisMetricLabels.Unit(AnalysisMetric.Eccentricity));
    }

    // ---- case 20c, the theme swap -------------------------------------------------------------

    [AvaloniaFact]
    public async Task TheChart_RepaintsEverySeriesOnAThemeSwap_AndStopsAfterItIsDisposed()
    {
        var tab = await Loaded((_, _) => Result(ThreeBandNights, ma7: Average(ThreeBandNights.Length)));
        tab.Smoothing = TimeSeriesSmoothing.Ma7;

        try
        {
            ThemeManager.Apply("luminance");
            AssertPaintsMatchTheTokens(tab);

            var tokenBefore = ChartTheme.Read(TimeSeriesChartViewModel.PointKey, ChartTheme.Fallback);
            var before = Fill(Chart(tab).Series.OfType<ScatterSeries<DateTimePoint>>().First());

            ThemeManager.Apply("deep-sky");

            // The swap has to have moved the ink at all, or the comparison below could not fail.
            Assert.NotEqual(
                tokenBefore,
                ChartTheme.Read(TimeSeriesChartViewModel.PointKey, ChartTheme.Fallback));

            // Red against a chart with no ChartTheme.Subscribe: the after-values keep the first
            // theme's ink (spike trap 5).
            AssertPaintsMatchTheTokens(tab);
            Assert.NotEqual(before, Fill(Chart(tab).Series.OfType<ScatterSeries<DateTimePoint>>().First()));

            // Red against a missing Dispose: the detached view-model still re-paints, and its
            // handler pins it for the life of the process (ChartTheme.cs:117).
            var chart = Chart(tab);
            var series = chart.Series;
            tab.Dispose();
            ThemeManager.Apply("luminance");
            Assert.Same(series, chart.Series);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // ---- case 20d, the empty state's axes ------------------------------------------------------

    [AvaloniaFact]
    public async Task AResultWithNoNights_PublishesOneAxisPerSideAndPlotsNothing()
    {
        var tab = await Loaded((_, _) => Result([]));

        Assert.Equal(AnalysisTabState.Empty, tab.State);

        // rc5.4's chart engine throws "XAxes and YAxes must contain at least one element" on an
        // empty axis collection, and it raises it from a dispatcher post rather than inline, so
        // nothing downstream can catch it (spike trap 11).
        Assert.Single(Chart(tab).XAxes);
        Assert.Single(Chart(tab).YAxes);
        Assert.Empty(Chart(tab).Series);
        Assert.False(Chart(tab).HasBaselineBand);
    }

    // ---- the segment redraws and does not re-query ---------------------------------------------

    [AvaloniaFact]
    public async Task SwitchingTheSmoothingSegment_RedrawsFromTheHeldResult_AndQueriesNothing()
    {
        var queries = 0;
        var tab = await Loaded((_, _) =>
        {
            queries++;
            return Result(
                ThreeBandNights,
                ma7: Average(ThreeBandNights.Length),
                ma30: Average(ThreeBandNights.Length, 42d));
        });

        var first = tab.PendingLoad;
        Assert.Equal(1, queries);

        tab.Smoothing = TimeSeriesSmoothing.Ma7;
        Assert.Contains("Moving average", Names(tab));

        tab.Smoothing = TimeSeriesSmoothing.Ma30;
        Assert.All(
            Values(Chart(tab).Series.Single(series => series.Name == "Moving average")),
            value => Assert.Equal(42d, value));

        tab.Smoothing = TimeSeriesSmoothing.Raw;
        Assert.DoesNotContain("Moving average", Names(tab));

        // Object identity first, because a counter alone races a background increment (section 0a).
        Assert.Same(first, tab.PendingLoad);
        Assert.Equal(1, queries);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static void AssertPaintsMatchTheTokens(TimeSeriesTabViewModel tab)
    {
        var chart = Chart(tab);
        var points = chart.Series.OfType<ScatterSeries<DateTimePoint>>().ToList();

        Assert.Equal(ChartTheme.Read(TimeSeriesChartViewModel.PointKey, ChartTheme.Fallback), Fill(points[0]));
        Assert.Equal(ChartTheme.Read(TimeSeriesChartViewModel.WatchKey, ChartTheme.Fallback), Fill(points[1]));
        Assert.Equal(ChartTheme.Read(TimeSeriesChartViewModel.RejectKey, ChartTheme.Fallback), Fill(points[2]));

        var band = chart.Series.Single(series => series.Name == "Baseline band");
        Assert.Equal(
            ChartTheme.Read(TimeSeriesChartViewModel.PointKey, ChartTheme.Fallback).WithAlpha(0x40),
            Fill(band));

        // The mask token at full alpha, which is ruling A23's "opaquely" and is what makes the
        // mask a mask in deep-sky, where ColorBgElevated itself carries alpha 0xE6.
        var mask = chart.Series.Single(series => series.Name == "Baseline mask");
        Assert.Equal(
            ChartTheme.Read(TimeSeriesChartViewModel.MaskKey, ChartTheme.Fallback).WithAlpha(0xFF),
            Fill(mask));

        foreach (var name in new[] { "Baseline median", "3 MAD bound" })
        {
            var rule = (LineSeries<DateTimePoint>)chart.Series.Single(series => series.Name == name);
            Assert.Equal(
                ChartTheme.Read(TimeSeriesChartViewModel.BaselineKey, ChartTheme.Fallback),
                Assert.IsType<SolidColorPaint>(rule.Stroke).Color);
        }

        var average = (LineSeries<DateTimePoint>)chart.Series.Single(series => series.Name == "Moving average");
        Assert.Equal(
            ChartTheme.Read(TimeSeriesChartViewModel.AverageKey, ChartTheme.Fallback),
            Assert.IsType<SolidColorPaint>(average.Stroke).Color);
    }

    private static TimeSeriesTabViewModel Tab(
        Func<AnalysisMetric, AnalysisFilter, TimeSeriesResult> query)
    {
        // The inline post seam, which is the windowless one. Assigning IsVisible is the whole of
        // how a tab is selected: the base's setter refreshes a tab that has never loaded.
        var tab = new TimeSeriesTabViewModel(() => AnyFilter, query, action => action())
        {
            IsVisible = true,
        };

        return tab;
    }

    private static async Task<TimeSeriesTabViewModel> Loaded(
        Func<AnalysisMetric, AnalysisFilter, TimeSeriesResult> query)
    {
        var tab = Tab(query);
        await tab.PendingLoad!;
        return tab;
    }

    private static TimeSeriesChartViewModel Chart(TimeSeriesTabViewModel tab)
    {
        Assert.NotNull(tab.Chart);
        return tab.Chart;
    }

    private static string?[] Names(TimeSeriesTabViewModel tab)
        => [.. Chart(tab).Series.Select(series => series.Name)];

    private static SKColor Fill(ISeries series) => series switch
    {
        LineSeries<DateTimePoint> line => Assert.IsType<SolidColorPaint>(line.Fill).Color,
        ScatterSeries<DateTimePoint> scatter => Assert.IsType<SolidColorPaint>(scatter.Fill).Color,
        _ => throw new InvalidOperationException($"Not a plotted series: {series.GetType().Name}"),
    };

    private static double?[] Values(ISeries series) => [.. Plotted(series).Select(point => point.Value)];

    private static DateTime[] Dates(ISeries series) => [.. Plotted(series).Select(point => point.DateTime)];

    private static IEnumerable<DateTimePoint> Plotted(ISeries series) => series switch
    {
        LineSeries<DateTimePoint> line => line.Values ?? [],
        ScatterSeries<DateTimePoint> scatter => scatter.Values ?? [],
        _ => throw new InvalidOperationException($"Not a plotted series: {series.GetType().Name}"),
    };

    private static TimeSeriesResult Result(
        IReadOnlyList<double> nights,
        IReadOnlyList<MovingAveragePoint>? ma7 = null,
        IReadOnlyList<MovingAveragePoint>? ma30 = null,
        int plateScales = 1)
        => new(
            [.. nights.Select((value, index) => new TimeSeriesPoint(
                new DateOnly(2025, 6, 1).AddDays(index), value, "M 31", 1, 10))],
            ma7 ?? [],
            ma30 ?? [],
            [],
            plateScales);

    // The same nights, each carrying its own position as its target name, so a tooltip mapping
    // that read the wrong list names a night the assertion can print.
    private static TimeSeriesResult Named(IReadOnlyList<double> nights)
        => new(
            [.. nights.Select((value, index) => new TimeSeriesPoint(
                new DateOnly(2025, 6, 1).AddDays(index), value, $"Night {index}", 1, 10))],
            [],
            [],
            [],
            1);

    private static MovingAveragePoint[] Average(int nights, double value = 3d)
        => [.. Enumerable.Range(0, nights).Select(
            index => new MovingAveragePoint(new DateOnly(2025, 6, 1).AddDays(index), value))];
}
