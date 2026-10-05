using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>One Date Range quick preset (design-spec 12.2): the last N days, ending today.</summary>
public sealed partial class DatePresetViewModel : ObservableObject
{
    private readonly Action<DatePresetViewModel> _apply;

    public DatePresetViewModel(string label, int days, Action<DatePresetViewModel> apply)
    {
        Label = label;
        Days = days;
        _apply = apply;
    }

    public string Label { get; }

    /// <summary>Window length in days, inclusive of today.</summary>
    public int Days { get; }

    [RelayCommand]
    private void Apply() => _apply(this);
}

/// <summary>
/// The collapsible left filter panel of the dashboard (design-spec 12.2). Holds every filter
/// value and projects them onto a <see cref="TargetListingCriteria"/>; it never queries, sorts or
/// pages. <see cref="Changed"/> tells the owning <see cref="DashboardViewModel"/> that something
/// that reaches the query moved, and that view-model owns the debounce and the query.
/// </summary>
public sealed partial class FilterPanelViewModel : ObservableObject
{
    private const string DefaultHeaderOperator = "=";

    /// <summary>The leading entry of the Camera and Telescope combo boxes. A combo box has no
    /// clear gesture of its own, so without this an equipment choice could be changed but never
    /// undone. Selecting it means "no equipment criterion", not a value named "Any".</summary>
    public const string AnyOption = "Any";

    /// <summary>Spec 12.15's group headings, in the fixed order the section draws them. User
    /// choice 17: the on-screen word for <see cref="CustomColumnScope.Session"/> is "Night", the
    /// vocabulary the rest of the application uses; the web's own captions are "Target",
    /// "Session" and "Rig".</summary>
    private static readonly (CustomColumnScope Scope, string Title)[] ScopeHeadings =
    [
        (CustomColumnScope.Target, "Target"),
        (CustomColumnScope.Session, "Night"),
        (CustomColumnScope.Rig, "Rig"),
    ];

    private readonly Func<AliasMap> _aliases;
    private readonly Func<DashboardFacets> _loadFacets;
    private readonly Func<IReadOnlyList<string>> _loadHeaderKeys;
    private readonly Func<IReadOnlyList<CustomColumnDefinition>> _loadCustomColumns;
    private readonly Func<DateOnly> _today;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private readonly bool _initialized;
    private bool _suspendChanged;

    // Phase review item 1. Two reloads can overlap (startup plus a scan that finishes during it,
    // or two scans in quick succession) and the two background queries can complete in either
    // order. Only the newest may publish, which is the same guard the dashboard's listing query,
    // search and root probe already use.
    private int _reloadGeneration;

