using Avalonia.Headless.XUnit;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

/// <summary>
/// Task 5 section 3, the Correlation half: the tab's own members and its chart. Nothing here
/// re-asserts a base rule, which <c>AnalysisTabBaseTests</c> already covers green.
/// </summary>
/// <remarks>
/// <para>
/// <b>The post seam.</b> No case in this file shows a bound view, so every one of them takes the
/// windowless seam. It is a collecting seam rather than an inline one: the posted closure is held
/// until the case has awaited <c>PendingLoad</c> and then run on the test's own thread, which
/// under <c>AvaloniaFact</c> is the UI thread. That is what lets a paint assertion read the real
/// token dictionary instead of <c>ChartTheme.Fallback</c>, and it also satisfies section 0's first
/// case rule: no assertion on a query count, a state or a series list runs before the load it
/// depends on has completed.
/// </para>
/// <para>
/// Every case drives the tab with lambdas. There is no stub type and no fake of
/// <c>AnalysisCache</c>.
/// </para>
/// </remarks>
public class CorrelationTabTests
{
    private static readonly AnalysisFilter FrameFilter =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    private static readonly AnalysisFilter SessionFilter =
        new(null, null, null, AnalysisGranularity.Session, null, null);

    // ---- the harness -------------------------------------------------------------------------

    private sealed class Probe : IDisposable
    {
        private readonly List<Action> _posted = [];

        public Probe(
            AnalysisMetric x = AnalysisMetric.Humidity,
            AnalysisMetric y = AnalysisMetric.Hfr,
            CorrelationResult? answer = null,
            AnalysisFilter? filter = null)
        {
            Answer = answer ?? Ready();
            Filter = filter ?? FrameFilter;

            Tab = new CorrelationTabViewModel(
                () => Filter,
                (queryX, queryY, _) =>
                {
                    Queries++;
                    LastPair = (queryX, queryY);
                    return Answer;
                },
                x,
                y,
                (persistedX, persistedY) => Persisted.Add((persistedX, persistedY)),
                _posted.Add);
        }

        public CorrelationTabViewModel Tab { get; }

        public AnalysisFilter Filter { get; set; }

        public CorrelationResult Answer { get; set; }

        public int Queries { get; private set; }

        public (AnalysisMetric X, AnalysisMetric Y) LastPair { get; private set; }

        public List<(AnalysisMetric X, AnalysisMetric Y)> Persisted { get; } = [];

        /// <summary>The first selection, which is what makes the base query at all.</summary>
        public async Task Show()
        {
            Tab.IsVisible = true;
            await Settle();
        }

        /// <summary>Joins the load in flight and then runs whatever it posted, in order.</summary>
        public async Task Settle()
        {
            if (Tab.PendingLoad is { } load)
            {
                await load;
            }

            var pending = _posted.ToArray();
            _posted.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }

        public void Dispose() => Tab.Dispose();
    }

    // ---- result builders ---------------------------------------------------------------------

    private static CorrelationPoint Point(
        double x, double y, bool outlier = false, Guid? target = null, int day = 1)
        => new(x, y, new DateOnly(2026, 3, day), target, outlier);

    private static SummaryStats Stats(int count = 8)
        => new(count, 1.5d, 9.5d, 4.25d, 4d, 1.75d);

    private static TrendLine Trend(
        double slope = 2d, double rSquared = 0.6d, bool band = true)
        => new(
            slope,
            0.5d,
            rSquared,
            0.77d,
            0.71d,
            band ? [new BandPoint(0d, 3d), new BandPoint(10d, 23d)] : [],
            band ? [new BandPoint(0d, 1d), new BandPoint(10d, 19d)] : []);

    private static CorrelationResult Build(
        IReadOnlyList<CorrelationPoint> points,
        TrendLine? trend = null,
        SummaryStats? xStats = null,
        SummaryStats? yStats = null,
        IReadOnlyDictionary<Guid, string>? targetNames = null,
        int plateScales = 1,
        int? total = null,
        int? sampled = null)
        => new(
            points,
            trend,
            xStats,
            yStats,
            targetNames ?? new Dictionary<Guid, string>(),
            trend is null ? CorrelationQuality.TooFewPoints : CorrelationQuality.Ok,
            plateScales,
            total ?? points.Count,
            sampled ?? points.Count);

    /// <summary>Four ordinary points, one outlier, a trend and a confidence band.</summary>
    private static CorrelationResult Ready()
        => Build(
            [
                Point(1d, 2d, day: 1),
                Point(3d, 6d, day: 2),
                Point(5d, 11d, day: 3),
                Point(7d, 15d, day: 4),
                Point(9d, 40d, outlier: true, day: 5),
            ],
            Trend(),
            Stats(),
            Stats());

    private static CorrelationResult NoRows() => Build([]);

    // ---- reading the built series ------------------------------------------------------------

    private static SKColor Colour(object? paint) => Assert.IsType<SolidColorPaint>(paint).Color;

    private static IReadOnlyList<ObservablePoint> Points(ISeries series) => series switch
    {
        LineSeries<ObservablePoint> line => [.. line.Values ?? []],
        ScatterSeries<ObservablePoint> scatter => [.. scatter.Values ?? []],
        ScatterSeries<ObservablePoint, CircleGeometry> rings => [.. rings.Values ?? []],
        _ => [],
    };

    // ---- case 1: the four layers, in the fixed order -----------------------------------------

    // Red against a view-model that appends the mask before the band, which renders the band as a
    // wedge down to the axis floor: the assertion names the swapped index.
    [AvaloniaFact]
    public async Task TheSeries_AreTheFourLayers_InTheOrderBandMaskPointsOutliersTrend()
    {
        using var probe = new Probe();
        await probe.Show();

        var series = probe.Tab.Chart.Series;
        Assert.Equal(5, series.Count);

        Assert.IsType<LineSeries<ObservablePoint>>(series[0]);
        Assert.IsType<LineSeries<ObservablePoint>>(series[1]);
        Assert.IsType<ScatterSeries<ObservablePoint>>(series[2]);
        Assert.IsType<ScatterSeries<ObservablePoint, CircleGeometry>>(series[3]);
        Assert.IsType<LineSeries<ObservablePoint>>(series[4]);

        // The band is the UPPER bound and the mask the lower one, which is what tells the two
        // LineSeries apart: a swap draws the fill above the band instead of under it.
        Assert.Equal(new double?[] { 3d, 23d }, [.. Points(series[0]).Select(point => point.Y)]);
        Assert.Equal(new double?[] { 1d, 19d }, [.. Points(series[1]).Select(point => point.Y)]);

        Assert.Equal(4, Points(series[2]).Count);
        Assert.Single(Points(series[3]));

        // The trend is drawn from the smallest plotted X to the largest, over the full set.
        Assert.Equal(new double?[] { 1d, 9d }, [.. Points(series[4]).Select(point => point.X)]);
    }

