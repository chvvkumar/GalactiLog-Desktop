using System.Globalization;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

// The export wizard over the shared page, with a fake StagingIo
// so no case copies a frame. Every path is a temp folder or the fictional library.
public class WbppExportWizardTests
{
    private static readonly DateOnly N1 = new(2025, 3, 20);

    private static string Ha(string file) => Path.Combine(
        Library.Root, "M31", N1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Ha", file);

    private sealed class Rig : IDisposable
    {
        public Rig(bool parked = false, Library? library = null, StagingCopyService? copies = null, JobRegistry? jobs = null)
        {
            Harness = ExportHarness.Create(library ?? new Library().Frame(N1, Ha("a.fits")), [N1], parked: parked);
            Jobs = jobs ?? new JobRegistry(action => action());
            Wizard = new WbppExportWizardViewModel(
                Harness.Page,
                _ => Task.CompletedTask,
                copies ?? new StagingCopyService(Jobs, (s, e, m, d) => Activity.Add((s, e, m, d))),
                openFolder: Opened.Add,
                runScript: Ran.Add,
                post: action => action());
        }

        public List<(string Severity, string EventType, string Message, object Details)> Activity { get; } = [];

        public List<string> Opened { get; } = [];

        public List<string> Ran { get; } = [];

        public ExportHarness Harness { get; }

        public JobRegistry Jobs { get; }

        public WbppExportWizardViewModel Wizard { get; }

        public int IoCalls;

        /// <summary>Installs a fake disk: every delegate counts, Enumerate runs <paramref name="onEnumerate"/>.</summary>
        public void FakeIo(Func<IEnumerable<FileSystemInfo>> onEnumerate, long? existingLength = null)
            => Harness.Page.StagingIoFor = _ => new StagingIo(
                _ =>
                {
                    Interlocked.Increment(ref IoCalls);
                    return new MemoryStream();
                },
                _ =>
                {
                    Interlocked.Increment(ref IoCalls);
                    return onEnumerate();
                },
                _ =>
                {
                    Interlocked.Increment(ref IoCalls);
                    return existingLength;
                },
                _ => Interlocked.Increment(ref IoCalls),
                _ =>
                {
                    Interlocked.Increment(ref IoCalls);
                    return new MemoryStream();
                });

        public async Task ToReviewAsync(string? stagingRoot = null)
        {
            Harness.Page.StagingPath = stagingRoot ?? Harness.TempFolder("staging");
            Harness.Page.CommitStagingCommand.Execute(null);
            for (var i = 0; i < 4; i++)
            {
                await Wizard.NextCommand.ExecuteAsync(null);
            }

            Assert.True(Wizard.IsReviewStep);
        }

        // Blocks a copier pool thread until the case opens the gate; outside any test body, because
        // xUnit1031 is an error here.
        public static void Park(TaskCompletionSource entered, ManualResetEventSlim gate)
        {
            entered.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(30));
        }

        public void Dispose()
        {
            Wizard.Dispose();
            Harness.Dispose();
        }
    }

    // A failure here is a step out of R3's order, or a step whose header names the wrong part.
    [Fact]
    public void Steps_AreInOrder_WithTheirTitlesAndTopics()
    {
        using var rig = new Rig();

        Assert.Equal(
            ["Folders to copy", "Quality filter (optional)", "Staging folder", "Copy or script", "Review", "Result"],
            rig.Wizard.Steps.Select(step => step.Title));
        Assert.Equal(WbppExportStepViewModel.HelpTopicIds, rig.Wizard.Steps.Select(step => step.HelpTopicId));
        Assert.Equal("Step 1 of 6: Folders to copy", rig.Wizard.StepHeader);
        Assert.False(rig.Wizard.Method.IsScript);
    }

    // A failure here is a Next that leaves the folders step before any level is known.
    [Fact]
    public void Folders_RefusesNext_WhileLoading()
    {
        using var rig = new Rig(parked: true);

        Assert.False(rig.Wizard.CanGoNext);
        Assert.False(rig.Wizard.NextCommand.CanExecute(null));

        rig.Harness.Release();

        Assert.True(rig.Wizard.CanGoNext);
    }

