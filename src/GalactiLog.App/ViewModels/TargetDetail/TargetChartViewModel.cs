using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// Spec 13's "Cross-session metric trend" on the Target detail page: one point per session date,
/// oldest first, one series per enabled metric, optionally split per filter, with a gap wherever a
/// night carried no value for that metric.
/// </summary>
/// <remarks>
/// It reads the page's own session cards rather than a copy of the overview list, for one reason:
/// spec 12.4 puts the per-filter medians on the <em>expanded</em> session detail, not on the
/// collapsed overview, so a per-filter split can only plot a session whose card has been expanded
/// (ruling Q20). The card is where both halves already live, it already raises
/// <c>PropertyChanged</c> when its detail arrives, and its lifetime is already the page's, so this
/// chart adds no second projection and no second query.
/// </remarks>
public sealed partial class TargetChartViewModel : MetricChartViewModel
{
    private readonly IReadOnlyList<SessionCardViewModel> _sessions;

    // The cards this chart is subscribed to. The page rebuilds its collection by clearing and
    // refilling it, so a set of what is actually hooked up is what keeps the subscriptions and the
    // collection from drifting apart.
    private readonly HashSet<SessionCardViewModel> _tracked = [];

    private bool _showAllSessions;

    // Set for the duration of the page's ReplaceSessions. The page rebuilds its card collection by
    // clearing and refilling it, and refreshing a carried card drops that card's detail, so an
    // unsuspended chart publishes a transient empty series set mid-reload before the closing
    // Reload puts the real one back (review finding 5).
    private bool _suspended;

    /// <param name="sessions">The page's live card collection, newest session first (spec 12.4).
    /// Read only: this chart never adds to it or removes from it.</param>
    /// <param name="selection">The shared metric and filter selection.</param>
    /// <param name="logger">Optional.</param>
    public TargetChartViewModel(
        IReadOnlyList<SessionCardViewModel> sessions,
        ChartSelectionViewModel selection,
        ILogger? logger = null)
        : base(selection, "Cross-session metric trend", logger)
    {
        _sessions = sessions;
        Reload();
    }

    /// <summary>How many of the newest sessions are plotted (ruling Q21's
    /// <c>default_chart_sessions</c>, or all of them once the scope is widened).</summary>
    [ObservableProperty]
    public partial int PlottedSessionCount { get; private set; }

    /// <summary>The plotted nights, oldest first, which is the lanes' band order. Raised only when
    /// the list changes, so the Compare table rebuilds with the nights and not with every rebuild.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<DateOnly> PlottedNights { get; private set; } = [];

    /// <summary>The All or Checked switch: true plots only the nights whose check box is ticked.
    /// Not persisted.</summary>
    [ObservableProperty]
    public partial bool CheckedOnly { get; set; }

    partial void OnCheckedOnlyChanged(bool value) => Rebuild();

    /// <summary>The "Show them" toggle: true plots the nights with no metric too. Not persisted,
    /// for the reason <see cref="CheckedOnly"/> is not: it is a per-visit decision.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyNightsAction))]
    public partial bool ShowEmptyNights { get; set; }

    partial void OnShowEmptyNightsChanged(bool value) => Rebuild();

    /// <summary>The nights under the All or Checked switch that carry none of the five medians,
    /// whether shown or not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyNights), nameof(EmptyNightsText))]
    public partial int EmptyNightCount { get; private set; }

    public bool HasEmptyNights => EmptyNightCount > 0;

    public string EmptyNightsText => EmptyNightCount == 1
        ? "1 night has no metrics."
        : string.Create(CultureInfo.InvariantCulture, $"{EmptyNightCount} nights have no metrics.");

    public string EmptyNightsAction => ShowEmptyNights ? "Hide them" : "Show them";

    [RelayCommand]
    private void ToggleEmptyNights() => ShowEmptyNights = !ShowEmptyNights;

    /// <summary>Every frame of every night, capture ordered, which the laned form plots as
    /// dots. Handed over by the page with its load; in memory, so a rebuild reads nothing.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<NightFramePoint> NightFrames { get; set; } = [];

