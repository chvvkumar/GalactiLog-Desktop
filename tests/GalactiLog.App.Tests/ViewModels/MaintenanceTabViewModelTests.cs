using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Data.Ingest;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.7's Maintenance tab. Delegates everywhere, so nothing here opens a database, a cache
// root or a window (design-spec 18.3). The dangerous halves are tested where they live:
// TargetRebuildTests, DatabaseResetTests and ThumbnailPurgeTests. What is tested here is the tab's
// own contract: one action at a time, the scan gate repeated in every command body, the spec 10.9
// events, and force: true on the "all" reference button.
public class MaintenanceTabViewModelTests
{
    private static readonly string[] EveryToken =
    [
        MaintenanceTabViewModel.RebuildTargetsAction,
        MaintenanceTabViewModel.RetryUnresolvedAction,
        MaintenanceTabViewModel.SmartRebuildAction,
        MaintenanceTabViewModel.CatalogIdentityBackfillAction,
        MaintenanceTabViewModel.ReferenceThumbnailsAction,
        MaintenanceTabViewModel.FrameThumbnailsAction,
        MaintenanceTabViewModel.PruneActivityAction,
        MaintenanceTabViewModel.ResetDatabaseAction,
    ];

    // Everything one test needs to drive and observe the tab.
    private sealed class Harness : IDisposable
    {
        public readonly Dictionary<string, int> Ran = [];
        public readonly List<(string EventType, string Message, object? Details)> Events = [];
        public readonly List<bool> ReferenceForces = [];
        public readonly List<int> PrunedWith = [];
        public readonly List<int> WorkThreads = [];
        public readonly List<CancellationToken> PurgeTokens = [];

        // One ordered log of everything that happened, so a test can assert that the started event
        // really precedes the work rather than only that the two events are in order.
        public readonly List<string> Trace = [];

        public int RetentionDays = 45;
        public int PurgeResult = 7;
        public int PruneResult = 12;
        public string? ResetSummary = "Database reset: 3 rows deleted from 9 tables. Settings and catalogues were kept.";
        public bool ResetFailed;
        public Exception? Throw;

        // Release for the blocking bodies. Unset means every body returns immediately.
        public ManualResetEventSlim? Gate;
        public TaskCompletionSource? AsyncGate;

        public ScanCoordinator Coordinator { get; } = ScanCoordinatorTestFactory.CreateBare();

        public ScanStatusService Status { get; }

        /// <summary>Spec 12's job registry (PAR-015). A synchronous post, like the tab's own, so a
        /// registration is visible the moment it happens.</summary>
        public JobRegistry Jobs { get; } = new(action => action());

        public MaintenanceTabViewModel ViewModel { get; }

        public Harness()
        {
            Status = new ScanStatusService(Coordinator, action => action());
            ViewModel = new MaintenanceTabViewModel(
                (report, ct) =>
                {
                    Enter(MaintenanceTabViewModel.RebuildTargetsAction);
                    report(1, 1, "Rebuilding 1/1 names...");
                    return new TargetRebuild.RebuildOutcome(4, 2, 1, 1, 3, 0);
                },
                (report, ct) =>
                {
                    Enter(MaintenanceTabViewModel.RetryUnresolvedAction);
                    report(1, 1, "Retrying 1/1 unresolved names...");
                    return new UnresolvedRetry.RetryOutcome(5, 1, 1, 2, 0, false);
                },
                (report, ct) =>
                {
                    Enter(MaintenanceTabViewModel.SmartRebuildAction);
                    report(1, 6, "Redirecting frames from merged targets...");
                    return new SmartRebuild.SmartRebuildOutcome(3, 2, 4, 1, 1, 5, 2);
                },
                (report, ct) =>
                {
                    Enter(MaintenanceTabViewModel.CatalogIdentityBackfillAction);
                    report(1, 1, "Backfilling 1/1 names...");
                    return new CatalogIdentityBackfill.BackfillOutcome(2, 7, 3);
                },
                // Wrapped in a Task.Run, as ScanCoordinator.RunReferenceThumbnailsAsync is: this
                // delegate is already asynchronous in production, so a double that blocked the
                // caller would be modelling something that cannot happen.
                (force, report, ct) =>
                {
                    ReferenceForces.Add(force);
                    return Task.Run(
                        () =>
                        {
                            Enter(MaintenanceTabViewModel.ReferenceThumbnailsAction);
                            report(1, 1, "Reference thumbnails 1/1");
                            return (ReferenceThumbnailPass.ReferenceThumbnailOutcome?)
                                new ReferenceThumbnailPass.ReferenceThumbnailOutcome(2, 2, 0, false);
                        },
                        ct);
                },
                purgeToken =>
                {
                    PurgeTokens.Add(purgeToken);
                    Enter(MaintenanceTabViewModel.FrameThumbnailsAction);
                    return PurgeResult;
                },
                () => RetentionDays,
                days =>
                {
                    PrunedWith.Add(days);
                    Enter(MaintenanceTabViewModel.PruneActivityAction);
                    return PruneResult;
                },
                // The reset's confirmation opens a window, so this one is called on the UI thread
                // by design and must never block it: the delete runs inside the dialog, on a
                // Task.Run of its own.
                () =>
                {
                    Record(MaintenanceTabViewModel.ResetDatabaseAction);
                    return AsyncGate is { } pending
                        ? pending.Task.ContinueWith<(string?, bool)>(
                            _ => (ResetSummary, ResetFailed), TaskScheduler.Default)
                        : Task.FromResult<(string?, bool)>((ResetSummary, ResetFailed));
                },
                (eventType, message, details) =>
                {
                    lock (Ran)
                    {
                        Events.Add((eventType, message, details));
                        Trace.Add("event:" + eventType);
                    }
                },
                scanStatus: Status,
                post: action => action(),
                jobs: Jobs);
        }

