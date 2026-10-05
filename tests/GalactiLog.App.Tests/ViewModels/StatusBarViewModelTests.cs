using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.Core.Scanning;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Plain xunit, no window: the command and its enablement are the view concern this type adds
// over ScanStatusService (design-spec 18.3). A synchronous post is used throughout, so every
// mutation is visible immediately without a dispatcher.
//
// Fix pass review item 6: ScanStatusService's properties are now partial properties with a
// private setter, so nothing outside that class can assign them directly any more (including
// this test's own Harness). Every state change below goes through the same public seam
// production uses: ScanCoordinator.RaiseProgress to reach "running", and a real (here,
// deliberately failing, since the coordinator's connection string is never migrated) RunAsync
// call to reach ScanFinished and drop back to idle.
public class StatusBarViewModelTests
{
    private sealed class Harness : IDisposable
    {
        public ScanCoordinator Coordinator { get; }
        public ScanStatusService Status { get; }
        public StatusBarViewModel ViewModel { get; }
        public int CancelInvocations { get; private set; }

        // Written on the pool thread the command's Task.Run uses, read on the test thread.
        private int _runScanInvocations;

        public int RunScanInvocations => Volatile.Read(ref _runScanInvocations);

        /// <summary>Parks the scan delegate until a test releases it, so a test can press the
        /// button a second time while the first run is genuinely in flight.</summary>
        public ManualResetEventSlim? ScanRelease { get; set; }

        /// <summary>Set the first time the scan delegate is entered.</summary>
        public ManualResetEventSlim ScanEntered { get; } = new(false);

        public Harness()
        {
            Coordinator = ScanCoordinatorTestFactory.CreateBare();
            Status = new ScanStatusService(Coordinator, action => action());
            ViewModel = new StatusBarViewModel(
                Status,
                () => CancelInvocations++,
                _ =>
                {
                    Interlocked.Increment(ref _runScanInvocations);
                    ScanEntered.Set();
                    ScanRelease?.Wait(TimeSpan.FromSeconds(30));
                    return Task.CompletedTask;
                });
        }

        // Reaches ScanTaskNames.Discovery-and-beyond -- i.e. IsRunning == true -- without a
        // real database.
        public void RaiseRunning(string taskName = ScanTaskNames.Discovery, string message = "Discovering...")
            => Coordinator.RaiseProgress(taskName, 1, 1, message, force: true);

        // Drops back to idle exactly the way ScanFinished does in production: the connection
        // string is never migrated, so this throws once ScanCoordinator's pipeline tries to
        // read general settings, but not before its finally block has already raised
        // ScanFinished -- which is the only thing this harness needs from the call.
        public async Task RaiseFinishedAsync()
            => await Assert.ThrowsAnyAsync<Exception>(
                () => Coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        public void Dispose()
        {
            ViewModel.Dispose();
            Status.Dispose();
            ScanEntered.Dispose();
        }
    }

    public static IEnumerable<object[]> EveryTaskNameWithItsDisplayText()
    {
        yield return [ScanTaskNames.Discovery, "Discovering"];
        yield return [ScanTaskNames.Classify, "Classifying"];
        yield return [ScanTaskNames.Ingest, "Ingesting"];
        yield return [ScanTaskNames.PruneOrphans, "Removing missing files"];
        yield return [ScanTaskNames.Dedup, "Finding duplicates"];
        yield return [ScanTaskNames.RefThumbnails, "Building thumbnails"];
        yield return [ScanTaskNames.PruneActivity, "Pruning activity"];
    }

    [Theory]
    [MemberData(nameof(EveryTaskNameWithItsDisplayText))]
    public void StateText_ForEachTaskName(string taskName, string expected)
    {
        using var harness = new Harness();

        harness.RaiseRunning(taskName);

        Assert.Equal(expected, harness.ViewModel.StateText);
    }

    [Fact]
    public void StateText_UnknownToken_FallsBackToTheToken()
    {
        using var harness = new Harness();

        harness.RaiseRunning("some_future_task");

        Assert.Equal("some_future_task", harness.ViewModel.StateText);
    }

