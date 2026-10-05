using System.Globalization;
using GalactiLog.App.Theme;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// Spec 13's "Per-session frame metrics", inside an expanded session card: one point per frame in
/// capture order, one series per enabled metric, optionally split per filter, and a horizontal
/// reference line at the session median of the primary metric.
/// </summary>
/// <remarks>
/// Constructed from the <c>SessionDetail</c> the card has already loaded and holds nothing else:
/// it issues no query, and the card disposes it on every <c>Invalidate</c> (Task 4 handoff).
/// </remarks>
public sealed class SessionChartViewModel : MetricChartViewModel
{
    private readonly SessionDetail _detail;

    /// <param name="detail">The loaded session, handed over by the card's chart factory.</param>
    /// <param name="selection">The shared metric and filter selection.</param>
    /// <param name="logger">Optional.</param>
    public SessionChartViewModel(
        SessionDetail detail,
        ChartSelectionViewModel selection,
        ILogger? logger = null)
        : base(selection, "Per-session frame metrics", logger)
    {
        _detail = detail;
        RefreshRigPills();
        Rebuild();
    }

    // The pills of this night only; the shared list keeps every loaded night's rigs and their
    // checked state, so a rig turned off here is still off when its night comes back.
    protected override bool OffersRig(string rigLabel)
        => _detail?.Rigs?.Any(rig => string.Equals(rig.Label, rigLabel, StringComparison.Ordinal)) == true;

    private NightLaneAxis? _laneAxis;
    private TimeZoneInfo _zone = TimeZoneInfo.Utc;
    private NightStripViewModel? _strip;
    private GuidingSectionViewModel? _guiding;
    private double[] _xs = [];
    private int[] _frameOfSlot = [];
    private IReadOnlyList<RectangularSection> _median = [];
    private IReadOnlyList<RectangularSection> _boundaries = [];

    /// <summary>The night's shared time axis. Null leaves the frame-index axis.</summary>
    public override NightLaneAxis? LaneAxis => _laneAxis;

    /// <inheritdoc />
    public override bool HasGuidingPill => true;

    /// <inheritdoc />
    public override bool IsGuidingAvailable => _guiding is not null;

    /// <inheritdoc />
    public override GuidingSectionViewModel? Guiding => _guiding;

    /// <inheritdoc />
    public override NightStripViewModel? Strip => _strip;

    /// <summary>The two topics the host's help glyph can show, for the placement census.</summary>
    public static IReadOnlyList<string> HelpTopicIds { get; } = ["target.session-metrics", "target.guiding"];

    /// <summary>The help glyph's topic: the guiding one while the guide trace is drawn.</summary>
    public string HelpTopicId => ShowsGuiding ? HelpTopicIds[1] : HelpTopicIds[0];

    protected override void OnGuidingChanged() => OnPropertyChanged(nameof(HelpTopicId));

    /// <summary>Puts the chart on the night's shared time axis; <paramref name="zone"/> turns a
    /// stored capture instant into the axis' local clock. <paramref name="strip"/> is the night's
    /// timeline model, which owns the selected frame this chart marks and selects through.
    /// <paramref name="guiding"/> is the night's Guiding section, drawn whole on the same axis
    /// while the Guiding pill is on; null disables the pill.</summary>
    public void UseLaneAxis(
        NightLaneAxis? axis, TimeZoneInfo zone, NightStripViewModel? strip = null, GuidingSectionViewModel? guiding = null)
    {
        _laneAxis = axis;
        _zone = zone;
        _guiding = guiding;
        if (guiding is not null)
        {
            guiding.DrawsWholeNight = true;
        }

        if (!ReferenceEquals(_strip, strip))
        {
            if (_strip is not null)
            {
                _strip.ActiveFrameChanged -= OnActiveFrameChanged;
            }

            _strip = strip;
            if (_strip is not null)
            {
                _strip.ActiveFrameChanged += OnActiveFrameChanged;
            }
        }

        OnPropertyChanged(nameof(LaneAxis));
        OnPropertyChanged(nameof(Strip));
        OnPropertyChanged(nameof(Guiding));
        RaiseGuidingChanged();
        Rebuild();
    }

