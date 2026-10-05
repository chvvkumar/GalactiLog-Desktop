using System.Globalization;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalactiLog.Data.Ingest;

// What started a scan. Persisted (lowercased) in scan_runs.trigger, spec 5.13.
public enum ScanTrigger { Manual, Watcher, Scheduler, Cli, FirstRun }

/// <summary>
/// Spec 10.3's two per-run arguments (PAR-013). Neither is a settings key and neither is
/// carried from one run to the next (spec 5.8.1): the Library tab's radio pair and its
/// cleanup checkbox build one of these per press, and every other trigger passes null.
/// </summary>
/// <param name="IncludeCalibration">This run's scope. Overrides
/// <c>general.include_calibration</c> for step 3's calibration decision (spec 7.5) and nothing
/// else; the stored key is never rewritten.</param>
/// <param name="ForceOrphanCleanup">Deletes the catalogue rows under a root that reached step
/// 4's 50 percent safety limit. Database rows only, never a file (spec 2.1). Off on every
/// trigger by default: only a manual run may set it, because an unattended run must not be the
/// thing that decides a storage volume is gone.</param>
public sealed record ScanRunOptions(bool IncludeCalibration, bool ForceOrphanCleanup);

// Everything a caller needs after a run: the CLI's --json payload, the status bar's
// completion summary, and the test assertions. AlreadyRunning is what a trigger arriving
// during a scan gets back -- no run id, because no scan_runs row was created for it; the
// pending flag starts one more scan when the current one finishes.
//
// The first three PHD2 counters are spec 5.13's own columns and spec 10.4's own meanings:
// Phd2Found is every guide log the walk discovered whether or not the delta skip opened it,
// Phd2Ingested is what this pass parsed and stored, Phd2Failed is what it recorded unreadable or
// failed. The last three, Phd2SkippedUnchanged, Phd2Empty and Phd2Removed, are the rest of the
// pass's own tally (spec 10.4) and have NO scan_runs column: they exist here so the CLI's scan
// output reconciles (spec 15), where phd2_found equals ingested plus skipped_unchanged plus empty
// plus failed ON A PASS THAT RAN TO COMPLETION. A cancelled pass counts every discovered log in
// Phd2Found and only the candidates it reached in the other four, so the identity does not hold
// there and the CLI prints that line under its own "Scan cancelled:" prefix.
// All six are zero when general.phd2_scan_enabled is off. They are trailing and
// DEFAULTED, not merely trailing: two test factories outside this task's file set build the record
// with a target-typed `new(...)`, and a default is what keeps them compiling. AlreadyRunning
// relies on those defaults rather than restating six zeros, and ScanVerbTests pins that it does.
public sealed record ScanRunOutcome(
    int? RunId, string State, int Discovered, int NewFiles, int ChangedFiles,
    int Completed, int Failed, int SkippedCalibration, int Removed,
    int Phd2Found = 0, int Phd2Ingested = 0, int Phd2Failed = 0,
    int Phd2SkippedUnchanged = 0, int Phd2Empty = 0, int Phd2Removed = 0)
{
    public static readonly ScanRunOutcome AlreadyRunning =
        new(null, "pending", 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// What a finished scan reports to <see cref="ScanCoordinator.ScanFinished"/>.
/// </summary>
/// <remarks>
/// Phase 14B Task 5, coordinator override (Task 2 escalation 2). The event used to carry
/// <c>EventArgs.Empty</c>, so <c>ScanStatusService</c> had no source for the outcome and finished
/// the scan's registry job Succeeded whether the run completed, was cancelled or failed; a
/// cancelled scan then read Succeeded in the status bar's recent list. It derives from
/// <see cref="EventArgs"/>, so every existing subscriber keeps compiling: a lambda infers the
/// argument and a method group taking <c>EventArgs</c> converts contravariantly.
/// </remarks>
/// <param name="Outcome">The run's own outcome record, or null when the run threw.</param>
/// <param name="Error">The exception that ended the run, or null.</param>
public sealed class ScanFinishedEventArgs(ScanRunOutcome? outcome, Exception? error) : EventArgs
{
    /// <summary>The run's outcome record, or null when the run threw before producing one.</summary>
    public ScanRunOutcome? Outcome { get; } = outcome;

    /// <summary>The exception that ended the run, or null.</summary>
    public Exception? Error { get; } = error;

    /// <summary>
    /// The run's terminal state, in the same vocabulary <c>scan_runs.state</c> uses:
    /// <c>complete</c>, <c>cancelled</c> or <c>failed</c>. A run that threw is failed whatever
    /// else it managed to record.
    /// </summary>
    public string State => Error is not null ? "failed" : Outcome?.State ?? "failed";
}

/// <summary>
/// The single in-process scan orchestrator (spec 10). One scan at a time process-wide: a
/// trigger arriving while a scan runs sets a pending flag, and exactly one more scan starts
/// when the current one finishes (the port of the web application's SCAN_PENDING_KEY, without
/// Redis).
/// </summary>
/// <remarks>
/// No Avalonia dependency (spec 4.2, coordinator ruling Q4): <see cref="ProgressChanged"/> is
/// raised on whatever thread reached the reporting point, throttled to at most ten events per
/// second, and the App subscriber marshals to the dispatcher. A phase's terminal event is
/// never throttled away, so a subscriber's last view of a phase is always that phase's final
/// state.
/// </remarks>
public sealed class ScanCoordinator(
    string connectionString,
    SettingsStore settingsStore,
    TargetResolver targetResolver,
    ScanRunRepository scanRuns,
    ILogger<ScanCoordinator> logger,
    // Spec 11.4's reference thumbnail render (Phase 8 Task 6). Normally
    // ThumbnailCache.EnsureReference, bound in AppHost: this assembly must not reference
    // GalactiLog.App, so the render arrives as a delegate. Optional and trailing, so no existing
    // construction site changes.
    //
    // Null is a TEST-ONLY shape, not the CLI's: Program.cs builds the same AppHost with
    // cliMode: true, that registration binds the render with no cliMode gate, and CliDispatcher
    // resolves this same singleton, so a `galactilog scan` DOES run the pass and does decode
    // frames. Null makes the pass a no-op that still raises its envelope, so a coordinator built
    // by hand in a test reports the phase rather than dropping it from spec 10.4's vocabulary.
    Func<Guid, string, bool, CancellationToken, string?>? ensureReferenceThumbnail = null)
{
    // Spec 10.4: at most 10 updates per second.
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(100);

    // Stateless over the same connection string, so it is constructed here rather than
    // threaded through the constructor and the DI registration: an extra instance costs
    // nothing and the alternative would churn a file Task 7 is editing concurrently.
    private readonly ActivityRepository _activity = new(connectionString);

    // Review item 5: a name rule whose regex or glob hits the 250 ms match timeout degrades
    // to "never matches", which without a subscriber is a silent behaviour change on a user's
    // scan. NameRuleMatcher.OnTimeout is a static hook and this is the one production type
    // that always exists before a scan can run, so it is installed here. Idempotent by
    // construction: a second coordinator simply reassigns an equivalent delegate. Installed
    // from a field initializer because those run inside the primary constructor.
    private readonly ILogger<ScanCoordinator> _logger = InstallNameRuleTimeoutHook(logger);

    private static ILogger<ScanCoordinator> InstallNameRuleTimeoutHook(ILogger<ScanCoordinator> log)
    {
        NameRuleMatcher.OnTimeout = rule => log.LogWarning(
            "Scan name rule {RuleId} ({RuleType} pattern {Pattern}) timed out while matching and " +
            "was treated as no match; simplify the pattern or disable the rule",
            rule.Id, rule.Type, rule.Pattern);
        return log;
    }

    private readonly object _gate = new();
    private readonly object _progressGate = new();
    private bool _running;
    private bool _pending;
    private ScanTrigger _pendingTrigger;
    private CancellationTokenSource? _cts;
    private DateTime _lastRaisedUtc = DateTime.MinValue;

    // The follow-up scan the pending flag scheduled, so WaitForIdleAsync covers it too: for a
    // moment after it is scheduled, _running is still false but the process is not idle.
    private Task? _followUp;

    public event EventHandler<ScanProgress>? ProgressChanged;

    // Fired exactly once at the end of every run (complete, cancelled, or failed), whatever
    // the trigger. Task 7's ScanScheduler subscribes to reset its interval.
    public event EventHandler<ScanFinishedEventArgs>? ScanFinished;

    public bool IsRunning { get { lock (_gate) return _running; } }

    // Phase 7 fixer item 1 (phase finding 1): the scan gate used to be one-way. The retry asked
    // IsRunning before it started, but nothing stopped a scan from starting underneath a retry
    // already in flight, which is the same two-writer hazard from the other direction
    // (TargetResolver creating targets on two threads, one of them clearing the negative cache).
    // The lease closes it: one flag, owned here, taken atomically under the same lock the scan
    // gate uses, so there is no window between "is a scan running" and "I have started".
    private bool _resolving;

    /// <summary>True while a resolution pass outside a scan (spec 9.7's retry) holds the lease.
    /// A scan refuses while it is held, exactly as a second scan does.</summary>
    public bool ResolutionInProgress { get { lock (_gate) return _resolving; } }

    /// <summary>Raised when <see cref="ResolutionInProgress"/> changes, on the thread that took
    /// or released the lease. <c>ScanStatusService</c> is the App-layer subscriber and marshals
    /// to the dispatcher; nothing else subscribes for UI purposes.</summary>
    public event EventHandler? ResolutionStateChanged;

    /// <summary>
    /// Takes the resolution lease for a pass that resolves names outside a scan. Returns null
    /// when a scan is running or another pass already holds it; dispose the returned handle to
    /// release. Disposing twice is a no-op.
    /// </summary>
    public IDisposable? TryBeginResolution()
    {
        lock (_gate)
        {
            if (_running || _resolving) return null;
            _resolving = true;
        }

        RaiseResolutionStateChanged();
        return new ResolutionLease(this);
    }

    private void EndResolution()
    {
        lock (_gate)
        {
            if (!_resolving) return;
            _resolving = false;
        }

        RaiseResolutionStateChanged();
    }

    private void RaiseResolutionStateChanged()
    {
        try
        {
            ResolutionStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A ResolutionStateChanged subscriber threw; the pass itself is unaffected");
        }
    }

    private sealed class ResolutionLease(ScanCoordinator owner) : IDisposable
    {
        private ScanCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndResolution();
    }

    /// <summary>
    /// Waits until no scan is running, no pending follow-up is queued, and any follow-up
    /// already scheduled has finished. Returns true once idle (immediately, when it already
    /// is), false if <paramref name="timeout"/> elapses first. Application shutdown pairs it
    /// with <see cref="Cancel"/> and a five second budget (spec 10.5).
    /// </summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_gate)
            {
                if (!_running && !_pending && _followUp is null or { IsCompleted: true }) return true;
            }
            if (DateTime.UtcNow >= deadline) return false;
            // ponytail: a 15 ms poll, not a completion signal. Shutdown checks this once with
            // a five second budget, so the granularity is irrelevant; swap in a
            // TaskCompletionSource if a caller ever needs to react in microseconds.
            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Full walk, classify, ingest and prune over every configured root, or over
    /// <paramref name="rootsOverride"/> when given (the CLI's optional path arguments, spec
    /// 15). Returns <see cref="ScanRunOutcome.AlreadyRunning"/> immediately when a scan is
    /// already running, having set the pending flag.
    /// </summary>
    /// <param name="options">
    /// Spec 10.3's two per-run arguments (PAR-013), or null for "the stored scope, no cleanup
    /// override", which is what every trigger but a manual run passes. Trailing and optional so
    /// that widening this one entry point moved no call site by accident; <see cref="RunGuardedAsync"/>
    /// stays the single gate, and no third entry point was added.
    /// </param>
    public Task<ScanRunOutcome> RunAsync(
        ScanTrigger trigger, IReadOnlyList<string>? rootsOverride, CancellationToken ct,
        ScanRunOptions? options = null)
        => RunGuardedAsync(trigger, ct, token => RunFullPipelineAsync(trigger, rootsOverride, options, token));

    /// <summary>
    /// Watcher-driven targeted ingest (spec 10.7): specific files already known to exist and
    /// be stable, filtered through the same <see cref="ScanFilterConfig"/>, skipping discovery,
    /// the classification walk and orphan pruning. A partial file list must never be mistaken
    /// for "everything on disk", which is exactly what orphan pruning would assume.
    /// </summary>
    public Task<ScanRunOutcome> RunTargetedAsync(ScanTrigger trigger, IReadOnlyList<string> specificFiles, CancellationToken ct)
        => RunGuardedAsync(trigger, ct, token => RunTargetedPipelineAsync(trigger, specificFiles, token));

    /// <summary>Cancels the run in progress, if any (spec 10.5). Safe to call when idle.</summary>
    /// <remarks>
    /// Clears the pending flag under the same lock (review item 2). Otherwise the finally in
    /// <see cref="RunGuardedAsync"/> would start a follow-up scan that this Cancel has no
    /// token for, and the shutdown drain would wait out its whole budget on a scan it just
    /// asked to stop.
    /// </remarks>
    public void Cancel()
    {
        lock (_gate)
        {
            _pending = false;
            _cts?.Cancel();
        }
    }

    // The one-scan-at-a-time gate, the per-run CancellationTokenSource, ScanFinished, and the
    // pending follow-up. Both entry points route through here, so none of that can be
    // forgotten at a future third entry point.
    private async Task<ScanRunOutcome> RunGuardedAsync(
        ScanTrigger trigger, CancellationToken ct, Func<CancellationToken, Task<ScanRunOutcome>> body)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_resolving)
            {
                // Phase 7 fixer item 1: a retry owns resolution and the negative cache for its
                // whole run, so a scan refuses while the lease is held, the same answer a second
                // scan gets. No pending flag: nothing consumes it here (only a finishing scan
                // does), so setting it would queue a scan that never starts.
                _logger.LogInformation(
                    "Scan refused for trigger {Trigger}: an unresolved-name retry is resolving",
                    TriggerName(trigger));
                return ScanRunOutcome.AlreadyRunning;
            }

            if (_running)
            {
                _pending = true;
                _pendingTrigger = trigger;
                _logger.LogInformation("Scan already running; pending flag set for trigger {Trigger}", TriggerName(trigger));
                return ScanRunOutcome.AlreadyRunning;
            }
            _running = true;
            // Linked, so both Cancel() and the caller's own token stop the run.
            _cts = cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        // Carried out of the try so the finally can tell a completed run from a cancelled one
        // from one that threw (coordinator override, Task 2 escalation 2).
        ScanRunOutcome? outcome = null;
        Exception? failure = null;

        try
        {
            outcome = await body(cts.Token).ConfigureAwait(false);
            return outcome;
        }
        catch (Exception ex)
        {
            // Recorded and rethrown untouched: the pipeline has already written the failed
            // scan_runs row, and this frame only needs the outcome for the event below.
            failure = ex;
            throw;
        }
        finally
        {
            bool runAgain;
            ScanTrigger nextTrigger;
            lock (_gate)
            {
                _running = false;
                runAgain = _pending;
                nextTrigger = _pendingTrigger;
                _pending = false;
                _cts = null;
            }
            cts.Dispose();

            // Scheduled BEFORE ScanFinished is raised: a subscriber that throws must not be
            // able to drop the pending scan on the floor.
            if (runAgain)
            {
                // Fire and forget by design: whoever triggered the follow-up was already told
                // "pending" and is not awaiting it. Wrapped so a failure is logged rather than
                // surfacing as an unobserved task exception.
                var followUp = Task.Run(async () =>
                {
                    try
                    {
                        await RunAsync(nextTrigger, null, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Pending follow-up scan failed");
                    }
                });
                lock (_gate) _followUp = followUp;
            }

            // Guarded: this runs in a finally, so an unguarded subscriber exception would
            // replace whatever the scan itself was reporting -- including the failure a
            // caller is waiting to see.
            try
            {
                ScanFinished?.Invoke(this, new ScanFinishedEventArgs(outcome, failure));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A ScanFinished subscriber threw; the scan itself is unaffected");
            }
        }
    }

    private async Task<ScanRunOutcome> RunFullPipelineAsync(
        ScanTrigger trigger, IReadOnlyList<string>? rootsOverride, ScanRunOptions? options, CancellationToken ct)
    {
        var general = settingsStore.GetGeneral();
        IReadOnlyList<string> configuredRoots = rootsOverride ?? general.ScanRoots;

        // Validated against the CONFIGURED roots, always: include_paths/exclude_paths are
        // checked for containment in general.scan_roots, so validating them against a
        // caller's override would reject every configuration that has an exclude path. A bad
        // filter configuration is a rejected request, not a failed scan, so this throws before
        // any scan_runs row exists.
        general.ScanFilters.Validate(general.ScanRoots);

        // An explicit override IS the effective root set: it must not be replaced by
        // include_paths, which is what EffectiveRoots(override) would silently do whenever
        // include_paths is non-empty.
        var effectiveRoots = rootsOverride ?? general.ScanFilters.EffectiveRoots(general.ScanRoots);

        var runId = scanRuns.Start(TriggerName(trigger));

        var discovered = new List<DiscoveredFile>();

        // Spec 10.3 step 5 item 1: the guide logs the ONE walk reported through its callback,
        // collected beside `discovered` in the same materialisation. The walk is not repeated.
        var guideLogs = new List<DiscoveredFile>();
        int? startedActivityId = null;
        int newFiles = 0, changedFiles = 0, completed = 0, failed = 0, skippedCalibration = 0, removed = 0;
        var phd2 = Phd2PassResult.Disabled;
        var cancelled = false;

        try
        {
            // Inside the try: it writes a row, so it can fail, and a failure here must record
            // the run as failed rather than leaving state = "running" forever.
            startedActivityId = EmitScanStarted(runId, trigger, configuredRoots);

            RaiseProgress(ScanTaskNames.Discovery, 0, 0, "Discovering files...", force: true);
            discovered = FileWalker.Walk(
                effectiveRoots, configuredRoots, general.ScanFilters,
                count => RaiseProgress(ScanTaskNames.Discovery, 0, 0, $"Discovered {count} files so far"),
                Warn, ct, guideLogs.Add).ToList();
            RaiseProgress(ScanTaskNames.Discovery, 0, 0, $"Discovered {discovered.Count} files", force: true);

            var known = LoadKnownFiles(options?.IncludeCalibration ?? general.IncludeCalibration);
            RaiseProgress(ScanTaskNames.Classify, 0, discovered.Count, "Classifying files...", force: true);
            var toIngest = new List<DiscoveredFile>();
            var classified = 0;
            foreach (var file in discovered)
            {
                ct.ThrowIfCancellationRequested();
                switch (FileWalker.Classify(file, known))
                {
                    case FileClassification.New:
                        newFiles++;
                        toIngest.Add(file);
                        break;
                    case FileClassification.Changed:
                        changedFiles++;
                        toIngest.Add(file);
                        break;
                }
                classified++;
                RaiseProgress(ScanTaskNames.Classify, classified, discovered.Count, $"Classified {classified}/{discovered.Count} files");
            }
            RaiseProgress(ScanTaskNames.Classify, discovered.Count, discovered.Count, "Classification complete", force: true);

            (completed, failed, skippedCalibration) =
                await RunIngestAsync(toIngest, general, options, startedActivityId, ct).ConfigureAwait(false);

            // Spec 10.5's between-phases checkpoint: a cancelled run skips orphan pruning and
            // every post-pass entirely, and is recorded cancelled.
            ct.ThrowIfCancellationRequested();

            // effectiveRoots, NOT configuredRoots: only what this run actually walked may be
            // pruned. With include_paths narrowing the scan to one subfolder, the rest of the
            // configured root was never walked, so every row under it would look undiscovered
            // and be deleted. RunOrphanPruning raises its own prune_orphans progress event,
            // where the candidate count is in scope.
            removed = RunOrphanPruning(
                effectiveRoots, discovered, startedActivityId, options?.ForceOrphanCleanup ?? false);

            // Spec 10.3 step 5, after the header pass and its orphan prune and before duplicate
            // detection, which is where the spec's own numbered list puts it. It is a second
            // writer that takes the write connection once the frame ingest is done with it.
            phd2 = Phd2Ingest.Run(
                connectionString, guideLogs, general, effectiveRoots,
                options?.ForceOrphanCleanup ?? false, startedActivityId,
                (step, total, message, force) =>
                    RaiseProgress(ScanTaskNames.Phd2Ingest, step, total, message, force),
                Warn, ct);

            // Spec 10.3 step 5 item 5: the correlation runs last inside the guide-log pass. The
            // key gate is repeated here rather than left to the pass, because with the key off
            // Phd2Ingest.Run reads nothing and returns Disabled, and a correlation over an
            // incremental night set would still clear and refill rows the user switched the
            // feature away from. The cancellation gate is explicit for the same kind of reason:
            // Phd2Ingest.Run RETURNS on cancellation rather than throwing, so the pipeline's own
            // between-phases checkpoint sits below this line, and a half-read corpus is exactly
            // the partial view spec 10.5 says a cancelled run must not act on.
            if (general.Phd2ScanEnabled && !ct.IsCancellationRequested)
            {
                RunPhd2Correlation(general, phd2, startedActivityId, ct);
            }

            // Spec 10.5's between-phases checkpoint again, and this one is what makes a cancelled
            // correlation read cancelled. Phd2Correlation.Run stops between nights and RETURNS a
            // result flagged Cancelled rather than throwing, so without this line the next phase's
            // envelope reaches ScanStatusService's sub-job seam and closes the phd2_correlate job
            // Succeeded, against spec 10.9's "a cancelled pass ... the job ends cancelled".
            ct.ThrowIfCancellationRequested();

            RunDuplicateDetection(newFiles, startedActivityId, ct);

            // Spec 7.7 and 10.3 step 6 (ruling R4): mosaic detection after duplicate detection,
            // so it reads the targets that pass left behind, and only on a run not cancelled. A
            // cancellation inside the pass throws and records the run cancelled; any other failure
            // is the pass's own and does not fail the scan.
            ct.ThrowIfCancellationRequested();
            RunMosaicDetection(startedActivityId, ct);

            RunReferenceThumbnails(startedActivityId, ct);

            // Spec 10.5 again, and this one is load-bearing: the reference pass BREAKS on
            // cancellation rather than throwing, so that it can keep what it produced. Without a
            // checkpoint here a run the user stopped inside that pass would be recorded complete,
            // emit scan_complete, and go on to prune. The pass has already saved its work and
            // raised its own envelope and event by this point.
            ct.ThrowIfCancellationRequested();

            RunActivityRetentionPrune();
            RaiseProgress(ScanTaskNames.PruneActivity, 0, 0, "Activity retention pruning", force: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            // Ruling Q6: record the failure, then rethrow. Nothing already written is rolled
            // back -- ScanPipeline has already committed whatever the writer drained.
            RecordTerminalGuarded(runId, startedActivityId, "failed", discovered.Count, newFiles, changedFiles,
                completed, failed, skippedCalibration, removed, phd2, ex.Message);
            _logger.LogError(ex, "Scan {RunId} failed", runId);
            throw;
        }

        var state = cancelled ? "cancelled" : "complete";
        RecordTerminal(runId, startedActivityId, state, discovered.Count, newFiles, changedFiles,
            completed, failed, skippedCalibration, removed, phd2, errorText: null);
        return new ScanRunOutcome(runId, state, discovered.Count, newFiles, changedFiles,
            completed, failed, skippedCalibration, removed, phd2.Found, phd2.Ingested, phd2.Failed,
            phd2.SkippedUnchanged, phd2.Empty, phd2.Removed);
    }

    private async Task<ScanRunOutcome> RunTargetedPipelineAsync(
        ScanTrigger trigger, IReadOnlyList<string> specificFiles, CancellationToken ct)
    {
        var general = settingsStore.GetGeneral();
        general.ScanFilters.Validate(general.ScanRoots);

        // AcceptsFile is the whole spec 10.2 decision -- supported format, root confinement,
        // exclude_paths, name rules, include narrowing -- with no disk access. The watcher's
        // own admission filter calls the same member (review item 10), so a targeted ingest
        // and a walk cannot drift apart. A path under no configured root is dropped, never
        // ingested.
        var accepted = specificFiles
            .Where(p => general.ScanFilters.AcceptsFile(p, general.ScanRoots))
            .Select(StatFile)
            .OfType<DiscoveredFile>()
            .ToList();

        var runId = scanRuns.Start(TriggerName(trigger));

        int? startedActivityId = null;
        int newFiles = 0, changedFiles = 0, completed = 0, failed = 0, skippedCalibration = 0;
        var cancelled = false;

        try
        {
            // roots, not the file list: the payload's `roots` means the same thing for every
            // trigger, and the file list would make a watcher batch unreadable. The batch size
            // is what a reader actually wants here.
            startedActivityId = EmitScanStarted(runId, trigger, general.ScanRoots, accepted.Count);

            var known = LoadKnownFiles(general.IncludeCalibration);
            var toIngest = new List<DiscoveredFile>();
            foreach (var file in accepted)
            {
                // An unchanged file is not re-ingested just because the watcher noticed a
                // touch: same size, same mtime, same row (spec 10.3 step 2).
                switch (FileWalker.Classify(file, known))
                {
                    case FileClassification.New:
                        newFiles++;
                        toIngest.Add(file);
                        break;
                    case FileClassification.Changed:
                        changedFiles++;
                        toIngest.Add(file);
                        break;
                }
            }

            (completed, failed, skippedCalibration) =
                await RunIngestAsync(toIngest, general, options: null, startedActivityId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // No orphan pruning, ever: a handful of watcher-reported paths is not "everything
            // on disk".
            RunActivityRetentionPrune();
            RaiseProgress(ScanTaskNames.PruneActivity, 0, 0, "Activity retention pruning", force: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            RecordTerminalGuarded(runId, startedActivityId, "failed", accepted.Count, newFiles, changedFiles,
                completed, failed, skippedCalibration, removed: 0, Phd2PassResult.Disabled,
                errorText: ex.Message);
            _logger.LogError(ex, "Targeted scan {RunId} failed", runId);
            throw;
        }

        // No guide-log pass, ever, for the same reason there is no orphan pruning here: a handful
        // of watcher-reported paths is not "everything on disk", and the pass's own orphan guards
        // are evaluated against exactly that claim. All six guide-log counters are zero.
        var state = cancelled ? "cancelled" : "complete";
        RecordTerminal(runId, startedActivityId, state, accepted.Count, newFiles, changedFiles,
            completed, failed, skippedCalibration, removed: 0, Phd2PassResult.Disabled, errorText: null);
        return new ScanRunOutcome(runId, state, accepted.Count, newFiles, changedFiles,
            completed, failed, skippedCalibration, 0);
    }

    // The ingest stage, shared by both pipelines: one long-lived tracking context, one
    // ScanWriter, and ScanPipeline's walk/reader/writer structure (spec 10.6).
    private async Task<(int Completed, int Failed, int SkippedCalibration)> RunIngestAsync(
        IReadOnlyList<DiscoveredFile> files, Core.Settings.GeneralSettings general, ScanRunOptions? options,
        int? startedActivityId, CancellationToken ct)
    {
        RaiseProgress(ScanTaskNames.Ingest, 0, files.Count, "Ingesting files...", force: true);
        if (files.Count == 0) return (0, 0, 0);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        var writer = new ScanWriter(context, targetResolver, startedActivityId, Warn, general.MosaicKeywords);
        // Spec 10.3: the run's scope reaches step 2's known set and step 3's calibration decision
        // and nothing else. The
        // GeneralSettings snapshot is NOT mutated and not copied with a `with`: it is shared with
        // the walk, the filter validation and the activity emission, and the one field that moves
        // is visible here rather than hidden in a copy.
        var readOptions = new ScanReadOptions(
            options?.IncludeCalibration ?? general.IncludeCalibration,
            general.UseImagingNight, general.ObserverLongitude, general.ObserverTimezone);
        var step = 0;

        try
        {
            await ScanPipeline.RunAsync(
                files, writer, readOptions, new ScanCounters(),
                () =>
                {
                    var current = Interlocked.Increment(ref step);
                    RaiseProgress(ScanTaskNames.Ingest, current, files.Count, $"Ingested {current}/{files.Count} files");
                },
                Warn, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The writer has already flushed its partial batch (spec 10.5): every frame
            // ingested stays ingested and the counters below are final. The caller's own
            // checkpoint is what records this run as "cancelled".
            _logger.LogInformation("Scan cancelled after {Completed} of {Total} files", writer.Completed, files.Count);
        }

        RaiseProgress(ScanTaskNames.Ingest, Volatile.Read(ref step), files.Count, "Ingest complete", force: true);
        return (writer.Completed, writer.Failed, writer.SkippedCalibration);
    }

    // Raised on the calling thread (spec 4.2): whichever thread reached the reporting point,
    // which for `ingest` is the writer task. Throttled to ProgressThrottle, except for a
    // phase's terminal event, which is never dropped -- otherwise a subscriber's last view of
    // a phase could be a stale intermediate count.
    //
    // Internal rather than private only so ScanCoordinatorTests can drive the throttle
    // directly instead of racing a real scan against a wall clock.
    internal void RaiseProgress(string task, int step, int totalSteps, string message, bool force = false)
    {
        lock (_progressGate)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastRaisedUtc < ProgressThrottle) return;
            _lastRaisedUtc = now;
        }

        // Guarded: a subscriber that throws must not fault the writer task and turn a healthy
        // scan into a recorded failure. Progress reporting is advisory; the scan is not.
        try
        {
            ProgressChanged?.Invoke(this, new ScanProgress(task, step, totalSteps, message));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A ScanProgress subscriber threw; the scan itself is unaffected");
        }
    }

    // Two queries per run: file_path -> (file_size, file_mtime) for the delta classification of
    // spec 10.3 step 2. FileWalker never touches the database itself. A run that excludes
    // calibration also knows every skipped_files row (spec 5.21), so a skipped frame is not
    // read again until it changes; a run that includes calibration leaves them out, so the same
    // frame is classified new and ingested.
    private Dictionary<string, (long? Size, double? Mtime)> LoadKnownFiles(bool includeCalibration)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString));

        // Indexer assignment, not ToDictionary: migration 0003 made images.file_path
        // COLLATE NOCASE, so two rows differing only in case can no longer be created, but a
        // database written before that migration could still hold a pair, and ToDictionary
        // with an OrdinalIgnoreCase comparer would throw on the second one. Last row wins,
        // which at worst re-ingests one file.
        var known = new Dictionary<string, (long? Size, double? Mtime)>(StringComparer.OrdinalIgnoreCase);

        // Skipped rows first, so an images row for the same path wins.
        if (!includeCalibration)
        {
            foreach (var row in context.SkippedFiles.Select(s => new { s.FilePath, s.FileSize, s.FileMtime }).AsEnumerable())
            {
                known[row.FilePath] = (row.FileSize, row.FileMtime);
            }
        }

        foreach (var row in context.Images.Select(i => new { i.FilePath, i.FileSize, i.FileMtime }).AsEnumerable())
        {
            known[row.FilePath] = (row.FileSize, row.FileMtime);
        }
        return known;
    }

    // Best effort: a watcher-reported file can vanish between the stability check and this
    // call, which is not an error -- it is simply not ingested.
    private DiscoveredFile? StatFile(string path)
    {
        try
        {
            var info = UserFiles.GetFileInfo(path);
            var length = info.Length; // forces the stat inside this try
            return new DiscoveredFile(path, length, (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Warn($"Skipping targeted file {path}: {ex.Message}");
            return null;
        }
    }

    // Failure path only (review item 11). The run is already reporting an exception; a
    // database failure while stamping the terminal row must be logged rather than replace it,
    // or the real cause disappears behind a bookkeeping problem. The row is then left
    // "running" and AppHost's interrupted-run reconciliation closes it at next start.
    private void RecordTerminalGuarded(
        int runId, int? startedActivityId, string state, int discovered, int newFiles, int changedFiles,
        int completed, int failed, int skippedCalibration, int removed, Phd2PassResult phd2,
        string? errorText)
    {
        try
        {
            RecordTerminal(runId, startedActivityId, state, discovered, newFiles, changedFiles,
                completed, failed, skippedCalibration, removed, phd2, errorText);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Could not record scan {RunId} as {State}; the scan_runs row stays 'running' " +
                "until the next start reconciles it", runId, state);
        }
    }

    private void RecordTerminal(
        int runId, int? startedActivityId, string state, int discovered, int newFiles, int changedFiles,
        int completed, int failed, int skippedCalibration, int removed, Phd2PassResult phd2,
        string? errorText)
    {
        scanRuns.Complete(runId, state, discovered, newFiles, changedFiles, completed, failed,
            skippedCalibration, removed, phd2.Found, phd2.Ingested, phd2.Failed, errorText: errorText);
        EmitScanTerminal(runId, startedActivityId, state, discovered, newFiles, changedFiles,
            completed, failed, skippedCalibration, removed, phd2);
    }

    private void Warn(string message) => _logger.LogWarning("{ScanWarning}", message);

    // ---- scan lifecycle activity and pruning (spec 10.3 steps 4-5, 10.9) -------------
    // Every event below goes through the one ActivityRepository.Emit spine, so there is a
    // single place an activity_events row is shaped.

    private int? EmitScanStarted(int runId, ScanTrigger trigger, IReadOnlyList<string> roots, int? fileCount = null)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        var row = ActivityRepository.Emit(
            context, "scan", "info", "scan_started", $"Scan started ({TriggerName(trigger)})",
            new { trigger = TriggerName(trigger), roots, run_id = runId, file_count = fileCount });
        context.SaveChanges();
        return row.Id;
    }

    // Spec 10.3 step 4. DELETES DATABASE ROWS ONLY -- NO FILE ON DISK IS EVER DELETED, MOVED
    // OR MODIFIED BY A SCAN (spec 2.1). OrphanPruner performs no filesystem access at all;
    // the warning and the counts come back as data because it has no logger of its own.
    private int RunOrphanPruning(
        IReadOnlyList<string> walkedRoots, IReadOnlyList<DiscoveredFile> discovered, int? startedActivityId,
        bool force)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        var result = OrphanPruner.Prune(context, walkedRoots, discovered.Select(f => f.Path), force);
        PruneSkippedFiles(context, walkedRoots, discovered, result, force);

        foreach (var skipped in result.SkippedRoots)
        {
            _logger.LogWarning(
                "Scan of {Root} discovered zero files but {KnownRows} rows are catalogued under it; " +
                "skipping orphan pruning for this root (possible unmounted or unreachable share).",
                skipped.Root, skipped.KnownRows);
            ActivityRepository.Emit(context, "scan", "warning", "orphan_prune_skipped",
                $"Orphan pruning skipped for {skipped.Root}: the scan discovered no files there, but " +
                $"{skipped.KnownRows} image{(skipped.KnownRows == 1 ? " is" : "s are")} catalogued under it",
                new { root = skipped.Root, known_rows = skipped.KnownRows }, parentId: startedActivityId);
        }

        // Spec 10.9's two new rows, both at warning: a forced removal past the limit is the event
        // a user needs to find later, and spec 12.8's Errors group collects warnings. One event
        // per limited root, beside the per-root orphan_prune_skipped above.
        //
        // Phase 14B fixer, fixer list item 36 (task5-review P3), recording the meaning of
        // orphan_prune_forced's "removed" rather than changing it. It is limited.MissingRows, the
        // per-root count the pass intended to delete, not what ExecuteDelete returned: the delete
        // is one statement over every walked root, so there is no per-root return to carry and
        // making one would be a second delete per root. The two figures can only disagree where
        // two walked roots overlap, and overlapping roots are refused on the settings write path,
        // so nothing the application can store makes them differ. The run's own totals
        // (scan_runs.removed and orphans_pruned) carry the delete's real figure. Spec 10.9 records
        // the same, phase-review spec list item 16 (Task 8).
        foreach (var limited in result.Limited)
        {
            var percent = limited.KnownRows == 0
                ? 0d
                : Math.Round(100d * limited.MissingRows / limited.KnownRows, 1);

            if (force)
            {
                _logger.LogWarning(
                    "Forced orphan pruning removed {Missing} of {Known} catalogue rows under {Root} " +
                    "past the safety limit; no file on disk was touched",
                    limited.MissingRows, limited.KnownRows, limited.Root);
                ActivityRepository.Emit(context, "scan", "warning", "orphan_prune_forced",
                    // Invariant, explicitly. This is the one number either orphan message formats,
                    // and a plain interpolation takes the current culture, so on a comma-decimal
                    // machine the feed would read "12,5%" where the details document beside it, a
                    // JSON double, always reads 12.5. The guide-log side formats its own copy the
                    // same way (Task 4b review: fix both sides or neither).
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Forced cleanup removed {limited.MissingRows} of {limited.KnownRows} " +
                        $"catalogue rows under {limited.Root} ({percent}% of that root), past the " +
                        $"safety limit"),
                    new
                    {
                        root = limited.Root,
                        removed = limited.MissingRows,
                        known_rows = limited.KnownRows,
                        percent_removed = percent,
                        forced = true,
                    },
                    parentId: startedActivityId);
                continue;
            }

            _logger.LogWarning(
                "Scan of {Root} found {Missing} of {Known} catalogued rows missing, at or past the " +
                "50 percent safety limit; skipping orphan pruning for this root",
                limited.Root, limited.MissingRows, limited.KnownRows);
            ActivityRepository.Emit(context, "scan", "warning", "orphan_prune_limited",
                $"Orphan pruning skipped for {limited.Root}: {limited.MissingRows} of " +
                $"{limited.KnownRows} catalogued files were missing, at or past the safety limit",
                new { root = limited.Root, missing_rows = limited.MissingRows, known_rows = limited.KnownRows },
                parentId: startedActivityId);
        }

        if (result.Removed > 0)
        {
            _logger.LogInformation(
                "Orphan pruning removed {Removed} catalogue rows; no file on disk was touched", result.Removed);
            ActivityRepository.Emit(context, "scan", "info", "orphans_pruned",
                $"Removed {result.Removed} deleted file{(result.Removed == 1 ? "" : "s")} from the catalogue",
                new { count = result.Removed }, parentId: startedActivityId);
        }

        context.SaveChanges();

        // Rows removed out of rows found missing, not out of itself: a phase that always
        // reported 100% told a subscriber nothing.
        RaiseProgress(
            ScanTaskNames.PruneOrphans, result.Removed, result.Candidates,
            $"Pruned {result.Removed} orphaned rows", force: true);
        return result.Removed;
    }

    // Spec 10.3 step 4 for skipped_files (spec 5.21): the same walked-root and discovered-set
    // rule as images. The zero-discovery guard is evaluated here over the discovered set itself,
    // not taken from the images prune: a root holding only calibration frames has no images row,
    // so OrphanPruner never names it, yet an unmounted share there must not wipe its rows. The
    // safety-limit hold-back follows the images prune. DATABASE ROWS ONLY. No safety limit of its
    // own and no place in scan_runs.removed: a wrongly pruned row costs one header read, not data.
    private static void PruneSkippedFiles(
        GalactiLogContext context, IReadOnlyList<string> walkedRoots, IReadOnlyList<DiscoveredFile> discovered,
        OrphanPruneResult imagesResult, bool force)
    {
        var discoveredPaths = new HashSet<string>(discovered.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var limited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!force)
        {
            limited.UnionWith(imagesResult.Limited.Select(l => l.Root));
        }
        var roots = walkedRoots.Select(Path.GetFullPath)
            .Where(r => !limited.Contains(r) && discoveredPaths.Any(p => PathConfinement.IsUnderOrEqual(r, p)))
            .ToList();
        if (roots.Count == 0) return;

        var stale = context.SkippedFiles.Select(s => s.FilePath).AsEnumerable()
            .Where(p => !discoveredPaths.Contains(p) && roots.Any(r => PathConfinement.IsUnderOrEqual(r, p)))
            .ToList();
        foreach (var batch in stale.Chunk(OrphanPruner.DeleteBatchSize))
        {
            context.SkippedFiles.Where(s => batch.Contains(s.FilePath)).ExecuteDelete();
        }
    }

    // Spec 10.3 step 5 item 5 and spec 7.6's Correlation subsection. DATABASE ROWS ONLY -- the
    // pass performs no filesystem access at all, and every value it writes goes through
    // Phd2Correlation's one write method, which carries the never-overwrite predicate of ruling F6
    // inside its update statement.
    private void RunPhd2Correlation(
        GeneralSettings general, Phd2PassResult phd2, int? startedActivityId, CancellationToken ct)
    {
        Phd2CorrelationResult result;
        try
        {
            // Spec 7.6's "Re-deriving session times", and it runs BEFORE the pass, never after:
            // a session ingested while its profile had no zone keeps a null started_at_utc, the
            // delta test of spec 10.3 never re-reads its unchanged log, and without this the
            // correlation would have nothing to match however many zones the user configures
            // afterwards. It writes phd2_sessions and phd2_calibrations only, never a guiding
            // column.
            var rederived = Phd2Correlation.RederiveSessionTimes(connectionString, general, ct);

            // The normalised map as it stands at the moment of the call, which is also what the
            // ingest above resolved its telescopes and zones from.
            var profileMap = Phd2Profiles.Normalize(general.Phd2ProfileMap);

            // Spec 9.1's alias map, built the way AliasMapCache builds its own. Both sides of a rig
            // name comparison are user data written at different times, so a raw string comparison
            // here is a defect. Built rather than cached for one reason: the cache is disposable
            // because it subscribes to the store, and a scan needs the map once. Built once and
            // handed to both calls below, so the two cannot disagree about a rig.
            var aliasMap = new AliasMap(settingsStore.GetFilters(), settingsStore.GetEquipment());

            void Report(int step, int total, string message, bool force)
                => RaiseProgress(ScanTaskNames.Phd2Correlate, step, total, message, force);

            result = Phd2Correlation.Run(
                connectionString,
                NightsToVisit(phd2, rederived, general.Phd2CorrelationPending),
                profileMap, aliasMap, Report, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is not a failure. Spec 10.9: a cancelled pass writes neither
            // phd2_correlation_complete nor phd2_correlation_failed, and the obligation recorded in
            // general.phd2_correlation_pending is deliberately left standing. The pipeline's
            // checkpoint below this call is what records the run itself cancelled.
            return;
        }
        catch (Exception ex)
        {
            // Spec 10.3 says nowhere that a correlation failure fails the scan, and the frame
            // ingest it follows has already committed every row it wrote. Turning a completed
            // frame scan into a failed one over a guiding column would throw away a correct
            // result for a secondary one, so this is logged, recorded and the scan continues.
            _logger.LogError(
                ex, "The PHD2 correlation pass failed; the frame scan itself is unaffected");

            // Spec 10.9's phd2_correlation_failed, and the terminal envelope that makes the
            // registered phd2_correlate job end FAILED with the same reason as its summary. Without
            // the envelope the sub-job is closed Succeeded by the next phase's envelope, so a pass
            // that filled nothing reads as a finished, successful job in the flyout.
            RaiseProgress(
                ScanTaskNames.Phd2Correlate, 0, Phd2CorrelationEvents.FailedEnvelopeTotalSteps,
                Phd2CorrelationEvents.FailedMessage(ex.Message), force: true);
            Phd2CorrelationEvents.EmitFailed(
                connectionString, Phd2CorrelationTriggers.Scan, ex.Message, startedActivityId, Warn);
            return;
        }

        if (result.Cancelled)
        {
            // As above: a half-visited night set is not a completion, so nothing is written and the
            // pending flag stays true.
            return;
        }

        // COORDINATOR RULING (fixer-p15a-e escalation): a library with nothing to say about guide
        // logs stays silent in the feed. The key ships ON, so before this ruling every scan of
        // every library that has never seen PHD2 wrote "PHD2 correlation filled 0 frame guiding
        // values over 0 nights" into the Activity feed, at every scan interval, for ever.
        //
        // `Nights` is the count of nights the pass actually VISITED, so this is silent exactly when
        // the union was empty and loud the moment there is one night's guiding to report. The pass
        // is still called on an empty union and still reports its own terminal envelope with
        // TotalSteps 0, which is what keeps spec 10.4's phase vocabulary intact and what stops a
        // job opening (AScanWithTheKeyOnAndNothingToCorrelate_OpensNoJob); the row is the only
        // thing withheld. phd2_correlation_failed is unaffected: a failure is always written.
        if (result.Nights > 0)
        {
            Phd2CorrelationEvents.Emit(
                connectionString, result, Phd2CorrelationTriggers.Scan, startedActivityId, phd2, Warn);
        }

        ClearCorrelationPending(general);
    }

    // Spec 7.6's "The obligation survives a crash": the pass that COMPLETES clears the flag,
    // whichever trigger brought it.
    //
    // It clears through SettingsStore.ClearCorrelationPendingIfUnchanged and never by writing the
    // flag itself (fix-wave review P1-1). `general` is the snapshot this run read at the top of the
    // pipeline, and that store method compares the four guiding inputs of it against the stored
    // document INSIDE its write gate, so a save that landed while this scan was running leaves the
    // flag true rather than being discharged by a pass that never saw it. The runner's clear is the
    // same one call through the delegate AppHost binds, so the two paths cannot drift.
    //
    // The early return is the cheap half of the same question: a pass that began with nothing owed
    // has nothing to discharge, and skipping spares an ordinary scan a settings read and write.
    private void ClearCorrelationPending(GeneralSettings general)
    {
        if (!general.Phd2CorrelationPending)
        {
            return;
        }

        try
        {
            settingsStore.ClearCorrelationPendingIfUnchanged(general);
        }
        catch (Exception ex)
        {
            // Leaving the flag set costs one redundant pass at the next start, which is the safe
            // direction; failing a completed scan over it is not.
            _logger.LogWarning(ex, "Could not clear general.phd2_correlation_pending after the PHD2 correlation");
        }
    }

    // Spec 10.3 step 5 item 5's own set: "the imaging nights this pass ingested and ... the nights
    // the orphan drop of step 4 emptied". The second half cannot be read back off the dropped rows,
    // because the cascade has already taken their sessions with them by the time this runs, and
    // Phd2PassResult carries a removed COUNT rather than a night set. InvalidatedNights is the
    // smallest set that provably contains them: it is the union of the nights holding a
    // phd2-sourced frame and the nights holding a guiding session, bounded by the guide-log corpus
    // rather than by the frame catalogue (spec 7.6). Taken only on a run that actually dropped a
    // row or moved a session's derived times, so the ordinary rescan re-derives exactly the nights
    // it ingested. The re-derive is the second reason: a session that has just gained a zone gains
    // a session_date in the same pass, and that night is in no set the ingest reported. It is read
    // AFTER the re-derive for exactly that reason (Phd2Correlation.InvalidatedNights says so).
    //
    // UNIONED with the nights that need a fill, and the pass is called ONCE over the union. Spec
    // 5.2's ordering guarantee is "the correlation later in the same scan visits that night and
    // restores it", and ScanWriter nulls a frame's phd2-sourced guiding when it rewrites an
    // existing image row from metadata carrying no CSV guiding, whichever night that row belongs
    // to. The set above holds only the nights this run ingested, so without the second half a run
    // that re-read night A's log and rewrote a frame on night B would leave B blank until some
    // later run that ingested nothing.
    //
    // It is one call over the union rather than two calls, an explicit one and an incremental one,
    // because a night in both sets would then be visited twice and counted twice, and `nights` is
    // printed to the user in the Activity feed and the job summary. One call visits each night
    // once, so every figure of phd2_correlation_complete is exact by construction.
    //
    // A night that entered only through the fill half is visited in explicit mode, so it is cleared
    // before it is refilled. That is idempotent and safe: both the clear and the fill go through
    // Phd2Correlation's one write method, which carries the never-overwrite predicate
    // `guiding_rms_source IS NULL OR guiding_rms_source = 'phd2'` inside the update statement
    // itself (ruling F6), so a csv value on such a night is unreachable from either write, and a
    // phd2 value is cleared and re-derived from the same samples to the same number.
    //
    // The union is returned even when it is empty, never null: null is the pass's own incremental
    // mode, and the second half of the union is that mode's set already, so passing null would ask
    // for the same read twice. An empty set visits nothing, and the pass then reports TotalSteps 0,
    // which is what stops a job opening for a scan with nothing to correlate.
    //
    // The third reason to widen to InvalidatedNights is general.phd2_correlation_pending (spec 7.6,
    // "The obligation survives a crash"). A re-run owed by a settings save and never finished
    // leaves nights carrying values the saved map no longer produces: they hold a filled frame, so
    // the fill half never returns them, and they were not ingested, so the explicit half never
    // returns them either. While the flag is true the pass widens to every invalidated night,
    // whichever trigger brought it, and the pass that completes clears the flag.
    private IReadOnlyCollection<DateOnly> NightsToVisit(
        Phd2PassResult phd2, int rederived, bool correlationPending)
    {
        IEnumerable<DateOnly> ingested = phd2.Removed == 0 && rederived == 0 && !correlationPending
            ? phd2.IngestedNights
            : phd2.IngestedNights.Concat(Phd2Correlation.InvalidatedNights(connectionString));

        return [.. ingested.Concat(Phd2Correlation.NightsNeedingFill(connectionString)).Distinct()];
    }

    // Spec 9.7's duplicate detection pass, over distinct unresolved OBJECT strings and then over
    // active targets sharing a normalized name. DATABASE ROWS ONLY -- no file on disk is touched.
    //
    // Gated on newFiles > 0 (spec 9.7 "every scan that ingested at least one new file", ruling
    // Q6). A skipped run still raises exactly one forced dedup envelope, so the status bar shows
    // the phase and spec 10.4's seven-name vocabulary stays intact.
    //
    // The detector is constructed here rather than injected, exactly as ActivityRepository is: it
    // is stateless over the connection string, the resolver is already in scope, and a
    // constructor parameter would churn every ScanCoordinator construction site.
    private void RunDuplicateDetection(int newFiles, int? startedActivityId, CancellationToken ct)
    {
        if (newFiles == 0)
        {
            RaiseProgress(ScanTaskNames.Dedup, 0, 0, "No new files; skipping duplicate detection", force: true);
            return;
        }

        // A createIfMissing: false call still writes catalog_cache rows, which is app data, not
        // target data, and is exactly what the CLI `resolve` verb already relies on.
        var detector = new DuplicateDetector(
            connectionString,
            (name, createIfMissing, token) => targetResolver.Resolve(name, createIfMissing, ct: token),
            _logger);

        var outcome = detector.Run(
            (step, total, message) => RaiseProgress(ScanTaskNames.Dedup, step, total, message),
            ct);

        RaiseProgress(
            ScanTaskNames.Dedup, outcome.NamesExamined, outcome.NamesExamined,
            $"Duplicate detection complete: {outcome.CandidatesWritten} " +
            $"suggestion{(outcome.CandidatesWritten == 1 ? "" : "s")}, {outcome.TargetsCreated} " +
            $"target{(outcome.TargetsCreated == 1 ? "" : "s")} created",
            force: true);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        ActivityRepository.Emit(
            context, "scan", "info", "duplicates_detected",
            $"Duplicate detection wrote {outcome.CandidatesWritten} suggestions",
            // snake_case, like every other details document in this solution (Phase 7 fixer item
            // 4): the record's own PascalCase members would be the only camel-shaped keys in
            // activity_events. Same shape UnresolvedRetry's retry_unresolved details already use.
            new
            {
                names_examined = outcome.NamesExamined,
                candidates_written = outcome.CandidatesWritten,
                targets_created = outcome.TargetsCreated,
                frames_assigned = outcome.FramesAssigned,
                orphan_count = outcome.OrphanCount,
                stopped_on_network_failure = outcome.StoppedOnNetworkFailure,
            },
            parentId: startedActivityId);
        context.SaveChanges();
    }

    // The scan's post-pass shape of mosaic detection: its envelope goes out on ProgressChanged
    // under mosaic_detection, terminal envelopes forced, and a failure is logged, reported and
    // swallowed so the scan still completes (spec 7.7).
    private void RunMosaicDetection(int? startedActivityId, CancellationToken ct)
    {
        try
        {
            RunMosaicDetectionCore(
                "scan", startedActivityId,
                (step, total, message, forced) => RaiseProgress(ScanTaskNames.MosaicDetection, step, total, message, forced),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Already logged, reported and recorded by the core.
        }
    }

    /// <summary>
    /// Spec 7.7's Run Detection: the same pass the scan's post-phase runs, on a background thread,
    /// under the resolution lease, writing the same activity event with <c>trigger</c>
    /// <c>manual</c>. <b>Null means a scan or another pass holds the lease.</b> A pass that throws
    /// writes <c>mosaic_detection_failed</c> and rethrows, so the caller's job ends failed.
    /// </summary>
    /// <remarks>
    /// The shape of <see cref="RunReferenceThumbnailsAsync"/>: the lease is held for the whole run,
    /// so a scan requested meanwhile is refused rather than racing it, and progress arrives through
    /// <paramref name="report"/> rather than <see cref="ProgressChanged"/>, which is the scan's
    /// envelope vocabulary. The App registers the job (kind <c>mosaic_detection</c>).
    /// </remarks>
    /// <param name="report">(step, totalSteps, message), called on the background thread: the four
    /// steps, then the summary "n suggestions".</param>
    public Task<MosaicDetectionResult?> RunMosaicDetectionAsync(Action<int, int, string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        return Task.Run(
            () =>
            {
                using var lease = TryBeginResolution();
                if (lease is null)
                {
                    _logger.LogInformation("Mosaic detection refused: a scan is already running");
                    return null;
                }

                return (MosaicDetectionResult?)RunMosaicDetectionCore(
                    "manual", startedActivityId: null, (step, total, message, _) => report(step, total, message), ct);
            },
            ct);
    }

    // The one implementation of the pass's settings read, its terminal envelopes and its two
    // activity events, shared by the scan and Run Detection (design-lessons rule 1). The bool on
    // the report delegate is "this is a terminal envelope", as RunReferenceThumbnailsCore's is.
    private MosaicDetectionResult RunMosaicDetectionCore(
        string trigger, int? startedActivityId, Action<int, int, string, bool> report, CancellationToken ct)
    {
        MosaicDetectionResult result;
        try
        {
            var pass = new MosaicDetectionPass(connectionString, MosaicDetectionPass.SettingsFrom(settingsStore.GetGeneral()));
            result = pass.Run((step, total, message) => report(step, total, message, false), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Mosaic detection failed");
            report(0, MosaicDetectionPass.FailedEnvelopeTotalSteps, $"Mosaic detection failed: {ex.Message}", true);
            EmitMosaicDetectionEvent(
                "error", "mosaic_detection_failed", $"Mosaic detection failed: {ex.Message}",
                new { trigger, reason = ex.Message }, startedActivityId);
            throw;
        }

        var summary = $"{result.SuggestionsWritten} suggestion{(result.SuggestionsWritten == 1 ? "" : "s")}";
        report(MosaicDetectionPass.TotalSteps, MosaicDetectionPass.TotalSteps, summary, true);
        EmitMosaicDetectionEvent(
            "info", "mosaic_detection_complete", $"Mosaic detection: {summary}",
            new
            {
                trigger,
                suggestions = result.SuggestionsWritten,
                relabelled = result.Relabelled,
                backfilled = result.Backfilled,
            },
            startedActivityId);
        return result;
    }

    // Guarded like EmitScanTerminal: the event is the feed's copy, and failing to write it must
    // not replace what the pass is reporting.
    private void EmitMosaicDetectionEvent(string severity, string eventType, string message, object details, int? startedActivityId)
    {
        try
        {
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
            ActivityRepository.Emit(context, "scan", severity, eventType, message, details, parentId: startedActivityId);
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the {EventType} activity event", eventType);
        }
    }

    // Spec 11.4's reference thumbnail pass, run after duplicate detection (spec 10.3 step 5's
    // order, which matters: dedup's outcome-2 branch creates targets this pass must then see) and
    // before activity retention pruning. DATABASE ROWS PLUS CACHE FILES ONLY -- every byte it puts
    // on disk goes through ThumbnailCache, and therefore through AppWriter (spec 2.1).
    //
    // NOT gated on newFiles (ruling Q15, unlike dedup): the query is the gate. The first scan after
    // this feature ships runs against an already-catalogued library where newFiles is 0 and every
    // target needs a thumbnail. A run with nothing to do still raises exactly one forced
    // ref_thumbnails envelope, so the status bar keeps spec 10.4's seven-name vocabulary intact.
    //
    // The pass and its query are constructed here rather than injected, exactly as
    // ActivityRepository and DuplicateDetector are: both are stateless over the connection string,
    // and constructor parameters would churn every ScanCoordinator construction site.
    private void RunReferenceThumbnails(int? startedActivityId, CancellationToken ct)
        => RunReferenceThumbnailsCore(
            force: false,
            startedActivityId,
            (step, total, message, forced) =>
                RaiseProgress(ScanTaskNames.RefThumbnails, step, total, message, forced),
            ct);

    /// <summary>
    /// Spec 12.7's "regenerate reference thumbnails (missing or all)". Runs the same pass the
    /// post-scan phase runs, on a background thread, under the same guard, and writes the same
    /// <c>reference_thumbnails</c> activity event. <b>Null means exactly one thing: a scan or
    /// another pass holds the resolution lease.</b> A coordinator built with no render delegate (a
    /// test-only shape) reports that through <paramref name="report"/> and returns an empty
    /// outcome instead, so the caller's summary line cannot call it a running scan (review
    /// minor 3).
    /// </summary>
    /// <remarks>
    /// The Maintenance trigger goes through the coordinator rather than constructing a second
    /// <see cref="ReferenceThumbnailPass"/>, because the coordinator owns the render delegate and
    /// the lease (<c>HANDOFF.md</c> section 5, <c>questions.md</c> Q27). A tab-built pass would
    /// need both handed to it and would be able to run while a scan is in its own reference phase,
    /// leaving two passes picking frames for one target against a render budget of two.
    /// <para>
    /// The lease is taken inside the background work and held for the whole run, exactly as
    /// <c>UnresolvedRetry.Run</c> takes it, so a scan requested meanwhile is refused rather than
    /// racing this pass.
    /// </para>
    /// <para>
    /// Progress arrives through <paramref name="report"/> and is <b>not</b> raised on
    /// <see cref="ProgressChanged"/>: that event is the scan's envelope vocabulary (spec 10.4) and
    /// a maintenance action is not a scan phase. The status bar therefore stays idle while this
    /// runs; the tab shows the progress.
    /// </para>
    /// </remarks>
    /// <param name="force">True re-offers every target and forwards into the render, which deletes
    /// the existing <c>reference/&lt;target id&gt;.jpg</c> and renders again (spec 11.3, FIXER item
    /// 17). False fills gaps only.</param>
    /// <param name="report">(step, totalSteps, message), called on the background thread.</param>
    public Task<ReferenceThumbnailPass.ReferenceThumbnailOutcome?> RunReferenceThumbnailsAsync(
        bool force, Action<int, int, string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        return Task.Run(
            () =>
            {
                using var lease = TryBeginResolution();
                if (lease is null)
                {
                    _logger.LogInformation(
                        "Reference thumbnail regeneration refused: a scan is already running");
                    return null;
                }

                return RunReferenceThumbnailsCore(
                    force,
                    startedActivityId: null,
                    (step, total, message, _) => report(step, total, message),
                    ct);
            },
            ct);
    }

    // The one implementation of the pass's setup, its envelopes and its activity event, shared by
    // the scan's post-phase and spec 12.7's maintenance action (design-lessons rule 1, applied at
    // the second occurrence). The scan shape lives entirely in the two things the callers supply:
    // the report delegate (which the scan path binds to its forced ref_thumbnails envelope and the
    // maintenance path binds to the tab's progress line) and startedActivityId (which parents the
    // event to scan_started during a scan and is null outside one).
    //
    // The pass and its query are constructed here rather than injected, exactly as
    // ActivityRepository and DuplicateDetector are: both are stateless over the connection string,
    // and constructor parameters would churn every ScanCoordinator construction site.
    //
    // The bool on the report delegate is "this is a terminal envelope": the scan path forwards it
    // as RaiseProgress's force flag so a phase's last state is never throttled away, and the
    // maintenance path ignores it because it has no throttle.
    private ReferenceThumbnailPass.ReferenceThumbnailOutcome? RunReferenceThumbnailsCore(
        bool force, int? startedActivityId, Action<int, int, string, bool> report, CancellationToken ct)
    {
        if (ensureReferenceThumbnail is null)
        {
            // No renderer was supplied, which in practice means a coordinator built by hand in a
            // test: AppHost binds one for the GUI and the CLI alike. Not a failure worth recording
            // as one, so the phase reports itself and writes no activity event. An empty outcome
            // rather than null, so the maintenance caller can tell this apart from a refused lease
            // (review minor 3); the scan path ignores the return either way.
            report(0, 0, "Reference thumbnails: no renderer configured", true);
            return new ReferenceThumbnailPass.ReferenceThumbnailOutcome(0, 0, 0, false);
        }

        var pass = new ReferenceThumbnailPass(
            connectionString,
            ensureReferenceThumbnail,
            new ReferenceThumbnailSourcesQuery(new DatabaseConnectionString(connectionString)).Get,
            _logger);

        var outcome = pass.Run(
            force,
            (step, total, message) => report(step, total, message, false),
            ct);

        if (outcome.Total == 0)
        {
            report(0, 0, "No targets need a reference thumbnail", true);
            return outcome;
        }

        report(
            outcome.Total, outcome.Total,
            $"Reference thumbnails: {outcome.Generated} generated, {outcome.Failed} failed",
            true);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        ActivityRepository.Emit(
            context, "thumbnail", "info", "reference_thumbnails",
            $"Reference thumbnails: {outcome.Generated} generated, {outcome.Failed} failed",
            // snake_case, like every other details document here. Spec 10.9's payload is
            // {generated, failed}; total and cancelled are additive and make the feed readable.
            new
            {
                generated = outcome.Generated,
                failed = outcome.Failed,
                total = outcome.Total,
                cancelled = outcome.Cancelled,
            },
            parentId: startedActivityId);
        context.SaveChanges();
        return outcome;
    }

    // scan_complete / scan_cancelled / scan_failed (spec 10.9).
    private void EmitScanTerminal(
        int runId, int? startedActivityId, string state, int discovered, int newFiles, int changedFiles,
        int completed, int failed, int skippedCalibration, int removed, Phd2PassResult phd2)
    {
        var (eventType, severity) = state switch
        {
            "complete" => ("scan_complete", "info"),
            "cancelled" => ("scan_cancelled", "warning"),
            _ => ("scan_failed", "error"),
        };

        try
        {
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));

            // RecordTerminal stamps scan_runs immediately before this call, so that row
            // already carries both the duration (spec 10.9's duration_ms) and the error text
            // (spec 10.9's {error}). Reading them back beats widening this signature and
            // beats starting a second clock that could disagree with scan_runs.
            var run = context.ScanRuns.SingleOrDefault(r => r.Id == runId);
            var durationMs = run?.FinishedAt is { } finishedAt && finishedAt > run.StartedAt
                ? (int?)(finishedAt - run.StartedAt).TotalMilliseconds
                : null;

            // A dictionary rather than an anonymous type: `error` belongs only on a failed
            // run, and an always-null key would be noise on every other row.
            var details = new Dictionary<string, object?>
            {
                ["discovered"] = discovered,
                ["new"] = newFiles,
                ["changed"] = changedFiles,
                ["completed"] = completed,
                ["failed"] = failed,
                ["skipped_calibration"] = skippedCalibration,
                ["removed"] = removed,
                // Spec 10.9 places the three after `removed` and before `duration_ms`, and they
                // are present and zero when general.phd2_scan_enabled is off rather than absent:
                // a reader must be able to tell "the pass found nothing" from "the key is off"
                // without consulting the settings document.
                ["phd2_found"] = phd2.Found,
                ["phd2_ingested"] = phd2.Ingested,
                ["phd2_failed"] = phd2.Failed,
                ["duration_ms"] = durationMs,
            };
            if (run?.ErrorText is { Length: > 0 } errorText) details["error"] = errorText;

            ActivityRepository.Emit(
                context, "scan", severity, eventType,
                BuildTerminalMessage(state, newFiles, changedFiles, completed, failed, skippedCalibration, removed, run?.ErrorText),
                details, parentId: startedActivityId, durationMs: durationMs);
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            // scan_runs is the durable record of a run; this event is the feed's readable
            // copy of it. The failure path calls this from inside its catch block, so a
            // failure writing the copy must never replace the exception the run is already
            // reporting -- that would hide the real cause behind a logging problem.
            _logger.LogWarning(ex, "Could not write the {EventType} activity event for scan {RunId}", eventType, runId);
        }
    }

    private static string BuildTerminalMessage(
        string state, int newFiles, int changedFiles, int completed, int failed, int skippedCalibration,
        int removed, string? errorText)
    {
        if (state == "failed") return $"Scan failed: {errorText ?? "unknown error"}";
        var counts =
            $"{newFiles} new, {changedFiles} changed, {completed} ingested, {failed} failed, " +
            $"{skippedCalibration} calibration skipped, {removed} removed";
        return state == "cancelled" ? $"Scan cancelled ({counts})" : $"Scan complete ({counts})";
    }

    // Spec 5.12: the activity log is pruned to general.activity_retention_days after every
    // scan (AppHost does the same at application start). Rows only, like everything else here.
    private void RunActivityRetentionPrune()
    {
        var retentionDays = settingsStore.GetGeneral().ActivityRetentionDays;
        var deleted = _activity.PruneRetention(retentionDays);
        if (deleted > 0)
        {
            _logger.LogInformation(
                "Activity log pruned: {Deleted} rows older than {RetentionDays} days removed",
                deleted, retentionDays);
        }
    }

    private static string TriggerName(ScanTrigger trigger) => trigger switch
    {
        ScanTrigger.Watcher => "watcher",
        ScanTrigger.Scheduler => "scheduler",
        ScanTrigger.Cli => "cli",
        ScanTrigger.FirstRun => "first_run",
        _ => "manual",
    };
}
