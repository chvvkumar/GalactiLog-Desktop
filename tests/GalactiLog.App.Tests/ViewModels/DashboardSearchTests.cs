using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.2's Search row, at the view-model level: no window, no database, no dispatcher
// (design-spec 18.3). The ranking itself is asserted in GalactiLog.Data.Tests against a seeded
// library (coordinator ruling Q8); what these assert is that the dashboard surfaces that order
// unchanged, pins the right criterion on selection, and shares the filter panel's debounce window.
public class DashboardSearchTests
{
    private static readonly TargetSearchResult AndromedaResult = new(
        TargetId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        UnresolvedObject: null,
        DisplayName: "M 31",
        ObjectType: "G",
        MatchedOn: "Andromeda Galaxy",
        FrameCount: 0,
        Score: 0.83);

    private static readonly TargetSearchResult UnresolvedResult = new(
        TargetId: null,
        UnresolvedObject: "Sh2-155",
        DisplayName: "Sh2-155",
        ObjectType: null,
        MatchedOn: "Sh2-155",
        FrameCount: 3,
        Score: 1.0);

    // Records every term the dashboard hands the search query. Search runs on a thread pool
    // thread (the dashboard never queries on the UI thread), hence the lock.
    private sealed class RecordingSearchQuery
    {
        private readonly Lock _gate = new();
        private readonly List<string> _terms = [];

        public IReadOnlyList<TargetSearchResult> Results { get; set; } = [];

        public IReadOnlyList<string> Terms
        {
            get { lock (_gate) { return [.. _terms]; } }
        }

        public IReadOnlyList<TargetSearchResult> Search(string term)
        {
            lock (_gate) { _terms.Add(term); }
            return Results;
        }
    }