    // ---- one shared floor under both band areas -------------------------------------------------

    // A LiveCharts area fill closes to the series' Pivot, default 0, so two areas left at the
    // default fill toward the zero line rather than toward the bottom of the plot: with a lower
    // bound below zero the mask stops covering the band, and with both bounds below zero it
    // swallows it entirely. Both areas therefore take one floor at or below everything drawn.
    //
    // Red against the shipped default on the first two rows, where both Pivot values are 0 and 0
    // sits above the lowest drawn value. The third row is the positive data case 1 already drives
    // and proves the fix changes nothing there.
    [AvaloniaTheory]
    [InlineData("wholly below zero", -9d, -4d, -6d, -9d)]
    [InlineData("straddling zero", -3d, 5d, 1.5d, -3d)]
    [InlineData("wholly positive", 1d, 19d, 2d, 1d)]
    public async Task TheBandAndItsMask_CloseToOneSharedFloorBelowEveryDrawnValue(
        string shape, double lowerBound, double upperBound, double lowestPoint, double expectedFloor)
    {
        Assert.NotEmpty(shape);

        using var probe = new Probe(
            answer: Build(
                [
                    new CorrelationPoint(1d, lowestPoint, new DateOnly(2026, 3, 1), null, false),
                    new CorrelationPoint(3d, lowestPoint + 4d, new DateOnly(2026, 3, 2), null, false),
                    new CorrelationPoint(5d, lowestPoint + 9d, new DateOnly(2026, 3, 3), null, false),
                ],
                new TrendLine(
                    2d, 0.5d, 0.6d, 0.77d, 0.71d,
                    [new BandPoint(1d, upperBound), new BandPoint(5d, upperBound + 1d)],
                    [new BandPoint(1d, lowerBound), new BandPoint(5d, lowerBound + 1d)]),
                Stats(3),
                Stats(3)));

        await probe.Show();

        var band = Assert.IsType<LineSeries<ObservablePoint>>(probe.Tab.Chart.Series[0]);
        var mask = Assert.IsType<LineSeries<ObservablePoint>>(probe.Tab.Chart.Series[1]);

        Assert.Equal(band.Pivot, mask.Pivot);
        Assert.Equal(expectedFloor, band.Pivot);

        // And the floor is really under everything drawn, which is the property the figure above
        // is only one witness of.
        var drawn = probe.Tab.Chart.Series
            .SelectMany(Points)
            .Select(point => point.Y ?? 0d)
            .ToArray();

        Assert.NotEmpty(drawn);
        Assert.All(drawn, value => Assert.True(
            band.Pivot <= value,
            $"the floor {band.Pivot} sits above a drawn value of {value} on a band {shape}"));
    }

    // The floor is taken over the FULL point set, so hiding the outliers cannot move it and the
    // band is what it was before the toggle. Red against a floor computed over the drawn points.
    [AvaloniaFact]
    public async Task TheBandsFloor_IsTakenOverTheFullPointSet_SoHidingOutliersDoesNotMoveIt()
    {
        using var probe = new Probe(
            answer: Build(
                [
                    new CorrelationPoint(1d, 5d, new DateOnly(2026, 3, 1), null, false),
                    new CorrelationPoint(3d, 6d, new DateOnly(2026, 3, 2), null, false),
                    new CorrelationPoint(5d, 7d, new DateOnly(2026, 3, 3), null, false),
                    new CorrelationPoint(7d, -20d, new DateOnly(2026, 3, 4), null, true),
                ],
                Trend(),
                Stats(4),
                Stats(4)));

        await probe.Show();

        var before = ((LineSeries<ObservablePoint>)probe.Tab.Chart.Series[0]).Pivot;
        Assert.Equal(-20d, before);

        probe.Tab.ToggleOutliersCommand.Execute(null);

        Assert.Equal(before, ((LineSeries<ObservablePoint>)probe.Tab.Chart.Series[0]).Pivot);
        Assert.Equal(before, ((LineSeries<ObservablePoint>)probe.Tab.Chart.Series[1]).Pivot);
    }

    // The helper both band charts on this page share. Its two members are the whole of the pattern
    // that had already diverged between this tab and Time Series (design lesson 1).
    [Fact]
    public void BandAreas_AnswersTheLowerOfTheTwoMinima_AndPinsTheMasksAlpha()
    {
        Assert.Equal(-4d, BandAreas.Floor([-1d, 2d], [-4d, 9d]));
        Assert.Equal(-7d, BandAreas.Floor([-7d, 2d], [0.5d, 9d]));
        Assert.Equal(0.5d, BandAreas.Floor([3d, 2d], [0.5d, 9d]));

        var translucent = new SKColor(0x18, 0x1B, 0x26, 0xE6);
        var opaque = BandAreas.OpaqueMask(translucent);

        Assert.Equal(0xFF, opaque.Alpha);
        Assert.Equal(
            (translucent.Red, translucent.Green, translucent.Blue),
            (opaque.Red, opaque.Green, opaque.Blue));
    }

    // ---- case 2: hover ------------------------------------------------------------------------

