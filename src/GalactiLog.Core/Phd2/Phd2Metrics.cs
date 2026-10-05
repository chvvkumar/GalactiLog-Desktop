using static GalactiLog.Core.Metrics.PythonNumerics;

namespace GalactiLog.Core.Phd2;

// Aggregation of parsed PHD2 guiding sections into storable session rows (spec 7.6, "Metrics").
// Port of backend/app/services/phd2_metrics.py. Pure functions over the parser's records: no file
// IO, no database, no settings object beyond the resolved zone and longitude handed in.
//
// Two conventions decide whether a reader trusts the number:
//
// 1. RMS excludes frames inside dither and settling windows. PHD2's own on-screen RMS excludes
//    them and a reader will put the two figures side by side. Without the exclusion every dithered
//    session reads several times worse than PHD2 said it was.
// 2. RMS is a population standard deviation (Python's statistics.pstdev), matching PHD2's own
//    definition of the figure it displays.

/// <summary>One guiding section reduced to the <c>phd2_sessions</c> row the parser can fill.
/// Mirrors <c>phd2_metrics.Phd2SessionMetrics</c>.</summary>
public sealed record Phd2SessionMetrics
{
    public DateTime StartedAtLocal { get; init; }

    // Null when no zone resolved (ruling F1). The Python falls back to the server's own zone; the
    // port never assumes the machine zone, because that answer looks correct on the developer's
    // machine and is silently wrong for every user in another zone.
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? EndedAtUtc { get; init; }
    public double DurationS { get; init; }
    public DateOnly? SessionDate { get; init; }

    public string? EquipmentProfile { get; init; }
    public double? PixelScaleArcsec { get; init; }
    public double? FocalLengthMm { get; init; }
    public string? GuideCamera { get; init; }
    public double? ExposureMs { get; init; }
    public string? MountName { get; init; }
    public string? DecGuideMode { get; init; }
    public string? AlgoRa { get; init; }
    public string? AlgoDec { get; init; }
    public double? MinMoveRa { get; init; }
    public double? MinMoveDec { get; init; }
    public double? AggressionRa { get; init; }
    public double? OrthoErrorDeg { get; init; }
    public string? LastCalIssue { get; init; }
    public string? PierSide { get; init; }
    public double? AltDeg { get; init; }
    public double? AzDeg { get; init; }
    public double? DecDeg { get; init; }
    public double? HourAngleHr { get; init; }

    public int FrameCount { get; init; }
    public int DropCount { get; init; }
    public int MaxDropRun { get; init; }
    public double UnguidedSeconds { get; init; }

    public double? RmsRaArcsec { get; init; }
    public double? RmsDecArcsec { get; init; }
    public double? RmsTotalArcsec { get; init; }
    public double? RmsRaFilteredArcsec { get; init; }
    public double? RmsDecFilteredArcsec { get; init; }
    public double? RmsTotalFilteredArcsec { get; init; }
    public double? PeakRaArcsec { get; init; }
    public double? PeakDecArcsec { get; init; }

    public double? SnrMean { get; init; }
    public double? SnrMin { get; init; }
    public double? StarMassMean { get; init; }

    public int PulseCountRaWest { get; init; }
    public int PulseCountRaEast { get; init; }
    public int PulseCountDecNorth { get; init; }
    public int PulseCountDecSouth { get; init; }
    public long PulseTotalMsRa { get; init; }
    public long PulseTotalMsDec { get; init; }

    public int DitherCount { get; init; }
    public int SettleCount { get; init; }
    public int SettleFailedCount { get; init; }
    public double? SettleMedianS { get; init; }

