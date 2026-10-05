using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.7's out-of-scan correlation re-run: the pass of spec 7.6 dispatched when the guiding
/// time inputs change, as a registered job (ruling F5, census member eleven).
/// </summary>
/// <remarks>
/// <para>
/// <b>One trigger.</b> <c>SettingsStore.Phd2GuidingInputsChanged</c> is the only thing that calls
/// <see cref="Queue"/>. That event is the choke point every writer of the general document
/// already passes through, so it covers the panel's per-field edits, the Location tab's three
/// fields, the equipment rename rewrite and any future writer alike, and it fires at most once
/// per save and only when an input of spec 7.6's resolution order really moved (design-lessons
/// rule 2). Nothing on the panel calls this type.
/// </para>
/// <para>
/// <b>Coalescing.</b> A change arriving while a run is in flight, or while one is already queued
/// behind it, results in exactly one further run rather than a queue of N. The pass re-reads the
/// map and the alias map at run time, so the single further run sees every change that arrived.
/// </para>
/// <para>
/// <b>Serialisation with the scan.</b> The pass takes <c>ScanCoordinator</c>'s resolution lease,
/// the same gate every Maintenance action takes, so it cannot run while a scan is running or
/// while another out-of-scan pass holds it. Unlike a Maintenance button it waits for the lease
/// rather than refusing outright, because the user's saved mapping has to take effect eventually
/// and nothing else will re-derive the nights it invalidated.
/// </para>
/// <para>
/// <b>It is not <see cref="IDisposable"/>.</b> It owns no thread (its pump is a pool task that
/// ends when the queue empties), no database handle beyond the connection string every other
/// out-of-scan caller carries, and no subscription of its own: <c>AppHost</c> owns the one
/// subscription. <c>AppHost_RegistersNoInstanceThatImplementsIDisposable</c> therefore has
/// nothing to say about it.
/// </para>
/// <para>
/// <b>It holds no event code of its own.</b> Spec 10.9's correlation rows go through
/// <see cref="Phd2CorrelationEvents"/>, the one implementation both triggers use, called here with
/// <see cref="Phd2CorrelationTriggers.SettingsChange"/> and no <c>parent_id</c> where the scan's
/// call passes <see cref="Phd2CorrelationTriggers.Scan"/> and the run's <c>scan_started</c> id. The
/// token is referenced, never re-spelled: spec 12's "no second vocabulary" sentence.
/// </para>
/// <para>
/// <b>The key gates it.</b> With <c>general.phd2_scan_enabled</c> off the pass does nothing and
/// registers no job, exactly as the in-scan path refuses for the same reason: a re-run would write
/// <c>phd2</c> values into a feature the user switched off, which a fresh scan under the same
/// settings would never produce.
/// </para>
/// </remarks>
public sealed class Phd2CorrelationRunner
{
    /// <summary>How long a run waits for the scan to give up the resolution lease before it gives
    /// up. Generous, because the alternative to waiting is a mapping that silently never takes
    /// effect.</summary>
    internal static readonly TimeSpan DefaultLeaseBudget = TimeSpan.FromMinutes(30);

    // ponytail: a poll, not a completion signal. The lease has no wait primitive, and a settings
    // save is human-paced; swap in a TaskCompletionSource on ScanCoordinator if a caller ever
    // needs this to react in milliseconds.
    private static readonly TimeSpan LeasePoll = TimeSpan.FromMilliseconds(250);

    private readonly string _connectionString;
    private readonly Func<GeneralSettings> _loadGeneral;
    private readonly Func<AliasMap> _loadAliasMap;
    private readonly Func<IDisposable?> _tryBeginLease;
    private readonly Action<GeneralSettings>? _clearCorrelationPending;
    private readonly JobRegistry? _jobs;
    private readonly ILogger _logger;
    private readonly TimeSpan _leaseBudget;

    // The whole of the coalescing state. _pending is "one more run is owed", _running is "a pump
    // is alive to honour it", and both are read and written only under _gate.
    private readonly object _gate = new();
    private bool _pending;
    private bool _running;
    private Task _pump = Task.CompletedTask;

