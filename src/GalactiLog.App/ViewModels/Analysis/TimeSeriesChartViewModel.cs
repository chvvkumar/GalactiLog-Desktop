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
/// Spec 13's "Nightly metric trend": the Time Series tab's chart, one nightly median per point on
/// a date axis, behind them the robust baseline band this view computes, and over them the moving
/// average the query already computed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Five layers in one fixed series order</b> (spec 13): the band, the mask, the baseline
/// median line and the dashed 3 MAD bound, the three tinted point series, and the moving average.
/// The band is an upper area filled at <c>0x40</c> alpha with an opaque area under it painting out
/// everything below the lower bound, because rc5.4 has no series that fills between two curves.
/// The mask is opaque, so the chart's own background cannot be transparent: it is
/// <c>ColorBgElevated</c>, the same token the mask takes, and the markup names it on the control
/// (the second and last exception to spec 13's transparent rule).
/// </para>
/// <para>
/// <b>Three point series and not one with a per-point paint callback</b>: a callback does not
/// survive a series replacement at rc5.4, which <c>StatsBarChartViewModel</c>'s own remarks
/// already record for the Filter usage chart. Each
/// series holds only its own band's nights and the three are always published, so the layer order
/// does not move when a library happens to have no watch night.
/// </para>
/// <para>
/// <b>This view-model computes the band and nothing else.</b> The nightly medians and both moving
/// averages arrive computed (the query sums nightly medians already rounded to 6, so an average
/// taken again here would differ in the sixth decimal), and the band is <c>Core.Metrics.FrameQuality</c>'s own median and median absolute
/// deviation through <see cref="MetricBaseline.Of"/>, which is the pair spec 12.4 grades frames
/// with. There is no second median and no second MAD here.
/// </para>
/// <para>
/// Every colour is a resource key read through <c>ChartTheme.Read</c> on the UI thread, at
/// construction and again on <c>ChartTheme.Changed</c>, and the subscription is held as one token
/// and disposed with this view-model: <c>ChartTheme.Changed</c> is static, so a handler left
/// behind pins this view-model for the life of the process.
/// <c>ChartTheme.Palette</c> will not do, because it carries no <c>ColorMetricWorst</c>, no
/// <c>ColorAccent</c>, no <c>ColorWarning</c> and no <c>ColorError</c>, and <c>ChartPalette</c> is
/// not extended.
/// </para>
/// </remarks>
public sealed partial class TimeSeriesChartViewModel : ObservableObject, IDisposable
{
    /// <summary>The band fill and the ordinary nightly point, spec 14.1's series ink.</summary>
    internal const string PointKey = "ColorMetricStars";

    /// <summary>The mask fill, and the token the markup gives the chart's own
    /// <c>Background</c>. Read by name and never through <c>ChartPalette.TooltipBackground</c>:
    /// that member is the tooltip's token, and a later phase that gave the tooltip its own token
    /// would diverge the mask from the card with no case failing.</summary>
    internal const string MaskKey = "ColorBgElevated";

    /// <summary>The baseline median line and the dashed 3 MAD bound.</summary>
    internal const string BaselineKey = "ColorTextSecondary";

    /// <summary>A nightly point in <see cref="QualityBand.Watch"/>.</summary>
    internal const string WatchKey = "ColorWarning";

    /// <summary>A nightly point in <see cref="QualityBand.Reject"/>.</summary>
    internal const string RejectKey = "ColorError";

    /// <summary>The moving average line. <c>accent</c> and not <c>metric-hfr</c>: in deep-sky
    /// <c>metric-hfr</c> and <c>warning</c> are the same hex, so a watch-band point and the
    /// average line were one colour, and <c>accent</c> is the honest ink for a derived overlay
    /// that is not a measurement.</summary>
    internal const string AverageKey = "ColorAccent";

    /// <summary>What a night with no single resolved target reads in place of a name (spec 12.14
    /// departure 6, pinned <c>TimeSeriesChart.tsx</c> line 165). Owned here: no string of it
    /// exists in Data, because the seam publishes a null name instead.</summary>
    public const string MixedTargetName = "Mixed";

    /// <summary>The time axis' title, pinned <c>TimeSeriesChart.tsx</c> line 145. The value axis
    /// has no constant: its title is the chosen metric's full label.</summary>
    public const string DateAxisName = "Date";

