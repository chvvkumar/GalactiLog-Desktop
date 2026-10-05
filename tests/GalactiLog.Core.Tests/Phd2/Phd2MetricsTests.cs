using System.Text.Json;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Phd2;
using Xunit;

namespace GalactiLog.Core.Tests.Phd2;

// Spec 7.6, "Metrics". Every expected figure below is the hand-derived number from task3.md
// sections 6.1 to 6.9, computed from the fixture's generating rule rather than from this port's
// own output. Each case names the wrong answer a broken implementation gives.
public class Phd2MetricsTests
{
    private static TimeZoneInfo Zone => Phd2MetricsFixtures.Zone;

    private static Phd2SessionMetrics Metrics(Phd2Section section)
        => Phd2Metrics.ComputeSessionMetrics(
            section, Zone, Phd2MetricsFixtures.ObserverLongitude, useImagingNight: true);

    // A separate helper rather than a defaulted parameter: a "zone ?? Zone" fallback would swallow
    // the explicit null these two cases exist to pass.
    private static Phd2SessionMetrics MetricsWithNoZone(Phd2Section section)
        => Phd2Metrics.ComputeSessionMetrics(
            section, null, Phd2MetricsFixtures.ObserverLongitude, useImagingNight: true);

    // ---- 6.1 LocalToUtc and the zone ----