    // Red against a copy that forgets one: the band enters the tooltip.
    //
    // The trend line is the third layer that must stay out of it, and it is the one the shipped
    // build got wrong. Spec 12.14 gives the tooltip to a POINT; a line series' hover area is a
    // full axis unit wide and this one carries no tooltip formatter, so under the chart's declared
    // CompareOnlyXTakeClosest strategy it answers a raw default tooltip at both ends of the line.
    // Red against the shipped series, which set no IsHoverable on it.
    [AvaloniaFact]
    public async Task TheBandTheMaskAndTheTrend_AreNotHoverable_AndThePointsAre()
    {
        using var probe = new Probe();
        await probe.Show();

        var series = probe.Tab.Chart.Series;
        Assert.False(series[0].IsHoverable);
        Assert.False(series[1].IsHoverable);
        Assert.True(series[2].IsHoverable);
        Assert.True(series[3].IsHoverable);
        Assert.False(series[4].IsHoverable);
    }

    // ---- case 4: a theme swap re-paints all five ---------------------------------------------

    // Red against a view-model with no ChartTheme.Subscribe: the after-values keep the first
    // theme's ink. Red against a paint read from ChartTheme.Palette: ColorMetricWorst is not in
    // MetricTokenOrder and the read does not compile.
    [AvaloniaFact]
    public async Task AThemeSwap_RepaintsEverySeries_AgainstChartThemeRead()
    {
        using var probe = new Probe();

        try
        {
            ThemeManager.Apply("luminance");
            await probe.Show();

            AssertPaints(probe.Tab.Chart, "luminance");
            var maskBefore = Colour(((LineSeries<ObservablePoint>)probe.Tab.Chart.Series[1]).Fill);
            var bandBefore = Colour(((LineSeries<ObservablePoint>)probe.Tab.Chart.Series[0]).Fill);

            ThemeManager.Apply("deep-sky");

            // Not vacuous: the two themes declare different values for both tokens, so the second
            // reading below is a change and not a coincidence. The mask is checked on its colour
            // channels rather than on its whole value, because its alpha is pinned and therefore
            // the same in both themes.
            Assert.NotEqual(
                maskBefore,
                Colour(((LineSeries<ObservablePoint>)probe.Tab.Chart.Series[1]).Fill));
            Assert.NotEqual(
                bandBefore,
                Colour(((LineSeries<ObservablePoint>)probe.Tab.Chart.Series[0]).Fill));

            AssertPaints(probe.Tab.Chart, "deep-sky");
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }

        // Coordinator ruling after the Time Series unit's finding: ChartTheme.Read degrades to
        // ChartTheme.Fallback off the UI thread and throws nothing, so an equality against it can
        // compare the fallback with itself. Every token is therefore asserted to have RESOLVED, and
        // the four roles are asserted to differ from one another, in both themes; a hard-coded
        // literal that happens to equal one theme's token cannot survive the second.
        static void AssertPaints(CorrelationChartViewModel chart, string theme)
        {
            var band = ChartTheme.Read(CorrelationChartViewModel.BandTokenKey, ChartTheme.Fallback);
            var mask = ChartTheme.Read(CorrelationChartViewModel.MaskTokenKey, ChartTheme.Fallback);
            var point = ChartTheme.Read(CorrelationChartViewModel.PointTokenKey, ChartTheme.Fallback);
            var outlier = ChartTheme.Read(CorrelationChartViewModel.OutlierTokenKey, ChartTheme.Fallback);

            foreach (var (key, colour) in new[]
                     {
                         (CorrelationChartViewModel.BandTokenKey, band),
                         (CorrelationChartViewModel.MaskTokenKey, mask),
                         (CorrelationChartViewModel.PointTokenKey, point),
                         (CorrelationChartViewModel.OutlierTokenKey, outlier),
                     })
            {
                Assert.True(
                    colour != ChartTheme.Fallback,
                    $"{theme} resolved no {key}: the read answered the documented neutral, so an "
                    + "equality against it would compare the fallback with itself.");
            }

            Assert.Equal(4, new[] { band, mask, point, outlier }.Distinct().Count());

            Assert.Equal(
                band.WithAlpha(0x40),
                Colour(((LineSeries<ObservablePoint>)chart.Series[0]).Fill));
            // The mask carries the token's own red, green and blue and a PINNED full alpha, which
            // is ruling A23's word "opaquely". Only deep-sky declares
            // ColorBgElevated below full alpha, so this half of the assertion is what that theme's
            // round exists for.
            var maskFill = Colour(((LineSeries<ObservablePoint>)chart.Series[1]).Fill);
            Assert.Equal(new SKColor(mask.Red, mask.Green, mask.Blue, 0xFF), maskFill);
            Assert.Equal(point, Colour(((ScatterSeries<ObservablePoint>)chart.Series[2]).Fill));
            Assert.Equal(
                outlier,
                Colour(((ScatterSeries<ObservablePoint, CircleGeometry>)chart.Series[3]).Stroke));
            Assert.Equal(band, Colour(((LineSeries<ObservablePoint>)chart.Series[4]).Stroke));
        }
    }

    // ---- case 5: the two geometry sizes -------------------------------------------------------

    // Spec 13: a session-granularity point draws at 6 pixels and a frame one at 4. Red against a
    // fixed 5.
    [AvaloniaTheory]
    [InlineData(AnalysisGranularity.Frame, 4d)]
    [InlineData(AnalysisGranularity.Session, 6d)]
    public async Task TheGeometrySize_FollowsTheGranularity(AnalysisGranularity granularity, double expected)
    {
        using var probe = new Probe(
            filter: granularity == AnalysisGranularity.Session ? SessionFilter : FrameFilter);
        await probe.Show();

        Assert.Equal(
            expected,
            ((ScatterSeries<ObservablePoint>)probe.Tab.Chart.Series[2]).GeometrySize);
    }

    // ---- case 6: Hide Outliers ----------------------------------------------------------------

