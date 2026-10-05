using System.Text.Json;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Scanning;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Xunit;
using RecordingPost = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory.RecordingPost;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Spec 17.1's update flow and the roadmap's Phase 10 row 4 Verify line: the prompt is suppressed
/// while a scan runs, the channel the feed reports is what the application shows, a failed check
/// logs at warning without disrupting startup, and the two activity events are written.
/// </summary>
/// <remarks>
/// Nothing here builds a Velopack update manager and nothing reaches the network:
/// <see cref="IUpdateChecker"/> is the seam and every case binds
/// <see cref="RecordingUpdateChecker"/>. Two source scans at the end of the file assert that
/// structurally rather than by convention.
/// </remarks>
public class UpdateServiceTests
{
    private const string TestChannel = "alpha";

    // ---------------------------------------------------------------- the stub and the harness

    private sealed class Harness : IDisposable
    {
        public Harness(
            Action<Action>? post = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            bool withPrompt = true,
            bool followUpScanOnScanFinished = false)
        {
            Database = new TempDatabase("galactilog-updateservice");
            Coordinator = ScanCoordinatorTestFactory.CreateBare();

            // Synchronous, so a state change is visible the instant it is made and the prompt
            // request is observable without a dispatcher.
            Status = new ScanStatusService(Coordinator, action => action());

            if (followUpScanOnScanFinished)
            {
                // Subscribed BEFORE UpdateService, so it runs first when ScanStatusService walks
                // its invocation list. It stands in for the follow-up scan the coordinator
                // schedules before it raises ScanFinished: by the time UpdateService's own
                // handler runs, a scan is running again.
                Status.ScanFinished += (_, _) => RaiseRunning();
            }

            Service = new UpdateService(
                new BuildInfo("1.0.0.0", "abc1234", TestChannel, isInstalled: true),
                () => Checker,
                Status,
                new ActivityRepository(Database.ConnectionString),
                showPrompt: withPrompt
                    ? () =>
                    {
                        Interlocked.Increment(ref _prompts);

                        // The seam's result is "a window opened", not "the user confirmed".
                        // PromptGate parks the dialog open, which is what a re-entrancy case
                        // needs; PromptShown false stands in for ModalHost's no-owner path.
                        return PromptGate is { } gate
                            ? gate.Task.ContinueWith(
                                _ => PromptShown,
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default)
                            : Task.FromResult(PromptShown);
                    }
                    : null,
                logger: Logger,
                post: post ?? (action => action()),
                delay: delay);
        }

        private int _prompts;

        /// <summary>Whether the prompt seam reports that a window opened. False stands in for
        /// <c>ModalHost</c>'s no-owner path.</summary>
        public bool PromptShown { get; set; } = true;

        /// <summary>Parks the prompt open until a test completes it, so a second request lands
        /// while the first dialog is genuinely on screen.</summary>
        public TaskCompletionSource? PromptGate { get; set; }

        public TempDatabase Database { get; }

        public ScanCoordinator Coordinator { get; }

        public ScanStatusService Status { get; }

        public RecordingUpdateChecker Checker { get; } = new();

        public RecordingLogger Logger { get; } = new();

        public UpdateService Service { get; }

        /// <summary>How many times the prompt was asked for.</summary>
        public int Prompts => Volatile.Read(ref _prompts);

        public IReadOnlyList<GalactiLog.Data.Entities.ActivityEvent> Events()
            => Database.Read(context => context.ActivityEvents.OrderBy(row => row.Id).ToList());

        /// <summary>Puts the shared status service into "a scan is running" without a database,
        /// exactly as <c>StatusBarViewModelTests</c> does.</summary>
        public void RaiseRunning()
            => Coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 1, "Discovering...", force: true);

        /// <summary>
        /// Drops back to idle the way production does: the connection string is never migrated,
        /// so the run throws once the pipeline reads settings, but not before its finally block
        /// has raised <c>ScanFinished</c>, which is the only thing this harness needs from it.
        /// </summary>
        public async Task RaiseFinishedAsync()
            => await Assert.ThrowsAnyAsync<Exception>(
                () => Coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        public void Dispose()
        {
            Service.Dispose();
            Status.Dispose();
            Database.Dispose();
        }
    }

