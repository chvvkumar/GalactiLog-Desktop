using System.Globalization;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalactiLog.Data.Ingest;

/// <summary>
/// Spec 9.7's duplicate detection pass, run at the end of a scan that ingested at least one new
/// file. Writes <c>merge_candidates</c> rows and, in Pass 1 outcome 2 only, assigns frames to a
/// newly created target. Database rows only: nothing here touches the filesystem.
/// </summary>
/// <param name="connectionString">The DI <c>DatabaseConnectionString.Value</c>. The detector
/// opens its own short-lived contexts, like <c>ActivityRepository</c> and
/// <c>ScanRunRepository</c>.</param>
/// <param name="resolve">Normally a lambda over <c>TargetResolver.Resolve</c>. A delegate, not
/// the resolver, so a test can drive all four Pass 1 outcomes with no network and no catalogs
/// directory (design-spec 18.2: "Network is never touched in tests"). The <c>bool</c> is
/// <c>createIfMissing</c>.</param>
/// <param name="logger">Per-name failures are logged, never thrown: a bad name must not fail a
/// scan that has already ingested successfully. Non-generic <see cref="ILogger"/>, so
/// <c>ScanCoordinator</c> can pass its own logger without gaining an
/// <c>ILoggerFactory</c>.</param>
public sealed class DuplicateDetector(
    string connectionString,
    Func<string, bool, CancellationToken, TargetResolver.ResolutionResult> resolve,
    ILogger? logger = null)
{
    /// <summary>Spec 9.7's trigram threshold, carried over unchanged. Strictly greater than,
    /// matching <c>tasks_target_dedup.py</c>'s <c>score &gt; 0.4</c>; a name scoring exactly 0.4
    /// falls through to the orphan outcome.</summary>
    public const double TrigramThreshold = 0.4;

    private const string StatusPending = "pending";

    /// <summary>What one run did, for the caller's progress message and the activity event.</summary>
    public sealed record DedupOutcome(
        int NamesExamined,
        int CandidatesWritten,
        int TargetsCreated,
        int FramesAssigned,
        int OrphanCount,
        bool StoppedOnNetworkFailure);

    // One active target as both passes need it. FrameCount is populated by Pass 2 only; Pass 1's
    // trigram step scores names and never looks at counts.
    private sealed record ActiveTarget(Guid Id, string PrimaryName, IReadOnlyList<string> Aliases, int FrameCount);

    // Mutable tally threaded through the passes, so each step reports what it did without every
    // helper returning a six-field tuple.
    private sealed class Counters
    {
        public int NamesExamined;
        public int CandidatesWritten;
        public int TargetsCreated;
        public int FramesAssigned;
        public int OrphanCount;
        public bool StoppedOnNetworkFailure;
    }

    /// <param name="report">Progress callback: (step, totalSteps, message). The coordinator
    /// forwards it into its own <c>dedup</c> envelope. Called at most once per name.</param>
    public DedupOutcome Run(Action<int, int, string> report, CancellationToken ct)
    {
        // One tracking context for the candidate writes, so PragmaConnectionInterceptor stays in
        // the path and spec 5.1's busy timeout applies to everything this pass writes.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        // Spec 9.7: "do not already have a merge_candidates row in any status". No status filter
        // at all, so a dismissed suggestion the user rejected does not come back next scan.
        var claimed = ReadCandidateSourceNames(connection);
        var names = UnresolvedObjects.Read(connection)
            .Where(row => !claimed.Contains(row.Name))
            .ToList();

        var counters = new Counters();
        RunPass1(context, connection, names, report, counters, ct);
        RunPass2(context, connection, counters, ct);

        return new DedupOutcome(
            counters.NamesExamined, counters.CandidatesWritten, counters.TargetsCreated,
            counters.FramesAssigned, counters.OrphanCount, counters.StoppedOnNetworkFailure);
    }

    // ---- Pass 1 -------------------------------------------------------------------------

    private void RunPass1(
        GalactiLogContext context, SqliteConnection connection,
        IReadOnlyList<(string Name, int FrameCount)> names,
        Action<int, int, string> report, Counters counters, CancellationToken ct)
    {
        for (var index = 0; index < names.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (name, frameCount) = names[index];
            report(index + 1, names.Count, $"Checking {name}");
            counters.NamesExamined++;

            // The catch spans the WHOLE per-name body, both resolve calls included: outcome 2's
            // create call reaches the same clients as the first one, so a name the catalog
            // service rejects there is skipped exactly as one rejected on the first call.
            try
            {
                if (!ExamineName(context, connection, name, frameCount, counters, ct))
                {
                    // A transient network failure: nothing was written for this name, and the
                    // rest of the pass is left for the next scan.
                    return;
                }
            }
            catch (NonTransientCatalogException ex)
            {
                // One name the catalog service rejected outright. Skip it and carry on: the pass
                // runs after a successful ingest and must not undo it (ruling Q7).
                logger?.LogWarning(
                    ex, "Duplicate detection skipped {ObjectName}: the catalog service rejected the query", name);
                continue;
            }

            // Saved per name, so a cancelled or failed pass keeps what it has already decided.
            context.SaveChanges();
        }
    }

    // One name's Pass 1 decision. Returns false when an online source could not be reached, which
    // stops the whole pass: the name was never actually checked, so no candidate is written for
    // it and the remaining names are picked up by the next scan. That is the same reasoning
    // TargetResolver applies when it refuses to negative-cache an unreachable name (spec 9.6,
    // ruling Q7).
    private bool ExamineName(
        GalactiLogContext context, SqliteConnection connection,
        string name, int frameCount, Counters counters, CancellationToken ct)
    {
        var result = resolve(name, false, ct);
        if (StopOnNetworkFailure(result, name, counters))
        {
            return false;
        }

        switch (result.Stage)
        {
            case TargetResolver.ResolutionStage.Unresolved:
                // Outcomes 3 and 4.
                WriteTrigramOrOrphan(context, connection, name, frameCount, counters);
                return true;

            case TargetResolver.ResolutionStage.Cache:
            case TargetResolver.ResolutionStage.Offline:
            case TargetResolver.ResolutionStage.Simbad:
            case TargetResolver.ResolutionStage.Sesame:
                return ResolvedName(context, connection, result, name, frameCount, counters, ct);

            // Task 7's sixth stage, decided here explicitly (FIXER LIST item 4). A solar-system
            // name is never a merge suggestion: the classifier's answer IS the target, so the
            // frames are assigned directly and no candidate is written. A TargetId here means the
            // resolver linked this name to a target an earlier spelling of the same body already
            // created ("Sol" onto "Sun"), which is outcome 1's shape but not outcome 1's meaning:
            // its candidate text would claim SIMBAD resolved a name SIMBAD has never heard of.
            // Without a TargetId nothing carries the name yet, and outcome 2's create call below
            // is exactly right.
            case TargetResolver.ResolutionStage.SolarSystem:
                if (result.TargetId is { } solarSystemId)
                {
                    counters.FramesAssigned += UnresolvedObjects.AssignFrames(connection, name, solarSystemId);
                    return true;
                }

                return ResolvedName(context, connection, result, name, frameCount, counters, ct);

            default:
                // A seventh stage would be a decision its own task has to make explicitly:
                // mapping an unknown stage onto one of the four outcomes here would silently file
                // its targets under the wrong one.
                throw new NotSupportedException(
                    $"Duplicate detection has no rule for resolution stage {result.Stage}");
        }
    }

    private bool StopOnNetworkFailure(TargetResolver.ResolutionResult result, string name, Counters counters)
    {
        if (!result.TransientNetworkFailure)
        {
            return false;
        }

        logger?.LogWarning(
            "Duplicate detection stopped at {ObjectName}: an online source could not be reached. " +
            "The remaining unresolved names are left for the next scan", name);
        counters.StoppedOnNetworkFailure = true;
        return true;
    }

    // Outcomes 1 and 2: the name resolved to an identity. Either an existing active target
    // already carries it, or nothing does and the correct answer is to create the target.
    // Returns false only when the create call hit a transient network failure, which stops the
    // pass exactly as a failure on the first call does.
    private bool ResolvedName(
        GalactiLogContext context, SqliteConnection connection, TargetResolver.ResolutionResult result,
        string name, int frameCount, Counters counters, CancellationToken ct)
    {
        if (result.TargetId is { } existingId)
        {
            // Outcome 1. The target's own primary_name, read back rather than taken from
            // result.Identity: on the offline and online stages the identity is the freshly
            // fetched payload, which need not carry the name the existing row displays.
            var targetName = context.Targets
                .Where(target => target.Id == existingId)
                .Select(target => target.PrimaryName)
                .FirstOrDefault() ?? result.Identity?.PrimaryName ?? name;

            WriteCandidate(
                context, name, frameCount, existingId, 1.0, "simbad",
                $"SIMBAD resolves \"{name}\" to the same object as \"{targetName}\"", counters);
            return true;
        }

        if (result.Identity is null)
        {
            // Unreachable through TargetResolver: every non-Unresolved stage carries an identity.
            // Nothing to attach the frames to, so the name is left for the next scan.
            logger?.LogWarning(
                "Duplicate detection skipped {ObjectName}: stage {Stage} produced no identity", name, result.Stage);
            return true;
        }

        // Outcome 2. Spec 9.7: "No candidate is written; a correct answer beats a suggestion."
        var created = resolve(name, true, ct);
        if (StopOnNetworkFailure(created, name, counters))
        {
            return false;
        }

        if (created.TargetId is not { } createdId)
        {
            logger?.LogWarning(
                "Duplicate detection could not create a target for {ObjectName}; it is left unresolved", name);
            return true;
        }

        counters.TargetsCreated++;
        counters.FramesAssigned += UnresolvedObjects.AssignFrames(connection, name, createdId);
        return true;
    }

    // Outcomes 3 and 4. Trigram.Similarity is called with the raw name and the raw candidate:
    // it lowercases and pads both sides itself, and pre-normalizing would move the scores off
    // the captured PostgreSQL reference values TrigramTests pins.
    private static void WriteTrigramOrOrphan(
        GalactiLogContext context, SqliteConnection connection, string name, int frameCount, Counters counters)
    {
        // ponytail: the active target set is re-read per unresolved name, because Pass 1 outcome
        // 2 can create a target mid-loop. Ceiling is (unresolved names) x (targets) reads per
        // scan; cache it and invalidate on outcome 2 if a library ever makes that measurable.
        var ranked = LoadActiveTargets(connection, withFrameCounts: false)
            .Select(target => (Target: target, Score: BestScore(name, target)))
            .OrderByDescending(scored => scored.Score)
            .ThenBy(scored => scored.Target.PrimaryName, StringComparer.Ordinal)
            .ToList();

        if (ranked.Count > 0 && ranked[0].Score > TrigramThreshold)
        {
            var best = ranked[0];
            // Truncation, not rounding: tasks_target_dedup.py writes int(float(score) * 100), so
            // 0.879 renders 87 and not 88.
            var percent = ((int)(best.Score * 100)).ToString(CultureInfo.InvariantCulture);
            WriteCandidate(
                context, name, frameCount, best.Target.Id, best.Score, "trigram",
                $"Name is {percent}% similar to \"{best.Target.PrimaryName}\"", counters);
            return;
        }

        WriteCandidate(
            context, name, frameCount, suggestedTargetId: null, 0.0, "orphan",
            "No match found in SIMBAD or existing targets", counters);
        counters.OrphanCount++;
    }

    private static double BestScore(string name, ActiveTarget target)
    {
        var best = Trigram.Similarity(name, target.PrimaryName);
        foreach (var alias in target.Aliases)
        {
            var score = Trigram.Similarity(name, alias);
            if (score > best)
            {
                best = score;
            }
        }
        return best;
    }

    // ---- Pass 2 -------------------------------------------------------------------------

    // Spec 9.7: "Detect active targets that share a normalized name or overlapping aliases with
    // each other and propose merging them" (ruling Q5). A union-find over the normalized names
    // and aliases of every active target; the member with the most frames is the suggested
    // winner and every other member gets one candidate.
    private static void RunPass2(
        GalactiLogContext context, SqliteConnection connection, Counters counters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Re-read after Pass 1's saves, so a name Pass 1 just created is not proposed again.
        var claimed = ReadCandidateSourceNames(connection);
        var active = LoadActiveTargets(connection, withFrameCounts: true);
        if (active.Count < 2)
        {
            return;
        }

        // Sorted, so the shared name a group reports does not depend on row order.
        var byName = new SortedDictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var target in active)
        {
            foreach (var candidate in new[] { target.PrimaryName }.Concat(target.Aliases))
            {
                var normalized = NameNormalizer.Normalize(candidate);
                if (normalized.Length == 0)
                {
                    continue;
                }
                if (!byName.TryGetValue(normalized, out var holders))
                {
                    byName[normalized] = holders = [];
                }
                if (!holders.Contains(target.Id))
                {
                    holders.Add(target.Id);
                }
            }
        }

        var parent = new Dictionary<Guid, Guid>();
        foreach (var holders in byName.Values.Where(holders => holders.Count >= 2))
        {
            foreach (var other in holders.Skip(1))
            {
                Union(parent, holders[0], other);
            }
        }

        // The first shared name of each finished group, in the same sorted order. Recorded after
        // every union rather than during it, so a group whose leader changed when two groups
        // merged still names a name it really shares.
        var sharedNames = new Dictionary<Guid, string>();
        foreach (var (normalized, holders) in byName.Where(entry => entry.Value.Count >= 2))
        {
            var leader = Find(parent, holders[0]);
            if (!sharedNames.ContainsKey(leader))
            {
                sharedNames[leader] = normalized;
            }
        }

        foreach (var group in active.GroupBy(target => Find(parent, target.Id)).Where(group => group.Count() >= 2))
        {
            var members = group
                .OrderByDescending(target => target.FrameCount)
                .ThenBy(target => target.PrimaryName, StringComparer.Ordinal)
                .ToList();
            var winner = members[0];
            var shared = sharedNames.TryGetValue(group.Key, out var name) ? name : "";

            foreach (var member in members.Skip(1))
            {
                // Add returns false for a name Pass 1 already wrote, and stops this group's third
                // member repeating its second member's row.
                if (!claimed.Add(member.PrimaryName))
                {
                    continue;
                }

                WriteCandidate(
                    context, member.PrimaryName, member.FrameCount, winner.Id, 1.0, "duplicate",
                    $"Shares alias \"{shared}\" with \"{winner.PrimaryName}\"", counters);
            }
        }

        context.SaveChanges();
    }

    private static Guid Find(Dictionary<Guid, Guid> parent, Guid id)
    {
        while (parent.TryGetValue(id, out var next) && next != id)
        {
            parent[id] = parent.TryGetValue(next, out var grandparent) ? grandparent : next;
            id = parent[id];
        }
        return id;
    }

    private static void Union(Dictionary<Guid, Guid> parent, Guid a, Guid b)
    {
        var leftLeader = Find(parent, a);
        var rightLeader = Find(parent, b);
        if (leftLeader != rightLeader)
        {
            parent[rightLeader] = leftLeader;
        }
    }

    // ---- reads and writes ----------------------------------------------------------------

    private static void WriteCandidate(
        GalactiLogContext context, string sourceName, int frameCount, Guid? suggestedTargetId,
        double similarityScore, string method, string reasonText, Counters counters)
    {
        context.MergeCandidates.Add(new MergeCandidate
        {
            Id = Guid.NewGuid(),
            SourceName = sourceName,
            SourceImageCount = frameCount,
            SuggestedTargetId = suggestedTargetId,
            SimilarityScore = similarityScore,
            Method = method,
            Status = StatusPending,
            ReasonText = reasonText,
            CreatedAt = DateTime.UtcNow,
            ResolvedAt = null,
        });
        counters.CandidatesWritten++;
    }

    private static HashSet<string> ReadCandidateSourceNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_name FROM merge_candidates;";

        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = SqlReaders.ReadText(reader, 0);
            if (name is not null)
            {
                names.Add(name);
            }
        }
        return names;
    }

    // Every active target, ordered by primary_name so both passes are deterministic. The frame
    // count is the target's LIGHT frames only, which is the same quantity a Pass 1 candidate's
    // source_image_count carries, so Task 3's list renders one column under one heading
    // (coordinator ruling on the Task 1 review). The web source counts every image row here.
    private static List<ActiveTarget> LoadActiveTargets(SqliteConnection connection, bool withFrameCounts)
    {
        using var command = connection.CreateCommand();
        command.CommandText = withFrameCounts
            ? $"""
              SELECT t.id, t.primary_name, t.aliases, coalesce(c.n, 0)
              FROM targets t
              LEFT JOIN (
                SELECT resolved_target_id, count(*) AS n FROM images
                WHERE {SqlFragments.LightFrameOnly} GROUP BY resolved_target_id) c
                ON c.resolved_target_id = t.id
              WHERE t.merged_into_id IS NULL
              ORDER BY t.primary_name;
              """
            : """
              SELECT t.id, t.primary_name, t.aliases, 0
              FROM targets t
              WHERE t.merged_into_id IS NULL
              ORDER BY t.primary_name;
              """;

        var targets = new List<ActiveTarget>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // ParseAliases, never a bare JsonSerializer.Deserialize: a hand-edited document must
            // not fail a scan.
            targets.Add(new ActiveTarget(
                reader.GetGuid(0),
                reader.GetString(1),
                SqlReaders.ParseAliases(SqlReaders.ReadText(reader, 2)),
                SqlReaders.ReadInt(reader, 3)));
        }
        return targets;
    }
}
