using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using GalactiLog.Core.Text;
using GalactiLog.Data.Queries;
using Queries = GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// Spec 12.4's Target detail page: the header block, the totals row, the four actions, the
/// collapsible target notes box, and the two regions Tasks 4 and 8 fill.
/// </summary>
/// <remarks>
/// Every collaborator arrives as a delegate rather than as a query or a repository object, the
/// same rule <c>DashboardViewModel</c> follows, so the page constructs in a unit test with lambdas
/// and no database (design-spec 18.3). The page is constructed on the UI thread from a dashboard
/// row click, so the load runs on a background thread and publishes through the <c>post</c> seam.
/// </remarks>
public sealed partial class TargetDetailViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, Queries.TargetDetail?> _get;
    private readonly Func<TargetHeaderBlock, SessionOverview, MergedCardSpec?, SessionCardViewModel> _createCard;
    private readonly Func<Guid, string, RenameOutcome> _rename;
    private readonly Func<Guid, string, CancellationToken, Task<(bool Changed, string Message)>> _reResolve;
    private readonly Func<Guid, Task<bool>>? _openMerge;
    private readonly Func<string, (Guid TargetId, string PrimaryName, string LoserName)?>? _mergedInto;
    private readonly ShellIntegration _shell;
    private readonly ChartSelectionViewModel _selection;
    private readonly ScanStatusService? _scanStatus;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly Func<string, ThumbnailSlotViewModel>? _createReferenceSlot;

    // Spec 12.4's Copy Frame List dialog, normally a lambda over FrameListDialogService.ShowAsync.
    // Null leaves the command enabled by the spec's own rule and doing nothing when it runs,
    // which is what lets a test construct the page with no modal host.
    private readonly Func<string, IReadOnlyList<FrameListNight>, Task>? _openFrameList;

    // Spec 12.13's Export for stacking, normally a lambda over WbppExportDialogService.ShowAsync.
    // Null leaves the command enabled by the spec's own rule and doing nothing when it runs, which
    // is what lets a test construct the page with no modal host.
    private readonly Func<string, string, IReadOnlyList<DateOnly>, Task>? _openWbppExport;

    // Spec 12.4's object type edit (PAR-009). Held only to hand to each TargetHeaderViewModel this
    // page builds: the pencil, the combo box and the write all live on the header, which is the
    // projection the Details panel binds.
    private readonly Action<Guid, string>? _setObjectType;

    // Spec 12.4's PAR-018 deep link. Nulled by the first ReplaceSessions that reads it, which is
    // the "consumed once" rule: a scan-driven reload after the user has moved off that night must
    // keep the night the user is on.
    private DateOnly? _initialSessionDate;

    // One page-lifetime source every background window is linked to, the shape
    // DashboardViewModel established (F6): closing the page cancels a load that is still parked.
    private readonly CancellationTokenSource _lifetime = new();

    // Only the newest load may write to the bindings. The token alone is not enough: a load that
    // already finished can still be sitting in the dispatcher queue when a newer one is
    // requested, and posting it then would show a stale page.
    private int _generation;

    private Queries.TargetDetail? _detail;
    private bool _disposed;

    // With an inline post a publish runs on the load's pool thread, so Dispose can land while it is
    // inside ReplaceSessions; whichever of the two finishes last disposes the chart, the cards and the reference thumbnail.
    private readonly object _publishGate = new();
    private int _publishing;
    private bool _disposeAfterPublish;

    // Phase 15B fixer F2, with items 30 and 38. The unsubscribe half of the derived-data follow,
    // null when the page was built without one. The notifier is a process singleton and this page
    // is transient, so the half that matters is this one: the shell disposes the outgoing page on
    // every navigation, and a handler left behind would keep reloading a page nobody can see.
    private readonly Action? _unfollowDerivedData;

    // The same half of the settings follow, for the same reason: SettingsStore is a process
    // singleton and this page is transient.
    private readonly Action? _unfollowGeneralChanged;

    // The storm rule. GeneralChanged fires on EVERY general save, including each committed
    // keystroke in the PHD2 profiles panel, so a notification arriving while a load is in flight
    // is remembered rather than started. A bool and not a count: a burst costs one further load.
    private bool _reloadPending;

    // The clock and observer settings the cards were built with (polish 2 ruling 2). Null until
    // the first publish, and on a page built without getGeneral, which never reloads for a save.
    private (string Timezone, bool Use24Hour, double? Latitude, double? Longitude)? _timeFormat;

    // Set for the next ReplaceSessions, which then carries no card: a card resolves its zone
    // once, at construction, so a zone change needs new cards rather than refreshed ones.
    private bool _rebuildCards;

    // ---- Spec 12.15's custom columns ---------------------------------------------------------

    // All three are null on a page built without them, which is every construction site outside
    // AppHost and every test that is not about the cells. A page with no reader draws no cell and
    // therefore measures exactly as it did before this phase.
    private readonly Func<IReadOnlyList<CustomColumnDefinition>>? _loadCustomColumns;
    private readonly Func<Guid, IReadOnlyList<CustomValueRow>>? _loadValuesForTarget;
    private readonly Func<Guid, CustomValueKey, string?, CustomWriteResult>? _writeCustomValue;

    // ---- Phase 21's two sends and the AstroBin CSV (spec 12.16) ------------------------------

    private readonly NinaClient? _ninaClient;
    private readonly StellariumClient? _stellariumClient;
    private readonly JobRegistry? _jobs;

    // Spec 12.4, normally SurveyViewModalService.ShowAsync.
    private readonly Func<SurveyTarget, Task>? _openSurveyView;

    // Spec 12.4 and 12.17 (Phase 18, ruling R12): the Create mosaic dialog over the checked nights.
    private readonly Func<Guid, string, IReadOnlyList<DateOnly>, Task>? _openCreateMosaic;

    // (severity, eventType, message, details, targetId), normally ActivityRepository.EmitStandalone
    // pinned to category "user_action" (spec 5.12 amendment 2b). A delegate rather than the
    // repository itself, the rule every collaborator here follows.
    private readonly Action<string, string, string, object?, Guid?>? _emitActivity;

    // Normally SettingsStore.GetGeneral, read fresh on every menu rebuild rather than cached
    // (core-shapes.md section 3 item 7): a save on the External Tools tab reaches this page's menu
    // with no navigation.
    private readonly Func<GeneralSettings>? _getGeneral;

    // Normally AliasMapCache.Current, for the AstroBin CSV's filter id lookup (AstroBinCsv.Build).
    private readonly Func<AliasMap>? _getAliasMap;

    // The same delegate SessionCardViewModel takes (SessionCardViewModel.cs:49), for the AstroBin
    // CSV's checked-but-unopened nights (task5-target-page.md section 3).
    private readonly Func<string, DateOnly, SessionDetail?>? _getSessionDetail;

    // The process-wide display.columns writer, the same instance the frame table and the dashboard
    // hold: `ledger` is a third table id on that one writer, never a settings key of its own.
    // Held for its Changed
    // event, so a toggle on the Settings Display tab reaches an open page with no navigation.
    private readonly DisplayColumnWriter? _displayColumns;

    // Every definition, and every session-scope and rig-scope value of this target, read once per
    // load on the background path and published onto the cards before they are shown. The session
    // pane's rig rows read the same two fields off the card, which is why the page reads once.
    private IReadOnlyList<CustomColumnDefinition> _customColumns = [];
    private IReadOnlyDictionary<(Guid ColumnId, CustomValueKey Key), string> _customValues =
        new Dictionary<(Guid, CustomValueKey), string>();

    // display.columns.ledger_hidden, the custom slugs switched OFF, empty on a fresh profile:
    // custom columns ship on. Seeded through the writer's last queued list first and the display
    // document second, the rule ColumnPickerViewModel.ForFrames states and for the reason recorded
    // there: the document is a snapshot and is stale the moment any picker toggles a column.
    private IReadOnlyList<string> _ledgerColumnKeys = [];

    /// <param name="groupKey">The dashboard row's <c>GroupKey</c>.</param>
    /// <param name="get">Normally <c>TargetDetailQuery.Get</c>. Returns null for a stale key
    /// (ruling Q4).</param>
    /// <param name="createCard">Builds one session card (Task 4). A factory delegate so this
    /// view-model does not have to know the session query, the notes writer or the chart. It
    /// takes the loaded header as well as the overview because the card's notes writer is keyed
    /// on <c>TargetHeaderBlock.TargetId</c>, which is null for an <c>obj:</c> group, and its
    /// expansion query is keyed on <c>TargetHeaderBlock.GroupKey</c>, the storage form.</param>
    /// <param name="rename">Normally <c>TargetWriteRepository.Rename</c>. Returns the outcome so
    /// a name collision is a message rather than an exception on the UI thread.</param>
    /// <param name="saveNotes">Normally <c>TargetWriteRepository.SaveTargetNotes</c>.</param>
    /// <param name="reResolve">Normally a lambda over the negative-cache clear (ruling Q12),
    /// <c>TargetResolver.ResolveIdentity</c> and <c>TargetEnrichmentRepository.ReEnrich</c>
    /// (FIXER LIST item 13). Takes the target's id, because re-enrichment writes to that row, and
    /// its primary name, because that is what is re-resolved. Returns whether anything changed,
    /// which decides whether the page reloads, and a short outcome sentence for the status
    /// line.</param>
    /// <param name="shell">The three allowed file actions.</param>
    /// <param name="selection">The shared chart metric and filter selection (Task 8). The page
    /// owns <see cref="TargetChart"/>, which it builds over its own card collection, so the chart
    /// arrives as the selection it needs rather than as a factory for a single product.</param>
    /// <param name="openMerge">Spec 12.4's "merge into another target" action (Phase 7 Task 5).
    /// Normally a lambda over <c>MergeDialogService.ShowAsync</c> with a <c>MergeRequest</c> whose
    /// winner is this target and whose loser is not chosen yet, so the dialog's own search box
    /// picks it. Completes with true when a merge happened. Null leaves the action disabled,
    /// which is what lets a test construct the page with no dialog host.</param>
    /// <param name="mergedInto">Normally <c>TargetDetailQuery.MergedInto</c>. Called only on the
    /// <see cref="IsMissing"/> path, so a merged-away key names its winner instead of reading as
    /// a generic stale key (FIXER LIST item 14, ruling Q12). <see cref="Get"/> still returns null
    /// for such a key: spec 12.10's "the merge is never hidden from the user" is unchanged, and
    /// there is no silent redirect.</param>
    /// <param name="createMergeHistory">Builds spec 12.9's merge history for this target (Phase 7
    /// Task 5). Keyed on the target id, which for a resolved page is the group key itself, so the
    /// list exists before the first load rather than being rebuilt by every reload. Null for an
    /// <c>obj:</c> group, which has no target to have absorbed anything.</param>
    /// <param name="scanStatus">Refreshes the page after a scan. Subscribed here, never to
    /// <c>ScanCoordinator</c> (ruling Q7).</param>
    /// <param name="delay">The autosave debounce seam, passed straight through to
    /// <see cref="Notes"/>.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load, rename or re-resolve is logged, never
    /// rethrown on the UI thread.</param>
    /// <param name="createReferenceSlot">Builds the header's reference thumbnail slot from the
    /// stored cache-relative path (Phase 8 Task 6). Optional and trailing, so no existing
    /// construction site changes; null leaves the header's slot null and the page shows
    /// spec 12.10's placeholder.</param>
    /// <param name="targetPage">The process-wide <c>display.target_page</c> holder, exposed as
    /// <see cref="TargetPage"/> for the parts. Null builds a private
    /// unpersisted one.</param>
    /// <param name="openFrameList">Spec 12.4's Copy Frame List dialog (PAR-006), normally a lambda
    /// over <c>FrameListDialogService.ShowAsync</c>. Takes the group key and the checked nights in
    /// the ledger's own order, each carrying the detail this page has already loaded. Optional and
    /// trailing, so no existing positional construction site changes; null makes the command a
    /// no-op, which is what lets a test construct the page with no modal host.</param>
    /// <param name="setObjectType">Spec 12.4's object type edit (PAR-009), normally
    /// <c>TargetWriteRepository.SetObjectType</c> with its outcome discarded. It rides down to
    /// <see cref="TargetHeaderViewModel"/>, which is where the pencil and the combo box live, so
    /// the page only passes it on. Optional and trailing; null makes the pencil inert, which is
    /// what every test that is not about the edit wants.</param>
    /// <param name="initialSessionDate">Spec 12.4's PAR-018 deep link: the night to open on.
    /// Consumed once, by the first <c>ReplaceSessions</c> after the first load, and cleared there,
    /// so a later scan-driven reload keeps the night the user has moved to rather than jumping
    /// back. Null selects the newest night, which is every route that exists today and is
    /// unchanged behaviour; a date the target has no night for falls back to the newest one and
    /// shows no error. Optional and trailing, so no existing positional construction site
    /// changes.</param>
    /// <param name="openWbppExport">Spec 12.13's Export for stacking page, normally a lambda over
    /// <c>WbppExportDialogService.ShowAsync</c>. Takes the group key, the target's primary name and
    /// the checked nights in the ledger's own order. Optional and trailing, so no existing
    /// positional construction site changes; null makes the command a no-op, which is what lets a
    /// test construct the page with no modal host.</param>
    /// <param name="display">Spec 12.15: the display document as the host read it, by value
    /// (ruling Q13), for the second half of the ledger column list's seeding rule. Optional and
    /// trailing; null is a fresh profile, whose <c>columns.ledger</c> is empty, so
    /// the page draws no custom cell at all.</param>
    /// <param name="displayColumns">The process-wide <c>display.columns</c> writer, the one this
    /// page's frame tables and the dashboard already share. Held for
    /// <c>DisplayColumnWriter.Changed</c>, so a toggle on the Settings Display tab's Nights ledger
    /// picker reaches an open page without a navigation, and for <c>LastWritten</c>, which
    /// outranks <paramref name="display"/>. Optional and trailing; null leaves the column list at
    /// the value the constructor computed, which is what a test that is not about the picker
    /// wants.</param>
    /// <param name="loadCustomColumns">Normally <c>CustomColumnRepository.List</c>. Read once per
    /// page load, on the existing background load path. Null draws no custom cell.</param>
    /// <param name="loadValuesForTarget">Normally
    /// <c>CustomColumnRepository.ValuesForTarget</c>. Read once per page load, beside
    /// <paramref name="loadCustomColumns"/>: it answers every session-scope AND rig-scope value of
    /// the target, and the ledger rows and the session pane's rig rows partition that one result, so the
    /// page reads once whatever is on screen.</param>
    /// <param name="writeCustomValue">Normally <c>CustomColumnRepository.SetValue</c>, handed to
    /// every cell this page builds. Invoked on a thread-pool thread by the cell itself. Null
    /// leaves the page drawing no cell at all rather than drawing one that silently writes
    /// nothing.</param>
    /// <param name="ninaClient">Spec 12.16's NINA client, the one <c>AppHost</c> registers.
    /// Optional and trailing; null leaves every "Send to NINA" item failing with
    /// <see cref="IntegrationMessages.NinaFailed"/>, which is what lets a test build the page with
    /// no client.</param>
    /// <param name="stellariumClient">Spec 12.16's Stellarium client. Same rule as
    /// <paramref name="ninaClient"/>.</param>
    /// <param name="jobs">Spec 12's job registry (PAR-015). Each send is a registered job with no
    /// cancel delegate (spec 12.16, "The result line"); the AstroBin CSV registers none. Optional
    /// and trailing; null runs every send exactly as before with no status bar entry, which is
    /// what lets a test build the page with no registry.</param>
    /// <param name="emitActivity">(severity, eventType, message, details, targetId), normally
    /// <c>ActivityRepository.EmitStandalone</c> pinned to category <c>user_action</c> (spec 5.12
    /// amendment 2b). Optional and trailing; null writes no durable record, which is what lets a
    /// test build the page with no database.</param>
    /// <param name="getGeneral">Normally <c>SettingsStore.GetGeneral</c>, read fresh every time the
    /// menu is rebuilt rather than cached, so a save on the External Tools tab reaches an open page
    /// without a navigation. Null offers no instance at all.</param>
    /// <param name="getAliasMap">Normally <c>AliasMapCache.Current</c>, for the AstroBin CSV's
    /// filter id lookup. Null falls back to an empty map, which looks every filter id up by its
    /// stored name alone.</param>
    /// <param name="getSessionDetail">The same delegate <see cref="SessionCardViewModel"/> takes
    /// (task5-target-page.md section 3), called on a pool thread for a checked night whose
    /// <see cref="SessionCardViewModel.Detail"/> is still null when the AstroBin CSV runs. A call
    /// that returns null or throws counts that night as failed. Null counts every unopened night as
    /// failed.</param>
    /// <param name="openSurveyView">Normally <c>SurveyViewModalService.ShowAsync</c>; null leaves
    /// <see cref="OpenSurveyViewCommand"/> unable to execute.</param>
    /// <param name="openCreateMosaic">Spec 12.17's Create mosaic dialog (Phase 18, ruling R12):
    /// the target id, its primary name and the checked nights in the ledger's own order. Normally
    /// a lambda that reads the dialog's rows and opens it on <c>ModalHost</c>. Optional and
    /// trailing; null makes the command a no-op.</param>
    public TargetDetailViewModel(
        string groupKey,
        Func<string, Queries.TargetDetail?> get,
        Func<TargetHeaderBlock, SessionOverview, MergedCardSpec?, SessionCardViewModel> createCard,
        Func<Guid, string, RenameOutcome> rename,
        Action<Guid, string?> saveNotes,
        Func<Guid, string, CancellationToken, Task<(bool Changed, string Message)>> reResolve,
        ShellIntegration shell,
        ChartSelectionViewModel selection,
        Func<Guid, Task<bool>>? openMerge = null,
        Func<string, (Guid TargetId, string PrimaryName, string LoserName)?>? mergedInto = null,
        Func<Guid, MergeHistoryViewModel>? createMergeHistory = null,
        ScanStatusService? scanStatus = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<string, ThumbnailSlotViewModel>? createReferenceSlot = null,
        TargetPageState? targetPage = null,
        Func<string, IReadOnlyList<FrameListNight>, Task>? openFrameList = null,
        Action<Guid, string>? setObjectType = null,
        DateOnly? initialSessionDate = null,
        Action<EventHandler>? subscribeDerivedDataChanged = null,
        Action<EventHandler>? unsubscribeDerivedDataChanged = null,
        Func<string, string, IReadOnlyList<DateOnly>, Task>? openWbppExport = null,
        DisplaySettings? display = null,
        DisplayColumnWriter? displayColumns = null,
        Func<IReadOnlyList<CustomColumnDefinition>>? loadCustomColumns = null,
        Func<Guid, IReadOnlyList<CustomValueRow>>? loadValuesForTarget = null,
        Func<Guid, CustomValueKey, string?, CustomWriteResult>? writeCustomValue = null,
        NinaClient? ninaClient = null,
        StellariumClient? stellariumClient = null,
        JobRegistry? jobs = null,
        Action<string, string, string, object?, Guid?>? emitActivity = null,
        Func<GeneralSettings>? getGeneral = null,
        Func<AliasMap>? getAliasMap = null,
        Func<string, DateOnly, SessionDetail?>? getSessionDetail = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null,
        Func<SurveyTarget, Task>? openSurveyView = null,
        Func<Guid, string, IReadOnlyList<DateOnly>, Task>? openCreateMosaic = null)
    {
        GroupKey = groupKey;
        _setObjectType = setObjectType;
        _initialSessionDate = initialSessionDate;
        _get = get;
        _createCard = createCard;
        _rename = rename;
        _reResolve = reResolve;
        _openMerge = openMerge;
        _mergedInto = mergedInto;
        _shell = shell;
        _selection = selection;
        TargetPage = targetPage ?? new TargetPageState();
        _scanStatus = scanStatus;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _createReferenceSlot = createReferenceSlot;
        _openFrameList = openFrameList;
        _openWbppExport = openWbppExport;
        RenameText = "";

        // Spec 12.15. Assigned before Load, which reads the first two on its own background pass.
        _loadCustomColumns = loadCustomColumns;
        _loadValuesForTarget = loadValuesForTarget;
        _writeCustomValue = writeCustomValue;
        _displayColumns = displayColumns;

        // The writer's last queued list first (review finding 1 of P13, restated by
        // FrameTableViewModel and by ColumnPickerViewModel.ForFrames): the display document is the
        // snapshot AppHost read on Program.Main's thread, and it is stale the moment the Display
        // tab's picker toggles a column. LastWritten reads no settings, which is what keeps it
        // legal on a construction path.
        _ledgerColumnKeys = displayColumns?.LastWritten(DisplaySettings.LedgerHiddenTableId)
            ?? (display ?? new DisplaySettings()).ColumnsFor(DisplaySettings.LedgerHiddenTableId);

        if (displayColumns is not null)
        {
            displayColumns.Changed += OnDisplayColumnsChanged;
        }

        // The notes writer is keyed on the target id, which an obj: group does not have. The
        // delegate no-ops rather than throwing, and CanEditNotes disables the box, so there are
        // two independent guards on the same rule.
        Notes = new AutosaveField(
            text =>
            {
                if (Header?.TargetId is { } targetId)
                {
                    saveNotes(targetId, text);
                }
            },
            delay,
            _post,
            _logger);

        // Spec 12.4's cross-session chart, which P12 R9 moved into the "Trend across nights" band
        // above the workbench, where the ledger and the session pane sit. Built
        // over Sessions itself, so the chart reads the same nights the page owns and the page keeps
        // one collection rather than a second projection of it. Constructed before Load, because
        // ReplaceSessions calls Reload on it.
        TargetChart = new TargetChartViewModel(Sessions, selection, _logger);
        TargetChart.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TargetChartViewModel.PlottedNights))
            {
                RebuildNightFilterMatrix();
            }
        };

        // Spec 12.9's merge history, on Target detail: the manifests whose winner is this target.
        // Keyed on the group key, which for a resolved page is the target id in its storage form
        // (the same equivalence SqlFragments.GroupScope relies on), so the list is built once here
        // rather than rebuilt by every reload. An obj: group has absorbed nothing and gets none.
        if (createMergeHistory is not null && Guid.TryParse(groupKey, out var historyTargetId))
        {
            MergeHistory = createMergeHistory(historyTargetId);
            MergeHistory.Undone += OnMergeUndone;
        }

        if (scanStatus is not null)
        {
            // ScanStatusService has already marshalled onto the UI thread; do not post again.
            scanStatus.ScanFinished += OnScanFinished;
        }

        // Phase 15B fixer F2, beside the scan subscription and for the same reason: a scan is not
        // the only thing that can rewrite what this page shows. Subscribed before the first load,
        // so a save landing in that window is not the one the reader has to refresh by hand.
        if (subscribeDerivedDataChanged is not null && unsubscribeDerivedDataChanged is not null)
        {
            subscribeDerivedDataChanged(OnDerivedDataChanged);
            _unfollowDerivedData = () => unsubscribeDerivedDataChanged(OnDerivedDataChanged);
        }

        // Phase 21, spec 12.16.
        _ninaClient = ninaClient;
        _stellariumClient = stellariumClient;
        _jobs = jobs;
        _emitActivity = emitActivity;
        _getGeneral = getGeneral;
        _getAliasMap = getAliasMap;
        _getSessionDetail = getSessionDetail;
        _openSurveyView = openSurveyView;
        _openCreateMosaic = openCreateMosaic;

        // An instance saved, disabled or deleted on the External Tools tab, and a timezone or
        // clock change on the Location tab, reach an open page here.
        if (subscribeGeneralChanged is not null && unsubscribeGeneralChanged is not null)
        {
            subscribeGeneralChanged(OnGeneralChanged);
            _unfollowGeneralChanged = () => unsubscribeGeneralChanged(OnGeneralChanged);
        }

        Load();
    }

    /// <summary>The dashboard group key this page was opened for.</summary>
    public string GroupKey { get; }

    /// <summary>
    /// The name of the <c>TargetPageMode</c> the Modes layout is showing, or null while the layout
    /// has not chosen one (its own default). The layout writes it on a mode click and reads it on
    /// attach; the shell observes it to push a history entry per mode switch
    /// (.planning/mouse-navigation.md, decision 2) and writes it back on Back or Forward.
    /// </summary>
    [ObservableProperty]
    public partial string? Mode { get; set; }

    /// <summary>Spec 12.4's header block. Null until the first load completes, and while
    /// <see cref="IsMissing"/> is true.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditNotes), nameof(IsResolved), nameof(Title), nameof(ShowsSurveyViewButton))]
    [NotifyCanExecuteChangedFor(nameof(OpenSurveyViewCommand))]
    public partial TargetHeaderViewModel? Header { get; private set; }

    /// <summary>Spec 12.4's totals row. Null on the same conditions as <see cref="Header"/>.
    /// </summary>
    [ObservableProperty]
    public partial TargetTotalsViewModel? Totals { get; private set; }

    /// <summary>The night by filter spine the Compare and Integration tables read; its Compare
    /// columns are the trend chart's plotted nights. Null with <see cref="Totals"/>.</summary>
    [ObservableProperty]
    public partial NightFilterMatrixViewModel? NightFilterMatrix { get; private set; }

    private IReadOnlyList<NightFilterOverview> _nightFilters = [];

    private void RebuildNightFilterMatrix()
    {
        if (Totals is null)
        {
            return;
        }

        NightFilterMatrix = new NightFilterMatrixViewModel(
            _nightFilters,
            Totals.FilterSwatches,
            TargetChart.PlottedNights,
            Totals.Totals);
    }

    /// <summary>The session cards, newest session first (Task 4). Task 8 reads this collection
    /// for its chart's X axis; each card carries its <c>SessionOverview</c> on
    /// <c>SessionCardViewModel.Overview</c>.</summary>
    public ObservableCollection<SessionCardViewModel> Sessions { get; } = [];

    /// <summary>
    /// Spec 12.4's checked nights, as session dates in the ledger's own order, which is
    /// <see cref="Sessions"/>' order, which is newest first. The selection spine the later phases
    /// consume: Phase 16's WBPP export, Phase 18's mosaic assembly and Phase 21's AstroBin CSV
    /// each read this rather than growing a selection of their own.
    /// </summary>
    /// <remarks>
    /// Not persisted (spec 12.4, and <c>task1-report.md</c> open question 2): restoring
    /// yesterday's checks would arm an action over nights the user has forgotten checking.
    /// Opening a different target clears it with no code here, because the page is constructed
    /// per group key and disposed when another target opens.
    /// <para>
    /// Rebuilt whole on every change rather than spliced. The ledger is tens of rows, and an
    /// insert-at-the-right-index scheme against a newest-first collection is a sorting bug waiting
    /// to happen.
    /// </para>
    /// </remarks>
    public ObservableCollection<DateOnly> SelectedNights { get; } = [];

    /// <summary>
    /// Spec 12.4's tri-state header box: true when every night is checked, false when none is,
    /// and null otherwise, which is what drives the box's indeterminate state. False for an empty
    /// ledger, where there is nothing to be all of, and pinned by
    /// <c>NightSelectionTests.AnEmptyLedger_ReportsNoSelectionRatherThanAll</c>.
    /// </summary>
    public bool? AreAllNightsSelected
    {
        get
        {
            if (Sessions.Count == 0)
            {
                return false;
            }

            var checkedCount = SelectedNights.Count;
            return checkedCount == Sessions.Count ? true : checkedCount == 0 ? false : null;
        }
    }

    /// <summary>
    /// Spec 12.4's "a tri-state box that selects all nights when it is clear and clears them when
    /// any are checked". Deliberately not a plain invert: from a partial selection it clears,
    /// which is the destructive-looking branch a plain invert would get wrong by checking the
    /// rest instead.
    /// </summary>
    [RelayCommand]
    private void ToggleAllNights()
    {
        var anyChecked = SelectedNights.Count > 0;

        // One merged card for the whole toggle, not one per night.
        _suspendReviewRules = true;
        try
        {
            foreach (var card in Sessions)
            {
                card.IsChecked = !anyChecked;
            }
        }
        finally
        {
            _suspendReviewRules = false;
        }

        RebuildReviewSession();

        // Task 4 review P3: on an empty ledger the loop changes no card, so no card raises,
        // RebuildSelectedNights never runs and the OneWay binding never overwrites the value the
        // click left on the box, which drew the header box checked over a ledger with no nights.
        // The raise is unconditional rather than guarded on an empty collection, because a raise
        // for a value that did not move costs one binding read.
        OnPropertyChanged(nameof(AreAllNightsSelected));
    }

    // Rebuilds SelectedNights from the cards, in ledger order, and republishes the tri-state and
    // the Export flyout entries' enablement. Called from every card's IsChecked change and at
    // the end of ReplaceSessions, so a night the scan pruned away leaves the set with its card.
    private void RebuildSelectedNights()
    {
        SelectedNights.Clear();
        foreach (var card in Sessions)
        {
            if (card.IsChecked)
            {
                SelectedNights.Add(card.SessionDate);
            }
        }

        OnPropertyChanged(nameof(AreAllNightsSelected));
        OnPropertyChanged(nameof(NoNightCheckedHint));
        OnPropertyChanged(nameof(ExportLabel));
        OnPropertyChanged(nameof(CopyFrameListLabel));
        OnPropertyChanged(nameof(ExportForStackingLabel));
        OnPropertyChanged(nameof(AstroBinCsvLabel));
        CopyFrameListCommand.NotifyCanExecuteChanged();
        ExportForStackingCommand.NotifyCanExecuteChanged();
        AstroBinCsvCommand.NotifyCanExecuteChanged();
        CreateMosaicCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Spec 12.4 and 12.13: the sentence both Export flyout entries carry while nothing is
    /// checked, and null once a night is, so an entry that will run carries no tooltip at all.
    /// </summary>
    public string? NoNightCheckedHint
        => SelectedNights.Count == 0 ? "Select one or more nights first" : null;

    /// <summary>The Export button's text, with the checked-night count once one night is checked.
    /// </summary>
    public string ExportLabel => SelectedNights.Count == 0 ? "Export" : $"Export ({SelectedNights.Count})";

    /// <summary>Spec 12.4: the Copy frame list entry's own label, with the checked-night count
    /// appended once one night is checked and bare at zero, the way the web's actions menu reads.
    /// The count is nights and never frames.</summary>
    public string CopyFrameListLabel
        => SelectedNights.Count == 0 ? "Copy frame list" : $"Copy frame list ({SelectedNights.Count})";

    /// <summary>Spec 12.4: the Export for stacking entry's own label, on the same rule as
    /// <see cref="CopyFrameListLabel"/>.</summary>
    public string ExportForStackingLabel
        => SelectedNights.Count == 0 ? "Export for stacking" : $"Export for stacking ({SelectedNights.Count})";

    /// <summary>Spec 12.16: the Export flyout's third entry, on the same rule as
    /// <see cref="CopyFrameListLabel"/>.</summary>
    public string AstroBinCsvLabel
        => SelectedNights.Count == 0 ? "AstroBin CSV" : $"AstroBin CSV ({SelectedNights.Count})";

    // Carried finding 17's third and fourth PropertyChanged name match: one handler over every
    // card, attached in ReplaceSessions where the cards are created or carried and detached in
    // the same loop that disposes the vanished ones, or the page leaks a handler per scan.
    private void OnSessionCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionCardViewModel.IsChecked) or null)
        {
            RebuildSelectedNights();
            if (!_suspendReviewRules)
            {
                RebuildReviewSession();
            }
        }
    }

    // ---- P25 R2: the review set and the merged card --------------------------------------------

    /// <summary>What the Night review pane shows: the lit card with a review set of one, the merged
    /// card over the lit row plus every checked night otherwise (P25 R2).</summary>
    [ObservableProperty]
    public partial SessionCardViewModel? ReviewSession { get; private set; }

    /// <summary>The merged card, when the review set holds two or more nights; null otherwise.</summary>
    internal SessionCardViewModel? MergedSession { get; private set; }

    // Set for the length of ReplaceSessions and ToggleAllNights, so the set's rules (the R1 clear
    // and the merged card's rebuild) run once at the end rather than once per card.
    private bool _suspendReviewRules;

    // Rebuilds ReviewSession from the lit row and the checks: the previous merged card is disposed
    // first, and a set of two or more builds a new one whose load merges the members' details.
    private void RebuildReviewSession()
    {
        var previous = MergedSession;
        MergedSession = null;
        previous?.Dispose();

        if (_disposed)
        {
            ReviewSession = null;
            return;
        }

        var lit = SelectedSession;
        var members = Sessions.Where(card => card.IsChecked || ReferenceEquals(card, lit)).ToList();
        if (members.Count < 2 || _detail?.Header is not { } header)
        {
            ReviewSession = lit;
            return;
        }

        var oldestFirst = members.OrderBy(card => card.SessionDate).ToList();
        var read = _getSessionDetail;
        var card = _createCard(
            header,
            SessionDetailMerge.Overview([.. members.Select(member => member.Overview)]),
            new MergedCardSpec(
                (group, _) => SessionDetailMerge.Merge([.. oldestFirst.Select(member => member.Detail
                    ?? read?.Invoke(group, member.SessionDate)
                    ?? throw new InvalidOperationException($"The night {MetricText.Date(member.SessionDate)} could not be read for the merged review."))]),
                [.. oldestFirst.Select(member => member.SessionDate)],
                oldestFirst));

        card.CompareWith(Totals);
        MergedSession = card;
        ReviewSession = card;
        card.IsExpanded = true;
    }

    /// <summary>Spec 12.4's target notes box: one second idle autosave, saving indicator, and a
    /// refresh that never clobbers in-progress typing.</summary>
    public AutosaveField Notes { get; }

    /// <summary>Spec 13's cross-session metric trend (Task 8), over <see cref="Sessions"/>. Owned
    /// by the page and disposed with it; the metric and filter selection it shares with every
    /// per-session chart is a process-wide singleton and outlives it.</summary>
    public TargetChartViewModel TargetChart { get; }

    /// <summary>The process-wide <c>display.target_page</c> holder, shared by every open page.</summary>
    public TargetPageState TargetPage { get; }

    /// <summary>Spec 12.9's merge history for this target, below the notes box. Null for an
    /// <c>obj:</c> group. The same view-model type the Settings Targets tab renders; only the
    /// load delegate differs.</summary>
    public MergeHistoryViewModel? MergeHistory { get; }

    /// <summary>An <c>obj:</c> group has no targets row to key a note on, so the box is disabled
    /// rather than silently discarding what is typed into it.</summary>
    public bool CanEditNotes => Header?.TargetId is not null;

    /// <summary>Whether the page is a resolved target. Spec 12.4 draws the Create mosaic entry for
    /// one only, because an unresolved <c>obj:</c> group has no target id for a panel night to
    /// name.</summary>
    public bool IsResolved => Header?.TargetId is not null;

    /// <summary>Spec 12.4's collapsible notes box. Kept because the notes field's own state reads
    /// it; the redesigned page puts the box in the Details drawer, where the drawer is the
    /// disclosure and this flag no longer opens anything (P12).</summary>
    [ObservableProperty]
    public partial bool AreNotesExpanded { get; set; }

    // ---- P12: the selected night, the drawer and the workbench's width ----------------------

    /// <summary>
    /// The night the workbench is open on. The comp opens on the newest night, so the "was last
    /// night good" answer is on screen with no click.
    /// </summary>
    /// <remarks>
    /// The setter is what keeps spec 12.4's "the session detail query is issued when a card
    /// expands, not up front" true in a page that no longer has cards: it clears the outgoing
    /// card's <see cref="SessionCardViewModel.IsExpanded"/> and sets the incoming one's, so
    /// <c>OnIsExpandedChanged</c> and <c>EnsureLoaded</c> run exactly as they did, one query per
    /// night, at most once per night per page load. The outgoing card keeps its loaded
    /// <c>Detail</c>, so arrowing back is free.
    /// <para>
    /// The two hooks stand down while <see cref="ReplaceSessions"/> is rebuilding the collection.
    /// A bound <c>ListBox</c> with <c>SelectionMode="AlwaysSelected"</c> writes its own selection
    /// back as the collection empties and refills, and honouring those writes would expand and
    /// query a night the user is not on. The invariant is restored once, at the end of the diff.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial SessionCardViewModel? SelectedSession { get; set; }

    partial void OnSelectedSessionChanging(SessionCardViewModel? oldValue, SessionCardViewModel? newValue)
    {
        if (!_replacingSessions && oldValue is not null)
        {
            oldValue.IsExpanded = false;
        }
    }

    partial void OnSelectedSessionChanged(SessionCardViewModel? value)
    {
        if (!_replacingSessions && value is not null)
        {
            value.IsExpanded = true;
        }

        if (_suspendReviewRules || _disposed)
        {
            return;
        }

        // P25 R1, Explorer semantics: a lit-row move outside a reload clears every check. The
        // per-card handler stands down so the set and the merged card are rebuilt once.
        _suspendReviewRules = true;
        try
        {
            foreach (var card in Sessions)
            {
                card.IsChecked = false;
            }
        }
        finally
        {
            _suspendReviewRules = false;
        }

        RebuildReviewSession();
    }

    // Guards the two hooks above for the length of one ReplaceSessions call.
    private bool _replacingSessions;

    /// <summary>
    /// Spec 12.4's PAR-018 "scrolls the ledger to it": the ledger row a deep link opened the page
    /// on, published for the view to scroll into view. Null on every load that was not a deep
    /// link, and null when the requested date named a night this target does not have.
    /// </summary>
    /// <remarks>
    /// A seam rather than a <c>ScrollIntoView</c> call, because that is a control call and the
    /// view-model does not reach into the <c>ListBox</c>. The shape is
    /// <c>FrameTableViewModel.HighlightedRow</c>'s, which the night strip's hover link already
    /// uses (P13 R9): the view subscribes to <c>PropertyChanged</c> and scrolls. Set once, by the
    /// first <see cref="ReplaceSessions"/> that consumes the requested date, so a later reload
    /// scrolls nothing and leaves the ledger where the user left it.
    /// </remarks>
    [ObservableProperty]
    public partial SessionCardViewModel? LedgerScrollTarget { get; private set; }

    /// <summary>Whether the comp's Details panel is open: the catalogue text, the target notes
    /// and the merge history, which are the page's least frequent content and sit behind one
    /// click.</summary>
    [ObservableProperty]
    public partial bool IsDetailsOpen { get; set; }

    /// <summary>Opens and closes the Details panel. Reached from the Details button, from Ctrl+D,
    /// and closed by Escape while it is open.</summary>
    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    /// <summary>
    /// The comp's responsive rule, decided structurally: at 1600 px and above the ledger is
    /// 640 px and the Details drawer is inline; below, the ledger is 520 px and the drawer
    /// overlays. Set by the view from <c>SizeChanged</c>, because the breakpoint is a layout fact
    /// and the view model is what the bindings read (ruling Q12). Starts narrow, so a page that
    /// has not been measured yet is laid out for the smaller window rather than overflowing it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(LedgerFiltersMaxWidth),
        nameof(LedgerFiltersSizeGroup),
        nameof(LedgerCustomMaxWidth),
        nameof(LedgerCustomSizeGroup),
        nameof(DetailsDisplayMode),
        nameof(NightColumnMinWidth))]
    public partial bool IsWide { get; private set; }

    // The drawn custom column set is the wide set or nothing, so crossing the
    // breakpoint changes it and the ledger's own width with it.
    //
    // Dragging the window under the breakpoint mid-edit therefore takes the cell off screen with the
    // caret and whatever was in it. The VALUE is not lost: CustomCellGroup.Dispose starts a flush
    // before it disposes, and AutosaveField.FlushAsync attaches the pending text to its write chain
    // under the gate before returning, so the write is already on the chain and disposing cancels
    // only the parked idle window. That ordering is what this depends on; a change to it would turn
    // a resize into a lost edit.
    partial void OnIsWideChanged(bool value) => PublishCustomColumns();

    /// <summary>
    /// The maximum width the ledger's filters column may reserve: the comp's 160 px above the
    /// breakpoint, and nothing at all below it.
    /// </summary>
    /// <remarks>
    /// Ruling Q12 hides the filter cells below the breakpoint, and that alone left the column
    /// reserving width for cells it does not draw. Avalonia's shared size groups only grow: once
    /// the wide ledger has been laid out the <c>LedgerFilters</c> group holds the widest cell it
    /// ever measured, and an invisible cell does not give it back, so the narrow ledger spent
    /// about 110 px on an empty column and the star Night column collapsed to about 16 px with no
    /// date drawn (Phase 12 verification, blocker 1). A column's user maximum clamps the shared
    /// minimum, so binding this to zero collapses the column definition itself rather than only
    /// its content. Bound by the three ledger grids in
    /// <c>Views/TargetDetail/TargetDetailView.axaml</c>, including the night-row template, which
    /// reaches it through the page rather than through its own <c>SessionCardViewModel</c>.
    /// </remarks>
    public double LedgerFiltersMaxWidth => IsWide ? WideLedgerFiltersMaxWidth : 0d;

    /// <summary>
    /// The shared size group the ledger's filters column joins: the ledger's own group above the
    /// breakpoint, and none at all below it.
    /// </summary>
    /// <remarks>
    /// The group is what carries the reservation, so the group is what has to go. A user maximum
    /// does not clamp a shared minimum in Avalonia (measured: the column still reported the 116 px
    /// the wide ledger had put in the group, with <see cref="LedgerFiltersMaxWidth"/> bound to
    /// zero), so below the breakpoint the three filters columns leave the group entirely and are
    /// plain Auto columns whose only content is invisible, which measures nothing. Above it they
    /// rejoin <c>LedgerFilters</c> and the header cell, the target row's swatches and every night
    /// row's swatches line up again. The maximum stays bound beside this because it is the comp's
    /// 160 px cap on the wide column and it costs nothing to keep the two in step.
    /// </remarks>
    public string? LedgerFiltersSizeGroup => IsWide ? LedgerFiltersSharedSizeGroup : null;

    /// <summary>The name of the wide ledger's filters size group, as the markup declares it.</summary>
    public const string LedgerFiltersSharedSizeGroup = "LedgerFilters";

    /// <summary>The comp's filters column, at its widest.</summary>
    public const double WideLedgerFiltersMaxWidth = 160d;

    /// <summary>
    /// Exactly what the drawn custom cells need, and nothing below the breakpoint, which is also
    /// the maximum the column definition may reserve.
    /// </summary>
    /// <remarks>
    /// Spec 12.15: the narrow ledger drops every custom column BEFORE it drops
    /// the Filters column, so the built-in columns and their minimums and maximums are
    /// unchanged whatever is switched on. Zero below the breakpoint collapses the column definition
    /// itself rather than only its content, exactly as <see cref="LedgerFiltersMaxWidth"/> does and
    /// for the reason written out there. Above it the figure is the strip's own measured width,
    /// so the column takes no width from any built-in column.
    /// </remarks>
    public double LedgerCustomMaxWidth => LedgerCustomStripWidth;

    /// <summary>
    /// The shared size group the ledger's custom column joins: the ledger's own group while a
    /// custom column is drawn, and none at all otherwise, which below the breakpoint is always.
    /// </summary>
    /// <remarks>
    /// The group is what carries the reservation, so the group is what has to go: a user maximum
    /// does not clamp a shared minimum in Avalonia, which <see cref="LedgerFiltersSizeGroup"/>'s own
    /// remark records as a measurement. Below the breakpoint this column leaves the group entirely
    /// and is a plain <c>Auto</c> column with no content at all, which measures nothing.
    /// </remarks>
    public string? LedgerCustomSizeGroup
        => LedgerCustomStripWidth > 0d ? LedgerCustomSharedSizeGroup : null;

    /// <summary>The name of the wide ledger's custom column size group, as the markup declares it.
    /// </summary>
    public const string LedgerCustomSharedSizeGroup = "LedgerCustom";

    /// <summary>The cap on how much extra width switched-on custom columns may add to the wide
    /// ledger: the strip plus <see cref="WideLedgerCustomGutter"/> never exceeds this. A column that
    /// does not fit under it is dropped, last first, by <see cref="RecomputeLedgerCustomColumns"/>.
    /// It admits a check box beside a text column even when the check box's heading is as wide as
    /// <see cref="CustomCellWidths.ForHeading"/> allows, so naming a column never drops its
    /// neighbour: two columns at the heading cap, one gap, and the gutter.
    /// </summary>
    public const double WideLedgerCustomWidthCap =
        WideLedgerCustomGutter + CustomCellWidths.Choice + CustomCellSpacing + CustomCellWidths.Choice;

    /// <summary>The gutter the grown wide ledger adds beside the custom strip: the wide ledger's
    /// built-in column minimums measure 29 px more than the ledger they sit in at the shipped face,
    /// so the ledger carries them with the strip or the last cell lands outside.</summary>
    public const double WideLedgerCustomGutter = 32d;

    /// <summary>What the wide ledger adds to its width while a custom column is drawn, and nothing
    /// at all while none is.</summary>
    public double LedgerCustomExtraWidth
        => LedgerCustomStripWidth > 0d ? LedgerCustomStripWidth + WideLedgerCustomGutter : 0d;

    /// <summary>The gap between two custom cells in a ledger strip, and the figure
    /// <see cref="RecomputeLedgerCustomColumns"/> adds once per gap.</summary>
    public const double CustomCellSpacing = 4d;

    /// <summary>
    /// What the drawn custom cells measure across: the sum of their per-kind widths plus one
    /// <see cref="CustomCellSpacing"/> per gap, and zero when none is drawn. Computed once, by
    /// <see cref="RecomputeLedgerCustomColumns"/>, and read by
    /// <see cref="LedgerCustomMaxWidth"/> and by <see cref="LedgerCustomSizeGroup"/>.
    /// </summary>
    public double LedgerCustomStripWidth { get; private set; }

    /// <summary>
    /// The Nights ledger's Night column floor: higher for the wide ledger than the narrow one
    /// (phase-review carried observation, phase14c/fixer-list.md item 2), bound by all three
    /// ledger grids in <c>Views/TargetDetail/TargetDetailView.axaml</c> (the header row, the
    /// totals row and the night-row template) the same way they already bind
    /// <see cref="LedgerFiltersMaxWidth"/>.
    /// </summary>
    public double NightColumnMinWidth => IsWide ? WideNightColumnMinWidth : NarrowNightColumnMinWidth;

    /// <summary>
    /// Ten tabular characters, the ISO date the column exists to show ("2025-03-20"), plus the
    /// cell's own 2 px left and right margin (<c>Grid.ledger-row &gt; TextBlock</c>), measured at
    /// the shipped face and the default "large" text size and rounded up: about 112. The wide
    /// ledger has the slack to give it, which <see cref="NarrowNightColumnMinWidth"/> does not.
    /// </summary>
    public const double WideNightColumnMinWidth = 112d;

    /// <summary>
    /// P14A ruling C5's original figure, kept for the narrow ledger only. HANDOFF 5.1 item 7b
    /// measured the narrow ledger's own slack at this floor as exactly 2 px, with three numeric
    /// columns already at their declared caps, so the wide ledger's 8 px raise has nowhere to
    /// come from here.
    /// </summary>
    public const double NarrowNightColumnMinWidth = 104d;

    // ---- Spec 12.15's ledger cells -----------------------------------------------------------

    /// <summary>
    /// The width one check box cell takes in a ledger custom column strip. This name exists because
    /// the night rows' editors and the header row's labels both have to take the figure and the two
    /// strips must line up, and the view binds it with <c>x:Static</c>; the figure itself is
    /// <see cref="CustomCellWidths.Check"/>, which is the one per-kind table the dashboard reads too.
    /// </summary>
    public const double CustomCellCheckWidth = CustomCellWidths.Check;

    /// <summary>The width one dropdown cell takes: <see cref="CustomCellWidths.Choice"/>.</summary>
    public const double CustomCellChoiceWidth = CustomCellWidths.Choice;

    /// <summary>
    /// The width one text cell takes: <see cref="CustomCellWidths.Text"/>, which is 120 and not the
    /// 160 a text-bearing dashboard cell otherwise carries. The reason is this page's:
    /// <see cref="WideLedgerCustomWidthCap"/> does not hold
    /// a text column and a check box together at 160. The figure is stated once, where both surfaces
    /// read it.
    /// </summary>
    public const double CustomCellTextWidth = CustomCellWidths.Text;

    /// <summary>
    /// The ledger's custom column headings: one per switched-on session-scope column, in display
    /// order, each carrying the width its cells take on every night row so the header strip and the
    /// night strips line up. Empty on every library until the reader switches a column on in the
    /// Display tab's Nights ledger columns picker.
    /// </summary>
    public IReadOnlyList<LedgerCustomHeadingViewModel> LedgerCustomHeadings { get; private set; } = [];

    /// <summary>
    /// The cell width one column's type takes. This page's name for
    /// <see cref="CustomCellWidths.For"/>, which is the one place the three figures are chosen, for
    /// the dashboard as well as for this ledger.
    /// </summary>
    public static double CustomCellWidth(CustomColumnType type) => CustomCellWidths.For(type);

    /// <summary>
    /// A toggle on the Settings Display tab, arriving through the one writer, so an open
    /// page adopts it without a navigation exactly as a live frame table adopts another table's
    /// toggle (<c>FrameTableViewModel.OnColumnsChanged</c>).
    /// </summary>
    /// <remarks>Raised inside <c>DisplayColumnWriter.Write</c>, which is called from the UI thread
    /// on a picker click, so this runs there too and must stay a cheap in-memory update.</remarks>
    private void OnDisplayColumnsChanged(string tableId, string[] keys)
    {
        if (_disposed || !string.Equals(tableId, DisplaySettings.LedgerHiddenTableId, StringComparison.Ordinal))
        {
            return;
        }

        _ledgerColumnKeys = keys;
        PublishCustomColumns();
    }

    /// <summary>
    /// The ONE place the ledger's drawn custom column set and its width are decided.
    /// Below the breakpoint the set is empty, which is the drop custom columns take there; above it the set is
    /// <c>CustomColumnSet.LedgerRow</c> truncated where the next column would take the strip past
    /// <see cref="WideLedgerCustomWidthCap"/>, which is the same drop applied one column at a time
    /// from the end. There is no second mechanism: the header strip, every night row's strip, the
    /// column definition's maximum, its size group and the ledger's own width all read what this
    /// computes.
    /// </summary>
    private void RecomputeLedgerCustomColumns()
    {
        var drawn = new List<CustomColumnDefinition>();
        var width = 0d;

        if (IsWide)
        {
            foreach (var column in CustomColumnSet.LedgerRow(_customColumns, _ledgerColumnKeys))
            {
                var next = width
                    + (drawn.Count == 0 ? 0d : CustomCellSpacing)
                    + LedgerCellWidth(column);

                // Dropped last first: the first column that does not fit takes every column after
                // it with it, so the set the reader sees is always a prefix of the display order.
                // Measured against the extra the LEDGER would have to take, gutter included, which
                // is the figure the cap bounds.
                if (next + WideLedgerCustomGutter > WideLedgerCustomWidthCap)
                {
                    break;
                }

                drawn.Add(column);
                width = next;
            }
        }

        _ledgerCustomColumns = drawn;
        LedgerCustomStripWidth = width;
        LedgerCustomHeadings =
            [.. drawn.Select(column => new LedgerCustomHeadingViewModel(column.Name, LedgerCellWidth(column)))];

        OnPropertyChanged(nameof(LedgerCustomStripWidth));
        OnPropertyChanged(nameof(LedgerCustomExtraWidth));
        OnPropertyChanged(nameof(LedgerCustomMaxWidth));
        OnPropertyChanged(nameof(LedgerCustomSizeGroup));
        OnPropertyChanged(nameof(LedgerCustomHeadings));
    }

    // The ledger view's type size and family, reported by the view so a heading is measured at
    // what it is drawn at. Until reported, a cell is its editor's width.
    private double _headingFontSize;
    private FontFamily? _headingFontFamily;

    /// <summary>One column's width on the ledger, heading and night cells alike:
    /// <see cref="CustomCellWidths.ForHeading"/>, the figure the dashboard takes too.</summary>
    private double LedgerCellWidth(CustomColumnDefinition column)
        => CustomCellWidths.ForHeading(column.Name, column.Type, _headingFontSize, _headingFontFamily);

    /// <summary>The view's type size and family, which only the view has. The night cells bind the
    /// same pair through <see cref="CustomCellWidths.HeadingWidth"/>, so the strips line up.</summary>
    public void ApplyHeadingFont(double fontSize, FontFamily fontFamily)
    {
        if (fontSize == _headingFontSize && Equals(fontFamily, _headingFontFamily))
        {
            return;
        }

        _headingFontSize = fontSize;
        _headingFontFamily = fontFamily;
        RecomputeLedgerCustomColumns();
    }

    // The drawn set, as RecomputeLedgerCustomColumns last computed it.
    private IReadOnlyList<CustomColumnDefinition> _ledgerCustomColumns = [];

    // Recomputes the drawn set and hands every card the definition list, the value map and the
    // write delegate. Called from Publish, before the cards are shown, from the Display tab's
    // toggle, and from the breakpoint. A card that already holds the same columns reseeds its cells
    // rather than rebuilding them, which is where typing that has not saved yet survives a refresh.
    private void PublishCustomColumns()
    {
        RecomputeLedgerCustomColumns();

        foreach (var card in Sessions)
        {
            card.PublishCustomColumns(
                Header?.TargetId,
                _customColumns,
                _ledgerCustomColumns,
                _customValues,
                _writeCustomValue);
        }
    }

    /// <summary>
    /// How the Details drawer sits over the workbench. An Avalonia enum in a view model, which
    /// this codebase otherwise avoids: it is a layout enum rather than a brush or a control, and
    /// the alternative is a boolean plus a converter for one binding.
    /// </summary>
    public SplitViewDisplayMode DetailsDisplayMode
        => IsWide ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;

    /// <summary>The 1600 px breakpoint, in device-independent pixels.</summary>
    public const double WideBreakpoint = 1600d;

    /// <summary>Applies the page's measured width. Called by the view on every
    /// <c>SizeChanged</c>; assigning the same value again raises nothing.</summary>
    /// <remarks>
    /// The width is the page's own, not the window's, which is the surface the phase review ruled
    /// the 1600 px rule names (P2-6, coordinator ruling (4)): the page is the window less the
    /// navigation rail, so the wide ledger appears at about an 1800 px window with the rail
    /// expanded and about 1650 with it collapsed. <c>task7-addenda.md</c> item 8 said the window and
    /// is corrected.
    /// </remarks>
    public void ApplyWidth(double width) => IsWide = width >= WideBreakpoint;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The key named no target: merged away, pruned, or never there (ruling Q4). The
    /// view renders a one-line callout and a Back button, never a blank page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingText), nameof(ShowStaleKeyCallout))]
    public partial bool IsMissing { get; private set; }

    /// <summary>The callout's text. Names the group key so a stale link is diagnosable.</summary>
    public string MissingText =>
        $"No target matches the group key {GroupKey}. It may have been merged into another target, or its frames may have been removed by a scan.";

    /// <summary>The surviving target's <c>primary_name</c> when this key names a merged-away
    /// target, otherwise null (FIXER LIST item 14, ruling Q12). Set only on the
    /// <see cref="IsMissing"/> path.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsMergedAway),
        nameof(MergedAwayText),
        nameof(OpenMergedIntoText),
        nameof(ShowStaleKeyCallout))]
    [NotifyCanExecuteChangedFor(nameof(OpenMergedIntoCommand))]
    public partial string? MergedIntoName { get; private set; }

    /// <summary>The surviving target's id, which <see cref="OpenMergedIntoCommand"/> hands the
    /// shell as a group key.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenMergedIntoCommand))]
    public partial Guid? MergedIntoTargetId { get; private set; }

    /// <summary>The merged-away target's own name, for the page title. Phase 7 FIXER item 18: the
    /// detail query returns no header for a merged-away key, so the title was empty and the
    /// callout below it named only the winner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string? MergedAwayName { get; private set; }

    /// <summary>What the page's title line shows: the header's name normally, the merged-away
    /// target's name when there is no header because it was merged away, and an empty string for
    /// the other missing cases, which the callout explains.</summary>
    public string Title => Header?.Name ?? MergedAwayName ?? string.Empty;

    public bool IsMergedAway => MergedIntoName is not null;

    /// <summary>Spec 12.10's rule kept and the dead end removed: the merge is named rather than
    /// hidden, and following it is the user's choice.</summary>
    public string MergedAwayText => $"This target was merged into \"{MergedIntoName}\".";

    public string OpenMergedIntoText => $"Open {MergedIntoName}";

    /// <summary>The plain stale-key callout, for the two cases that are not a merge: a pruned
    /// group and a key that never existed.</summary>
    public bool ShowStaleKeyCallout => IsMissing && MergedIntoName is null;

    /// <summary>The last load failure, logged and kept rather than swallowed. The page stays
    /// usable: whatever loaded before is still on screen.</summary>
    [ObservableProperty]
    public partial Exception? LastFailure { get; private set; }

    /// <summary>The inline rename editor's state. Separate from the header's displayed name so
    /// cancelling restores it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitRenameCommand))]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitRenameCommand))]
    public partial string RenameText { get; set; }

    [ObservableProperty]
    public partial string? RenameError { get; private set; }

    /// <summary>The re-resolve outcome line, cleared when a new one starts.</summary>
    [ObservableProperty]
    public partial string? ReResolveStatus { get; private set; }

    /// <summary>Spec 12.16's AstroBin CSV result, "AstroBin CSV copied: <c>r</c> rows from
    /// <c>n</c> nights.". Cleared when a new copy starts, reported inline on the page where
    /// <see cref="ReResolveStatus"/> is.</summary>
    [ObservableProperty]
    public partial string? AstroBinCsvResultText { get; private set; }

    /// <summary>The in-flight load, so a test can await it instead of sleeping. Mirrors
    /// <c>DashboardViewModel.PendingQuery</c>.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>Test seam: the shell asserts that opening a second target disposed the first.
    /// </summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Raised when the page's Back button is pressed. The shell owns the content region
    /// and answers this by calling its own <c>CloseDetail</c>, which is the one place a detail
    /// page is disposed (ruling Q9). The page therefore carries a command its own view can bind
    /// with a compiled binding, and the shell keeps sole ownership of navigation.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Raised after a rename is committed and accepted (FIXER LIST F10). The dashboard's
    /// listing carries the target's name, so a row the user renamed from this page still shows the
    /// old one until the listing is re-queried. The shell answers this on the dashboard's existing
    /// reload path, for the same reason it owns <see cref="BackRequested"/>: a detail page does not
    /// reach into another page's query.</summary>
    public event EventHandler? TargetRenamed;

    /// <summary>Raised when the user asks to open another target's page from this one, carrying
    /// that target's group key (ruling Q12's merged-away callout). Answered exactly as
    /// <see cref="BackRequested"/> is: <c>MainWindowViewModel</c> owns navigation, so it replaces
    /// its one detail overlay. Never a second overlay, and never a silent redirect.</summary>
    public event EventHandler<string>? OpenTargetRequested;

    /// <summary>Spec 12.4's Back affordance.</summary>
    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens the target this one was merged into. The shell answers, because a detail
    /// page does not own navigation.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenMergedInto))]
    private void OpenMergedInto()
    {
        if (MergedIntoTargetId is { } targetId)
        {
            // The group key of a resolved target is its id: SqlFragments.GroupScope parses the
            // key back to a Guid, so the round trip is exact.
            OpenTargetRequested?.Invoke(this, targetId.ToString());
        }
    }

    private bool CanOpenMergedInto() => MergedIntoTargetId is not null;

    /// <summary>
    /// Spec 12.4's "merge into another target" action. Opens spec 12.9's modal with this target
    /// as the winner and no loser chosen, so the dialog's own search box picks the loser (Task 4's
    /// entry point). A confirmed merge reloads the page, refreshes the history below it, and
    /// raises <see cref="TargetRenamed"/>, because a merge changes this target's name set and
    /// frame count, which is exactly what that event exists to tell the dashboard.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        if (_openMerge is null || Header?.TargetId is not { } targetId)
        {
            return;
        }

        try
        {
            // Not wrapped in Task.Run: the dialog is a window, and a window is opened on the UI
            // thread. The delegate completes when the modal closes.
            var merged = await _openMerge(targetId).ConfigureAwait(false);
            if (!merged)
            {
                return;
            }

            _post(() =>
            {
                Load();
                MergeHistory?.Reload();
                TargetRenamed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
            // The page closed underneath the dialog.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The merge dialog for target {TargetId} failed", targetId);
        }
    }

    private bool CanMerge() => _openMerge is not null && Header?.TargetId is not null;

    // An undo restores the loser as an active target and moves its frames back, so this page's
    // totals, sessions and aliases are all stale. The dashboard's listing is too, for the same
    // reason a merge makes it stale.
    private void OnMergeUndone(object? sender, EventArgs e)
    {
        Load();
        TargetRenamed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Enters the inline rename editor. Disabled for an <c>obj:</c> group, which has no
    /// target row to rename.</summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private void BeginRename()
    {
        RenameError = null;
        RenameText = Header?.Name ?? "";
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameError = null;
        RenameText = Header?.Name ?? "";
    }

    /// <summary>
    /// Sets the new name through the write repository. A collision keeps edit mode open with the
    /// error shown; success sets the header's name and <c>NameLocked</c> without a reload.
    /// </summary>
    /// <remarks>
    /// The write runs inside <see cref="Task.Run(Func{object})"/> for the same reason every query
    /// in this application does: a locked SQLite database would otherwise freeze the window, and
    /// there is no global dispatcher exception handler to catch what it throws.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCommitRename))]
    private async Task CommitRenameAsync()
    {
        if (Header?.TargetId is not { } targetId)
        {
            return;
        }

        var name = RenameText.Trim();
        if (name.Length == 0)
        {
            RenameError = "A target name cannot be empty.";
            return;
        }

        RenameError = null;
        try
        {
            var outcome = await Task
                .Run(() => _rename(targetId, name), _lifetime.Token)
                .ConfigureAwait(false);

            _post(() => ApplyRenameOutcome(outcome, name));
        }
        catch (OperationCanceledException)
        {
            // The page closed while the write was in flight.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Renaming the target failed");
            _post(() => RenameError = "The rename could not be saved. See the log for details.");
        }
    }

    private void ApplyRenameOutcome(RenameOutcome outcome, string name)
    {
        switch (outcome)
        {
            case RenameOutcome.Renamed when Header is not null:
                Header.Name = name;
                Header.NameLocked = true;
                IsRenaming = false;
                RenameError = null;

                // FIXER LIST F10: the dashboard row behind this page still shows the old name.
                // Raised rather than reloaded from here, because this page does not own the
                // dashboard's query; the shell wires it to the dashboard's existing reload path.
                TargetRenamed?.Invoke(this, EventArgs.Empty);
                break;

            case RenameOutcome.NameTaken:
                RenameError = $"Another target is already named {name}.";
                break;

            case RenameOutcome.NotFound:
                RenameError = "That target no longer exists.";
                break;

            default:
                IsRenaming = false;
                break;
        }
    }

    /// <summary>
    /// FIXER LIST item 13: the one switch that decides whether the Re-resolve action is offered,
    /// and the only thing <c>TargetDetailView</c> reads to decide whether to render the button.
    /// <para>
    /// True since Phase 7 Task 7. The action asks the catalogues what this target's name resolves
    /// to now (<c>TargetResolver.ResolveIdentity</c>, which ignores the stored row) and writes the
    /// answer back through <c>TargetEnrichmentRepository</c>, so it can genuinely change the page:
    /// a target created before a catalogue was bundled, or before a name mapping existed, picks up
    /// its catalog id, coordinates, object type, constellation, sizes and memberships. It was
    /// false in Phase 6 because the writer did not exist and the action could only report the
    /// stage the target already had.
    /// </para>
    /// <para>
    /// Kept as a constant rather than deleted: hiding the action again is one edit here, and
    /// nothing else in the view decides it.
    /// </para>
    /// </summary>
    public const bool ReResolveAvailable = true;

    /// <summary>
    /// Spec 12.4's re-resolve action: clear the resolver negative-cache row for this target's
    /// primary name (ruling Q12), read the identity the catalogues give for it now, write that
    /// back through the re-enrichment writer, and report what changed.
    /// <para>
    /// A changed target reloads the page, because the header block's own fields are what changed,
    /// and raises <see cref="TargetRenamed"/>, because the dashboard row behind this page is stale
    /// for the same reason a rename makes it stale (FIXER LIST F10). An unchanged, suppressed or
    /// failed outcome reloads nothing.
    /// </para>
    /// </summary>
    /// <remarks>
    /// No <see cref="CancellationToken"/> parameter (FIXER LIST F24, and Task 8's deviation D10):
    /// a command built from a <c>Func&lt;CancellationToken, Task&gt;</c> cancels the in-flight
    /// token on a second <c>Execute</c>, and <c>Task.Run</c> with an already-cancelled token skips
    /// the delegate, so the second click would abort the running resolve rather than be refused by
    /// the one-at-a-time guard below. The page lifetime is what cancels this.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanReResolve))]
    private async Task ReResolveAsync()
    {
        // In the body, not only in CanExecute: RelayCommand.Execute runs regardless of
        // CanExecute, and an obj: group has no target row to re-enrich.
        if (Header is not { TargetId: { } targetId } header)
        {
            return;
        }

        // One at a time (Phase 7 fixer item 7), the same shape and the same reason as
        // MergeHistoryViewModel's undo: a second click arriving before the first Task.Run
        // finishes would run the resolver twice for one target and let the slower answer's
        // status sentence overwrite the faster one's.
        if (_reResolving)
        {
            return;
        }

        _reResolving = true;
        var name = header.Name;
        ReResolveStatus = null;
        try
        {
            var token = _lifetime.Token;
            var (changed, message) = await Task
                .Run(() => _reResolve(targetId, name, token), token)
                .ConfigureAwait(false);

            _post(() =>
            {
                ReResolveStatus = message;
                if (changed)
                {
                    Load();
                    TargetRenamed?.Invoke(this, EventArgs.Empty);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // The user navigated away, or the page closed.
        }
        catch (Exception ex)
        {
            // The same reasoning as DashboardViewModel.RunScanAsync: there is no global
            // dispatcher exception handler, so a network or database failure is surfaced here
            // rather than rethrown on the UI thread.
            _logger.LogWarning(ex, "Re-resolving {Name} failed", name);
            _post(() => ReResolveStatus = "Re-resolve failed. See the log for details.");
        }
        finally
        {
            _reResolving = false;
        }
    }

    // Guards ReResolveAsync's body. Not an observable property: nothing binds to it, and the
    // action is fast enough that greying it would flicker.
    private bool _reResolving;

    /// <summary>
    /// Spec 12.4's Copy Frame List (PAR-006): opens the dialog over the checked nights, and is
    /// disabled while nothing is checked. Until this phase it copied every frame path of the whole
    /// target with no dialog at all (ruling Q11); the format the dialog defaults to is that same
    /// string byte for byte, which is ruling C6's whole point.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyFrameList))]
    private async Task CopyFrameListAsync()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the rule that
        // makes the command correct is in the body as well as in the guard.
        if (SelectedNights.Count == 0 || _openFrameList is null)
        {
            return;
        }

        // The checked nights in the ledger's own order, each carrying the detail the page has
        // already loaded, so a night the user has been looking at costs no second query.
        var nights = new List<FrameListNight>(SelectedNights.Count);
        foreach (var card in Sessions)
        {
            if (card.IsChecked)
            {
                nights.Add(new FrameListNight(card.SessionDate, card.Detail));
            }
        }

        await _openFrameList(GroupKey, nights).ConfigureAwait(true);
    }

    /// <summary>
    /// Spec 12.13's Export for stacking: opens the export page over the checked nights, and is
    /// disabled while nothing is checked. The second entry of the identity line's Export flyout,
    /// beside Copy Frame List (spec 12.4).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportForStacking))]
    private async Task ExportForStackingAsync()
    {
        // TRACKING section 6 item 13 and HANDOFF rule 8: RelayCommand.Execute ignores CanExecute,
        // so the rule that makes the command correct is in the body as well as in the guard.
        if (SelectedNights.Count == 0 || _openWbppExport is null)
        {
            return;
        }

        // Copied, for the reason CopyFrameListAsync builds its own list: the page must not observe
        // the ledger's own collection changing under a computation that has already run.
        await _openWbppExport(GroupKey, Title, [.. SelectedNights]).ConfigureAwait(true);
    }

    /// <summary>
    /// Spec 12.16's AstroBin CSV: the Export flyout's third entry, over the checked nights. Loads
    /// the session detail of a checked night the page has not already opened (task5-target-page.md
    /// section 3), builds one <see cref="AstroBinRow"/> per (night, rig, filter) group the loaded
    /// detail's <c>FilterAcquisitions</c> carries, and copies the rendered CSV through
    /// <see cref="ShellIntegration.CopyTextAsync"/>. Registers no job: it is a clipboard write, not
    /// a network call, and reports inline on the page instead (spec 12.16, "The result line").
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAstroBinCsv))]
    private async Task AstroBinCsvAsync()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the rule that
        // makes the command correct is in the body as well as in the guard.
        if (SelectedNights.Count == 0)
        {
            return;
        }

        AstroBinCsvResultText = null;

        var rows = new List<AstroBinRow>();
        var failedNights = 0;
        var contributingNights = 0;

        // Sessions, not SelectedNights: the ledger's own order, and each card carries the date the
        // row needs.
        foreach (var card in Sessions)
        {
            if (!card.IsChecked)
            {
                continue;
            }

            var detail = card.Detail;
            if (detail is null && _getSessionDetail is not null)
            {
                try
                {
                    detail = await Task.Run(() => _getSessionDetail(GroupKey, card.SessionDate))
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Loading session {SessionDate} of {GroupKey} for the AstroBin CSV failed",
                        card.SessionDate,
                        GroupKey);
                    detail = null;
                }
            }

            if (detail?.FilterAcquisitions is not { } acquisitions)
            {
                failedNights++;
                continue;
            }

            if (acquisitions.Count > 0)
            {
                contributingNights++;
            }

            foreach (var group in acquisitions)
            {
                rows.Add(new AstroBinRow(
                    card.SessionDate,
                    group.FilterName,
                    group.FrameCount,
                    group.ExposureTime,
                    group.ModalGain,
                    group.MedianSensorTemp,
                    group.MedianSkyQuality,
                    group.MedianFwhm,
                    group.MedianAmbientTemp));
            }
        }

        var general = _getGeneral?.Invoke();
        var filterIds = IntegrationSettings.ReadFilterIds(general?.AstroBinFilterIdsDocument);
        var bortle = IntegrationSettings.ReadBortle(general?.AstroBinBortle);
        var aliases = _getAliasMap?.Invoke()
            ?? new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings());
        var csv = AstroBinCsv.Build(rows, filterIds, bortle, aliases);

        // No catch: ShellIntegration.CopyTextAsync logs a failed copy and never rethrows, exactly
        // as it does for Copy Frame List.
        await _shell.CopyTextAsync(csv).ConfigureAwait(true);

        // Spec 12.16: one sentence, with the failed-night clause inside it, and nights is the
        // number of checked nights the rows came from.
        var failedClause = failedNights > 0 ? $", {failedNights} nights could not be loaded" : "";
        var summary =
            $"AstroBin CSV copied: {rows.Count} rows from {contributingNights} nights{failedClause}.";

        AstroBinCsvResultText = summary;

        _emitActivity?.Invoke(
            "info",
            "astrobin_csv_copied",
            summary,
            new { target_id = Header?.TargetId, nights = contributingNights, rows = rows.Count },
            Header?.TargetId);
    }

    /// <summary>Ruling Q10: <c>explorer.exe /select</c> on the most recent frame by capture date,
    /// which is the last entry of the query's capture-ordered list.</summary>
    [RelayCommand(CanExecute = nameof(CanRevealFolder))]
    private void RevealFolder() => _shell.RevealFolderOf(FramePaths);

    private IReadOnlyList<string> FramePaths => _detail?.FramePaths ?? [];

    /// <summary>Spec 12.4's Sky view button, absent rather than disabled when the header carries
    /// no target id, RA or Dec.</summary>
    public bool ShowsSurveyViewButton => Header?.Block is { TargetId: not null, Ra: not null, Dec: not null };

    /// <summary>Spec 12.4: <c>general.survey_downloads_enabled</c>, read once at load and kept
    /// current by <see cref="OnGeneralChanged"/> so a switch flip on the General tab reaches an
    /// open page with no navigation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SurveyViewTooltip))]
    [NotifyCanExecuteChangedFor(nameof(OpenSurveyViewCommand))]
    public partial bool SurveyDownloadsEnabled { get; private set; }

    /// <summary>Spec 12.4's disabled-button tooltip, null once the switch is on so the button
    /// carries no tooltip at all while it can be pressed.</summary>
    public string? SurveyViewTooltip => SurveyDownloadsEnabled ? null : SurveyMessages.DisabledTooltip;

    private void RefreshSurveyDownloadsEnabled() =>
        SurveyDownloadsEnabled = _getGeneral?.Invoke().SurveyDownloadsEnabled ?? false;

    /// <summary>Spec 12.4: opens the Sky view window on this target's own header values, never
    /// re-derived.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSurveyView))]
    private async Task OpenSurveyViewAsync()
    {
        if (_openSurveyView is null || Header?.Block is not { TargetId: { } targetId, Ra: { } ra, Dec: { } dec } block)
        {
            return;
        }

        var target = new SurveyTarget(targetId, block.PrimaryName, ra, dec, block.SizeMajor);
        await _openSurveyView(target).ConfigureAwait(true);
    }

    private bool CanOpenSurveyView() => ShowsSurveyViewButton && SurveyDownloadsEnabled && _openSurveyView is not null;

    private bool CanRename() => Header?.TargetId is not null;

    // Spec 12.4: disabled while nothing is checked, the rule CanCopyFrameList states.
    private bool CanCreateMosaic() => SelectedNights.Count > 0;

    /// <summary>
    /// Spec 12.4's "Create mosaic from selected nights" (Phase 18, ruling R12): opens spec 12.17's
    /// dialog over the checked nights. On success the dialog itself routes to the mosaic detail
    /// page, so the page has nothing to reload.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateMosaic))]
    private async Task CreateMosaicAsync()
    {
        if (SelectedNights.Count == 0 || _openCreateMosaic is null || Header?.Block is not { TargetId: { } targetId } block)
        {
            return;
        }

        try
        {
            await _openCreateMosaic(targetId, block.PrimaryName, [.. SelectedNights]).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The dialog's own reads failed before it could open, so nothing appeared: the
            // Activity feed is where the reader learns why (spec 10.9's mosaic_action_failed).
            _logger.LogWarning(ex, "The Create mosaic dialog for target {TargetId} failed", targetId);
            var name = block.PrimaryName;
            try
            {
                _emitActivity?.Invoke(
                    "warning",
                    "mosaic_action_failed",
                    $"Could not create a mosaic from {name}: {ex.Message}",
                    new { action = "create", name, reason = ex.Message },
                    targetId);
            }
            catch (Exception emitFailure)
            {
                _logger.LogWarning(emitFailure, "The mosaic_action_failed event could not be written");
            }
        }
    }

    private bool CanCommitRename() => IsRenaming && Header?.TargetId is not null && RenameText.Trim().Length > 0;

    private bool CanReResolve() => Header?.TargetId is not null;

    // Spec 12.4: "whose command is disabled while nothing is checked". Deliberately not also
    // gated on the dialog delegate: a page built with no dialog host still reports the rule the
    // spec states, and the body is where the missing delegate is handled.
    private bool CanCopyFrameList() => SelectedNights.Count > 0;

    // Spec 12.13: disabled while nothing is checked. Deliberately not also gated on the dialog
    // delegate, for the reason CanCopyFrameList states: a page built with no dialog host still
    // reports the rule the spec states, and the body is where the missing delegate is handled.
    private bool CanExportForStacking() => SelectedNights.Count > 0;

    // Spec 12.16: disabled while nothing is checked, on the same rule as CanCopyFrameList.
    private bool CanAstroBinCsv() => SelectedNights.Count > 0;

    private bool CanRevealFolder() => FramePaths.Count > 0;

    // Not a query on the constructing thread: the page is constructed on the UI thread from a row
    // click, and TargetDetailQuery.Get is a synchronous SQLite read.
    private void Load()
    {
        if (_disposed)
        {
            return;
        }

        IsLoading = true;
        var generation = ++_generation;
        var token = _lifetime.Token;
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    var detail = _get(GroupKey);

                    // Ruling Q12: only on the null path, and on the same background thread, so
                    // the callout can name the merge without a second round trip to the UI
                    // thread. Get still returns null for a merged-away key.
                    var mergedInto = detail is null ? _mergedInto?.Invoke(GroupKey) : null;

                    // Spec 12.15, on this same background pass and not on a second one: the
                    // definitions once, and every session-scope and rig-scope value of this target
                    // once. The session pane's rig rows partition the same result, so the page reads once
                    // whatever is on screen, and a ledger of ten nights costs one read rather than
                    // ten. Skipped entirely for a load that found nothing and for an obj: group,
                    // which has no target id to key a value on.
                    // Ruling C33: a library with no custom column pays nothing. Without the count
                    // guard, every load and every reload of this page issued a second command that
                    // could only return nothing, on every library that has never defined a column.
                    var custom = detail is null ? [] : _loadCustomColumns?.Invoke() ?? [];
                    var customValues =
                        custom.Count > 0
                        && detail?.Header.TargetId is { } valueTargetId
                        && _loadValuesForTarget is not null
                            ? _loadValuesForTarget(valueTargetId)
                            : [];

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() =>
                    {
                        Publish(generation, detail, mergedInto, null, custom, customValues);
                        DrainDeferredReload(generation);
                    });
                }
                catch (OperationCanceledException)
                {
                    // The page closed while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the target detail page for {GroupKey} failed", GroupKey);
                    _post(() =>
                    {
                        Publish(generation, null, null, ex, [], []);
                        DrainDeferredReload(generation);
                    });
                }
            },
            token);
    }

    // Runs on the UI thread through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Publish(
        int generation,
        Queries.TargetDetail? detail,
        (Guid TargetId, string PrimaryName, string LoserName)? mergedInto,
        Exception? failure,
        IReadOnlyList<CustomColumnDefinition> customColumns,
        IReadOnlyList<CustomValueRow> customValues)
    {
        lock (_publishGate)
        {
            if (generation != _generation || _disposed)
            {
                return;
            }

            _publishing++;
        }

        try
        {
            PublishCore(detail, mergedInto, failure, customColumns, customValues);
        }
        finally
        {
            bool disposeNow;
            lock (_publishGate)
            {
                _publishing--;
                disposeNow = _publishing == 0 && _disposeAfterPublish;
            }

            if (disposeNow)
            {
                DisposePublishedState();
            }
        }
    }

    private void PublishCore(
        Queries.TargetDetail? detail,
        (Guid TargetId, string PrimaryName, string LoserName)? mergedInto,
        Exception? failure,
        IReadOnlyList<CustomColumnDefinition> customColumns,
        IReadOnlyList<CustomValueRow> customValues)
    {
        IsLoading = false;

        if (failure is not null)
        {
            // Whatever loaded before stays on screen; the page remains usable.
            LastFailure = failure;
            NotifyActionState();
            return;
        }

        LastFailure = null;

        if (detail is null)
        {
            _detail = null;
            MergedIntoTargetId = mergedInto?.TargetId;
            MergedIntoName = mergedInto?.PrimaryName;
            MergedAwayName = mergedInto?.LoserName;
            IsMissing = true;
            RebuildIntegrationItems();
            NotifyActionState();
            return;
        }

        _detail = detail;
        MergedIntoTargetId = null;
        MergedIntoName = null;
        MergedAwayName = null;
        IsMissing = false;
        // Before the header is replaced: a scan-driven reload builds a new slot, and the outgoing
        // one owns a decoded bitmap (Phase 8 Task 6).
        DisposeReferenceThumbnail();
        Header = new TargetHeaderViewModel(detail.Header, _createReferenceSlot, _setObjectType);
        RefreshSurveyDownloadsEnabled();

        // Spec 12.16: rebuilt from the stored document on every publish rather than cached, and
        // after Header so the "no RA or no Dec" disabled state reads this load's own coordinates.
        RebuildIntegrationItems();

        // The filter dots in the log line and in the ledger resolve through the same lookup the
        // chart pills use, rather than through a second one. Safe here and not at construction:
        // the alias map can fall through to a synchronous settings read, and this runs after the
        // page's data has loaded.
        Totals = new TargetTotalsViewModel(detail.Totals, _selection.FilterTint, _getAliasMap, detail.NightFilters);
        _nightFilters = detail.NightFilters;

        // Spec 12.15, before the cards are shown, so a card is never drawn once without its cells
        // and again with them. The map is built here rather than on the background thread because
        // it is a projection of a list this method has already been handed; the read itself is what
        // had to stay off the UI thread. An index over the four key parts, not a scan per cell: a
        // ledger of tens of nights times the switched-on columns would otherwise be quadratic.
        _customColumns = customColumns;
        var values = new Dictionary<(Guid ColumnId, CustomValueKey Key), string>();
        foreach (var row in customValues)
        {
            values[(row.ColumnId, row.Key)] = row.Value;
        }

        _customValues = values;

        ReplaceSessions(detail.Header, detail.Sessions);
        TargetChart.NightFrames = detail.NightFrames;
        RebuildNightFilterMatrix();

        // After the diff, so every card the load carried and every card it created is handed the
        // same list in the same call.
        PublishCustomColumns();

        // Adopts an externally changed note but never overwrites in-progress typing.
        Notes.Reseed(detail.Header.Notes);

        if (!IsRenaming)
        {
            RenameText = Header.Name;
        }

        NotifyActionState();
    }

    // A scan-driven reload must not throw away what the user has open. Disposing and rebuilding
    // every card collapsed an expanded one and discarded the detail it had already read, so the
    // session date set is diffed instead (review finding 3): a night that is still there keeps
    // its card and takes the fresh overview through Refresh, which re-reads only what the card
    // already had (nothing for a collapsed card, exactly one query for an expanded one); a new
    // night gets a new card; a night the scan pruned away is disposed.
    private void ReplaceSessions(TargetHeaderBlock header, IReadOnlyList<SessionOverview> sessions)
    {
        // Bracketed, because the collection below is rebuilt by clearing and refilling it and
        // refreshing a carried card drops that card's loaded detail: a chart that rebuilt on each
        // step would reconcile the filter pills against a momentarily empty session list and
        // publish a transient empty chart on the way. EndUpdate publishes exactly once. The
        // finally is not decoration: a chart left suspended would never draw again.
        // Read before anything empties the collection. A bound ListBox writes its own selection
        // back on the Reset that Sessions.Clear() raises, so by the time the refill is done
        // SelectedSession no longer names the night the user was on; resolving the kept night from
        // the property at that point always lands on the newest one (review P2-1). The date is a
        // value, so nothing can overwrite it underneath the diff.
        var previousDate = SelectedSession?.SessionDate;

        TargetChart.BeginUpdate();

        // A bound ListBox writes its own selection back while the collection is emptied and
        // refilled; those writes must not expand and query a night the user is not on. The
        // invariant is restored once below, after the diff.
        _replacingSessions = true;
        _suspendReviewRules = true;
        try
        {
            // The query groups by session_date, so the key is unique.
            var carried = Sessions.ToDictionary(card => card.SessionDate);
            Sessions.Clear();

            foreach (var session in sessions)
            {
                if (!_rebuildCards && carried.Remove(session.SessionDate, out var card))
                {
                    card.Refresh(session);
                    Sessions.Add(card);
                }
                else
                {
                    var outgoing = carried.GetValueOrDefault(session.SessionDate);
                    card = _createCard(header, session, null);

                    // A rebuild keeps the user's check on the night; the outgoing card is disposed
                    // below with the rest of the map. Set before the subscription, so the set is
                    // rebuilt once at the end of the diff and not once per carried check.
                    if (outgoing is not null)
                    {
                        card.IsChecked = outgoing.IsChecked;
                    }

                    // Spec 12.4's selection spine. Attached where the card is created and
                    // nowhere else: a carried card is already subscribed, and subscribing it a
                    // second time would rebuild the set twice per tick.
                    card.PropertyChanged += OnSessionCardChanged;
                    Sessions.Add(card);
                }

                // P12 R4's worse ink. One call site rather than two: this method runs inside
                // Publish, immediately after Totals was assigned, so every card added or carried
                // is compared against the row it will sit under.
                card.CompareWith(Totals);
            }

            _rebuildCards = false;

            // Whatever is left in the map is a night this load no longer has, or every card of a
            // rebuild, which the loop below disposes the same way.
            foreach (var vanished in carried.Values)
            {
                if (ReferenceEquals(vanished, SelectedSession))
                {
                    SelectedSession = null;
                }

                // In the same loop that disposes them, or the page leaks a handler per scan.
                vanished.PropertyChanged -= OnSessionCardChanged;
                vanished.Dispose();
            }

            // Spec 12.4's three terms, in order and in one expression, because two branches in
            // two places is how the fallback gets lost:
            //
            //   1. the night PAR-018's deep link asked for, when this ledger holds it;
            //   2. the night the user was on, when it survived the scan;
            //   3. the newest night, which is Sessions[0]: TargetDetailQuery returns the session
            //      overview list newest first.
            //
            // The requested date is read into a local and the field cleared in the same breath,
            // which is the "consumed once" rule: a scan-driven reload after the user has moved to
            // another night must keep the night the user is on rather than jumping back. Cleared
            // whether or not the ledger held it, so a date this target has no night for costs one
            // miss and not one per reload.
            var requestedDate = _initialSessionDate;
            _initialSessionDate = null;

            var requestedCard = requestedDate is { } requested
                ? Sessions.FirstOrDefault(card => card.SessionDate == requested)
                : null;

            var keep = requestedCard
                ?? (previousDate is { } date
                    ? Sessions.FirstOrDefault(card => card.SessionDate == date)
                    : null)
                ?? Sessions.FirstOrDefault();

            _replacingSessions = false;
            SelectedSession = keep;

            // Spec 12.4: a non-null date "scrolls the ledger to it". ScrollIntoView is a control
            // call, so the view-model publishes the card and the view does the scroll, which is
            // the seam FrameTableViewModel.HighlightedRow already established (P13 R9). Published
            // only when the requested night was actually found: a plain reload and a date this
            // target has no night for both leave the ledger's scroll where it is, and the fallback
            // lands on the newest night, which is the row the ledger already starts at.
            if (requestedCard is not null)
            {
                LedgerScrollTarget = requestedCard;
            }

            // Applied once the guard is lifted, and idempotently: the assignment above is a no-op
            // when the ListBox already wrote the same card back, and the selected night still has
            // to end up expanded so its detail query runs exactly once. Inside the bracket,
            // because expanding a card raises property changes the chart listens to and an
            // unsuspended chart would publish its series a second time.
            foreach (var card in Sessions)
            {
                card.IsExpanded = ReferenceEquals(card, SelectedSession);
            }
        }
        finally
        {
            _replacingSessions = false;
            _suspendReviewRules = false;
            TargetChart.EndUpdate();

            // After the diff, so a night the scan pruned away leaves the checked set with its
            // card and a night that survived keeps its check (spec 12.4).
            RebuildSelectedNights();
            RebuildReviewSession();
        }
    }

    private void NotifyActionState()
    {
        BeginRenameCommand.NotifyCanExecuteChanged();
        CommitRenameCommand.NotifyCanExecuteChanged();
        MergeCommand.NotifyCanExecuteChanged();
        ReResolveCommand.NotifyCanExecuteChanged();
        CopyFrameListCommand.NotifyCanExecuteChanged();
        ExportForStackingCommand.NotifyCanExecuteChanged();
        RevealFolderCommand.NotifyCanExecuteChanged();
        OpenSurveyViewCommand.NotifyCanExecuteChanged();
    }

    // A scan can add a session, change a metric, or prune the target away entirely. The page
    // reloads and ReplaceSessions diffs the session dates, so a card the user has open survives
    // the refresh and re-reads its own detail once.
    private void OnScanFinished(object? sender, EventArgs e) => Load();

    /// <summary>
    /// Phase 15B fixer F2, with items 30 and 38. A settings save or a completed correlation re-run
    /// rewrote the guiding figures this page shows, so the page reloads exactly as it does after a
    /// scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Posted, unlike <see cref="OnScanFinished"/>.</b> <c>ScanStatusService</c> has already
    /// marshalled onto the UI thread; this notification is raised on whichever thread saved the
    /// document or ran the pass, and <see cref="Load"/> writes observable properties (ruling G9).
    /// </para>
    /// <para>
    /// <b>What the reload keeps.</b> Everything a scan-driven reload keeps, and by the same
    /// mechanism: <c>ReplaceSessions</c> reads the selected night's date before the collection is
    /// emptied and re-selects that night when this load still carries it, carries the card
    /// instance itself for every night that survived, so an expanded card stays expanded and an
    /// open Guiding band stays open, and the ledger's own disclosure lives in the process-wide
    /// <c>TargetPageState</c> rather than on this page at all. Each carried card's
    /// <c>Invalidate</c> drops its loaded detail so the open band re-reads once, which is the
    /// whole point of the notification: item 38 needs no second mechanism.
    /// </para>
    /// <para>
    /// <b>Coalesced.</b> A notification arriving while a load is in flight sets
    /// <see cref="_reloadPending"/> rather than starting a second read, and the load in flight
    /// honours it once when it lands. Dropping it would be wrong rather than merely cheaper: the
    /// read in flight may have run before the handler that dropped the memos.
    /// </para>
    /// <para>
    /// It does not ask whether the page is showing. This page is transient and the shell disposes
    /// the outgoing one on every navigation, so a page that is not showing has already unfollowed.
    /// </para>
    /// </remarks>
    private void OnDerivedDataChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        Load();
    });

    // Spec 12.16: raised on the saving thread, so posted the way FrameTableViewModel posts its own
    // handler. A reload only when the clock, zone or coordinates changed (polish 2 ruling 2): every
    // other save feeds nothing but the submenus and, by the storm rule above, must not reload.
    private void OnGeneralChanged(object? sender, GeneralSettings general) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        RebuildIntegrationItems();
        RefreshSurveyDownloadsEnabled();

        var format = TimeFormatOf(general);
        if (_timeFormat is null || format == _timeFormat)
        {
            return;
        }

        _timeFormat = format;
        _rebuildCards = true;
        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        Load();
    });

    private static (string Timezone, bool Use24Hour, double? Latitude, double? Longitude) TimeFormatOf(
        GeneralSettings general)
        => (general.DisplayTimezoneId, general.Use24HTime, general.ObserverLatitude, general.ObserverLongitude);

    // Honours at most one notification that arrived while the load now landing was in flight.
    // Guarded on the generation so a response older than the newest request, which publishes
    // nothing, also starts nothing.
    private void DrainDeferredReload(int generation)
    {
        if (!_reloadPending || _disposed || generation != _generation)
        {
            return;
        }

        _reloadPending = false;
        Load();
    }

    private void DisposeCards()
    {
        // Before the cards go: a page that still named a disposed card as its selection would
        // leave the ledger bound to a view model whose query and notes field are gone.
        SelectedSession = null;
        ReviewSession = null;
        MergedSession?.Dispose();
        MergedSession = null;

        // Over a snapshot, so a handler a card raises while it is disposed cannot invalidate the loop.
        foreach (var card in Sessions.ToList())
        {
            card.PropertyChanged -= OnSessionCardChanged;
            card.Dispose();
        }

        // Cleared after the loop (phase review item 6): a disposed page that still exposes its
        // cards leaves the view bound to view-models whose queries and notes fields are gone.
        Sessions.Clear();
    }

    // ---- Phase 21: the night heading's Send to menu (spec 12.16, ruling B5) -------------------

    /// <summary>The offered NINA instances, one <see cref="IntegrationSendItemViewModel"/> per
    /// enabled instance with a name and a URL (<see cref="IntegrationInstance.IsOffered"/>), in the
    /// stored order. The night heading's "NINA" submenu binds this as its <c>ItemsSource</c> and
    /// is hidden while it is empty (spec 12.16 step 3d, ruling B5).</summary>
    public ObservableCollection<IntegrationSendItemViewModel> NinaSendItems { get; } = [];

    /// <summary>The offered Stellarium instances, same rule as <see cref="NinaSendItems"/>.</summary>
    public ObservableCollection<IntegrationSendItemViewModel> StellariumSendItems { get; } = [];

    public bool HasNinaSendItems => NinaSendItems.Count > 0;

    public bool HasStellariumSendItems => StellariumSendItems.Count > 0;

    /// <summary>The night heading's Send to button, hidden with both submenus so a page with
    /// nothing configured looks exactly as it does today (spec 12.16 step 3d).</summary>
    public bool HasSendItems => HasNinaSendItems || HasStellariumSendItems;

    /// <summary>What a send carries: the night under review's own pointing, read off its
    /// reference frame (or the first frame that recorded one) with that frame's rotator angle,
    /// because each night can be framed and rotated differently; the target's catalogue
    /// coordinates and position angle when no frame of the night recorded a pointing. Null when
    /// neither has coordinates, which is what disables every item.</summary>
    /// <param name="FromNight">True when the pointing is the night's own, which is what makes
    /// Stellarium slew to the coordinates rather than to the catalogue name.</param>
    internal readonly record struct SendPointing(double Ra, double Dec, double? Angle, bool FromNight);

    // ponytail: the items' enabled state is computed at rebuild, so a night whose frames carry a
    // pointing on a target with no catalogue coordinates stays disabled until the next rebuild.
    // Rebuild on ReviewSession's detail load if that case ever matters.
    internal SendPointing? CurrentPointing()
    {
        var detail = ReviewSession?.Detail;
        if (detail is not null)
        {
            var frame = detail.Frames.FirstOrDefault(f => f.ImageId == detail.ReferenceImageId && f is { RaDeg: not null, DecDeg: not null })
                ?? detail.Frames.FirstOrDefault(f => f is { RaDeg: not null, DecDeg: not null });
            if (frame is { RaDeg: { } frameRa, DecDeg: { } frameDec })
            {
                return new SendPointing(frameRa, frameDec, frame.RotatorPosition, true);
            }
        }

        if (Header?.Block.Ra is { } ra && Header?.Block.Dec is { } dec)
        {
            return new SendPointing(ra, dec, Header.Block.PositionAngle, false);
        }

        return null;
    }

    /// <summary>
    /// The ONE place the two submenus are built: read fresh from the stored document every publish
    /// rather than cached (core-shapes.md section 3 item 7), filtered to
    /// <see cref="IntegrationInstance.IsOffered"/>, and disabled together with the one tooltip
    /// sentence while the target carries no RA or no Dec (spec 12.16 step 3d).
    /// </summary>
    private void RebuildIntegrationItems()
    {
        var general = _getGeneral?.Invoke();
        if (general is not null)
        {
            // Memoised here, off the construction path, because every load path passes through.
            _timeFormat ??= TimeFormatOf(general);
        }

        var ninaInstances = general is null
            ? []
            : IntegrationSettings.ReadInstances(general.NinaInstancesDocument);
        var stellariumInstances = general is null
            ? []
            : IntegrationSettings.ReadInstances(general.StellariumInstancesDocument);

        var hasCoordinates = CurrentPointing() is not null;
        var toolTip = hasCoordinates ? null : "This target has no coordinates to send.";

        NinaSendItems.Clear();
        foreach (var instance in ninaInstances)
        {
            if (!IntegrationInstance.IsOffered(instance))
            {
                continue;
            }

            var captured = instance;
            NinaSendItems.Add(new IntegrationSendItemViewModel(
                captured.Name.Trim(), hasCoordinates, toolTip, () => SendNinaAsync(captured)));
        }

        StellariumSendItems.Clear();
        foreach (var instance in stellariumInstances)
        {
            if (!IntegrationInstance.IsOffered(instance))
            {
                continue;
            }

            var captured = instance;
            StellariumSendItems.Add(new IntegrationSendItemViewModel(
                captured.Name.Trim(), hasCoordinates, toolTip, () => SlewStellariumAsync(captured)));
        }

        OnPropertyChanged(nameof(HasNinaSendItems));
        OnPropertyChanged(nameof(HasStellariumSendItems));
        OnPropertyChanged(nameof(HasSendItems));
    }

    /// <summary>The kind the NINA send registers under, named here so
    /// <c>JobRegistryCensusTest</c> and the registration read one token.</summary>
    public const string NinaSendJobKind = "nina_send";

    /// <summary>The kind the Stellarium slew registers under.</summary>
    public const string StellariumSendJobKind = "stellarium_send";

    /// <summary>What one send hands back: the job outcome, the one sentence the registry and the
    /// activity row share, and that row's details object.</summary>
    private readonly record struct SendOutcome(bool Ok, string Summary, object Details);

    /// <summary>
    /// The spine of the two sends (design lesson 1): the job handle, its finish, the failure
    /// sentence and the activity row are the same either way, and only the client call, the level
    /// it logs at and the details it builds differ.
    /// </summary>
    private async Task RunSendAsync(
        string kind, string title, string failureText, Func<Task<SendOutcome>> call)
    {
        var handle = _jobs?.Begin(kind, title);
        try
        {
            var outcome = await call().ConfigureAwait(true);
            handle?.Finish(outcome.Ok ? JobResult.Succeeded : JobResult.Failed, outcome.Summary);
            handle = null;
            _emitActivity?.Invoke(
                outcome.Ok ? "info" : "warning",
                kind,
                outcome.Summary,
                outcome.Details,
                Header?.TargetId);
        }
        finally
        {
            // Nulled above once finished, so this reaches only a throw out of the client, which
            // would otherwise leave the job Running for the life of the process.
            handle?.Finish(JobResult.Failed, failureText);
        }
    }

    /// <summary>
    /// Spec 12.16's NINA send: <see cref="CurrentPointing"/>'s RA, Dec and angle, unchanged, behind
    /// <see cref="JobRegistry"/> with no cancel delegate. The exception is Task 5's to log
    /// (core-shapes.md section 1): <c>Error</c> for a failed send, <c>Warning</c> for a failed
    /// rotation, the instance name and its URL as Serilog properties, never the exception's own
    /// text in a user-visible string.
    /// </summary>
    private Task SendNinaAsync(IntegrationInstance instance)
    {
        if (CurrentPointing() is not { } pointing)
        {
            // The item is disabled for this case; the body guard repeats the rule
            // RelayCommand.Execute ignoring CanExecute makes every other command here repeat.
            return Task.CompletedTask;
        }

        return RunSendAsync(
            NinaSendJobKind,
            $"Send to NINA {instance.Name}",
            IntegrationMessages.NinaFailed,
            async () =>
            {
                var result = _ninaClient is null
                    ? new NinaSendResult(false, NinaRotation.None, IntegrationMessages.NinaFailed, null)
                    : await _ninaClient
                        .SendCoordinatesAsync(instance.Url, pointing.Ra, pointing.Dec, pointing.Angle)
                        .ConfigureAwait(true);

                if (!result.Ok)
                {
                    _logger.LogError(
                        result.Failure,
                        "NINA send to {Instance} at {BaseAddress} failed",
                        instance.Name,
                        instance.Url);
                }
                else if (result.Rotation == NinaRotation.Failed)
                {
                    _logger.LogWarning(
                        result.Failure,
                        "NINA rotation to {Instance} at {BaseAddress} failed",
                        instance.Name,
                        instance.Url);
                }

                var summary = !result.Ok
                    ? IntegrationMessages.NinaFailed
                    : result.Rotation == NinaRotation.Failed
                        ? $"Sent to NINA: {instance.Name}, rotation not applied"
                        : $"Sent to NINA: {instance.Name}";

                var rotationText = result.Rotation switch
                {
                    NinaRotation.Sent => "sent",
                    NinaRotation.Failed => "failed",
                    _ => "none",
                };

                object details = result.Ok
                    ? new { instance = instance.Name, target_id = Header?.TargetId, ok = true, rotation = rotationText }
                    : new
                    {
                        instance = instance.Name,
                        target_id = Header?.TargetId,
                        ok = false,
                        rotation = rotationText,
                        reason = result.Failure?.Message ?? result.FailureMessage,
                    };

                return new SendOutcome(result.Ok, summary, details);
            });
    }

    /// <summary>Spec 12.16's Stellarium slew, on the same rule as <see cref="SendNinaAsync"/>.
    /// The target name is the header block's own <see cref="TargetHeaderViewModel.Name"/>, and is
    /// withheld when the pointing is the night's own, so Stellarium goes to where the night was
    /// framed rather than to the catalogue object.</summary>
    private Task SlewStellariumAsync(IntegrationInstance instance)
    {
        if (CurrentPointing() is not { } pointing)
        {
            return Task.CompletedTask;
        }

        return RunSendAsync(
            StellariumSendJobKind,
            $"Slew Stellarium {instance.Name}",
            IntegrationMessages.StellariumFailed,
            async () =>
            {
                var result = _stellariumClient is null
                    ? new StellariumSlewResult(
                        false, StellariumFocus.Coordinates, IntegrationMessages.StellariumFailed, null)
                    : await _stellariumClient
                        .SlewAsync(instance.Url, pointing.Ra, pointing.Dec, pointing.FromNight ? null : Header?.Name)
                        .ConfigureAwait(true);

                if (!result.Ok)
                {
                    _logger.LogError(
                        result.Failure,
                        "Stellarium slew to {Instance} at {BaseAddress} failed",
                        instance.Name,
                        instance.Url);
                }

                var summary = result.Ok
                    ? $"Slewed Stellarium: {instance.Name}"
                    : IntegrationMessages.StellariumFailed;

                var focusText = result.Focus switch
                {
                    StellariumFocus.CatalogName => "catalog_name",
                    StellariumFocus.FullName => "full_name",
                    _ => "coordinates",
                };

                object details = result.Ok
                    ? new { instance = instance.Name, target_id = Header?.TargetId, ok = true, focus = focusText }
                    : new
                    {
                        instance = instance.Name,
                        target_id = Header?.TargetId,
                        ok = false,
                        focus = focusText,
                        reason = result.Failure?.Message ?? result.FailureMessage,
                    };

                return new SendOutcome(result.Ok, summary, details);
            });
    }

    /// <summary>
    /// Cancels the page's background work, drops its subscription, flushes an unsaved note, and
    /// disposes every card. A leaked page keeps a query alive past the window, which is the defect
    /// FIXER LIST item 9 recorded against <c>DashboardViewModel</c>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_scanStatus is not null)
        {
            _scanStatus.ScanFinished -= OnScanFinished;
        }

        // F2's other half, beside the scan unsubscribe. The notifier is a process singleton and
        // this page is transient, so a handler left on it pins every page the reader ever opened.
        _unfollowDerivedData?.Invoke();
        _unfollowGeneralChanged?.Invoke();

        // Before the lifetime is cancelled: a note typed and immediately navigated away from must
        // not be lost. Bounded, because Dispose runs on the UI thread and a locked database must
        // not hang the window; the write itself is on the thread pool.
        try
        {
            Notes.FlushAsync().Wait(FlushBudget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flushing the target note on close failed");
        }

        Notes.Dispose();
        _lifetime.Cancel();

        if (MergeHistory is not null)
        {
            MergeHistory.Undone -= OnMergeUndone;
            MergeHistory.Dispose();
        }

        // The display columns writer is a process-wide singleton on the same rule, and this page is
        // transient: a handler left on it would keep every page the reader ever opened alive.
        if (_displayColumns is not null)
        {
            _displayColumns.Changed -= OnDisplayColumnsChanged;
        }

        bool deferred;
        lock (_publishGate)
        {
            deferred = _disposeAfterPublish = _publishing > 0;
        }

        if (!deferred)
        {
            DisposePublishedState();
        }

        _lifetime.Dispose();
    }

    // Everything a publish replaces or refills, so it is disposed after any publish in flight.
    private void DisposePublishedState()
    {
        // The header's reference thumbnail holds a decoded bitmap, which is unmanaged memory.
        DisposeReferenceThumbnail();

        // Before the cards, because the chart is subscribed to them, and before the page is gone,
        // because the selection it listens to is a process-wide singleton.
        TargetChart.Dispose();
        DisposeCards();
    }

    private void DisposeReferenceThumbnail() => Header?.ReferenceThumbnail?.Dispose();

    // Long enough for a SQLite write behind one other write, short enough that a locked database
    // is a stutter rather than a hang.
    private static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(2);
}

