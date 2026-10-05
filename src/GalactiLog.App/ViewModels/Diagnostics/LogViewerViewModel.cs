using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Diagnostics;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Diagnostics;

/// <summary>
/// Spec 12.8's log viewer: a virtualized, keyset-paged list over the current Serilog file and the
/// retained rolled files, with a minimum level filter, free-text search, follow tail, copy
/// selection, copy all, open log folder, and the <c>general.log_level</c> selector.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two level pickers, and they are not the same control.</b> <see cref="MinimumLevel"/> is the
/// view filter, labelled "Show": it narrows what is displayed from what was already captured.
/// <see cref="CaptureLevel"/> is <c>general.log_level</c>, labelled "Capture": it changes what
/// Serilog records from now on. Conflating them is the defect this page is most likely to grow,
/// which is why the view renders them apart and
/// <c>LogViewerViewTests.View_HasTwoSeparateLevelPickers</c> pins it.
/// </para>
/// <para>
/// The paging contract is <c>ActivityQuery</c>'s, reused rather than reinvented (HANDOFF section 5
/// note 3, design-lessons rule 1): the same two-part predicate, the same newest-first ordering,
/// the same "a cursor only when the page came back exactly full" rule, and the same limit
/// constants. See <see cref="LogCursor"/>.
/// </para>
/// <para>
/// Every read is blocking file IO, so every load runs inside <c>Task.Run</c> and publishes through
/// the dispatcher seam. The constructor runs no read: the first load is kicked through
/// <see cref="RefreshCommand"/>, for the reason <c>DiagnosticsViewModel</c>'s constructor gives.
/// </para>
/// </remarks>
public sealed partial class LogViewerViewModel : ObservableObject, IDisposable
{
    /// <summary>The page size, which is <c>ActivityQuery.DefaultLimit</c> through
    /// <see cref="LogReader.DefaultLimit"/>.</summary>
    public const int PageSize = LogReader.DefaultLimit;

    /// <summary>Ruling Q26: "copy all" is capped at 50,000 entries, newest first, matching the
    /// web's <c>_DOWNLOAD_CAP</c>. An uncapped copy would put hundreds of megabytes on the
    /// clipboard and block the paste.</summary>
    public const int DefaultCopyAllCap = 50_000;

    /// <summary>Spec 12.10's empty state when the log directory holds no file at all.</summary>
    public const string NoFilesMessage = "No log files yet.";

    /// <summary>Spec 12.10's log viewer empty state, verbatim: files exist and the level filter
    /// excluded every entry in them.</summary>
    public const string NoEntriesAtLevelMessage = "No log entries at this level.";

    /// <summary>Spec 12.10's empty state when files exist and the search matched nothing.
    /// </summary>
    public const string NoSearchMatchMessage = "No entries match this search.";

    /// <summary>Reported in place of an empty state when the read itself threw. A failed read
    /// taught nothing about whether the log is empty (spec 12.10's rule).</summary>
    public const string ReadFailedMessage = "The log files could not be read.";

    /// <summary>Spec 12.8's viewer cap message (PAR-012): paging older stopped because the loaded
    /// count reached <c>general.app_log_max_rows</c>, not because there is no more log. The fifth
    /// message beside the four above, added here rather than reusing one of them so the reason on
    /// screen is never misleading.</summary>
    public const string ViewerCapReachedMessage =
        "Loaded rows reached the row cap. Older entries are not shown.";

    /// <summary>Task 7's three retention editors (PAR-012, spec 12.7): what the activity retention
    /// and viewer row cap editors state beside themselves. Both take effect with no restart.
    /// </summary>
    public const string AppliesImmediatelyText = "Applies immediately.";

    /// <summary>What the log retention editor states beside itself. The sink's
    /// <c>retainedFileCountLimit</c> is fixed at startup (design-spec 16.1, <c>questions.md</c>
    /// Q7), so a change here is read the next time GalactiLog starts, not at the next roll and not
    /// at once. This task does not rebuild the logger to force it.</summary>
    public const string AppliesAtNextStartText = "Applies the next time GalactiLog starts.";

    /// <summary>
    /// Ruling Q29: the follow-tail poll interval. The Serilog sink's <c>flushToDiskInterval</c> is
    /// one second, so a poll faster than that reads the same bytes twice; two seconds is one flush
    /// of headroom.
    /// </summary>
    public static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(2);

    /// <summary>The six levels in ascending order, which is the order both pickers render.
    /// </summary>
    public static readonly IReadOnlyList<LogLineLevel> AllLevels =
        [.. Enum.GetValues<LogLineLevel>().OrderBy(level => level)];

    /// <summary>The same debounce window every other search box in this application uses, taken
    /// from the Activity page rather than redeclared, so the two cannot drift apart.</summary>
    internal static readonly TimeSpan DebounceWindow = ActivityViewModel.DebounceWindow;

    private readonly LogReader _reader;
    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> _mutateGeneral;
    private readonly ShellIntegration _shell;
    private readonly string _logDirectory;
    private readonly int _copyAllCap;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;
    private readonly AppWriter? _appWriter;
    private readonly Action<string, string>? _exportLog;

    // The last known-good stored value for each of the three editors, so a refused edit reverts
    // to what is actually on disk rather than to a compile-time default (section 7.1).
    private int _storedActivityRetentionDays;
    private int _storedAppLogRetentionDays;
    private int _storedAppLogMaxRows;

    // One page-lifetime source every background window is linked to, the shape ActivityViewModel
    // established: this page is reached from a DI singleton, so a load or a follow tick started
    // just before shutdown must not keep running afterwards.
    private readonly CancellationTokenSource _lifetime = new();

    // The search window, on the shared Debouncer rather than a hand-rolled copy of
    // cancel-previous, link-to-lifetime, await-the-window.
    private readonly Debouncer _searchWindow;

    // The open follow-tail loop's source, or null when follow tail is off.
    private CancellationTokenSource? _follow;

