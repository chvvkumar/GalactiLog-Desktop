using System.Globalization;
using Avalonia.Headless.XUnit;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

/// <summary>
/// Spec 12.14's Distributions tab, its two halves and its two charts (Task 6, Distributions).
/// </summary>
/// <remarks>
/// <para>
/// Windowless throughout, so every tab takes the inline post seam of the harness ruling; the two
/// cases that read a resolved theme token take <c>AvaloniaFact</c> instead, because
/// <c>ChartTheme.Read</c> answers its documented neutral with no application and an assertion
/// against the neutral would agree with itself.
/// </para>
/// <para>
/// Every assertion on a query count, a state or a series collection is preceded by a join on the
/// load or by an identity check on <c>PendingLoad</c>: a counter alone races the background
/// increment and reads green with the defect present. A case that starts a SECOND load joins
/// through <see cref="AnalysisSettle.Tab"/> after each of them, its remarks say why, and a case
/// that awaits <c>PendingLoad</c> directly is one that starts exactly one.
/// </para>
/// <para>
/// None of these cases re-proves a base rule. <c>AnalysisTabBaseTests</c> covers the load loop,
/// the states, the generation token and the callout.
/// </para>
/// </remarks>
public class DistributionsTabTests : IDisposable
{
    private readonly List<DistributionsTabViewModel> _tabs = [];
    private readonly List<IDisposable> _charts = [];

