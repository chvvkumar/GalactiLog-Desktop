using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Test doubles shared with ScanSchedulerTests. They live here rather than in a third file
// because this task is allowed to create exactly two test files.

// A real migrated SQLite database plus a real SettingsStore over it. SettingsStore is sealed
// with non-virtual members by design, so there is nothing to stub: the cheapest honest
// substitute is the real thing over a temp file. Also owns a temp scan-root directory, since
// the stability gate has to look at real bytes on disk.
internal sealed class SettingsFixture : IDisposable
{
    // F8: one App.Tests temp-database type, which also drops this fixture's ClearAllPools.
    private readonly TempDatabase _database = new("galactilog-watcher");

    public SettingsStore Store { get; }
    public string Root { get; }

    /// <summary>
    /// A second scan root, disjoint from <see cref="Root"/> rather than a subdirectory of it.
    /// </summary>
    /// <remarks>
    /// FIXER LIST F10 raised the duplicate-or-nested scan-root rule into
    /// <c>ScanFilterConfig.Validate</c>, so a stored root set that nests is refused by the store.
    /// These cases only ever needed two roots, never two nested ones.
    /// </remarks>
    public string SecondRoot { get; }

    // AppShutdownTests builds a real ScanCoordinator over the same database.
    public string ConnectionString => _database.ConnectionString;

    public SettingsFixture()
    {
        Store = new SettingsStore(new SettingsRepository(ConnectionString));
        Root = Directory.CreateTempSubdirectory("galactilog-watcher-").FullName;
        SecondRoot = Directory.CreateTempSubdirectory("galactilog-watcher2-").FullName;
    }

    public void Save(Func<GeneralSettings, GeneralSettings> mutate) => Store.SaveGeneral(mutate(Store.GetGeneral()));

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        if (Directory.Exists(SecondRoot)) Directory.Delete(SecondRoot, recursive: true);
    }
}

internal sealed class FakeWatcherSource(string path) : IFileSystemWatcherSource
{
    public string Path { get; } = path;
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public bool ThrowOnStart { get; set; }

    public event EventHandler<string>? Changed;
    public event EventHandler? Error;

    public void Start()
    {
        // What a real FileSystemWatcher does when its root has gone away between construction
        // and EnableRaisingEvents.
        if (ThrowOnStart) throw new IOException("watcher root unavailable");
        Started = true;
    }
    public void Dispose() => Disposed = true;

    public void RaiseChanged(string fullPath) => Changed?.Invoke(this, fullPath);
    public void RaiseError() => Error?.Invoke(this, EventArgs.Empty);
}

// The injected delay. Every wait is recorded and parked until the test releases it, so a
// debounce window, a stability window and a scheduler interval are all things the test opens
// and closes explicitly instead of sleeping for.
internal sealed class FakeDelay
{
    private readonly Lock _gate = new();
    private readonly List<TaskCompletionSource> _waiters = [];
    private readonly List<TimeSpan> _requested = [];

    // Runs at the moment a wait of the given duration is requested, i.e. after whatever the
    // service did just before it. That is how the stability tests mutate a file strictly
    // between the gate's two length reads.
    public Action<TimeSpan>? OnWait { get; set; }

    public IReadOnlyList<TimeSpan> Requested
    {
        get { lock (_gate) { return [.. _requested]; } }
    }

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
    {
        OnWait?.Invoke(duration);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _requested.Add(duration);
            _waiters.Add(tcs);
        }

        cancellationToken.Register(() => tcs.TrySetCanceled());
        if (cancellationToken.IsCancellationRequested) tcs.TrySetCanceled();
        return tcs.Task;
    }

    public void Release()
    {
        TaskCompletionSource[] pending;
        lock (_gate)
        {
            pending = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in pending) waiter.TrySetResult();
    }

    // Releases every parked wait, repeatedly, until the piece of work under test finishes.
    // A debounce fire opens two waits in sequence (debounce, then stability), so one Release
    // is not enough.
    public async Task DrainAsync(Task? work)
    {
        if (work is null) return;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!work.IsCompleted && DateTime.UtcNow < deadline)
        {
            Release();
            await Task.Delay(2);
        }

        Assert.True(work.IsCompleted, "The watcher's pending work never completed.");
        await work;
    }

    public async Task WaitForRequestCountAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Requested.Count < count && DateTime.UtcNow < deadline) await Task.Delay(2);
        Assert.True(Requested.Count >= count, $"Expected at least {count} delay requests, saw {Requested.Count}.");
    }
}