    // Only the newest load may write to the bindings. The token alone is not enough: a load that
    // already finished can still be sitting in the dispatcher queue when a newer one is requested.
    private int _generation;

    // False until the constructor has finished seeding CaptureLevel from the stored document, so
    // seeding the picker does not write the value straight back into settings.
    private bool _capturesToSettings;

    // False until the constructor has finished seeding the three retention/cap editors, and set
    // true again around a revert-to-stored assignment, so neither seeding nor reverting writes
    // the value straight back into settings (the same shape _capturesToSettings uses for one
    // property, generalized to three).
    private bool _seedingEditors;

    private bool _hasLoaded;
    private bool _disposed;

    /// <param name="reader">The one reader of the log files (collision map: a second one would
    /// open the live sink's file with the wrong share mode).</param>
    /// <param name="mutateGeneral">The one door into the general document,
    /// <c>SettingsStore.MutateGeneral</c>, taken as a delegate rather than as the store itself:
    /// design-spec 18.3's rule and the shape every Settings tab already follows
    /// (<c>GeneralSettingsTabViewModel</c>, <c>DisplayTabViewModel</c> and the four preference
    /// tabs). <see cref="CaptureLevel"/> writes <c>general.log_level</c> through this and through
    /// nothing else (TRACKING section 6 item 14). Phase review minor P9: this page was the one
    /// view-model in the solution holding the store, and the store being sealed is a reason for
    /// what a test passes, not for the constructor signature. The fixture still passes
    /// <c>SettingsStore.MutateGeneral</c> over a temp database.</param>
    /// <param name="shell">The only process launcher and clipboard in this application
    /// (spec 2.1). Nothing here composes a path or starts a process of its own.</param>
    /// <param name="logDirectory">The rolling log directory, which <c>AppHost</c> resolves from
    /// <c>DiagnosticsService.LogDirectoryName</c> under the authorized app data root.</param>
    /// <param name="initialCaptureLevel">The stored <c>general.log_level</c>, read once by
    /// <c>AppHost</c> and passed by value, so the picker does not open a SQLite read on the UI
    /// thread at construction.</param>
    /// <param name="post">The dispatcher seam every view-model takes; defaults to
    /// <c>UiPost.Default</c>.</param>
    /// <param name="delay">The one delay seam, shared by the search debounce and the follow-tail
    /// loop, so a test drives both instead of sleeping. Normally <c>Task.Delay</c>.</param>
    /// <param name="copyAllCap">The "copy all" bound. Defaults to ruling Q26's
    /// <see cref="DefaultCopyAllCap"/>; a test passes a small value so the cap and its notice are
    /// asserted without writing fifty thousand fixture entries.</param>
    /// <param name="logger">A failed read or copy is logged, never rethrown on the UI thread.
    /// </param>
    /// <param name="initialActivityRetentionDays">The stored <c>general.activity_retention_days</c>,
    /// read once by <c>AppHost</c> and passed by value, the same shape
    /// <paramref name="initialCaptureLevel"/> already uses.</param>
    /// <param name="initialAppLogRetentionDays">The stored <c>general.app_log_retention_days</c>.
    /// </param>
    /// <param name="initialAppLogMaxRows">The stored <c>general.app_log_max_rows</c>. Also the
    /// viewer cap in force from construction: <see cref="MaxRows"/> is read live by
    /// <see cref="LoadMoreAsync"/>, not captured once, so a later edit takes effect on the next
    /// load with no restart (spec 12.8).</param>
    /// <param name="saveLogAsDestinationPicker">The initial value of
    /// <see cref="SaveLogAsDestinationPicker"/>. Trailing and optional so no existing construction
    /// site moves; <c>LogViewerView</c>'s code-behind installs the real one when it attaches.
    /// </param>
    /// <param name="exportLog">Normally <c>DiagnosticsService.ExportLog</c>, taken as a delegate for
    /// the same reason <see cref="DiagnosticsViewModel"/> takes <c>exportBundle</c>: the service is
    /// sealed and non-virtual, and <c>FileSafetyTest</c>'s <c>BeginExportAllowlist</c> forbids a
    /// second caller of <c>AppWriter.BeginExport</c> anywhere but that class
    /// (<c>questions.md</c> Q8). Null disables <see cref="SaveLogAsCommand"/>.</param>
    /// <param name="appWriter">Authorizes <see cref="ClearLogCommand"/>'s deletes under the app
    /// data root. <c>AppWriter.Delete</c> is not in <c>FileSafetyTest</c>'s
    /// <c>BeginExportAllowlist</c> and needs no entry there: the allowlist gates
    /// <c>BeginExport</c> callers, and <c>Delete</c> trips no pattern (section 6.4). Null disables
    /// <see cref="ClearLogCommand"/>.</param>
    public LogViewerViewModel(
        LogReader reader,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        ShellIntegration shell,
        string logDirectory,
        LogLineLevel initialCaptureLevel,
        Action<Action>? post = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int copyAllCap = DefaultCopyAllCap,
        ILogger? logger = null,
        int initialActivityRetentionDays = 90,
        int initialAppLogRetentionDays = 14,
        int initialAppLogMaxRows = 50_000,
        Func<Task<string?>>? saveLogAsDestinationPicker = null,
        Action<string, string>? exportLog = null,
        AppWriter? appWriter = null)
    {
        _reader = reader;
        _mutateGeneral = mutateGeneral;
        _shell = shell;
        _logDirectory = logDirectory;
        _copyAllCap = copyAllCap;
        _post = post ?? UiPost.Default;
        _delay = delay ?? Task.Delay;
        _logger = logger ?? NullLogger.Instance;
        _searchWindow = new Debouncer(_lifetime.Token, _delay, DebounceWindow);
        _exportLog = exportLog;
        _appWriter = appWriter;
        SaveLogAsDestinationPicker = saveLogAsDestinationPicker;

        CaptureLevel = initialCaptureLevel;
        _capturesToSettings = true;

        // Seeded through the same guard the three editors' write-through uses, so construction
        // does not write these straight back into settings (section 7.1's revert shape).
        _seedingEditors = true;
        _storedActivityRetentionDays = initialActivityRetentionDays;
        _storedAppLogRetentionDays = initialAppLogRetentionDays;
        _storedAppLogMaxRows = initialAppLogMaxRows;
        RetentionDays = initialActivityRetentionDays;
        AppLogRetentionDays = initialAppLogRetentionDays;
        MaxRows = initialAppLogMaxRows;
        _seedingEditors = false;

        // Not a read: the first load is scheduled through the dispatcher seam and then runs on the
        // pool, because blocking file IO in a constructor on the UI thread is the defect
        // TRACKING item 16 exists to prevent.
        _post(() => RefreshCommand.Execute(null));
    }