    // Red against a toggle that re-queries: the query counter moves. Red against one that recomputes
    // the trend or the band over the filtered points: those two assertions fail.
    [AvaloniaFact]
    public async Task HideOutliers_EmptiesTheRingSeries_AndChangesNoFigure()
    {
        using var probe = new Probe();
        await probe.Show();

        var queriesBefore = probe.Queries;
        var ordinaryBefore = Points(probe.Tab.Chart.Series[2]).Select(point => (point.X, point.Y)).ToArray();
        var bandBefore = Points(probe.Tab.Chart.Series[0]).Select(point => (point.X, point.Y)).ToArray();
        var maskBefore = Points(probe.Tab.Chart.Series[1]).Select(point => (point.X, point.Y)).ToArray();
        var trendBefore = Points(probe.Tab.Chart.Series[4]).Select(point => (point.X, point.Y)).ToArray();
        var xCardBefore = probe.Tab.XCard;
        var yCardBefore = probe.Tab.YCard;
        var verdictBefore = probe.Tab.Verdict;

        Assert.Equal(CorrelationTabViewModel.HideOutliersLabel, probe.Tab.OutliersToggleLabel);
        Assert.Single(Points(probe.Tab.Chart.Series[3]));

        probe.Tab.ToggleOutliersCommand.Execute(null);

        Assert.True(probe.Tab.OutliersHidden);
        Assert.Equal(CorrelationTabViewModel.ShowOutliersLabel, probe.Tab.OutliersToggleLabel);
        Assert.Equal(queriesBefore, probe.Queries);

        Assert.Empty(Points(probe.Tab.Chart.Series[3]));
        Assert.Equal(
            ordinaryBefore,
            Points(probe.Tab.Chart.Series[2]).Select(point => (point.X, point.Y)).ToArray());
        Assert.Equal(
            bandBefore,
            Points(probe.Tab.Chart.Series[0]).Select(point => (point.X, point.Y)).ToArray());
        Assert.Equal(
            maskBefore,
            Points(probe.Tab.Chart.Series[1]).Select(point => (point.X, point.Y)).ToArray());
        Assert.Equal(
            trendBefore,
            Points(probe.Tab.Chart.Series[4]).Select(point => (point.X, point.Y)).ToArray());

        // The two cards and the sentence were computed before the toggle was read, so they are the
        // very same objects.
        Assert.Same(xCardBefore, probe.Tab.XCard);
        Assert.Same(yCardBefore, probe.Tab.YCard);
        Assert.Equal(verdictBefore, probe.Tab.Verdict);

        // And back again.
        probe.Tab.ToggleOutliersCommand.Execute(null);
        Assert.False(probe.Tab.OutliersHidden);
        Assert.Single(Points(probe.Tab.Chart.Series[3]));
        Assert.Equal(queriesBefore, probe.Queries);
    }

    // ---- case 7: a preset sets both pickers and fires ONE query --------------------------------

    // Red against two writes: the counter reads 2 and the persist list holds two entries.
    [AvaloniaFact]
    public async Task APreset_SetsBothPickers_AndFiresOneQuery()
    {
        using var probe = new Probe();
        await probe.Show();

        var queriesBefore = probe.Queries;
        probe.Persisted.Clear();

        var preset = probe.Tab.Presets.Single(entry => entry.Label == "Wind vs Guiding");
        preset.Command.Execute(null);
        await probe.Settle();

        Assert.Equal(queriesBefore + 1, probe.Queries);
        Assert.Equal(AnalysisMetric.WindSpeed, probe.Tab.XMetric);
        Assert.Equal(AnalysisMetric.GuidingRms, probe.Tab.YMetric);
        Assert.Equal((AnalysisMetric.WindSpeed, AnalysisMetric.GuidingRms), probe.LastPair);
        Assert.Equal(
            (AnalysisMetric.WindSpeed, AnalysisMetric.GuidingRms),
            Assert.Single(probe.Persisted));

        // Both pickers followed, and the preset draws selected while the others do not.
        Assert.Equal(AnalysisMetric.WindSpeed, probe.Tab.SelectedXOption!.Metric);
        Assert.Equal(AnalysisMetric.GuidingRms, probe.Tab.SelectedYOption!.Metric);
        Assert.True(preset.IsSelected);
        Assert.All(
            probe.Tab.Presets.Where(entry => entry != preset),
            entry => Assert.False(entry.IsSelected));
    }

    // ---- case 8: the six presets are exactly the spec table ------------------------------------

    // Red against a transposed pair.
    [Theory]
    [InlineData(0, "Humidity vs HFR", AnalysisMetric.Humidity, AnalysisMetric.Hfr)]
    [InlineData(1, "Airmass vs FWHM", AnalysisMetric.Airmass, AnalysisMetric.Fwhm)]
    [InlineData(2, "Wind vs Guiding", AnalysisMetric.WindSpeed, AnalysisMetric.GuidingRms)]
    [InlineData(3, "Temp vs Eccentricity", AnalysisMetric.AmbientTemp, AnalysisMetric.Eccentricity)]
    [InlineData(4, "Sky Quality vs Stars", AnalysisMetric.SkyQuality, AnalysisMetric.DetectedStars)]
    [InlineData(5, "Guiding vs FWHM", AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Fwhm)]
    public void ThePresets_AreExactlyTheSpecTable(int index, string label, AnalysisMetric x, AnalysisMetric y)
    {
        using var probe = new Probe();

        Assert.Equal(6, probe.Tab.Presets.Count);
        Assert.Equal(label, probe.Tab.Presets[index].Label);
        Assert.Equal(x, probe.Tab.Presets[index].X);
        Assert.Equal(y, probe.Tab.Presets[index].Y);
    }

    // ---- the X picker's group header -----------------------------------------------------------

    // Section 1.1: the eleventh entry is the "Guiding (PHD2)" header, which carries no metric and
    // cannot be selected. Red against a plain string list: the header becomes a selectable X metric
    // and AnalysisMetrics.Parse answers null for it at query time.
    [AvaloniaFact]
    public async Task TheXPickersGroupHeader_CannotBeSelected()
    {
        using var probe = new Probe();
        await probe.Show();

        Assert.Equal(
            AnalysisMetrics.X.Count + 1 + AnalysisMetrics.Phd2X.Count,
            probe.Tab.XOptions.Count);

        var header = probe.Tab.XOptions[AnalysisMetrics.X.Count];
        Assert.Equal(CorrelationTabViewModel.Phd2GroupHeader, header.Label);
        Assert.True(header.IsHeader);
        Assert.False(header.IsSelectable);
        Assert.All(
            probe.Tab.XOptions.Where(option => option != header),
            option => Assert.True(option.IsSelectable));

        var queriesBefore = probe.Queries;
        var before = probe.Tab.XMetric;

        probe.Tab.SelectedXOption = header;
        await probe.Settle();

        Assert.Equal(before, probe.Tab.XMetric);
        Assert.Equal(queriesBefore, probe.Queries);
        Assert.Empty(probe.Persisted);

        // The Y picker offers the ten Y metrics and no header.
        Assert.Equal(AnalysisMetrics.Y.Count, probe.Tab.YOptions.Count);
        Assert.All(probe.Tab.YOptions, option => Assert.True(option.IsSelectable));
    }

