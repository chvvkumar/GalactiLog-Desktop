using System.Text.Json;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalactiLog.Data.Ingest;

/// <summary>
/// Spec 12.7's "smart rebuild" (PAR-007): repairs target data from the local database and the
/// resolver cache with no network call at all, in six passes, each reporting its own count, then
/// runs duplicate detection inline. Database rows only: nothing here touches the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Shaped exactly like <see cref="TargetRebuild"/> and <see cref="UnresolvedRetry"/>, because it
/// is the same class of work: the lease taken before any write, the one short-lived tracking
/// context, the <c>UnresolvedObjects.Read</c> name list and the
/// <c>UnresolvedObjects.AssignFrames</c> assignment are the same code in all three
/// (design-lessons rule 1). This is the third occurrence of that shape and it reuses the seam
/// rather than restating it.
/// </para>
/// <para>
/// <b>No second copy of a resolution rule.</b> Pass 4 curates through
/// <c>TargetResolver.Curate</c> itself, widened from private to internal for this caller, rather
/// than re-deriving spec 9.4.5's three steps here; a second curation that disagreed by one step
/// would give a target a name no resolution would ever have given it.
/// </para>
/// <para>
/// <b>It creates no <c>targets</c> row and deletes none</b>, for the reason
/// <see cref="TargetRebuild"/> deletes none: <c>merge_manifests.loser_id</c> is
/// <c>ON DELETE CASCADE</c>, so deleting a target silently takes the manifests that record merges
/// into it, and <see cref="TargetResolver"/> reuses rows by identity so a delete buys nothing.
/// </para>
/// <para>
/// <b>Not ported</b>, both named in spec 12.7 and both departure 7 of <c>task1-report.md</c>: the
/// web source's mosaic panel membership recomputation, because this port has no panels (spec
/// 19.1), and its queued follow-up tasks, because this port has no queue. Duplicate detection is
/// the one follow-up that mattered and it runs inline at the end of this same action instead.
/// </para>
/// </remarks>
/// <param name="connectionString">The DI <c>DatabaseConnectionString.Value</c>, the raw string the
/// rest of this namespace takes. This service opens its own short-lived context.</param>
/// <param name="resolveCacheOnly">Normally
/// <c>TargetResolver.Resolve(name, createIfMissing, dryRun: false, skipOnline: true, ct)</c>.
/// <c>skipOnline</c> is spec 9.6's <c>skipSimbad</c>. A delegate, not the resolver, so spec 12.7's
/// "a resolver stub asserts the pass makes no network call" is an assertion rather than an
/// inspection. It carries the <c>createIfMissing</c> flag because its one consumer is the inline
/// <see cref="DuplicateDetector"/>, which passes false for its probe and true for its create, and
/// a delegate that dropped the flag would turn that probe into a create. None of the six passes
/// resolves anything at all.</param>
/// <param name="tryBeginResolution">Takes the resolution lease, or returns null when a scan is
/// running or another pass holds it. Required, not optional: the choke point is here so a caller
/// cannot forget the gate (design-lessons rule 2). Refused, the run has touched nothing.</param>
/// <param name="logger">Per-row failures are logged, never thrown: one unreadable cache payload
/// must not abandon the rest of the run.</param>
public sealed class SmartRebuild(
    string connectionString,
    Func<string, bool, CancellationToken, TargetResolver.ResolutionResult> resolveCacheOnly,
    Func<IDisposable?> tryBeginResolution,
    ILogger? logger = null)
{
    /// <summary>Progress is reported every five units inside the two passes that have a countable
    /// unit, and once per pass boundary. Never per row: the scan's own throttle is 100 ms and this
    /// pass has none.</summary>
    private const int ReportEvery = 5;

    /// <summary>The denominator of every pass-boundary progress report.</summary>
    private const int TotalPasses = 6;

    /// <summary>A guard against a <c>merged_into_id</c> cycle, which no writer in this application
    /// can produce but a hand-edited database can.</summary>
    private const int MaxMergeChain = 64;

    /// <summary>The <c>merge_candidates.method</c> value the orphan outcome writes. An orphan row
    /// has a null <c>suggested_target_id</c> because it never named a target, not because the
    /// target it named went away, so pass 6 leaves it alone.</summary>
    private const string OrphanMethod = "orphan";

    /// <summary>Why a run ended. <see cref="SmartRebuildStatus.ScanInProgress"/> means nothing at
    /// all was touched: no frame redirected, no alias added, no candidate deleted.</summary>
    public enum SmartRebuildStatus { Completed, ScanInProgress }

    /// <summary>What one run did: one count per spec 12.7 pass, in the spec's own order, plus what
    /// the inline duplicate detection produced.</summary>
    /// <param name="FramesRedirected">Pass 1. Frames pointing at a merged-away target, moved to
    /// the end of that merge chain.</param>
    /// <param name="FramesLinkedByAlias">Pass 2. Unresolved LIGHT frames whose normalized
    /// <c>OBJECT</c> is an alias of an active target, linked to it.</param>
    /// <param name="AliasesAdded">Pass 3. Distinct normalized <c>OBJECT</c> strings seen on a
    /// target's own frames and missing from its <c>aliases</c>, appended.</param>
    /// <param name="IdentitiesReDerived">Pass 4. Active targets that are neither
    /// <c>name_locked</c> nor <c>user_defined</c> whose identity columns a positive
    /// <c>catalog_cache</c> row changed.</param>
    /// <param name="NamesRebuilt">Pass 5. Remaining such targets whose <c>primary_name</c>
    /// disagreed with their <c>catalog_id</c> and <c>common_name</c>.</param>
    /// <param name="StaleCandidatesRemoved">Pass 6. <c>merge_candidates</c> rows whose
    /// <c>suggested_target_id</c> names a merged-away or absent row.</param>
    /// <param name="DuplicateCandidatesWritten">The inline duplicate detection's suggestions
    /// (spec 9.7). Not one of the six passes; departure 6's replacement for the queued
    /// follow-up.</param>
    /// <param name="Status">Why the run ended.</param>
    public sealed record SmartRebuildOutcome(
        int FramesRedirected,
        int FramesLinkedByAlias,
        int AliasesAdded,
        int IdentitiesReDerived,
        int NamesRebuilt,
        int StaleCandidatesRemoved,
        int DuplicateCandidatesWritten,
        SmartRebuildStatus Status = SmartRebuildStatus.Completed);

    /// <param name="report">(step, totalSteps, message), forwarded to the caller's progress
    /// surface. One call per pass boundary naming the pass, plus one per
    /// <see cref="ReportEvery"/> units inside the two passes that have a countable unit.</param>
    /// <param name="ct">Cancellation ends the run with <see cref="OperationCanceledException"/>
    /// and keeps every repair already committed.</param>
    public SmartRebuildOutcome Run(Action<int, int, string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ct.ThrowIfCancellationRequested();

        // Taken before anything is written, so a refused run has redirected no frame and deleted
        // no candidate, and held for the whole run so a scan cannot start underneath it (spec 5.1,
        // 9.6, and 9.7's mutual exclusion). Released when Run returns, every exception path
        // included.
        using var lease = tryBeginResolution();
        if (lease is null)
        {
            logger?.LogInformation("Smart rebuild refused: a scan is already running");
            return new SmartRebuildOutcome(0, 0, 0, 0, 0, 0, 0, SmartRebuildStatus.ScanInProgress);
        }

        // One tracking context, so PragmaConnectionInterceptor stays in the path and spec 5.1's
        // busy timeout applies to every statement below.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        report(1, TotalPasses, "Redirecting frames from merged targets...");
        var framesRedirected = RedirectMergedAwayFrames(connection, ct);

        report(2, TotalPasses, "Linking unresolved names to target aliases...");
        var framesLinked = LinkUnresolvedNamesByAlias(context, connection, report, ct);

        report(3, TotalPasses, "Backfilling aliases from frame names...");
        var aliasesAdded = BackfillAliasesFromFrames(context, connection, ct);

        report(4, TotalPasses, "Re-deriving identities from the lookup cache...");
        var (identitiesReDerived, handledByPassFour) = ReDeriveIdentities(context, connection, report, ct);

        report(5, TotalPasses, "Rebuilding names from catalogue identities...");
        var namesRebuilt = RebuildNames(context, handledByPassFour, ct);

        report(6, TotalPasses, "Removing stale merge suggestions...");
        var staleCandidates = DeleteStaleCandidates(connection);

        ct.ThrowIfCancellationRequested();

        // Departure 6: the web source queues duplicate detection as a follow-up task. This port
        // has no queue, so spec 12.7 says it "runs inline at the end of the same action instead".
        // Its own instance, because DuplicateDetector owns its contexts and its activity event and
        // a second copy of the trigram pass is exactly what design-lessons rule 1 forbids.
        var dedup = new DuplicateDetector(connectionString, resolveCacheOnly, logger).Run(report, ct);

        return new SmartRebuildOutcome(
            framesRedirected, framesLinked, aliasesAdded, identitiesReDerived, namesRebuilt,
            staleCandidates, dedup.CandidatesWritten);
    }

    // ---- pass 1: merged-away redirect -----------------------------------------------------

    // Spec 12.7: "frames pointing at a merged-away target are redirected to that merge's winner".
    // A chain of merges resolves to the end of the chain, and a cycle (which no writer here can
    // produce, but a hand-edited database can) leaves its frames alone rather than looping.
    private static int RedirectMergedAwayFrames(SqliteConnection connection, CancellationToken ct)
    {
        var mergedInto = new Dictionary<Guid, Guid>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, merged_into_id FROM targets WHERE merged_into_id IS NOT NULL;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                mergedInto[reader.GetGuid(0)] = reader.GetGuid(1);
            }
        }

        var redirected = 0;
        foreach (var loser in mergedInto.Keys)
        {
            ct.ThrowIfCancellationRequested();

            if (WinnerOf(mergedInto, loser) is not { } winner)
            {
                continue;
            }

            using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE images SET resolved_target_id = @winner WHERE resolved_target_id = @loser;";
            update.Parameters.Add(new SqliteParameter("@winner", winner));
            update.Parameters.Add(new SqliteParameter("@loser", loser));
            redirected += update.ExecuteNonQuery();
        }

        return redirected;
    }

    // The end of the merge chain, or null when the chain closes on itself or runs longer than any
    // real merge history could.
    private static Guid? WinnerOf(Dictionary<Guid, Guid> mergedInto, Guid loser)
    {
        var seen = new HashSet<Guid> { loser };
        var current = loser;

        for (var hop = 0; hop < MaxMergeChain; hop++)
        {
            if (!mergedInto.TryGetValue(current, out var next))
            {
                return current == loser ? null : current;
            }

            if (!seen.Add(next))
            {
                return null;
            }

            current = next;
        }

        return null;
    }

    // ---- pass 2: alias link ---------------------------------------------------------------

    // Spec 12.7: "unresolved LIGHT frames whose normalized OBJECT equals an alias of an active
    // target are linked to it". Aliases are stored uppercase and panel-stripped, so the incoming
    // OBJECT is normalized the same way before the comparison. Only LIGHT frames move, because
    // UnresolvedObjects.AssignFrames is the one frame assignment in this solution and it is
    // LIGHT-only by spec 9.7.
    //
    // ponytail: TargetRepository.FindByAliasExact materializes the active targets once per name,
    // which is O(names x targets) client-side. That method is the one alias-containment rule in
    // this solution and an index built here would be a second copy of it; build the index inside
    // TargetRepository if a library ever makes this pass slow.
    private static int LinkUnresolvedNamesByAlias(
        GalactiLogContext context, SqliteConnection connection,
        Action<int, int, string> report, CancellationToken ct)
    {
        var names = UnresolvedObjects.Read(connection).Select(row => row.Name).ToList();
        var targets = new TargetRepository(context);
        var frames = 0;

        for (var index = 0; index < names.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            if ((index + 1) % ReportEvery == 0 || index == names.Count - 1)
            {
                report(index + 1, names.Count, $"Linking {index + 1}/{names.Count} unresolved names...");
            }

            var normalized = NameNormalizer.StripPanel(NameNormalizer.Normalize(names[index]));
            if (normalized.Length == 0)
            {
                continue;
            }

            if (targets.FindByAliasExact(normalized) is { } target)
            {
                frames += UnresolvedObjects.AssignFrames(connection, names[index], target.Id);
            }
        }

        return frames;
    }

    // ---- pass 3: alias backfill -----------------------------------------------------------

    // Spec 12.7: "every distinct normalized OBJECT seen on a target's own frames that is missing
    // from that target's aliases is added to them". Spec 9.7 permits this write on a name-locked
    // and on a user-defined target explicitly ("may add an alias to it"), so neither is exempt.
    private static int BackfillAliasesFromFrames(
        GalactiLogContext context, SqliteConnection connection, CancellationToken ct)
    {
        var seen = new List<(Guid TargetId, string ObjectName)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                $"""
                SELECT i.resolved_target_id, json_extract(i.raw_headers,'$.OBJECT') AS obj
                FROM images i
                WHERE i.resolved_target_id IS NOT NULL
                  AND i.{SqlFragments.LightFrameOnly}
                  AND obj IS NOT NULL
                  AND obj <> ''
                GROUP BY i.resolved_target_id, obj;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                // ReadText, not GetString: json_extract yields a SQLite number for an unquoted
                // numeric OBJECT card (Phase 5 fix F1).
                var name = SqlReaders.ReadText(reader, 1);
                if (!string.IsNullOrEmpty(name))
                {
                    seen.Add((reader.GetGuid(0), name));
                }
            }
        }

        var repository = new TargetRepository(context);
        var added = 0;

        foreach (var (targetId, objectName) in seen)
        {
            ct.ThrowIfCancellationRequested();

            var alias = NameNormalizer.StripPanel(NameNormalizer.Normalize(objectName));
            if (alias.Length == 0 || context.Targets.Find(targetId) is not { } target)
            {
                continue;
            }

            // Phase 14B fixer, fixer list item 29 (task4-review P3). The web carries
            // "AND t.merged_into_id IS NULL" on this pass and this copy did not. A merged-away
            // row keeps its frames only through a merged_into_id cycle, which WinnerOf refuses to
            // follow, and growing a dead row's alias list is not a repair.
            if (target.MergedIntoId is not null)
            {
                continue;
            }

            // AddAliasIfMissing is the one append rule (case-insensitive dedup) and it returns
            // nothing, so the document itself says whether anything was appended.
            var before = target.Aliases;
            repository.AddAliasIfMissing(target, alias);
            if (!string.Equals(before, target.Aliases, StringComparison.Ordinal))
            {
                added++;
            }
        }

        context.SaveChanges();
        return added;
    }

    // ---- pass 4: identity re-derive -------------------------------------------------------

    // Spec 12.7: "for each active target that is neither name_locked nor user_defined, a positive
    // catalog_cache row for its identity re-derives catalog_id, common_name and primary_name
    // through the same curation a resolution uses (section 9.4.5)". Ruling D5 is written into the
    // pass rather than relied on from the wording, and NameLockedPassTests pins it.
    //
    // The cache is read once into a dictionary rather than per target: a per-target lookup through
    // CatalogCacheRepository opens one context per key and this pass has several keys each.
    private (int Changed, HashSet<Guid> Handled) ReDeriveIdentities(
        GalactiLogContext context, SqliteConnection connection,
        Action<int, int, string> report, CancellationToken ct)
    {
        var payloads = ReadPositiveCachePayloads(connection);
        var targets = RepairableTargets(context);
        var taken = ActivePrimaryNames(context);
        var handled = new HashSet<Guid>();
        var changed = 0;

        for (var index = 0; index < targets.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            if ((index + 1) % ReportEvery == 0 || index == targets.Count - 1)
            {
                report(index + 1, targets.Count, $"Re-deriving {index + 1}/{targets.Count} identities...");
            }

            var target = targets[index];
            if (FindCachedIdentity(payloads, target) is not { } identity)
            {
                continue;
            }

            // primary_name is unique among unmerged targets (spec 5.3), so a re-derived name that
            // another active target already carries is left alone rather than failing the run.
            var collides = !string.Equals(identity.PrimaryName, target.PrimaryName, StringComparison.Ordinal)
                && taken.Contains(identity.PrimaryName);

            var rewrites =
                !string.Equals(target.CatalogId, identity.CatalogId, StringComparison.Ordinal)
                || !string.Equals(target.CommonName, identity.CommonName, StringComparison.Ordinal)
                || (!collides && !string.Equals(target.PrimaryName, identity.PrimaryName, StringComparison.Ordinal));

            // Handled either way: the cache answered for this target, so pass 5 does not
            // second-guess a name this curation just confirmed.
            handled.Add(target.Id);
            if (!rewrites)
            {
                continue;
            }

            target.CatalogId = identity.CatalogId;
            target.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId(identity.CatalogId);
            target.CommonName = identity.CommonName;

            if (collides)
            {
                logger?.LogInformation(
                    "Smart rebuild kept {PrimaryName}: the re-derived name is already taken", target.PrimaryName);
            }
            else
            {
                taken.Remove(target.PrimaryName);
                target.PrimaryName = identity.PrimaryName;
                taken.Add(identity.PrimaryName);
            }

            changed++;
        }

        context.SaveChanges();
        return (changed, handled);
    }

    // Every positive simbad or sesame payload, by its cache key. simbad wins a key both sources
    // hold, which is the order TargetResolver consults them in.
    private static Dictionary<string, string> ReadPositiveCachePayloads(SqliteConnection connection)
    {
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);

        using var read = connection.CreateCommand();
        read.CommandText =
            """
            SELECT key, payload, source FROM catalog_cache
            WHERE negative = 0 AND payload IS NOT NULL AND source IN ('simbad', 'sesame')
            ORDER BY CASE source WHEN 'simbad' THEN 0 ELSE 1 END;
            """;
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var key = SqlReaders.ReadText(reader, 0);
            var payload = SqlReaders.ReadText(reader, 1);
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(payload))
            {
                payloads.TryAdd(key, payload);
            }
        }

        return payloads;
    }

    // The keys a resolution for this target would have written: its catalog id or, failing that,
    // its display name, in the cache's own normalized form. The first positive payload wins.
    private ResolvedIdentity? FindCachedIdentity(
        IReadOnlyDictionary<string, string> payloads, Entities.Target target)
    {
        foreach (var key in IdentityKeys(target))
        {
            if (!payloads.TryGetValue(key, out var payload))
            {
                continue;
            }

            try
            {
                var raw = JsonSerializer.Deserialize<TargetResolver.CachedIdentityPayload>(payload);
                if (raw is not null)
                {
                    // The one curation in this solution, spec 9.4.5, reached rather than repeated.
                    return TargetResolver.Curate(raw);
                }
            }
            catch (JsonException ex)
            {
                logger?.LogWarning(ex, "Smart rebuild skipped an unreadable cache payload for {Key}", key);
            }
        }

        return null;
    }

    /// <summary>
    /// The target's own identity, normalized: its <c>catalog_id</c> when it has one, and its
    /// <c>primary_name</c> otherwise or as well. Never an alias.
    /// </summary>
    /// <remarks>
    /// Phase 14B fixer, fixer list item 2 (Task 4 review escalation 2, ruled to the fixer). The
    /// aliases used to be keys here too, and pass 3 runs immediately before this one and appends
    /// every distinct normalized <c>OBJECT</c> seen on the target's frames to that alias list, so
    /// one mis-assigned frame could supply the cache key that rewrote a target's whole catalogue
    /// identity in the same run. The web's phase 4 looks up exactly one key,
    /// <c>normalize_object_name(target.catalog_id or target.primary_name)</c>, and spec 12.7 says
    /// "a positive catalog_cache row for its identity"; an alias is not the target's identity.
    /// </remarks>
    private static IEnumerable<string> IdentityKeys(Entities.Target target)
    {
        if (!string.IsNullOrWhiteSpace(target.CatalogId))
        {
            yield return NameNormalizer.Normalize(target.CatalogId);
        }

        if (!string.IsNullOrWhiteSpace(target.PrimaryName))
        {
            yield return NameNormalizer.Normalize(target.PrimaryName);
        }
    }

    // ---- pass 5: name rebuild -------------------------------------------------------------

    // Spec 12.7: "each remaining active target that is neither name_locked nor user_defined and
    // whose primary_name disagrees with its catalog_id and common_name has its name rebuilt from
    // them". "Remaining" is one pass 4 did not already handle. AliasCurator.BuildPrimaryName is
    // the same rule a resolution builds a name with.
    private int RebuildNames(GalactiLogContext context, HashSet<Guid> handledByPassFour, CancellationToken ct)
    {
        var taken = ActivePrimaryNames(context);
        var rebuilt = 0;

        foreach (var target in RepairableTargets(context))
        {
            ct.ThrowIfCancellationRequested();

            if (handledByPassFour.Contains(target.Id))
            {
                continue;
            }

            // Nothing to rebuild from: BuildPrimaryName answers "Unknown" for two blanks, and
            // renaming a target that simply has no catalogue identity yet would be a repair that
            // destroys the only name it has.
            if (string.IsNullOrWhiteSpace(target.CatalogId) && string.IsNullOrWhiteSpace(target.CommonName))
            {
                continue;
            }

            var name = AliasCurator.BuildPrimaryName(target.CatalogId, target.CommonName);
            if (string.Equals(name, target.PrimaryName, StringComparison.Ordinal))
            {
                continue;
            }

            if (taken.Contains(name))
            {
                logger?.LogInformation(
                    "Smart rebuild kept {PrimaryName}: the rebuilt name is already taken", target.PrimaryName);
                continue;
            }

            taken.Remove(target.PrimaryName);
            target.PrimaryName = name;
            taken.Add(name);
            rebuilt++;
        }

        context.SaveChanges();
        return rebuilt;
    }

    // Ruling D5, written into both passes that re-derive an identity rather than left to the
    // wording: no automatic pass re-resolves a name-locked or a user-defined target.
    private static List<Entities.Target> RepairableTargets(GalactiLogContext context)
        => [.. context.Targets
            .Where(t => t.MergedIntoId == null && !t.NameLocked && !t.UserDefined)
            .OrderBy(t => t.PrimaryName)];

    private static HashSet<string> ActivePrimaryNames(GalactiLogContext context)
        => [.. context.Targets.Where(t => t.MergedIntoId == null).Select(t => t.PrimaryName)];

    // ---- pass 6: stale candidate sweep ----------------------------------------------------

    // Spec 12.7: "every merge_candidates row whose suggested_target_id names a row that is merged
    // away or absent is deleted". suggested_target_id is a nullable FK with
    // ReferentialAction.SetNull (20260909155342_InitialCreate.cs lines 267 to 272), so a dangling
    // id cannot exist and "absent" is a row whose id was nulled when its target went. An orphan
    // candidate also carries a null, because it never named a target at all, and it is the record
    // the Create target form on the Targets tab is built on; the method token tells the two apart.
    private static int DeleteStaleCandidates(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM merge_candidates
            WHERE (suggested_target_id IS NOT NULL
                   AND suggested_target_id IN (SELECT id FROM targets WHERE merged_into_id IS NOT NULL))
               OR (suggested_target_id IS NULL AND method <> @orphan);
            """;
        command.Parameters.Add(new SqliteParameter("@orphan", OrphanMethod));
        return command.ExecuteNonQuery();
    }
}
