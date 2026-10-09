using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// The dashboard's target list (design-spec 12.2): the rows, the six sort keys in both
/// directions, paging at <c>general.default_page_size</c>, and column visibility persisted to
/// <c>display.columns.dashboard</c> (design-spec 5.8.2).
/// <para>
/// It never queries. Sort and paging changes raise <see cref="Changed"/> and the owning
/// <see cref="DashboardViewModel"/> re-queries through the one debounced call site Task 6
/// established; <see cref="ApplyTo"/> is how this state reaches the criteria.
/// </para>
/// </summary>
public sealed partial class TargetListViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.2's six sort keys and the direction a fresh click on each starts in:
    /// descending for the numeric and date keys (most integration, newest session first),
    /// ascending for the two textual ones. <c>frames</c> and <c>sessions</c> are sortable without
    /// being columns of their own: they are rendered in the Sessions expander and the row summary,
    /// which is why this list and the column key list differ.</summary>
    private static readonly Dictionary<string, (TargetListingSort Sort, bool Descending)> SortKeys = new(StringComparer.Ordinal)
    {
        ["name"] = (TargetListingSort.Name, false),
        ["integration"] = (TargetListingSort.Integration, true),
        ["frames"] = (TargetListingSort.Frames, true),
        ["sessions"] = (TargetListingSort.Sessions, true),
        ["last_session"] = (TargetListingSort.LastSession, true),
        ["equipment"] = (TargetListingSort.Equipment, false),
    };

    private static readonly Dictionary<string, string> ColumnTitles = new(StringComparer.Ordinal)
    {
        ["name"] = "Name",
        ["designation"] = "Designation",
        ["palette"] = "Palette",
        ["integration"] = "Integration",
        ["equipment"] = "Equipment",
        ["last_session"] = "Last Session",
    };

    private readonly DisplayColumnWriter _columns;

    // Phase 14C Task 4, spec 5.8.1. Null until the constructor's very last line, so the
    // constructor's own page-size seed cannot reach it (see the constructor's own remark).
    private readonly Action<int>? _writeDefaultPageSize;

    // ---- Spec 12.15's custom columns (Phase 20 Task 5a). All four delegates are null on a list
    // built without them, which is every construction site outside AppHost, and a null set leaves
    // the page exactly as it was before this phase.
    private readonly DisplaySettings _initialDisplay;
    private readonly Func<IReadOnlyList<CustomColumnDefinition>>? _loadCustomColumns;
    private readonly Func<IReadOnlyCollection<Guid>, IReadOnlyList<CustomValueRow>>? _loadTargetValues;
    private readonly Func<Guid, IReadOnlyList<CustomValueRow>>? _loadValuesForTarget;
    private readonly Func<Guid, CustomValueKey, string?, CustomWriteResult>? _writeValue;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, CancellationToken, Task>? _cellDelay;
    private readonly ILogger? _cellLogger;
    private readonly Func<AliasMap>? _aliases;

    /// <summary>How long <see cref="Dispose"/> waits for the page's cell writes, the same budget
    /// <c>SessionCardViewModel.FlushBudget</c> spends on the Target page.</summary>
    private static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(2);

    private IReadOnlyList<CustomColumnDefinition> _definitions = [];
    private IReadOnlyDictionary<Guid, IReadOnlyList<CustomValueRow>> _valuesByTarget =
        new Dictionary<Guid, IReadOnlyList<CustomValueRow>>();

    private CustomCellContext? _customContext;

    private int _suspendDepth;

    /// <param name="initialDisplay">The display document as it was when the host was built, read
    /// on <c>Program.Main</c>'s thread (phase review item 2). By value, not a delegate: a
    /// <c>Func&lt;DisplaySettings&gt;</c> here would be a synchronous SQLite read on the UI thread
    /// during window construction, and this signature makes that read structurally impossible.</param>
    /// <param name="getDisplay">Normally <c>SettingsStore.GetDisplay</c>. Used only by the
    /// background load-modify-save behind a column click, never on the construction path.</param>
    /// <param name="saveDisplay">Normally <c>SettingsStore.SaveDisplay</c>. The only write this
    /// view-model performs, and it goes to the database, never to a file.</param>
    /// <param name="defaultPageSize"><c>general.default_page_size</c> (design-spec 5.8.1).</param>
    /// <param name="writeDefaultPageSize">See the primary constructor's own parameter.</param>
    /// <remarks>The two delegates are a convenience over the real dependency below: they build a
    /// <see cref="DisplayColumnWriter"/> that belongs to this view-model alone. The application
    /// never uses this overload, because <c>display.columns</c> has two tables writing it and one
    /// chain per writer is the lost update the writer exists to prevent; a unit test with one
    /// table and one fake store does.</remarks>
    public TargetListViewModel(
        DisplaySettings initialDisplay,
        Func<DisplaySettings> getDisplay,
        Action<DisplaySettings> saveDisplay,
        int defaultPageSize,
        ILogger? logger = null,
        Action<int>? writeDefaultPageSize = null)
        : this(
            initialDisplay,
            new DisplayColumnWriter(getDisplay, saveDisplay, logger ?? NullLogger.Instance),
            defaultPageSize,
            writeDefaultPageSize)
    {
    }

    /// <param name="initialDisplay">The display document as it was when the host was built, by
    /// value, for the reason the other constructor's parameter documents.</param>
    /// <param name="columns">The process-wide <c>display.columns</c> writer (Phase 6 Task 5). One
    /// chain shared with the frame table, so two tables' column clicks cannot interleave into a
    /// lost update.</param>
    /// <param name="defaultPageSize"><c>general.default_page_size</c> (design-spec 5.8.1).</param>
    /// <param name="writeDefaultPageSize">Writes <c>general.default_page_size</c> when the
    /// pager's page-size select changes (spec 5.8.1). Trailing and optional, so no existing
    /// construction site has to move; normally
    /// <c>size =&gt; settingsStore.MutateGeneral(general =&gt; general with { DefaultPageSize = size })</c>,
    /// bound once in <c>AppHost</c>. Null lets the select still change the page size for the
    /// session and stores nothing, which is what every other construction site gets for free.
    /// </param>
    /// <param name="loadCustomColumns">Normally <c>CustomColumnRepository.List</c> (spec 12.15).
    /// Read on the page's existing background query hop, never on the UI thread, through
    /// <see cref="ReadCustomColumns"/>. Null leaves the page with no custom column at all.</param>
    /// <param name="loadTargetValues">Normally <c>CustomColumnRepository.TargetValues</c>. One
    /// round trip for the whole page of rows, on the same background hop.</param>
    /// <param name="loadValuesForTarget">Normally <c>CustomColumnRepository.ValuesForTarget</c>.
    /// Read lazily, when a row is expanded (ruling C11): a page of 50 targets must not issue 50
    /// reads to draw rows nobody has opened.</param>
    /// <param name="writeValue">Normally <c>CustomColumnRepository.SetValue</c>. Handed to every
    /// cell, which invokes it off the UI thread itself.</param>
    /// <param name="post">How a background read reaches the UI thread. Defaults to
    /// <c>UiPost.Default</c>; the dashboard hands its own seam, so a case runs the closure inline.
    /// </param>
    /// <param name="cellDelay">The cells' debounce seam, handed to every cell this list builds.
    /// Null in the application, which takes <c>AutosaveField.IdleWindow</c>; a case parks and
    /// releases it rather than sleeping.</param>
    /// <param name="cellLogger">Handed to every custom column cell this list builds and to the lazy
    /// night read (review P3-2): a cell write that throws on the dashboard was logged nowhere, while
    /// the same failure on the Target page was logged, because that page passes its own. Trailing and
    /// optional, so no existing construction site has to move; null falls back to the cell's own null
    /// logger, which is what every case gets.</param>
    /// <remarks>This view-model itself logs nothing but one thing: the only thing it ever logged
    /// was a failed column write, which now belongs to <see cref="DisplayColumnWriter"/> along with
    /// the write itself. <paramref name="cellLogger"/> is handed to the cells and used here for the
    /// one case a cell cannot report, a flush that faults or overruns in <see cref="Dispose"/>.
    /// </remarks>
    public TargetListViewModel(
        DisplaySettings initialDisplay,
        DisplayColumnWriter columns,
        int defaultPageSize,
        Action<int>? writeDefaultPageSize = null,
        Func<IReadOnlyList<CustomColumnDefinition>>? loadCustomColumns = null,
        Func<IReadOnlyCollection<Guid>, IReadOnlyList<CustomValueRow>>? loadTargetValues = null,
        Func<Guid, IReadOnlyList<CustomValueRow>>? loadValuesForTarget = null,
        Func<Guid, CustomValueKey, string?, CustomWriteResult>? writeValue = null,
        Action<Action>? post = null,
        Func<TimeSpan, CancellationToken, Task>? cellDelay = null,
        ILogger? cellLogger = null,
        Func<AliasMap>? aliases = null)
    {
        _columns = columns;
        _initialDisplay = initialDisplay;
        _loadCustomColumns = loadCustomColumns;
        _loadTargetValues = loadTargetValues;
        _loadValuesForTarget = loadValuesForTarget;
        _writeValue = writeValue;
        _post = post ?? UiPost.Default;
        _cellDelay = cellDelay;
        _cellLogger = cellLogger;
        _aliases = aliases;

        // Columns first: the property setters below reach UpdateSortGlyphs.
        //
        // Spec 5.8.2's fallback lives in the settings record, not here: Phase 6's frame table
        // needs the same rule. The order is the documented default order, always; the stored list
        // contributes visibility only (spec 12.2 asks for visibility, not reordering).
        var visible = initialDisplay.ColumnsFor(DisplaySettings.DashboardTableId);
        foreach (var key in DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId])
        {
            Columns.Add(new ColumnViewModel(
                key, ColumnTitles[key], visible.Contains(key), canHide: key != "name"));
        }

        // Spec 12.2's landing state: newest session first, page one, page size from
        // general.default_page_size (spec 5.8.1). Assigned rather than declared as property
        // initializers because [ObservableProperty] partial properties take none.
        Sort = TargetListingSort.LastSession;
        Descending = true;
        Page = 1;

        // SelectedPageSize first: its own change handler assigns PageSize as a side effect (see
        // that property's remarks below), which the real value right after overwrites. Selected
        // is what the select renders, and the select has exactly four buttons, so a page size
        // outside them has no button to land on and reads as the default, 50 (spec 5.8.1).
        // PageSize keeps its own long-standing clamp, unrelated to the select's four-item
        // vocabulary: a page size spec 5.8.1's list never named still has to query by itself
        // rather than silently becoming 50, which is why it is written last and wins.
        SelectedPageSize = NormalizePageSize(defaultPageSize);
        PageSize = Math.Max(1, defaultPageSize);

        // Wired last, after the seeding above has already run: OnSelectedPageSizeChanged fires
        // from the SelectedPageSize assignment above, and with the delegate still null at that
        // point the seed writes nothing back to the settings document. Only a later, real select
        // change reaches the writer.
        _writeDefaultPageSize = writeDefaultPageSize;

        // Built last, over the collection above, so the gear's grouped list is the picker's one
        // split rather than a second one written in the view.
        ColumnPicker = ColumnPickerViewModel.ForDashboard(Columns, column => ToggleColumn(column));
    }

    public ObservableCollection<TargetRowViewModel> Rows { get; } = [];

    /// <summary>The six built-in columns, in the documented order, whatever their visibility, then
    /// one entry per target-scope custom column in display order (spec 12.15).
    /// <para>
    /// Observable since Phase 20: the custom entries arrive with the first query rather than at
    /// construction, and the column gear's flyout, the Display tab's picker and this view's own
    /// width arithmetic all read this one collection. A snapshot taken at construction would show
    /// no custom column until the page was rebuilt, which is the Phase 16 flyout trap in another
    /// shape.
    /// </para></summary>
    public ObservableCollection<ColumnViewModel> Columns { get; } = [];

    /// <summary>The visible subset, in the same documented order.</summary>
    public IEnumerable<ColumnViewModel> VisibleColumns => Columns.Where(column => column.IsVisible);

    /// <summary>
    /// The column gear's own view over <see cref="Columns"/>, for spec 12.15's rule that a picker
    /// lists custom columns under a "Custom" heading below the built-in ones.
    /// </summary>
    /// <remarks>
    /// Over the live collection above and this list's own toggle, so there is exactly one dashboard
    /// column state in the process and the gear, the Display tab's picker and this view's width
    /// arithmetic all read the same <c>ColumnViewModel</c> objects (ruling D4). The grouping itself
    /// is the picker's one implementation rather than a second split written here (design lesson 1).
    /// </remarks>
    public ColumnPickerViewModel ColumnPicker { get; }

    /// <summary>Spec 12.15's target-scope custom columns the reader has not switched off, in display
    /// order: what the header strip labels and what every row builds a cell for. Empty until a
    /// query brings the definitions back, and empty on every library with no custom column, which
    /// is what makes the custom grid column measure zero.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrawnCustomColumns))]
    private IReadOnlyList<CustomColumnDefinition> _customColumns = [];

    // Ruling C24. The width rule owns this figure and pushes it down through
    // SetDrawnCustomColumns; int.MaxValue means "draw them all", which is what a list with no view
    // over it answers and what every case that is not about the width rule gets.
    private int _drawnCustomColumns = int.MaxValue;

    /// <summary>Ruling C24: the custom columns actually drawn, a prefix of
    /// <see cref="CustomColumns"/>. Custom columns are the first thing to give way when the row
    /// does not fit the width the list has, last first, and folding the filter panel or widening
    /// the window brings them straight back: nothing is rebuilt and nothing is re-read, so a cell
    /// that returns is the cell it was.
    /// <para>
    /// The header strip binds this and every row's cells are sliced by the same figure, which is
    /// what makes the two sets equal by construction rather than by two rules agreeing.
    /// </para></summary>
    public IReadOnlyList<CustomColumnDefinition> DrawnCustomColumns
        => _drawnCustomColumns >= CustomColumns.Count
            ? CustomColumns
            : [.. CustomColumns.Take(_drawnCustomColumns)];

    /// <summary>Ruling C24's figure, written by the view's width rule, which is the one place the
    /// taken set is computed.</summary>
    internal void SetDrawnCustomColumns(int count)
    {
        if (_drawnCustomColumns == count)
        {
            return;
        }

        _drawnCustomColumns = count;
        OnPropertyChanged(nameof(DrawnCustomColumns));
        foreach (var row in Rows)
        {
            row.SetDrawnCustomCells(count);
        }
    }

    [ObservableProperty]
    public partial TargetListingSort Sort { get; set; }

    [ObservableProperty]
    public partial bool Descending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousPage), nameof(HasNextPage), nameof(PageStatusText), nameof(PageButtons))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int Page { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount), nameof(HasPreviousPage), nameof(HasNextPage), nameof(PageStatusText), nameof(PageButtons))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int PageSize { get; set; }

    /// <summary>Groups in the whole filtered set, from the page's totals.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount), nameof(HasPreviousPage), nameof(HasNextPage), nameof(PageStatusText), nameof(PageButtons))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial int TotalGroups { get; private set; }

    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalGroups / (double)Math.Max(1, PageSize)));

    // Review finding (fix pass): a page command dropped during a refetch had no affordance
    // saying so, because CanExecute read the page bounds alone and the page buttons sit outside
    // the dimmed rows region. IsRefetching is folded into the bounds check itself, so
    // NextPageCommand and PreviousPageCommand grey the same way HasNextPage and HasPreviousPage
    // already drive them, through the existing NotifyCanExecuteChangedFor wiring on IsRefetching
    // below. The body guards stay (TRACKING item 13: CanExecute is the affordance, not the
    // guard).
    public bool HasPreviousPage => Page > 1 && !IsRefetching;

    public bool HasNextPage => Page < PageCount && !IsRefetching;

    // Review finding (fix pass): the page-count form repeated what the numbered buttons already
    // say and was the longest run in the pager's one star column, trimming into the page-size
    // select at 1280 by 800 with 25 per page. The web's own line is a range: first and last item
    // index within the page, never the page count a second time. "0 of 0" when the filtered set
    // is empty, since Page and PageCount both read 1 there and the range arithmetic would
    // otherwise print a first past the last.
    public string PageStatusText
    {
        get
        {
            if (TotalGroups == 0)
            {
                return "0 of 0";
            }

            var first = ((Page - 1) * PageSize) + 1;
            var last = Math.Min(Page * PageSize, TotalGroups);
            return $"{first} to {last} of {TotalGroups}";
        }
    }

    /// <summary>Spec 12.2's page button set, the web's own <c>pageRange</c> rule
    /// (<c>TargetFeed.tsx</c>): every page at 7 or fewer; above 7, page 1, an ellipsis, a window of
    /// the current page and one neighbour each side, an ellipsis, the last page. Recomputed
    /// whenever <see cref="Page"/>, <see cref="PageSize"/> or <see cref="TotalGroups"/> changes
    /// (the three attribute lists above), never stored.</summary>
    public IReadOnlyList<PageButtonViewModel> PageButtons => BuildPageButtons(Page, PageCount);

    // The window is centred on the current page and shifted, not independently clamped, so it
    // stays three pages wide even next to a boundary: at page 1 of 40 the window is {1,2,3}, not
    // {2} (task4.md section 6's two boundary cases). Page 1 and the last page are always in the
    // result set regardless of the window; SortedSet dedupes the overlap once the window reaches
    // either edge.
    private static IReadOnlyList<PageButtonViewModel> BuildPageButtons(int page, int pageCount)
    {
        if (pageCount <= 7)
        {
            return [.. Enumerable.Range(1, pageCount).Select(p => new PageButtonViewModel(p, p == page))];
        }

        var low = page - 1;
        var high = page + 1;
        if (low < 1)
        {
            high += 1 - low;
            low = 1;
        }

        if (high > pageCount)
        {
            low -= high - pageCount;
            high = pageCount;
        }

        low = Math.Max(low, 1);
        high = Math.Min(high, pageCount);

        var pages = new SortedSet<int> { 1, pageCount };
        for (var p = low; p <= high; p++)
        {
            pages.Add(p);
        }

        var result = new List<PageButtonViewModel>(pages.Count + 2);
        var previous = 0;
        foreach (var p in pages)
        {
            if (previous != 0 && p - previous > 1)
            {
                result.Add(new PageButtonViewModel(null, false));
            }

            result.Add(new PageButtonViewModel(p, p == page));
            previous = p;
        }

        return result;
    }

    /// <summary>Spec 5.8.1's four choices, in order. One declaration, read by the page-size select
    /// and by the case that pins it.</summary>
    public static IReadOnlyList<int> PageSizeOptions { get; } = [25, 50, 100, 250];

    /// <summary>A stored or requested page size outside <see cref="PageSizeOptions"/> reads as the
    /// default, 50 (spec 5.8.1). Not a clamp to the nearest option: a page size carries no
    /// ordering relationship to its neighbours that would make "nearest" meaningful.</summary>
    internal static int NormalizePageSize(int stored) => PageSizeOptions.Contains(stored) ? stored : 50;

    /// <summary>The page-size select's two-way binding (spec 12.2). Setting it writes
    /// <c>general.default_page_size</c> through the constructor's write delegate and moves
    /// <see cref="PageSize"/>, which is what actually re-queries.</summary>
    [ObservableProperty]
    public partial int SelectedPageSize { get; set; }

    partial void OnSelectedPageSizeChanged(int value)
    {
        // PageSize first: AppHost's writer raises GeneralChanged synchronously, which reaches
        // AppHost.FollowDefaultPageSize on this same call stack, and that method's own guard is
        // "PageSize already equals the clamped value, do nothing". Writing PageSize before the
        // delegate is what makes that guard fire and keeps the echo to the one write the pager
        // made, not two (questions.md Q6).
        PageSize = value;

        // Review finding (fix pass): set by FollowPageSize below while an external write is
        // being applied back to this list, so that path never re-enters the write delegate.
        if (!_followingExternalPageSize)
        {
            _writeDefaultPageSize?.Invoke(value);
        }
    }

    // Review finding (fix pass): AppHost.FollowDefaultPageSize used to assign PageSize alone, so
    // a page-size change made on the Settings Display tab paged the list by the new size while
    // the pager's own select kept showing the old one. This is the one place a stored
    // general.default_page_size re-enters the list from outside a select change, so it is also
    // the one place spec 5.8.1's normalization has to run for every writer, not only the
    // constructor's own seed: a stored value outside PageSizeOptions reads as 50 here too.
    private bool _followingExternalPageSize;

    internal void FollowPageSize(int size)
    {
        var normalized = NormalizePageSize(size);
        _followingExternalPageSize = true;
        try
        {
            PageSize = normalized;
            SelectedPageSize = normalized;
        }
        finally
        {
            _followingExternalPageSize = false;
        }
    }

    /// <summary>The rows' dim and disable while a filtered refetch is in flight (spec 12.2, "the
    /// refetch dim"). Lives on the list, not on the dashboard, because the list is what the view
    /// binds and what the treatment scopes to. Set and cleared by
    /// <see cref="DashboardViewModel"/> on the UI thread, through <see cref="SetRefetching"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousPage), nameof(HasNextPage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial bool IsRefetching { get; private set; }

    internal void SetRefetching(bool value) => IsRefetching = value;

    /// <summary>Sort or page state changed; the dashboard re-queries. Column visibility does not
    /// raise this: hiding a column is a rendering change and must not cost a round trip to the
    /// images table.</summary>
    public event EventHandler? Changed;

    /// <summary>A row was activated, or a session line's Deep dive action was (Phase 14B Task 6).
    /// Phase 6 navigates to Target detail; Phase 5 has no destination (coordinator ruling Q17) and
    /// only raises this. One event with a widened payload rather than a second near-identical one
    /// beside it (task6.md 6.2, design-lessons rule 1): a row click and a Deep dive click differ
    /// only in whether <see cref="TargetOpenRequest.SessionDate"/> is set.</summary>
    public event EventHandler<TargetOpenRequest>? TargetOpened;

    /// <summary>A row's mosaic link was clicked (spec 12.2, Phase 18): the id of the mosaic to open
    /// on the shell's detail overlay.</summary>
    public event EventHandler<Guid>? MosaicOpened;

    /// <summary>Projects sort and paging onto the criteria the filter panel built. The dashboard
    /// owns the single query call site; this is called from inside it.</summary>
    public TargetListingCriteria ApplyTo(TargetListingCriteria criteria) => criteria with
    {
        Sort = Sort,
        Descending = Descending,
        Page = Page,
        PageSize = PageSize,
    };

    /// <summary>Spec 12.15's two page reads, taken on the dashboard's own background query hop and
    /// never on the UI thread. Called between the listing query and <see cref="Load"/>; the answer
    /// is handed straight back to it, so the definitions and the values belong to the same page of
    /// rows they are drawn against.</summary>
    internal CustomColumnPage ReadCustomColumns(TargetListingPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (_loadCustomColumns is null)
        {
            return CustomColumnPage.None;
        }

        var definitions = _loadCustomColumns();

        // Ruling C33: a library with no custom column pays nothing. Without this the page issued a
        // TargetValues command with a fifty element id list on every query, which could only ever
        // return nothing, on every library that has never defined a column.
        if (definitions.Count == 0)
        {
            return CustomColumnPage.None;
        }

        var targetIds = page.Rows
            .Select(row => row.TargetId)
            .OfType<Guid>()
            .Distinct()
            .ToArray();

        var values = _loadTargetValues is null || targetIds.Length == 0
            ? []
            : _loadTargetValues(targetIds);

        return new CustomColumnPage(definitions, values);
    }

    /// <summary>Replaces the rows and the totals from a query result.</summary>
    /// <param name="page">The listing page.</param>
    /// <param name="custom">What <see cref="ReadCustomColumns"/> answered for this same page, or
    /// null on a list built with no custom column delegates, which leaves the custom column set
    /// exactly as it was.</param>
    public void Load(TargetListingPage page, CustomColumnPage? custom = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (custom is not null)
        {
            ApplyCustomColumns(custom);
        }

        // A re-query keeps the rows the reader opened; a key that is gone is dropped.
        var expanded = Rows.Where(row => row.IsExpanded).Select(row => row.GroupKey).ToHashSet(StringComparer.Ordinal);

        // Every row's cells go with the row that held them. An undisposed cell keeps an open
        // debounce window alive (AutosaveField.Dispose), so a page refresh during typing would
        // otherwise leave one write per recycled row parked on a row nothing renders.
        foreach (var row in Rows)
        {
            row.Dispose();
        }

        Rows.Clear();
        foreach (var row in page.Rows)
        {
            // The closure reads IsRefetching live rather than capturing its value at
            // construction, because a row instance survives the whole refetch it was built
            // outside of (review finding, fix pass: ToggleSessionsCommand's own body guard).
            var added = new TargetRowViewModel(row, Columns, () => IsRefetching, _customContext, ValuesFor(row.TargetId), _aliases);
            added.SetDrawnCustomCells(_drawnCustomColumns);
            if (expanded.Contains(added.GroupKey))
            {
                added.IsExpanded = true;
                added.RefreshSessionCells();
            }

            Rows.Add(added);
        }

        TotalGroups = page.TotalGroups;

        // Review item 3: a scan or a filter can shrink the set under the page the user is standing
        // on. Clamping raises Changed, so the dashboard re-queries once for the page that now
        // exists rather than leaving an empty list and "Page 5 of 2" under it.
        if (Page > PageCount)
        {
            Page = PageCount;
        }
    }

    /// <summary>Back to page one without raising <see cref="Changed"/>. The dashboard calls this
    /// when a filter changes: that change is already opening a query window of its own, and a
    /// second one would only restart the same debounce.</summary>
    public void ResetPage() => Suspended(() => Page = 1);

    // ---- Spec 12.15: the custom column set, the column rows and the cell wiring ----

    // The stored hidden-slug list this page seeds a custom column's tick from. LastWritten first,
    // for the reason ColumnPickerViewModel.ForFrames states: the display document AppHost read at
    // startup is a snapshot, and it is stale the moment any table toggles a column, so a column
    // hidden since then would come back ticked and the next toggle would write the stale list
    // back over the hide.
    private IReadOnlyList<string> StoredHiddenKeys()
        => _columns.LastWritten(DisplaySettings.DashboardHiddenTableId)
            ?? _initialDisplay.ColumnsFor(DisplaySettings.DashboardHiddenTableId);

    // The live custom ticks as a hidden-slug list, the shape CustomColumnSet takes.
    private string[] HiddenCustomKeys()
        => [.. Columns
            .Where(column => CustomColumnSlug.IsCustom(column.Key) && !column.IsVisible)
            .Select(column => column.Key)];

    private void ApplyCustomColumns(CustomColumnPage custom)
    {
        _definitions = custom.Definitions;
        _valuesByTarget = custom.Values
            .Where(value => value.Key.TargetId is not null)
            .GroupBy(value => value.Key.TargetId!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CustomValueRow>)[.. group]);

        SyncCustomColumnRows(StoredHiddenKeys());
        RefreshCustomColumnSet();
    }

    // One ColumnViewModel per target-scope custom column, appended after last_session, so the
    // column gear and the Display tab's picker can switch one off. CustomColumnSet is the one place
    // the scope filter and the display order live (design lesson 1); the picker needs a row for a
    // column that is switched OFF as well, so the gate it is handed here hides nothing, and the
    // shown subset comes from the same member with the live ticks in RefreshCustomColumnSet.
    //
    // An entry is reused rather than rebuilt while its slug and its name both still match, so a
    // picker already holding the object keeps holding the one this list holds.
    private void SyncCustomColumnRows(IReadOnlyList<string> hidden)
    {
        var wanted = CustomColumnSet.DashboardRow(_definitions, []);

        // Nothing changed, which is every query on a library whose columns nobody has just edited.
        // Removing and appending the same entries raises two collection changes per column, and
        // each one rebuilds the header strip and resets any control bound to Columns, the open
        // column gear among them, which is the Phase 16 flyout trap in another shape.
        if (CustomTailMatches(wanted))
        {
            return;
        }

        var existing = Columns
            .Where(column => CustomColumnSlug.IsCustom(column.Key))
            .ToDictionary(column => column.Key, StringComparer.Ordinal);

        for (var index = Columns.Count - 1; index >= 0; index--)
        {
            if (CustomColumnSlug.IsCustom(Columns[index].Key))
            {
                Columns.RemoveAt(index);
            }
        }

        foreach (var definition in wanted)
        {
            Columns.Add(
                existing.TryGetValue(definition.Slug, out var column) && column.Title == definition.Name
                    ? column
                    // On by default: a new column's slug is in no hidden list, so it shows until
                    // the picker switches it off. No metric group gates a custom column.
                    : new ColumnViewModel(
                        definition.Slug,
                        definition.Name,
                        CustomColumnSet.IsShown(hidden, definition.Slug),
                        canHide: true,
                        isGroupEnabled: true));
        }
    }

    // Whether the custom entries already on the end of Columns are the wanted ones, in order, by
    // slug and by name. A renamed column changes its name and a reordered one changes its position,
    // so both are caught here and nothing else can change what a column row carries.
    private bool CustomTailMatches(IReadOnlyList<CustomColumnDefinition> wanted)
    {
        var existing = Columns.Where(column => CustomColumnSlug.IsCustom(column.Key)).ToArray();
        return existing.Length == wanted.Count
            && existing.Zip(wanted).All(pair =>
                string.Equals(pair.First.Key, pair.Second.Slug, StringComparison.Ordinal)
                && string.Equals(pair.First.Title, pair.Second.Name, StringComparison.Ordinal));
    }

    // The drawn set and the wiring, from the live ticks rather than from the stored list, so a
    // toggle takes effect without a round trip to the display document.
    private void RefreshCustomColumnSet()
    {
        CustomColumns = CustomColumnSet.DashboardRow(_definitions, HiddenCustomKeys());

        _customContext = _writeValue is null || _loadValuesForTarget is null
            ? null
            : new CustomCellContext(
                CustomColumns,
                CustomColumnSet.NightExpander(_definitions),
                _loadValuesForTarget,
                _writeValue,
                _post,
                _cellDelay,
                _cellLogger);
    }

    private IReadOnlyList<CustomValueRow>? ValuesFor(Guid? targetId)
        => targetId is Guid id && _valuesByTarget.TryGetValue(id, out var values) ? values : null;

    [RelayCommand]
    private void SortBy(string? columnKey)
    {
        // A column key with no sort mapping (designation, palette) is ignored rather than
        // clearing the sort: spec 12.2 lists six sort keys and neither of those is one.
        if (columnKey is null || !SortKeys.TryGetValue(columnKey, out var mapped))
        {
            return;
        }

        Suspended(() =>
        {
            if (Sort == mapped.Sort)
            {
                Descending = !Descending;
            }
            else
            {
                Sort = mapped.Sort;
                Descending = mapped.Descending;
            }

            Page = 1;
        });

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Coordinator ruling 3: a page command that must not run during a refetch carries the same
    // check in its body, because CanExecute is the affordance and not the guard (TRACKING item
    // 13's own rule, applied to the refetch flag rather than to the page bounds). Without this a
    // rapid double click, or a direct Execute past a CanExecute that has not yet notified, could
    // open a second query window on top of the one already in flight.
    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private void NextPage()
    {
        if (IsRefetching)
        {
            return;
        }

        Page++;
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private void PreviousPage()
    {
        if (IsRefetching)
        {
            return;
        }

        Page--;
    }

    /// <summary>A numbered page button was clicked (spec 12.2). The ellipsis is not a button and
    /// never reaches this, but the current page's own button does, so the gate is repeated in the
    /// body rather than left to a <c>CanExecute</c> alone (TRACKING section 6 item 13): a direct
    /// <c>Execute</c> past that gate must still do nothing.</summary>
    [RelayCommand]
    private void GoToPage(int? page)
    {
        if (IsRefetching || page is not int target || target < 1 || target > PageCount || target == Page)
        {
            return;
        }

        Page = target;
    }

    // Review finding (fix pass): IsHitTestVisible blocks the pointer alone, so Enter or Space on
    // a focused row still reached this command during a flight. The guard matches the pointer's
    // own outcome rather than leaving keyboard activation as the one path still open.
    [RelayCommand]
    private void OpenTarget(TargetRowViewModel? row)
    {
        if (row is not null && !IsRefetching)
        {
            TargetOpened?.Invoke(this, new TargetOpenRequest(row.GroupKey, null));
        }
    }

    /// <summary>Spec 12.2's mosaic link: opens the first of the row's mosaics by name and never
    /// the row's Target detail. Guarded on a flight the way <see cref="OpenTarget"/> is.</summary>
    [RelayCommand]
    private void OpenMosaic(TargetRowViewModel? row)
    {
        if (row?.MosaicId is { } mosaicId && !IsRefetching)
        {
            MosaicOpened?.Invoke(this, mosaicId);
        }
    }

    /// <summary>Spec 12.2's Deep dive action: the row's group key and the session line's own date,
    /// and nothing else (task6.md 6.3). Works identically for an unresolved <c>obj:</c> group,
    /// whose <see cref="SessionRowViewModel.GroupKey"/> already carries that prefix.</summary>
    [RelayCommand]
    private void DeepDive(SessionRowViewModel? session)
    {
        if (session is not null)
        {
            TargetOpened?.Invoke(this, new TargetOpenRequest(session.GroupKey, session.SessionDate));
        }
    }

    [RelayCommand]
    private void ToggleColumn(ColumnViewModel? column)
    {
        // Ruling Q5: name is the one column that cannot be turned off. Turning it back on is
        // allowed, which is what makes an externally written list without it recoverable.
        if (column is null || (!column.CanHide && column.IsVisible))
        {
            return;
        }

        column.IsVisible = !column.IsVisible;
        OnPropertyChanged(nameof(VisibleColumns));
        PersistColumns();

        // Spec 12.15: a custom column's tick is what decides whether a cell is drawn at all, so
        // the drawn set and every row's cells follow it here rather than waiting for the next
        // query. Hiding a column disposes its cells, which flushes whatever was being typed.
        if (CustomColumnSlug.IsCustom(column.Key))
        {
            _columns.Write(DisplaySettings.DashboardHiddenTableId, HiddenCustomKeys());
            RefreshCustomColumnSet();
            foreach (var row in Rows)
            {
                row.RebuildCustomCells(_customContext, ValuesFor(row.Row.TargetId));
                row.SetDrawnCustomCells(_drawnCustomColumns);
            }
        }
    }

    /// <summary>Flushes and disposes every row's cells. The dashboard owns this list and disposes
    /// it with itself, so the last second of typing in a cell is not lost at shutdown.</summary>
    /// <remarks>The wait is what makes that sentence true, and it happens once for the whole page,
    /// not once per row: <c>CustomCellGroup.Dispose</c> starts a flush and cannot await it, so a
    /// row disposed on its own only queues the write. The budget is the same two seconds
    /// <c>SessionCardViewModel</c> spends on the Target page, and is a stutter rather than a hang:
    /// the window closes on time whatever the write chain is doing. The per-row Dispose below stays
    /// non-blocking, so a page replacement still costs the UI thread nothing.</remarks>
    public void Dispose()
    {
        try
        {
            Task.WhenAll(Rows.Select(row => row.FlushAsync())).Wait(FlushBudget);
        }
        catch (Exception ex)
        {
            _cellLogger?.LogWarning(ex, "Flushing the dashboard's custom column cells on close failed");
        }

        foreach (var row in Rows)
        {
            row.Dispose();
        }

        // The gear's picker follows the live column collection, so it holds a subscription to drop.
        ColumnPicker.Dispose();
    }

    /// <summary>The in-flight column write, so a test can await it instead of sleeping. Forwards
    /// to <see cref="DisplayColumnWriter.Pending"/>, which is now where the chain lives.</summary>
    internal Task PendingPersist => _columns.Pending;

    // The ordered list of visible keys, in the fixed column order rather than the order the user
    // clicked them (spec 5.8.2 stores an order; spec 12.2 asks only for visibility). The writer
    // copies the keys on this thread and does the load-modify-save off it, so the frames table's
    // entry and every other key of the display document survive.
    private void PersistColumns()
        => _columns.Write(
            DisplaySettings.DashboardTableId,
            [.. VisibleColumns.Select(column => column.Key)]);

    // Sort, direction and page move together on one click; the observers must see one Changed,
    // not three. A depth counter, not a flag: SortBy's block nests the property setters' own.
    private void Suspended(Action mutate)
    {
        _suspendDepth++;
        try
        {
            mutate();
        }
        finally
        {
            _suspendDepth--;
        }
    }

    private void RaiseChanged()
    {
        if (_suspendDepth == 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Any of the four re-queries. Changing sort, direction or page size goes back to page one;
    // changing the page does not.
    partial void OnSortChanged(TargetListingSort value) => ResetPageAndNotify();

    partial void OnDescendingChanged(bool value) => ResetPageAndNotify();

    partial void OnPageSizeChanged(int value) => ResetPageAndNotify();

    partial void OnPageChanged(int value) => RaiseChanged();

    // The header glyph on the active sort column, kept on the columns themselves so the header
    // template stays a plain binding.
    private void UpdateSortGlyphs()
    {
        foreach (var column in Columns)
        {
            column.SortGlyph = SortKeys.TryGetValue(column.Key, out var mapped) && mapped.Sort == Sort
                ? Descending ? "\u25BC" : "\u25B2"
                : "";
        }
    }

    private void ResetPageAndNotify()
    {
        UpdateSortGlyphs();
        Suspended(() => Page = 1);
        RaiseChanged();
    }
}

/// <summary>What one dashboard query's custom column read answered (spec 12.15): every definition
/// and every target-scope value of the targets on that page, taken together on the page's own
/// background hop so the two cannot describe different pages.</summary>
/// <param name="Definitions">Every custom column of every scope, in the repository's order. The
/// scope filters are <c>CustomColumnSet</c>'s and are applied by the surface, not here.</param>
/// <param name="Values">The target-scope values of the page's resolved rows. An unresolved
/// <c>obj:</c> group has no target id and contributes nothing.</param>
public sealed record CustomColumnPage(
    IReadOnlyList<CustomColumnDefinition> Definitions,
    IReadOnlyList<CustomValueRow> Values)
{
    /// <summary>What a page with no custom column delegates answers: no definition and no value,
    /// so no caller writes a null check.</summary>
    public static CustomColumnPage None { get; } = new([], []);
}

/// <summary>A request to open Target detail: PAR-018's group key and optional night (spec 12.4).
/// Carries nothing else (task6.md 6.2's widened-payload shape), so a dashboard row click and a
/// session line's Deep dive action route through the one <see cref="TargetListViewModel.TargetOpened"/>
/// event and the shell's one handler.</summary>
public sealed record TargetOpenRequest(string GroupKey, DateOnly? SessionDate);

/// <summary>One entry of the pager's page button set (spec 12.2). <see cref="Page"/> is null for
/// the ellipsis, which the view draws as a non-interactive text run rather than a button. A
/// record, not a class: nothing on it is mutable and the whole list is rebuilt on every change.
/// </summary>
public sealed record PageButtonViewModel(int? Page, bool IsCurrent);