    [Fact]
    public async Task CancelCommand_IsDisabledWhenIdle_AndEnabledWhileRunning()
    {
        using var harness = new Harness();

        Assert.False(harness.ViewModel.CancelCommand.CanExecute(null));

        harness.RaiseRunning();
        Assert.True(harness.ViewModel.CancelCommand.CanExecute(null));

        await harness.RaiseFinishedAsync();
        Assert.False(harness.ViewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void CancelCommand_Invokes_TheInjectedCancelDelegate_Once()
    {
        using var harness = new Harness();
        harness.RaiseRunning();

        harness.ViewModel.CancelCommand.Execute(null);

        Assert.Equal(1, harness.CancelInvocations);
    }

    [Fact]
    public void CancelCommand_CanExecuteChanged_RaisedWhenIsRunningChanges()
    {
        using var harness = new Harness();
        var raised = 0;
        harness.ViewModel.CancelCommand.CanExecuteChanged += (_, _) => raised++;

        harness.RaiseRunning();

        Assert.True(raised > 0);
    }

    // Phase 7 FIXER item 19: the dashboard's Run Scan button is part of its empty state, so a
    // library with frames had no manual rescan affordance. The status bar is always on screen.
    [Fact]
    public async Task RunScanCommand_InvokesTheInjectedScanDelegate()
    {
        using var harness = new Harness();

        Assert.True(harness.ViewModel.RunScanCommand.CanExecute(null));
        harness.ViewModel.RunScanCommand.Execute(null);
        await harness.ViewModel.RunScanCommand.ExecutionTask!;

        Assert.Equal(1, harness.RunScanInvocations);
    }

    [Fact]
    public async Task RunScanCommand_IsRefusedWhileAScanIsRunning()
    {
        using var harness = new Harness();
        harness.RaiseRunning();

        Assert.False(harness.ViewModel.RunScanCommand.CanExecute(null));

        // The body guards too, so a direct Execute is refused as well (TRACKING item 13).
        harness.ViewModel.RunScanCommand.Execute(null);
        if (harness.ViewModel.RunScanCommand.ExecutionTask is { } execution)
        {
            await execution;
        }

        Assert.Equal(0, harness.RunScanInvocations);
    }

    // FIXER LIST F24 (and Task 8's deviation D10): the command used to be built from a
    // Func<CancellationToken, Task>, so a second Execute cancelled the in-flight command token and
    // the running scan was aborted rather than the second press being refused.
    [Fact]
    public async Task RunScanCommand_ExecutedTwice_RefusesTheSecondPress_AndDoesNotAbortTheFirst()
    {
        using var release = new ManualResetEventSlim(false);
        using var harness = new Harness { ScanRelease = release };

        harness.ViewModel.RunScanCommand.Execute(null);
        var first = harness.ViewModel.RunScanCommand.ExecutionTask!;
        Assert.True(harness.ScanEntered.Wait(TimeSpan.FromSeconds(30)));

        // The state the body guard reads, reached through the same seam production uses.
        harness.RaiseRunning();
        harness.ViewModel.RunScanCommand.Execute(null);

        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, harness.RunScanInvocations);
    }

    [Fact]
    public void RunScanCommand_WithNoDelegate_IsDisabled()
    {
        using var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        using var viewModel = new StatusBarViewModel(status, () => { });

        Assert.False(viewModel.RunScanCommand.CanExecute(null));
    }

    [Fact]
    public void Message_TracksTheService()
    {
        using var harness = new Harness();

        harness.RaiseRunning(ScanTaskNames.Classify, "Classifying 3/10 files");

        Assert.Equal("Classifying 3/10 files", harness.ViewModel.Status.Message);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheService()
    {
        using var harness = new Harness();
        harness.ViewModel.Dispose();

        var raised = 0;
        harness.ViewModel.CancelCommand.CanExecuteChanged += (_, _) => raised++;

        harness.RaiseRunning();

        Assert.Equal(0, raised);
    }

    // ---- Phase 10 Task 4: spec 12's update indicator ----------------------------------------

    // A second harness rather than four more constructor parameters on the first: the update half
    // needs an UpdateService over a recording checker and a temp database, and the scan half
    // above needs neither.
    private sealed class UpdateHarness : IDisposable
    {
        public UpdateHarness()
        {
            Database = new TempDatabase("galactilog-statusbar-update");
            Coordinator = ScanCoordinatorTestFactory.CreateBare();
            Status = new ScanStatusService(Coordinator, action => action());
            Updates = new UpdateService(
                new BuildInfo("1.0.0.0", "abc1234", "alpha", isInstalled: true),
                () => Checker,
                Status,
                new ActivityRepository(Database.ConnectionString),
                showPrompt: () =>
                {
                    Interlocked.Increment(ref _prompts);

                    // The seam reports whether a window opened, not what the user answered.
                    return Task.FromResult(true);
                },
                post: action => action());
            ViewModel = new StatusBarViewModel(Status, () => { }, updates: Updates);
        }

        private int _prompts;

        public TempDatabase Database { get; }

        public ScanCoordinator Coordinator { get; }

        public ScanStatusService Status { get; }

        public RecordingUpdateChecker Checker { get; } = new();

        public UpdateService Updates { get; }

        public StatusBarViewModel ViewModel { get; }

        public int Prompts => Volatile.Read(ref _prompts);

        public void RaiseRunning()
            => Coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 1, "Discovering...", force: true);

        public void Dispose()
        {
            ViewModel.Dispose();
            Updates.Dispose();
            Status.Dispose();
            Database.Dispose();
        }
    }

    [Fact]
    public async Task UpdateIndicator_IsHidden_WhenThePhaseIsIdleOrChecking()
    {
        using var harness = new UpdateHarness();

        // Idle, before any check.
        Assert.False(harness.ViewModel.UpdateAvailable);
        Assert.Equal("", harness.ViewModel.UpdateText);

        // Checking, observed from inside the check itself, which is the only moment that phase
        // exists.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawChecking = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Checker.CheckGate = gate;
        harness.Updates.StateChanged += (_, state) =>
        {
            if (state.Phase == UpdatePhase.Checking)
            {
                sawChecking.TrySetResult(harness.ViewModel.UpdateAvailable);
            }
        };

        var check = harness.Updates.CheckNowAsync();
        Assert.False(await sawChecking.Task.WaitAsync(TimeSpan.FromSeconds(30)));

        gate.SetResult();
        await check;
    }

    [Fact]
    public async Task UpdateIndicator_IsHidden_WhenThePhaseIsFailed()
    {
        using var harness = new UpdateHarness();
        harness.Checker.CheckThrows = new InvalidOperationException("the feed was unreachable");

        await harness.Updates.CheckNowAsync();

        // A failed background check is a log line and an About tab field, not a banner.
        Assert.Equal(UpdatePhase.Failed, harness.Updates.State.Phase);
        Assert.False(harness.ViewModel.UpdateAvailable);
        Assert.False(harness.ViewModel.ShowUpdatePromptCommand.CanExecute(null));
    }

    [Fact]
    public async Task UpdateIndicator_ShowsDownloadPercent_WhileDownloading()
    {
        using var harness = new UpdateHarness();
        harness.Checker.Offered = new AvailableUpdate("1.1.0", "Notes", new object());
        harness.Checker.ProgressPercents = [40];

        var seen = new List<(bool Downloading, double Percent)>();
        harness.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StatusBarViewModel.UpdateDownloadPercent))
            {
                seen.Add((harness.ViewModel.IsDownloadingUpdate, harness.ViewModel.UpdateDownloadPercent));
            }
        };

