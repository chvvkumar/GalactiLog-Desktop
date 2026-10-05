using GalactiLog.Core.Phd2;
using Xunit;
using static GalactiLog.Core.Tests.Phd2.Phd2Assert;

namespace GalactiLog.Core.Tests.Phd2;

// Whole-file, row, event and ASIAIR cases for the PHD2 guide-log parser, spec 7.6. Port of
// backend/tests/test_phd2_parser.py. Every case names the wrong output a broken parser gives,
// not only the passing assertion: a case that knows the failure shape catches a regression that
// happens to land on a different wrong answer.
public class Phd2LogParserTests
{
    // --- Empty against unreadable ------------------------------------------------------------

    // Failure shape: a thrown Phd2UnreadableLogException, which would mean the marker check
    // fires on text with no markers.
    [Fact]
    public void EmptyText_YieldsNoRuns()
    {
        Assert.Empty(Phd2LogParser.Parse(""));
    }

    // Failure shape: one run with a null log version, which would mean the blank-line skip is
    // missing.
    [Fact]
    public void WhitespaceOnlyText_YieldsNoRuns()
    {
        Assert.Empty(Phd2LogParser.Parse("  \n\n\t\n"));
    }

    // Failure shape: a throw, which would mean the marker set matches a substring rather than a
    // line prefix.
    [Fact]
    public void ProseWithNoGuideLogContent_YieldsNoRuns()
    {
        Assert.Empty(Phd2LogParser.Parse("not a guide log at all\n"));
    }

    // Failure shape: an empty list, which files an unreadable log beside the genuinely empty
    // ones. Ten real ASIAIR logs holding 74 unseen guiding sections sat in that state for a year.
    [Fact]
    public void GuideLogContentThatCannotBePlaced_Throws()
    {
        Assert.Throws<Phd2UnreadableLogException>(() => Phd2LogParser.Parse(Phd2LogFixtures.HeadlessLog));
    }

    [Fact]
    public void AnUnrecognisedBannerShape_Throws()
    {
        Assert.Throws<Phd2UnreadableLogException>(() => Phd2LogParser.Parse(Phd2LogFixtures.UnknownBannerLog));
    }

    // Failure shape: a generic message with no line, which leaves a user with nothing to look at.
    [Fact]
    public void TheUnreadableError_QuotesTheLineItCouldNotPlace()
    {
        var error = Assert.Throws<Phd2UnreadableLogException>(
            () => Phd2LogParser.Parse(Phd2LogFixtures.UnknownBannerLog));
        Assert.Contains("PHD2 version 3.0.0 (Windows)", error.Message, StringComparison.Ordinal);
    }

    // Failure shape: only the derived type catches, which would force every caller to import the
    // name. ThrowsAny rather than Throws because xunit's Throws<T> is an exact-type assertion.
    [Fact]
    public void TheUnreadableError_IsCatchableAsAFormatException()
    {
        Assert.ThrowsAny<FormatException>(() => Phd2LogParser.Parse(Phd2LogFixtures.HeadlessLog));
    }

    // Failure shape: a throw, because the run list is non-empty and the marker rule must not be
    // consulted at all. The parser understood the file perfectly; there is nothing in it.
    [Fact]
    public void ABannerOnlyLogWithNoSections_IsEmptyNotUnreadable()
    {
        var runs = Phd2LogParser.Parse(
            "PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-01-19 17:21:18\n" +
            "Log Summary: calcnt:0 gcnt:0 gdur:0 gacnt:0\n" +
            "Log closed at 2026-01-19 17:21:51\n");
        var run = Assert.Single(runs);
        Assert.Empty(run.Sections);
        Assert.Empty(run.Calibrations);
        Assert.Empty(run.Warnings);
    }

    // --- Stacked runs -------------------------------------------------------------------------

    // Failure shape: one run, because the segmentation fell back to "Log enabled at", which is
    // identical across all five.
    [Fact]
    public void StackedEmptyLog_YieldsFiveRunsWithNoSections()
    {
        var runs = Phd2LogParser.Parse(Phd2LogFixtures.EmptyStackedLog);
        Assert.Equal(5, runs.Count);
        Assert.All(runs, run =>
        {
            Assert.Empty(run.Sections);
            Assert.Empty(run.Calibrations);
            Assert.NotNull(run.Summary);
            Assert.Equal(0, run.Summary!.CalibrationCount);
            Assert.Equal(0, run.Summary.GuidingCount);
            Assert.Equal(0, run.Summary.GuidingDurationS);
            Assert.Equal(0, run.Summary.GuidingFrameCount);
            Assert.Empty(run.Warnings);
            Assert.Equal(new DateTime(2026, 1, 19, 17, 21, 18), run.LogEnabledAt);
        });
    }

    // Failure shape: five identical values, because the close line is being written to the first
    // run.
    [Fact]
    public void StackedEmptyLog_EachRunKeepsItsOwnCloseTime()
    {
        var closed = Phd2LogParser.Parse(Phd2LogFixtures.EmptyStackedLog).Select(r => r.LogClosedAt).ToList();
        Assert.Equal(5, closed.Distinct().Count());
        Assert.Equal(new DateTime(2026, 1, 19, 17, 21, 51), closed[0]);
        Assert.Equal(new DateTime(2026, 1, 19, 18, 21, 50), closed[^1]);
    }

    // Failure shape: a null version, which is the optional banner group made optional the wrong
    // way. Two versions including a dev build.
    [Fact]
    public void DesktopBanner_StillCarriesItsVersionAndPlatform()
    {
        var runs = Phd2LogParser.Parse(Phd2LogFixtures.EmptyStackedLog);
        Assert.Equal(
            new[] { "2.6.13dev8", "2.6.14", "2.6.14", "2.6.14", "2.6.14" },
            runs.Select(r => r.Phd2Version));
        Assert.All(runs, r => Assert.Equal("Windows", r.Platform));
    }

