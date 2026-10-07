using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels;

/// <summary>
/// What the dashboard's list region is showing (spec 12.10). One enum rather than a set of
/// booleans, so the states cannot overlap and no combination of flags can render two empty states
/// at once.
/// </summary>
public enum DashboardContentState
{
    /// <summary>Before the first query has come back.</summary>
    Loading,

    /// <summary>The target list.</summary>
    Rows,

    /// <summary>"No frames catalogued yet", with a Run Scan button. No filter is active, so an
    /// empty library is the only explanation: no roots configured, roots never scanned, or a scan
    /// that found nothing all land here.</summary>
    NoFramesYet,

    /// <summary>"No targets match these filters", with a Reset Filters button.</summary>
    NoMatches,
}

/// <summary>
/// The dashboard page (design-spec 12.2). Owns the filter panel, the summary strip, the panel's
/// collapsed flag, and the single debounced path from a filter change to
/// <c>TargetListingQuery.List</c>. Task 7 adds the target list around it and Task 8 the search
/// results and empty states; neither adds a second query call site.
/// <para>
/// Registered as a DI singleton (coordinator ruling Q4) so filter state "persists for the
/// session", which on a desktop application means the process lifetime: navigating away and back
/// preserves every value, and nothing is written to disk.
/// </para>
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    /// <summary>Coordinator ruling Q3. Spec 12.2 says the inputs are debounced without giving a
    /// figure; one shared window for every input, not a fast path and a slow path.</summary>
    internal static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    private readonly Func<TargetListingCriteria, TargetListingPage> _list;
    private readonly Action<Action> _post;
    private readonly Func<string, IReadOnlyList<TargetSearchResult>> _search;
    private readonly Func<IReadOnlyList<string>> _probeRoots;
    private readonly Func<CancellationToken, Task>? _startScan;
    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings>? _mutateGeneral;
    private readonly ScanStatusService? _scanStatus;
    private readonly ILogger _logger;

    // Phase 14C, spec 12.2. The one chain over the display document, kept as a field because the
    // panel's two keys are written through the same writer the column list already uses: two
    // chains over one document lose updates.
    private readonly DisplayColumnWriter _display;

    // F6: one host-lifetime source every window below is linked to. The dashboard is a DI
    // singleton, so without it a debounce opened just before shutdown keeps a query and a scan
    // root probe running after the host (and its SQLite files) are gone.
    private readonly CancellationTokenSource _lifetime = new();

    // FIXER LIST F11: the three windows, each on the shared Debouncer. Only the newest request in
    // a window may write to the bindings, which is what each one's generation is for: a token
    // alone is not enough, because a query that already finished can still be sitting in the
    // dispatcher queue when a newer one is requested, and posting it then would show a stale page.
    private readonly Debouncer _queryWindow;
    private readonly Debouncer _searchWindow;

    // The probe uses the window half only: it is not driven by typing, so it never calls Wait.
    private readonly Debouncer _probeWindow;

    private bool _disposed;

    /// <param name="list">Normally <c>TargetListingQuery.List</c>. A delegate rather than the
    /// query object, matching how <c>WatcherService</c> and <c>ScanScheduler</c> already take
    /// delegates bound to <c>ScanCoordinator</c>, so every test constructs this with lambdas and
    /// no database.</param>
    /// <param name="loadFacets">Normally <c>DashboardFacetsQuery.Load</c>.</param>
    /// <param name="loadHeaderKeys">Normally <c>DistinctHeaderKeysQuery.Load</c>.</param>
    /// <param name="aliases">Normally <c>() =&gt; aliasMapCache.Current</c>.</param>
    /// <param name="general">Supplies the default page size (spec 5.8.1). By value, like
    /// <paramref name="initialDisplay"/>: both settings documents are read once on
    /// <c>Program.Main</c>'s thread inside <c>AppHost.Build</c> (phase review item 2).</param>
    /// <param name="initialDisplay">The display document as the host read it at build time. The
    /// column list is taken from here; no settings read happens on the construction path.</param>
    /// <param name="delay">The debounce seam, the same shape <c>ScanScheduler</c> already uses
    /// and tests with <c>FakeDelay</c>.</param>
    /// <param name="post">How to reach the UI thread, the same seam and the same default as
    /// <c>ScanStatusService</c>. Every query result is applied through it, so no binding is ever
    /// written from the thread pool.</param>
    /// <param name="search">Normally <c>TargetSearchQuery.Search</c> (Task 8). Defaults to
    /// returning nothing, so a test that does not care about the dropdown skips it.</param>
    /// <param name="probeRoots">Returns the configured scan roots that are <em>not</em> reachable
    /// (spec 12.10). <c>AppHost</c> binds it to a read of <c>general.scan_roots</c> filtered by
    /// <c>UserFiles.DirectoryExists</c>; the filtering lives there rather than here so a test can
    /// fake an unreachable root without creating or deleting a directory. Always invoked off the
    /// UI thread: a disconnected NAS can make that call take seconds (ruling Q18).</param>
    /// <param name="startScan">Normally
    /// <c>coordinator.RunAsync(ScanTrigger.Manual, null, ct)</c>, bound as a delegate the same way
    /// <c>WatcherService</c> and <c>ScanScheduler</c> take theirs. Backs the Run Scan button of
    /// the "No frames catalogued yet" state; null leaves the button disabled.</param>
    /// <param name="writeDefaultPageSize">Forwarded straight through to
    /// <see cref="TargetListViewModel"/>'s own parameter of the same name (Phase 14C Task 4, spec
    /// 5.8.1).</param>
    /// <param name="loadCustomColumns">Normally <c>CustomColumnRepository.List</c> (spec 12.15).
    /// Forwarded straight through to <see cref="FilterPanelViewModel"/>'s parameter of the same
    /// name, which is where the eighth filter section is built from it; null leaves the panel with
    /// the seven sections it had before Phase 20 (user choice 19). Task 5a forwards the same
    /// delegate to <see cref="TargetListViewModel"/>, which reads it on this page's own
    /// background query hop.</param>
    /// <param name="loadTargetValues">Normally <c>CustomColumnRepository.TargetValues</c> (spec
    /// 12.15). Forwarded to <see cref="TargetListViewModel"/>: one round trip per page of rows,
    /// taken beside the listing query itself.</param>
    /// <param name="loadValuesForTarget">Normally <c>CustomColumnRepository.ValuesForTarget</c>.
    /// Forwarded to <see cref="TargetListViewModel"/>, which reads it when a row is expanded and
    /// never with the page (ruling C11).</param>
    /// <param name="writeCustomValue">Normally <c>CustomColumnRepository.SetValue</c>. Forwarded
    /// to <see cref="TargetListViewModel"/> and from there to every cell it builds.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>, the same seam the
    /// Library tab writes through. The notice's Review action persists
    /// <c>general.scan_filters_reviewed</c> with it (polish wave 1, ruling 1); null leaves Review
    /// as a route only.</param>
    public DashboardViewModel(
        Func<TargetListingCriteria, TargetListingPage> list,
        Func<DashboardFacets> loadFacets,
        Func<IReadOnlyList<string>> loadHeaderKeys,
        Func<AliasMap> aliases,
        GeneralSettings general,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        DisplaySettings? initialDisplay = null,
        Func<DisplaySettings>? getDisplay = null,
        Action<DisplaySettings>? saveDisplay = null,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null,
        Func<string, IReadOnlyList<TargetSearchResult>>? search = null,
        Func<IReadOnlyList<string>>? probeRoots = null,
        Func<CancellationToken, Task>? startScan = null,
        DisplayColumnWriter? displayColumns = null,
        ILogger? logger = null,
        Action<int>? writeDefaultPageSize = null,
        Func<IReadOnlyList<CustomColumnDefinition>>? loadCustomColumns = null,
        Func<IReadOnlyCollection<Guid>, IReadOnlyList<CustomValueRow>>? loadTargetValues = null,
        Func<Guid, IReadOnlyList<CustomValueRow>>? loadValuesForTarget = null,
        Func<Guid, CustomValueKey, string?, CustomWriteResult>? writeCustomValue = null,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings>? mutateGeneral = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _list = list;
        _mutateGeneral = mutateGeneral;
        _post = post ?? UiPost.Default;
        var debounce = delay ?? Task.Delay;

        // FIXER LIST F11. One window each, all three on the shared Debouncer, all three linked to
        // the page lifetime (the F6 fix) so a window opened just before shutdown cannot publish
        // into a disposed page.
        _queryWindow = new Debouncer(_lifetime.Token, debounce, DebounceWindow);
        _searchWindow = new Debouncer(_lifetime.Token, debounce, DebounceWindow);
        _probeWindow = new Debouncer(_lifetime.Token, debounce, DebounceWindow);
        _search = search ?? (_ => []);
        _probeRoots = probeRoots ?? (() => []);
        _startScan = startScan;
        _scanStatus = scanStatus;

        Filters = new FilterPanelViewModel(
            aliases, loadFacets, loadHeaderKeys, post: _post, logger: _logger,
            loadCustomColumns: loadCustomColumns);

        // Phase review item 8: handlers first, then the panel's first option-list load. The panel
        // used to call Reload from its own constructor, so a publish that changed what
        // BuildCriteria produces raised a Changed nothing was listening for yet.
        Filters.Changed += OnFiltersChanged;
        Filters.SearchTextChanged += OnSearchTextChanged;

        // Task 7. Column visibility is the one piece of dashboard state that outlives the process
        // (spec 5.8.2), and it is the only thing this page writes; everything else is session
        // state per ruling Q4. The defaults let a test that only needs *a* dashboard skip the
        // settings store entirely -- AppHost always passes both.
        var display = initialDisplay ?? new DisplaySettings();

        // Phase 6 Task 5: the process-wide display.columns writer, so the dashboard's column
        // clicks and the frame table's share one chain. AppHost passes the registered
        // singleton; a test that only names the two delegates gets a writer of its own, which
        // is the same behaviour when the dashboard is the only table in the process.
        _display = displayColumns ?? new DisplayColumnWriter(
            getDisplay ?? (() => new DisplaySettings()),
            saveDisplay ?? (_ => { }),
            _logger);

        Targets = new TargetListViewModel(
            display,
            _display,
            general.DefaultPageSize,
            writeDefaultPageSize,
            loadCustomColumns,
            loadTargetValues,
            loadValuesForTarget,
            writeCustomValue,
            _post,
            // The page's own logger, so a lazy night-value read that fails is recorded rather than
            // swallowed. Named, because the delay seam between them is a test-only parameter.
            cellLogger: _logger,
            aliases: aliases);
        Targets.Changed += OnTargetsChanged;
        Targets.TargetOpened += OnTargetOpened;
        Targets.MosaicOpened += OnMosaicOpened;

        // Spec 12.2's list must reflect what a scan just ingested. ScanStatusService has already
        // marshalled this onto the UI thread (Task 5) -- do not post again.
        if (scanStatus is not null)
        {
            scanStatus.ScanFinished += OnScanFinished;

            // Run Scan is disabled while a scan is running, from any trigger (Task 8).
            scanStatus.PropertyChanged += OnScanStatusChanged;
        }

        // Phase 14C, spec 12.2 and 5.8.2: the panel's state and its committed width, per profile.
        // Seeded into the backing fields rather than through the setters, because a setter here
        // would persist the document back over itself before the page has rendered once. The read
        // clamp is the record's own member, so this seam cannot forget it.
        _isFilterPanelCollapsed = !display.Dashboard.FilterPanelExpanded;
        _filterPanelWidth = display.Dashboard.ClampedFilterPanelWidth;
        Filters.IsPanelCollapsed = _isFilterPanelCollapsed;
        Filters.TogglePanelCommand = ToggleFilterPanelCommand;
        Filters.ExpandPanelOnCommand = ExpandFilterPanelOnCommand;

        // Spec 12.2's notice, from the document this page was already handed by value.
        ShowScanFilterNotice = ScanFilterConfig.ShowsSetupNotice(general);

        Seed = new TargetListingCriteria { PageSize = general.DefaultPageSize };

        // Phase review item 8: the panel's first option-list load, started here rather than from
        // the panel's own constructor. OnFiltersChanged reaches Targets, so it runs only once every
        // handler above is wired AND everything those handlers touch exists.
        Filters.Reload();

        // The first page is fetched through the same debounced path as every later one, so there
        // is exactly one call site.
        RequestQuery();

        // Ruling Q18: on dashboard load and after each ScanFinished, never on a timer, always off
        // the UI thread.
        ProbeRoots();
    }

    public FilterPanelViewModel Filters { get; }

    /// <summary>The target list: rows, sort, paging and column visibility (Task 7). It never
    /// queries; it contributes its state to the criteria through
    /// <see cref="TargetListViewModel.ApplyTo"/> inside this page's single query call site.</summary>
    public TargetListViewModel Targets { get; }

    /// <summary>Spec 12.2's collapsed strip, the same 48 pixels the navigation rail and the Nights
    /// ledger already collapse to (DESIGN.md section 6).</summary>
    public const double CollapsedStripWidth = 48d;

    /// <summary>Spec 12.2's collapsible left panel. Unlike every filter value, which is session
    /// state, this persists per profile in <c>display.dashboard.filter_panel_expanded</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterPanelColumnWidth))]
    [NotifyPropertyChangedFor(nameof(IsFilterPanelShowingStrip))]
    private bool _isFilterPanelCollapsed;

    partial void OnIsFilterPanelCollapsedChanged(bool value)
    {
        if (value)
        {
            // A collapse is the user putting the panel away, which ends the forced state as surely
            // as a resize does.
            IsFilterPanelForcedOpen = false;
        }

        Filters.IsPanelCollapsed = IsFilterPanelShowingStrip;
        PersistPanelState();
    }

    /// <summary>Whether an explicit gesture has opened the panel at a width where the layout bound
    /// would otherwise render the strip. The bound governs what the application decides by itself,
    /// on load and on resize; a gesture the user makes in the window in front of them wins, because
    /// the alternative is a chevron that toggles a stored flag and changes nothing on screen, which
    /// reads as a broken button, with the filters unreachable at every window below about 1198
    /// pixels, the shipped 1024 minimum included.</summary>
    /// <remarks>
    /// A view flag and nothing else: it is never read from the document, never written to it, and
    /// <see cref="PersistPanelState"/> is not called for it. It is cleared by a collapse and by any
    /// change of <see cref="MaxRenderedPanelWidth"/>, so a resize puts the bound back in charge.
    /// While it is set the panel renders at its 220 floor and the target list is clipped at its
    /// trailing edge, exactly as it was before the bound existed.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterPanelColumnWidth))]
    [NotifyPropertyChangedFor(nameof(IsFilterPanelShowingStrip))]
    private bool _isFilterPanelForcedOpen;

    partial void OnIsFilterPanelForcedOpenChanged(bool value)
        => Filters.IsPanelCollapsed = IsFilterPanelShowingStrip;

    /// <summary>The widest the panel may be rendered at the page's current width, written by
    /// <c>DashboardView.axaml.cs</c> from the view's own Bounds (fixer-list item 6, phase-review
    /// P2's interaction between the 1024 window minimum, the 480 panel maximum and the target
    /// list's no-scroller trimming rule). It is a layout bound and nothing else: it is never read
    /// from the document, never written to it, and <see cref="PersistPanelState"/> is not called
    /// for it, so widening the window gives the stored width and the stored state straight
    /// back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterPanelColumnWidth))]
    [NotifyPropertyChangedFor(nameof(IsFilterPanelShowingStrip))]
    private double _maxRenderedPanelWidth = double.PositiveInfinity;

    partial void OnMaxRenderedPanelWidthChanged(double value)
    {
        // The window changed width, so whatever the user forced open in the old one is spent and
        // the bound decides again.
        IsFilterPanelForcedOpen = false;
        Filters.IsPanelCollapsed = IsFilterPanelShowingStrip;
    }

    /// <summary>Whether the panel renders as the 48 pixel strip: because the user collapsed it, or
    /// because even the 220 floor would leave the target list short of what a row needs and no
    /// gesture has overridden that. The views bind this rather than
    /// <see cref="IsFilterPanelCollapsed"/>, which stays the stored state alone.</summary>
    public bool IsFilterPanelShowingStrip
        => IsFilterPanelCollapsed
            || (MaxRenderedPanelWidth < DashboardDisplaySettings.MinPanelWidth && !IsFilterPanelForcedOpen);

    private double _filterPanelWidth;

    /// <summary>The expanded panel's committed width, clamped 220 to 480 on every write through
    /// the same three constants the read clamp uses (spec 12.2, ruling E1). Hand written rather
    /// than generated because the clamp belongs in the setter: a generated setter would store the
    /// out-of-range figure and leave every reader to remember the range.</summary>
    /// <remarks>
    /// A clamped write that does not move the stored figure raises nothing here, because nothing
    /// changed: the escape review finding P2 names is in the rendered column, not in this value,
    /// and it is closed where it happens, in <c>DashboardView.axaml.cs</c>. Raising the property
    /// change here as well was tried and measured first, and it does not close it: Avalonia's
    /// binding expression caches the last value it produced and skips publishing an unchanged one,
    /// so the second consecutive over-drag still rendered 680 with the raise in place. That comment
    /// carries the whole explanation.
    /// </remarks>
    public double FilterPanelWidth
    {
        get => _filterPanelWidth;
        set
        {
            var clamped = Math.Clamp(
                value,
                DashboardDisplaySettings.MinPanelWidth,
                DashboardDisplaySettings.MaxPanelWidth);

            if (SetProperty(ref _filterPanelWidth, clamped))
            {
                OnPropertyChanged(nameof(FilterPanelColumnWidth));
                PersistPanelState();
            }
        }
    }

    /// <summary>The panel column's width in both states. The view binds this one member, so the
    /// two states are one column and never two. The expanded width is the stored one bounded by
    /// what the target list needs at the page's current width, and never below the 220 floor, which
    /// is what a forced-open panel renders at; the stored figure itself is untouched.</summary>
    public GridLength FilterPanelColumnWidth => IsFilterPanelShowingStrip
        ? new GridLength(CollapsedStripWidth)
        : new GridLength(Math.Max(
            DashboardDisplaySettings.MinPanelWidth,
            Math.Min(FilterPanelWidth, MaxRenderedPanelWidth)));

    /// <summary>Bound to the chevron on the expanded panel and to the chevron at the top of the
    /// collapsed strip. One command for both so the two cannot disagree.</summary>
    /// <remarks>
    /// It turns on what is drawn rather than on the stored flag. At a width the bound governs the
    /// two disagree: the stored flag says expanded while the strip is what renders, and flipping
    /// the flag would take two presses to open the panel, the first of them changing nothing.
    /// </remarks>
    [RelayCommand]
    private void ToggleFilterPanel()
    {
        if (IsFilterPanelShowingStrip)
        {
            OpenFilterPanel();
        }
        else
        {
            IsFilterPanelCollapsed = true;
        }
    }

    /// <summary>The one way the panel opens, from either gesture. The forced flag is set before the
    /// stored one, so the strip is already gone by the time the flag's own notifications run, and
    /// a panel that was never collapsed still opens: setting an unchanged flag raises nothing.
    /// </summary>
    private void OpenFilterPanel()
    {
        IsFilterPanelForcedOpen = MaxRenderedPanelWidth < DashboardDisplaySettings.MinPanelWidth;
        IsFilterPanelCollapsed = false;
    }

    /// <summary>Spec 12.2: clicking a section's label on the collapsed strip expands the panel with
    /// that section already open. The other six are left exactly as they were, so reopening does
    /// not rearrange the panel the user left behind.</summary>
    [RelayCommand]
    private void ExpandFilterPanelOn(string sectionKey)
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the gate is
        // repeated here rather than only declared.
        if (string.IsNullOrEmpty(sectionKey))
        {
            return;
        }

        var section = Filters.Sections.FirstOrDefault(
            candidate => string.Equals(candidate.Key, sectionKey, StringComparison.Ordinal));

        if (section is null)
        {
            return;
        }

        OpenFilterPanel();
        section.IsExpanded = true;
    }

    /// <summary>The tail of the panel's write chain, so a case can await the write instead of
    /// sleeping. Matches <c>TargetListViewModel.PendingPersist</c>.</summary>
    internal Task PendingPanelPersist => _display.Pending;

    /// <summary>The one place both <c>display.dashboard</c> keys are written. The write clamp calls
    /// the same Math.Clamp through the same three constants the read clamp does, and it writes
    /// those two keys alone: every filter value is session state and stays out of the document
    /// (spec 12.2's Layout paragraph).</summary>
    private void PersistPanelState()
    {
        var expanded = !IsFilterPanelCollapsed;
        var width = (int)Math.Round(Math.Clamp(
            FilterPanelWidth,
            DashboardDisplaySettings.MinPanelWidth,
            DashboardDisplaySettings.MaxPanelWidth));

        _display.Write(document => document with
        {
            Dashboard = document.Dashboard with
            {
                FilterPanelExpanded = expanded,
                FilterPanelWidth = width,
            },
        });
    }

    // Summary strip (spec 12.2). All three figures describe the whole filtered set, from
    // TargetListingPage's totals, not the rows of the current page.

    /// <summary>Total integration of the filtered set, in hours.</summary>
    [ObservableProperty]
    private double _totalIntegrationHours;

    [ObservableProperty]
    private int _totalGroups;

    [ObservableProperty]
    private int _totalFrames;

    /// <summary>Spec 12.2's "filtered" marker.</summary>
    [ObservableProperty]
    private bool _isFiltered;

    /// <summary>Spec 12.2 requires this label to stay distinct from the Statistics page's
    /// "resolved targets": the figure counts unresolved <c>obj:</c> groups too. A constant here,
    /// asserted by a test, so a later rename cannot quietly merge the two concepts.</summary>
    public string GroupsLabel => "Groups";

    /// <summary>The criteria both the panel and the list project onto. Sort and paging belong to
    /// the dashboard rather than the filter panel: <see cref="FilterPanelViewModel.BuildCriteria"/>
    /// carries those four properties through untouched and
    /// <see cref="TargetListViewModel.ApplyTo"/> then sets them (Task 7).</summary>
    internal TargetListingCriteria Seed { get; set; }

    /// <summary>The criteria behind the figures currently shown.</summary>
    public TargetListingCriteria Criteria { get; private set; } = new();

    /// <summary>The in-flight debounce-and-query, so a test can await it instead of sleeping.
    /// Mirrors <c>WatcherService.PendingWork</c>.</summary>
    internal Task? PendingQuery { get; private set; }

    /// <summary>The last query failure, logged and kept rather than swallowed. Observable and
    /// public so Task 8's error banner binds to it; null once a query succeeds again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure), nameof(FailureText))]
    private Exception? _lastQueryFailure;

    /// <summary>The last failure from the Run Scan button, kept the same way (review item 1).
    /// Cleared when a scan starts; a cancellation is not a failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure), nameof(FailureText))]
    private Exception? _lastScanFailure;

    /// <summary>Drives the failure banner: spec 12.10 wants a failure reported, not a silent
    /// empty list.</summary>
    public bool HasFailure => LastQueryFailure is not null || LastScanFailure is not null;

    /// <summary>The banner's text. A scan that could not start is the more recent news when both
    /// are set, because it is the one the user just asked for.</summary>
    public string FailureText => LastScanFailure is not null
        ? "The scan could not be started. See the log for details."
        : "The dashboard query failed. The figures below are the last that loaded.";

    /// <summary>Clears both failures; a later one sets either back and shows the banner again.</summary>
    [RelayCommand]
    private void DismissFailure()
    {
        LastQueryFailure = null;
        LastScanFailure = null;
    }

    /// <summary>What the list region shows (spec 12.10). Evaluated once per completed query, from
    /// the criteria that query actually ran with, so the two empty states are mutually exclusive
    /// by construction.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRows), nameof(ShowNoFramesYet), nameof(ShowFirstScan), nameof(ShowNoMatches), nameof(ShowEmptyState))]
    private DashboardContentState _contentState;

    public bool ShowRows => ContentState == DashboardContentState.Rows;

    public bool ShowNoFramesYet => ContentState == DashboardContentState.NoFramesYet && !IsScanRunning;

    /// <summary>The empty library's first scan: NoFramesYet while a scan runs. The page shows that
    /// scan's progress, not an instruction to add a folder the scan is already walking.</summary>
    public bool ShowFirstScan => ContentState == DashboardContentState.NoFramesYet && IsScanRunning;

    public bool ShowNoMatches => ContentState == DashboardContentState.NoMatches;

    public bool ShowEmptyState => ShowNoFramesYet || ShowFirstScan || ShowNoMatches;

    /// <summary>The scan status the first-scan block binds its message and bar to. Null in a
    /// shell built without one.</summary>
    public ScanStatusService? ScanStatus => _scanStatus;

    private bool IsScanRunning => _scanStatus?.IsRunning == true;

    /// <summary>Configured scan roots that could not be reached, in configured order (spec
    /// 12.10). The banner names every one of them and never replaces the list.</summary>
    public ObservableCollection<string> UnreachableRoots { get; } = [];

    /// <summary>The snapshot dismiss took, null once nothing has been dismissed or a probe
    /// cleared it. The banner returns only when a later probe's set differs from it.</summary>
    private HashSet<string>? _dismissedUnreachableRoots;

    public bool HasUnreachableRoots => UnreachableRoots.Count > 0
        && (_dismissedUnreachableRoots is null || !_dismissedUnreachableRoots.SetEquals(UnreachableRoots));

    [RelayCommand]
    private void DismissUnreachableRoots()
    {
        _dismissedUnreachableRoots = [.. UnreachableRoots];
        OnPropertyChanged(nameof(HasUnreachableRoots));
    }

    // ---- Phase 14B Task 5: spec 12.2's scan filter notice (PAR-014). ----

    /// <summary>
    /// Spec 12.2's notice, above the summary strip, while the stored scan filters are still only
    /// what the setup wizard seeded and unreviewed. One computation,
    /// <see cref="ScanFilterConfig.ShowsSetupNotice"/>, which the Library tab's own notice also
    /// reads, so the two surfaces cannot disagree.
    /// </summary>
    /// <remarks>It informs and never blocks: nothing on this page is disabled by it, and a scan
    /// runs with it showing.</remarks>
    [ObservableProperty]
    private bool _showScanFilterNotice;

    /// <summary>
    /// Re-reads the notice condition from a general document. <c>AppHost</c> calls it from the
    /// <c>GeneralChanged</c> subscription this page already owns, so saving a rule on the Library
    /// tab takes the notice down without a restart.
    /// </summary>
    public void FollowScanFilters(GeneralSettings general)
    {
        ArgumentNullException.ThrowIfNull(general);
        var show = ScanFilterConfig.ShowsSetupNotice(general);
        _post(() => ShowScanFilterNotice = show);
    }

    /// <summary>
    /// Raised by the notice's one action, Review. The shell opens Settings on the Library tab
    /// (spec 12.2); the route is <c>MainWindowViewModel</c>'s, the same idiom the Statistics
    /// timeline's <c>DateRangeRequested</c> already uses, rather than a second one.
    /// </summary>
    /// <remarks>
    /// Carries a <see cref="SettingsDestination"/> since Phase 15B Task 5c, so the shell's one
    /// handler answers this notice and the Statistics page's Guiding notice through one switch
    /// rather than through a handler each (design lesson 1 at the third destination). The event
    /// keeps its own name because the notice it belongs to is this page's.
    /// </remarks>
    public event EventHandler<SettingsDestination>? ReviewScanFiltersRequested;

    /// <summary>Spec 12.2's Review action. The notice has no "use defaults" action: the port's
    /// wizard has already written those defaults by the time the notice can show.</summary>
    /// <remarks>Polish wave 1, ruling 1: the notice goes down first, then the reviewed flag is
    /// persisted, then the route runs. A refused write is logged and the click still routes,
    /// because the flag is a convenience and the Settings tab is the action.</remarks>
    [RelayCommand]
    private void ReviewScanFilters()
    {
        ShowScanFilterNotice = false;
        try
        {
            _mutateGeneral?.Invoke(general => general with { ScanFiltersReviewed = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The dashboard could not persist the scan filter review");
        }

        ReviewScanFiltersRequested?.Invoke(this, SettingsDestination.LibraryNameRules);
    }

    /// <summary>The in-flight debounce-and-search, so a test can await it instead of sleeping.</summary>
    internal Task? PendingSearch { get; private set; }

    /// <summary>The in-flight reachability probe, so a test can await it instead of sleeping.</summary>
    internal Task? PendingRootProbe { get; private set; }

    /// <summary>Opens (or restarts) the debounce window. Every change reaches the query through
    /// here: N rapid keystrokes, a pill toggle, a combo selection and Reset all take the same
    /// path, so they coalesce into exactly one query.</summary>
    internal void RequestQuery() => PendingQuery = _queryWindow.Restart(RunQueryAsync);

    private void OnFiltersChanged(object? sender, EventArgs e)
    {
        // A narrower filtered set can have fewer pages than the one the user is standing on, so
        // any filter change goes back to page one. Silently: this change is already opening a
        // query window.
        Targets.ResetPage();
        RequestQuery();
    }

    private void OnTargetsChanged(object? sender, EventArgs e) => RequestQuery();

    /// <summary>A dashboard row was activated, or a session line's Deep dive action was (spec
    /// 12.2 "row click navigation", Phase 14B Task 6). Forwarded from
    /// <see cref="Dashboard.TargetListViewModel.TargetOpened"/> so the shell subscribes to the
    /// page it owns rather than reaching two levels into it. Unsubscribed in
    /// <see cref="Dispose"/> alongside the others.</summary>
    public event EventHandler<TargetOpenRequest>? TargetOpened;

    private void OnTargetOpened(object? sender, TargetOpenRequest request) => TargetOpened?.Invoke(this, request);

    /// <summary>Re-raises the target list's mosaic link click (spec 12.2, Phase 18) for the shell,
    /// on the same terms as <see cref="TargetOpened"/>.</summary>
    public event EventHandler<Guid>? MosaicOpened;

    private void OnMosaicOpened(object? sender, Guid mosaicId) => MosaicOpened?.Invoke(this, mosaicId);

    /// <summary>
    /// Spec 12.10's Run Scan button, on the "No frames catalogued yet" state. Progress is already
    /// rendered by the status bar (Task 5); this button shows none of its own.
    /// </summary>
    /// <remarks>
    /// Review item 1. The delegate is invoked inside a <see cref="Task.Run(Func{Task})"/>, not
    /// awaited straight from the command: <c>ScanCoordinator.RunAsync</c> does its directory walk,
    /// its known-file load and its classification pass synchronously before its first await, so
    /// calling it from the command handler would freeze the window for the length of a scan. The
    /// same call is also the one that validates the configured roots and throws, and there is no
    /// global dispatcher exception handler to catch that, so the failure is caught here and
    /// surfaced through <see cref="LastScanFailure"/> rather than being rethrown on the UI thread.
    /// </remarks>
    /// <remarks>
    /// No <see cref="CancellationToken"/> parameter (FIXER LIST F24, and Task 8's deviation D10):
    /// a command built from a <c>Func&lt;CancellationToken, Task&gt;</c> cancels the in-flight
    /// token on a second <c>Execute</c>, and <c>Task.Run</c> with an already-cancelled token skips
    /// the delegate, so a second press would abort the running scan instead of being refused by
    /// the guard below. The scan is cancelled through the status bar's own Cancel, which reaches
    /// <c>ScanCoordinator.Cancel</c>; the page lifetime is what stops the work at shutdown.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunScan))]
    private async Task RunScanAsync()
    {
        // TRACKING item 13: CanExecute is the affordance, the body is the guard. A direct Execute,
        // and a click that lands between a scan starting and the command being notified, both
        // arrive here, and the second press is refused rather than cancelling the first.
        if (_startScan is null || !CanRunScan())
        {
            return;
        }

        var token = _lifetime.Token;
        LastScanFailure = null;
        try
        {
            await Task.Run(() => _startScan(token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled the scan from the status bar. Not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The dashboard could not start a scan");
            _post(() => LastScanFailure = ex);
        }
    }

    // Phase 7 fixer item 1: the coordinator refuses a scan while the unresolved-name retry holds
    // the resolution lease, so the button greys on that too rather than offering a run that comes
    // straight back as "pending".
    private bool CanRunScan()
        => _startScan is not null && _scanStatus?.IsRunning != true && _scanStatus?.ResolutionInProgress != true;

    private void OnScanStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(ScanStatusService.IsRunning)
            or nameof(ScanStatusService.ResolutionInProgress))
        {
            RunScanCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is null or nameof(ScanStatusService.IsRunning))
        {
            OnPropertyChanged(nameof(ShowNoFramesYet));
            OnPropertyChanged(nameof(ShowFirstScan));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    // Free text alone pins nothing, so it never reaches the listing query; it drives the results
    // dropdown, on the same debounce window as every other input (ruling Q3).
    private void OnSearchTextChanged(object? sender, EventArgs e)
    {
        // The term is captured here, on the raising thread, so the window searches the text that
        // opened it rather than whatever the box holds when the debounce elapses.
        var term = Filters.SearchText;
        PendingSearch = _searchWindow.Restart((generation, token) => RunSearchAsync(generation, term, token));
    }

    private async Task RunSearchAsync(int generation, string term, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                // An emptied box closes the dropdown immediately; there is nothing to wait for.
                _post(() => Publish(generation, []));
                return;
            }

            await _searchWindow.Wait(cancellationToken).ConfigureAwait(false);

            var results = await Task
                .Run(() => _search(term), cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // The order is the query's, unchanged: the view-model surfaces the ranking, it does
            // not re-rank (ruling Q8).
            _post(() => Publish(generation, [.. results.Select(result => new SearchResultViewModel(result))]));
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this window.
        }
        catch (Exception ex)
        {
            // A failed search leaves the dropdown as it was; it must not take the page down.
            _logger.LogWarning(ex, "The dashboard search query failed");
        }

        void Publish(int published, IReadOnlyList<SearchResultViewModel> results)
        {
            if (_searchWindow.IsCurrent(published))
            {
                Filters.PublishSearchResults(results);
            }
        }
    }

    /// <summary>Ruling Q18: off the UI thread, on load and after each scan, never on a timer.</summary>
    private void ProbeRoots() => PendingRootProbe = _probeWindow.Restart(RunProbeAsync);

    // Review item 4: a disconnected share can make one probe take seconds, so a probe started by
    // an earlier scan can still be in flight when a later one starts. Only the newest may publish,
    // the same generation guard the listing query and the search already use. No debounce: a probe
    // is not driven by typing. F6: the token is the host lifetime's, so a probe parked on a dead
    // share does not publish into a disposed page.
    private Task RunProbeAsync(int generation, CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            try
            {
                var unreachable = _probeRoots();
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _post(() =>
                {
                    if (!_probeWindow.IsCurrent(generation))
                    {
                        return;
                    }

                    UnreachableRoots.Clear();
                    foreach (var root in unreachable)
                    {
                        UnreachableRoots.Add(root);
                    }

                    if (UnreachableRoots.Count == 0)
                    {
                        _dismissedUnreachableRoots = null;
                    }

                    OnPropertyChanged(nameof(HasUnreachableRoots));
                });
            }
            catch (OperationCanceledException)
            {
                // The host is going away, or a newer probe superseded this one.
            }
            catch (Exception ex)
            {
                // The banner keeps whatever it already showed. A probe that throws is not worth
                // failing the page over.
                _logger.LogWarning(ex, "The scan root reachability probe failed");
            }
        },
        cancellationToken);

    /// <summary>
    /// A custom column was created, renamed, re-optioned, reordered or deleted somewhere else in the
    /// application. Called on the UI thread by the composition root's own route.
    /// </summary>
    /// <remarks>
    /// Exactly what <see cref="OnScanFinished"/> does minus the root probe, and for the same reason:
    /// the option lists the panel offers and the columns the rows draw are both built from the
    /// definition list. <c>Reload</c> re-reads the definitions on its own background thread and
    /// publishes the Custom section, and the query re-reads them on the listing hop and gives a new
    /// column its own row entry, switched off. No scan and no restart.
    /// <para>
    /// Nothing here can interrupt a reader typing into a cell: a definition is only written from a
    /// Settings tab, and reaching one closes the Target detail page and puts the dashboard behind the
    /// Settings page.
    /// </para>
    /// </remarks>
    internal void RefreshCustomColumns()
    {
        Filters.Reload();
        RequestQuery();
    }

    /// <summary>A mosaic write committed somewhere else in the application (accept, delete, the
    /// Create mosaic dialog, a removed night or panel). The listing query carries spec 12.2's
    /// mosaic links, so the page re-runs it, debounced, as it does after a scan. Called on the UI
    /// thread by the composition root's own route.</summary>
    internal void RefreshMosaicLinks() => RequestQuery();

    private void OnScanFinished(object? sender, EventArgs e)
    {
        // A root that came back (or went away) since the last scan is picked up here, which is
        // also when its frames appear or stop appearing (ruling Q18).
        ProbeRoots();

        // A scan can add a filter, a camera or a header key the panel has never offered, so the
        // option lists are refreshed alongside the page. Reload does its own queries on a
        // background thread; nothing here blocks the UI thread.
        Filters.Reload();
        RequestQuery();
    }

    private async Task RunQueryAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await _queryWindow.Wait(cancellationToken).ConfigureAwait(false);

            var criteria = Targets.ApplyTo(Filters.BuildCriteria(Seed));

            // Spec 12.2's refetch dim: the rows already on screen dim and stop taking input for
            // as long as this query runs. Posted, not written directly, because everything past
            // the debounce wait above runs off the UI thread (TRACKING section 5's threading
            // rule; ConfigureAwait(false) belongs below this view-model, never above it).
            _post(() => Targets.SetRefetching(true));

            // The query itself never runs on the UI thread: a slow dashboard query must not
            // freeze the window.
            var page = await Task.Run(() => _list(criteria), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Spec 12.15: the custom column definitions and this page's target-scope values, on
            // the same background hop as the listing itself, so nothing about them is read on the
            // UI thread and the two describe the same page of rows.
            var custom = await Task
                .Run(() => Targets.ReadCustomColumns(page), cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _post(() => Apply(generation, criteria, page, custom));
        }
        catch (OperationCanceledException)
        {
            // A newer change superseded this window. Nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The dashboard listing query failed; the previous figures are kept");
            _post(() =>
            {
                if (_queryWindow.IsCurrent(generation))
                {
                    LastQueryFailure = ex;

                    // A failed query still ends the refetch it started (IsRefetching truth
                    // table): without this a query that throws leaves the list dimmed with
                    // nothing further in flight to ever clear it.
                    Targets.SetRefetching(false);
                }
            });
        }
    }

    /// <summary>
    /// F6. This view-model is a DI singleton, so the host owns its lifetime: disposing the host
    /// disposes it. Cancels the host-lifetime source every window is linked to, then drops every
    /// subscription, so nothing it started can publish into a page whose database is gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        Filters.Changed -= OnFiltersChanged;
        Filters.SearchTextChanged -= OnSearchTextChanged;
        Targets.Changed -= OnTargetsChanged;
        Targets.TargetOpened -= OnTargetOpened;
        Targets.MosaicOpened -= OnMosaicOpened;
        if (_scanStatus is not null)
        {
            _scanStatus.ScanFinished -= OnScanFinished;
            _scanStatus.PropertyChanged -= OnScanStatusChanged;
        }

        // Spec 12.15: every open cell is flushed and disposed with the page that owns it, so the
        // last second of typing in one is not lost at shutdown.
        Targets.Dispose();

        _queryWindow.Dispose();
        _searchWindow.Dispose();
        _probeWindow.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Joins whatever background work is in flight. Test-only: a fixture that deletes a
    /// temp database must not race a query still holding a connection to it. Faults are ignored
    /// here; the tests that care assert them through the failure properties.</summary>
    internal void Quiesce(TimeSpan timeout)
    {
        var pending = new[] { PendingQuery, PendingSearch, PendingRootProbe, Filters.PendingReload }
            .Where(task => task is not null)
            .Select(task => task!);
        Task.WhenAll(pending).ContinueWith(_ => { }, TaskScheduler.Default).Wait(timeout);
    }

    // Runs on the UI thread, through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Apply(
        int generation,
        TargetListingCriteria criteria,
        TargetListingPage page,
        CustomColumnPage custom)
    {
        if (!_queryWindow.IsCurrent(generation))
        {
            return;
        }

        Criteria = criteria;
        TotalIntegrationHours = page.TotalIntegrationSeconds / 3600d;
        TotalGroups = page.TotalGroups;
        TotalFrames = page.TotalFrames;
        IsFiltered = criteria.AnyFilterActive;

        // Phase review item 3: Load clamps the page when the filtered set shrank under the page the
        // user is standing on, and that raises Changed, which opens a newer query window. Anything
        // written past this point would belong to a request that is already superseded.
        Targets.Load(page, custom);
        if (!_queryWindow.IsCurrent(generation))
        {
            return;
        }

        // The refetch dim ends here, inside the same generation guard that already protects the
        // rest of this method: a stale response that lands after a newer window opened must not
        // clear a dim the newer window is still holding.
        Targets.SetRefetching(false);

        LastQueryFailure = null;

        // Spec 12.10, evaluated from the criteria this page was actually produced with, so the
        // two empty states cannot both be reachable for the same result.
        ContentState = page.TotalGroups > 0
            ? DashboardContentState.Rows
            : criteria.AnyFilterActive
                ? DashboardContentState.NoMatches
                : DashboardContentState.NoFramesYet;
    }
}
