using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Theme;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// The median mark's own marker: a flat bar across the box, in the ink
/// <see cref="BoxPlotChartViewModel"/> chose for it.
/// </summary>
/// <remarks>
/// A scatter marker is square, <c>GeometrySize</c> on a side, so none of the library's own
/// geometries is a median mark: a rectangle is a block that would cover the box and a circle is a
/// dot that reads as one more outlier ring. This one keeps the square's width and draws a bar of
/// <see cref="Thickness"/> across its middle, which is the shape a box plot's median has.
/// </remarks>
internal sealed class MedianMarkGeometry : RectangleGeometry
{
    /// <summary>The mark's height in pixels, the box series' own stroke thickness.</summary>
    internal const float Thickness = 2f;

    /// <inheritdoc/>
    public override void Draw(SkiaSharpDrawingContext context)
    {
        // Height is the marker square's, never the mark's: the scatter writes GeometrySize into
        // both sides. The bar is centred in it, which is where the scatter put the median.
        var bar = Math.Min(Thickness, Height);
        context.Canvas.DrawRect(X, Y + ((Height - bar) / 2f), Width, bar, context.ActiveSkiaPaint);
    }
}

/// <summary>
/// Spec 13's "Grouped box plot", the ONE box plot on the Analysis page: the Distributions tab's
/// box plot half draws N groups through it and the Compare tab draws its exactly two groups
/// through the same type, not a second one (spec 12.14's Compare subsection, ruling A7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one <c>BoxSeries</c>, whatever the group count.</b> A second box series offsets both
/// series inside each category slot while the outlier scatter at integer X stays centred, so the
/// rings would float between the boxes (spike trap 9). Vertical, because rc5.4 has no horizontal
/// box series at all: there is no <c>RowBoxSeries</c> and no orientation on the series (ruling
/// A24, spike trap 6).
/// </para>
/// <para>
/// <b>The whiskers and the box outline are one <c>Stroke</c></b>, which is what the one series
/// paints both with, on <c>ColorTextPrimary</c> (ruling A25). The series paints its own median
/// mark with that same stroke, and on a filled box that is not readable: <c>text-primary</c>
/// measures 1.04 against the box fill on <c>red-light</c>, 1.51 on <c>deep-sky</c> and 2.44 on
/// <c>luminance</c>, so on the theme whose whole purpose is a dark-adapted eye the box read
/// as one solid block and the median could only be had from the tooltip.
/// </para>
/// <para>
/// <b>So the median mark is drawn a second time, in an ink chosen by contrast</b> against the box
/// fill, the better of the held <c>ColorTextPrimary</c> and the held <c>ColorBgElevated</c> forced
/// opaque, which is the rule the Matrix cell labels use and which both now take from
/// <see cref="InkContrast"/>. It cannot be done by re-inking the series' stroke: that one paint
/// also draws the whiskers, which stand on the page and not on the box, and every theme here is
/// dark, so the whiskers would have gone where the median mark came from. It is a THIRD series
/// instead, a scatter of <see cref="MedianMarkGeometry"/> at each group's own integer X, drawn
/// last and therefore over the box. Its <c>GeometrySize</c> is a pixel width deliberately under
/// the box series' <c>MaxBarWidth</c>, so the mark sits inside the box it marks at every chart
/// width instead of overhanging it.
/// </para>
/// <para>
/// <b>The caller decides the group order.</b> Nothing is sorted here, because the two callers want
/// different orders: the Distributions tab sorts ordinally by group name (ruling P3-9) while
/// Compare draws group A and then group B.
/// </para>
/// <para>
/// Ruling P1-7: every ink is resolved through <see cref="ChartTheme.Read(string, SKColor)"/> on
/// the UI thread at construction and again on every <c>ChartTheme.Changed</c>, HELD as a field,
/// and only then used by <see cref="Rebuild"/>. Resolving inside the rebuild would be a trap:
/// <c>ChartTheme.Read</c> answers its documented neutral rather than throwing off the UI thread,
/// and a rebuild can run on the query's own thread-pool thread under a post seam that runs its
/// closure inline, so all three layers would paint the same grey and a case that resolved the
/// token on that thread would compare one fallback with another. The subscription is held as one
/// token and disposed with the view-model. <c>ChartTheme.Palette</c> would not do: it carries no
/// <c>ColorMetricWorst</c> and no <c>ColorTextPrimary</c>.
/// </para>
/// </remarks>
public sealed partial class BoxPlotChartViewModel : ObservableObject, IDisposable
{
    /// <summary>The interquartile box's fill.</summary>
    public const string BoxTokenKey = "ColorMetricStars";

