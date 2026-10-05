using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging;
using Xunit;
using RecordingPost = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory.RecordingPost;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// The roadmap's Phase 11 row 5 Verify line, written out: the notice is requested once per
/// finished scan, only while hidden, and only when enabled (design-spec 12.11 behaviour 10).
/// </summary>
/// <remarks>
/// <para>
/// No database: both of the watcher's reads are injected delegates, so the settings document and
/// the <c>scan_runs</c> row are lambdas here (design-spec 18.3). <c>ScanStatusService</c> is
/// driven through the seam production uses, <c>ScanCoordinator</c>, exactly as
/// <c>UpdateServiceTests</c> drives it.
/// </para>
/// <para>
/// Every case that asserts anything about background work awaits that work through
/// <c>ScanCompletionWatcher.PendingNotice</c> and none of them sleeps or blocks
/// (<c>TRACKING.md</c> section 2 item 8).
/// </para>
/// </remarks>
public class ScanCompletionWatcherTests
{
    private static ScanRun Run(string state = "complete", int newFiles = 4)
        => new()
        {
            Id = 11,
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
            Trigger = "manual",
            State = state,
            NewFiles = newFiles,
        };

    private sealed class Harness : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<int> _readThreads = [];
        private readonly List<bool> _readFromThreadPool = [];

        public Harness(Action<Action>? post = null, bool start = true)
        {
            Coordinator = ScanCoordinatorTestFactory.CreateBare();

            // Synchronous, so a finished scan reaches the watcher on the raising thread exactly
            // as it reaches it on the dispatcher thread in production.
            Status = new ScanStatusService(Coordinator, action => action());
            Residency = new WindowResidencyService(() => General, post: action => action());

            Watcher = new ScanCompletionWatcher(
                Status,
                Residency,
                () => General,
                ReadRecentRuns,
                Notifier,
                post: post ?? (action => action()),
                logger: Logger);

            if (start)
            {
                Watcher.Start();
            }
        }

        public ScanCoordinator Coordinator { get; }

        public ScanStatusService Status { get; }

        public WindowResidencyService Residency { get; }

        public RecordingScanCompletionNotifier Notifier { get; } = new();

        public RecordingLogger Logger { get; } = new();

        public ScanCompletionWatcher Watcher { get; }

        /// <summary>The general document, read live by the watcher on every finished scan.
        /// </summary>
        public GeneralSettings General { get; set; } = new() { NotifyOnScanComplete = true };

        /// <summary>What the injected <c>scan_runs</c> read returns, newest first. Empty stands in
        /// for a library that has never scanned.</summary>
        public IReadOnlyList<ScanRun> RecentRuns { get; set; } = [Run()];

        /// <summary>The limit the watcher asked for, on the most recent read.</summary>
        public int RequestedLimit { get; private set; }

        /// <summary>Set to have the <c>scan_runs</c> read throw.</summary>
        public Exception? ThrowOnRead { get; set; }

        /// <summary>Parks the <c>scan_runs</c> read until the test releases it, so a case can
        /// observe the instant between the event returning and the read finishing.</summary>
        public TaskCompletionSource? ReadGate { get; set; }

        /// <summary>The managed thread the event was raised on, captured by a subscriber
        /// registered before the watcher's own.</summary>
        public int RaiseThread { get; private set; }

        /// <summary>Whether the raising thread was a thread-pool thread.</summary>
        public bool RaisedFromThreadPool { get; private set; }

        public IReadOnlyList<int> ReadThreads
        {
            get { lock (_gate) { return [.. _readThreads]; } }
        }

        public IReadOnlyList<bool> ReadsFromThreadPool
        {
            get { lock (_gate) { return [.. _readFromThreadPool]; } }
        }

        public int Reads
        {
            get { lock (_gate) { return _readThreads.Count; } }
        }

        /// <summary>Records the thread the event arrives on. Subscribed on demand and before the
        /// watcher subscribes, so it sees the same thread the watcher's handler runs on.</summary>
        public void RecordTheRaisingThread()
            => Status.ScanFinished += (_, _) =>
            {
                RaiseThread = Environment.CurrentManagedThreadId;
                RaisedFromThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            };

