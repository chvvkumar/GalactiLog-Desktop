using GalactiLog.Core.Io;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Ingest;

/// <summary>One root whose pruning was skipped by the zero-discovery guard.</summary>
public sealed record OrphanPruneSkip(string Root, int KnownRows);

/// <summary>
/// One root whose missing-row count reached spec 10.3's 50 percent safety limit. Recorded
/// whether or not the run was forced: the caller writes <c>orphan_prune_limited</c> when it
/// was not and <c>orphan_prune_forced</c> when it was, and both events carry these counts.
/// </summary>
public sealed record OrphanPruneLimit(string Root, int MissingRows, int KnownRows);

/// <summary>
/// What a prune pass did: how many <c>images</c> rows were deleted, which roots were skipped
/// by the zero-discovery guard (the caller owns the logging, spec 10.3), and how many rows
/// were found missing in the first place -- <c>Candidates</c> is the denominator the
/// <c>prune_orphans</c> progress event reports against. It excludes rows under a skipped
/// root, which were never candidates for deletion, and includes rows under a root the 50
/// percent safety limit stopped, which were.
/// </summary>
public sealed record OrphanPruneResult(
    int Removed, IReadOnlyList<OrphanPruneSkip> SkippedRoots, int Candidates = 0)
{
    /// <summary>
    /// Every root whose missing count reached the 50 percent safety limit (spec 10.3 guard 2).
    /// A limited root's rows ARE counted in <see cref="Candidates"/>, unlike a skipped root's:
    /// they were found missing, and the run either refused to delete them or was forced to.
    /// </summary>
    public IReadOnlyList<OrphanPruneLimit> Limited { get; init; } = [];
}

/// <summary>
/// What the two guards of spec 10.3 decided for one run, with no table, no context and no
/// deletion in sight. Produced by <see cref="OrphanPruner.Plan"/> and consumed by both the frame
/// side (<see cref="OrphanPruner.Prune"/>, over <c>images</c>) and the guide-log side
/// (<c>Phd2Ingest</c>, over <c>phd2_logs</c>).
/// </summary>
/// <param name="ToDelete">Every known path the run may delete. Empty when both guards refused.
/// </param>
/// <param name="SkippedRoots">Roots the absolute zero-discovery guard held back.</param>
/// <param name="LimitedRoots">Roots that reached the 50 percent safety limit, whether or not the
/// run was forced: the caller writes <c>*_prune_limited</c> when it was not and
/// <c>*_prune_forced</c> when it was.</param>
/// <param name="LimitedCandidates">Rows found missing under a root the limit stopped, which no
/// deletion will reach this run. Added to <c>ToDelete.Count</c> for the candidate denominator.
/// </param>
public sealed record OrphanPrunePlan(
    IReadOnlyList<string> ToDelete,
    IReadOnlyList<OrphanPruneSkip> SkippedRoots,
    IReadOnlyList<OrphanPruneLimit> LimitedRoots,
    int LimitedCandidates);

/// <summary>
/// Spec 10.3 step 4. Deletes <c>images</c> rows whose file is no longer on disk under a
/// walked root, and holds <see cref="Plan"/>, the per-root guard arithmetic the guide-log pass
/// of step 5 shares with it.
/// </summary>
/// <remarks>
/// THIS TYPE DELETES DATABASE ROWS ONLY. IT NEVER DELETES, MOVES, RENAMES, OR OTHERWISE
/// MODIFIES ANY FILE OR DIRECTORY ON DISK, UNDER ANY CIRCUMSTANCE (spec 2.1). It performs no
/// filesystem access at all: the only disk-shaped input is the caller-supplied
/// <c>discoveredPaths</c> set, already collected by <c>FileWalker</c> through the read-only
/// <c>UserFiles</c> gateway. The only <c>System.IO</c> member referenced here is
/// <c>Path</c> (pure string manipulation, no I/O). <c>FileSafetyTest</c> (spec 2.1.3)
/// source-scans <c>src/**</c> and fails the build if that ever stops being true; this file
/// needs no allowlist exception and must never be given one.
/// </remarks>
public static class OrphanPruner
{
    /// <summary>
    /// Bounds a generated <c>IN (...)</c> list, mirroring the web application's
    /// <c>orphan_cleanup.py</c> batch size for the same operation. An unbounded list over a whole
    /// library exceeds SQLite's parameter limit and fails the whole prune rather than a batch of
    /// it. Public because the guide-log side (<c>Phd2Repository.DeleteByPaths</c>) chunks the same
    /// delete by the same number: one declaration, not two.
    /// </summary>
    public const int DeleteBatchSize = 500;

