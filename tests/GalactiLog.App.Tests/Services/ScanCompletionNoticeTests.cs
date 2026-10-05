using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Data.Entities;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Design-spec 12.11 behaviour 10's text, composed from one design-spec 5.13 row.
/// </summary>
/// <remarks>
/// Pure and fast: no database, no harness, no dispatcher. <c>ScanCompletionNotice.From</c> is the
/// one composer of this text, so the tooltip that ships today and the toast the spec records as
/// the upgrade path read the same sentence and cannot drift apart (design-lessons rule 1).
/// </remarks>
public class ScanCompletionNoticeTests
{
    private static ScanRun Run(
        string state = "complete",
        int discovered = 0,
        int newFiles = 0,
        int changedFiles = 0,
        int completed = 0,
        int failed = 0,
        int skippedCalibration = 0,
        int removed = 0,
        string? errorText = null)
        => new()
        {
            Id = 7,
            StartedAt = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            FinishedAt = new DateTime(2019, 3, 4, 5, 9, 7, DateTimeKind.Utc),
            Trigger = "manual",
            State = state,
            Discovered = discovered,
            NewFiles = newFiles,
            ChangedFiles = changedFiles,
            Completed = completed,
            Failed = failed,
            SkippedCalibration = skippedCalibration,
            Removed = removed,
            ErrorText = errorText,
        };

    [Fact]
    public void From_ACompletedRun_TitlesItComplete()
    {
        var notice = ScanCompletionNotice.From(Run(newFiles: 3, completed: 3));

        Assert.True(notice.Succeeded);
        Assert.Equal(ScanCompletionNotice.CompleteTitle, notice.Title);
        Assert.Equal("3 new files, 3 frames ingested", notice.Body);
    }

    [Fact]
    public void From_AFailedRun_TitlesItFailed_AndCarriesTheErrorText()
    {
        var notice = ScanCompletionNotice.From(
            Run(state: "failed", discovered: 12, failed: 2, errorText: "the catalogue is locked"));

        Assert.False(notice.Succeeded);
        Assert.Equal(ScanCompletionNotice.FailedTitle, notice.Title);
        Assert.Equal("12 files discovered, 2 failures. the catalogue is locked", notice.Body);
    }

    // Deviation from the brief's From_AnInterruptedRun_NamesTheState: design-spec 5.13 defines
    // four states and "interrupted" is not one of them. ScanRunRepository.MarkInterrupted writes
    // state "failed" with error_text "interrupted", so an interrupted run reads as a failure that
    // says what happened, and "cancelled" is the state that is neither complete nor failed. Both
    // halves are asserted, here and in the case below.
    [Fact]
    public void From_AnInterruptedRun_ReadsAsAFailureAndSaysInterrupted()
    {
        var notice = ScanCompletionNotice.From(Run(state: "failed", errorText: "interrupted"));

        Assert.False(notice.Succeeded);
        Assert.Equal(ScanCompletionNotice.FailedTitle, notice.Title);
        Assert.Equal("interrupted", notice.Body);
    }

    [Fact]
    public void From_ACancelledRun_NamesTheState()
    {
        var notice = ScanCompletionNotice.From(Run(state: "cancelled", discovered: 4));

        // Not a success, and not reported as one: the title carries the state the row holds.
        Assert.False(notice.Succeeded);
        Assert.Equal("Scan cancelled", notice.Title);
    }

    [Fact]
    public void From_ARunThatIsStillRunning_NamesTheState()
    {
        // ScanCoordinator schedules its pending follow-up run before it raises ScanFinished, so
        // the newest scan_runs row can already be the follow-up's "running" row by the time the
        // notice is composed. It reads as running rather than as a success.
        var notice = ScanCompletionNotice.From(Run(state: "running"));

        Assert.False(notice.Succeeded);
        Assert.Equal("Scan running", notice.Title);
    }

    [Fact]
    public void From_ARowWithNoState_StillTitlesSomething()
    {
        var notice = ScanCompletionNotice.From(Run(state: ""));

        Assert.False(notice.Succeeded);
        Assert.Equal(ScanCompletionNotice.UnknownStateTitle, notice.Title);
    }