    // Tier 1. A failure here is a copy that touches the disk before the wizard has locked (R3, R4).
    [Fact]
    public async Task CommitWithCopy_SetsIsCommitted_BeforeTheIoSeamSeesItsFirstCall()
    {
        using var rig = new Rig();
        var committedAtFirstCall = new List<bool>();
        rig.FakeIo(() =>
        {
            lock (committedAtFirstCall)
            {
                committedAtFirstCall.Add(rig.Wizard.IsCommitted);
            }

            return [];
        });
        await rig.ToReviewAsync();

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.True(committedAtFirstCall.Count > 0, "the copy never reached the io seam");
        Assert.All(committedAtFirstCall, committed => Assert.True(committed));
        Assert.Equal(5, rig.Wizard.StepIndex);
        Assert.Equal(StagingOutcome.Completed, rig.Wizard.Result.Result!.Outcome);
    }

    // A failure here is a Cancel that is lost, or a cancelled run reported as completed (R4).
    [Fact]
    public async Task Cancel_DuringTheCopy_YieldsACancelledResult()
    {
        using var rig = new Rig();
        rig.FakeIo(() =>
        {
            rig.Wizard.CancelCopyCommand.Execute(null);
            return [];
        });
        await rig.ToReviewAsync();

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.Equal(5, rig.Wizard.StepIndex);
        Assert.Equal(StagingOutcome.Cancelled, rig.Wizard.Result.Result!.Outcome);
        Assert.False(rig.Wizard.IsBusy);
    }

    // A failure here is a Cancel button left disabled for the whole copy: the view only re-reads
    // CanExecute when CanExecuteChanged fires.
    [Fact]
    public async Task CancelCopy_BecomesExecutableOnceTheCopyStarts()
    {
        using var rig = new Rig();
        var enabledAtEvent = new List<bool>();
        rig.Wizard.CancelCopyCommand.CanExecuteChanged +=
            (_, _) => enabledAtEvent.Add(rig.Wizard.CancelCopyCommand.CanExecute(null));
        rig.FakeIo(() => []);
        await rig.ToReviewAsync();

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.Contains(true, enabledAtEvent);
    }

    // A failure here is a different-size file that is not listed, or no Save report beside it (R5, R14).
    [Fact]
    public async Task AResultWithADifferentSizeSkip_ListsItAndOffersSaveReport()
    {
        using var rig = new Rig();
        var source = new FileInfo(Path.Combine(rig.Harness.TempFolder("src"), "a.fits"));
        await using (var stream = source.Create())
        {
            stream.WriteByte(1);
        }

        source.Refresh();
        rig.FakeIo(() => [source], existingLength: 999);
        await rig.ToReviewAsync();

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        var skip = Assert.Single(rig.Wizard.Result.Result!.Skipped);
        Assert.Equal(StagingSkipReason.ExistsDifferentSize, skip.Reason);
        Assert.Equal([skip.Path], rig.Wizard.Result.DifferentSizePaths);
        Assert.True(rig.Wizard.Result.HasProblems);
        // Review finding 10: the window's copy writes its Activity row through the injected service.
        var row = Assert.Single(rig.Activity);
        Assert.Equal(("warning", StagingCopyService.CopyJobKind), (row.Severity, row.EventType));
    }

    // A failure here is a restart that keeps the lock, the old result or the result step (R14).
    [Fact]
    public async Task StartAnotherExport_LandsOnStepOne_Unlocked()
    {
        using var rig = new Rig();
        rig.FakeIo(() => []);
        await rig.ToReviewAsync();
        await rig.Wizard.CommitCommand.ExecuteAsync(null);
        Assert.True(rig.Wizard.IsCommitted);

        rig.Wizard.StartAnotherCommand.Execute(null);

        Assert.Equal(0, rig.Wizard.StepIndex);
        Assert.False(rig.Wizard.IsCommitted);
        Assert.Null(rig.Wizard.Result.Result);
        Assert.True(rig.Wizard.CanGoNext);
    }

    // Tier 1. A failure here is a Script commit that also copies, or that writes no script (R2, R15).
    [Fact]
    public async Task ScriptMethod_WritesExactlyOneFile_AndNothingThroughTheStagingSeam()
    {
        using var rig = new Rig();
        rig.FakeIo(() => []);
        rig.Harness.Destination = rig.Harness.ScriptPath("wbpp_M_31.ps1");
        await rig.ToReviewAsync();
        rig.Wizard.Method.IsScript = true;

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.True(File.Exists(rig.Harness.Destination));
        Assert.Single(rig.Harness.SuggestedNames);
        Assert.Equal(0, rig.IoCalls);
        Assert.Equal(5, rig.Wizard.StepIndex);
        Assert.True(rig.Wizard.Result.IsScriptResult);
        Assert.Equal(rig.Harness.Destination, rig.Harness.Page.ScriptPath);
    }