    /// <param name="connectionString">The library database. The pass and the activity write each
    /// open their own short-lived context over it, exactly as every other out-of-scan caller
    /// does.</param>
    /// <param name="loadGeneral">Read at run time, never at subscription time: the run has to see
    /// the map the user just saved, not the one that was stored when the host was built.</param>
    /// <param name="loadAliasMap">Spec 9.1's telescope alias map, also read at run time.</param>
    /// <param name="tryBeginLease"><c>ScanCoordinator.TryBeginResolution</c>. Null means a scan is
    /// running or another pass holds the lease.</param>
    /// <param name="jobs">The one job registry (spec 12). Null in a test that is not looking at
    /// registration.</param>
    /// <param name="leaseBudget">Overridable for tests only.</param>
    /// <param name="clearCorrelationPending">Discharges <c>general.phd2_correlation_pending</c>
    /// (spec 7.6's "The obligation survives a crash"), bound to
    /// <c>SettingsStore.ClearCorrelationPendingIfUnchanged</c>. Called only by a pass that
    /// COMPLETED: a cancelled pass, a failed one and one the application exits under all leave the
    /// flag true, which is what queues the re-run again at the next GUI start. <b>It takes the
    /// general document this pass read at its start</b>, because the store clears only while the
    /// stored guiding inputs still equal that snapshot; a save that landed mid-pass is not
    /// discharged by a pass that never saw it (fix-wave review P1-1). A delegate rather than the
    /// settings store, like every other seam here, so the runner learns nothing about settings
    /// storage.</param>
    public Phd2CorrelationRunner(
        string connectionString,
        Func<GeneralSettings> loadGeneral,
        Func<AliasMap> loadAliasMap,
        Func<IDisposable?> tryBeginLease,
        JobRegistry? jobs = null,
        ILogger? logger = null,
        TimeSpan? leaseBudget = null,
        Action<GeneralSettings>? clearCorrelationPending = null)
    {
        _connectionString = connectionString;
        _loadGeneral = loadGeneral;
        _loadAliasMap = loadAliasMap;
        _tryBeginLease = tryBeginLease;
        _clearCorrelationPending = clearCorrelationPending;
        _jobs = jobs;
        _logger = logger ?? NullLogger.Instance;
        _leaseBudget = leaseBudget ?? DefaultLeaseBudget;
    }

    /// <summary>
    /// Raised once by a pass that ran to completion, on the pool thread that ran it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 15B fixer item 30, with items 38 and F2. The pass rewrites
    /// <c>images.guiding_rms_arcsec</c>, which is what the Statistics page's guiding figures and
    /// the Target detail band both read, and until this event nothing told an open page that the
    /// figures it is showing had been rewritten underneath it. <c>AppHost</c> is the one
    /// subscriber; it drops the derived memos and raises one process-level notification.
    /// </para>
    /// <para>
    /// <b>Only a completed pass.</b> Not the disabled early return, which wrote nothing; not a
    /// cancelled pass, which visited a half of the night set and is not a completion (spec 10.9
    /// draws the same line for the activity row); and not a failed one. Those three leave
    /// <c>general.phd2_correlation_pending</c> true, and it is the pass that finally completes
    /// that both clears the flag and raises this.
    /// </para>
    /// <para>
    /// It carries no payload. A subscriber re-reads rather than patching, because the pass rewrote
    /// rows across an arbitrary set of nights and there is no smaller truthful statement to make.
    /// </para>
    /// </remarks>
    public event EventHandler? PassCompleted;

    /// <summary>
    /// Queues one re-run and returns at once. The handler <c>AppHost</c> binds to the settings
    /// store's guiding time inputs change event, and the reason the panel does not block on a
    /// corpus-wide correlation while the user is editing a row (spec 12.7).
    /// </summary>
    public void Queue()
    {
        lock (_gate)
        {
            _pending = true;
            if (_running)
            {
                // Coalesced. The pump already alive will make exactly one more pass, and that
                // pass re-reads the settings, so it sees this change too.
                return;
            }

            _running = true;
            _pump = Task.Run(PumpAsync);
        }
    }

    /// <summary>The pump task, or a completed task when nothing is queued. Tests await it; nothing
    /// in the application does.</summary>
    internal Task InFlight
    {
        get { lock (_gate) { return _pump; } }
    }