    /// <param name="aliases">Reads the alias map cache, normally <c>() =&gt; cache.Current</c>.
    /// A delegate rather than the cache itself so this view-model constructs in a unit test with
    /// no database (design-spec 18.3); the cache's TTL is preserved because the delegate is
    /// re-read on every <see cref="Reload"/>.</param>
    /// <param name="loadFacets">Normally <c>DashboardFacetsQuery.Load</c>.</param>
    /// <param name="loadHeaderKeys">Normally <c>DistinctHeaderKeysQuery.Load</c>.</param>
    /// <param name="today">The clock behind the Date Range presets. Defaults to the local date.</param>
    /// <param name="post">How to reach the UI thread, the same seam and the same default as
    /// <c>ScanStatusService</c>. The option lists are filled on a background thread and published
    /// through it.</param>
    /// <param name="loadCustomColumns">Normally <c>CustomColumnRepository.List</c> (spec 12.15).
    /// Trailing and optional, so a library with no custom column and every existing construction
    /// site behave exactly as they did: the eighth section is absent until this answers a
    /// definition (user choice 19). Read on the same background thread as the facets.</param>
    public FilterPanelViewModel(
        Func<AliasMap> aliases,
        Func<DashboardFacets> loadFacets,
        Func<IReadOnlyList<string>> loadHeaderKeys,
        Func<DateOnly>? today = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<IReadOnlyList<CustomColumnDefinition>>? loadCustomColumns = null)
    {
        _aliases = aliases;
        _loadFacets = loadFacets;
        _loadHeaderKeys = loadHeaderKeys;
        _loadCustomColumns = loadCustomColumns ?? (() => []);
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        // Spec 9.8's full toggle list: the nine SIMBAD-derived display categories, the five solar
        // system categories, Other, then Unresolved. Built from the Core type so the pills cannot
        // drift from the categoriser. Static, unlike the three lists Reload fills.
        ObjectTypes =
        [
            .. ObjectTypeCategories.DisplayCategories
                .Concat(ObjectTypeCategories.SolarSystemCategories)
                .Concat(["Other", TargetListingCriteria.UnresolvedCategory])
                .Select(category => new ToggleOptionViewModel(category, category)),
        ];

        DatePresets =
        [
            new DatePresetViewModel("Last 7 days", 7, ApplyDatePreset),
            new DatePresetViewModel("Last 30 days", 30, ApplyDatePreset),
            new DatePresetViewModel("Last 90 days", 90, ApplyDatePreset),
            new DatePresetViewModel("Last year", 365, ApplyDatePreset),
        ];

        MetricGroups = MetricRangeViewModel.CreateGroups();

        // The third argument is the collapsed strip's short label (spec 12.2, Phase 14C,
        // questions.md Q1, built as proposed). Four characters is the ceiling rather than a taste:
        // the 48 pixel strip clips a fifth character at the Extra Large text size.
        SearchSection = new FilterSectionViewModel("search", "Search", "Srch", "IconSearch", () =>
            PinnedTargetId is not null
            || !string.IsNullOrWhiteSpace(PinnedUnresolvedObject)
            || !string.IsNullOrWhiteSpace(SearchText));
        ObjectTypeSection = new FilterSectionViewModel("object_type", "Object Type", "Type", "IconObjectType", () =>
            ObjectTypes.Any(pill => pill.IsSelected));
        DateRangeSection = new FilterSectionViewModel("date_range", "Date Range", "Date", "IconDateRange", () =>
            DateFrom is not null || DateTo is not null);
        FiltersSection = new FilterSectionViewModel("filters", "Filters", "Filt", "IconFilters", () =>
            Filters.Any(pill => pill.IsSelected));
        EquipmentSection = new FilterSectionViewModel("equipment", "Equipment", "Eqp", "IconEquipment", () =>
            Chosen(SelectedCamera) is not null || Chosen(SelectedTelescope) is not null);
        MetricsSection = new FilterSectionViewModel("metrics", "Metrics Quality", "Metr", "IconMetrics", () =>
            AllMetrics.Any(metric => metric.Range.IsSet));
        HeaderQuerySection = new FilterSectionViewModel("header_query", "FITS Header Query", "FITS", "IconHeaderQuery", () =>
            HeaderConditions.Count > 0);

        // Spec 12.15's eighth section. Built like the other seven and held here whether or not it
        // is in Sections, so appending and removing it costs no reconstruction. "Cust" is four
        // characters, the strip's measured ceiling.
        CustomSection = new FilterSectionViewModel("custom", "Custom", "Cust", "IconCustom", () =>
            CustomFilters.Any(filter => filter.IsActive));

        // An ObservableCollection rather than a fixed list (ruling C10): the eighth section is
        // APPENDED when a definition exists and REMOVED when the last one goes, so every existing
        // index read stays where it was and a library with no custom column shows exactly the
        // seven sections it showed before this phase (user choice 19). Collection<T> implements
        // IReadOnlyList<T>, so every existing read of this member compiles unchanged.
        Sections =
        [
            SearchSection,
            ObjectTypeSection,
            DateRangeSection,
            FiltersSection,
            EquipmentSection,
            MetricsSection,
            HeaderQuerySection,
        ];

        foreach (var pill in ObjectTypes)
        {
            pill.PropertyChanged += OnChildChanged;
        }

        foreach (var metric in AllMetrics)
        {
            metric.PropertyChanged += OnChildChanged;
        }

        HeaderConditions.CollectionChanged += OnHeaderConditionsChanged;

        _initialized = true;

        // Phase review item 8: the first Reload is NOT started here. Publishing its lists can raise
        // Changed (a selected filter or the chosen equipment no longer exists), and a constructor
        // cannot have handlers attached yet, so that first event went nowhere. The owning
        // DashboardViewModel wires its handlers and then calls Reload; nothing above touched the
        // database, so construction still never waits on SQLite.
    }

