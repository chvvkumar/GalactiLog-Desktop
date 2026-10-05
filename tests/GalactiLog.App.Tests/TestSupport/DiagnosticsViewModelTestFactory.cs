using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one place App.Tests builds a Diagnostics page and the snapshot behind it, so a later
/// record or constructor change is one edit rather than thirty. No database, no window and no
/// dispatcher: the snapshot is a value and the post seam runs its closure inline unless a test
/// asks for the deferred one (design-spec 18.3).
/// </summary>
/// <remarks>
/// <see cref="DiagnosticsService"/> is sealed and its <c>Snapshot</c> is non-virtual, so the stub
/// every case here uses is the <c>Func&lt;DiagnosticsSnapshot&gt;</c> the page takes, in the shape
/// <c>ActivityViewModelTestFactory</c> established for its query delegates.
/// </remarks>
internal static class DiagnosticsViewModelTestFactory
{
    /// <summary>The instant every seeded snapshot hangs off, so a formatted time is a fixed
    /// string.</summary>
    public static readonly DateTimeOffset Noon =
        new(2025, 3, 4, 12, 0, 0, TimeSpan.Zero);

    public static DiagnosticsSnapshot Snapshot(
        AppDiagnostics? app = null,
        PathDiagnostics? paths = null,
        DatabaseDiagnostics? database = null,
        ScanDiagnostics? scan = null,
        ResolverDiagnostics? resolver = null,
        IReadOnlyList<UnresolvedNameRow>? unresolved = null,
        IReadOnlyList<LogRingEntry>? recentErrors = null)
        => new(
            Noon,
            app ?? new AppDiagnostics("1.2.3.0", "abc1234", "stable", "10.0.0", "11.3.0", "3.46.1", "Windows"),
            paths ?? new PathDiagnostics(
                @"C:\AppData\GalactiLog",
                @"C:\AppData\GalactiLog\galactilog.db",
                @"C:\AppData\GalactiLog\logs",
                @"C:\AppData\GalactiLog\thumbnails",
                @"C:\Program Files\GalactiLog\catalogs",
                "default",
                DiagnosticsService.StartupShortcutNotApplicable,
                DiagnosticsService.StartedMinimizedNo),
            database ?? new DatabaseDiagnostics(
                @"C:\AppData\GalactiLog\galactilog.db",
                4096,
                0,
                12,
                DiagnosticsQuery.RowCountTables.ToDictionary(table => table, _ => 3L),
                // Spec 12.8's "PHD2 data size" and the method that produced it (ruling F2).
                2048,
                DiagnosticsQuery.MeasuredMethod),
            scan ?? new ScanDiagnostics(
                false, "", "Ready", 0, false, null, [], null),
            resolver ?? new ResolverDiagnostics(10, 4, 1, 7, 2),
            unresolved ?? [],
            recentErrors ?? []);

    public static ScanRun Run(
        string state = "complete",
        DateTime? startedAt = null,
        DateTime? finishedAt = null)
        => new()
        {
            Id = 1,
            StartedAt = startedAt ?? Noon.UtcDateTime,
            FinishedAt = finishedAt ?? Noon.UtcDateTime.AddMinutes(3),
            Trigger = "manual",
            State = state,
            Discovered = 40,
            NewFiles = 31,
            ChangedFiles = 2,
            Completed = 30,
            Failed = 1,
            SkippedCalibration = 2,
            Removed = 5,
        };

    /// <summary>A run with no finish: still in flight, or left open by a crash. Spec 12.8's
    /// "last run duration" has nothing to report for one.</summary>
    public static ScanRun UnfinishedRun()
    {
        var run = Run();
        run.State = "running";
        run.FinishedAt = null;
        return run;
    }

    public static LogRingEntry Warning(string message, DateTimeOffset? timestamp = null)
        => new(timestamp ?? Noon, "Warning", message, null);

