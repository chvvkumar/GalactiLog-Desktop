using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.ViewModels;

/// <summary>
/// The shell (design-spec 12): the seven rail destinations, which one is selected, the page the
/// content region shows, and how wide that region may grow.
/// </summary>
/// <remarks>
/// Takes a <see cref="GeneralSettings"/> value rather than a settings store, and a constructed
/// <see cref="DashboardViewModel"/> rather than a service provider, so it constructs in a unit
/// test with no window and no database (design-spec 18.3). AppHost passes
/// <c>settingsStore.GetGeneral()</c>. Every view-model in the application follows this rule.
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DashboardViewModel _dashboard;
    private readonly Func<StatisticsViewModel> _statisticsFactory;
    private readonly Func<AnalysisViewModel> _analysisFactory;
    private readonly Func<ActivityViewModel> _activityFactory;
    private readonly Func<DiagnosticsViewModel>? _diagnosticsFactory;
    private readonly Func<string, DateOnly?, TargetDetailViewModel>? _openDetail;
    private readonly Func<MosaicsPageViewModel>? _mosaicsFactory;
    private readonly Func<Guid, MosaicDetailViewModel>? _openMosaic;

    // Held for spec 12.2's Review route, which selects this page's Library tab. The same
    // instance the rail's "settings" entry carries.
    private readonly SettingsViewModel _settings;

    // The Statistics page once something has read its rail entry, and null until then. Held only
    // so Dispose can drop the DateRangeRequested subscription the first construction attaches;
    // an unvisited page has no subscription to drop (FIXER LIST F21).
    private StatisticsViewModel? _statistics;
    private bool _disposed;

    /// <param name="settings">Spec 12.7's Settings page (Phase 7 Task 3, ruling Q2). A
    /// constructed page like <paramref name="dashboard"/>, not a placeholder: Phase 7 owns the
    /// Targets tab, and Phases 9 and 10 replace one placeholder tab inside it at a time.</param>
    /// <param name="statistics">Builds spec 12.5's Statistics page (Phase 9 Task 3) on the first
    /// read of its rail entry. A factory rather than a constructed page since FIXER LIST F21: the
    /// page runs Task 2's full-library aggregate in its own constructor, which every application
    /// start paid whether or not the user ever opened it.</param>
    /// <param name="analysis">Builds spec 12.14's Analysis page (Phase 17 Task 4) on the first read
    /// of its rail entry. Required and positional like <paramref name="statistics"/> and lazy for
    /// the reason FIXER LIST F21 records: the page reads the display document and resolves two
    /// singletons in its constructor, and an application start that never opens it pays
    /// nothing.</param>
    /// <param name="activity">Builds spec 12.6's Activity page (Phase 9 Task 4) on the first read
    /// of its rail entry. Required and positional like <paramref name="statistics"/> since FIXER
    /// LIST F12, and lazy for the same reason: the page loads its first activity page in its own
    /// constructor.</param>
    /// <param name="diagnostics">Builds spec 12.8's Diagnostics page (Phase 10 Task 1) on the
    /// first read of its rail entry, like <paramref name="statistics"/> and
    /// <paramref name="activity"/>. Optional for the reason <paramref name="openDetail"/> is: the
    /// page needs a database, and null leaves the rail entry a placeholder so a shell still
    /// constructs in a unit test with no queries. <c>AppHost</c> always supplies it.</param>
    /// <param name="openDetail">Builds a detail page for a dashboard group key and, since
    /// PAR-018, for the optional night it opens on. AppHost binds it to the DI-resolved queries;
    /// a unit test passes a lambda. A delegate rather than a service provider, like every other
    /// seam in this application (design-spec 18.3). Null leaves the route inert, which is what
    /// lets a test construct a shell with no queries. The date is null on every route that exists
    /// today; spec 12.4's "Opening the page" block says what a non-null one does, and Phase 14B's
    /// dashboard session rows are the first caller to pass one.</param>
    /// <param name="mosaics">Builds spec 12.17's Mosaics page (Phase 18) on the first read of its
    /// rail entry, lazy for the reason <paramref name="statistics"/> is. Optional for the reason
    /// <paramref name="diagnostics"/> is: null leaves the entry a placeholder, so a shell still
    /// constructs in a unit test with no queries. <c>AppHost</c> always supplies it.</param>
    /// <param name="openMosaic">Builds spec 12.17's mosaic detail page for a mosaic id (Phase 18
    /// Task 5), the overlay's second kind of page. Null leaves <see cref="OpenMosaic"/> inert, for
    /// the reason <paramref name="openDetail"/> may be null.</param>
    public MainWindowViewModel(
        GeneralSettings general,
        DashboardViewModel dashboard,
        StatusBarViewModel statusBar,
        SettingsViewModel settings,
        Func<StatisticsViewModel> statistics,
        Func<AnalysisViewModel> analysis,
        Func<ActivityViewModel> activity,
        Func<DiagnosticsViewModel>? diagnostics = null,
        Func<string, DateOnly?, TargetDetailViewModel>? openDetail = null,
        Func<MosaicsPageViewModel>? mosaics = null,
        Func<Guid, MosaicDetailViewModel>? openMosaic = null,
        PendingEditsRegistry? pendingEdits = null)
    {
        PendingEdits = pendingEdits ?? new PendingEditsRegistry();
        _mosaicsFactory = mosaics;
        _openMosaic = openMosaic;
        StatusBar = statusBar;
        _dashboard = dashboard;
        _statisticsFactory = statistics;
        _analysisFactory = analysis;
        _activityFactory = activity;
        _diagnosticsFactory = diagnostics;
        _openDetail = openDetail;
        _settings = settings;
        dashboard.TargetOpened += OnTargetOpened;
        dashboard.MosaicOpened += OnMosaicOpenRequested;

        // Phase 14B Task 5. Spec 12.2's scan filter notice carries one action, Review, which
        // "opens Settings on the Library tab with the rule editor in view". Two pages, so the
        // route is the shell's, and it is the same idiom OnDateRangeRequested already uses.
        //
        // Phase 15B Task 5c: onto the one Settings handler, which the notice's event now carries a
        // destination for. This is the shell's only constructor-time subscription to it; the
        // Statistics page's is attached when that page is first built, because it does not exist
        // yet here.
        dashboard.ReviewScanFiltersRequested += OnOpenSettingsRequested;

        // Mouse navigation decision 2: a Settings tab switch is one history entry. The page is
        // eager, so the subscription is constructor-time; the Analysis page's is attached when it
        // is built, and a target page's when it is opened.
        settings.PropertyChanged += OnStripChanged;

        Items =
        [
            new NavigationItem("dashboard", "Dashboard", dashboard, "IconDashboard"),

            // Spec 12.17, ruling R3: Mosaics is second on the rail, after Dashboard.
            mosaics is null
                ? new NavigationItem("mosaics", "Mosaics", new PlaceholderPageViewModel(
                    "Mosaics", "The mosaics page is not available on this surface."), "IconMosaics")
                : new NavigationItem("mosaics", "Mosaics", BuildMosaics, "IconMosaics"),
            new NavigationItem("statistics", "Statistics", BuildStatistics, "IconStatistics"),

            // Spec 12.14, ruling A3: the sixth destination, placed after Statistics and before
            // Activity, so the rail reads Dashboard, Statistics, Analysis, Activity, Diagnostics,
            // Settings.
            new NavigationItem("analysis", "Analysis", BuildAnalysis, "IconAnalysis"),
            new NavigationItem("activity", "Activity", BuildActivity, "IconActivity"),
            diagnostics is null
                ? new NavigationItem("diagnostics", "Diagnostics", new PlaceholderPageViewModel(
                    "Diagnostics", "The diagnostics page is not available on this surface."), "IconDiagnostics")
                : new NavigationItem("diagnostics", "Diagnostics", BuildDiagnostics, "IconDiagnostics"),
            new NavigationItem("settings", "Settings", settings, "IconGear"),
        ];

        // Assigned to the field, not through the generated setter, so the compiler sees the
        // non-nullable property as definitely assigned and no PropertyChanged fires during
        // construction.
        _selected = Items[0];
        _shownPage = CurrentPage;
        _history.Push(Snapshot());

        ContentMaxWidth = ResolveContentMaxWidth(general.ContentWidth);
        RootFontSize = ResolveRootFontSize(general.TextSize);
    }

    /// <summary>The rail, in design-spec 12's order.</summary>
    public IReadOnlyList<NavigationItem> Items { get; }

    /// <summary>
    /// Spec 12.6's rail badge (PAR-017): the Activity entry carries the count of unseen rows while
    /// it is above zero. Zero before the Activity destination has ever been visited this session
    /// (the entry is <c>NavigationItem.IsConstructed</c> false), matching "no badge at all" rather
    /// than a badge reading zero.
    /// </summary>
    public int UnseenActivityCount
    {
        get
        {
            var item = Items.FirstOrDefault(candidate => candidate.Key == "activity");
            return item is { IsConstructed: true } && item.Page is ActivityViewModel page
                ? page.UnseenCount
                : 0;
        }
    }

    /// <summary>The rail's width, expanded. The narrow strip keeps one icon per destination and
    /// drops the labels.</summary>
    public const double NavRailExpandedWidth = 200d;

    /// <summary>The rail's width while <see cref="IsNavRailCollapsed"/> is set.</summary>
    public const double NavRailCollapsedWidth = 48d;

    /// <summary>Whether the rail shows as a narrow strip. Collapsed on every launch (polish 1
    /// ruling 4). Session state, like the dashboard's filter panel flag: nothing persists it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NavRailWidth))]
    public partial bool IsNavRailCollapsed { get; set; } = true;

    /// <summary>What <c>MainWindow.axaml</c> gives the rail column as its width.</summary>
    public double NavRailWidth => IsNavRailCollapsed ? NavRailCollapsedWidth : NavRailExpandedWidth;

    [RelayCommand]
    private void ToggleNavRail() => IsNavRailCollapsed = !IsNavRailCollapsed;

    // NavigationItem memoizes the factory result, so each of these runs once, on the first read of
    // that rail entry's Page, on the UI thread.
    private object BuildStatistics()
    {
        var page = _statisticsFactory();

        // questions.md Q16: a timeline bar click asks for that period on the dashboard. Answered
        // here rather than by the page, for the reason the row-click route is: the shell owns the
        // content region and the rail. Attached at first construction rather than in the shell's
        // constructor, because before that there is no page to subscribe to (FIXER LIST F21).
        page.DateRangeRequested += OnDateRangeRequested;

        // Phase 15B Task 5c, spec 12.5: the Guiding section's empty notice carries a link to a
        // Settings tab at a section. Attached here and not in the shell's constructor for the
        // same reason as the line above, and it is the same shape: a routing event carrying the
        // value the route needs.
        page.OpenSettingsRequested += OnOpenSettingsRequested;
        _statistics = page;
        return page;
    }

    // Spec 12.14's Analysis page (Phase 17 Task 4), the third sibling of the two above. The page
    // raises no routing event; the one subscription is the history's, so a tab switch on the
    // strip pushes an entry (mouse navigation decision 2).
    private object BuildAnalysis()
    {
        var page = _analysisFactory();
        page.PropertyChanged += OnStripChanged;
        _analysis = page;
        return page;
    }

    // The Analysis page once built, so Dispose can drop the handler above.
    private AnalysisViewModel? _analysis;

    // Spec 12.17's Mosaics page (Phase 18). Its two routing events are the shell's, for the reason
    // the Statistics page's are: the shell owns the content region and the detail overlay.
    private object BuildMosaics()
    {
        var page = _mosaicsFactory!();
        page.MosaicOpenRequested += OnMosaicOpenRequested;
        page.TargetOpenRequested += OnMosaicTargetOpenRequested;
        _mosaics = page;
        return page;
    }

    // The Mosaics page once built, so Dispose can drop the two handlers above.
    private MosaicsPageViewModel? _mosaics;

    private void OnMosaicTargetOpenRequested(object? sender, Guid targetId) => OpenDetail(targetId.ToString());

    private void OnMosaicOpenRequested(object? sender, Guid mosaicId) => OpenMosaic(mosaicId);

    /// <summary>
    /// Spec 12.17's route to the mosaic detail page: a Mosaics table row click, a dashboard mosaic
    /// link and a completed Create mosaic dialog all land here. The page opens on this shell's one
    /// detail overlay, closing whatever detail page was open (spec 12 shell: one detail page at a
    /// time, of either kind). Its Back and a confirmed Delete mosaic close it; a target link on it
    /// opens that target, which closes it.
    /// </summary>
    public void OpenMosaic(Guid mosaicId)
    {
        if (_openMosaic is null || _disposed)
        {
            return;
        }

        CloseDetailCore();
        var page = _openMosaic(mosaicId);
        page.BackRequested += OnDetailBackRequested;
        page.OpenTargetRequested += OnMosaicTargetOpenRequested;
        Detail = page;
        Navigate(Snapshot());
    }

    private object BuildActivity()
    {
        var page = _activityFactory();

        // Spec 12.6's rail badge (PAR-017). UnseenActivityCount reads the page's own UnseenCount,
        // so the rail's binding through the parent needs to be told when that count moves,
        // including from a background reload triggered while some other destination is showing
        // (ScanStatusService.ScanFinished). NavigationItem itself stays a plain sealed class with
        // no INotifyPropertyChanged (section 7.3); this forwards from the page instead of making
        // the item observable.
        //
        // Phase 14B fixer, fixer list item 48 (task7-review P3): a named method, not an anonymous
        // handler, so Dispose can drop it beside the two subscriptions three lines apart. The page
        // is a rail page this shell does not own, so a handler left on it outlives the shell.
        page.PropertyChanged += OnActivityPropertyChanged;
        _activity = page;
        return page;
    }

    // The Activity page once something has read its rail entry, and null until then. Held only so
    // Dispose can drop the handler above; an unvisited page has no handler to drop, the same
    // shape _statistics uses for DateRangeRequested.
    private ActivityViewModel? _activity;

    private void OnActivityPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityViewModel.UnseenCount))
        {
            OnPropertyChanged(nameof(UnseenActivityCount));
        }
    }

    // Spec 12.8's Diagnostics page (Phase 10 Task 1). A lazy factory entry like the two above:
    // the page reads the whole snapshot on its first refresh, so an application start that never
    // opens it pays nothing. The factory resolves a DI singleton the Settings Diagnostics tab
    // reaches through the same delegate (coordinator ruling Q3), so both surfaces share one page,
    // one refresh and, from Task 3, one export button. The host owns its lifetime; this shell
    // does not dispose rail pages.
    private object BuildDiagnostics() => _diagnosticsFactory!();

    /// <summary>The persistent status bar (design-spec 12): scan state, progress, and cancel.
    /// Phase 5 Task 5's marshalling subscriber lives underneath it.</summary>
    public StatusBarViewModel StatusBar { get; }

    /// <summary>The app-wide unsaved-edits registry the save bar binds. AppHost passes the DI
    /// singleton the settings tabs register with; the default is an empty one so the existing
    /// construction sites and tests compile unchanged.</summary>
    public PendingEditsRegistry PendingEdits { get; }

    private NavigationItem _selected;

    /// <summary>
    /// The rail's current destination. Hand-written rather than [ObservableProperty] for the
    /// null case: the rail's ListBox writes null to this through the two-way SelectedItem
    /// binding when the user ctrl-clicks the already-selected row, and the shell always has a
    /// destination. Null is ignored, and PropertyChanged is still raised so the binding pulls
    /// the surviving value straight back into the rail.
    /// </summary>
    public NavigationItem Selected
    {
        get => _selected;
        set
        {
            if (value is null)
            {
                OnPropertyChanged();
                return;
            }

            // A rail click clears the pushed detail page first (ruling Q9), so clicking
            // Statistics while a target is open lands on Statistics and not on a detail page over
            // it. Unconditional, because re-selecting the destination the detail was pushed over
            // is also a request to see that destination. Clearing Detail raises CurrentPage
            // through the generated setter, so that case needs no raise of its own here.
            CloseDetailCore();
            if (SetProperty(ref _selected, value))
            {
                // Reading Page is what builds a factory destination (FIXER LIST F21), and the
                // Statistics page's DateRangeRequested subscription is attached by that build.
                // Forced here rather than left to the content region's binding so selecting a
                // destination constructs it whether or not a view is bound to this shell, which is
                // what keeps the rule the same in the application and in a view-model unit test.
                _ = value.Page;
                if (value.Key == "activity") { (value.Page as ActivityViewModel)?.MarkOpened(); OnPropertyChanged(nameof(UnseenActivityCount)); }
                OnPropertyChanged(nameof(CurrentPage));
            }

            // After the close above as well as after a change: re-selecting the destination under
            // a detail page is the move from that page to the destination, which is one entry.
            Navigate(Snapshot());
        }
    }

    /// <summary>
    /// The pushed detail page, or null when the rail's own destination is showing: a
    /// <see cref="TargetDetailViewModel"/> or, from Phase 18, a <see cref="MosaicDetailViewModel"/>
    /// (spec 12 shell). One nullable overlay rather than a navigation stack: a stack for a depth of
    /// one is machinery with no user (ruling Q9).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    public partial object? Detail { get; private set; }

    /// <summary>What the content region shows: the detail page when one is open, otherwise the
    /// rail destination. Raised whenever either changes.</summary>
    public object CurrentPage => Detail ?? (object)Selected.Page;

    /// <summary>Closes the detail page and returns to whichever rail destination is selected.
    /// Bound to the page's Back button through its own <c>BackCommand</c>, and to the shell's own
    /// close path.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        CloseDetailCore();
        Navigate(Snapshot());
    }

    // Spec 12.2's row click navigation, forwarded by the dashboard rather than reached two levels
    // into its target list. Phase 14B Task 6 widens the payload with the optional session date a
    // dashboard session row's Deep dive action carries (task6.md 6.2); a plain row click still
    // passes null, exactly as before.
    private void OnTargetOpened(object? sender, TargetOpenRequest request) => OpenDetail(request.GroupKey, request.SessionDate);

    // Phase 7 Task 5, ruling Q12: the merged-away callout's "Open <winner>" lands here, so the
    // one overlay is replaced rather than a second one pushed. The same route as a row click,
    // which is the point of routing it through the shell at all.
    private void OnDetailOpenTargetRequested(object? sender, string groupKey) => OpenDetail(groupKey);

    /// <summary>
    /// Spec 12.4's PAR-018 route: opens the Target detail overlay for a dashboard group key, on
    /// the night <paramref name="sessionDate"/> names when the ledger holds it and on the newest
    /// night when it is null or the target has no such night.
    /// </summary>
    /// <remarks>
    /// Public rather than private because the date has no caller inside this type: both existing
    /// routes pass null and behave exactly as they did, and Phase 14B's dashboard session rows
    /// are the first surface to pass a date. There is no URL and no query string (spec 12.4); this
    /// is the method that opens the shell's single overlay. The history below records the open; it
    /// does not stack overlays.
    /// </remarks>
    public void OpenDetail(string groupKey, DateOnly? sessionDate = null)
    {
        if (_openDetail is null || _disposed)
        {
            return;
        }

        // Opening a second target disposes the first the same way closing it does: the outgoing
        // page owns a background load and an autosave window.
        CloseDetailCore();
        var page = _openDetail(groupKey, sessionDate);
        page.BackRequested += OnDetailBackRequested;

        // FIXER LIST F10: a rename on the detail page leaves the dashboard row behind it showing
        // the old name. The dashboard's own debounced query is the reload path; nothing new is
        // built for this. A merge and an undo raise the same event, for the same reason.
        page.TargetRenamed += OnDetailTargetRenamed;
        page.OpenTargetRequested += OnDetailOpenTargetRequested;
        page.PropertyChanged += OnStripChanged;
        Detail = page;
        Navigate(Snapshot());
    }

    // The page's Back keeps its meaning, close the overlay, and that close is an entry like any
    // other navigation (mouse navigation decision 3).
    private void OnDetailBackRequested(object? sender, EventArgs e) => CloseDetail();

    // The page on screen before the latest CurrentPage change.
    private object _shownPage;

    // A target written on another page (Targets tab, merge, Maintenance) raises
    // nothing the dashboard hears, so returning to it re-reads the page. Never on the first show.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(CurrentPage) || ReferenceEquals(CurrentPage, _shownPage))
        {
            return;
        }

        _shownPage = CurrentPage;
        if (!_disposed && ReferenceEquals(CurrentPage, _dashboard))
        {
            _dashboard.RequestQuery();
        }
    }

    private void OnDetailTargetRenamed(object? sender, EventArgs e) => _dashboard.RequestQuery();

    // questions.md Q16, which amends spec 12.5: the Statistics timeline's bar click lands on the
    // dashboard with that period's date range. The filter panel's own two date properties raise
    // Changed, which opens the dashboard's existing debounced query window; no second reload path
    // is added for this.
    private void OnDateRangeRequested(object? sender, (DateOnly From, DateOnly To) range)
    {
        if (_disposed)
        {
            return;
        }

        _dashboard.Filters.DateFrom = new DateTimeOffset(range.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        _dashboard.Filters.DateTo = new DateTimeOffset(range.To.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Selected = Items.First(item => item.Key == "dashboard");
    }

    // Every route that opens Settings at a section, in one handler over one enum.
    //
    // LibraryNameRules is spec 12.2's Dashboard scan filter notice Review action, and the Phase 14B
    // fixer list item 4 behind it: spec 12.2 says "with the rule editor in view", and selecting the
    // tab alone left the editor, the fourth of eight regions inside that tab's ScrollViewer, below
    // the fold. The other two are spec 12.5's Guiding empty notice, which sends the user to the
    // Library tab's "Read PHD2 guide logs" switch when no guide log is catalogued and to the
    // Equipment tab's PHD2 profiles panel when guiding sessions exist with no profile mapped.
    //
    // Design lesson 1, taken at the third destination on the reviewer's ruling: one event type,
    // one handler, one switch. Two events still reach it, because each belongs to the notice that
    // raises it, and both carry the same value. A fourth destination is a member on
    // SettingsDestination, an arm here and the Request...InView() member that arm names.
    private void OnOpenSettingsRequested(object? sender, SettingsDestination destination)
    {
        if (_disposed)
        {
            return;
        }

        switch (destination)
        {
            case SettingsDestination.LibraryGuideLogSwitch:
                OpenSettingsAt("library", page => (page as LibraryTabViewModel)?.RequestGuideLogSwitchInView());
                break;
            case SettingsDestination.EquipmentPhd2Profiles:
                OpenSettingsAt("equipment", page => (page as EquipmentTabViewModel)?.RequestPhd2ProfilesInView());
                break;
            case SettingsDestination.LibraryNameRules:
                OpenSettingsAt("library", page => (page as LibraryTabViewModel)?.RequestNameRulesInView());
                break;

            // Review P3-1. The comment above promises that a fourth destination is a member and an
            // arm; without this, forgetting the arm routes the user silently nowhere and the notice
            // reads as a dead link. A throw here reaches a developer, never a user: every raise
            // names a member of this enum.
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(destination), destination, "No Settings route is registered for this destination.");
        }
    }

    // The spine those routes share: select the tab, ask that tab for the section, then select the
    // rail's Settings entry.
    //
    // Keyed rather than indexed, so a tab moving in spec 12.7's strip order moves no route with
    // it, and only the matched tab's Page is read, so the other ten stay unbuilt (TRACKING
    // section 6 item 16). Reading Page is what builds the lazy tab, which is what the request has
    // to land on; the request is a delegate over object because a surface whose tab is a
    // placeholder still lands on the tab and simply asks nothing of it.
    private void OpenSettingsAt(string tabKey, Action<object> request)
    {
        if (_settings.Tabs.FirstOrDefault(candidate => candidate.Key == tabKey) is not { } tab)
        {
            return;
        }

        _settings.Selected = tab;
        request(tab.Page);
        Selected = Items.First(item => item.Key == "settings");
    }

    // Setting Detail through the generated setter raises PropertyChanged for both Detail and
    // CurrentPage, so nothing here raises either by hand.
    private void CloseDetailCore()
    {
        switch (Detail)
        {
            case TargetDetailViewModel page:
                page.BackRequested -= OnDetailBackRequested;
                page.TargetRenamed -= OnDetailTargetRenamed;
                page.OpenTargetRequested -= OnDetailOpenTargetRequested;
                page.PropertyChanged -= OnStripChanged;
                Detail = null;
                page.Dispose();
                break;
            case MosaicDetailViewModel mosaic:
                mosaic.BackRequested -= OnDetailBackRequested;
                mosaic.OpenTargetRequested -= OnMosaicTargetOpenRequested;
                Detail = null;
                mosaic.Dispose();

                // The mosaic page renames, edits and deletes the mosaic the Mosaics table shows
                // under it, and that table reloads only on a job's end (ruling R5), so closing
                // re-reads the table.
                if (!_disposed)
                {
                    _ = _mosaics?.ReloadAsync(suggestions: false);
                }

                break;
        }
    }

    // ---- Mouse back and forward (.planning/mouse-navigation.md) --------------------------------
    //
    // Every path that moves the user ends in Navigate(Snapshot()): the Selected setter, OpenDetail,
    // OpenMosaic, CloseDetail (the command and the page's Back), and the three strips through
    // OnStripChanged. CloseDetailCore itself pushes nothing: OpenDetail and OpenMosaic call it to
    // replace the overlay, and target A to target B is one move, not two. Back and Forward apply
    // an entry under _applying, so the same paths run and push nothing.

    private readonly NavigationHistory _history = new();
    private bool _applying;

    /// <summary>Whether <see cref="BackCommand"/> has an entry to go to.</summary>
    public bool CanGoBack => _history.CanGoBack;

    /// <summary>Whether <see cref="ForwardCommand"/> has an entry to go to.</summary>
    public bool CanGoForward => _history.CanGoForward;

    /// <summary>The history's current entry, for tests.</summary>
    internal NavigationEntry? CurrentEntry => _history.Current;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (!_disposed && _history.Back() is { } entry)
        {
            Apply(entry);
            RaiseHistoryChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward()
    {
        if (!_disposed && _history.Forward() is { } entry)
        {
            Apply(entry);
            RaiseHistoryChanged();
        }
    }

    // Where the user is right now, as one entry. The tab key is read only off the page that is
    // showing, so a strip changing on a page that is not current snapshots to the entry already
    // current and pushes nothing.
    private NavigationEntry Snapshot() => Detail switch
    {
        TargetDetailViewModel target => new NavigationEntry(Selected.Key, DetailKind.Target, target.GroupKey, target.Mode),
        MosaicDetailViewModel mosaic => new NavigationEntry(Selected.Key, DetailKind.Mosaic, mosaic.Id.ToString(), null),
        _ => new NavigationEntry(Selected.Key, DetailKind.None, null, Selected.Key switch
        {
            "settings" => _settings.Selected.Key,
            "analysis" when Selected.IsConstructed => (Selected.Page as AnalysisViewModel)?.SelectedTab.Key,
            _ => null,
        }),
    };

    // The choke point. Applying an entry runs the same paths that call this, hence the flag.
    private void Navigate(NavigationEntry entry)
    {
        if (_applying || _disposed || !_history.Push(entry))
        {
            return;
        }

        RaiseHistoryChanged();
    }

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    // Selects the destination, rebuilds the detail page through the existing factory when the
    // entry names a different one (decision 5: fresh page; decision 4: a stale key opens anyway and
    // the page explains), then sets the tab or mode. A detail page the entry already names is
    // kept: a Back across a mode switch moves the mode, it does not reload the target.
    private void Apply(NavigationEntry entry)
    {
        _applying = true;
        try
        {
            var destination = Items.First(item => item.Key == entry.Destination);
            if (!ReferenceEquals(Selected, destination))
            {
                Selected = destination;
            }

            if (!DetailMatches(entry))
            {
                switch (entry.Detail)
                {
                    case DetailKind.Target:
                        OpenDetail(entry.DetailKey!);
                        break;
                    case DetailKind.Mosaic:
                        OpenMosaic(Guid.Parse(entry.DetailKey!));
                        break;
                    default:
                        CloseDetailCore();
                        break;
                }
            }

            switch (Detail)
            {
                case TargetDetailViewModel target:
                    target.Mode = entry.TabKey;
                    break;
                case null when Selected.Key == "settings" && entry.TabKey is { } tabKey:
                    if (_settings.Tabs.FirstOrDefault(tab => tab.Key == tabKey) is { } tab)
                    {
                        _settings.Selected = tab;
                    }

                    break;
                case null when Selected.Key == "analysis" && entry.TabKey is { } analysisTab
                    && Selected.Page is AnalysisViewModel analysis:
                    if (analysis.Tabs.FirstOrDefault(tab => tab.Key == analysisTab) is { } found)
                    {
                        analysis.SelectedTab = found;
                    }

                    break;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private bool DetailMatches(NavigationEntry entry) => Detail switch
    {
        TargetDetailViewModel target => entry.Detail == DetailKind.Target && target.GroupKey == entry.DetailKey,
        MosaicDetailViewModel mosaic => entry.Detail == DetailKind.Mosaic && mosaic.Id.ToString() == entry.DetailKey,
        _ => entry.Detail == DetailKind.None,
    };

    // The three strips (decision 2): Analysis SelectedTab, Settings Selected, target page Mode.
    private void OnStripChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnalysisViewModel.SelectedTab) or nameof(SettingsViewModel.Selected)
            or nameof(TargetDetailViewModel.Mode))
        {
            Navigate(Snapshot());
        }
    }

    /// <summary>
    /// The shell is a DI singleton, so the host owns its lifetime. Disposing it closes an open
    /// detail page, which is what flushes a note the user typed and never navigated away from
    /// before quitting.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dashboard.TargetOpened -= OnTargetOpened;
        _dashboard.MosaicOpened -= OnMosaicOpenRequested;
        _dashboard.ReviewScanFiltersRequested -= OnOpenSettingsRequested;
        _settings.PropertyChanged -= OnStripChanged;

        if (_analysis is { } analysis)
        {
            analysis.PropertyChanged -= OnStripChanged;
        }

        // Null when nobody ever opened Statistics, which is the case F21 exists to make cheap.
        if (_statistics is { } statistics)
        {
            statistics.DateRangeRequested -= OnDateRangeRequested;
            statistics.OpenSettingsRequested -= OnOpenSettingsRequested;
        }

        if (_mosaics is { } mosaicsPage)
        {
            mosaicsPage.MosaicOpenRequested -= OnMosaicOpenRequested;
            mosaicsPage.TargetOpenRequested -= OnMosaicTargetOpenRequested;
        }

        // The same, for the Activity page's rail-badge forwarding (fixer list item 48).
        if (_activity is { } activity)
        {
            activity.PropertyChanged -= OnActivityPropertyChanged;
        }

        CloseDetailCore();
    }

    /// <summary>
    /// The content region's <c>MaxWidth</c>, from <c>general.content_width</c> (design-spec
    /// 5.8.1, read by "12 shell").
    /// </summary>
    /// <remarks>Observable rather than get-only since Phase 9 Task 6: the Settings Display tab
    /// writes <c>content_width</c> while this shell is alive, and <see cref="ApplyGeneral"/>
    /// brings the change in with no restart.</remarks>
    [ObservableProperty]
    public partial double ContentMaxWidth { get; private set; }

    /// <summary>
    /// The window's root font size, from <c>general.text_size</c> (design-spec 14.4: "Root font
    /// size is user-selectable: Small 14 (default), Medium 16, Large 18, Extra large 20, set as
    /// <c>MainWindow.FontSize</c> so every relative size scales").
    /// </summary>
    /// <remarks>
    /// FIXER item 2. <c>MainWindow.axaml</c> binds <c>FontSize</c> to this and every control in
    /// the window inherits it. <c>Scales.axaml</c>'s four <c>FontSize*</c> keys stay ratios and
    /// nothing binds one to a <c>FontSize</c>; a control that needs a tier multiplies the
    /// inherited size by the ratio through <c>MultiplyConverter</c> (questions.md Q24), which is
    /// why <c>FontSizeTokenTest</c> is unchanged.
    /// </remarks>
    [ObservableProperty]
    public partial double RootFontSize { get; private set; }

    /// <summary>
    /// Re-reads the two shell-level preferences from a saved general document. <c>AppHost</c>
    /// binds this to <c>SettingsStore.GeneralChanged</c>, posted to the UI thread, so a text size
    /// or content width saved on the Settings Display tab takes effect with no restart.
    /// </summary>
    public void ApplyGeneral(GeneralSettings general)
    {
        if (_disposed)
        {
            return;
        }

        ContentMaxWidth = ResolveContentMaxWidth(general.ContentWidth);
        RootFontSize = ResolveRootFontSize(general.TextSize);
    }

    // Design-spec 14.4's four sizes. An unrecognized value, which a hand-edited settings document
    // can produce, falls back to the fresh profile's small, 14, rather than to zero, exactly as
    // ResolveContentMaxWidth falls back. SettingsStore.ValidateGeneral refuses
    // anything outside the four on the write path; this is the read side's backstop.
    internal static double ResolveRootFontSize(string textSize) => textSize switch
    {
        "medium" => 16d,
        "large" => 18d,
        "x-large" => 20d,
        _ => 14d,
    };

    // Coordinator ruling Q2: the spec names the three values and no pixel figures. normal is
    // slightly narrower than the 1280 default window, wide fits a maximized 1080p display, and
    // extra-wide (the default) fills whatever it is given. An unrecognized value, which a
    // hand-edited settings document can produce, falls back to the documented default rather
    // than to zero width.
    internal static double ResolveContentMaxWidth(string contentWidth) => contentWidth switch
    {
        "normal" => 1200d,
        "wide" => 1600d,
        _ => double.PositiveInfinity,
    };
}
