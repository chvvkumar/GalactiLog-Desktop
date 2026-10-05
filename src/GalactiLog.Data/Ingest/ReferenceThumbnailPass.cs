using GalactiLog.Data.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.Data.Ingest;

/// <summary>
/// Spec 11.4's reference thumbnail pass: one thumbnail per target, generated in the background
/// after the header pass from the target's own most recent LIGHT frames, through the same debayer,
/// stretch and resample pipeline as any other thumbnail.
/// </summary>
/// <remarks>
/// <para>
/// <b>This port never fetches a survey image.</b> The web application's
/// <c>tasks_thumbnails.generate_reference_thumbnails</c> downloads a DSS image from NASA SkyView;
/// spec 11.4 and 19.2 forbid that outright. There is no HTTP client in this file, no URL constant,
/// and no coordinate is read. A target whose frames all fail pixel reading has no reference
/// thumbnail and renders a placeholder.
/// </para>
/// <para>
/// Commits every <see cref="CommitChunk"/> targets so an interrupted run keeps what it produced,
/// and the next run re-queries only the targets still missing one, so it continues rather than
/// starting over.
/// </para>
/// <para>
/// It writes exactly one column, <c>targets.reference_thumbnail_path</c>, through a tracking
/// context on the scan thread. That is not a fourth App-layer writer of <c>targets</c> rows: it is
/// a Data-layer scan post-pass with the same standing as <c>ScanWriter</c> and
/// <c>TargetResolver</c>. <c>TRACKING.md</c> section 6 item 14's writer census names it and the
/// one column it writes, so the gate a future writer is checked against stays complete.
/// </para>
/// </remarks>
/// <param name="connectionString">The coordinator's connection string. The pass opens its own
/// tracking context, like <c>DuplicateDetector</c>.</param>
/// <param name="ensureReference">Normally <c>ThumbnailCache.EnsureReference</c>, bound in
/// <c>AppHost</c>. A delegate because <c>GalactiLog.Data</c> must not reference
/// <c>GalactiLog.App</c>, and because a test then renders no image. The <c>bool</c> is this run's
/// <c>force</c>, forwarded unchanged: the cache keys a reference thumbnail on the target id, so
/// without it a forced run is served last run's file and produces no new pixels. Returns the
/// cache-relative path, or null when the frame's pixels could not be read (spec 6.1.5, 6.2.6):
/// that null is spec 11.4's "not rejected for pixel reading" test (questions.md Q13).</param>
/// <param name="sources">Normally <c>ReferenceThumbnailSourcesQuery.Get</c>. The
/// <c>bool</c> is <c>force</c>.</param>
/// <param name="logger">Per-target failures are logged, never thrown: one bad frame must not fail
/// a scan that has already ingested successfully, which is the rule <c>DuplicateDetector</c>
/// applies per name. Non-generic <see cref="ILogger"/>, so <c>ScanCoordinator</c> can pass its
/// own.</param>
public sealed class ReferenceThumbnailPass(
    string connectionString,
    Func<Guid, string, bool, CancellationToken, string?> ensureReference,
    Func<bool, IReadOnlyList<ReferenceThumbnailSource>> sources,
    ILogger? logger = null)
{
    /// <summary>Spec 11.4's commit cadence, from <c>tasks_thumbnails.py</c>'s
    /// <c>COMMIT_CHUNK</c>: a run that is killed mid-loop persists the thumbnails it already
    /// generated instead of rolling back the whole batch, and the next run continues from
    /// there.</summary>
    public const int CommitChunk = 10;

    /// <summary>The web source reports progress every five targets and on the last one. Kept, so
    /// the status bar moves on a small library; the coordinator's 10 Hz throttle handles the
    /// rest.</summary>
    private const int ReportEvery = 5;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>What one run did, for the caller's progress message and the activity event.</summary>
    public sealed record ReferenceThumbnailOutcome(int Total, int Generated, int Failed, bool Cancelled);

    /// <param name="force">False processes only targets with no path. True re-offers every
    /// target and is forwarded to the render, which deletes the existing file and renders again,
    /// so a forced run does replace the pixels (spec 12.7's Phase 9 maintenance action).</param>
    /// <param name="report">(step, totalSteps, message), forwarded into the coordinator's
    /// <c>ref_thumbnails</c> envelope.</param>
    public ReferenceThumbnailOutcome Run(bool force, Action<int, int, string> report, CancellationToken ct)
    {
        var targets = sources(force);
        if (targets.Count == 0)
        {
            return new ReferenceThumbnailOutcome(0, 0, 0, Cancelled: false);
        }

        // One tracking context for the whole pass, so PragmaConnectionInterceptor stays in the
        // path and spec 5.1's busy timeout applies to every write. Same shape as
        // DuplicateDetector.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));

        var generated = 0;
        var failed = 0;
        var cancelled = false;

        for (var index = 0; index < targets.Count; index++)
        {
            if (ct.IsCancellationRequested)
            {
                // A break, not a throw: the paths already generated must be saved. The
                // coordinator's ct.ThrowIfCancellationRequested() between phases is what records
                // the run as cancelled; this loop's job is to keep its work.
                cancelled = true;
                break;
            }

            var target = targets[index];
            var result = TryGenerate(context, target, force, ct);
            if (result == TargetOutcome.Cancelled)
            {
                // Cancellation observed inside a render, which is the common case: the renderer
                // checks the token several times per frame. It is not a render failure and must
                // not be counted as one, and the break lets the trailing SaveChanges keep every
                // path generated since the last commit.
                cancelled = true;
                break;
            }

            if (result == TargetOutcome.Generated)
            {
                generated++;
            }
            else
            {
                failed++;
            }

            if ((index + 1) % CommitChunk == 0)
            {
                context.SaveChanges();
            }

            if ((index + 1) % ReportEvery == 0 || index == targets.Count - 1)
            {
                report(index + 1, targets.Count, $"Reference thumbnails: {index + 1}/{targets.Count} ({generated} generated)");
            }
        }

        // Also after a cancelling break, which is the whole point of the break.
        context.SaveChanges();
        return new ReferenceThumbnailOutcome(targets.Count, generated, failed, cancelled);
    }

    // What one target's attempt did. Cancelled is deliberately not Failed: a run the user stopped
    // must not report a render failure against a target whose frames were never tried.
    private enum TargetOutcome { Generated, Failed, Cancelled }

    // The frames arrive newest first and at most
    // ReferenceThumbnailSourcesQuery.MaxFramesPerTarget of them (questions.md Q14): the first one
    // that renders wins, and a target whose every candidate fails counts as ONE failure and keeps
    // a null path, so the next run picks it up again.
    //
    // The token is not checked between a target's own frames: at most three renders, each of which
    // takes the token itself and throws out of it, so the outer loop is the one cancellation point
    // and an interrupted target is never mistaken for a failed one.
    private TargetOutcome TryGenerate(
        GalactiLogContext context, ReferenceThumbnailSource target, bool force, CancellationToken ct)
    {
        foreach (var framePath in target.FramePaths)
        {
            string? relativePath;
            try
            {
                relativePath = ensureReference(target.TargetId, framePath, force, ct);
            }
            catch (OperationCanceledException)
            {
                // NOT rethrown: escaping Run here would skip the trailing SaveChanges and drop
                // every path generated since the last commit, which is up to CommitChunk - 1 of
                // them. The coordinator's checkpoint after the pass is what records the run as
                // cancelled.
                return TargetOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                // One unreadable frame must not fail a scan that has already ingested
                // successfully. Warning rather than debug: an exception out of the renderer is
                // not the ordinary "these pixels do not decode" outcome, which is a null.
                _logger.LogWarning(ex, "Reference thumbnail failed for {Target} from {Frame}", target.PrimaryName, framePath);
                continue;
            }

            if (relativePath is null)
            {
                // Spec 6.1.5, 6.2.6: the frame's pixels could not be read. Debug, not warning: a
                // library of unsupported frames would otherwise log per target on every scan.
                _logger.LogDebug("Reference thumbnail skipped for {Target}: {Frame} could not be rendered", target.PrimaryName, framePath);
                continue;
            }

            // ponytail: one row read per generated target rather than one batched read of the
            // whole work set. Ceiling: a forced regeneration of a 500-target library issues 500
            // extra point lookups on an indexed primary key, which is noise next to 500 image
            // decodes. Upgrade path if it ever matters: load the tracked rows in chunks of
            // CommitChunk before the loop.
            var row = context.Targets.SingleOrDefault(t => t.Id == target.TargetId);
            if (row is null)
            {
                // Merged away or deleted between the query and here. Nothing to write. Counted as
                // a failure, reported in the ref_thumbnails envelope and written into the activity
                // payload as one, because this pass has two outcomes and no third: the thumbnail
                // exists on disk but no row points at it, which is the state a later run should
                // look at again.
                return TargetOutcome.Failed;
            }

            row.ReferenceThumbnailPath = relativePath;
            return TargetOutcome.Generated;
        }

        return TargetOutcome.Failed;
    }
}