    /// <summary>The seven sections of spec 12.2's table, in spec order, plus spec 12.15's eighth
    /// while the library holds a custom column. Also the collapsed strip's item source, which is
    /// why the strip needs no list of its own and why the strip and the expanded body can never
    /// disagree about whether the eighth section exists.</summary>
    public ObservableCollection<FilterSectionViewModel> Sections { get; }

    // Phase 14C, spec 12.2's two panel states. The panel's own DataContext is this view-model, so
    // the three members below are pass-throughs the owning DashboardViewModel sets: the view
    // switches states and drives both affordances off one DataContext rather than reaching up the
    // visual tree with a $parent binding that resolves to null whenever the panel is hosted alone.
    // The state itself lives on DashboardViewModel and DisplaySettings, never on the view, which is
    // what lets FilterPanelViewTests rebuild the view and find the same instance.

    /// <summary>True while the panel renders as the 48 pixel strip.</summary>
    [ObservableProperty]
    private bool _isPanelCollapsed;

    /// <summary>The page's <c>ToggleFilterPanelCommand</c>, for the strip's own chevron.</summary>
    public IRelayCommand? TogglePanelCommand { get; internal set; }

    /// <summary>The page's <c>ExpandFilterPanelOnCommand</c>, taking a section key.</summary>
    public IRelayCommand<string>? ExpandPanelOnCommand { get; internal set; }

    public FilterSectionViewModel SearchSection { get; }

    public FilterSectionViewModel ObjectTypeSection { get; }

    public FilterSectionViewModel DateRangeSection { get; }

    public FilterSectionViewModel FiltersSection { get; }

    public FilterSectionViewModel EquipmentSection { get; }

    public FilterSectionViewModel MetricsSection { get; }

    public FilterSectionViewModel HeaderQuerySection { get; }

    /// <summary>Spec 12.15's Custom section. In <see cref="Sections"/> only while
    /// <see cref="HasCustomColumns"/> is true.</summary>
    public FilterSectionViewModel CustomSection { get; }

    /// <summary>Every defined column's filter row, flattened across the three groups in the order
    /// they are drawn. What <see cref="BuildCriteria"/> and <c>Reset</c> read.</summary>
    public ObservableCollection<CustomColumnFilterViewModel> CustomFilters { get; } = [];

    /// <summary>The same rows grouped by scope, "Target" then "Night" then "Rig", each group in
    /// <c>display_order</c>. A scope with no column produces no group, so no empty heading is
    /// drawn (spec 12.15).</summary>
    public ObservableCollection<CustomFilterGroupViewModel> CustomFilterGroups { get; } = [];

    /// <summary>
    /// Zero items or one: the Custom section itself while the library holds a column. The eighth
    /// <c>Expander</c> is realized FROM this collection rather than hidden with an
    /// <c>IsVisible</c> binding, because a hidden control is still in the visual tree and
    /// <c>FilterPanelViewTests.FilterPanelView_Constructs_AndBindsToAPopulatedViewModel</c> counts
    /// the realized <c>Expander</c>s of a seven-section panel. It is kept in step with
    /// <see cref="Sections"/> by <see cref="PublishCustomColumns"/> and by nothing else, which is
    /// what makes the expanded body and the collapsed strip agree.
    /// </summary>
    public ObservableCollection<FilterSectionViewModel> CustomSectionSlot { get; } = [];

    /// <summary>True while the library holds at least one custom column. Computed rather than
    /// stored: the two collections above are the state, and a third copy of the same fact is one
    /// that can disagree with them.</summary>
    public bool HasCustomColumns => CustomFilters.Count > 0;

    // Search. Free text alone pins nothing: it drives the results dropdown, which is Task 8's.
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private Guid? _pinnedTargetId;

    [ObservableProperty]
    private string? _pinnedUnresolvedObject;

    /// <summary>Chip text for the pinned selection; display only.</summary>
    [ObservableProperty]
    private string? _pinnedLabel;

    /// <summary>The search dropdown's rows, in the order <c>TargetSearchQuery</c> ranked them
    /// (Task 8). Filled through <see cref="PublishSearchResults"/> by the owning
    /// <see cref="DashboardViewModel"/>, which owns the debounce and the query; the panel still
    /// queries nothing itself.</summary>
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    /// <summary>Whether the results dropdown is showing. Display state, not a criterion.</summary>
    [ObservableProperty]
    private bool _isSearchDropdownOpen;

