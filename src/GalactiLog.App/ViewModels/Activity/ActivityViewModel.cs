using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Activity;

/// <summary>One filter pill: the value it selects, or null for "all", and the label it wears.
/// </summary>
public sealed partial class ActivityFilterOptionViewModel(string? key, string label) : ObservableObject
{
    /// <summary>The <c>severity</c> or <c>category</c> value, or null for the "all" pill.</summary>
    public string? Key { get; } = key;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Spec 12.6's Activity page: a reverse-chronological, keyset-paged list of top-level
/// <c>activity_events</c>, with severity, category and free-text filters, refresh and prune now.
/// Mirrors <c>frontend/src/components/ActivityFeed.tsx</c>.
/// </summary>
/// <remarks>
/// <para>
/// Delegates, never the query and the repository objects, exactly as every other page view-model
/// in this application takes them, so the page constructs in a unit test with lambdas, no database
/// and no window (spec 18.3).
/// </para>
/// <para>
/// <b>There is no poll.</b> The web feed polls every 10 seconds because a browser has no event
/// channel to the scanner; this application has <c>ScanStatusService.ScanFinished</c>, which is
/// the only automatic refresh trigger here, and Refresh is a button (questions.md Q19, HANDOFF
/// section 5). There is no direct <c>ScanCoordinator</c> subscription and no timer, so the web's
/// scroll-position heuristic and its "N new, click to view" pill have no port either.
/// </para>
/// <para>
/// <see cref="IsLoading"/>, <see cref="LoadFailed"/> and <see cref="IsEmpty"/> are three separate
/// states. A failed read taught nothing about whether the log is empty, so it renders
/// <see cref="FailureText"/> in place of spec 12.10's empty state.
/// </para>
/// </remarks>
public sealed partial class ActivityViewModel : ObservableObject, IDisposable
{
    /// <summary>The web's <c>limit: 50</c>.</summary>
    public const int PageSize = ActivityQuery.DefaultLimit;

    /// <summary>Spec 12.10's Activity empty state, verbatim. The web says "No activity recorded
    /// yet."; the spec wins.</summary>
    public const string EmptyStateText = "No activity recorded.";

    /// <summary>Shown in place of the list when the read threw (spec 12.10: a failed read is
    /// reported, never rendered as an empty list).</summary>
    public const string FailureText = "The activity log could not be loaded.";

    /// <summary>Shown beside the "Load older" button when an append threw. The page already on
    /// screen stays: a failed append taught nothing about the rows that did load.</summary>
    public const string AppendFailureText = "Older activity could not be loaded.";

    /// <summary>The same 250 ms window every debounced input in this application uses
    /// (<c>DashboardViewModel.DebounceWindow</c>, ruling Q3 of Phase 5). Referenced rather than
    /// redeclared, so the search box here and the dashboard's cannot drift apart.</summary>
    internal static readonly TimeSpan DebounceWindow = DashboardViewModel.DebounceWindow;

    // FIXER LIST F8: the theme subscription and its unsubscribe as one token. The unsubscribe is
    // the half that matters: ChartTheme.Changed is static, so a handler left behind pins this
    // view-model for the life of the process.
    private readonly IDisposable _themeSubscription;

    private readonly Func<ActivityFilters, ActivityCursor?, int, ActivityPage> _page;
    private readonly Func<int> _retentionDays;
    private readonly Func<int, int> _pruneNow;
    private readonly ScanStatusService? _scanStatus;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings>? _mutateGeneral;
    private readonly Func<DateTime?, int>? _countUnseen;

    // Spec 12.6's marker: the value rows are rendered against. Seeded from the stored document at
    // construction and moved forward only by MarkOpened's deferred commit, never eagerly, which is
    // what keeps a first open's render reading the pre-open value (section 7.2).
    private DateTimeOffset? _seenAt;

    // Spec 14.5, and Task 3's standing rule: theme tokens are resolved on the UI thread only.
    // The page is constructed on the UI thread; a load's post callback runs inline on the loading
    // thread in a unit test, so it must never build these. Rebuilt from ChartTheme.Changed, which
    // is raised on the UI thread too (review finding 4).
    private SeverityBrushes _brushes;