    // Moving a picker writes both stored keys in ONE call and issues one query.
    [AvaloniaFact]
    public async Task MovingTheXPicker_WritesBothKeysOnce_AndQueriesOnce()
    {
        using var probe = new Probe();
        await probe.Show();

        var queriesBefore = probe.Queries;
        probe.Persisted.Clear();

        probe.Tab.SelectedXOption = probe.Tab.XOptions.Single(
            option => option.Metric == AnalysisMetric.Airmass);
        await probe.Settle();

        Assert.Equal(queriesBefore + 1, probe.Queries);
        Assert.Equal((AnalysisMetric.Airmass, AnalysisMetric.Hfr), Assert.Single(probe.Persisted));
        Assert.Equal((AnalysisMetric.Airmass, AnalysisMetric.Hfr), probe.LastPair);
    }

    // ---- case 9: the verdict's seven arms ------------------------------------------------------

    // Each arm asserts the exact clause, and the two absence assertions are what go red against a
    // verbatim port of the web's wording, which is spec 12.14 departure 7.
    [Theory]
    [InlineData(0.02d, 1d, CorrelationVerdict.NoMeaningfulBand,
        "humidity does not appear to move your HFR.")]
    [InlineData(0.02d, -1d, CorrelationVerdict.NoMeaningfulBand,
        "humidity does not appear to move your HFR.")]
    [InlineData(0.1d, 1d, CorrelationVerdict.WeakBand,
        "HFR tends to be slightly higher at higher humidity, but the effect is minor.")]
    [InlineData(0.1d, -1d, CorrelationVerdict.WeakBand,
        "HFR tends to be slightly lower at higher humidity, but the effect is minor.")]
    [InlineData(0.3d, 1d, CorrelationVerdict.ModerateBand,
        "Higher humidity goes with higher HFR in your data.")]
    [InlineData(0.3d, -1d, CorrelationVerdict.ModerateBand,
        "Higher humidity goes with lower HFR in your data.")]
    [InlineData(0.8d, 1d, CorrelationVerdict.StrongBand,
        "HFR rises with humidity in your data. This is a strong pattern at your site.")]
    [InlineData(0.8d, -1d, CorrelationVerdict.StrongBand,
        "HFR falls as humidity rises in your data. This is a strong pattern at your site.")]
    public void TheVerdict_NamesItsBand_AndItsClauseIsDirectionNeutral(
        double rSquared, double slope, string band, string clause)
    {
        var sentence = CorrelationVerdict.Describe(
            new TrendLine(slope, 0.5d, rSquared, 0.77d, 0.71d, [], []),
            12,
            AnalysisMetric.Humidity,
            AnalysisMetric.Hfr);

        Assert.StartsWith(band + " (R2=", sentence, StringComparison.Ordinal);
        Assert.EndsWith(clause, sentence, StringComparison.Ordinal);
        AssertNeutral(sentence);
    }

    // The eighth arm: no trend, or fewer than three points.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(12, true)]
    public void TheVerdict_ReadsNotEnoughData_BelowThreePointsOrWithNoTrend(int points, bool nullTrend)
    {
        var sentence = CorrelationVerdict.Describe(
            nullTrend ? null : Trend(),
            points,
            AnalysisMetric.Humidity,
            AnalysisMetric.Hfr);

        Assert.Equal($"Not enough data to determine a pattern ({points} points).", sentence);
    }

    // The three figures, each to two decimals, in the web's own order and with its own separators.
    [Fact]
    public void TheVerdict_PrintsItsThreeFigures_ToTwoDecimalsEach()
        => Assert.Equal(
            "Strong correlation (R2=0.62, Pearson r=0.79, Spearman rho=0.74). "
            + "HFR rises with humidity in your data. This is a strong pattern at your site.",
            CorrelationVerdict.Describe(
                new TrendLine(1.5d, 0.5d, 0.6234d, 0.7851d, 0.7449d, [], []),
                40,
                AnalysisMetric.Humidity,
                AnalysisMetric.Hfr));

    // Departure 7's own defect: detected_stars is higher-is-better, and the web's clause would call
    // a rising slope a "negative impact". Ruling P2-4: sky_quality cannot be used here, because it
    // is an X-only metric and the clause reads the Y one.
    [Theory]
    [InlineData(0.3d, 1d, "Higher humidity goes with higher star count in your data.")]
    [InlineData(0.8d, 1d,
        "star count rises with humidity in your data. This is a strong pattern at your site.")]
    [InlineData(0.8d, -1d,
        "star count falls as humidity rises in your data. This is a strong pattern at your site.")]
    public void TheVerdict_ReadsCorrectly_ForAHigherIsBetterYMetric(
        double rSquared, double slope, string clause)
    {
        var sentence = CorrelationVerdict.Describe(
            new TrendLine(slope, 0.5d, rSquared, 0.77d, 0.71d, [], []),
            12,
            AnalysisMetric.Humidity,
            AnalysisMetric.DetectedStars);

        Assert.EndsWith(clause, sentence, StringComparison.Ordinal);
        AssertNeutral(sentence);
    }

    // A slope of exactly zero takes the falling arm, which is what trend.slope > 0 does in the web.
    [Fact]
    public void TheVerdict_TreatsAFlatSlope_AsFalling()
        => Assert.EndsWith(
            "Higher humidity goes with lower HFR in your data.",
            CorrelationVerdict.Describe(
                new TrendLine(0d, 0.5d, 0.3d, 0.1d, 0.1d, [], []),
                12,
                AnalysisMetric.Humidity,
                AnalysisMetric.Hfr),
            StringComparison.Ordinal);

    [Fact]
    public void TheDisclaimer_IsSpec1214sSentence()
        => Assert.Equal(
            "Correlations show statistical associations, not causation. Many factors affect image "
            + "quality simultaneously.",
            CorrelationVerdict.Disclaimer);

