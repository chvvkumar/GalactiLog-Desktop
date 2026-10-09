using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.2 and 18.3: the target list owns sorting, paging and column visibility, and
// contributes them to TargetListingCriteria. Plain xunit facts -- no window, no dispatcher, and
// no database except where a test asserts the settings round trip, which uses the same
// SettingsFixture (a real migrated SQLite file plus a real SettingsStore) the watcher tests use.
public class TargetListViewModelTests
{
    // ---- construction helpers -------------------------------------------------------------

    // The display document, in memory. SettingsStore is sealed over a real database, so the
    // view-model takes the two operations it needs as delegates (the same rule Task 6 applied to
    // AliasMapCache); the round-trip tests below bind them to a real store instead.
    private sealed class FakeDisplayStore
    {
        public FakeDisplayStore(DisplaySettings? seed = null) => Current = seed ?? new DisplaySettings();

        public DisplaySettings Current { get; private set; }

        public int Saves { get; private set; }

        public DisplaySettings Get() => Current;

        public void Save(DisplaySettings value)
        {
            Current = value;
            Saves++;
        }

        public string[] DashboardColumns => Current.Columns[DisplaySettings.DashboardTableId];
    }

    // Phase review item 2: SettingsRepository.Load is a synchronous SQLite read, and this
    // view-model is constructed on the UI thread during host startup. The column list therefore
    // arrives by value; the delegate exists only for the background load-modify-save behind a
    // column click. A getDisplay that throws proves nothing on the construction path calls it.
    [Fact]
    public void Constructor_TakesTheColumnsByValue_AndNeverReadsTheSettingsDocument()
    {
        var stored = new DisplaySettings
        {
            Columns = new Dictionary<string, string[]>
            {
                [DisplaySettings.DashboardTableId] = ["name", "integration"],
            },
        };

        var list = new TargetListViewModel(
            stored,
            () => throw new InvalidOperationException("the constructor must not read settings"),
            _ => { },
            50);

        Assert.Equal(
            ["name", "integration"],
            list.VisibleColumns.Select(column => column.Key));
    }

    private static TargetListViewModel CreateList(FakeDisplayStore? store = null, int pageSize = 50)
    {
        store ??= new FakeDisplayStore();
        return new TargetListViewModel(store.Get(), store.Get, store.Save, pageSize);
    }

    private static TargetRow Row(
        string name = "M 31",
        Guid? targetId = null,
        string? commonName = "Andromeda Galaxy",
        string? catalogId = "M 31",
        double integrationSeconds = 44_640d,
        int frameCount = 148,
        int sessionCount = 3,
        DateOnly? lastSession = null,
        IReadOnlyList<FilterBadge>? palette = null,
        IReadOnlyList<string>? equipment = null,
        IReadOnlyList<SessionSummary>? sessions = null)
        => new(
            GroupKey: targetId?.ToString() ?? $"obj:{name}",
            TargetId: targetId,
            Name: name,
            CommonName: commonName,
            CatalogId: catalogId,
            ObjectType: "G",
            ObjectCategory: "Galaxy",
            IntegrationSeconds: integrationSeconds,
            FrameCount: frameCount,
            SessionCount: sessionCount,
            FirstSession: new DateOnly(2024, 1, 5),
            LastSession: lastSession ?? new DateOnly(2025, 12, 7),
            Palette: palette ?? [new FilterBadge("Ha", "#FF0000", 60, 18_000d)],
            Equipment: equipment ?? ["RC8 / ASI2600MM"],
            Aliases: [],
            Sessions: sessions ?? [new SessionSummary(new DateOnly(2025, 12, 7), 40, 12_000d)]);

    private static TargetListingPage PageOf(IReadOnlyList<TargetRow> rows, int totalGroups = 0)
        => new(rows, totalGroups == 0 ? rows.Count : totalGroups, 0d, 0, 1, 50);

    // Records every criteria the dashboard hands the listing query. The dashboard runs the query
    // off the calling thread, hence the lock.
    private sealed class RecordingListQuery
    {
        private readonly Lock _gate = new();
        private readonly List<TargetListingCriteria> _calls = [];

        public TargetListingPage Page { get; set; } = new([], 0, 0d, 0, 1, 50);

        public IReadOnlyList<TargetListingCriteria> Calls
        {
            get { lock (_gate) { return [.. _calls]; } }
        }

        public void Clear()
        {
            lock (_gate) { _calls.Clear(); }
        }

        public TargetListingPage List(TargetListingCriteria criteria)
        {
            lock (_gate) { _calls.Add(criteria); }
            return Page;
        }
    }

