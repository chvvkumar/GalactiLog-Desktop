using Avalonia.Headless.XUnit;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.StatisticsViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// The roadmap's first named assertion for Phase 9 row 3: "each chart binds its specified series
// and colour token". One case per chart in spec 13's table, asserting the series type, the axis
// configuration and the resolved colour against ChartTheme.Palette's entry for the token spec 13
// names.
//
// AvaloniaFact throughout, because the palette is only real once Apply has read the merged token
// dictionary; with no application every entry falls back to the neutral grey and a colour
// assertion would pass by agreeing with itself.
public class StatsChartBindingTests
{
    private const string FilterColour = "#FF2211";

    private static SKColor Token(string key) => ChartTheme.Palette.Metrics[key];

    private static StatisticsViewModel CreatePage()
    {
        ChartTheme.Apply();
        return Factory.Create(aliases: () => Factory.Aliases(("Ha", FilterColour)));
    }

    private static SKColor FillOf(ISeries series) => series switch
    {
        ColumnSeries<double?> column => ((SolidColorPaint)column.Fill!).Color,
        RowSeries<double?> row => ((SolidColorPaint)row.Fill!).Color,
        LineSeries<double?> line => ((SolidColorPaint)line.Stroke!).Color,
        PieSeries<double> pie => ((SolidColorPaint)pie.Fill!).Color,
        _ => throw new InvalidOperationException($"Unhandled series type {series.GetType().Name}"),
    };

    [AvaloniaFact]
    public void FilterUsage_IsAHorizontalBar_TintedWithEachFiltersConfiguredColour()
    {
        using var page = CreatePage();

        // Spec 13: "Horizontal bar ... One bar per canonical filter, tinted with its configured
        // colour." One series per filter is how a per-bar tint is expressed; see
        // StatsBarChartViewModel's remarks.
        Assert.Equal(StatsChartOrientation.Bar, page.FilterUsageChart.Orientation);
        Assert.Equal(2, page.FilterUsageChart.Series.Count);
        Assert.All(page.FilterUsageChart.Series, series => Assert.IsType<RowSeries<double?>>(series));

        var ha = page.FilterUsageChart.Series.Single(series => series.Name == "Ha");
        Assert.Equal(new SKColor(0xFF, 0x22, 0x11), FillOf(ha));

        // Categories on Y, values on X.
        var categorical = Assert.IsType<Axis>(page.FilterUsageChart.YAxes[0]);
        Assert.NotNull(categorical.Labels);
        Assert.Null(Assert.IsType<Axis>(page.FilterUsageChart.XAxes[0]).Labels);
    }

    [AvaloniaFact]
    public void FilterUsage_UnconfiguredFilter_FallsBackToTheNeutralGrey()
    {
        ChartTheme.Apply();
        // P13 R2a re-pointed this case. The sample's default filter usage is Ha and OIII, which
        // now fold to the seeded palette and read red and blue; the grey is what a filter nobody
        // configured AND whose name folds to no category reads as, so the usage list names two.
        using var page = Factory.Create(
            loadStats: () => Factory.Sample(
                filterUsage: [new FilterUsageEntry("Duoband", 60_000d), new FilterUsageEntry("IR", 90_000d)]),
            aliases: Factory.NoAliases);

        // Spec 5.8.4's #808080, which is the value FilterColor.Fallback carries and the one
        // AliasMap.FilterColor hands back at the end of its resolution order.
        Assert.All(
            page.FilterUsageChart.Series,
            series => Assert.Equal(new SKColor(0x80, 0x80, 0x80), FillOf(series)));
    }

    [AvaloniaFact]
    public void TopTargets_IsAHorizontalBar_InMetricIntegration()
    {
        using var page = CreatePage();

        Assert.Equal(StatsChartOrientation.Bar, page.TopTargetsChart.Orientation);
        var series = Assert.IsType<RowSeries<double?>>(Assert.Single(page.TopTargetsChart.Series));
        Assert.Equal(Token("ColorMetricIntegration"), FillOf(series));
    }