    private static void AssertNeutral(string sentence)
    {
        foreach (var banned in new[] { "negative impact", "improves", "worse", "better" })
        {
            Assert.DoesNotContain(banned, sentence, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- the verdict's point count -----------------------------------------------------------------

    // Pinned CorrelationChart.tsx draws the count on EVERY band, whenever there are points, as its
    // own opacity-60 span, and spec 12.14 line 7679 says "The point count follows in the tertiary
    // ink". Red against the shipped tree, which drew a count only inside the too-few sentence.
    //
    // The second half is the one the web itself gets wrong: it passes the FILTERED set into the
    // chart, so hiding the outliers drops its count, while spec 12.14 line 7660 promises the counts
    // are unchanged. Red against a port that reads the drawn set.
    [AvaloniaFact]
    public async Task ThePointCount_IsDrawnOnEveryBand_AndHidingOutliersDoesNotMoveIt()
    {
        var points = new List<CorrelationPoint>();
        for (var index = 0; index < 40; index++)
        {
            points.Add(Point(index, index * 1.5d, outlier: index >= 36, day: (index % 27) + 1));
        }

        using var probe = new Probe(answer: Build(points, Trend(), Stats(40), Stats(40)));
        await probe.Show();

        Assert.Equal(AnalysisTabState.Ready, probe.Tab.State);
        Assert.Equal("(40 points)", probe.Tab.PointCount);

        // The verdict itself is a band sentence, not the too-few one, so the count is a separate
        // element and not a repeat of anything inside it.
        Assert.DoesNotContain("points", probe.Tab.Verdict, StringComparison.Ordinal);

        probe.Tab.ToggleOutliersCommand.Execute(null);

        Assert.True(probe.Tab.OutliersHidden);
        Assert.Equal("(40 points)", probe.Tab.PointCount);
        Assert.Empty(Points(probe.Tab.Chart.Series[3]));
    }

    [AvaloniaFact]
    public async Task ThePointCount_IsEmpty_InTheNoRowsState()
    {
        using var probe = new Probe(answer: NoRows());
        await probe.Show();

        Assert.Equal(AnalysisTabState.Empty, probe.Tab.State);
        Assert.Equal(string.Empty, probe.Tab.PointCount);
    }

    // ---- a seeded metric outside a picker's list ---------------------------------------------------

    // AnalysisDisplay clamps both stored keys and the Matrix route supplies only list members, so
    // this shape is unreachable today. It is pinned because the clamp is the kind of guard a later
    // signature change silently removes: the picker draws blank, nothing throws, the query still
    // runs with what it was handed, and nothing is written back.
    [AvaloniaFact]
    public async Task AnXMetricOutsideThePickersList_LeavesTheSelectionNull_AndStrandsNothing()
    {
        using var probe = new Probe(x: AnalysisMetric.Hfr);
        await probe.Show();

        Assert.Null(probe.Tab.SelectedXOption);
        Assert.Equal(1, probe.Queries);
        Assert.Equal((AnalysisMetric.Hfr, AnalysisMetric.Hfr), probe.LastPair);
        Assert.Empty(probe.Persisted);
        Assert.Equal(AnalysisTabState.Ready, probe.Tab.State);
    }

    // ---- the preset already selected does nothing ----------------------------------------------

    // Red against ApplyPreset with no equality guard: the counter and the persist list both move.
    [AvaloniaFact]
    public async Task APresetAlreadySelected_WritesNothing_AndQueriesNothing()
    {
        using var probe = new Probe();
        await probe.Show();

        var preset = probe.Tab.Presets.Single(entry => entry.Label == "Humidity vs HFR");
        Assert.True(preset.IsSelected);

        var queriesBefore = probe.Queries;
        probe.Persisted.Clear();

        preset.Command.Execute(null);
        await probe.Settle();

        Assert.Equal(queriesBefore, probe.Queries);
        Assert.Empty(probe.Persisted);
        Assert.Equal(AnalysisMetric.Humidity, probe.Tab.XMetric);
        Assert.Equal(AnalysisMetric.Hfr, probe.Tab.YMetric);
    }

    // ---- case 10: the PHD2 note ----------------------------------------------------------------

    // Red against a check on x.ToString().StartsWith("Phd2"), which is also correct today and is the
    // shape to avoid: this asserts membership of AnalysisMetrics.Phd2X.
    [Fact]
    public void ThePhd2Note_ShowsForTheFivePhd2XMetrics_AndForNoOther()
    {
        var wrong = new List<string>();

        foreach (var metric in AnalysisMetrics.X.Concat(AnalysisMetrics.Phd2X))
        {
            using var probe = new Probe(x: metric);
            var expected = AnalysisMetrics.Phd2X.Contains(metric);

            if (probe.Tab.Phd2NoteVisible != expected)
            {
                wrong.Add($"{metric}: note {probe.Tab.Phd2NoteVisible}, expected {expected}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            "The PHD2 join note follows membership of AnalysisMetrics.Phd2X. Wrong:"
            + Environment.NewLine + string.Join(Environment.NewLine, wrong));

        Assert.Equal(
            "Night-level PHD2 figures joined by rig and imaging night; nights with no mapped PHD2 "
            + "profile are omitted.",
            CorrelationTabViewModel.Phd2NoteText);
    }

    // The note follows the picker, not just the seeded pair.
    [AvaloniaFact]
    public async Task ThePhd2Note_FollowsThePicker()
    {
        using var probe = new Probe();
        await probe.Show();
        Assert.False(probe.Tab.Phd2NoteVisible);

        probe.Tab.SelectedXOption = probe.Tab.XOptions.Single(
            option => option.Metric == AnalysisMetric.Phd2SnrMean);
        await probe.Settle();

        Assert.True(probe.Tab.Phd2NoteVisible);
    }

    // ---- case 11: the sampling sentence ---------------------------------------------------------

    // Ruling P2-1: the condition is SampledCount < TotalCount and nothing else. The third row is the
    // arm a 5,000 literal gets wrong; a missing N0 fails the first.
    [AvaloniaTheory]
    [InlineData(5000, 12345,
        "Showing 5,000 of 12,345 frames (sampled for display; trend and statistics use all frames)")]
    [InlineData(4, 4, "")]
    [InlineData(40, 41,
        "Showing 40 of 41 frames (sampled for display; trend and statistics use all frames)")]
    public async Task TheSamplingSentence_KeysOnSampledBelowTotal(int sampled, int total, string expected)
    {
        using var probe = new Probe(
            answer: Build(
                [Point(1d, 2d), Point(3d, 6d), Point(5d, 11d), Point(7d, 15d)],
                Trend(),
                Stats(),
                Stats(),
                total: total,
                sampled: sampled));

        await probe.Show();

        Assert.Equal(expected, probe.Tab.SamplingSentence);
    }

    // ---- case 12: one, two and three points -------------------------------------------------------

    // Spec 12.14's States table. Red against a view-model that draws a trend at 2 points.
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BelowThreePoints_ThePointsDraw_AndNothingElseDoes(int points)
    {
        using var probe = new Probe(
            answer: Build(
                [.. Enumerable.Range(0, points).Select(index => Point(index, index * 2d, day: index + 1))]));

        await probe.Show();

        Assert.Equal(AnalysisTabState.Ready, probe.Tab.State);

        // Two series only: the ordinary points and an empty ring series. No band, no mask, no trend.
        Assert.Equal(2, probe.Tab.Chart.Series.Count);
        Assert.IsType<ScatterSeries<ObservablePoint>>(probe.Tab.Chart.Series[0]);
        Assert.IsType<ScatterSeries<ObservablePoint, CircleGeometry>>(probe.Tab.Chart.Series[1]);
        Assert.Equal(points, Points(probe.Tab.Chart.Series[0]).Count);

        Assert.Equal($"Not enough data to determine a pattern ({points} points).", probe.Tab.Verdict);

        // Absent stats hide the pair of cards rather than showing zeroes.
        Assert.False(probe.Tab.StatsVisible);
        Assert.False(probe.Tab.XCard.IsVisible);
        Assert.False(probe.Tab.YCard.IsVisible);
    }

    // The chart's own gate, proved against a result that carries a trend at two points, which is a
    // shape the query never answers but which is exactly what the gate exists to refuse. Red
    // against a view-model that draws the trend at any point count, and red against one that draws
    // the band whenever a trend is present.
    [AvaloniaFact]
    public async Task AtTwoPoints_NeitherTheTrendNorTheBandIsDrawn_EvenWithATrendOnTheResult()
    {
        using var probe = new Probe(
            answer: Build(
                [Point(1d, 2d, day: 1), Point(3d, 6d, day: 2)],
                Trend(),
                Stats(2),
                Stats(2)));

        await probe.Show();

        Assert.Equal(2, probe.Tab.Chart.Series.Count);
        Assert.IsType<ScatterSeries<ObservablePoint>>(probe.Tab.Chart.Series[0]);
        Assert.IsType<ScatterSeries<ObservablePoint, CircleGeometry>>(probe.Tab.Chart.Series[1]);
        Assert.Equal("Not enough data to determine a pattern (2 points).", probe.Tab.Verdict);
    }

    [AvaloniaFact]
    public async Task AtThreePoints_TheTrendTheBandAndTheVerdictAppear()
    {
        using var probe = new Probe(
            answer: Build(
                [Point(1d, 2d, day: 1), Point(3d, 6d, day: 2), Point(5d, 11d, day: 3)],
                Trend(),
                Stats(3),
                Stats(3)));

        await probe.Show();

        Assert.Equal(5, probe.Tab.Chart.Series.Count);
        Assert.StartsWith(
            CorrelationVerdict.StrongBand, probe.Tab.Verdict, StringComparison.Ordinal);
        Assert.True(probe.Tab.StatsVisible);
    }

    // ---- ruling P3-15: the two cards hide together ------------------------------------------------

    // Red against two independent flags: with one stat set present the web shows neither card.
    [AvaloniaFact]
    public async Task TheTwoStatsCards_HideTogether_WhenOnlyOneStatSetIsPresent()
    {
        using var probe = new Probe(
            answer: Build(
                [Point(1d, 2d, day: 1), Point(3d, 6d, day: 2), Point(5d, 11d, day: 3)],
                Trend(),
                Stats(3),
                yStats: null));

        await probe.Show();

        Assert.False(probe.Tab.StatsVisible);
    }

    // The card labels and units come from AnalysisMetricLabels and never from a literal.
    [AvaloniaFact]
    public async Task TheTwoStatsCards_TakeTheirLabelAndUnitFromTheMetricTable()
    {
        using var probe = new Probe(x: AnalysisMetric.Humidity, y: AnalysisMetric.Hfr);
        await probe.Show();

        Assert.Equal($"X: {AnalysisMetricLabels.Label(AnalysisMetric.Humidity)}", probe.Tab.XCard.Label);
        Assert.Equal($"Y: {AnalysisMetricLabels.Label(AnalysisMetric.Hfr)}", probe.Tab.YCard.Label);

        // Hfr's unit is " px", appended with no separator of its own.
        Assert.EndsWith(" px", probe.Tab.YCard.Mean, StringComparison.Ordinal);
        Assert.EndsWith("%", probe.Tab.XCard.Mean, StringComparison.Ordinal);
    }

    // ---- case 13: the empty result -------------------------------------------------------------

    // Spike trap 11. Red against PublishEmpty publishing an empty axis array: rc5.4's
    // CartesianChartEngine.Measure throws "XAxes and YAxes must contain at least one element".
    [AvaloniaFact]
    public async Task AnEmptyResult_PublishesOneAxisPerSide_AndNoSeriesValues()
    {
        using var probe = new Probe(answer: NoRows());
        await probe.Show();

        Assert.Equal(AnalysisTabState.Empty, probe.Tab.State);
        Assert.Empty(probe.Tab.Chart.Series);
        Assert.Single(probe.Tab.Chart.XAxes);
        Assert.Single(probe.Tab.Chart.YAxes);
        Assert.True(probe.Tab.Chart.IsEmpty);
    }

    // ---- case 14: sky_quality on a library where it is entirely null ----------------------------

    // realdata-prep-report.md S1 as a case: sky_quality is null on all 416 frames, so choosing it
    // produces an empty chart with null stats cards. It must not throw.
    [AvaloniaFact]
    public async Task SkyQualityWithNoRows_ShowsTheEmptyState_WithNullCards()
    {
        using var probe = new Probe(x: AnalysisMetric.SkyQuality, answer: NoRows());
        await probe.Show();

        Assert.Equal(AnalysisTabState.Empty, probe.Tab.State);
        Assert.Equal(
            "No frames match the current filters. Widen them in the filter bar above.",
            probe.Tab.StatusLine);
        Assert.False(probe.Tab.StatsVisible);
        Assert.False(probe.Tab.XCard.IsVisible);
        Assert.False(probe.Tab.YCard.IsVisible);
        Assert.Equal(string.Empty, probe.Tab.Verdict);

        // The controls are alive in the empty state, so the reader can pick another metric.
        Assert.Equal(6, probe.Tab.Presets.Count);
        Assert.NotEmpty(probe.Tab.XOptions);
        Assert.NotEmpty(probe.Tab.YOptions);
    }

    // ---- section 1.3: the tooltip ----------------------------------------------------------------

    // CorrelationChart.tsx line 190: X carries ONE decimal and Y TWO. Red against a single format for
    // both axes, and red against "" in place of "Unknown" for an unresolved id.
    [Fact]
    public void TheTooltip_NamesTheTargetTheNightAndBothValues_WithTheWebsTwoFormats()
    {
        var resolved = Guid.NewGuid();
        var unresolved = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [resolved] = "M 31" };

        Assert.Equal(
            "M 31 (2026-03-07): 12.3, 1.46",
            CorrelationChartViewModel.Tooltip(
                new CorrelationPoint(12.345d, 1.4567d, new DateOnly(2026, 3, 7), resolved, false), names));

        Assert.Equal(
            "Unknown (2026-03-07): 12.3, 1.46",
            CorrelationChartViewModel.Tooltip(
                new CorrelationPoint(12.345d, 1.4567d, new DateOnly(2026, 3, 7), unresolved, false), names));

        Assert.Equal(
            "Unknown (2026-03-07): 12.3, 1.46",
            CorrelationChartViewModel.Tooltip(
                new CorrelationPoint(12.345d, 1.4567d, new DateOnly(2026, 3, 7), null, false), names));
    }

    // The two point series carry the label formatter and the X one is suppressed, so the tooltip
    // shows the composed sentence once rather than beside a bare X figure.
    [AvaloniaFact]
    public async Task TheTwoPointSeries_CarryTheLabelFormatter()
    {
        using var probe = new Probe();
        await probe.Show();

        var ordinary = (ScatterSeries<ObservablePoint>)probe.Tab.Chart.Series[2];
        var rings = (ScatterSeries<ObservablePoint, CircleGeometry>)probe.Tab.Chart.Series[3];

        Assert.NotNull(ordinary.YToolTipLabelFormatter);
        Assert.NotNull(rings.YToolTipLabelFormatter);
        Assert.NotNull(ordinary.XToolTipLabelFormatter);
        Assert.NotNull(rings.XToolTipLabelFormatter);
    }

    // ---- the axis titles -------------------------------------------------------------------------

    // Every axis title comes from AnalysisMetricLabels and no axis sets a paint, which is what keeps
    // the global axis rule in charge of the label and separator ink.
    [AvaloniaFact]
    public async Task TheAxes_TakeTheMetricLabels_AndSetNoPaint()
    {
        using var probe = new Probe(x: AnalysisMetric.Airmass, y: AnalysisMetric.Fwhm);
        await probe.Show();

        var x = Assert.IsType<Axis>(probe.Tab.Chart.XAxes[0]);
        var y = Assert.IsType<Axis>(probe.Tab.Chart.YAxes[0]);

        Assert.Equal(AnalysisMetricLabels.Label(AnalysisMetric.Airmass), x.Name);
        Assert.Equal(AnalysisMetricLabels.Label(AnalysisMetric.Fwhm), y.Name);
        Assert.Null(x.LabelsPaint);
        Assert.Null(y.LabelsPaint);
        Assert.Null(x.SeparatorsPaint);
        Assert.Null(y.SeparatorsPaint);
    }

    // ---- the Matrix route's pair move ------------------------------------------------------------

    // The page's OpenCorrelationOn assigns XMetric and YMetric directly and drives the refresh
    // itself, so the callout's re-evaluation has to sit on the property that decides the answer and
    // not at each of the tab's own three callers. Driven here exactly as the page drives it, by
    // assignment, because AnalysisPageTests belongs to another file.
    //
    // The assertion is on the NOTIFICATION and not on the value: the callout is computed, so a
    // reader that queries it fresh answers correctly whether or not it was ever raised, while the
    // bound Border on screen keeps the previous answer until something raises it. Red against the
    // three per-caller calls this replaced: the pair moves, the raised list holds XMetric, YMetric
    // and both selected options, and PlateScaleWarningVisible is absent from it.
    [AvaloniaFact]
    public async Task TheMatrixRoutesPairAssignment_RaisesThePlateScaleCallout()
    {
        using var probe = new Probe(y: AnalysisMetric.Hfr, answer: Build(
            [Point(1d, 2d), Point(3d, 6d), Point(5d, 11d)], Trend(), plateScales: 2));
        await probe.Show();

        // Two trains and a pixel-domain Y, so the callout is up and a reader who now moves off HFR
        // must see it go down.
        Assert.True(probe.Tab.PlateScaleWarningVisible);

        var raised = new List<string?>();
        probe.Tab.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        probe.Tab.XMetric = AnalysisMetric.Airmass;
        probe.Tab.YMetric = AnalysisMetric.Fwhm;

        Assert.Contains(nameof(AnalysisTabViewModel.PlateScaleWarningVisible), raised);
        Assert.False(probe.Tab.PlateScaleWarningVisible);

        // The reverse direction, back onto HFR under the same two-train result.
        raised.Clear();
        probe.Tab.YMetric = AnalysisMetric.Hfr;

        Assert.Contains(nameof(AnalysisTabViewModel.PlateScaleWarningVisible), raised);
        Assert.True(probe.Tab.PlateScaleWarningVisible);
    }
}
