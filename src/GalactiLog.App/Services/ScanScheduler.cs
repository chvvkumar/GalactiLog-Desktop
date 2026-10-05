using GalactiLog.Data;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.Services;

// Interval auto-scan (design-spec 10.8): every general.auto_scan_interval_minutes a full scan
// runs, the interval restarts from zero whenever any scan finishes from any trigger, and a
// due tick is skipped outright while a scan is already running. Never started by the CLI
// (spec 15); AppHost constructs it, App.axaml.cs starts it.
//
// The wait is an injected delay rather than a PeriodicTimer: PeriodicTimer is sealed and
// wall-clock driven, so a scheduler built on it can only be tested by actually waiting. The
// observable behaviour (fixed interval, reset on completion, skip while running) is identical.
//
// isScanRunning / runFullScan are bound in AppHost to ScanCoordinator.IsRunning and
// ScanCoordinator.RunAsync; ResetInterval is wired there to ScanCoordinator.ScanFinished.
public sealed class ScanScheduler(
    SettingsStore settingsStore,
    Func<bool> isScanRunning,
    Func<CancellationToken, Task> runFullScan,
    ILogger<ScanScheduler> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _waitCts;

    // Written and cleared under _gate on the lines adjacent to _waitCts, so the pair cannot
    // drift. Spec 12.8's "next scheduled scan time".
    private DateTime? _nextScheduledUtc;

    // Test seam only: lets a test await the loop's completion after Stop() instead of polling.
    internal Task? LoopTask { get; private set; }

    /// <summary>When the current wait is due to elapse, or null when no wait is in flight (the
    /// loop is not running, a scheduled scan is running, or auto-scan is disabled). Spec 12.8's
    /// "next scheduled scan time".</summary>
    /// <remarks>
    /// Recorded when the wait starts, never derived from <c>general.auto_scan_interval_minutes</c>
    /// at read time: the interval is read live on every iteration, so a settings change between
    /// the wait starting and this read would report a time the loop is not going to wake at.
    /// </remarks>
    public DateTime? NextScheduledUtc
    {
        get { lock (_gate) { return _nextScheduledUtc; } }
    }

    public void Start()
    {
        CancellationToken ct;
        lock (_gate)
        {
            if (_lifetimeCts is not null) return;
            _lifetimeCts = new CancellationTokenSource();
            ct = _lifetimeCts.Token;
        }

        LoopTask = RunLoopAsync(ct);
    }

    public void Stop()
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
        }

        // Cancelled outside the lock for the same reason as ResetInterval, and never
        // disposed: the loop still holds this token.
        lifetime?.Cancel();
    }

    // Restarts the current wait with a fresh full interval. Wired to ScanCoordinator's
    // ScanFinished, so a manual, watcher-driven, CLI or first-run scan all push the next
    // scheduled scan out by a whole interval (spec 10.8). A no-op when no wait is in flight,
    // which is the case while a scheduled scan is itself running -- the loop starts a fresh
    // interval on its next iteration anyway.
    public void ResetInterval()
    {
        // Captured under the lock, cancelled outside it. Cancel() runs the parked wait's
        // continuation inline on the calling thread -- which is ScanCoordinator's scan thread,
        // via ScanFinished -- and that continuation goes straight into the loop's next
        // iteration, including a SQLite settings read. Doing that while holding _gate would
        // put a database round trip inside this lock.
        CancellationTokenSource? wait;
        lock (_gate) { wait = _waitCts; }

        // The loop's own finally can dispose this source between the capture and the call,
        // which only happens when the wait already ended -- exactly the case where there is
        // nothing left to reset.
        try { wait?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunLoopAsync(CancellationToken lifetimeCt)
    {
        while (!lifetimeCt.IsCancellationRequested)
        {
            // Read live on every iteration, so an interval change on the Settings screen
            // takes effect from the next wait onward with no restart.
            var interval = TimeSpan.FromMinutes(settingsStore.GetGeneral().AutoScanIntervalMinutes);

            var waitCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCt);
            lock (_gate) { _waitCts = waitCts; _nextScheduledUtc = DateTime.UtcNow + interval; }

            var wasReset = false;
            var ended = false;
            try
            {
                await _delay(interval, waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                wasReset = !lifetimeCt.IsCancellationRequested;
            }
            catch (Exception ex)
            {
                // Task 4 review M2, applied to both periodic loops rather than to one of them
                // (fixer list code item 6): in production _delay is Task.Delay over a constant
                // interval, so only an injected seam can fail this way, and the surrounding code
                // takes care that nothing else ends the loop. Without this the loop ended anyway
                // and faulted its task with nobody observing it. Ending rather than continuing:
                // a loop whose wait does not work would spin.
                logger.LogError(ex, "The scheduler's wait failed; the scheduler is stopping.");
                ended = true;
            }
            finally
            {
                lock (_gate) { _waitCts = null; _nextScheduledUtc = null; }
                waitCts.Dispose();   // safe: this wait has already ended
            }

            if (ended) break;
            if (lifetimeCt.IsCancellationRequested) break;
            if (wasReset) continue;

            var general = settingsStore.GetGeneral();
            if (!general.AutoScanEnabled) continue;
            if (isScanRunning()) continue;

            try
            {
                await runFullScan(lifetimeCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stop() during the scan; the loop condition ends it.
            }
            catch (Exception ex)
            {
                // A failing scan must not kill the scheduler: the next interval still runs.
                logger.LogError(ex, "Scheduled scan failed.");
            }
        }
    }
}
