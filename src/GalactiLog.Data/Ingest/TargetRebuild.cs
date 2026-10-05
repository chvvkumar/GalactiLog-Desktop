using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalactiLog.Data.Ingest;

/// <summary>
/// Spec 12.7's "rebuild targets": clear every non-user-defined frame assignment, then re-resolve
/// each distinct unresolved <c>OBJECT</c> name in spec 9.6's cache-only mode. No network call is
/// made. Database rows only: nothing here touches the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Shaped exactly like <see cref="UnresolvedRetry"/>, because it is the same loop run for a
/// different reason: the lease, the short-lived tracking context, the
/// <c>UnresolvedObjects.Read</c> name list and the <c>UnresolvedObjects.AssignFrames</c>
/// assignment are the same code in both (design-lessons rule 1). What differs is the first three
/// steps, which put every non-user-defined frame back into the unresolved pool before the loop
/// starts.
/// </para>
/// <para>
/// <b>Cache-only, unlike the web source.</b> <c>tasks_target_rebuild.py::rebuild_targets</c> calls
/// the full SIMBAD network path and sleeps 0.3 s per name as a rate limit; the web's cache-only
/// action is a separate endpoint, <c>smart_rebuild_targets</c>. Spec 12.7 merges the two
/// behaviours under one name and says this action runs "in <c>skipSimbad</c> cache-only mode per
/// 9.6", and the roadmap's row 7 Verify line says it "performs no network call". The resolution
/// arrives as <paramref name="resolveCacheOnly"/>, which <c>AppHost</c> binds with
/// <c>skipOnline: true</c>; with no network there is no rate limit to respect either, for the
/// same reason <see cref="UnresolvedRetry"/> dropped the sleep.
/// </para>
/// <para>
/// <b>This rebuild deletes no <c>targets</c> row, and that is deliberate.</b> The web source runs
/// <c>DELETE FROM targets WHERE user_defined = FALSE</c> as its phase 1.
/// <c>TRACKING.md</c> section 6 item 15: <c>merge_manifests.loser_id</c> is
/// <c>ON DELETE CASCADE</c> to <c>targets</c>, so deleting a target silently deletes the manifests
/// that record merges into it, and an undo that would have restored those frames is gone with no
/// trace and no warning. The delete is not needed for correctness either:
/// <see cref="TargetResolver"/> reuses an existing target row by identity (spec 9.5), so
/// re-resolving a name lands back on the row it already had, and a target left with no frames is
/// harmless because the dashboard groups by frames (spec 12.2). The alternative (consume the
/// manifests first and say so) is a migration and a data-loss conversation, recorded in
/// <c>questions.md</c> Q26 and ruled against. A future reader will check the web source and find
/// it says otherwise: this paragraph is why.
/// </para>
/// <para>
/// A target marked <c>user_defined</c> keeps its frames outright (step 2's exemption), and a
/// target marked <c>name_locked</c> is never re-derived: <see cref="TargetResolver"/>'s step 2
/// returns an existing target by name without mutating a single column, which is
/// <c>smart_rebuild_targets</c>' phase 4 rule reached from the other direction.
/// </para>
/// <para>
/// It writes <c>images.resolved_target_id</c> and deletes <c>merge_candidates</c> rows. It is not
/// a fourth App-layer writer of <c>targets</c> rows (<c>TRACKING.md</c> section 6 item 14): the
/// only <c>targets</c> write in a rebuild is whatever <see cref="TargetResolver"/> performs on its
/// own account, exactly as during a scan.
/// </para>
/// </remarks>
/// <param name="connectionString">The DI <c>DatabaseConnectionString.Value</c>. This service opens
/// its own short-lived contexts, like <c>ActivityRepository</c> and
/// <see cref="UnresolvedRetry"/>.</param>
/// <param name="resolveCacheOnly">Normally
/// <c>TargetResolver.Resolve(name, createIfMissing: true, dryRun: false, skipOnline: true, ct)</c>.
/// <c>skipOnline</c> is spec 9.6's <c>skipSimbad</c>. A delegate, not the resolver, so a test
/// drives every outcome with no network at all (design-spec 18.2) and the roadmap's "performs no
/// network call" assertion is testable rather than asserted by inspection.</param>
/// <param name="tryBeginResolution">Takes the resolution lease, or returns null when a scan is
/// running or another pass holds it. Required, not optional: this is the choke point, so a caller
/// cannot forget the gate (design-lessons rule 2). It matters more here than it does for the
/// retry, because this action begins by unassigning frames; refused, it has touched
/// nothing.</param>
/// <param name="logger">Per-name failures are logged, never thrown: one name the resolver rejects
/// must not abandon the rest of the run.</param>
public sealed class TargetRebuild(
    string connectionString,
    Func<string, CancellationToken, TargetResolver.ResolutionResult> resolveCacheOnly,
    Func<IDisposable?> tryBeginResolution,
    ILogger? logger = null)
{
    /// <summary>The web source reports progress every five names and on the last one. Kept, so
    /// the progress line moves on a small library without flooding a 10 Hz surface on a large
    /// one.</summary>
    private const int ReportEvery = 5;

    /// <summary>Why a run ended. <see cref="RebuildStatus.ScanInProgress"/> means nothing at all
    /// was touched: no frame unassigned, no candidate deleted, no cache row cleared, no name
    /// examined.</summary>
    public enum RebuildStatus { Completed, ScanInProgress }

    /// <summary>What one run did, for the caller's summary line and the activity event.</summary>
    /// <param name="FramesUnassigned">LIGHT frames whose <c>resolved_target_id</c> was cleared
    /// because their target is not <c>user_defined</c>. Calibration frames are never unassigned:
    /// the loop can only reassign LIGHT frames, so unassigning one would orphan it for good.</param>
    /// <param name="CandidatesCleared">Every <c>merge_candidates</c> row. Candidates are
    /// suggestions, not history.</param>
    /// <param name="NamesExamined">Distinct unresolved <c>OBJECT</c> names whose body started,
    /// including one skipped by a resolver rejection.</param>
    /// <param name="NamesResolved">Names that resolved to a target from the cache.</param>
    /// <param name="FramesAssigned">LIGHT frames moved onto those targets.</param>
    /// <param name="NamesStillUnresolved">Names the cache had no answer for. Nothing was asked of
    /// the network, so these are "not in the cache", not "does not exist".</param>
    /// <param name="Status">Why the run ended.</param>
    public sealed record RebuildOutcome(
        int FramesUnassigned,
        int CandidatesCleared,
        int NamesExamined,
        int NamesResolved,
        int FramesAssigned,
        int NamesStillUnresolved,
        RebuildStatus Status = RebuildStatus.Completed);

    /// <param name="report">(step, totalSteps, message), forwarded to the caller's progress
    /// surface. Called every <see cref="ReportEvery"/> names and on the last one.</param>
    /// <param name="ct">Cancellation ends the run with <see cref="OperationCanceledException"/>
    /// and keeps every assignment already committed.</param>
    public RebuildOutcome Run(Action<int, int, string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ct.ThrowIfCancellationRequested();

        // Taken before anything is written, so a refused rebuild has unassigned no frame and
        // deleted no candidate. Held for the whole run, so a scan cannot start underneath a loop
        // that is creating targets and clearing the negative cache (spec 5.1, 9.6; Phase 7 fixer
        // item 1). Released when Run returns, including every exception path.
        using var lease = tryBeginResolution();
        if (lease is null)
        {
            logger?.LogInformation("Rebuild targets refused: a scan is already running");
            return new RebuildOutcome(0, 0, 0, 0, 0, 0, RebuildStatus.ScanInProgress);
        }

        // One tracking context, so PragmaConnectionInterceptor stays in the path and spec 5.1's
        // busy timeout applies to the statements below. Nothing here writes through EF.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var framesUnassigned = UnassignNonUserDefinedFrames(connection);
        var candidatesCleared = DeleteEveryMergeCandidate(connection);

        // Matches the web's _redis.delete("target_resolver:negative"): a name suppressed by the
        // 7 day negative TTL is really re-consulted. Positive rows are never touched here, which
        // is what spec 9.6 means by "Cleared only by the reset-database action" -- and it is also
        // what makes a cache-only rebuild able to resolve anything at all.
        //
        // Constructed here rather than resolved, deliberately and counter-free (Task 8 review
        // minor 6). GalactiLog.Data has no container to resolve from, and this instance is used
        // for ClearNegative alone: the per-process resolver hit and miss counters are recorded
        // only in Get, so a repository that never reads the cache has nothing to report and
        // passing counters: null loses no figure. "One CatalogCacheRepository per process" is
        // therefore a claim about the resolution stack, which AppHost owns, and not about this
        // maintenance path.
        new CatalogCacheRepository(connectionString).ClearNegative();

        var names = UnresolvedObjects.Read(connection).Select(row => row.Name).ToList();

        var examined = 0;
        var resolved = 0;
        var frames = 0;
        var stillUnresolved = 0;

        for (var index = 0; index < names.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var name = names[index];
            if ((index + 1) % ReportEvery == 0 || index == names.Count - 1)
            {
                report(index + 1, names.Count, $"Rebuilding {index + 1}/{names.Count} names...");
            }

            examined++;

            TargetResolver.ResolutionResult result;
            try
            {
                result = resolveCacheOnly(name, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One name the resolver could not answer for. Counted and skipped, never thrown:
                // the same per-name isolation UnresolvedRetry and DuplicateDetector apply, and
                // broader than theirs because a cache-only resolve has no network failure mode to
                // distinguish from a genuine rejection.
                logger?.LogWarning(ex, "Rebuild targets skipped {ObjectName}: resolution failed", name);
                stillUnresolved++;
                continue;
            }

            if (result.TargetId is { } targetId && result.Stage != TargetResolver.ResolutionStage.Unresolved)
            {
                resolved++;
                frames += UnresolvedObjects.AssignFrames(connection, name, targetId);
            }
            else
            {
                // The cache had no answer. No network was consulted, so the resolver writes no
                // negative row for it either (spec 9.6: only a clean no-match from every source
                // consulted is negative-cached), and the name is simply left unresolved.
                stillUnresolved++;
            }
        }

        if (names.Count == 0)
        {
            report(0, 0, "No unresolved names to rebuild");
        }

        return new RebuildOutcome(
            framesUnassigned, candidatesCleared, examined, resolved, frames, stillUnresolved);
    }

    // The web's phase 1, minus its "OR resolved_target_id IS NULL" clause, which is a no-op in
    // SQL: a row whose assignment is already NULL cannot be cleared again, and the RETURNING count
    // it produced in the source was only used to log a number.
    //
    // user_defined targets are exempt because they are the user's own rows, created by hand or by
    // the solar-system classifier and never re-derived from a catalogue (spec 9.8). Every other
    // LIGHT frame goes back into the unresolved pool the loop then walks.
    //
    // LIGHT ONLY, and this is a second deliberate divergence from the web source (review finding
    // I2). ScanWriter assigns resolved_target_id to any image carrying a non-empty OBJECT with no
    // image_type gate, so a calibration frame that happened to carry an OBJECT card has a target
    // too (MergePreviewQuery says so outright). UnresolvedObjects.AssignFrames, the one frame
    // assignment in this solution, restores LIGHT frames only, because
    // SqlFragments.UnresolvedObjectCounts is the one grouping and it is LIGHT-only by spec 9.7.
    // Unassigning a calibration frame here would therefore orphan it permanently: nothing in the
    // application would ever put it back short of re-ingesting the file. The rule is to unassign
    // nothing the loop cannot restore.
    private static int UnassignNonUserDefinedFrames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE images SET resolved_target_id = NULL
            WHERE resolved_target_id IS NOT NULL
              AND {SqlFragments.LightFrameOnly}
              AND resolved_target_id NOT IN (SELECT id FROM targets WHERE user_defined = 1);
            """;
        return command.ExecuteNonQuery();
    }

    // The web's phase 1 second statement. A candidate is a suggestion the dedup pass produced from
    // the assignments this run has just thrown away, so keeping them would offer the user merges
    // computed against a library state that no longer exists. Unlike a manifest, nothing is lost:
    // the next scan's duplicate detection recomputes them.
    private static int DeleteEveryMergeCandidate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM merge_candidates;";
        return command.ExecuteNonQuery();
    }
}