    /// <summary>The whiskers and the box outline, which are one stroke, and the first of the two
    /// candidates for the median mark's ink.</summary>
    public const string OutlineTokenKey = "ColorTextPrimary";

    /// <summary>The second candidate for the median mark's ink, forced opaque: <c>deep-sky</c>
    /// declares this token at alpha 0xE6 and a translucent mark would take some of the box's own
    /// fill back and lose the contrast it was chosen for.</summary>
    public const string MedianAltTokenKey = "ColorBgElevated";

    /// <summary>The hollow outlier rings.</summary>
    public const string OutlierTokenKey = "ColorMetricWorst";

    /// <summary>The outlier scatter's series name, so a case can name the series it means rather
    /// than the position it sits at.</summary>
    public const string OutlierSeriesName = "Outliers";

    /// <summary>The median mark scatter's series name, for the same reason.</summary>
    public const string MedianSeriesName = "Median";

    /// <summary>The widest a box is drawn, in pixels. Set rather than left at the library's own
    /// default because <see cref="MedianMarkWidth"/> is chosen against it.</summary>
    public const double BoxWidth = 50d;

    /// <summary>The median mark's width in pixels, under <see cref="BoxWidth"/> by enough that the
    /// mark is inside the box whichever of the two the library's padding takes off.</summary>
    public const double MedianMarkWidth = 40d;

    private readonly IDisposable _themeSubscription;

    // Resolved on the UI thread and held, never resolved inside Rebuild: see the remarks above.
    private SKColor _boxInk;
    private SKColor _outlineInk;
    private SKColor _outlierInk;
    private SKColor _medianInk;

    private IReadOnlyList<BoxPlot> _groups = [];
    private AnalysisMetric _metric = AnalysisMetric.Hfr;
    private bool _disposed;

