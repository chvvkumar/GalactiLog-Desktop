using System.Diagnostics;
using System.Text.RegularExpressions;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Tray;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// FIXER LIST 4 / spec 10.5. App.DrainForShutdown is the body of the desktop lifetime's
// ShutdownRequested handler, extracted so it can be exercised without a window: it must stop
// the watcher and the scheduler FIRST (so nothing can start a new scan while the drain runs),
// then cancel whatever is in flight and wait for the coordinator to go idle.
public class AppShutdownTests
{
    [Fact]
    public async Task DrainForShutdown_StopsWatcherAndScheduler_ThenLeavesTheCoordinatorIdle()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with
        {
            ScanRoots = [settings.Root],
            WatcherEnabled = true,
            AutoScanIntervalMinutes = 60,
        });

        var delay = new FakeDelay();
        var sources = new List<FakeWatcherSource>();
        var coordinator = ScanCoordinatorTestFactory.Create(settings);

        var watcher = new WatcherService(
            settings.Store,
            (files, token) => coordinator.RunTargetedAsync(ScanTrigger.Watcher, files, token),
            token => coordinator.RunAsync(ScanTrigger.Watcher, null, token),
            NullLogger<WatcherService>.Instance,
            path => { var source = new FakeWatcherSource(path); sources.Add(source); return source; },
            delay.Delay);
        var scheduler = new ScanScheduler(
            settings.Store,
            () => coordinator.IsRunning,
            token => coordinator.RunAsync(ScanTrigger.Scheduler, null, token),
            NullLogger<ScanScheduler>.Instance,
            delay.Delay);

        watcher.Start();
        scheduler.Start();
        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.True(source.Started));

        var idle = App.DrainForShutdown(watcher, scheduler, coordinator);

        Assert.True(idle, "the drain must reach idle well inside its five second budget");
        Assert.All(sources, source => Assert.True(source.Disposed, "every watcher source must be released"));
        // FIXER LIST F11, the flake this test showed twice: DrainForShutdown returns as soon as
        // the coordinator reports idle, and with no scan running WaitForIdleAsync returns on its
        // first check without ever yielding. Stop() only cancels the scheduler's token, so the
        // loop task observes that cancellation on a pool thread afterwards and a bare IsCompleted
        // read here was a race that a loaded machine lost. The behaviour asserted is unchanged:
        // the loop must exit, and it must exit without being waited on for long. Awaited, never
        // blocked on (TRACKING section 2 item 8): WaitAsync throws TimeoutException if the loop
        // never exits, which is the failure this assertion is for.
        Assert.NotNull(scheduler.LoopTask);
        await scheduler.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(coordinator.IsRunning);
    }

    // Idempotent: shutdown can be requested twice (a close button plus a session-end signal)
    // and the second pass must not throw on already-stopped services.
    [Fact]
    public void DrainForShutdown_CalledTwice_IsSafe()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root], WatcherEnabled = false });

        var delay = new FakeDelay();
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var watcher = new WatcherService(
            settings.Store,
            (files, token) => coordinator.RunTargetedAsync(ScanTrigger.Watcher, files, token),
            token => coordinator.RunAsync(ScanTrigger.Watcher, null, token),
            NullLogger<WatcherService>.Instance,
            path => new FakeWatcherSource(path),
            delay.Delay);
        var scheduler = new ScanScheduler(
            settings.Store, () => coordinator.IsRunning,
            token => coordinator.RunAsync(ScanTrigger.Scheduler, null, token),
            NullLogger<ScanScheduler>.Instance, delay.Delay);

        Assert.True(App.DrainForShutdown(watcher, scheduler, coordinator));
        Assert.True(App.DrainForShutdown(watcher, scheduler, coordinator));
    }

    // Phase 8 Task 5, TRACKING section 6 item 6: the thumbnail worker holds two background tasks
    // and a CancellationTokenSource, so the drain that already stops the scan stops it too. The
    // two assertions above must pass unchanged, which is the evidence this was added beside the
    // scan drain rather than in place of it.
    [Fact]
    public async Task DrainForShutdown_DisposesTheThumbnailWorker()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root], WatcherEnabled = false });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var (watcher, scheduler) = CreateTriggers(settings, coordinator);

        var worker = new ThumbnailWorker(
            (_, _) => "frames/rendered.jpg",
            (_, _) => "previews/rendered.jpg",
            action => action());

        Assert.True(App.DrainForShutdown(watcher, scheduler, coordinator, worker));

        // A disposed worker renders nothing and leaves nobody waiting: the request completes as
        // "not rendered" rather than queueing behind a pump that is never coming back.
        var results = new List<string?>();
        using var afterDrain = worker.RequestFrame("frame.fits", results.Add);
        Assert.Equal([null], results);
        Assert.Equal(0, worker.OutstandingCount);

        Assert.NotNull(scheduler.LoopTask);
        await scheduler.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task DrainForShutdown_CompletesEveryOutstandingThumbnailRequest()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root], WatcherEnabled = false });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var (watcher, scheduler) = CreateTriggers(settings, coordinator);

        // Two renders in flight and blocked, two more queued behind them.
        var hang = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var worker = new ThumbnailWorker(
            (_, token) =>
            {
                started.Release();
                return hang.Task.WaitAsync(token).GetAwaiter().GetResult();
            },
            (_, _) => null,
            action => action());

        var completed = new SemaphoreSlim(0);
        var results = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var handles = new List<IDisposable>();
        for (var i = 0; i < 4; i++)
        {
            handles.Add(worker.RequestFrame($"frame{i}.fits", result =>
            {
                results.Enqueue(result);
                completed.Release();
            }));
        }
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(App.DrainForShutdown(watcher, scheduler, coordinator, worker));

        // Awaited with a bounded timeout, never read immediately after a call that returns
        // without yielding (FIXER item 3, the shape the scan drain's assertion already uses).
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await completed.WaitAsync(TimeSpan.FromSeconds(30)), $"only {results.Count} requests completed");
        }
        Assert.Equal(4, results.Count);
        Assert.All(results, Assert.Null);

        foreach (var handle in handles)
        {
            handle.Dispose();
        }
        Assert.NotNull(scheduler.LoopTask);
        await scheduler.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // Spec 10.5 gives the whole shutdown five seconds, not five per subsystem. The worker is
    // cancelled without being waited on, the scan drains against the budget, and the worker's
    // pumps are joined with whatever is left; before this pass a stalled render cost its own
    // five seconds on top of the scan's.
    [Fact]
    public async Task DrainForShutdown_StalledRender_SharesTheScansBudgetRatherThanAddingToIt()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root], WatcherEnabled = false });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var (watcher, scheduler) = CreateTriggers(settings, coordinator);

        // A render that ignores its cancellation token: the pump cannot leave until it returns,
        // which is the only case where the join budget is what bounds the drain.
        var stalled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var worker = new ThumbnailWorker(
            (_, _) =>
            {
                started.Release();
                return stalled.Task.GetAwaiter().GetResult();
            },
            (_, _) => null,
            action => action());

        var completed = new SemaphoreSlim(0);
        var results = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var handles = new List<IDisposable>();
        for (var i = 0; i < 3; i++)
        {
            handles.Add(worker.RequestFrame($"frame{i}.fits", result =>
            {
                results.Enqueue(result);
                completed.Release();
            }));
        }
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));

        var budget = TimeSpan.FromMilliseconds(400);
        var clock = Stopwatch.StartNew();
        var idle = App.DrainForShutdown(watcher, scheduler, coordinator, worker, budget);
        clock.Stop();

        Assert.True(idle);
        Assert.True(
            clock.Elapsed < ThumbnailWorker.DisposeDrainTimeout,
            $"the drain took {clock.Elapsed.TotalSeconds:F1}s, which is the worker's own join budget on top of the scan's");

        // Cancelled rather than abandoned: every request is completed as "not rendered" even
        // though the render itself is still stuck.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await completed.WaitAsync(TimeSpan.FromSeconds(30)), $"only {results.Count} requests completed");
        }
        Assert.All(results, Assert.Null);

        stalled.TrySetResult(null);
        foreach (var handle in handles)
        {
            handle.Dispose();
        }
        Assert.NotNull(scheduler.LoopTask);
        await scheduler.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // The other end of the same arithmetic: a scan that used the whole budget leaves nothing to
    // join with, and the drain must then not wait for the worker at all.
    [Fact]
    public async Task DrainForShutdown_BudgetAlreadySpentByTheScan_DoesNotWaitForTheWorker()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root], WatcherEnabled = false });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var (watcher, scheduler) = CreateTriggers(settings, coordinator);

        var stalled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var worker = new ThumbnailWorker(
            (_, _) =>
            {
                started.Release();
                return stalled.Task.GetAwaiter().GetResult();
            },
            (_, _) => null,
            action => action());
        using var request = worker.RequestFrame("frame.fits", _ => { });
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)));

        var clock = Stopwatch.StartNew();
        App.DrainForShutdown(watcher, scheduler, coordinator, worker, TimeSpan.Zero);
        clock.Stop();

        Assert.True(
            clock.Elapsed < ThumbnailWorker.DisposeDrainTimeout,
            $"a spent budget still waited {clock.Elapsed.TotalSeconds:F1}s for the worker");
        Assert.Equal(0, worker.OutstandingCount);

        stalled.TrySetResult(null);
        Assert.NotNull(scheduler.LoopTask);
        await scheduler.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---- Phase 11 Task 2: spec 12.11 behaviour 7, one drain and one exit --------------------
    //
    // The six cases above are untouched (TRACKING section 6 item 3 makes one of them a watch
    // item). These three are about the exit path around the drain rather than the drain itself.

    [Fact]
    public void ShutdownRequested_MarksTheResidencyServiceExitingBeforeTheDrainRuns()
    {
        // Spec 12.11 behaviour 5: the application's own shutdown is never converted into a hide.
        // Avalonia documents that a ShutdownRequested which is not cancelled proceeds to close
        // every non-owned window, so if the flag were set after the drain those closes would be
        // converted back into hides and the process would never exit.
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root], WatcherEnabled = false });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var (watcher, scheduler) = CreateTriggers(settings, coordinator);

        WindowResidencyService? residency = null;
        bool? exitingWhenTheDrainRan = null;
        residency = new WindowResidencyService(
            settings.Store.GetGeneral,
            post: action => action(),
            requestShutdown: () =>
            {
                // The handler App.axaml.cs installs, in the order it installs it.
                residency!.OnShutdownRequested();
                exitingWhenTheDrainRan = residency.IsExiting;
                App.DrainForShutdown(watcher, scheduler, coordinator);
                return true;
            });

        residency.RequestExit();

        Assert.True(exitingWhenTheDrainRan);
    }

    [Fact]
    public void ExitFromTheTray_RunsTheDrainExactlyOnce()
    {
        // Spec 12.11 behaviour 7: "there is one drain and one exit". A second Exit while the drain
        // runs must do nothing, which is what makes a tray menu press safe to repeat.
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root], WatcherEnabled = false });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var (watcher, scheduler) = CreateTriggers(settings, coordinator);

        WindowResidencyService? residency = null;
        var drains = 0;
        residency = new WindowResidencyService(
            settings.Store.GetGeneral,
            post: action => action(),
            requestShutdown: () =>
            {
                residency!.OnShutdownRequested();
                drains++;
                App.DrainForShutdown(watcher, scheduler, coordinator);
                return true;
            });

        // Declared before the view-model it feeds and disposed by the using, so a failing
        // assertion cannot leak this subscription to the coordinator into the rest of the class
        // (review minor finding 4).
        using var status = new ScanStatusService(coordinator, action => action());
        using var statusBar = new StatusBarViewModel(status, coordinator.Cancel);
        using var tray = new TrayIconViewModel(statusBar, residency, post: action => action());

        tray.ExitCommand.Execute(null);
        tray.ExitCommand.Execute(null);

        Assert.Equal(1, drains);
        Assert.True(residency.IsExiting);
    }

    [Fact]
    public void DrainForShutdown_HasExactlyOneCallSiteInSrc()
    {
        // Design-lessons rule 2: "one drain" is made structural rather than conventional.
        //
        // The file census alone was not that rule (phase review finding P10): it pinned the drain
        // only to the granularity of "inside App.axaml.cs", so a second DrainForShutdown( added
        // anywhere in that file passed it. Spec 12.11 behaviour 7 says one drain, so the count
        // inside the file is asserted too.
        Assert.Equal(
            ["App.axaml.cs"],
            SourceScan.FilesMatching(@"DrainForShutdown\s*\("));

        var app = SourceScan.StripComments(File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "App.axaml.cs")));

        // The call shape, which is the rule itself: the ShutdownRequested handler resolves every
        // argument from the container and nothing else in src calls the drain at all.
        Assert.Single(Regex.Matches(app, @"DrainForShutdown\(\s*services\."));

        // Four occurrences in total: two overload declarations, the delegation from the budget-less
        // one to the other, and the one call above. A fifth is a second drain or a second overload,
        // and either is a design decision rather than a convenience.
        Assert.Equal(4, Regex.Matches(app, @"DrainForShutdown\s*\(").Count);
    }

    private static (WatcherService Watcher, ScanScheduler Scheduler) CreateTriggers(
        SettingsFixture settings, ScanCoordinator coordinator)
    {
        var delay = new FakeDelay();
        var watcher = new WatcherService(
            settings.Store,
            (files, token) => coordinator.RunTargetedAsync(ScanTrigger.Watcher, files, token),
            token => coordinator.RunAsync(ScanTrigger.Watcher, null, token),
            NullLogger<WatcherService>.Instance,
            path => new FakeWatcherSource(path),
            delay.Delay);
        var scheduler = new ScanScheduler(
            settings.Store, () => coordinator.IsRunning,
            token => coordinator.RunAsync(ScanTrigger.Scheduler, null, token),
            NullLogger<ScanScheduler>.Instance, delay.Delay);
        watcher.Start();
        scheduler.Start();
        return (watcher, scheduler);
    }
}
