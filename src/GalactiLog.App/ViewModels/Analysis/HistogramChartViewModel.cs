using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Theme;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// Spec 13's "Metric histogram": the Distributions tab's histogram half, a column series over the
/// query's own bins with a dashed vertical reference line at the distribution's median.
/// </summary>
/// <remarks>
/// <para>
/// This view-model computes no statistic. The bins, the median and the skewness are
/// <c>DistributionResult</c>'s own arithmetic; the only arithmetic here is the
/// value-to-coordinate mapping of <see cref="SectionAt"/>, which converts the median from the
/// metric's own domain into the bin-edge domain the chart is drawn in.
/// </para>
/// <para>
/// <b>One convention, in bin-EDGE space.</b> Bin <c>i</c> occupies the axis from <c>i</c> to
/// <c>i + 1</c>. Its bar spans that whole interval, edge to edge, because a histogram bar IS its
/// bin; its tick sits at <c>i</c>, the bar's LEFT edge, and is labelled with the bin's own lower
/// edge; and the median line falls inside the bar at its proportional position within the bin. The
/// three used to be in three different places: the ticks stood at the bin edges, the bars were
/// drawn centred on those ticks at the library's default 50 pixel cap, and the median landed at
/// the bin's centre, which on a wide chart put it in the empty gap between two columns.
/// </para>
/// <para>
/// Edge-to-edge bars are the library's own documented pairing, <c>MaxBarWidth</c> of
/// <c>double.MaxValue</c> with <c>Padding</c> of zero, over a NUMERIC axis rather than a category
/// one: a column on a category axis is centred on its tick, so the lower-edge label could only
/// ever sit at the bar's middle there. The numeric axis carries one tick per bin edge through
/// <c>MinStep</c> 1 with <c>ForceStepToMin</c>, and <c>MinLimit</c> and <c>MaxLimit</c> pin it to
/// exactly the bins so the first bar starts at the axis's origin and the last one ends at its end.
/// </para>
/// <para>
/// Ruling P1-7: the two inks are resolved through <see cref="ChartTheme.Read(string, SKColor)"/>
/// on the UI thread at construction and again on every <c>ChartTheme.Changed</c>, HELD as fields,
/// and only then used by <see cref="Rebuild"/>. Resolving them inside the rebuild would be a trap:
/// <c>ChartTheme.Read</c> answers its documented neutral rather than throwing when it is called
/// off the UI thread, and a rebuild can run on the query's own thread-pool thread under a post
/// seam that runs its closure inline, so every layer would paint grey and a case that resolved the
/// token on the same thread would compare one fallback with another and read green. The
/// subscription is held as one token and disposed with the view-model, because
/// <c>ChartTheme.Changed</c> is static and a handler left behind pins its view-model for the life
/// of the process. <c>ChartTheme.Palette</c> would not do: its <c>Metrics</c> dictionary carries
/// the ten <c>metric-*</c> keys only and holds no <c>ColorTextSecondary</c>.
/// </para>
/// <para>
/// <see cref="PublishEmpty"/> publishes one bare axis per side rather than none, which is the rule
/// <c>StatsBarChartViewModel</c> and <c>MetricChartViewModel</c> already document: rc5.4's
/// <c>CartesianChartEngine.Measure</c> throws "XAxes and YAxes must contain at least one element",
/// and the control measures while it is collapsed. <c>ChartEmptyAxisCensusTest</c> checks this
/// rule on every chart view-model, not only this one.
/// </para>
/// </remarks>
public sealed partial class HistogramChartViewModel : ObservableObject, IDisposable
{
    /// <summary>The columns' token.</summary>
    public const string ColumnTokenKey = "ColorMetricHfr";

    /// <summary>The median reference line's token.</summary>
    public const string MedianTokenKey = "ColorTextSecondary";

    /// <summary>The value axis's own name. The metric's name and unit are the bin axis's,
    /// from <see cref="AnalysisMetricLabels"/>; this axis counts frames and has no unit of its
    /// own.</summary>
    public const string FrameAxisName = "Frames";

    /// <summary>The median line's draw order, over the columns rather than behind them. Well
    /// clear of the series' own, which rc5.4 derives from the series' position, and set on the
    /// section AND on its paint because the two are not the same order: the launched look showed
    /// the line still behind the bar with the section's own index at 1.</summary>
    public const int MedianLineZIndex = 1000;

    // A tick the library asks for between two bin edges is named by nothing, and ForceStepToMin
    // keeps it from asking. Floating point is why this is a tolerance and not an equality.
    private const double EdgeTolerance = 1e-6d;

    private readonly IDisposable _themeSubscription;

    // Resolved on the UI thread and held, never resolved inside Rebuild: see the remarks above.
    private SKColor _columnInk;
    private SKColor _referenceInk;