    // Only the lanes read the frames, so the overlaid form does not rebuild for them.
    partial void OnNightFramesChanged(IReadOnlyList<NightFramePoint> value)
    {
        if (IsLaned)
        {
            Rebuild();
        }
    }

    /// <summary>The scope control appears only when the setting is actually hiding a session.
    /// </summary>
    public override bool HasSessionScope => Pool().Count > PlottedSessionCount || ShowAllSessions;

    /// <summary>Ruling Q21: deliberately not persisted. <c>default_chart_sessions</c> is what the
    /// chart opens with; widening the scope is a per-visit decision, and writing it back would
    /// change the setting's meaning from "on first open" to "last used".</summary>
    public override bool ShowAllSessions
    {
        get => _showAllSessions;
        set
        {
            if (_showAllSessions == value)
            {
                return;
            }

            _showAllSessions = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public override string SessionScopeText => Pool().Count == PlottedSessionCount
        ? string.Create(CultureInfo.InvariantCulture, $"all {PlottedSessionCount} sessions")
        : string.Create(CultureInfo.InvariantCulture, $"newest {PlottedSessionCount} of {Pool().Count} sessions");

    /// <summary>
    /// Suspends rebuilds until <see cref="EndUpdate"/>. The page brackets its whole
    /// <c>ReplaceSessions</c> with the pair, so one reload publishes once rather than once per
    /// card added, refreshed or dropped, and never publishes the empty state it passes through on
    /// the way (review finding 5).
    /// </summary>
    public void BeginUpdate() => _suspended = true;

    /// <summary>Resumes rebuilds and publishes exactly once, through <see cref="Reload"/>.
    /// </summary>
    public void EndUpdate()
    {
        _suspended = false;
        Reload();
    }

    /// <summary>
    /// Re-subscribes to the page's current cards, offers the filters the loaded sessions actually
    /// contain, and rebuilds. Called once at construction and once by the page at the end of every
    /// load, which is why the page brackets <c>ReplaceSessions</c> with
    /// <see cref="BeginUpdate"/> and <see cref="EndUpdate"/>: rebuilding on each step would
    /// publish the empty chart it passes through on the way.
    /// </summary>
    public void Reload()
    {
        Track();

        // Unconditional (FIXER LIST F17): the empty-offer guard lives inside OfferFilters, which
        // is the only place that can enforce it for every caller. It used to be here, so a second
        // caller would have had to remember it.
        Selection.OfferFilters(CanonicalFilters());

        // Spec 13's rig pills, offered from the same place and on the same rule. Only this chart
        // offers them, exactly as only this chart offers the filters: the selection is shared with
        // every session card's chart, and two offers of two different lists would fight.
        Selection.OfferRigs(CanonicalRigs());

        Rebuild();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (var card in _tracked)
        {
            card.PropertyChanged -= OnCardChanged;
        }

        _tracked.Clear();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override void Rebuild()
    {
        if (_suspended)
        {
            return;
        }

        EmptyNightCount = Candidates().Count(card => !HasMetrics(card));
        var plotted = Plotted();
        PlottedSessionCount = plotted.Count;
        var nights = plotted.Select(card => card.SessionDate).ToList();
        if (!nights.SequenceEqual(PlottedNights))
        {
            PlottedNights = nights;
        }

        var enabled = Selection.EnabledMetrics;
        var splits = SplitFilters();
        var overall = IncludeOverall();

        if (IsLaned)
        {
            RebuildLanes(plotted, enabled, splits, overall);
            NotifyScope();
            return;
        }

        Lanes = [];
        var rigs = RigSeries();

        // Spec 13's axis rule, from the metrics this chart actually plots: a metric that is
        // enabled but that no plotted session measured decides nothing.
        var plan = AxisPlan.For(
            enabled,
            metric => rigs.Any(rig =>
                (overall && plotted.Any(card => SessionValue(card, rig.Label, metric) is not null))
                || (rig.SplitsByFilter && splits.Any(filter => plotted.Any(
                    card => FilterMedian(card, filter, metric, rig.Label) is not null)))));

        if (plan.Primary is null)
        {
            PublishEmpty();
            NotifyScope();
            return;
        }

        var series = new List<ISeries>();
        var plottedValues = new Dictionary<ChartMetric, List<double>>();
        foreach (var metric in enabled)
        {
            var scalesYAt = plan.ScalesYAt(metric);
            var own = plottedValues[metric] = [];

            // Spec 13: on a target imaged by more than one rig each metric splits into one series
            // per enabled rig. On a single-rig target the one entry here has a null label and no
            // dash, so the chart draws exactly what it drew before this phase.
            foreach (var rig in rigs)
            {
                if (overall)
                {
                    // One point per session through the table's own accessor, so the null that
                    // means "this night measured nothing" reaches the series intact: not filtered
                    // out, not substituted with zero, which are the two ways a gap gets lost.
                    double?[] whole = [.. plotted.Select(card => SessionValue(card, rig.Label, metric))];
                    own.AddRange(whole.OfType<double>());
                    series.Add(Line(
                        metric.Label + rig.Suffix,
                        whole,
                        MetricColour(metric),
                        scalesYAt,
                        rig.Dash));
                }

                if (!rig.SplitsByFilter)
                {
                    // The whole-night spec beside the per-rig ones on a multi-rig target. It has
                    // no per-filter line to draw: see RigSeriesSpec.SplitsByFilter.
                    continue;
                }

                foreach (var filter in splits)
                {
                    // Ruling Q20: a session whose card has not been expanded has no per-filter
                    // median to plot, so it contributes a null and the gap machinery renders it
                    // correctly.
                    double?[] split = [.. plotted.Select(card => FilterMedian(card, filter, metric, rig.Label))];
                    own.AddRange(split.OfType<double>());
                    series.Add(Line(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{metric.Label} ({filter}){rig.Suffix}"),
                        split,
                        FilterColour(filter),
                        scalesYAt,
                        rig.Dash));
                }
            }
        }

        plan.PinTo(plottedValues);
        var tip = DateTip(DateLabels(plotted));
        foreach (var line in series.OfType<LineSeries<double?>>())
        {
            line.XToolTipLabelFormatter = tip;
        }

        Publish(series, [XAxis(plotted)], plan);
        NotifyScope();
    }

    /// <summary>One lane per toggled metric: each plotted night's frames as dots in its band in
    /// capture order, the night median as a short line on top, and on a multi-rig target each
    /// enabled rig's dashed median line, as the overlaid form draws.</summary>
    private void RebuildLanes(
        List<SessionCardViewModel> plotted, IReadOnlyList<ChartMetric> enabled, IReadOnlyList<string> splits, bool overall)
    {
        var nightIndex = plotted.Select((card, index) => (card.SessionDate, index))
            .ToDictionary(entry => entry.SessionDate, entry => entry.index);

        // Each frame's x: its night's index, spread across the middle of the band in capture order.
        var placed = NightFrames
            .Where(frame => nightIndex.ContainsKey(frame.SessionDate))
            .GroupBy(frame => frame.SessionDate)
            .SelectMany(night =>
            {
                var frames = night.ToList();
                var at = nightIndex[night.Key];
                return frames.Select((frame, k) => (Frame: frame, X: at - BandHalf + (2d * BandHalf * (k + 0.5d) / frames.Count)));
            })
            .ToList();

        // One pass per rebuild: each (night, filter) group's frames, read by every filter median.
        var byNightAndFilter = placed.ToLookup(
            entry => (entry.Frame.SessionDate, entry.Frame.Filter.ToUpperInvariant()));
        var rigs = RigSeries().Where(rig => rig.Label is not null).ToList();

        var metrics = enabled
            .Where(metric => plotted.Any(card => metric.SessionValue(card.Overview) is not null)
                || placed.Any(entry => metric.NightFrameValue(entry.Frame) is not null))
            .ToList();

        PublishLanes(
            metrics,
            metric =>
            {
                List<ISeries> dots = [];
                List<ISeries> medians = [];

                if (overall)
                {
                    dots.Add(Dots(metric.Label, placed, metric, null, MetricColour(metric)));
                    medians.Add(Medians(metric.Label + " median", plotted, card => metric.SessionValue(card.Overview), MetricColour(metric)));
                }

                foreach (var filter in splits)
                {
                    var name = string.Create(CultureInfo.InvariantCulture, $"{metric.Label} ({filter})");
                    dots.Add(Dots(name, placed, metric, filter, FilterColour(filter)));
                    medians.Add(Medians(
                        name + " median",
                        plotted,
                        card => Statistics.Median(byNightAndFilter[(card.SessionDate, filter.ToUpperInvariant())]
                            .Select(entry => metric.NightFrameValue(entry.Frame))),
                        FilterColour(filter)));
                }

                foreach (var rig in rigs)
                {
                    medians.Add(Medians(
                        metric.Label + " median" + rig.Suffix,
                        plotted,
                        card => SessionValue(card, rig.Label, metric),
                        MetricColour(metric),
                        rig.Dash));
                }

                var laneTip = DateTip(DateLabels(plotted));
                foreach (var shown in dots.Concat(medians))
                {
                    switch (shown)
                    {
                        case ScatterSeries<ObservablePoint> scatter:
                            scatter.XToolTipLabelFormatter = laneTip;
                            break;
                        case LineSeries<ObservablePoint> line:
                            line.XToolTipLabelFormatter = laneTip;
                            break;
                    }
                }

                return [.. dots, .. medians];
            },
            bottom => LaneXAxis(plotted, bottom),
            NightLaneAxis.SharedPlotLeft,
            NightLaneAxis.SharedPlotRight,
            labelsBottom: true);
    }

    private static ScatterSeries<ObservablePoint> Dots(
        string name, List<(NightFramePoint Frame, double X)> placed, ChartMetric metric, string? filter, SKColor colour)
        => new()
        {
            Name = name,
            Values = [.. placed
                .Where(entry => filter is null || IsFilter(entry.Frame, filter))
                .Select(entry => (entry.X, Value: metric.NightFrameValue(entry.Frame)))
                .Where(entry => entry.Value is not null)
                .Select(entry => new ObservablePoint(entry.X, entry.Value))],
            GeometrySize = DotSize,
            Fill = new SolidColorPaint(colour),
            Stroke = null,
            YToolTipLabelFormatter = ThreeDecimals,
        };

    // One short level line per night across its band; a null point between nights breaks the line.
    private static LineSeries<ObservablePoint> Medians(
        string name,
        List<SessionCardViewModel> plotted,
        Func<SessionCardViewModel, double?> median,
        SKColor colour,
        double[]? dash = null)
        => new()
        {
            Name = name,
            Values = [.. plotted.SelectMany((card, index) => median(card) is { } value
                ? new[] { new ObservablePoint(index - BandHalf, value), new ObservablePoint(index + BandHalf, value), new ObservablePoint(index + 0.5d, null) }
                : [new ObservablePoint(index, null)])],
            Stroke = new SolidColorPaint(colour)
            {
                StrokeThickness = 2,
                PathEffect = dash is { Length: > 0 } ? new DashEffect([.. dash.Select(entry => (float)entry)]) : null,
            },
            Fill = null,
            GeometrySize = 0,
            LineSmoothness = 0,
            EnableNullSplitting = true,
        };

    // The nights as bands centred on their index; the date labels on the bottom lane only.
    private static ICartesianAxis LaneXAxis(List<SessionCardViewModel> plotted, bool labelled)
    {
        var labels = DateLabels(plotted);

        return new Axis
        {
            MinLimit = -0.5d,
            MaxLimit = plotted.Count - 0.5d,
            CustomSeparators = [.. Enumerable.Range(0, plotted.Count).Select(index => (double)index)],
            Labeler = DateLabeler(labels, labelled),
        };
    }

    private static bool IsFilter(NightFramePoint frame, string filter)
        => string.Equals(frame.Filter, filter, StringComparison.OrdinalIgnoreCase);

    private const double BandHalf = 0.4d;
    private const double DotSize = 4d;

    /// <summary>
    /// Spec 13's rig split for this target. On a single-rig target it is exactly one label-free,
    /// dash-free entry, so the chart draws what it drew before this phase. On a multi-rig target
    /// it is that same whole-night entry <em>and</em> one entry per enabled rig beside it. The
    /// dash index is the rig's position in the full offered list, not in the enabled subset, so
    /// unchecking one rig does not re-pattern another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase review P2-1, ruled option 1. The whole-night entry is kept rather than replaced. Its
    /// source is <see cref="SessionCardViewModel.Overview"/>, which every ledger row carries
    /// whether or not the reader has opened that night, while a per-rig figure is taken over the
    /// loaded night's own frames and an unopened night has none. Replacing it turned a season
    /// trend into one marker per opened night the moment a second rig was offered, and left
    /// <c>AxisPlan.For</c> with no value to scale on at all during a reload, when
    /// <c>SessionCardViewModel.Invalidate</c> has nulled every <c>Detail</c> and
    /// <c>OfferRigs</c> is still holding the two pills.
    /// </para>
    /// <para>
    /// Because the whole-night line is the solid one here, the rig entries take rows 1 to 5 of
    /// spec 13's dash table, rig index plus one and repeating past five, which is what
    /// <see cref="RigDashOffset"/> carries into both the series and the pill row. The per-night
    /// chart has every rig loaded by construction, needs no whole-night line, and keeps the table
    /// as written with rig 0 solid.
    /// </para>
    /// </remarks>
    private IReadOnlyList<RigSeriesSpec> RigSeries()
    {
        if (!Selection.HasRigPills)
        {
            return [WholeNight];
        }

        List<RigSeriesSpec> specs = [WholeNightUnfiltered];
        for (var index = 0; index < Selection.Rigs.Count; index++)
        {
            var label = Selection.Rigs[index].Key;
            if (Selection.IsRigEnabled(label))
            {
                specs.Add(new RigSeriesSpec(label, $" [{label}]", RigDash(index + RigDashOffset)));
            }
        }

        return specs;
    }

    /// <summary>The unsplit series a single-rig target draws: one point per night from the
    /// overview, no label suffix and no dash, and the per-filter split as well, because on a
    /// single-rig night the query's <c>FilterMedians</c> rows are the night's own.</summary>
    private static readonly RigSeriesSpec WholeNight = new(null, "", null);

    /// <summary>The same series beside the per-rig ones on a multi-rig target, contributing the
    /// unfiltered line alone.</summary>
    private static readonly RigSeriesSpec WholeNightUnfiltered = new(null, "", null, SplitsByFilter: false);

    /// <inheritdoc />
    protected override int RigDashOffset => 1;

    /// <summary>
    /// One night's value for a metric, for the whole night or for one rig of it.
    /// </summary>
    /// <remarks>
    /// The whole-night figure is the overview's own, the same number the ledger row shows. A
    /// per-rig figure has no such precomputed home: <c>SessionOverview</c> is one row per night and
    /// <c>RigGroup</c> carries counts and ranges rather than the five chart metrics. It is
    /// therefore taken here, over the loaded night's own frames, through the same
    /// <c>Statistics.Median</c> the query builds every other median with, so the two cannot
    /// disagree about an even frame count. Ruling Q20 applies as it does to the filter split: a
    /// night whose card has not been expanded has no frames to split and contributes a null.
    /// </remarks>
    private static double? SessionValue(SessionCardViewModel card, string? rigLabel, ChartMetric metric)
    {
        if (rigLabel is null)
        {
            return metric.SessionValue(card.Overview);
        }

        return card.Detail is { } detail
            ? Statistics.Median(detail.Frames
                .Where(frame => string.Equals(frame.Rig, rigLabel, StringComparison.Ordinal))
                .Select(metric.FrameValue))
            : null;
    }

    /// <summary>One rig's contribution to the series set. A null label is the whole-night case and
    /// admits every night.</summary>
    /// <param name="SplitsByFilter">
    /// Whether this spec also draws one line per enabled filter. True everywhere except the
    /// whole-night spec of a multi-rig target (fix-wave review P2-1F).
    /// </param>
    /// <remarks>
    /// The per-filter lines belong to the rig series on a multi-rig target, and only to them.
    /// <c>FilterMedian</c> with a null label reads
    /// <c>detail.FilterMedians.FirstOrDefault(name matches)</c>, and
    /// <c>SessionDetailQuery.SplitPerRig</c> builds that block per rig and stamps every row with
    /// its rig's label whenever the night has more than one rig: there is no night-level row to
    /// find, so the lookup returned the first rig's and the chart drew a solid, unsuffixed
    /// "HFR (SII)" line that was one rig's median under the night's name, duplicating that rig's
    /// own dashed line. The whole-night spec's honest contribution on such a target is the
    /// unfiltered line alone, which is the one the ruling names ("one point per night from the
    /// overview"). A single-rig night's <c>FilterMedians</c> rows carry no rig label and are the
    /// night's own, so there the same spec draws both and nothing changed.
    /// </remarks>
    private sealed record RigSeriesSpec(
        string? Label, string Suffix, double[]? Dash, bool SplitsByFilter = true);

    // Spec 13: "X axis session date, ordinal with date labels". One label per plotted session, so
    // MinStep and ForceStepToMin keep a 40 session target from rendering half a label per tick.
    private static ICartesianAxis XAxis(IReadOnlyList<SessionCardViewModel> plotted)
    {
        var labels = DateLabels(plotted);

        // No Labels list: the drawing step prints every entry of one and ignores the labeler. The
        // series' tooltip formatter carries the full date instead.
        return new Axis
        {
            Labeler = DateLabeler(labels),
            MinStep = 1,
            ForceStepToMin = true,
        };
    }

    // The year is only shown when the plotted span crosses one, which is the difference
    // between a readable axis and eleven repetitions of the same four digits.
    internal static string[] DateLabels(IReadOnlyList<SessionCardViewModel> plotted)
    {
        var format = plotted.Count > 0 && plotted[0].SessionDate.Year != plotted[^1].SessionDate.Year
            ? "yyyy-MM-dd"
            : "MM-dd";
        return [.. plotted.Select(card => card.SessionDate.ToString(format, CultureInfo.InvariantCulture))];
    }

    private static Func<LiveChartsCore.Kernel.ChartPoint, string> DateTip(string[] labels)
        => point => DateAt(labels, point.Coordinate.SecondaryValue);

    internal static string DateAt(string[] labels, double x)
    {
        var index = (int)Math.Round(x);
        return index >= 0 && index < labels.Length ? labels[index] : "";
    }

    private static Func<double, string> DateLabeler(string[] labels, bool labelled = true)
        => value =>
        {
            var index = (int)Math.Round(value);
            return labelled && index >= 0 && index < labels.Length && LabelShown(index, labels.Length) ? labels[index] : "";
        };

    private const int MaxDateLabels = 8;

    /// <summary>Whether the night at <paramref name="index"/> of <paramref name="count"/> prints its
    /// date: every step-th night, the first and the last always, so a long span stays readable.
    /// </summary>
    internal static bool LabelShown(int index, int count)
    {
        var step = Math.Max(1, (int)Math.Ceiling(count / (double)MaxDateLabels));
        return index == 0
            || index == count - 1
            || (index % step == 0 && count - 1 - index >= (step + 1) / 2);
    }

    // On a multi-rig night the query already split the per-filter medians per rig and stamped each
    // row with its rig's label, so the rig split of a filter series is a second clause on the same
    // lookup rather than a second computation.
    private static double? FilterMedian(
        SessionCardViewModel card, string filter, ChartMetric metric, string? rigLabel)
        => card.Detail is { } detail
            && detail.FilterMedians.FirstOrDefault(medians =>
                string.Equals(medians.FilterName, filter, StringComparison.OrdinalIgnoreCase)
                && (rigLabel is null
                    || string.Equals(medians.RigLabel, rigLabel, StringComparison.Ordinal))) is { } match
            ? metric.FilterValue(match)
            : null;

    // Newest first out of the page, newest N kept (ruling Q21), then reversed so the X axis reads
    // left to right in time.
    private List<SessionCardViewModel> Plotted()
    {
        var pool = Pool();
        var take = ShowAllSessions
            ? pool.Count
            : Math.Min(pool.Count, Math.Max(1, Selection.DefaultChartSessions));

        return [.. pool.Take(take).Reverse()];
    }

    // The nights the chart may plot: measured ones, and the empty ones once shown. The newest-N cap
    // and the scope text therefore count measured nights only.
    private List<SessionCardViewModel> Pool()
        => [.. Candidates().Where(card => ShowEmptyNights || HasMetrics(card))];

    private IEnumerable<SessionCardViewModel> Candidates()
        => CheckedOnly ? _sessions.Where(card => card.IsChecked) : _sessions;

    // The ledger row's own medians, the figures the whole-night series plots, so the chart, the
    // Compare table and the count read one rule.
    private static bool HasMetrics(SessionCardViewModel card)
        => ChartMetrics.All.Any(metric => metric.SessionValue(card.Overview) is not null);

    /// <summary>Every rig the loaded session details carry, in the order they first appear walking
    /// the target's nights oldest first, which is spec 12.4's first-capture order extended across
    /// the target. A card that has not been expanded carries no rig list yet (ruling Q20), exactly
    /// as it carries no per-filter medians.</summary>
    private IReadOnlyList<string> CanonicalRigs()
    {
        List<string> order = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var card in _sessions.Reverse())
        {
            foreach (var rig in card.Detail?.Rigs ?? [])
            {
                if (seen.Add(rig.Label))
                {
                    order.Add(rig.Label);
                }
            }
        }

        return order;
    }

    private IReadOnlyList<string> CanonicalFilters()
        => [.. _sessions
            .SelectMany(card => card.Overview.FiltersUsed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, Selection.FilterComparer)];

    private void Track()
    {
        foreach (var gone in _tracked.Except(_sessions).ToList())
        {
            gone.PropertyChanged -= OnCardChanged;
            _tracked.Remove(gone);
        }

        foreach (var card in _sessions)
        {
            if (_tracked.Add(card))
            {
                card.PropertyChanged += OnCardChanged;
            }
        }
    }

    // A card that has just loaded its detail brings the per-filter medians a split series needs
    // (ruling Q20), so expanding a card fills in its filter-split points rather than leaving them
    // gapped until something else happens to trigger a rebuild.
    //
    // It brings the rig groups on the same publication and for the same reason (review P2-1). A
    // card's Detail arrives long after Reload, which is the only other place the rigs are offered,
    // so without this an ordinary visit leaves ChartSelectionViewModel.Rigs empty: neither chart
    // would draw spec 13's pill row and this chart would never split per rig.
    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionCardViewModel.Detail))
        {
            Selection.OfferRigs(CanonicalRigs());
            Rebuild();
        }
        else if (e.PropertyName == nameof(SessionCardViewModel.IsChecked) && CheckedOnly)
        {
            // The switch is disabled with nothing checked, so the last uncheck returns to All.
            if (_sessions.Any(card => card.IsChecked))
            {
                Rebuild();
            }
            else
            {
                CheckedOnly = false;
            }
        }
    }

    private void NotifyScope()
    {
        OnPropertyChanged(nameof(HasSessionScope));
        OnPropertyChanged(nameof(SessionScopeText));
    }
}