        /// <summary>
        /// Drops the coordinator through a finished run the way <c>UpdateServiceTests</c> does:
        /// the in-memory connection is never migrated, so the pipeline throws once it reads
        /// settings, but not before the finally block has raised <c>ScanFinished</c>.
        /// </summary>
        public async Task RaiseFinishedAsync()
            => await Assert.ThrowsAnyAsync<Exception>(
                () => Coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        /// <summary>
        /// The same raise, from a dedicated thread that is deliberately not a thread-pool thread.
        /// The pipeline throws before its first await, so the run, its finally and
        /// <c>ScanFinished</c> all complete on that thread: "the read did not run on the raising
        /// thread" is then a property of the thread rather than an identifier comparison a busy
        /// pool could satisfy by accident (<c>TRACKING.md</c> section 6 item 25).
        /// </summary>
        public Task RaiseFinishedOnADedicatedThreadAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var run = Coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
                    Assert.True(
                        run.IsCompleted,
                        "the bare coordinator's pipeline throws before its first await, so the run "
                        + "and its ScanFinished raise complete on this thread");
                    _ = run.Exception;
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "scan-completion-raise",
            };

            thread.Start();
            return done.Task;
        }

        /// <summary>Joins the in-flight read rather than sleeping on it.</summary>
        public async Task AwaitPendingAsync()
        {
            if (Watcher.PendingNotice is { } pending)
            {
                await pending;
            }
        }

        public void Dispose()
        {
            Watcher.Dispose();
            Status.Dispose();
        }

        private IReadOnlyList<ScanRun> ReadRecentRuns(int limit)
        {
            lock (_gate)
            {
                RequestedLimit = limit;
                _readThreads.Add(Environment.CurrentManagedThreadId);
                _readFromThreadPool.Add(Thread.CurrentThread.IsThreadPoolThread);
            }

            // Parked, never slept on: the test releases the gate when it has finished observing
            // the instant after the event returned. The result is asserted rather than discarded,
            // so a gate that is never released fails the case instead of passing it thirty
            // seconds later.
            if (ReadGate is { } gate)
            {
                Assert.True(
                    gate.Task.Wait(TimeSpan.FromSeconds(30)),
                    "the parked scan_runs read was never released");
            }

            return ThrowOnRead is { } error ? throw error : [.. RecentRuns.Take(limit)];
        }
    }

    // ---- the three conditions -----------------------------------------------------------------

    [Fact]
    public async Task ScanFinished_WhileHidden_AndEnabled_RequestsTheNoticeOnce()
    {
        using var harness = new Harness();

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Notifier.Count);
        Assert.Equal(ScanCompletionNotice.CompleteTitle, harness.Notifier.Notices[0].Title);
        Assert.Equal("4 new files", harness.Notifier.Notices[0].Body);
    }

    [AvaloniaFact]
    public async Task ScanFinished_WhileVisible_RequestsNothing()
    {
        using var harness = new Harness();
        var window = new Window();
        window.Show();
        harness.Residency.Attach(window);
        Assert.True(harness.Residency.IsWindowVisible);

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(0, harness.Notifier.Count);

        // The cheap condition is first: a window on screen costs no database read at all.
        Assert.Equal(0, harness.Reads);

        window.Close();
    }

    [Fact]
    public async Task ScanFinished_WhileDisabled_RequestsNothing()
    {
        using var harness = new Harness { General = new GeneralSettings() };

        Assert.False(harness.General.NotifyOnScanComplete);

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(0, harness.Notifier.Count);
    }

    [Fact]
    public async Task ScanFinished_WithNoScanRunRow_RequestsNothing()
    {
        using var harness = new Harness { RecentRuns = [] };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Reads);
        Assert.Equal(0, harness.Notifier.Count);
    }

    [Fact]
    public async Task TwoScansFinishing_RequestTwoNotices()
    {
        using var harness = new Harness();

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();
        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        // Once per finished scan, not once per distinct outcome: two consecutive runs with the
        // same counts are two runs, and the coordinator's pending follow-up is a real second run.
        Assert.Equal(2, harness.Notifier.Count);
    }

    [Fact]
    public async Task ScanFinished_ReadsTheSettingLive_NotTheValueCapturedAtStart()
    {
        using var harness = new Harness { General = new GeneralSettings() };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();
        Assert.Equal(0, harness.Notifier.Count);

        // The user turns it on in the General tab while the application runs. No restart, and no
        // re-subscription.
        harness.General = new GeneralSettings { NotifyOnScanComplete = true };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Notifier.Count);
    }

    [AvaloniaFact]
    public async Task ScanFinished_ReadsTheWindowVisibilityLive()
    {
        using var harness = new Harness();
        using var gate = new Gate();
        harness.ReadGate = gate.Source;
        var window = new Window();
        harness.Residency.Attach(window);

        await harness.RaiseFinishedAsync();

        // The instant the implementation samples is the instant of the event, on the UI thread
        // and before either database read, which is what keeps the cheap condition first. A
        // window shown after that instant therefore does not suppress a notice that was already
        // decided on, and the next finished scan reads the new answer.
        window.Show();
        Assert.True(harness.Residency.IsWindowVisible);
        gate.Source.TrySetResult();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Notifier.Count);

        // And the next one, decided while the window is up, requests nothing.
        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Notifier.Count);

        window.Close();
    }

    // ---- the threading ------------------------------------------------------------------------

    [Fact]
    public async Task ScanFinished_RunsTheDatabaseReadOffTheUiThread()
    {
        using var harness = new Harness();
        harness.RecordTheRaisingThread();

        await harness.RaiseFinishedOnADedicatedThreadAsync();
        await harness.AwaitPendingAsync();

        Assert.False(harness.RaisedFromThreadPool);
        Assert.Equal(1, harness.Reads);
        Assert.True(harness.ReadsFromThreadPool[0]);
        Assert.NotEqual(harness.RaiseThread, harness.ReadThreads[0]);
        Assert.Equal(1, harness.Notifier.Count);
    }

    [Fact]
    public async Task ScanFinished_DoesNotBlockTheEvent()
    {
        using var harness = new Harness();
        using var gate = new Gate();
        harness.ReadGate = gate.Source;

        await harness.RaiseFinishedAsync();

        // The raise has returned with the read still parked. This is the case a .Result, a
        // .Wait() or a GetAwaiter().GetResult() inside the handler fails: the scan's completion
        // path would have waited out the read before returning.
        Assert.Equal(0, harness.Notifier.Count);

        gate.Source.TrySetResult();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Notifier.Count);
    }

    [Fact]
    public async Task ScanFinished_PublishesTheNoticeThroughThePostSeam()
    {
        var post = new RecordingPost();
        using var harness = new Harness(post.Post);

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        // Nothing is shown until the post is drained: the reads happen off the dispatcher and the
        // Show is published back onto it, which is the half a notification surface needs.
        Assert.Equal(0, harness.Notifier.Count);
        Assert.Equal(1, post.Queued);
        Assert.True(post.PostedFromThreadPool[0]);

        post.Drain();

        Assert.Equal(1, harness.Notifier.Count);
    }

    // ---- the failures -------------------------------------------------------------------------

    [Fact]
    public async Task ScanFinished_WhenTheReadThrows_LogsAndRequestsNothing()
    {
        using var harness = new Harness { ThrowOnRead = new InvalidOperationException("no catalogue") };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(0, harness.Notifier.Count);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task ScanFinished_WhenTheNotifierThrows_IsSwallowedAndLogged()
    {
        using var harness = new Harness();
        harness.Notifier.ThrowOnShow = new InvalidOperationException("no notification area");

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Notifier.Count);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException);

        // And the next scan still notifies: one failed notice does not end the subscription.
        harness.Notifier.ThrowOnShow = null;
        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(2, harness.Notifier.Count);
    }

    [Fact]
    public async Task ScanFinished_WithAnUnavailableNotifier_RequestsNothing()
    {
        using var harness = new Harness();
        harness.Notifier.IsAvailable = false;

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();
        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(0, harness.Notifier.Count);
        Assert.Equal(0, harness.Reads);

        // Logged once, not once per scan: an unavailable surface is a property of the build.
        Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    // ---- the subscription ---------------------------------------------------------------------

    [Fact]
    public async Task Start_CalledTwice_SubscribesOnce()
    {
        using var harness = new Harness();

        harness.Watcher.Start();
        harness.Watcher.Start();

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Reads);
        Assert.Equal(1, harness.Notifier.Count);
    }

    [Fact]
    public void Stop_BeforeStart_IsSafe()
    {
        using var harness = new Harness(start: false);

        harness.Watcher.Stop();
    }

    [Fact]
    public void Stop_CalledTwice_IsSafe()
    {
        using var harness = new Harness();

        harness.Watcher.Stop();
        harness.Watcher.Stop();
    }

    [Fact]
    public async Task Stop_StopsNotifying()
    {
        using var harness = new Harness();

        harness.Watcher.Stop();
        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(0, harness.Notifier.Count);

        // And starting again resumes, still with one subscription.
        harness.Watcher.Start();
        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Notifier.Count);
        Assert.Equal(1, harness.Reads);
    }

    [Fact]
    public async Task Dispose_Stops()
    {
        using var harness = new Harness();

        harness.Watcher.Dispose();
        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(0, harness.Notifier.Count);

        // It disposes neither collaborator: the host owns both, and the status service is still
        // live enough to have raised the event this case just ignored.
        Assert.False(harness.Residency.IsExiting);
        Assert.False(harness.Status.IsRunning);
    }

    [Fact]
    public async Task ScanFinished_ComposesTheNoticeFromTheRowItRead()
    {
        var row = new ScanRun
        {
            Id = 3,
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
            Trigger = "scheduler",
            State = "failed",
            Discovered = 5,
            Failed = 1,
            ErrorText = "the catalogue is locked",
        };
        using var harness = new Harness { RecentRuns = [row] };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        // One composer: the watcher does not write its own sentence.
        Assert.Equal(ScanCompletionNotice.From(row), Assert.Single(harness.Notifier.Notices));
    }

    // ---- the finished run is found by state, not by "newest row" (fix round 1) ----------------

    [Fact]
    public async Task ScanFinished_WithARunningNewestRow_RequestsNothing()
    {
        using var harness = new Harness { RecentRuns = [Run(state: "running")] };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        // "Scan running" on an idle tooltip is worse than no tooltip: nothing clears
        // TrayIconViewModel.LastCompletion until a scan next starts, so a wrong notice persists.
        Assert.Equal(1, harness.Reads);
        Assert.Equal(0, harness.Notifier.Count);
    }

    [Fact]
    public async Task ScanFinished_WithAConcurrentCliScan_ReportsTheRunThatFinished()
    {
        var finished = Run(state: "complete", newFiles: 7);
        using var harness = new Harness
        {
            // Spec 12.11 behaviour 2: `galactilog scan` beside a running GUI is supported. Its
            // row is newer than the GUI run that just finished, this process never hears about
            // that scan, and IsRunning therefore stays false.
            RecentRuns = [Run(state: "running"), finished],
        };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(ScanCompletionNotice.From(finished), Assert.Single(harness.Notifier.Notices));
        Assert.Equal(ScanCompletionWatcher.RecentRunsScanned, harness.RequestedLimit);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task ScanFinished_ReportsEveryTerminalState(string state)
    {
        using var harness = new Harness { RecentRuns = [Run(state: state)] };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        // Skipping a non-terminal row must not become skipping a cancelled or failed one: an
        // interrupted run still reports, which is the half spec 12.10 asks for.
        Assert.Equal(ScanCompletionNotice.From(Run(state: state)), Assert.Single(harness.Notifier.Notices));
    }

    [Fact]
    public async Task ScanFinished_WithOnlyRunningRowsInTheLookBack_RequestsNothing()
    {
        using var harness = new Harness
        {
            RecentRuns = [.. Enumerable.Repeat(Run(state: "running"), ScanCompletionWatcher.RecentRunsScanned)],
        };

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        Assert.Equal(0, harness.Notifier.Count);
    }

    [Fact]
    public async Task Stop_WhileTheReadIsParked_PublishesNothing()
    {
        using var harness = new Harness();
        using var gate = new Gate();
        harness.ReadGate = gate.Source;

        await harness.RaiseFinishedAsync();

        // The shutdown path stops the watcher outside the drain's budget, so a read handed to the
        // pool before the stop can still be in flight. Detaching the handler cannot recall it.
        harness.Watcher.Stop();
        gate.Source.TrySetResult();
        await harness.AwaitPendingAsync();

        Assert.Equal(1, harness.Reads);
        Assert.Equal(0, harness.Notifier.Count);
    }

    [Fact]
    public async Task ScanFinished_WhenThePostSeamThrows_IsSwallowedAndLogged()
    {
        using var harness = new Harness(_ => throw new InvalidOperationException("the dispatcher is shutting down"));

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        // The task is fire and forget, so an unguarded throw here would surface only as an
        // unobserved task exception and spec 12.10's "never a silent default" would be broken.
        Assert.Equal(0, harness.Notifier.Count);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task ScanFinished_WhileDisabled_DoesNotAskTheNotifierWhetherItIsAvailable()
    {
        using var harness = new Harness { General = new GeneralSettings() };
        harness.Notifier.IsAvailable = false;

        await harness.RaiseFinishedAsync();
        await harness.AwaitPendingAsync();

        // The setting is read before the surface is: a build with no surface must not write a log
        // line for a user who never turned the notice on.
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level == LogLevel.Information);
    }

    /// <summary>A completion source with a using-friendly wrapper, so a parked read is always
    /// released even when an assertion fails first.</summary>
    private sealed class Gate : IDisposable
    {
        public TaskCompletionSource Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => Source.TrySetResult();
    }
}