    [Fact]
    public void LocalToUtc_UsesTheNamedZone()
    {
        // A Kind of Unspecified reaches a database column as a local instant.
        var utc = Phd2Metrics.LocalToUtc(new DateTime(2026, 7, 14, 21, 42, 27), Zone);

        Assert.Equal(new DateTime(2026, 7, 15, 1, 42, 27, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
    }

    [Fact]
    public void LocalToUtc_WithNoZone_IsNull()
    {
        // Ruling F1. The Python applies the machine's own zone here, which looks correct on the
        // developer's machine and is wrong for every user in another zone, with nothing on screen
        // that would ever say so.
        Assert.Null(Phd2Metrics.LocalToUtc(new DateTime(2026, 7, 14, 21, 42, 27), null));
    }

    [Fact]
    public void LocalToUtc_InTheSpringForwardGap_UsesThePreTransitionOffset()
    {
        // 06:30 UTC is the post-transition offset and disagrees with the web by an hour; a thrown
        // ArgumentException from ConvertTimeToUtc takes down a whole log's ingest on one March
        // session.
        var utc = Phd2Metrics.LocalToUtc(new DateTime(2026, 3, 8, 2, 30, 0), Zone);

        Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
    }

    [Fact]
    public void LocalToUtc_InTheFallBackOverlap_TakesTheFirstOccurrence()
    {
        // 06:30 UTC is what ConvertTimeToUtc returns unaided, because it assumes standard time,
        // and it is an hour late on every November session.
        var utc = Phd2Metrics.LocalToUtc(new DateTime(2026, 11, 1, 1, 30, 0), Zone);

        Assert.Equal(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
    }

    [Fact]
    public void AnUnresolvedZone_LeavesTheSessionStoredWithNoUtcStart()
    {
        // F1 requires the session to be stored and only the correlation to skip it, so a thrown
        // exception or a zero frame count is the failure.
        var metrics = MetricsWithNoZone(Phd2MetricsFixtures.Base());

        Assert.Null(metrics.StartedAtUtc);
        Assert.Null(metrics.EndedAtUtc);
        Assert.Null(metrics.SessionDate);
        Assert.Equal(200, metrics.FrameCount);
        Assert.Equal(2.0, metrics.RmsRaArcsec!.Value, 6);
        Assert.Equal(new DateTime(2026, 3, 1, 21, 0, 0), metrics.StartedAtLocal);
    }

    [Fact]
    public void AnUnresolvedZone_StillReportsADuration()
    {
        // 0.0 would print an empty session on the page.
        Assert.Equal(100.0, MetricsWithNoZone(Phd2MetricsFixtures.Base()).DurationS, 6);
    }

    // ---- 6.2 The window rule ----

    [Fact]
    public void AWindow_SpansTheDitherToTheSettleDone()
    {
        // Two windows means the settle start opened a second one while the dither's was open.
        var section = Phd2MetricsFixtures.EventsOnly(
            (Phd2EventTypes.Dither, 10.0),
            (Phd2EventTypes.SettleStart, 10.0),
            (Phd2EventTypes.SettleDone, 14.0));

        var window = Assert.Single(Phd2Metrics.DitherSettleWindows(section));

        Assert.Equal(10.0, window.Start, 6);
        Assert.Equal(14.0, window.End, 6);
    }

    [Fact]
    public void AnUnclosedWindow_RunsToTheLastFrame()
    {
        // (5.0, 5.0) excludes nothing, so a session PHD2 was still settling through reads as fully
        // guided.
        var section = Phd2MetricsFixtures.DropRun(10);
        section.Events.Add(new Phd2Event(Phd2EventTypes.SettleStart, 5.0, ""));

        var window = Assert.Single(Phd2Metrics.DitherSettleWindows(section));

        Assert.Equal(5.0, window.Start, 6);
        Assert.Equal(10.0, window.End, 6);
    }

    [Fact]
    public void AnUnclosedWindow_InASectionWithNoFrames_ClosesAtItsOwnStart()
    {
        // A failure is an exception from indexing an empty frame list.
        var section = Phd2MetricsFixtures.EventsOnly((Phd2EventTypes.SettleStart, 5.0));

        var window = Assert.Single(Phd2Metrics.DitherSettleWindows(section));

        Assert.Equal(5.0, window.Start, 6);
        Assert.Equal(5.0, window.End, 6);
    }

    [Fact]
    public void ASettleFailed_ClosesAWindowJustAsASettleDoneDoes()
    {
        // Zero windows means every frame of a failed settle counts toward the RMS.
        var section = Phd2MetricsFixtures.EventsOnly(
            (Phd2EventTypes.Dither, 10.0),
            (Phd2EventTypes.SettleFailed, 14.0));

        var window = Assert.Single(Phd2Metrics.DitherSettleWindows(section));

        Assert.Equal(10.0, window.Start, 6);
        Assert.Equal(14.0, window.End, 6);
    }

    [Fact]
    public void Boundary_KeepsTheFrameAtTheWindowStartAndExcludesTheFrameAtItsEnd()
    {
        // 7.0 is one of four distinct answers and each names a different defect. 3.0 means the
        // lower bound was made inclusive and the legitimately guided frame at t = 2.0 was thrown
        // away. 9.0 means the upper bound was made exclusive and a frame written while PHD2 was
        // still settling was counted. 50.0 means no exclusion happened at all.
        Assert.Equal(7.0, Metrics(Phd2MetricsFixtures.Boundary()).PeakRaArcsec!.Value, 6);
    }

    [Fact]
    public void Boundary_RmsOverTheThreeKeptFrames()
    {
        // The population standard deviation of 3.0, 7.0 and 3.0 at pixel scale 1.0. 1.154701 and
        // 2.309401 are the sample standard deviation under one of the two wrong denominators.
        Assert.Equal(1.885618, Metrics(Phd2MetricsFixtures.Boundary()).RmsRaArcsec!.Value, 6);
    }

    [Fact]
    public void Windows_RmsIgnoresTheTwentyExcludedFrames()
    {
        // About 18.0997 for RA is the figure with the twenty 30-pixel frames included, which is
        // several times worse than what PHD2 itself displayed the same night.
        var metrics = Metrics(Phd2MetricsFixtures.Windows());

        Assert.Equal(2.0, metrics.RmsRaArcsec!.Value, 6);
        Assert.Equal(4.0, metrics.RmsDecArcsec!.Value, 6);
        Assert.Equal(4.472136, metrics.RmsTotalArcsec!.Value, 6);
    }

    [Fact]
    public void Windows_PeakIsTakenFromTheGuidedFramesOnly()
        => Assert.Equal(2.0, Metrics(Phd2MetricsFixtures.Windows()).PeakRaArcsec!.Value, 6);

    [Fact]
    public void Windows_FrameCountStillCountsEveryRow()
    {
        // 180 would mean the exclusion removed rows rather than removing samples.
        Assert.Equal(200, Metrics(Phd2MetricsFixtures.Windows()).FrameCount);
    }

    [Fact]
    public void Windows_SettleMetrics()
    {
        // DitherCount 1 with SettleCount 0 means only settle_done was counted as a start.
        var metrics = Metrics(Phd2MetricsFixtures.Windows());

        Assert.Equal(1, metrics.DitherCount);
        Assert.Equal(1, metrics.SettleCount);
        Assert.Equal(0, metrics.SettleFailedCount);
        Assert.Equal(10.0, metrics.SettleMedianS!.Value, 6);
    }

    // The events as Task 4 stores them and Phase 15B reads them back, which is the shape the
    // stored overload exists for. Going through the serializer rather than through the parser
    // record is what makes the two cases below able to fail: the overloads share one private
    // implementation, so comparing them against each other can only ever pass, however wrong that
    // implementation is. A drift in Phd2Event's stored names turns GetProperty red here.
    private static IEnumerable<(string Type, double TimeOffset)> StoredEvents(Phd2Section section)
        => JsonSerializer.SerializeToElement(section.Events)
            .EnumerateArray()
            .Select(e => (e.GetProperty("type").GetString() ?? "", e.GetProperty("t").GetDouble()))
            .ToList();

    [Fact]
    public void TheStoredEventOverload_ReadsTheStoredShapeAndGivesTheWrittenWindow()
    {
        // Both overloads are asserted against a written window list, not against each other. A
        // divergence would mean a per-frame figure and the session figure printed beside it stop
        // describing the same frames. The fixture's lock_shift and param_change are the ignored
        // types: an implementation that opened a window on either would report a second window
        // here from t 70.0 to t 100.0.
        var section = Phd2MetricsFixtures.Windows();
        (double Start, double End)[] expected = [(50.0, 60.0)];

        Assert.Equal(expected, Phd2Metrics.DitherSettleWindows(section));
        Assert.Equal(expected, Phd2Metrics.DitherSettleWindows(
            StoredEvents(section), section.Frames[^1].TimeOffset));
    }

    [Fact]
    public void TheStoredEventOverload_ClosesAnUnclosedWindowAtTheLastFrameToo()
    {
        // The unclosed branch is the one that reads lastTimeOffset, which is the parameter the two
        // entry points supply differently. (5.0, 5.0) means it was ignored.
        var section = Phd2MetricsFixtures.DropRun(10);
        section.Events.Add(new Phd2Event(Phd2EventTypes.Dither, 5.0, ""));
        (double Start, double End)[] expected = [(5.0, 10.0)];

        Assert.Equal(expected, Phd2Metrics.DitherSettleWindows(section));
        Assert.Equal(expected, Phd2Metrics.DitherSettleWindows(StoredEvents(section), 10.0));
    }

    [Fact]
    public void AnIgnoredEventType_NeitherOpensNorClosesAWindow()
    {
        // lock_shift, param_change and star_lost are classified and must do nothing here. Treating
        // an unrecognised type as an opener over-excludes frames and reads a session as better
        // guided than it was, in the same direction as a broken excursion filter.
        var section = Phd2MetricsFixtures.EventsOnly(
            (Phd2EventTypes.LockShift, 1.0),
            (Phd2EventTypes.ParamChange, 2.0),
            (Phd2EventTypes.StarLost, 3.0));

        Assert.Empty(Phd2Metrics.DitherSettleWindows(section));
    }

    // ---- 6.3 Base200, every metric by hand ----

    [Fact]
    public void Base200_TimestampsAndDuration()
    {
        // 21:00 EST is UTC-5 on 1 March. A DurationS of 0.0 means the end timestamp was not
        // converted.
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal(new DateTime(2026, 3, 2, 2, 0, 0, DateTimeKind.Utc), metrics.StartedAtUtc);
        Assert.Equal(new DateTime(2026, 3, 2, 2, 1, 40, DateTimeKind.Utc), metrics.EndedAtUtc);
        Assert.Equal(100.0, metrics.DurationS, 6);
    }

    [Fact]
    public void Base200_SessionDate()
    {
        // The imaging-night offset is 12 - (-80/15) = 17 hours 20 minutes, and 02:00 UTC on
        // 2 March less 17:20 is 08:40 on 1 March. Reading 2026-03-02 means the shift was not
        // applied, which files every session under the wrong night.
        Assert.Equal(new DateOnly(2026, 3, 1), Metrics(Phd2MetricsFixtures.Base()).SessionDate);
    }

    [Fact]
    public void Base200_Rms()
    {
        // 2.005017 is the sample standard deviation: the denominator is 199, not 200, and PHD2
        // displays the population figure. 1.0 means the pixel scale was not applied and null means
        // it was read from the run rather than from the section's own header. A sixth-decimal
        // disagreement on the total means the hypotenuse was taken over the unrounded pixel values.
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal(2.0, metrics.RmsRaArcsec!.Value, 6);
        Assert.Equal(4.0, metrics.RmsDecArcsec!.Value, 6);
        Assert.Equal(4.472136, metrics.RmsTotalArcsec!.Value, 6);
    }

    [Fact]
    public void Base200_FilteredRms()
    {
        // The 5 sigma limits are 5.0 px and 10.0 px, so nothing is dropped on either axis.
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal(2.0, metrics.RmsRaFilteredArcsec!.Value, 6);
        Assert.Equal(4.0, metrics.RmsDecFilteredArcsec!.Value, 6);
        Assert.Equal(4.472136, metrics.RmsTotalFilteredArcsec!.Value, 6);
    }

    [Fact]
    public void Base200_Peaks()
    {
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal(2.0, metrics.PeakRaArcsec!.Value, 6);
        Assert.Equal(4.0, metrics.PeakDecArcsec!.Value, 6);
    }

    [Fact]
    public void Base200_DropMetrics()
    {
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal(0, metrics.DropCount);
        Assert.Equal(0, metrics.MaxDropRun);
        Assert.Equal(0.0, metrics.UnguidedSeconds, 6);
        Assert.Empty(metrics.StarLostReasons);
    }

    [Fact]
    public void Base200_PulseTallies()
    {
        // PulseTotalMsRa reading 0 means the duration field was read as nullable and the
        // null-coalesce to 0 was applied to the wrong side.
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal(100, metrics.PulseCountRaWest);
        Assert.Equal(0, metrics.PulseCountRaEast);
        Assert.Equal(50, metrics.PulseCountDecNorth);
        Assert.Equal(0, metrics.PulseCountDecSouth);
        Assert.Equal(10000L, metrics.PulseTotalMsRa);
        Assert.Equal(10000L, metrics.PulseTotalMsDec);
    }

    [Fact]
    public void Base200_SnrAndStarMass()
    {
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal(30.0, metrics.SnrMean!.Value, 6);
        Assert.Equal(30.0, metrics.SnrMin!.Value, 6);
        Assert.Equal(1700.0, metrics.StarMassMean!.Value, 6);
    }

    [Fact]
    public void Base200_SettleMetricsAreAllZeroWithNoEvents()
    {
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal(0, metrics.DitherCount);
        Assert.Equal(0, metrics.SettleCount);
        Assert.Equal(0, metrics.SettleFailedCount);
        Assert.Null(metrics.SettleMedianS);
        Assert.Empty(metrics.Events);
    }

    [Fact]
    public void TheEventsOnTheRecord_AreACopyNotTheParsersOwnList()
    {
        // Phd2SessionMetrics is an immutable record; aliasing the parser's mutable List would make
        // that a convention about who touches the parser record rather than a property of the
        // record. A failure here is the metrics record growing an event after it was built.
        var section = Phd2MetricsFixtures.Windows();
        var metrics = Metrics(section);
        var before = metrics.Events.Count;

        section.Events.Add(new Phd2Event(Phd2EventTypes.Dither, 90.0, ""));

        Assert.Equal(before, metrics.Events.Count);
    }

    [Fact]
    public void Base200_CountsEveryRowAndCarriesTheSectionFlags()
    {
        var section = Phd2MetricsFixtures.Base();
        section.Truncated = true;
        section.DiscardedRows = 7;

        var metrics = Metrics(section);

        Assert.Equal(200, metrics.FrameCount);
        Assert.True(metrics.Truncated);
        Assert.Equal(7, metrics.DiscardedRows);
    }

    [Fact]
    public void Base200_EchoesTheHeader()
    {
        var metrics = Metrics(Phd2MetricsFixtures.Base());

        Assert.Equal("Rig A", metrics.EquipmentProfile);
        Assert.Equal(2.0, metrics.PixelScaleArcsec!.Value, 6);
        Assert.Equal(784.0, metrics.FocalLengthMm!.Value, 6);
        Assert.Equal("ZWO ASI174MM Mini", metrics.GuideCamera);
        Assert.Equal(500.0, metrics.ExposureMs!.Value, 6);
        Assert.Equal("ASI Mount (ASCOM)", metrics.MountName);
        Assert.Equal("Auto", metrics.DecGuideMode);
        Assert.Equal("Hysteresis", metrics.AlgoRa);
        Assert.Equal("Resist Switch", metrics.AlgoDec);
        Assert.Equal(0.25, metrics.MinMoveRa!.Value, 6);
        Assert.Equal(0.25, metrics.MinMoveDec!.Value, 6);
        Assert.Equal(0.7, metrics.AggressionRa!.Value, 6);
        Assert.Equal(11.8, metrics.OrthoErrorDeg!.Value, 6);
        Assert.Equal("None", metrics.LastCalIssue);
        Assert.Equal("West", metrics.PierSide);
        Assert.Equal(43.7, metrics.AltDeg!.Value, 6);
        Assert.Equal(70.4, metrics.AzDeg!.Value, 6);
        Assert.Equal(38.5, metrics.DecDeg!.Value, 6);
        Assert.Equal(-4.02, metrics.HourAngleHr!.Value, 6);
    }

    [Fact]
    public void ASectionWithNoPixelScale_StoresACountAndNoArcsecondFigure()
    {
        var section = Phd2MetricsFixtures.Base();
        section.Header.PixelScaleArcsec = null;

        var metrics = Metrics(section);

        Assert.Equal(200, metrics.FrameCount);
        Assert.Null(metrics.RmsRaArcsec);
        Assert.Null(metrics.RmsTotalArcsec);
        Assert.Null(metrics.PeakRaArcsec);
    }

    // ---- 6.4 The 5 sigma excursion filter ----

    [Fact]
    public void Excursion_UnfilteredRms()
    {
        // 100 values of +1, 99 of -1 and one of -21. The mean is -0.1, the sum of squared
        // deviations is 638.0, the variance is 3.19 and sigma is 1.78605711 px, times 2.0.
        Assert.Equal(3.572114, Metrics(Phd2MetricsFixtures.Excursion()).RmsRaArcsec!.Value, 6);
    }

    [Fact]
    public void Excursion_FilteredRmsDropsTheSingleExcursion()
    {
        // The limit is 8.93028555 px, so the -21 goes and the other 199 stay. Equal to the
        // unfiltered figure means the filter dropped nothing: either the threshold was taken
        // against the mean rather than against zero, or the comparison is the wrong way round. Near
        // zero means the filter ran more than once, and iterating would eventually keep nothing.
        Assert.Equal(1.999975, Metrics(Phd2MetricsFixtures.Excursion()).RmsRaFilteredArcsec!.Value, 6);
    }

    [Fact]
    public void Excursion_LeavesTheDecAxisUntouched()
    {
        var metrics = Metrics(Phd2MetricsFixtures.Excursion());

        Assert.Equal(4.0, metrics.RmsDecArcsec!.Value, 6);
        Assert.Equal(4.0, metrics.RmsDecFilteredArcsec!.Value, 6);
    }

    [Fact]
    public void Excursion_TotalsAreTakenOverTheRoundedArcsecondValues()
    {
        // The hypotenuse of the rounded 3.572114 and 4.0, and of the rounded 1.999975 and 4.0,
        // each rounded again. A sixth-decimal disagreement means the hypotenuse was taken over the
        // unrounded pixel values; that is the defect, not the expectation.
        var metrics = Metrics(Phd2MetricsFixtures.Excursion());

        Assert.Equal(5.362835, metrics.RmsTotalArcsec!.Value, 6);
        Assert.Equal(4.472125, metrics.RmsTotalFilteredArcsec!.Value, 6);
    }

    [Fact]
    public void Excursion_PeakIsTakenOverTheUnfilteredValues()
    {
        // 2.0 means the peak was taken over the filtered values, which deletes the exact finding
        // the peak exists to report.
        Assert.Equal(42.0, Metrics(Phd2MetricsFixtures.Excursion()).PeakRaArcsec!.Value, 6);
    }

    // ---- 6.5 Drops ----

    [Fact]
    public void Drops_CountsRunsAndUnguidedTime()
    {
        // First run: the frame before it is 50 at t 25.0 and the frame that ends it is 54 at
        // t 27.0, so 2.0 s. Second run: frame 101 at t 50.5 to frame 103 at t 51.5, so 1.0 s.
        // 2.0 total means the frame before the run was taken as the run's own first frame, which
        // under-reports every gap by one exposure. A MaxDropRun of 4 means the run length was
        // computed as index - runStart + 1.
        var metrics = Metrics(Phd2MetricsFixtures.Drops());

        Assert.Equal(200, metrics.FrameCount);
        Assert.Equal(4, metrics.DropCount);
        Assert.Equal(3, metrics.MaxDropRun);
        Assert.Equal(3.0, metrics.UnguidedSeconds, 6);
    }

    [Fact]
    public void Drops_StarLostReasons()
    {
        var reasons = Metrics(Phd2MetricsFixtures.Drops()).StarLostReasons;

        Assert.Equal(3, Assert.Contains("Star lost - mass changed", reasons));
        Assert.Equal(1, Assert.Contains("Star lost - low SNR", reasons));
        Assert.Equal(2, reasons.Count);
    }

    [Fact]
    public void ADropRowWithNoReason_IsCountedAsUnknown()
    {
        var section = Phd2MetricsFixtures.Base();
        section.Frames[0] = section.Frames[0] with { Dropped = true, RaRaw = null, DecRaw = null, DropReason = "" };

        Assert.Equal(1, Assert.Contains("unknown", Metrics(section).StarLostReasons));
    }

    [Fact]
    public void Drops_RmsSkipsANullRatherThanReadingItAsZero()
    {
        // 1.979899 means a null RaRaw was read as 0.0, which adds four artificial zero-error
        // samples.
        var metrics = Metrics(Phd2MetricsFixtures.Drops());

        Assert.Equal(2.0, metrics.RmsRaArcsec!.Value, 6);
        Assert.Equal(4.0, metrics.RmsDecArcsec!.Value, 6);
        Assert.Equal(4.472136, metrics.RmsTotalArcsec!.Value, 6);
    }

    [Fact]
    public void Drops_SnrAndStarMassIncludeDropRows()
    {
        // A SnrMin of 30.0 and a SnrMean of 30.0 together mean DROP rows were excluded, and the
        // collapsing-SNR diagnostic disappears.
        var metrics = Metrics(Phd2MetricsFixtures.Drops());

        Assert.Equal(29.6, metrics.SnrMean!.Value, 6);
        Assert.Equal(10.0, metrics.SnrMin!.Value, 6);
        Assert.Equal(1700.0, metrics.StarMassMean!.Value, 6);
    }

    [Fact]
    public void Drops_PulseTalliesExcludeDroppedFrames()
    {
        // 100 here means dropped frames were counted as pulses. Frame 52 was the one dropped frame
        // that carried a Dec pulse.
        var metrics = Metrics(Phd2MetricsFixtures.Drops());

        Assert.Equal(98, metrics.PulseCountRaWest);
        Assert.Equal(9800L, metrics.PulseTotalMsRa);
        Assert.Equal(49, metrics.PulseCountDecNorth);
        Assert.Equal(9800L, metrics.PulseTotalMsDec);
    }

    [Fact]
    public void ALeadingDropRun_MeasuresFromItsOwnFirstFrame()
    {
        // A failure is an index-out-of-range exception from reaching for the frame before index 0,
        // or a negative figure.
        var metrics = Metrics(Phd2MetricsFixtures.DropRun(4, 1, 2));

        Assert.Equal(2, metrics.MaxDropRun);
        Assert.Equal(2.0, metrics.UnguidedSeconds, 6);
    }

    [Fact]
    public void ATrailingDropRun_ClosesAgainstTheLastFrame()
    {
        // MaxDropRun 0 with UnguidedSeconds 0.0 is the run that never closes because the loop
        // ended, and it is the run most likely to be the one that cost the night.
        var metrics = Metrics(Phd2MetricsFixtures.DropRun(4, 3, 4));

        Assert.Equal(2, metrics.MaxDropRun);
        Assert.Equal(2.0, metrics.UnguidedSeconds, 6);
    }

    // ---- 6.6 Settle metrics ----

    [Fact]
    public void SettleMetrics_CountAFailureAndTakeTheEvenCountMedian()
    {
        // SettleCount 1 means a failed settle was not counted as a settle. A median of 2.0 or 6.0
        // means the even-count median took a middle element instead of the mean of the two. A null
        // median means the open start was cleared before it was used.
        var section = Phd2MetricsFixtures.EventsOnly(
            (Phd2EventTypes.Dither, 1.0),
            (Phd2EventTypes.SettleStart, 1.0),
            (Phd2EventTypes.SettleDone, 3.0),
            (Phd2EventTypes.Dither, 5.0),
            (Phd2EventTypes.SettleStart, 5.0),
            (Phd2EventTypes.SettleFailed, 11.0));

        var metrics = Metrics(section);

        Assert.Equal(2, metrics.DitherCount);
        Assert.Equal(2, metrics.SettleCount);
        Assert.Equal(1, metrics.SettleFailedCount);
        Assert.Equal(4.0, metrics.SettleMedianS!.Value, 6);
    }

    [Fact]
    public void ASettleDoneWithNoStart_CountsButRecordsNoDuration()
    {
        // A failure records a duration measured from 0.0, which invents a settle the log never
        // timed.
        var metrics = Metrics(Phd2MetricsFixtures.EventsOnly((Phd2EventTypes.SettleDone, 9.0)));

        Assert.Equal(1, metrics.SettleCount);
        Assert.Null(metrics.SettleMedianS);
    }

    // ---- 6.7 Gating and the night rollup ----

    private static Phd2SessionMetrics NightRow(
        int frameCount, double? rms, string? profile = "P1", string? calIssue = null) => new()
    {
        FrameCount = frameCount,
        RmsRaArcsec = rms,
        RmsDecArcsec = rms,
        RmsTotalArcsec = rms is { } value ? Math.Round(double.Hypot(value, value), 6) : null,
        EquipmentProfile = profile,
        LastCalIssue = calIssue,
        DropCount = 2,
        MaxDropRun = 1,
        UnguidedSeconds = 3.0,
        DitherCount = 1,
        SettleFailedCount = 1,
        SettleMedianS = 4.0,
    };

    [Fact]
    public void TheNightRollup_WeightsByFrameCountAndGatesTheShortSession()
    {
        // About 5.2345 means the 99-frame row was included, which is the number a night reads as
        // when one short fragment around an autofocus run dominates the average, five times the
        // truth. A DropCount of 4 means the gate was applied to the event counts too. CalIssues
        // containing None means the literal was compared case-sensitively.
        var summary = Phd2Metrics.AggregateNight(
        [
            NightRow(99, 9.0, "P1", "Orthogonality"),
            NightRow(100, 1.0, "P1"),
            NightRow(101, 1.0, "P2", "None"),
        ]);

        Assert.Equal(3, summary.SessionCount);
        Assert.Equal(1, summary.GatedSessionCount);
        Assert.Equal(300L, summary.FrameCount);
        Assert.Equal(1.0, summary.RmsRaArcsec!.Value, 6);
        Assert.Equal(1.0, summary.RmsDecArcsec!.Value, 6);
        Assert.Equal(1.414214, summary.RmsTotalArcsec!.Value, 6);
        Assert.Equal(6, summary.DropCount);
        Assert.Equal(1, summary.MaxDropRun);
        Assert.Equal(9.0, summary.UnguidedSeconds, 6);
        Assert.Equal(3, summary.DitherCount);
        Assert.Equal(3, summary.SettleFailedCount);
        Assert.Equal(4.0, summary.SettleMedianS!.Value, 6);
        Assert.Equal(new[] { "Orthogonality" }, summary.CalIssues);
        Assert.Equal(new[] { "P1", "P2" }, summary.Profiles);
    }

    [Fact]
    public void NinetyNineFrames_AreGatedOutOfTheAverage()
    {
        // 1.0 here means the gate is "< 99".
        var summary = Phd2Metrics.AggregateNight([NightRow(99, 1.0)]);

        Assert.Null(summary.RmsRaArcsec);
        Assert.Equal(1, summary.GatedSessionCount);
        Assert.Equal(99L, summary.FrameCount);
    }

    [Fact]
    public void ExactlyOneHundredFrames_AreKept()
    {
        // Null here means the gate is "<= MinFrames" and is off by one at the exact boundary the
        // constant names.
        var summary = Phd2Metrics.AggregateNight([NightRow(100, 1.0)]);

        Assert.Equal(1.0, summary.RmsRaArcsec!.Value, 6);
        Assert.Equal(0, summary.GatedSessionCount);
    }

    [Fact]
    public void OneHundredAndOneFrames_AreKept()
    {
        var summary = Phd2Metrics.AggregateNight([NightRow(101, 1.0)]);

        Assert.Equal(1.0, summary.RmsRaArcsec!.Value, 6);
        Assert.Equal(0, summary.GatedSessionCount);
    }

    [Fact]
    public void ANightOfOnlyGatedSessions_ReportsNoRms()
    {
        // 0.0 rather than null is what a UI prints as perfect guiding.
        var summary = Phd2Metrics.AggregateNight([NightRow(99, 1.0)]);

        Assert.Null(summary.RmsRaArcsec);
        Assert.Null(summary.RmsDecArcsec);
        Assert.Null(summary.RmsTotalArcsec);
        Assert.Equal(1, summary.GatedSessionCount);
        Assert.Equal(99L, summary.FrameCount);
    }

    [Fact]
    public void AnEmptyNight_ReportsZerosAndNulls()
    {
        // A failure is an exception from taking a maximum over an empty sequence.
        var summary = Phd2Metrics.AggregateNight([]);

        Assert.Equal(0, summary.SessionCount);
        Assert.Equal(0L, summary.FrameCount);
        Assert.Equal(0, summary.MaxDropRun);
        Assert.Null(summary.RmsRaArcsec);
        Assert.Null(summary.RmsDecArcsec);
        Assert.Null(summary.RmsTotalArcsec);
        Assert.Null(summary.SettleMedianS);
        Assert.Empty(summary.CalIssues);
        Assert.Empty(summary.Profiles);
    }

    [Fact]
    public void AShortSession_StillStoresItsOwnRms()
    {
        // Spec 7.6 states the split in one sentence: the gate applies to the rollup, not to the
        // row. A null session RMS here is the gate applied to the row instead, and no other case
        // in this group catches it, because those rows are built directly rather than computed.
        var metrics = Metrics(Phd2MetricsFixtures.Base(20));

        Assert.Equal(20, metrics.FrameCount);
        Assert.Equal(2.0, metrics.RmsRaArcsec!.Value, 6);

        var summary = Phd2Metrics.AggregateNight([metrics]);

        Assert.Null(summary.RmsRaArcsec);
        Assert.Equal(1, summary.GatedSessionCount);
    }

    // ---- 6.8 SelectNightRows ----

    private sealed record Row(string? Telescope, string? Profile);

    private static IReadOnlyList<Row> Select(IEnumerable<Row> rows, params string[] telescopes)
        => Phd2Metrics.SelectNightRows(
            rows, r => r.Telescope, r => r.Profile, telescopes.ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void NoRows_SelectsNothing() => Assert.Empty(Select([], "Askar 120"));

    [Fact]
    public void RowsMatchingTheRigsAliasSet_AreSelected()
    {
        // Nothing selected when the stored name is an alias of the canonical one is the SVBony
        // defect the rule was written for.
        var rows = new Row[] { new("SVBony 80ED", "Rig A"), new("Askar 120", "Rig B") };

        var selected = Select(rows, "SVBony SV503 80mm", "SVBony 80ED");

        Assert.Equal(new[] { rows[0] }, selected);
    }

    [Fact]
    public void AProfileMappedToAnotherRig_IsNotStolen()
    {
        // The other rig's session attributed to this one puts the guided rig's numbers on the
        // wrong card.
        Assert.Empty(Select([new Row("Askar 120", "Rig B")], "SVBony 80ED"));
    }

    [Fact]
    public void TheNightsSoleUnmappedProfile_IsAttributed()
    {
        // Nothing attributed means a user who has not configured the map sees no guiding at all.
        var rows = new Row[] { new(null, "Rig A"), new("", "Rig A") };

        Assert.Equal(rows, Select(rows, "SVBony 80ED"));
    }

    [Fact]
    public void TwoUnmappedProfilesOnOneNight_AttributeNothing()
    {
        // Picking one of the two attaches wrong guiding numbers to real data.
        Assert.Empty(Select([new Row(null, "Rig A"), new Row(null, "Rig B")], "SVBony 80ED"));
    }

    [Fact]
    public void AMappedRowBesideAnUnmappedOne_TakesOnlyTheMappedOne()
    {
        var rows = new Row[] { new("SVBony 80ED", "Rig A"), new(null, "Rig B") };

        Assert.Equal(new[] { rows[0] }, Select(rows, "SVBony 80ED"));
    }

    [Fact]
    public void ANullProfileBesideAnEmptyProfile_IsOneProfile()
    {
        // The one declared departure from select_phd2_night_rows. The web counts null and the
        // empty string as two distinct profiles and attributes nothing; both mean "PHD2 named no
        // equipment profile", which questions-a.md Q15 rules are the same profile, and two
        // spellings of the pre-configuration state are one pre-configuration state. Do not
        // "correct" this back to the web's answer.
        var rows = new Row[] { new(null, null), new(null, "") };

        Assert.Equal(rows, Select(rows, "SVBony 80ED"));
    }

    [Fact]
    public void TheNullProfileFold_DoesNotReachTheMappedToAnotherRigVeto()
    {
        // The fold can only widen attribution on a night where no session carries a telescope at
        // all. One row mapped to another rig still vetoes, because that guard runs before the
        // profile count. Attributing here would put the guided rig's numbers on the wrong card.
        var rows = new Row[] { new(null, null), new("Askar 120", "") };

        Assert.Empty(Select(rows, "SVBony 80ED"));
    }

    // ---- 6.9 BuildCalibrationMetrics ----

    private static Phd2Calibration Calibration(bool completed = true, string? mountName = "Mount from completion")
    {
        var calibration = new Phd2Calibration
        {
            StartedAtLocal = new DateTime(2026, 3, 1, 20, 0, 0, DateTimeKind.Unspecified),
            Header = Phd2MetricsFixtures.BaseHeader(),
            WestAngleDeg = 91.2,
            WestRatePxS = 0.0123,
            WestParity = "+",
            Completed = completed,
            MountName = mountName,
        };

        calibration.Header.RaGuideSpeed = 7.5;
        calibration.Header.DecGuideSpeed = 7.5;
        calibration.Steps.Add(new Phd2CalibrationStep("West", 1, 1.0, 0.0, 10.0, 20.0, 1.0));
        calibration.Steps.Add(new Phd2CalibrationStep("Backlash", 1, 0.5, 0.0, 11.0, 20.0, 0.5));
        calibration.Steps.Add(new Phd2CalibrationStep("North", 1, 0.0, 1.0, 11.0, 21.0, 1.0));
        return calibration;
    }

    [Fact]
    public void TheMountName_PrefersTheCompletionLineOverTheHeader()
    {
        // The header's name always loses the name PHD2 confirmed at completion.
        Assert.Equal("Mount from completion",
            Phd2Metrics.BuildCalibrationMetrics(Calibration(), Zone, -80.0, true).MountName);

        Assert.Equal("ASI Mount (ASCOM)",
            Phd2Metrics.BuildCalibrationMetrics(Calibration(mountName: null), Zone, -80.0, true).MountName);
    }

    [Fact]
    public void TheStepsAreCarriedThroughInOrder()
    {
        // A reordering or a filtered Backlash leg is the failure.
        var steps = Phd2Metrics.BuildCalibrationMetrics(Calibration(), Zone, -80.0, true).Steps;

        Assert.Equal(new[] { "West", "Backlash", "North" }, steps.Select(s => s.Direction));
    }

    [Fact]
    public void AnAbortedCalibration_StillProducesARow()
    {
        // A null row loses a calibration that half worked and the reason it stopped.
        var metrics = Phd2Metrics.BuildCalibrationMetrics(
            Calibration(completed: false, mountName: null), Zone, -80.0, true);

        Assert.False(metrics.Completed);
        Assert.Equal(91.2, metrics.WestAngleDeg!.Value, 6);
        Assert.Equal(0.0123, metrics.WestRatePxS!.Value, 6);
        Assert.Equal("+", metrics.WestParity);
        Assert.Null(metrics.NorthAngleDeg);
        Assert.Equal(new DateTime(2026, 3, 2, 1, 0, 0, DateTimeKind.Utc), metrics.StartedAtUtc);
    }

    [Fact]
    public void WithNoZone_TheCalibrationUtcStartAndSessionDateAreNull()
    {
        // A failure is a thrown exception.
        var metrics = Phd2Metrics.BuildCalibrationMetrics(Calibration(), null, -80.0, true);

        Assert.Null(metrics.StartedAtUtc);
        Assert.Null(metrics.SessionDate);
        Assert.Equal(new DateTime(2026, 3, 1, 20, 0, 0), metrics.StartedAtLocal);
        Assert.Equal("Rig A", metrics.EquipmentProfile);
        Assert.Equal(7.5, metrics.RaGuideSpeed!.Value, 6);
        Assert.Equal(3, metrics.Steps.Count);
    }

    // ---- RoundLikePython: the one rounding rule, measured against Python itself ----

    // Every expectation below was PRINTED BY PYTHON, not reasoned out. The command, run on this
    // machine's Python 3.14.7 (tags/v3.14.7:823f032, Aug  5 2026, 10:51:32) [MSC v.1944 64 bit
    // (AMD64)], was:
    //
    //   py -c "import struct
    //   b=lambda x: struct.unpack('<Q', struct.pack('<d', x))[0]
    //   for v,n in CASES: print('%016X %d %016X' % (b(v), n, b(round(v, n))))"
    //
    // Both the input and the expected result travel as the double's exact 64 bits, never as a
    // decimal literal, so the two languages provably round the same number. The comment on each
    // row is that value's Python repr, for a reader.
    //
    // The last seven rows are the seven real divergences found by running both implementations
    // over the user's own 60 PHD2 guide logs, 937 guiding sections and 1,229,517 frames. Before
    // this fix Math.Round gave 14.908, 13.196, 12.586, 13.900, 12.510, 18.536 and 25.6018 against
    // Python's 14.909, 13.197, 12.585, 13.899, 12.511, 18.537 and 25.6017: seven of seven wrong.
    [Theory]
    [InlineData("3FE0000000000000", 0, "0000000000000000")] // round(0.5, 0) == 0.0
    [InlineData("3FF8000000000000", 0, "4000000000000000")] // round(1.5, 0) == 2.0
    [InlineData("4004000000000000", 0, "4000000000000000")] // round(2.5, 0) == 2.0
    [InlineData("400C000000000000", 0, "4010000000000000")] // round(3.5, 0) == 4.0
    [InlineData("BFE0000000000000", 0, "8000000000000000")] // round(-0.5, 0) == -0.0
    [InlineData("BFF8000000000000", 0, "C000000000000000")] // round(-1.5, 0) == -2.0
    [InlineData("C004000000000000", 0, "C000000000000000")] // round(-2.5, 0) == -2.0
    [InlineData("3FC0000000000000", 2, "3FBEB851EB851EB8")] // round(0.125, 2) == 0.12
    [InlineData("3FD8000000000000", 2, "3FD851EB851EB852")] // round(0.375, 2) == 0.38
    [InlineData("BFC0000000000000", 2, "BFBEB851EB851EB8")] // round(-0.125, 2) == -0.12
    [InlineData("BFD8000000000000", 2, "BFD851EB851EB852")] // round(-0.375, 2) == -0.38
    [InlineData("3FD0000000000000", 1, "3FC999999999999A")] // round(0.25, 1) == 0.2
    [InlineData("3FD6666666666666", 1, "3FD3333333333333")] // round(0.35, 1) == 0.3
    [InlineData("BFD0000000000000", 1, "BFC999999999999A")] // round(-0.25, 1) == -0.2
    [InlineData("4005666666666666", 2, "40055C28F5C28F5C")] // round(2.675, 2) == 2.67
    [InlineData("3FF0147AE147AE14", 2, "3FF0000000000000")] // round(1.005, 2) == 1.0
    [InlineData("4004000000000000", 2, "4004000000000000")] // round(2.5, 2) == 2.5
    [InlineData("4480F0CF064DD592", 2, "4480F0CF064DD592")] // round(1e+22, 2) == 1e+22
    [InlineData("4480F0CF064DD592", 0, "4480F0CF064DD592")] // round(1e+22, 0) == 1e+22
    [InlineData("0000000000000001", 2, "0000000000000000")] // round(5e-324, 2) == 0.0
    [InlineData("0000000000000001", 320, "0000000000000000")] // round(5e-324, 320) == 0.0
    [InlineData("3EA0C6F7A0B5ED8D", 6, "0000000000000000")] // round(5e-07, 6) == 0.0
    [InlineData("3FF000008637BD06", 6, "3FF000010C6F7A0B")] // round(1.0000005, 6) == 1.000001
    [InlineData("3E8421F5F40D8376", 6, "0000000000000000")] // round(1.5e-07, 6) == 0.0
    [InlineData("BFF000008637BD06", 6, "BFF000010C6F7A0B")] // round(-1.0000005, 6) == -1.000001
    [InlineData("C02DD126E978D4FE", 3, "C02DD16872B020C5")] // round(-14.9085, 3) == -14.909
    [InlineData("C0292BC6A7EF9DB2", 3, "C0292B851EB851EC")] // round(-12.5855, 3) == -12.585
    [InlineData("C0399A0C49BA5E35", 4, "C0399A0902DE00D2")] // round(-25.60175, 4) == -25.6017
    [InlineData("7E37E43C8800759C", 2, "7E37E43C8800759C")] // round(1e+300, 2) == 1e+300
    [InlineData("7FEFFFFFFFFFFFFF", 2, "7FEFFFFFFFFFFFFF")] // round(1.7976931348623157e+308, 2) == 1.7976931348623157e+308
    [InlineData("FFEFFFFFFFFFFFFF", 2, "FFEFFFFFFFFFFFFF")] // round(-1.7976931348623157e+308, 2) == -1.7976931348623157e+308
    [InlineData("0000000000000000", 3, "0000000000000000")] // round(0.0, 3) == 0.0
    [InlineData("8000000000000000", 3, "8000000000000000")] // round(-0.0, 3) == -0.0
    [InlineData("419D6F3457F35BA8", 6, "419D6F3457F35B92")] // round(123456789.98765433, 6) == 123456789.987654
    [InlineData("C19D6F3457F35BA8", 6, "C19D6F3457F35B92")] // round(-123456789.98765433, 6) == -123456789.987654
    [InlineData("4005666666666666", 0, "4008000000000000")] // round(2.675, 0) == 3.0
    [InlineData("4341C37937E08000", 0, "4341C37937E08000")] // round(1e+16, 0) == 1e+16
    [InlineData("4340000000000000", 0, "4340000000000000")] // round(9007199254740992.0, 0) == 9007199254740992.0
    [InlineData("3FC28F5C28F5C28F", 2, "3FC1EB851EB851EC")] // round(0.145, 2) == 0.14
    [InlineData("3FC147AE147AE148", 2, "3FC1EB851EB851EC")] // round(0.135, 2) == 0.14
    [InlineData("402DD126E978D4FE", 3, "402DD16872B020C5")] // round(14.9085, 3) == 14.909
    [InlineData("402A649BA5E353F8", 3, "402A64DD2F1A9FBE")] // round(13.1965, 3) == 13.197
    [InlineData("40292BC6A7EF9DB2", 3, "40292B851EB851EC")] // round(12.5855, 3) == 12.585
    [InlineData("402BCC8B43958106", 3, "402BCC49BA5E353F")] // round(13.8995, 3) == 13.899
    [InlineData("402905604189374C", 3, "402905A1CAC08312")] // round(12.5105, 3) == 12.511
    [InlineData("4032895810624DD3", 3, "40328978D4FDF3B6")] // round(18.5365, 3) == 18.537
    [InlineData("40399A0C49BA5E35", 4, "40399A0902DE00D2")] // round(25.60175, 4) == 25.6017
    public void RoundLikePython_MatchesPython(string valueBits, int digits, string expectedBits)
    {
        var value = BitConverter.Int64BitsToDouble(Convert.ToInt64(valueBits, 16));
        var expected = Convert.ToInt64(expectedBits, 16);

        var actual = BitConverter.DoubleToInt64Bits(PythonNumerics.RoundLikePython(value, digits));

        // Compared as bits, so a negative zero cannot pass as a positive zero and a one-ulp
        // difference cannot hide behind a tolerance.
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RoundLikePython_PassesNonFiniteValuesThrough()
    {
        // A NaN or an infinity reaching a format string and coming back parsed would throw, which
        // would take down a whole log's ingest on one empty statistic.
        Assert.True(double.IsNaN(PythonNumerics.RoundLikePython(double.NaN, 3)));
        Assert.Equal(double.PositiveInfinity, PythonNumerics.RoundLikePython(double.PositiveInfinity, 6));
        Assert.Equal(double.NegativeInfinity, PythonNumerics.RoundLikePython(double.NegativeInfinity, 6));
    }

    [Fact]
    public void RoundLikePython_RefusesANegativeDigitCount()
    {
        // "F-1" is a custom format string, not a standard one, so a negative count would silently
        // produce a different number rather than fail.
        Assert.Throws<ArgumentOutOfRangeException>(() => PythonNumerics.RoundLikePython(1.5, -1));
    }

    [Fact]
    public void MathRound_IsADifferentFunction_OnTheSevenRealCases()
    {
        // The measured cause, kept as a case so nobody reverts the helper to Math.Round believing
        // the two agree: .NET rounds the shortest round-trippable decimal form and Python rounds
        // the exact binary value. Each pair below is the same double read two ways.
        (double Value, int Digits, double Python, double MathRound)[] cases =
        [
            (14.9085, 3, 14.909, 14.908),
            (13.1965, 3, 13.197, 13.196),
            (12.5855, 3, 12.585, 12.586),
            (13.8995, 3, 13.899, 13.900),
            (12.5105, 3, 12.511, 12.510),
            (18.5365, 3, 18.537, 18.536),
            (25.60175, 4, 25.6017, 25.6018),
        ];

        foreach (var (value, digits, python, mathRound) in cases)
        {
            Assert.Equal(python, PythonNumerics.RoundLikePython(value, digits));
            Assert.Equal(mathRound, Math.Round(value, digits));
            Assert.NotEqual(python, mathRound);
        }
    }
}