    /// <summary>Exactly sixteen pills, spec 9.8 order.</summary>
    public IReadOnlyList<ToggleOptionViewModel> ObjectTypes { get; }

    [ObservableProperty]
    private DateTimeOffset? _dateFrom;

    [ObservableProperty]
    private DateTimeOffset? _dateTo;

    public IReadOnlyList<DatePresetViewModel> DatePresets { get; }

    /// <summary>Canonical optical filters, each tinted with its configured colour. Filled by
    /// <see cref="Reload"/>.</summary>
    public ObservableCollection<ToggleOptionViewModel> Filters { get; } = [];

    /// <summary>Canonical cameras, led by <see cref="AnyOption"/>. Filled by <see cref="Reload"/>.</summary>
    public ObservableCollection<string> Cameras { get; } = [];

    /// <summary>Canonical telescopes, led by <see cref="AnyOption"/>. Filled by <see cref="Reload"/>.</summary>
    public ObservableCollection<string> Telescopes { get; } = [];

    [ObservableProperty]
    private string? _selectedCamera;

    [ObservableProperty]
    private string? _selectedTelescope;

    /// <summary>Six groups, ten metrics (spec 12.2).</summary>
    public IReadOnlyList<MetricGroupViewModel> MetricGroups { get; }

    /// <summary>Distinct header keys present in the library. Filled by <see cref="Reload"/>.</summary>
    public ObservableCollection<string> HeaderKeys { get; } = [];