    private static AvailableUpdate Update(string version = "1.1.0", string? notes = "New in 1.1.0")
        => new(version, notes, new object());

    // Awaits a condition rather than blocking on it: an assertion about background work joins
    // that work (TRACKING section 2 item 8).
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(2).ConfigureAwait(false);
        }

        if (!condition())
        {
            throw new TimeoutException($"Timed out waiting for {what}.");
        }
    }

    // ---------------------------------------------------------------- the loop

    [Fact]
    public void Start_OnANotInstalledBuild_StartsNoLoop_AndChecksNothing()
    {
        using var harness = new Harness();
        harness.Checker.IsInstalled = false;

        harness.Service.Start();

        Assert.Null(harness.Service.LoopTask);
        Assert.Equal(0, harness.Checker.Checks);
        Assert.Equal(UpdatePhase.Idle, harness.Service.State.Phase);
    }

    [Fact]
    public async Task Start_RunsOneCheckImmediately_BeforeTheFirstDelay()
    {
        var delays = 0;
        using var harness = new Harness(delay: (_, ct) =>
        {
            Interlocked.Increment(ref delays);
            return Task.Delay(Timeout.Infinite, ct);
        });

        harness.Service.Start();
        await WaitForAsync(() => harness.Checker.Checks >= 1, "the first check");

        // The check ran, and the loop is parked in its first wait: spec 17.1's "on start and
        // every 6 hours" is one check before the first interval, not one after it.
        Assert.Equal(1, harness.Checker.Checks);
        harness.Service.Stop();
        await harness.Service.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Loop_ChecksAgainAfterTheInterval()
    {
        var intervals = new List<TimeSpan>();
        var gate = new Lock();
        using var harness = new Harness(delay: (span, ct) =>
        {
            int count;
            lock (gate)
            {
                intervals.Add(span);
                count = intervals.Count;
            }

            // The first wait ends at once so the loop comes round again; the second parks, so
            // the case ends with a countable number of checks.
            return count >= 2 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;
        });

        harness.Service.Start();
        await WaitForAsync(() => harness.Checker.Checks >= 2, "the second check");

        Assert.Equal(TimeSpan.FromHours(6), UpdateService.CheckInterval);
        lock (gate)
        {
            Assert.Equal(UpdateService.CheckInterval, intervals[0]);
        }

        harness.Service.Stop();
        await harness.Service.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Check_ThatThrows_LogsAtWarning_AndTheLoopContinues()
    {
        var intervals = 0;
        using var harness = new Harness(delay: (_, ct) =>
            Interlocked.Increment(ref intervals) >= 2 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask);
        harness.Checker.CheckThrows = new InvalidOperationException("the feed was unreachable");

        harness.Service.Start();
        await WaitForAsync(() => harness.Checker.Checks >= 2, "the check after the failure");

        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException);
        Assert.Equal(UpdatePhase.Failed, harness.Service.State.Phase);
        Assert.Equal("the feed was unreachable", harness.Service.State.LastError);

        harness.Service.Stop();
        await harness.Service.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Check_ThatThrows_DoesNotThrowOutOfStart()
    {
        using var harness = new Harness(delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        harness.Checker.CheckThrows = new InvalidOperationException("the feed was unreachable");

        // "Without disrupting startup": Start is what App.axaml.cs calls beside the watcher and
        // the scheduler, so it must not throw whatever the feed does.
        harness.Service.Start();
        await WaitForAsync(() => harness.Checker.Checks >= 1, "the failing check");

        harness.Service.Stop();
        await harness.Service.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Stop_EndsTheLoop()
    {
        using var harness = new Harness(delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));

        harness.Service.Start();
        var loop = harness.Service.LoopTask;
        Assert.NotNull(loop);

        harness.Service.Stop();

        // Awaited with a bound rather than read through IsCompleted the instant after Stop: the
        // loop unwinds on its own thread.
        await loop.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(loop.IsCompleted);
    }

    /// <summary>
    /// Review M2, fixer list code item 6: the same case exists for <c>ScanScheduler</c>'s
    /// identical wait, because the reviewer's point applies to both loops or to neither. A delay
    /// seam that fails any way other than cancellation used to end the loop and fault
    /// <c>LoopTask</c> with nobody observing it, which is a
    /// <c>TaskScheduler.UnobservedTaskException</c> at the next collection rather than a message.
    /// Only an injected seam reaches it: in production the delay is <c>Task.Delay</c> over the
    /// constant <c>CheckInterval</c>.
    /// </summary>
    [Fact]
    public async Task RunLoop_WhenTheDelaySeamThrows_EndsCleanly_AndDoesNotFaultTheLoopTask()
    {
        using var harness = new Harness(
            delay: (_, _) => Task.FromException(new InvalidOperationException("the delay seam failed")));

        harness.Service.Start();
        var loop = harness.Service.LoopTask;
        Assert.NotNull(loop);

        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskStatus.RanToCompletion, loop.Status);

        // The check before the first wait still ran, so the loop ended at the wait and not before
        // it, and the failure was reported rather than swallowed.
        Assert.Equal(1, harness.Checker.Checks);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.Contains("wait failed", StringComparison.Ordinal));

        // Idempotent afterwards: Stop on a loop that already ended must not throw.
        harness.Service.Stop();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var harness = new Harness();

        harness.Service.Dispose();
        harness.Service.Dispose();
        harness.Dispose();
    }

    // ---------------------------------------------------------------- the channel

    [Fact]
    public async Task Channel_ReportedByTheChecker_IsWhatTheStateAndAboutTabShow()
    {
        using var harness = new Harness();
        harness.Checker.Channel = "rc";
        harness.Checker.Offered = Update();

        await harness.Service.CheckNowAsync();

        Assert.Equal("rc", harness.Service.Channel);
        var announced = Assert.Single(harness.Events(), row => row.EventType == "update_available");
        Assert.Equal("rc", Details(announced)["channel"]);
    }

    [Fact]
    public void UpdateService_SetsNoExplicitChannel()
    {
        // Ruling Q12. Spec 17.1's "a stable install never offers itself a prerelease" is the
        // update manager's default behaviour; ExplicitChannel would override the channel the
        // build was installed from and break the guarantee. Enforced at the one place it can be
        // enforced, which is "the name appears nowhere" (design-lessons rule 2).
        Assert.Empty(SourceScan.FilesMatching(@"ExplicitChannel"));
    }

    [Fact]
    public void VelopackTypes_AreNamedInExactlyOneFile()
    {
        Assert.Equal(
            new[] { "VelopackUpdateChecker.cs" },
            SourceScan.FilesMatching(@"\b(UpdateManager|GithubSource|UpdateInfo)\b"));

        // VelopackApp is the one other Velopack name in src/**, and it belongs to exactly one
        // file: Program.cs names it and must, because its hooks run and exit during install,
        // update and uninstall (spec 17.1).
        Assert.Equal(
            new[] { "Program.cs" },
            SourceScan.FilesMatching(@"\bVelopackApp\b"));
    }

    /// <summary>
    /// Phase 11 Task 3's third Velopack census, beside the two above. Exactly one file in
    /// <c>src/**</c> may name <c>Shortcuts</c>, <c>ShortcutLocation</c> or <c>ShellLink</c>, the
    /// "legacy, stability not guaranteed" surface the shortcut API is built on:
    /// <c>VelopackStartupShortcut.cs</c>, the one implementation of <c>IStartupShortcut</c> (spec
    /// 2.1.1, 2.1.3, 12.11). The two censuses above are unaffected: the names this task adds are
    /// disjoint from <c>UpdateManager</c>, <c>GithubSource</c>, <c>UpdateInfo</c> and
    /// <c>VelopackApp</c>.
    /// </summary>
    [Fact]
    public void StartupShortcut_IsNamedInExactlyOneFile()
    {
        Assert.Equal(
            new[] { "VelopackStartupShortcut.cs" },
            SourceScan.FilesMatching(@"\b(Shortcuts|ShortcutLocation|ShellLink)\b"));
    }

    // VelopackLocator is deliberately not censused (coordinator ruling on this task): it names
    // the updater's own install identity (spec 2.1.2's paths, and the channel
    // VelopackUpdateChecker already reads), not the "legacy" shortcut surface the census above
    // confines to one file, so it is not part of either group.

    // ---------------------------------------------------------------- mid-scan suppression

    [Fact]
    public async Task Prompt_IsSuppressed_WhileAScanIsRunning()
    {
        using var harness = new Harness();
        harness.Checker.Offered = Update();
        harness.RaiseRunning();

        await harness.Service.CheckNowAsync();

        // The state still reaches ReadyToApply and the status bar still shows it. Only the modal
        // is withheld (spec 17.1: "nothing is applied silently while the user is mid-scan").
        Assert.Equal(UpdatePhase.ReadyToApply, harness.Service.State.Phase);
        Assert.Equal(0, harness.Prompts);
    }

    [Fact]
    public async Task Prompt_Opens_WhenTheScanFinishes()
    {
        using var harness = new Harness();
        harness.Checker.Offered = Update();
        harness.RaiseRunning();
        await harness.Service.CheckNowAsync();
        Assert.Equal(0, harness.Prompts);

        await harness.RaiseFinishedAsync();

        Assert.Equal(1, harness.Prompts);
    }

    [Fact]
    public async Task Prompt_DoesNotOpen_WhenScanFinishedFiresButAFollowUpScanIsAlreadyRunning()
    {
        // The case the naive implementation gets wrong. ScanStatusService invokes each
        // ScanFinished target in turn, so the harness's own subscriber, registered before
        // UpdateService, stands in for the follow-up scan the coordinator schedules BEFORE it
        // raises the event: by the time UpdateService's handler runs, a scan is running again.
        // An implementation that captured the running state when the handler was attached, or
        // assumed it false because a scan had just finished, opens the prompt here.
        using var harness = new Harness(followUpScanOnScanFinished: true);
        harness.Checker.Offered = Update();
        harness.RaiseRunning();
        await harness.Service.CheckNowAsync();

        await harness.RaiseFinishedAsync();

        Assert.True(harness.Status.IsRunning);
        Assert.Equal(0, harness.Prompts);
        Assert.Equal(UpdatePhase.ReadyToApply, harness.Service.State.Phase);
    }

    // ---------------------------------------------------------------- a staged update survives

    [Fact]
    public async Task Check_ThatFailsAfterASuccessfulDownload_StillLetsTheUserApply()
    {
        // Review round 1, I1. The user answered Later to a staged version; six hours later the
        // machine is offline. Before the fix that drove the phase to Failed with _pending still
        // set, which took the indicator away and made ApplyAndRestart refuse a package that was
        // already on disk.
        using var harness = new Harness();
        harness.Checker.Offered = Update("2.0.0");
        await harness.Service.CheckNowAsync();
        Assert.Equal(UpdatePhase.ReadyToApply, harness.Service.State.Phase);

        harness.Checker.CheckThrows = new InvalidOperationException("the feed was unreachable");
        await harness.Service.CheckNowAsync();

        Assert.Equal(UpdatePhase.ReadyToApply, harness.Service.State.Phase);
        Assert.Null(harness.Service.State.LastError);
        Assert.Equal("2.0.0", harness.Service.State.AvailableVersion);

        harness.Service.ApplyAndRestart();

        Assert.Equal(1, harness.Checker.Applies);
        Assert.Single(harness.Events(), row => row.EventType == "update_applied");
    }

    [Fact]
    public async Task Loop_DoesNotDownloadTheStagedPackageAgain_OnTheNextTick()
    {
        // The other half of I1: a check that succeeded would re-download the same package on
        // every tick for as long as the process runs.
        var ticks = 0;
        using var harness = new Harness(delay: (_, ct) =>
            Interlocked.Increment(ref ticks) >= 2 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask);
        harness.Checker.Offered = Update("2.0.0");

        harness.Service.Start();
        await WaitForAsync(() => Volatile.Read(ref ticks) >= 2, "the second tick");

        Assert.Equal(1, harness.Checker.Checks);
        Assert.Equal(1, harness.Checker.Downloads);
        Assert.Equal(UpdatePhase.ReadyToApply, harness.Service.State.Phase);

        harness.Service.Stop();
        await harness.Service.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Prompt_IsShownOncePerVersion_EvenAfterSeveralScansFinish()
    {
        using var harness = new Harness();
        harness.Checker.Offered = Update();
        harness.RaiseRunning();
        await harness.Service.CheckNowAsync();

        await harness.RaiseFinishedAsync();
        await harness.RaiseFinishedAsync();
        await harness.RaiseFinishedAsync();

        // A user who dismissed the prompt is not asked again every time a scan finishes.
        Assert.Equal(1, harness.Prompts);
    }

    [Fact]
    public async Task PromptNow_PressedTwice_OpensOneWindow()
    {
        // Review round 1, M1. Two quick presses of the status bar indicator, with the first
        // dialog still on screen.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness { PromptGate = gate };
        harness.Checker.Offered = Update();

        // The check opens the automatic prompt and the gate parks it on screen, which is the
        // other half of the case M1 names: a press of the indicator racing the prompt a check or
        // a finishing scan already raised.
        await harness.Service.CheckNowAsync();
        Assert.Equal(1, harness.Prompts);

        harness.Service.PromptNow();
        harness.Service.PromptNow();

        Assert.Equal(1, harness.Prompts);

        // And once the dialog closes, the affordance works again. The release runs on the pool
        // when the seam's task completes, so the press is retried until it takes rather than
        // made once against a flag that may not have been cleared yet. Each retry before the
        // release is refused, so the count cannot overshoot.
        harness.PromptGate = null;
        gate.SetResult();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (harness.Prompts < 2 && DateTime.UtcNow < deadline)
        {
            harness.Service.PromptNow();
            await Task.Delay(2);
        }

        Assert.Equal(2, harness.Prompts);
    }

    [Fact]
    public async Task Prompt_ThatNoWindowCouldBeShownFor_IsOfferedAgainWhenTheNextScanFinishes()
    {
        // Review round 1, M5. ModalHost's no-owner path decides nothing, so the version must not
        // be recorded as prompted.
        using var harness = new Harness { PromptShown = false };
        harness.Checker.Offered = Update();
        harness.RaiseRunning();
        await harness.Service.CheckNowAsync();

        await harness.RaiseFinishedAsync();
        Assert.Equal(1, harness.Prompts);

        await harness.RaiseFinishedAsync();
        Assert.Equal(2, harness.Prompts);
    }

    [Fact]
    public async Task ApplyAndRestart_WhileAScanIsRunning_DoesNothing()
    {
        using var harness = new Harness();
        harness.Checker.Offered = Update();
        await harness.Service.CheckNowAsync();
        Assert.Equal(UpdatePhase.ReadyToApply, harness.Service.State.Phase);

        harness.RaiseRunning();

        // Called directly, bypassing any CanExecute: TRACKING item 13's guard lives in the body,
        // and this is the most consequential instance of that rule in the application.
        harness.Service.ApplyAndRestart();

        Assert.Equal(0, harness.Checker.Applies);
        Assert.DoesNotContain(harness.Events(), row => row.EventType == "update_applied");
    }

    // ---------------------------------------------------------------- the activity events

    [Fact]
    public async Task ApplyAndRestart_EmitsUpdateAppliedBeforeCallingTheChecker()
    {
        using var harness = new Harness();
        harness.Checker.Offered = Update("2.0.0");
        await harness.Service.CheckNowAsync();

        var rowsAtApplyTime = -1;
        harness.Checker.OnApply = () => rowsAtApplyTime =
            harness.Events().Count(row => row.EventType == "update_applied");

        harness.Service.ApplyAndRestart();

        // The process does not survive ApplyUpdatesAndRestart, so an event emitted afterwards
        // would never be written.
        Assert.Equal(1, rowsAtApplyTime);
        Assert.Equal(1, harness.Checker.Applies);
    }

    [Fact]
    public async Task UpdateAvailable_EmitsOneActivityEventPerDistinctVersion()
    {
        using var harness = new Harness();
        harness.Checker.Offered = Update("1.1.0");

        // The downloads fail, so no check stages an update and every check below genuinely runs:
        // a staged update short-circuits the next check (review round 1, I1), which is the right
        // behaviour and would otherwise make this case assert that a check it prevented emitted
        // nothing. The event is written before the download either way.
        harness.Checker.DownloadThrows = new IOException("the package could not be written");

        await harness.Service.CheckNowAsync();
        await harness.Service.CheckNowAsync();
        await harness.Service.CheckNowAsync();

        Assert.Equal(3, harness.Checker.Checks);
        Assert.Single(harness.Events(), row => row.EventType == "update_available");

        harness.Checker.Offered = Update("1.2.0");
        await harness.Service.CheckNowAsync();

        Assert.Equal(2, harness.Events().Count(row => row.EventType == "update_available"));
    }

    [Fact]
    public async Task ActivityEvents_UseSnakeCaseDetails_AndTheSystemCategory()
    {
        using var harness = new Harness();
        harness.Checker.Offered = Update("3.1.4");
        await harness.Service.CheckNowAsync();
        harness.Service.ApplyAndRestart();

        var available = Assert.Single(harness.Events(), row => row.EventType == "update_available");
        var applied = Assert.Single(harness.Events(), row => row.EventType == "update_applied");

        foreach (var row in new[] { available, applied })
        {
            Assert.Equal("system", row.Category);
            Assert.Equal("info", row.Severity);
        }

        var availableDetails = Details(available);
        Assert.Equal("3.1.4", availableDetails["version"]);
        Assert.Equal(TestChannel, availableDetails["channel"]);
        Assert.Equal("1.0.0.0", availableDetails["current_version"]);

        var appliedDetails = Details(applied);
        Assert.Equal("3.1.4", appliedDetails["version"]);
        Assert.Equal(TestChannel, appliedDetails["channel"]);
        Assert.Equal("1.0.0.0", appliedDetails["previous_version"]);
    }

    // ---------------------------------------------------------------- download progress

    [Fact]
    public async Task Download_ReportsProgressOnTheUiThread()
    {
        var post = new RecordingPost();
        using var harness = new Harness(post: post.Post);
        harness.Checker.Offered = Update();
        harness.Checker.ProgressPercents = [25, 75, 100];

        var percents = new List<double>();
        harness.Service.StateChanged += (_, state) =>
        {
            if (state.Phase == UpdatePhase.Downloading)
            {
                percents.Add(state.DownloadPercent);
            }
        };

        await harness.Service.CheckNowAsync();

        // Every state change was handed to the dispatcher seam rather than raised on the thread
        // the download reported on. Draining is what runs them, and the drain happens here, on
        // the test's own thread, which stands in for the UI thread.
        Assert.Empty(percents);
        Assert.True(post.Queued > 0);
        post.Drain();

        Assert.Equal(post.DrainThread, Environment.CurrentManagedThreadId);
        Assert.Contains(25d, percents);
        Assert.Contains(75d, percents);
        Assert.Equal(UpdatePhase.ReadyToApply, harness.Service.State.Phase);
    }

    private static Dictionary<string, string?> Details(GalactiLog.Data.Entities.ActivityEvent row)
        => JsonSerializer.Deserialize<Dictionary<string, string?>>(row.Details ?? "{}") ?? [];
}
