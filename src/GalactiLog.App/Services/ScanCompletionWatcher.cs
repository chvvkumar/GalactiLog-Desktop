using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11 behaviour 10's three conditions, in one place: a scan finished, no window is on
/// screen, and the user asked to be told.
/// </summary>
/// <remarks>
/// <para>
/// The third subscriber to <see cref="ScanStatusService.ScanFinished"/> and never a second
/// subscription to <c>ScanCoordinator</c>, which would throttle the scan's writer task. It is an
/// event subscriber and not a periodic loop: no interval, no injected delay, and no
/// per-iteration settings read, so the count of periodic loops in this application stays at two
/// and the spine stays unextracted (design-lessons rule 1).
/// </para>
/// <para>
/// <see cref="ScanStatusService.ScanFinished"/> arrives on the UI thread and its own comment says
/// not to post again to enter. Both of this handler's reads are SQLite reads, so the cheap
/// condition is answered on the UI thread, the two reads are handed to the thread pool, and the
/// <see cref="IScanCompletionNotifier.Show"/> is published back through the post seam. Nothing
/// here waits on anything: a handler that blocked would stall the window for the length of a
/// database read on every scan, window or no window.
/// </para>
/// </remarks>
public sealed class ScanCompletionWatcher : IDisposable
{
    /// <summary>
    /// How many <c>scan_runs</c> rows the notice looks back over to find the run that just
    /// finished (fix round 1, review important finding).
    /// </summary>
    /// <remarks>
    /// The event carries no payload and neither <c>ScanStatusService</c> nor
    /// <c>ScanCoordinator</c> exposes the finished run's id at <c>ScanFinished</c>, so the row is
    /// found by state rather than by identity. The newest row is not always the one that finished:
    /// <c>ScanCoordinator</c> starts its pending follow-up run before raising the event, and a
    /// <c>galactilog scan</c> beside the window is a second process writing its own
    /// <c>running</c> row (spec 12.11 behaviour 2). Both are bounded: one GUI run at a time plus a
    /// small number of concurrent CLI runs, so a look-back of five rows covers the case with room
    /// to spare and still reads one indexed page.
    /// </remarks>
    public const int RecentRunsScanned = 5;

    private readonly ScanStatusService _scanStatus;
    private readonly WindowResidencyService _residency;
    private readonly Func<GeneralSettings> _readGeneral;
    private readonly Func<int, IReadOnlyList<ScanRun>> _readRecentRuns;
    private readonly IScanCompletionNotifier _notifier;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private bool _started;
    private bool _disposed;

    // Both are touched from the thread pool as well as the UI thread, so both are interlocked
    // rather than plain fields. The generation is bumped by Stop and fences a read already in
    // flight; the flag keeps "logged once" honest when two reads race.
    private int _generation;
    private int _reportedUnavailable;

    /// <param name="scanStatus">The one App-layer mirror of the scan (spec 4.2, 10.4). Its
    /// <c>ScanFinished</c> is the trigger, and it is the only event this type subscribes to.</param>
    /// <param name="residency">Spec 12.11's one owner of whether a window is on screen. Read
    /// live; this type keeps no copy of the answer.</param>
    /// <param name="readGeneral">Normally <c>SettingsStore.GetGeneral</c>. Read live inside the
    /// handler, because the user can turn the notice on or off while the application runs.</param>
    /// <param name="readRecentRuns">Normally the method group <c>ScanRunRepository.Recent</c>, the
    /// newest rows by <c>started_at</c>, newest first. The notice's counts come from a row rather
    /// than from the event, which carries no payload (Phase 10 ruling Q2), and the newest row is
    /// not necessarily the one that finished, so this reads
    /// <see cref="RecentRunsScanned"/> of them and takes the newest terminal one.</param>
    /// <param name="notifier">Spec 12.11 behaviour 10's one seam. The mechanism behind it is a
    /// coordinator ruling (questions.md Q6) and replacing it is one file.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed read and a failed notice are logged at warning and
    /// swallowed: a notice that could not be shown must never take the scan's completion path
    /// down with it (spec 12.10).</param>
    public ScanCompletionWatcher(
        ScanStatusService scanStatus,
        WindowResidencyService residency,
        Func<GeneralSettings> readGeneral,
        Func<int, IReadOnlyList<ScanRun>> readRecentRuns,
        IScanCompletionNotifier notifier,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _scanStatus = scanStatus;
        _residency = residency;
        _readGeneral = readGeneral;
        _readRecentRuns = readRecentRuns;
        _notifier = notifier;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Subscribes. Called from App.axaml.cs beside the watcher, the scheduler and the
    /// update loop, so the CLI structurally never notifies (spec 15).</summary>
    /// <remarks>Idempotent: a second call subscribes nothing further, so one finished scan stays
    /// one notice.</remarks>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started)
            {
                return;
            }

            _started = true;
        }

