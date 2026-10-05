using System.Globalization;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>One night's slice of a stitched axis (Phase 25 R4): its own strip domain mapped onto
/// a contiguous fraction range, the share proportional to the night's duration.</summary>
public sealed record LaneSegment(DateOnly Night, DateTime StartLocal, DateTime EndLocal, double FromFraction, double ToFraction);

/// <summary>A night's start on a stitched axis and the date label drawn there. The first one
/// sits at fraction 0 and carries its label and no line.</summary>
public sealed record LaneBoundary(double Fraction, string Label);

/// <summary>
/// One night's shared time axis: the timeline, the per-frame chart and the guide graph all
/// map a local time through it, so one instant sits at one x in all three lanes.
/// </summary>
/// <param name="StartLocal">The timeline's domain start, local wall clock.</param>
/// <param name="EndLocal">The timeline's domain end.</param>
/// <param name="PlotLeft">Pixels from a lane's left edge to its plot.</param>
/// <param name="PlotRight">Pixels from a lane's plot to its right edge.</param>
public sealed record NightLaneAxis(DateTime StartLocal, DateTime EndLocal, double PlotLeft, double PlotRight)
{
    /// <summary>The one plot left edge, the largest measured before the lanes shared an axis.
    /// </summary>
    public const double SharedPlotLeft = 76d;

    /// <summary>The one plot right edge.</summary>
    public const double SharedPlotRight = 52d;

    /// <summary>The timeline's hour marks as fractions of the axis; the chart and the guide graph
    /// draw their grid lines here and no labels of their own.</summary>
    public IReadOnlyList<double> TickFractions { get; init; } = [];

    /// <summary>The nights on this axis, oldest first, contiguous from 0 to 1. A single night is
    /// one segment over the whole axis.</summary>
    public IReadOnlyList<LaneSegment> Segments { get; init; } = [new(default, StartLocal, EndLocal, 0d, 1d)];

    /// <summary>One entry per segment at its start; empty on a single night.</summary>
    public IReadOnlyList<LaneBoundary> Boundaries { get; init; } = [];

    /// <summary>The axis over several nights' own axes, oldest first: shares by duration,
    /// contiguous from 0 to 1, every member's hour marks remapped, one boundary per night (R4).
    /// </summary>
    public static NightLaneAxis Stitched(IReadOnlyList<(DateOnly Night, NightLaneAxis Axis)> nights)
    {
        ArgumentNullException.ThrowIfNull(nights);
        if (nights.Count == 0)
        {
            throw new ArgumentException("A stitched axis needs at least one night.", nameof(nights));
        }

        var ordered = nights.OrderBy(night => night.Night).ToArray();
        var total = ordered.Sum(night => Minutes(night.Axis));
        var format = ordered[0].Night.Year != ordered[^1].Night.Year ? "yyyy-MM-dd" : "MM-dd";

        var segments = new List<LaneSegment>(ordered.Length);
        var boundaries = new List<LaneBoundary>(ordered.Length);
        var ticks = new List<double>();
        var from = 0d;
        for (var index = 0; index < ordered.Length; index++)
        {
            var (night, axis) = ordered[index];
            var to = index == ordered.Length - 1 ? 1d : from + (total <= 0d ? 0d : Minutes(axis) / total);
            segments.Add(new LaneSegment(night, axis.StartLocal, axis.EndLocal, from, to));
            boundaries.Add(new LaneBoundary(from, night.ToString(format, CultureInfo.InvariantCulture)));
            ticks.AddRange(axis.TickFractions.Select(tick => from + (tick * (to - from))));
            from = to;
        }

        return new NightLaneAxis(ordered[0].Axis.StartLocal, ordered[^1].Axis.EndLocal, SharedPlotLeft, SharedPlotRight)
        {
            TickFractions = ticks,
            Segments = segments,
            Boundaries = boundaries,
        };
    }

    /// <summary>0 at <see cref="StartLocal"/>, 1 at <see cref="EndLocal"/>, clamped, as the
    /// timeline has always placed its ticks. On a stitched axis a time outside every segment is
    /// clamped to the edge of the segment whose night is nearest by date.</summary>
    public double FractionOf(DateTime local)
    {
        var segment = SegmentFor(local);
        return Math.Clamp(FractionIn(segment, local), segment.FromFraction, segment.ToFraction);
    }

    /// <summary><see cref="FractionOf"/> unclamped: below 0 before the domain, above 1 after it.
    /// </summary>
    public double RawFractionOf(DateTime local) => FractionIn(SegmentFor(local), local);

    // The segment whose domain contains the time, else the one whose night is nearest by date.
    private LaneSegment SegmentFor(DateTime local)
    {
        var date = DateOnly.FromDateTime(local);
        LaneSegment? nearest = null;
        var nearestDays = int.MaxValue;
        foreach (var segment in Segments)
        {
            if (local >= segment.StartLocal && local <= segment.EndLocal)
            {
                return segment;
            }

            var days = Math.Abs(date.DayNumber - segment.Night.DayNumber);
            if (days < nearestDays)
            {
                nearest = segment;
                nearestDays = days;
            }
        }

        return nearest ?? new LaneSegment(default, StartLocal, EndLocal, 0d, 1d);
    }

    private static double FractionIn(LaneSegment segment, DateTime local)
    {
        var span = (segment.EndLocal - segment.StartLocal).TotalMinutes;
        var within = span <= 0d ? 0d : (local - segment.StartLocal).TotalMinutes / span;
        return segment.FromFraction + (within * (segment.ToFraction - segment.FromFraction));
    }

    private static double Minutes(NightLaneAxis axis) => Math.Max(0d, (axis.EndLocal - axis.StartLocal).TotalMinutes);
}