        private void Record(string token)
        {
            lock (Ran)
            {
                Ran[token] = Ran.TryGetValue(token, out var count) ? count + 1 : 1;
                WorkThreads.Add(Environment.CurrentManagedThreadId);
                Trace.Add("work:" + token);
            }
        }

        private void Enter(string token)
        {
            Record(token);

            Gate?.Wait(TimeSpan.FromSeconds(30));

            if (Throw is { } failure)
            {
                throw failure;
            }
        }

        public int RanCount(string token)
        {
            lock (Ran)
            {
                return Ran.TryGetValue(token, out var count) ? count : 0;
            }
        }

        public MaintenanceActionViewModel Action(string token) => ViewModel.Action(token);

        /// <summary>Presses a card's button, arming the inline confirm first where the card has
        /// one, and completes with the run.</summary>
        public async Task PressAsync(string token, int button = 0)
        {
            var action = ViewModel.Action(token);
            var command = action.Buttons[button].Command;

            if (action.RequiresConfirm && !action.ConfirmPending)
            {
                command.Execute(null);
                if (command.ExecutionTask is { } arm)
                {
                    await arm;
                }
            }

            command.Execute(null);
            if (command.ExecutionTask is { } run)
            {
                await run;
            }
        }

        /// <summary>Presses without awaiting, for the one-at-a-time cases.</summary>
        public void Press(string token, int button = 0)
        {
            var action = ViewModel.Action(token);
            if (action.RequiresConfirm && !action.ConfirmPending)
            {
                action.Buttons[button].Command.Execute(null);
            }

            action.Buttons[button].Command.Execute(null);
        }

        public void StartScan()
            => Coordinator.RaiseProgress(ScanTaskNames.Classify, 1, 2, "Classifying...", force: true);

        public void Dispose()
        {
            ViewModel.Dispose();
            Status.Dispose();
            Gate?.Dispose();
        }
    }

    // ---- the eight cards ---------------------------------------------------------------------

    [Fact]
    public void TheTab_HasTheEightSpecActions_InOrder()
    {
        using var harness = new Harness();
        Assert.Equal(EveryToken, harness.ViewModel.Actions.Select(action => action.Token));
    }

    [Fact]
    public void EveryAction_UsesItsOwnActionToken()
    {
        using var harness = new Harness();
        Assert.Equal(8, harness.ViewModel.Actions.Select(action => action.Token).Distinct().Count());
        Assert.Equal("reset_database", MaintenanceTabViewModel.ResetDatabaseAction);
        Assert.All(harness.ViewModel.Actions, action => Assert.NotEmpty(action.Buttons));
    }

    // ---- one at a time -----------------------------------------------------------------------

    [Theory]
    [InlineData(MaintenanceTabViewModel.RebuildTargetsAction)]
    [InlineData(MaintenanceTabViewModel.RetryUnresolvedAction)]
    [InlineData(MaintenanceTabViewModel.SmartRebuildAction)]
    [InlineData(MaintenanceTabViewModel.CatalogIdentityBackfillAction)]
    [InlineData(MaintenanceTabViewModel.ReferenceThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.FrameThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.PruneActivityAction)]
    [InlineData(MaintenanceTabViewModel.ResetDatabaseAction)]
    public async Task OnlyOneActionRunsAtATime(string running)
    {
        using var harness = new Harness { Gate = new ManualResetEventSlim(false) };
        harness.AsyncGate = new TaskCompletionSource();

        harness.Press(running);
        await WaitFor(() => harness.ViewModel.IsBusy, "the first action to start");

        foreach (var other in EveryToken.Where(token => token != running))
        {
            // Execute directly, so CanExecute is skipped: the refusal has to be in the body.
            harness.Press(other);
            Assert.Equal(0, harness.RanCount(other));
            Assert.Equal(MaintenanceTabViewModel.AnotherActionMessage, harness.Action(other).Summary);
            Assert.False(harness.Action(other).IsRunning);
        }

        // And every button is greyed while one runs.
        Assert.All(
            harness.ViewModel.Actions.SelectMany(action => action.Buttons),
            button => Assert.False(button.Command.CanExecute(null)));

        harness.Gate!.Set();
        harness.AsyncGate.TrySetResult();
        await WaitFor(() => !harness.ViewModel.IsBusy, "the first action to finish");
        Assert.Equal(1, harness.RanCount(running));
    }

    // ---- the gates ---------------------------------------------------------------------------

    [Fact]
    public void EveryAction_IsDisabledWhileAScanIsRunning()
    {
        using var harness = new Harness();
        Assert.All(
            harness.ViewModel.Actions.SelectMany(action => action.Buttons),
            button => Assert.True(button.Command.CanExecute(null)));

        harness.StartScan();

        Assert.True(harness.ViewModel.ScanRunning);
        Assert.All(
            harness.ViewModel.Actions.SelectMany(action => action.Buttons),
            button => Assert.False(button.Command.CanExecute(null)));
    }

    [Fact]
    public void EveryAction_IsDisabledWhileResolutionIsInProgress()
    {
        using var harness = new Harness();
        using var lease = harness.Coordinator.TryBeginResolution();
        Assert.NotNull(lease);

        Assert.True(harness.ViewModel.ScanRunning);
        Assert.All(
            harness.ViewModel.Actions.SelectMany(action => action.Buttons),
            button => Assert.False(button.Command.CanExecute(null)));
    }

