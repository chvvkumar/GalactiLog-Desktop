using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Phd2;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>An inclusive window over one axis of the guide graph, elapsed seconds or arcseconds.
/// Port of <c>GuideView</c>, <c>frontend/src/utils/phd2Guide.ts:105-108</c>.</summary>
public readonly record struct GuideView(double Min, double Max);

/// <summary>The range a window is confined to. Port of <c>GuideBounds</c>,
/// <c>phd2Guide.ts:117-119</c>. A null <paramref name="MaxSpan"/> means the range is its own
/// limit, which is the time axis; the arcsecond axis sets it so it can be zoomed out past the
/// data.</summary>
public readonly record struct GuideBounds(double Min, double Max, double? MaxSpan = null);

/// <summary>One shaded settle band. Port of <c>SettleWindow</c>, <c>phd2Guide.ts:17-21</c>.
/// <paramref name="Failed"/> is what paints the band in the drop ink.</summary>
public readonly record struct SettleWindow(double Start, double End, bool Failed);

/// <summary>One axis mark: where it sits on its axis and what it prints.</summary>
public readonly record struct GuideTick(double Value, string Label);

/// <summary>The five drawn layers, in legend order (spec 12.4).</summary>
public enum GuideLayer
{
    Ra,
    Dec,
    StarLost,
    Dither,
    Settling,
}

/// <summary>One entry of the session selector.</summary>
public sealed record GuideSessionOption(Guid Id, string Label)
{
    /// <summary>The label, so a combo box with no item template prints it.</summary>
    public override string ToString() => Label;
}

/// <summary>One legend entry: a label and a toggle. A hidden entry's layer is not drawn at all.
/// Per visit and never persisted: it names which of tonight's layers the reader is looking at,
/// which is not a preference.</summary>
public sealed partial class GuideLegendEntry(GuideLayer layer, string label) : ObservableObject
{
    public GuideLayer Layer { get; } = layer;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;
}

/// <summary>
/// The guide graph's state and every rule behind it (spec 12.4): the two-window transform, the
/// presentation settle windows, the downsample, the session selector, the rig filter's label
/// list, the legend, the hover readout and the range caption. Port of
/// <c>frontend/src/utils/phd2Guide.ts</c> and the stateful half of
/// <c>frontend/src/components/Phd2GuideGraph.tsx</c> in <c>../GalactiLog</c> at <c>591234b</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ruling G1: <c>Controls/GuideGraph.cs</c> turns a pointer position into a data coordinate and
/// calls one member here. Every clamp, every span check and every ported figure lives in this
/// file, as pure static members a table-driven case drives with plain literals, because the
/// headless harness cannot raise a routed wheel event.
/// </para>
/// <para>
/// Two settle-window rules coexist in this solution on purpose. <see cref="SettleWindows"/> is the
/// presentation rule, which carries the failed flag and splits on a second start;
/// <c>Phd2Metrics.DitherSettleWindows</c> is the metric rule of spec 7.6, which decides which
/// frames an RMS counted. Spec 12.4: "the two are deliberately different questions".
/// </para>
/// </remarks>
public sealed partial class GuideGraphViewModel : ObservableObject
{
    /// <summary>One wheel notch inward; its reciprocal outward. <c>Phd2GuideGraph.tsx:65</c>,
    /// applied at <c>:522</c>.</summary>
    public const double ZoomStep = 0.82;

    /// <summary>Narrowest time window, seconds. <c>phd2Guide.ts:126</c>.</summary>
    public const double MinTimeSpan = 10;

    /// <summary>Narrowest arcsecond window, arcseconds of total span. <c>phd2Guide.ts:138</c>.
    /// </summary>
    public const double MinArcsecSpan = 0.1;

    /// <summary>Empty axis above and below the data at full zoom, as a fraction of the bound.
    /// <c>phd2Guide.ts:146</c>.</summary>
    public const double ArcsecHeadroom = 0.08;

    /// <summary>How far past its own full width the arcsecond axis zooms out, as a multiple.
    /// <c>phd2Guide.ts:155</c>.</summary>
    public const double ArcsecZoomOut = 10;

    /// <summary>The plotted point budget. <c>Phd2GuideGraph.tsx:63</c>. An integer because it is a
    /// count.</summary>
    public const int MaxPlotPoints = 2000;

    /// <summary>The rig filter's first entry, <c>Phd2GuideGraph.tsx:810</c>.</summary>
    public const string AllRigs = "All rigs";

    private const string Separator = " \u00B7 ";
    private const string ArcsecMark = "\u2033";
    private const int MaxTicks = 8;

    private readonly Func<Guid, Phd2SessionFrames?> _getFrames;
    private readonly TimeZoneInfo _zone;
    private readonly bool _use24Hour;
    private readonly Action<Action> _post;
    private readonly Func<double, string> _formatDuration;
    private readonly CancellationToken _lifetime;

    private int _generation;
    private Guid? _requestedId;
    private DateTime? _startedAtUtc;
    private IReadOnlyList<Phd2FramePoint> _frames = [];
    private IReadOnlyList<IReadOnlyList<Phd2FramePoint>> _sessionFrames = [];
    private IReadOnlyList<Phd2FramePoint>? _plotted;
    private bool _wholeNight;
    // Lazy, so two reads in flight for one session (a reload landing while the first read runs)
    // share one call of the query rather than each making their own.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Lazy<Phd2SessionFrames?>> _loadedFrames = new();
    private GuideView? _timeOverride;
    private GuideView? _arcsecOverride;