    // The web's own alpha for the band fill, and spec 13's "a fill at 0x40 alpha for a band",
    // which is one of the four encodings that carry in red-light where hue does not.
    private const byte BandAlpha = 0x40;

    // The subscribe and its unsubscribe as one token. The unsubscribe is the half that matters
    // (ChartTheme.cs:117).
    private readonly IDisposable _themeSubscription;

    private IReadOnlyList<TimeSeriesPoint> _points = [];
    private IReadOnlyList<MovingAveragePoint> _average = [];
    private AnalysisMetric _metric = AnalysisMetric.Hfr;
    private SKColor _point;
    private SKColor _mask;
    private SKColor _baselineInk;
    private SKColor _watch;
    private SKColor _reject;
    private SKColor _accent;
    private bool _disposed;

    public TimeSeriesChartViewModel()
    {
        // The tokens are resolved ON THE UI THREAD, at construction and again on
        // ChartTheme.Changed, and never inside Rebuild. Rebuild runs
        // wherever the tab's post seam publishes from, and ChartTheme.Read answers its documented
        // neutral grey rather than throwing when it is called off the UI thread, so a lookup there
        // would paint a whole chart grey with nothing failing. ChartTheme.Palette is not the
        // snapshot to use here: it carries no ColorWarning, no ColorError and no ColorAccent.
        ReadTokens();

        // Seeded before anything can bind, for the reason PublishEmpty documents.
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];

        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>What the <c>CartesianChart</c> binds to, replaced wholesale on a rebuild so
    /// LiveCharts sees one change instead of N.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> XAxes { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> YAxes { get; private set; }

    /// <summary>
    /// What each point series answers when one of ITS points is hovered, in the series order the
    /// three tinted layers are published in: ordinary, watch, reject. The index is the point's
    /// position within that one series, never within the whole result, which is the distinction a
    /// tooltip that read the result's own list would get wrong on every watch and reject point,
    /// silently and on exactly the points a reader hovers.
    /// </summary>
    internal IReadOnlyList<Func<int, string>> PointTooltips { get; private set; } = [];

    /// <summary>Spec 12.14's "Time Series with fewer than 8 nights, or a zero MAD": the points and
    /// any full-window average still draw and the three baseline layers are absent. Exposed so a
    /// case reads the decision rather than counting series.</summary>
    public bool HasBaselineBand { get; private set; }

    /// <summary>Spec 12.14's tooltip, both arms. The name is published only for a night that
    /// resolved exactly one target, so a null name is both the two-target night and the night with
    /// none resolved, and both read <see cref="MixedTargetName"/>: two arms, never three. The
    /// value carries two decimals and the frame count is an integer, which is pinned
    /// <c>TimeSeriesChart.tsx</c> line 165's <c>toFixed(2)</c>.</summary>
    public static string Tooltip(TimeSeriesPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{point.TargetName ?? MixedTargetName}: {point.Value:F2} ({point.FrameCount} frames)");
    }

    /// <summary>One point series' hover text, by the point's position in THAT series' own list of
    /// nights. The one place a drawn point becomes a night, so the series the chart hovers and the
    /// mapping a case reads cannot disagree.</summary>
    internal static Func<int, string> TooltipFor(IReadOnlyList<TimeSeriesPoint> nights)
        => index => Tooltip(nights[index]);

    /// <summary>The two metrics spec 12.14 grades the other way up, which is what mirrors the
    /// dashed bound to <c>median - 3 * MAD</c> and flips the sign of every point's deviation.
    /// </summary>
    public static bool HigherIsBetter(AnalysisMetric metric)
        => metric is AnalysisMetric.DetectedStars or AnalysisMetric.SkyQuality;

    /// <summary>Replaces the chart's data. The inputs are kept so a theme change re-resolves every
    /// token without the tab re-querying, and so the smoothing segment redraws from the held
    /// result.</summary>
    /// <param name="points">The nightly medians, in ascending date order.</param>
    /// <param name="average">The chosen moving average, or empty for Raw and for a window the
    /// library is too short for.</param>
    /// <param name="metric">The chosen metric, for the value axis and the higher-is-better rule.
    /// </param>
    public void Update(
        IReadOnlyList<TimeSeriesPoint> points,
        IReadOnlyList<MovingAveragePoint> average,
        AnalysisMetric metric)
    {
        _points = points;
        _average = average;
        _metric = metric;
        Rebuild();
    }