    /// <summary>The shared unresolved-names list the Unresolved group hosts, over lambdas: this
    /// suite asserts what the page does with it, never what it loads.</summary>
    public static UnresolvedNamesViewModel Unresolved(
        Func<IReadOnlyList<UnresolvedNameRow>>? load = null)
        => new(
            load ?? (() => []),
            (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
            post: action => action());

    /// <summary>
    /// The blocking form, for plain xunit facts, which run on a pool thread of their own. The
    /// first refresh is kicked through the post seam in the constructor, so this awaits it before
    /// handing the page back.
    /// </summary>
    public static DiagnosticsViewModel Create(
        Func<DiagnosticsSnapshot>? snapshot = null,
        UnresolvedNamesViewModel? unresolved = null,
        ScanStatusService? scanStatus = null)
    {
        var page = Build(snapshot, unresolved, scanStatus, post: null);
        Settle(page);
        return page;
    }

    /// <summary>
    /// The awaiting form. An <c>AvaloniaFact</c> runs on the headless UI thread, and that thread
    /// is a pool thread, so a blocking wait at construction can inline the refresh onto it
    /// (TRACKING section 2 item 8). Every AvaloniaFact suite builds its page through this.
    /// </summary>
    public static async Task<DiagnosticsViewModel> CreateAsync(
        Func<DiagnosticsSnapshot>? snapshot = null,
        UnresolvedNamesViewModel? unresolved = null,
        ScanStatusService? scanStatus = null)
    {
        var page = Build(snapshot, unresolved, scanStatus, post: null);
        await SettleAsync(page).ConfigureAwait(true);
        return page;
    }

    /// <summary>The raw form, for the cases that supply their own post seam.</summary>
    /// <param name="exportBundle">Spec 16.3's export, as the delegate the page takes. Null in
    /// every case that does not press the export command, which is the shape a host with no
    /// export surface has.</param>
    /// <param name="pickDestination">The save-dialog seam. Null means the page has no dialog and
    /// exports nothing.</param>
    public static DiagnosticsViewModel Build(
        Func<DiagnosticsSnapshot>? snapshot,
        UnresolvedNamesViewModel? unresolved,
        ScanStatusService? scanStatus,
        Action<Action>? post,
        Action<string>? exportBundle = null,
        Func<Task<string?>>? pickDestination = null)
        => new(
            snapshot ?? (() => Snapshot()),
            unresolved ?? Unresolved(),
            scanStatus,
            post ?? (action => action()),
            exportBundle: exportBundle,
            pickDestination: pickDestination);

    /// <summary>
    /// Joins whatever refresh the page has in flight, bounded and without rethrowing, the shape
    /// <c>ActivityViewModel.Quiesce</c> established.
    /// </summary>
    /// <remarks>
    /// Bounded, because this form blocks a pool thread while the refresh it is waiting for needs
    /// one of its own; an unbounded wait under pool pressure is a hang rather than a failure
    /// (TRACKING section 2 item 8). Non-rethrowing, because a harness helper exists to join work,
    /// not to assert on it: a test that cares about the outcome asserts on the page.
    /// </remarks>
    public static void Settle(DiagnosticsViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            if (page.RefreshCommand.ExecutionTask is { } pending)
            {
                pending.ContinueWith(_ => { }, TaskScheduler.Default).Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    /// <summary>The awaiting form, for the tests that assert work never runs on the UI thread.
    /// </summary>
    public static async Task SettleAsync(DiagnosticsViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            if (page.RefreshCommand.ExecutionTask is { } pending)
            {
                await pending.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A post seam that records how each closure was handed over and runs them only when a test
    /// drains it, so "the work ran off the caller" and "the publish ran on the thread the seam
    /// dispatched to" are facts about ordering rather than races.
    /// </summary>
    /// <remarks>
    /// The shape <c>LogViewerViewModelTests</c>'s private seam of the same name established in its
    /// own follow-up round, for the same defect family (TRACKING section 2 item 8): a thread-id
    /// comparison against a caller parked on an await is load dependent, because that caller is
    /// back in the pool and can be reused by the very work item it is waiting for. Recorded here
    /// in TestSupport rather than privately in the test class, and the phase close (fixer list
    /// code item 5) folded Task 2's private copy onto this one, so this is the project's only
    /// implementation: <c>LogViewerViewModelTests</c> and <c>UpdateServiceTests</c> both alias it.
    /// </remarks>
    internal sealed class RecordingPost
    {
        private readonly Lock _gate = new();
        private readonly List<Action> _queued = [];
        private readonly List<int> _threads = [];
        private readonly List<bool> _fromThreadPool = [];
        private int _drainThread;

        /// <summary>The thread each closure was handed over on, in order.</summary>
        public IReadOnlyList<int> Threads
        {
            get { lock (_gate) { return [.. _threads]; } }
        }

        /// <summary>Whether each closure was handed over from a thread-pool thread, in order. A
        /// property of the thread rather than an id comparison, which is what makes it
        /// deterministic.</summary>
        public IReadOnlyList<bool> PostedFromThreadPool
        {
            get { lock (_gate) { return [.. _fromThreadPool]; } }
        }

        /// <summary>The thread the most recent drain ran on, or 0 before the first drain. Read
        /// under the gate, so a drain on one thread is visible to an assertion on another.
        /// </summary>
        public int DrainThread
        {
            get { lock (_gate) { return _drainThread; } }
        }

        public int Queued
        {
            get { lock (_gate) { return _queued.Count; } }
        }

        public void Post(Action action)
        {
            lock (_gate)
            {
                _threads.Add(Environment.CurrentManagedThreadId);
                _fromThreadPool.Add(Thread.CurrentThread.IsThreadPoolThread);
                _queued.Add(action);
            }
        }

        public void Drain()
        {
            Action[] pending;
            lock (_gate)
            {
                _drainThread = Environment.CurrentManagedThreadId;
                pending = [.. _queued];
                _queued.Clear();
            }

            foreach (var action in pending)
            {
                action();
            }
        }

        /// <summary>Awaits until at least <paramref name="count"/> closures are queued. Bounded,
        /// and it awaits rather than blocks.</summary>
        public async Task WaitForQueuedAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Queued < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(2).ConfigureAwait(false);
            }

            if (Queued < count)
            {
                throw new TimeoutException($"Expected at least {count} posted closures, saw {Queued}.");
            }
        }
    }

    /// <summary>A post seam that holds every closure until a test drains it, so "the constructor
    /// ran no query" is a fact about ordering rather than a race against the pool.</summary>
    internal sealed class DeferredPost
    {
        private readonly List<Action> _queued = [];

        public int Queued => _queued.Count;

        public void Post(Action action) => _queued.Add(action);

        public void Drain()
        {
            var pending = _queued.ToArray();
            _queued.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }
    }
}