    [Theory]
    [InlineData(MaintenanceTabViewModel.RebuildTargetsAction)]
    [InlineData(MaintenanceTabViewModel.RetryUnresolvedAction)]
    [InlineData(MaintenanceTabViewModel.SmartRebuildAction)]
    [InlineData(MaintenanceTabViewModel.CatalogIdentityBackfillAction)]
    [InlineData(MaintenanceTabViewModel.ReferenceThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.FrameThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.PruneActivityAction)]
    [InlineData(MaintenanceTabViewModel.ResetDatabaseAction)]
    public async Task EveryAction_RepeatsItsGuardInTheCommandBody(string token)
    {
        // TRACKING.md section 6 item 13: RelayCommand.Execute ignores CanExecute, so the gate has
        // to be in the body as well. Executed directly with the gate closed.
        using var harness = new Harness();
        harness.StartScan();

        await harness.PressAsync(token);

        Assert.Equal(0, harness.RanCount(token));
        Assert.Equal(MaintenanceTabViewModel.ScanInProgressMessage, harness.Action(token).Summary);
        Assert.Empty(harness.Events);
        Assert.False(harness.ViewModel.IsBusy);
    }

    // ---- off the UI thread -------------------------------------------------------------------

    [Theory]
    [InlineData(MaintenanceTabViewModel.RebuildTargetsAction)]
    [InlineData(MaintenanceTabViewModel.RetryUnresolvedAction)]
    [InlineData(MaintenanceTabViewModel.FrameThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.PruneActivityAction)]
    public async Task EveryAction_RunsOffTheUiThread(string token)
    {
        // Awaited, never blocked (TRACKING.md section 2 item 8, FIXER item 11).
        //
        // The four the tab wraps in Task.Run itself. The reference action's background thread
        // belongs to ScanCoordinator.RunReferenceThumbnailsAsync and is asserted in
        // ScanCoordinatorTests; the reset's belongs to ResetConfirmViewModel, which opens its
        // window on the UI thread and runs the delete on a Task.Run of its own, and is asserted in
        // ResetConfirmViewModelTests.
        using var harness = new Harness();
        var caller = Environment.CurrentManagedThreadId;

        await harness.PressAsync(token);

        Assert.Equal(1, harness.RanCount(token));
        Assert.All(harness.WorkThreads, thread => Assert.NotEqual(caller, thread));
    }

    // ---- spec 10.9 ---------------------------------------------------------------------------

    [Theory]
    [InlineData(MaintenanceTabViewModel.RebuildTargetsAction)]
    [InlineData(MaintenanceTabViewModel.RetryUnresolvedAction)]
    [InlineData(MaintenanceTabViewModel.SmartRebuildAction)]
    [InlineData(MaintenanceTabViewModel.CatalogIdentityBackfillAction)]
    [InlineData(MaintenanceTabViewModel.ReferenceThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.FrameThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.PruneActivityAction)]
    public async Task EveryAction_EmitsRebuildStartedAndRebuildComplete_WithSnakeCaseDetails(string token)
    {
        using var harness = new Harness();

        await harness.PressAsync(token);

        Assert.Equal(2, harness.Events.Count);
        Assert.Equal("rebuild_started", harness.Events[0].EventType);
        Assert.Equal("rebuild_complete", harness.Events[1].EventType);

        foreach (var emitted in harness.Events)
        {
            using var details = JsonDocument.Parse(JsonSerializer.Serialize(emitted.Details));
            Assert.Equal(
                new[] { "action", "affected" },
                details.RootElement.EnumerateObject().Select(property => property.Name).Order().ToList());
            Assert.Equal(token, details.RootElement.GetProperty("action").GetString());
        }
    }

    [Theory]
    [InlineData(MaintenanceTabViewModel.RebuildTargetsAction)]
    [InlineData(MaintenanceTabViewModel.RetryUnresolvedAction)]
    [InlineData(MaintenanceTabViewModel.SmartRebuildAction)]
    [InlineData(MaintenanceTabViewModel.CatalogIdentityBackfillAction)]
    [InlineData(MaintenanceTabViewModel.ReferenceThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.FrameThumbnailsAction)]
    [InlineData(MaintenanceTabViewModel.PruneActivityAction)]
    public async Task EveryAction_EmitsRebuildStartedBeforeTheWorkBegins(string token)
    {
        // Review finding I3: spec 10.9's vocabulary exists so a user watching the activity log
        // sees a long action begin. Emitted after the work, a rebuild over a large library writes
        // nothing until it finishes and then writes both rows at once.
        using var harness = new Harness();

        await harness.PressAsync(token);

        Assert.Equal(
            new[] { "event:rebuild_started", "work:" + token, "event:rebuild_complete" },
            harness.Trace);
    }