    // Spec 5.8.1's display zone and clock, resolved once: FindSystemTimeZoneById is a system
    // lookup and must not run per row (the rule FrameTableViewModel states).
    private readonly TimeZoneInfo _zone;
    private readonly bool _use24Hour;

    // One page-lifetime source every background window is linked to, the shape
    // DashboardViewModel's F6 fix established: this page is a DI singleton, so without it a load
    // or a prune started just before shutdown keeps running after the SQLite files are gone.
    private readonly CancellationTokenSource _lifetime = new();

    // FIXER LIST F11: the search window, on the shared Debouncer rather than a fourth hand-rolled
    // copy of cancel-previous, link-to-lifetime, await-the-window.
    private readonly Debouncer _searchWindow;

    // Only the newest load may write to the bindings. The token alone is not enough: a load that
    // already finished can still be sitting in the dispatcher queue when a newer one is requested,
    // and posting it then would show a stale page.
    private int _generation;

    private bool _hasLoaded;
    private bool _disposed;

    /// <param name="page">Normally <c>ActivityQuery.Page</c>.</param>
    /// <param name="retentionDays">Normally <c>() =&gt; settingsStore.GetGeneral()
    /// .ActivityRetentionDays</c> (spec 5.8.1: an int from 1 to 3650 defaulting to 90). Read per
    /// prune rather than by value, because the Storage settings tab can change it while this page
    /// is alive.</param>
    /// <param name="pruneNow">Normally <c>ActivityRepository.PruneRetention</c>, which already
    /// implements the whole action, the <c>activity_pruned</c> event included. Nothing here writes
    /// or deletes a row of its own: <c>ActivityRepository</c> is the one writer and deleter
    /// (spec 10.9).</param>
    /// <param name="general">Spec 5.8.1's <c>timezone</c> and <c>use_24h_time</c>. Display
    /// formatting only; it never affects a <c>session_date</c> (spec 12.4).</param>
    /// <param name="scanStatus">The only automatic refresh trigger. Already on the UI thread when
    /// a subscriber sees it, so its handler does not post again. Never a direct
    /// <c>ScanCoordinator</c> subscription.</param>
    /// <param name="post">The dispatcher seam every view-model takes; defaults to
    /// <c>UiPost.Default</c>.</param>
    /// <param name="delay">The debounce seam, the same shape and the same window
    /// <c>DashboardViewModel</c> uses for its search box, so a test drives the debounce instead of
    /// sleeping on it.</param>
    /// <param name="logger">A failed load or prune is logged, never rethrown on the UI thread.
    /// </param>
    /// <param name="mutateGeneral">Task 7's one door into <c>general.activity_seen_at</c>
    /// (PAR-017, spec 12.6), normally <c>SettingsStore.MutateGeneral</c>. Trailing and optional so
    /// no existing construction site moves; null leaves <see cref="MarkOpened"/> a no-op, which is
    /// the shape a unit test with no store has.</param>
    /// <param name="countUnseen">The rail badge's count (spec 12.6), normally
    /// <c>ActivityQuery.CountUnseen</c>: events newer than the marker, ignoring every filter and
    /// the search, which <see cref="Total"/> cannot answer because it is the filtered total.
    /// </param>
    public ActivityViewModel(
        Func<ActivityFilters, ActivityCursor?, int, ActivityPage> page,
        Func<int> retentionDays,
        Func<int, int> pruneNow,
        Func<GeneralSettings> general,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? logger = null,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings>? mutateGeneral = null,
        Func<DateTime?, int>? countUnseen = null)
    {
        _page = page;
        _retentionDays = retentionDays;
        _pruneNow = pruneNow;
        _scanStatus = scanStatus;
        _post = post ?? UiPost.Default;
        _searchWindow = new Debouncer(_lifetime.Token, delay ?? Task.Delay, DebounceWindow);
        _logger = logger ?? NullLogger.Instance;
        _brushes = new SeverityBrushes();
        _mutateGeneral = mutateGeneral;
        _countUnseen = countUnseen;

        var settings = general();
        _zone = SessionTimeFormat.Resolve(settings.DisplayTimezoneId);
        _use24Hour = settings.Use24HTime;
        _seenAt = settings.ActivitySeenAt;

        // The web's pill sets. Severity's labels are all / info / warn / error, so "warning"
        // displays as "warn"; the value bound into the filter is still the stored vocabulary.
        SeverityOptions =
        [
            new ActivityFilterOptionViewModel(null, "all") { IsSelected = true },
            new ActivityFilterOptionViewModel("info", "info"),
            new ActivityFilterOptionViewModel("warning", "warn"),
            new ActivityFilterOptionViewModel("error", "error"),
        ];
        CategoryOptions =
        [
            new ActivityFilterOptionViewModel(null, "all") { IsSelected = true },
            .. ActivityQuery.ValidCategories.Select(category =>
                new ActivityFilterOptionViewModel(category, ActivityRowViewModel.CategoryLabelFor(category))),
        ];

        if (scanStatus is not null)
        {
            // ScanStatusService has already marshalled onto the UI thread; do not post again.
            scanStatus.ScanFinished += OnScanFinished;
        }

        // Review finding 4: every other theme-brush holder in the application rebuilds on
        // ChartTheme.Changed, so the severity and child-alert glyphs repaint with the
        // DynamicResource text around them instead of keeping the previous theme's colours until
        // restart. Raised on the UI thread, which is where the rebuild has to happen.
        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);

