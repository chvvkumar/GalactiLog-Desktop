using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14C Task 4, spec 12.2's Pager paragraph and spec 5.8.1's default_page_size paragraph.
// Plain xunit facts, no window, no dispatcher. A new file rather than an append to
// TargetListViewModelTests.cs (task4.md section 9.2): that file is this task's own "must pass
// untouched" surface except the two lines the seeding change forces, and its 38-case figure
// stays quotable this way.
public class TargetListPagerTests
{
    // ---- construction helpers, the same shape TargetListViewModelTests.cs uses --------------

    private sealed class RecordingQuery
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

    private static TargetListViewModel CreateList(int pageSize = 50, Action<int>? writeDefaultPageSize = null)
        => new(
            new DisplaySettings(),
            () => new DisplaySettings(),
            _ => { },
            pageSize,
            writeDefaultPageSize: writeDefaultPageSize);

    private static DashboardViewModel CreateDashboard(RecordingQuery query, FakeDelay delay)
        => new(
            query.List,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            delay.Delay,
            post: action => action());

    private static async Task DrainAsync(DashboardViewModel dashboard, FakeDelay delay)
    {
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

    // PageSize is set to 1, so TotalGroups alone gives the exact page count: ceil(n / 1) = n.
    // Page is then written directly, bypassing GoToPage's own gate, because the button set has
    // to render every current page this suite names, not only the ones GoToPage would reach in
    // one call.
    private static void Seed(TargetListViewModel list, int pageCount, int page)
    {
        list.PageSize = 1;
        list.Load(new TargetListingPage([], pageCount, 0d, 0, 1, 1));
        list.Page = page;
    }

    // ---- the page button set (spec 12.2's Pager paragraph, the web's pageRange) --------------

    [Theory]
    [InlineData(1, 1, "1")]
    [InlineData(5, 3, "1 2 3 4 5")]
    [InlineData(7, 7, "1 2 3 4 5 6 7")]
    [InlineData(40, 20, "1 ... 19 20 21 ... 40")]
    [InlineData(40, 1, "1 2 3 ... 40")]
    [InlineData(40, 40, "1 ... 38 39 40")]
    public void PageButtons_FollowTheWebsRule(int pageCount, int current, string expected)
    {
        var list = CreateList();
        Seed(list, pageCount, current);

        var rendered = string.Join(" ", list.PageButtons.Select(button => button.Page?.ToString() ?? "..."));

        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void PageButtons_AtOnePage_AreJustPageOne_WithBothArrowsDisabled()
    {
        var list = CreateList();
        Seed(list, pageCount: 1, page: 1);

        Assert.Equal(new[] { new PageButtonViewModel(1, true) }, list.PageButtons);
        Assert.False(list.HasPreviousPage);
        Assert.False(list.HasNextPage);
        Assert.False(list.NextPageCommand.CanExecute(null));
        Assert.False(list.PreviousPageCommand.CanExecute(null));
    }

    [Fact]
    public void PageButtons_AtFivePages_ShowEveryNumber_AndNoEllipsis()
    {
        var list = CreateList();
        Seed(list, pageCount: 5, page: 3);

        Assert.DoesNotContain(list.PageButtons, button => button.Page is null);
        Assert.Equal(5, list.PageButtons.Count);
    }

    [Fact]
    public void PageButtons_AtFortyPagesOnPageTwenty_ShowOneEllipsisNineteenTwentyTwentyOneEllipsisForty()
    {
        var list = CreateList();
        Seed(list, pageCount: 40, page: 20);

        Assert.Equal(
            new int?[] { 1, null, 19, 20, 21, null, 40 },
            list.PageButtons.Select(button => button.Page));
        Assert.True(list.PageButtons.Single(button => button.Page == 20).IsCurrent);
    }

    [Fact]
    public void PageButtons_AtFortyPagesOnPageOne_ShowNoLeadingEllipsis()
    {
        var list = CreateList();
        Seed(list, pageCount: 40, page: 1);

        // task4.md section 6's first boundary case: 1 2 3 ... 40, not 1 ... 2 3 ... 40. The
        // naive per-endpoint clamp gets this wrong; the entry right after page 1 is a real page.
        Assert.NotNull(list.PageButtons[1].Page);
        Assert.DoesNotContain(list.PageButtons.Take(3), button => button.Page is null);
    }

    [Fact]
    public void PageButtons_AtFortyPagesOnTheLastPage_ShowNoTrailingEllipsis()
    {
        var list = CreateList();
        Seed(list, pageCount: 40, page: 40);

        // The mirror boundary case: 1 ... 38 39 40, with no ellipsis between 38 and the last page.
        Assert.NotNull(list.PageButtons[^2].Page);
        Assert.DoesNotContain(list.PageButtons.TakeLast(3), button => button.Page is null);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(5, 5)]
    [InlineData(40, 1)]
    [InlineData(40, 20)]
    [InlineData(40, 40)]
    public void PageButtons_MarkExactlyOneCurrent(int pageCount, int page)
    {
        var list = CreateList();
        Seed(list, pageCount, page);

        // Over the whole produced list, not the entry at Page - 1: indexing into the list
        // assumes the ellipsis positions, which is the thing under test.
        Assert.Single(list.PageButtons, button => button.IsCurrent);
    }

    // ---- GoToPage (TRACKING item 13: the gate is repeated in the body) -----------------------

    [Fact]
    public async Task GoToPage_MovesThePage_AndQueriesOnce()
    {
        var query = new RecordingQuery { Page = new TargetListingPage([], 120, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);
        query.Clear();

        dashboard.Targets.GoToPageCommand.Execute(3);
        await DrainAsync(dashboard, delay);

        Assert.Equal(3, Assert.Single(query.Calls).Page);
    }

    [Fact]
    public void GoToPage_OnTheCurrentPage_DoesNothing_EvenWhenExecutedDirectly()
    {
        var list = CreateList();
        list.Load(new TargetListingPage([], 120, 0d, 0, 1, 50));
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.GoToPageCommand.Execute(1);

        Assert.Equal(1, list.Page);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void GoToPage_OnAnEllipsis_DoesNothing_EvenWhenExecutedDirectly()
    {
        var list = CreateList();
        list.Load(new TargetListingPage([], 120, 0d, 0, 1, 50));
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.GoToPageCommand.Execute(null);

        Assert.Equal(1, list.Page);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void GoToPage_OutsideTheRange_DoesNothing_EvenWhenExecutedDirectly()
    {
        var list = CreateList();
        list.Load(new TargetListingPage([], 120, 0d, 0, 1, 50)); // PageCount 3
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.GoToPageCommand.Execute(0);
        list.GoToPageCommand.Execute(4);
        list.GoToPageCommand.Execute(-1);

        Assert.Equal(1, list.Page);
        Assert.Equal(0, changed);
    }

    // Coordinator ruling 3: CanExecute is the affordance, not the guard, so a direct Execute
    // that reaches past it while a refetch is already in flight must still do nothing.
    [Fact]
    public void GoToPage_WhileRefetching_DoesNothing_EvenWhenExecutedDirectly()
    {
        var list = CreateList();
        list.Load(new TargetListingPage([], 120, 0d, 0, 1, 50));
        list.SetRefetching(true);
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.GoToPageCommand.Execute(2);

        Assert.Equal(1, list.Page);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void NextPage_WhileRefetching_DoesNothing_EvenWhenExecutedDirectly()
    {
        var list = CreateList();
        list.Load(new TargetListingPage([], 120, 0d, 0, 1, 50));
        list.SetRefetching(true);
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.NextPageCommand.Execute(null);

        Assert.Equal(1, list.Page);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void PreviousPage_WhileRefetching_DoesNothing_EvenWhenExecutedDirectly()
    {
        var list = CreateList();
        list.Load(new TargetListingPage([], 120, 0d, 0, 1, 50));
        list.NextPageCommand.Execute(null);
        Assert.Equal(2, list.Page);

        list.SetRefetching(true);
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.PreviousPageCommand.Execute(null);

        Assert.Equal(2, list.Page);
        Assert.Equal(0, changed);
    }

    // ---- the page-size select (spec 5.8.1) ----------------------------------------------------

    [Fact]
    public void PageSizeOptions_AreTheSpecsFour()
    {
        Assert.Equal([25, 50, 100, 250], TargetListViewModel.PageSizeOptions);
    }

    [Fact]
    public void SelectedPageSize_WritesTheGeneralKeyOnce()
    {
        var writes = new List<int>();
        var list = CreateList(writeDefaultPageSize: writes.Add);

        list.SelectedPageSize = 100;

        Assert.Equal([100], writes);
    }

    [Fact]
    public async Task SelectedPageSize_ResetsToPageOne_AndQueriesOnce()
    {
        var query = new RecordingQuery { Page = new TargetListingPage([], 500, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);
        dashboard.Targets.NextPageCommand.Execute(null);
        await DrainAsync(dashboard, delay);
        Assert.Equal(2, dashboard.Targets.Page);
        query.Clear();

        dashboard.Targets.SelectedPageSize = 25;
        await DrainAsync(dashboard, delay);

        Assert.Equal(1, dashboard.Targets.Page);
        Assert.Equal(1, Assert.Single(query.Calls).Page);
    }

    [Fact]
    public void AStoredPageSizeOutsideTheList_ReadsAsFifty()
    {
        var list = CreateList(pageSize: 37);

        Assert.Equal(50, list.SelectedPageSize);
    }

    [Fact]
    public void AStoredPageSizeInsideTheList_ReadsAsItself()
    {
        var list = CreateList(pageSize: 100);

        Assert.Equal(100, list.SelectedPageSize);
    }

    [Fact]
    public void TheWriteEchoingBackThroughGeneralChanged_ChangesNothingASecondTime()
    {
        TargetListViewModel? list = null;
        var writes = 0;

        // Reproduces AppHost's own wiring: the delegate writes the document and the same
        // subscription AppHost.FollowDefaultPageSize is echoes it straight back.
        list = CreateList(writeDefaultPageSize: size =>
        {
            writes++;
            AppHost.FollowDefaultPageSize(list!, size, action => action());
        });

        list.SelectedPageSize = 100;

        Assert.Equal(1, writes);
        Assert.Equal(100, list.PageSize);
    }

    [Fact]
    public void AListBuiltWithNoWriteDelegate_ChangesThePageSizeAndStoresNothing()
    {
        var list = CreateList();

        list.SelectedPageSize = 100;

        Assert.Equal(100, list.PageSize);
        Assert.Equal(100, list.SelectedPageSize);
    }
}