    // A failure here is a Close that is refused during a copy, or one that stops the copy.
    [Fact]
    public async Task Close_IsAllowed_WhileTheCopyRuns_AndTheCopyCarriesOn()
    {
        using var rig = new Rig();
        bool? closedWithWrote = null;
        rig.Wizard.CloseRequested += (_, wrote) => closedWithWrote = wrote;
        bool? canCloseDuringCopy = null;
        rig.FakeIo(() =>
        {
            canCloseDuringCopy = rig.Wizard.CloseCommand.CanExecute(null);
            rig.Wizard.CloseCommand.Execute(null);
            return [];
        });
        await rig.ToReviewAsync();

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.True(canCloseDuringCopy);
        Assert.True(closedWithWrote);
        Assert.Equal(JobResult.Succeeded, Assert.Single(rig.Jobs.Recent).Result);
    }

    // Reverses ruling E10: closing hands the copy to StagingCopyService instead of cancelling it.
    // A failure here is a close during a copy that waits for the copy, or that stops it.
    [Fact]
    public async Task Dispose_DuringAPendingCopy_HandsItOff_WithoutWaiting()
    {
        var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim(false);
        rig.FakeIo(() =>
        {
            Rig.Park(entered, gate);
            return [];
        });
        await rig.ToReviewAsync();

        var commit = rig.Wizard.CommitCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var progressText = rig.Wizard.Review.ProgressText;

        rig.Wizard.Dispose();

        Assert.False(commit.IsCompleted, "Dispose waited for the copy");
        gate.Set();
        await commit.WaitAsync(TimeSpan.FromSeconds(30));
        // The disposed wizard publishes nothing; the status bar's job records the outcome.
        Assert.Equal(JobResult.Succeeded, Assert.Single(rig.Jobs.Recent).Result);
        Assert.Null(rig.Wizard.Result.Result);
        Assert.Equal(progressText, rig.Wizard.Review.ProgressText);
        rig.Harness.Dispose();
    }

    // Gap G1. A failure here is a Close button left disabled for the whole copy: IsBusy is raised
    // before IsCopying flips, and the view only re-reads CanExecute on CanExecuteChanged.
    [Fact]
    public async Task CloseCommand_IsEnabledByEvent_WhenTheCopyStarts()
    {
        using var rig = new Rig();
        var enabledAtEvent = new List<bool>();
        rig.FakeIo(() => []);
        await rig.ToReviewAsync();
        rig.Wizard.CloseCommand.CanExecuteChanged +=
            (_, _) => enabledAtEvent.Add(rig.Wizard.IsBusy && rig.Wizard.CloseCommand.CanExecute(null));

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.Contains(true, enabledAtEvent);
    }

    // A failure here is a Close that lets the window go while a script's save dialog is still open.
    [Fact]
    public async Task Close_IsStillRefused_WhileAScriptCommitRuns()
    {
        using var rig = new Rig();
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Harness.Page.ScriptDestinationPicker = _ => picked.Task;
        await rig.ToReviewAsync();
        rig.Wizard.Method.IsScript = true;

        var commit = rig.Wizard.CommitCommand.ExecuteAsync(null);
        var closed = false;
        rig.Wizard.CloseRequested += (_, _) => closed = true;

        Assert.True(rig.Wizard.IsBusy);
        Assert.False(rig.Wizard.CloseCommand.CanExecute(null));
        // Review finding 9: Execute ignores CanExecute, so the guard in Close's body is what holds.
        rig.Wizard.CloseCommand.Execute(null);
        Assert.False(closed);
        picked.SetResult(null);
        await commit.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(rig.Wizard.CloseCommand.CanExecute(null));
    }