    [Fact]
    public async Task AnActionRefusedByItsOwnLease_StillClosesTheEventPair()
    {
        // The pair the started event opened is always closed, with affected 0 for a run that did
        // nothing, so the log never holds a started with no complete.
        using var harness = new Harness();
        using var refused = new MaintenanceTabViewModel(
            (_, _) => new TargetRebuild.RebuildOutcome(0, 0, 0, 0, 0, 0, TargetRebuild.RebuildStatus.ScanInProgress),
            (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
            (_, _) => new SmartRebuild.SmartRebuildOutcome(0, 0, 0, 0, 0, 0, 0),
            (_, _) => new CatalogIdentityBackfill.BackfillOutcome(0, 0, 0),
            (_, _, _) => Task.FromResult<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>(null),
            _ => 0,
            () => 30,
            _ => 0,
            emit: (eventType, message, details) => harness.Events.Add((eventType, message, details)),
            post: action => action());

        var card = refused.Action(MaintenanceTabViewModel.RebuildTargetsAction);
        card.Buttons[0].Command.Execute(null);
        await card.Buttons[0].Command.ExecutionTask!;
        card.Buttons[0].Command.Execute(null);
        await card.Buttons[0].Command.ExecutionTask!;

        Assert.Equal(2, harness.Events.Count);
        Assert.Equal("rebuild_started", harness.Events[0].EventType);
        Assert.Equal("rebuild_complete", harness.Events[1].EventType);

        using var details = JsonDocument.Parse(JsonSerializer.Serialize(harness.Events[1].Details));
        Assert.Equal(0, details.RootElement.GetProperty("affected").GetInt32());
        Assert.Equal(MaintenanceTabViewModel.ScanInProgressMessage, card.Summary);
    }

    [Fact]
    public async Task ResetDatabase_EmitsNothingFromTheTab_BecauseTheActionWritesItsOwnPair()
    {
        // DatabaseReset writes rebuild_started and rebuild_complete after the delete, because a
        // started event written before it would be one of the rows deleted and a complete event
        // written here would duplicate the one the action already wrote.
        using var harness = new Harness();

        await harness.PressAsync(MaintenanceTabViewModel.ResetDatabaseAction);

        Assert.Equal(1, harness.RanCount(MaintenanceTabViewModel.ResetDatabaseAction));
        Assert.Empty(harness.Events);
        Assert.Contains("Database reset", harness.Action(MaintenanceTabViewModel.ResetDatabaseAction).Summary!);
    }

    [Fact]
    public async Task ResetDatabase_Cancelled_RunsNothingAndSaysSo()
    {
        using var harness = new Harness { ResetSummary = null };

        await harness.PressAsync(MaintenanceTabViewModel.ResetDatabaseAction);

        Assert.Empty(harness.Events);
        Assert.Equal(
            MaintenanceTabViewModel.ResetCancelledMessage,
            harness.Action(MaintenanceTabViewModel.ResetDatabaseAction).Summary);
        Assert.False(harness.Action(MaintenanceTabViewModel.ResetDatabaseAction).Failed);
    }

    [Fact]
    public async Task ResetDatabase_RefusedByTheLease_ShowsTheDialogsLine_NotACancel()
    {
        // Review finding I4: the summary was discarded on every path but a completed reset, so a
        // user whose reset a running scan refused was told they had cancelled it.
        using var harness = new Harness { ResetSummary = ResetConfirmViewModel.ScanInProgressMessage };

        await harness.PressAsync(MaintenanceTabViewModel.ResetDatabaseAction);

        var card = harness.Action(MaintenanceTabViewModel.ResetDatabaseAction);
        Assert.Equal(ResetConfirmViewModel.ScanInProgressMessage, card.Summary);
        Assert.False(card.Failed);
    }

    [Fact]
    public async Task ResetDatabase_ThatFailed_ShowsTheFailureLine_AndColoursIt()
    {
        using var harness = new Harness
        {
            ResetSummary = ResetConfirmViewModel.FailureMessage,
            ResetFailed = true,
        };

        await harness.PressAsync(MaintenanceTabViewModel.ResetDatabaseAction);

        var card = harness.Action(MaintenanceTabViewModel.ResetDatabaseAction);
        Assert.Equal(ResetConfirmViewModel.FailureMessage, card.Summary);
        Assert.True(card.Failed);
    }

    // ---- the reference thumbnail card ---------------------------------------------------------

    [Fact]
    public async Task ReferenceThumbnails_All_PassesForceTrue()
    {
        // FIXER item 17: without it the pass re-offers every target, the cache serves the existing
        // file as a hit, and the action reports a generated count while producing no new pixels.
        using var harness = new Harness();

        await harness.PressAsync(MaintenanceTabViewModel.ReferenceThumbnailsAction, button: 1);

        Assert.Equal(new[] { true }, harness.ReferenceForces);
        Assert.Equal("Regenerate all", harness.Action(MaintenanceTabViewModel.ReferenceThumbnailsAction).Buttons[1].Label);
    }

    [Fact]
    public async Task ReferenceThumbnails_Missing_PassesForceFalse()
    {
        using var harness = new Harness();

        await harness.PressAsync(MaintenanceTabViewModel.ReferenceThumbnailsAction, button: 0);

        Assert.Equal(new[] { false }, harness.ReferenceForces);
        Assert.Equal("Missing only", harness.Action(MaintenanceTabViewModel.ReferenceThumbnailsAction).Buttons[0].Label);
    }

    [Fact]
    public void ReferenceThumbnails_GoThroughTheCoordinator_NotASecondPassInstance()
    {
        // By construction, both halves: the tab constructs no pass of its own, and the production
        // binding is ScanCoordinator.RunReferenceThumbnailsAsync (questions.md Q27).
        var root = FindRepoRoot();
        var tab = File.ReadAllText(Path.Combine(
            root, "src", "GalactiLog.App", "ViewModels", "Settings", "MaintenanceTabViewModel.cs"));
        Assert.DoesNotContain("new ReferenceThumbnailPass", tab, StringComparison.Ordinal);

        var appHost = File.ReadAllText(Path.Combine(root, "src", "GalactiLog.App", "AppHost.cs"));
        var registration = Regex.Match(appHost, @"new MaintenanceTabViewModel\([\s\S]*?\)\);");
        Assert.True(registration.Success, "no MaintenanceTabViewModel registration found in AppHost.cs");
        Assert.Contains("RunReferenceThumbnailsAsync", registration.Value, StringComparison.Ordinal);
        Assert.Contains("Purge(ThumbnailKind.Frame, cancellationToken)", registration.Value, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found.");
    }

    // ---- the frame thumbnail card --------------------------------------------------------------

    [Fact]
    public async Task FrameThumbnails_OffersOnlyPurgeAndRegenerate_AndSaysWhy()
    {
        using var harness = new Harness();
        var card = harness.Action(MaintenanceTabViewModel.FrameThumbnailsAction);

        var button = Assert.Single(card.Buttons);
        Assert.Equal("Purge and regenerate", button.Label);
        Assert.True(button.IsDestructive);

        // questions.md Q31: the card states plainly why there is no missing-only variant.
        Assert.Contains("no \"missing only\" option", card.Description, StringComparison.Ordinal);
        Assert.Contains("thumbnail path", card.Description, StringComparison.Ordinal);
        Assert.Contains("thumbnail cache", card.Description, StringComparison.Ordinal);

        await harness.PressAsync(MaintenanceTabViewModel.FrameThumbnailsAction);
        Assert.Contains("7 frame thumbnails", card.Summary!);

        // Review minor 5: the purge is handed the tab's own lifetime token, so closing the page
        // stops it part way through a large directory.
        var token = Assert.Single(harness.PurgeTokens);
        Assert.True(token.CanBeCanceled);
        harness.ViewModel.Dispose();
        Assert.True(token.IsCancellationRequested);
    }

    // ---- the retry card ------------------------------------------------------------------------

    [Fact]
    public async Task RetryUnresolved_CallsTheSameUnresolvedRetryAsTheTargetsTab()
    {
        using var harness = new Harness();

        await harness.PressAsync(MaintenanceTabViewModel.RetryUnresolvedAction);

        Assert.Equal(1, harness.RanCount(MaintenanceTabViewModel.RetryUnresolvedAction));
        var summary = harness.Action(MaintenanceTabViewModel.RetryUnresolvedAction).Summary!;
        Assert.Contains("1 resolved", summary, StringComparison.Ordinal);
        Assert.Contains("2 frames assigned", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryUnresolved_WhileTheTargetsTabRetryIsRunning_IsRefused()
    {
        // The Targets tab's retry holds ScanCoordinator's resolution lease for its whole run
        // (UnresolvedRetry takes it at the choke point), which is what closes this gate.
        using var harness = new Harness();
        using var targetsTabRetry = harness.Coordinator.TryBeginResolution();

        await harness.PressAsync(MaintenanceTabViewModel.RetryUnresolvedAction);

        Assert.Equal(0, harness.RanCount(MaintenanceTabViewModel.RetryUnresolvedAction));
        Assert.Equal(
            MaintenanceTabViewModel.ScanInProgressMessage,
            harness.Action(MaintenanceTabViewModel.RetryUnresolvedAction).Summary);
    }

    [Fact]
    public async Task RetryUnresolved_RefusedByItsOwnLease_ShowsTheScanMessage()
    {
        // The gate above is usability; this is the authority. A retry that reached UnresolvedRetry
        // and was refused there reports the same sentence.
        using var harness = new Harness();
        var refused = new MaintenanceTabViewModel(
            (_, _) => throw new InvalidOperationException(),
            (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false, UnresolvedRetry.RetryStatus.ScanInProgress),
            (_, _) => new SmartRebuild.SmartRebuildOutcome(0, 0, 0, 0, 0, 0, 0),
            (_, _) => new CatalogIdentityBackfill.BackfillOutcome(0, 0, 0),
            (_, _, _) => Task.FromResult<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>(null),
            _ => 0,
            () => 30,
            _ => 0,
            emit: (eventType, message, details) => harness.Events.Add((eventType, message, details)),
            post: action => action());

        var command = refused.Action(MaintenanceTabViewModel.RetryUnresolvedAction).Buttons[0].Command;
        command.Execute(null);
        await command.ExecutionTask!;

        Assert.Equal(
            MaintenanceTabViewModel.ScanInProgressMessage,
            refused.Action(MaintenanceTabViewModel.RetryUnresolvedAction).Summary);

        // A run that did nothing still closes the pair its started event opened, with affected 0.
        Assert.Equal(
            new[] { "rebuild_started", "rebuild_complete" },
            harness.Events.Select(emitted => emitted.EventType));
        refused.Dispose();
    }

    // ---- the prune card --------------------------------------------------------------------------

    [Fact]
    public async Task PruneActivity_UsesTheConfiguredRetention_AndReportsTheCount()
    {
        using var harness = new Harness { RetentionDays = 45, PruneResult = 12 };

        await harness.PressAsync(MaintenanceTabViewModel.PruneActivityAction);

        Assert.Equal(new[] { 45 }, harness.PrunedWith);
        var summary = harness.Action(MaintenanceTabViewModel.PruneActivityAction).Summary!;
        Assert.Contains("12 activity entries", summary, StringComparison.Ordinal);
        Assert.Contains("45 days", summary, StringComparison.Ordinal);
    }

    // ---- the rebuild card ------------------------------------------------------------------------

    [Fact]
    public async Task RebuildTargets_FirstPressArms_SecondPressRuns()
    {
        using var harness = new Harness();
        var card = harness.Action(MaintenanceTabViewModel.RebuildTargetsAction);
        Assert.True(card.RequiresConfirm);

        card.Buttons[0].Command.Execute(null);
        await card.Buttons[0].Command.ExecutionTask!;
        Assert.True(card.ConfirmPending);
        Assert.Equal(0, harness.RanCount(MaintenanceTabViewModel.RebuildTargetsAction));

        card.Buttons[0].Command.Execute(null);
        await card.Buttons[0].Command.ExecutionTask!;
        Assert.False(card.ConfirmPending);
        Assert.Equal(1, harness.RanCount(MaintenanceTabViewModel.RebuildTargetsAction));
        Assert.Contains("4 frames unassigned", card.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RebuildTargets_CancelClearsTheArmedConfirm()
    {
        using var harness = new Harness();
        var card = harness.Action(MaintenanceTabViewModel.RebuildTargetsAction);

        card.Buttons[0].Command.Execute(null);
        await card.Buttons[0].Command.ExecutionTask!;
        Assert.True(card.ConfirmPending);

        card.CancelConfirmCommand.Execute(null);

        Assert.False(card.ConfirmPending);
        Assert.Equal(0, harness.RanCount(MaintenanceTabViewModel.RebuildTargetsAction));
    }

    [Fact]
    public void RebuildTargets_TheCardSaysNoTargetIsDeleted()
    {
        using var harness = new Harness();
        var card = harness.Action(MaintenanceTabViewModel.RebuildTargetsAction);

        // TRACKING.md section 6 item 15, stated where the user can read it.
        Assert.Contains("No target is deleted", card.Description, StringComparison.Ordinal);
        Assert.Contains("offline", card.Description, StringComparison.Ordinal);
    }

    // ---- failure -----------------------------------------------------------------------------------

    [Fact]
    public async Task AnActionThatThrows_ShowsAFailureLine_AndReleasesTheRunningGate()
    {
        using var harness = new Harness { Throw = new InvalidOperationException("boom") };

        await harness.PressAsync(MaintenanceTabViewModel.PruneActivityAction);

        var card = harness.Action(MaintenanceTabViewModel.PruneActivityAction);
        Assert.Equal(MaintenanceTabViewModel.FailureMessage, card.Summary);
        Assert.True(card.Failed);
        Assert.False(card.IsRunning);
        Assert.False(harness.ViewModel.IsBusy);

        // And the next action still runs.
        harness.Throw = null;
        await harness.PressAsync(MaintenanceTabViewModel.FrameThumbnailsAction);
        Assert.Equal(1, harness.RanCount(MaintenanceTabViewModel.FrameThumbnailsAction));
    }

    [Fact]
    public async Task AProgressLine_ReachesTheRunningCard()
    {
        using var harness = new Harness();
        var card = harness.Action(MaintenanceTabViewModel.RetryUnresolvedAction);
        var seen = new List<string?>();
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaintenanceActionViewModel.Progress))
            {
                seen.Add(card.Progress);
            }
        };

        await harness.PressAsync(MaintenanceTabViewModel.RetryUnresolvedAction);

        Assert.Contains("Retrying 1/1 unresolved names...", seen);
        // Cleared when the run finishes.
        Assert.Null(card.Progress);
    }

    // ---- disposal ------------------------------------------------------------------------------------

    [Fact]
    public void Dispose_UnsubscribesFromScanStatusService()
    {
        using var harness = new Harness();
        var notified = 0;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaintenanceTabViewModel.ScanRunning))
            {
                notified++;
            }
        };

        harness.StartScan();
        Assert.Equal(1, notified);

        harness.ViewModel.Dispose();
        harness.Coordinator.RaiseProgress(ScanTaskNames.Ingest, 1, 2, "Ingesting...", force: true);

        Assert.Equal(1, notified);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var harness = new Harness();
        harness.ViewModel.Dispose();
        harness.ViewModel.Dispose();
    }