    /// <summary>The entries on screen, newest first. A refresh replaces them; "Load more"
    /// appends.</summary>
    public ObservableCollection<LogLineViewModel> Lines { get; } = [];

    /// <summary>
    /// The keyset position of each entry in <see cref="Lines"/>, index for index.
    /// </summary>
    /// <remarks>
    /// Verification B2 residual. <see cref="LogLineViewModel"/> is a formatted record whose
    /// equality <see cref="Selected"/> depends on, so the cursor cannot live on it: an extra
    /// member would join that equality. Kept beside the list instead, and the invariant is that
    /// <see cref="Lines"/> is mutated in exactly two places, <c>Publish</c> and
    /// <c>OnMaxRowsChanged</c>, and both keep this in step. It exists so a trim can say where
    /// paging should resume, which is what makes the cap sentence true after one and what lets a
    /// later raise fetch the trimmed lines back instead of re-reading page one.
    /// </remarks>
    private readonly List<LogCursor> _cursors = [];

    /// <summary>The six levels, ascending, for both pickers.</summary>
    public IReadOnlyList<LogLineLevel> Levels => AllLevels;

    /// <summary>
    /// The <b>view</b> filter, spec 12.8's "minimum level filter", labelled "Show" in the view.
    /// It narrows what is displayed from what was already captured and changes nothing about what
    /// Serilog records. Changing it reloads from the first page.
    /// </summary>
    [ObservableProperty]
    public partial LogLineLevel MinimumLevel { get; set; }

    /// <summary>
    /// <c>general.log_level</c>, labelled "Capture" in the view. It changes what Serilog records
    /// from now on and nothing about what is already on screen. Spec 12.7: this selector "appears
    /// nowhere else: it is a troubleshooting control, not a preference".
    /// </summary>
    /// <remarks>
    /// The setter writes through <c>SettingsStore.MutateGeneral</c>, which raises
    /// <c>GeneralChanged</c>, which <c>AppHost</c>'s existing handler turns into
    /// <c>levelSwitch.MinimumLevel = ParseLevel(general.LogLevel)</c>. That is the whole "takes
    /// effect with no restart" path (spec 16.1); this page adds the control, not the wiring.
    /// <c>value.ToString()</c> is exact because the enum member names are spec 5.8.1's values.
    /// </remarks>
    [ObservableProperty]
    public partial LogLineLevel CaptureLevel { get; set; }