    private sealed class RecordingListQuery
    {
        private readonly Lock _gate = new();
        private readonly List<TargetListingCriteria> _calls = [];

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
            return new TargetListingPage([], 0, 0d, 0, criteria.Page, criteria.PageSize);
        }
    }

    private static DashboardViewModel CreateDashboard(
        RecordingListQuery list,
        RecordingSearchQuery search,
        FakeDelay delay)
    {
        var dashboard = new DashboardViewModel(
            list.List,
            () => new DashboardFacets([], [], []),
            () => [],
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()),
            new GeneralSettings(),
            delay.Delay,
            post: action => action(),
            search: search.Search);
        dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        return dashboard;
    }

    // Releases parked waits until the named in-flight task has resolved, the same shape
    // FilterPanelViewModelTests uses for the listing query.
    private static async Task DrainAsync(Func<Task?> pending)
    {
        var work = pending();
        if (work is null)
        {
            return;
        }

        await work;
    }

    private static async Task DrainAsync(DashboardViewModel dashboard, FakeDelay delay, Func<Task?> pending)
    {
        var work = pending();
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

        Assert.True(work.IsCompleted, "The dashboard's pending work never completed.");
        await work;
        Assert.Null(dashboard.LastQueryFailure);
    }

    private static async Task<DashboardViewModel> SearchAsync(
        RecordingListQuery list,
        RecordingSearchQuery search,
        FakeDelay delay,
        string term)
    {
        var dashboard = CreateDashboard(list, search, delay);
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);
        list.Clear();

        dashboard.Filters.SearchText = term;
        await DrainAsync(dashboard, delay, () => dashboard.PendingSearch);
        return dashboard;
    }

    [Fact]
    public async Task SelectingATargetResult_PinsTargetIdCriterion()
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [AndromedaResult] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "andromeda");

        dashboard.Filters.SelectSearchResultCommand.Execute(dashboard.Filters.SearchResults[0]);
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);

        Assert.Equal(AndromedaResult.TargetId, dashboard.Filters.PinnedTargetId);
        Assert.Equal(AndromedaResult.TargetId, list.Calls[^1].TargetId);
        Assert.Null(list.Calls[^1].UnresolvedObject);
        Assert.Equal("M 31", dashboard.Filters.PinnedLabel);
    }

    [Fact]
    public async Task SelectingAnUnresolvedResult_PinsTheObjectStringCriterion()
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [UnresolvedResult] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "sh2");

        dashboard.Filters.SelectSearchResultCommand.Execute(dashboard.Filters.SearchResults[0]);
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);

        Assert.Equal("Sh2-155", dashboard.Filters.PinnedUnresolvedObject);
        Assert.Equal("Sh2-155", list.Calls[^1].UnresolvedObject);
        Assert.Null(list.Calls[^1].TargetId);
    }

    [Fact]
    public async Task SelectingAResult_ClearsTheSearchTextAndClosesTheDropdown()
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [AndromedaResult] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "andromeda");
        Assert.True(dashboard.Filters.IsSearchDropdownOpen);

        dashboard.Filters.SelectSearchResultCommand.Execute(dashboard.Filters.SearchResults[0]);
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);

        Assert.Equal("", dashboard.Filters.SearchText);
        Assert.False(dashboard.Filters.IsSearchDropdownOpen);
        Assert.Empty(dashboard.Filters.SearchResults);
        // Clearing the text must not re-open a search window of its own.
        Assert.Equal(["andromeda"], search.Terms);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectingAResult_SetsExactlyOnePinnedProperty(bool resolved)
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [resolved ? AndromedaResult : UnresolvedResult] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "term");

        dashboard.Filters.SelectSearchResultCommand.Execute(dashboard.Filters.SearchResults[0]);
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);

        Assert.Equal(
            resolved,
            dashboard.Filters.PinnedTargetId is not null && dashboard.Filters.PinnedUnresolvedObject is null);
        Assert.Equal(
            !resolved,
            dashboard.Filters.PinnedTargetId is null && dashboard.Filters.PinnedUnresolvedObject is not null);
    }

    [Fact]
    public async Task ClearPinnedTarget_RemovesBothPinnedCriteria_AndRequeriesOnce()
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [AndromedaResult] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "andromeda");
        dashboard.Filters.SelectSearchResultCommand.Execute(dashboard.Filters.SearchResults[0]);
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);
        list.Clear();

        // Task 6 landed this command as ClearPin; it is the "clear the pinned target" gesture the
        // chip's x button already binds to, so Task 8 reuses it rather than adding a second.
        dashboard.Filters.ClearPinCommand.Execute(null);
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);

        Assert.Null(dashboard.Filters.PinnedTargetId);
        Assert.Null(dashboard.Filters.PinnedUnresolvedObject);
        Assert.Null(dashboard.Filters.PinnedLabel);
        Assert.Single(list.Calls);
        Assert.Null(list.Calls[0].TargetId);
        Assert.Null(list.Calls[0].UnresolvedObject);
    }

    [Fact]
    public async Task Search_IsDebouncedOnTheSameWindowAsTheFilters()
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [AndromedaResult] };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(list, search, delay);
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);

        dashboard.Filters.SearchText = "a";
        dashboard.Filters.SearchText = "an";
        dashboard.Filters.SearchText = "and";
        await DrainAsync(dashboard, delay, () => dashboard.PendingSearch);

        Assert.Equal(["and"], search.Terms);
        Assert.All(delay.Requested, requested => Assert.Equal(DashboardViewModel.DebounceWindow, requested));
    }

    [Fact]
    public async Task Search_SurfacesTheQueryOrderUnchanged()
    {
        var list = new RecordingListQuery();
        var second = AndromedaResult with { DisplayName = "M 32", Score = 0.5 };
        var search = new RecordingSearchQuery { Results = [AndromedaResult, second] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "m 3");

        Assert.Equal(["M 31", "M 32"], dashboard.Filters.SearchResults.Select(row => row.DisplayName));
    }

    [Fact]
    public async Task Search_EmptiedText_ClosesTheDropdownWithoutQuerying()
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [AndromedaResult] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "andromeda");

        dashboard.Filters.SearchText = "";
        await DrainAsync(() => dashboard.PendingSearch);

        Assert.False(dashboard.Filters.IsSearchDropdownOpen);
        Assert.Empty(dashboard.Filters.SearchResults);
        Assert.Equal(["andromeda"], search.Terms);
    }

    // Review item 3. SelectSearchResult clears SearchText inside its suspend block, so without a
    // SearchTextChanged of its own the window that keystroke opened stayed live: releasing its
    // debounce afterwards re-opened the dropdown over the pin the user had just made.
    [Fact]
    public async Task SelectingAResult_CancelsAPendingSearchWindow_SoTheDropdownStaysClosed()
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [AndromedaResult] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "andromeda");

        // A further keystroke opens a window that is still parked on the debounce...
        dashboard.Filters.SearchText = "andromeda g";
        var superseded = dashboard.PendingSearch;
        Assert.NotNull(superseded);
        dashboard.Filters.SelectSearchResultCommand.Execute(dashboard.Filters.SearchResults[0]);

        // ...and is then released. It must not publish. Phase review item 9: awaiting the window
        // that was superseded is the deterministic form of this; the sleep it replaces asserted a
        // negative against a wall clock.
        await DrainAsync(dashboard, delay, () => dashboard.PendingQuery);
        delay.Release();
        await superseded!;

        Assert.False(dashboard.Filters.IsSearchDropdownOpen);
        Assert.Empty(dashboard.Filters.SearchResults);
        Assert.Equal(AndromedaResult.TargetId, dashboard.Filters.PinnedTargetId);
        Assert.Equal(["andromeda"], search.Terms);
    }

    // Review E1.
    [Fact]
    public async Task Escape_ClosesTheDropdown_WithoutClearingTheTextOrThePin()
    {
        var list = new RecordingListQuery();
        var search = new RecordingSearchQuery { Results = [AndromedaResult] };
        var delay = new FakeDelay();
        var dashboard = await SearchAsync(list, search, delay, "andromeda");
        Assert.True(dashboard.Filters.IsSearchDropdownOpen);

        dashboard.Filters.CloseSearchDropdownCommand.Execute(null);

        Assert.False(dashboard.Filters.IsSearchDropdownOpen);
        Assert.Equal("andromeda", dashboard.Filters.SearchText);
        Assert.Null(dashboard.Filters.PinnedTargetId);
        // Dismissing the dropdown pins nothing, so it costs no listing query.
        Assert.Empty(list.Calls);
    }

    [Fact]
    public void SearchResultRow_ShowsNameObjectTypeAndMatchedAlias()
    {
        var row = new SearchResultViewModel(AndromedaResult);

        Assert.Equal("M 31", row.DisplayName);
        Assert.Equal("Galaxy", row.CategoryText);
        Assert.Equal("matched Andromeda Galaxy", row.MatchedText);
        Assert.True(row.HasMatchedText);
        Assert.False(row.HasFrameCount);

        // A score that came from the name already on the row does not repeat it.
        var byName = new SearchResultViewModel(AndromedaResult with { MatchedOn = "M 31" });
        Assert.Equal("", byName.MatchedText);
        Assert.False(byName.HasMatchedText);
    }

    [Fact]
    public void SearchResultRow_UnresolvedRow_ShowsTheFrameCount()
    {
        var row = new SearchResultViewModel(UnresolvedResult);

        Assert.True(row.IsUnresolved);
        Assert.True(row.HasFrameCount);
        Assert.Equal("3 frames", row.FrameCountText);
        Assert.Equal(TargetListingCriteria.UnresolvedCategory, row.CategoryText);
        Assert.Equal("1 frame", new SearchResultViewModel(UnresolvedResult with { FrameCount = 1 }).FrameCountText);
    }
}
