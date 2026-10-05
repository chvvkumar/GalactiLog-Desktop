using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.Services;

/// <summary>One configured scan root as spec 12.8's Scan group reports it.</summary>
/// <param name="Root">The configured root, in <c>general.scan_roots</c> order.</param>
/// <param name="Watching">Whether a watcher is currently attached to it.</param>
/// <param name="Reachable">Whether the directory exists right now. False for an unplugged
/// external drive or an unavailable network share.</param>
public sealed record WatcherRootState(string Root, bool Watching, bool Reachable);

// One FileSystemWatcher per scan root, debounced, size-stability gated, feeding a targeted
// ingest (design-spec 10.7). Never started by the CLI (spec 15): AppHost only constructs it,
// App.axaml.cs starts it, and that code path runs for the GUI alone.
//
// ingestTargeted / runFullScan are bound in AppHost to ScanCoordinator.RunTargetedAsync and
// ScanCoordinator.RunAsync. They are delegates rather than a coordinator reference so this
// service can be tested without standing up a migrated database, a TargetResolver and two
// HTTP clients; the production path is still the real coordinator.
public sealed class WatcherService(
    SettingsStore settingsStore,
    Func<IReadOnlyList<string>, CancellationToken, Task> ingestTargeted,
    Func<CancellationToken, Task> runFullScan,
    ILogger<WatcherService> logger,
    Func<string, IFileSystemWatcherSource>? sourceFactory = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly Func<string, IFileSystemWatcherSource> _sourceFactory =
        sourceFactory ?? (path => new FileSystemWatcherSource(path));
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IFileSystemWatcherSource> _sources = [];

    // Every raw notification bumps the generation. A debounce task only proceeds if its own
    // generation is still the newest when its wait ends, which is what makes the window
    // sliding: a burst of N events leaves exactly one task doing work. A generation counter
    // rather than a per-event linked CancellationTokenSource because the latter would either
    // leak a registration on the lifetime token per event or risk disposing a token another
    // task is still awaiting.
    private long _generation;
    private CancellationTokenSource? _lifetimeCts;

    // What the running watcher was built from (questions.md Q36). Null until Start() has been
    // called and again after Stop(), which is what keeps a settings change from starting a
    // watcher the CLI never started (design-spec 15).
    //
    // Review finding I2: a volatile reference to an immutable snapshot, deliberately NOT guarded
    // by _gate. Restart holds _gate across a SQLite read and one FileSystemWatcher construction
    // per root, and OnGeneralChanged has to be able to answer "nothing I care about changed" while
    // that is in flight. Reading it under _gate made every GeneralChanged subscriber wait out an
    // in-flight rebuild on the thread that saved, which from Task 6's tabs and Task 9's wizard is
    // the UI thread. Only the restart itself serialises now.
    private volatile Binding? _binding;

    // How the settings subscription gets off the raising thread. Written before the event handler
    // is attached and cleared after it is detached, and read without a lock for the same reason
    // _binding is.
    private volatile Action<Action>? _runRestart;

    // 0 or 1, through Interlocked, so FollowSettingsChanges and StopFollowing are each idempotent
    // without taking _gate.
    private int _following;

    // The root set and enabled flag a running watcher is bound to. Immutable, so a reader that
    // takes no lock still sees one whole snapshot rather than a half-written pair. A class rather
    // than a record struct because a volatile field must be a reference.
    private sealed class Binding(bool enabled, string[] roots)
    {
        public bool Enabled { get; } = enabled;

        public string[] Roots { get; } = roots;
    }

    // Test seam only: lets a test await the debounce work it just triggered instead of
    // polling. Null until the first notification arrives.
    internal Task? PendingWork { get; private set; }

    public void Start()
    {
        GeneralSettings general;
        lock (_gate)
        {
            general = settingsStore.GetGeneral();

            // Recorded before the enabled check, not after it: turning watcher_enabled back on
            // has to read as a change, and it cannot if a disabled watcher remembers nothing.
            _binding = new Binding(general.WatcherEnabled, [.. general.ScanRoots]);

            // FIXER LIST F28. OnGeneralChanged takes no lock and returns when there is no
            // binding, so a save that commits between the read above and the publish above it is
            // dropped twice over: the handler saw no binding, and this method then publishes a
            // document written before that save. The watcher would stay on the previous root set
            // until the next general save.
            //
            // The window is closed by confirming against a second read. A write that completed
            // before this read is visible to it, so the only writes it can miss are ones that
            // commit after the binding is already published, and those are exactly the ones
            // OnGeneralChanged handles in the ordinary way. A write landing between the publish
            // and this read is handled twice, once here and once by the handler's restart; a
            // restart is idempotent, so that costs a rebuild and nothing else.
            //
            // Both reads and the publish are under the gate Restart already holds across its
            // Stop and Start, so no second Start can interleave with this pair.
            var confirmed = settingsStore.GetGeneral();
            if (confirmed.WatcherEnabled != general.WatcherEnabled
                || !RootsEqual(general.ScanRoots, confirmed.ScanRoots))
            {
                general = confirmed;
                _binding = new Binding(general.WatcherEnabled, [.. general.ScanRoots]);
            }
        }

        if (!general.WatcherEnabled)
        {
            logger.LogInformation("File system watcher disabled by general.watcher_enabled; not watching any scan root.");
            return;
        }

        lock (_gate)
        {
            if (_lifetimeCts is not null) return;
            _lifetimeCts = new CancellationTokenSource();
        }

        foreach (var root in general.ScanRoots)
        {
            IFileSystemWatcherSource? source = null;
            try
            {
                source = _sourceFactory(root);
                source.Changed += OnChanged;
                source.Error += OnError;
                source.Start();
                lock (_gate) { _sources.Add(source); }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // An unplugged external drive or a deleted root must not stop the other
                // roots from being watched, and must never be fatal to startup. A source that
                // was constructed but failed to start never reaches _sources, so Stop() will
                // not dispose it; unhook and dispose it here or its OS handle leaks.
                if (source is not null)
                {
                    source.Changed -= OnChanged;
                    source.Error -= OnError;
                    source.Dispose();
                }

                logger.LogWarning(ex, "Not watching scan root {ScanRoot}.", root);
            }
        }
    }

    public void Stop()
    {
        List<IFileSystemWatcherSource> sources;
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
            sources = [.. _sources];
            _sources.Clear();
            _pending.Clear();
        }

        _binding = null;

        // Cancelled outside the lock: a cancellation callback runs the waiting continuation
        // inline on this thread, and that continuation takes _gate. Cancelled, never disposed:
        // in-flight debounce tasks still hold this token, and disposing it underneath them
        // would turn an orderly cancellation into an ObjectDisposedException.
        //
        // Review finding I2, second half: Restart calls this with _gate already held, so at that
        // one call site the cancel does happen under the lock. It is safe because
        // System.Threading.Lock is reentrant and the inline continuation runs on this same
        // thread, but the "outside the lock" property above is a property of the direct callers,
        // not of every caller.
        lifetime?.Cancel();

        foreach (var source in sources)
        {
            source.Changed -= OnChanged;
            source.Error -= OnError;
            source.Dispose();
        }
    }

    /// <summary>
    /// Rebinds the watcher to the current <c>general.scan_roots</c> and
    /// <c>general.watcher_enabled</c>: <see cref="Stop"/> then <see cref="Start"/>
    /// (questions.md Q36). Idempotent and safe when nothing is running, in which case the stop
    /// is a no-op and the start does what a first start would.
    /// </summary>
    /// <remarks>
    /// Serialised on the same gate <see cref="Start"/> and <see cref="Stop"/> take, so two
    /// concurrent restarts cannot interleave into a half-built source list.
    /// <c>System.Threading.Lock</c> is reentrant, so the nested acquisitions inside those two
    /// cost nothing. Holding it across the pair is safe against <c>SettingsStore</c>'s own write
    /// gate, which <see cref="Start"/> takes through <c>GetGeneral</c>: <c>SaveGeneral</c>
    /// releases that gate before it raises <c>GeneralChanged</c>, so no thread ever holds the
    /// store's gate while waiting for this one.
    /// </remarks>
    public void Restart()
    {
        lock (_gate)
        {
            Stop();
            Start();
        }
    }

    /// <summary>One row per configured scan root: the root, whether a watcher is currently attached
    /// to it, and whether the directory is reachable right now (spec 12.8's Scan group).</summary>
    /// <remarks>
    /// <para>
    /// Takes no lock. <c>_binding</c> is read once into a local: it is a volatile reference to an
    /// immutable snapshot and review finding I2 says explicitly that it is deliberately not
    /// guarded by <c>_gate</c>. <see cref="Restart"/> holds that gate across a SQLite read and one
    /// <c>FileSystemWatcher</c> construction per root, and a diagnostics refresh must not park on
    /// it. A <c>lock (_gate)</c> added here is a defect, not a hardening.
    /// </para>
    /// <para>
    /// A null binding means <see cref="Start"/> was never called or <see cref="Stop"/> has had the
    /// last word. The root list then comes from <c>general.scan_roots</c> and nothing is watching.
    /// The CLI never starts the watcher (spec 15), so that is the shape a CLI-built host reports.
    /// </para>
    /// <para>
    /// <b>Blocking.</b> Reachability is a filesystem probe per root, so an unreachable network
    /// root can make this call take seconds. <c>DiagnosticsService.Snapshot</c> runs it off the UI
    /// thread and the view-model awaits that.
    /// </para>
    /// </remarks>
    public IReadOnlyList<WatcherRootState> DescribeRoots()
    {
        var binding = _binding;
        var roots = binding?.Roots ?? [.. settingsStore.GetGeneral().ScanRoots];
        var watched = binding is { Enabled: true }
            ? new HashSet<string>(binding.Roots, StringComparer.OrdinalIgnoreCase)
            : [];

        // Configured order, never sorted: the Settings Library tab lists the roots in this order
        // and the two screens must agree.
        return [.. roots.Select(root => new WatcherRootState(
            root,
            watched.Contains(root),
            UserFiles.DirectoryExists(root)))];
    }

    /// <summary>
    /// Subscribes this service to <c>SettingsStore.GeneralChanged</c> so a scan-root or
    /// watcher-enabled edit takes effect without an application restart (questions.md Q36).
    /// Called once, from <c>AppHost</c>, where the service is constructed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before Phase 9 nothing in the running process could write <c>general.scan_roots</c> or
    /// <c>general.watcher_enabled</c>, so binding the root set once at <see cref="Start"/> never
    /// mattered. The Settings Library tab and the setup wizard both write them, and without this
    /// a user who adds a root has no live ingest for it until the next application start.
    /// </para>
    /// <para>
    /// The comparison runs on the thread that raised the event, so an unrelated general save (a
    /// log level, a theme) costs a set comparison and nothing else: a restart tears down and
    /// rebuilds every <c>FileSystemWatcher</c> and its OS handles, which is not something to do
    /// on a keystroke. The restart itself is handed to <paramref name="runRestart"/>, whose
    /// default puts it on the thread pool, because <c>GeneralChanged</c> fires on whichever
    /// thread saved and that is usually the UI thread.
    /// </para>
    /// </remarks>
    /// <param name="runRestart">How to run the restart off the raising thread. Defaults to
    /// <c>Task.Run</c>. A test passes an inline runner so it can assert without waiting.</param>
    public void FollowSettingsChanges(Action<Action>? runRestart = null)
    {
        if (Interlocked.CompareExchange(ref _following, 1, 0) != 0)
        {
            return;
        }

        // Written before the handler is attached, so the handler can read it without a lock.
        _runRestart = runRestart ?? (work => _ = Task.Run(work));
        settingsStore.GeneralChanged += OnGeneralChanged;
    }

    /// <summary>
    /// Detaches the <c>GeneralChanged</c> handler <see cref="FollowSettingsChanges"/> attached.
    /// Idempotent, and a no-op when nothing is subscribed. Called by <see cref="Dispose"/>, which
    /// the host container runs at shutdown.
    /// </summary>
    public void StopFollowing()
    {
        if (Interlocked.Exchange(ref _following, 0) == 0)
        {
            return;
        }

        settingsStore.GeneralChanged -= OnGeneralChanged;
        _runRestart = null;
    }

    /// <summary>
    /// Detaches the settings subscription and stops every watcher source. Registered as a DI
    /// singleton, so the host container runs this at shutdown; the shutdown drain's explicit
    /// <see cref="Stop"/> still runs first and is unaffected, because both are idempotent.
    /// </summary>
    public void Dispose()
    {
        StopFollowing();
        Stop();
    }

    private void OnGeneralChanged(object? sender, GeneralSettings general)
    {
        // Review finding I2: no lock here at all. This runs on whichever thread called
        // SaveGeneral, and from Phase 9's Settings tabs and the setup wizard that is the UI
        // thread. Taking _gate would make it wait out an in-flight Restart, which holds the gate
        // across a SQLite read and one FileSystemWatcher construction per root.
        //
        // No binding means Start() has never been called, or Stop() has had the last word, or a
        // restart is between its stop and its start. In all three cases there is nothing to
        // rebind: the first two must not start a watcher in a process that deliberately never
        // starts one (design-spec 15), and the third is about to read the current settings
        // anyway.
        if (_binding is not { } binding)
        {
            return;
        }

        if (binding.Enabled == general.WatcherEnabled && RootsEqual(binding.Roots, general.ScanRoots))
        {
            return;
        }

        _runRestart?.Invoke(Restart);
    }

    // Order-insensitive and case-insensitive: reordering scan_roots changes nothing about what
    // is watched, and Windows paths are compared case-insensitively everywhere else in this
    // application (PathConfinement).
    private static bool RootsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count
            && new HashSet<string>(left, StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private void OnChanged(object? sender, string path)
    {
        long generation;
        CancellationToken ct;
        lock (_gate)
        {
            if (_lifetimeCts is null) return;
            _pending.Add(path);
            generation = ++_generation;
            ct = _lifetimeCts.Token;
        }

        PendingWork = RunDebounceAsync(generation, ct);
    }

    private void OnError(object? sender, EventArgs e)
    {
        CancellationToken ct;
        lock (_gate)
        {
            if (_lifetimeCts is null) return;
            ct = _lifetimeCts.Token;
        }

        // Events have been lost, so the pending path list is no longer a truthful account of
        // what changed on disk; only a full scan can catch up (spec 10.7).
        logger.LogWarning("File system watcher reported an error (typically buffer overflow); escalating to a full scan.");
        PendingWork = RunGuardedAsync(() => runFullScan(ct));
    }

    private async Task RunDebounceAsync(long generation, CancellationToken ct)
    {
        try
        {
            // Settings are read live on every fire rather than cached at Start(), so a
            // Settings change takes effect without an app restart.
            await _delay(TimeSpan.FromMilliseconds(settingsStore.GetGeneral().WatcherDebounceMs), ct).ConfigureAwait(false);

            List<string> paths;
            lock (_gate)
            {
                if (generation != _generation) return;   // superseded by a newer notification
                paths = [.. _pending];
                _pending.Clear();
            }

            var general = settingsStore.GetGeneral();

            // ScanFilterConfig.AcceptsFile is the one admission test, shared with the
            // coordinator's targeted ingest (review item 10): supported frame format plus the
            // spec 10.2 filter decision. It runs before the stability gate, so a N.I.N.A. log
            // file written next to the frames costs neither a stability wait nor a scan_runs
            // row.
            var candidates = paths
                .Where(p => general.ScanFilters.AcceptsFile(p, general.ScanRoots))
                .ToList();
            if (candidates.Count == 0) return;

            var stable = await StableSubsetAsync(candidates, general.WatcherStabilityCheckMs, ct).ConfigureAwait(false);
            if (stable.Count == 0) return;

            await ingestTargeted(stable, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop() during a debounce window or an ingest; nothing to report.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Watcher-triggered ingest failed.");
        }
    }

    // Two-check stability gate (spec 10.7): a file counts as settled only if it can be
    // OPENED FOR READ and reports the same length on both checks, separated by
    // watcher_stability_check_ms. One shared wait for the whole batch, not one per file. A
    // file that vanished, is exclusively locked, or changed size is treated as not-yet-settled
    // and simply skipped; the next notification for it starts a fresh window.
    private async Task<IReadOnlyList<string>> StableSubsetAsync(
        IReadOnlyList<string> candidates, int stabilityCheckMs, CancellationToken ct)
    {
        var before = candidates.ToDictionary(p => p, TryReadableLength, StringComparer.OrdinalIgnoreCase);
        await _delay(TimeSpan.FromMilliseconds(stabilityCheckMs), ct).ConfigureAwait(false);
        return [.. candidates.Where(p => before[p] is long length && TryReadableLength(p) == length)];
    }

    // The open is the point, not just the length: a directory entry's size is stale while a
    // writer still holds the handle, and spec 10.7 asks for "can be opened for read" as well
    // as a stable size. UserFiles.OpenRead is FileAccess.Read + FileShare.ReadWrite, so a
    // frame N.I.N.A. still has open for writing is readable here (and its stream length is
    // live), while a FileShare.None holder makes the open fail and the file is deferred.
    // UserFiles has no write members at all (spec 2.1.1).
    private static long? TryReadableLength(string path)
    {
        try
        {
            using var stream = UserFiles.OpenRead(path);
            return stream.Length;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // Takes a factory, not a Task: a delegate that throws synchronously would otherwise
    // escape into a FileSystemWatcher event callback, where nothing can catch it.
    private async Task RunGuardedAsync(Func<Task> work)
    {
        try { await work().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogError(ex, "Watcher-escalated full scan failed."); }
    }
}
