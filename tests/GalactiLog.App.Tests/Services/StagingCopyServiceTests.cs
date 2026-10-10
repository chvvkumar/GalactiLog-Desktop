using GalactiLog.App.Services;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// The stacking copy's app-lifetime owner over fake runs: each run is a delegate that reports what
// it is told and waits on a TaskCompletionSource, so no case copies a file or blocks a thread. The
// registry posts synchronously unless a case says otherwise.
public class StagingCopyServiceTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "galactilog-staging-service");

    private readonly JobRegistry _jobs = new(action => action());
    private readonly List<(string Severity, string EventType, string Message, object Details)> _rows = [];

    private StagingCopyService Service(JobRegistry? jobs = null)
        => new(jobs ?? _jobs, (severity, eventType, message, details) => _rows.Add((severity, eventType, message, details)));

    private static string Folder(string name) => Path.Combine(Root, name);

    private static StagingCopyResult Clean(int copied = 1)
        => new(StagingOutcome.Completed, copied, 100 * copied, [], [], [], null);

    private static StagingCopyResult Cancelled(int copied = 0, params string[] partials)
        => new(StagingOutcome.Cancelled, copied, 100 * copied, [], [], partials, null);

    // A run that waits for the case to release it and ends Cancelled when its token fires.
    private static Func<IProgress<StagingProgress>, CancellationToken, Task<StagingCopyResult>> Parked(
        TaskCompletionSource<StagingCopyResult> release, TaskCompletionSource? entered = null, int copiedOnCancel = 0)
        => async (_, ct) =>
        {
            entered?.TrySetResult();
            var cancelled = Task.Delay(Timeout.Infinite, ct);
            var first = await Task.WhenAny(release.Task, cancelled);
            return first == release.Task ? await release.Task : Cancelled(copiedOnCancel);
        };

    // A failure here is a status bar row with no count, or a count trimmed off behind the stats.
    [Fact]
    public async Task Start_RegistersOneJob_WhoseMessageCarriesCountAndStats()
    {
        var service = Service();
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = service.StartAsync("M 31", Folder("a"), async (progress, _) =>
        {
            progress.Report(new StagingProgress(1, 2, 50, 100, 50, "a.fits"));
            return await release.Task;
        }, null, CancellationToken.None);

        var job = Assert.Single(_jobs.Running);
        Assert.Equal(StagingCopyService.CopyJobKind, job.Kind);
        Assert.Equal("Copy for stacking: M 31", job.Title);
        // Review finding 6: count first, no byte totals (the bar carries those), so the speed and
        // time left fit the flyout's one line once a second has passed.
        Assert.Equal("Copying 1 of 2 files", job.Message);
        Assert.Equal(50d, job.Percent);

        release.SetResult(Clean());
        await run;
        Assert.Equal(JobResult.Succeeded, Assert.Single(_jobs.Recent).Result);
    }

    // A failure here is a status bar Cancel that does not reach the copier.
    [Fact]
    public async Task TheStatusBarCancel_CancelsTheTokenTheRunSees_AndFinishesCancelled()
    {
        var service = Service();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = service.StartAsync("M 31", Folder("a"), Parked(release, entered), null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        _jobs.Running.Single().CancelCommand.Execute(null);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(StagingOutcome.Cancelled, result.Outcome);
        Assert.Equal(JobResult.Cancelled, Assert.Single(_jobs.Recent).Result);
    }

    // Review finding 7. A failure here is a status bar Cancel with no visible effect while the
    // files in flight finish, or a later report that puts "Copying" back.
    [Fact]
    public async Task TheStatusBarCancel_SaysCancelling_AtOnceAndThroughLaterReports()
    {
        var service = Service();
        var reported = new TaskCompletionSource<IProgress<StagingProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = service.StartAsync("M 31", Folder("a"), async (progress, _) =>
        {
            progress.Report(new StagingProgress(1, 4, 25, 100, 25, "a.fits"));
            reported.SetResult(progress);
            return await release.Task;
        }, null, CancellationToken.None);
        var progress = await reported.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var row = _jobs.Running.Single();
        row.CancelCommand.Execute(null);
        Assert.Equal((StagingCopyService.CancellingText, 25d), (row.Message, row.Percent));

        progress.Report(new StagingProgress(2, 4, 50, 100, 50, "b.fits"));
        Assert.Equal((StagingCopyService.CancellingText, 50d), (row.Message, row.Percent));

        release.SetResult(Cancelled(2));
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // Review finding 1. A failure here is a finished row in Recent that keeps the viewer, and
    // through it the closed export window, alive.
    [Fact]
    public async Task AFinishedRow_DoesNotKeepTheViewerAlive()
    {
        var service = Service();
        var viewer = await RunWithViewer(service);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Single(_jobs.Recent);
        Assert.False(viewer.TryGetTarget(out _));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<IProgress<StagingProgress>>> RunWithViewer(StagingCopyService service)
    {
        IProgress<StagingProgress> viewer = new Progress<StagingProgress>();
        await service.StartAsync("M 31", Folder("a"), (_, _) => Task.FromResult(Clean()), viewer, CancellationToken.None);
        return new WeakReference<IProgress<StagingProgress>>(viewer);
    }

    // Review finding 2. A failure here is an exit that cuts files short and leaves no list of them.
    [Fact]
    public async Task WaitForIdle_OnTimeout_NamesEachFileStillOpen()
    {
        var logger = new TestSupport.RecordingLogger();
        var service = new StagingCopyService(_jobs, logger: logger);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new StagingIo(_ => Stream.Null, _ => [], _ => null, _ => { }, _ => new MemoryStream());
        var path = Path.Combine(Folder("a"), "light.fits");
        var run = service.StartAsync("M 31", Folder("a"), async (_, track, _) =>
        {
            var tracked = track(io);
            await using (tracked.CreateDestination(path))
            {
                opened.SetResult();
                await release.Task;
            }

            await using (tracked.CreateDestination(Path.Combine(Folder("a"), "closed.fits")))
            {
            }

            return Clean();
        }, null, CancellationToken.None);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            service.CancelAll();
            Assert.False(service.WaitForIdle(TimeSpan.FromMilliseconds(100)));
            Assert.Contains(logger.Entries, entry => entry.Message.EndsWith(": " + path, StringComparison.Ordinal));
        }
        finally
        {
            release.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    // A failure here is an ObjectDisposedException from a Cancel pressed on a row whose Finish has
    // not been posted yet, after the run disposed its source.
    [Fact]
    public async Task TheStatusBarCancel_AfterTheRunEnded_DoesNotThrow()
    {
        var queued = new List<Action>();
        var jobs = new JobRegistry(queued.Add);
        var service = Service(jobs);

        await service.StartAsync("M 31", Folder("a"), (_, _) => Task.FromResult(Clean()), null, CancellationToken.None);
        queued[0]();

        var row = Assert.Single(jobs.Running);
        Assert.True(row.CanCancel);
        row.CancelCommand.Execute(null);
    }

    // A failure here is a copy that stops when the window that started it goes away.
    [Fact]
    public async Task TheCopy_OutlivesTheCallersToken()
    {
        var service = Service();
        var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = service.StartAsync("M 31", Folder("a"), Parked(release, entered), null, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        caller.Dispose();
        release.SetResult(Clean());

        Assert.Equal(StagingOutcome.Completed, (await run.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.Equal(JobResult.Succeeded, Assert.Single(_jobs.Recent).Result);
    }

    // Design rule 2: the overlap check lives in the service. A failure here is two copies into one
    // tree at once, or a folder that stays refused after its copy ended.
    [Fact]
    public async Task ASecondStart_IntoTheSameParentOrChildFolder_IsRefused_WithoutRunningOrAJob()
    {
        var service = Service();
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.StartAsync("M 31", Folder("a"), Parked(release), null, CancellationToken.None);
        var sibling = service.StartAsync("M 33", Folder("b"), Parked(release), null, CancellationToken.None);
        Assert.Equal(2, _jobs.Running.Count);

        var ran = 0;
        foreach (var folder in new[] { Folder("a"), Path.Combine(Folder("a"), "sub"), Root })
        {
            Assert.NotNull(service.RefusalFor(folder));
            var refused = await service.StartAsync("M 31", folder, (_, _) =>
            {
                ran++;
                return Task.FromResult(Clean());
            }, null, CancellationToken.None);
            Assert.Equal(StagingOutcome.Aborted, refused.Outcome);
            Assert.Contains("is still running", refused.AbortReason, StringComparison.Ordinal);
        }

        Assert.Equal(0, ran);
        Assert.Equal(2, _jobs.Running.Count);

        release.SetResult(Clean());
        await Task.WhenAll(first, sibling).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(service.RefusalFor(Folder("a")));
        Assert.Equal(StagingOutcome.Completed,
            (await service.StartAsync("M 31", Folder("a"), (_, _) => Task.FromResult(Clean()), null, CancellationToken.None)).Outcome);
    }

    // A failure here is a run whose synchronous throw leaves its folder locked for the session.
    [Fact]
    public async Task ARunThatThrowsSynchronously_ReleasesItsDestination_AndCompletesAborted()
    {
        var service = Service();

        var result = await service.StartAsync(
            "M 31", Folder("a"), (_, _) => throw new InvalidOperationException("seam exploded"), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Aborted, result.Outcome);
        Assert.Contains("seam exploded", result.AbortReason, StringComparison.Ordinal);
        Assert.Null(service.RefusalFor(Folder("a")));
        Assert.Equal(JobResult.Failed, Assert.Single(_jobs.Recent).Result);
    }

    // A failure here is a faulted task nobody observes once the window has gone.
    [Fact]
    public async Task ExceptionMapping()
    {
        var service = Service();
        Task<StagingCopyResult> Throwing(Exception ex)
            => service.StartAsync("M 31", Folder(Guid.NewGuid().ToString("N")), async (_, _) =>
            {
                await Task.Yield();
                throw ex;
            }, null, CancellationToken.None);

        var cancelled = Throwing(new OperationCanceledException());
        var io = Throwing(new IOException("disk gone"));
        var other = Throwing(new InvalidOperationException("boom"));
        await Task.WhenAll(cancelled, io, other).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(cancelled.IsFaulted || io.IsFaulted || other.IsFaulted);
        Assert.Equal(StagingOutcome.Cancelled, (await cancelled).Outcome);
        Assert.Equal((StagingOutcome.Aborted, "disk gone"), ((await io).Outcome, (await io).AbortReason));
        Assert.Equal(StagingOutcome.Aborted, (await other).Outcome);
        Assert.StartsWith("an unexpected error", (await other).AbortReason, StringComparison.Ordinal);
    }

    // Spec 10.5. A failure here is a shutdown that leaves the copy starting files, or a new copy
    // accepted while the process exits.
    [Fact]
    public async Task CancelAll_ThenWaitForIdle_JoinsTheRun_AndLaterStartsAreRefused()
    {
        var service = Service();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = service.StartAsync("M 31", Folder("a"), Parked(release, entered), null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        service.CancelAll();

        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.True(run.IsCompletedSuccessfully);
        Assert.Equal(StagingOutcome.Cancelled, (await run).Outcome);
        Assert.Equal("GalactiLog is closing.", service.RefusalFor(Folder("z")));
        Assert.Equal(StagingOutcome.Aborted,
            (await service.StartAsync("M 31", Folder("z"), (_, _) => Task.FromResult(Clean()), null, CancellationToken.None)).Outcome);
        service.CancelAll();
    }

    // A failure here is a drain that waits past its budget for a run that ignores its cancel.
    [Fact]
    public async Task WaitForIdle_ReturnsFalse_WhenTheBudgetElapses()
    {
        var service = Service();
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = service.StartAsync("M 31", Folder("a"), (_, _) => release.Task, null, CancellationToken.None);
        try
        {
            service.CancelAll();
            Assert.False(service.WaitForIdle(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            release.SetResult(Clean());
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    // A failure here is a run that waits on the UI thread, which the blocking drain holds.
    [Fact]
    public async Task WaitForIdle_ReturnsTrue_WhenThePostIsNeverDrained()
    {
        var never = new List<Action>();
        var service = Service(new JobRegistry(never.Add));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = service.StartAsync("M 31", Folder("a"), Parked(release, entered), null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        service.CancelAll();

        Assert.True(service.WaitForIdle(TimeSpan.FromSeconds(30)));
        Assert.True(run.IsCompleted);
    }

    // Decision 8. A failure here is a copy that ended after the window closed with no trace of
    // what it skipped or failed.
    [Fact]
    public async Task TheActivityRow_IsWarningWithCounts_WhenThereAreProblems()
    {
        var service = Service();
        var result = new StagingCopyResult(
            StagingOutcome.Completed, 3, 300,
            [new StagingSkip(@"X:\a.fits", StagingSkipReason.ExistsDifferentSize)],
            [new StagingFailure(@"Y:\b.fits", "denied")], [], null);

        await service.StartAsync("M 31", Folder("night"), (_, _) => Task.FromResult(result), null, CancellationToken.None);

        var row = Assert.Single(_rows);
        Assert.Equal(("warning", "stacking_copy"), (row.Severity, row.EventType));
        Assert.Equal("Export for stacking copied 3 files to night", row.Message);
        Assert.Equal(
            ["target", "destination", "outcome", "copied", "bytes_copied", "skipped_same_size",
                "skipped_different_size", "skipped_linked", "failed", "partial"],
            row.Details.GetType().GetProperties().Select(property => property.Name));
        // Review finding 4: the surface the user can find, not "the application log".
        Assert.EndsWith(
            "The file list is in the log viewer on the Diagnostics page.", Assert.Single(_jobs.Recent).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheActivityRow_IsInfo_WhenClean()
    {
        var service = Service();

        await service.StartAsync("M 31", Folder("night"), (_, _) => Task.FromResult(Clean(2)), null, CancellationToken.None);

        var row = Assert.Single(_rows);
        Assert.Equal(("info", "Export for stacking copied 2 files to night"), (row.Severity, row.Message));
    }

    [Fact]
    public async Task TheActivityRow_IsNotWritten_WhenNothingWasCopiedAndNothingWentWrong()
    {
        var service = Service();

        await service.StartAsync("M 31", Folder("night"), (_, _) => Task.FromResult(Cancelled()), null, CancellationToken.None);

        Assert.Empty(_rows);
    }

    [Fact]
    public async Task TheActivityRow_TheShutdownCancel_SaysGalactiLogClosed()
    {
        var service = Service();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StagingCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = service.StartAsync("M 31", Folder("night"), Parked(release, entered, copiedOnCancel: 4), null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        service.CancelAll();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        var row = Assert.Single(_rows);
        Assert.Equal("Export for stacking copied 4 files to night (cancelled because GalactiLog closed)", row.Message);
    }

    // A failure here is a copy reported failed because the activity log refused its row.
    [Fact]
    public async Task AThrowingEmit_IsLogged_NotRethrown()
    {
        var service = new StagingCopyService(_jobs, (_, _, _, _) => throw new InvalidOperationException("db gone"));

        var result = await service.StartAsync(
            "M 31", Folder("night"), (_, _) => Task.FromResult(Clean()), null, CancellationToken.None);

        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Equal(JobResult.Succeeded, Assert.Single(_jobs.Recent).Result);
    }

    // A failure here is a speed inflated by skipped bytes, or a speed shown before it means anything.
    [Fact]
    public void TransferStats_UsesBytesWrittenForSpeedAndTimeLeft()
    {
        // 4 GB done of 10 GB, but only 2 GB written in 20 s: 100 MB/s, 6 GB left is 60 s.
        var p = new StagingProgress(10, 30, 4_000_000_000, 10_000_000_000, 2_000_000_000, "f");

        Assert.Equal(
            "4.0 GB of 10.0 GB, 100 MB/s, about 1m left",
            StagingCopyService.TransferStats(p, TimeSpan.FromSeconds(20)));
        Assert.Equal("4.0 GB of 10.0 GB", StagingCopyService.TransferStats(p, TimeSpan.FromMilliseconds(500)));
    }
}
