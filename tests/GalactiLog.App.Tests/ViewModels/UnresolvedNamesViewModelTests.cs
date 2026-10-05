using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 7 Task 6. Spec 12.7's unresolved-name list, spec 9.7's retry action, and spec 12.10's
// "Every OBJECT name resolved." empty state. Every collaborator is a lambda, so nothing here
// touches a database or the network.
public class UnresolvedNamesViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static UnresolvedNameRow Row(string name = "Zzyzx Blob 42", int frameCount = 12)
        => new(name, "obj:" + name, frameCount);

    private static UnresolvedRetry.RetryOutcome Outcome(
        int cleared = 3, int examined = 2, int resolved = 1, int frames = 4, int stillUnresolved = 1,
        bool stopped = false)
        => new(cleared, examined, resolved, frames, stillUnresolved, stopped);

    private sealed class Harness : IDisposable
    {
        public RecordingLogger Logger { get; } = new();

        public IReadOnlyList<UnresolvedNameRow> Names { get; set; } = [];

        public int Loads;

        /// <summary>The thread every load ran on, when <see cref="Create"/> was given an
        /// <c>onUiThread</c> reader.</summary>
        public List<bool> LoadOnUiThread { get; } = [];

        public int Retries;

        /// <summary>The thread every retry ran on.</summary>
        public List<bool> RetryOnUiThread { get; } = [];

        /// <summary>Progress the retry delegate publishes, one entry per call.</summary>
        public List<(int Step, int Total, string Message)> RetryReports { get; } = [];

        public UnresolvedRetry.RetryOutcome RetryOutcome { get; set; } = Outcome();

        /// <summary>Thrown by the retry delegate while set.</summary>
        public Exception? RetryThrows { get; set; }

        /// <summary>Parks the retry delegate until a test releases it.</summary>
        public ManualResetEventSlim RetryRelease { get; } = new(initialState: true);

        /// <summary>Set by the retry delegate the moment it starts.</summary>
        public ManualResetEventSlim RetryStarted { get; } = new();

        /// <summary>True when the retry delegate observed its cancellation token.</summary>
        public bool RetryCancelled;

        /// <summary>Waits for its own token instead of the release gate, so a test can cancel it.
        /// </summary>
        public bool RetryWaitsForCancellation { get; set; }

        public List<string> AssignedNames { get; } = [];

        public bool AssignResult { get; set; }

        /// <summary>Every name the create-target row action opened the form for (Phase 14B Task
        /// 3's seam).</summary>
        public List<string> CreatedNames { get; } = [];

        public ScanStatusService? ScanStatus { get; init; }

        public UnresolvedNamesViewModel ViewModel { get; internal set; } = null!;

        /// <summary>Joins the in-flight load. The blocking wait lives here rather than in a test
        /// method, which is what xunit's own analyzer asks for.</summary>
        public Harness Settle()
        {
            try
            {
                ViewModel.PendingLoad?.Wait(Budget);
            }
            catch (AggregateException)
            {
                // A load cancelled by Dispose counts as settled.
            }

            return this;
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            RetryRelease.Dispose();
            RetryStarted.Dispose();
        }
    }

    private static Harness Create(
        IReadOnlyList<UnresolvedNameRow>? names = null,
        ScanStatusService? scanStatus = null,
        Func<bool>? onUiThread = null,
        ManualResetEventSlim? loadRelease = null,
        bool withDialog = true,
        bool withCreateForm = true,
        Action<Action>? post = null)
    {
        var harness = new Harness { ScanStatus = scanStatus, Names = names ?? [] };

        Func<string, Task<bool>>? assign = withDialog
            ? name =>
            {
                lock (harness.AssignedNames)
                {
                    harness.AssignedNames.Add(name);
                }

                return Task.FromResult(harness.AssignResult);
            }
            : null;

        // Null leaves the create-target row action disabled, the same rule assign follows.
        Action<string>? createTarget = withCreateForm
            ? name =>
            {
                lock (harness.CreatedNames)
                {
                    harness.CreatedNames.Add(name);
                }
            }
            : null;

        harness.ViewModel = new UnresolvedNamesViewModel(
            () =>
            {
                Interlocked.Increment(ref harness.Loads);
                lock (harness.LoadOnUiThread)
                {
                    if (onUiThread is not null)
                    {
                        harness.LoadOnUiThread.Add(onUiThread());
                    }
                }

                loadRelease?.Wait(Budget);
                return harness.Names;
            },
            (report, token) =>
            {
                Interlocked.Increment(ref harness.Retries);
                lock (harness.RetryOnUiThread)
                {
                    if (onUiThread is not null)
                    {
                        harness.RetryOnUiThread.Add(onUiThread());
                    }
                }

                harness.RetryStarted.Set();

                if (harness.RetryWaitsForCancellation)
                {
                    token.WaitHandle.WaitOne(Budget);
                    harness.RetryCancelled = token.IsCancellationRequested;
                    token.ThrowIfCancellationRequested();
                }
                else
                {
                    harness.RetryRelease.Wait(Budget);
                }

                if (harness.RetryThrows is { } failure)
                {
                    throw failure;
                }

                foreach (var (step, total, message) in harness.RetryReports.ToList())
                {
                    report(step, total, message);
                }

                return harness.RetryOutcome;
            },
            assign,
            scanStatus,
            post: post ?? (action => action()),
            logger: harness.Logger,
            createTarget: createTarget);

        return harness;
    }

    private static (ScanCoordinator Coordinator, ScanStatusService Status) EmptyScan(SettingsFixture settings)
    {
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        return (coordinator, new ScanStatusService(coordinator, action => action()));
    }

    // ---- loading -------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Construction_LoadsTheNamesOffTheUiThread()
    {
        // UnresolvedNamesQuery is a synchronous SQLite read; it must never run on the dispatcher.
        // The load is awaited rather than blocked on (TRACKING section 2 item 8).
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        using var harness = Create([Row()], onUiThread: () => Dispatcher.UIThread.CheckAccess());
        await harness.ViewModel.PendingLoad!;

        Assert.Equal(1, harness.Loads);
        Assert.NotEmpty(harness.LoadOnUiThread);
        Assert.All(harness.LoadOnUiThread, Assert.False);
        var row = Assert.Single(harness.ViewModel.Names);
        Assert.Equal("Zzyzx Blob 42", row.Name);
        Assert.Equal("obj:Zzyzx Blob 42", row.GroupKey);
    }

    [Fact]
    public void EmptyList_ShowsTheEveryObjectNameResolvedState()
    {
        using var harness = Create().Settle();

        Assert.Empty(harness.ViewModel.Names);
        Assert.True(harness.ViewModel.ShowAllResolved);
    }

    [Fact]
    public void EmptyStateIsHiddenWhileTheFirstLoadIsInFlight()
    {
        using var release = new ManualResetEventSlim();
        using var harness = Create(loadRelease: release);

        Assert.False(harness.ViewModel.ShowAllResolved);

        release.Set();
        harness.Settle();
        Assert.True(harness.ViewModel.ShowAllResolved);
    }

    // ---- the retry -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Retry_RunsOffTheUiThread()
    {
        // UnresolvedRetry reaches the network; it must never run on the dispatcher.
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        using var harness = Create([Row()], onUiThread: () => Dispatcher.UIThread.CheckAccess()).Settle();
        await harness.ViewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Retries);
        Assert.NotEmpty(harness.RetryOnUiThread);
        Assert.All(harness.RetryOnUiThread, Assert.False);
    }

    [AvaloniaFact]
    public async Task Retry_IsDisabledWhileRunning()
    {
        using var harness = Create([Row()]).Settle();
        harness.RetryRelease.Reset();

        var retry = harness.ViewModel.RetryCommand.ExecuteAsync(null);
        harness.RetryStarted.Wait(Budget);

        Assert.True(harness.ViewModel.IsRetrying);
        Assert.False(harness.ViewModel.RetryCommand.CanExecute(null));

        harness.RetryRelease.Set();
        await retry;

        Assert.False(harness.ViewModel.IsRetrying);
        Assert.True(harness.ViewModel.RetryCommand.CanExecute(null));
    }

    // FIXER LIST F24 (and Task 8's deviation D10): the command used to be built from a
    // Func<CancellationToken, Task>, so a second Execute cancelled the in-flight command token and
    // the running retry was aborted mid-loop rather than the second press being refused.
    [AvaloniaFact]
    public async Task Retry_ExecutedTwice_RefusesTheSecondPress_AndDoesNotAbortTheFirst()
    {
        using var harness = Create([Row()]).Settle();
        harness.RetryRelease.Reset();

        harness.ViewModel.RetryCommand.Execute(null);
        var first = harness.ViewModel.RetryCommand.ExecutionTask!;
        harness.RetryStarted.Wait(Budget);
        Assert.True(harness.ViewModel.IsRetrying);

        // The second press lands while the first delegate is parked. It replaces the command's
        // ExecutionTask, which is why the first one is held onto above.
        harness.ViewModel.RetryCommand.Execute(null);

        harness.RetryRelease.Set();
        await first.WaitAsync(Budget);

        Assert.Equal(1, harness.Retries);
        Assert.False(harness.ViewModel.IsRetrying);

        // And the guard is released, so a later press still works.
        await harness.ViewModel.RetryCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.Retries);
    }

    // FIXER LIST F15: the Maintenance tab's retry takes the resolution lease without a scan
    // running, so a button that read only IsRunning stayed enabled through a run it could not win.
    [Fact]
    public void Retry_IsDisabledWhileAnotherRetryHoldsTheResolutionLease()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Create([Row()], status).Settle();

        Assert.False(harness.ViewModel.ScanRunning);
        Assert.True(harness.ViewModel.RetryCommand.CanExecute(null));

        var raised = 0;
        harness.ViewModel.RetryCommand.CanExecuteChanged += (_, _) => raised++;

        // The same lease the Maintenance tab's retry takes, through the same seam production uses.
        var lease = coordinator.TryBeginResolution();
        Assert.NotNull(lease);
        Assert.True(status.ResolutionInProgress);
        Assert.False(status.IsRunning);

        Assert.True(harness.ViewModel.ScanRunning);
        Assert.False(harness.ViewModel.RetryCommand.CanExecute(null));
        Assert.True(raised > 0, "the command must be told its CanExecute changed");

        lease!.Dispose();
        Assert.False(harness.ViewModel.ScanRunning);
        Assert.True(harness.ViewModel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public void Retry_IsDisabledWhileAScanIsRunning()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Create([Row()], status).Settle();

        Assert.False(harness.ViewModel.ScanRunning);
        Assert.True(harness.ViewModel.RetryCommand.CanExecute(null));

        // A scan owns resolution and the negative cache for its whole run (spec 5.1, 9.6). The
        // button greys from the same ScanStatusService.IsRunning signal the dashboard's Run scan
        // button uses; the refusal itself is UnresolvedRetry.Run's.
        var raised = 0;
        harness.ViewModel.RetryCommand.CanExecuteChanged += (_, _) => raised++;
        coordinator.RaiseProgress(ScanTaskNames.Classify, 1, 2, "Classifying...", force: true);

        Assert.True(harness.ViewModel.ScanRunning);
        Assert.False(harness.ViewModel.RetryCommand.CanExecute(null));
        Assert.True(raised > 0, "the command must be told its CanExecute changed");
    }

    [Fact]
    public async Task Retry_WhileAScanIsRunning_DoesNotCallTheService()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Create([Row()], status).Settle();
        coordinator.RaiseProgress(ScanTaskNames.Classify, 1, 2, "Classifying...", force: true);

        // The body re-checks, because RelayCommand.Execute ignores CanExecute.
        await harness.ViewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.Retries);
        Assert.False(harness.ViewModel.IsRetrying);
        Assert.Equal(
            "A scan is running. The retry is available when it finishes.",
            harness.ViewModel.RetrySummary);
    }

    [AvaloniaFact]
    public async Task Retry_RefusedByTheService_ShowsTheScanInProgressState()
    {
        using var harness = Create([Row()]).Settle();
        harness.RetryOutcome = new UnresolvedRetry.RetryOutcome(
            0, 0, 0, 0, 0, false, UnresolvedRetry.RetryStatus.ScanInProgress);

        await harness.ViewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal(
            "A scan is running. The retry is available when it finishes.",
            harness.ViewModel.RetrySummary);
    }

    [AvaloniaFact]
    public async Task Retry_PublishesProgress()
    {
        using var harness = Create([Row()]).Settle();
        harness.RetryReports.Add((1, 2, "Retrying 1/2 unresolved names..."));

        var seen = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UnresolvedNamesViewModel.RetryProgress))
            {
                seen.Add(harness.ViewModel.RetryProgress);
            }
        };

        await harness.ViewModel.RetryCommand.ExecuteAsync(null);

        Assert.Contains("Retrying 1/2 unresolved names...", seen);

        // The line is cleared when the run finishes, so a stale progress message does not sit
        // under a finished summary.
        Assert.Null(harness.ViewModel.RetryProgress);
    }

    [AvaloniaFact]
    public async Task Retry_ReloadsTheListWhenItFinishes()
    {
        using var harness = Create([Row()]).Settle();
        Assert.Equal(1, harness.Loads);

        harness.Names = [];
        await harness.ViewModel.RetryCommand.ExecuteAsync(null);
        harness.Settle();

        Assert.Equal(2, harness.Loads);
        Assert.Empty(harness.ViewModel.Names);
        Assert.True(harness.ViewModel.ShowAllResolved);
    }

    [AvaloniaFact]
    public async Task Retry_ReportsTheSummary()
    {
        using var harness = Create([Row()]).Settle();
        harness.RetryOutcome = Outcome(examined: 2, resolved: 1, frames: 4, stillUnresolved: 1);

        await harness.ViewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal(
            "Retried 2 names: 1 resolved, 4 frames assigned, 1 still unresolved.",
            harness.ViewModel.RetrySummary);
    }

    [AvaloniaFact]
    public async Task Retry_ThatThrows_ReportsAMessageAndReenablesTheButton()
    {
        using var harness = Create([Row()]).Settle();
        harness.RetryThrows = new InvalidOperationException("the catalogue exploded");

        await harness.ViewModel.RetryCommand.ExecuteAsync(null);

        // No text from the exception on screen; the log carries that.
        Assert.Equal(
            "The retry could not be completed. See the log for details.",
            harness.ViewModel.RetrySummary);
        Assert.DoesNotContain("exploded", harness.ViewModel.RetrySummary);
        Assert.False(harness.ViewModel.IsRetrying);
        Assert.True(harness.ViewModel.RetryCommand.CanExecute(null));
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    // ---- the assign action ---------------------------------------------------------------

    [AvaloniaFact]
    public async Task Assign_OpensTheMergeDialogForThatName()
    {
        using var harness = Create([Row()]).Settle();

        await harness.ViewModel.AssignCommand.ExecuteAsync(harness.ViewModel.Names[0]);

        Assert.Equal(["Zzyzx Blob 42"], harness.AssignedNames);
    }

    [AvaloniaFact]
    public async Task Assign_ThatMerged_ReloadsTheList()
    {
        using var harness = Create([Row()]).Settle();
        harness.AssignResult = true;

        await harness.ViewModel.AssignCommand.ExecuteAsync(harness.ViewModel.Names[0]);
        harness.Settle();

        Assert.Equal(2, harness.Loads);
    }

    [Fact]
    public void Assign_WithNoDialog_IsDisabled()
    {
        using var harness = Create([Row()], withDialog: false).Settle();

        Assert.False(harness.ViewModel.AssignCommand.CanExecute(harness.ViewModel.Names[0]));
    }

    // ---- refresh and disposal --------------------------------------------------------------

    [Fact]
    public async Task ScanFinished_ReloadsTheList()
    {
        using var settings = new SettingsFixture();
        var (coordinator, scanStatus) = EmptyScan(settings);
        using var status = scanStatus;

        using var harness = Create([Row()], status).Settle();
        harness.Names = [Row("first"), Row("second")];

        // A scan can both add and remove unresolved names, so the list is re-read.
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        harness.Settle();

        Assert.Equal(2, harness.Loads);
        Assert.Equal(2, harness.ViewModel.Names.Count);
    }

    [AvaloniaFact]
    public async Task Dispose_CancelsAnInFlightRetry()
    {
        using var harness = Create([Row()]).Settle();
        harness.RetryWaitsForCancellation = true;

        var retry = harness.ViewModel.RetryCommand.ExecuteAsync(null);
        harness.RetryStarted.Wait(Budget);

        // Closing the application must not leave a resolution loop walking the name list.
        harness.ViewModel.Dispose();
        await retry;

        Assert.True(harness.RetryCancelled);
    }

    // ---- spec 12.7's create target row action (PAR-001, Phase 14B Task 3) -------------------

    [Fact]
    public void CreateTargetCommand_IsDisabledWithNoDelegate()
    {
        using var without = Create([Row()], withCreateForm: false).Settle();
        using var with = Create([Row()]).Settle();

        var row = new UnresolvedNameRowViewModel(Row());
        Assert.False(without.ViewModel.CreateTargetCommand.CanExecute(row));
        Assert.True(with.ViewModel.CreateTargetCommand.CanExecute(row));
    }

    [Fact]
    public void CreateTargetCommand_OpensTheFormWithTheNamePreFilled()
    {
        using var harness = Create([Row()]).Settle();

        harness.ViewModel.CreateTargetCommand.Execute(new UnresolvedNameRowViewModel(Row("Comet C/2026 X1")));

        Assert.Equal("Comet C/2026 X1", Assert.Single(harness.CreatedNames));
    }

    [Fact]
    public void CreateTargetCommand_ExecutedPastCanExecute_DoesNothingWithNoDelegate()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the null guard
        // is repeated in the command body.
        using var harness = Create([Row()], withCreateForm: false).Settle();

        harness.ViewModel.CreateTargetCommand.Execute(new UnresolvedNameRowViewModel(Row()));

        Assert.Empty(harness.CreatedNames);
    }

    // ---- the row view-model ----------------------------------------------------------------

    [Fact]
    public void RowViewModel_FrameCountText_SingularAndPlural()
    {
        // The same wording SearchResultViewModel.FrameCountText uses.
        Assert.Equal("1 frame", new UnresolvedNameRowViewModel(Row(frameCount: 1)).FrameCountText);
        Assert.Equal("12 frames", new UnresolvedNameRowViewModel(Row(frameCount: 12)).FrameCountText);
    }
}
