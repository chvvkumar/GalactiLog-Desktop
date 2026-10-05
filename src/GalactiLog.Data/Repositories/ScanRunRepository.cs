using GalactiLog.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Repositories;

// Durable scan history (spec 5.13). Live scan state lives in memory on ScanCoordinator; this
// is the table the diagnostics page and the ingest history chart read.
//
// Two calls per run: Start writes the `running` row and returns its id, Complete stamps the
// terminal state, the counters and finished_at. Each opens its own short-lived tracking
// context, matching every other repository here -- the scan's long-lived writer context
// belongs to ScanWriter alone.
public sealed class ScanRunRepository(string connectionString)
{
    public int Start(string trigger)
    {
        using var context = Open();
        var run = new ScanRun { StartedAt = DateTime.UtcNow, Trigger = trigger, State = "running" };
        context.ScanRuns.Add(run);
        context.SaveChanges();
        return run.Id;
    }

    // `state` is one of "complete", "cancelled", "failed" (spec 5.13). `errorText` is set
    // only for "failed".
    //
    // The three PHD2 counters are trailing and default to 0, which is the same value a run with
    // general.phd2_scan_enabled off writes and the same value the migration's column default
    // gives a row written by an earlier version. Defaulted so that a caller writing a run with no
    // guide-log pass says nothing about one.
    public void Complete(
        int id, string state, int discovered, int newFiles, int changedFiles,
        int completed, int failed, int skippedCalibration, int removed,
        int phd2Found = 0, int phd2Ingested = 0, int phd2Failed = 0, string? errorText = null)
    {
        using var context = Open();
        var run = context.ScanRuns.Single(r => r.Id == id);
        run.FinishedAt = DateTime.UtcNow;
        run.State = state;
        run.Discovered = discovered;
        run.NewFiles = newFiles;
        run.ChangedFiles = changedFiles;
        run.Completed = completed;
        run.Failed = failed;
        run.SkippedCalibration = skippedCalibration;
        run.Removed = removed;
        run.Phd2Found = phd2Found;
        run.Phd2Ingested = phd2Ingested;
        run.Phd2Failed = phd2Failed;
        run.ErrorText = errorText;
        context.SaveChanges();
    }

    /// <summary>
    /// Reads one run back (FIXER LIST 5). Opened READ-ONLY: the CLI's <c>--json</c> payload
    /// prints the row it just wrote, and printing a row must never be able to create or
    /// migrate anything. Returns null when no such run exists.
    /// </summary>
    public ScanRun? Get(int id)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(DatabasePaths.AsReadOnly(connectionString)));
        return context.ScanRuns.SingleOrDefault(r => r.Id == id);
    }

    /// <summary>The newest scan_runs row by started_at, or null on a library that has never
    /// scanned. Spec 12.8's Scan group "last run".</summary>
    public ScanRun? Latest() => Recent(1).FirstOrDefault();

    /// <summary>The newest rows by started_at, newest first, capped at limit. Spec 16.3's
    /// "scan_runs": the last 50 scan_runs rows.</summary>
    /// <remarks>
    /// Ordered by the pair <c>started_at DESC, id DESC</c> for the reason <c>ActivityQuery</c>
    /// orders by a pair: two runs can share a timestamp at clock resolution and a sort on one
    /// column alone is not total. This is the one ORDER BY expression in the file, which is why
    /// <see cref="Latest"/> is written in terms of it.
    /// </remarks>
    public IReadOnlyList<ScanRun> Recent(int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(DatabasePaths.AsReadOnly(connectionString)));
        return context.ScanRuns
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Closes out rows left at "running" by a crash, a kill, or a shutdown drain that ran out
    /// of budget (review item 3): state "failed", error_text "interrupted", finished_at now.
    /// Called once at application start, before anything can start a new scan, so a row still
    /// "running" then belongs by definition to a process that no longer exists -- one scan at
    /// a time is process-wide. Returns how many rows were closed.
    /// </summary>
    public int MarkInterrupted()
    {
        using var context = Open();
        return context.ScanRuns
            .Where(r => r.State == "running")
            .ExecuteUpdate(setters => setters
                .SetProperty(r => r.State, "failed")
                .SetProperty(r => r.ErrorText, "interrupted")
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow));
    }

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(connectionString, tracking: true));
}