public class WatcherServiceTests
{
    private const int DebounceMs = 10;
    private const int StabilityMs = 40;

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(DebounceMs);
    private static readonly TimeSpan StabilityWindow = TimeSpan.FromMilliseconds(StabilityMs);

    private sealed class Harness : IDisposable
    {
        public SettingsFixture Settings { get; }
        public FakeDelay Delay { get; } = new();
        public List<FakeWatcherSource> Sources { get; } = [];
        public List<IReadOnlyList<string>> TargetedIngests { get; } = [];
        public int FullScans;

        // Substring of a scan root whose source should fail in Start(). Set before Start().
        public string? ThrowOnStartFor { get; set; }

        // Runs inside Start()'s source loop, on the thread doing the start. Lets a test observe
        // which thread a restart ran on, and hold a restart open while it asserts that a
        // GeneralChanged raised meanwhile does not queue behind it.
        public Action? OnSourceCreated { get; set; }

        public WatcherService Service { get; }

        public Harness(SettingsFixture settings)
        {
            Settings = settings;
            Service = new WatcherService(
                settings.Store,
                (files, _) => { TargetedIngests.Add(files); return Task.CompletedTask; },
                _ => { Interlocked.Increment(ref FullScans); return Task.CompletedTask; },
                NullLogger<WatcherService>.Instance,
                path =>
                {
                    var source = new FakeWatcherSource(path)
                    {
                        ThrowOnStart = ThrowOnStartFor is not null && path.Contains(ThrowOnStartFor),
                    };
                    lock (Sources)
                    {
                        Sources.Add(source);
                    }

                    OnSourceCreated?.Invoke();
                    return source;
                },
                Delay.Delay);
        }

        public void Dispose()
        {
            Service.Stop();
            Settings.Dispose();
        }
    }

    // Default configuration: watcher on, one scan root, short distinguishable windows.
    private static Harness CreateHarness(Func<GeneralSettings, GeneralSettings>? mutate = null)
    {
        var settings = new SettingsFixture();
        settings.Save(general =>
        {
            var configured = general with
            {
                ScanRoots = [settings.Root],
                WatcherEnabled = true,
                WatcherDebounceMs = DebounceMs,
                WatcherStabilityCheckMs = StabilityMs,
            };
            return mutate is null ? configured : mutate(configured);
        });
        return new Harness(settings);
    }

    private static string WriteFrame(string directory, string name, int bytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task Changed_MultipleEventsWithinDebounceWindow_CoalesceIntoOneTargetedScan()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        var paths = Enumerable.Range(0, 5)
            .Select(i => WriteFrame(harness.Settings.Root, $"frame{i}.fits", 64))
            .ToList();

        foreach (var path in paths) harness.Sources[0].RaiseChanged(path);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        var ingest = Assert.Single(harness.TargetedIngests);
        Assert.Equal(paths.OrderBy(p => p), ingest.OrderBy(p => p));
        Assert.Equal(0, harness.FullScans);
        // The configured windows are what was actually waited on, not hardcoded constants.
        Assert.Contains(DebounceWindow, harness.Delay.Requested);
        Assert.Contains(StabilityWindow, harness.Delay.Requested);
    }

    [Fact]
    public async Task Changed_EventsAcrossTwoDebounceWindows_ProduceTwoScans()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        var first = WriteFrame(harness.Settings.Root, "first.fits", 64);
        var second = WriteFrame(harness.Settings.Root, "second.fits", 64);

