using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.Core.Scanning;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Plain xunit facts, no window and no database: design-spec 18.3's rule that every view-model is
// unit-testable on its own is what these assert.
public class DiagnosticsViewModelTests
{
    // Drops back to idle exactly the way ScanFinished does in production: the bare coordinator's
    // connection string is never migrated, so the run throws once its pipeline reads settings, but
    // not before its finally block has raised ScanFinished. The shape ActivityViewModelTests,
    // StatusBarViewModelTests and StatisticsViewModelTests already use.
    private static Task RaiseScanFinishedAsync(ScanCoordinator coordinator)
        => Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

    [Fact]
    public void Groups_AreTheSevenSpecGroups_InSpecOrder()
    {
        using var page = Factory.Create();

        Assert.Equal(
            new[] { "Database", "Scan", "Resolver", "Unresolved", "Errors", "Versions", "Paths" },
            page.Groups.Select(group => group.Title));
    }

    [Fact]
    public async Task Refresh_RunsTheSnapshotOffTheUiThread()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var snapshotThread = 0;
        var page = Factory.Build(
            () => { snapshotThread = Environment.CurrentManagedThreadId; return Factory.Snapshot(); },
            unresolved: null,
            scanStatus: null,
            post: null);

        // Awaited, never blocked on (TRACKING section 2 item 8).
        await Factory.SettleAsync(page);