        Load(fromStart: true);
        RefreshUnseenCount();
    }

    /// <summary>The page's rows, newest first. The initial load replaces them; "Load older"
    /// appends.</summary>
    public ObservableCollection<ActivityRowViewModel> Rows { get; } = [];

    /// <summary>The severity pills, "all" first (the web's <c>["all", "info", "warning",
    /// "error"]</c>).</summary>
    public IReadOnlyList<ActivityFilterOptionViewModel> SeverityOptions { get; }

    /// <summary>The category pills, "all" followed by one per spec 5.12 category.</summary>
    public IReadOnlyList<ActivityFilterOptionViewModel> CategoryOptions { get; }

    /// <summary>Null means every severity. Changing it reloads from the start and drops the
    /// cursor.</summary>
    [ObservableProperty]
    public partial string? SeverityFilter { get; private set; }

    /// <summary>Null means every category.</summary>
    [ObservableProperty]
    public partial string? CategoryFilter { get; private set; }

    /// <summary>Spec 12.6's free-text search over the message. Debounced before it reloads, on the
    /// application's one shared window.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowLoadingLine))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The read threw. Rendered as <see cref="FailureText"/>, never as the empty state.
    /// Set only by a load from the start: a failed "Load older" reports
    /// <see cref="AppendFailed"/> instead and leaves the loaded page on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    public partial bool LoadFailed { get; private set; }

    /// <summary>A "Load older" that threw. Reported beside the button as
    /// <see cref="AppendFailureText"/>; the rows already loaded stay exactly where they are
    /// (review finding 1).</summary>
    [ObservableProperty]
    public partial bool AppendFailed { get; private set; }

    /// <summary>Spec 12.10's <see cref="EmptyStateText"/>: the filtered log has no rows and a load
    /// has completed, so the empty state never flashes before the first page arrives.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    public partial bool IsEmpty { get; private set; }

    /// <summary>
    /// The list renders whenever it has something to render. It is deliberately NOT gated on
    /// <see cref="IsLoading"/> once rows exist: every reload sets that flag, so gating on it
    /// collapsed the whole feed to "Loading activity..." on Refresh, on "Load older" and on every
    /// post-scan reload, not only on the first load (review finding 1).
    /// </summary>
    public bool ShowList => !LoadFailed && !IsEmpty && (Rows.Count > 0 || !IsLoading);

    /// <summary>The loading line, shown only while there is nothing else to show.</summary>
    public bool ShowLoadingLine => IsLoading && Rows.Count == 0;

    /// <summary>The full filtered count the query reported, which is every matching event and not
    /// the number loaded so far.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    public partial int Total { get; private set; }

    public string TotalText => Total == 1
        ? "1 event"
        : Total.ToString("N0", CultureInfo.InvariantCulture) + " events";

    /// <summary>Non-null while there is an older page to fetch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadOlder))]
    [NotifyCanExecuteChangedFor(nameof(LoadOlderCommand))]
    public partial ActivityCursor? NextCursor { get; private set; }

