using System.ComponentModel;
using System.Globalization;
using GalactiLog.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Maintenance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Spec 12.7's Maintenance tab: eight cards over the maintenance actions, of which two are the
/// only dangerous operations in the application. Rebuild targets, retry unresolved, smart rebuild,
/// catalog identity backfill, regenerate reference thumbnails (missing or all), purge and
/// regenerate frame thumbnails, prune activity events, and reset database behind a typed
/// confirmation.
/// </summary>
/// <remarks>
/// <para>
/// One card type, eight instances (design-lessons rule 1). The tab owns everything the cards share:
/// the one-action-at-a-time gate, the scan and resolution gate, the spec 10.9 activity events, and
/// the progress seam.
/// </para>
/// <para>
/// <b>Every collaborator arrives as a delegate</b>, so the tab builds in a unit test with no
/// database, no cache root and no window (design-spec 18.3). In particular nothing here
/// constructs a <c>ReferenceThumbnailPass</c>, composes a thumbnail cache path, or opens a window:
/// those three belong to <c>ScanCoordinator</c>, <c>ThumbnailCache.Purge</c> and <c>ModalHost</c>
/// respectively, and a second implementation of any of them is what the collision map's owner
/// table forbids.
/// </para>
/// <para>
/// <b>Three gates, in order of authority.</b> The resolution lease inside
/// <c>TargetRebuild</c>, <c>UnresolvedRetry</c>, <c>ScanCoordinator</c> and <c>DatabaseReset</c>
/// is the correctness gate and refuses a run outright. The scan gate here greys the buttons so the
/// user is not offered a pointless round trip. The one-at-a-time gate is usability, matching the
/// web's <c>MaintenanceSection</c>, and it is what keeps two progress lines from overwriting each
/// other. Each is repeated in the command body, because <c>RelayCommand.Execute</c> ignores
/// <c>CanExecute</c> (<c>TRACKING.md</c> section 6 item 13).
/// </para>
/// <para>
/// <b>Activity events.</b> Every action emits spec 10.9's <c>rebuild</c> / <c>rebuild_started</c>
/// on start and <c>rebuild</c> / <c>rebuild_complete</c> on finish, with snake_case details
/// carrying at least <c>action</c> and <c>affected</c>. The one exception is reset database:
/// <see cref="DatabaseReset"/> writes its own pair after the delete, because a
/// <c>rebuild_started</c> written here would be one of the rows the reset deletes and a
/// <c>rebuild_complete</c> written here would duplicate the one the action already wrote. That
/// exception is the reason <see cref="MaintenanceActionViewModel.EmitsItsOwnEvents"/> exists. The
/// started event is written before the work begins, never around it (review finding I3), and every
/// path closes the pair: a run refused by its own lease and a run that threw both write a complete
/// with <c>affected</c> zero.
/// </para>
/// </remarks>
public sealed partial class MaintenanceTabViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 10.9's <c>action</c> detail values, one fixed token per card. Constants so a
    /// test names them and so nothing spells one twice.</summary>
    public const string RebuildTargetsAction = "rebuild_targets";

    /// <inheritdoc cref="RebuildTargetsAction"/>
    public const string RetryUnresolvedAction = "retry_unresolved";

    /// <inheritdoc cref="RebuildTargetsAction"/>
    public const string SmartRebuildAction = "smart_rebuild";

    /// <inheritdoc cref="RebuildTargetsAction"/>
    public const string CatalogIdentityBackfillAction = "catalog_identity_backfill";

    /// <inheritdoc cref="RebuildTargetsAction"/>
    public const string ReferenceThumbnailsAction = "reference_thumbnails";

    /// <inheritdoc cref="RebuildTargetsAction"/>
    public const string FrameThumbnailsAction = "frame_thumbnails";

    /// <inheritdoc cref="RebuildTargetsAction"/>
    public const string PruneActivityAction = "prune_activity";

    /// <inheritdoc cref="RebuildTargetsAction"/>
    public const string ResetDatabaseAction = DatabaseReset.ResetActionToken;

    /// <summary>What a run refused by the scan or resolution gate reads, on every card. The same
    /// sentence <c>UnresolvedNamesViewModel</c> shows, with the action named generically because
    /// eight cards share it.</summary>
    internal const string ScanInProgressMessage =
        "A scan is running. This action is available when it finishes.";

    /// <summary>What a card shows while another card's action is running.</summary>
    internal const string AnotherActionMessage =
        "Another maintenance action is running. Wait for it to finish.";

    /// <summary>Spec 12.10's failure line: one neutral sentence, no exception text.</summary>
    internal const string FailureMessage =
        "The action could not be completed. See the log for details.";

    private readonly Func<Action<int, int, string>, CancellationToken, TargetRebuild.RebuildOutcome> _rebuildTargets;
    private readonly Func<Action<int, int, string>, CancellationToken, UnresolvedRetry.RetryOutcome> _retryUnresolved;
    private readonly Func<Action<int, int, string>, CancellationToken, SmartRebuild.SmartRebuildOutcome> _smartRebuild;
    private readonly Func<Action<int, int, string>, CancellationToken, CatalogIdentityBackfill.BackfillOutcome> _catalogIdentityBackfill;
    private readonly Func<bool, Action<int, int, string>, CancellationToken, Task<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>> _referenceThumbnails;
    private readonly Func<CancellationToken, int> _purgeFrameThumbnails;
    private readonly Func<int> _activityRetentionDays;
    private readonly Func<int, int> _pruneActivity;
    private readonly Func<Task<(string? Summary, bool Failed)>> _confirmAndResetDatabase;
    private readonly Action<string, string, object?> _emit;
    private readonly ScanStatusService? _scanStatus;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly JobRegistry? _jobs;

    // The handle for the action in flight, or null. One field rather than one per card, because
    // the tab runs one action at a time and RunningAction is the same singleton state.
    private JobHandle? _runningJob;

    // One tab-lifetime source every background action is linked to, the shape
    // DashboardViewModel established: disposing the page cancels a rebuild still walking the name
    // list.
    private readonly CancellationTokenSource _lifetime = new();

    private bool _disposed;

    /// <param name="rebuildTargets">Normally <c>TargetRebuild.Run</c>.</param>
    /// <param name="retryUnresolved">Normally <c>UnresolvedRetry.Run</c>, the same instance the
    /// Targets tab's retry button uses. There is one retry implementation and this is a second
    /// caller of it, never a second copy (its own class comment says so).</param>
    /// <param name="smartRebuild">Normally <c>SmartRebuild.Run</c>. Positional beside its family
    /// rather than trailing, because the delegates around it are positional and a new action's
    /// delegate reads best next to the other rebuild-family ones.</param>
    /// <param name="catalogIdentityBackfill">Normally <c>CatalogIdentityBackfill.Run</c>.</param>
    /// <param name="referenceThumbnails">Normally
    /// <c>ScanCoordinator.RunReferenceThumbnailsAsync</c>. The coordinator owns the render
    /// delegate and the lease, so the Maintenance trigger goes through it rather than constructing
    /// a second pass (<c>HANDOFF.md</c> section 5, <c>questions.md</c> Q27).</param>
    /// <param name="purgeFrameThumbnails">Normally
    /// <c>ThumbnailCache.Purge(ThumbnailKind.Frame, ct)</c>, returning how many files were deleted.
    /// That member is the only path composition for a purge and the only bulk delete, so no
    /// view-model ever builds a cache path (<c>TRACKING.md</c> section 6 item 21). The token is the
    /// tab's own lifetime, so closing the page stops a purge part way through a large directory
    /// (review minor 5).</param>
    /// <param name="activityRetentionDays">Normally
    /// <c>SettingsStore.GetGeneral().ActivityRetentionDays</c>, read at click time so a change on
    /// the Display tab takes effect with no restart.</param>
    /// <param name="pruneActivity">Normally <c>ActivityRepository.PruneRetention</c>, which is the
    /// whole prune action and already emits its own <c>activity_pruned</c> housekeeping
    /// event.</param>
    /// <param name="confirmAndResetDatabase">Normally <c>ResetConfirmDialogService.ShowAsync</c>.
    /// Opens the typed confirmation on <c>ModalHost</c> and completes with whatever the dialog
    /// produced: a summary and a failed flag for a reset that ran, that the lease refused, or that
    /// threw, and a null summary only for a genuine cancel (review finding I4). The reset itself
    /// runs inside that dialog, so the window cannot be dismissed mid-delete. Omitted, the reset
    /// card reports a cancel and does nothing.</param>
    /// <param name="emit">(eventType, message, details), normally
    /// <c>ActivityRepository.EmitStandalone</c> pinned to category <c>rebuild</c> and severity
    /// <c>info</c>. Spec 10.9's last paragraph: an event raised outside a scan is written by its
    /// own caller on a short-lived context.</param>
    /// <param name="scanStatus">The one App-layer scan subscriber. Greys every button while a scan
    /// runs or another pass holds the resolution lease; never <c>ScanCoordinator</c>
    /// directly.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed action is logged, never rethrown on the UI
    /// thread.</param>
    /// <param name="jobs">Spec 12's job registry (PAR-015, ruling D1). Every action on this tab is
    /// a registered job, so its progress and its outcome are in the status bar flyout as well as
    /// inline on its card. The registration lives here rather than on
    /// <c>MaintenanceActionViewModel</c> because the card holds no collaborator and performs no
    /// work: every command body is on this type. Trailing and optional, so no existing construction
    /// site moved and a tab built with no registry runs every action exactly as before.</param>
    public MaintenanceTabViewModel(
        Func<Action<int, int, string>, CancellationToken, TargetRebuild.RebuildOutcome> rebuildTargets,
        Func<Action<int, int, string>, CancellationToken, UnresolvedRetry.RetryOutcome> retryUnresolved,
        Func<Action<int, int, string>, CancellationToken, SmartRebuild.SmartRebuildOutcome> smartRebuild,
        Func<Action<int, int, string>, CancellationToken, CatalogIdentityBackfill.BackfillOutcome> catalogIdentityBackfill,
        Func<bool, Action<int, int, string>, CancellationToken, Task<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>> referenceThumbnails,
        Func<CancellationToken, int> purgeFrameThumbnails,
        Func<int> activityRetentionDays,
        Func<int, int> pruneActivity,
        Func<Task<(string? Summary, bool Failed)>>? confirmAndResetDatabase = null,
        Action<string, string, object?>? emit = null,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        JobRegistry? jobs = null)
    {
        ArgumentNullException.ThrowIfNull(rebuildTargets);
        ArgumentNullException.ThrowIfNull(retryUnresolved);
        ArgumentNullException.ThrowIfNull(smartRebuild);
        ArgumentNullException.ThrowIfNull(catalogIdentityBackfill);
        ArgumentNullException.ThrowIfNull(referenceThumbnails);
        ArgumentNullException.ThrowIfNull(purgeFrameThumbnails);
        ArgumentNullException.ThrowIfNull(activityRetentionDays);
        ArgumentNullException.ThrowIfNull(pruneActivity);

        _rebuildTargets = rebuildTargets;
        _retryUnresolved = retryUnresolved;
        _smartRebuild = smartRebuild;
        _catalogIdentityBackfill = catalogIdentityBackfill;
        _referenceThumbnails = referenceThumbnails;
        _purgeFrameThumbnails = purgeFrameThumbnails;
        _activityRetentionDays = activityRetentionDays;
        _pruneActivity = pruneActivity;
        _confirmAndResetDatabase = confirmAndResetDatabase ?? (() => Task.FromResult<(string?, bool)>((null, false)));
        _emit = emit ?? ((_, _, _) => { });
        _scanStatus = scanStatus;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _jobs = jobs;

        Actions = BuildActions();

        if (scanStatus is not null)
        {
            // ScanStatusService has already marshalled onto the UI thread; do not post again.
            scanStatus.PropertyChanged += OnScanStatusChanged;
        }
    }

    /// <summary>The eight cards, in spec 12.7's order.</summary>
    public IReadOnlyList<MaintenanceActionViewModel> Actions { get; }

    /// <summary>The card whose action is running, or null. One at a time, matching the web's
    /// <c>MaintenanceSection</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial MaintenanceActionViewModel? RunningAction { get; private set; }

    public bool IsBusy => RunningAction is not null;

    /// <summary>True while a scan runs or another pass holds the resolution lease (spec 5.1, 9.6).
    /// Every button greys on it. The refusal itself belongs to each action's own lease, so a race
    /// between this flag and a scan starting is refused rather than run.</summary>
    public bool ScanRunning =>
        _scanStatus?.IsRunning == true || _scanStatus?.ResolutionInProgress == true;

    /// <summary>A card by its spec 10.9 token, so a test and the view name one the same way.
    /// </summary>
    public MaintenanceActionViewModel Action(string token)
        => Actions.Single(action => action.Token == token);

    // ---- the eight cards -----------------------------------------------------------------

    private IReadOnlyList<MaintenanceActionViewModel> BuildActions()
    {
        var rebuild = new MaintenanceActionViewModel(
            RebuildTargetsAction,
            "Rebuild targets",
            "Clears every frame's target assignment except on targets you created yourself, then "
            + "re-resolves each distinct OBJECT name from the local catalogues and the lookup "
            + "cache. No catalogue is contacted over the network, so a rebuild works offline. No "
            + "target is deleted and no merge history is lost.",
            MaintenanceRisk.Moderate,
            requiresConfirm: true);
        rebuild.Buttons.Add(Button(rebuild, "Rebuild targets", destructive: false, RunRebuildAsync));

        var retry = new MaintenanceActionViewModel(
            RetryUnresolvedAction,
            "Retry unresolved names",
            "Clears every negative lookup cache row and re-runs resolution for each OBJECT name "
            + "that has no target yet, assigning the frames where it now succeeds. This is the "
            + "same action as the Retry button on the Targets tab; only one of them can run at a "
            + "time.",
            MaintenanceRisk.Safe);
        retry.Buttons.Add(Button(retry, "Retry unresolved", destructive: false, RunRetryAsync));

        // Spec 12.7's tab list puts both new actions with the other rebuild-family actions, so
        // they sit after retry_unresolved and before reference_thumbnails. The BuildActions return
        // array below is the rendering order and the tab has no other sort.
        //
        // Smart rebuild is Moderate, the same risk level as rebuild targets: both rewrite frame
        // assignments. The catalogue identity backfill is Safe: it links unlinked frames to targets
        // that already exist and writes no identity column at all. Neither is Destructive, because
        // neither deletes a targets row and neither touches a file. questions.md Q5: neither
        // requires a confirm either. Smart rebuild creates no target,
        // deletes no target, and every frame it moves it moves to a better owner; the backfill only
        // links frames that are unlinked. The inline confirm callout's sentence is hard coded to
        // the rebuild's wording in MaintenanceTabView.axaml, so a second requiresConfirm action
        // would show a sentence that is wrong for it.
        var smart = new MaintenanceActionViewModel(
            SmartRebuildAction,
            "Smart rebuild",
            "Repairs target data from the local database and the lookup cache in six passes, with "
            + "no network call at all: frames on a merged target follow the merge, unresolved "
            + "names matching a target alias are linked, names seen on a target's frames are added "
            + "to its aliases, identities and names are re-derived from cached catalogue answers, "
            + "and stale merge suggestions are removed. None of the six passes creates a target "
            + "or deletes one. Duplicate detection runs at the end, and it can create a target for "
            + "an unresolved name the offline catalogue answers for, which is the one row this "
            + "action can add.",
            MaintenanceRisk.Moderate);
        smart.Buttons.Add(Button(smart, "Smart rebuild", destructive: false, RunSmartRebuildAsync));

        var backfill = new MaintenanceActionViewModel(
            CatalogIdentityBackfillAction,
            "Catalog identity backfill",
            "Re-runs the identity matcher over frames that have no target, busiest name first, "
            + "resolving from the lookup cache only. A name whose identity an existing target "
            + "already carries has its frames linked to that target. A name that matches nothing "
            + "is left as it is, for duplicate detection and the Create target form on the Targets "
            + "tab. No target is created, and running it again is safe.",
            MaintenanceRisk.Safe);
        backfill.Buttons.Add(
            Button(backfill, "Backfill identities", destructive: false, RunCatalogIdentityBackfillAsync));

        var reference = new MaintenanceActionViewModel(
            ReferenceThumbnailsAction,
            "Reference thumbnails",
            "One representative image per target, rendered from that target's own frames. "
            + "\"Missing only\" fills the gaps and leaves existing images alone. \"All\" replaces "
            + "every existing reference image with a fresh render, which is what to use after "
            + "changing the stretch settings.",
            MaintenanceRisk.Moderate);
        reference.Buttons.Add(Button(reference, "Missing only", destructive: false, () => RunReferenceAsync(force: false)));
        reference.Buttons.Add(Button(reference, "Regenerate all", destructive: false, () => RunReferenceAsync(force: true)));

        var frames = new MaintenanceActionViewModel(
            FrameThumbnailsAction,
            "Frame thumbnails",
            "Deletes every cached frame thumbnail. They are generated again on demand as you "
            + "browse. There is no \"missing only\" option: GalactiLog never records a thumbnail "
            + "path on a frame row, so nothing tells it which thumbnails already exist. Only "
            + "files inside the thumbnail cache are deleted; no image, no database row and no "
            + "file of yours is touched.",
            MaintenanceRisk.Moderate);
        frames.Buttons.Add(Button(frames, "Purge and regenerate", destructive: true, RunFrameThumbnailsAsync));

        var prune = new MaintenanceActionViewModel(
            PruneActivityAction,
            "Prune activity log",
            "Removes activity entries older than the configured retention window. This runs "
            + "automatically at start-up and after every scan; the button is here for when you "
            + "have just shortened the window.",
            MaintenanceRisk.Safe);
        prune.Buttons.Add(Button(prune, "Prune now", destructive: false, RunPruneAsync));

        var reset = new MaintenanceActionViewModel(
            ResetDatabaseAction,
            "Reset database",
            "Deletes every catalogued frame, target, merge record, session note, scan record and "
            + "activity entry, then leaves an empty database at the current schema version. Your "
            + "settings and the shipped catalogues are kept, the database file is not deleted, "
            + "and no file on disk is removed. Merges cannot be undone afterwards. You will be "
            + "asked to type a confirmation phrase.",
            MaintenanceRisk.Destructive,
            emitsItsOwnEvents: true);
        reset.Buttons.Add(Button(reset, "Reset database", destructive: true, RunResetAsync));

        return [rebuild, retry, smart, backfill, reference, frames, prune, reset];
    }

    private MaintenanceButtonViewModel Button(
        MaintenanceActionViewModel action, string label, bool destructive, Func<Task> run)
        => new(label, destructive, run, () => CanRun(action));

    // Greys a button. Never the authority: every body below repeats this, and each action's own
    // lease is the authority after that.
    private bool CanRun(MaintenanceActionViewModel action)
        => !_disposed && !IsBusy && !ScanRunning && !action.IsRunning;

    // ---- the eight bodies ----------------------------------------------------------------

    private Task RunRebuildAsync()
    {
        var action = Action(RebuildTargetsAction);

        // The web's inline two-click confirm: the first press arms, the second runs. Checked
        // before the gate, so arming a card while a scan runs is not possible either.
        if (!_disposed && !IsBusy && !ScanRunning && !action.ConfirmPending)
        {
            action.ConfirmPending = true;
            return Task.CompletedTask;
        }

        action.ConfirmPending = false;
        return RunAsync(
            action,
            (report, ct) => Task.Run(
                () =>
                {
                    var outcome = _rebuildTargets(report, ct);
                    return new MaintenanceResult(
                        Describe(outcome),
                        outcome.FramesAssigned,
                        Ran: outcome.Status == TargetRebuild.RebuildStatus.Completed);
                },
                ct));
    }

    private Task RunRetryAsync()
        => RunAsync(
            Action(RetryUnresolvedAction),
            (report, ct) => Task.Run(
                () =>
                {
                    var outcome = _retryUnresolved(report, ct);
                    return new MaintenanceResult(
                        Describe(outcome),
                        outcome.FramesAssigned,
                        Ran: outcome.Status == UnresolvedRetry.RetryStatus.Completed);
                },
                ct));

    private Task RunSmartRebuildAsync()
        => RunAsync(
            Action(SmartRebuildAction),
            (report, ct) => Task.Run(
                () =>
                {
                    var outcome = _smartRebuild(report, ct);
                    return new MaintenanceResult(
                        Describe(outcome),
                        outcome.FramesRedirected + outcome.FramesLinkedByAlias,
                        Ran: outcome.Status == SmartRebuild.SmartRebuildStatus.Completed);
                },
                ct));

    private Task RunCatalogIdentityBackfillAsync()
        => RunAsync(
            Action(CatalogIdentityBackfillAction),
            (report, ct) => Task.Run(
                () =>
                {
                    var outcome = _catalogIdentityBackfill(report, ct);
                    return new MaintenanceResult(
                        Describe(outcome),
                        outcome.LinkedFrames,
                        Ran: outcome.Status == CatalogIdentityBackfill.BackfillStatus.Completed);
                },
                ct));

    // FIXER item 17 and TRACKING section 6 item 17: the "all" button passes force: true. Without
    // it the pass re-offers every target, the cache serves the existing file as a hit, and the
    // action reports a generated count while producing no new pixels.
    private Task RunReferenceAsync(bool force)
        => RunAsync(
            Action(ReferenceThumbnailsAction),
            async (report, ct) =>
            {
                // Already off the UI thread: RunReferenceThumbnailsAsync runs the pass on a
                // Task.Run of its own, under the coordinator's resolution lease.
                var outcome = await _referenceThumbnails(force, report, ct).ConfigureAwait(false);
                return new MaintenanceResult(
                    Describe(outcome, force), outcome?.Generated ?? 0, Ran: outcome is not null);
            });

    private Task RunFrameThumbnailsAsync()
        => RunAsync(
            Action(FrameThumbnailsAction),
            (report, ct) => Task.Run(
                () =>
                {
                    report(0, 0, "Deleting cached frame thumbnails...");
                    var deleted = _purgeFrameThumbnails(ct);
                    return new MaintenanceResult(
                        $"Purged {Plural(deleted, "frame thumbnail")}. They are generated again on "
                        + "demand as you browse.",
                        deleted);
                },
                ct));

    private Task RunPruneAsync()
        => RunAsync(
            Action(PruneActivityAction),
            (report, ct) => Task.Run(
                () =>
                {
                    var days = _activityRetentionDays();
                    report(0, 0, $"Pruning activity entries older than {days} days...");
                    var deleted = _pruneActivity(days);
                    return new MaintenanceResult(
                        $"Pruned {Plural(deleted, "activity entry", "activity entries")} older "
                        + $"than {days} days.",
                        deleted);
                },
                ct));

    // The dialog opens on the UI thread and runs the reset itself, so the window cannot be
    // dismissed mid-delete. DatabaseReset writes its own rebuild_started and rebuild_complete
    // after the delete, so this action emits none: see the class remarks.
    private Task RunResetAsync()
        => RunAsync(
            Action(ResetDatabaseAction),
            async (_, _) =>
            {
                // Review finding I4: the dialog's summary comes back whenever it produced one, so
                // a reset refused by the lease and a reset that threw both reach the card. Null is
                // the genuine cancel, and nothing else.
                var (summary, failed) = await _confirmAndResetDatabase().ConfigureAwait(true);
                return new MaintenanceResult(
                    summary ?? ResetCancelledMessage, 0, Ran: summary is not null, Failed: failed);
            });

    /// <summary>What the reset card shows when the user closed the confirmation without
    /// confirming. Not a failure, so it is not coloured as one.</summary>
    internal const string ResetCancelledMessage = "Reset cancelled.";

    // ---- the shared body -----------------------------------------------------------------

    /// <summary>What one action's body reports back.</summary>
    /// <param name="Summary">The card's summary line.</param>
    /// <param name="Affected">Spec 10.9's <c>affected</c> detail value for this action.</param>
    /// <param name="Ran">False when the action declined to do anything (a refused lease, a
    /// cancelled confirmation). The <c>rebuild_complete</c> for such a run carries
    /// <c>affected: 0</c>, so the pair the started event opened is always closed.</param>
    /// <param name="Failed">True when the summary is spec 12.10's failure line rather than an
    /// outcome, so the card colours it. A refused lease is not a failure: it is the same
    /// informational refusal every other card shows.</param>
    internal sealed record MaintenanceResult(
        string Summary, int Affected, bool Ran = true, bool Failed = false);

    private async Task RunAsync(
        MaintenanceActionViewModel action,
        Func<Action<int, int, string>, CancellationToken, Task<MaintenanceResult>> work)
    {
        // Every gate repeated in the body, because RelayCommand.Execute ignores CanExecute
        // (TRACKING.md section 6 item 13). A caller that skipped the predicate gets nothing.
        if (_disposed)
        {
            return;
        }

        if (IsBusy)
        {
            action.Finish(AnotherActionMessage);
            return;
        }

        if (ScanRunning)
        {
            action.Finish(ScanInProgressMessage);
            return;
        }

        RunningAction = action;
        action.Begin();
        NotifyButtons();

        // Spec 12, and spec 12.7's last sentence: every action on this tab registers. The kind is
        // the card's own spec 10.9 token and the title is the card's own title, so there is no
        // second vocabulary. cancel: null for all of them, which is what the spec's "or null when
        // it cannot be cancelled" is for: no action on this tab has a user-facing cancel
        // affordance, only the tab-lifetime CancellationTokenSource that disposing the page trips.
        _runningJob = _jobs?.Begin(action.Token, action.Title, cancel: null);

        // Spec 10.9, and review finding I3: BEFORE the work, not around it. The vocabulary exists
        // so a user watching the activity log sees a long action begin; emitted afterwards, a
        // rebuild over a large library writes nothing until it finishes and then writes both rows
        // with the same timestamp. Every path below closes the pair, including a run the action's
        // own lease refused (affected 0) and one that threw, so a started event is never left
        // open.
        //
        // The reset card emits neither here: DatabaseReset writes its own pair after the delete,
        // because anything written before it is one of the rows that delete removes.
        // On a background thread and awaited, so the row really is in the log before the work
        // begins without putting a SQLite write on the dispatcher. ConfigureAwait(true) because
        // the reset's own body opens a window and has to resume on the UI thread; every other
        // body is a Task.Run of its own and does not care.
        await EmitAsync("rebuild_started", action, $"{action.Title} started", 0).ConfigureAwait(true);

        try
        {
            var result = await work(Report, _lifetime.Token).ConfigureAwait(false);

            Emit(
                "rebuild_complete",
                action,
                $"{action.Title}: {result.Summary}",
                result.Ran ? result.Affected : 0);

            FinishJob(result.Failed ? JobResult.Failed : JobResult.Succeeded, result.Summary);
            _post(() => Complete(action, result.Summary, result.Failed));
        }
        catch (OperationCanceledException)
        {
            Emit("rebuild_complete", action, $"{action.Title} was cancelled", 0);
            FinishJob(JobResult.Cancelled, "The action was cancelled.");
            _post(() => Complete(action, "The action was cancelled.", failed: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {MaintenanceAction} maintenance action failed", action.Token);
            Emit("rebuild_complete", action, $"{action.Title} failed", 0);
            FinishJob(JobResult.Failed, FailureMessage);
            _post(() => Complete(action, FailureMessage, failed: true));
        }
    }

    // The registry's outcome, taken from what the card is already told: a summary with failed true
    // is JobResult.Failed, one with failed false is Succeeded, and an OperationCanceledException is
    // Cancelled. The handle is cleared here and nowhere else, so a body that threw still releases
    // it, exactly as Complete releases the running gate.
    private void FinishJob(JobResult result, string summary)
    {
        var job = _runningJob;
        _runningJob = null;
        job?.Finish(result, summary);
    }

    // The started event only. The complete events are already off the UI thread, because the work
    // they follow was awaited with ConfigureAwait(false).
    private Task EmitAsync(string eventType, MaintenanceActionViewModel action, string message, int affected)
        => action.EmitsItsOwnEvents
            ? Task.CompletedTask
            : Task.Run(() => Emit(eventType, action, message, affected));

    // The one emission point, so the "a card that writes its own pair gets none from here" rule is
    // in one place rather than at four call sites.
    //
    // Phase 14B fixer, fixer list item 15 (task2-review P3). Nothing this writes can throw out of
    // here. _emit is a SQLite write in AppHost, and RunAsync awaits it for rebuild_started ABOVE
    // the try that owns every FinishJob: a failed write unwound RunAsync with the job handle
    // already open and never disposed, so the job stayed Running for the life of the process and
    // the running gate never cleared. The same throw from either rebuild_complete call, both of
    // which sit inside catch arms, did the same. One guard at the one emission point rather than
    // one at each of the four call sites (design-lessons rule 2). A row the activity log could
    // not take is not a reason to abandon the action or to strand its job entry.
    private void Emit(string eventType, MaintenanceActionViewModel action, string message, int affected)
    {
        if (action.EmitsItsOwnEvents)
        {
            return;
        }

        try
        {
            _emit(eventType, message, Details(action, affected));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The {EventType} event for the {MaintenanceAction} maintenance action could not be written",
                eventType,
                action.Token);
        }
    }

    // snake_case, like every other details document in this solution (HANDOFF.md section 4 item
    // 9). Spec 10.9's two keys for the rebuild category are exactly action and affected.
    private static object Details(MaintenanceActionViewModel action, int affected)
        => new { action = action.Token, affected };

    // Called from the action's own thread. Marshalled through the post seam, like every other
    // background writer in this application.
    private void Report(int step, int total, string message)
    {
        // The card keeps the message and discards step and total, exactly as it always has: it has
        // no numeric progress and growing one is a UI change this did not budget for. The registry
        // is given the percent the spec gives it, from the same call. The handle posts on its own,
        // so this is deliberately outside the closure below rather than duplicated inside it.
        _runningJob?.Report(message, total > 0 ? 100.0 * step / total : null);

        _post(() =>
        {
            if (!_disposed && RunningAction is { } running)
            {
                running.ReportProgress(message);
            }
        });
    }

    // Runs on the UI thread through the post seam. The running gate is released here and nowhere
    // else, so a body that threw still frees it.
    private void Complete(MaintenanceActionViewModel action, string? summary, bool failed)
    {
        if (_disposed)
        {
            return;
        }

        action.Finish(summary, failed);
        RunningAction = null;
        NotifyButtons();
    }

    private void NotifyButtons()
    {
        foreach (var action in Actions)
        {
            action.NotifyButtons();
        }
    }

    private void OnScanStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(ScanStatusService.IsRunning)
            or nameof(ScanStatusService.ResolutionInProgress))
        {
            OnPropertyChanged(nameof(ScanRunning));
            NotifyButtons();
        }
    }

    // ---- summary lines -------------------------------------------------------------------

    private static string Describe(TargetRebuild.RebuildOutcome outcome)
    {
        if (outcome.Status == TargetRebuild.RebuildStatus.ScanInProgress)
        {
            return ScanInProgressMessage;
        }

        return $"Rebuilt from {Plural(outcome.NamesExamined, "name")}: "
            + $"{Plural(outcome.FramesUnassigned, "frame")} unassigned, "
            + $"{outcome.NamesResolved} resolved, "
            + $"{Plural(outcome.FramesAssigned, "frame")} reassigned, "
            + $"{outcome.NamesStillUnresolved} still unresolved, "
            + $"{Plural(outcome.CandidatesCleared, "merge suggestion")} cleared.";
    }

    private static string Describe(UnresolvedRetry.RetryOutcome outcome)
    {
        if (outcome.Status == UnresolvedRetry.RetryStatus.ScanInProgress)
        {
            return ScanInProgressMessage;
        }

        var sentence = $"Retried {Plural(outcome.NamesExamined, "name")}: {outcome.NamesResolved} "
            + $"resolved, {Plural(outcome.FramesAssigned, "frame")} assigned, "
            + $"{outcome.NamesStillUnresolved} still unresolved.";

        return outcome.StoppedOnNetworkFailure
            ? sentence + " A catalogue could not be reached, so the rest were left for the next run."
            : sentence;
    }

    private static string Describe(SmartRebuild.SmartRebuildOutcome outcome)
    {
        if (outcome.Status == SmartRebuild.SmartRebuildStatus.ScanInProgress)
        {
            return ScanInProgressMessage;
        }

        // One figure per pass, in spec 12.7's own order, so the card's line and the outcome record
        // carry the same six counts.
        return $"Repaired in six passes: {Plural(outcome.FramesRedirected, "frame")} redirected, "
            + $"{Plural(outcome.FramesLinkedByAlias, "frame")} linked by alias, "
            + $"{Plural(outcome.AliasesAdded, "alias", "aliases")} added, "
            + $"{outcome.IdentitiesReDerived} identities re-derived, "
            + $"{Plural(outcome.NamesRebuilt, "name")} rebuilt, "
            + $"{Plural(outcome.StaleCandidatesRemoved, "stale merge suggestion")} removed, "
            + $"{Plural(outcome.DuplicateCandidatesWritten, "merge suggestion")} found.";
    }

    private static string Describe(CatalogIdentityBackfill.BackfillOutcome outcome)
    {
        if (outcome.Status == CatalogIdentityBackfill.BackfillStatus.ScanInProgress)
        {
            return ScanInProgressMessage;
        }

        return $"Backfilled {Plural(outcome.LinkedNames, "name")}: "
            + $"{Plural(outcome.LinkedFrames, "frame")} linked, "
            + $"{outcome.SkippedNames} left as they are.";
    }

    private static string Describe(
        ReferenceThumbnailPass.ReferenceThumbnailOutcome? outcome, bool force)
    {
        if (outcome is null)
        {
            return ScanInProgressMessage;
        }

        if (outcome.Total == 0)
        {
            return force
                ? "No targets have frames to render a reference image from."
                : "Every target already has a reference image.";
        }

        var sentence = $"{(force ? "Regenerated" : "Filled")} "
            + $"{Plural(outcome.Generated, "reference image")} of {outcome.Total} "
            + $"target{(outcome.Total == 1 ? "" : "s")}, {outcome.Failed} failed.";

        return outcome.Cancelled ? sentence + " The run was cancelled part way through." : sentence;
    }

    private static string Plural(int count, string noun) => Plural(count, noun, noun + "s");

    private static string Plural(int count, string singular, string plural)
        => count == 1
            ? "1 " + singular
            : count.ToString("N0", CultureInfo.InvariantCulture) + " " + plural;

    /// <summary>Cancels the lifetime source every background action is linked to, which also
    /// cancels a rebuild still walking the name list, and drops the scan subscription. Closing the
    /// application does not leave a maintenance loop running.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        if (_scanStatus is not null)
        {
            _scanStatus.PropertyChanged -= OnScanStatusChanged;
        }

        _lifetime.Dispose();
    }
}
