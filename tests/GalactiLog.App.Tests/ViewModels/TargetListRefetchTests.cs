using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14C Task 4, spec 12.2's "The refetch dim" paragraph. DashboardViewModel.RunQueryAsync
// already keeps the old page on screen for the whole in-flight query (Load only runs once the
// new page lands); what this task adds is the signal, IsRefetching, and this file is its truth
// table. A new file rather than an append (task4.md section 9.2), so it does not collide with
// Task 3's own TargetListViewModelTests.cs edits.
public class TargetListRefetchTests
{
    private sealed class RecordingQuery
    {
        public TargetListingPage Page { get; set; } = new([], 0, 0d, 0, 1, 50);

        public TargetListingPage List(TargetListingCriteria criteria) => Page;
    }

    // A query that blocks inside Task.Run until the test releases it, so IsRefetching can be
    // observed while a query is genuinely still running rather than inferred from timing alone.
    // Open by default, so the dashboard's own first query at construction never parks on it.
    private sealed class GatedQuery
    {
        private readonly ManualResetEventSlim _gate = new(true);
        private readonly Lock _countGate = new();
        private int _calls;

        public TargetListingPage Page { get; set; } = new([], 0, 0d, 0, 1, 50);

        public Exception? Throw { get; set; }

        public int Calls
        {
            get { lock (_countGate) { return _calls; } }
        }

        public void Block() => _gate.Reset();

        public void Release() => _gate.Set();

        public TargetListingPage List(TargetListingCriteria criteria)
        {
            lock (_countGate) { _calls++; }
            _gate.Wait(TimeSpan.FromSeconds(10));
            if (Throw is { } ex)
            {
                throw ex;
            }

            return Page;
        }
    }

    private static TargetRow Row(string name) => new(
        GroupKey: $"obj:{name}",
        TargetId: null,
        Name: name,
        CommonName: null,
        CatalogId: name,
        ObjectType: "G",
        ObjectCategory: "Galaxy",
        IntegrationSeconds: 1_000d,
        FrameCount: 10,
        SessionCount: 1,
        FirstSession: new DateOnly(2025, 1, 1),
        LastSession: new DateOnly(2025, 1, 1),
        Palette: [],
        Equipment: [],
        Aliases: [],
        Sessions: []);

    private static DashboardViewModel CreateDashboard(
        Func<TargetListingCriteria, TargetListingPage> list,
        FakeDelay delay,
        Action<Action>? post = null,
        ScanStatusService? scanStatus = null,
        TimeSpan? refetchDimDelay = null)
        => CreateDashboard(list, delay.Delay, post, scanStatus, refetchDimDelay);