    [Fact]
    public void From_ARunWithNothingNew_SaysSo_RatherThanListingZeroes()
    {
        var notice = ScanCompletionNotice.From(Run());

        // Design-spec 15: a scan that found nothing new is the normal result of a scheduled scan
        // and is not a failure, so it reads as one line rather than as seven zeroes.
        Assert.True(notice.Succeeded);
        Assert.Equal(ScanCompletionNotice.NothingNewBody, notice.Body);
        Assert.DoesNotContain("0", notice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void From_ListsOnlyNonZeroCounts_InSpecColumnOrder()
    {
        var notice = ScanCompletionNotice.From(
            Run(discovered: 3, changedFiles: 2, failed: 1, removed: 4));

        // Design-spec 5.13's column order: discovered, new, changed, completed, failed, skipped
        // calibration, removed.
        Assert.Equal("3 files discovered, 2 changed files, 1 failure, 4 rows removed", notice.Body);
    }

    [Fact]
    public void From_UsesSingularAndPluralLabels()
    {
        var one = ScanCompletionNotice.From(Run(
            discovered: 1, newFiles: 1, changedFiles: 1, completed: 1,
            failed: 1, skippedCalibration: 1, removed: 1));
        var many = ScanCompletionNotice.From(Run(
            discovered: 2, newFiles: 2, changedFiles: 2, completed: 2,
            failed: 2, skippedCalibration: 2, removed: 2));

        Assert.Equal(
            "1 file discovered, 1 new file, 1 changed file, 1 frame ingested, 1 failure, "
            + "1 calibration frame skipped, 1 row removed",
            one.Body);
        Assert.Equal(
            "2 files discovered, 2 new files, 2 changed files, 2 frames ingested, 2 failures, "
            + "2 calibration frames skipped, 2 rows removed",
            many.Body);
    }

    [Fact]
    public void From_TruncatesALongErrorText()
    {
        var notice = ScanCompletionNotice.From(
            Run(state: "failed", errorText: new string('x', 4000)));

        // A notification surface with no length limit does not exist.
        Assert.Equal(ScanCompletionNotice.ErrorTextMaxLength, notice.Body.Length);
        Assert.EndsWith("...", notice.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void From_DoesNotFormatATime()
    {
        var notice = ScanCompletionNotice.From(
            Run(state: "failed", discovered: 1, errorText: "disk full"));

        // The notice is about counts. No clock, no general.timezone, nothing that would make the
        // same row compose two different sentences on two machines.
        Assert.DoesNotContain("2019", notice.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("2019", notice.Body, StringComparison.Ordinal);

        // Structural rather than by convention: the composer names no clock type at all, so a
        // later edit cannot quietly put a formatted time into a notice the tooltip truncates.
        Assert.DoesNotContain(
            "ScanCompletionNotice.cs",
            SourceScan.FilesMatching(@"DateTime|TimeZoneInfo|ToLocalTime"));
    }

    // ---- the terminal-state vocabulary (fix round 1) -------------------------------------------

    [Theory]
    [InlineData("complete", true)]
    [InlineData("cancelled", true)]
    [InlineData("failed", true)]
    [InlineData("running", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("pending", false)]
    public void IsTerminalState_MatchesSpec513sThreeFinishedStates(string? state, bool expected)
    {
        // Spec 5.13 defines four states and only three of them belong to a run that has finished.
        // The vocabulary lives here, beside the titles composed from it, rather than in the
        // watcher that consumes it (design-lessons rule 1).
        Assert.Equal(expected, ScanCompletionNotice.IsTerminalState(state));
    }

    [Fact]
    public void IsTerminalState_IgnoresCaseAndSurroundingSpace()
    {
        Assert.True(ScanCompletionNotice.IsTerminalState(" Complete "));
        Assert.False(ScanCompletionNotice.IsTerminalState(" Running "));
    }

    [Fact]
    public void From_IsPureOverOneRow()
    {
        var run = Run(state: "complete", discovered: 9, newFiles: 2);

        // Same row, same sentence, twice, with nothing else in the process changed: the property
        // that makes one composer safe to call from the tooltip, from a later toast, and from a
        // test.
        Assert.Equal(ScanCompletionNotice.From(run), ScanCompletionNotice.From(run));
    }
}
