using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.10: the two dashboard empty states and the unreachable-root warning banner, at the
// view-model level with no window and no database (design-spec 18.3). The reachability probe is
// an injected delegate, so nothing here creates, touches or deletes a directory.
public class DashboardEmptyStateTests
{
    private static readonly TargetRow SampleRow = new(
        GroupKey: "10000000-0000-0000-0000-000000000000",
        TargetId: Guid.Parse("10000000-0000-0000-0000-000000000000"),
        Name: "M 31",
        CommonName: "Andromeda Galaxy",
        CatalogId: "M 31",
        ObjectType: "G",
        ObjectCategory: "Galaxy",
        IntegrationSeconds: 44_640d,
        FrameCount: 148,
        SessionCount: 2,
        FirstSession: new DateOnly(2024, 1, 5),
        LastSession: new DateOnly(2025, 12, 7),
        Palette: [],
        Equipment: ["RC8 / ASI2600MM"],
        Aliases: [],
        Sessions: []);

    private sealed class Harness : IDisposable
    {
        private readonly FakeDelay _delay = new();

        public Harness(
            bool withRows = false,
            IReadOnlyList<string>? unreachableRoots = null,
            Func<CancellationToken, Task>? startScan = null,
            bool withScanStatus = false,
            Action<bool>? recordProbeThread = null,
            Func<IReadOnlyList<string>>? probeOverride = null)
        {
            if (withScanStatus)
            {
                Coordinator = ScanCoordinatorTestFactory.CreateBare();
                Status = new ScanStatusService(Coordinator, action => action());
            }

            // Assigned before the view-model exists: its constructor starts the probe on a
            // background thread, which reads this.
            UnreachableRoots = unreachableRoots ?? [];

            Dashboard = new DashboardViewModel(
                criteria =>
                {
                    LastCriteria = criteria;
                    return withRows
                        ? new TargetListingPage([SampleRow], 1, 44_640d, 148, 1, criteria.PageSize)
                        : new TargetListingPage([], 0, 0d, 0, 1, criteria.PageSize);
                },
                () => new DashboardFacets([], [], []),
                () => [],
                () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()),
                new GeneralSettings(),
                _delay.Delay,
                scanStatus: Status,
                post: action => action(),
                probeRoots: () =>
                {
                    Interlocked.Increment(ref _probeCount);
                    recordProbeThread?.Invoke(Dispatcher.UIThread.CheckAccess());
                    return probeOverride is null ? UnreachableRoots : probeOverride();
                },
                startScan: startScan);

            Dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        }

        public DashboardViewModel Dashboard { get; }

        public ScanCoordinator? Coordinator { get; }

        public ScanStatusService? Status { get; }

        public TargetListingCriteria? LastCriteria { get; private set; }

        /// <summary>What the next probe returns. Assigned before <see cref="SettleAsync"/>.</summary>
        public IReadOnlyList<string> UnreachableRoots { get; set; } = [];

        private int _probeCount;

        public int ProbeCount => Volatile.Read(ref _probeCount);

        // Releases the parked debounce until both the query and the probe opened by construction
        // (or by a scan) have resolved.
        public async Task SettleAsync()
        {
            await DrainAsync(Dashboard.PendingQuery);
            await DrainAsync(Dashboard.PendingRootProbe);
        }

