using System.ComponentModel;
using System.Globalization;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Dashboard;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// The shape <c>MetricChartView</c> binds to, and the second occurrence of "draw a metric line
/// chart": spec 13's two Target detail charts differ in their axes and their series and share
/// every piece of chrome, so the chrome is built once here rather than copied.
/// </summary>
/// <remarks>
/// A base class rather than an interface: it carries real state (the shared selection, the
/// expanded flag, the built series) and there will never be a second implementation of that state.
/// <para>
/// Every colour comes from <c>ChartTheme.Palette</c>, keyed by <c>ChartMetric.TokenKey</c>, and a
/// per-filter split takes the tint the shared selection already resolved for that filter's pill.
/// Neither chart picks a colour of its own (spec 14.5), and there is no <c>SKColors</c> literal
/// anywhere below.
/// </para>
/// <para>
/// <see cref="Rebuild"/> is synchronous and cheap: a target has tens of sessions and a session has
/// hundreds of frames, and every value is already in memory. No background thread, no debounce,
/// and no database read at all.
/// </para>
/// </remarks>
public abstract partial class MetricChartViewModel : ObservableObject, IDisposable
{
    // FIXER LIST F8: the theme subscription and its unsubscribe as one token. The unsubscribe is
    // the half that matters: ChartTheme.Changed is static, so a handler left behind pins this
    // view-model for the life of the process.
    private readonly IDisposable _themeSubscription;

    private bool _disposed;

    /// <param name="selection">The process-wide metric and filter selection (spec 5.8.3 is one
    /// document). Shared, never constructed here.</param>
    /// <param name="title">The chart's own heading, from spec 13's row name.</param>
    /// <param name="logger">Optional.</param>
    protected MetricChartViewModel(ChartSelectionViewModel selection, string title, ILogger? logger = null)
    {
        Selection = selection;
        Title = title;
        Logger = logger ?? NullLogger.Instance;
        MetricPills = BuildMetricPills(selection);

        // Reads the virtual RigDashOffset from a base constructor, which is legal here because
        // every override is a constant expression over no instance state. A derived chart that
        // wanted a computed offset would have to seed the pills itself instead.
        RigPills = BuildRigPills(selection);

        // Seeded before the concrete chart's constructor calls Rebuild, so a chart that is bound
        // mid-construction renders an empty grid rather than throwing: rc5.4's chart engine
        // refuses an empty axis collection outright (see PublishEmpty).
        Series = [];
        Sections = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];

        selection.Changed += OnSelectionChanged;
        selection.PropertyChanged += OnSelectionPropertyChanged;

        // Spec 13's "re-read on theme change": the paints already built have to be replaced,
        // which for this view-model means rebuilding the series it handed to the control.
        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>Shared with every other chart in the process (spec 5.8.3 is one document).
    /// </summary>
    public ChartSelectionViewModel Selection { get; }

    /// <summary>Spec 13's row name for this chart.</summary>
    public string Title { get; }

    protected ILogger Logger { get; }

    /// <summary>The metric toggles paired with the theme colour their series is drawn in, so the
    /// pill strip tints like the dashboard's Filters section. The toggle is the shared
    /// <see cref="ToggleOptionViewModel"/> out of <see cref="Selection"/>, not a second toggle
    /// type: checking a pill writes <c>graph.enabled_metrics</c> through the one writer.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MetricPill> MetricPills { get; private set; }

    /// <summary>What the <c>CartesianChart</c> binds to. Replaced wholesale on a rebuild rather
    /// than mutated, so LiveCharts sees one change instead of N.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> XAxes { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> YAxes { get; private set; }

    /// <summary>The per-session chart's median reference line. Empty for the cross-session chart,
    /// which spec 13 gives no section.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RectangularSection> Sections { get; protected set; }

    /// <summary>The plot's edges when a lane fixes them; null lets the chart engine decide.
    /// </summary>
    [ObservableProperty]
    public partial Margin? DrawMargin { get; protected set; }

    /// <summary>The first entry of <see cref="ChartMetrics.All"/> that is enabled and
    /// has at least one value in this chart's data. It is what the left axis is scaled for, and
    /// what the per-session chart draws its median line at. Null when there is nothing to plot.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowsEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowsOverlaid))]
    [NotifyPropertyChangedFor(nameof(ShowsLanes))]
    public partial ChartMetric? PrimaryMetric { get; private set; }