    private static DashboardViewModel CreateDashboard(
        RecordingListQuery query,
        FakeDelay delay,
        FakeDisplayStore? store = null,
        ScanStatusService? scanStatus = null)
    {
        store ??= new FakeDisplayStore();
        return new DashboardViewModel(
            query.List,
            () => new DashboardFacets([], [], []),
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            delay.Delay,
            store.Get(),
            store.Get,
            store.Save,
            scanStatus,
            // Apply the result inline: these are plain xunit facts with no dispatcher, and the
            // same seam ScanStatusServiceTests uses.
            post: action => action());
    }

    private static async Task DrainAsync(DashboardViewModel dashboard, FakeDelay delay)
    {
        // The panel reloads its option lists on a background task and raises Changed when it
        // lands, which opens a query window of its own. Settle that first or a later assertion
        // counts it as this test's query.
        if (dashboard.Filters.PendingReload is { } reload)
        {
            await reload;
        }

        var work = dashboard.PendingQuery;
        if (work is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!work.IsCompleted && DateTime.UtcNow < deadline)
        {
            delay.Release();
            await Task.Delay(2);
        }

        Assert.True(work.IsCompleted, "The dashboard's pending query never completed.");
        await work;
        Assert.Null(dashboard.LastQueryFailure);
    }

    // ---- sorting (spec 12.2's six keys) ----------------------------------------------------

    [Theory]
    [InlineData("name", TargetListingSort.Name)]
    [InlineData("integration", TargetListingSort.Integration)]
    [InlineData("frames", TargetListingSort.Frames)]
    [InlineData("sessions", TargetListingSort.Sessions)]
    [InlineData("last_session", TargetListingSort.LastSession)]
    [InlineData("equipment", TargetListingSort.Equipment)]
    public void Sort_EachColumnKey_MapsToItsSortKey(string columnKey, TargetListingSort expected)
    {
        var list = CreateList();

        list.SortByCommand.Execute(columnKey);

        Assert.Equal(expected, list.Sort);
    }

    [Fact]
    public void Sort_ClickingTheActiveColumn_FlipsDirection()
    {
        var list = CreateList();
        var changed = 0;
        list.Changed += (_, _) => changed++;

        // The landing state is last session, descending.
        list.SortByCommand.Execute("last_session");
        Assert.Equal(TargetListingSort.LastSession, list.Sort);
        Assert.False(list.Descending);

        list.SortByCommand.Execute("last_session");
        Assert.True(list.Descending);

        // One query window per click, not one per property that moved.
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Sort_ClickingAnotherColumn_ResetsToThatColumnsDefaultDirection()
    {
        var list = CreateList();

        // Descending for the numeric and date keys, ascending for the two textual ones.
        list.SortByCommand.Execute("integration");
        Assert.True(list.Descending);

        list.SortByCommand.Execute("name");
        Assert.Equal(TargetListingSort.Name, list.Sort);
        Assert.False(list.Descending);

        list.SortByCommand.Execute("equipment");
        Assert.False(list.Descending);

        list.SortByCommand.Execute("frames");
        Assert.True(list.Descending);
    }

    [Fact]
    public async Task Sort_ProducesTheExpectedCriteria()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);
        query.Clear();

        dashboard.Targets.SortByCommand.Execute("integration");
        await DrainAsync(dashboard, delay);

        var criteria = Assert.Single(query.Calls);
        Assert.Equal(TargetListingSort.Integration, criteria.Sort);
        Assert.True(criteria.Descending);
        Assert.Equal(1, criteria.Page);
        Assert.Equal(new GeneralSettings().DefaultPageSize, criteria.PageSize);

        query.Clear();
        dashboard.Targets.SortByCommand.Execute("integration");
        await DrainAsync(dashboard, delay);