        // Drops back to idle exactly the way ScanFinished does in production: the coordinator's
        // connection string is never migrated, so the run throws once the pipeline reads settings,
        // but not before its finally block has raised ScanFinished.
        public async Task RaiseScanFinishedAsync()
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => Coordinator!.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));
            await SettleAsync();
        }

        public void Dispose()
        {
            Status?.Dispose();
        }

        private async Task DrainAsync(Task? work)
        {
            if (work is null)
            {
                return;
            }

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!work.IsCompleted && DateTime.UtcNow < deadline)
            {
                _delay.Release();
                await Task.Delay(2);
            }

            Assert.True(work.IsCompleted, "The dashboard's pending work never completed.");
            await work;
        }
    }

    private static void AssertExactlyOneState(DashboardViewModel dashboard, DashboardContentState expected)
    {
        Assert.Equal(expected, dashboard.ContentState);
        Assert.Equal(
            1,
            new[]
            {
                dashboard.ContentState == DashboardContentState.Loading,
                dashboard.ShowRows,
                dashboard.ShowNoFramesYet,
                dashboard.ShowNoMatches,
            }.Count(flag => flag));
    }

    [Fact]
    public async Task NoFrames_AndNoFiltersActive_RendersNoFramesCatalogued()
    {
        using var harness = new Harness();

        await harness.SettleAsync();

        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoFramesYet);
        Assert.False(harness.LastCriteria!.AnyFilterActive);
    }

    [Fact]
    public async Task NoResults_WithFiltersActive_RendersNoTargetsMatch()
    {
        using var harness = new Harness();
        await harness.SettleAsync();

        harness.Dashboard.Filters.ObjectTypes.First(pill => pill.Key == "Galaxy").IsSelected = true;
        await harness.SettleAsync();

        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoMatches);
        Assert.True(harness.LastCriteria!.AnyFilterActive);
    }

    // FIXER LIST F3 amendment. A blank Filters entry contributes no SQL clause, so it narrows
    // nothing; if AnyFilterActive still called it a filter, an empty library would offer
    // "No targets match these filters" with a Reset button that clears nothing.
    [Fact]
    public async Task NoFrames_WithOnlyABlankFilterEntrySelected_StillRendersNoFramesCatalogued()
    {
        using var harness = new Harness();
        await harness.SettleAsync();

        // The panel never offers a blank pill (AliasMap drops blank canonical names), so the entry
        // is injected here and the query re-requested through the one call site.
        harness.Dashboard.Filters.Filters.Add(new ToggleOptionViewModel("", "") { IsSelected = true });
        harness.Dashboard.RequestQuery();
        await harness.SettleAsync();

        Assert.Equal([""], harness.LastCriteria!.Filters);
        Assert.False(harness.LastCriteria.AnyFilterActive);
        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoFramesYet);
    }

    [Fact]
    public async Task TheTwoEmptyStates_AreMutuallyExclusive()
    {
        using var harness = new Harness();
        await harness.SettleAsync();
        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoFramesYet);

        harness.Dashboard.Filters.ObjectTypes.First(pill => pill.Key == "Galaxy").IsSelected = true;
        await harness.SettleAsync();
        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoMatches);

        harness.Dashboard.Filters.ResetCommand.Execute(null);
        await harness.SettleAsync();
        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoFramesYet);
    }

    [Fact]
    public async Task RowsPresent_RenderNeitherEmptyState()
    {
        using var harness = new Harness(withRows: true);

        await harness.SettleAsync();

        AssertExactlyOneState(harness.Dashboard, DashboardContentState.Rows);
    }

    [Fact]
    public async Task RunScanCommand_InvokesTheInjectedStartScanDelegate_Once()
    {
        // The delegate runs inside RunScanAsync's Task.Run, so Execute returns at the first await
        // and the count is written on a pool thread. Awaiting ExecutionTask joins that work, as
        // this test's three siblings already do; Interlocked and Volatile.Read give the counter
        // itself the barrier a captured local int does not have.
        using var harness = new Harness(startScan: _ =>
        {
            Interlocked.Increment(ref _startScanInvocations);
            return Task.CompletedTask;
        });
        await harness.SettleAsync();

        Assert.True(harness.Dashboard.RunScanCommand.CanExecute(null));
        harness.Dashboard.RunScanCommand.Execute(null);
        await harness.Dashboard.RunScanCommand.ExecutionTask!;

        Assert.Equal(1, Volatile.Read(ref _startScanInvocations));
    }

    // Written off-thread by the test above, read on the test thread.
    private int _startScanInvocations;

    [Fact]
    public async Task RunScanCommand_IsDisabledWhileAScanIsRunning()
    {
        using var harness = new Harness(withScanStatus: true, startScan: _ => Task.CompletedTask);
        await harness.SettleAsync();
        Assert.True(harness.Dashboard.RunScanCommand.CanExecute(null));

        // Reaches IsRunning == true through the same seam production uses.
        harness.Coordinator!.RaiseProgress(GalactiLog.Core.Scanning.ScanTaskNames.Discovery, 1, 1, "Discovering...", force: true);

        Assert.False(harness.Dashboard.RunScanCommand.CanExecute(null));
    }

    // The first scan of a fresh install: the library is empty and a scan is running, so the page
    // shows that scan's progress rather than an instruction to add a folder that already exists.
    [Fact]
    public async Task NoFramesYet_ShowsTheRunningScan_InsteadOfTheInstruction()
    {
        using var harness = new Harness(withScanStatus: true, startScan: _ => Task.CompletedTask);
        await harness.SettleAsync();
        Assert.True(harness.Dashboard.ShowNoFramesYet);
        Assert.False(harness.Dashboard.ShowFirstScan);

        harness.Coordinator!.RaiseProgress(GalactiLog.Core.Scanning.ScanTaskNames.Discovery, 1, 4, "Discovering...", force: true);

        Assert.True(harness.Dashboard.ShowFirstScan);
        Assert.False(harness.Dashboard.ShowNoFramesYet);
        Assert.True(harness.Dashboard.ShowEmptyState);
        Assert.Same(harness.Status, harness.Dashboard.ScanStatus);

        await harness.RaiseScanFinishedAsync();

        Assert.False(harness.Dashboard.ShowFirstScan);
        Assert.True(harness.Dashboard.ShowNoFramesYet);
    }

    [Fact]
    public async Task RunScanCommand_IsDisabledWhileARetryHoldsTheResolutionLease()
    {
        // Phase 7 fixer item 1: the coordinator refuses a scan while spec 9.7's retry is
        // resolving, so the button greys rather than offering a run that comes back as pending.
        using var harness = new Harness(withScanStatus: true, startScan: _ => Task.CompletedTask);
        await harness.SettleAsync();
        Assert.True(harness.Dashboard.RunScanCommand.CanExecute(null));

        // The same lease the retry takes, through the same seam production uses.
        var lease = harness.Coordinator!.TryBeginResolution();
        Assert.NotNull(lease);
        Assert.True(harness.Status!.ResolutionInProgress);
        Assert.False(harness.Dashboard.RunScanCommand.CanExecute(null));

        lease!.Dispose();
        Assert.False(harness.Status.ResolutionInProgress);
        Assert.True(harness.Dashboard.RunScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunScanCommand_IsDisabledWithoutAStartScanDelegate()
    {
        using var harness = new Harness();

        await harness.SettleAsync();

        Assert.False(harness.Dashboard.RunScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResetFiltersButton_OnTheNoMatchesState_ClearsEverySection()
    {
        using var harness = new Harness();
        await harness.SettleAsync();

        var filters = harness.Dashboard.Filters;
        filters.ObjectTypes.First(pill => pill.Key == "Galaxy").IsSelected = true;
        filters.DateFrom = new DateTimeOffset(new DateTime(2025, 1, 1), TimeSpan.Zero);
        filters.DateTo = new DateTimeOffset(new DateTime(2025, 6, 1), TimeSpan.Zero);
        filters.PinnedTargetId = Guid.NewGuid();
        filters.DraftHeaderKey = "GAIN";
        filters.DraftHeaderValue = "100";
        filters.AddHeaderConditionCommand.Execute(null);
        await harness.SettleAsync();
        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoMatches);

        filters.ResetCommand.Execute(null);
        await harness.SettleAsync();

        Assert.False(harness.LastCriteria!.AnyFilterActive);
        Assert.All(filters.Sections, section => Assert.False(section.IsActive));
        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoFramesYet);
    }

    [Fact]
    public async Task UnreachableRoot_RendersABannerNamingThePath()
    {
        using var harness = new Harness(unreachableRoots: [@"Z:\Astro\Lights", @"\\nas\captures"]);

        await harness.SettleAsync();

        Assert.True(harness.Dashboard.HasUnreachableRoots);
        Assert.Equal([@"Z:\Astro\Lights", @"\\nas\captures"], harness.Dashboard.UnreachableRoots);
    }

    [Fact]
    public async Task UnreachableRoot_BannerDoesNotReplaceExistingRows()
    {
        using var harness = new Harness(withRows: true, unreachableRoots: [@"Z:\Astro\Lights"]);

        await harness.SettleAsync();

        // Spec 12.10: "a warning banner ... not a silent empty list".
        Assert.True(harness.Dashboard.HasUnreachableRoots);
        AssertExactlyOneState(harness.Dashboard, DashboardContentState.Rows);
        Assert.Single(harness.Dashboard.Targets.Rows);
    }

    [Fact]
    public async Task UnreachableRoots_AreRefreshedAfterAScanFinishes()
    {
        using var harness = new Harness(withScanStatus: true, unreachableRoots: [@"Z:\Astro\Lights"]);
        await harness.SettleAsync();
        Assert.True(harness.Dashboard.HasUnreachableRoots);
        var probesBefore = harness.ProbeCount;

        // The root came back between scans; the next ScanFinished is what notices (ruling Q18).
        harness.UnreachableRoots = [];
        await harness.RaiseScanFinishedAsync();

        Assert.True(harness.ProbeCount > probesBefore);
        Assert.False(harness.Dashboard.HasUnreachableRoots);
        Assert.Empty(harness.Dashboard.UnreachableRoots);
    }

    [Fact]
    public async Task NoConfiguredRoots_ProducesNoBanner()
    {
        // Zero configured roots is not an unreachable root: the probe returns nothing and the
        // page falls to the "No frames catalogued yet" state instead.
        using var harness = new Harness(unreachableRoots: []);

        await harness.SettleAsync();

        Assert.False(harness.Dashboard.HasUnreachableRoots);
        AssertExactlyOneState(harness.Dashboard, DashboardContentState.NoFramesYet);
    }

    [Fact]
    public async Task ReachableRoots_ProduceNoBanner()
    {
        using var harness = new Harness(withRows: true, unreachableRoots: []);

        await harness.SettleAsync();

        Assert.False(harness.Dashboard.HasUnreachableRoots);
        Assert.Empty(harness.Dashboard.UnreachableRoots);
    }

    // Review item 1. ScanCoordinator.RunAsync walks directories, loads the known-file set and
    // classifies synchronously before its first await, so invoking it straight from the command
    // handler froze the window for the length of a scan. Fails before the fix (Execute blocked
    // until the gate timed out and ExecutionTask was already complete when it returned), passes
    // after.
    [Fact]
    public async Task RunScanCommand_DoesNotBlockTheCallerWhileTheScanRuns()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var harness = new Harness(startScan: _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        });
        await harness.SettleAsync();

        harness.Dashboard.RunScanCommand.Execute(null);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "The scan delegate was never invoked.");
        Assert.False(
            harness.Dashboard.RunScanCommand.ExecutionTask!.IsCompleted,
            "Execute returned only after the scan delegate finished; the scan ran on the caller's thread.");

        release.Set();
        await harness.Dashboard.RunScanCommand.ExecutionTask!;
        Assert.Null(harness.Dashboard.LastScanFailure);
    }

    // FIXER LIST F24 (and Task 8's deviation D10): the command used to be built from a
    // Func<CancellationToken, Task>, so a second Execute cancelled the in-flight command token and
    // the running scan was aborted rather than the second press being refused. The token is gone
    // and the body guard is what answers the second press.
    [Fact]
    public async Task RunScanCommand_ExecutedTwice_RefusesTheSecondPress_AndDoesNotAbortTheFirst()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var harness = new Harness(withScanStatus: true, startScan: _ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        });
        await harness.SettleAsync();

        harness.Dashboard.RunScanCommand.Execute(null);
        var first = harness.Dashboard.RunScanCommand.ExecutionTask!;
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "The scan delegate was never invoked.");

        // Reaches IsRunning == true through the same seam production uses, which is what the body
        // guard reads.
        harness.Coordinator!.RaiseProgress(
            GalactiLog.Core.Scanning.ScanTaskNames.Discovery, 1, 1, "Discovering...", force: true);
        harness.Dashboard.RunScanCommand.Execute(null);

        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Null(harness.Dashboard.LastScanFailure);
    }

    [Fact]
    public async Task RunScanCommand_AThrowingDelegate_SurfacesTheFailureAndDoesNotPropagate()
    {
        using var harness = new Harness(startScan: _ => throw new InvalidOperationException("no scan roots"));
        await harness.SettleAsync();

        harness.Dashboard.RunScanCommand.Execute(null);
        await harness.Dashboard.RunScanCommand.ExecutionTask!;

        Assert.IsType<InvalidOperationException>(harness.Dashboard.LastScanFailure);
        Assert.True(harness.Dashboard.HasFailure);
        Assert.Equal("The scan could not be started. See the log for details.", harness.Dashboard.FailureText);
    }

    // Review item 4. A disconnected share can make one probe take seconds, so a probe started by
    // an earlier load can still be in flight when a later scan starts a newer one.
    [Fact]
    public async Task RootProbe_AnOlderProbeCompletingLast_DoesNotPublish()
    {
        using var gate = new ManualResetEventSlim();
        var calls = 0;
        using var harness = new Harness(withScanStatus: true, probeOverride: () =>
            Interlocked.Increment(ref calls) == 1
                ? Stale(gate)
                : ["FRESH"]);

        var staleProbe = harness.Dashboard.PendingRootProbe!;
        await harness.RaiseScanFinishedAsync();
        Assert.Equal(["FRESH"], harness.Dashboard.UnreachableRoots);

        gate.Set();
        await staleProbe;

        Assert.Equal(["FRESH"], harness.Dashboard.UnreachableRoots);

        static IReadOnlyList<string> Stale(ManualResetEventSlim gate)
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return ["STALE"];
        }
    }

    // Polish wave 5 ruling 2: dismiss snapshots the set; the banner returns only when a later
    // probe's set differs (set comparison, order ignored).
    [Fact]
    public async Task DismissUnreachableRoots_HidesTheBanner()
    {
        using var harness = new Harness(unreachableRoots: [@"Z:\Astro\Lights"]);
        await harness.SettleAsync();
        Assert.True(harness.Dashboard.HasUnreachableRoots);

        harness.Dashboard.DismissUnreachableRootsCommand.Execute(null);

        Assert.False(harness.Dashboard.HasUnreachableRoots);
    }

    [Fact]
    public async Task DismissUnreachableRoots_ALaterProbeWithTheSameSet_StaysHidden()
    {
        using var harness = new Harness(withScanStatus: true, unreachableRoots: [@"Z:\Astro\Lights"]);
        await harness.SettleAsync();
        harness.Dashboard.DismissUnreachableRootsCommand.Execute(null);
        Assert.False(harness.Dashboard.HasUnreachableRoots);

        await harness.RaiseScanFinishedAsync();

        Assert.False(harness.Dashboard.HasUnreachableRoots);
    }

    [Fact]
    public async Task DismissUnreachableRoots_ALaterProbeWithADifferentSet_ShowsAgain()
    {
        using var harness = new Harness(withScanStatus: true, unreachableRoots: [@"Z:\Astro\Lights"]);
        await harness.SettleAsync();
        harness.Dashboard.DismissUnreachableRootsCommand.Execute(null);
        Assert.False(harness.Dashboard.HasUnreachableRoots);

        harness.UnreachableRoots = [@"Z:\Astro\Lights", @"\\nas\captures"];
        await harness.RaiseScanFinishedAsync();

        Assert.True(harness.Dashboard.HasUnreachableRoots);
    }

    // A probe publishing an empty list clears the dismissed snapshot, so a folder that comes
    // back and later drops out again shows the banner again.
    [Fact]
    public async Task DismissUnreachableRoots_AnEmptyProbeThenTheSameSetAgain_ShowsTheBannerAgain()
    {
        using var harness = new Harness(withScanStatus: true, unreachableRoots: [@"Z:\Astro\Lights"]);
        await harness.SettleAsync();
        harness.Dashboard.DismissUnreachableRootsCommand.Execute(null);
        Assert.False(harness.Dashboard.HasUnreachableRoots);

        harness.UnreachableRoots = [];
        await harness.RaiseScanFinishedAsync();
        Assert.False(harness.Dashboard.HasUnreachableRoots);

        harness.UnreachableRoots = [@"Z:\Astro\Lights"];
        await harness.RaiseScanFinishedAsync();

        Assert.True(harness.Dashboard.HasUnreachableRoots);
    }

    // Polish wave 5 ruling 3: dismiss clears both failures; the next one shows the banner again.
    [Fact]
    public async Task DismissFailure_ClearsHasFailure_AndANewFailureShowsItAgain()
    {
        using var harness = new Harness(startScan: _ => throw new InvalidOperationException("no scan roots"));
        await harness.SettleAsync();
        harness.Dashboard.RunScanCommand.Execute(null);
        await harness.Dashboard.RunScanCommand.ExecutionTask!;
        Assert.True(harness.Dashboard.HasFailure);

        harness.Dashboard.DismissFailureCommand.Execute(null);
        Assert.False(harness.Dashboard.HasFailure);

        harness.Dashboard.RunScanCommand.Execute(null);
        await harness.Dashboard.RunScanCommand.ExecutionTask!;

        Assert.True(harness.Dashboard.HasFailure);
    }

    // [AvaloniaFact], not [Fact]: Dispatcher.UIThread means nothing without a real UI thread --
    // in a plain xunit test the first CheckAccess() call initializes the dispatcher on whatever
    // thread asked, so the assertion would pass vacuously. The headless session gives a genuine
    // UI thread to be off.
    //
    // async, and it awaits the probe rather than blocking on it (phase review item 2, F18). The
    // headless UI thread is itself a thread-pool thread, so a blocking GetResult() on a Task.Run
    // that was queued from it lets the pool inline that work onto the very thread the test is
    // asserting is not the UI thread. That is why this test failed intermittently in full runs and
    // passed filtered: it needs a saturated pool to reproduce.
    [AvaloniaFact]
    public async Task RootProbe_RunsOffTheUiThread()
    {
        // A disconnected share can make a directory probe block for seconds, which is why it
        // never runs on the dispatcher (ruling Q18).
        var onUiThread = new List<bool>();
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        using var harness = new Harness(recordProbeThread: onUiThread.Add);
        await harness.Dashboard.PendingRootProbe!;

        Assert.NotEmpty(onUiThread);
        Assert.All(onUiThread, Assert.False);
    }
}
