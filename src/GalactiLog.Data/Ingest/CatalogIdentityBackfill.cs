using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalactiLog.Data.Ingest;

/// <summary>
/// Spec 12.7's "catalog identity backfill" (PAR-007): re-runs the identity matcher over unlinked
/// frames. For each distinct <c>OBJECT</c> string carried by LIGHT frames with no
/// <c>resolved_target_id</c>, most frames first, it resolves cache-only and, when the result
/// matches an existing target by identity (spec 9.5), links that name's unresolved LIGHT frames to
/// it. Database rows only: nothing here touches the filesystem, and no network call is made.
/// </summary>
/// <remarks>
/// <para>
/// The fourth occurrence of the "cache-only pass over unresolved names under the resolution lease"
/// shape, after <see cref="UnresolvedRetry"/>, <see cref="TargetRebuild"/> and
/// <see cref="SmartRebuild"/>: the same lease, the same one short-lived tracking context, the same
/// <c>UnresolvedObjects.Read</c> name list and the same <c>UnresolvedObjects.AssignFrames</c>
/// assignment (design-lessons rule 1).
/// </para>
/// <para>
/// <b>It creates no target, and that is the load-bearing difference from
/// <see cref="TargetRebuild"/></b>, which binds its delegate to
/// <c>Resolve(createIfMissing: true)</c>. The delegate here is
/// <c>TargetResolver.ResolveIdentity</c>, which runs on a non-tracking context, never writes a
/// <c>targets</c> row and always returns a null <c>TargetId</c>: a name that resolves to an
/// identity no target carries cannot create one even by accident. A name that resolves to nothing,
/// or to no existing target, is left orphaned for duplicate detection and the Create target form
/// on the Targets tab.
/// </para>
/// <para>
/// <b>The identity match is spec 9.5's one rule</b>, <c>TargetResolver.MatchTargetByIdentity</c>,
/// widened from private to internal for this caller rather than copied. A backfill that disagreed
/// with the scan by one normalization step would link frames the scan would not.
/// </para>
/// <para>
/// <b>Not ported</b>: the web task's first phase, which repairs quote-corrupted SIMBAD cache rows
/// from a data defect this port never had (spec 12.7, departure 7 of <c>task1-report.md</c>).
/// </para>
/// </remarks>
/// <param name="connectionString">The DI <c>DatabaseConnectionString.Value</c>, the raw string the
/// rest of this namespace takes. This service opens its own short-lived context.</param>
/// <param name="resolveIdentityCacheOnly">Normally
/// <c>TargetResolver.ResolveIdentity(name, skipOnline: true, ct)</c>. <c>skipOnline</c> is spec
/// 9.6's <c>skipSimbad</c>. A delegate, not the resolver, so the roadmap's "no network call (a
/// resolver stub asserts none)" is an assertion rather than an inspection.</param>
/// <param name="tryBeginResolution">Takes the resolution lease, or returns null when a scan is
/// running or another pass holds it. Required, not optional: the choke point is here so a caller
/// cannot forget the gate (design-lessons rule 2). Refused, the run has touched nothing.</param>
/// <param name="logger">Per-name failures are logged, never thrown: one name the resolver rejects
/// must not abandon the rest of the run.</param>
public sealed class CatalogIdentityBackfill(
    string connectionString,
    Func<string, CancellationToken, TargetResolver.ResolutionResult> resolveIdentityCacheOnly,
    Func<IDisposable?> tryBeginResolution,
    ILogger? logger = null)
{
    /// <summary>The same cadence <see cref="TargetRebuild"/> reports on: every five names and on
    /// the last one.</summary>
    private const int ReportEvery = 5;

    /// <summary>Why a run ended. <see cref="BackfillStatus.ScanInProgress"/> means nothing at all
    /// was touched: no name examined and no frame moved.</summary>
    public enum BackfillStatus { Completed, ScanInProgress }

    /// <summary>What one run did, for the caller's summary line and the activity event.</summary>
    /// <param name="LinkedNames">Distinct <c>OBJECT</c> strings that matched an existing target by
    /// identity.</param>
    /// <param name="LinkedFrames">Unresolved LIGHT frames moved onto those targets.</param>
    /// <param name="SkippedNames">Names that resolved to nothing, or to no existing target. They
    /// are left orphaned, never created.</param>
    /// <param name="Status">Why the run ended.</param>
    public sealed record BackfillOutcome(
        int LinkedNames,
        int LinkedFrames,
        int SkippedNames,
        BackfillStatus Status = BackfillStatus.Completed);

    /// <param name="report">(step, totalSteps, message), forwarded to the caller's progress
    /// surface. Called every <see cref="ReportEvery"/> names and on the last one.</param>
    /// <param name="ct">Cancellation ends the run with <see cref="OperationCanceledException"/>
    /// and keeps every link already committed.</param>
    public BackfillOutcome Run(Action<int, int, string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ct.ThrowIfCancellationRequested();

        // Taken before anything is written, so a refused run has linked no frame. Held for the
        // whole run, so a scan cannot start underneath it (spec 9.7's mutual exclusion).
        using var lease = tryBeginResolution();
        if (lease is null)
        {
            logger?.LogInformation("Catalog identity backfill refused: a scan is already running");
            return new BackfillOutcome(0, 0, 0, BackfillStatus.ScanInProgress);
        }

        // One tracking context, so PragmaConnectionInterceptor stays in the path, spec 5.1's busy
        // timeout applies, and the alias MatchTargetByIdentity appends is persisted by the
        // SaveChanges below.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        // "Most frames first" is UnresolvedObjects.Read's own ordering (most frames first, ties by
        // name), so this pass inherits spec 12.7's order rather than sorting again.
        var names = UnresolvedObjects.Read(connection).Select(row => row.Name).ToList();
        var targets = new TargetRepository(context);

        var linkedNames = 0;
        var linkedFrames = 0;
        var skipped = 0;

        for (var index = 0; index < names.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var name = names[index];
            if ((index + 1) % ReportEvery == 0 || index == names.Count - 1)
            {
                report(index + 1, names.Count, $"Backfilling {index + 1}/{names.Count} names...");
            }

            TargetResolver.ResolutionResult result;
            try
            {
                result = resolveIdentityCacheOnly(name, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One name the resolver could not answer for. Counted and skipped, never thrown:
                // the same per-name isolation TargetRebuild and UnresolvedRetry apply.
                logger?.LogWarning(ex, "Catalog identity backfill skipped {ObjectName}: resolution failed", name);
                skipped++;
                continue;
            }

            if (result.Identity is not { } identity)
            {
                // The cache had no answer. Nothing was asked of the network, so this is "not in
                // the cache", not "does not exist", and the name stays orphaned.
                skipped++;
                continue;
            }

            // Spec 9.5's one identity rule, reached rather than repeated. It appends the
            // panel-stripped uppercase incoming name as an alias on a hit, which spec 9.7 permits
            // even on a name-locked target, and changes no identity column of one.
            var match = TargetResolver.MatchTargetByIdentity(targets, identity, name);
            if (match is null)
            {
                skipped++;
                continue;
            }

            linkedNames++;
            linkedFrames += UnresolvedObjects.AssignFrames(connection, name, match.Id);
            context.SaveChanges();
        }

        if (names.Count == 0)
        {
            report(0, 0, "No unlinked names to backfill");
        }

        return new BackfillOutcome(linkedNames, linkedFrames, skipped);
    }
}