        await harness.Updates.CheckNowAsync();

        Assert.Contains((true, 40d), seen);
        Assert.True(harness.ViewModel.UpdateAvailable);
        Assert.Contains("1.1.0", harness.ViewModel.UpdateText, StringComparison.Ordinal);
        Assert.False(harness.ViewModel.IsDownloadingUpdate);
    }

    [Fact]
    public async Task UpdateIndicator_DoesNotDriveTheScanProgressBar()
    {
        using var harness = new UpdateHarness();
        harness.Checker.Offered = new AvailableUpdate("1.1.0", "Notes", new object());
        harness.Checker.ProgressPercents = [40, 100];

        await harness.Updates.CheckNowAsync();

        // Two different operations, two different bars. The scan's stays where it was.
        Assert.Equal(0d, harness.ViewModel.Status.Percent);
        Assert.False(harness.ViewModel.Status.IsRunning);
        Assert.Equal(100d, harness.ViewModel.UpdateDownloadPercent);
    }

    [Fact]
    public async Task UpdatePrompt_IsRefused_WhileAScanIsRunning()
    {
        using var harness = new UpdateHarness();
        harness.Checker.Offered = new AvailableUpdate("1.1.0", "Notes", new object());
        await harness.Updates.CheckNowAsync();
        var promptsAfterTheCheck = harness.Prompts;

        harness.RaiseRunning();
        Assert.False(harness.ViewModel.ShowUpdatePromptCommand.CanExecute(null));

        // A direct Execute bypasses CanExecute, so the body repeats the rule.
        harness.ViewModel.ShowUpdatePromptCommand.Execute(null);

        Assert.Equal(promptsAfterTheCheck, harness.Prompts);
    }

    [Fact]
    public void StatusBar_BuiltWithNoUpdateService_ShowsNoIndicator_AndDoesNotThrow()
    {
        using var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        using var viewModel = new StatusBarViewModel(status, () => { });

        Assert.False(viewModel.UpdateAvailable);
        Assert.False(viewModel.IsDownloadingUpdate);
        Assert.Equal("", viewModel.UpdateText);
        Assert.Equal(0d, viewModel.UpdateDownloadPercent);
        Assert.False(viewModel.ShowUpdatePromptCommand.CanExecute(null));

        // The body's guard, on a view-model that has no service to reach at all.
        viewModel.ShowUpdatePromptCommand.Execute(null);
    }

    [Fact]
    public async Task Dispose_DetachesTheUpdateStateSubscription()
    {
        using var harness = new UpdateHarness();
        harness.ViewModel.Dispose();

        harness.Checker.Offered = new AvailableUpdate("1.1.0", "Notes", new object());
        await harness.Updates.CheckNowAsync();

        Assert.False(harness.ViewModel.UpdateAvailable);
        Assert.Equal("", harness.ViewModel.UpdateText);
    }

    // ---- spec 12's job monitor (PAR-015, Phase 14B Task 2) -----------------------------------

    [Fact]
    public void HasRunningJobs_IsFalseWithNoJobs()
    {
        using var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        var jobs = new JobRegistry(action => action());
        using var viewModel = new StatusBarViewModel(status, () => { }, jobs: jobs);

        Assert.False(viewModel.HasRunningJobs);
        Assert.Equal("", viewModel.RunningJobsText);
        Assert.Same(jobs, viewModel.Jobs);
    }

    [Fact]
    public void RunningJobsText_IsSingularForOne_AndPluralForTwo()
    {
        using var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        var jobs = new JobRegistry(action => action());
        using var viewModel = new StatusBarViewModel(status, () => { }, jobs: jobs);

        jobs.Begin("scan", "Library scan");
        Assert.Equal("1 job", viewModel.RunningJobsText);

        jobs.Begin("prune_activity", "Prune activity log");
        Assert.Equal("2 jobs", viewModel.RunningJobsText);
    }

    [Fact]
    public void TheCount_TracksTheRegistry()
    {
        using var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        var jobs = new JobRegistry(action => action());
        using var viewModel = new StatusBarViewModel(status, () => { }, jobs: jobs);

        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var handle = jobs.Begin("scan", "Library scan");

        Assert.True(viewModel.HasRunningJobs);
        Assert.Contains(nameof(StatusBarViewModel.HasRunningJobs), raised);
        Assert.Contains(nameof(StatusBarViewModel.RunningJobsText), raised);

        handle.Finish(JobResult.Succeeded, "The scan finished.");

        Assert.False(viewModel.HasRunningJobs);
        Assert.Equal("", viewModel.RunningJobsText);
        Assert.Single(jobs.Recent);
    }

    [Fact]
    public void AStatusBarBuiltWithNoRegistry_ShowsNoJobMonitor_AndDoesNotThrow()
    {
        using var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        using var viewModel = new StatusBarViewModel(status, () => { });

        Assert.Null(viewModel.Jobs);
        Assert.False(viewModel.HasRunningJobs);
        Assert.Equal("", viewModel.RunningJobsText);
    }

    // ---- fix pass, review escalation 1 -------------------------------------------------------

    // The spec keeps an outcome "because an outcome nobody has looked at yet is the reason the list
    // exists". Hiding the monitor the instant the running count reached zero made that list
    // unreachable at exactly the moment it became worth reading.
    [Fact]
    public void HasJobsToShow_StaysTrueAfterTheLastJobFinishes()
    {
        using var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        var jobs = new JobRegistry(action => action());
        using var viewModel = new StatusBarViewModel(status, () => { }, jobs: jobs);

        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var handle = jobs.Begin("scan", "Library scan");
        Assert.True(viewModel.HasJobsToShow);
        Assert.Contains(nameof(StatusBarViewModel.HasJobsToShow), raised);

        handle.Finish(JobResult.Succeeded, "The scan finished.");

        Assert.False(viewModel.HasRunningJobs);
        Assert.Equal("", viewModel.RunningJobsText);
        Assert.True(viewModel.HasJobsToShow);
        Assert.Single(jobs.Recent);
    }

    [Fact]
    public void HasJobsToShow_IsFalseWithAnEmptyRegistry_AndWithNoRegistryAtAll()
    {
        using var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        var jobs = new JobRegistry(action => action());
        using var withRegistry = new StatusBarViewModel(status, () => { }, jobs: jobs);
        using var withNone = new StatusBarViewModel(status, () => { });

        Assert.Empty(jobs.Running);
        Assert.Empty(jobs.Recent);
        Assert.False(withRegistry.HasJobsToShow);
        Assert.False(withNone.HasJobsToShow);
    }
}