    [Fact]
    public async Task AfterDispose_AnActionDoesNothing()
    {
        using var harness = new Harness();
        harness.ViewModel.Dispose();

        await harness.PressAsync(MaintenanceTabViewModel.PruneActivityAction);

        Assert.Equal(0, harness.RanCount(MaintenanceTabViewModel.PruneActivityAction));
        Assert.Empty(harness.Events);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 600 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), $"timed out waiting for {what}");
    }

    // ---- spec 12's job registry (PAR-015, Phase 14B Task 2) -----------------------------------

    [Fact]
    public async Task EveryAction_OpensAJobWhenItStarts()
    {
        foreach (var token in EveryToken)
        {
            using var harness = new Harness();
            var isReset = token == MaintenanceTabViewModel.ResetDatabaseAction;
            if (isReset)
            {
                harness.AsyncGate = new TaskCompletionSource();
            }
            else
            {
                harness.Gate = new ManualResetEventSlim(false);
            }

            harness.Press(token);

            var running = Assert.Single(harness.Jobs.Running);
            Assert.Equal(token, running.Kind);
            Assert.Equal(harness.Action(token).Title, running.Title);
            Assert.Equal(1, harness.Jobs.RunningCount);
            Assert.Empty(harness.Jobs.Recent);

            if (isReset)
            {
                harness.AsyncGate!.SetResult();
            }
            else
            {
                harness.Gate!.Set();
            }

            await harness.Action(token).Buttons[0].Command.ExecutionTask!;
        }
    }

    [Fact]
    public async Task EveryAction_FinishesItsJobWithItsOwnSummary()
    {
        foreach (var token in EveryToken)
        {
            using var harness = new Harness();

            await harness.PressAsync(token);

            Assert.Empty(harness.Jobs.Running);
            Assert.Equal(0, harness.Jobs.RunningCount);
            var job = Assert.Single(harness.Jobs.Recent);
            Assert.Equal(token, job.Kind);
            Assert.True(job.IsFinished);
            Assert.Equal(JobResult.Succeeded, job.Result);
            Assert.Equal(harness.Action(token).Summary, job.Summary);
        }
    }

    [Fact]
    public async Task AFailedAction_FinishesItsJobAsFailed()
    {
        using var harness = new Harness();
        harness.Throw = new InvalidOperationException("boom");

        await harness.PressAsync(MaintenanceTabViewModel.RebuildTargetsAction);

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(MaintenanceTabViewModel.RebuildTargetsAction, job.Kind);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Equal(MaintenanceTabViewModel.FailureMessage, job.Summary);
        Assert.True(harness.Action(MaintenanceTabViewModel.RebuildTargetsAction).Failed);
    }

    [Fact]
    public async Task AnActionsJob_CarriesAPercentTheCardDoesNot()
    {
        using var harness = new Harness();

        // The harness's rebuild delegate reports step 1 of 1, which is 100 percent.
        await harness.PressAsync(MaintenanceTabViewModel.RebuildTargetsAction);

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(100d, job.Percent);
        Assert.True(job.HasPercent);
        Assert.Equal("Rebuilding 1/1 names...", job.Message);
    }

    // The case that catches the obvious implementation: pushing the percent into the card's own
    // Progress line passes every other case in this group. The card keeps the message and nothing
    // else, exactly as it did before the registry existed.
    //
    // Phase 14B fixer, fixer list item 20 (task2-review P3): the sibling above used to close with
    // a reflection assertion that MaintenanceActionViewModel declares no numeric property at all,
    // which would fail on any later numeric member for any unrelated reason. This case already
    // carries the real rule.
    [Fact]
    public async Task TheCard_StillShowsTheMessageOnly()
    {
        using var harness = new Harness();
        var card = harness.Action(MaintenanceTabViewModel.RebuildTargetsAction);
        var progressLines = new List<string?>();
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaintenanceActionViewModel.Progress))
            {
                progressLines.Add(card.Progress);
            }
        };

        await harness.PressAsync(MaintenanceTabViewModel.RebuildTargetsAction);

        Assert.Contains("Rebuilding 1/1 names...", progressLines);
        Assert.DoesNotContain(progressLines, line => line is not null && line.Contains('%'));
        Assert.DoesNotContain(progressLines, line => line is not null && line.Contains("100"));
    }

    [Fact]
    public async Task EveryActionsJobKind_IsItsOwnActionToken()
    {
        using var harness = new Harness();

        foreach (var token in EveryToken)
        {
            await harness.PressAsync(token);
        }

        // Newest first, so the recent list is the token list reversed.
        Assert.Equal(EveryToken.Reverse(), harness.Jobs.Recent.Select(job => job.Kind));
        Assert.All(
            harness.Jobs.Recent,
            job => Assert.Matches("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", job.Kind));
    }

    // Section 6.2: none of the actions on this tab has a user-facing cancel affordance, only the
    // tab-lifetime CancellationTokenSource, so every one of them registers cancel: null. That is
    // what the spec's "or null when it cannot be cancelled" is for.
    [Fact]
    public async Task EveryActionsJob_RegistersNoCancelDelegate()
    {
        using var harness = new Harness();

        foreach (var token in EveryToken)
        {
            await harness.PressAsync(token);
        }

        Assert.Equal(EveryToken.Length, harness.Jobs.Recent.Count);
        Assert.All(harness.Jobs.Recent, job =>
        {
            Assert.False(job.CanCancel);
            Assert.False(job.CancelCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task ATabBuiltWithNoRegistry_RunsEveryActionAndDoesNotThrow()
    {
        using var tab = new MaintenanceTabViewModel(
            (report, _) =>
            {
                report(1, 1, "Rebuilding 1/1 names...");
                return new TargetRebuild.RebuildOutcome(0, 0, 0, 0, 0, 0);
            },
            (report, _) =>
            {
                report(1, 1, "Retrying 1/1 unresolved names...");
                return new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false);
            },
            (report, _) =>
            {
                report(1, 6, "Redirecting frames from merged targets...");
                return new SmartRebuild.SmartRebuildOutcome(0, 0, 0, 0, 0, 0, 0);
            },
            (report, _) =>
            {
                report(1, 1, "Backfilling 1/1 names...");
                return new CatalogIdentityBackfill.BackfillOutcome(0, 0, 0);
            },
            (_, report, _) =>
            {
                report(1, 1, "Reference thumbnails 1/1");
                return Task.FromResult<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>(
                    new ReferenceThumbnailPass.ReferenceThumbnailOutcome(0, 0, 0, false));
            },
            _ => 0,
            () => 90,
            _ => 0,
            () => Task.FromResult<(string?, bool)>(("Database reset.", false)),
            post: action => action());

        foreach (var token in EveryToken)
        {
            var action = tab.Action(token);
            var command = action.Buttons[0].Command;

            if (action.RequiresConfirm && !action.ConfirmPending)
            {
                command.Execute(null);
                if (command.ExecutionTask is { } arm)
                {
                    await arm;
                }
            }

            command.Execute(null);
            if (command.ExecutionTask is { } run)
            {
                await run;
            }

            Assert.False(action.IsRunning);
            Assert.True(action.HasSummary);
        }
    }

    // ---- Phase 14B Task 4: smart rebuild and catalog identity backfill (PAR-007) --------------

    [Fact]
    public void TheTab_HasEightActionsInTheSpecifiedOrder()
    {
        using var harness = new Harness();
        var tokens = harness.ViewModel.Actions.Select(action => action.Token).ToList();

        Assert.Equal(8, tokens.Count);

        // Spec 12.7's tab list puts both new actions with the other rebuild-family actions: after
        // retry_unresolved and before reference_thumbnails. BuildActions' return array is the
        // rendering order and the tab has no other sort.
        Assert.Equal(
            [
                MaintenanceTabViewModel.RebuildTargetsAction,
                MaintenanceTabViewModel.RetryUnresolvedAction,
                MaintenanceTabViewModel.SmartRebuildAction,
                MaintenanceTabViewModel.CatalogIdentityBackfillAction,
                MaintenanceTabViewModel.ReferenceThumbnailsAction,
                MaintenanceTabViewModel.FrameThumbnailsAction,
                MaintenanceTabViewModel.PruneActivityAction,
                MaintenanceTabViewModel.ResetDatabaseAction,
            ],
            tokens);
    }

    [Fact]
    public async Task SmartRebuild_ReportsItsCountsInline()
    {
        using var harness = new Harness();

        await harness.PressAsync(MaintenanceTabViewModel.SmartRebuildAction);

        // The harness returns (3, 2, 4, 1, 1, 5, 2): one figure per spec 12.7 pass, plus what the
        // inline duplicate detection found.
        var card = harness.Action(MaintenanceTabViewModel.SmartRebuildAction);
        Assert.False(card.Failed);
        Assert.Contains("3 frames redirected", card.Summary);
        Assert.Contains("2 frames linked by alias", card.Summary);
        Assert.Contains("4 aliases added", card.Summary);
        Assert.Contains("1 identities re-derived", card.Summary);
        Assert.Contains("1 name rebuilt", card.Summary);
        Assert.Contains("5 stale merge suggestions removed", card.Summary);
        Assert.Contains("2 merge suggestions found", card.Summary);
    }

    [Fact]
    public async Task CatalogIdentityBackfill_ReportsItsCountsInline()
    {
        using var harness = new Harness();

        await harness.PressAsync(MaintenanceTabViewModel.CatalogIdentityBackfillAction);

        // The harness returns (2 linked names, 7 linked frames, 3 skipped names).
        var card = harness.Action(MaintenanceTabViewModel.CatalogIdentityBackfillAction);
        Assert.False(card.Failed);
        Assert.Contains("2 names", card.Summary);
        Assert.Contains("7 frames linked", card.Summary);
        Assert.Contains("3 left as they are", card.Summary);
    }

    [Fact]
    public async Task BothNewActions_RefuseWhileAScanIsRunning()
    {
        // The authority is each pass's own lease, not the tab's flag, so this drives the refusal
        // through the outcome record the pass returns when the lease is already held.
        using var harness = new Harness();
        using var refused = new MaintenanceTabViewModel(
            (_, _) => new TargetRebuild.RebuildOutcome(0, 0, 0, 0, 0, 0),
            (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
            (_, _) => new SmartRebuild.SmartRebuildOutcome(
                0, 0, 0, 0, 0, 0, 0, SmartRebuild.SmartRebuildStatus.ScanInProgress),
            (_, _) => new CatalogIdentityBackfill.BackfillOutcome(
                0, 0, 0, CatalogIdentityBackfill.BackfillStatus.ScanInProgress),
            (_, _, _) => Task.FromResult<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>(null),
            _ => 0,
            () => 30,
            _ => 0,
            emit: (eventType, message, details) => harness.Events.Add((eventType, message, details)),
            post: action => action());

        foreach (var token in new[]
                 {
                     MaintenanceTabViewModel.SmartRebuildAction,
                     MaintenanceTabViewModel.CatalogIdentityBackfillAction,
                 })
        {
            var command = refused.Action(token).Buttons[0].Command;
            command.Execute(null);
            await command.ExecutionTask!;

            var card = refused.Action(token);
            Assert.Equal(MaintenanceTabViewModel.ScanInProgressMessage, card.Summary);

            // A refusal is not a failure: it is the same informational sentence every other card
            // shows.
            Assert.False(card.Failed);
        }
    }

    [Fact]
    public async Task BothNewActions_RegisterWithTheJobRegistry()
    {
        // Spec 12.7's last sentence, and ruling D1. Nothing here registers anything of its own:
        // both actions inherit registration from the tab's one Begin site, which is the choke point
        // working (design-lessons rule 2). JobRegistryCensusTest's
        // TheDeclaredMaintenanceSet_IsEveryCardOnTheTab is the census half of the same rule.
        using var harness = new Harness();

        await harness.PressAsync(MaintenanceTabViewModel.SmartRebuildAction);
        await harness.PressAsync(MaintenanceTabViewModel.CatalogIdentityBackfillAction);

        var kinds = harness.Jobs.Recent.Select(job => job.Kind).ToList();
        Assert.Contains(MaintenanceTabViewModel.SmartRebuildAction, kinds);
        Assert.Contains(MaintenanceTabViewModel.CatalogIdentityBackfillAction, kinds);
        Assert.All(harness.Jobs.Recent, job => Assert.Equal(JobResult.Succeeded, job.Result));
    }

    [Theory]
    [InlineData(MaintenanceTabViewModel.SmartRebuildAction)]
    [InlineData(MaintenanceTabViewModel.CatalogIdentityBackfillAction)]
    public async Task BothNewActions_EmitTheRebuildStartedAndCompletePair(string token)
    {
        // The TargetRebuild precedent, not the UnresolvedRetry one: the tab emits the pair and the
        // Data class emits nothing of its own, so there is no double emission to suppress.
        using var harness = new Harness();

        await harness.PressAsync(token);

        Assert.Equal(
            new[] { "rebuild_started", "rebuild_complete" },
            harness.Events.Select(emitted => emitted.EventType));

        using var details = JsonDocument.Parse(JsonSerializer.Serialize(harness.Events[1].Details));
        Assert.Equal(token, details.RootElement.GetProperty("action").GetString());
        Assert.True(details.RootElement.TryGetProperty("affected", out _));
    }

    [Fact]
    public void NeitherNewAction_RequiresAConfirm()
    {
        // questions.md Q5, built to the proposed answer. MaintenanceTabView.axaml hard codes the
        // inline confirm callout's sentence to the rebuild's wording, so a second requiresConfirm
        // action would show a sentence that is wrong for it. Neither of these writes a destructive
        // change: smart rebuild creates and deletes no target and every frame it moves it moves to
        // a better owner, and the backfill only links frames that are unlinked.
        using var harness = new Harness();

        foreach (var token in new[]
                 {
                     MaintenanceTabViewModel.SmartRebuildAction,
                     MaintenanceTabViewModel.CatalogIdentityBackfillAction,
                 })
        {
            var card = harness.Action(token);
            Assert.False(card.RequiresConfirm);
            Assert.False(card.ConfirmPending);
            Assert.NotEqual(MaintenanceRisk.Destructive, card.Risk);
        }

        // Rebuild targets is still the only card that arms.
        Assert.True(harness.Action(MaintenanceTabViewModel.RebuildTargetsAction).RequiresConfirm);
    }

    [Theory]
    [InlineData(MaintenanceTabViewModel.SmartRebuildAction)]
    [InlineData(MaintenanceTabViewModel.CatalogIdentityBackfillAction)]
    public async Task AFailedNewAction_ShowsTheFailureLine(string token)
    {
        using var harness = new Harness { Throw = new InvalidOperationException("boom") };

        await harness.PressAsync(token);

        var card = harness.Action(token);
        Assert.Equal(MaintenanceTabViewModel.FailureMessage, card.Summary);
        Assert.True(card.Failed);
        Assert.False(card.IsRunning);
        Assert.Null(harness.ViewModel.RunningAction);

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(JobResult.Failed, job.Result);
    }
}