        _scanStatus.ScanFinished += OnScanFinished;
    }

    /// <summary>Unsubscribes. Safe when never started, and called twice.</summary>
    /// <remarks>
    /// It also fences a read already in flight (fix round 1, review minor 2). Detaching the
    /// handler cannot recall work the pool has already been given, and this is called from the
    /// <c>ShutdownRequested</c> handler, so without the fence a notice could still reach the
    /// tooltip between the stop and the end of the drain.
    /// </remarks>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
        }

        Interlocked.Increment(ref _generation);
        _scanStatus.ScanFinished -= OnScanFinished;
    }

    /// <summary>The in-flight read, so a test awaits it rather than sleeping.</summary>
    /// <remarks>Assigned before the work can complete, so a test that reads it immediately after
    /// the event has something to join.</remarks>
    internal Task? PendingNotice { get; private set; }

    /// <summary>Stops. Disposes neither collaborator: the host owns them.</summary>
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    // The three conditions, live and in order. Condition 1 is free and is answered here on the UI
    // thread; conditions 2 and 3 are SQLite reads and are answered off it.
    private void OnScanFinished(object? sender, EventArgs e)
    {
        // 1. No window on screen. Not a value captured at subscription: the user can open the
        //    window between the scan starting and the scan finishing.
        if (_residency.IsWindowVisible)
        {
            _logger.LogDebug("A scan finished with the window on screen; no notice was requested");
            return;
        }

        // Sampled here rather than inside the read, so a Stop that lands while the read is parked
        // is seen by the read that was already in flight.
        var generation = Volatile.Read(ref _generation);

        // Handed to the pool rather than awaited: ScanStatusService invokes each subscriber in
        // turn inside its dispatcher post, so a subscriber that waited on SQLite here would stall
        // the UI for the length of the read on every finished scan.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(async () =>
        {
            await gate.Task.ConfigureAwait(false);
            ReadAndPublish(generation);
        });

        // Assigned before the gate is released, so the task cannot have completed by the time a
        // test reads this property.
        PendingNotice = pending;
        gate.SetResult();
    }

    private void ReadAndPublish(int generation)
    {
        ScanCompletionNotice notice;
        try
        {
            // 2. The user asked to be told. Live, for the same reason condition 1 is, and read
            //    before the notifier is asked whether it has a surface (fix round 1, review minor
            //    3): a build with no surface should not write a log line for a user who never
            //    turned the notice on.
            if (!_readGeneral().NotifyOnScanComplete)
            {
                return;
            }

            if (!_notifier.IsAvailable)
            {
                // Once, not once per scan: an unavailable surface is a property of the build.
                if (Interlocked.Exchange(ref _reportedUnavailable, 1) == 0)
                {
                    _logger.LogInformation(
                        "The scan completion notifier reports no surface on this build; no notice will be shown");
                }

                return;
            }

            // 3. There is a finished run to report. The event carries no payload and no run id, so
            //    the row is found by state: the newest terminal row within the look-back.
            if (FindFinishedRun() is not { } run)
            {
                _logger.LogDebug("A scan finished with no terminal scan_runs row to report");
                return;
            }

            notice = ScanCompletionNotice.From(run);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The scan completion notice could not be composed");
            return;
        }

        if (Volatile.Read(ref _generation) != generation)
        {
            _logger.LogDebug("The watcher was stopped while a scan completion notice was being read");
            return;
        }

        try
        {
            _post(() =>
            {
                try
                {
                    _notifier.Show(notice);
                }
                catch (Exception ex)
                {
                    // Spec 12.10: a failure is reported, never a silent default, and never at the
                    // cost of the next scan's notice.
                    _logger.LogWarning(ex, "The scan completion notice could not be shown");
                }
            });
        }
        catch (Exception ex)
        {
            // The post seam itself, not the notice: a dispatcher that is already shutting down
            // throws here, and this task is fire and forget, so an unguarded throw would surface
            // only as an unobserved task exception (fix round 1, review minor 1).
            _logger.LogWarning(ex, "The scan completion notice could not be published to the UI thread");
        }
    }

    // The newest row whose state is one a finished run carries. Not the newest row outright:
    // ScanCoordinator starts its pending follow-up before raising the event, and a galactilog scan
    // beside the window writes its own running row that this process never hears about (spec 12.11
    // behaviour 2). This is a test on the row's state, not deduplication by run id, so two
    // consecutive runs with the same counts are still two notices.
    private ScanRun? FindFinishedRun()
    {
        foreach (var run in _readRecentRuns(RecentRunsScanned))
        {
            if (ScanCompletionNotice.IsTerminalState(run.State))
            {
                return run;
            }
        }

        return null;
    }
}