    /// <summary>
    /// Runs one pass now: takes the lease, re-derives the session times the new inputs change,
    /// correlates the nights the change invalidated, writes spec 10.9's events and finishes the
    /// job. Never throws; a failure is logged and the job finishes failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Internal, not public.</b> <see cref="Queue"/> and its pump are the one production caller,
    /// and the gate they share is what keeps two passes off the database at once; a public entry
    /// would be a way round that gate for no gain. The tests share this assembly.
    /// </para>
    /// <para>
    /// <b>Everything is inside the try, including the registry calls.</b>
    /// <see cref="JobRegistry.Begin"/> posts to the dispatcher, and a post against a dispatcher
    /// that is shutting down is not guaranteed to be silent, so <c>Begin</c> and the source it
    /// takes its cancel delegate from sit under the same catch as the pass itself. The pump's own
    /// finally is the first line of defence; this is the second.
    /// </para>
    /// <para>
    /// <b>A dropped pass is not a lost correlation.</b> <c>_pending</c> is the in-process coalescing
    /// flag and is cleared before the pass starts, so a lease budget that expires, a throw, or a
    /// cancel from the flyout consumes that save in this process. What survives the process is
    /// <c>general.phd2_correlation_pending</c> (spec 7.6): the save that queued the re-run set it,
    /// only a pass that COMPLETES clears it, and <c>AppHost</c> queues one re-run at the next GUI
    /// start while it is true. Within one session there is a second path back as well:
    /// <see cref="Phd2Correlation.InvalidatedNights"/> is derived from the database rather than
    /// from a marker this pass consumes, and <c>ScanCoordinator</c> widens to it on any scan that
    /// moved a session, dropped a row, or found the flag set.
    /// </para>
    /// </remarks>
    internal async Task RunAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cancellation = null;
        JobHandle? job = null;

        // "This pass has already written its one terminal row." Read by the catch below.
        var reported = false;

        try
        {
            // Spec 10.3 and 12.7: with general.phd2_scan_enabled off, nothing in phd2_* is read,
            // written or pruned. The in-scan path gates on the key for the reason its own comment
            // gives, and the same reason applies here unchanged: a re-run would re-derive every
            // stored session time and clear and refill the images guiding columns for a feature the
            // user switched off, writing phd2 values a fresh scan under the same settings would
            // never produce. Read once, before anything is registered, so a disabled re-run leaves
            // no job in the flyout either.
            var general = _loadGeneral();
            if (!general.Phd2ScanEnabled)
            {
                return;
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // The captured source outlives this method deliberately. JobViewModel.CanCancel is
            // cleared inside the registry's dispatcher post, which runs after Finish has been
            // queued and after this method has returned, so a cancel click landing in that window
            // reaches a delegate whose source a `using` would already have disposed. That throws
            // ObjectDisposedException on the UI thread out of a RelayCommand, which is exactly the
            // hazard TRACKING section 6 item 13 exists for. The swallow makes the delegate safe
            // for the whole life of the job row; the source is disposed below, after the finish
            // has been queued, and a cancel after that is a no-op rather than a throw.
            var source = cancellation;
            job = _jobs?.Begin(
                ScanStatusService.Phd2CorrelateJobKind,
                ScanStatusService.Phd2JobTitle(ScanStatusService.Phd2CorrelateJobKind),
                () =>
                {
                    try
                    {
                        source.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });

            using var lease = await AcquireLeaseAsync(job, cancellation.Token);
            if (lease is null)
            {
                _logger.LogWarning(
                    "The PHD2 correlation re-run gave up waiting for the scan to release the resolution lease");
                job?.Finish(JobResult.Failed, LeaseRefusedSummary);
                return;
            }

            var result = await Task.Run(
                () => RunPass(general, job, cancellation.Token), cancellation.Token);

            if (result.Cancelled)
            {
                // Spec 10.9: a cancelled pass writes neither phd2_correlation_complete nor
                // phd2_correlation_failed, because a half-visited night set is not a completion and
                // cancellation is not a failure. The pending flag is deliberately left true.
                job?.Finish(JobResult.Cancelled, CancelledSummary);
                return;
            }

            Phd2CorrelationEvents.Emit(
                _connectionString, result, Phd2CorrelationTriggers.SettingsChange, parentId: null,
                warn: message => _logger.LogWarning("{Phd2ActivityWarning}", message));

            // Spec 10.9's "exactly one of complete and failed per pass that ran to an end"
            // (fix-wave review P3-5). Finish posts to the dispatcher, and a post against a
            // dispatcher that is shutting down is not guaranteed to be silent, so without this the
            // general catch below would write phd2_correlation_failed for a pass that has already
            // written its complete row.
            reported = true;
            ClearCorrelationPending(general);
            job?.Finish(JobResult.Succeeded, Phd2CorrelationEvents.Describe(result));

            // Last, and only here: every other exit of this method is a disabled, cancelled or
            // failed pass. The pass's own writes are committed by now, so a subscriber that
            // re-reads sees them. A subscriber that throws reaches the catch below, which writes
            // no second terminal row because `reported` is already true and finds the job already
            // finished.
            PassCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            job?.Finish(JobResult.Cancelled, CancelledSummary);
        }
        catch (Exception ex)
        {
            // Spec 7.6 makes guiding a secondary column of a frame that is already correct on
            // disk and in the catalogue. A failed re-run must not take the process down with it,
            // and the pending flag, left true, is what brings it back.
            _logger.LogError(ex, "The PHD2 correlation re-run failed");
            if (!reported)
            {
                Phd2CorrelationEvents.EmitFailed(
                    _connectionString, Phd2CorrelationTriggers.SettingsChange, ex.Message,
                    parentId: null,
                    warn: message => _logger.LogWarning("{Phd2ActivityWarning}", message));
            }

            // Spec 10.9: the job ends failed carrying the same reason as its one-line summary.
            job?.Finish(JobResult.Failed, FailedSummary(ex.Message));
        }
        finally
        {
            // Dispose is the backstop for a job whose Finish was missed, and it finishes rather
            // than throws, so an already finished job is untouched.
            job?.Dispose();
            cancellation?.Dispose();
        }
    }

    /// <summary>
    /// Honours every queued change in turn and then stands down, leaving <c>_running</c> false on
    /// every path.
    /// </summary>
    /// <remarks>
    /// The finally is load-bearing and is the whole reason this method exists rather than a loop
    /// inlined in <see cref="Queue"/>. Without it, one escaping throw would fault this pool task,
    /// which nothing in the application observes, and leave <c>_running</c> true for the life of
    /// the process: every later <see cref="Queue"/> would then take the coalescing branch and
    /// return, believing a live pump would honour it, and the re-run would be dead with no log
    /// line, no job row and no activity event. The catch inside the loop is the other half: one
    /// bad pass is logged and the pump carries on honouring <c>_pending</c> rather than taking
    /// every later change down with it.
    /// </remarks>
    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_pending)
                    {
                        // Cleared under the same gate Queue takes, so a change arriving in this
                        // window either starts a new pump or is honoured by this one. It cannot
                        // fall between the two.
                        _running = false;
                        return;
                    }

