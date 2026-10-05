using System.Globalization;
using Avalonia.Media.Immutable;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>One exposure on the night strip.</summary>
/// <param name="FrameIndex">The index into the capture-ordered frame list the strip was built
/// from, which is the same order <c>SessionDetail.Frames</c> and <c>FrameTableViewModel</c>'s
/// capture order use, so a click can name a row.</param>
/// <param name="Fraction">Where the tick sits on the axis, 0 at the strip's start and 1 at its
/// end. Computed once at construction: the control must not do time arithmetic in Render.</param>
/// <param name="Brush">The filter's configured colour, or the fallback grey. An
/// <see cref="ImmutableSolidColorBrush"/>, never a <c>SolidColorBrush</c> (spec 14.5): the strip is
/// built wherever the session detail arrives, which is not always the UI thread.</param>
/// <param name="IsOutlier">Either of the query's two outlier flags. The tick is then drawn taller
/// and in the worse ink, which is the only thing on the strip that is not a filter colour.</param>
/// <param name="ToolTipText">"L 02:11, HFR 2.91" and so on, built once.</param>
public sealed record NightTick(
    int FrameIndex,
    double Fraction,
    ImmutableSolidColorBrush Brush,
    bool IsOutlier,
    string ToolTipText);

/// <summary>One hour mark on the night strip's axis.</summary>
/// <param name="Fraction">0 to 1 across the axis.</param>
/// <param name="Label">"22:00" or "10 PM" per <c>general.use_24h_time</c>.</param>
public sealed record AxisTick(double Fraction, string Label);

/// <summary>
/// The comp's signature graphic: one night drawn dusk to dawn, one tick per exposure in its
/// filter's ink, outliers taller and in the worse ink, the astronomical-night band behind it.
/// It replaces the frame-index chart as the session's first graphic (P12 direction), so it is
/// deliberately not a LiveCharts series: a chart control per session would pay a chart's whole
/// layout cost to draw a few hundred one-pixel lines.
/// </summary>
/// <remarks>
/// <para>
/// Everything the control needs is computed here, once, at construction. <c>Render</c> runs on
/// every resize and every theme swap, so no time arithmetic, no formatting and no brush lookup
/// happens there.
/// </para>
/// <para>
/// A frame with a null <c>CaptureDate</c> produces no tick: it has no place on a time axis.
/// Spec 12.4's null <c>session_date</c> paragraph keeps such a frame out of every session on the
/// page, but a session detail can still hand one over, so the rule is explicit rather than
/// assumed. <see cref="Ticks"/>.Count is therefore the dated frame count, not the frame count.
/// </para>
/// </remarks>
public sealed class NightStripViewModel
{
    /// <summary>Half the hit width, as a fraction of the axis. 0.004 is about 5 px on a 1180 px
    /// strip, which is a comfortable target for a 1.4 px tick.</summary>
    public const double HitFraction = 0.004d;

    /// <summary>Axis marks fall on the hour, every this many hours, strictly inside the domain.
    /// The comp's strip runs 18:00 to 06:00 and marks 20:00, 22:00, 00:00, 02:00 and 04:00.</summary>
    private const int AxisHourStep = 2;

    // The two clock formats live on SessionTimeFormat, which is the one renderer of a clock time
    // on this page (coordinator item 1). An axis mark and a frame table Time cell cannot disagree
    // on shape while both read it.

    public NightStripViewModel(
        IReadOnlyList<FrameRow> frames,
        IReadOnlyDictionary<string, ImmutableSolidColorBrush> filterBrushes,
        ImmutableSolidColorBrush fallbackFilterBrush,
        (DateTime Dusk, DateTime Dawn)? bounds,
        TimeZoneInfo zone,
        bool use24Hour)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(filterBrushes);
        ArgumentNullException.ThrowIfNull(fallbackFilterBrush);
        ArgumentNullException.ThrowIfNull(zone);

        // Pass one: the dated frames, in the order they arrived, which is capture order.
        var dated = new List<(int Index, FrameRow Frame, DateTime Local)>(frames.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame.CaptureDate is not { } captureUtc)
            {
                continue;
            }

