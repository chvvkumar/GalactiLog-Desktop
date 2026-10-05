using GalactiLog.Core.Targets;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalactiLog.Data.Ingest;

/// <summary>
/// Spec 9.7's retry action: clear every negative cache row, then re-run resolution for each
/// distinct unresolved <c>OBJECT</c>, assigning frames where it now succeeds. Port of
/// <c>tasks_target_rebuild.py::retry_unresolved</c>. Database rows only: nothing here touches the
/// filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Sits beside <see cref="DuplicateDetector"/> because it is the same loop shape (resolve a name,
/// assign its frames) run for a different reason. The name list and the frame assignment are
/// literally the same code in both: <c>UnresolvedObjects.Read</c> and
/// <c>UnresolvedObjects.AssignFrames</c>. Spec 12.7 puts this action on both
/// the Targets tab and the Maintenance tab; there is one implementation and Phase 9's Maintenance
/// tab calls it.
/// </para>
/// <para>
/// No sleep between names, unlike the web source's <c>time.sleep(0.3)</c>:
/// <see cref="TargetResolver"/> already carries spec 9.6's 3-attempt backoff and this is one
/// desktop process, not a Celery fleet (questions.md Q16).
/// </para>
/// </remarks>
/// <param name="connectionString">The DI <c>DatabaseConnectionString.Value</c>. This service opens
/// its own short-lived contexts, like <c>ActivityRepository</c> and
/// <see cref="DuplicateDetector"/>.</param>
/// <param name="cache">Cleared of every negative row before the loop starts, so a name suppressed
/// by the 7 day negative TTL is really re-queried.</param>
/// <param name="resolve">Normally a lambda over
/// <c>TargetResolver.Resolve(name, createIfMissing: true, ct: token)</c>. The retry creates
/// targets: that is what "assigning frames where it now succeeds" means. A delegate, not the
/// resolver, so a test drives every outcome with no network (design-spec 18.2).</param>
/// <param name="tryBeginResolution">Takes the resolution lease, or returns null when a scan is
/// running or another pass holds it. Required, not optional: this is the choke point, so a caller
/// cannot forget the gate (design-lessons rule 2). A scan owns resolution and the negative cache
/// for its whole run (spec 5.1, 9.6), so a retry that started underneath one would clear rows the
/// scan writer is still consulting and create targets on a second thread. The lease closes the
/// other direction too (Phase 7 fixer item 1): a scan requested while the retry holds it is
/// refused, so a "may I start" read that a scan could invalidate a microsecond later is not
/// enough. <c>AppHost</c> binds it to <c>ScanCoordinator.TryBeginResolution</c>, the live
/// coordinator, never the UI mirror.</param>
/// <param name="logger">Per-name failures are logged, never thrown: one name the catalog service
/// rejects must not abandon the rest of the run.</param>
public sealed class UnresolvedRetry(
    string connectionString,
    CatalogCacheRepository cache,
    Func<string, CancellationToken, TargetResolver.ResolutionResult> resolve,
    Func<IDisposable?> tryBeginResolution,
    ILogger? logger = null)
{
    /// <summary>Why a run ended. <see cref="RetryStatus.ScanInProgress"/> means nothing was
    /// touched at all: no cache row cleared, no name examined, no frame moved.</summary>
    public enum RetryStatus { Completed, ScanInProgress }

    /// <summary>What one run did, for the caller's summary line and the activity event.</summary>
    /// <param name="NegativeRowsCleared">Every <c>catalog_cache</c> row with <c>negative = 1</c>,
    /// across every source. Positive rows are never touched.</param>
    /// <param name="NamesExamined">Distinct unresolved <c>OBJECT</c> names whose body started,
    /// including one stopped by a network failure and one skipped by a catalog rejection.</param>
    /// <param name="NamesResolved">Names that now resolve to a target.</param>
    /// <param name="FramesAssigned">LIGHT frames moved onto those targets.</param>
    /// <param name="NamesStillUnresolved">Names that resolved to nothing again. The resolver
    /// re-writes their negative rows itself, under its own rules (spec 9.6).</param>
    /// <param name="StoppedOnNetworkFailure">An online source could not be reached, so the
    /// remaining names were left for the next run rather than judged unresolvable.</param>
    /// <param name="Status">Why the run ended. Defaults to
    /// <see cref="RetryStatus.Completed"/>, so every existing construction site stays
    /// source-compatible.</param>
    public sealed record RetryOutcome(
        int NegativeRowsCleared,
        int NamesExamined,
        int NamesResolved,
        int FramesAssigned,
        int NamesStillUnresolved,
        bool StoppedOnNetworkFailure,
        RetryStatus Status = RetryStatus.Completed);

    /// <param name="report">(step, totalSteps, message), forwarded to the caller's progress
    /// surface. Called once per name.</param>
    /// <param name="ct">Cancellation ends the run with <see cref="OperationCanceledException"/> and
    /// keeps every assignment already committed.</param>
    public RetryOutcome Run(Action<int, int, string> report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The gate is here rather than in each caller's button: Phase 9's Maintenance tab and
        // Phase 10's Diagnostics page call this same method, and a check they each have to
        // remember is a check one of them eventually forgets (design-lessons rule 2). Refused
        // before the clear, so a refused run has touched nothing. Taken as a lease rather than
        // read as a flag (Phase 7 fixer item 1): holding it for the whole run is what makes a
        // scan starting underneath this loop impossible, and taking it atomically removes the
        // window a check-then-start would leave. Released when Run returns, including the
        // activity write below and every exception path.
        using var lease = tryBeginResolution();
        if (lease is null)
        {
            logger?.LogInformation("Retry unresolved refused: a scan is already running");
            return new RetryOutcome(0, 0, 0, 0, 0, false, RetryStatus.ScanInProgress);
        }

        // Step 1 (spec 9.7): "clears every negative cache row", not the resolver source's alone.
        var cleared = cache.ClearNegative();

        // One tracking context, so PragmaConnectionInterceptor stays in the path and spec 5.1's
        // busy timeout applies to the frame assignments. Nothing here writes through EF.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var names = UnresolvedObjects.Read(connection).Select(row => row.Name).ToList();

        var examined = 0;
        var resolved = 0;
        var frames = 0;
        var stillUnresolved = 0;
        var stopped = false;

        for (var index = 0; index < names.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var name = names[index];
            report(index + 1, names.Count, $"Retrying {index + 1}/{names.Count} unresolved names...");
            examined++;

            TargetResolver.ResolutionResult result;
            try
            {
                result = resolve(name, ct);
            }
            catch (NonTransientCatalogException ex)
            {
                // One name the catalog service rejected outright. Skip it and carry on.
                logger?.LogWarning(
                    ex, "Retry unresolved skipped {ObjectName}: the catalog service rejected the query", name);
                continue;
            }

            if (result.TransientNetworkFailure)
            {
                // The same rule Task 1's pass applies: a name that could not be reached was not
                // checked, so the rest of the list is left for the next run rather than recorded
                // as unresolvable.
                logger?.LogWarning(
                    "Retry unresolved stopped at {ObjectName}: an online source could not be reached. " +
                    "The remaining names are left for the next run", name);
                stopped = true;
                break;
            }

            switch (result.Stage)
            {
                case TargetResolver.ResolutionStage.Unresolved:
                    // The resolver writes the negative row itself, under its own rules (spec 9.6:
                    // only a clean no-match from every source consulted is negative-cached), so
                    // this service never writes one.
                    stillUnresolved++;
                    break;

                case TargetResolver.ResolutionStage.Cache:
                case TargetResolver.ResolutionStage.Offline:
                case TargetResolver.ResolutionStage.Simbad:
                case TargetResolver.ResolutionStage.Sesame:
                // Task 7's sixth stage, decided here explicitly (FIXER LIST item 4): the resolver
                // created a user_defined target for a solar-system name, so the retry counts it
                // resolved and assigns its frames exactly as for any other created target. This is
                // the run that rescues the names an older negative cache row was hiding, because
                // step 1 of the pipeline still short-circuits on one and Run cleared every such
                // row before the loop.
                case TargetResolver.ResolutionStage.SolarSystem:
                    if (result.TargetId is { } targetId)
                    {
                        resolved++;
                        frames += UnresolvedObjects.AssignFrames(connection, name, targetId);
                    }
                    else
                    {
                        // Unreachable through TargetResolver with createIfMissing true: there is
                        // nothing to attach the frames to, so the name is left for the next run.
                        logger?.LogWarning(
                            "Retry unresolved could not create a target for {ObjectName}; it stays unresolved",
                            name);
                        stillUnresolved++;
                    }

                    break;

                default:
                    // A seventh stage would be a decision its own task has to make explicitly
                    // here; filing an unknown stage under one of these arms would silently
                    // miscount the run.
                    throw new NotSupportedException(
                        $"Retry unresolved has no rule for resolution stage {result.Stage}");
            }
        }

        var outcome = new RetryOutcome(cleared, examined, resolved, frames, stillUnresolved, stopped);

        // Ruling Q21: one user_action event per run, carrying the counts.
        new ActivityRepository(connectionString).EmitStandalone(
            category: "user_action", severity: "info", eventType: "retry_unresolved",
            message: $"Retried {examined} unresolved name{Plural(examined)}: {resolved} resolved, "
                + $"{frames} frame{Plural(frames)} assigned, {stillUnresolved} still unresolved, "
                + $"{cleared} negative cache row{Plural(cleared)} cleared",
            // snake_case, like every other details document in this solution: the record's own
            // PascalCase members would be the only camel-shaped keys in activity_events.
            details: new
            {
                negative_rows_cleared = cleared,
                names_examined = examined,
                names_resolved = resolved,
                frames_assigned = frames,
                names_still_unresolved = stillUnresolved,
                stopped_on_network_failure = stopped,
            });

        return outcome;
    }

    private static string Plural(int count) => count == 1 ? "" : "s";
}