    /// <summary>Spec 12.8's free-text search, matched against the message only (ruling Q27).
    /// Debounced on the application's one shared window before it reloads.</summary>
    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>Spec 12.8's follow-tail toggle. While true, page one is re-read every
    /// <see cref="FollowInterval"/>. While false nothing reloads on its own, so a reader who has
    /// scrolled keeps their place.</summary>
    [ObservableProperty]
    public partial bool FollowTail { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Spec 12.10's empty state: a load has completed and nothing matched.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Which empty-state sentence to render. One of the four constants above.</summary>
    [ObservableProperty]
    public partial string EmptyMessage { get; private set; } = NoFilesMessage;

    /// <summary>The selected entry, or null. "Copy selection" copies its raw text.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectionCommand))]
    public partial LogLineViewModel? Selected { get; set; }

    /// <summary>Non-null while there is an older page to fetch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial LogCursor? NextCursor { get; private set; }

    /// <summary>What the "Load more" button's visibility binds to.</summary>
    /// <remarks>
    /// Verification B2 (steps 32 and 33): the cap is a term here, so the button and
    /// <see cref="ViewerCapReachedMessage"/> are mutually exclusive. It used to read the cursor
    /// alone, so at the cap the viewer offered a button whose every press
    /// <see cref="LoadMoreAsync"/>'s own body refused, beside a sentence saying why.
    /// </remarks>
    public bool CanLoadMore => NextCursor is not null && Lines.Count < MaxRows;

    /// <summary>Spec 12.8's viewer cap (PAR-012): there is a further page (<see cref="NextCursor"/>
    /// is not null) but paging stopped because <see cref="Lines"/> reached <see cref="MaxRows"/>.
    /// The view binds <see cref="ViewerCapReachedMessage"/>'s visibility to this rather than to
    /// <see cref="CanLoadMore"/>, so the reason on screen is never "no more log" when there is
    /// more.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    public partial bool IsAtViewerCap { get; private set; }

    /// <summary>Task 7's three retention editors (PAR-012, spec 12.7). <c>RetentionDays</c> is
    /// <c>general.activity_retention_days</c>, 1 to 3650; the only editor for that key anywhere in
    /// the application today (section 7.1). Each writes through <see cref="OnRetentionDaysChanged"/>
    /// and the two below it, the same immediate-write-and-revert shape
    /// <see cref="OnCaptureLevelChanged"/> already uses, rather than <c>AutosaveField</c>: these are
    /// bounded numeric values with a synchronous range check, not free text on a debounce window,
    /// and the revert-on-refuse behaviour spec 12.7 asks for is exactly what seeding this property
    /// back to the stored value already does.</summary>
    [ObservableProperty]
    public partial int RetentionDays { get; set; }

    /// <summary><c>general.app_log_retention_days</c>, 1 to 3650. States
    /// <see cref="AppliesAtNextStartText"/> beside itself: the Serilog sink's
    /// <c>retainedFileCountLimit</c> is read once at startup (section 5.3, <c>questions.md</c>
    /// Q7), and this task does not rebuild the logger to force it.</summary>
    [ObservableProperty]
    public partial int AppLogRetentionDays { get; set; }

    /// <summary><c>general.app_log_max_rows</c>, 1000 to 500000. Also the live viewer cap:
    /// <see cref="LoadMoreAsync"/> reads this property, not a value captured at construction, so a
    /// successful edit takes effect on the next load with no restart, and lowering it trims the
    /// oldest loaded lines (the tail of <see cref="Lines"/>, which is newest-first) rather than
    /// waiting for the next load to catch up. Does not resize <c>LogRingBuffer</c>'s fixed
    /// 500-entry warning ring (departure 2): that ring feeds the Diagnostics Errors group and this
    /// key governs the viewer and the copy cap only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    public partial int MaxRows { get; set; }

    /// <summary>The "copy all" bound in force (ruling Q26).</summary>
    public int CopyAllCap => _copyAllCap;

    /// <summary>The "copy all" tooltip, derived from <see cref="CopyAllCap"/> rather than spelled
    /// again in the markup, so a page built with a different cap cannot show a tooltip that lies
    /// (review finding M3).</summary>
    public string CopyAllTooltip => string.Create(
        CultureInfo.InvariantCulture,
        $"Copies every matching entry, newest first, up to {_copyAllCap:N0}");

    /// <summary>The follow-tail tooltip, derived from <see cref="FollowInterval"/> for the same
    /// reason.</summary>
    public string FollowTailTooltip => string.Create(
        CultureInfo.InvariantCulture,
        $"Re-reads the newest entries every {FollowInterval.TotalSeconds:0} seconds");

    /// <summary>
    /// Opens the platform save dialog and returns the chosen absolute path, or null. Settable
    /// rather than constructor-only, the same shape <c>DiagnosticsViewModel.DestinationPicker</c>
    /// uses: this page is a DI singleton built before any view exists, so <c>LogViewerView</c>'s
    /// code-behind installs the real picker when it attaches and clears it when it detaches
    /// (section 6.2). Null disables <see cref="SaveLogAsCommand"/>.
    /// </summary>
    public Func<Task<string?>>? SaveLogAsDestinationPicker { get; set; }

    /// <summary>Spec 12.8's Clear log notice (PAR-016): named before the click does anything, with
    /// no typed confirmation (spec-writer question 5, ruled as written). Updated on every load, so
    /// it always states what a click would delete; updated again after a click, to report what it
    /// deleted. Null before the first load completes.</summary>
    [ObservableProperty]
    public partial string? ClearLogNotice { get; private set; }

    /// <summary>The in-flight load, so a test awaits it instead of blocking on it (TRACKING
    /// section 2 item 8).</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>The in-flight debounce-and-reload opened by the search box.</summary>
    internal Task? PendingSearch { get; private set; }

    /// <summary>The open follow-tail loop, or null.</summary>
    internal Task? PendingFollow { get; private set; }

    /// <summary>How many reads have been started. A test seam: "reloaded once" and "did not
    /// reload" are otherwise unobservable, because <c>LogReader</c> is sealed with non-virtual
    /// members and there is nothing to count calls on.</summary>
    internal int LoadCount { get; private set; }

    /// <summary>Re-reads the first page.</summary>
    /// <remarks>
    /// The already-loading guard is repeated in the body and not left to <see cref="CanRefresh"/>,
    /// because <c>RelayCommand.Execute</c> ignores <c>CanExecute</c> (TRACKING section 6 item 13).
    /// The command takes no <c>CancellationToken</c>: cancellation is not offered here.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync()
        => _disposed || IsLoading ? Task.CompletedTask : Load(fromStart: true);

    private bool CanRefresh() => !IsLoading && !_disposed;

    /// <summary>Appends the next keyset page and advances the cursor.</summary>
    /// <remarks>
    /// Both guards are repeated in the body because <c>RelayCommand.Execute</c> ignores
    /// <c>CanExecute</c>. A run with no cursor would re-fetch page one and append it to itself; a
    /// run while another load is in flight would interleave two pages into one list. Spec 12.8's
    /// viewer cap (PAR-012) is a third: paging stops when <see cref="Lines"/> reaches
    /// <see cref="MaxRows"/>, and <see cref="IsAtViewerCap"/> is already true by the time this can
    /// be pressed again, because <see cref="Publish"/> computes it on every load.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanLoadMoreNow))]
    private Task LoadMoreAsync()
        => _disposed || IsLoading || NextCursor is null || Lines.Count >= MaxRows
            ? Task.CompletedTask
            : Load(fromStart: false);

    private bool CanLoadMoreNow()
        => !_disposed && !IsLoading && NextCursor is not null && Lines.Count < MaxRows;

    /// <summary>Spec 12.8's "copy selection": the selected entry exactly as the file holds it,
    /// its exception and stack frames included.</summary>
    /// <remarks>The selection guard is repeated in the body because <c>Execute</c> ignores
    /// <c>CanExecute</c>.</remarks>
    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private async Task CopySelectionAsync()
    {
        if (_disposed || Selected is not { } selected)
        {
            return;
        }

        await _shell.CopyTextAsync(selected.Raw).ConfigureAwait(false);
    }

    private bool CanCopySelection() => !_disposed && Selected is not null;

    /// <summary>
    /// Spec 12.8's "copy all": every entry matching the current filters, newest first, bounded by
    /// ruling Q26's cap. When the cap was reached the copied text says so on its first line, so a
    /// truncated copy is never silently truncated.
    /// </summary>
    [RelayCommand]
    private async Task CopyAllAsync()
    {
        if (_disposed)
        {
            return;
        }

        var filters = CurrentFilters(Search);
        var token = _lifetime.Token;

        IReadOnlyList<LogLine> lines;
        try
        {
            // Off the UI thread: this can walk every retained file.
            lines = await Task.Run(() => _reader.All(filters, _copyAllCap), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Reading the log files for a copy failed");
            return;
        }

        // The clipboard is UI-thread affine: AppHost binds ShellIntegration's copy delegate to
        // MainWindow.Clipboard.SetTextAsync, which reads a TopLevel and calls a platform
        // clipboard. Every other call site in this application reaches CopyTextAsync with no
        // preceding await and therefore on the UI thread; this one has just come off the pool, so
        // the write goes back through the dispatcher seam (review finding I1). Awaited through a
        // completion source, never blocked on, so the command's task does not complete before the
        // copy has.
        var text = Compose(lines);
        var copied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() => CopyOnUiThread(text, copied));
        await copied.Task.ConfigureAwait(false);
    }

    // Runs on the UI thread through the post seam. ShellIntegration.CopyTextAsync catches and logs
    // every failure of the clipboard itself, so the only thing that can throw here is reaching the
    // clipboard at all, and a copy that could not happen must not fault the command.
    private void CopyOnUiThread(string text, TaskCompletionSource completion)
    {
        try
        {
            _shell.CopyTextAsync(text)
                .ContinueWith(_ => completion.TrySetResult(), TaskScheduler.Default);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Copying the log to the clipboard failed");
            completion.TrySetResult();
        }
    }

    /// <summary>
    /// Spec 12.8's "open log folder in Explorer". Ruling Q28: the newest log file is revealed, so
    /// the folder opens with that file selected; with no file yet, the directory is opened.
    /// </summary>
    /// <remarks>
    /// The Windows guard is repeated in the body because <c>Execute</c> ignores
    /// <c>CanExecute</c>. The enumeration is a directory listing of at most fifteen names, which
    /// is why this one action reads on the calling thread rather than through <c>Task.Run</c>.
    /// Nothing here composes a path or starts a process: <c>ShellIntegration</c> is the only
    /// launcher in this application.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanOpenLogFolder))]
    private void OpenLogFolder()
    {
        if (_disposed || !ShellIntegration.IsWindowsShellAvailable)
        {
            return;
        }

        var files = LogFileSet.Newest(_logDirectory);
        if (files.Count > 0)
        {
            _shell.RevealInExplorer(files[0]);
        }
        else
        {
            _shell.OpenWithDefaultApplication(_logDirectory);
        }
    }

    private bool CanOpenLogFolder() => !_disposed && ShellIntegration.IsWindowsShellAvailable;

    /// <summary>
    /// Spec 12.8's "Save log as" (PAR-016). Departure 11: writes the lines the viewer currently
    /// holds after the minimum level filter and the search, oldest first, bounded by the viewer
    /// cap already applied to <see cref="Lines"/>. Not a fresh <c>_reader.All(...)</c> read: spec
    /// 12.8 says "currently holds", which <see cref="CopyAllAsync"/>'s re-read is not.
    /// </summary>
    /// <remarks>
    /// The guards are repeated in the body because <c>RelayCommand.Execute</c> ignores
    /// <c>CanExecute</c>. Reached through <c>DiagnosticsService.ExportLog</c> by delegate
    /// (<c>questions.md</c> Q8, section 6.1): <c>FileSafetyTest</c>'s <c>BeginExportAllowlist</c>
    /// forbids a second caller of <c>AppWriter.BeginExport</c> anywhere but
    /// <c>DiagnosticsService</c>. An <c>IOException</c> or <c>UnauthorizedPathException</c> from
    /// the export propagates out of this command; there is no retry, the same rule
    /// <c>DiagnosticsService.ExportBundle</c>'s own doc states.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSaveLogAs))]
    private async Task SaveLogAsAsync()
    {
        if (_disposed || SaveLogAsDestinationPicker is not { } pick || _exportLog is not { } exportLog)
        {
            return;
        }

        // Phase 14B fixer, fixer list item 10 (phase review P3-3). Composed BEFORE the picker is
        // awaited. Lines is a dispatcher-affine ObservableCollection that a follow-tail tick
        // clears and repopulates every two seconds, and the picker's await is
        // ConfigureAwait(false), so the continuation is not promised the UI thread: this read used
        // to work only because LogViewerView's own picker happens to await with
        // ConfigureAwait(true). It is also the more faithful reading of spec 12.8's "the lines the
        // viewer currently holds", which is the set the reader saw when they pressed the button
        // rather than whatever the tail had scrolled to by the time they chose a path.
        //
        // Departure 11: oldest first, the reverse of Lines' own newest-first order and the reverse
        // of Compose's clipboard order, written deliberately rather than by accident.
        var contents = string.Join(Environment.NewLine, Lines.Reverse().Select(line => line.Raw));

        var destination = await pick().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        await Task.Run(() => exportLog(destination, contents)).ConfigureAwait(false);
    }

    private bool CanSaveLogAs() => !_disposed && SaveLogAsDestinationPicker is not null;

    /// <summary>
    /// Spec 12.8's "Clear log" (PAR-016): deletes the rolled Serilog files under the app data log
    /// directory through <c>AppWriter.Delete</c>, and nothing else. The file the sink currently
    /// holds open is identified by name (today's date prefix, the newest-sorted match) and excluded
    /// deliberately rather than left to the operating system's share lock, per section 6.4.
    /// </summary>
    /// <remarks>
    /// The guard is repeated in the body because <c>RelayCommand.Execute</c> ignores
    /// <c>CanExecute</c>. No typed confirmation (spec-writer question 5, ruled as written):
    /// <see cref="ClearLogNotice"/> already named what this would delete before the click.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanClearLog))]
    private async Task ClearLogAsync()
    {
        if (_disposed || _appWriter is not { } appWriter)
        {
            return;
        }

        var appWriterLocal = appWriter;
        int removed = 0;
        long bytes = 0;
        try
        {
            await Task.Run(() =>
            {
                // Off the UI thread: the listing and every delete below are blocking file IO.
                var (rolled, _) = IdentifyLogFiles();
                foreach (var file in rolled)
                {
                    long length = 0;
                    try
                    {
                        length = new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                        // The count and the bytes are best-effort: a file that vanished between
                        // the listing and the delete is not a failure of this action.
                    }

                    appWriterLocal.Delete(file);
                    removed++;
                    bytes += length;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Clearing the rolled log files failed");
        }

        // The reload runs first, so its own Publish has already recomputed ClearLogNotice from
        // the post-delete reality (the rolled files are gone) before the report below overwrites
        // it one last time: the report is what the reader sees, not what the reload's own
        // "nothing to clear now" notice would otherwise leave on screen.
        //
        // Verification B4, the root cause of the first pass's unexplained exit. The await above
        // resumes on a thread-pool thread, and Load writes IsLoading and LoadCount, both
        // observable and IsLoading carrying [NotifyCanExecuteChangedFor]. Calling RefreshAsync
        // straight from here therefore raised CanExecuteChanged off the dispatcher, a bound Button
        // read its Command property in the handler, and Avalonia threw
        // "Call from invalid thread" with nothing above it to catch: the process went with it, on
        // every press. The reload is STARTED on the UI thread through the same post seam every
        // other write in this class uses, and then awaited from here.
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() => started.TrySetResult(RefreshAsync()));
        var reload = await started.Task.ConfigureAwait(false);
        await reload.ConfigureAwait(false);

        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            ClearLogNotice = DescribeCleared(removed, bytes);
            cleared.TrySetResult();
        });
        await cleared.Task.ConfigureAwait(false);
    }

    private bool CanClearLog() => !_disposed && _appWriter is not null;

    // Excludes the file the sink currently holds open rather than relying on its share lock
    // (AppHost's file sink opens with shared: false). The open file is the greatest date plus
    // within-day sequence encoded in the name, which is what LogFileSet.Live reads. Every other
    // file is rolled and safe to delete.
    //
    // Phase 14B fixer, fixer list item 44 (task7-review P3). This used to find the live file by
    // today's date prefix, which is wrong between midnight and the first log event of the new
    // day: the sink rolls on the next write, not on the clock, so in that window every file
    // including the open one was treated as rolled, AppWriter.Delete threw IOException on it, the
    // outer catch swallowed the throw, the loop abandoned the remaining deletes and the report
    // presented the partial count as a success. The ordering argument above was already the real
    // rule; the date match was a second, weaker statement of it.
    private (IReadOnlyList<string> Rolled, string? Live) IdentifyLogFiles()
    {
        // Verification E1: LogFileSet.Live reads the sink's own naming rather than taking the
        // first element of this list. Position was right only while the list came from
        // LogFileSet.Newest and Newest kept its ordering, and this is a DELETE: everything not
        // named here is removed, so the identification must not depend on an ordering the caller
        // could change.
        var files = LogFileSet.Newest(_logDirectory);
        var live = LogFileSet.Live(files);
        var rolled = live is null
            ? files
            : [.. files.Where(file => !string.Equals(file, live, StringComparison.OrdinalIgnoreCase))];
        return (rolled, live);
    }

    private static string DescribeCleared(int removed, long bytes) => removed switch
    {
        0 => "No rolled log files to clear.",
        1 => $"Cleared 1 rolled log file, {MetricText.Bytes(bytes)}.",
        _ => $"Cleared {removed:N0} rolled log files, {MetricText.Bytes(bytes)}.",
    };

    // Runs off the UI thread: a directory listing plus a handful of FileInfo reads. Updated on
    // every load (spec 12.8's "updated when the viewer loads"), so ClearLogNotice always states
    // what a click would delete before the click happens.
    private string DescribeWouldClear()
    {
        var (rolled, _) = IdentifyLogFiles();
        if (rolled.Count == 0)
        {
            return "No rolled log files to clear.";
        }

        long bytes = 0;
        foreach (var file in rolled)
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // Best-effort: a file that vanished since the listing does not fail the notice.
            }
        }