    // A zero dim delay by default, so the truth table below dims the moment a query starts
    // rather than racing the grace wait against the query; the grace has its own cases.
    private static DashboardViewModel CreateDashboard(
        Func<TargetListingCriteria, TargetListingPage> list,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<Action>? post = null,
        ScanStatusService? scanStatus = null,
        TimeSpan? refetchDimDelay = null)
        => new(
            list,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            delay,
            scanStatus: scanStatus,
            post: post ?? (action => action()),
            refetchDimDelay: refetchDimDelay ?? TimeSpan.Zero);

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
    }

    // For the two tests below, which intercept the whole post seam (the same technique
    // FilterPanelViewModelTests.StaleResponse_IsDiscarded_WhenANewerQueryHasAlreadyBeenApplied
    // uses) so the two closures a completed query posts, SetRefetching(true) and Apply, can be
    // invoked one at a time in a chosen order rather than inline. Settles construction's own
    // first query the same way DrainAsync does everywhere else in this file.
    private static async Task SettleAsync(DashboardViewModel dashboard, FakeDelay delay, List<Action> posted)
    {
        for (var round = 0; round < 10; round++)
        {
            await DrainAsync(dashboard, delay);
            if (posted.Count == 0)
            {
                return;
            }

            var pending = posted.ToArray();
            posted.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }

        Assert.Empty(posted);
    }

    // Spins the debounce alone, never the query: used while a GatedQuery is deliberately still
    // parked, so the shared DrainAsync (which insists the whole query complete) cannot be used.
    private static async Task PumpDebounceUntilAsync(FakeDelay delay, Func<bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!until() && DateTime.UtcNow < deadline)
        {
            delay.Release();
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task IsRefetching_IsFalseAtRest()
    {
        var query = new RecordingQuery();
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);

        Assert.False(dashboard.Targets.IsRefetching);
    }

    [Fact]
    public async Task IsRefetching_IsTrueWhileTheQueryIsInFlight()
    {
        var query = new GatedQuery { Page = new TargetListingPage([Row("M 31")], 1, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);

        query.Block();
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await PumpDebounceUntilAsync(delay, () => dashboard.Targets.IsRefetching);

        Assert.True(dashboard.Targets.IsRefetching);

        query.Release();
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);
    }

    [Fact]
    public async Task IsRefetching_IsFalseAfterThePageLands()
    {
        var query = new RecordingQuery { Page = new TargetListingPage([], 3, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);

        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await DrainAsync(dashboard, delay);

        Assert.False(dashboard.Targets.IsRefetching);
    }

    [Fact]
    public async Task IsRefetching_IsFalseAfterASupersededGenerationIsDropped()
    {
        var query = new GatedQuery { Page = new TargetListingPage([], 3, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);

        // Generation 1 reaches the query and parks there (the gate is still closed). Generation
        // 2 then supersedes it: its own token cancels generation 1's, but generation 1's
        // Task.Run has already started and does not observe the token, so it keeps running and
        // returns quietly once the gate opens. It must not leave the dim stuck on, and it must
        // not be the one that clears it either: generation 2 owns that.
        query.Block();
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await PumpDebounceUntilAsync(delay, () => query.Calls >= 1);
        Assert.Equal(1, query.Calls);

        dashboard.Filters.SelectedCamera = "ASI294MM";
        query.Release();
        await DrainAsync(dashboard, delay);

        Assert.False(dashboard.Targets.IsRefetching);
    }

    // The coordinator's own follow-up on the case above: the generation-guard half of the truth
    // table needs its own two-sided proof, with the queries' own Apply closures captured rather
    // than run inline, so the exact order production's dispatcher races can produce is under this
    // test's control rather than the scheduler's. A stale Apply landing while a still newer one
    // is outstanding must not clear the dim that newer one owns; the newer one landing after it
    // is the one that clears it.
    [Fact]
    public async Task IsRefetching_TheStaleGenerationGuard_DoesNotClearWhileNewerIsOutstanding_AndClearsOnceItLands()
    {
        var query = new RecordingQuery { Page = new TargetListingPage([], 3, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var posted = new List<Action>();
        var dashboard = CreateDashboard(query.List, delay, posted.Add);
        await SettleAsync(dashboard, delay, posted);
        Assert.False(dashboard.Targets.IsRefetching);

        // Generation N completes and posts SetRefetching(true) then Apply; neither has run yet.
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await DrainAsync(dashboard, delay);
        Assert.Equal(2, posted.Count);
        var setTrueN = posted[0];
        var applyN = posted[1];
        posted.Clear();

        // Generation N + 1 supersedes it before either of N's own closures ever ran.
        dashboard.Filters.SelectedCamera = "ASI294MM";
        await DrainAsync(dashboard, delay);
        Assert.Equal(2, posted.Count);
        var setTrueNPlus1 = posted[0];
        var applyNPlus1 = posted[1];

        setTrueN();
        setTrueNPlus1();
        Assert.True(dashboard.Targets.IsRefetching);

        // The stale response lands while the newer one is still outstanding: its own Apply has
        // not run yet. Apply's generation guard must no-op silently here, and in particular must
        // not clear the dim the still-outstanding newer query owns.
        applyN();
        Assert.True(dashboard.Targets.IsRefetching, "The stale response cleared a dim the newer query still owns.");

        // Once the truly current generation lands, it is the one that clears it, and nothing is
        // left in flight afterwards.
        applyNPlus1();
        Assert.False(dashboard.Targets.IsRefetching);
    }

    [Fact]
    public async Task IsRefetching_IsFalseAfterAFailedQuery()
    {
        var query = new GatedQuery { Page = new TargetListingPage([], 3, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);

        query.Throw = new InvalidOperationException("the query is refused");
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await DrainAsync(dashboard, delay);

        Assert.NotNull(dashboard.LastQueryFailure);
        Assert.False(dashboard.Targets.IsRefetching);
    }

    [Fact]
    public async Task TheRows_AreNotClearedWhileTheQueryIsInFlight()
    {
        var query = new GatedQuery
        {
            Page = new TargetListingPage([Row("M 31"), Row("M 42")], 2, 0d, 0, 1, 50),
        };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);
        Assert.Equal(2, dashboard.Targets.Rows.Count);

        query.Block();
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await PumpDebounceUntilAsync(delay, () => dashboard.Targets.IsRefetching);
        Assert.True(dashboard.Targets.IsRefetching);

        // Nothing cleared in the interval: the count, not an empty list.
        Assert.Equal(2, dashboard.Targets.Rows.Count);

        query.Release();
        await DrainAsync(dashboard, delay);
    }

    [Fact]
    public async Task TheRowInstances_AreTheSameObjectsUntilThePageLands()
    {
        var query = new GatedQuery
        {
            Page = new TargetListingPage([Row("M 31"), Row("M 42")], 2, 0d, 0, 1, 50),
        };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);
        var before = dashboard.Targets.Rows.ToList();

        // The response already parked behind the gate is a different page: a row count that
        // stays put while every element is quietly swapped is exactly the "nothing replaced"
        // claim failing without this being stronger than the row-count case above.
        query.Block();
        query.Page = new TargetListingPage([Row("M 31"), Row("NGC 6960")], 2, 0d, 0, 1, 50);
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await PumpDebounceUntilAsync(delay, () => dashboard.Targets.IsRefetching);
        Assert.True(dashboard.Targets.IsRefetching);

        Assert.Same(before[0], dashboard.Targets.Rows[0]);
        Assert.Same(before[1], dashboard.Targets.Rows[1]);

        query.Release();
        await DrainAsync(dashboard, delay);

        Assert.NotSame(before[1], dashboard.Targets.Rows[1]);
        Assert.Equal("NGC 6960", dashboard.Targets.Rows[1].Name);
    }

    [Fact]
    public async Task SetRefetching_WritesOnTheCapturedContext()
    {
        var query = new RecordingQuery { Page = new TargetListingPage([], 3, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var insidePost = false;
        var wroteOutsidePost = false;

        var dashboard = new DashboardViewModel(
            query.List,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            delay.Delay,
            post: action =>
            {
                insidePost = true;
                try
                {
                    action();
                }
                finally
                {
                    insidePost = false;
                }
            });

        dashboard.Targets.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TargetListViewModel.IsRefetching) && !insidePost)
            {
                wroteOutsidePost = true;
            }
        };

        await DrainAsync(dashboard, delay);
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await DrainAsync(dashboard, delay);

        Assert.False(wroteOutsidePost);
    }

    // Review finding (fix pass, task4-review.md P3): the report's truth table named the filter
    // path only. A page or page-size change reaches RunQueryAsync the same way, through
    // OnTargetsChanged, so it takes the same dim.
    [Fact]
    public async Task IsRefetching_CyclesForAPageChange_NotOnlyAFilterChange()
    {
        var query = new GatedQuery { Page = new TargetListingPage([], 100, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);
        Assert.True(dashboard.Targets.HasNextPage);

        query.Block();
        dashboard.Targets.NextPageCommand.Execute(null);
        await PumpDebounceUntilAsync(delay, () => dashboard.Targets.IsRefetching);
        Assert.True(dashboard.Targets.IsRefetching);

        query.Release();
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);
    }

    // A scan landing re-reads the page under criteria the reader did not change, so it is a quiet
    // refresh: the rows stay lit and clickable. A real ScanCoordinator over an empty temp root,
    // the same shape TargetListViewModelTests.ScanFinished_RefreshesTheList already uses, rather
    // than a fake event source: what matters is that the real event reaches the query.
    [Fact]
    public async Task IsRefetching_StaysFalseWhileAScanRefreshRuns()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var status = new ScanStatusService(coordinator, action => action());

        var query = new GatedQuery { Page = new TargetListingPage([], 0, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay, scanStatus: status);
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);

        query.Block();
        var calls = query.Calls;
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        await PumpDebounceUntilAsync(delay, () => query.Calls > calls);
        Assert.True(query.Calls > calls, "The scan never reached the listing query.");
        Assert.False(dashboard.Targets.IsRefetching);

        query.Release();
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);
    }

    [Fact]
    public async Task IsRefetching_StaysFalseForAQuietRequest()
    {
        var query = new GatedQuery { Page = new TargetListingPage([Row("M 31")], 1, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);

        query.Block();
        var calls = query.Calls;
        dashboard.RequestQuery(quiet: true);
        await PumpDebounceUntilAsync(delay, () => query.Calls > calls);

        Assert.True(query.Calls > calls, "The quiet request never reached the listing query.");
        Assert.False(dashboard.Targets.IsRefetching);

        query.Release();
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);
    }

    // A quiet refresh landing inside a filter change's debounce window coalesces into it, and
    // must not take the dim away from the change the reader is waiting on.
    [Fact]
    public async Task IsRefetching_AQuietRequestCoalescedIntoAFilterChange_KeepsTheDim()
    {
        var query = new GatedQuery { Page = new TargetListingPage([Row("M 31")], 1, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay);
        await DrainAsync(dashboard, delay);

        query.Block();
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        dashboard.RequestQuery(quiet: true);
        await PumpDebounceUntilAsync(delay, () => dashboard.Targets.IsRefetching);
        Assert.True(dashboard.Targets.IsRefetching);

        query.Release();
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);

        // The debt is paid once that window lands: a later quiet request does not dim.
        query.Block();
        var calls = query.Calls;
        dashboard.RequestQuery(quiet: true);
        await PumpDebounceUntilAsync(delay, () => query.Calls > calls);
        Assert.False(dashboard.Targets.IsRefetching);

        query.Release();
        await DrainAsync(dashboard, delay);
    }

    // The grace: a query that answers inside RefetchDimDelay never dims. The delay seam completes
    // the debounce at once and never completes the grace wait, so the query always wins the race.
    [Fact]
    public async Task IsRefetching_NeverTurnsOn_WhenTheQueryLandsInsideTheGrace()
    {
        var query = new RecordingQuery { Page = new TargetListingPage([Row("M 31")], 1, 0d, 0, 1, 50) };
        Task Delay(TimeSpan duration, CancellationToken token) => duration == DashboardViewModel.DebounceWindow
            ? Task.CompletedTask
            : Task.Delay(Timeout.Infinite, token);
        var dashboard = CreateDashboard(query.List, Delay, refetchDimDelay: DashboardViewModel.RefetchDimDelay);
        if (dashboard.Filters.PendingReload is { } reload)
        {
            await reload;
        }

        await dashboard.PendingQuery!;

        var dimmed = false;
        dashboard.Targets.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TargetListViewModel.IsRefetching) && dashboard.Targets.IsRefetching)
            {
                dimmed = true;
            }
        };

        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await dashboard.PendingQuery!;

        Assert.False(dimmed);
        Assert.False(dashboard.Targets.IsRefetching);
    }

    [Fact]
    public async Task IsRefetching_TurnsOn_OnceTheGraceElapsesWithTheQueryStillRunning()
    {
        var query = new GatedQuery { Page = new TargetListingPage([Row("M 31")], 1, 0d, 0, 1, 50) };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query.List, delay, refetchDimDelay: DashboardViewModel.RefetchDimDelay);
        await DrainAsync(dashboard, delay);

        query.Block();
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await PumpDebounceUntilAsync(delay, () => dashboard.Targets.IsRefetching);

        Assert.True(dashboard.Targets.IsRefetching);
        Assert.Contains(DashboardViewModel.RefetchDimDelay, delay.Requested);

        query.Release();
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.Targets.IsRefetching);
    }
}