        Assert.False(Assert.Single(query.Calls).Descending);
    }

    [Theory]
    [InlineData("designation")]
    [InlineData("palette")]
    [InlineData("no_such_column")]
    [InlineData(null)]
    public void Sort_UnmappedColumnKey_IsIgnored(string? columnKey)
    {
        var list = CreateList();
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.SortByCommand.Execute(columnKey);

        Assert.Equal(TargetListingSort.LastSession, list.Sort);
        Assert.True(list.Descending);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Sort_ActiveColumn_CarriesTheDirectionGlyph()
    {
        var list = CreateList();

        var lastSession = list.Columns.Single(column => column.Key == "last_session");
        Assert.Equal("\u25BC", lastSession.SortGlyph);   // descending

        list.SortByCommand.Execute("name");

        Assert.Equal("", lastSession.SortGlyph);
        Assert.Equal("\u25B2", list.Columns.Single(column => column.Key == "name").SortGlyph);   // ascending
    }

    // ---- paging ----------------------------------------------------------------------------

    [Fact]
    public async Task Paging_NextAndPrevious_ProduceTheExpectedPageCriteria()
    {
        var query = new RecordingListQuery { Page = new TargetListingPage([], 120, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);
        query.Clear();

        dashboard.Targets.NextPageCommand.Execute(null);
        await DrainAsync(dashboard, delay);
        Assert.Equal(2, Assert.Single(query.Calls).Page);

        query.Clear();
        dashboard.Targets.NextPageCommand.Execute(null);
        await DrainAsync(dashboard, delay);
        Assert.Equal(3, Assert.Single(query.Calls).Page);

        query.Clear();
        dashboard.Targets.PreviousPageCommand.Execute(null);
        await DrainAsync(dashboard, delay);
        Assert.Equal(2, Assert.Single(query.Calls).Page);
    }

    [Fact]
    public void Paging_PageSize_SeedsFromDefaultPageSize()
    {
        Assert.Equal(50, new GeneralSettings().DefaultPageSize);
        Assert.Equal(50, CreateList().PageSize);
        Assert.Equal(25, CreateList(pageSize: 25).PageSize);

        // A non-positive figure cannot reach here through SettingsStore (it validates), but a
        // zero page size would divide by zero in PageCount, so it is clamped rather than trusted.
        // This is PageSize's own clamp, unrelated to spec 5.8.1's select vocabulary (Phase 14C
        // Task 4): SelectedPageSize is what normalizes to the select's default of 50, below.
        Assert.Equal(1, CreateList(pageSize: 0).PageSize);
        Assert.Equal(50, CreateList(pageSize: 0).SelectedPageSize);
        Assert.Equal(50, CreateList(pageSize: 37).SelectedPageSize);
        Assert.Equal(25, CreateList(pageSize: 25).SelectedPageSize);
    }

    [Fact]
    public void Paging_NextPage_DisabledOnTheLastPage()
    {
        var list = CreateList(pageSize: 10);
        list.Load(PageOf([], totalGroups: 25));

        Assert.Equal(3, list.PageCount);
        Assert.True(list.NextPageCommand.CanExecute(null));

        list.NextPageCommand.Execute(null);
        list.NextPageCommand.Execute(null);

        Assert.Equal(3, list.Page);
        Assert.False(list.HasNextPage);
        Assert.False(list.NextPageCommand.CanExecute(null));
        Assert.Equal("21 to 25 of 25", list.PageStatusText);
    }

    [Fact]
    public void Paging_PreviousPage_DisabledOnTheFirstPage()
    {
        var list = CreateList(pageSize: 10);
        list.Load(PageOf([], totalGroups: 25));

        Assert.Equal(1, list.Page);
        Assert.False(list.HasPreviousPage);
        Assert.False(list.PreviousPageCommand.CanExecute(null));

        list.NextPageCommand.Execute(null);
        Assert.True(list.PreviousPageCommand.CanExecute(null));
    }

    [Fact]
    public void Paging_ChangingSort_ResetsToPageOne()
    {
        var list = CreateList(pageSize: 10);
        list.Load(PageOf([], totalGroups: 100));
        list.NextPageCommand.Execute(null);
        list.NextPageCommand.Execute(null);
        Assert.Equal(3, list.Page);

        list.SortByCommand.Execute("name");
        Assert.Equal(1, list.Page);

        list.NextPageCommand.Execute(null);
        list.PageSize = 20;
        Assert.Equal(1, list.Page);
    }

    [Fact]
    public void Paging_ChangingPage_DoesNotResetSort()
    {
        var list = CreateList(pageSize: 10);
        list.Load(PageOf([], totalGroups: 100));
        list.SortByCommand.Execute("name");

        list.NextPageCommand.Execute(null);

        Assert.Equal(2, list.Page);
        Assert.Equal(TargetListingSort.Name, list.Sort);
        Assert.False(list.Descending);
    }

    [Fact]
    public async Task Paging_AFilterChange_ResetsToPageOne()
    {
        // A narrower set can have fewer pages than the one the user is standing on.
        var query = new RecordingListQuery { Page = new TargetListingPage([], 120, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);

        dashboard.Targets.NextPageCommand.Execute(null);
        await DrainAsync(dashboard, delay);
        Assert.Equal(2, dashboard.Targets.Page);

        query.Clear();
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await DrainAsync(dashboard, delay);

        Assert.Equal(1, dashboard.Targets.Page);
        Assert.Equal(1, Assert.Single(query.Calls).Page);
    }

    // ---- column visibility (spec 5.8.2) ----------------------------------------------------

    [Fact]
    public void Columns_LoadFromDisplaySettings()
    {
        var store = new FakeDisplayStore(new DisplaySettings
        {
            Columns = new Dictionary<string, string[]>
            {
                [DisplaySettings.DashboardTableId] = ["name", "integration", "last_session"],
            },
        });

        var list = CreateList(store);

        // Six columns always, in the documented order; the stored list sets visibility only.
        Assert.Equal(
            ["name", "designation", "palette", "integration", "equipment", "last_session"],
            list.Columns.Select(column => column.Key));
        Assert.Equal(
            ["name", "integration", "last_session"],
            list.VisibleColumns.Select(column => column.Key));
    }

    [Fact]
    public async Task Columns_HidingAColumn_WritesTheOrderedVisibleKeyList()
    {
        // Through a real SettingsStore over a real migrated database: this is the roadmap's
        // "hiding a column writes the ordered list" line, and it has to survive serialization.
        using var settings = new SettingsFixture();
        var list = new TargetListViewModel(
            settings.Store.GetDisplay(), settings.Store.GetDisplay, settings.Store.SaveDisplay, 50);

        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "palette"));
        await list.PendingPersist;

        Assert.Equal(
            ["name", "designation", "integration", "equipment", "last_session"],
            settings.Store.GetDisplay().ColumnsFor(DisplaySettings.DashboardTableId));

        // Every other table's entry survives the load-modify-save.
        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.FramesTableId],
            settings.Store.GetDisplay().ColumnsFor(DisplaySettings.FramesTableId));

        // And it comes back on.
        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "palette"));
        await list.PendingPersist;
        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            settings.Store.GetDisplay().ColumnsFor(DisplaySettings.DashboardTableId));
    }

    [Fact]
    public async Task Columns_HidingAColumn_PreservesTheDocumentedOrder_NotClickOrder()
    {
        var store = new FakeDisplayStore();
        var list = CreateList(store);

        // Hide the last, then the second; turn the last back on. Click order is last, designation,
        // last -- the persisted list is still the documented order.
        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "last_session"));
        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "designation"));
        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "last_session"));
        await list.PendingPersist;

        Assert.Equal(["name", "palette", "integration", "equipment", "last_session"], store.DashboardColumns);
    }

    [Fact]
    public async Task Columns_NameColumn_CannotBeHidden()
    {
        var store = new FakeDisplayStore();
        var list = CreateList(store);
        var name = list.Columns.Single(column => column.Key == "name");

        Assert.False(name.CanHide);

        list.ToggleColumnCommand.Execute(name);
        await list.PendingPersist;

        Assert.True(name.IsVisible);
        Assert.Equal(0, store.Saves);

        // Ruling Q5's other half: an externally written list that omits name is recoverable,
        // because name is forced visible rather than merely un-hideable.
        var external = new FakeDisplayStore(new DisplaySettings
        {
            Columns = new Dictionary<string, string[]> { [DisplaySettings.DashboardTableId] = [] },
        });

        Assert.True(CreateList(external).Columns.Single(column => column.Key == "name").IsVisible);
    }

    [Fact]
    public async Task Columns_ToggleDoesNotIssueAQuery()
    {
        // Hiding palette or equipment is a rendering change. It must not cost a round trip to
        // the images table.
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var store = new FakeDisplayStore();
        var dashboard = CreateDashboard(query, delay, store);
        await DrainAsync(dashboard, delay);
        query.Clear();

        var columnsChanged = 0;
        dashboard.Targets.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TargetListViewModel.VisibleColumns)) columnsChanged++;
        };

        dashboard.Targets.ToggleColumnCommand.Execute(
            dashboard.Targets.Columns.Single(column => column.Key == "equipment"));

        await dashboard.Targets.PendingPersist;
        await DrainAsync(dashboard, delay);

        Assert.Empty(query.Calls);
        Assert.Equal(1, columnsChanged);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void Columns_MissingDashboardEntry_FallsBackToTheDefaultList()
    {
        // The view-model level of the same rule DisplaySettingsColumnsTests pins in Core.
        var store = new FakeDisplayStore(new DisplaySettings { Columns = new Dictionary<string, string[]>() });

        var list = CreateList(store);

        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            list.VisibleColumns.Select(column => column.Key));
    }

    // ---- rows ------------------------------------------------------------------------------

    [Fact]
    public void Rows_RenderEverySpecifiedColumnValue()
    {
        var list = CreateList();
        list.Load(PageOf([Row(
            name: "M 31",
            targetId: Guid.Parse("10000000-0000-0000-0000-000000000000"),
            commonName: "Andromeda Galaxy",
            catalogId: "M 31",
            integrationSeconds: 44_640d,
            palette: [new FilterBadge("Ha", "#FF0000", 60, 18_000d), new FilterBadge("OIII", "bad-colour", 40, 12_000d)],
            equipment: ["FRA600 / ASI294MC", "RC8 / ASI2600MM"],
            lastSession: new DateOnly(2025, 12, 7))]));

        var row = Assert.Single(list.Rows);

        Assert.Equal("M 31", row.Name);
        Assert.Equal("Andromeda Galaxy", row.CommonName);
        Assert.True(row.HasCommonName);
        Assert.Equal("M 31", row.Designation);
        Assert.Equal(["Ha", "OIII"], row.PaletteBadges.Select(badge => badge.CanonicalName));
        Assert.Equal([60, 40], row.PaletteBadges.Select(badge => badge.FrameCount));
        Assert.Equal("12.4", row.IntegrationText);
        Assert.Equal("FRA600 / ASI294MC, RC8 / ASI2600MM", row.EquipmentText);
        Assert.Equal("2025-12-07", row.LastSessionText);

        // A malformed configured colour falls back to grey rather than throwing in a template.
        Assert.Equal(Avalonia.Media.Color.Parse("#FF0000"), row.PaletteBadges[0].Tint.Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#808080"), row.PaletteBadges[1].Tint.Color);

        // Every cell can find its column, which is what the row template binds visibility to.
        Assert.Same(list.Columns, row.Columns);
    }

    [Fact]
    public void Rows_PaletteBadges_RankLrgbShoFirst_ThenTheRestAlphabetically()
    {
        // Polish ruling 5. A failure is the alphabetical order B, Ha, IR, L, or the query's own
        // order IR, Ha, B, L.
        var list = CreateList();
        list.Load(PageOf([Row(
            name: "M 31",
            palette:
            [
                new FilterBadge("IR", "#808080", 1, 100d),
                new FilterBadge("Ha", "#FF0000", 60, 18_000d),
                new FilterBadge("B", "#0000FF", 40, 12_000d),
                new FilterBadge("L", "#FFFFFF", 90, 27_000d),
            ])]));

        var row = Assert.Single(list.Rows);

        Assert.Equal(["L", "B", "Ha", "IR"], row.PaletteBadges.Select(badge => badge.CanonicalName));
    }

    [Fact]
    public void Rows_MissingValues_RenderTheDash()
    {
        // Spec.md item 6: a table cell never goes silently blank. The common name is not a cell of
        // its own (it rides beside the name), so it stays empty.
        var list = CreateList();
        list.Load(PageOf([Row(commonName: null, catalogId: null, equipment: []) with { LastSession = null }]));

        var row = Assert.Single(list.Rows);

        Assert.Equal("", row.CommonName);
        Assert.False(row.HasCommonName);
        Assert.Equal(MetricText.Missing, row.Designation);
        Assert.Equal(MetricText.Missing, row.EquipmentText);
        Assert.Equal(MetricText.Missing, row.LastSessionText);
    }

    [Fact]
    public void Columns_TitlesAreSentenceCase()
    {
        // Spec.md item 5 and ruling R15: one header style, sentence case, and the integration
        // column is "Hours" over unitless figures.
        Assert.Equal(
            ["Name", "Designation", "Palette", "Hours", "Equipment", "Last session"],
            CreateList().Columns.Select(column => column.Title));
    }

    [Fact]
    public void Rows_TheInlineCommonName_IsSuppressedWhenThePrimaryNameAlreadyCarriesIt()
    {
        // Coordinator addition from C:\tmp\p14c-fxrows-noequip.png: the row read
        // "NGC 1909 - the Witch Head Nebula  the Witch Head Nebula", because the primary name
        // already carries the common name and the Name cell printed it again in the secondary ink.
        // The second run is shown only when the primary name does not already contain it.
        var list = CreateList();
        list.Load(PageOf([
            Row(name: "NGC 1909 - the Witch Head Nebula", commonName: "the Witch Head Nebula"),
            Row(name: "NGC 1909 - The Witch Head Nebula", commonName: "the witch head nebula"),
            Row(name: "NGC 6960 - Veil Nebula", commonName: "Veil Nebula,Filamentary Nebula,Western Veil"),
            Row(name: "NGC 6960 - Veil Nebula", commonName: " Veil Nebula , Filamentary Nebula "),
            Row(name: "M 31", commonName: "Andromeda Galaxy"),
            Row(name: "M 31", commonName: "Andromeda Galaxy,Messier 31"),
        ]));

        // Contained, and contained whatever the casing.
        Assert.False(list.Rows[0].HasCommonName);
        Assert.False(list.Rows[1].HasCommonName);

        // A comma separated list is suppressed on its first entry.
        Assert.False(list.Rows[2].HasCommonName);
        Assert.False(list.Rows[3].HasCommonName);

        // A name that does not carry it still shows it, list or not.
        Assert.True(list.Rows[4].HasCommonName);
        Assert.Equal("Andromeda Galaxy", list.Rows[4].CommonName);
        Assert.True(list.Rows[5].HasCommonName);
    }

    [Fact]
    public void Rows_SessionsExpander_ListsEverySessionSummary()
    {
        var list = CreateList();
        list.Load(PageOf([Row(sessionCount: 3, sessions:
        [
            new SessionSummary(new DateOnly(2025, 12, 7), 40, 12_000d),
            new SessionSummary(new DateOnly(2025, 11, 19), 30, 9_000d),
            new SessionSummary(new DateOnly(2025, 10, 1), 20, 6_000d),
        ])]));

        var row = Assert.Single(list.Rows);

        Assert.Equal("3 sessions", row.SessionsSummaryText);
        Assert.False(row.IsExpanded);
        Assert.Equal(["2025-12-07", "2025-11-19", "2025-10-01"], row.Sessions.Select(session => session.DateText));
        Assert.Equal([40, 30, 20], row.Sessions.Select(session => session.FrameCount));
        Assert.Equal(["3.3", "2.5", "1.7"], row.Sessions.Select(session => session.IntegrationText));
    }

    [Fact]
    public void Sessions_FrameCountText_UsesThousandsSeparators()
    {
        var list = CreateList();
        list.Load(PageOf([Row(sessions: [new SessionSummary(new DateOnly(2025, 12, 7), 1204, 12_000d)])]));

        Assert.Equal("1,204", Assert.Single(Assert.Single(list.Rows).Sessions).FrameCountText);
    }

    [Fact]
    public void Rows_SessionsSummary_IsSingularAtOne()
    {
        var list = CreateList();
        list.Load(PageOf([Row(sessionCount: 1)]));

        Assert.Equal("1 session", Assert.Single(list.Rows).SessionsSummaryText);
    }

    [Theory]
    [InlineData(0d, "0.0")]
    [InlineData(3_600d, "1.0")]
    [InlineData(44_640d, "12.4")]
    [InlineData(270_000d, "75.0")]
    [InlineData(4_500_000d, "1,250.0")]
    public void Rows_IntegrationText_IsHoursToOneDecimal_WithNoUnit(double seconds, string expected)
    {
        var list = CreateList();
        list.Load(PageOf([Row(integrationSeconds: seconds)]));

        Assert.Equal(expected, Assert.Single(list.Rows).IntegrationText);
    }

    [Fact]
    public void Rows_UnresolvedGroup_RendersTheObjectStringAsTheName()
    {
        var list = CreateList();
        list.Load(PageOf([Row(name: "Bubble Neb", targetId: null, commonName: null, catalogId: null)]));

        var row = Assert.Single(list.Rows);

        Assert.Equal("Bubble Neb", row.Name);
        Assert.Equal("obj:Bubble Neb", row.GroupKey);
        Assert.Null(row.TargetId);
        Assert.Equal(MetricText.Missing, row.Designation);
    }

    [Fact]
    public void OpenTarget_RaisesTargetOpened_WithTheClickedRowsGroupKeyAndNoDate()
    {
        var list = CreateList();
        list.Load(PageOf([Row(name: "M 31"), Row(name: "M 42")]));

        var opened = new List<TargetOpenRequest>();
        list.TargetOpened += (_, request) => opened.Add(request);

        list.OpenTargetCommand.Execute(list.Rows[1]);
        list.OpenTargetCommand.Execute(null);

        // Ruling Q17: it records the request and stops. Phase 6 owns the destination. Phase 14B
        // Task 6 widens the payload to a group key and an optional date; a plain row click still
        // passes null (task6.md 6.2).
        var request = Assert.Single(opened);
        Assert.Equal(list.Rows[1].GroupKey, request.GroupKey);
        Assert.Null(request.SessionDate);
    }

    [Fact]
    public void Load_ReplacesRowsAndTotals()
    {
        var list = CreateList(pageSize: 10);
        list.Load(PageOf([Row(name: "M 31"), Row(name: "M 42")], totalGroups: 25));

        Assert.Equal(["M 31", "M 42"], list.Rows.Select(row => row.Name));
        Assert.Equal(25, list.TotalGroups);
        Assert.Equal(3, list.PageCount);

        list.Load(PageOf([Row(name: "M 13")], totalGroups: 1));

        Assert.Equal(["M 13"], list.Rows.Select(row => row.Name));
        Assert.Equal(1, list.TotalGroups);
        Assert.Equal(1, list.PageCount);
    }

    [Fact]
    public void Load_PageBeyondTheNewPageCount_ClampsAndRequeriesOnce()
    {
        // Review item 3: a scan (or any filter) can shrink the set under the page the user is
        // standing on. Without the clamp the list sits on an empty page with no way back except
        // Previous, and the totals line reads "Page 5 of 2".
        var list = CreateList(pageSize: 10);
        list.Load(PageOf([], totalGroups: 100));
        for (var i = 0; i < 4; i++)
        {
            list.NextPageCommand.Execute(null);
        }

        Assert.Equal(5, list.Page);

        var changed = 0;
        list.Changed += (_, _) => changed++;
        list.Load(PageOf([], totalGroups: 20));

        Assert.Equal(2, list.Page);
        Assert.Equal(2, list.PageCount);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Load_PageWithinTheNewPageCount_IsLeftAlone()
    {
        var list = CreateList(pageSize: 10);
        list.Load(PageOf([], totalGroups: 100));
        list.NextPageCommand.Execute(null);

        var changed = 0;
        list.Changed += (_, _) => changed++;
        list.Load(PageOf([], totalGroups: 60));

        Assert.Equal(2, list.Page);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Columns_Persist_RunsOffTheCallingThreadAndSerialises()
    {
        // Review item 7: the write is a synchronous SQLite load-modify-save. It must not run on
        // the UI thread, and two quick clicks must not interleave into a lost update.
        //
        // F18 follow-up: the off-thread half is proven by parking the save and observing that the
        // click returned anyway, not by comparing thread ids after an await that frees the calling
        // thread for the pool to reuse (TRACKING section 2 item 8).
        using var release = new ManualResetEventSlim(false);
        var display = new DisplaySettings();
        var writes = new List<string[]>();
        var writeThreads = new List<int>();
        var concurrent = 0;
        var overlapped = false;

        void SlowSave(DisplaySettings value)
        {
            if (Interlocked.Increment(ref concurrent) > 1)
            {
                overlapped = true;
            }

            release.Wait(TimeSpan.FromSeconds(30));
            Thread.Sleep(25);
            display = value;
            writes.Add(value.Columns[DisplaySettings.DashboardTableId]);
            writeThreads.Add(Environment.CurrentManagedThreadId);
            Interlocked.Decrement(ref concurrent);
        }

        var list = new TargetListViewModel(display, () => display, SlowSave, 50);

        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "palette"));
        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "equipment"));

        // Both clicks returned while the first save is parked: a synchronous save would have
        // parked the click itself.
        Assert.False(list.PendingPersist.IsCompleted);

        release.Set();
        await list.PendingPersist;

        Assert.False(overlapped);
        Assert.Equal(2, writes.Count);
        Assert.Equal(["name", "designation", "integration", "equipment", "last_session"], writes[0]);
        Assert.Equal(["name", "designation", "integration", "last_session"], writes[1]);
        Assert.Equal(2, writeThreads.Count);
    }

    [Fact]
    public async Task Columns_PersistFailure_IsSwallowedRatherThanCrashingTheClick()
    {
        // The click already changed what is on screen. A failing settings write must not take the
        // window down with it, and must not stop the next click from trying again.
        var display = new DisplaySettings();
        var attempts = 0;
        var logger = new RecordingLogger();
        var list = new TargetListViewModel(
            display,
            () => display,
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("database is locked");
            },
            50,
            logger);

        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "palette"));
        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "equipment"));
        await list.PendingPersist;

        Assert.Equal(2, attempts);

        // F7: the swallow is reported through the injected logger rather than static Serilog.
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task ScanFinished_RefreshesTheList()
    {
        // Through Task 5's ScanStatusService, which has already marshalled the event onto the UI
        // thread. An empty scan root is enough: what matters is that a completed run re-queries.
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var status = new ScanStatusService(coordinator, action => action());

        var query = new RecordingListQuery { Page = PageOf([Row(name: "M 31")]) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay, scanStatus: status);
        await DrainAsync(dashboard, delay);
        Assert.Equal(["M 31"], dashboard.Targets.Rows.Select(row => row.Name));
        query.Clear();

        query.Page = PageOf([Row(name: "M 31"), Row(name: "NGC 6960")], totalGroups: 2);
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        await DrainAsync(dashboard, delay);

        Assert.NotEmpty(query.Calls);
        Assert.Equal(["M 31", "NGC 6960"], dashboard.Targets.Rows.Select(row => row.Name));
        Assert.Equal(2, dashboard.Targets.TotalGroups);
    }

    [Fact]
    public async Task Dashboard_CarriesSortAndPagingIntoTheSameSingleQuery()
    {
        // The list contributes to the criteria the filter panel built: one query call site, one
        // debounce window, both halves present.
        var query = new RecordingListQuery { Page = new TargetListingPage([], 120, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);

        dashboard.Filters.SearchText = "andromeda";
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        dashboard.Targets.SortByCommand.Execute("name");
        dashboard.Targets.NextPageCommand.Execute(null);
        query.Clear();
        await DrainAsync(dashboard, delay);

        var criteria = Assert.Single(query.Calls);
        Assert.Equal("ASI2600MM", criteria.Camera);
        Assert.Equal(TargetListingSort.Name, criteria.Sort);
        Assert.False(criteria.Descending);
        Assert.Equal(2, criteria.Page);
    }

    // ---- default_page_size reaching the singleton list (Phase 9 FIXER item 5) ------------------
    //
    // PageSize is seeded once at construction and this view-model is a singleton underneath the
    // dashboard, so a page size saved on the Settings Display tab must reach it without a restart.
    // The whole fix is one AppHost subscription: no line of this view-model changed, because
    // PageSize is already a settable observable property whose change handler resets the page and
    // re-queries. AppHost.FollowDefaultPageSize is that subscription's body, lifted out so the two
    // rules it carries can be asserted with no window (design-spec 18.3).

    [Fact]
    public void PageSize_CanBeChangedAfterConstruction_AndResetsToPageOne()
    {
        var list = CreateList(pageSize: 50);
        list.Load(PageOf([Row()], totalGroups: 500));
        list.NextPageCommand.Execute(null);
        Assert.Equal(2, list.Page);

        list.PageSize = 25;

        Assert.Equal(25, list.PageSize);
        Assert.Equal(1, list.Page);
    }

    [Fact]
    public void SavingDefaultPageSize_ReachesTheSingletonTargetList()
    {
        var list = CreateList(pageSize: 50);
        var posted = 0;

        AppHost.FollowDefaultPageSize(list, 100, action =>
        {
            posted++;
            action();
        });

        Assert.Equal(100, list.PageSize);

        // Posted to the UI thread, never applied on the saving thread: GeneralChanged is raised on
        // whichever thread wrote, and PageSize's change notification drives an
        // ObservableCollection reload.
        Assert.Equal(1, posted);
    }

    [Fact]
    public void AnUnrelatedGeneralSave_DoesNotResetTheCurrentPage()
    {
        var list = CreateList(pageSize: 50);
        list.Load(PageOf([Row()], totalGroups: 500));
        list.NextPageCommand.Execute(null);
        Assert.Equal(2, list.Page);

        // A theme change, a log level change, an observer coordinate: every general save raises
        // the same event with the same page size in it.
        AppHost.FollowDefaultPageSize(list, 50, action => Assert.Fail("nothing should be posted"));

        Assert.Equal(50, list.PageSize);
        Assert.Equal(2, list.Page);
    }

    // The Sessions expander became a row button: the command flips the flag and the label
    // follows it, so the view binds both and holds no state of its own.
    [Fact]
    public void Rows_ToggleSessions_FlipsIsExpanded_AndTheLabelFollows()
    {
        var list = CreateList();
        list.Load(PageOf([Row(sessionCount: 2, sessions:
        [
            new SessionSummary(new DateOnly(2025, 12, 7), 40, 12_000d),
            new SessionSummary(new DateOnly(2025, 11, 19), 30, 9_000d),
        ])]));
        var row = Assert.Single(list.Rows);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(row.IsExpanded);
        var closedLabel = row.SessionsToggleText;

        row.ToggleSessionsCommand.Execute(null);

        Assert.True(row.IsExpanded);
        Assert.NotEqual(closedLabel, row.SessionsToggleText);
        Assert.Contains(nameof(TargetRowViewModel.SessionsToggleText), raised);

        row.ToggleSessionsCommand.Execute(null);

        Assert.False(row.IsExpanded);
        Assert.Equal(closedLabel, row.SessionsToggleText);
    }

    // A re-query keeps the rows the reader expanded, by group key. A failure reads
    // as M 31 collapsed after the second Load.
    [Fact]
    public void Load_KeepsAnExpandedRowExpanded_AndLeavesANewRowCollapsed()
    {
        var list = CreateList();
        list.Load(PageOf([Row("M 31"), Row("M 33")]));
        list.Rows.Single(row => row.Name == "M 31").ToggleSessionsCommand.Execute(null);

        list.Load(PageOf([Row("M 31"), Row("NGC 7000")]));

        Assert.True(list.Rows.Single(row => row.Name == "M 31").IsExpanded);
        Assert.False(list.Rows.Single(row => row.Name == "NGC 7000").IsExpanded);
    }
}
