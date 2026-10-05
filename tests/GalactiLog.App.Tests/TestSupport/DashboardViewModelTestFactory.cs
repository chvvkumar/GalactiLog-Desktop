using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 5 Task 6: DashboardViewModel stopped being parameterless. The one place tests that only
// need *a* dashboard (the shell navigation and MainWindowViewModel suites) build one, so a later
// constructor change is one edit rather than five. Tests that assert dashboard behaviour build
// their own with the delegates they want to observe.
internal static class DashboardViewModelTestFactory
{
    public static readonly TargetListingPage EmptyPage = new([], 0, 0d, 0, 1, 50);

    public static AliasMap EmptyAliasMap() => new(new Dictionary<string, FilterSetting>(), new EquipmentSettings());

    public static DashboardFacets EmptyFacets() => new([], [], []);

    // No database, no window, no dispatcher and no real debounce: the delay completes
    // immediately, the post seam runs its closure inline, and the listing delegate returns a
    // fixed empty page. The background tasks (the filter panel's option-list reload, the first
    // query, the scan-root probe) are all awaited here so the returned view-model is settled.
    //
    // Task 8 added the three optional delegates. They default to what the previous signature
    // implied -- an empty page, no unreachable roots, no scan trigger -- so every existing call
    // site is unchanged.
    public static DashboardViewModel Create(
        Func<TargetListingCriteria, TargetListingPage>? list = null,
        Func<IReadOnlyList<string>>? probeRoots = null,
        Func<CancellationToken, Task>? startScan = null,
        ScanStatusService? scanStatus = null)
    {
        var dashboard = new DashboardViewModel(
            list ?? (_ => EmptyPage),
            EmptyFacets,
            () => [],
            EmptyAliasMap,
            new GeneralSettings(),
            (_, _) => Task.CompletedTask,
            scanStatus: scanStatus,
            post: action => action(),
            probeRoots: probeRoots,
            startScan: startScan);
        dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        Settle(dashboard);
        return dashboard;
    }

    /// <summary>Waits out every in-flight window: the listing query, the search, the root probe
    /// and the filter panel's option-list reload. Nothing here needs a dispatcher: the post seam
    /// above runs its closure inline. F6: a test that leaves one of these running leaves a task
    /// alive past the view-model it belongs to.</summary>
    public static void Settle(DashboardViewModel dashboard) =>
        dashboard.Quiesce(TimeSpan.FromSeconds(30));
}
