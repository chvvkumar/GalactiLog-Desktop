using GalactiLog.Data.Entities;

namespace GalactiLog.App.Services;

/// <summary>Spec 12.11 behaviour 10: what a finished scan is worth telling the user.</summary>
/// <param name="Succeeded">Whether the run reached a completed state (spec 5.13's
/// <c>scan_runs.state</c>).</param>
/// <param name="Title">One short line, the outcome.</param>
/// <param name="Body">The counts that are non-zero, in spec 5.13's column order, or a single
/// "nothing new" line when every count is zero.</param>
public sealed record ScanCompletionNotice(bool Succeeded, string Title, string Body)
{
    /// <summary>Spec 5.13's terminal state for a run that finished its work.</summary>
    public const string CompleteState = "complete";

    /// <summary>Spec 5.13's terminal state for a run that did not. <c>MarkInterrupted</c> writes
    /// it too, with <c>error_text</c> "interrupted", so an interrupted run reads as a failure that
    /// says what happened rather than as a success.</summary>
    public const string FailedState = "failed";

    /// <summary>Spec 5.13's third terminal state, written when the shutdown drain or the user
    /// cancels a run.</summary>
    public const string CancelledState = "cancelled";

    /// <summary>The title of a run that reached <see cref="CompleteState"/>.</summary>
    public const string CompleteTitle = "Scan complete";

    /// <summary>The title of a run that reached <see cref="FailedState"/>.</summary>
    public const string FailedTitle = "Scan failed";

    /// <summary>The title of a row carrying no state at all, which no writer in this application
    /// produces and which is therefore never reported as a success.</summary>
    public const string UnknownStateTitle = "Scan finished";

    /// <summary>The body of a run whose seven counts are all zero. Spec 15: a scan that found
    /// nothing new is the normal result of a scheduled scan and is not a failure.</summary>
    public const string NothingNewBody = "No new or changed files were found.";

    /// <summary>The bound on the error text carried in <see cref="Body"/>, ellipsis included. A
    /// notification surface with no length limit does not exist.</summary>
    public const int ErrorTextMaxLength = 80;

    // Every other state spec 5.13 defines ("cancelled"), and the "running" row a follow-up scan
    // may already have written by the time this composes, read as themselves.
    private const string TitlePrefix = "Scan ";
    private const string Ellipsis = "...";

    /// <summary>
    /// Whether a spec 5.13 <c>scan_runs.state</c> is one a finished run can carry: <c>complete</c>,
    /// <c>cancelled</c> or <c>failed</c>. <c>running</c>, and anything this application does not
    /// write, is not.
    /// </summary>
    /// <remarks>
    /// Here rather than in the watcher, because this record already owns spec 5.13's state
    /// vocabulary and a second copy of it is how the two would disagree (design-lessons rule 1).
    /// The watcher uses it to skip a run that has not finished: a <c>galactilog scan</c> beside the
    /// window writes a <c>running</c> row that is newer than the run that just finished (spec 12.11
    /// behaviour 2), and reporting that row would put "Scan running" on an idle tooltip.
    /// </remarks>
    public static bool IsTerminalState(string? state)
    {
        var trimmed = state?.Trim() ?? "";
        return string.Equals(trimmed, CompleteState, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, CancelledState, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, FailedState, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Composes a notice from a scan_runs row. The one composer: the tooltip, a future
    /// toast and every test read the same text (design-lessons rule 1).</summary>
    /// <remarks>
    /// Pure over one row. No database, no settings, no clock, and nothing that depends on
    /// <c>general.timezone</c>: the notice is about counts, so the same row composes the same
    /// sentence on every machine and on every surface.
    /// </remarks>
    public static ScanCompletionNotice From(ScanRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var state = run.State?.Trim() ?? "";
        var succeeded = string.Equals(state, CompleteState, StringComparison.OrdinalIgnoreCase);
        var title = succeeded
            ? CompleteTitle
            : string.Equals(state, FailedState, StringComparison.OrdinalIgnoreCase)
                ? FailedTitle
                : state.Length == 0
                    ? UnknownStateTitle
                    : TitlePrefix + state;

        return new ScanCompletionNotice(succeeded, title, ComposeBody(run));
    }

    private static string ComposeBody(ScanRun run)
    {
        // Spec 5.13's column order, which is also the order the user reads the run in: what was
        // seen, what was new, what changed, what landed, what did not, what was skipped, what
        // went away.
        var parts = new List<string>(7);
        Add(parts, run.Discovered, "file discovered", "files discovered");
        Add(parts, run.NewFiles, "new file", "new files");
        Add(parts, run.ChangedFiles, "changed file", "changed files");
        Add(parts, run.Completed, "frame ingested", "frames ingested");
        Add(parts, run.Failed, "failure", "failures");
        Add(parts, run.SkippedCalibration, "calibration frame skipped", "calibration frames skipped");
        Add(parts, run.Removed, "row removed", "rows removed");

        var error = Truncate(run.ErrorText);
        if (parts.Count == 0)
        {
            // A run that failed before it counted anything says why, rather than claiming there
            // was nothing to find.
            return error.Length > 0 ? error : NothingNewBody;
        }

        var counts = string.Join(", ", parts);
        return error.Length > 0 ? counts + ". " + error : counts;
    }

    private static void Add(List<string> parts, int count, string singular, string plural)
    {
        if (count > 0)
        {
            parts.Add($"{count} {(count == 1 ? singular : plural)}");
        }
    }

    private static string Truncate(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length <= ErrorTextMaxLength
            ? trimmed
            : trimmed[..(ErrorTextMaxLength - Ellipsis.Length)] + Ellipsis;
    }
}