    // --- The desktop sample --------------------------------------------------------------------

    // Failure shape: a non-empty warning list, which means the summary cross-check disagrees with
    // what was parsed.
    [Fact]
    public void FullSample_YieldsOneCalibrationAndOneGuidingSection()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog));
        Assert.Single(run.Calibrations);
        Assert.Single(run.Sections);
        Assert.Empty(run.Warnings);
    }

    // Failure shape: nulls for the first two, which means the optional banner group was made
    // optional in a way that stops capturing when the text is present.
    [Fact]
    public void FullSample_RunCarriesItsVersionPlatformAndLogVersion()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog));
        Assert.Equal("2.6.14", run.Phd2Version);
        Assert.Equal("Windows", run.Platform);
        Assert.Equal("2.5", run.LogVersion);
    }

    // Failure shape: Kind Local or Utc, which means something called ToUniversalTime or
    // SpecifyKind. The parser returns the wall clock exactly as written (ruling F1).
    [Fact]
    public void FullSample_LogEnabledAndClosedAreTheWrittenWallClock()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog));
        Assert.Equal(new DateTime(2026, 7, 14, 20, 13, 33), run.LogEnabledAt);
        Assert.Equal(new DateTime(2026, 7, 14, 21, 43, 0), run.LogClosedAt);
        Assert.Equal(DateTimeKind.Unspecified, run.LogEnabledAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Unspecified, run.LogClosedAt!.Value.Kind);
    }

    [Fact]
    public void LogSummary_IsCapturedPerRun()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog));
        Assert.NotNull(run.Summary);
        Assert.Equal(1, run.Summary!.CalibrationCount);
        Assert.Equal(1, run.Summary.GuidingCount);
        Assert.Equal(19, run.Summary.GuidingDurationS);
        Assert.Equal(5, run.Summary.GuidingFrameCount);
    }

    // Failure shape: no warnings, which means the cross-check compares against itself rather
    // than against the parsed counts.
    [Fact]
    public void LogSummaryMismatch_ProducesAWarning()
    {
        var text = Phd2LogFixtures.FullSampleLog.Replace(
            "Log Summary: calcnt:1 gcnt:1 gdur:19 gacnt:5",
            "Log Summary: calcnt:2 gcnt:3 gdur:19 gacnt:5",
            StringComparison.Ordinal);
        var run = Assert.Single(Phd2LogParser.Parse(text));
        Assert.Contains(run.Warnings, w => w.Contains("gcnt:3", StringComparison.Ordinal));
        Assert.Contains(run.Warnings, w => w.Contains("calcnt:2", StringComparison.Ordinal));
    }

    // --- Frame rows -----------------------------------------------------------------------------

    // Failure shape: RaDurationMs null-coalesced to 0 on a row that has 98, which means the field
    // index is off by one.
    [Fact]
    public void GuidingFrames_CarryTheirDirectionsAndPulses()
    {
        var section = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Sections[0];
        Assert.Equal(new DateTime(2026, 7, 14, 21, 42, 27), section.StartedAtLocal);
        Assert.Equal(new DateTime(2026, 7, 14, 21, 42, 46), section.EndedAtLocal);
        Assert.False(section.Truncated);
        Assert.Equal(new[] { 1, 2, 3, 7, 9067 }, section.Frames.Select(f => f.FrameIndex));
        Assert.Equal(new[] { "W", "", "E", "W", "" }, section.Frames.Select(f => f.RaDirection));
        Assert.Equal(new[] { 98, 0, 125, 108, 0 }, section.Frames.Select(f => f.RaDurationMs));
        Assert.Equal(new[] { "", "", "", "S", "" }, section.Frames.Select(f => f.DecDirection));
        Assert.Equal(new[] { 0, 0, 0, 267, 0 }, section.Frames.Select(f => f.DecDurationMs));
        Near(-0.710, section.Frames[2].RaRaw);
        Near(1.183, section.Frames[3].DecRaw);
    }

    // Failure shape: any single field reading a neighbour's value, which an index-by-index
    // assertion catches and a spot check does not.
    [Fact]
    public void AFrameRow_ReadsEveryColumnAtItsOwnIndex()
    {
        // 1,1.228,"Mount",0.330,0.544,0.556,-0.189,0.350,0.000,98,W,0,,,,1713,28.59,1
        var f = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Sections[0].Frames[0];
        Assert.Equal(1, f.FrameIndex);
        Near(1.228, f.TimeOffset);
        Assert.False(f.Dropped);
        Near(0.330, f.Dx);
        Near(0.544, f.Dy);
        Near(0.556, f.RaRaw);
        Near(-0.189, f.DecRaw);
        Near(0.350, f.RaGuide);
        Near(0.000, f.DecGuide);
        Assert.Equal(98, f.RaDurationMs);
        Assert.Equal("W", f.RaDirection);
        Assert.Equal(0, f.DecDurationMs);
        Assert.Equal("", f.DecDirection);
        Near(1713.0, f.StarMass);
        Near(28.59, f.Snr);
        Assert.Equal(1, f.ErrorCode);
        Assert.Equal("", f.DropReason);
    }

    // Failure shape: a 17-column read that shifts StarMass, Snr and ErrorCode one place left.
    // XStep and YStep are read past and stored nowhere.
    [Fact]
    public void XStepAndYStep_AreReadAndDiscarded()
    {
        Assert.DoesNotContain(
            typeof(Phd2Frame).GetProperties(),
            p => p.Name.Contains("Step", StringComparison.Ordinal));

        var f = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Sections[0].Frames[0];
        Near(1713.0, f.StarMass);
        Near(28.59, f.Snr);
        Assert.Equal(1, f.ErrorCode);
    }

    // Failure shape: the reason still quoted, which means a plain comma split was used; or an
    // empty DropReason, which means the nineteenth field was never read.
    [Fact]
    public void ADropRow_WithNineteenFields_KeepsItsReason()
    {
        var drop = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Sections[0].Frames[^1];
        Assert.Equal(9067, drop.FrameIndex);
        Assert.True(drop.Dropped);
        Assert.Equal("Star lost - mass changed", drop.DropReason);
        Assert.Null(drop.RaRaw);
        Assert.Null(drop.DecRaw);
        Assert.Equal(7, drop.ErrorCode);
    }

    // Failure shape: nulls, which means DROP rows are skipped before the cells are read. Task 3's
    // SnrMin then never sees a collapsing star.
    [Fact]
    public void ADropRow_StillCarriesItsStarMassAndSnr()
    {
        var drop = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Sections[0].Frames[^1];
        Near(929.0, drop.StarMass);
        Near(19.23, drop.Snr);
    }

    // Failure shape: null, which reaches Task 3's reason tally and becomes a null dictionary key.
    [Fact]
    public void AnEighteenFieldRow_HasAnEmptyDropReason()
    {
        var f = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Sections[0].Frames[0];
        Assert.NotNull(f.DropReason);
        Assert.Equal("", f.DropReason);
    }

    // Failure shape: it is kept, with a dozen nulls, which is invented data.
    [Fact]
    public void ARowWithFewerThanEighteenFields_IsUnusable()
    {
        var section = Phd2LogParser.Parse(Phd2LogFixtures.TruncatedLog)[0].Sections[0];
        Assert.Equal(new[] { 1, 2 }, section.Frames.Select(f => f.FrameIndex));
        Assert.DoesNotContain(section.Frames, f => f.FrameIndex == 3);
    }

    // Failure shape: accepted with index 0.
    [Fact]
    public void ARowWhoseFrameIndexIsNotAnInteger_IsUnusable()
    {
        var text = Phd2LogFixtures.CorruptMidSection.Replace(
            "1,1.228,\"Mount\"", "x,1.228,\"Mount\"", StringComparison.Ordinal);
        var section = Phd2LogParser.Parse(text)[0].Sections[0];
        Assert.Empty(section.Frames);
        Assert.True(section.Truncated);
        Assert.Equal(4, section.DiscardedRows);

        // The other side of the divergence, pinned so it is a decision and not an accident: the
        // Frame cell goes through the one ParseInt, which goes through ParseDouble, so "1.0" is
        // accepted and truncated where Python's int() would refuse the whole row. Unreachable
        // from a PHD2-written log, which writes an integer in this column.
        var decimalIndex = Phd2LogFixtures.CorruptMidSection.Replace(
            "1,1.228,\"Mount\"", "1.0,1.228,\"Mount\"", StringComparison.Ordinal);
        var kept = Phd2LogParser.Parse(decimalIndex)[0].Sections[0];
        Assert.Equal(1, Assert.Single(kept.Frames).FrameIndex);
    }

    // Failure shape: null. ErrorCode is parsed as a double and truncated, so 7 and 7.0 both read
    // seven.
    [Fact]
    public void TheErrorCodeColumn_ReadsAnIntegerFromADecimalCell()
    {
        var text = Phd2LogFixtures.FullSampleLog.Replace(
            ",1713,28.59,1", ",1713,28.59,7.0", StringComparison.Ordinal);
        var f = Phd2LogParser.Parse(text)[0].Sections[0].Frames[0];
        Assert.Equal(7, f.ErrorCode);
    }

    // --- Truncation and discarded rows ------------------------------------------------------

    // Failure shape: three frames, because the half row was accepted and its missing cells read
    // as null; or zero frames, because the whole section was thrown away.
    [Fact]
    public void TruncatedTail_MarksTheSectionTruncatedAndKeepsTheGoodRows()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.TruncatedLog));
        Assert.Null(run.LogClosedAt);
        var section = Assert.Single(run.Sections);
        Assert.Equal(2, section.Frames.Count);
        Assert.True(section.Truncated);
        Assert.Equal(1, section.DiscardedRows);
        Assert.Null(section.EndedAtLocal);
    }

    // Failure shape: DiscardedRows 1, which counts only the bad row and hides the two-row loss
    // the count exists to report.
    [Fact]
    public void CorruptMidSection_CountsTheDiscardedRows()
    {
        var section = Phd2LogParser.Parse(Phd2LogFixtures.CorruptMidSection)[0].Sections[0];
        Assert.True(section.Truncated);
        Assert.Single(section.Frames);
        Assert.Equal(3, section.DiscardedRows);
    }

    // Failure shape: an empty warning list, which is the old silent behaviour: a corrupt row two
    // lines into a 5000-row section then costs a whole night with nothing to say so.
    [Fact]
    public void CorruptMidSection_TheDiscardCountReachesTheRunWarnings()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.CorruptMidSection));
        Assert.Contains(run.Warnings, w =>
            w.Contains("discarded", StringComparison.Ordinal) && w.Contains("3", StringComparison.Ordinal));
    }

    // Failure shape: null, which means discard mode swallowed the structural line. The section
    // still has to end where PHD2 says it ended.
    [Fact]
    public void CorruptMidSection_TheEndLineStillClosesTheSection()
    {
        var section = Phd2LogParser.Parse(Phd2LogFixtures.CorruptMidSection)[0].Sections[0];
        Assert.NotNull(section.EndedAtLocal);
    }

    // Failure shape: any warning, which means the discard pass fires on a zero count.
    [Fact]
    public void ACleanSection_DiscardsNothingAndWarnsAboutNothing()
    {
        var text = Phd2LogFixtures.CorruptMidSection.Replace(
            "2,2.093,\"Mount\",0.524",
            "2,2.093,\"Mount\",0.524,0.132,0.151,-0.477,0.000,0.000,0,,0,,,,1702,28.36,0",
            StringComparison.Ordinal);
        var run = Assert.Single(Phd2LogParser.Parse(text));
        var section = run.Sections[0];
        Assert.Equal(0, section.DiscardedRows);
        Assert.Equal(4, section.Frames.Count);
        Assert.DoesNotContain(run.Warnings, w => w.Contains("discarded", StringComparison.Ordinal));
    }

    // --- CSV header rules ----------------------------------------------------------------------

    [Fact]
    public void TheByteStableCsvHeaders_AreExposedAsConstants()
    {
        Assert.StartsWith("Frame,Time,mount,dx,dy,", Phd2LogParser.GuideCsvHeader, StringComparison.Ordinal);
        Assert.EndsWith(",StarMass,SNR,ErrorCode", Phd2LogParser.GuideCsvHeader, StringComparison.Ordinal);
        Assert.Equal("Direction,Step,dx,dy,x,y,Dist", Phd2LogParser.CalCsvHeader);
    }

    // Failure shape: zero frames and no warning, which is the exact silent failure the rule
    // exists for: a PHD2 release that adds one column would drop every frame with nothing to
    // point at.
    [Fact]
    public void ANearMissGuideCsvHeader_IsReportedLoudly()
    {
        var text = Phd2LogFixtures.CorruptMidSection.Replace(
            "Frame,Time,mount,dx,dy,RARawDistance",
            "Frame,Time,mount,dx,dy,dz,RARawDistance",
            StringComparison.Ordinal);
        var run = Assert.Single(Phd2LogParser.Parse(text));
        Assert.Empty(run.Sections[0].Frames);
        Assert.Contains(run.Warnings, w =>
            w.Contains("does not match", StringComparison.Ordinal)
            && w.Contains("Frame,Time", StringComparison.Ordinal));
    }

    // What this catches is the GuideCsvHeader constant drifting away from the layout the fixture
    // writes, which would make the exact header fall through to the near-miss branch and warn on
    // a clean file. It does not catch the equality check being loosened to a prefix check, since
    // the exact header still matches first either way; ANearMissGuideCsvHeader_IsReportedLoudly
    // covers that direction.
    [Fact]
    public void TheExactGuideCsvHeader_ProducesNoSuchWarning()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.CorruptMidSection));
        Assert.DoesNotContain(run.Warnings, w => w.Contains("does not match", StringComparison.Ordinal));
    }

    // Failure shape: no warning.
    [Fact]
    public void ANearMissCalibrationCsvHeader_IsReportedToo()
    {
        var text = Phd2LogFixtures.CorruptMidSection.Replace(
            "Frame,Time,mount,dx,dy,RARawDistance",
            "Direction,Step,dx,dy,x,y,Dist,Extra\nFrame,Time,mount,dx,dy,RARawDistance",
            StringComparison.Ordinal);
        var run = Assert.Single(Phd2LogParser.Parse(text));
        Assert.Contains(run.Warnings, w => w.Contains("Direction,Step", StringComparison.Ordinal));
    }

    // --- Events ---------------------------------------------------------------------------------

    // Failure shape: every offset 0.0, which means the running frame time is never updated.
    // Task 3's window exclusion then excludes nothing.
    [Fact]
    public void Events_AreTypedAndStampedWithThePrecedingFrameTime()
    {
        var section = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Sections[0];
        Assert.Equal(
            new[]
            {
                Phd2EventTypes.SettleStart, Phd2EventTypes.SettleDone,
                Phd2EventTypes.Dither, Phd2EventTypes.LockShift,
            },
            section.Events.Select(e => e.Type));
        Near(0.0, section.Events[0].TimeOffset);
        Near(3.149, section.Events[1].TimeOffset);
        Near(7.381, section.Events[2].TimeOffset);
        Near(9716.891, section.Events[3].TimeOffset);
        Assert.Equal("3.4, -1.2, new lock pos = 80.807, 203.220", section.Events[2].Detail);
    }

    // Failure shape: a stamp taken from the previous section, which happens when the running time
    // is not reset on Guiding Begins.
    [Fact]
    public void AnEventBeforeTheFirstRow_IsStampedZero()
    {
        var section = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Sections[0];
        Assert.Equal(Phd2EventTypes.SettleStart, section.Events[0].Type);
        Near(0.0, section.Events[0].TimeOffset);
    }

    // Failure shape: a second section whose settle window opens at 18.593, the last time of the
    // first section.
    [Fact]
    public void TheRunningFrameTime_ResetsAtEachGuidingBegins()
    {
        var second = Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog)[0].Sections[1];
        Assert.Equal(Phd2EventTypes.SettleStart, second.Events[0].Type);
        Near(0.0, second.Events[0].TimeOffset);
        Near(6.243, second.Events[1].TimeOffset);
    }

    // Failure shape: an event with a null type, which reaches the stored JSON. PHD2 writes many
    // INFO lines the parser has no pattern for.
    [Fact]
    public void AnUnknownInfoLine_IsDroppedSilently()
    {
        var text = Phd2LogFixtures.FullSampleLog.Replace(
            "INFO: SETTLING STATE CHANGE, Settling started",
            "INFO: Something else entirely\nINFO: SETTLING STATE CHANGE, Settling started",
            StringComparison.Ordinal);
        var run = Assert.Single(Phd2LogParser.Parse(text));
        Assert.Equal(4, run.Sections[0].Events.Count);
        Assert.Empty(run.Warnings);
    }

    // Failure shape: a settle_done, which closes a window that never closed.
    [Fact]
    public void ASettlingStateChangeWithAnUnknownDetail_AddsNoEvent()
    {
        var text = Phd2LogFixtures.FullSampleLog.Replace(
            "INFO: SETTLING STATE CHANGE, Settling started",
            "INFO: SETTLING STATE CHANGE, Settling aborted\nINFO: SETTLING STATE CHANGE, Settling started",
            StringComparison.Ordinal);
        var section = Phd2LogParser.Parse(text)[0].Sections[0];
        Assert.Equal(4, section.Events.Count);
        Assert.Equal(Phd2EventTypes.SettleStart, section.Events[0].Type);
    }

    // Failure shape: an appended event on a null section, which throws a NullReferenceException.
    // The STAR LOST line sits inside a calibration block, where no guiding section is open.
    [Fact]
    public void StarLostDuringCalibration_IsNotAttachedToAnySection()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AbortedCalLog));
        Assert.Empty(run.Sections);
        Assert.Single(run.Calibrations);
    }

    // --- Calibration -------------------------------------------------------------------------

    // Failure shape: the Backlash row dropped, which means directions are validated against a
    // four-value set.
    [Fact]
    public void CalibrationSteps_CaptureEveryLegInOrder()
    {
        var cal = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Calibrations[0];
        Assert.Equal(
            new[] { "West", "West", "West", "East", "East", "Backlash", "North", "North", "South", "South" },
            cal.Steps.Select(s => s.Direction));
        Assert.Equal(1, cal.Steps[1].Step);
        Near(0.175, cal.Steps[1].Dx);
        Near(1.175, cal.Steps[1].Dy);
        Near(86.042, cal.Steps[1].X);
        Near(201.133, cal.Steps[1].Y);
        Near(1.188, cal.Steps[1].Dist);
    }

    [Fact]
    public void TheCalibrationBlock_CarriesItsOwnHeaderAndStartTime()
    {
        var cal = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Calibrations[0];
        Assert.Equal(new DateTime(2026, 7, 14, 21, 41, 21), cal.StartedAtLocal);
        Assert.Equal("AM5n_OAG_ASI174M", cal.Header.EquipmentProfile);
        Near(1.54, cal.Header.PixelScaleArcsec);
    }

    // Failure shape: the North values in the West fields, which is what a shared setter does.
    [Fact]
    public void TheWestAndNorthAxes_CarryTheirAngleRateAndParity()
    {
        var cal = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Calibrations[0];
        Near(87.9, cal.WestAngleDeg);
        Near(3.579, cal.WestRatePxS);
        Assert.Equal("N/A", cal.WestParity);
        Near(-170.3, cal.NorthAngleDeg);
        Near(4.437, cal.NorthRatePxS);
        Assert.Equal("N/A", cal.NorthParity);
    }

    // Failure shape: "ASI Mount (ASCOM)." with its full stop still attached.
    [Fact]
    public void TheCalibrationCompleteLine_TakesTheMountNameWithoutItsFullStop()
    {
        var cal = Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog)[0].Calibrations[0];
        Assert.True(cal.Completed);
        Assert.Equal("ASI Mount (ASCOM)", cal.MountName);
    }

    // Failure shape: Completed true, which means the flag defaults wrong or the missing line is
    // inferred. Five of the corpus's 37 calibrations end this way.
    [Fact]
    public void AbortedCalibration_IsMarkedIncomplete()
    {
        var cal = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AbortedCalLog)[0].Calibrations);
        Assert.False(cal.Completed);
        Assert.Null(cal.MountName);
        Assert.Equal(2, cal.Steps.Count);
        Assert.Null(cal.WestAngleDeg);
    }

    // Failure shape: it is kept, with Dist read from the wrong cell. A step row is exactly seven
    // fields, not six and not eight, and a dropped one is silent.
    [Fact]
    public void ACalibrationStepRowWithEightFields_IsDropped()
    {
        var text = Phd2LogFixtures.FullSampleLog.Replace(
            "West,1,0.175,1.175,86.042,201.133,1.188",
            "West,1,0.175,1.175,86.042,201.133,1.188\nWest,9,0.1,0.2,0.3,0.4,0.5,0.6",
            StringComparison.Ordinal);
        var run = Assert.Single(Phd2LogParser.Parse(text));
        var cal = run.Calibrations[0];
        Assert.Equal(10, cal.Steps.Count);
        Assert.DoesNotContain(cal.Steps, s => s.Step == 9);
    }

    // --- Orphans ------------------------------------------------------------------------------

    // Failure shape: a thrown exception, or a section with a null start.
    [Fact]
    public void OrphanGuidingEnds_IsCountedNotFatal()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.OrphanEndLog));
        Assert.Empty(run.Sections);
        Assert.Equal(1, run.OrphanGuidingEnds);
    }

    // --- The ASIAIR build -----------------------------------------------------------------------

    // Failure shape: zero runs. Ten corpus files parsed to zero runs until the version and
    // platform were made one optional unit, and nothing in the product ever said so.
    [Fact]
    public void AsiairBanner_WithoutVersionOrPlatform_IsParsed()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog));
        Assert.Equal("2.5", run.LogVersion);
        Assert.Equal(new DateTime(2024, 9, 16, 20, 12, 29), run.LogEnabledAt);
    }

    // Failure shape: "", which a UI then prints as a blank version rather than omitting the
    // field. Absent is recorded as absent.
    [Fact]
    public void AsiairVersionAndPlatform_AreAbsentNotInvented()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog));
        Assert.Null(run.Phd2Version);
        Assert.Null(run.Platform);
    }

    // Failure shape: one section, which means the second Guiding Begins did not abandon the
    // first.
    [Fact]
    public void AsiairSample_YieldsBothOfItsGuidingSections()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog));
        Assert.Equal(2, run.Sections.Count);
        var first = run.Sections[0];
        var second = run.Sections[1];
        Assert.Equal(new DateTime(2024, 9, 16, 20, 12, 31), first.StartedAtLocal);
        Assert.Equal(new DateTime(2024, 9, 16, 20, 12, 51), first.EndedAtLocal);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, first.Frames.Select(f => f.FrameIndex));
        Assert.Equal(new[] { 1, 2, 3 }, second.Frames.Select(f => f.FrameIndex));
        Assert.Null(second.EndedAtLocal);
        Assert.Null(run.LogClosedAt);
    }

    // Failure shape: true on both, which means the flag is set without checking EndedAtLocal.
    [Fact]
    public void AsiairSecondSection_IsMarkedTruncated()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog));
        Assert.False(run.Sections[0].Truncated);
        Assert.True(run.Sections[1].Truncated);
    }

    // Failure shape: the second header empty, which means one header instance is shared across
    // the file and a per-section pixel scale is lost.
    [Fact]
    public void AsiairSections_EachCarryTheirOwnHeader()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog));
        Assert.All(run.Sections, s =>
        {
            Near(6.45, s.Header.PixelScaleArcsec);
            Assert.Equal("ZWO ASI120MM Mini", s.Header.GuideCamera);
            Near(2000.0, s.Header.MaxRaDurationMs);
            Assert.Null(s.Header.RaHr);
            Assert.Null(s.Header.CalTimestamp);
        });
        Near(-2.77, run.Sections[0].Header.HourAngleHr);
        Near(-2.76, run.Sections[1].Header.HourAngleHr);
    }

    // The ASIAIR names no equipment profile, and both forms the corpus carries produce the same
    // answer: null. The header line is trimmed before it is matched, so the trailing space one
    // of the two forms carries is gone before "^Equipment Profile = (.+)$" is tried and the line
    // matches nothing either way. Task 3 looks the profile up by (profile ?? ""), so a null
    // profile and an empty one resolve identically and both fall through to the global zone.
    // Failure shape: the two forms disagreeing, which would put half the ASIAIR corpus under a
    // different map key from the other half.
    [Fact]
    public void AsiairSection_HasNoEquipmentProfileEitherFormOfTheLine()
    {
        var withoutSpace = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog));
        Assert.All(withoutSpace.Sections, s => Assert.Null(s.Header.EquipmentProfile));

        var withSpace = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLogWithProfileSpace));
        Assert.All(withSpace.Sections, s => Assert.Null(s.Header.EquipmentProfile));
    }

    // --- The four run-boundary truncation triggers (review P2-1) --------------------------------
    //
    // Brief section 5.2 names five triggers for Truncated: the end of the text, the next banner,
    // Log closed at, Guiding Begins at and Calibration Begins at. Only the first was exercised,
    // because every fixture that meets a boundary has already closed its section with its own
    // Guiding Ends line, so AbandonSection runs against a null section and does nothing. Each
    // case below opens a section and leaves it open when the boundary arrives, and each goes red
    // when AbandonSection is removed from the branch it names.
    //
    // A single good CSV row and no Guiding Ends line: the section is still open, and no failed
    // row has set Truncated for another reason.
    private const string OpenSectionPrelude =
        "PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-07-14 20:13:33\n" +
        "Guiding Begins at 2026-07-14 21:42:27\n" +
        "Pixel scale = 1.54 arc-sec/px, Binning = 1, Focal length = 784 mm\n" +
        Phd2LogParser.GuideCsvHeader + "\n" +
        "1,1.228,\"Mount\",0.330,0.544,0.556,-0.189,0.350,0.000,98,W,0,,,,1713,28.59,1\n";

    // Failure shape: a first section that is not truncated, or run 2's content landing on it. The
    // second is the worse one: without the abandon the stale section reference stays live across
    // the run boundary, so run 2's INFO line before its own Guiding Begins is appended to run 1's
    // section.
    [Fact]
    public void ASectionStillOpenAtTheNextBanner_IsTruncatedAndKeepsItsRowsOnItsOwnRun()
    {
        var runs = Phd2LogParser.Parse(OpenSectionPrelude + Phd2LogFixtures.FullSampleLog);
        Assert.Equal(2, runs.Count);

        var abandoned = Assert.Single(runs[0].Sections);
        Assert.True(abandoned.Truncated);
        Assert.Null(abandoned.EndedAtLocal);
        Assert.Equal(new[] { 1 }, abandoned.Frames.Select(f => f.FrameIndex));
        Assert.Empty(abandoned.Events);

        var carriedOn = Assert.Single(runs[1].Sections);
        Assert.False(carriedOn.Truncated);
        Assert.Equal(5, carriedOn.Frames.Count);
        Assert.Equal(4, carriedOn.Events.Count);
        Assert.Single(runs[1].Calibrations);
    }

    // The run has an end and the section does not, which is what a killed PHD2 leaves behind.
    //
    // The trailing INFO line is synthetic and is the point of the case: the Log closed at branch
    // is the one boundary whose Truncated flag the end-of-text abandon would set anyway, so the
    // only thing that distinguishes abandoning here from abandoning at the end of the file is
    // that a later line cannot reach the closed run's section. An INFO line is the one rule that
    // consults the open section directly rather than the mode, so it is the smallest input that
    // tells the two apart. Failure shape: the abandoned section carrying that event, which means
    // the stale reference outlived the run it belongs to.
    [Fact]
    public void ASectionStillOpenAtLogClosed_IsTruncatedAndTakesNoLaterLine()
    {
        var run = Assert.Single(Phd2LogParser.Parse(
            OpenSectionPrelude
            + "Log closed at 2026-07-14 21:43:00\n"
            + "INFO: Guiding parameter change, MultiStar = true\n"));
        Assert.Equal(new DateTime(2026, 7, 14, 21, 43, 0), run.LogClosedAt);

        var section = Assert.Single(run.Sections);
        Assert.True(section.Truncated);
        Assert.Null(section.EndedAtLocal);
        Assert.Single(section.Frames);
        Assert.Empty(section.Events);
    }

    // Failure shape: the first section not truncated, or the two sections sharing frames. PHD2
    // writes this whenever it restarts guiding without having written the previous end line.
    [Fact]
    public void ASectionStillOpenAtTheNextGuidingBegins_IsTruncated()
    {
        var run = Assert.Single(Phd2LogParser.Parse(
            OpenSectionPrelude
            + "Guiding Begins at 2026-07-14 21:50:00\n"
            + "Pixel scale = 6.45 arc-sec/px\n"
            + Phd2LogParser.GuideCsvHeader + "\n"
            + "1,2.093,\"Mount\",0.524,0.132,0.151,-0.477,0.000,0.000,0,,0,,,,1702,28.36,0\n"
            + "Guiding Ends at 2026-07-14 21:50:30\n"));
        Assert.Equal(2, run.Sections.Count);

        Assert.True(run.Sections[0].Truncated);
        Assert.Null(run.Sections[0].EndedAtLocal);
        Assert.Single(run.Sections[0].Frames);
        Near(1.54, run.Sections[0].Header.PixelScaleArcsec);

        Assert.False(run.Sections[1].Truncated);
        Assert.Equal(new DateTime(2026, 7, 14, 21, 50, 30), run.Sections[1].EndedAtLocal);
        Assert.Single(run.Sections[1].Frames);
        Near(6.45, run.Sections[1].Header.PixelScaleArcsec);
    }

    // The calibration that interrupted the section still parses in full. The STAR LOST line
    // inside the calibration block is the shape AbortedCalLog carries, and it is what makes this
    // branch observable: the end-of-text abandon would set Truncated anyway, so what the abandon
    // here buys is that a calibration-block event cannot land on the guiding section the
    // calibration interrupted. Failure shape: that star_lost event appearing on the section,
    // which is the stale reference outliving its block.
    [Fact]
    public void ASectionStillOpenAtACalibrationBegins_IsTruncatedAndTakesNoCalibrationEvent()
    {
        var run = Assert.Single(Phd2LogParser.Parse(
            OpenSectionPrelude
            + "Calibration Begins at 2026-07-14 21:50:00\n"
            + Phd2LogParser.CalCsvHeader + "\n"
            + "West,0,0.000,0.000,86.217,202.308,0.000\n"
            + "INFO: STAR LOST during calibration, Mass= 0, SNR= 0.00, Error= 4, Status=Star lost - low HFD\n"
            + "Calibration complete, mount = ASI Mount (ASCOM).\n"));

        var section = Assert.Single(run.Sections);
        Assert.True(section.Truncated);
        Assert.Null(section.EndedAtLocal);
        Assert.Single(section.Frames);
        Assert.Empty(section.Events);

        var cal = Assert.Single(run.Calibrations);
        Assert.True(cal.Completed);
        Assert.Single(cal.Steps);
        Assert.Equal("ASI Mount (ASCOM)", cal.MountName);
    }

    // --- The two event kinds nothing pinned (review P2-2) ---------------------------------------
    //
    // star_lost and param_change appeared in no assertion: in every fixture that carries them the
    // INFO line sits outside an open guiding section, so the event is dropped whether its pattern
    // matches or not, and deleting either pattern broke nothing. Both lines are placed inside an
    // open section here, after the row at 3.149, so the type, the stamp and the detail are all
    // load bearing. A mistyped pattern would otherwise reach Task 4 as a silently absent event
    // class in the stored events document, which Phase 15B reads back.

    private static string WithInfoLineAfterTheThirdRow(string infoLine)
        => Phd2LogFixtures.FullSampleLog.Replace(
            "INFO: SETTLING STATE CHANGE, Settling complete",
            infoLine + "\nINFO: SETTLING STATE CHANGE, Settling complete",
            StringComparison.Ordinal);

    [Fact]
    public void AParameterChangeInsideASection_IsTypedAndStamped()
    {
        var section = Phd2LogParser.Parse(
            WithInfoLineAfterTheThirdRow("INFO: Guiding parameter change, MultiStar = true"))
            [0].Sections[0];

        Assert.Equal(5, section.Events.Count);
        var ev = section.Events[1];
        Assert.Equal(Phd2EventTypes.ParamChange, ev.Type);
        Near(3.149, ev.TimeOffset);
        Assert.Equal("MultiStar = true", ev.Detail);
    }

    [Fact]
    public void AStarLostLineInsideASection_IsTypedAndStamped()
    {
        var section = Phd2LogParser.Parse(WithInfoLineAfterTheThirdRow(
            "INFO: STAR LOST during calibration, Mass= 0, SNR= 0.00, Error= 4, Status=Star lost - low HFD"))
            [0].Sections[0];

        Assert.Equal(5, section.Events.Count);
        var ev = section.Events[1];
        Assert.Equal(Phd2EventTypes.StarLost, ev.Type);
        Near(3.149, ev.TimeOffset);
        Assert.Equal("Mass= 0, SNR= 0.00, Error= 4, Status=Star lost - low HFD", ev.Detail);
    }

    // --- Line endings, parity and the two pinned decisions (review P3-1, P3-2, P3-5, P3-7) -------

    // PHD2 on Windows writes CRLF and Task 4 hands Parse exactly what the file holds, so the
    // whole suite running on LF text left the real production shape untested. The CRLF text is
    // built at run time because the writing tools in this toolchain normalise line endings in a
    // source literal. Failure shape: zero frames, because a stray carriage return left on a line
    // makes the byte-stable CSV header comparison fail and row mode never opens.
    [Fact]
    public void ACrlfLog_ParsesToTheSameRecordsAsTheLfForm()
    {
        var crlfText = Phd2LogFixtures.FullSampleLog
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Contains("\r\n", crlfText, StringComparison.Ordinal);

        var lf = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog));
        var crlf = Assert.Single(Phd2LogParser.Parse(crlfText));

        Assert.Equal(lf.LogEnabledAt, crlf.LogEnabledAt);
        Assert.Equal(lf.LogClosedAt, crlf.LogClosedAt);
        Assert.Equal(lf.Summary, crlf.Summary);
        Assert.Equal(lf.Warnings, crlf.Warnings);

        // Phd2Frame, Phd2Event, Phd2CalibrationStep and Phd2Header carry no collections, so
        // record value equality compares them field by field.
        Assert.Equal(lf.Sections[0].Header, crlf.Sections[0].Header);
        Assert.Equal(lf.Sections[0].Frames, crlf.Sections[0].Frames);
        Assert.Equal(lf.Sections[0].Events, crlf.Sections[0].Events);
        Assert.Equal(lf.Calibrations[0].Steps, crlf.Calibrations[0].Steps);
        Assert.Equal(5, crlf.Sections[0].Frames.Count);
    }

    // The ASIAIR Mount line ends "parity = +/+," and (\S+) takes the trailing comma with it,
    // exactly as the Python does. Task 4 stores the string with its comma, so this pins that the
    // comma is expected rather than something to clean up later. Failure shape: "+/+", which
    // would be a silent divergence from the web for the same file.
    [Fact]
    public void AnAsiairParity_KeepsTheTrailingCommaTheLineCarries()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog));
        Assert.All(run.Sections, s => Assert.Equal("+/+,", s.Header.Parity));
    }

    // Brief section 5 step 4: the Log Summary line closes nothing and changes no mode. Every
    // fixture writes it after the section has already closed, so a branch that dropped to mode
    // None would break nothing. Here it sits between the CSV header and the first row. Failure
    // shape: zero frames, because row mode was abandoned mid-section.
    [Fact]
    public void ALogSummaryLineInsideASection_ChangesNoMode()
    {
        var text = Phd2LogFixtures.FullSampleLog.Replace(
            "INFO: SETTLING STATE CHANGE, Settling started",
            "Log Summary: calcnt:1 gcnt:1 gdur:19 gacnt:5\nINFO: SETTLING STATE CHANGE, Settling started",
            StringComparison.Ordinal);
        var run = Assert.Single(Phd2LogParser.Parse(text));
        Assert.Equal(5, run.Sections[0].Frames.Count);
        Assert.NotNull(run.Summary);
        Assert.Empty(run.Warnings);
    }

    // --- The five Verify-clause fixtures --------------------------------------------------------

    [Fact]
    public void ADesktopLog_Parses()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.FullSampleLog));
        Assert.Single(run.Sections);
        Assert.Single(run.Calibrations);
        Assert.Equal(5, run.Sections[0].Frames.Count);
    }

    [Fact]
    public void AnAsiairLog_Parses()
    {
        var run = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.AsiairSampleLog));
        Assert.Equal(2, run.Sections.Count);
        Assert.Empty(run.Calibrations);
    }

    // Failure shape: one run with two sections, which is what happens when the banner does not
    // close the open section.
    [Fact]
    public void AStackedTwoRunFile_YieldsTwoRuns()
    {
        var runs = Phd2LogParser.Parse(
            Phd2LogFixtures.FullSampleLog + "\n" + Phd2LogFixtures.FullSampleLog);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, run =>
        {
            Assert.Single(run.Sections);
            Assert.Single(run.Calibrations);
        });
        Assert.NotSame(runs[0].Sections[0], runs[1].Sections[0]);
    }

    [Fact]
    public void ATruncatedTail_Parses()
    {
        var section = Assert.Single(Phd2LogParser.Parse(Phd2LogFixtures.TruncatedLog)[0].Sections);
        Assert.True(section.Truncated);
        Assert.Equal(2, section.Frames.Count);
    }

    [Fact]
    public void AnEmptyFile_Parses()
    {
        Assert.Empty(Phd2LogParser.Parse(""));
        Assert.Empty(Phd2LogParser.Parse("   \n\n"));
    }

    // --- Null guards and lazy line enumeration (fixer set C, phase review EnumerateLines item,
    // Task 2 review P3-3 and P3-6) --------------------------------------------------------------

    // Failure shape: a NullReferenceException from inside Parse, which is not the same thing as
    // "this text could not be read" and must not be recorded as unreadable by a caller that
    // catches FormatException.
    [Fact]
    public void Parse_NullText_ThrowsArgumentNullExceptionNamingTheParameter()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Phd2LogParser.Parse(null!));
        Assert.Equal("text", ex.ParamName);
    }

    // Failure shape: a NullReferenceException from inside ApplyHeaderLine, for the same reason.
    [Fact]
    public void ApplyHeaderLine_NullRawLine_ThrowsArgumentNullExceptionNamingTheParameter()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => Phd2LogParser.ApplyHeaderLine(new Phd2Header(), null!));
        Assert.Equal("rawLine", ex.ParamName);
    }

    // No separator at all after the last line: the lazy enumerator has to yield the tail once
    // the loop that looks for \r and \n runs out of text, not only at an explicit separator.
    // Failure shape: an empty run list, which is what a hand-rolled enumerator gives when it
    // forgets to yield the text after the last separator it found.
    [Fact]
    public void TextWithNoTrailingNewline_StillParsesItsLastLine()
    {
        var run = Assert.Single(Phd2LogParser.Parse(
            "PHD2 version 2.6.14 [Windows], Log version 2.5. Log enabled at 2026-01-19 17:21:18"));
        Assert.Equal("2.5", run.LogVersion);
        Assert.Equal(new DateTime(2026, 1, 19, 17, 21, 18), run.LogEnabledAt);
    }

    // A lone \r, with no \n on either side, is text that is only separators: every "line" it
    // splits into is empty. Failure shape: a thrown exception or an out-of-range index, which is
    // what an enumerator that assumes \r is always followed by \n would give.
    [Fact]
    public void TextThatIsOnlyASeparator_YieldsNoRuns()
    {
        Assert.Empty(Phd2LogParser.Parse("\r"));
    }
}