    // Decision 10. A failure here is a second export copying into a folder another copy is still
    // filling, or one that lands on the result step as a failed copy.
    [Fact]
    public async Task Commit_IntoAFolderAlreadyCopying_StaysOnReview_WithTheRefusal()
    {
        var jobs = new JobRegistry(action => action());
        var copies = new StagingCopyService(jobs);
        using var first = new Rig(copies: copies, jobs: jobs);
        using var second = new Rig(copies: copies, jobs: jobs);
        var staging = first.Harness.TempFolder("staging");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim(false);
        first.FakeIo(() =>
        {
            Rig.Park(entered, gate);
            return [];
        });
        second.FakeIo(() => []);
        await first.ToReviewAsync(staging);
        await second.ToReviewAsync(staging);
        var commit = first.Wizard.CommitCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            await second.Wizard.CommitCommand.ExecuteAsync(null);

            Assert.True(second.Wizard.IsReviewStep);
            Assert.Contains("is still running", second.Wizard.StepError, StringComparison.Ordinal);
            Assert.False(second.Wizard.IsCommitted);
            Assert.False(second.Wizard.IsBusy);
            Assert.Equal(0, second.IoCalls);
        }
        finally
        {
            gate.Set();
            await commit.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    // Review finding 3. A failure here is a script, which copies with -Force, written for a folder
    // an in-app copy is still filling.
    [Fact]
    public async Task AScriptCommit_IntoAFolderAlreadyCopying_StaysOnReview_WithTheRefusal()
    {
        var jobs = new JobRegistry(action => action());
        var copies = new StagingCopyService(jobs);
        using var first = new Rig(copies: copies, jobs: jobs);
        using var second = new Rig(copies: copies, jobs: jobs);
        var staging = first.Harness.TempFolder("staging");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim(false);
        first.FakeIo(() =>
        {
            Rig.Park(entered, gate);
            return [];
        });
        second.Harness.Destination = second.Harness.ScriptPath("wbpp_M_31.ps1");
        await first.ToReviewAsync(staging);
        await second.ToReviewAsync(staging);
        second.Wizard.Method.IsScript = true;
        var commit = first.Wizard.CommitCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            await second.Wizard.CommitCommand.ExecuteAsync(null);

            Assert.True(second.Wizard.IsReviewStep);
            Assert.Contains("is still running", second.Wizard.StepError, StringComparison.Ordinal);
            Assert.False(second.Wizard.IsCommitted);
            Assert.Empty(second.Harness.SuggestedNames);
            Assert.False(File.Exists(second.Harness.Destination));
        }
        finally
        {
            gate.Set();
            await commit.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    // Review finding 7. A failure here is a footer Cancel that changes nothing on screen while the
    // files in flight finish.
    [Fact]
    public async Task CancelCopy_SaysCancelling_AtOnce()
    {
        using var rig = new Rig();
        string? textAfterCancel = null;
        rig.FakeIo(() =>
        {
            rig.Wizard.CancelCopyCommand.Execute(null);
            textAfterCancel = rig.Wizard.Review.ProgressText;
            return [];
        });
        await rig.ToReviewAsync();

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.Equal(StagingCopyService.CancellingText, textAfterCancel);
    }

    // Ruling E14 (R2: one staging folder for both routes). A failure here is a script that copies
    // into the bare staging root while the review step and the in-app copy name the subfolder.
    [Fact]
    public async Task ScriptMethod_WithTheSubfolderBoxTicked_GeneratesAgainstCopyDestination()
    {
        using var rig = new Rig();
        rig.Harness.Destination = rig.Harness.ScriptPath("wbpp_M_31.ps1");
        await rig.ToReviewAsync();
        rig.Wizard.Method.IsScript = true;
        Assert.True(rig.Harness.Page.IsSubfolderOn);

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        var destination = rig.Harness.Page.CopyDestination!;
        Assert.EndsWith(rig.Harness.Page.SubfolderName, destination, StringComparison.Ordinal);
        Assert.Contains(destination, rig.Harness.Page.ScriptText!, StringComparison.Ordinal);
    }

    // Ruling E15. A failure here is a long-path figure measured against the bare root while the
    // copy and the script both write into the subfolder.
    [Fact]
    public void TickingTheSubfolderBox_RaisesTheLongestDestinationBySubfolderPlusSeparator()
    {
        using var rig = new Rig();
        var page = rig.Harness.Page;
        page.StagingPath = rig.Harness.TempFolder("staging");
        page.CommitStagingCommand.Execute(null);
        page.IsSubfolderOn = false;
        var bare = page.LongestDestinationLength;
        Assert.True(bare > 0);

        page.IsSubfolderOn = true;

        Assert.Equal(bare + page.SubfolderName.Length + 1, page.LongestDestinationLength);
    }

    // Review blocker 1. A failure here is a Commit whose last CanExecuteChanged was raised against
    // the previous visit's blocks, so the button stays disabled after the block clears.
    [Fact]
    public async Task Commit_IsReEnabled_WhenABlockedReviewIsFixedAndRevisited()
    {
        using var rig = new Rig();
        var frame = rig.Harness.Library.Ids[Ha("a.fits")];
        rig.Harness.Page.SetExcludedFrames([frame]);
        await rig.ToReviewAsync();
        Assert.True(rig.Wizard.Review.IsBlocked);

        rig.Wizard.BackCommand.Execute(null);
        rig.Harness.Page.SetExcludedFrames([]);
        var seen = new List<bool>();
        rig.Wizard.CommitCommand.CanExecuteChanged += (_, _) => seen.Add(rig.Wizard.CommitCommand.CanExecute(null));
        await rig.Wizard.NextCommand.ExecuteAsync(null);

        Assert.False(rig.Wizard.Review.IsBlocked);
        Assert.True(seen.Count > 0 && seen[^1], "the last CanExecuteChanged reported Commit disabled");
    }

    // Review blocker 2. A failure here is a second script whose save dialog was cancelled being
    // reported as written, because the first script's state survived Start another export.
    [Fact]
    public async Task ASecondScriptWithACancelledDialog_StaysOnReview_ClaimingNothing()
    {
        using var rig = new Rig();
        rig.Harness.Destination = rig.Harness.ScriptPath("wbpp_M_31.ps1");
        await rig.ToReviewAsync();
        rig.Wizard.Method.IsScript = true;
        await rig.Wizard.CommitCommand.ExecuteAsync(null);
        Assert.Equal(5, rig.Wizard.StepIndex);

        rig.Wizard.StartAnotherCommand.Execute(null);
        Assert.False(rig.Harness.Page.HasScript);
        rig.Harness.Destination = null;
        for (var i = 0; i < 4; i++)
        {
            await rig.Wizard.NextCommand.ExecuteAsync(null);
        }

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.Equal(4, rig.Wizard.StepIndex);
        Assert.False(rig.Wizard.IsCommitted);
        Assert.Null(rig.Harness.Page.ScriptPath);
    }

    // Review blocker 3. A failure here is Open folder handing a path that is not a folder on disk
    // to the shell, which would launch whatever sits there.
    [Fact]
    public async Task OpenFolder_IsDisabled_UntilTheDestinationFolderExists()
    {
        using var rig = new Rig();
        rig.FakeIo(() => []);
        await rig.ToReviewAsync();
        Assert.False(rig.Wizard.OpenFolderCommand.CanExecute(null));

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.False(rig.Wizard.OpenFolderCommand.CanExecute(null));
        rig.Wizard.OpenFolderCommand.Execute(null);
        Assert.Empty(rig.Opened);

        rig.Harness.TempFolder(Path.Combine("staging", rig.Harness.Page.SubfolderName));
        Assert.True(rig.Wizard.OpenFolderCommand.CanExecute(null));
        rig.Wizard.OpenFolderCommand.Execute(null);
        Assert.Equal([rig.Harness.Page.CopyDestination!], rig.Opened);
    }

    // A failure here is Run script reachable before a PowerShell script stands on the result
    // step, or one that hands the shell a path other than the file this export wrote.
    [Fact]
    public async Task RunScript_IsDisabled_UntilAPowerShellScriptStandsOnTheResultStep()
    {
        using var rig = new Rig();
        rig.Harness.Destination = rig.Harness.ScriptPath("wbpp_M_31.ps1");
        await rig.ToReviewAsync();
        rig.Wizard.Method.IsScript = true;
        Assert.False(rig.Wizard.RunScriptCommand.CanExecute(null));
        rig.Wizard.RunScriptCommand.Execute(null);
        Assert.Empty(rig.Ran);

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.Equal(5, rig.Wizard.StepIndex);
        Assert.Equal(WbppScriptType.PowerShell, rig.Harness.Page.GeneratedScriptType);
        Assert.True(rig.Wizard.RunScriptCommand.CanExecute(null));
        rig.Wizard.RunScriptCommand.Execute(null);
        Assert.Equal([rig.Harness.Page.ScriptPath!], rig.Ran);
    }

    // Review risk 6. A failure here is an unexpected exception that escapes the commit and leaves
    // the wizard locked on the review step.
    [Fact]
    public async Task AnUnexpectedCopyException_LandsOnTheResultStep_Unlockable()
    {
        using var rig = new Rig();
        rig.Harness.Page.StagingIoFor = _ => throw new InvalidOperationException("seam exploded");
        await rig.ToReviewAsync();

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.Equal(5, rig.Wizard.StepIndex);
        Assert.Equal(StagingOutcome.Aborted, rig.Wizard.Result.Result!.Outcome);
        Assert.Contains("seam exploded", rig.Wizard.Result.OutcomeText, StringComparison.Ordinal);
        Assert.True(rig.Wizard.StartAnotherCommand.CanExecute(null));
    }

    // Review nit. A failure here is Start another export reachable before the result step.
    [Fact]
    public void StartAnother_IsDisabled_OffTheResultStep()
    {
        using var rig = new Rig();

        Assert.False(rig.Wizard.StartAnotherCommand.CanExecute(null));
    }

    // R11 blocks. Each failure is a commit allowed into a destination the review must refuse.
    [Fact]
    public async Task Review_BlocksADestinationInsideAScanRoot()
    {
        using var rig = new Rig();
        await rig.ToReviewAsync();
        rig.Harness.SetScanRoots(Library.Root, rig.Harness.Page.CopyDestination!);

        rig.Wizard.Review.OnEntered();

        Assert.Contains(WbppExportViewModel.ScanRootRefusal, rig.Wizard.Review.Blocks);
        Assert.False(rig.Wizard.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Review_BlocksADriveRoot()
    {
        using var rig = new Rig();
        rig.Harness.Page.IsSubfolderOn = false;

        await rig.ToReviewAsync(@"Q:\");

        Assert.Equal([WbppExportViewModel.DriveRootRefusal], rig.Wizard.Review.Blocks);
        Assert.False(rig.Wizard.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Review_BlocksZeroFramesAfterTheFilter()
    {
        using var rig = new Rig();
        rig.Harness.Page.SetExcludedFrames([rig.Harness.Library.Ids[Ha("a.fits")]]);

        await rig.ToReviewAsync();

        Assert.Contains(rig.Wizard.Review.Blocks, block => block.Contains("no frames", StringComparison.Ordinal));
    }

    // R11 warnings. Each failure is a hazard the review does not name, or one that blocks.
    [Fact]
    public async Task Review_WarnsWhenFreeSpaceIsShort()
    {
        using var rig = new Rig(library: new Library().Frame(N1, Ha("a.fits"), size: 1L << 60));

        await rig.ToReviewAsync();

        Assert.Contains(rig.Wizard.Review.Warnings, warning => warning.Contains(" free, less than ", StringComparison.Ordinal));
        Assert.False(rig.Wizard.Review.IsBlocked);
    }

    [Fact]
    public async Task Review_WarnsOnALongDestinationPath()
    {
        using var rig = new Rig(library: new Library().Frame(N1, Ha(new string('a', 250) + ".fits")));

        await rig.ToReviewAsync();

        Assert.Contains(rig.Wizard.Review.Warnings, warning => warning.Contains("PixInsight", StringComparison.Ordinal));
        Assert.False(rig.Wizard.Review.IsBlocked);
    }

    [Fact]
    public async Task Review_WarnsWhenTheDestinationAlreadyHoldsFiles()
    {
        using var rig = new Rig();
        var existing = rig.Harness.TempFolder(Path.Combine("staging", rig.Harness.Page.SubfolderName));
        await using (var stream = new FileStream(Path.Combine(existing, "old.fits"), FileMode.CreateNew))
        {
            stream.WriteByte(1);
        }

        await rig.ToReviewAsync(Path.GetDirectoryName(existing));

        Assert.Contains(rig.Wizard.Review.Warnings, warning => warning.Contains("already holds 1 file.", StringComparison.Ordinal));
    }

    // R14. A failure here is Save report offered for a clean run.
    [Fact]
    public async Task SaveReport_IsHidden_WhenNothingWasSkippedOrFailed()
    {
        using var rig = new Rig();
        rig.FakeIo(() => []);
        await rig.ToReviewAsync();

        await rig.Wizard.CommitCommand.ExecuteAsync(null);

        Assert.Equal(StagingOutcome.Completed, rig.Wizard.Result.Result!.Outcome);
        Assert.False(rig.Wizard.Result.HasProblems);
    }

    // A failure here is a cancel that left only part-written files with no Save report to list them.
    [Fact]
    public void SaveReport_IsOffered_WhenOnlyPartialFilesRemain()
    {
        using var rig = new Rig();

        rig.Wizard.Result.Result = new StagingCopyResult(StagingOutcome.Cancelled, 0, 0, [], [], [@"X:\a.fits"], null);

        Assert.True(rig.Wizard.Result.HasProblems);
        Assert.Contains(@"Partial: X:\a.fits", rig.Wizard.Result.ReportText, StringComparison.Ordinal);
    }
}