    /// <summary>Drops the data and publishes the empty state.</summary>
    public void Clear() => Update([], [], _metric);

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

    // Spec 13's "re-read on theme change". Raised on the UI thread by ChartTheme.Apply, which is
    // the other of the two places the tokens may be read.
    private void OnThemeChanged()
    {
        ReadTokens();
        Rebuild();
    }

    private void ReadTokens()
    {
        _point = ChartTheme.Read(PointKey, ChartTheme.Fallback);
        _mask = ChartTheme.Read(MaskKey, ChartTheme.Fallback);
        _baselineInk = ChartTheme.Read(BaselineKey, ChartTheme.Fallback);
        _watch = ChartTheme.Read(WatchKey, ChartTheme.Fallback);
        _reject = ChartTheme.Read(RejectKey, ChartTheme.Fallback);
        _accent = ChartTheme.Read(AverageKey, ChartTheme.Fallback);
    }

    private void Rebuild()
    {
        if (_points.Count == 0)
        {
            PublishEmpty();
            return;
        }

        // One median and one MAD, Core.Metrics'. MadZ answers null below FrameQuality.MinGroup and
        // at a zero MAD, so the tint ladder needs no second gate: every night is Neutral there and
        // takes the ordinary ink, which is the same condition that hides the three band layers.
        var baseline = MetricBaseline.Of(_points.Select(night => (double?)night.Value));
        var higherIsBetter = HigherIsBetter(_metric);
        HasBaselineBand = baseline is { Median: not null, Mad: > 0 } && baseline.N >= FrameQuality.MinGroup;

        var series = new List<ISeries>(7);

        if (HasBaselineBand && baseline is { Median: { } median, Mad: { } deviation })
        {
            // Layers 1 and 2, the band and its mask, in this order: the mask paints out everything
            // below the lower bound and must come after the fill it truncates.
            //
            // The floor and the mask's alpha are both BandAreas', which is where the two rules and
            // the reasoning behind them live: this is the pattern's second occurrence on the page
            // and one copy of each rule is what design lesson 1 asks for. The band's lower bound is
            // one flat line here rather than a curve, so the floor's first argument is that single
            // value; what it guards is the same thing, four of the picker's twenty metrics carrying
            // negative nightly medians on a winter library (Ambient Temp, Dew Point, Focuser Temp,
            // Sensor Temp) and a metric that straddles zero losing its sub-zero half.
            var floor = BandAreas.Floor(
                [median - deviation], _points.Select(night => night.Value));

            series.Add(Area("Baseline band", median + deviation, _point.WithAlpha(BandAlpha), floor));
            series.Add(Area("Baseline mask", median - deviation, BandAreas.OpaqueMask(_mask), floor));

            // Layer 3, the baseline median line and the dashed 3 MAD bound, both in text-secondary.
            // The bound is mirrored for the two higher-is-better metrics, because for those the bad
            // side is below the median (spec 12.14).
            series.Add(Rule("Baseline median", median, _baselineInk, dashed: false));
            series.Add(Rule(
                "3 MAD bound",
                higherIsBetter
                    ? median - (FrameQuality.ZReject * deviation)
                    : median + (FrameQuality.ZReject * deviation),
                _baselineInk,
                dashed: true));
        }

        // Layer 4, the nightly medians, three series and never one with a per-point callback.
        List<TimeSeriesPoint> ordinary = [];
        List<TimeSeriesPoint> watched = [];
        List<TimeSeriesPoint> rejected = [];
        foreach (var night in _points)
        {
            var bucket = FrameQuality.BandForZ(FrameQuality.MadZ(night.Value, baseline, higherIsBetter)) switch
            {
                QualityBand.Watch => watched,
                QualityBand.Reject => rejected,
                _ => ordinary,
            };

            bucket.Add(night);
        }

        // One mapping per point series, built once and used BOTH as that series' tooltip formatter
        // and as what PointTooltips publishes, so a case cannot pass over a mapping the chart does
        // not actually hover with.
        var tooltips = new List<Func<int, string>>(3);
        foreach (var (bucket, ink) in new[] { (ordinary, _point), (watched, _watch), (rejected, _reject) })
        {
            var tooltip = TooltipFor(bucket);
            tooltips.Add(tooltip);
            series.Add(Nights(bucket, ink, tooltip));
        }

        PointTooltips = tooltips;

        // Layer 5, the moving average, present only while its segment is selected and the library
        // is long enough for one full window. Plotted verbatim: no window, no sum and no window
        // length exists in this file.
        if (_average.Count > 0)
        {
            series.Add(new LineSeries<DateTimePoint>
            {
                Name = "Moving average",
                Values = [.. _average.Select(entry => new DateTimePoint(AsDateTime(entry.Date), entry.Value))],
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(_accent) { StrokeThickness = 2 },
            });
        }

        Series = series;

        // Both axes are titled, as the pinned TimeSeriesChart.tsx titles them and as the
        // Correlation, Distributions histogram and box plot charts title theirs: Name alone, with
        // the paint left to ChartTheme's own appended axis rule. The value axis takes the metric's FULL
        // label, whose parenthesis already carries the unit, so no tick spells a unit of its own
        // and the unit is still read from the one label table. Nothing here is a resource lookup,
        // so the title follows the picker and a theme swap from wherever Rebuild runs.
        //
        // No LabelsPaint and no SeparatorsPaint on either axis: ChartTheme's global axis rule sets
        // both from the tokens and setting them here would override the theme.
        XAxes =
        [
            new DateTimeAxis(TimeSpan.FromDays(1), date => date.ToString("MMM d", CultureInfo.InvariantCulture))
            {
                Name = DateAxisName,
            },
        ];

        YAxes = [new Axis { Name = AnalysisMetricLabels.Label(_metric) }];
    }