    /// <summary>What the "Load older" button's visibility binds to. The web shows the button only
    /// while <c>next_cursor</c> is not null.</summary>
    public bool CanLoadOlder => NextCursor is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PruneNowCommand))]
    public partial bool IsPruning { get; private set; }

    /// <summary>The last prune's outcome as one sentence, or null. The same shape
    /// <c>UnresolvedNamesViewModel.RetrySummary</c> reports its retry in.</summary>
    [ObservableProperty]
    public partial string? PruneSummary { get; private set; }

    /// <summary>
    /// Spec 12.6's rail badge (PAR-017): events newer than <c>general.activity_seen_at</c>,
    /// ignoring every filter and the search, zero while there is nothing unseen (the rail shows no
    /// badge at all, not a badge reading zero). Not <see cref="Total"/>: that is the filtered
    /// count, and reusing it would count what the feed's own pills are hiding.
    /// </summary>
    [ObservableProperty]
    public partial int UnseenCount { get; private set; }

    /// <summary>The in-flight load, so a test awaits it instead of blocking on it (TRACKING
    /// section 2 item 8).</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>The in-flight debounce-and-reload opened by the search box.</summary>
    internal Task? PendingSearch { get; private set; }

    /// <summary>The in-flight prune, so a test awaits it instead of blocking on it. Covers the
    /// whole operation: the delete, the summary line and the reload it posts.</summary>
    internal Task? PendingPrune { get; private set; }

    /// <summary>
    /// Re-reads the first page. Spec 12.6's refresh action, and the only manual one: there is no
    /// poll (questions.md Q19).
    /// </summary>
    /// <remarks>
    /// <c>RelayCommand.Execute</c> ignores <c>CanExecute</c> (TRACKING section 6 item 13), so the
    /// already-loading guard is repeated in the body and not left to <see cref="CanRefresh"/>.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        if (IsLoading || _disposed)
        {
            return;
        }

        Load(fromStart: true);
    }

    private bool CanRefresh() => !IsLoading && !_disposed;

    /// <summary>
    /// The web's "Load older": appends the next keyset page and advances the cursor.
    /// </summary>
    /// <remarks>
    /// Both guards are repeated in the body because <c>RelayCommand.Execute</c> ignores
    /// <c>CanExecute</c>. A run with no cursor would re-fetch page one and append it to itself;
    /// a run while another load is in flight would interleave two pages into one list.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanExecuteLoadOlder))]
    private void LoadOlder()
    {
        if (_disposed || IsLoading || NextCursor is null)
        {
            return;
        }

        Load(fromStart: false);
    }

    private bool CanExecuteLoadOlder() => !_disposed && !IsLoading && NextCursor is not null;

    /// <summary>
    /// Spec 12.6's "prune now": applies <c>general.activity_retention_days</c> immediately rather
    /// than waiting for the next application start or scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ActivityRepository.PruneRetention</c> does the whole thing, the retention floor, the bulk
    /// delete with its <c>parent_id</c> cascade and the <c>activity_pruned</c> event included.
    /// Nothing here re-implements any of it. It is a database-only delete: no file on disk is
    /// touched (spec 2.1).
    /// </para>
    /// <para>
    /// The one-at-a-time guard is in the body, not only in <see cref="CanPrune"/>, because
    /// <c>RelayCommand.Execute</c> ignores <c>CanExecute</c>: two prunes running together would
    /// report two contradictory counts for one deletion.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPrune))]
    private Task PruneNowAsync()
    {
        if (_disposed || IsPruning)
        {
            return Task.CompletedTask;
        }

        IsPruning = true;
        PruneSummary = null;

        // PendingPrune is the WHOLE operation, not just the delete: the summary and the reload are
        // posted from the delete's continuation, so a test that awaited only the inner Task.Run
        // could observe a null summary. That is the exact shape of the double-Execute race the
        // Phase 9 flake fix recorded for TRACKING section 6 item 13.
        PendingPrune = RunPruneAsync();
        return PendingPrune;
    }

    private async Task RunPruneAsync()
    {
        var token = _lifetime.Token;

        try
        {
            var retention = _retentionDays();

            // A bulk delete over a retention window's worth of rows, so never on the dispatcher.
            var deleted = await Task.Run(() => _pruneNow(retention), token).ConfigureAwait(false);

            _post(() => FinishPrune(Describe(deleted, retention)));
        }
        catch (OperationCanceledException)
        {
            _post(() => FinishPrune(null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The activity log prune failed");
            _post(() => FinishPrune("The activity log could not be pruned. See the log for details."));
        }
    }

    private bool CanPrune() => !IsPruning && !_disposed;

    // Runs on the UI thread through the post seam. The list is re-read whatever the outcome: a
    // prune that threw partway may still have deleted rows.
    private void FinishPrune(string? summary)
    {
        if (_disposed)
        {
            return;
        }

        IsPruning = false;
        PruneSummary = summary;
        Load(fromStart: true);
    }

    private static string Describe(int deleted, int retentionDays) => deleted == 0
        ? $"Nothing to prune: no activity is older than {retentionDays} days."
        : $"Pruned {Plural(deleted, "entry", "entries")} older than {retentionDays} days.";

    private static string Plural(int count, string singular, string plural)
        => count == 1
            ? "1 " + singular
            : count.ToString("N0", CultureInfo.InvariantCulture) + " " + plural;

    /// <summary>Selects a severity pill. Null is the "all" pill.</summary>
    [RelayCommand]
    private void SelectSeverity(ActivityFilterOptionViewModel option)
    {
        SelectOnly(SeverityOptions, option);
        SeverityFilter = option.Key;
    }

    /// <summary>Selects a category pill. Null is the "all" pill.</summary>
    [RelayCommand]
    private void SelectCategory(ActivityFilterOptionViewModel option)
    {
        SelectOnly(CategoryOptions, option);
        CategoryFilter = option.Key;
    }

    private static void SelectOnly(
        IReadOnlyList<ActivityFilterOptionViewModel> options, ActivityFilterOptionViewModel chosen)
    {
        foreach (var option in options)
        {
            option.IsSelected = ReferenceEquals(option, chosen);
        }
    }

    // A filter change re-runs the initial load, exactly as the web does: the cursor belongs to the
    // previous filter's ordering and means nothing under a new one.
    partial void OnSeverityFilterChanged(string? value) => Load(fromStart: true);

    partial void OnCategoryFilterChanged(string? value) => Load(fromStart: true);

    // The search box is the one input that is debounced, on the application's shared window. The
    // term is captured here, on the raising thread, and passed into the load, so the window
    // searches the text that opened it rather than whatever the box holds when it elapses. That is
    // DashboardViewModel.OnSearchTextChanged's rule, now actually implemented rather than only
    // claimed (review finding 3).
    partial void OnSearchTextChanged(string value)
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

    private void OnScanFinished(object? sender, EventArgs e)
    {
        Load(fromStart: true);
        RefreshUnseenCount();
    }

    /// <summary>
    /// Spec 12.6's open signal (PAR-017): "captures the current time, renders the page against the
    /// value that was stored before the capture, and then writes the captured value." Called from
    /// <c>MainWindowViewModel.Selected</c>'s setter on every navigation to the Activity
    /// destination, including the one that constructs this page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No reload (P2-2, coordinator ruling on the reviewer's escalation).</b> This page is a DI
    /// singleton that keeps its scroll position and its currently loaded page set across
    /// navigations on purpose (the class doc's whole point). So "renders the page against the
    /// value that was stored before the capture" cannot mean re-fetching: it means correcting the
    /// marker on every row already loaded, in place, against the marker in force before this open
    /// (<see cref="_seenAt"/>, read here before anything changes it). A row built the one time it
    /// loaded and never touched again would otherwise carry whatever cutoff was in force back
    /// then, forever, which is exactly the defect that made the third-open truth-table row
    /// (spec 12.6: "nothing new, marks nothing") impossible: a row marked unseen on a first open
    /// never became seen on a later one. <see cref="ActivityRowViewModel.RefreshUnseen"/> is the
    /// correction, walking each row's already-built children too.
    /// </para>
    /// <para>
    /// A row that arrives later through paging (<c>LoadOlderAsync</c>, or a
    /// <c>ScanStatusService.ScanFinished</c> reload) is unaffected by this loop: it is built by
    /// <see cref="Build"/> against whatever <see cref="_seenAt"/> is live at that moment, and
    /// <see cref="_seenAt"/> does not move until the deferred commit below lands, so it keeps
    /// using the same captured value "until the next open" exactly as spec 12.6 says.
    /// </para>
    /// <para>
    /// <b>Why the write is still deferred rather than immediate.</b> The constructor's own initial
    /// <see cref="Load"/> is asynchronous (its read runs in <see cref="Task.Run(Action)"/> and its
    /// publish is posted), and this method is called synchronously right after construction on the
    /// very same navigation. Writing <see cref="_seenAt"/> here immediately would race that first
    /// publish: if the write landed first, the rows it renders would be built against the
    /// just-written value and show no marker at all on the first open (the obvious, wrong
    /// implementation this method's tests exist to catch). On a first open <see cref="Rows"/> is
    /// still empty when the loop above runs, so there is nothing to correct in place yet; the
    /// build-time path (<see cref="IsUnseen(ActivityRow)"/>) is what renders that first page
    /// correctly, once the deferred load lands.
    /// </para>
    /// <para>
    /// The commit is attached as a continuation of whatever load is in flight at the moment of the
    /// call (<see cref="PendingLoad"/>, or <c>Task.CompletedTask</c> when nothing is loading).
    /// Because <see cref="Publish"/> is itself posted from inside that same load's background
    /// action, and a <c>Task.Run</c> antecedent only completes after its body, including the
    /// enqueue of that post, has returned, the continuation's own post is necessarily enqueued
    /// after Publish's on the single dispatcher queue: render happens before write, provably, not
    /// by timing luck. On every later open there is ordinarily no load in flight, so the
    /// continuation runs at once.
    /// </para>
    /// </remarks>
    internal void MarkOpened()
    {
        if (_disposed)
        {
            return;
        }

        // Read before anything below can move it: the marker in force for this open's render.
        var renderAgainst = _seenAt;
        foreach (var row in Rows)
        {
            row.RefreshUnseen(renderAgainst);
        }

        var captured = DateTimeOffset.UtcNow;
        var pending = PendingLoad ?? Task.CompletedTask;
        PendingOpen = pending.ContinueWith(
            _ => _post(() => CommitSeenAt(captured)), TaskScheduler.Default);
    }

    /// <summary>The in-flight open commit, so a test awaits it instead of racing it (mirrors
    /// <see cref="PendingLoad"/>).</summary>
    internal Task? PendingOpen { get; private set; }

    // Runs on the UI thread through the post seam, strictly after the render it was deferred past.
    // Display state only (spec 12.6): filters nothing, changes no query, and retention pruning
    // (ActivityRepository.PruneRetention) never reads it.
    private void CommitSeenAt(DateTimeOffset captured)
    {
        if (_disposed)
        {
            return;
        }

        if (_mutateGeneral is { } mutate)
        {
            try
            {
                mutate(general => general with { ActivitySeenAt = captured });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Writing general.activity_seen_at failed");
            }
        }

        _seenAt = captured;
        RefreshUnseenCount();
    }

    // Off the UI thread would be more correct for a COUNT(*), but this page's every other read
    // already runs its query synchronously from the UI thread inside Load's Task.Run, never here;
    // a rail badge count is one indexed COUNT(*) and Load's own read is the far heavier one this
    // page already accepts synchronously relative to construction. ponytail: if this ever shows up
    // in a profile, move it behind the same Task.Run Load already uses.
    private void RefreshUnseenCount()
    {
        if (_disposed || _countUnseen is not { } countUnseen)
        {
            UnseenCount = 0;
            return;
        }

        try
        {
            UnseenCount = countUnseen(_seenAt?.UtcDateTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Counting unseen activity failed");
        }
    }

    // Runs on the UI thread, where ChartTheme.Changed is raised, which is the only thread a token
    // may be read on (Task 3's standing rule). Rows repaint in place rather than being rebuilt: a
    // theme switch must not collapse an expanded scan or lose the reader's scroll position.
    private void OnThemeChanged()
    {
        if (_disposed)
        {
            return;
        }

        _brushes = new SeverityBrushes();
        foreach (var row in Rows)
        {
            row.RefreshBrushes(_brushes);
        }
    }

    // The shape TargetDetailViewModel.Load and UnresolvedNamesViewModel.Load use: a generation
    // counter plus the lifetime token, with the read itself on a background thread because
    // ActivityQuery is a synchronous SQLite read and this page lives on the UI thread.
    private void Load(bool fromStart, string? searchTerm = null)
    {
        if (_disposed)
        {
            return;
        }

        var cursor = fromStart ? null : NextCursor;
        var filters = new ActivityFilters(SeverityFilter, CategoryFilter, searchTerm ?? SearchText);
        var generation = ++_generation;
        IsLoading = true;
        var token = _lifetime.Token;

        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    var result = _page(filters, cursor, PageSize);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, result, fromStart));
                }
                catch (OperationCanceledException)
                {
                    // The page went away while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the activity page failed");
                    _post(() => Publish(generation, null, fromStart));
                }
            },
            token);
    }

    // Runs on the UI thread through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Publish(int generation, ActivityPage? result, bool fromStart)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (result is not null)
        {
            if (fromStart)
            {
                Rows.Clear();
            }

            foreach (var row in result.Rows)
            {
                Rows.Add(Build(row, result.Children));
            }

            NextCursor = result.NextCursor;
            Total = result.Total;
            _hasLoaded = true;
        }

        // A failed read from the start replaces the list with the failure line; a failed append
        // reports beside the button and leaves the loaded rows where they are (review finding 1).
        if (fromStart)
        {
            LoadFailed = result is null;
            AppendFailed = false;
        }
        else
        {
            AppendFailed = result is null;
        }

        IsEmpty = _hasLoaded && !LoadFailed && Rows.Count == 0;
        IsLoading = false;

        // Rows is the only mutable input to these two, and Publish is the only place it changes.
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowLoadingLine));
    }

    // Children arrive as query rows and stay that way until the reader expands the parent: a scan's
    // child count is unbounded by construction (ScanWriter emits one file_rejected per rejected file
    // and one target_created per new target, all parented to scan_started), so building a
    // view-model per child of every row on the page would cost thousands of objects nobody asked
    // for (review finding 2).
    private ActivityRowViewModel Build(
        ActivityRow row, IReadOnlyDictionary<int, IReadOnlyList<ActivityRow>> children)
    {
        var childRows = children.TryGetValue(row.Id, out var found) ? found : [];
        return new ActivityRowViewModel(
            row, childRows, BuildChild, _brushes, _zone, _use24Hour, IsUnseen(row));
    }

    // A child row has no children of its own: parent_id is one level deep by construction
    // (spec 10.9), so it needs no builder.
    private ActivityRowViewModel BuildChild(ActivityRow child)
        => new(child, [], null, _brushes, _zone, _use24Hour, IsUnseen(child));

    // Spec 12.6: a row is unseen when its timestamp is strictly newer than the marker in force at
    // the moment the row is built. Set here at build time and corrected again on every later open
    // by MarkOpened's in-place re-render (P2-2 review), which is what makes "a row arrives while
    // open keeps its marker until the next open" true without ever going stale.
    private bool IsUnseen(ActivityRow row) => ActivityRowViewModel.ComputeIsUnseen(row.Timestamp, _seenAt);

    /// <summary>Cancels every background window and drops the scan subscription. The page is a DI
    /// singleton, so closing the application must not leave a read or a prune running against a
    /// database that is going away.</summary>
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
            _scanStatus.ScanFinished -= OnScanFinished;
        }

        // The theme is static and outlives this page, so a page that does not unsubscribe keeps
        // repainting rows after it has closed.
        _themeSubscription.Dispose();

        _searchWindow.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Joins whatever background work is in flight. Test-only: a fixture that deletes a
    /// temp database must not race a load still holding a connection to it.</summary>
    internal void Quiesce(TimeSpan timeout)
    {
        var pending = new[] { PendingLoad, PendingSearch, PendingPrune }
            .Where(task => task is not null)
            .Select(task => task!);
        Task.WhenAll(pending).ContinueWith(_ => { }, TaskScheduler.Default).Wait(timeout);
    }
}