        harness.Sources[0].RaiseChanged(first);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);
        harness.Sources[0].RaiseChanged(second);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.Equal(2, harness.TargetedIngests.Count);
        Assert.Equal(first, Assert.Single(harness.TargetedIngests[0]));
        Assert.Equal(second, Assert.Single(harness.TargetedIngests[1]));
    }

    [Fact]
    public async Task StabilityGate_GrowingFile_NotIngestedUntilSizeStable()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        var path = WriteFrame(harness.Settings.Root, "growing.fits", 64);

        // Appending strictly between the gate's two length reads is what a half-written
        // N.I.N.A. frame looks like.
        harness.Delay.OnWait = duration =>
        {
            if (duration == StabilityWindow) File.AppendAllText(path, new string('x', 128));
        };
        harness.Sources[0].RaiseChanged(path);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.Empty(harness.TargetedIngests);

        // Same file, no longer growing: now it is ingested.
        harness.Delay.OnWait = null;
        harness.Sources[0].RaiseChanged(path);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.Equal(path, Assert.Single(Assert.Single(harness.TargetedIngests)));
    }

    [Fact]
    public async Task StabilityGate_FileGoneBetweenChecks_TreatedAsUnstable_NotIngested()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        var vanishing = WriteFrame(harness.Settings.Root, "vanishing.fits", 64);
        var survivor = WriteFrame(harness.Settings.Root, "survivor.fits", 64);

        harness.Delay.OnWait = duration =>
        {
            if (duration == StabilityWindow) File.Delete(vanishing);
        };
        harness.Sources[0].RaiseChanged(vanishing);
        harness.Sources[0].RaiseChanged(survivor);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        // The vanished path is dropped without taking the rest of the batch with it.
        Assert.Equal(survivor, Assert.Single(Assert.Single(harness.TargetedIngests)));
    }

    [Fact]
    public async Task StabilityGate_ExclusivelyLockedFile_NotIngested()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        var locked = WriteFrame(harness.Settings.Root, "locked.fits", 64);
        var readable = WriteFrame(harness.Settings.Root, "readable.fits", 64);

        // The gate opens each candidate through UserFiles.OpenRead (FileShare.ReadWrite), so a
        // FileShare.None holder -- a writer that has not finished -- makes that open fail. A
        // directory-entry stat would have reported a stable size and let the file through.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            harness.Sources[0].RaiseChanged(locked);
            harness.Sources[0].RaiseChanged(readable);
            await harness.Delay.DrainAsync(harness.Service.PendingWork);
        }

        Assert.Equal(readable, Assert.Single(Assert.Single(harness.TargetedIngests)));

        // Released: the next notification for it does ingest.
        harness.Sources[0].RaiseChanged(locked);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.Equal(2, harness.TargetedIngests.Count);
        Assert.Equal(locked, Assert.Single(harness.TargetedIngests[1]));
    }

    [Fact]
    public async Task Start_SourceThatFailsToStart_IsDisposedAndDoesNotBlockOtherRoots()
    {
        using var harness = CreateHarness(general => general with
        {
            ScanRoots = [general.ScanRoots[0] + "-unstartable", general.ScanRoots[0]],
        });
        harness.ThrowOnStartFor = "unstartable";

        harness.Service.Start();

        Assert.Equal(2, harness.Sources.Count);
        Assert.True(harness.Sources[0].Disposed, "A source that failed to start must be disposed.");
        Assert.False(harness.Sources[1].Disposed);

        // The failed source's subscriptions were removed with it, so its events are inert.
        var path = WriteFrame(harness.Settings.Root, "after-failure.fits", 64);
        harness.Sources[0].RaiseChanged(path);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.Empty(harness.TargetedIngests);
    }

    [Fact]
    public async Task Error_TriggersFullScan_NotTargeted()
    {
        using var harness = CreateHarness();
        harness.Service.Start();

        harness.Sources[0].RaiseError();
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.Equal(1, harness.FullScans);
        Assert.Empty(harness.TargetedIngests);
    }

    [Fact]
    public async Task Changed_PathExcludedByScanFilterConfig_NeverReachesCoordinator()
    {
        var excludedDirectory = string.Empty;
        using var harness = CreateHarness(general =>
        {
            excludedDirectory = Path.Combine(general.ScanRoots[0], "skip");
            return general with { ScanFilters = general.ScanFilters with { ExcludePaths = [excludedDirectory] } };
        });
        harness.Service.Start();
        var excluded = WriteFrame(excludedDirectory, "ignored.fits", 64);

        harness.Sources[0].RaiseChanged(excluded);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.Empty(harness.TargetedIngests);
        // Dropped before the stability gate, so no stability wait was ever opened for it.
        Assert.DoesNotContain(StabilityWindow, harness.Delay.Requested);
    }

    [Fact]
    public async Task Changed_UnsupportedExtension_NeverReachesCoordinator()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        var sidecar = WriteFrame(harness.Settings.Root, "session.log.txt", 64);

        harness.Sources[0].RaiseChanged(sidecar);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.Empty(harness.TargetedIngests);
    }

    [Fact]
    public async Task Changed_PathOutsideEveryScanRoot_NeverReachesCoordinator()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        var outside = WriteFrame(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.fits", 64);
        try
        {
            harness.Sources[0].RaiseChanged(outside);
            await harness.Delay.DrainAsync(harness.Service.PendingWork);

            Assert.Empty(harness.TargetedIngests);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Start_WatcherEnabledFalse_CreatesNoWatcherSources()
    {
        using var harness = CreateHarness(general => general with { WatcherEnabled = false });

        harness.Service.Start();

        Assert.Empty(harness.Sources);
    }

    [Fact]
    public void Start_OneSourcePerConfiguredRoot()
    {
        using var harness = CreateHarness(general => general with
        {
            ScanRoots = [general.ScanRoots[0], general.ScanRoots[0] + "-second"],
        });

        harness.Service.Start();

        Assert.Equal(2, harness.Sources.Count);
        Assert.All(harness.Sources, source => Assert.True(source.Started));
    }

    [Fact]
    public void Start_CalledTwice_DoesNotDoubleSubscribe()
    {
        using var harness = CreateHarness();

        harness.Service.Start();
        harness.Service.Start();

        Assert.Single(harness.Sources);
    }

    [Fact]
    public async Task Stop_DisposesEverySourceAndIgnoresLaterEvents()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        var path = WriteFrame(harness.Settings.Root, "after-stop.fits", 64);
        var source = harness.Sources[0];

        harness.Service.Stop();
        source.RaiseChanged(path);
        await harness.Delay.DrainAsync(harness.Service.PendingWork);

        Assert.True(source.Disposed);
        Assert.Empty(harness.TargetedIngests);
    }

    // ---- Restart and the settings subscription (Phase 9 Task 5, questions.md Q36) -------------
    //
    // WatcherService bound its root set once at Start() and had no GeneralChanged subscription.
    // Nothing before Phase 9 could write general.scan_roots or general.watcher_enabled from
    // inside the running process; the Settings Library tab and the setup wizard both can, and
    // without this the watcher watches the wrong set until the application is restarted.

    [Fact]
    public void Restart_PicksUpANewRootSet()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        Assert.Single(harness.Sources);
        var original = harness.Sources[0];

        var second = harness.Settings.SecondRoot;
        harness.Settings.Save(general => general with { ScanRoots = [harness.Settings.Root, second] });
        harness.Service.Restart();

        Assert.True(original.Disposed, "The source for the old root set must be disposed.");
        Assert.Equal(3, harness.Sources.Count);
        Assert.Equal([harness.Settings.Root, second], harness.Sources.Skip(1).Select(source => source.Path));
        Assert.All(harness.Sources.Skip(1), source => Assert.True(source.Started));
    }

    [Fact]
    public void Restart_StopsWatchingARemovedRoot()
    {
        var removed = string.Empty;
        using var harness = CreateHarness(general =>
        {
            removed = general.ScanRoots[0] + "-removed";
            return general with { ScanRoots = [general.ScanRoots[0], removed] };
        });
        harness.Service.Start();
        Assert.Equal(2, harness.Sources.Count);

        harness.Settings.Save(general => general with { ScanRoots = [harness.Settings.Root] });
        harness.Service.Restart();

        Assert.All(harness.Sources.Take(2), source => Assert.True(source.Disposed));
        Assert.Equal(harness.Settings.Root, Assert.Single(harness.Sources.Skip(2)).Path);
        Assert.DoesNotContain(harness.Sources.Skip(2), source => source.Path == removed);
    }

    [Fact]
    public void Restart_WhenNotRunning_IsSafe()
    {
        using var harness = CreateHarness();

        // Never started: Stop is a no-op and Start does what a first start would.
        harness.Service.Restart();
        Assert.Single(harness.Sources);

        // Stopped for good, then restarted again: still one source per configured root, and no
        // exception from either the stop or the start.
        harness.Service.Stop();
        harness.Service.Restart();
        Assert.Equal(2, harness.Sources.Count);
        Assert.True(harness.Sources[1].Started);
    }

    [Fact]
    public void Restart_WithWatcherDisabled_WatchesNothing()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        Assert.Single(harness.Sources);

        harness.Settings.Save(general => general with { WatcherEnabled = false });
        harness.Service.Restart();

        Assert.True(harness.Sources[0].Disposed);
        Assert.Single(harness.Sources);
    }

    [Fact]
    public void SavingAnUnrelatedGeneralKey_DoesNotRestartTheWatcher()
    {
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges(work => work());
        harness.Service.Start();
        var original = harness.Sources[0];

        // A theme or a log level change must not tear down and rebuild every FileSystemWatcher.
        harness.Settings.Save(general => general with { LogLevel = "Debug", Theme = "deep-sky" });

        Assert.Single(harness.Sources);
        Assert.False(original.Disposed);
    }

    [Fact]
    public void SavingANewRootSet_RestartsTheWatcher()
    {
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges(work => work());
        harness.Service.Start();
        var original = harness.Sources[0];

        var second = harness.Settings.SecondRoot;
        harness.Settings.Save(general => general with { ScanRoots = [harness.Settings.Root, second] });

        Assert.True(original.Disposed);
        Assert.Equal(3, harness.Sources.Count);
        Assert.Equal([harness.Settings.Root, second], harness.Sources.Skip(1).Select(source => source.Path));
    }

    [Fact]
    public void SavingWatcherEnabledFalse_StopsTheWatcher()
    {
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges(work => work());
        harness.Service.Start();

        harness.Settings.Save(general => general with { WatcherEnabled = false });

        Assert.True(harness.Sources[0].Disposed);
        Assert.Single(harness.Sources);

        // And back on again, which is the change a disabled watcher has to notice.
        harness.Settings.Save(general => general with { WatcherEnabled = true });

        Assert.Equal(2, harness.Sources.Count);
        Assert.True(harness.Sources[1].Started);
    }

    [Fact]
    public void ASettingsChangeBeforeStart_DoesNotStartTheWatcher()
    {
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges(work => work());

        // Spec 15: the CLI never starts the watcher. A settings write in a process that never
        // called Start must not start one.
        harness.Settings.Save(general => general with { ScanRoots = [harness.Settings.SecondRoot] });

        Assert.Empty(harness.Sources);
    }

    [Fact]
    public async Task FollowSettingsChanges_WithTheDefaultRunner_RestartsOffTheSavingThread()
    {
        // Ruling Q36 has two clauses. The comparison policy is asserted by the cases above with an
        // inline runner; this is the second clause, the default Task.Run runner, which is what
        // keeps OS-handle construction off the thread that saved (from Phase 9's Settings tabs and
        // the setup wizard, the UI thread).
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges();
        harness.Service.Start();

        var restarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnSourceCreated = () => restarted.TrySetResult(Environment.CurrentManagedThreadId);
        var savingThread = Environment.CurrentManagedThreadId;

        harness.Settings.Save(general => general with
        {
            ScanRoots = [harness.Settings.Root, harness.Settings.SecondRoot],
        });

        var restartThread = await restarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(savingThread, restartThread);
    }

    [Fact]
    public async Task AGeneralChangedRaisedDuringARestart_DoesNotWaitForIt()
    {
        // Review finding I2. Restart holds the watcher gate across a SQLite read and one
        // FileSystemWatcher construction per root; the settings handler must be able to answer
        // "nothing I care about changed" without queueing behind that.
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges(work => work());
        harness.Service.Start();

        using var insideStart = new ManualResetEventSlim(false);
        using var releaseStart = new ManualResetEventSlim(false);
        harness.OnSourceCreated = () =>
        {
            insideStart.Set();
            releaseStart.Wait(TimeSpan.FromSeconds(30));
        };

        var restart = Task.Run(harness.Service.Restart);
        Assert.True(insideStart.Wait(TimeSpan.FromSeconds(30)), "The restart never reached the source loop.");

        // An unrelated save, raised while that restart is parked mid-rebuild.
        var save = Task.Run(() => harness.Settings.Save(general => general with { LogLevel = "Debug" }));
        var finished = await Task.WhenAny(save, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(save, finished);
        await save;

        releaseStart.Set();
        await restart;
    }

    [Fact]
    public void StopFollowing_DetachesTheSubscription()
    {
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges(work => work());
        harness.Service.Start();

        harness.Service.StopFollowing();
        harness.Settings.Save(general => general with
        {
            ScanRoots = [harness.Settings.Root, harness.Settings.SecondRoot],
        });

        Assert.Single(harness.Sources);
        Assert.False(harness.Sources[0].Disposed);

        // Idempotent, and re-subscribable. The saved root set is compared against what the
        // running watcher is bound to, which is still the single root Start() read, so saving
        // the two-root set again is a change even though the stored document already holds it.
        harness.Service.StopFollowing();
        harness.Service.FollowSettingsChanges(work => work());
        harness.Settings.Save(general => general with
        {
            ScanRoots = [harness.Settings.Root, harness.Settings.SecondRoot],
        });

        Assert.Equal(3, harness.Sources.Count);
        Assert.True(harness.Sources[0].Disposed);
    }

    [Fact]
    public void Dispose_DetachesTheSubscriptionAndStopsTheSources()
    {
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges(work => work());
        harness.Service.Start();

        harness.Service.Dispose();

        Assert.True(harness.Sources[0].Disposed);

        harness.Settings.Save(general => general with
        {
            ScanRoots = [harness.Settings.Root, harness.Settings.SecondRoot],
        });

        Assert.Single(harness.Sources);
    }

    [Fact]
    public void FollowSettingsChanges_CalledTwice_SubscribesOnce()
    {
        using var harness = CreateHarness();
        harness.Service.FollowSettingsChanges(work => work());
        harness.Service.FollowSettingsChanges(work => work());
        harness.Service.Start();

        harness.Settings.Save(general => general with { ScanRoots = [harness.Settings.SecondRoot] });

        // One restart, not two: 1 original plus 1 new source.
        Assert.Equal(2, harness.Sources.Count);
    }

    // ---- Phase 10 Task 1: spec 12.8's Scan group watcher state --------------------------------

    [Fact]
    public void DescribeRoots_BeforeStart_ReportsTheConfiguredRootsAsNotWatching()
    {
        using var harness = CreateHarness();

        var rows = harness.Service.DescribeRoots();

        // No binding yet, so the list comes from general.scan_roots. This is the shape a
        // CLI-built host reports: the CLI never starts the watcher (spec 15).
        var row = Assert.Single(rows);
        Assert.Equal(harness.Settings.Root, row.Root);
        Assert.False(row.Watching);
        Assert.True(row.Reachable);
        Assert.Empty(harness.Sources);
    }

    [Fact]
    public void DescribeRoots_AfterStart_ReportsEachBoundRootAsWatching()
    {
        using var harness = CreateHarness();
        harness.Settings.Save(general => general with
        {
            ScanRoots = [harness.Settings.Root, harness.Settings.SecondRoot],
        });
        harness.Service.Start();

        var rows = harness.Service.DescribeRoots();

        // Configured order, not sorted.
        Assert.Equal(
            new[] { harness.Settings.Root, harness.Settings.SecondRoot },
            rows.Select(row => row.Root));
        Assert.All(rows, row => Assert.True(row.Watching));
        Assert.All(rows, row => Assert.True(row.Reachable));
    }

    [Fact]
    public void DescribeRoots_AfterStop_ReportsNotWatching()
    {
        using var harness = CreateHarness();
        harness.Service.Start();
        Assert.True(harness.Service.DescribeRoots()[0].Watching);

        harness.Service.Stop();

        var row = Assert.Single(harness.Service.DescribeRoots());
        Assert.Equal(harness.Settings.Root, row.Root);
        Assert.False(row.Watching);
    }

    [Fact]
    public void DescribeRoots_WhenWatcherDisabled_ReportsEveryRootAsNotWatching()
    {
        using var harness = CreateHarness(general => general with { WatcherEnabled = false });
        harness.Service.Start();

        var rows = harness.Service.DescribeRoots();

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.False(row.Watching));
        Assert.Empty(harness.Sources);
    }

    [Fact]
    public void DescribeRoots_ReportsAMissingRootAsUnreachable()
    {
        using var harness = CreateHarness();
        var missing = Path.Combine(harness.Settings.Root, "gone", "nowhere");
        harness.Settings.Save(general => general with { ScanRoots = [missing] });

        var row = Assert.Single(harness.Service.DescribeRoots());

        Assert.Equal(missing, row.Root);
        Assert.False(row.Reachable);
    }

    [Fact]
    public async Task DescribeRoots_DoesNotBlock_WhileARestartHoldsTheGate()
    {
        // Review finding I2, the same rule OnGeneralChanged is held to: Restart holds the watcher
        // gate across a SQLite read and one FileSystemWatcher construction per root, and a
        // diagnostics refresh must be able to read the root state without queueing behind that.
        using var harness = CreateHarness();
        harness.Service.Start();

        using var insideStart = new ManualResetEventSlim(false);
        using var releaseStart = new ManualResetEventSlim(false);
        harness.OnSourceCreated = () =>
        {
            insideStart.Set();
            releaseStart.Wait(TimeSpan.FromSeconds(30));
        };

        var restart = Task.Run(harness.Service.Restart);
        Assert.True(insideStart.Wait(TimeSpan.FromSeconds(30)), "The restart never reached the source loop.");

        var describe = Task.Run(() => harness.Service.DescribeRoots());
        var finished = await Task.WhenAny(describe, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(describe, finished);
        Assert.NotEmpty(await describe);

        releaseStart.Set();
        await restart;
    }
}