                    _pending = false;
                }

                try
                {
                    await RunAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The PHD2 correlation re-run pass threw past its own guard");
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private async Task<IDisposable?> AcquireLeaseAsync(JobHandle? job, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _leaseBudget;
        var announced = false;

        while (true)
        {
            var lease = _tryBeginLease();
            if (lease is not null)
            {
                return lease;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            if (!announced)
            {
                job?.Report("Waiting for the scan to finish");
                announced = true;
            }

            await Task.Delay(LeasePoll, ct);
        }
    }

    private Phd2CorrelationResult RunPass(GeneralSettings general, JobHandle? job, CancellationToken ct)
    {
        // Spec 7.6's "Re-deriving session times", before the pass and never after, exactly as the
        // in-scan path orders it. A session ingested while its profile had no zone carries a null
        // started_at_utc and no session_date, and an unchanged log is never re-read, so without
        // this a re-run after the user finally sets a zone would match nothing at all. The
        // invalidation set is read AFTER it, because the re-derive is what moves the nights those
        // sessions belong to.
        Phd2Correlation.RederiveSessionTimes(_connectionString, general, ct);

        return Phd2Correlation.Run(
            _connectionString,
            Phd2Correlation.InvalidatedNights(_connectionString),
            Phd2Profiles.Normalize(general.Phd2ProfileMap),
            _loadAliasMap(),
            (step, total, message, _) => job?.Report(message, total > 0 ? 100.0 * step / total : null),
            ct);
    }

    // Spec 7.6's "The obligation survives a crash", and fix-wave review P1-1. The snapshot this
    // pass read at its start goes back to the store, which compares its guiding inputs against the
    // stored document INSIDE its write gate and clears only when they still agree. A save that
    // landed while this pass was running therefore leaves the flag true, and the coalesced second
    // pass, which does see that save, is what discharges it. Without the snapshot the newer save's
    // obligation survived only in _pending and died with the process.
    //
    // Guarded: the pass's own writes are committed by now, and a failure to clear the flag costs
    // one redundant pass at the next start, which is the safe direction.
    private void ClearCorrelationPending(GeneralSettings observed)
    {
        try
        {
            _clearCorrelationPending?.Invoke(observed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear general.phd2_correlation_pending after the PHD2 correlation re-run");
        }
    }

    internal const string LeaseRefusedSummary =
        "The re-run could not start because a scan was running. Save the mapping again once it finishes.";

    internal const string CancelledSummary = "The re-run was stopped.";

    internal static string FailedSummary(string reason) => "The re-run failed: " + reason;
}