    /// <summary>The night's chart is never laned (D212).</summary>
    protected override bool DrawsLanes => false;

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_strip is not null)
        {
            _strip.ActiveFrameChanged -= OnActiveFrameChanged;
            _strip = null;
        }

        base.Dispose();
    }

    private void OnActiveFrameChanged(object? sender, EventArgs e) => Sections = [.. _median, .. _boundaries, .. ActiveMark()];

    // The selected frame's mark at its timeline tick's fraction, in the timeline's active ink.
    private IEnumerable<RectangularSection> ActiveMark()
        => _laneAxis is not null
            && _strip is { ActiveFrame: { } index } strip
            && strip.Ticks.FirstOrDefault(tick => tick.FrameIndex == index) is { } active
            ? [new RectangularSection
            {
                Xi = active.Fraction,
                Xj = active.Fraction,
                Fill = null,
                Stroke = new SolidColorPaint(ChartTheme.Palette.TooltipText) { StrokeThickness = ActiveMarkWidth },
            }]
            : [];

    // A press names the frame whose point sits nearest the pressed x and selects it through the
    // strip, the member a timeline tick uses, so the frames table scrolls to it.
    protected override void PressedAt(double x)
    {
        if (_laneAxis is null || _strip is not { } strip)
        {
            return;
        }

        int? nearest = null;
        for (var index = 0; index < _xs.Length; index++)
        {
            if (double.IsFinite(_xs[index]) && (nearest is not { } best || Math.Abs(_xs[index] - x) < Math.Abs(_xs[best] - x)))
            {
                nearest = index;
            }
        }

        if (nearest is { } slot)
        {
            strip.SelectFrame(_frameOfSlot[slot]);
        }
    }

    /// <inheritdoc />
    protected override void Rebuild()
    {
        var frames = _detail.Frames;
        var enabled = Selection.EnabledMetrics;
        var splits = SplitFilters();
        var overall = IncludeOverall();
        var rigs = RigSeries();

        var plan = AxisPlan.For(
            enabled,
            metric => rigs.Any(rig =>
                (overall && frames.Any(frame => IsRig(frame, rig.Label) && metric.FrameValue(frame) is not null))
                || splits.Any(filter => frames.Any(frame =>
                    IsRig(frame, rig.Label)
                    && IsFilter(frame, filter)
                    && metric.FrameValue(frame) is not null))),
            fixedInsets: _laneAxis is not null);

        if (plan.Primary is null)
        {
            _xs = [];
            _frameOfSlot = [];
            _median = [];
            _boundaries = [];
            PublishEmpty();
            return;
        }

        // R6: on the lane axis a merged night's frames are laid over slots with one empty slot
        // ahead of every night after the first, so the null split breaks the line there.
        HashSet<int> breaks = _laneAxis is null ? [] : [.. _detail.Nights.Skip(1).Select(span => span.FirstFrameIndex)];
        var slots = new List<FrameRow?>(frames.Count + breaks.Count);
        var slotOf = new int[frames.Count];
        var frameOfSlot = new List<int>(frames.Count + breaks.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            if (breaks.Contains(index))
            {
                slots.Add(null);
                frameOfSlot.Add(-1);
            }

            slotOf[index] = slots.Count;
            frameOfSlot.Add(index);
            slots.Add(frames[index]);
        }

        _frameOfSlot = [.. frameOfSlot];

        // On the night's lane axis a frame sits at its capture time, else at its index.
        var xs = slots.Select((frame, index) => frame is null
            ? double.NaN
            : _laneAxis is not { } axis
                ? index
                : frame.CaptureDate is { } utc ? axis.FractionOf(NightStripViewModel.ToLocal(utc, _zone)) : double.NaN).ToArray();
        _xs = xs;
        var frameXs = Array.ConvertAll(slotOf, slot => xs[slot]);
        LineSeries<double?> Placed(LineSeries<double?> line)
        {
            if (_laneAxis is not null)
            {
                line.Mapping = (value, index) => value is { } y && double.IsFinite(xs[index])
                    ? new Coordinate(xs[index], y)
                    : Coordinate.Empty;
            }

            return line;
        }

        var series = new List<ISeries>();
        var rings = new List<ISeries>();
        foreach (var metric in enabled)
        {
            var scalesYAt = plan.ScalesYAt(metric);
            if (OutlierRing(metric, frameXs, scalesYAt, overall, splits, rigs) is { } ring)
            {
                rings.Add(ring);
            }

            // Spec 13: on a multi-rig night each metric splits into one series per enabled rig,
            // and on a single-rig night the one entry here has a null label and no dash, so the
            // chart draws exactly what it drew before this phase.
            foreach (var rig in rigs)
            {
                if (overall)
                {
                    // A frame with no HFR breaks the HFR line rather than joining across it, the
                    // same rule the cross-session chart applies to a night with no value. A frame
                    // of another rig is a null for the same reason and by the same mechanism,
                    // which is what the web application's TargetMetricsChart does.
                    series.Add(Placed(Line(
                        metric.Label + rig.Suffix,
                        [.. slots.Select(frame =>
                            frame is not null && IsRig(frame, rig.Label) ? metric.FrameValue(frame) : null)],
                        MetricColour(metric),
                        scalesYAt,
                        rig.Dash,
                        splitOnNull: rig.SplitsOnNull)));
                }

                foreach (var filter in splits)
                {
                    // Here the per-filter values are trivially available: the metric's value for
                    // frames of that filter, null elsewhere, which is what the web application's
                    // SessionMetricsChart plots.
                    series.Add(Placed(Line(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{metric.Label} ({filter}){rig.Suffix}"),
                        [.. slots.Select(frame =>
                            frame is not null && IsRig(frame, rig.Label) && IsFilter(frame, filter)
                                ? metric.FrameValue(frame)
                                : null)],
                        FilterColour(filter),
                        scalesYAt,
                        rig.Dash,
                        splitOnNull: rig.SplitsOnNull)));
                }
            }
        }

        // The lanes fix the plot's left and right edges; the top and bottom only clear the y
        // labels, since the timeline carries the clock.
        DrawMargin = _laneAxis is { } lane
            ? new Margin((float)lane.PlotLeft, LaneVerticalMargin, (float)lane.PlotRight, LaneVerticalMargin)
            : null;
        series.AddRange(rings);
        _median = MedianLine(plan.Primary);
        _boundaries = BoundarySections(_laneAxis);
        Publish(series, [_laneAxis is { } shared ? LaneXAxis(shared) : XAxis()], plan, [.. _median, .. _boundaries, .. ActiveMark()]);
    }

    // R6: each night boundary after the first as a dashed one pixel line in the separator ink
    // with its date as the label; the first boundary carries the label alone. None on one night.
    private static IReadOnlyList<RectangularSection> BoundarySections(NightLaneAxis? axis)
        => axis is null
            ? []
            : [.. axis.Boundaries.Select((boundary, index) => new RectangularSection
            {
                Xi = boundary.Fraction,
                Xj = boundary.Fraction,
                Fill = null,
                Stroke = index == 0
                    ? null
                    : new SolidColorPaint(ChartTheme.Palette.Separators)
                    {
                        StrokeThickness = 1,
                        PathEffect = new DashEffect([4f, 4f]),
                    },
                Label = boundary.Label,
                LabelPaint = new SolidColorPaint(ChartTheme.Palette.AxisLabels),
                LabelSize = 11,
                ScalesYAt = 0,
            })];

    /// <summary>A ring around each outlier frame's point, in the timeline's outlier ink, with
    /// no fill, so the point keeps its metric colour. Null when no plotted frame is an outlier.
    /// </summary>
    private ScatterSeries<ObservablePoint>? OutlierRing(
        ChartMetric metric,
        double[] xs,
        int scalesYAt,
        bool overall,
        IReadOnlyList<string> splits,
        IReadOnlyList<RigSeriesSpec> rigs)
    {
        var frames = _detail.Frames;
        var points = new List<ObservablePoint>();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if ((frame.IsHfrOutlier || frame.IsEccentricityOutlier)
                && metric.FrameValue(frame) is { } value
                && double.IsFinite(xs[index])
                && (overall || splits.Any(filter => IsFilter(frame, filter)))
                && rigs.Any(rig => IsRig(frame, rig.Label)))
            {
                points.Add(new ObservablePoint(xs[index], value));
            }
        }

        return points.Count == 0
            ? null
            : new ScatterSeries<ObservablePoint>
            {
                Name = metric.Label + " outliers",
                Values = points,
                GeometrySize = RingSize,
                Stroke = new SolidColorPaint(ChartTheme.Palette.Outlier) { StrokeThickness = 1.5f },
                Fill = null,
                ScalesYAt = scalesYAt,
            };
    }

    // The timeline's marks as grid lines over its domain, and no labels or name of its own. The
    // default unit width of 1 is the whole 0 to 1 domain, which made every point's hover area
    // the full plot width, so a press hit every series and the first one's point won.
    private static ICartesianAxis LaneXAxis(NightLaneAxis axis) => new Axis
    {
        MinLimit = 0d,
        MaxLimit = 1d,
        UnitWidth = 1e-6d,
        CustomSeparators = axis.TickFractions,
        Labeler = _ => "",
    };

    private const float LaneVerticalMargin = 10f;
    private const double RingSize = 12d;
    private const float ActiveMarkWidth = 2f;

    /// <summary>Spec 13's rig split for this night: one entry per enabled rig on a multi-rig night,
    /// and exactly one label-free, dash-free entry on a single-rig one. The dash index comes from
    /// the full rig list rather than from the enabled subset, so unchecking one rig does not
    /// re-pattern another.</summary>
    private IReadOnlyList<RigSeriesSpec> RigSeries()
    {
        var rigs = _detail.Rigs ?? [];
        if (rigs.Count <= 1)
        {
            return [new RigSeriesSpec(null, "", null)];
        }

        return
        [
            .. rigs
                .Where(rig => Selection.IsRigEnabled(rig.Label))
                .Select(rig => new RigSeriesSpec(
                    rig.Label,
                    $" [{rig.Label}]",
                    RigDash(RigDashIndex(rig.Label, rig.Index)))),
        ];
    }

    private static bool IsRig(FrameRow frame, string? rigLabel)
        => rigLabel is null || string.Equals(frame.Rig, rigLabel, StringComparison.Ordinal);

    /// <summary>One rig's contribution to the series set: which frames it admits, what its series
    /// names are suffixed with, and the dash it is drawn with. A null label is the single-rig case
    /// and admits every frame.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="SplitsOnNull"/> is verification blocker B1. A rig's series is built as a
    /// night-length array with a null at every frame index that does not belong to that rig, and
    /// <c>Line</c> sets <c>EnableNullSplitting</c> for spec 13's "gaps preserved for missing
    /// sessions". On a multi-rig night the two rigs interleave in capture order, so every one of a
    /// rig's values sits between two nulls, every segment of the line is split to a single point,
    /// and no stroke survives to carry the dash: both rigs rendered identically and spec 13's one
    /// distinction was invisible in the running application while the pills went on drawing their
    /// dashed samples.
    /// </para>
    /// <para>
    /// The nulls in a per-rig series are not missing measurements. They are the other rig's
    /// frames, which this series has nothing to say about, so breaking the line on them states a
    /// gap that is not there. Turning the split off for these series alone joins each rig's own
    /// frames into one line at their real frame indices, which is what the reader is looking at.
    /// The single-rig spec keeps it true and its chart is byte for byte what it was; the cost is
    /// that a genuinely missing metric value inside one rig's own frames now joins across rather
    /// than breaking, which is the narrower of the two errors and the one spec 13 does not make a
    /// rule about for a rig split.
    /// </para>
    /// </remarks>
    private sealed record RigSeriesSpec(string? Label, string Suffix, double[]? Dash)
    {
        public bool SplitsOnNull => Label is null;
    }

    // Spec 13: "X axis frame index, linear". The labeler is what makes it one-based, rather than
    // an ordinal Labels list, which would make the axis categorical instead of linear.
    private static ICartesianAxis XAxis() => new Axis
    {
        Name = "Frame",
        Labeler = value => (value + 1).ToString("N0", CultureInfo.InvariantCulture),
        MinStep = 1,
    };

    /// <summary>
    /// Spec 13's "horizontal reference line at the session median for the primary metric", drawn
    /// in the primary metric's own colour at reduced opacity so it reads as an annotation rather
    /// than as a sixth series.
    /// </summary>
    /// <remarks>
    /// The figure comes from the <c>MetricRangeSummary</c> Task 2 already computed, never from a
    /// median recomputed here: two medians that can disagree on an even frame count is a defect
    /// waiting to happen. Detected stars is the one metric <c>SessionDetail</c> carries no summary
    /// for, and it falls back to <c>Statistics.Median</c>, which is the same function Task 2 built
    /// the other four with, so the two still cannot disagree.
    /// </remarks>
    private IReadOnlyList<RectangularSection> MedianLine(ChartMetric primary)
    {
        var median = primary.SessionRange(_detail)?.Median
            ?? Statistics.Median(_detail.Frames.Select(frame => primary.FrameValue(frame)));

        if (median is not { } value)
        {
            return [];
        }

        var colour = MetricColour(primary);
        return
        [
            new RectangularSection
            {
                Yi = value,
                Yj = value,
                Fill = null,
                Stroke = new SolidColorPaint(colour.WithAlpha(ReferenceLineAlpha)) { StrokeThickness = 1 },
                Label = string.Create(
                    CultureInfo.InvariantCulture,
                    $"median {value.ToString("N" + primary.Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}{primary.Unit}"),
                LabelPaint = new SolidColorPaint(colour.WithAlpha(ReferenceLineAlpha)),
                LabelSize = 11,
                // The primary metric always defines the left axis (spec 13), so the section that
                // annotates it scales against index 0 whether or not a right axis is present.
                ScalesYAt = 0,
            },
        ];
    }

    private static bool IsFilter(FrameRow frame, string canonicalFilter)
        => string.Equals(frame.FilterUsed, canonicalFilter, StringComparison.OrdinalIgnoreCase);

    // Six tenths: visible against the plot background, clearly behind the series it annotates.
    private const byte ReferenceLineAlpha = 153;
}