    [AvaloniaFact]
    public void ImagingTimeline_IntegrationSeries_IsAColumnInMetricIntegration()
    {
        using var page = CreatePage();

        var integration = Assert.IsType<ColumnSeries<double?>>(page.Timeline.Series[0]);
        Assert.Equal(Token("ColorMetricIntegration"), FillOf(integration));
        Assert.Equal(0, integration.ScalesYAt);

        // Spec 13: "Period (month, ISO week, or day) categorical" on X, integration hours on Y.
        Assert.NotNull(Assert.IsType<Axis>(page.Timeline.XAxes[0]).Labels);
    }

    [AvaloniaFact]
    public void ImagingTimeline_EfficiencySeries_IsALineInMetricTime_OnASecondaryZeroToHundredAxis()
    {
        using var page = CreatePage();

        var efficiency = Assert.IsType<LineSeries<double?>>(page.Timeline.Series[1]);
        Assert.Equal(Token("ColorMetricTime"), FillOf(efficiency));
        Assert.Equal(1, efficiency.ScalesYAt);

        var secondary = Assert.IsType<Axis>(page.Timeline.YAxes[1]);
        Assert.Equal(0d, secondary.MinLimit);
        Assert.Equal(100d, secondary.MaxLimit);
        Assert.Equal(AxisPosition.End, secondary.Position);
    }

    [AvaloniaFact]
    public void ImagingTimeline_EfficiencySeries_IsAbsent_WhenCoordinatesAreUnset()
    {
        ChartTheme.Apply();
        using var page = Factory.Create(general: Factory.WithoutCoordinates);

        // The roadmap's second named assertion, at the chart level: no series and no second axis.
        Assert.Single(page.Timeline.Series);
        Assert.Single(page.Timeline.YAxes);
    }

    [AvaloniaFact]
    public void HfrPixelHistogram_IsAColumnInMetricHfr()
    {
        using var page = CreatePage();

        var series = Assert.IsType<ColumnSeries<double?>>(Assert.Single(page.HfrPixelChart.Series));
        Assert.Equal(Token("ColorMetricHfr"), FillOf(series));
        Assert.Equal(StatsChartOrientation.Column, page.HfrPixelChart.Orientation);

        // questions.md Q4: every bucket, zero-count ones included.
        Assert.Equal(2, series.Values!.Count);
    }

    [AvaloniaFact]
    public void HfrArcsecHistogram_IsAColumnInMetricHfr()
    {
        using var page = CreatePage();

        var series = Assert.IsType<ColumnSeries<double?>>(Assert.Single(page.HfrArcsecChart.Series));
        Assert.Equal(Token("ColorMetricHfr"), FillOf(series));
        Assert.Equal(StatsChartOrientation.Column, page.HfrArcsecChart.Orientation);
    }

    [AvaloniaFact]
    public void EquipmentPerformanceComparison_HasOneSeriesPerMetric_InHfrFwhmAndGuidingTokens()
    {
        using var page = CreatePage();

        Assert.Equal(3, page.EquipmentComparisonChart.Series.Count);
        Assert.Equal(
            new[] { Token("ColorMetricHfr"), Token("ColorMetricFwhm"), Token("ColorMetricGuiding") },
            page.EquipmentComparisonChart.Series.Select(FillOf));
        Assert.All(
            page.EquipmentComparisonChart.Series,
            series => Assert.IsType<ColumnSeries<double?>>(series));
    }

    [AvaloniaFact]
    public void IngestHistory_IsAColumnInMetricFrames()
    {
        using var page = CreatePage();

        var series = Assert.IsType<ColumnSeries<double?>>(Assert.Single(page.IngestHistoryChart.Series));
        Assert.Equal(Token("ColorMetricFrames"), FillOf(series));
    }

    [AvaloniaFact]
    public void StorageBreakdown_HasThreeSlices_InIntegrationFramesAndStars()
    {
        using var page = CreatePage();

        // Three, not spec 13's four: the "other disk usage" slice went with the dropped du figure
        // (questions.md Q11), and the coordinator's Task 2 review ruling amends spec 13's row at
        // phase close. border-emphasis therefore goes unused on this screen.
        Assert.Equal(3, page.Storage.Series.Count);
        Assert.All(page.Storage.Series, series => Assert.IsType<PieSeries<double>>(series));
        Assert.Equal(
            new[] { Token("ColorMetricIntegration"), Token("ColorMetricFrames"), Token("ColorMetricStars") },
            page.Storage.Series.Select(FillOf));
        Assert.Equal(
            new[] { "Catalogued FITS", "Thumbnail cache", "Database" },
            page.Storage.Series.Select(series => series.Name));
    }

