using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.Core.Scanning;
using GalactiLog.Data.Ingest;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// FIXER LIST F6. DashboardViewModel is a DI singleton, so the host owns its lifetime: a debounce
// window or a scan-root probe opened just before shutdown must not outlive the host that owns the
// database it queries. Plain xunit facts: no window, no dispatcher, no database (design-spec 18.3).
public class DashboardLifetimeTests
{
    private static DashboardViewModel Create(
        FakeDelay delay,
        ScanStatusService? scanStatus = null,
        Func<IReadOnlyList<string>>? probeRoots = null)
        => new(
            _ => DashboardViewModelTestFactory.EmptyPage,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            delay.Delay,
            scanStatus: scanStatus,
            post: action => action());

    [Fact]
    public async Task Dispose_DuringAPendingDebounce_LeavesNoRunningTask()
    {
        var delay = new FakeDelay();
        var dashboard = Create(delay);
        await dashboard.Filters.PendingReload!;

        // The constructor's first query is parked on the debounce, and nothing will ever release
        // it: disposal is the only thing that can finish this task.
        var pending = dashboard.PendingQuery;
        Assert.NotNull(pending);
        Assert.False(pending!.IsCompleted);

        dashboard.Dispose();

        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pending.IsCompleted);
        Assert.Null(dashboard.LastQueryFailure);
    }

    // F6 amendment: disposal drops BOTH subscriptions the dashboard holds on the status service.
    // ScanFinished would otherwise re-query and re-probe a page whose host is gone; PropertyChanged
    // would poke the Run Scan command. The real scan over an empty root is the same shape
    // ScanStatusServiceTests already uses to get a genuine ScanFinished.
    [Fact]
    public async Task Dispose_UnsubscribesFromScanFinishedAndPropertyChanged()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, post: action => action());

        var delay = new FakeDelay();
        var dashboard = Create(delay, scanStatus);
        await dashboard.Filters.PendingReload!;

        dashboard.Dispose();
        var queryAfterDispose = dashboard.PendingQuery;
        var probeAfterDispose = dashboard.PendingRootProbe;
        var reloadAfterDispose = dashboard.Filters.PendingReload;

        coordinator.RaiseProgress(ScanTaskNames.Classify, 1, 2, "after dispose", force: true);
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Same(queryAfterDispose, dashboard.PendingQuery);
        Assert.Same(probeAfterDispose, dashboard.PendingRootProbe);
        Assert.Same(reloadAfterDispose, dashboard.Filters.PendingReload);
    }

    // Phase review item 3. TargetListViewModel.Load clamps the page when the filtered set shrank
    // under the page the user is standing on, and that clamp raises Changed, which opens a newer
    // query window. Everything Apply writes after Load therefore belongs to a superseded request.
    [Fact]
    public async Task Apply_WhenLoadClampsThePage_StopsWritingForTheSupersededRequest()
    {
        var delay = new FakeDelay();
        var calls = 0;
        TargetListingPage Next(TargetListingCriteria _)
            => Interlocked.Increment(ref calls) == 1
                ? new TargetListingPage([], TotalGroups: 3, 0d, 0, Page: 1, PageSize: 50)
                : new TargetListingPage([], TotalGroups: 0, 0d, 0, Page: 4, PageSize: 50);

        var dashboard = new DashboardViewModel(
            Next,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            delay.Delay,
            post: action => action());
        await dashboard.Filters.PendingReload!;
        await delay.DrainAsync(dashboard.PendingQuery);

        // Standing on page 4 of a set that now holds a single page: Load clamps to 1 and the clamp
        // raises Changed, so a newer window is open by the time Apply gets past it.
        dashboard.Targets.Page = 4;
        await delay.DrainAsync(dashboard.PendingQuery);

        Assert.Equal(1, dashboard.Targets.Page);

        // The empty response for page 4 is superseded by the query the clamp opened, so the state
        // Apply writes after Load is still the first response's, not "No frames catalogued yet".
        Assert.Equal(DashboardContentState.Rows, dashboard.ContentState);

        dashboard.Dispose();
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var delay = new FakeDelay();
        var dashboard = Create(delay);
        await dashboard.Filters.PendingReload!;

        dashboard.Dispose();
        dashboard.Dispose();

        // A request after disposal opens no new window rather than throwing on a disposed source.
        dashboard.RequestQuery();
        Assert.True(dashboard.PendingQuery!.IsCompleted);
    }
}