    public IReadOnlyDictionary<string, int> StarLostReasons { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyList<Phd2Event> Events { get; init; } = [];
    public bool Truncated { get; init; }

    // Spec 5.16 stores phd2_sessions.discarded_rows, which the web keeps on the parser record and
    // never stores. Carried here so Task 4 reads one record rather than reaching back into the
    // parser record beside it.
    public int DiscardedRows { get; init; }
}

/// <summary>One calibration block reduced to its stored row. Mirrors
/// <c>phd2_metrics.build_calibration_row</c>. Named so it cannot be mistaken for the parser's
/// <c>Phd2Calibration</c>.</summary>
public sealed record Phd2CalibrationMetrics
{
    public DateTime? StartedAtLocal { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateOnly? SessionDate { get; init; }
    public string? EquipmentProfile { get; init; }
    public double? PixelScaleArcsec { get; init; }
    public double? FocalLengthMm { get; init; }
    public string? GuideCamera { get; init; }
    public string? MountName { get; init; }
    public double? RaGuideSpeed { get; init; }
    public double? DecGuideSpeed { get; init; }
    public double? DecDeg { get; init; }
    public double? HourAngleHr { get; init; }
    public string? PierSide { get; init; }
    public double? AltDeg { get; init; }
    public double? AzDeg { get; init; }
    public double? WestAngleDeg { get; init; }
    public double? WestRatePxS { get; init; }
    public string? WestParity { get; init; }
    public double? NorthAngleDeg { get; init; }
    public double? NorthRatePxS { get; init; }
    public string? NorthParity { get; init; }
    public bool Completed { get; init; }
    public IReadOnlyList<Phd2CalibrationStep> Steps { get; init; } = [];
}

/// <summary>A night's, or a night and rig's, sessions rolled up into one summary. Mirrors
/// <c>phd2_metrics.aggregate_night</c>.</summary>
public sealed record Phd2NightSummary
{
    public int SessionCount { get; init; }
    public int GatedSessionCount { get; init; }
    public long FrameCount { get; init; }
    public double? RmsRaArcsec { get; init; }
    public double? RmsDecArcsec { get; init; }
    public double? RmsTotalArcsec { get; init; }
    public int DropCount { get; init; }
    public int MaxDropRun { get; init; }
    public double UnguidedSeconds { get; init; }
    public int DitherCount { get; init; }
    public int SettleFailedCount { get; init; }
    public double? SettleMedianS { get; init; }
    public IReadOnlyList<string> CalIssues { get; init; } = [];
    public IReadOnlyList<string> Profiles { get; init; } = [];
}

public static class Phd2Metrics
{
    /// <summary>Sessions shorter than this contribute their event counts, drop metrics and settle
    /// metrics to every rollup, and contribute no RMS: a 20-frame fragment around an autofocus run
    /// gives a standard deviation with no statistical meaning, and roughly 70 of the 800 sessions
    /// in the reference corpus are that short. The gate applies to the rollup, never to the row
    /// (spec 7.6).</summary>
    public const int MinFrames = 100;

    /// <summary>Excursion filter threshold, in session sigmas. One cable-snag spike can inflate a
    /// session RMS several-fold (2.6 arcsec with, 0.5 without, in the PHD2 analysis guide's worked
    /// example), so both figures are stored and neither replaces the other.</summary>
    public const double ExcursionSigma = 5.0;