    /// <summary>
    /// Spec 10.3 guard 2: when the missing row count under a root reaches
    /// <c>max(1, known) * 0.5</c>, that root is not pruned unless the run is forced. Half a
    /// library disappearing between two scans is more often a storage fault than a deliberate
    /// deletion.
    /// </summary>
    public const double SafetyLimitFraction = 0.5;

    /// <summary>
    /// Deletes every catalogued row that sits under one of <paramref name="walkedRoots"/> and
    /// was not in <paramref name="discoveredPaths"/>.
    /// </summary>
    /// <param name="walkedRoots">
    /// Every configured scan root walked this run -- NOT the narrowed include_paths. A row is
    /// eligible only when it sits under a root the user still has configured, so rows under a
    /// root that was removed from configuration are left alone by construction: that root is
    /// simply absent from this list (spec 10.3 step 4, "deleting them would lose data the
    /// moment a user temporarily unplugs a drive").
    /// </param>
    /// <param name="discoveredPaths">
    /// Every path the walk returned this run. Compared case-insensitively, matching how
    /// ScanCoordinator builds its known-file map and the NOCASE collation migration 0003 put
    /// on images.file_path: Windows paths are case-insensitive, so a case-only variant found
    /// on disk must not be read as "the other one is gone".
    /// </param>
    /// <param name="force">
    /// Spec 10.3's per-run orphan cleanup override, off on every trigger but a manual run. True
    /// deletes the rows under a root that reached the safety limit; it does NOT lift the
    /// zero-discovery guard, which is absolute, because a root that produced nothing at all
    /// carries no evidence that anything was deleted. Forced or not, this deletes database rows
    /// and nothing else.
    /// </param>
    public static OrphanPruneResult Prune(
        GalactiLogContext context, IReadOnlyList<string> walkedRoots, IEnumerable<string> discoveredPaths,
        bool force = false)
    {
        // Load, plan, delete. The middle step is Plan, which the guide-log pass calls with
        // phd2_logs rows in place of images rows (spec 10.3 step 5): one implementation of the
        // two guards, never two (design lesson 1, and spec 10.3 says so in as many words).
        var knownPaths = context.Images.Select(i => i.FilePath).ToList();
        var plan = Plan(walkedRoots, knownPaths, discoveredPaths, force);
        var toDelete = plan.ToDelete;

        if (toDelete.Count == 0)
        {
            return new OrphanPruneResult(0, plan.SkippedRoots, plan.LimitedCandidates)
            {
                Limited = plan.LimitedRoots,
            };
        }

        var removed = 0;
        foreach (var batch in toDelete.Chunk(DeleteBatchSize))
        {
            removed += context.Images.Where(i => batch.Contains(i.FilePath)).ExecuteDelete();
        }
        return new OrphanPruneResult(removed, plan.SkippedRoots, toDelete.Count + plan.LimitedCandidates)
        {
            Limited = plan.LimitedRoots,
        };
    }