    public void Dispose()
    {
        foreach (var tab in _tabs)
        {
            tab.Dispose();
        }

        foreach (var chart in _charts)
        {
            chart.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static AnalysisFilter Filter(string? telescope = null, string? camera = null)
        => new(telescope, camera, null, AnalysisGranularity.Frame, null, null);

    private DistributionsTabViewModel Tab(
        Func<AnalysisMetric, AnalysisFilter, DistributionResult?>? distribution = null,
        Func<AnalysisMetric, BoxPlotGrouping, AnalysisFilter, BoxPlotResult>? boxPlot = null,
        Func<AnalysisFilter?>? filter = null,
        Action<Action>? post = null)
    {
        var tab = new DistributionsTabViewModel(
            filter ?? (() => Filter()),
            distribution ?? ((_, _) => AnalysisViewModelTestFactory.Distribution()),
            boxPlot ?? ((_, _, _) => AnalysisViewModelTestFactory.Boxes()),
            post ?? (action => action()));

        _tabs.Add(tab);
        tab.IsVisible = true;
        return tab;
    }

    private static DistributionResult Histogram(IReadOnlyList<HistogramBin> bins, SummaryStats stats)
        => new(bins, stats, 0.25d, 1);

    private static BoxPlotResult Boxes(params string[] names) => new(
        [.. names.Select(name => new BoxPlot(name, 1d, 2d, 3d, 4d, 5d, [], 12))],
        1,
        names.Length * 12);

    // ---- case 1 -------------------------------------------------------------------------------

    // Spec 12.14: "both keep their own controls and their own last result". Red against a tab that
    // shares one result field or one metric: switching back redraws the box plot's data, loses the
    // histogram's own metric, or re-queries.
    [Fact]
    public async Task TheTwoHalves_KeepTheirOwnControlsAndTheirOwnLastResult()
    {
        var histogramCalls = 0;
        var boxCalls = 0;
        var tab = Tab(
            distribution: (_, _) => { histogramCalls++; return AnalysisViewModelTestFactory.Distribution(bins: 4); },
            boxPlot: (_, _, _) => { boxCalls++; return AnalysisViewModelTestFactory.Boxes(groups: 3); });

        await tab.PendingLoad!;
        tab.HistogramMetric = AnalysisMetric.Fwhm;
        await tab.PendingLoad!;
        Assert.Equal(2, histogramCalls);

        var label = tab.Card.Label;
        var bins = tab.Histogram.BinTooltips.Count;

        tab.IsBoxPlot = true;
        await tab.PendingLoad!;
        Assert.Equal(1, boxCalls);

        tab.BoxMetric = AnalysisMetric.Eccentricity;
        await tab.PendingLoad!;
        Assert.Equal(2, boxCalls);
        Assert.Equal(3, tab.BoxPlot.GroupNames.Count);

        // Back to the histogram: its own result is redrawn and nothing is queried. The identity
        // check comes before the counters, because a counter alone races the background increment.
        var pending = tab.PendingLoad;
        tab.IsBoxPlot = false;
        Assert.Same(pending, tab.PendingLoad);

        Assert.Equal(2, histogramCalls);
        Assert.Equal(2, boxCalls);
        Assert.Equal(AnalysisMetric.Fwhm, tab.HistogramMetric);
        Assert.Equal(AnalysisMetric.Eccentricity, tab.BoxMetric);
        Assert.Equal(AnalysisMetric.Fwhm, tab.Metric);
        Assert.Equal(label, tab.Card.Label);
        Assert.Equal(bins, tab.Histogram.BinTooltips.Count);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
    }

    // A half whose held result predates the bar's current filter is re-queried rather than
    // redrawn, which is the other side of case 1: the segment must not put figures on screen that
    // the filter bar no longer describes.
    [Fact]
    public async Task ASegmentSwitch_ReQueriesAHalfWhoseHeldResultPredatesTheFilter()
    {
        var histogramCalls = 0;
        var filter = Filter();
        var tab = Tab(
            distribution: (_, _) => { histogramCalls++; return AnalysisViewModelTestFactory.Distribution(); },
            filter: () => filter);

        await tab.PendingLoad!;
        Assert.Equal(1, histogramCalls);

        tab.IsBoxPlot = true;
        await tab.PendingLoad!;

        filter = Filter("RC8", "ASI2600MM");
        tab.IsBoxPlot = false;
        await tab.PendingLoad!;
        Assert.Equal(2, histogramCalls);
    }

    // ---- cases 2 and 3 ------------------------------------------------------------------------

    // Red against a list built from Enum.GetValues, which offers twenty-five and sends a PHD2
    // metric the query refuses.
    [Fact]
    public void TheHistogramPicker_OffersTwentyMetrics_InXThenYOrder_AndNoPhd2Metric()
    {
        IReadOnlyList<AnalysisMetric> expected = [.. AnalysisMetrics.X, .. AnalysisMetrics.Y];

        Assert.Equal(20, DistributionsTabViewModel.HistogramMetrics.Count);
        Assert.Equal(expected, DistributionsTabViewModel.HistogramMetrics);
        Assert.All(
            AnalysisMetrics.Phd2X,
            metric => Assert.DoesNotContain(metric, DistributionsTabViewModel.HistogramMetrics));

        // Every label is the one table's and none is spelled in the tab (ruling B15).
        Assert.Equal(
            expected.Select(AnalysisMetricLabels.Label).ToList(),
            DistributionsTabViewModel.HistogramMetricChoices.Select(choice => choice.Label).ToList());
        Assert.Equal(
            expected.ToList(),
            DistributionsTabViewModel.HistogramMetricChoices.Select(choice => choice.Metric!.Value).ToList());
    }

    [Fact]
    public void TheBoxPlotPicker_OffersExactlyTheTenYMetrics()
    {
        Assert.Equal(10, DistributionsTabViewModel.BoxMetrics.Count);
        Assert.Equal(AnalysisMetrics.Y, DistributionsTabViewModel.BoxMetrics);
        Assert.Equal(
            AnalysisMetrics.Y.Select(AnalysisMetricLabels.Label).ToList(),
            DistributionsTabViewModel.BoxMetricChoices.Select(choice => choice.Label).ToList());
    }

    [Fact]
    public async Task TheMetricPickers_SendTheirOwnHalfsMetricToTheQuery()
    {
        AnalysisMetric? histogram = null;
        AnalysisMetric? box = null;
        var tab = Tab(
            distribution: (metric, _) => { histogram = metric; return AnalysisViewModelTestFactory.Distribution(); },
            boxPlot: (metric, _, _) => { box = metric; return AnalysisViewModelTestFactory.Boxes(); });

        await tab.PendingLoad!;
        tab.SelectedHistogramMetricChoice = DistributionsTabViewModel.HistogramMetricChoices[^1];
        await tab.PendingLoad!;
        Assert.Equal(AnalysisMetric.AduStdev, histogram);

        // Awaited BETWEEN the two, not once at the end: the segment switch issues its own query
        // and the picker issues a second, and although the base drops the first PUBLISH by
        // generation, the first query's own body still runs and writes this case's variable. Two
        // in flight and the assertion reads whichever finished last.
        tab.IsBoxPlot = true;
        await tab.PendingLoad!;

        tab.SelectedBoxMetricChoice = DistributionsTabViewModel.BoxMetricChoices[1];
        await tab.PendingLoad!;
        Assert.Equal(AnalysisMetric.Fwhm, box);
        Assert.Equal(AnalysisMetric.AduStdev, histogram);
    }

    // ---- case 4 -------------------------------------------------------------------------------

    // Red against a line SERIES: the line then enters the legend and the tooltip, and the series
    // count names it.
    //
    // Built on the chart itself rather than through a tab, because a token resolves only on the UI
    // thread: under the windowless inline post seam the mapper runs on the query's own thread-pool
    // thread and ChartTheme.Read answers its documented neutral, which would make the assertion
    // agree with itself. The tab-to-chart wiring is pinned by the tooltip, the ordering and the
    // dropped-group cases below.
    [AvaloniaFact]
    public void TheMedianLine_IsOneDashedSectionInTextSecondary_AndNeverASeries()
    {
        ChartTheme.Apply();
        var chart = new HistogramChartViewModel();
        _charts.Add(chart);
        chart.Show(AnalysisViewModelTestFactory.Distribution(), AnalysisMetric.Hfr);

        var section = Assert.Single(chart.Sections);
        Assert.Equal(section.Xi, section.Xj);

        var stroke = Assert.IsType<SolidColorPaint>(section.Stroke);
        Assert.Equal(
            ChartTheme.Read(HistogramChartViewModel.MedianTokenKey, ChartTheme.Fallback),
            stroke.Color);
        Assert.NotEqual(ChartTheme.Fallback, stroke.Color);
        Assert.IsType<DashEffect>(stroke.PathEffect);

        // Over the columns, on the section AND on its paint. A section draws behind the series by
        // default, and now that a bar spans its whole bin the line is inside a drawn bar: at the
        // default order the launched look showed it only in the sliver above that bar's own top,
        // and with the section's own index alone at 1 it was still behind the fill.
        Assert.Equal(HistogramChartViewModel.MedianLineZIndex, section.ZIndex);
        Assert.Equal(HistogramChartViewModel.MedianLineZIndex, stroke.ZIndex);
        Assert.True(section.ZIndex > 0);

        // One series, the columns. A second would be the line.
        var columns = Assert.IsType<ColumnSeries<ObservablePoint>>(Assert.Single(chart.Series));
        Assert.Equal(
            ChartTheme.Read(HistogramChartViewModel.ColumnTokenKey, ChartTheme.Fallback),
            Assert.IsType<SolidColorPaint>(columns.Fill).Color);
        Assert.NotEqual(ChartTheme.Fallback, Assert.IsType<SolidColorPaint>(columns.Fill).Color);
    }

    // ---- case 5a, ruling P1-6 and finding B12, under the one convention of the look's F3 --------

    // Ten bins half a unit wide from 1.0, so that bin-edge space and the metric's own domain
    // cannot be confused: a value of 2.25 lands at 2.5 here and at 2.25 under a chart that plots
    // the raw value.
    private static IReadOnlyList<HistogramBin> HalfWidthBins() =>
        [.. Enumerable.Range(0, 10).Select(index => new HistogramBin(1d + (0.5d * index), 1.5d + (0.5d * index), 1))];

    // The X axis is the bin EDGE, not the metric value, and bin i runs from i to i + 1 so that the
    // line falls INSIDE the bar of the bin that holds the median. Red against Xi = Stats.Median: a
    // median of 2.25 lands at 2.25 where this case expects 2.5. Red against the previous
    // convention, index - 0.5 + fraction, which put the same median at 2.0, the shared edge
    // between two bins and, with the bars then drawn narrow and centred, in the gap between them.
    [Fact]
    public void TheMedianLine_LandsInBinEdgeSpace_StrictlyInsideItsOwnBin()
    {
        var bins = HalfWidthBins();

        // Half way into bin 2, which spans 2.0 to 2.5 in the metric and 2 to 3 on the axis.
        var middle = HistogramChartViewModel.SectionAt(bins, 2.25d)!.Value;
        Assert.Equal(2.5d, middle, 10);
        Assert.True(middle > 2d && middle < 3d, "the median must fall strictly inside bin 2's bar");

        // The LAST bin, which under user ruling U1 is the one that admits its own upper edge. A
        // median four fifths of the way into it lands four fifths of the way along its bar.
        var last = HistogramChartViewModel.SectionAt(bins, 5.9d)!.Value;
        Assert.Equal(9.8d, last, 10);
        Assert.True(last > 9d && last < 10d, "the median must fall strictly inside the last bar");

        // A median exactly at vMax is the one value that lands on an edge rather than inside a
        // bar, and it is the LAST bin's upper edge and not the axis's overflow.
        Assert.Equal(10d, HistogramChartViewModel.SectionAt(bins, 6d)!.Value, 10);

        // A shared bin edge: the first bin that admits the value wins, which is the web's own loop
        // order, so the line stands at that bin's right edge.
        Assert.Equal(2d, HistogramChartViewModel.SectionAt(bins, 2d)!.Value, 10);

        // Both clamps, which are the axis's own two ends.
        Assert.Equal(0d, HistogramChartViewModel.SectionAt(bins, -4d)!.Value, 10);
        Assert.Equal(10d, HistogramChartViewModel.SectionAt(bins, 99d)!.Value, 10);

        // The constant-metric single bin takes the zero-width arm, so the line lands on the
        // column's own centre rather than at its edge or off the axis.
        IReadOnlyList<HistogramBin> constant = [new HistogramBin(-10d, -10d, 59)];
        Assert.Equal(0.5d, HistogramChartViewModel.SectionAt(constant, -10d)!.Value, 10);

        Assert.Null(HistogramChartViewModel.SectionAt([], 1d));
    }

    [Fact]
    public async Task TheMedianLine_IsPlacedInBinEdgeSpaceOnARealResult()
    {
        var bins = HalfWidthBins();
        var stats = new SummaryStats(10, 1d, 6d, 2.3d, 2.25d, 1d);
        var tab = Tab(distribution: (_, _) => Histogram(bins, stats));

        await tab.PendingLoad!;

        var section = Assert.Single(tab.Histogram.Sections);
        Assert.Equal(2.5d, section.Xi!.Value, 10);
        Assert.NotEqual(stats.Median, section.Xi!.Value);
    }

    // The look's F3: the bars, the ticks and the median line were in three different places, so
    // the line stood in the empty gap between two columns. One convention now: a bar spans its bin
    // edge to edge, the tick names the bin's lower edge and stands at the bar's LEFT edge, and the
    // median falls inside the bar.
    //
    // Red against the category axis this chart used to carry: Labels was set, no Labeler was, the
    // columns were centred on their ticks, and MaxBarWidth left at the library's 50 pixel default
    // drew a 47 pixel column in the middle of a 137 pixel slot.
    [AvaloniaFact]
    public void TheHistogram_DrawsOneBarPerBinEdgeToEdge_WithItsTickAtTheBarsLeftEdge()
    {
        ChartTheme.Apply();
        var bins = HalfWidthBins();
        var chart = new HistogramChartViewModel();
        _charts.Add(chart);
        chart.Show(Histogram(bins, new SummaryStats(10, 1d, 6d, 2.3d, 2.25d, 1d)), AnalysisMetric.Hfr);

        var columns = Assert.IsType<ColumnSeries<ObservablePoint>>(Assert.Single(chart.Series));

        // Edge to edge: no pixel cap and no padding, which is the library's own documented pairing
        // for a bar with no gap, over bars centred on their bin's centre.
        Assert.Equal(double.MaxValue, columns.MaxBarWidth);
        Assert.Equal(0d, columns.Padding);
        Assert.Equal(
            [0.5d, 1.5d, 2.5d, 3.5d, 4.5d, 5.5d, 6.5d, 7.5d, 8.5d, 9.5d],
            columns.Values!.Select(point => point.X!.Value));

        var axis = Assert.IsType<Axis>(Assert.Single(chart.XAxes));

        // A NUMERIC axis, not a category one: a column on a category axis is centred on its tick,
        // so the lower-edge label could only ever sit at the bar's middle there.
        Assert.Null(axis.Labels);
        Assert.Equal(0d, axis.MinLimit);
        Assert.Equal(bins.Count, axis.MaxLimit);
        Assert.Equal(1d, axis.MinStep);
        Assert.True(axis.ForceStepToMin);

        // The tick at each bar's LEFT edge names that bar's own bin's lower edge, and the one
        // extra tick at the end names the last bin's upper edge, which under user ruling U1 is the
        // distribution's own maximum.
        for (var index = 0; index < bins.Count; index++)
        {
            var left = columns.Values!.ElementAt(index).X!.Value - 0.5d;
            Assert.Equal(index, left, 10);
            Assert.Equal(
                bins[index].BinStart.ToString("F1", CultureInfo.InvariantCulture),
                axis.Labeler(left));
        }

        Assert.Equal("6.0", axis.Labeler(bins.Count));

        // The median of this result is inside bin 2's bar, between its two ticks.
        var median = Assert.Single(chart.Sections).Xi!.Value;
        Assert.True(median > 2d && median < 3d, "the median line must fall inside a drawn bar");
        Assert.Equal("2.0", axis.Labeler(Math.Floor(median)));
        Assert.Equal("2.5", axis.Labeler(Math.Floor(median) + 1d));
    }

    // ---- case 5, ruling P2-7 and seam ruling S8 -----------------------------------------------

    // Red against one format for every bin, which is user ruling U1 losing its only visible trace,
    // and red against a different number format.
    [Fact]
    public async Task TheBinTooltips_AreHalfOpenExceptTheLastBinOfARangedDistribution()
    {
        IReadOnlyList<HistogramBin> bins =
        [
            new(1d, 1.5d, 3),
            new(1.5d, 2d, 1),
            new(2d, 2.5d, 7),
        ];
        var tab = Tab(distribution: (_, _) => Histogram(bins, new SummaryStats(11, 1d, 2.5d, 1.8d, 1.75d, 0.3d)));

        await tab.PendingLoad!;

        Assert.Equal(
            ["1.00 to 1.50), 3 frames", "1.50 to 2.00), 1 frame", "2.00 to 2.50], 7 frames"],
            tab.Histogram.BinTooltips);
    }

    // Seam ruling S8: the closed last edge applies only when the data has a range at all. The
    // fixture's own constant column is sensor_temp at -10.00 on all 59 frames, and Sturges' rule
    // still yields several bins there, so Bins.Count cannot discriminate and the published summary
    // does.
    [Fact]
    public async Task AConstantMetric_KeepsTheHalfOpenFormOnItsLastBin()
    {
        IReadOnlyList<HistogramBin> bins =
        [
            new(-10d, -9d, 59),
            new(-9d, -8d, 0),
        ];
        var tab = Tab(distribution: (_, _) => Histogram(bins, new SummaryStats(59, -10d, -10d, -10d, -10d, 0d)));

        await tab.PendingLoad!;

        Assert.Equal(
            ["-10.00 to -9.00), 59 frames", "-9.00 to -8.00), 0 frames"],
            tab.Histogram.BinTooltips);
    }

    // ---- cases 6, 7 and 8 ---------------------------------------------------------------------

    // One box series and one outlier scatter, never two box series (spike trap 9), with ruling
    // A25's tokens. Red against a per-group series: the rings float between the boxes. Red against
    // metric-hfr on the stroke, which is ruling A25 reversed.
    //
    // On the chart itself, for the UI-thread reason the median line case above records.
    [AvaloniaFact]
    public void TheBoxPlot_IsOneBoxSeriesAndOneOutlierScatter_InTheRuledTokens()
    {
        ChartTheme.Apply();
        var chart = new BoxPlotChartViewModel();
        _charts.Add(chart);
        chart.Show(
            [
                new BoxPlot("Ha", 1d, 2d, 3d, 4d, 5d, [9d, 10d], 12),
                new BoxPlot("OIII", 1.1d, 2.1d, 3.1d, 4.1d, 5.1d, [], 14),
            ],
            AnalysisMetric.Hfr);

        Assert.Equal(3, chart.Series.Count);
        var box = Assert.Single(chart.Series.OfType<BoxSeries<BoxValue>>());
        Assert.Same(chart.Series[0], box);

        // BoxValue(max, thirdQuartile, firstQuartile, min, median): seeded so a transposition of
        // Min and Q1 draws a plausible-looking wrong box and fails here.
        var first = Assert.IsType<BoxValue>(box.Values!.First());
        Assert.Equal(5d, first.Max);
        Assert.Equal(4d, first.ThirdQuartile);
        Assert.Equal(2d, first.FirtQuartile);
        Assert.Equal(1d, first.Min);
        Assert.Equal(3d, first.Median);

        Assert.Equal(
            ChartTheme.Read(BoxPlotChartViewModel.BoxTokenKey, ChartTheme.Fallback),
            Assert.IsType<SolidColorPaint>(box.Fill).Color);

        var stroke = Assert.IsType<SolidColorPaint>(box.Stroke);
        Assert.Equal(
            ChartTheme.Read(BoxPlotChartViewModel.OutlineTokenKey, ChartTheme.Fallback),
            stroke.Color);
        Assert.NotEqual(ChartTheme.Read("ColorMetricHfr", ChartTheme.Fallback), stroke.Color);

        // Hollow rings at their own group's integer X, ruling P3-10's size and stroke.
        var rings = Assert.IsType<ScatterSeries<ObservablePoint, CircleGeometry>>(chart.Series[1]);
        Assert.Null(rings.Fill);
        Assert.Equal(6d, rings.GeometrySize);
        var ringStroke = Assert.IsType<SolidColorPaint>(rings.Stroke);
        Assert.Equal(
            ChartTheme.Read(BoxPlotChartViewModel.OutlierTokenKey, ChartTheme.Fallback),
            ringStroke.Color);
        Assert.Equal(1.5f, ringStroke.StrokeThickness);
        Assert.Equal([0d, 0d], rings.Values!.Select(point => point.X!.Value));
        Assert.Equal([9d, 10d], rings.Values!.Select(point => point.Y!.Value));

        // The median mark, drawn LAST so it stands over the box, at the same integer X, one per
        // group and at that group's own median. Narrower than the box it marks, and hoverable by
        // nothing: a tooltip over the middle of the box would cover the box's own.
        var marks = Assert.IsType<ScatterSeries<ObservablePoint, MedianMarkGeometry>>(chart.Series[2]);
        Assert.Equal([0d, 1d], marks.Values!.Select(point => point.X!.Value));
        Assert.Equal([3d, 3.1d], marks.Values!.Select(point => point.Y!.Value));
        Assert.Equal(BoxPlotChartViewModel.MedianMarkWidth, marks.GeometrySize);
        Assert.True(BoxPlotChartViewModel.MedianMarkWidth < BoxPlotChartViewModel.BoxWidth);
        Assert.Equal(BoxPlotChartViewModel.BoxWidth, box.MaxBarWidth);
        Assert.Null(marks.Stroke);
        Assert.False(marks.IsHoverable);
        Assert.False(marks.IsVisibleAtLegend);
    }

    // The look's F5: on red-light the mark measured 1.13 to 1 against its fill and the box read as
    // one solid block. Ruling A25's flat text-primary is refuted in all three themes, so the ink
    // is CHOSEN, by the same contrast rule the Matrix cell labels use.
    //
    // Red against the shipped chart: the mark had no paint of its own at all, it was the box
    // series' stroke, so there was no third series to find. Red against a mark left on
    // text-primary: the chosen ink differs from the outline's in every one of the three themes.
    [AvaloniaFact]
    public void TheMedianMarksInk_IsChosenByContrastAgainstTheBoxFill_InEveryTheme()
    {
        try
        {
            var chart = new BoxPlotChartViewModel();
            _charts.Add(chart);

            foreach (var theme in ThemeManager.Available)
            {
                ThemeManager.Apply(theme);
                chart.Show([new BoxPlot("Ha", 1d, 2d, 3d, 4d, 5d, [], 12)], AnalysisMetric.Hfr);

                var box = Assert.Single(chart.Series.OfType<BoxSeries<BoxValue>>());
                var fill = Assert.IsType<SolidColorPaint>(box.Fill).Color;
                var outline = Assert.IsType<SolidColorPaint>(box.Stroke).Color;
                var elevated = ChartTheme
                    .Read(BoxPlotChartViewModel.MedianAltTokenKey, ChartTheme.Fallback)
                    .WithAlpha(0xFF);

                var mark = Assert.Single(chart.Series.OfType<ScatterSeries<ObservablePoint, MedianMarkGeometry>>());
                var ink = Assert.IsType<SolidColorPaint>(mark.Fill).Color;

                // The better of the two candidates, and in all three themes that is the elevated
                // background: text-primary measures 1.04 on red-light, 1.51 on deep-sky and 2.44
                // on luminance against their own box fills.
                Assert.Equal(InkContrast.Choose(fill, outline, elevated), ink);
                Assert.Equal(elevated, ink);
                Assert.True(
                    InkContrast.Contrast(ink, fill) > InkContrast.Contrast(outline, fill),
                    $"{theme}: the chosen ink must beat text-primary against the fill");
                Assert.True(
                    InkContrast.Contrast(ink, fill) >= 3d,
                    $"{theme}: a graphical object is asked for 3 to 1");

                // The whiskers and the outline are NOT re-inked: they stand on the page, which is
                // dark in every theme here, and this ink was chosen against the box.
                Assert.Equal(ChartTheme.Read(BoxPlotChartViewModel.OutlineTokenKey, ChartTheme.Fallback), outline);
                Assert.NotEqual(outline, ink);
            }
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // Ruling P3-7, both strings pinned. Min and Max are the WHISKER ends, which is what the word
    // Range names.
    [Fact]
    public async Task TheBoxTooltips_ArePinnedVerbatim()
    {
        var tab = Tab(boxPlot: (_, _, _) => new BoxPlotResult(
            [new BoxPlot("Ha", 1.234d, 2.345d, 3.456d, 4.567d, 5.678d, [], 42)], 1, 42));

        await AnalysisSettle.Tab(tab);
        tab.IsBoxPlot = true;
        await AnalysisSettle.Tab(tab);

        Assert.Equal("Q1: 2.35, Median: 3.46, Q3: 4.57 (N=42)", Assert.Single(tab.BoxPlot.BoxTooltips));
        Assert.Equal("Range: 1.23 to 5.68", Assert.Single(tab.BoxPlot.WhiskerTooltips));
    }

    // ---- ruling P3-9 and observation 9, the ordinal group order -------------------------------

    // The order is the QUERY's, which BoxPlotResult.Groups now promises is ascending ordinal, and
    // AnalysisQueryTests.TheBoxPlot_DropsAGroupOfThree_KeepsAGroupOfFour_AndOrdersOrdinally is
    // where it is proved. This tab draws what it is given and orders nothing: a second sort here
    // would silently overrule the query's the day that order changed deliberately. Red against a
    // tab that sorts: the seeded order is not ordinal and a sort would reorder it.
    [Fact]
    public async Task TheGroupsAreDrawnInTheQuerysOwnOrder_AndThisTabSortsNothing()
    {
        var tab = Tab(boxPlot: (_, _, _) => Boxes("Z", "a", "B"));

        await AnalysisSettle.Tab(tab);
        tab.IsBoxPlot = true;
        await AnalysisSettle.Tab(tab);

        Assert.Equal(["Z", "a", "B"], tab.BoxPlot.GroupNames);
        Assert.NotEqual(["B", "Z", "a"], tab.BoxPlot.GroupNames);
        Assert.Equal(
            ["Z", "a", "B"],
            Assert.IsType<Axis>(tab.BoxPlot.XAxes[0]).Labels!);
    }

    // ---- case 9 -------------------------------------------------------------------------------

    // Red against a switch that sends Filter for Target.
    [Theory]
    [InlineData(0, BoxPlotGrouping.Filter)]
    [InlineData(1, BoxPlotGrouping.Equipment)]
    [InlineData(2, BoxPlotGrouping.Month)]
    [InlineData(3, BoxPlotGrouping.Target)]
    public async Task TheGroupingPicker_SendsTheMemberItsIndexNames(int index, BoxPlotGrouping expected)
    {
        BoxPlotGrouping? seen = null;
        var tab = Tab(boxPlot: (_, grouping, _) => { seen = grouping; return AnalysisViewModelTestFactory.Boxes(); });

        await AnalysisSettle.Tab(tab);
        tab.IsBoxPlot = true;
        await AnalysisSettle.Tab(tab);

        tab.GroupByIndex = index;
        await AnalysisSettle.Tab(tab);

        Assert.Equal(expected, tab.GroupBy);
        Assert.Equal(expected, seen);
        Assert.Equal(4, DistributionsTabViewModel.GroupByLabels.Count);
    }

    // ---- case 10, the view-model half ---------------------------------------------------------

    // Spec 12.14: the note and the warning are different statements and both are shown when both
    // apply. Red against an implementation that shows one or the other.
    [Fact]
    public async Task ThePixelNoteAndThePlateScaleWarning_BothStand()
    {
        var tab = Tab(
            boxPlot: (_, _, _) => new BoxPlotResult(
                [new BoxPlot("RC8 + ASI2600MM", 1d, 2d, 3d, 4d, 5d, [], 12)], 2, 12),
            filter: () => Filter("RC8", "ASI2600MM"));

        await AnalysisSettle.Tab(tab);
        tab.IsBoxPlot = true;
        await AnalysisSettle.Tab(tab);
        tab.GroupBy = BoxPlotGrouping.Equipment;
        await AnalysisSettle.Tab(tab);

        Assert.True(tab.PixelNoteVisible);
        Assert.True(tab.PlateScaleWarningVisible);
    }

    // Spec 12.14's two arms, and the metric gate over both.
    [Fact]
    public async Task ThePixelNote_ShowsOnlyForHfrAndOnlyWhileTheScopeCanSpanTwoTrains()
    {
        var both = Filter("RC8", "ASI2600MM");
        var tab = Tab(filter: () => both);
        await tab.PendingLoad!;

        // Both names chosen and the histogram half: one optical train, so no note.
        Assert.False(tab.PixelNoteVisible);

        tab.HistogramMetric = AnalysisMetric.Fwhm;
        await tab.PendingLoad!;
        Assert.False(tab.PixelNoteVisible);

        tab.HistogramMetric = AnalysisMetric.Hfr;
        await tab.PendingLoad!;

        tab.IsBoxPlot = true;
        await tab.PendingLoad!;
        Assert.False(tab.PixelNoteVisible);

        // By Equipment widens the scope back over every train the filters admit.
        tab.GroupBy = BoxPlotGrouping.Equipment;
        await tab.PendingLoad!;
        Assert.True(tab.PixelNoteVisible);

        // A non-pixel metric closes it again, whatever the grouping.
        tab.BoxMetric = AnalysisMetric.Fwhm;
        await tab.PendingLoad!;
        Assert.False(tab.PixelNoteVisible);
    }

    [Fact]
    public async Task ThePixelNote_ShowsWhenTheBarHasNotChosenBothNames()
    {
        var tab = Tab(filter: () => Filter("RC8"));
        await tab.PendingLoad!;
        Assert.True(tab.PixelNoteVisible);
    }

    // The note reads the filter BAR, so it has to follow the bar and not only this tab's own
    // controls. Nothing on the tab moves in this case: the equipment selection changes and the
    // refresh the page drives is the whole of what the tab sees.
    //
    // The assertion is on the NOTIFICATION and not on the value: the note is a computed property,
    // so a reader that queries it fresh answers correctly whether or not it was ever raised, while
    // the bound caption on screen keeps the previous answer until something raises it. Red against
    // the three raise sites this joined, all of them on the tab's own controls: the bar moves, the
    // chart redraws for one optical train, and the caption still tells the reader the figures are
    // not comparable across trains.
    [Fact]
    public async Task ThePixelNote_FollowsTheFilterBar_WhenNoControlOnThisTabMoves()
    {
        var filter = Filter();
        var tab = Tab(filter: () => filter);
        await tab.PendingLoad!;

        // HFR, and the bar names neither a telescope nor a camera, so the scope spans trains.
        Assert.True(tab.PixelNoteVisible);

        var raised = new List<string?>();
        tab.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        // One equipment combination chosen in the bar, which is the page's MarkStale and Refresh
        // and nothing else.
        filter = Filter("RC8", "ASI2600MM");
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;

        Assert.Contains(nameof(DistributionsTabViewModel.PixelNoteVisible), raised);
        Assert.False(tab.PixelNoteVisible);

        // The mirror: widening back to all equipment puts it up again.
        raised.Clear();
        filter = Filter();
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;

        Assert.Contains(nameof(DistributionsTabViewModel.PixelNoteVisible), raised);
        Assert.True(tab.PixelNoteVisible);
    }

    // ---- the segment under a reversed date range ------------------------------------------------

    // Spec 12.14: a reversed range queries nothing and the last drawn result stays. The base's own
    // reversed-range arm writes no state while a result is drawn, so the OUTGOING half's state
    // would stand over the incoming half's chart. Red against a switch that only calls Refresh: the
    // state is still Ready, the result region stays open, and the reader gets an empty box plot
    // with no sentence saying why.
    //
    // The incoming half has never loaded here, so the region closes and the tab carries the filter
    // bar's own sentence. The no-rows sentence is not shown while the range is reversed: widening
    // the filters is not what helps, and the earlier expectation of it here was corrected with this
    // finding.
    [Fact]
    public async Task SwitchingTheHalfUnderAReversedRange_DrawsTheIncomingHalfInsteadOfKeepingTheOutgoingState()
    {
        AnalysisFilter? filter = Filter();
        var histogramCalls = 0;
        var boxCalls = 0;
        var tab = Tab(
            distribution: (_, _) => { histogramCalls++; return AnalysisViewModelTestFactory.Distribution(); },
            boxPlot: (_, _, _) => { boxCalls++; return AnalysisViewModelTestFactory.Boxes(); },
            filter: () => filter);
        await tab.PendingLoad!;

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.True(tab.ShowsResult);
        Assert.Equal(1, histogramCalls);

        // The reader reverses the two date fields, then presses Box Plot.
        filter = null;
        var pending = tab.PendingLoad;
        tab.IsBoxPlot = true;

        // Nothing was queried, which is the whole of the reversed-range rule.
        Assert.Same(pending, tab.PendingLoad);
        Assert.Equal(0, boxCalls);

        Assert.True(tab.IsBoxPlot);
        Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        Assert.False(tab.ShowsResult);
        Assert.Equal(AnalysisTabViewModel.ReversedRangeText, tab.StatusLine);
        Assert.Empty(tab.BoxPlot.Series);

        // Back to the histogram, whose own result is still held and is drawn again, which is spec
        // 12.14's "the last drawn result stays on screen" under a reversed range. A drawn result
        // carries no sentence of its own; the date error is the filter bar's to show.
        tab.IsBoxPlot = false;

        Assert.Same(pending, tab.PendingLoad);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.True(tab.ShowsResult);
        Assert.Equal(string.Empty, tab.StatusLine);
        Assert.Equal(1, histogramCalls);

        // The reader corrects the range. The page's own move is MarkStale then Refresh on every
        // tab, and exactly one query follows, for the half that is showing.
        filter = Filter();
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;

        Assert.Equal(2, histogramCalls);
        Assert.Equal(0, boxCalls);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(string.Empty, tab.StatusLine);
    }

    // The ruled rule read straight off: what the reader sees depends on the state and never on the
    // route taken to it. "Histogram drawn, range reversed" is reached twice, once by reversing with
    // the histogram showing and once by reversing with the box plot showing and then pressing
    // Histogram, and both must answer the same state, the same sentence and the same region. Red
    // against a segment switch that writes a sentence of its own over a redrawn result: route 2
    // then says something route 1 does not.
    [Fact]
    public async Task HistogramDrawnUnderAReversedRange_ReadsTheSameWhicheverRouteReachedIt()
    {
        AnalysisFilter? filter = Filter();
        var boxCalls = 0;
        var tab = Tab(
            boxPlot: (_, _, _) => { boxCalls++; return AnalysisViewModelTestFactory.Boxes(); },
            filter: () => filter);
        await tab.PendingLoad!;

        // Route 1: the histogram is showing and the reader reverses the two date fields.
        filter = null;
        tab.MarkStale();
        tab.Refresh();

        var route1 = (tab.State, tab.StatusLine, tab.ShowsResult);
        Assert.Equal((AnalysisTabState.Ready, string.Empty, true), route1);

        // Route 2: a legal range again, the box plot half loaded as well, reversed there, and then
        // Histogram pressed.
        filter = Filter();
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;

        tab.IsBoxPlot = true;
        await tab.PendingLoad!;
        Assert.Equal(1, boxCalls);

        filter = null;
        tab.MarkStale();
        tab.Refresh();
        var pending = tab.PendingLoad;
        tab.IsBoxPlot = false;

        Assert.Same(pending, tab.PendingLoad);
        Assert.Equal(route1, (tab.State, tab.StatusLine, tab.ShowsResult));
        Assert.NotEmpty(tab.Histogram.BinTooltips);

        // And the other direction with a result on the incoming half: the box plot's own groups
        // come back, still without a query and still without a sentence.
        tab.IsBoxPlot = true;

        Assert.Same(pending, tab.PendingLoad);
        Assert.Equal(1, boxCalls);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(string.Empty, tab.StatusLine);
        Assert.True(tab.ShowsResult);
        Assert.NotEmpty(tab.BoxPlot.GroupNames);
    }

    // A half that LOADED and holds nothing is the third holding, between a drawn result and a half
    // that never loaded, and it is the one the smallest fix would have let through. The histogram
    // half here answered below two values, so its own sentence is the too-few one. Red against a
    // redraw that keeps the drawn state's sentence: the tab tells a reader whose dates are reversed
    // to widen the filters, which is the wrong advice for the state they are in.
    [Fact]
    public async Task SwitchingToAHalfThatLoadedNoRows_UnderAReversedRange_NamesTheRangeAndNotTheFilters()
    {
        AnalysisFilter? filter = Filter();
        var tab = Tab(
            distribution: (_, _) => null,
            filter: () => filter);
        await tab.PendingLoad!;

        Assert.Equal(AnalysisTabState.TooFew, tab.State);
        Assert.Equal(DistributionsTabViewModel.TooFewToBinText, tab.StatusLine);

        tab.IsBoxPlot = true;
        await tab.PendingLoad!;
        Assert.Equal(AnalysisTabState.Ready, tab.State);

        // Reversed, then Histogram: that half is loaded and holds nothing at all.
        filter = null;
        var pending = tab.PendingLoad;
        tab.IsBoxPlot = false;

        Assert.Same(pending, tab.PendingLoad);
        Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        Assert.False(tab.ShowsResult);
        Assert.Equal(AnalysisTabViewModel.ReversedRangeText, tab.StatusLine);
    }

    // The same holding on the other half and in the other direction, with the no-rows sentence
    // named explicitly because it is the one P2-2 was raised over. Red against the shipped redraw:
    // the box plot half answers Empty and the tab repeats "Widen them in the filter bar above."
    [Fact]
    public async Task SwitchingToABoxHalfThatLoadedNoRows_UnderAReversedRange_NeverShowsTheNoRowsSentence()
    {
        AnalysisFilter? filter = Filter();
        var tab = Tab(
            boxPlot: (_, _, _) => AnalysisViewModelTestFactory.Boxes(groups: 0),
            filter: () => filter);
        await tab.PendingLoad!;

        tab.IsBoxPlot = true;
        await tab.PendingLoad!;
        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.Equal(AnalysisTabViewModel.EmptyText, tab.StatusLine);

        // Back to the histogram under a legal range, which redraws its held result as usual.
        tab.IsBoxPlot = false;
        Assert.Equal(AnalysisTabState.Ready, tab.State);

        filter = null;
        var pending = tab.PendingLoad;
        tab.IsBoxPlot = true;

        Assert.Same(pending, tab.PendingLoad);
        Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        Assert.False(tab.ShowsResult);
        Assert.Equal(AnalysisTabViewModel.ReversedRangeText, tab.StatusLine);
        Assert.NotEqual(AnalysisTabViewModel.EmptyText, tab.StatusLine);
    }

    // The other arm of the same switch: a tab first selected under a reversed range has no result
    // on either half, so there is no stale region to close and the base's reversed-range sentence
    // is the only sentence there is. Red against a redraw that runs unconditionally: the segment
    // move replaces it with the no-rows wording, which tells the reader to widen filters that are
    // not the problem.
    [Fact]
    public void SwitchingTheHalfWithNothingEverDrawn_KeepsTheReversedRangeSentence()
    {
        var tab = Tab(filter: () => null);

        Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        Assert.Equal(AnalysisTabViewModel.ReversedRangeText, tab.StatusLine);

        tab.IsBoxPlot = true;

        Assert.Null(tab.PendingLoad);
        Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        Assert.Equal(AnalysisTabViewModel.ReversedRangeText, tab.StatusLine);
        Assert.False(tab.ShowsResult);

        // And back, which is the same holding in the other direction.
        tab.IsBoxPlot = false;

        Assert.Null(tab.PendingLoad);
        Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        Assert.Equal(AnalysisTabViewModel.ReversedRangeText, tab.StatusLine);
        Assert.False(tab.ShowsResult);
    }

    // ---- case 11 ------------------------------------------------------------------------------

    // A group the query dropped at its four-value gate leaves no row and no notice, which is what
    // _compute_box_plot returning null does. Red against a tab that renders an empty category.
    [Fact]
    public async Task ADroppedGroup_LeavesNoRowAndNoNotice()
    {
        var tab = Tab(boxPlot: (_, _, _) => Boxes("Ha"));

        await AnalysisSettle.Tab(tab);
        tab.IsBoxPlot = true;
        await AnalysisSettle.Tab(tab);

        Assert.Equal(["Ha"], tab.BoxPlot.GroupNames);
        Assert.DoesNotContain("OIII", tab.BoxPlot.GroupNames);
        Assert.Single(Assert.Single(tab.BoxPlot.Series.OfType<BoxSeries<BoxValue>>()).Values!);
        Assert.Equal(["Ha"], Assert.IsType<Axis>(tab.BoxPlot.XAxes[0]).Labels!);

        // Nothing on screen mentions the dropped group and no sentence appears at all.
        Assert.Equal(string.Empty, tab.StatusLine);
        Assert.True(tab.ShowsResult);
    }

    // ---- case 12 ------------------------------------------------------------------------------

    // Spec 12.14's States table, "Distributions with fewer than 2 values". The chart still
    // publishes one axis per side (spike trap 11): an empty axis array throws at rc5.4.
    [Fact]
    public async Task FewerThanTwoValues_StatesTheTooFewSentenceAndDrawsNothing()
    {
        var tab = Tab(distribution: (_, _) => null);
        await tab.PendingLoad!;

        Assert.Equal(AnalysisTabState.TooFew, tab.State);
        Assert.Equal(DistributionsTabViewModel.TooFewToBinText, tab.StatusLine);
        Assert.False(tab.ShowsResult);
        Assert.Empty(tab.Histogram.Series);
        Assert.Empty(tab.Histogram.Sections);
        Assert.Single(tab.Histogram.XAxes);
        Assert.Single(tab.Histogram.YAxes);
        Assert.False(tab.Card.IsVisible);
        Assert.Equal(0, tab.DistinctPlateScales);
    }

    // ---- spec 12.14's two distinct box plot states -----------------------------------------------

    // Rows matched and every group was dropped at the query's four-value gate. Red against the
    // no-rows arm: the reader is told to widen the filters, which admits more groups and is the
    // direction that makes this state worse, where grouping more coarsely is what helps.
    [Fact]
    public async Task ABoxPlotWithRowsButNoGroupLeft_StatesThatNoGroupHasEnoughFrames()
    {
        var tab = Tab(boxPlot: (_, _, _) => new BoxPlotResult([], 1, 30));

        await AnalysisSettle.Tab(tab);
        tab.IsBoxPlot = true;
        await AnalysisSettle.Tab(tab);
        tab.GroupBy = BoxPlotGrouping.Target;
        await AnalysisSettle.Tab(tab);

        Assert.Equal(AnalysisTabState.TooFew, tab.State);
        Assert.Equal(DistributionsTabViewModel.BoxNoGroupText, tab.StatusLine);
        Assert.NotEqual(
            "No frames match the current filters. Widen them in the filter bar above.",
            tab.StatusLine);
        Assert.DoesNotContain("Widen", tab.StatusLine, StringComparison.Ordinal);
        Assert.False(tab.ShowsResult);
        Assert.Empty(tab.BoxPlot.Series);
    }

    // No row matched at all, which is the OTHER row of the same table and takes the base's own
    // wording. Red against a tab that answers the no-group sentence for both: widening the filters
    // is exactly the right advice here.
    [Fact]
    public async Task ABoxPlotThatReadNoRowAtAll_TakesTheBasesEmptyWording()
    {
        var tab = Tab(boxPlot: (_, _, _) => new BoxPlotResult([], 0, 0));

        await AnalysisSettle.Tab(tab);
        tab.IsBoxPlot = true;
        await AnalysisSettle.Tab(tab);

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.Equal(
            "No frames match the current filters. Widen them in the filter bar above.",
            tab.StatusLine);
        Assert.NotEqual(DistributionsTabViewModel.BoxNoGroupText, tab.StatusLine);
        Assert.False(tab.ShowsResult);
    }

    // The two halves keep their own too-few sentence, which is what a single shared TooFewMessage
    // would lose the moment the reader switched back.
    [Fact]
    public async Task EachHalfKeepsItsOwnTooFewSentence()
    {
        var tab = Tab(
            distribution: (_, _) => null,
            boxPlot: (_, _, _) => new BoxPlotResult([], 1, 30));

        await tab.PendingLoad!;
        Assert.Equal(DistributionsTabViewModel.TooFewToBinText, tab.StatusLine);

        tab.IsBoxPlot = true;
        await tab.PendingLoad!;
        Assert.Equal(DistributionsTabViewModel.BoxNoGroupText, tab.StatusLine);

        tab.IsBoxPlot = false;
        await tab.PendingLoad!;
        Assert.Equal(DistributionsTabViewModel.TooFewToBinText, tab.StatusLine);
    }

    // ---- the filter travels with the result ------------------------------------------------------

    // Two queries in flight, run out of issue order, with the publishes drained afterwards. The
    // two gates and a queueing post seam make the interleaving deterministic:
    //
    //   query 2 runs and publishes into the queue, then query 1 runs, then the queue is drained.
    //
    // A field written inside Query and read inside Map therefore holds refresh ONE's filter when
    // refresh TWO's result is mapped, and the half records a filter its result was never computed
    // under. Red against that field: the half then believes its held result predates the bar's
    // current filter and re-queries on a segment switch that should have redrawn, which is the
    // assertion at the end.
    [Fact]
    public async Task TheFilterTravelsWithTheResult_WhenTwoQueriesRunOutOfIssueOrder()
    {
        var posted = new List<Action>();
        var held = new SemaphoreSlim(0, 1);
        var secondRan = new SemaphoreSlim(0, 1);
        var calls = 0;
        var filter = Filter();

        void Drain()
        {
            List<Action> batch;
            lock (posted)
            {
                batch = [.. posted];
                posted.Clear();
            }

            foreach (var publish in batch)
            {
                publish();
            }
        }

        var tab = Tab(
            distribution: (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    // Held until query 2 has run and posted its own publish.
                    held.Wait(TimeSpan.FromSeconds(30));
                }
                else
                {
                    secondRan.Release();
                }

                return AnalysisViewModelTestFactory.Distribution();
            },
            filter: () => filter,
            post: action =>
            {
                lock (posted)
                {
                    posted.Add(action);
                }
            });

        var firstLoad = tab.PendingLoad;

        // Refresh two, under a DIFFERENT filter, issued while refresh one is still held.
        filter = Filter("RC8", "ASI2600MM");
        tab.Refresh();
        var secondLoad = tab.PendingLoad;

        await secondRan.WaitAsync(TimeSpan.FromSeconds(30));
        await secondLoad!;

        held.Release();
        await firstLoad!;

        // Refresh two's publish runs first; refresh one's is dropped by the base's generation.
        Drain();

        Assert.Equal(2, calls);
        Assert.Equal(AnalysisTabState.Ready, tab.State);

        tab.IsBoxPlot = true;
        await tab.PendingLoad!;
        Drain();

        // The histogram's held result WAS read under the bar's current filter, so coming back
        // redraws it and queries nothing.
        var pending = tab.PendingLoad;
        tab.IsBoxPlot = false;
        Assert.Same(pending, tab.PendingLoad);
        Assert.Equal(2, calls);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
    }

    // ---- case 20d, the empty-state axis rule per chart -----------------------------------------

    [Fact]
    public async Task AnEmptyBoxPlot_PublishesOneAxisPerSideAndAnEmptySeriesList()
    {
        var tab = Tab(boxPlot: (_, _, _) => new BoxPlotResult([], 0, 0));

        await AnalysisSettle.Tab(tab);
        tab.IsBoxPlot = true;
        await AnalysisSettle.Tab(tab);

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.Empty(tab.BoxPlot.Series);
        Assert.Single(tab.BoxPlot.XAxes);
        Assert.Single(tab.BoxPlot.YAxes);
        Assert.True(tab.BoxPlot.IsEmpty);
    }

    // ---- the stats card -----------------------------------------------------------------------

    [Fact]
    public async Task TheStatsCard_IsLabelledWithTheMetricAndItsSkewnessToTwoDecimals()
    {
        var stats = new SummaryStats(12, 1.1d, 3.3d, 2.2d, 2.1d, 0.4d);
        var tab = Tab(distribution: (_, _) => new DistributionResult(
            [new HistogramBin(1d, 2d, 12)], stats, -0.4567d, 1));

        await tab.PendingLoad!;

        Assert.Equal("HFR (px) (skewness: -0.46)", tab.Card.Label);
        Assert.True(tab.Card.IsVisible);
        Assert.Equal("2.10 px", tab.Card.Median);
    }

    // ---- case 20c, ruling P1-7 and finding B13 -------------------------------------------------

    // Per chart: every paint equals ChartTheme.Read of its own key before the swap and equals the
    // same call's new answer after it; then the disposed view-model ignores a further Apply. Red
    // against a chart with no ChartTheme.Subscribe: the series object is never rebuilt and the
    // after-values keep the first theme's ink (spike trap 5). Red against a missing Dispose: the
    // detached view-model still re-paints.
    //
    // Two themes, because in one theme a hard-coded literal can happen to equal the token, and
    // every paint is asserted to differ from ChartTheme.Fallback and from the paints whose role
    // differs from its own. The fallback assertion is the one that catches the coordinator's trap:
    // ChartTheme.Read answers its documented neutral rather than throwing off the UI thread, so a
    // chart that resolved its tokens inside a rebuild run on the query's thread-pool thread would
    // paint every layer grey while a case resolving on the same thread compared one fallback with
    // another. Both charts read their inks on the UI thread at construction and on Changed and
    // hold them, which is what this case pins.
    [AvaloniaFact]
    public void BothChartsRepaintOnAThemeSwapAndReleaseTheirToken()
    {
        try
        {
            ThemeManager.Apply(ThemeManager.Available[0]);

            var histogram = new HistogramChartViewModel();
            var box = new BoxPlotChartViewModel();
            _charts.Add(histogram);
            _charts.Add(box);

            histogram.Show(AnalysisViewModelTestFactory.Distribution(), AnalysisMetric.Hfr);
            box.Show([new BoxPlot("Ha", 1d, 2d, 3d, 4d, 5d, [6d], 12)], AnalysisMetric.Hfr);

            var first = AssertPaints(histogram, box);
            var histogramSeries = histogram.Series;
            var boxSeries = box.Series;

            ThemeManager.Apply("deep-sky");

            // Rebuilt, which is what the subscription is for, and repainted from the new answers.
            Assert.NotSame(histogramSeries, histogram.Series);
            Assert.NotSame(boxSeries, box.Series);
            var second = AssertPaints(histogram, box);

            // The swap moved real ink somewhere, so the case is not passing on a theme pair that
            // happens to declare the same five colours.
            Assert.NotEqual(first, second);

            histogram.Dispose();
            box.Dispose();
            var frozenHistogram = histogram.Series;
            var frozenBox = box.Series;

            ThemeManager.Apply("red-light");

            Assert.Same(frozenHistogram, histogram.Series);
            Assert.Same(frozenBox, box.Series);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // The coordinator's trap, as its own case. A tab under the windowless inline post seam maps
    // its result, and therefore rebuilds its chart, on the query's own thread-pool thread.
    // ChartTheme.Read degrades to ChartTheme.Fallback there and nothing throws, so a chart that
    // resolved its tokens inside that rebuild would paint every layer the neutral grey while the
    // application itself, publishing through UiPost.Default, painted correctly. Red against a
    // chart that resolves inside Rebuild: both paints come back as ChartTheme.Fallback and the
    // assertion names the first.
    [AvaloniaFact]
    public async Task AChartRebuiltOffTheUiThread_KeepsTheInksItReadOnIt()
    {
        ChartTheme.Apply();

        // Constructed on the UI thread, which is where both charts read and hold their inks.
        var tab = Tab(boxPlot: (_, _, _) => new BoxPlotResult(
            [new BoxPlot("Ha", 1d, 2d, 3d, 4d, 5d, [6d], 12)], 1, 12));

        await tab.PendingLoad!;
        tab.IsBoxPlot = true;
        await tab.PendingLoad!;

        var columns = Assert.IsType<ColumnSeries<ObservablePoint>>(Assert.Single(tab.Histogram.Series));
        var columnInk = Assert.IsType<SolidColorPaint>(columns.Fill).Color;
        var boxInk = Assert.IsType<SolidColorPaint>(
            Assert.Single(tab.BoxPlot.Series.OfType<BoxSeries<BoxValue>>()).Fill).Color;

        Assert.NotEqual(ChartTheme.Fallback, columnInk);
        Assert.NotEqual(ChartTheme.Fallback, boxInk);
        Assert.Equal(ChartTheme.Read(HistogramChartViewModel.ColumnTokenKey, ChartTheme.Fallback), columnInk);
        Assert.Equal(ChartTheme.Read(BoxPlotChartViewModel.BoxTokenKey, ChartTheme.Fallback), boxInk);
    }

    // Every paint against its own token, against the documented neutral, and against the paints it
    // must be told apart from. Returns the five inks so the caller can compare two themes.
    private static IReadOnlyList<SKColor> AssertPaints(
        HistogramChartViewModel histogram, BoxPlotChartViewModel box)
    {
        var columns = Assert.IsType<ColumnSeries<ObservablePoint>>(Assert.Single(histogram.Series));
        var columnInk = Assert.IsType<SolidColorPaint>(columns.Fill).Color;
        var referenceInk = Assert.IsType<SolidColorPaint>(Assert.Single(histogram.Sections).Stroke).Color;

        var series = Assert.Single(box.Series.OfType<BoxSeries<BoxValue>>());
        var boxInk = Assert.IsType<SolidColorPaint>(series.Fill).Color;
        var outlineInk = Assert.IsType<SolidColorPaint>(series.Stroke).Color;

        var rings = Assert.Single(box.Series.OfType<ScatterSeries<ObservablePoint, CircleGeometry>>());
        var outlierInk = Assert.IsType<SolidColorPaint>(rings.Stroke).Color;

        // The look's F5: the median mark's ink is CHOSEN against the box fill on every theme, so
        // it is asserted here through the same rule rather than against a token of its own.
        var mark = Assert.Single(box.Series.OfType<ScatterSeries<ObservablePoint, MedianMarkGeometry>>());
        var medianInk = Assert.IsType<SolidColorPaint>(mark.Fill).Color;
        Assert.Equal(
            InkContrast.Choose(
                boxInk,
                outlineInk,
                ChartTheme.Read(BoxPlotChartViewModel.MedianAltTokenKey, ChartTheme.Fallback).WithAlpha(0xFF)),
            medianInk);
        Assert.NotEqual(boxInk, medianInk);
        Assert.True(
            InkContrast.Contrast(medianInk, boxInk) >= InkContrast.Contrast(outlineInk, boxInk),
            "the chosen ink is never worse against the box fill than text-primary is");

        Assert.Equal(ChartTheme.Read(HistogramChartViewModel.ColumnTokenKey, ChartTheme.Fallback), columnInk);
        Assert.Equal(ChartTheme.Read(HistogramChartViewModel.MedianTokenKey, ChartTheme.Fallback), referenceInk);
        Assert.Equal(ChartTheme.Read(BoxPlotChartViewModel.BoxTokenKey, ChartTheme.Fallback), boxInk);
        Assert.Equal(ChartTheme.Read(BoxPlotChartViewModel.OutlineTokenKey, ChartTheme.Fallback), outlineInk);
        Assert.Equal(ChartTheme.Read(BoxPlotChartViewModel.OutlierTokenKey, ChartTheme.Fallback), outlierInk);

        IReadOnlyList<SKColor> inks = [columnInk, referenceInk, boxInk, outlineInk, outlierInk];
        Assert.All(inks, ink => Assert.NotEqual(ChartTheme.Fallback, ink));

        // Ruling A25's whole point: the median mark and the box must be told apart, and so must the
        // outlier rings.
        Assert.NotEqual(boxInk, outlineInk);
        Assert.NotEqual(boxInk, outlierInk);
        Assert.NotEqual(outlineInk, outlierInk);
        Assert.NotEqual(columnInk, referenceInk);

        return inks;
    }
}