    private IReadOnlyList<HistogramBin> _bins = [];
    private double _median;
    private bool _closedLastBin;
    private AnalysisMetric _metric = AnalysisMetric.Hfr;
    private bool _disposed;

    /// <summary>Builds the empty state, which is what a tab that has not queried yet draws.</summary>
    public HistogramChartViewModel()
    {
        Series = [];
        Sections = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        BinTooltips = [];
        IsEmpty = true;

        ReadInks();
        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>What the <c>CartesianChart</c> binds to. Replaced wholesale on a rebuild rather
    /// than mutated, so LiveCharts sees one change instead of N.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> XAxes { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> YAxes { get; private set; }

    /// <summary>The median reference line, or empty when the median falls outside every bin.
    /// A <c>RectangularSection</c> with <c>Xi</c> equal to <c>Xj</c> and never a series, so the
    /// line enters neither the legend nor the tooltip (ruling P1-6).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RectangularSection> Sections { get; private set; }

    /// <summary>One tooltip per bin, in bin order, which the column series' own formatter reads by
    /// point index. Exposed because a case pins both strings and a <c>ChartPoint</c> cannot be
    /// constructed outside a live chart.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> BinTooltips { get; private set; }

    /// <summary>True when there is nothing to plot.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Draws one distribution answer. Every figure is the query's.</summary>
    /// <param name="result">The query's answer for the chosen metric and filter.</param>
    /// <param name="metric">The chosen metric, for the axis name and the unit.</param>
    public void Show(DistributionResult result, AnalysisMetric metric)
    {
        ArgumentNullException.ThrowIfNull(result);

        _bins = result.Bins;
        _median = result.Stats.Median;
        _metric = metric;

        // Seam ruling S8: user ruling U1's closed last edge applies only where the data has a
        // range at all. A metric that is constant over the filtered frames keeps the Python's own
        // single-bin shape and its one bin takes the ordinary half-open form. Bins.Count does not
        // discriminate, because Sturges' rule counts values and not range; the published summary
        // does.
        _closedLastBin = result.Stats.Max > result.Stats.Min;
        Rebuild();
    }

    /// <summary>Drops the data and publishes the empty state.</summary>
    public void Clear()
    {
        _bins = [];
        Rebuild();
    }

    /// <summary>
    /// Where a value in the metric's own domain lands on the bin-edge axis, the proportional
    /// position of pinned <c>HistogramChart.tsx</c> lines 15 to 33 carried onto this chart's own
    /// convention: bin <c>i</c> runs from <c>i</c> to <c>i + 1</c>, so a value in that bin answers
    /// strictly between the two and the line falls inside the drawn bar.
    /// </summary>
    /// <remarks>
    /// A median in the metric's own units is not a coordinate on this axis at all:
    /// <c>Xi = Stats.Median</c> puts an HFR median of 1.36 at bin 1. The zero-width arm is the
    /// constant-metric single-bin case, where the line lands on the column's own centre rather
    /// than at its edge. Both clamps are the web's, in this space: a value under the first bin
    /// answers the first bin's left edge and a value over the last bin answers the last bin's
    /// right edge.
    /// </remarks>
    /// <returns>The X coordinate, or null when there are no bins and therefore no axis to land
    /// on.</returns>
    internal static double? SectionAt(IReadOnlyList<HistogramBin> bins, double value)
    {
        for (var index = 0; index < bins.Count; index++)
        {
            var bin = bins[index];
            if (value < bin.BinStart || value > bin.BinEnd)
            {
                continue;
            }

            var width = bin.BinEnd - bin.BinStart;
            var fraction = width > 0d ? (value - bin.BinStart) / width : 0.5d;
            return index + fraction;
        }

        if (bins.Count == 0)
        {
            return null;
        }

        return value < bins[0].BinStart ? 0d
            : value > bins[^1].BinEnd ? bins.Count
            : null;
    }

    /// <summary>
    /// The tick label at <paramref name="value"/>: the bin edge standing there, or nothing at all
    /// where the library asks for a position between two edges. <paramref name="edges"/> holds one
    /// entry per bin lower edge and one more for the last bin's upper edge, which under user
    /// ruling U1 is the distribution's own maximum.
    /// </summary>
    internal static string EdgeLabel(IReadOnlyList<string> edges, double value)
    {
        var index = (int)Math.Round(value);
        return Math.Abs(value - index) > EdgeTolerance || index < 0 || index >= edges.Count
            ? string.Empty
            : edges[index];
    }

    /// <summary>
    /// One bin's tooltip (ruling P2-7). The web renders the two edges around an en dash with no
    /// bracket at all, which this port's prose rules forbid, so both strings are authored against
    /// spec 12.14 instead: the closing bracket is the only visible trace of user ruling U1, whose
    /// last bin admits a value equal to its own upper edge.
    /// </summary>
    /// <param name="bin">The bin.</param>
    /// <param name="closed">Whether this is the last bin of a ranged distribution, which is the
    /// one bin whose upper edge is inclusive.</param>
    internal static string Tooltip(HistogramBin bin, bool closed)
    {
        var frames = bin.Count == 1
            ? "1 frame"
            : $"{bin.Count.ToString(CultureInfo.InvariantCulture)} frames";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{bin.BinStart:F2} to {bin.BinEnd:F2}{(closed ? ']' : ')')}, {frames}");
    }

    private void Rebuild()
    {
        if (_bins.Count == 0)
        {
            PublishEmpty();
            return;
        }

        var tooltips = new string[_bins.Count];
        for (var index = 0; index < _bins.Count; index++)
        {
            tooltips[index] = Tooltip(_bins[index], closed: _closedLastBin && index == _bins.Count - 1);
        }

        BinTooltips = tooltips;

        var label = AnalysisMetricLabels.Label(_metric);
        Series =
        [
            new ColumnSeries<ObservablePoint>
            {
                Name = label,

                // Each bar is centred on its own bin's centre, which in this space is the bin's
                // index plus a half.
                Values = [.. _bins.Select((bin, index) => new ObservablePoint(index + 0.5d, bin.Count))],

                // Edge to edge, the library's own documented pairing for a bar with no gap. The
                // default 50 pixel cap and the default padding drew a narrow column in the middle
                // of a wide empty slot, which is what put the median line between two bars.
                MaxBarWidth = double.MaxValue,
                Padding = 0,
                Fill = new SolidColorPaint(_columnInk),
                Stroke = null,

                // The formatter reads the list above rather than formatting here, so the two
                // strings a case pins and the two strings the chart shows are one thing.
                YToolTipLabelFormatter = point => TooltipAt(point.Index),
            },
        ];

        // Ruling P3-8: each tick is labelled with a bin edge to one decimal, which is pinned
        // HistogramChart.tsx line 44's bin_start.toFixed(1). The tick for bin i stands at i, the
        // LEFT edge of that bin's bar, and the one extra tick at the end names the last bin's
        // upper edge, the only edge no bin's lower edge names.
        var edges = new string[_bins.Count + 1];
        for (var index = 0; index < _bins.Count; index++)
        {
            edges[index] = _bins[index].BinStart.ToString("F1", CultureInfo.InvariantCulture);
        }

        edges[^1] = _bins[^1].BinEnd.ToString("F1", CultureInfo.InvariantCulture);

        XAxes =
        [
            new Axis
            {
                Name = label,
                Labeler = value => EdgeLabel(edges, value),
                MinStep = 1,
                ForceStepToMin = true,
                MinLimit = 0,
                MaxLimit = _bins.Count,
            },
        ];

        YAxes =
        [
            new Axis
            {
                Name = FrameAxisName,
                Labeler = value => value.ToString("N0", CultureInfo.InvariantCulture),
                MinLimit = 0,
            },
        ];

        Sections = SectionAt(_bins, _median) is { } x
            ?
            [
                new RectangularSection
                {
                    Xi = x,
                    Xj = x,

                    // Over the bars, not under them. A section draws behind the series by
                    // default, and now that a bar spans its whole bin the line falls inside a
                    // drawn bar and was hidden by it everywhere except above the bar's own top.
                    ZIndex = MedianLineZIndex,
                    Stroke = new SolidColorPaint(_referenceInk)
                    {
                        StrokeThickness = 2,
                        PathEffect = new DashEffect([6f, 4f]),
                        ZIndex = MedianLineZIndex,
                    },
                },
            ]
            : [];

        IsEmpty = false;
    }

    // On the UI thread, from the constructor and from ChartTheme.Changed, which is raised on it.
    private void ReadInks()
    {
        _columnInk = ChartTheme.Read(ColumnTokenKey, ChartTheme.Fallback);
        _referenceInk = ChartTheme.Read(MedianTokenKey, ChartTheme.Fallback);
    }

    private void OnThemeChanged()
    {
        ReadInks();
        Rebuild();
    }

    private string TooltipAt(int index)
        => index >= 0 && index < BinTooltips.Count ? BinTooltips[index] : string.Empty;

    // Fresh axis instances per call, because an axis carries measure state and two charts must not
    // share one.
    private void PublishEmpty()
    {
        Series = [];
        Sections = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        BinTooltips = [];
        IsEmpty = true;
    }

    /// <summary>The static theme outlives every chart, so a chart that does not unsubscribe keeps
    /// rebuilding after its page has closed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _themeSubscription.Dispose();
    }
}