            dated.Add((index, frame, ToLocal(captureUtc, zone)));
        }

        var earliestTick = dated.Count == 0 ? (DateTime?)null : dated.Min(entry => entry.Local);
        var latestTick = dated.Count == 0 ? (DateTime?)null : dated.Max(entry => entry.Local);

        // A zero or inverted interval is treated as no band: NightBounds returns null for the
        // no-coordinates and polar-summer cases, and anything else degenerate draws nothing rather
        // than a rectangle of negative width.
        HasBand = bounds is not null && bounds.Value.Dawn > bounds.Value.Dusk;
        var duskOrNull = HasBand ? Unspecified(bounds!.Value.Dusk) : (DateTime?)null;
        var dawnOrNull = HasBand ? Unspecified(bounds!.Value.Dawn) : (DateTime?)null;

        // The axis domain: the earlier of the first tick and dusk, floored to the hour, to the
        // later of the last tick and dawn, ceilinged to the hour, so a tick never sits on the edge.
        var domainStart = Earlier(earliestTick, duskOrNull);
        var domainEnd = Later(latestTick, dawnOrNull);

        if (domainStart is { } start && domainEnd is { } end)
        {
            StartLocal = FloorToHour(start);
            EndLocal = CeilingToHour(end);
        }

        var span = (EndLocal - StartLocal).TotalMinutes;

        var ticks = new List<NightTick>(dated.Count);
        var outliers = 0;
        foreach (var (index, frame, local) in dated)
        {
            var isOutlier = frame.IsHfrOutlier || frame.IsEccentricityOutlier;
            if (isOutlier)
            {
                outliers++;
            }

            ticks.Add(new NightTick(
                index,
                FractionOf(local, StartLocal, span),
                BrushFor(frame.FilterUsed, filterBrushes, fallbackFilterBrush),
                isOutlier,
                ToolTip(frame, zone, use24Hour)));
        }

        Ticks = ticks;
        OutlierCount = outliers;

        if (HasBand)
        {
            BandStartFraction = FractionOf(duskOrNull!.Value, StartLocal, span);
            BandEndFraction = FractionOf(dawnOrNull!.Value, StartLocal, span);
            BandLabel = "astronomical night";
            Bands = [(BandStartFraction, BandEndFraction)];
        }

        AxisTicks = BuildAxisTicks(StartLocal, EndLocal, span, use24Hour);
        AxisLabelTicks = AxisTicks.Count > 0 || span <= 0d
            ? AxisTicks
            : [new(0d, Clock(StartLocal, use24Hour)), new(1d, Clock(EndLocal, use24Hour))];
        LaneAxis = span > 0d
            ? new NightLaneAxis(StartLocal, EndLocal, NightLaneAxis.SharedPlotLeft, NightLaneAxis.SharedPlotRight)
            {
                TickFractions = [.. AxisTicks.Select(tick => tick.Fraction)],
            }
            : null;

        if (earliestTick is { } first)
        {
            _firstClock = Clock(first, use24Hour);
            FirstLabel = "first " + _firstClock;
            FirstFraction = FractionOf(first, StartLocal, span);
        }

        if (latestTick is { } last)
        {
            _lastClock = Clock(last, use24Hour);
            LastLabel = "last " + _lastClock;
            LastFraction = FractionOf(last, StartLocal, span);
        }
    }

    /// <summary>The stitched strip (Phase 25 R5): the members' ticks, bands, marks and labels
    /// remapped through <see cref="NightLaneAxis.Stitched"/>. <paramref name="nights"/> oldest
    /// first, each a single-night strip; <c>IndexOffset</c> is the night's first index in the
    /// merged frame list, so a tick still names its row. A member whose own axis is null draws
    /// nothing; a set of nothing but those has no axis.</summary>
    public static NightStripViewModel Stitched(IReadOnlyList<(DateOnly Night, NightStripViewModel Strip, int IndexOffset)> nights)
    {
        ArgumentNullException.ThrowIfNull(nights);
        var members = nights.Where(night => night.Strip.LaneAxis is not null).OrderBy(night => night.Night).ToArray();
        if (members.Length == 0)
        {
            return new NightStripViewModel(null, [], [], [], [], 0, "", 0d, "", 0d);
        }

        var axis = NightLaneAxis.Stitched([.. members.Select(night => (night.Night, night.Strip.LaneAxis!))]);
        var ticks = new List<NightTick>();
        var bands = new List<(double Start, double End)>();
        var axisTicks = new List<AxisTick>();
        var labelTicks = new List<AxisTick>();
        var outliers = 0;
        var prefix = nights.Count > 1;
        var firstLabel = "";
        var firstFraction = 0d;
        var lastLabel = "";
        var lastFraction = 0d;
        for (var index = 0; index < members.Length; index++)
        {
            var (night, strip, offset) = members[index];
            var segment = axis.Segments[index];
            double Map(double fraction) => segment.FromFraction + (fraction * (segment.ToFraction - segment.FromFraction));

            ticks.AddRange(strip.Ticks.Select(tick => tick with { FrameIndex = tick.FrameIndex + offset, Fraction = Map(tick.Fraction) }));
            outliers += strip.OutlierCount;
            if (strip.HasBand)
            {
                bands.Add((Map(strip.BandStartFraction), Map(strip.BandEndFraction)));
            }

            axisTicks.AddRange(strip.AxisTicks.Select(tick => tick with { Fraction = Map(tick.Fraction) }));
            labelTicks.AddRange(strip.AxisLabelTicks.Select(tick => tick with { Fraction = Map(tick.Fraction) }));

            // The members are oldest first and their domains contiguous, so the set's first dated
            // frame is the first member's and its last is the last member's that has one.
            var date = prefix ? night.ToString("MM-dd ", CultureInfo.InvariantCulture) : "";
            if (firstLabel.Length == 0 && strip.Ticks.Count > 0)
            {
                firstLabel = "first " + date + strip._firstClock;
                firstFraction = Map(strip.FirstFraction);
            }

            if (strip.Ticks.Count > 0)
            {
                lastLabel = "last " + date + strip._lastClock;
                lastFraction = Map(strip.LastFraction);
            }
        }

        return new NightStripViewModel(axis, ticks, bands, axisTicks, labelTicks, outliers, firstLabel, firstFraction, lastLabel, lastFraction);
    }

    private NightStripViewModel(
        NightLaneAxis? axis,
        IReadOnlyList<NightTick> ticks,
        IReadOnlyList<(double Start, double End)> bands,
        IReadOnlyList<AxisTick> axisTicks,
        IReadOnlyList<AxisTick> axisLabelTicks,
        int outlierCount,
        string firstLabel,
        double firstFraction,
        string lastLabel,
        double lastFraction)
    {
        LaneAxis = axis;
        StartLocal = axis?.StartLocal ?? default;
        EndLocal = axis?.EndLocal ?? default;
        Ticks = ticks;
        Bands = bands;
        Boundaries = axis?.Boundaries ?? [];
        HasBand = bands.Count > 0;
        if (HasBand)
        {
            BandStartFraction = bands[0].Start;
            BandEndFraction = bands[0].End;
            BandLabel = "astronomical night";
        }

        AxisTicks = axisTicks;
        AxisLabelTicks = axisLabelTicks;
        OutlierCount = outlierCount;
        FirstLabel = firstLabel;
        FirstFraction = firstFraction;
        LastLabel = lastLabel;
        LastFraction = lastFraction;
    }

    private readonly string _firstClock = "";
    private readonly string _lastClock = "";

    /// <summary>Capture time of the first dated frame, or the band's dusk if that is earlier,
    /// floored to the enclosing hour. Equal to <see cref="EndLocal"/> when the night has neither
    /// a dated frame nor a band.</summary>
    public DateTime StartLocal { get; }

    /// <summary>Capture time of the last dated frame, or the band's dawn if that is later,
    /// ceilinged to the enclosing hour.</summary>
    public DateTime EndLocal { get; }

    /// <summary>One tick per dated frame, in capture order. Shorter than the frame list when a
    /// frame carries no capture time; see the type remarks.</summary>
    public IReadOnlyList<NightTick> Ticks { get; }

    /// <summary>Ticks whose frame carried either outlier flag. Equal to the flagged frame count
    /// among dated frames, which is the roadmap's verify line.</summary>
    public int OutlierCount { get; }

    /// <summary>False when <c>NightBounds</c> returned null, which is the no-coordinates case and
    /// the polar-summer case. The control then draws no band and no band label.</summary>
    public bool HasBand { get; }

    public double BandStartFraction { get; }

    public double BandEndFraction { get; }

    /// <summary>Every astronomical-night band on the axis: the one band on a single night, one
    /// per night that has bounds on a stitched strip (R5).</summary>
    public IReadOnlyList<(double Start, double End)> Bands { get; } = [];

    /// <summary>The axis' night boundaries, empty on a single night.</summary>
    public IReadOnlyList<LaneBoundary> Boundaries { get; } = [];

    /// <summary>Hour marks on the axis, every two hours on the hour strictly inside the domain,
    /// each with its label.</summary>
    public IReadOnlyList<AxisTick> AxisTicks { get; }

    /// <summary>The marks that carry a clock label: <see cref="AxisTicks"/>, or the night's two
    /// ends when it is too short to hold one.</summary>
    public IReadOnlyList<AxisTick> AxisLabelTicks { get; }

    /// <summary>This night's shared lane axis over the same domain and marks, or null when the
    /// domain is empty.</summary>
    public NightLaneAxis? LaneAxis { get; }

    /// <summary>"first 19:05", empty when the night has no dated frame.</summary>
    public string FirstLabel { get; } = "";

    /// <summary>"last 03:31", empty when the night has no dated frame.</summary>
    public string LastLabel { get; } = "";

    /// <summary>Where <see cref="FirstLabel"/> is anchored: the earliest dated frame's fraction.
    /// Computed here rather than read off <c>Ticks[0]</c> in <c>Render</c> so the label and its
    /// anchor cannot come from two different frames if a caller ever hands the frames over in
    /// something other than capture order.</summary>
    public double FirstFraction { get; }

    /// <summary>Where <see cref="LastLabel"/> is anchored: the latest dated frame's fraction.</summary>
    public double LastFraction { get; }

    /// <summary>"astronomical night", empty when <see cref="HasBand"/> is false.</summary>
    public string BandLabel { get; } = "";

    /// <summary>Raised by the control on a pointer press over a tick, carrying that tick's
    /// <see cref="NightTick.FrameIndex"/>. The host subscribes and selects the frame row.</summary>
    public event EventHandler<int>? FrameSelected;

    /// <summary>Raises <see cref="FrameSelected"/>. Public so the control, which holds no
    /// reference to the event, can raise it, and so a test can call it without synthesising a
    /// pointer.</summary>
    public void SelectFrame(int frameIndex) => FrameSelected?.Invoke(this, frameIndex);

    /// <summary>Raised as the pointer crosses ticks, carrying the tick's
    /// <see cref="NightTick.FrameIndex"/>, and with null when the pointer is between ticks or has
    /// left the strip (R9). The host highlights the frame row; it never changes the selection.
    /// </summary>
    public event EventHandler<int?>? FrameHovered;

    /// <summary>Raises <see cref="FrameHovered"/>. Public for the same reason
    /// <see cref="SelectFrame"/> is: the control holds no reference to the event, and a test must
    /// be able to call it without synthesising a pointer.</summary>
    public void HoverFrame(int? frameIndex) => FrameHovered?.Invoke(this, frameIndex);

    /// <summary>The active-frame index: the frame the host wants marked at
    /// rest, when the strip has no hover of its own. Set only through <see cref="SetActiveFrame"/>.
    /// The control reads this only when its own hovered index is null, so a hover always wins
    /// (spec 12.4's own ordering: "the tick under the pointer and, at rest, ...").</summary>
    public int? ActiveFrame { get; private set; }

    /// <summary>Raised by <see cref="SetActiveFrame"/> when <see cref="ActiveFrame"/> actually
    /// changes. The control subscribes to this to repaint; a raise on an unchanged value would
    /// repaint on every unrelated write of whatever the host is really watching.</summary>
    public event EventHandler? ActiveFrameChanged;

    /// <summary>Sets <see cref="ActiveFrame"/> and raises <see cref="ActiveFrameChanged"/>, but
    /// only when the value actually changes. <c>SessionCardViewModel.OnFrameTableChanged</c> calls
    /// this on every <c>PropertyChanged</c> of the frame table's selection, which fires on every
    /// unrelated write too, so the no-op guard is what keeps a repaint from following it.</summary>
    public void SetActiveFrame(int? frameIndex)
    {
        if (frameIndex == ActiveFrame)
        {
            return;
        }

        ActiveFrame = frameIndex;
        ActiveFrameChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Nearest tick to a fraction of the axis, or null when nothing is within
    /// <see cref="HitFraction"/>. The control's hit test, kept here so the rule is testable
    /// without a window.</summary>
    public NightTick? HitTest(double fraction)
    {
        if (!double.IsFinite(fraction))
        {
            return null;
        }

        NightTick? nearest = null;
        var nearestDistance = double.PositiveInfinity;

        foreach (var tick in Ticks)
        {
            var distance = Math.Abs(tick.Fraction - fraction);
            if (distance < nearestDistance)
            {
                nearest = tick;
                nearestDistance = distance;
            }
        }

        return nearestDistance <= HitFraction ? nearest : null;
    }

    private static ImmutableSolidColorBrush BrushFor(
        string? filterName,
        IReadOnlyDictionary<string, ImmutableSolidColorBrush> filterBrushes,
        ImmutableSolidColorBrush fallback)
        => !string.IsNullOrWhiteSpace(filterName) && filterBrushes.TryGetValue(filterName, out var brush)
            ? brush
            : fallback;

    private static string ToolTip(FrameRow frame, TimeZoneInfo zone, bool use24Hour)
    {
        var filter = string.IsNullOrWhiteSpace(frame.FilterUsed) ? MetricText.Missing : frame.FilterUsed;
        var time = SessionTimeFormat.Format(frame.CaptureDate, zone, use24Hour);
        var head = string.IsNullOrEmpty(time) ? filter : filter + " " + time;
        var hfr = MetricText.Format(frame.MedianHfr, "0.00");
        return string.IsNullOrEmpty(hfr) ? head : head + ", HFR " + hfr;
    }

    private static IReadOnlyList<AxisTick> BuildAxisTicks(
        DateTime start,
        DateTime end,
        double span,
        bool use24Hour)
    {
        if (span <= 0d)
        {
            return [];
        }

        var marks = new List<AxisTick>();

        // The first whole hour after the start, advanced to the next even hour, then every two
        // hours until the end. Strictly inside, so a domain shorter than the step yields none.
        var mark = CeilingToHour(start);
        if (mark <= start)
        {
            mark = mark.AddHours(1);
        }

        while (mark.Hour % AxisHourStep != 0)
        {
            mark = mark.AddHours(1);
        }

        for (; mark < end; mark = mark.AddHours(AxisHourStep))
        {
            marks.Add(new AxisTick(FractionOf(mark, start, span), Clock(mark, use24Hour)));
        }

        return marks;
    }

    private static double FractionOf(DateTime local, DateTime start, double span)
        => span <= 0d
            ? 0d
            : Math.Clamp((local - start).TotalMinutes / span, 0d, 1d);

    internal static DateTime ToLocal(DateTime captureUtc, TimeZoneInfo zone)
        => Unspecified(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(captureUtc, DateTimeKind.Utc),
            zone));

    private static DateTime Unspecified(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private static DateTime? Earlier(DateTime? left, DateTime? right)
        => left is { } a ? (right is { } b && b < a ? b : a) : right;

    private static DateTime? Later(DateTime? left, DateTime? right)
        => left is { } a ? (right is { } b && b > a ? b : a) : right;

    private static DateTime FloorToHour(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Unspecified);

    private static DateTime CeilingToHour(DateTime value)
    {
        var floor = FloorToHour(value);
        return floor == value ? floor : floor.AddHours(1);
    }

    /// <summary>A local wall-clock time as the axis renders it. <c>SessionTimeFormat.Format</c>
    /// is the path for anything that starts as a stored UTC instant; the axis marks and the band
    /// are already local, and converting them back to UTC to reuse it would throw on a local time
    /// a daylight-saving jump skipped.</summary>
    private static string Clock(DateTime local, bool use24Hour)
        => SessionTimeFormat.FormatLocal(local, use24Hour);
}