        return rolled.Count == 1
            ? $"Clear log deletes 1 rolled log file, {MetricText.Bytes(bytes)}."
            : $"Clear log deletes {rolled.Count:N0} rolled log files, {MetricText.Bytes(bytes)}.";
    }

    // A view-filter change re-runs the initial load: the cursor belongs to the previous filter's
    // result and means nothing under a new one.
    partial void OnMinimumLevelChanged(LogLineLevel value)
    {
        if (!_disposed)
        {
            Load(fromStart: true);
        }
    }

    // general.log_level, through the one door into the general document. Synchronous: it is a
    // single small SQLite write behind a picker, the same shape every Settings tab uses, and the
    // live-reload half is AppHost's existing GeneralChanged handler.
    partial void OnCaptureLevelChanged(LogLineLevel value)
    {
        if (!_capturesToSettings || _disposed)
        {
            return;
        }

        try
        {
            _mutateGeneral(general => general with { LogLevel = value.ToString() });
        }
        catch (Exception exception)
        {
            // A settings write that failed must not take the page down. The picker keeps the value
            // the user chose; the next write retries it.
            _logger.LogWarning(exception, "Writing general.log_level failed");
        }
    }

    // general.activity_retention_days. Immediate write-and-revert, the same shape
    // OnCaptureLevelChanged uses: this page is a numeric box with a synchronous range check, not
    // free text on a debounce window, so AutosaveField's one-second idle window buys nothing here
    // (section 7.1).
    partial void OnRetentionDaysChanged(int value)
    {
        if (_seedingEditors || _disposed)
        {
            return;
        }

        if (value is < 1 or > 3650)
        {
            SeedProperty(() => RetentionDays = _storedActivityRetentionDays);
            return;
        }

        try
        {
            var next = _mutateGeneral(general => general with { ActivityRetentionDays = value });
            _storedActivityRetentionDays = next.ActivityRetentionDays;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Writing general.activity_retention_days failed");
            SeedProperty(() => RetentionDays = _storedActivityRetentionDays);
        }
    }

    // general.app_log_retention_days. Same shape as OnRetentionDaysChanged; the sink itself is not
    // rebuilt (section 5.3, questions.md Q7).
    partial void OnAppLogRetentionDaysChanged(int value)
    {
        if (_seedingEditors || _disposed)
        {
            return;
        }

        if (value is < 1 or > 3650)
        {
            SeedProperty(() => AppLogRetentionDays = _storedAppLogRetentionDays);
            return;
        }

        try
        {
            var next = _mutateGeneral(general => general with { AppLogRetentionDays = value });
            _storedAppLogRetentionDays = next.AppLogRetentionDays;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Writing general.app_log_retention_days failed");
            SeedProperty(() => AppLogRetentionDays = _storedAppLogRetentionDays);
        }
    }

    // general.app_log_max_rows. Same write-and-revert shape, plus the two live effects spec 12.8
    // asks for on a successful edit: the viewer cap changes for the next load (MaxRows is read
    // live by LoadMoreAsync, not captured), and lowering it trims the oldest loaded lines now
    // rather than waiting for the next load to catch up (Lines is newest-first, so the tail is the
    // oldest). The ring is untouched (departure 2): nothing here reaches LogRingBuffer.
    partial void OnMaxRowsChanged(int value)
    {
        if (_seedingEditors || _disposed)
        {
            return;
        }

        if (value is < 1000 or > 500_000)
        {
            SeedProperty(() => MaxRows = _storedAppLogMaxRows);
            return;
        }

        try
        {
            var next = _mutateGeneral(general => general with { AppLogMaxRows = value });
            _storedAppLogMaxRows = next.AppLogMaxRows;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Writing general.app_log_max_rows failed");
            SeedProperty(() => MaxRows = _storedAppLogMaxRows);
            return;
        }

        var trimmed = false;
        while (Lines.Count > value)
        {
            Lines.RemoveAt(Lines.Count - 1);
            trimmed = true;
        }

        // Kept index for index with Lines by Publish, which is the only other mutator. A list that
        // has drifted out of step (a test that appends to Lines directly) is cut back to Lines'
        // own length and then contributes no cursor at all, rather than throwing here: the trim
        // itself is the behaviour this method owes the reader and it must not depend on the
        // bookkeeping being perfect.
        while (_cursors.Count > Lines.Count)
        {
            _cursors.RemoveAt(_cursors.Count - 1);
        }

        // Verification B2 residual (step 32). A trim drops lines the reader had already fetched,
        // so after one there IS more to show than the viewer holds whether or not the reader had
        // paged to the end of the log. The cursor becomes the one after the last line kept, which
        // is what makes the state below true AND what lets a later raise fetch those lines back
        // rather than re-reading page one. Without it, lowering the cap at the end of the log left
        // NextCursor null, so neither the Load more button nor the cap sentence was offered and
        // the dropped lines were unreachable.
        if (trimmed && _cursors.Count == Lines.Count && _cursors.Count > 0)
        {
            NextCursor = _cursors[^1];
        }

        // Phase 14B fixer, fixer list item 47 (task7-review P3). The same expression Publish uses,
        // rather than a one-way clear: lowering the cap trims Lines to exactly the new cap, so
        // "Lines.Count < value" was false and the flag kept whatever it had. With a further page
        // available that left the Load more button visible, every click refused by the body's own
        // guard, and the line that explains why hidden.
        IsAtViewerCap = NextCursor is not null && Lines.Count >= value;

        // Verification B2: raising the cap re-enables paging with no restart and no reload. Both
        // of these read Lines.Count and MaxRows, neither of which raises a change these bindings
        // would see on its own.
        OnPropertyChanged(nameof(CanLoadMore));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    // Assigns a property without re-entering its own write-through: the constructor's seeding and
    // a refused edit's revert both go through this rather than toggling _seedingEditors by hand at
    // each call site.
    private void SeedProperty(Action assign)
    {
        var was = _seedingEditors;
        _seedingEditors = true;
        try
        {
            assign();
        }
        finally
        {
            _seedingEditors = was;
        }
    }

    // The search box is the one debounced input, on the application's shared window. The term is
    // captured here, on the raising thread, and passed into the load, so the window searches the
    // text that opened it rather than whatever the box holds when it elapses.
    partial void OnSearchChanged(string value)
    {
        if (_disposed)
        {
            return;
        }

        PendingSearch = _searchWindow.Restart((_, token) => DebounceAsync(value, token));
    }

    private async Task DebounceAsync(string term, CancellationToken cancellationToken)
    {
        try
        {
            await _searchWindow.Wait(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _post(() => Load(fromStart: true, term));
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this window.
        }
    }

    partial void OnFollowTailChanged(bool value)
    {
        StopFollow();

        if (value && !_disposed)
        {
            _follow = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            PendingFollow = FollowAsync(_follow.Token);
        }
    }

    // The periodic reload, on the injected delay seam so a test drives it without waiting. Stopped
    // by FollowTail going false and by Dispose, both through the linked source above.
    private async Task FollowAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delay(FollowInterval, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _post(() =>
                {
                    if (!_disposed && FollowTail)
                    {
                        Load(fromStart: true);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The toggle went off, or the page went away.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The log viewer follow-tail loop stopped");
        }
    }

    private void StopFollow()
    {
        var follow = _follow;
        _follow = null;
        follow?.Cancel();
        follow?.Dispose();
    }

    private LogFilters CurrentFilters(string? searchTerm)
        => new(MinimumLevel, searchTerm ?? Search);

    // The shape ActivityViewModel.Load uses: a generation counter plus the lifetime token, with
    // the read on a background thread because LogReader is blocking file IO and this page lives on
    // the UI thread.
    private Task Load(bool fromStart, string? searchTerm = null)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        var cursor = fromStart ? null : NextCursor;
        var filters = CurrentFilters(searchTerm);
        var search = filters.Search;
        var generation = ++_generation;
        LoadCount++;
        IsLoading = true;
        var token = _lifetime.Token;

        var load = Task.Run(
            () =>
            {
                try
                {
                    var page = _reader.Page(filters, cursor, PageSize);

                    // Only asked when the page came back empty, so the ordinary load costs one
                    // directory listing fewer. It is what separates "no log files yet" from
                    // "nothing matched".
                    var hasFiles = page.Lines.Count > 0
                        || LogFileSet.Newest(_logDirectory).Count > 0;

                    // Off the UI thread, alongside the read above: the Clear log notice is a
                    // directory listing plus a handful of FileInfo reads and must not run on the
                    // dispatcher thread that Publish runs on (spec 12.8's "updated when the viewer
                    // loads").
                    var clearNotice = DescribeWouldClear();

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, page, hasFiles, fromStart, search, clearNotice));
                }
                catch (OperationCanceledException)
                {
                    // The page went away while the read was in flight.
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Reading the log files failed");
                    _post(() => Publish(generation, null, false, fromStart, search, null));
                }
            },
            token);

        PendingLoad = load;
        return load;
    }

    // Runs on the UI thread through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it, and nothing is published into a page that has gone.
    private void Publish(
        int generation, LogPage? page, bool hasFiles, bool fromStart, string? search,
        string? clearNotice)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (page is not null)
        {
            // Held across the rebuild so a follow-tail tick, which reloads page one every two
            // seconds, does not make "Copy selection" go inert under the reader's hands
            // (review finding M5). LogLineViewModel is a record, so the row that comes back for
            // the same entry is equal to the one that went in. A follow tick deliberately DOES
            // drop pages appended with "Load more": following the tail means showing the newest,
            // and the reader turns the toggle off to stop that.
            var previous = fromStart ? Selected : null;

            if (fromStart)
            {
                Lines.Clear();
                _cursors.Clear();
            }

            // Verification B2 (steps 32 and 33). The cap is a bound on the LOADED SET and it is
            // enforced here, at the one place lines enter it, rather than only as a gate on the
            // next press: a page that would carry Lines past MaxRows is taken as far as the cap
            // and no further, so the viewer holds exactly MaxRows lines and never MaxRows plus a
            // part page.
            var taken = Math.Min(Math.Max(0, MaxRows - Lines.Count), page.Lines.Count);
            for (var index = 0; index < taken; index++)
            {
                Lines.Add(LogLineViewModel.From(page.Lines[index]));
                _cursors.Add(new LogCursor(page.Lines[index].Timestamp, page.Lines[index].Ordinal));
            }

            if (previous is not null)
            {
                Selected = Lines.FirstOrDefault(line => line == previous);
            }

            // When the page was truncated, the cursor is the one AFTER the last line kept, not the
            // reader's own end-of-page cursor: raising the cap later has to resume at the first
            // line this load dropped rather than skipping the remainder of the page for good.
            // Composed the same way LogReader composes its own (timestamp plus ordinal of the last
            // line handed out).
            NextCursor = taken == page.Lines.Count
                ? page.NextCursor
                : taken == 0
                    ? NextCursor
                    : new LogCursor(page.Lines[taken - 1].Timestamp, page.Lines[taken - 1].Ordinal);
            EmptyMessage = Describe(hasFiles, search);

            // Spec 12.8's viewer cap (PAR-012): true only when paging stopped because of the cap
            // and not because the log genuinely ran out, so the reason on screen is never "no
            // more log" when NextCursor says there is more.
            IsAtViewerCap = NextCursor is not null && Lines.Count >= MaxRows;
        }
        else
        {
            EmptyMessage = ReadFailedMessage;
        }

        // Spec 12.8's Clear log notice: updated on every load, so it always states what a click
        // would delete before the click happens (section 6.4). Computed off the UI thread inside
        // Load, alongside the read above, and only applied here when it is a real reading (a
        // failed load leaves the previous notice on screen rather than replacing it with a guess).
        if (clearNotice is not null)
        {
            ClearLogNotice = clearNotice;
        }

        // Gated on a completed load, so the empty state never flashes before the first page
        // arrives (spec 12.10).
        _hasLoaded = true;
        IsEmpty = _hasLoaded && Lines.Count == 0;
        IsLoading = false;

        // Lines is an ObservableCollection, so its Count moving is not a property change on this
        // page: CanLoadMore reads it and has to be told (verification B2).
        OnPropertyChanged(nameof(CanLoadMore));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    // Spec 12.10's three conditions, in the order that makes each one reachable: no files at all
    // beats a filter that excluded everything, and a search term beats the level filter, because
    // a search that matched nothing is what the reader just did.
    private static string Describe(bool hasFiles, string? search)
    {
        if (!hasFiles)
        {
            return NoFilesMessage;
        }

        return string.IsNullOrWhiteSpace(search) ? NoEntriesAtLevelMessage : NoSearchMatchMessage;
    }

    // Ruling Q26: when the cap was reached, the copied text says so on its first line.
    private string Compose(IReadOnlyList<LogLine> lines)
    {
        var body = string.Join(Environment.NewLine, lines.Select(line => line.Raw));
        if (lines.Count < _copyAllCap)
        {
            return body;
        }

        var notice = string.Create(
            CultureInfo.InvariantCulture,
            $"Truncated to the newest {_copyAllCap:N0} entries.");

        return body.Length == 0 ? notice : notice + Environment.NewLine + body;
    }

    /// <summary>
    /// Stops the follow-tail loop and cancels every background window. Disposes nothing it does
    /// not own: the reader and the shell are DI singletons the host owns, and the settings door is
    /// a delegate rather than the store.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopFollow();
        _lifetime.Cancel();
        _searchWindow.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Joins whatever background work is in flight. Test-only: a fixture that deletes a
    /// temp log directory must not race a read still walking it.</summary>
    internal void Quiesce(TimeSpan timeout)
    {
        var pending = new[] { PendingLoad, PendingSearch, PendingFollow }
            .Where(task => task is not null)
            .Select(task => task!);
        Task.WhenAll(pending).ContinueWith(_ => { }, TaskScheduler.Default).Wait(timeout);
    }
}