    public static TheoryData<string> EveryCartesianChart() =>
    [
        "FilterUsage", "TopTargets", "HfrPixel", "HfrArcsec", "EquipmentComparison", "IngestHistory",
        "Timeline",
    ];

    [AvaloniaTheory]
    [MemberData(nameof(EveryCartesianChart))]
    public void EveryChartEmptyState_PublishesAtLeastOneAxisPerSide(string chart)
    {
        ChartTheme.Apply();
        using var page = Factory.Create(loadStats: () => Factory.Empty());

        var (series, xAxes, yAxes) = chart switch
        {
            "FilterUsage" => Axes(page.FilterUsageChart),
            "TopTargets" => Axes(page.TopTargetsChart),
            "HfrPixel" => Axes(page.HfrPixelChart),
            "HfrArcsec" => Axes(page.HfrArcsecChart),
            "EquipmentComparison" => Axes(page.EquipmentComparisonChart),
            "IngestHistory" => Axes(page.IngestHistoryChart),
            _ => (page.Timeline.Series, page.Timeline.XAxes, page.Timeline.YAxes),
        };

        // FIXER LIST item 10: rc5.4's CartesianChartEngine.Measure throws "XAxes and YAxes must
        // contain at least one element", and the control measures while it is collapsed, so an
        // empty state that publishes no axes takes the page down from a chart nobody can see.
        Assert.Empty(series);
        Assert.NotEmpty(xAxes);
        Assert.NotEmpty(yAxes);
    }

    [AvaloniaFact]
    public void StorageBreakdown_EmptyState_PublishesNoSeries_AndHasNoAxesToPublish()
    {
        ChartTheme.Apply();
        using var page = Factory.Create(loadStats: () => Factory.Empty());

        // The pie is the one chart FIXER item 10 does not reach: a PieChart has no axis
        // collections at all, so there is nothing that can be empty.
        Assert.True(page.Storage.IsEmpty);
        Assert.Empty(page.Storage.Series);
    }

    [AvaloniaFact]
    public void NoChartSetsAPaintOfItsOwn()
    {
        using var page = CreatePage();

        // Spec 13: axis label and separator paints come from ChartTheme's global rule and are
        // re-read on theme change. A chart that set its own would survive a theme swap unchanged.
        foreach (var axis in AllAxes(page))
        {
            Assert.Null(axis.LabelsPaint);
            Assert.Null(axis.SeparatorsPaint);
            Assert.Null(axis.NamePaint);
            Assert.Null(axis.TicksPaint);
            Assert.Null(axis.SubseparatorsPaint);
            Assert.Null(axis.ZeroPaint);
        }
    }

    [AvaloniaFact]
    public void EveryChart_RepublishesOnAThemeChange()
    {
        using var page = CreatePage();
        var before = page.TopTargetsChart.Series;
        var timelineBefore = page.Timeline.Series;
        var storageBefore = page.Storage.Series;

        ChartTheme.Apply();

        // Re-published, not mutated in place: LiveCharts sees one change instead of N, and the
        // paints the control already holds are replaced.
        Assert.NotSame(before, page.TopTargetsChart.Series);
        Assert.NotSame(timelineBefore, page.Timeline.Series);
        Assert.NotSame(storageBefore, page.Storage.Series);
    }

    private static (IReadOnlyList<ISeries> Series, IReadOnlyList<ICartesianAxis> X, IReadOnlyList<ICartesianAxis> Y)
        Axes(StatsBarChartViewModel chart) => (chart.Series, chart.XAxes, chart.YAxes);

    private static IEnumerable<Axis> AllAxes(StatisticsViewModel page)
    {
        StatsBarChartViewModel[] charts =
        [
            page.FilterUsageChart, page.TopTargetsChart, page.HfrPixelChart, page.HfrArcsecChart,
            page.EquipmentComparisonChart, page.IngestHistoryChart,
        ];

        foreach (var chart in charts)
        {
            foreach (var axis in chart.XAxes.Concat(chart.YAxes).OfType<Axis>())
            {
                yield return axis;
            }
        }

        foreach (var axis in page.Timeline.XAxes.Concat(page.Timeline.YAxes).OfType<Axis>())
        {
            yield return axis;
        }
    }
}