    /// <summary>The seven operators of spec 12.2, read from the builder that enforces them so the
    /// combo box and the gate cannot disagree.</summary>
    public IReadOnlyList<string> HeaderOperators => HeaderQueryBuilder.SupportedOperators;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddHeaderConditionCommand))]
    private string? _draftHeaderKey;

    [ObservableProperty]
    private string _draftHeaderOperator = DefaultHeaderOperator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddHeaderConditionCommand))]
    private string _draftHeaderValue = "";

    public ObservableCollection<HeaderConditionViewModel> HeaderConditions { get; } = [];

    /// <summary>Raised whenever a value that reaches the query changes. The owning
    /// <see cref="DashboardViewModel"/> debounces this and re-queries; the panel never queries.
    /// Deliberately not raised for <see cref="SearchText"/>, which pins nothing and therefore
    /// changes no criterion.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when <see cref="SearchText"/> changes. Task 8's results dropdown listens
    /// here; the listing query does not, because free text alone is not a criterion.</summary>
    public event EventHandler? SearchTextChanged;

    /// <summary>The in-flight <see cref="Reload"/>, so a test can await it instead of sleeping.
    /// Mirrors <c>WatcherService.PendingWork</c>.</summary>
    internal Task? PendingReload { get; private set; }

    private IEnumerable<MetricRangeViewModel> AllMetrics => MetricGroups.SelectMany(group => group.Metrics);

    /// <summary>
    /// Refills the three library-derived lists: the Filters pills, the two equipment combo boxes
    /// and the header key combo box. Runs both queries on a background thread and publishes
    /// through the post seam, so neither startup nor a post-scan refresh blocks the UI thread.
    /// Called once at construction and again after every scan; selections that still exist are
    /// preserved, so a refresh never silently drops a filter the user set.
    /// </summary>
    public void Reload()
    {
        var generation = ++_reloadGeneration;
        PendingReload = Task.Run(() =>
        {
            try
            {
                var map = _aliases();
                var facets = _loadFacets();
                var headerKeys = _loadHeaderKeys();

                // Spec 12.15's definition list, read beside the facets on the same background
                // thread and published through the same post, so the panel still queries nothing
                // on the UI thread and still starts no load in its constructor.
                var customColumns = _loadCustomColumns();
                _post(() =>
                {
                    // Captured before the queries, checked inside the post: a superseded reload
                    // that finished first can still be sitting in the dispatcher queue.
                    if (generation != _reloadGeneration)
                    {
                        return;
                    }

                    Publish(map, facets, headerKeys);
                    PublishCustomColumns(customColumns);
                });
            }
            catch (Exception ex)
            {
                // The lists keep whatever they already held; an empty combo box is a better
                // failure than a dead window.
                _logger.LogWarning(ex, "The dashboard filter panel could not load its option lists");
            }
        });
    }

    /// <summary>
    /// Projects the panel's values onto <paramref name="seed"/>. <c>Sort</c>, <c>Descending</c>,
    /// <c>Page</c> and <c>PageSize</c> belong to the dashboard, not the panel, so they are carried
    /// through untouched.
    /// </summary>
    public TargetListingCriteria BuildCriteria(TargetListingCriteria seed) => seed with
    {
        TargetId = PinnedTargetId,
        UnresolvedObject = PinnedUnresolvedObject,
        ObjectCategories = [.. ObjectTypes.Where(pill => pill.IsSelected).Select(pill => pill.Key)],
        SessionDateFrom = ToDateOnly(DateFrom),
        SessionDateTo = ToDateOnly(DateTo),
        Filters = [.. Filters.Where(pill => pill.IsSelected).Select(pill => pill.Key)],
        Camera = Chosen(SelectedCamera),
        Telescope = Chosen(SelectedTelescope),
        MetricRanges = AllMetrics
            .Where(metric => metric.Range.IsSet)
            .ToDictionary(metric => metric.Key, metric => metric.Range, StringComparer.Ordinal),
        HeaderConditions = [.. HeaderConditions.Select(row => row.ToCondition())],

        CustomFilters = ActiveCustomFilters(),
    };

    /// <summary>
    /// Spec 12.15's contribution to the criteria: only the active rows, each already resolved to a
    /// clause-bearing mode, which is what makes <c>TargetListingCriteria.AnyFilterActive</c>'s
    /// <c>CustomFilters.Count &gt; 0</c> agree with the SQL exactly (F3). A row on Any, or a
    /// Contains whose text is blank, contributes nothing: it would otherwise report a filter active
    /// while contributing no clause, and an empty result would show "No targets match these
    /// filters" with a Reset button that clears nothing.
    /// </summary>
    /// <remarks>One projection, read by <see cref="BuildCriteria"/> and by
    /// <see cref="PublishCustomColumns"/>'s before-and-after comparison. A second copy of it is one
    /// that can disagree about which republishes reach the query.</remarks>
    private IReadOnlyList<CustomColumnFilter> ActiveCustomFilters() =>
        [.. CustomFilters.Where(filter => filter.IsActive).Select(filter => filter.ToFilter())];

    /// <summary>
    /// Replaces the eighth section's rows with the definitions the catalogue now holds (spec
    /// 12.15), and appends or removes the section itself so it is absent while no column exists
    /// (user choice 19). Runs on the UI thread, through the same post seam as
    /// <see cref="Publish"/>.
    /// </summary>
    public void PublishCustomColumns(IReadOnlyList<CustomColumnDefinition> definitions)
    {
        bool criteriaChanged;
        _suspendChanged = true;
        try
        {
            // The same before-and-after comparison Publish makes for the three library-derived
            // lists, over the clauses these rows contribute. A rename, a reorder or an option edit
            // leaves that set exactly as it was, because Adopt and AdoptFrom carry the chosen value
            // across by slug, so those republishes still cost no query. A column that is DELETED
            // takes its row away, and a slug whose type changed lands on AdoptFrom's early return,
            // and both of those drop a clause the criteria were carrying: without this the rows on
            // screen stay narrowed by a filter that no longer has a control, every section marker
            // reads inactive, and Reset Filters brings the hidden targets back for nobody.
            // A set rather than a sequence, exactly as Publish compares its selected filter keys:
            // a slug is unique among the active rows, so nothing is lost, and a pure reorder does
            // not read as a change.
            var before = ActiveCustomFilters().ToHashSet();

            // Kept by slug so a post-scan refresh never silently drops a filter the user set,
            // which is the rule Publish already applies to the three library-derived lists. The
            // slug is stable across a rename (spec 12.15 never re-slugs).
            var previous = CustomFilters.ToDictionary(filter => filter.Column.Slug, StringComparer.Ordinal);
            foreach (var filter in CustomFilters)
            {
                filter.PropertyChanged -= OnChildChanged;
            }

            CustomFilters.Clear();
            CustomFilterGroups.Clear();

            foreach (var (scope, title) in ScopeHeadings)
            {
                var rows = definitions
                    .Where(definition => definition.Scope == scope)
                    .OrderBy(definition => definition.DisplayOrder)
                    .Select(definition => Adopt(definition, previous))
                    .ToList();
                if (rows.Count == 0)
                {
                    continue;
                }

                CustomFilterGroups.Add(new CustomFilterGroupViewModel(title, rows));
                foreach (var row in rows)
                {
                    CustomFilters.Add(row);
                }
            }

            criteriaChanged = !before.SetEquals(ActiveCustomFilters());
        }
        finally
        {
            _suspendChanged = false;
        }

        // APPENDED, never inserted: every existing index read of Sections stays where it was, and
        // the collapsed strip takes the same collection, so the two states cannot disagree.
        if (HasCustomColumns)
        {
            if (!Sections.Contains(CustomSection))
            {
                Sections.Add(CustomSection);
                CustomSectionSlot.Add(CustomSection);
            }
        }
        else
        {
            Sections.Remove(CustomSection);
            CustomSectionSlot.Remove(CustomSection);
        }

        // After the section has been appended or removed, so the markers are refreshed over the
        // list the panel now draws.
        RaiseOrRefresh(criteriaChanged);
    }

    private CustomColumnFilterViewModel Adopt(
        CustomColumnDefinition definition, IReadOnlyDictionary<string, CustomColumnFilterViewModel> previous)
    {
        var row = new CustomColumnFilterViewModel(definition);
        if (previous.TryGetValue(definition.Slug, out var existing))
        {
            row.AdoptFrom(existing);
        }

        row.PropertyChanged += OnChildChanged;
        return row;
    }

    [RelayCommand(CanExecute = nameof(CanAddHeaderCondition))]
    private void AddHeaderCondition()
    {
        // No validation: spec 12.3's key gate and numeric parse are HeaderQueryBuilder's, and it
        // is the single place a condition is accepted or dropped. The only guards are that a
        // blank key or a blank value would produce a row that narrows nothing and reads as an
        // accident.
        HeaderConditions.Add(new HeaderConditionViewModel(
            DraftHeaderKey!.Trim(),
            DraftHeaderOperator,
            DraftHeaderValue,
            RemoveHeaderCondition));

        DraftHeaderKey = null;
        DraftHeaderValue = "";
    }

    private bool CanAddHeaderCondition() =>
        !string.IsNullOrWhiteSpace(DraftHeaderKey) && !string.IsNullOrWhiteSpace(DraftHeaderValue);

    [RelayCommand]
    private void RemoveHeaderCondition(HeaderConditionViewModel row) => HeaderConditions.Remove(row);

    /// <summary>
    /// Replaces the dropdown rows with what the query returned, unchanged and unreordered, and
    /// opens the dropdown when there is something to show (Task 8). Called on the UI thread,
    /// through the dashboard's post seam.
    /// </summary>
    public void PublishSearchResults(IReadOnlyList<SearchResultViewModel> results)
    {
        SearchResults.Clear();
        foreach (var result in results)
        {
            SearchResults.Add(result);
        }

        IsSearchDropdownOpen = results.Count > 0;
    }

    /// <summary>
    /// Spec 12.2: "Selecting a result pins <c>target_id</c>. Unresolved <c>OBJECT</c> strings ...
    /// select as <c>obj:&lt;name&gt;</c>". Exactly one of the two pinned properties is ever
    /// non-null, the typed text and the dropdown are cleared, and one <see cref="Changed"/> is
    /// raised so the pin costs exactly one query.
    /// </summary>
    [RelayCommand]
    private void SelectSearchResult(SearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }

        _suspendChanged = true;
        try
        {
            PinnedTargetId = result.TargetId;
            PinnedUnresolvedObject = result.TargetId is null ? result.UnresolvedObject : null;
            PinnedLabel = result.DisplayName;
            SearchText = "";
        }
        finally
        {
            _suspendChanged = false;
        }

        SearchResults.Clear();
        IsSearchDropdownOpen = false;
        RaiseChanged();

        // Review item 3. The text was cleared inside the suspend block, so this is what tells the
        // dashboard to cancel the search window that keystroke opened. Without it, a window
        // already past its debounce lands afterwards and re-opens the dropdown over the pin
        // the user just made. Reset does the same, for the same reason.
        SearchTextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Dismisses the results dropdown without pinning anything. Bound to Escape in the
    /// search box; the typed text is left alone, so the user can keep editing it.</summary>
    [RelayCommand]
    private void CloseSearchDropdown() => IsSearchDropdownOpen = false;

    /// <summary>Drops the pinned search selection, leaving the typed text alone.</summary>
    [RelayCommand]
    private void ClearPin()
    {
        _suspendChanged = true;
        try
        {
            PinnedTargetId = null;
            PinnedUnresolvedObject = null;
            PinnedLabel = null;
        }
        finally
        {
            _suspendChanged = false;
        }

        RaiseChanged();
    }

    // A DatePicker has no clear gesture of its own, so a date bound once could never be unset.
    [RelayCommand]
    private void ClearDateFrom() => DateFrom = null;

    [RelayCommand]
    private void ClearDateTo() => DateTo = null;

    /// <summary>Spec 12.2's "Reset Filters": clears every section's values. Section expansion, the
    /// panel's collapsed state, sorting and paging are untouched.</summary>
    [RelayCommand]
    private void Reset()
    {
        _suspendChanged = true;
        try
        {
            SearchText = "";
            PinnedTargetId = null;
            PinnedUnresolvedObject = null;
            PinnedLabel = null;

            foreach (var pill in ObjectTypes)
            {
                pill.IsSelected = false;
            }

            DateFrom = null;
            DateTo = null;

            foreach (var pill in Filters)
            {
                pill.IsSelected = false;
            }

            SelectedCamera = Cameras.Count > 0 ? AnyOption : null;
            SelectedTelescope = Telescopes.Count > 0 ? AnyOption : null;

            foreach (var metric in AllMetrics)
            {
                metric.Clear();
            }

            HeaderConditions.Clear();
            DraftHeaderKey = null;
            DraftHeaderOperator = DefaultHeaderOperator;
            DraftHeaderValue = "";

            // Spec 12.15, inside the same suspend block as every other section: one Changed for
            // the whole reset stays one however many custom columns are defined.
            foreach (var filter in CustomFilters)
            {
                filter.Clear();
            }
        }
        finally
        {
            _suspendChanged = false;
        }

        // Task 8: the dropdown belongs to the text that was just cleared.
        SearchResults.Clear();
        IsSearchDropdownOpen = false;

        // One Changed for the whole reset, so the debounce window is opened once rather than
        // restarted a dozen times before it can elapse.
        RaiseChanged();
        SearchTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyDatePreset(DatePresetViewModel preset)
    {
        var today = _today();
        _suspendChanged = true;
        try
        {
            DateFrom = ToOffset(today.AddDays(-(preset.Days - 1)));
            DateTo = ToOffset(today);
        }
        finally
        {
            _suspendChanged = false;
        }

        RaiseChanged();
    }

    // Replaces the three library-derived lists in one shot, on the UI thread, preserving whatever
    // the user had selected that still exists.
    private void Publish(AliasMap map, DashboardFacets facets, IReadOnlyList<string> headerKeys)
    {
        bool criteriaChanged;
        _suspendChanged = true;
        try
        {
            var selected = Filters
                .Where(pill => pill.IsSelected)
                .Select(pill => pill.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var pill in Filters)
            {
                pill.PropertyChanged -= OnChildChanged;
            }

            Filters.Clear();

            // The union of what the user configured and what the library actually holds (spec
            // 12.2, 12.7), in FilterOrder (polish ruling 5). A configured filter present on no
            // frame is still offered; a discovered filter that was never configured takes the
            // default colour.
            var discovered = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var facet in facets.Filters)
            {
                discovered[facet.CanonicalName] = (facet.CanonicalName, facet.FrameCount);
            }

            foreach (var configured in map.ConfiguredFilters)
            {
                if (!discovered.ContainsKey(configured))
                {
                    discovered[configured] = (configured, 0);
                }
            }

            foreach (var entry in discovered.Values.OrderBy(entry => entry.Name, FilterOrder.Comparer(map)))
            {
                var pill = new ToggleOptionViewModel(
                    entry.Name,
                    entry.Name,
                    TargetRowViewModel.ParseTint(map.FilterColor(entry.Name)),
                    entry.Count)
                {
                    IsSelected = selected.Contains(entry.Name),
                };
                pill.PropertyChanged += OnChildChanged;
                Filters.Add(pill);
            }

            var camera = SelectedCamera;
            var telescope = SelectedTelescope;
            Replace(Cameras, [AnyOption, .. Union(facets.Cameras, map.ConfiguredCameras)]);
            Replace(Telescopes, [AnyOption, .. Union(facets.Telescopes, map.ConfiguredTelescopes)]);
            Replace(HeaderKeys, headerKeys);

            // Clearing an ItemsSource nulls the bound SelectedItem; restore what still exists so a
            // refresh cannot silently drop an equipment criterion.
            SelectedCamera = camera is not null && Cameras.Contains(camera) ? camera : AnyOption;
            SelectedTelescope = telescope is not null && Telescopes.Contains(telescope) ? telescope : AnyOption;

            // What RaiseOrRefresh below decides on: the refresh altered what BuildCriteria would
            // produce only when a selected filter or the chosen equipment no longer exists.
            criteriaChanged =
                !selected.SetEquals(Filters.Where(pill => pill.IsSelected).Select(pill => pill.Key))
                || Chosen(camera) != Chosen(SelectedCamera)
                || Chosen(telescope) != Chosen(SelectedTelescope);
        }
        finally
        {
            _suspendChanged = false;
        }

        RaiseOrRefresh(criteriaChanged);
    }

    /// <summary>
    /// How both halves of a reload settle. Refreshing a list is not itself a filter change:
    /// <see cref="Changed"/> is raised only when the refresh actually altered what
    /// <see cref="BuildCriteria"/> would produce, which is a selected filter or a chosen piece of
    /// equipment that no longer exists, or a custom column whose active row has gone. Otherwise the
    /// startup reload and every post-scan reload would each cost a redundant query, and the markers
    /// alone are refreshed. One tail for both halves, so the two cannot drift apart.
    /// </summary>
    private void RaiseOrRefresh(bool criteriaChanged)
    {
        if (criteriaChanged)
        {
            RaiseChanged();
        }
        else
        {
            RefreshSectionMarkers();
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case null:
            // The draft header row is not part of the query until it is added, and PinnedLabel is
            // chip text for a pin that has already raised Changed through PinnedTargetId.
            case nameof(DraftHeaderKey):
            case nameof(DraftHeaderOperator):
            case nameof(DraftHeaderValue):
            case nameof(PinnedLabel):
            // Dropdown visibility is display state; it constrains nothing (Task 8).
            case nameof(IsSearchDropdownOpen):
                return;

            // Free text pins nothing (BuildCriteria never reads it), so a keystroke must not
            // re-issue the listing query. It still moves the Search section's active marker and
            // still signals Task 8's results dropdown.
            case nameof(SearchText):
                if (_initialized && !_suspendChanged)
                {
                    RefreshSectionMarkers();
                    SearchTextChanged?.Invoke(this, EventArgs.Empty);
                }

                return;

            default:
                RaiseChanged();
                return;
        }
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e) => RaiseChanged();

    private void OnHeaderConditionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseChanged();

    // Seven predicate evaluations per change, eight once a custom column exists, is not a cost
    // worth optimizing, and it is what keeps an active marker from ever disagreeing with the
    // values behind it.
    private void RefreshSectionMarkers()
    {
        foreach (var section in Sections)
        {
            section.RaiseIsActive();
        }
    }

    private void RaiseChanged()
    {
        if (!_initialized || _suspendChanged)
        {
            return;
        }

        RefreshSectionMarkers();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static IReadOnlyList<string> Union(IEnumerable<string> discovered, IEnumerable<string> configured) =>
        [.. discovered
            .Concat(configured)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Null for "no criterion": unset, blank, or the combo box's leading Any entry.</summary>
    private static string? Chosen(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, AnyOption, StringComparison.Ordinal)
            ? null
            : value;

    private static DateOnly? ToDateOnly(DateTimeOffset? value) =>
        value is null ? null : DateOnly.FromDateTime(value.Value.Date);

    private static DateTimeOffset ToOffset(DateOnly value) =>
        new(value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
