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
/// <b>The ticks are NOT the bin edges.</b> Sturges' width is the range over the bin count and is
/// almost never round, so edge ticks read 1.4, 1.9, 2.4. The axis instead carries
/// <c>CustomSeparators</c> at multiples of <see cref="NiceStep"/> in the metric's own domain,
/// mapped into bin-edge space through the bins' one shared width, and the labeler maps a position
/// back. The bars and the median line are untouched by this: only where the ticks stand changed.
/// </para>
/// <para>
/// Edge-to-edge bars are the library's own documented pairing, <c>MaxBarWidth</c> of
/// <c>double.MaxValue</c> with <c>Padding</c> of zero, over a NUMERIC axis rather than a category
/// one: a column on a category axis is centred on its tick, so the lower-edge label could only
/// ever sit at the bar's middle there. <c>MinLimit</c> and <c>MaxLimit</c> pin the numeric axis
/// to exactly the bins so the first bar starts at the axis's origin and the last one ends at its
/// end.
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

    // Roughly how many ticks the value axis aims for. NiceStep rounds the step up, so the drawn
    // count is this or fewer.
    private const int TargetTicks = 8;

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
    /// The smallest round step, 1, 2, 2.5 or 5 times a power of ten, that covers
    /// <paramref name="range"/> in at most <paramref name="targetTicks"/> intervals. Zero for an
    /// empty or non-finite range, which has no ticks to space.
    /// </summary>
    internal static double NiceStep(double range, int targetTicks)
    {
        if (!(range > 0d) || !double.IsFinite(range) || targetTicks < 1)
        {
            return 0d;
        }

        var raw = range / targetTicks;
        var magnitude = Math.Pow(10d, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;

        // A small tolerance, so a raw step that is already round (2.0000000001 after the division)
        // is not pushed up to the next one.
        foreach (var nice in (double[])[1d, 2d, 2.5d, 5d])
        {
            if (normalized <= nice * (1d + 1e-9d))
            {
                return nice * magnitude;
            }
        }

        return 10d * magnitude;
    }

    /// <summary>How many decimals a multiple of <paramref name="step"/> needs to print exactly:
    /// none for 1 or 5, one for 0.5, two for 0.25.</summary>
    internal static int DecimalsFor(double step)
    {
        var decimals = 0;
        while (decimals < 6)
        {
            var scaled = step * Math.Pow(10d, decimals);
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9d * Math.Max(1d, scaled))
            {
                break;
            }

            decimals++;
        }

        return decimals;
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

        XAxes = [ValueAxis(_bins, label)];

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

    /// <summary>
    /// The bin axis: bin-edge space for the bars and the median, ticked at round values of the
    /// metric. Sturges' bins share one width, so value <c>v</c> stands at
    /// <c>(v - start) / width</c> and a tick position maps back the same way.
    /// </summary>
    internal static Axis ValueAxis(IReadOnlyList<HistogramBin> bins, string name)
    {
        var start = bins[0].BinStart;
        var end = bins[^1].BinEnd;
        var axis = new Axis { Name = name, MinLimit = 0, MaxLimit = bins.Count };

        // A metric that is constant over the filtered frames has no range: one tick under the
        // one bar's centre, naming the value.
        if (!(end > start))
        {
            var only = start.ToString("F1", CultureInfo.InvariantCulture);
            axis.CustomSeparators = [bins.Count / 2d];
            axis.Labeler = _ => only;
            return axis;
        }

        var width = (end - start) / bins.Count;
        var step = NiceStep(end - start, TargetTicks);
        var decimals = DecimalsFor(step);
        var format = "F" + decimals.ToString(CultureInfo.InvariantCulture);

        // Multiples of the step inside the bins' own span. The index arithmetic, rather than an
        // accumulating sum, keeps each tick an exact multiple however many there are.
        var first = (long)Math.Ceiling((start / step) - 1e-9d);
        var last = (long)Math.Floor((end / step) + 1e-9d);
        var separators = new List<double>();
        for (var k = first; k <= last; k++)
        {
            separators.Add(((k * step) - start) / width);
        }

        axis.CustomSeparators = separators;
        // Rounded to the step's own decimals plus zero, so a tick at zero that the mapping lands a
        // hair below it prints "0" and never "-0".
        axis.Labeler = position =>
            (Math.Round(start + (position * width), decimals) + 0d)
                .ToString(format, CultureInfo.InvariantCulture);
        return axis;
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