/// <summary>
/// One offered instance on the night heading's "NINA" or "Stellarium" submenu (spec 12.16, ruling
/// B5). The submenu's <c>ItemContainerTheme</c> binds every member here on the generated
/// <c>MenuItem</c>. Enabled follows <see cref="TargetDetailViewModel.CurrentPointing"/>; the
/// instance's offered state is what puts it in the list at all
/// (<see cref="TargetDetailViewModel.NinaSendItems"/>).
/// </summary>
public sealed partial class IntegrationSendItemViewModel : ObservableObject
{
    private readonly Func<Task> _send;

    internal IntegrationSendItemViewModel(string name, bool isEnabled, string? toolTip, Func<Task> send)
    {
        Name = name;
        IsEnabled = isEnabled;
        ToolTipText = toolTip;
        _send = send;
        SendCommand = new AsyncRelayCommand(_send);
    }

    /// <summary>The instance name, trimmed. The submenu's own header already names the client, so
    /// this is the whole of the leaf item's text.</summary>
    public string Name { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>"This target has no coordinates to send." while disabled; null while enabled.</summary>
    [ObservableProperty]
    public partial string? ToolTipText { get; set; }

    public IAsyncRelayCommand SendCommand { get; }
}

/// <summary>
/// One heading of spec 12.15's ledger custom column strip: the column's name, and the width its
/// cells take on every night row.
/// </summary>
/// <remarks>
/// The width travels with the name because the header strip and the night strips are three
/// separate <c>ItemsControl</c>s inside one shared-size column, so nothing but an agreed per-cell
/// width lines the second heading up with the second cell. The figures come from
/// <see cref="TargetDetailViewModel.CustomCellWidth"/>, which forwards to
/// <c>CustomCellWidths</c>, their one home for this page and for the dashboard alike.
/// </remarks>
public sealed record LedgerCustomHeadingViewModel(string Name, double Width);