    // The band and the mask: a flat line over the plotted dates, strokeless, filled, and out of
    // the tooltip. IsHoverable is false on all four baseline layers, which is what the web's own
    // tooltip filter does by name (TimeSeriesChart.tsx line 155).
    /// <param name="pivot">The value the fill closes to, which is the floor shared by the band
    /// and its mask and never the library's default of 0.</param>
    private LineSeries<DateTimePoint> Area(string name, double level, SKColor fill, double pivot)
        => new()
        {
            Name = name,
            Values = Flat(level),
            Fill = new SolidColorPaint(fill),
            Stroke = null,
            GeometrySize = 0,
            LineSmoothness = 0,
            IsHoverable = false,
            Pivot = pivot,
        };

    private LineSeries<DateTimePoint> Rule(string name, double level, SKColor ink, bool dashed)
        => new()
        {
            Name = name,
            Values = Flat(level),
            Fill = null,
            GeometrySize = 0,
            LineSmoothness = 0,
            IsHoverable = false,
            Stroke = dashed
                ? new SolidColorPaint(ink) { StrokeThickness = 1, PathEffect = new DashEffect([4f, 4f]) }
                : new SolidColorPaint(ink) { StrokeThickness = 1.5f },
        };

    // One band's nights, as filled dots 8 across, which is the pinned pointRadius of 4
    // (TimeSeriesChart.tsx line 101): GeometrySize is a DIAMETER, the reading spec 13's "a
    // session-granularity point draws at 6 pixels" takes everywhere else in this phase.
    private static ScatterSeries<DateTimePoint> Nights(
        IReadOnlyList<TimeSeriesPoint> nights, SKColor ink, Func<int, string> tooltip)
        => new()
        {
            Name = "Nightly median",
            Values = [.. nights.Select(night => new DateTimePoint(AsDateTime(night.Date), night.Value))],
            GeometrySize = 8,
            Fill = new SolidColorPaint(ink),
            Stroke = null,
            YToolTipLabelFormatter = drawn => tooltip(drawn.Index),
        };

    private IReadOnlyList<DateTimePoint> Flat(double level)
        => [.. _points.Select(night => new DateTimePoint(AsDateTime(night.Date), level))];

    private static DateTime AsDateTime(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    /// <summary>
    /// The empty state: nothing plotted, and one bare axis on each side rather than none. rc5.4's
    /// <c>CartesianChartEngine.Measure</c> throws "XAxes and YAxes must contain at least one
    /// element" on an empty axis collection and the control measures even while it is collapsed,
    /// so an empty state with no axes takes the page down instead of showing the state sentence
    /// Fresh axis instances per call, because an axis carries measure state.
    /// </summary>
    private void PublishEmpty()
    {
        HasBaselineBand = false;
        PointTooltips = [];
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
    }
}