    /// <summary>The laned form, set by the layout through the part and never stored: one lane
    /// per toggled metric instead of one overlaid plot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOverlaid))]
    [NotifyPropertyChangedFor(nameof(ShowsLanes))]
    public partial bool IsLaned { get; set; }

    partial void OnIsLanedChanged(bool value) => Rebuild();

    /// <summary>The laned form's lanes, top to bottom. Empty in the overlaid form.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartLane> Lanes { get; protected set; } = [];

    /// <summary>False for a chart that stays overlaid whatever <see cref="IsLaned"/> holds.</summary>
    protected virtual bool DrawsLanes => true;

    public bool ShowsOverlaid => !IsEmpty && !(IsLaned && DrawsLanes) && !ShowsGuiding;

    public bool ShowsLanes => !IsEmpty && IsLaned && DrawsLanes && !ShowsGuiding;

    /// <summary>True when there is nothing to plot: no metric selected, no data at all, or no row
    /// carrying a value for any selected metric. The view renders a one-line message instead of an
    /// empty grid.</summary>
    public bool IsEmpty => PrimaryMetric is null;

    /// <summary>Whether the empty message is drawn: the guide trace is a plot of its own.</summary>
    public bool ShowsEmpty => IsEmpty && !ShowsGuiding;

    /// <summary>Phase 24 R3's Guiding pill, present on the night chart only. The members below
    /// live on the base for <see cref="HasSessionScope"/>'s reason: one control binds both
    /// charts, and the trend chart answers false and null.</summary>
    public virtual bool HasGuidingPill => false;

    /// <summary>True when the night has a guide log to draw; the pill is disabled otherwise.</summary>
    public virtual bool IsGuidingAvailable => false;

    /// <summary>The pill's state, stored with the metric and filter choices
    /// (<c>graph.show_guiding</c>).</summary>
    public bool ShowGuiding
    {
        get => Selection.ShowGuiding;
        set => Selection.ShowGuiding = value;
    }

    /// <summary>True while the plot draws the guide trace instead of the metric dots: the pill is
    /// on and the night has a guide log. The metric pills keep their set meanwhile.</summary>
    public bool ShowsGuiding => ShowGuiding && IsGuidingAvailable;

    /// <summary>The night's Guiding section: the figures, the legend and the graph. Null off the
    /// night chart and on a night with no guide log.</summary>
    public virtual GuidingSectionViewModel? Guiding => null;

    /// <summary>The night's timeline model, which marks the selected frame on the guide trace.</summary>
    public virtual NightStripViewModel? Strip => null;

    /// <summary>The night's shared time axis; null off it.</summary>
    public virtual NightLaneAxis? LaneAxis => null;

    /// <summary>Raises every projection of <see cref="ShowsGuiding"/>.</summary>
    protected void RaiseGuidingChanged()
    {
        OnPropertyChanged(nameof(ShowGuiding));
        OnPropertyChanged(nameof(IsGuidingAvailable));
        OnPropertyChanged(nameof(ShowsGuiding));
        OnPropertyChanged(nameof(ShowsOverlaid));
        OnPropertyChanged(nameof(ShowsLanes));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnGuidingChanged();
    }

    /// <summary>What a chart adds to <see cref="RaiseGuidingChanged"/>; nothing by default.</summary>
    protected virtual void OnGuidingChanged()
    {
    }

    /// <summary>Whether this chart has a plotted-session scope to widen
    /// (<c>default_chart_sessions</c>). Only the cross-session chart does; the three members here
    /// live on the base because one control binds both charts, so the per-session chart answers
    /// false and ignores the setter rather than each chart getting a view of its own.</summary>
    public virtual bool HasSessionScope => false;

    /// <summary>The unpersisted "All sessions" affordance. Ignored by a chart that has no
    /// session scope.</summary>
    public virtual bool ShowAllSessions
    {
        get => false;
        set { }
    }

    /// <summary>How many sessions of how many are plotted, for the scope toggle's label.</summary>
    public virtual string SessionScopeText => "";

    /// <summary>Rebuilds the series, the axes and the sections from the current data and
    /// selection. Called by the concrete chart on construction, on every
    /// <c>Selection.Changed</c>, on a theme change, and when the data is replaced. Pure with
    /// respect to the database: it reads nothing.</summary>
    protected abstract void Rebuild();