        Assert.NotEqual(0, snapshotThread);
        Assert.NotEqual(uiThread, snapshotThread);
        page.Dispose();
    }

    [Fact]
    public async Task Refresh_PressedTwice_RunsTheSnapshotOnce()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var page = Factory.Build(
            () =>
            {
                Interlocked.Increment(ref calls);
                gate.Task.GetAwaiter().GetResult();
                return Factory.Snapshot();
            },
            unresolved: null,
            scanStatus: null,
            post: null);

        // The first execution's task is captured BEFORE the second Execute: reading ExecutionTask
        // after the second press can pick up a completed first run and assert nothing (the Phase 9
        // flake shape, TRACKING section 6 item 13).
        var first = page.RefreshCommand.ExecutionTask;
        Assert.NotNull(first);

        page.RefreshCommand.Execute(null);
        gate.SetResult();
        await first;
        await Factory.SettleAsync(page);

        Assert.Equal(1, calls);
        page.Dispose();
    }

    [Fact]
    public async Task Refresh_WhileRefreshing_IsGuardedInTheCommandBody_NotOnlyByCanExecute()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var page = Factory.Build(
            () =>
            {
                Interlocked.Increment(ref calls);
                gate.Task.GetAwaiter().GetResult();
                return Factory.Snapshot();
            },
            unresolved: null,
            scanStatus: null,
            post: null);

        var first = page.RefreshCommand.ExecutionTask;

        // RelayCommand.Execute ignores CanExecute, so the guard has to be in the body as well.
        page.RefreshCommand.Execute(null);
        page.RefreshCommand.Execute(null);

        gate.SetResult();
        Assert.NotNull(first);
        await first;
        await Factory.SettleAsync(page);

        Assert.Equal(1, calls);
        page.Dispose();
    }

    [Fact]
    public void Fields_ForAnAbsentValue_RenderUnavailable_NotEmpty()
    {
        using var page = Factory.Create(() => Factory.Snapshot(
            scan: new ScanDiagnostics(false, "", "", 0, false, null, [], null),
            app: new AppDiagnostics("1.0.0.0", "", "", "10.0.0", "11.3.0", "3.46.1", "Windows")));

        var scan = page.Groups.Single(group => group.Title == "Scan");
        var versions = page.Groups.Single(group => group.Title == "Versions");

        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(scan, "Last run"));
        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(scan, "Last run trigger"));
        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(scan, "Last run duration"));
        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(scan, "Next scheduled scan"));
        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(scan, "Current task"));
        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(versions, "Git commit"));
        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(versions, "Update channel"));

        // The roadmap's Verify clause: no null field where the spec names a value, and no empty
        // one either.
        Assert.All(
            page.Groups.SelectMany(group => group.Fields),
            field => Assert.False(string.IsNullOrWhiteSpace(field.Value)));
    }

    // Spec 12.8's Scan row, asserted as a field SET rather than as non-emptiness of whatever
    // happens to be there. The review's finding I1 was that the trigger and the duration were
    // absent and every existing case walked only the fields that existed, so nothing failed.
    [Fact]
    public void Scan_RendersEveryFieldSpec128NamesForThatRow()
    {
        using var page = Factory.Create(() => Factory.Snapshot(
            scan: new ScanDiagnostics(
                true, "ingest", "Ingested 4/10 files", 40, true, Factory.Run(),
                [new(@"D:\Astro", true, true)],
                Factory.Noon.UtcDateTime.AddHours(1))));

        var scan = page.Groups.Single(group => group.Title == "Scan");

        // Current state, the progress envelope, then the last run's trigger, start, finish,
        // duration and every counter, then the roots and the next scheduled time.
        Assert.Equal(
            new[]
            {
                "Scan running",
                "Current task",
                "Current message",
                "Progress",
                "Last run",
                "Last run trigger",
                "Last run finished",
                "Last run duration",
                "Last run results",
                "Next scheduled scan",
                "Library folders",
            },
            scan.Fields.Select(field => field.Label));

        Assert.Equal("manual", Value(scan, "Last run trigger"));

        // Factory.Run finishes three minutes after it starts.
        Assert.Equal("00:03:00", Value(scan, "Last run duration"));
        Assert.Contains("complete", Value(scan, "Last run"));
        Assert.Contains("2025-03-04 12:00:00 UTC", Value(scan, "Last run"));
        Assert.Equal("2025-03-04 13:00:00 UTC", Value(scan, "Next scheduled scan"));
        Assert.Equal("40 %", Value(scan, "Progress"));

        // The per-root rows are the Scan group's other half of spec 12.8's row.
        var root = Assert.Single(scan.WatcherRoots);
        Assert.Equal(@"D:\Astro", root.Root);
        Assert.True(root.Watching);
        Assert.True(root.Reachable);
    }

    [Fact]
    public void Scan_LastRunDuration_IsUnavailable_WhenTheRunNeverFinished()
    {
        using var page = Factory.Create(() => Factory.Snapshot(
            scan: new ScanDiagnostics(
                true, "ingest", "working", 0, false,
                Factory.UnfinishedRun(),
                [], null)));

        var scan = page.Groups.Single(group => group.Title == "Scan");

        // A run still in flight, or one a crash left open, has no finish and therefore no
        // duration worth reporting as a number. The trigger is still known.
        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(scan, "Last run duration"));
        Assert.Equal(DiagnosticsViewModel.Unavailable, Value(scan, "Last run finished"));
        Assert.Equal("manual", Value(scan, "Last run trigger"));
    }

    [Fact]
    public void Database_RowCounts_AreRenderedInSpecOrder()
    {
        using var page = Factory.Create();

        var database = page.Groups.Single(group => group.Title == "Database");

        // Rendered through DiagnosticsQuery.RowCountTables, never through the dictionary's own
        // enumeration order, which is unspecified (review finding M1).
        Assert.Equal(
            DiagnosticsQuery.RowCountTables,
            database.Fields
                .Select(field => field.Label)
                .Where(label => DiagnosticsQuery.RowCountTables.Contains(label))
                .ToArray());
    }

    [Fact]
    public void Fields_RenderTheSnapshotsRowCountsAndCounters()
    {
        using var page = Factory.Create();

        var database = page.Groups.Single(group => group.Title == "Database");
        var resolver = page.Groups.Single(group => group.Title == "Resolver");

        Assert.All(
            DiagnosticsQuery.RowCountTables,
            table => Assert.Equal("3", Value(database, table)));
        Assert.Equal("0 B", Value(database, "Write-ahead log size"));

        // Spec 12.8's "PHD2 data size" (ruling F2): the figure and, in the same value, the method
        // that produced it. A reader who cannot tell a measured figure from an estimate has a
        // figure they cannot use.
        Assert.Equal(
            "2 KB (" + DiagnosticsQuery.MeasuredMethod + ")",
            Value(database, DiagnosticsViewModel.Phd2DataSizeLabel));
        Assert.Equal("7", Value(resolver, "Cache hits this session"));
        Assert.Equal("2", Value(resolver, "Cache misses this session"));
    }

    [Fact]
    public async Task Refresh_RebuildsFieldsInPlace_AndKeepsTheExpanderState()
    {
        var hits = 1L;
        using var page = Factory.Create(() => Factory.Snapshot(
            resolver: new ResolverDiagnostics(0, 0, 0, hits, 0)));

        var groups = page.Groups.ToArray();
        var resolver = page.Groups.Single(group => group.Title == "Resolver");
        resolver.IsExpanded = true;

        hits = 9;

        // Awaited rather than fired and settled afterwards, so the assertions below cannot race
        // the publish (TRACKING section 2 item 8).
        await page.RefreshCommand.ExecuteAsync(null);

        // The same group objects, so the expander state a user set survives a refresh.
        Assert.Equal(groups, page.Groups.ToArray());
        Assert.True(resolver.IsExpanded);
        Assert.Equal("9", Value(resolver, "Cache hits this session"));
    }

    [Fact]
    public async Task ScanFinished_TriggersARefresh()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var calls = 0;
        using var page = Factory.Create(
            () => { Interlocked.Increment(ref calls); return Factory.Snapshot(); },
            scanStatus: status);
        Assert.Equal(1, calls);

        await RaiseScanFinishedAsync(coordinator);
        await Factory.SettleAsync(page);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Dispose_DetachesTheScanFinishedSubscription()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var calls = 0;
        var page = Factory.Create(
            () => { Interlocked.Increment(ref calls); return Factory.Snapshot(); },
            scanStatus: status);

        page.Dispose();
        await RaiseScanFinishedAsync(coordinator);
        await Factory.SettleAsync(page);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Constructor_RunsNoQuery()
    {
        var calls = 0;
        var post = new Factory.DeferredPost();

        var page = Factory.Build(
            () => { calls++; return Factory.Snapshot(); },
            unresolved: null,
            scanStatus: null,
            post: post.Post);

        // The first refresh is scheduled through the dispatcher seam, never run inside the
        // constructor: a synchronous SQLite read on the UI thread at construction is the defect
        // TRACKING item 16 exists to prevent, and this page is a lazily built rail destination.
        Assert.Equal(0, calls);
        Assert.Equal(1, post.Queued);
        Assert.All(page.Groups, group => Assert.Empty(group.Fields));

        post.Drain();
        Factory.Settle(page);

        Assert.Equal(1, calls);
        page.Dispose();
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeTheSharedUnresolvedNamesViewModel()
    {
        var unresolved = Factory.Unresolved();
        var page = Factory.Create(unresolved: unresolved);

        page.Dispose();

        // A DI singleton the host owns, and the Settings Targets tab renders the same instance.
        // Still usable after the page that hosted it has gone.
        Assert.Same(unresolved, page.Unresolved);

        // AWAITED, never fired and forgotten (TRACKING section 2 item 8). The reload's read runs
        // on the pool under the list's own lifetime token, and the Dispose below cancels that
        // token: a work item the pool has not started yet is cancelled rather than run, and
        // AsyncRelayCommand.Execute rethrows that cancellation on the ambient synchronization
        // context, which xUnit attributes to whichever test is running. Fix round 2.
        await unresolved.ReloadCommand.ExecuteAsync(null);

        Assert.False(unresolved.LoadFailed);
        unresolved.Dispose();
    }

    [Fact]
    public async Task LogViewer_IsHosted_AndIsDisposedWithThePage()
    {
        // Phase 10 Task 2. The log viewer is the one thing this page owns outright: nothing else
        // renders it and nothing else holds it, so unlike the shared unresolved-names list it is
        // disposed here, which is what stops its follow-tail loop.
        using var fixture = new LogViewerFixture();
        var viewer = await fixture.CreateAsync();
        var page = new DiagnosticsViewModel(
            () => Factory.Snapshot(),
            Factory.Unresolved(),
            post: action => action(),
            logViewer: viewer);

        Assert.Same(viewer, page.LogViewer);
        Assert.True(viewer.RefreshCommand.CanExecute(null));

        page.Dispose();

        // The viewer's own commands go inert only when it has been disposed.
        Assert.False(viewer.RefreshCommand.CanExecute(null));
        Assert.False(viewer.OpenLogFolderCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- spec 16.3's export

    [Fact]
    public async Task ExportBundle_CallsThePickerExactlyOnce()
    {
        var picks = 0;
        var written = new List<string>();
        var page = Factory.Build(
            snapshot: null,
            unresolved: null,
            scanStatus: null,
            post: null,
            exportBundle: written.Add,
            pickDestination: () =>
            {
                Interlocked.Increment(ref picks);
                return Task.FromResult<string?>(@"D:\Support\bundle.json");
            });
        await Factory.SettleAsync(page);

        await page.ExportBundleCommand.ExecuteAsync(null);

        Assert.Equal(1, picks);
        Assert.Equal([@"D:\Support\bundle.json"], written);
        Assert.Equal(
            DiagnosticsViewModel.ExportedPrefix + @"D:\Support\bundle.json",
            page.ExportStatus);
        Assert.False(page.IsExporting);
        page.Dispose();
    }

    [Fact]
    public async Task ExportBundle_WhenThePickerReturnsNull_WritesNothing()
    {
        var written = new List<string>();
        var page = Factory.Build(
            snapshot: null, unresolved: null, scanStatus: null, post: null,
            exportBundle: written.Add,
            pickDestination: () => Task.FromResult<string?>(null));
        await Factory.SettleAsync(page);

        await page.ExportBundleCommand.ExecuteAsync(null);

        // The user cancelled: nothing is written and the status line is left as it was.
        Assert.Empty(written);
        Assert.Null(page.ExportStatus);
        page.Dispose();
    }

    [Fact]
    public async Task ExportBundle_WhenThePickerReturnsBlank_WritesNothing()
    {
        var written = new List<string>();
        var page = Factory.Build(
            snapshot: null, unresolved: null, scanStatus: null, post: null,
            exportBundle: written.Add,
            pickDestination: () => Task.FromResult<string?>("   "));
        await Factory.SettleAsync(page);

        await page.ExportBundleCommand.ExecuteAsync(null);

        Assert.Empty(written);
        Assert.Null(page.ExportStatus);
        page.Dispose();
    }

    [Fact]
    public async Task ExportBundle_WithNoPickerSeam_WritesNothing_AndDoesNotThrow()
    {
        var written = new List<string>();
        var page = Factory.Build(
            snapshot: null, unresolved: null, scanStatus: null, post: null,
            exportBundle: written.Add,
            pickDestination: null);
        await Factory.SettleAsync(page);

        // A surface with no dialog exports nothing. A null seam is not an error and not a log
        // line: it is the shape a unit-test host has.
        await page.ExportBundleCommand.ExecuteAsync(null);

        Assert.Empty(written);
        Assert.Null(page.ExportStatus);
        Assert.False(page.IsExporting);
        page.Dispose();
    }

    [Fact]
    public async Task ExportBundle_RunsTheWriteOffTheUiThread()
    {
        // A recording post seam and a gated write, so "the write ran off the caller" and "the
        // status was published on the thread the seam dispatched to" are facts about ordering
        // rather than races. The previous shape compared thread ids against a caller captured
        // before an await, which is load dependent for the reason Task 2's follow-up round
        // documents: a caller parked on an await is back in the pool and can be reused by the
        // very Task.Run it is waiting for (review finding F5, TRACKING section 2 item 8 family).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeOnPool = false;
        var writeThread = 0;
        var writeFinished = false;

        var post = new Factory.RecordingPost();
        var statusThreads = new List<int>();

        var page = Factory.Build(
            snapshot: null, unresolved: null, scanStatus: null, post: post.Post,
            exportBundle: _ =>
            {
                writeThread = Environment.CurrentManagedThreadId;
                writeOnPool = Thread.CurrentThread.IsThreadPoolThread;
                writeStarted.TrySetResult();
                gate.Task.GetAwaiter().GetResult();
                writeFinished = true;
            },
            pickDestination: () => Task.FromResult<string?>(@"D:\Support\bundle.json"));

        // The constructor's first refresh is posted, so it is drained before the export begins.
        post.Drain();
        await Factory.SettleAsync(page);
        post.Drain();

        page.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiagnosticsViewModel.ExportStatus))
            {
                statusThreads.Add(Environment.CurrentManagedThreadId);
            }
        };

        var callerThread = Environment.CurrentManagedThreadId;
        var export = page.ExportBundleCommand.ExecuteAsync(null);

        // Checked before any await, so it is the caller's own thread that is observed to have run
        // neither the write to completion nor the publish. Task.Run cannot have inlined onto it.
        Assert.False(export.IsCompleted);
        Assert.False(writeFinished);
        Assert.Equal(0, post.Queued);

        // The write is on a thread-pool thread, which is where Task.Run put it. A property of
        // the thread, not an id comparison.
        await writeStarted.Task;
        Assert.True(writeOnPool);

        gate.SetResult();
        await post.WaitForQueuedAsync(1);

        // The publish was handed to the seam from a thread-pool thread, which is where Task.Run
        // put the write. This command hands the closure over and returns rather than awaiting the
        // publish, exactly as RefreshAsync does, so it can already be complete here; what is
        // asserted is where the closure came from, not that the command is still in flight.
        Assert.True(post.PostedFromThreadPool[^1]);

        // Drained on a dedicated non-pool thread, standing in for the dispatcher thread, so the
        // publishing thread is knowable exactly: a thread created here has an id distinct from
        // every thread already alive and is never a pool thread. Awaited through a completion
        // source rather than joined, so this test never blocks.
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new Thread(() =>
        {
            try
            {
                post.Drain();
            }
            finally
            {
                drained.TrySetResult();
            }
        })
        { IsBackground = true, Name = "DiagnosticsExportDrain" };
        drain.Start();
        await export;
        await drained.Task;

        var statusThread = Assert.Single(statusThreads);
        Assert.Equal(drain.ManagedThreadId, post.DrainThread);
        Assert.Equal(post.DrainThread, statusThread);
        Assert.NotEqual(callerThread, statusThread);
        Assert.NotEqual(writeThread, statusThread);
        Assert.NotEqual(post.Threads[^1], statusThread);
        Assert.Equal(
            DiagnosticsViewModel.ExportedPrefix + @"D:\Support\bundle.json",
            page.ExportStatus);
        page.Dispose();
    }

    [Fact]
    public async Task ExportBundle_PressedTwice_ExportsOnce()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var page = Factory.Build(
            snapshot: null, unresolved: null, scanStatus: null, post: null,
            exportBundle: _ =>
            {
                Interlocked.Increment(ref calls);
                gate.Task.GetAwaiter().GetResult();
            },
            pickDestination: () => Task.FromResult<string?>(@"D:\Support\bundle.json"));
        await Factory.SettleAsync(page);

        page.ExportBundleCommand.Execute(null);

        // The first execution's task is captured BEFORE the second Execute: reading ExecutionTask
        // after the second press can pick up a completed first run and assert nothing.
        var first = page.ExportBundleCommand.ExecutionTask;
        Assert.NotNull(first);

        page.ExportBundleCommand.Execute(null);
        gate.SetResult();
        await first;

        Assert.Equal(1, calls);
        page.Dispose();
    }

    [Fact]
    public async Task ExportBundle_ExecutedDirectly_IsGuardedInTheCommandBody()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var page = Factory.Build(
            snapshot: null, unresolved: null, scanStatus: null, post: null,
            exportBundle: _ =>
            {
                Interlocked.Increment(ref calls);
                gate.Task.GetAwaiter().GetResult();
            },
            pickDestination: () => Task.FromResult<string?>(@"D:\Support\bundle.json"));
        await Factory.SettleAsync(page);

        // RelayCommand.Execute ignores CanExecute, so the guard has to be in the body as well.
        page.ExportBundleCommand.Execute(null);
        var first = page.ExportBundleCommand.ExecutionTask;
        page.ExportBundleCommand.Execute(null);
        page.ExportBundleCommand.Execute(null);

        gate.SetResult();
        Assert.NotNull(first);
        await first;

        Assert.Equal(1, calls);
        page.Dispose();
    }

    [Fact]
    public async Task ExportBundle_OnAnIoFailure_ReportsTheMessage_AndDoesNotThrow()
    {
        var page = Factory.Build(
            snapshot: null, unresolved: null, scanStatus: null, post: null,
            exportBundle: _ => throw new IOException("the disk is full"),
            pickDestination: () => Task.FromResult<string?>(@"D:\Support\bundle.json"));
        await Factory.SettleAsync(page);

        await page.ExportBundleCommand.ExecuteAsync(null);

        Assert.Equal("the disk is full", page.ExportStatus);
        Assert.False(page.IsExporting);
        Assert.True(page.ExportBundleCommand.CanExecute(null));
        page.Dispose();
    }

    [Fact]
    public async Task ExportBundle_TheViewInstallsTheOnlyDestinationPicker()
    {
        var written = new List<string>();
        var page = Factory.Build(
            snapshot: null, unresolved: null, scanStatus: null, post: null,
            exportBundle: written.Add,
            pickDestination: null);
        await Factory.SettleAsync(page);

        // The seam the view's code-behind installs when it attaches, which is the only source of
        // an export path in the application (coordinator ruling Q10).
        page.DestinationPicker = () => Task.FromResult<string?>(@"D:\Support\bundle.json");
        await page.ExportBundleCommand.ExecuteAsync(null);

        Assert.Equal([@"D:\Support\bundle.json"], written);
        page.Dispose();
    }

    // Spec 12.8's Paths row and ruling Q9.10 (Phase 10 Task 9): the page shows where the data
    // location came from beside the path, not only the support bundle.
    [Fact]
    public void Paths_ShowTheDataLocationSourceBesideTheAppDataPath()
    {
        using var page = Factory.Create();

        var paths = page.Groups.Single(group => group.Title == "Paths");

        Assert.Equal(
            new[]
            {
                "Application data",
                "Data location source",
                "Database",
                "Logs",
                "Thumbnail cache",
                "Catalogues",
                "Startup shortcut",
                "Started minimized",
            },
            paths.Fields.Select(field => field.Label));

        Assert.Equal("default", Value(paths, "Data location source"));
    }

    // Phase 11 Task 3: the two fields spec 12.8's Paths group closes with (spec 12.11 behaviours
    // 8, 9), added to the same test the data-location-source case above already asserts the group
    // shape from.
    [Fact]
    public void Paths_ShowTheStartupShortcutAndStartedMinimizedFields()
    {
        using var page = Factory.Create(() => Factory.Snapshot(
            paths: new PathDiagnostics(
                @"C:\AppData\GalactiLog",
                @"C:\AppData\GalactiLog\galactilog.db",
                @"C:\AppData\GalactiLog\logs",
                @"C:\AppData\GalactiLog\thumbnails",
                @"C:\Program Files\GalactiLog\catalogs",
                "default",
                DiagnosticsService.StartupShortcutPresent,
                DiagnosticsService.StartedMinimizedYes)));

        var paths = page.Groups.Single(group => group.Title == "Paths");

        Assert.Equal("present", Value(paths, "Startup shortcut"));
        Assert.Equal("yes", Value(paths, "Started minimized"));
    }

    private static string Value(DiagnosticsGroupViewModel group, string label)
        => group.Fields.Single(field => field.Label == label).Value;
}