    /// <summary>Builds the empty state, which is what a tab that has not queried yet draws.</summary>
    public BoxPlotChartViewModel()
    {
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        BoxTooltips = [];
        WhiskerTooltips = [];
        IsEmpty = true;

        ReadInks();
        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> XAxes { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> YAxes { get; private set; }

    /// <summary>One quartile tooltip per group, in the order the caller gave, which the box
    /// series' own Y formatter reads by point index (ruling P3-7). Exposed because a case pins the
    /// string and a <c>ChartPoint</c> cannot be constructed outside a live chart.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> BoxTooltips { get; private set; }

    /// <summary>One whisker-range tooltip per group, read by the same series' X formatter, so the
    /// one tooltip a reader sees carries both of spec 12.14's strings.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> WhiskerTooltips { get; private set; }

    /// <summary>True when there is nothing to plot.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The group names on the category axis, in the order they are drawn. Exposed so a
    /// case can assert the order and the dropped groups without reading an axis.</summary>
    public IReadOnlyList<string> GroupNames => [.. _groups.Select(group => group.GroupName)];

    /// <summary>Draws one set of groups. Every figure is the query's; a group the query dropped at
    /// its four-value gate simply is not here and draws no row and no notice.</summary>
    /// <param name="groups">The boxes, in the order they are to be drawn.</param>
    /// <param name="metric">The chosen metric, for the value axis's name and unit.</param>
    public void Show(IReadOnlyList<BoxPlot> groups, AnalysisMetric metric)
    {
        ArgumentNullException.ThrowIfNull(groups);

        _groups = groups;
        _metric = metric;
        Rebuild();
        OnPropertyChanged(nameof(GroupNames));
    }

    /// <summary>Drops the data and publishes the empty state.</summary>
    public void Clear() => Show([], _metric);

    /// <summary>
    /// The quartile tooltip, pinned <c>BoxPlotChart.tsx</c> line 100 with the en dash the port's
    /// prose rules forbid replaced by the word "to" in its whisker companion.
    /// </summary>
    internal static string BoxTooltip(BoxPlot group) => string.Create(
        CultureInfo.InvariantCulture,
        $"Q1: {group.Q1:F2}, Median: {group.Median:F2}, Q3: {group.Q3:F2} (N={group.Count})");

    /// <summary>
    /// The whisker tooltip, pinned <c>BoxPlotChart.tsx</c> line 103. <c>Min</c> and <c>Max</c> are
    /// the WHISKER ENDS and not the group's extremes, which is what the seam's own summary says
    /// and what the word "Range" names here.
    /// </summary>
    internal static string WhiskerTooltip(BoxPlot group) => string.Create(
        CultureInfo.InvariantCulture,
        $"Range: {group.Min:F2} to {group.Max:F2}");

    private void Rebuild()
    {
        if (_groups.Count == 0)
        {
            PublishEmpty();
            return;
        }

        BoxTooltips = [.. _groups.Select(BoxTooltip)];
        WhiskerTooltips = [.. _groups.Select(WhiskerTooltip)];

        // The rings sit at their own group's integer X, which is where the one box series centres
        // its box (spike trap 9).
        var outliers = new List<ObservablePoint>();
        for (var index = 0; index < _groups.Count; index++)
        {
            foreach (var outlier in _groups[index].Outliers)
            {
                outliers.Add(new ObservablePoint(index, outlier));
            }
        }

        var label = AnalysisMetricLabels.Label(_metric);
        Series =
        [
            new BoxSeries<BoxValue>
            {
                Name = label,

                // BoxValue(max, thirdQuartile, firstQuartile, min, median), in that argument
                // order: the seam's Min and Max are the whisker ends and go where the library
                // calls them min and max.
                Values =
                [
                    .. _groups.Select(group => new BoxValue(
                        group.Max, group.Q3, group.Q1, group.Min, group.Median)),
                ],
                Fill = new SolidColorPaint(_boxInk),
                Stroke = new SolidColorPaint(_outlineInk)
                {
                    StrokeThickness = 2,
                },

                // Pinned rather than left at the library's default, because the median mark's own
                // width is chosen against it.
                MaxBarWidth = BoxWidth,
                YToolTipLabelFormatter = point => At(BoxTooltips, point.Index),
                XToolTipLabelFormatter = point => At(WhiskerTooltips, point.Index),
            },

            // Ruling P3-10: a hollow ring is Fill null plus a Stroke, with CircleGeometry named
            // explicitly because the one-argument form takes the theme's default geometry, which
            // is a circle today and is not a contract. GeometrySize 6 and a 1.5 pixel stroke are
            // the pinned BoxPlotChart.tsx radius 3 and border width 1.5.
            new ScatterSeries<ObservablePoint, CircleGeometry>
            {
                Name = OutlierSeriesName,
                Values = outliers,
                GeometrySize = 6,
                Fill = null,
                Stroke = new SolidColorPaint(_outlierInk)
                {
                    StrokeThickness = 1.5f,
                },
            },

            // The median mark, last so it is drawn over the box, at the same integer X the box and
            // the rings use. It answers no tooltip: the median is already in the box's own
            // quartile line, and a second tooltip over the middle of the box would cover it.
            new ScatterSeries<ObservablePoint, MedianMarkGeometry>
            {
                Name = MedianSeriesName,
                Values =
                [
                    .. _groups.Select((group, index) => new ObservablePoint(index, group.Median)),
                ],
                GeometrySize = MedianMarkWidth,
                Fill = new SolidColorPaint(_medianInk),
                Stroke = null,
                IsHoverable = false,
                IsVisibleAtLegend = false,
            },
        ];

        XAxes =
        [
            new Axis
            {
                Labels = [.. _groups.Select(group => group.GroupName)],
                MinStep = 1,
                ForceStepToMin = true,
            },
        ];

        YAxes = [new Axis { Name = label }];
        IsEmpty = false;
    }

    // On the UI thread, from the constructor and from ChartTheme.Changed, which is raised on it.
    private void ReadInks()
    {
        _boxInk = ChartTheme.Read(BoxTokenKey, ChartTheme.Fallback);
        _outlineInk = ChartTheme.Read(OutlineTokenKey, ChartTheme.Fallback);
        _outlierInk = ChartTheme.Read(OutlierTokenKey, ChartTheme.Fallback);

        // Re-chosen here and nowhere else, so a theme swap re-runs the comparison rather than
        // carrying the previous theme's answer.
        _medianInk = InkContrast.Choose(
            _boxInk,
            _outlineInk,
            ChartTheme.Read(MedianAltTokenKey, ChartTheme.Fallback).WithAlpha(0xFF));
    }

    private void OnThemeChanged()
    {
        ReadInks();
        Rebuild();
    }

    private static string At(IReadOnlyList<string> tooltips, int index)
        => index >= 0 && index < tooltips.Count ? tooltips[index] : string.Empty;

    // One bare axis per side rather than none: rc5.4's CartesianChartEngine.Measure throws "XAxes
    // and YAxes must contain at least one element" and the control measures while it is collapsed,
    // so an empty state with no axes takes the page down. Fresh instances per call, because an
    // axis carries measure state. ChartEmptyAxisCensusTest checks this rule on every chart
    // view-model, not only this one.
    private void PublishEmpty()
    {
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        BoxTooltips = [];
        WhiskerTooltips = [];
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