    /// <summary>Publishes one rebuild's output in one place, so neither chart can forget the
    /// primary metric or leave a stale section behind.</summary>
    protected void Publish(
        IReadOnlyList<ISeries> series,
        IReadOnlyList<ICartesianAxis> xAxes,
        AxisPlan plan,
        IReadOnlyList<RectangularSection>? sections = null)
    {
        PrimaryMetric = plan.Primary;
        Series = series;
        XAxes = xAxes;
        YAxes = plan.YAxes;
        Sections = sections ?? [];
    }

    /// <summary>
    /// Publishes the empty state: no series, and one bare axis on each side rather than none.
    /// </summary>
    /// <remarks>
    /// rc5.4's <c>CartesianChartEngine.Measure</c> throws "XAxes and YAxes must contain at least
    /// one element" on an empty axis collection, and the control measures even while it is
    /// collapsed, so handing it nothing at all takes the page down instead of rendering the
    /// one-line empty message. The axes are fresh instances per call because an axis carries
    /// measure state and two charts must not share one.
    /// </remarks>
    protected void PublishEmpty() => Publish([], [new Axis()], AxisPlan.Nothing());

    /// <summary>Publishes the laned form: one lane per metric in <paramref name="metrics"/>, top to
    /// bottom, each with <paramref name="seriesOf"/>'s series on its own number axis and the x axis
    /// <paramref name="xAxisOf"/> builds (true for the bottom lane). No metric publishes empty.</summary>
    protected void PublishLanes(
        IReadOnlyList<ChartMetric> metrics,
        Func<ChartMetric, IReadOnlyList<ISeries>> seriesOf,
        Func<bool, ICartesianAxis> xAxisOf,
        double plotLeft,
        double plotRight,
        bool labelsBottom)
    {
        if (metrics.Count == 0)
        {
            Lanes = [];
            PublishEmpty();
            return;
        }

        List<ChartLane> lanes = [];
        foreach (var metric in metrics)
        {
            var bottom = ReferenceEquals(metric, metrics[^1]);
            lanes.Add(new ChartLane(
                LaneTitle(metric),
                seriesOf(metric),
                [xAxisOf(bottom)],
                [AxisPlan.NumberAxis(metric)],
                new Margin((float)plotLeft, LaneMarginTop, (float)plotRight, bottom && labelsBottom ? LaneMarginBottomLabelled : LaneMarginBottom)));
        }

        Publish([], [new Axis()], AxisPlan.For(metrics, _ => true));
        Lanes = lanes;
    }

    // A lane's ticks carry the number only, so the unit moves into its title.
    private static string LaneTitle(ChartMetric metric)
        => metric.Unit.Length == 0 || metric.Label.EndsWith(')')
            ? metric.Label
            : string.Create(CultureInfo.InvariantCulture, $"{metric.Label} ({metric.Unit.Trim()})");

    // Room above the plot for the lane's title, clear of the top tick label's upper half.
    private const float LaneMarginTop = 30f;
    private const float LaneMarginBottom = 10f;
    private const float LaneMarginBottomLabelled = 28f;

    /// <summary>What the overlaid plot's <c>DataPointerDownCommand</c> is bound to: the pressed
    /// point's own x, never pointer pixels.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void PointPressed(IEnumerable<LiveChartsCore.Kernel.ChartPoint>? points)
    {
        if (points?.FirstOrDefault() is { } point)
        {
            PressedAt(point.Coordinate.SecondaryValue);
        }
    }

    /// <summary>A press on a plotted point at <paramref name="x"/>; nothing by default.</summary>
    protected virtual void PressedAt(double x)
    {
    }

    /// <summary>
    /// The metric colour spec 14.5 fixes for this metric, from the theme token
    /// <see cref="ChartMetric.TokenKey"/> names. The one lookup either chart performs.
    /// </summary>
    protected static SKColor MetricColour(ChartMetric metric)
        => ChartTheme.Palette.Metrics[metric.TokenKey];