    /// <param name="sessions">The night's sessions, as <c>Phd2NightQuery</c> answered them.</param>
    /// <param name="getFrames">Normally <c>Phd2FramesQuery.Get</c>. Runs off the UI thread.</param>
    /// <param name="zone">The observer's zone, for the axis, the caption and the selector.</param>
    /// <param name="use24Hour">The display settings' clock.</param>
    /// <param name="post">The UI-thread post the loaded frames are published through.</param>
    /// <param name="formatDuration">The compact duration form. One implementation exists, on the
    /// Guiding section's view-model, and Task 4b binds it here; a second copy of
    /// <c>formatSecondsShort</c> is not written.</param>
    /// <param name="lifetime">The host section's own token. A graph dropped while a frames read is
    /// queued would otherwise still take that read and hold its rows alive past the card; the
    /// generation guard already drops the answer, so this only stops the work
    /// (<c>task4b-review.md</c> P3-11). Optional and trailing; the default never cancels.</param>
    /// <param name="wholeNight">Phase 24 R3: true loads every drawable session of the visible list
    /// and draws them on one time base, the earliest session's start, instead of the selected one.
    /// </param>
    public GuideGraphViewModel(
        IReadOnlyList<Phd2SessionSummary> sessions,
        Func<Guid, Phd2SessionFrames?> getFrames,
        TimeZoneInfo zone,
        bool use24Hour,
        Action<Action> post,
        Func<double, string> formatDuration,
        CancellationToken lifetime = default,
        bool wholeNight = false)
    {
        _lifetime = lifetime;
        _wholeNight = wholeNight;
        _getFrames = getFrames;
        _zone = zone;
        _use24Hour = use24Hour;
        _post = post;
        _formatDuration = formatDuration;

        Legend =
        [
            new GuideLegendEntry(GuideLayer.Ra, "RA"),
            new GuideLegendEntry(GuideLayer.Dec, "Dec"),
            new GuideLegendEntry(GuideLayer.StarLost, "Star lost"),
            new GuideLegendEntry(GuideLayer.Dither, "Dither"),
            new GuideLegendEntry(GuideLayer.Settling, "Settling"),
        ];

        foreach (var entry in Legend)
        {
            entry.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Legend));
        }

        SetSessions(sessions, null);
    }

    // ---------------------------------------------------------------- the pure transform

    /// <summary>Forces a window inside the range. Port of <c>clampGuideView</c>,
    /// <c>phd2Guide.ts:184-201</c>. The width is clamped first; a window wider than the range is
    /// centred on it; otherwise it is parked against the edge it ran past, width unchanged.
    /// </summary>
    public static GuideView ClampView(GuideView view, GuideBounds full, double minSpan)
    {
        var fullSpan = full.Max - full.Min;
        if (!(fullSpan > 0))
        {
            return new GuideView(full.Min, full.Max);
        }

        var ceiling = Math.Max(fullSpan, full.MaxSpan ?? fullSpan);
        var floor = Math.Min(minSpan, fullSpan);
        var span = Math.Min(ceiling, Math.Max(floor, view.Max - view.Min));
        if (span > fullSpan)
        {
            var centre = (full.Min + full.Max) / 2;
            return new GuideView(centre - (span / 2), centre + (span / 2));
        }

        var min = view.Min;
        if (min < full.Min)
        {
            min = full.Min;
        }

        if (min + span > full.Max)
        {
            min = full.Max - span;
        }

        return new GuideView(min, min + span);
    }

    /// <summary>True when a window is the range's own width, so an override holding it can be
    /// dropped. Port of <c>isFullGuideView</c>, <c>phd2Guide.ts:217-222</c>. Width alone, with a
    /// tolerance of <c>fullSpan * 1e-9</c>. A window wider than the range is deliberately not
    /// full: on the arcsecond axis that is a real zoomed-out state and is kept.</summary>
    public static bool IsFullView(GuideView view, GuideBounds full)
    {
        var fullSpan = full.Max - full.Min;
        if (!(fullSpan > 0))
        {
            return true;
        }

        return Math.Abs(view.Max - view.Min - fullSpan) <= fullSpan * 1e-9;
    }

    /// <summary>Scales the window about <paramref name="cursor"/>, keeping the value under the
    /// pointer under the pointer. Port of <c>zoomGuideView</c>, <c>phd2Guide.ts:228-241</c>. A
    /// factor below 1 zooms in.</summary>
    public static GuideView ZoomView(GuideView view, GuideBounds full, double cursor, double factor, double minSpan)
    {
        var span = view.Max - view.Min;
        if (!(span > 0))
        {
            return ClampView(view, full, minSpan);
        }

        var ratio = Math.Clamp((cursor - view.Min) / span, 0, 1);
        var next = span * factor;
        var min = cursor - (ratio * next);
        return ClampView(new GuideView(min, min + next), full, minSpan);
    }

    /// <summary>Slides the window by <paramref name="delta"/>. Port of <c>panGuideView</c>,
    /// <c>phd2Guide.ts:244-251</c>.</summary>
    public static GuideView PanView(GuideView view, GuideBounds full, double delta, double minSpan)
        => ClampView(new GuideView(view.Min + delta, view.Max + delta), full, minSpan);

    /// <summary>Half-height of the zero-centred arcsecond axis: the largest absolute finite RA or
    /// Dec value over the whole session, up to a tenth, never below
    /// <paramref name="minimum"/>. Port of <c>symmetricYBound</c>, <c>phd2Guide.ts:285-293</c>.
    /// A ceiling, not a parity rounding (G6).</summary>
    public static double SymmetricBound(IEnumerable<Phd2FramePoint> frames, double minimum = 1)
    {
        var max = 0d;
        foreach (var frame in frames)
        {
            if (frame.Ra is { } ra && double.IsFinite(ra))
            {
                max = Math.Max(max, Math.Abs(ra));
            }

            if (frame.Dec is { } dec && double.IsFinite(dec))
            {
                max = Math.Max(max, Math.Abs(dec));
            }
        }

        return max == 0 ? minimum : Math.Max(minimum, Math.Ceiling(max * 10) / 10);
    }

    /// <summary>The arcsecond axis at full zoom, which is also the view a reset returns to. Port
    /// of <c>guideYRange</c>, <c>phd2Guide.ts:166-169</c>.</summary>
    public static GuideBounds ArcsecRange(double bound)
    {
        var edge = bound * (1 + ArcsecHeadroom);
        return new GuideBounds(-edge, edge, 2 * edge * ArcsecZoomOut);
    }

    /// <summary>
    /// The presentation settle windows (spec 12.4). Port of <c>settleWindows</c>,
    /// <c>phd2Guide.ts:80-102</c>. Not <c>Phd2Metrics.DitherSettleWindows</c>: that is the metric
    /// rule, which a dither also opens, which swallows a second start, and which carries no
    /// failed flag.
    /// </summary>
    /// <param name="events">Any order; sorted here, stably, as the web sorts.</param>
    /// <param name="endFallback">Where a window still open at the end closes: the last frame's
    /// time, or 0 with no frame.</param>
    public static IReadOnlyList<SettleWindow> SettleWindows(IEnumerable<Phd2Event> events, double endFallback)
    {
        var windows = new List<SettleWindow>();
        double? open = null;

        foreach (var e in events.OrderBy(e => e.TimeOffset))
        {
            if (e.Type == Phd2EventTypes.SettleStart)
            {
                // Two starts in a row means the first never reported a result.
                if (open is { } unfinished)
                {
                    windows.Add(new SettleWindow(unfinished, e.TimeOffset, false));
                }

                open = e.TimeOffset;
            }
            else if (e.Type is Phd2EventTypes.SettleDone or Phd2EventTypes.SettleFailed)
            {
                if (open is not { } start)
                {
                    continue;
                }

                windows.Add(new SettleWindow(start, e.TimeOffset, e.Type == Phd2EventTypes.SettleFailed));
                open = null;
            }
        }

        if (open is { } dangling)
        {
            windows.Add(new SettleWindow(dangling, endFallback, false));
        }

        return windows;
    }

    /// <summary>
    /// Min and max preserving bucket downsample. Port of <c>downsampleGuideFrames</c>,
    /// <c>phd2Guide.ts:31-73</c>. Each bucket keeps its largest absolute RA, its largest absolute
    /// Dec and its first dropped frame, or its first frame when it has none of the three; both
    /// endpoints are kept. A plain stride would erase the excursions the graph exists to show.
    /// </summary>
    public static IReadOnlyList<Phd2FramePoint> Downsample(
        IReadOnlyList<Phd2FramePoint> frames, int maxPoints = MaxPlotPoints)
    {
        if (frames.Count <= maxPoints)
        {
            return frames;
        }

        var bucketCount = Math.Max(1, maxPoints / 3);
        var bucketSize = (double)frames.Count / bucketCount;
        var kept = new SortedSet<int>();

        for (var b = 0; b < bucketCount; b++)
        {
            var start = (int)Math.Floor(b * bucketSize);
            var end = Math.Min(frames.Count, (int)Math.Floor((b + 1) * bucketSize));
            if (end <= start)
            {
                continue;
            }

            int raIdx = -1, decIdx = -1, dropIdx = -1;
            double raBest = -1, decBest = -1;

            for (var i = start; i < end; i++)
            {
                var f = frames[i];
                if (f.Dropped && dropIdx == -1)
                {
                    dropIdx = i;
                }

                if (f.Ra is { } ra && double.IsFinite(ra) && Math.Abs(ra) > raBest)
                {
                    raBest = Math.Abs(ra);
                    raIdx = i;
                }

                if (f.Dec is { } dec && double.IsFinite(dec) && Math.Abs(dec) > decBest)
                {
                    decBest = Math.Abs(dec);
                    decIdx = i;
                }
            }

            if (raIdx >= 0)
            {
                kept.Add(raIdx);
            }

            if (decIdx >= 0)
            {
                kept.Add(decIdx);
            }

            if (dropIdx >= 0)
            {
                kept.Add(dropIdx);
            }

            if (raIdx < 0 && decIdx < 0 && dropIdx < 0)
            {
                kept.Add(start);
            }
        }

        kept.Add(0);
        kept.Add(frames.Count - 1);
        return [.. kept.Select(i => frames[i])];
    }

    /// <summary>The frames inside a time window plus one either side, so the plotted line reaches
    /// both edges. Port of <c>sliceFramesByTime</c>, <c>phd2Guide.ts:259-282</c>. Binary search,
    /// so a zoom step costs log n.</summary>
    public static IReadOnlyList<Phd2FramePoint> SliceByTime(IReadOnlyList<Phd2FramePoint> frames, double min, double max)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var start = Math.Max(0, LowerBound(frames, t => t < min) - 1);
        var end = Math.Min(frames.Count, LowerBound(frames, t => t <= max) + 1);
        return end > start ? [.. frames.Skip(start).Take(end - start)] : [];
    }

    // The first index whose time fails the predicate; the predicate is true on a prefix.
    private static int LowerBound(IReadOnlyList<Phd2FramePoint> frames, Func<double, bool> before)
    {
        var lo = 0;
        var hi = frames.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (before(frames[mid].T))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>The data value at a fraction of a window, 0 at <c>Min</c> and 1 at <c>Max</c>.
    /// The one conversion the control's handlers are allowed.</summary>
    public static double ValueAt(GuideView view, double fraction)
        => view.Min + (fraction * (view.Max - view.Min));

    /// <summary>The inverse of <see cref="ValueAt"/>. A zero-width window, which is a session of
    /// one frame, answers the middle rather than dividing by zero.</summary>
    public static double FractionOf(GuideView view, double value)
    {
        var span = view.Max - view.Min;
        return span > 0 ? (value - view.Min) / span : 0.5;
    }

    /// <summary>The mark positions for a window: multiples of a step chosen so at most
    /// <paramref name="maxTicks"/> fit. Presentation, not parity. <paramref name="clockSteps"/>
    /// picks the time axis's steps (1, 2, 5, 10, 15, 30 seconds, then minutes and hours the same
    /// way); otherwise the step is 1, 2 or 5 times a power of ten.</summary>
    public static IReadOnlyList<double> TickValues(GuideView view, int maxTicks, bool clockSteps)
    {
        var span = view.Max - view.Min;
        if (!(span > 0) || !double.IsFinite(span) || maxTicks < 1)
        {
            return [];
        }

        var raw = span / maxTicks;
        double step;
        if (clockSteps)
        {
            double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400];
            step = steps.FirstOrDefault(s => s >= raw, 86400 * Math.Ceiling(raw / 86400));
        }
        else
        {
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var unit = raw / magnitude;
            step = magnitude * (unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 5 ? 5 : 10);
        }

        var values = new List<double>();
        for (var k = Math.Ceiling(view.Min / step); k * step <= view.Max && values.Count <= maxTicks + 1; k++)
        {
            values.Add(k * step);
        }

        return values;
    }

    /// <summary>The rig a session belongs to: its mapped telescope, falling back to the raw
    /// profile name when the profile is unmapped. Port of <c>guideRigLabel</c>,
    /// <c>Phd2GuideGraph.tsx:81-83</c>.</summary>
    public static string RigLabel(Phd2SessionSummary session) => session.Telescope ?? session.EquipmentProfile;

    /// <summary>One selector label. Port of <c>sessionOptionLabel</c>,
    /// <c>Phd2GuideGraph.tsx:86-95</c>. The <c>short</c> marker reads
    /// <see cref="Phd2SessionSummary.Gated"/>, the one home of the <c>Phd2Metrics.MinFrames</c>
    /// gate.</summary>
    public static string SessionLabel(
        Phd2SessionSummary session, string startLabel, bool showRig, Func<double, string> formatDuration)
    {
        List<string> parts = [startLabel, formatDuration(session.DurationS)];
        if (showRig)
        {
            parts.Add(RigLabel(session));
        }

        if (session.Gated)
        {
            parts.Add("short");
        }

        return string.Join(Separator, parts);
    }

    /// <summary>The clock time <paramref name="seconds"/> into the session, or the elapsed
    /// duration when <paramref name="startedAtUtc"/> is null: spec 7.6's unzoned session has no
    /// origin, and an elapsed figure is preferred to a wrong clock time. Port of
    /// <c>guideTimeTick</c> and <c>guideTooltipTitle</c>, <c>Phd2GuideGraph.tsx:148-165</c>.
    /// </summary>
    public static string ClockAt(
        DateTime? startedAtUtc,
        double seconds,
        TimeZoneInfo zone,
        bool use24Hour,
        bool withSeconds,
        Func<double, string> formatDuration)
    {
        if (startedAtUtc is not { } start || !double.IsFinite(seconds))
        {
            return formatDuration(seconds);
        }

        var instant = start.AddSeconds(seconds);
        if (!withSeconds)
        {
            return SessionTimeFormat.Format(instant, zone, use24Hour);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(instant, DateTimeKind.Utc), zone);
        return SessionTimeFormat.FormatWithSeconds(local, use24Hour);
    }

    /// <summary>Two clock texts as a span; one time when they read alike.</summary>
    private static string ClockSpan(string from, string to) => from == to ? from : $"{from} to {to}";

    /// <summary>The line under the plot. Port of <c>guideRangeCaption</c>,
    /// <c>Phd2GuideGraph.tsx:168-179</c>, always in its slice form (spec 12.4 names no other).
    /// </summary>
    public static string RangeCaption(
        GuideView view,
        double sessionLength,
        DateTime? startedAtUtc,
        TimeZoneInfo zone,
        bool use24Hour,
        Func<double, string> formatDuration)
    {
        var from = ClockAt(startedAtUtc, view.Min, zone, use24Hour, false, formatDuration);
        var to = ClockAt(startedAtUtc, view.Max, zone, use24Hour, false, formatDuration);
        return $"Error in arcseconds, {ClockSpan(from, to)} of {formatDuration(sessionLength)}.";
    }

    // ---------------------------------------------------------------- sessions and the rig filter

    /// <summary>The selector's entries, in the night query's order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GuideSessionOption> SessionOptions { get; private set; } = [];

    /// <summary>The plotted session. A change clears both windows, which described the session
    /// the reader left, and loads the new frames; the previous plot stays until they land.
    /// </summary>
    [ObservableProperty]
    public partial GuideSessionOption? SelectedSession { get; set; }

    /// <summary>Offered only when the visible list holds more than one session.</summary>
    public bool ShowSessionSelector => SessionOptions.Count > 1;

    /// <summary><see cref="AllRigs"/> first, then each rig label once, ordinally sorted. Empty
    /// when the night carries one rig.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> RigOptions { get; private set; } = [];

    /// <summary>The filter's chosen entry, <see cref="AllRigs"/> at rest.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedRig))]
    public partial string SelectedRigOption { get; set; } = AllRigs;

    /// <summary>The chosen rig label, null for the whole night. This type holds the label and
    /// hands it back. The host answers a change by narrowing the night it already holds by
    /// <see cref="RigLabel"/> equality, as the web's <c>filterSessionsByProfile</c> does
    /// (<c>Phd2GuideGraph.tsx:111-117</c>), and calling <see cref="SetSessions"/>; no night query
    /// is issued for this filter (coordinator ruling e1, option A).
    /// </summary>
    public string? SelectedRig => SelectedRigOption == AllRigs ? null : SelectedRigOption;

    /// <summary>Offered only when the night carries more than one rig label.</summary>
    public bool ShowRigFilter => RigOptions.Count > 1;

    /// <summary>
    /// Hands over a night. <paramref name="night"/> is every session of the night and feeds the
    /// rig label list; <paramref name="visible"/> is that same night narrowed by the
    /// host to the sessions whose <see cref="RigLabel"/> equals <see cref="SelectedRig"/>, null
    /// meaning the whole night. Both arguments are required so that a caller cannot hand a
    /// narrowed list over as the night and collapse the rig list. A filter naming a rig the night
    /// does not carry clears itself rather than showing an empty panel
    /// (<c>Phd2GuideGraph.tsx:312-315</c>).
    /// </summary>
    public void SetSessions(IReadOnlyList<Phd2SessionSummary> night, IReadOnlyList<Phd2SessionSummary>? visible)
    {
        // Clearing a stale filter raises SelectedRig, which the host answers with another
        // SetSessions. That answer arrives half way through this body and is dropped: the clear
        // already falls back to the whole night.
        if (_settingSessions)
        {
            return;
        }

        _settingSessions = true;
        try
        {
            ApplySessions(night, visible);
        }
        finally
        {
            _settingSessions = false;
        }
    }

    private bool _settingSessions;

    // The picker's own figure, so the caption and the picker give one length.
    private Dictionary<Guid, double> _sessionLengths = [];

    // The length of the session whose frames are plotted, not of the one just selected.
    private double? _loadedLength;

    private void ApplySessions(IReadOnlyList<Phd2SessionSummary> night, IReadOnlyList<Phd2SessionSummary>? visible)
    {
        var rigs = night.Select(RigLabel).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        // The rig list is a pure function of the unfiltered night, which does not change while a
        // graph lives, so an equal list keeps its instance. Replacing it on every SetSessions is
        // what made the bound combo box clear its selection mid-narrowing and write the cleared
        // value back over the rig the reader had just chosen (fixer item 53).
        IReadOnlyList<string> next = rigs.Count > 1 ? [AllRigs, .. rigs] : [];
        if (!RigOptions.SequenceEqual(next, StringComparer.Ordinal))
        {
            RigOptions = next;
        }

        OnPropertyChanged(nameof(ShowRigFilter));

        if (SelectedRig is { } rig && !rigs.Contains(rig, StringComparer.Ordinal))
        {
            SelectedRigOption = AllRigs;
            visible = null;
        }

        _sessionLengths = night.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().DurationS);
        var list = visible ?? night;
        var mixed = list.Select(RigLabel).Distinct(StringComparer.Ordinal).Count() > 1;
        SessionOptions =
        [
            .. list.Select(s => new GuideSessionOption(
                s.Id,
                SessionLabel(s, SessionTimeFormat.Format(s.StartedAtUtc, _zone, _use24Hour), mixed, _formatDuration))),
        ];
        OnPropertyChanged(nameof(ShowSessionSelector));

        // The current session when the list still holds it, else the first that is not gated,
        // else the first: a night of short sessions is still plotted (Phd2GuideGraph.tsx:320-326).
        var keep = list.FirstOrDefault(s => s.Id == SelectedSession?.Id)
            ?? list.FirstOrDefault(s => !s.Gated)
            ?? list.FirstOrDefault();
        SelectedSession = keep is null ? null : SessionOptions.First(o => o.Id == keep.Id);
    }

    /// <summary>Whether every drawable session of the visible list is drawn on one time base
    /// rather than the selected one. Switching it reloads.</summary>
    public bool DrawsWholeNight
    {
        get => _wholeNight;
        set
        {
            if (_wholeNight == value)
            {
                return;
            }

            _wholeNight = value;
            Load(SelectedSession);
        }
    }

    partial void OnSelectedSessionChanged(GuideSessionOption? value)
    {
        // A relabelled option for the session already requested is not a new session.
        if (value?.Id == _requestedId)
        {
            return;
        }

        Load(value);
    }

    private void Load(GuideSessionOption? value)
    {
        _requestedId = value?.Id;
        _timeOverride = null;
        _arcsecOverride = null;
        var generation = ++_generation;

        // The previous plot stays until the next lands, but whole: its zoomed slice must not be
        // drawn against the full axis the cleared windows fall back to.
        if (HasFrames)
        {
            ViewChanged();
        }

        if (value is null)
        {
            Publish(generation, [], null);
            return;
        }

        IsLoading = true;
        Guid[] ids = _wholeNight ? [.. SessionOptions.Select(option => option.Id)] : [value.Id];
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    // A session read once is not read again for this graph's life: the whole-night
                    // switch lands after the first session's read whenever the night query beats
                    // the detail query, and the section drops the graph on a rescan anyway.
                    var frames = ids.Select(id => _loadedFrames.GetOrAdd(id, key => new Lazy<Phd2SessionFrames?>(() => _getFrames(key))).Value).ToList();
                    _post(() => Publish(generation, frames, null));
                }
                catch (OperationCanceledException)
                {
                    // The section was disposed or invalidated while the read was in flight.
                }
                catch (Exception ex)
                {
                    _post(() => Publish(generation, [], ex));
                }
            },
            _lifetime);
    }

    // ---------------------------------------------------------------- the loaded session

    /// <summary>The frames read in flight. Completes when the publication has been posted, not
    /// when it has run: a case that awaits it supplies a synchronous <c>post</c>, and production
    /// code does not await it.</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why the last frames read failed, null when it did not. The previous plot stays.
    /// </summary>
    [ObservableProperty]
    public partial Exception? LastFailure { get; private set; }

    /// <summary>The line under the plot, empty with no frame.</summary>
    [ObservableProperty]
    public partial string Caption { get; private set; } = "";

    /// <summary>The five legend entries: RA, Dec, Star lost, Dither, Settling.</summary>
    public IReadOnlyList<GuideLegendEntry> Legend { get; }

    // Runs on the UI thread through the post seam. A response older than the newest request is
    // dropped rather than overwriting it.
    private void Publish(int generation, IReadOnlyList<Phd2SessionFrames?> loaded, Exception? failure)
    {
        if (generation != _generation)
        {
            return;
        }

        IsLoading = false;
        LastFailure = failure;
        if (failure is not null)
        {
            return;
        }

        // The time base is the earliest zoned start. A session with no start instant (spec 7.6's
        // unzoned log) cannot be placed against another, so it draws only when it is the sole one.
        var sessions = loaded.OfType<Phd2SessionFrames>().ToList();
        DateTime? started = sessions.Min(session => session.StartedAtUtc);
        sessions = started is null ? [.. sessions.Take(1)] : [.. sessions.Where(session => session.StartedAtUtc is not null)];
        _loadedLength = !_wholeNight && _requestedId is { } id && _sessionLengths.TryGetValue(id, out var length) ? length : null;
        _startedAtUtc = started;

        List<IReadOnlyList<Phd2FramePoint>> perSession = [];
        List<SettleWindow> bands = [];
        List<double> dithers = [];
        foreach (var session in sessions)
        {
            var offset = started is { } origin && session.StartedAtUtc is { } at ? (at - origin).TotalSeconds : 0d;
            IReadOnlyList<Phd2FramePoint> frames = offset == 0d
                ? session.Frames
                : [.. session.Frames.Select(frame => frame with { T = frame.T + offset })];
            IReadOnlyList<Phd2Event> events = offset == 0d
                ? session.Events
                : [.. session.Events.Select(e => e with { TimeOffset = e.TimeOffset + offset })];
            if (frames.Count > 0)
            {
                perSession.Add(frames);
            }

            bands.AddRange(SettleWindows(events, frames.Count > 0 ? frames[^1].T : offset));
            dithers.AddRange(events.Where(e => e.Type == Phd2EventTypes.Dither).Select(e => e.TimeOffset));
        }

        _sessionFrames = perSession;
        _frames = perSession.Count == 1 ? perSession[0] : [.. perSession.SelectMany(frames => frames).OrderBy(frame => frame.T)];

        var last = _frames.Count > 0 ? _frames[^1].T : 0;
        TimeRange = _frames.Count > 0 ? new GuideBounds(_frames[0].T, last) : new GuideBounds(0, 0);
        ArcsecBounds = ArcsecRange(SymmetricBound(_frames));
        SettleBands = bands;
        DitherTimes = dithers;
        ViewChanged();
    }

    private NightLaneAxis? _laneAxis;
    private bool _apart;

    /// <summary>The night's shared time axis, set by the control that draws this graph.
    /// </summary>
    public NightLaneAxis? LaneAxis => _laneAxis;

    /// <summary>Opens the graph on the night's domain. A zoom or a drag still takes it off the
    /// shared axis and the reset brings it back.</summary>
    /// <param name="apart">True where the graph is not under the timeline: a session wholly
    /// outside the night then shows on its own span instead of an empty plot.</param>
    public void UseLaneAxis(NightLaneAxis? axis, bool apart = false)
    {
        // A reload hands a new but equal axis; only a changed domain or edge drops a held zoom.
        var same = apart == _apart && axis is not null && _laneAxis is not null
            && axis.StartLocal == _laneAxis.StartLocal && axis.EndLocal == _laneAxis.EndLocal
            && axis.PlotLeft == _laneAxis.PlotLeft && axis.PlotRight == _laneAxis.PlotRight;
        _laneAxis = axis;
        _apart = apart;
        if (same)
        {
            return;
        }

        _timeOverride = null;
        ViewChanged();
    }

    /// <summary>The night's domain in this session's elapsed seconds, or null without a lane axis
    /// or a start instant to measure from, or for a session outside the night on a graph that
    /// stands apart from the timeline.</summary>
    public GuideView? NightView => _apart && IsOutsideNight ? null : NightSpan;

    private GuideView? NightSpan => _laneAxis is { } axis && _startedAtUtc is { } started
        ? new GuideView(SecondsAt(axis.StartLocal, started, earlier: true), SecondsAt(axis.EndLocal, started))
        : null;

    // No sample of the loaded session falls inside the night's domain.
    private bool IsOutsideNight => HasFrames && NightSpan is { } night
        && (TimeRange.Max < night.Min || TimeRange.Min > night.Max);

    /// <summary>A local wall-clock time in the graph's clock form.</summary>
    public string ClockLabel(DateTime local) => SessionTimeFormat.FormatLocal(local, _use24Hour);

    /// <summary>True while the time window is the night's domain, so the grid follows the
    /// timeline's marks and draws no clock labels of its own.</summary>
    public bool IsOnLaneAxis => _timeOverride is null && NightView is not null;

    // The range the gestures clamp to: the night and the log together while the night is shown,
    // so guide data before an hour-floored start stays reachable; else the session.
    private GuideBounds TimeBounds => NightView is { } night
        ? new GuideBounds(Math.Min(night.Min, TimeRange.Min), Math.Max(night.Max, TimeRange.Max))
        : TimeRange;

    // A skipped wall-clock time takes the standard offset; a repeated one its later instant, or
    // its earlier one for a domain start so the whole repeated hour stays inside the window.
    private double SecondsAt(DateTime local, DateTime startedUtc, bool earlier = false)
    {
        var offset = earlier && _zone.IsAmbiguousTime(local)
            ? _zone.GetAmbiguousTimeOffsets(local).Max()
            : _zone.GetUtcOffset(local);
        return (local - offset - startedUtc).TotalSeconds;
    }

    /// <summary>The fraction of the time window an elapsed second sits at. On the shared axis it
    /// is the second's local clock time on the night's axis, as the strip and the chart place a
    /// frame; unclamped, so data past the domain falls outside the plot.</summary>
    public double TimeFractionOf(double seconds)
    {
        if (!IsOnLaneAxis)
        {
            return FractionOf(TimeView, seconds);
        }

        return _laneAxis!.RawFractionOf(NightStripViewModel.ToLocal(_startedAtUtc!.Value.AddSeconds(seconds), _zone));
    }

    /// <summary>The inverse of <see cref="TimeFractionOf"/>, for the pointer.</summary>
    public double TimeAt(double fraction)
    {
        if (!IsOnLaneAxis)
        {
            return ValueAt(TimeView, fraction);
        }

        var axis = _laneAxis!;
        var local = axis.StartLocal.AddMinutes(fraction * (axis.EndLocal - axis.StartLocal).TotalMinutes);
        return SecondsAt(local, _startedAtUtc!.Value);
    }

    /// <summary>True once a session with at least one frame has landed.</summary>
    public bool HasFrames => _frames.Count > 0;

    /// <summary>The whole session on the time axis, elapsed seconds.</summary>
    public GuideBounds TimeRange { get; private set; }

    /// <summary>The arcsecond axis at full zoom, from the whole session and never from the
    /// visible slice, so panning the time axis never makes the vertical scale jump.</summary>
    public GuideBounds ArcsecBounds { get; private set; } = ArcsecRange(1);

    /// <summary>The time window held while zoomed, null while it shows everything.</summary>
    public GuideView? TimeOverride => _timeOverride;

    /// <summary>The arcsecond window held while zoomed in or out, null at the data's own width.
    /// </summary>
    public GuideView? ArcsecOverride => _arcsecOverride;

    /// <summary>The time window <c>Render</c> applies.</summary>
    public GuideView TimeView => _timeOverride ?? NightView ?? new GuideView(TimeRange.Min, TimeRange.Max);

    /// <summary>The arcsecond window <c>Render</c> applies.</summary>
    public GuideView ArcsecView => _arcsecOverride ?? new GuideView(ArcsecBounds.Min, ArcsecBounds.Max);

    public IReadOnlyList<SettleWindow> SettleBands { get; private set; } = [];

    public IReadOnlyList<double> DitherTimes { get; private set; } = [];

    /// <summary>The frames to draw: the visible window's slice, downsampled. Over the window and
    /// not over the session, so zooming in resolves a spike rather than magnifying the bucket
    /// that survived a first pass.</summary>
    public IReadOnlyList<Phd2FramePoint> Plotted => _plotted ??= BuildPlotted();

    /// <summary>The frame between two sessions' traces: no value, so the trace ends at one
    /// session's last frame and begins again at the next's first.</summary>
    public static readonly Phd2FramePoint Break = new(0d, null, null, 0, "", 0, "", null, null, false);

    // One slice and downsample per session, because a bucket spanning two sessions would keep
    // one frame of each and join them across the gap.
    private IReadOnlyList<Phd2FramePoint> BuildPlotted()
    {
        if (_sessionFrames.Count <= 1)
        {
            return Downsample(_timeOverride is { } window ? SliceByTime(_frames, window.Min, window.Max) : _frames);
        }

        var plotted = new List<Phd2FramePoint>();
        foreach (var frames in _sessionFrames)
        {
            if (plotted.Count > 0)
            {
                plotted.Add(Break);
            }

            plotted.AddRange(Downsample(_timeOverride is { } window ? SliceByTime(frames, window.Min, window.Max) : frames));
        }

        return plotted;
    }

    public bool IsShown(GuideLayer layer) => Legend[(int)layer].IsShown;

    public IReadOnlyList<GuideTick> TimeTicks
        =>
        [
            .. TickValues(TimeView, MaxTicks, clockSteps: true).Select(v => new GuideTick(
                v,
                ClockAt(_startedAtUtc, v, _zone, _use24Hour, TimeView.Max - TimeView.Min < 60 * MaxTicks, _formatDuration))),
        ];

    public IReadOnlyList<GuideTick> ArcsecTicks
    {
        get
        {
            var values = TickValues(ArcsecView, MaxTicks, clockSteps: false);
            var format = values.Count > 1 && values[1] - values[0] < 0.1 ? "0.00" : "0.0";
            return [.. values.Select(v => new GuideTick(v, v.ToString(format, CultureInfo.InvariantCulture)))];
        }
    }

    // ---------------------------------------------------------------- the gestures

    /// <summary>Wheel. Positive notches zoom the time axis in about the cursor. A window back at
    /// full width drops the override.</summary>
    public void ZoomTime(double cursorSeconds, double notches)
        => Zoom(ref _timeOverride, TimeView, TimeBounds, cursorSeconds, notches, MinTimeSpan);

    /// <summary>Shift and wheel. Only a window back at the data's own width drops the override:
    /// wider than that is a zoomed-out reading of a calm night and is kept, which is
    /// <see cref="IsFullView"/> refusing a window wider than the range.</summary>
    public void ZoomArcsec(double cursorArcsec, double notches)
        => Zoom(ref _arcsecOverride, ArcsecView, ArcsecBounds, cursorArcsec, notches, MinArcsecSpan);

    // One notch is one ZoomStep; a zero delta (a tilt wheel, a sideways swipe) steps nothing.
    private void Zoom(
        ref GuideView? held, GuideView view, GuideBounds full, double cursor, double notches, double minSpan)
    {
        if (notches == 0 || !double.IsFinite(notches) || !double.IsFinite(cursor) || !(full.Max > full.Min))
        {
            return;
        }

        var next = ZoomView(view, full, cursor, Math.Pow(ZoomStep, notches), minSpan);
        GuideView? updated = IsFullView(next, full) ? null : next;

        // A notch that could not move the window (one more in at the minimum span, one more out
        // at full width) recomputes and repaints nothing.
        if (updated == held)
        {
            return;
        }

        held = updated;
        ViewChanged();
    }

    /// <summary>Left button drag. Pans whichever axis is zoomed; does nothing while both show
    /// everything, since there is no slack to take up (<c>Phd2GuideGraph.tsx:566-567</c>).
    /// </summary>
    public void Pan(double deltaSeconds, double deltaArcsec)
    {
        if (!double.IsFinite(deltaSeconds) || !double.IsFinite(deltaArcsec))
        {
            return;
        }

        // No slack to take up: nothing moves, so nothing is recomputed and nothing repaints. The
        // web never enters its pan path here (Phd2GuideGraph.tsx:566-567).
        if (_timeOverride is null && _arcsecOverride is null)
        {
            return;
        }

        if (_timeOverride is { } time)
        {
            _timeOverride = PanView(time, TimeBounds, deltaSeconds, MinTimeSpan);
        }

        if (_arcsecOverride is { } arcsec)
        {
            _arcsecOverride = PanView(arcsec, ArcsecBounds, deltaArcsec, MinArcsecSpan);
        }

        ViewChanged();
    }

    /// <summary>Double click. Both axes back to everything.</summary>
    public void ResetView()
    {
        _timeOverride = null;
        _arcsecOverride = null;
        ViewChanged();
    }

    /// <summary>The hover readout for a pointer at <paramref name="seconds"/>: the nearest frame
    /// on the time axis, its clock time to the second, then one line per visible trace, or
    /// "Star lost" alone for a dropped frame. Null with no frame.</summary>
    public string? HoverText(double seconds)
    {
        if (_frames.Count == 0 || !double.IsFinite(seconds))
        {
            return null;
        }

        var at = Math.Min(_frames.Count - 1, LowerBound(_frames, t => t < seconds));
        if (at > 0 && seconds - _frames[at - 1].T <= _frames[at].T - seconds)
        {
            at--;
        }

        var frame = _frames[at];
        List<string> lines = [ClockAt(_startedAtUtc, frame.T, _zone, _use24Hour, true, _formatDuration)];
        if (frame.Dropped)
        {
            if (IsShown(GuideLayer.StarLost))
            {
                lines.Add("Star lost");
            }
        }
        else
        {
            if (IsShown(GuideLayer.Ra) && frame.Ra is not null)
            {
                lines.Add("RA: " + MetricText.Format(frame.Ra, "0.00", ArcsecMark));
            }

            if (IsShown(GuideLayer.Dec) && frame.Dec is not null)
            {
                lines.Add("Dec: " + MetricText.Format(frame.Dec, "0.00", ArcsecMark));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void ViewChanged()
    {
        _plotted = null;
        Caption = !HasFrames
            ? ""
            : IsOnLaneAxis && IsOutsideNight
                ? "Guide session "
                    + ClockSpan(
                        ClockAt(_startedAtUtc, TimeRange.Min, _zone, _use24Hour, false, _formatDuration),
                        ClockAt(_startedAtUtc, TimeRange.Max, _zone, _use24Hour, false, _formatDuration))
                    + " lies outside the timeline, "
                    + $"{ClockLabel(_laneAxis!.StartLocal)} to {ClockLabel(_laneAxis.EndLocal)}."
                : RangeCaption(
                    TimeView, _loadedLength ?? TimeRange.Max, _startedAtUtc, _zone, _use24Hour, _formatDuration);
        OnPropertyChanged(nameof(TimeView));
    }
}