    /// <summary>
    /// Spec 10.3's two guards, evaluated per walked root over a caller-supplied known set. The
    /// whole decision and nothing else: no <c>GalactiLogContext</c>, no table name, no deletion.
    /// </summary>
    /// <remarks>
    /// Extracted so the guide-log pass of spec 10.3 step 5 reaches the SAME arithmetic rather
    /// than a second copy of it. Spec 10.3: "<c>OrphanPruner</c> already holds <c>known</c>,
    /// <c>missing</c>, the <c>missing &gt;= max(1, known) * 0.5</c> test and the override rule,
    /// and the guide-log pass calls that shared code with <c>phd2_logs</c> rows in place of
    /// <c>images</c> rows." A second implementation that is correct on the day it is written
    /// drifts the first time one side is tuned, which is the failure design lesson 1 names.
    /// <para>
    /// Both sides compare paths <c>OrdinalIgnoreCase</c>, which is why <c>images.file_path</c>
    /// and <c>phd2_logs.file_path</c> both carry <c>NOCASE</c>.
    /// </para>
    /// </remarks>
    /// <param name="walkedRoots">Every configured scan root walked this run. A root this run did
    /// not walk is simply absent, so its rows prune nothing by construction.</param>
    /// <param name="knownPaths">Every catalogued path of the table being pruned.</param>
    /// <param name="discoveredPaths">Every path the walk returned this run, of the same kind.
    /// </param>
    /// <param name="force">The per-run override. It reaches guard 2 only; guard 1 is absolute.
    /// </param>
    public static OrphanPrunePlan Plan(
        IReadOnlyList<string> walkedRoots,
        IEnumerable<string> knownPaths,
        IEnumerable<string> discoveredPaths,
        bool force = false)
    {
        // Built here rather than taken as a set, so the comparer cannot be got wrong at a
        // call site; duplicates that differ only in case collapse instead of throwing.
        var discovered = new HashSet<string>(discoveredPaths, StringComparer.OrdinalIgnoreCase);
        var known = knownPaths as IReadOnlyList<string> ?? [.. knownPaths];
        var skippedRoots = new List<OrphanPruneSkip>();
        var limitedRoots = new List<OrphanPruneLimit>();
        var toDelete = new List<string>();

        // Rows found missing under a root the limit stopped, which are candidates that no
        // deletion will reach this run. Added to toDelete.Count for the Candidates figure.
        var limitedCandidates = 0;

        foreach (var root in walkedRoots)
        {
            var normalizedRoot = Path.GetFullPath(root);
            var knownUnderRoot = known.Where(p => PathConfinement.IsUnderOrEqual(normalizedRoot, p)).ToList();

            // Nothing catalogued under this root: no orphans to find, and no "the share went
            // away" signal either -- a healthy empty root must not warn.
            if (knownUnderRoot.Count == 0) continue;

            // Spec 10.3's first guard: a root that previously had rows and discovered nothing
            // this run is far more likely to be an unmounted or unreachable share than a
            // user who deleted every frame under it. Per root, not global, so one dead share
            // does not stop a healthy root's orphans from being cleaned up in the same run.
            if (!discovered.Any(p => PathConfinement.IsUnderOrEqual(normalizedRoot, p)))
            {
                skippedRoots.Add(new OrphanPruneSkip(normalizedRoot, knownUnderRoot.Count));
                continue;
            }

            var missing = knownUnderRoot.Where(p => !discovered.Contains(p)).ToList();

            // Spec 10.3's second guard, verbatim in its arithmetic: max(1, known), not known,
            // and >= rather than >, so exactly half trips it. Per root, like the first guard, so
            // one dead share does not stop a healthy root being cleaned in the same run.
            if (missing.Count >= Math.Max(1, knownUnderRoot.Count) * SafetyLimitFraction)
            {
                limitedRoots.Add(new OrphanPruneLimit(normalizedRoot, missing.Count, knownUnderRoot.Count));
                if (!force)
                {
                    limitedCandidates += missing.Count;
                    continue;
                }
            }

            toDelete.AddRange(missing);
        }

        return new OrphanPrunePlan(toDelete, skippedRoots, limitedRoots, limitedCandidates);
    }
}