    /// <summary>
    /// One line of spec 13's "multi-series line, points on, gaps preserved". The null-splitting
    /// flag is set explicitly, with the requirement named, because it is the whole of spec 13's
    /// "gaps preserved for missing sessions": a later change of the library default must not
    /// silently join a line across a night that carried no value.
    /// </summary>
    /// <param name="splitOnNull">
    /// Whether a null value breaks the line. True everywhere except a per-rig series on the
    /// per-night chart (verification blocker B1), where the nulls are not missing measurements but
    /// the other rig's frames: see <c>SessionChartViewModel.Rebuild</c>.
    /// </param>
    protected static LineSeries<double?> Line(
        string name,
        IReadOnlyList<double?> values,
        SKColor colour,
        int scalesYAt,
        double[]? dash = null,
        bool splitOnNull = true)
        => new()
        {
            Name = name,
            Values = values,
            // A fresh paint per role: LiveCharts paints carry per-zone animation state, so two
            // series sharing one instance is a rendering defect waiting to happen.
            //
            // Spec 13's rig mark is the dash and only the dash: the colour still comes from the
            // metric, and a null or empty array is a solid line rather than a dash array that
            // happens to look solid. The geometry paints stay solid whatever the stroke does,
            // because a dashed point marker is a rendering artefact and not a distinction.
            Stroke = dash is { Length: > 0 }
                ? new SolidColorPaint(colour)
                {
                    StrokeThickness = 2,
                    PathEffect = new DashEffect([.. dash.Select(entry => (float)entry)]),
                }
                : new SolidColorPaint(colour) { StrokeThickness = 2 },
            // These are lines, not areas (spec 13).
            Fill = null,
            // Spec 13: "points on".
            GeometrySize = 6,
            GeometryStroke = new SolidColorPaint(colour) { StrokeThickness = 2 },
            GeometryFill = new SolidColorPaint(colour),
            LineSmoothness = 0,
            // Spec 13's "gaps preserved for missing sessions", and the roadmap row 7 Verify line.
            // A null value breaks the line here instead of joining its two neighbours. Off only
            // where a null does not mean a missing measurement; the one caller that turns it off
            // says why.
            EnableNullSplitting = splitOnNull,
            ScalesYAt = scalesYAt,
        };

    /// <summary>The filter tint the shared selection already resolved for this canonical filter,
    /// as a chart colour. Falls back to the documented neutral grey for a filter with no
    /// configured colour, which is what <c>AliasMap.FilterColor</c> hands back anyway (spec 14.5).
    /// </summary>
    protected SKColor FilterColour(string canonicalFilter)
    {
        var pill = Selection.Filters.FirstOrDefault(
            entry => string.Equals(entry.Key, canonicalFilter, StringComparison.OrdinalIgnoreCase));

        return pill?.Tint is { } tint ? ChartTheme.ToSkColor(tint.Color) : ChartTheme.Fallback;
    }

    /// <summary>
    /// Spec 13's fixed dash table (PAR-004), taken by rig index and repeating from the top when a
    /// target has more than five rigs. Rig 0 is solid, which is an empty array rather than a dash
    /// pattern that happens to look solid, and is why a single-rig target and a single-rig night
    /// draw exactly what they drew before this phase.
    /// </summary>
    /// <remarks>
    /// The web's own <c>RIG_DASH_PATTERNS</c> in <c>TargetMetricsChart.tsx</c> has six entries and
    /// its index 4 is <c>[1, 2]</c> rather than <c>1, 3</c>; the sixth is <c>[10, 4, 2, 4]</c>. The
    /// spec's five-row table is the contract here and the difference is recorded rather than
    /// reconciled (questions.md Q9): the divergence at index 4 is one pixel of gap, and five rigs
    /// on one target is already an unusual library.
    /// </remarks>
    public static double[] RigDash(int rigIndex)
    {
        var index = ((rigIndex % RigDashTable.Length) + RigDashTable.Length) % RigDashTable.Length;
        return [.. RigDashTable[index]];
    }

    // Copied out by RigDash so a caller cannot mutate the table through the array it is handed.
    private static readonly double[][] RigDashTable =
    [
        [],
        [6, 3],
        [2, 2],
        [8, 3, 2, 3],
        [1, 3],
    ];

    /// <summary>Spec 13's rig pills, one per offered rig, each carrying its dash pattern as the
    /// short drawn line the markup renders instead of the 7 pixel dot a metric pill carries. Empty
    /// until something offers a rig list, and the pill row is hidden on a single-rig target.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRigPills))]
    public partial IReadOnlyList<RigPill> RigPills { get; private set; } = [];

    /// <summary>True when this chart offers more than one rig, which is when its pill row shows.</summary>
    public bool HasRigPills => RigPills.Count > 1;

    /// <summary>Whether this chart offers the shared list's rig as a pill. The trend chart offers
    /// every rig of every loaded night; the per-frame chart narrows to its own night.</summary>
    protected virtual bool OffersRig(string rigLabel) => true;