    /// <summary>Interpret a naive PHD2 wall-clock timestamp in <paramref name="zone"/> and return
    /// UTC. A null zone gives null (ruling F1): the Python falls back to the server's own zone and
    /// the port refuses to guess.</summary>
    public static DateTime? LocalToUtc(DateTime naiveLocal, TimeZoneInfo? zone)
    {
        if (zone is null)
        {
            return null;
        }

        // The spring-forward gap: a wall-clock time that never happened. ConvertTimeToUtc throws
        // ArgumentException on it, which would take down a whole log's ingest on one March
        // session, while Python's fold=0 applies the offset in force BEFORE the transition.
        // Converting an hour earlier and adding the hour back applies that same offset.
        // Ceiling: a DST gap longer than one hour would need different arithmetic. No zone in the
        // current database has one.
        if (zone.IsInvalidTime(naiveLocal))
        {
            return TimeZoneInfo.ConvertTimeToUtc(naiveLocal.AddHours(-1), zone).AddHours(1);
        }

        // The fall-back overlap: a wall-clock time that happened twice. ConvertTimeToUtc assumes
        // standard time and therefore picks the SECOND occurrence, while Python's fold=0 takes the
        // first. A fall-back always runs daylight time to standard time, so the first occurrence
        // is the one with the larger UTC offset, north and south alike.
        if (zone.IsAmbiguousTime(naiveLocal))
        {
            var first = zone.GetAmbiguousTimeOffsets(naiveLocal).Max();
            return DateTime.SpecifyKind(naiveLocal - first, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(naiveLocal, zone);
    }

    /// <summary>The intervals to exclude from an RMS, from a parsed section.</summary>
    public static IReadOnlyList<(double Start, double End)> DitherSettleWindows(Phd2Section section)
        => WindowsFromPairs(
            section.Events.Select(e => (e.Type, e.TimeOffset)),
            section.Frames.Count > 0 ? section.Frames[^1].TimeOffset : null);

    /// <summary>The same rule over events read back from the stored <c>events</c> JSON. One
    /// caller in <c>src</c>: <c>Phd2Correlation.CorrelateOneNight</c>, which needs the windows a
    /// stored session excluded in order to compute one frame's RMS from the pooled samples
    /// (spec 7.6). <b>Not the guide graph</b>, which draws the web's presentation rule
    /// <c>settleWindows</c> in <c>GuideGraphViewModel.SettleWindows</c> and deliberately opens no
    /// window on a dither; a case there asserts this member's name is absent from that file.
    /// <para>
    /// One implementation, two entry points: two implementations of "which frames does an RMS
    /// exclude" would eventually disagree, at which point a per-frame figure and the session
    /// figure printed beside it stop describing the same frames.
    /// </para></summary>
    public static IReadOnlyList<(double Start, double End)> DitherSettleWindows(
        IEnumerable<(string Type, double TimeOffset)> events, double? lastTimeOffset)
        => WindowsFromPairs(events, lastTimeOffset);

    /// <summary>Reduce one parsed guiding section to its stored aggregate row.</summary>
    public static Phd2SessionMetrics ComputeSessionMetrics(
        Phd2Section section, TimeZoneInfo? zone, double? observerLongitude, bool useImagingNight)
    {
        var header = section.Header;
        var pixelScale = header.PixelScaleArcsec;
        var frames = section.Frames;

        // A section exists only because of a "Guiding Begins at" line, so its local start is always
        // set; the parser's field is nullable only because the record is folded together field by
        // field as the log is walked.
        var startedLocal = section.StartedAtLocal ?? default;
        var startedUtc = LocalToUtc(startedLocal, zone);
        var endedUtc = section.EndedAtLocal is { } endedLocal ? LocalToUtc(endedLocal, zone) : null;

        double duration;
        if (startedUtc is { } start && endedUtc is { } end)
        {
            duration = (end - start).TotalSeconds;
        }
        else if (startedUtc is null && section.EndedAtLocal is { } localEnd)
        {
            // A departure the Python never reaches: with no zone at all there is no UTC pair, and a
            // session still deserves a duration. The two differ only across a DST transition, which
            // is exactly what an unconfigured zone makes unknowable anyway. A 0.0 here would print
            // an empty session on the page.
            duration = (localEnd - startedLocal).TotalSeconds;
        }
        else if (frames.Count > 0)
        {
            duration = frames[^1].TimeOffset;
        }
        else
        {
            duration = 0.0;
        }

        var windows = DitherSettleWindows(section);
        var guided = frames.Where(f => !f.Dropped && !InWindows(f.TimeOffset, windows)).ToList();
        var raValues = guided.Where(f => f.RaRaw is not null).Select(f => f.RaRaw!.Value).ToList();
        var decValues = guided.Where(f => f.DecRaw is not null).Select(f => f.DecRaw!.Value).ToList();

        var rmsRa = ToArcsec(PopulationSigma(raValues), pixelScale);
        var rmsDec = ToArcsec(PopulationSigma(decValues), pixelScale);
        var rmsRaFiltered = ToArcsec(PopulationSigma(Filtered(raValues)), pixelScale);
        var rmsDecFiltered = ToArcsec(PopulationSigma(Filtered(decValues)), pixelScale);

        // Peaks come from the UNFILTERED guided values: the peak is the number that says a cable
        // snagged, and filtering it out would delete the finding the peak exists to report.
        var peakRa = ToArcsec(MaxAbsolute(raValues), pixelScale);
        var peakDec = ToArcsec(MaxAbsolute(decValues), pixelScale);

        var (dropCount, maxDropRun, unguided) = DropMetrics(frames);

        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var frame in frames.Where(f => f.Dropped))
        {
            var reason = frame.DropReason.Length > 0 ? frame.DropReason : "unknown";
            reasons[reason] = reasons.TryGetValue(reason, out var seen) ? seen + 1 : 1;
        }

        // SNR and star mass include DROP rows: PHD2 still reports them there, and a collapsing SNR
        // trend during a star-loss burst is the whole diagnostic.
        var snrValues = frames.Where(f => f.Snr is not null).Select(f => f.Snr!.Value).ToList();
        var massValues = frames.Where(f => f.StarMass is not null).Select(f => f.StarMass!.Value).ToList();

        // Pulses count every non-dropped frame including those inside a dither window: the
        // corrections issued to chase a dither are real mount commands.
        var pulsed = frames.Where(f => !f.Dropped).ToList();
        var (ditherCount, settleCount, settleFailed, settleMedian) = SettleMetrics(section.Events);

        return new Phd2SessionMetrics
        {
            StartedAtLocal = startedLocal,
            StartedAtUtc = startedUtc,
            EndedAtUtc = endedUtc,
            DurationS = RoundLikePython(duration, 3),
            SessionDate = Sessions.SessionDate.Compute(startedUtc, useImagingNight, observerLongitude),

            EquipmentProfile = header.EquipmentProfile,
            PixelScaleArcsec = pixelScale,
            FocalLengthMm = header.FocalLengthMm,
            GuideCamera = header.GuideCamera,
            ExposureMs = header.ExposureMs,
            MountName = header.MountName,
            DecGuideMode = header.DecGuideMode,
            AlgoRa = header.AlgoRa,
            AlgoDec = header.AlgoDec,
            MinMoveRa = header.MinMoveRa,
            MinMoveDec = header.MinMoveDec,
            AggressionRa = header.AggressionRa,
            OrthoErrorDeg = header.OrthoErrorDeg,
            LastCalIssue = header.LastCalIssue,
            PierSide = header.PierSide,
            AltDeg = header.AltDeg,
            AzDeg = header.AzDeg,
            DecDeg = header.DecDeg,
            HourAngleHr = header.HourAngleHr,

            FrameCount = frames.Count,
            DropCount = dropCount,
            MaxDropRun = maxDropRun,
            UnguidedSeconds = unguided,

            RmsRaArcsec = rmsRa,
            RmsDecArcsec = rmsDec,
            RmsTotalArcsec = Hypotenuse(rmsRa, rmsDec),
            RmsRaFilteredArcsec = rmsRaFiltered,
            RmsDecFilteredArcsec = rmsDecFiltered,
            RmsTotalFilteredArcsec = Hypotenuse(rmsRaFiltered, rmsDecFiltered),
            PeakRaArcsec = peakRa,
            PeakDecArcsec = peakDec,

            // ExactMean, not Average: phd2_metrics.py:369 and :371 call statistics.fmean, which
            // sums exactly. Enumerable.Average sums left to right, and the two are different
            // doubles often enough to part at the fourth decimal on real data.
            SnrMean = snrValues.Count > 0 ? RoundLikePython(ExactMean(snrValues), 4) : null,
            SnrMin = snrValues.Count > 0 ? snrValues.Min() : null,
            StarMassMean = massValues.Count > 0 ? RoundLikePython(ExactMean(massValues), 4) : null,

            PulseCountRaWest = pulsed.Count(f => string.Equals(f.RaDirection, "W", StringComparison.Ordinal)),
            PulseCountRaEast = pulsed.Count(f => string.Equals(f.RaDirection, "E", StringComparison.Ordinal)),
            PulseCountDecNorth = pulsed.Count(f => string.Equals(f.DecDirection, "N", StringComparison.Ordinal)),
            PulseCountDecSouth = pulsed.Count(f => string.Equals(f.DecDirection, "S", StringComparison.Ordinal)),
            PulseTotalMsRa = pulsed.Sum(f => (long)f.RaDurationMs),
            PulseTotalMsDec = pulsed.Sum(f => (long)f.DecDurationMs),

            DitherCount = ditherCount,
            SettleCount = settleCount,
            SettleFailedCount = settleFailed,
            SettleMedianS = settleMedian,

            StarLostReasons = reasons,
            // Copied, not aliased. section.Events is the parser's own mutable List, and storing
            // the instance would make this record's immutability a convention about who touches
            // the parser record rather than a property of the record itself.
            Events = [.. section.Events],
            Truncated = section.Truncated,
            DiscardedRows = section.DiscardedRows,
        };
    }

    /// <summary>Reduce one parsed calibration block to its stored row.</summary>
    public static Phd2CalibrationMetrics BuildCalibrationMetrics(
        Phd2Calibration calibration, TimeZoneInfo? zone, double? observerLongitude, bool useImagingNight)
    {
        var header = calibration.Header;
        var startedUtc = calibration.StartedAtLocal is { } startedLocal
            ? LocalToUtc(startedLocal, zone)
            : null;

        return new Phd2CalibrationMetrics
        {
            StartedAtLocal = calibration.StartedAtLocal,
            StartedAtUtc = startedUtc,
            SessionDate = Sessions.SessionDate.Compute(startedUtc, useImagingNight, observerLongitude),
            EquipmentProfile = header.EquipmentProfile,
            PixelScaleArcsec = header.PixelScaleArcsec,
            FocalLengthMm = header.FocalLengthMm,
            GuideCamera = header.GuideCamera,
            // The completion line's name first: it is the one PHD2 confirmed at completion, and the
            // header's is the fallback for a calibration that never got there.
            MountName = calibration.MountName ?? header.MountName,
            RaGuideSpeed = header.RaGuideSpeed,
            DecGuideSpeed = header.DecGuideSpeed,
            DecDeg = header.DecDeg,
            HourAngleHr = header.HourAngleHr,
            PierSide = header.PierSide,
            AltDeg = header.AltDeg,
            AzDeg = header.AzDeg,
            WestAngleDeg = calibration.WestAngleDeg,
            WestRatePxS = calibration.WestRatePxS,
            WestParity = calibration.WestParity,
            NorthAngleDeg = calibration.NorthAngleDeg,
            NorthRatePxS = calibration.NorthRatePxS,
            NorthParity = calibration.NorthParity,
            Completed = calibration.Completed,
            Steps = calibration.Steps.ToList(),
        };
    }

    /// <summary>Roll a night's sessions up into one summary. Event counts include every session,
    /// however short: a star-loss burst inside a 30-frame fragment is still a real star-loss burst.
    /// The RMS figures exclude sessions under <see cref="MinFrames"/>, and the count of those is
    /// reported so a screen can say why a number moved.</summary>
    public static Phd2NightSummary AggregateNight(IEnumerable<Phd2SessionMetrics> rows)
    {
        var all = rows.ToList();
        var settleMedians = all.Where(r => r.SettleMedianS is not null)
            .Select(r => r.SettleMedianS!.Value).ToList();

        var issues = all
            .Where(r => !string.IsNullOrEmpty(r.LastCalIssue)
                && !string.Equals(r.LastCalIssue, "none", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.LastCalIssue!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var profiles = all
            .Where(r => !string.IsNullOrEmpty(r.EquipmentProfile))
            .Select(r => r.EquipmentProfile!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new Phd2NightSummary
        {
            SessionCount = all.Count,
            GatedSessionCount = all.Count(r => r.FrameCount < MinFrames),
            FrameCount = all.Sum(r => (long)r.FrameCount),
            RmsRaArcsec = WeightedRms(all, r => r.RmsRaArcsec, r => r.FrameCount),
            RmsDecArcsec = WeightedRms(all, r => r.RmsDecArcsec, r => r.FrameCount),
            RmsTotalArcsec = WeightedRms(all, r => r.RmsTotalArcsec, r => r.FrameCount),
            DropCount = all.Sum(r => r.DropCount),
            MaxDropRun = all.Count > 0 ? all.Max(r => r.MaxDropRun) : 0,
            // CompensatedSum, not Sum: phd2_metrics.py:545 is a bare sum() over a float column,
            // and on the backend's Python that builtin is Neumaier-compensated. Enumerable.Sum is
            // not. DropMetrics below keeps its naive accumulation, because the Python it ports is
            // an explicit += loop and not sum().
            UnguidedSeconds = RoundLikePython(CompensatedSum(all.Select(r => r.UnguidedSeconds)), 3),
            DitherCount = all.Sum(r => r.DitherCount),
            SettleFailedCount = all.Sum(r => r.SettleFailedCount),
            // A median of medians, deliberately: the per-session durations are not stored, only
            // each session's own median, and this figure is a typical-settle indicator rather than
            // a statistic anyone integrates.
            SettleMedianS = settleMedians.Count > 0 ? RoundLikePython(Median(settleMedians), 3) : null,
            CalIssues = issues,
            Profiles = profiles,
        };
    }

    /// <summary>Pick the guiding sessions that belong to one night's rig. Port of
    /// <c>select_phd2_night_rows</c>, generic over the row type with two accessors rather than an
    /// interface: Task 5's entity and Phase 15B's DTO are different types and an interface with two
    /// implementations for two field reads buys nothing.
    /// <para>
    /// <paramref name="telescopes"/> holds every name that means this rig, the canonical one and
    /// all of its aliases, because neither side of the comparison can be assumed canonical: the
    /// stored telescope is whatever the user picked when they mapped the profile. A raw-string
    /// comparison missed a night shot on "SVBony SV503 80mm" whose profile was mapped to
    /// "SVBony 80ED".
    /// </para></summary>
    public static IReadOnlyList<T> SelectNightRows<T>(
        IEnumerable<T> rows, Func<T, string?> telescope, Func<T, string?> profile,
        IReadOnlySet<string> telescopes)
    {
        var all = rows.ToList();
        if (all.Count == 0)
        {
            return [];
        }

        var matched = all
            .Where(r => telescope(r) is { Length: > 0 } name && telescopes.Contains(name))
            .ToList();
        if (matched.Count > 0)
        {
            return matched;
        }

        // A profile mapped to a different rig is a decision the user already made, and overriding
        // it would put the guided rig's numbers on the other rig's card.
        if (all.Any(r => telescope(r) is { Length: > 0 }))
        {
            return [];
        }

        // An unmapped profile is the pre-configuration state and attributing it is a helpful guess.
        // Two unmapped profiles on one night attribute nothing: guessing which rig a target used
        // would attach wrong guiding numbers to real data, which is worse than showing none.
        // A null profile and an empty profile are the same profile: the PHD2 embedded in an ASIAIR
        // writes the Equipment Profile line with nothing but a trailing space, so one file gives
        // the empty string where another gives no line at all.
        var profiles = all.Select(r => profile(r) ?? "").Distinct(StringComparer.Ordinal).ToList();
        return profiles.Count == 1 ? all : [];
    }

    private static IReadOnlyList<(double Start, double End)> WindowsFromPairs(
        IEnumerable<(string Type, double TimeOffset)> events, double? lastTimeOffset)
    {
        var windows = new List<(double Start, double End)>();
        double? openAt = null;

        foreach (var (type, offset) in events)
        {
            if (string.Equals(type, Phd2EventTypes.Dither, StringComparison.Ordinal)
                || string.Equals(type, Phd2EventTypes.SettleStart, StringComparison.Ordinal))
            {
                // A dither followed by a settle start opens one window, not two.
                openAt ??= offset;
            }
            else if (string.Equals(type, Phd2EventTypes.SettleDone, StringComparison.Ordinal)
                || string.Equals(type, Phd2EventTypes.SettleFailed, StringComparison.Ordinal))
            {
                if (openAt is { } start)
                {
                    windows.Add((start, offset));
                    openAt = null;
                }
            }
        }

        // A window left open at the end closes at the last frame, because PHD2 was still settling
        // when the section ended, and never before its own start.
        if (openAt is { } stillOpen)
        {
            var end = lastTimeOffset ?? stillOpen;
            windows.Add((stillOpen, Math.Max(end, stillOpen)));
        }

        return windows;
    }

    /// <summary>
    /// Whether a frame at <paramref name="offset"/> falls inside one of the dither and settle
    /// windows <see cref="DitherSettleWindows(Phd2Section)"/> produced, and is therefore excluded
    /// from an RMS.
    /// </summary>
    /// <remarks>
    /// The lower bound is exclusive and the upper bound inclusive, because the parser stamps an
    /// untimestamped INFO line with the time of the PRECEDING CSV row. A frame at exactly the start
    /// was therefore written before the dither began and is a legitimately guided sample, while a
    /// frame at exactly the end was written while PHD2 was still settling.
    /// <para>
    /// Public because the per-frame correlation of spec 7.6 excludes the same frames from the same
    /// windows, and two implementations of "which frames does an RMS exclude" would eventually
    /// disagree, at which point a per-frame figure and the session figure printed beside it would
    /// stop describing the same frames. One rule, one membership test, two callers.
    /// </para>
    /// </remarks>
    public static bool InWindows(double offset, IReadOnlyList<(double Start, double End)> windows)
    {
        foreach (var (start, end) in windows)
        {
            if (start < offset && offset <= end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The population standard deviation of <paramref name="values"/>, or null when there are none.
    /// </summary>
    /// <remarks>
    /// Population, not sample: PHD2's own displayed figure is the population figure and users will
    /// put the two numbers side by side. Two-pass rather than E[x squared] minus mean squared,
    /// which loses precision on values that are large relative to their spread, which is exactly
    /// the excursion case.
    /// <para>
    /// Public for the same reason as <see cref="InWindows"/>: the per-frame correlation of spec 7.6
    /// takes the same sigma over the same kind of values, and spec 7.6 says in terms that the
    /// per-frame measurement is deliberately identical to the session one.
    /// </para>
    /// <para>
    /// The one place the port knowingly computes a different function from the Python, and the
    /// <c>Average</c> below is deliberate rather than the defect <see cref="ExactMean"/> fixed.
    /// CPython's
    /// <c>statistics.pstdev</c> is exact: <c>_ss</c> converts every value to a <c>Fraction</c> and
    /// takes the sum of squared deviations in exact rational arithmetic, rounding once at the end.
    /// This is two passes of ordinary double arithmetic. Measured rather than argued: over 4,000
    /// constructed samples of PHD2 shape (120 to 14,000 values, sigma 0.2 to 1.2 px) the worst
    /// relative disagreement with <c>pstdev</c> was 5.4e-15, about 24 units in the last place. An
    /// RMS near 0.65 arcsec therefore sits within about 3.5e-15 of the Python's answer, against a
    /// six-decimal rounding step of 1e-6, so a stored decimal moves only when the exact value
    /// lands within those few units of a midpoint. No such case exists in the user's corpus: all
    /// six RMS columns of all 937 real sessions compare EXACTLY with the Python. A
    /// rational-arithmetic port is thirty lines against a defect no input has shown, and swapping
    /// this one mean for <see cref="ExactMean"/> would move figures that agree today without
    /// making the whole computation exact, so both were declined.
    /// </para>
    /// </remarks>
    public static double? PopulationSigma(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var mean = values.Average();
        var sum = 0.0;
        foreach (var value in values)
        {
            var deviation = value - mean;
            sum += deviation * deviation;
        }

        return Math.Sqrt(sum / values.Count);
    }

    // One pass, never iterated to convergence: after one pass the remaining values have a smaller
    // sigma, and iterating would eventually keep nothing. The threshold is against ZERO, not
    // against the mean, because PHD2's guide error is a deviation from the lock position and zero
    // is its reference. A sigma of zero or null filters nothing: a zero-variance list has no
    // outliers and dividing by it would drop everything.
    private static IReadOnlyList<double> Filtered(IReadOnlyList<double> values)
    {
        var sigma = PopulationSigma(values);
        if (sigma is not { } spread || spread == 0.0)
        {
            return values;
        }

        var limit = ExcursionSigma * spread;
        return values.Where(v => Math.Abs(v) <= limit).ToList();
    }

    /// <summary>Convert a PHD2 pixel figure to arcseconds at <paramref name="pixelScale"/>, rounded
    /// to six decimals, null when either side is null. Port of <c>api/phd2.py:131</c>, which is the
    /// one rule for this conversion.
    /// <para>
    /// Public for the session metrics above and for <c>Phd2FramesQuery</c>, which converts each
    /// stored frame's pixel values at read time. A second multiply-and-round would make the session
    /// RMS and the guide graph's trace two functions of the same pixels, and the sixth decimal is
    /// where they would first disagree. Named for the direction and the unit, because a caller
    /// outside this file cannot see which way a bare "scale" runs.
    /// </para></summary>
    public static double? ToArcsec(double? pixels, double? pixelScale)
        => pixels is { } value && pixelScale is { } scale ? RoundLikePython(value * scale, 6) : null;

    // Over the already scaled and already rounded arcsecond values, then rounded again, exactly as
    // the Python's math.hypot call does. Taking it over the unrounded pixel values shows up in the
    // sixth decimal.
    private static double? Hypotenuse(double? ra, double? dec)
        => ra is { } a && dec is { } b ? RoundLikePython(double.Hypot(a, b), 6) : null;

    private static double? MaxAbsolute(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var peak = 0.0;
        foreach (var value in values)
        {
            peak = Math.Max(peak, Math.Abs(value));
        }

        return peak;
    }

    // Run length matters more than raw count: a star reappearing many minutes later is probably a
    // different star and the target is likely off-frame, so the elapsed unguided time is the number
    // worth surfacing.
    private static (int DropCount, int MaxDropRun, double UnguidedSeconds) DropMetrics(
        IReadOnlyList<Phd2Frame> frames)
    {
        var dropCount = 0;
        var maxRun = 0;
        var unguided = 0.0;
        int? runStartIndex = null;

        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].Dropped)
            {
                dropCount++;
                runStartIndex ??= i;
                continue;
            }

            if (runStartIndex is { } runStart)
            {
                maxRun = Math.Max(maxRun, i - runStart);
                var before = frames[runStart > 0 ? runStart - 1 : runStart];
                unguided += frames[i].TimeOffset - before.TimeOffset;
                runStartIndex = null;
            }
        }

        // A run still open at the end closes against the last frame. It is the run most likely to
        // be the one that cost the night.
        if (runStartIndex is { } openRun)
        {
            maxRun = Math.Max(maxRun, frames.Count - openRun);
            var before = frames[openRun > 0 ? openRun - 1 : openRun];
            unguided += frames[^1].TimeOffset - before.TimeOffset;
        }

        return (dropCount, maxRun, RoundLikePython(unguided, 3));
    }

    private static (int DitherCount, int SettleCount, int SettleFailedCount, double? SettleMedianS)
        SettleMetrics(IReadOnlyList<Phd2Event> events)
    {
        var dither = 0;
        var settleCount = 0;
        var failed = 0;
        double? started = null;
        var durations = new List<double>();

        foreach (var e in events)
        {
            if (string.Equals(e.Type, Phd2EventTypes.Dither, StringComparison.Ordinal))
            {
                dither++;
            }
            else if (string.Equals(e.Type, Phd2EventTypes.SettleStart, StringComparison.Ordinal))
            {
                started = e.TimeOffset;
            }
            else if (string.Equals(e.Type, Phd2EventTypes.SettleDone, StringComparison.Ordinal)
                || string.Equals(e.Type, Phd2EventTypes.SettleFailed, StringComparison.Ordinal))
            {
                settleCount++;
                if (string.Equals(e.Type, Phd2EventTypes.SettleFailed, StringComparison.Ordinal))
                {
                    failed++;
                }

                // The open start is cleared once used, so a stray completion adds to the count and
                // not to the durations: a duration measured from 0.0 would invent a settle the log
                // never timed.
                if (started is { } start)
                {
                    durations.Add(e.TimeOffset - start);
                    started = null;
                }
            }
        }

        return (dither, settleCount, failed,
            durations.Count > 0 ? RoundLikePython(Median(durations), 3) : null);
    }

    // The median of an even count is the mean of the two middle values, which is what Python's
    // statistics.median does.
    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>Frame-count weighted RMS over <paramref name="rows"/>, across the sessions long
    /// enough to mean anything, rounded to six decimals. Null when no row cleared
    /// <see cref="MinFrames"/> with a value. The comparison excludes below <see cref="MinFrames"/>,
    /// so exactly 100 frames is kept.
    /// <para>
    /// Generic over the row type with two accessors rather than an interface, the shape
    /// <see cref="SelectNightRows{T}"/> already uses one screen above and for the same reason: the
    /// night rollup's record and the Statistics page's stored row are different types and an
    /// interface for two field reads buys nothing. Public because
    /// <c>phd2_stats.py</c> delegates to <c>phd2_metrics._weighted_rms</c> for exactly the reason
    /// the port should, so the Statistics page and a session card never disagree about what a
    /// night's RMS was.
    /// </para>
    /// <para>
    /// Phase 17, ruling S11: the accumulation term stays <c>frames * figure * figure</c>, the same
    /// grouping this member has always used, so every Phase 15B figure stays identical to the bit.
    /// <see cref="WeightedRmsUnrounded{T}"/> is a SEPARATE entry point over the shared
    /// <see cref="WeightedRmsRatio{T}"/> core with a different term grouping, not a caller of this
    /// method: the two port different source expressions (<c>phd2_stats.py</c>'s own accumulation
    /// here, PostgreSQL's <c>sum(col * col * fc)</c> there) and double multiplication is not
    /// associative, so rounding one to get the other would move a figure neither source produces.
    /// </para></summary>
    public static double? WeightedRms<T>(
        IEnumerable<T> rows, Func<T, double?> value, Func<T, int> frameCount)
        => WeightedRmsRatio(rows, value, frameCount, static (figure, frames) => frames * figure * figure)
            is { } ratio
            ? RoundLikePython(Math.Sqrt(ratio), 6)
            : null;

    /// <summary>The Analysis page's frame-count weighted RMS (<c>analysis.py</c>'s
    /// <c>phd2_night_subquery</c>, lines 113 to 118), unrounded: the web's SQL never rounds this
    /// figure and consumes it unrounded in <c>_compute_trend</c>, <c>_pearson_r</c> and
    /// <c>_compute_summary_stats</c> (core-shapes.md section 2 item 5, ruling S1). Null under the
    /// same gate as <see cref="WeightedRms{T}"/>.</summary>
    /// <remarks>
    /// The term is <c>(figure * figure) * frames</c>, PostgreSQL's own grouping of
    /// <c>sum(col * col * fc)</c>, not <see cref="WeightedRms{T}"/>'s <c>frames * figure *
    /// figure</c>. Double multiplication is not associative, so the two groupings can and do
    /// disagree in the last bit on real magnitudes, and this port has no tolerance to spend:
    /// <c>AnalysisQuery</c> is the only caller and <see cref="WeightedRms{T}"/> keeps every other
    /// caller.
    /// </remarks>
    public static double? WeightedRmsUnrounded<T>(
        IEnumerable<T> rows, Func<T, double?> value, Func<T, int> frameCount)
        => WeightedRmsRatio(rows, value, frameCount, static (figure, frames) => (figure * figure) * frames)
            is { } ratio
            ? Math.Sqrt(ratio)
            : null;

    /// <summary>The one accumulation loop behind both <see cref="WeightedRms{T}"/> and
    /// <see cref="WeightedRmsUnrounded{T}"/>: the <see cref="MinFrames"/> gate, the denominator and
    /// the null-on-zero-denominator answer are shared: <paramref name="term"/> is the only thing
    /// that differs between the two callers. Answers the unrounded, un-square-rooted ratio, so a
    /// caller controls both the term grouping and whether the result is rounded.</summary>
    private static double? WeightedRmsRatio<T>(
        IEnumerable<T> rows, Func<T, double?> value, Func<T, int> frameCount,
        Func<double, int, double> term)
    {
        var numerator = 0.0;
        var denominator = 0L;

        foreach (var row in rows)
        {
            var frames = frameCount(row);
            if (value(row) is not { } figure || frames < MinFrames)
            {
                continue;
            }

            numerator += term(figure, frames);
            denominator += frames;
        }

        return denominator == 0 ? null : numerator / denominator;
    }
}