    /// <summary>Rebuilds the pill row for a chart whose <see cref="OffersRig"/> reads state set
    /// after the base constructor ran.</summary>
    protected void RefreshRigPills() => RigPills = BuildRigPills(Selection);

    /// <summary>
    /// What a chart adds to a rig's position before reading <see cref="RigDash"/>. Zero here, so
    /// rig 0 is solid, which is spec 13's table as written and what the per-night chart draws. The
    /// cross-session chart overrides it to 1, because it keeps an unsplit whole-night series and
    /// that line is the solid one (phase review P2-1, ruled option 1). It is read by the series
    /// and by the pill row alike, so the legend cannot disagree with the lines it labels.
    /// </summary>
    protected virtual int RigDashOffset => 0;

    /// <summary>
    /// The dash index for one rig label: its position in the offered rig list when it has one, and
    /// the caller's own fallback otherwise. Spec 13's rule is that the index comes from the full
    /// rig list rather than from the enabled subset, so unchecking rig 1 does not re-pattern rig 2,
    /// and reading it off the offered list is what keeps a rig's pattern the same on both charts.
    /// </summary>
    protected int RigDashIndex(string rigLabel, int fallback)
    {
        var offered = Selection.Rigs;
        for (var index = 0; index < offered.Count; index++)
        {
            if (string.Equals(offered[index].Key, rigLabel, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return fallback;
    }

    /// <summary>The canonical filters this chart may split by: every enabled filter except the
    /// <c>overall</c> sentinel, which is the unsplit series.</summary>
    protected IReadOnlyList<string> SplitFilters()
        => [.. Selection.EnabledFilters.Where(key => !string.Equals(
            key, ChartSelectionViewModel.OverallFilterKey, StringComparison.OrdinalIgnoreCase))];

    /// <summary>True when the <c>overall</c> sentinel is on, which is the one series over every
    /// row coloured by the metric token (spec 13).</summary>
    protected bool IncludeOverall()
        => Selection.EnabledFilters.Contains(
            ChartSelectionViewModel.OverallFilterKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cancels this chart's subscriptions. The shared selection and the static theme outlive every
    /// chart, so a chart that does not unsubscribe keeps rebuilding after its page has closed.
    /// </summary>
    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Selection.Changed -= OnSelectionChanged;
        Selection.PropertyChanged -= OnSelectionPropertyChanged;
        _themeSubscription.Dispose();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => Rebuild();

    private void OnSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A rig list offered while this chart is alive changes both the pill row and the series,
        // and OfferRigs raises no Changed of its own: offering a list is not a selection change.
        if (e.PropertyName == nameof(ChartSelectionViewModel.Rigs))
        {
            RigPills = BuildRigPills(Selection);
            Rebuild();
        }

        if (e.PropertyName == nameof(ChartSelectionViewModel.ShowGuiding))
        {
            RaiseGuidingChanged();
        }
    }

    private void OnThemeChanged()
    {
        MetricPills = BuildMetricPills(Selection);
        Rebuild();
    }

    private IReadOnlyList<RigPill> BuildRigPills(ChartSelectionViewModel selection)
        => [.. selection.Rigs
            .Select((toggle, index) => (toggle, index))
            .Where(entry => OffersRig(entry.toggle.Key))
            .Select(entry => new RigPill(
                entry.toggle,
                new AvaloniaList<double>(RigDash(entry.index + RigDashOffset))))];

    private static IReadOnlyList<MetricPill> BuildMetricPills(ChartSelectionViewModel selection)
        => [.. selection.Metrics.Select(toggle => new MetricPill(
            toggle,
            PillBrush(MetricColour(ChartMetrics.ByKey(toggle.Key)!))))];

    // The reverse of ChartTheme.ToSkColor, and the only place it is needed: a pill is Avalonia
    // chrome while the palette holds what LiveCharts paints with. Alpha is carried through.
    //
    // Immutable, and not Avalonia's SolidColorBrush, for the same reason Phase 5's
    // TargetRowViewModel.ParseTint is: SolidColorBrush is an AvaloniaObject whose constructor
    // calls Dispatcher.VerifyAccess, so building one off the UI thread throws "Call from invalid
    // thread". A chart is constructed on the UI thread in production (the card publishes through
    // its post seam), but a view-model that cannot be constructed anywhere else is a landmine, and
    // an immutable brush has no dispatcher affinity at all.
    private static ISolidColorBrush PillBrush(SKColor colour)
        => new ImmutableSolidColorBrush(Color.FromArgb(colour.Alpha, colour.Red, colour.Green, colour.Blue));

    /// <summary>
    /// The one implementation of spec 13's "Left: primary metric value. Right: secondary metric
    /// when two units are shown", for both charts. The primary metric and every enabled metric
    /// sharing its <see cref="ChartMetric.Unit"/> scale against the left axis; enabled metrics of
    /// any other unit scale against the right; a chart showing one unit has no right axis at all.
    /// </summary>
    protected sealed record AxisPlan(
        ChartMetric? Primary, string LeftUnit, IReadOnlyList<ICartesianAxis> YAxes)
    {
        /// <summary>Nothing to plot: no primary metric, and one bare Y axis because rc5.4's
        /// chart engine refuses an empty axis collection (see <see cref="PublishEmpty"/>).
        /// </summary>
        public static AxisPlan Nothing() => new(null, "", [new Axis()]);

        /// <summary>Which axis index a series for this metric scales against. Index 0 is the left
        /// axis whenever there is only one, which is the defect this method exists to prevent: a
        /// series pinned to index 1 with no right axis present renders against nothing.</summary>
        public int ScalesYAt(ChartMetric metric)
            => YAxes.Count > 1 && !string.Equals(metric.Unit, LeftUnit, StringComparison.Ordinal)
                ? 1
                : 0;

        /// <param name="enabled">The enabled metrics, in <see cref="ChartMetrics.All"/> order.
        /// </param>
        /// <param name="hasData">Whether this chart's data carries any value for a metric. The
        /// primary metric and the second unit are both decided from the metrics actually plotted,
        /// so an enabled metric that nothing measured neither claims the left axis nor conjures a
        /// right one.</param>
        /// <param name="fixedInsets">The plot's left and right edges are fixed by the night's lane
        /// axis, so a label keeps its unit only where the inset holds it: never on the right, and
        /// on the left only a unit as short as " px".</param>
        public static AxisPlan For(
            IReadOnlyList<ChartMetric> enabled,
            Func<ChartMetric, bool> hasData,
            bool fixedInsets = false)
        {
            var plotted = enabled.Where(hasData).ToList();
            if (plotted.Count == 0)
            {
                return Nothing();
            }

            var primary = plotted[0];

            // Everything that is not the primary metric's unit shares the right axis, so the
            // right axis can carry more than one unit and its ticks then belong to no single one.
            // Review finding 2: the suffix is dropped in that case rather than labelling every
            // tick with the second unit's name. The left axis carries exactly one unit by
            // construction, so it always keeps its suffix.
            var rightUnits = plotted
                .Where(metric => !string.Equals(metric.Unit, primary.Unit, StringComparison.Ordinal))
                .Select(metric => metric.Unit)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Unit length stands in for the measured label width, which the label case guards.
            var leftSuffix = !fixedInsets || primary.Unit.Length <= FixedInsetUnitLength;
            List<ICartesianAxis> axes = [UnitAxis(primary, AxisPosition.Start, leftSuffix)];
            if (rightUnits.Count > 0)
            {
                var secondary = plotted.First(
                    metric => string.Equals(metric.Unit, rightUnits[0], StringComparison.Ordinal));

                // A count anywhere on the right axis makes every tick a whole number.
                var anyCount = plotted.Any(metric => metric.Decimals == 0
                    && !string.Equals(metric.Unit, primary.Unit, StringComparison.Ordinal));
                var right = UnitAxis(
                    secondary,
                    AxisPosition.End,
                    withSuffix: !fixedInsets && rightUnits.Count == 1,
                    forceWhole: anyCount,
                    floorAtZero: fixedInsets);
                if (fixedInsets)
                {
                    // The fixed right inset is narrow, so the label column gives up its side padding.
                    right.Padding = new LiveChartsCore.Drawing.Padding(FixedInsetLabelPadding, right.Padding.Top, FixedInsetLabelPadding, right.Padding.Bottom);
                }

                axes.Add(right);
            }

            return new AxisPlan(primary, primary.Unit, axes);
        }

        /// <summary>Pins each axis to the values its metrics plotted, so no point lies off the axis
        /// and a small plot still labels the data's own floor and ceiling.</summary>
        public void PinTo(IReadOnlyDictionary<ChartMetric, List<double>> plotted)
        {
            for (var index = 0; index < YAxes.Count; index++)
            {
                var onAxis = plotted.Where(entry => entry.Value.Count > 0 && ScalesYAt(entry.Key) == index).ToList();
                if (onAxis.Count > 0)
                {
                    PinToData(
                        (Axis)YAxes[index],
                        onAxis.Min(entry => entry.Value.Min()),
                        onAxis.Max(entry => entry.Value.Max()),
                        onAxis.Any(entry => entry.Key.Decimals == 0) ? 0 : onAxis.Max(entry => entry.Key.Decimals));
                }
            }
        }

        // A small plot lets the engine pick a tick step wider than the data, so the labels it
        // prints are not the data's; labelling the padded floor, the middle and the padded ceiling always is.
        private static void PinToData(Axis axis, double min, double max, int decimals)
        {
            var pad = max > min ? (max - min) * 0.05d : Math.Max(Math.Abs(max) * 0.1d, Math.Pow(10d, -decimals));
            // Every charted metric is non-negative, so the padded floor stops at zero.
            var floor = Math.Max(0d, min - pad);
            axis.MinLimit = floor;
            axis.MaxLimit = max + pad;
            // The separators are padded values, so the labeler rounds to the metric's own decimals.
            var format = axis.Labeler;
            axis.Labeler = value => format(Math.Round(value, decimals));
            axis.CustomSeparators = new[] { floor, Math.Round((min + max) / 2d, decimals), max + pad };
        }

        private const int FixedInsetUnitLength = 3;
        private const float FixedInsetLabelPadding = 2f;

        /// <summary>A left axis for one metric whose ticks carry the number and no unit.</summary>
        public static Axis NumberAxis(ChartMetric metric) => UnitAxis(metric, AxisPosition.Start, withSuffix: false);

        // No LabelsPaint and no SeparatorsPaint: ChartTheme's global axis rule sets both from the
        // tokens, and setting them here would override the theme (Task 7 handoff).
        private static Axis UnitAxis(
            ChartMetric metric, AxisPosition position, bool withSuffix, bool forceWhole = false, bool floorAtZero = false)
        {
            // The metric's own decimals, and up to four more where a fine step needs them.
            // A whole-number metric is a count: no decimals and no step under one.
            var whole = forceWhole || metric.Decimals == 0;
            var format = whole ? "#,##0" : "#,##0." + new string('0', metric.Decimals) + "####";
            var suffix = withSuffix ? metric.Unit : "";

            return new Axis
            {
                Position = position,
                MinStep = whole ? 1d : 0d,
                // Every charted metric is a non-negative measurement, so the night's right axis never
                // runs below zero.
                MinLimit = floorAtZero ? 0d : null,
                Labeler = value => value.ToString(format, CultureInfo.InvariantCulture) + suffix,
            };
        }
    }
}

/// <summary>One lane of the laned form: one metric's series over its own labelled y axis.
/// Every lane carries the same left and right draw margin, so the plots line up.</summary>
public sealed record ChartLane(
    string Title,
    IReadOnlyList<ISeries> Series,
    IReadOnlyList<ICartesianAxis> XAxes,
    IReadOnlyList<ICartesianAxis> YAxes,
    Margin DrawMargin);

/// <summary>One metric toggle plus the colour its series is drawn in, so the pill strip can tint
/// by the metric token without a converter and without a second toggle type. <see cref="Toggle"/>
/// is the shared instance out of <c>ChartSelectionViewModel.Metrics</c>.</summary>
public sealed record MetricPill(ToggleOptionViewModel Toggle, ISolidColorBrush Tint);

/// <summary>
/// One rig toggle plus the dash pattern its series is drawn with (spec 13). The pill carries that
/// pattern as a short drawn line rather than the 7 pixel dot a metric pill carries, because the
/// colour on this page belongs to the metric and the dash is the rig's only mark.
/// </summary>
/// <param name="Toggle">The shared instance out of <c>ChartSelectionViewModel.Rigs</c>.</param>
/// <param name="StrokeDashArray">Empty for rig 0, which draws a solid line. An
/// <c>AvaloniaList</c> because that is what <c>Shape.StrokeDashArray</c> takes, so the markup binds
/// it with no converter.</param>
public sealed record RigPill(ToggleOptionViewModel Toggle, AvaloniaList<double> StrokeDashArray);
