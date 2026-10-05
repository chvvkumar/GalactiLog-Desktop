using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Repositories;

/// <summary>One stored guide log's identity for the delta skip of spec 10.3 step 2.</summary>
/// <param name="FilePath">The path as it was walked and stored.</param>
/// <param name="FileSize">Bytes, or null when the walk could not read it.</param>
/// <param name="FileMtime">Unix seconds, compared with the 1.0 second tolerance
/// <c>images</c> uses.</param>
public sealed record Phd2StoredFile(string FilePath, long? FileSize, double? FileMtime);

/// <summary>
/// The guide-log tables of spec 5.15 to 5.18, as the discovery pass and the correlation use them.
/// Entities only: nothing here parses, correlates, reads a setting or names a
/// <c>GalactiLog.Core.Phd2</c> type. The caller hands over rows it has already built.
/// </summary>
/// <remarks>
/// THIS TYPE DELETES DATABASE ROWS ONLY. IT NEVER DELETES, MOVES, RENAMES, OR OTHERWISE
/// MODIFIES ANY FILE OR DIRECTORY ON DISK, UNDER ANY CIRCUMSTANCE (spec 2.1). It performs no
/// filesystem access at all: every path it handles is a string the caller collected through the
/// read-only <c>UserFiles</c> gateway. The only <c>System.IO</c> member referenced here is
/// <c>Path</c> (pure string manipulation, no I/O). <c>FileSafetyTest</c> (spec 2.1.3) source-scans
/// <c>src/**</c> and fails the build if that ever stops being true; this file needs no allowlist
/// exception and must never be given one.
/// <para>
/// Sessions, frames and calibrations are never deleted directly. Every delete here removes a
/// <c>phd2_logs</c> row and lets the cascade of spec 5.16 to 5.18 take the rest, so there is one
/// delete path and no way to strand a frame row.
/// </para>
/// </remarks>
public sealed class Phd2Repository(string connectionString)
{
    /// <summary>
    /// Every stored log's path, size and mtime, for the delta skip of spec 10.3 step 2. Read-only.
    /// </summary>
    public IReadOnlyList<Phd2StoredFile> StoredFiles()
    {
        using var context = OpenReadOnly();
        return context.Phd2Logs
            .Select(log => new Phd2StoredFile(log.FilePath, log.FileSize, log.FileMtime))
            .ToList();
    }

    /// <summary>
    /// Every stored log path that lies under one of <paramref name="roots"/>, for the orphan drop
    /// of spec 10.3 step 4. A row under no walked root is not an orphan: it belongs to a root the
    /// user removed from the scan set, and dropping it would delete the history of a library that
    /// was merely unplugged.
    /// </summary>
    /// <remarks>
    /// The prefix test runs in memory rather than as SQL, and deliberately: the comparison is
    /// <c>OrdinalIgnoreCase</c> over a normalized directory separator, which SQLite's
    /// <c>LIKE</c> cannot express, and the guide-log corpus is a few thousand paths.
    /// </remarks>
    public IReadOnlyList<string> StoredPathsUnder(IReadOnlyCollection<string> roots)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        var prefixes = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(WithTrailingSeparator)
            .ToList();
        if (prefixes.Count == 0)
        {
            return [];
        }

        using var context = OpenReadOnly();
        return context.Phd2Logs
            .Select(log => log.FilePath)
            .AsEnumerable()
            .Where(path => prefixes.Any(
                prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Deletes the <c>phd2_logs</c> row at <paramref name="filePath"/> and, through the cascade,
    /// its sessions, frames and calibrations. Returns how many log rows went, which is 0 or 1.
    /// </summary>
    public int DeleteByPath(string filePath) => DeleteByPaths([filePath]);

    /// <summary>
    /// Deletes a batch of <c>phd2_logs</c> rows by path, relying on the same cascade. Returns how
    /// many log rows went.
    /// </summary>
    /// <remarks>
    /// Chunked at <see cref="OrphanPruner.DeleteBatchSize"/>, the frame side's own constant and
    /// the one declaration of that number: an unbounded <c>IN (...)</c> list over a whole library
    /// exceeds SQLite's parameter limit and fails the whole prune rather than a batch of it. The
    /// count is summed across the chunks, so a caller that reports it reports what is actually
    /// gone.
    /// </remarks>
    public int DeleteByPaths(IReadOnlyCollection<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return 0;
        }

        using var context = Open();
        var removed = 0;
        foreach (var batch in filePaths.Chunk(OrphanPruner.DeleteBatchSize))
        {
            removed += context.Phd2Logs
                .Where(log => batch.Contains(log.FilePath))
                .ExecuteDelete();
        }

        return removed;
    }

    /// <summary>
    /// Spec 10.3 step 3, "one file is one transaction": the log row and every session, frame and
    /// calibration under it are written together or not at all. Any row already stored for the
    /// same path is deleted first, so a re-ingest replaces rather than duplicates.
    /// </summary>
    /// <param name="log">The <c>phd2_logs</c> row, already built by the caller.</param>
    /// <param name="sessions">Its sessions, with <c>log_id</c> already set.</param>
    /// <param name="frames">Their frames, with <c>session_id</c> already set.</param>
    /// <param name="calibrations">Its calibration blocks, with <c>log_id</c> already set.</param>
    /// <remarks>
    /// The frame rows are written in chunks of <see cref="FrameBatchSize"/> with change detection
    /// off, and the tracker is emptied between chunks, so one desktop guide log of hundreds of
    /// thousands of frames does not sit in an EF change tracker whole (phase review P2-1b). The
    /// chunks are inside the one transaction, so the file is still replaced atomically: a throw in
    /// the last chunk rolls back the first chunk, the sessions, the log row and the pre-delete
    /// together.
    /// </remarks>
    public void Insert(
        Phd2Log log,
        IReadOnlyList<Phd2Session> sessions,
        IReadOnlyList<Phd2Frame> frames,
        IReadOnlyList<Phd2Calibration> calibrations)
    {
        using var context = Open();

        // Nothing here mutates a tracked entity after adding it, so the sweep DetectChanges makes
        // before every SaveChanges has nothing to find and is pure cost over a chunk of thousands.
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        using var transaction = context.Database.BeginTransaction();

        // The path is the identity (spec 5.15's unique index), so a re-parse of a changed file
        // replaces the old row rather than colliding with it. The cascade takes the children.
        context.Phd2Logs.Where(row => row.FilePath == log.FilePath).ExecuteDelete();

        // The parents first and in one save: a frame's foreign key is checked against the session
        // rows this statement writes.
        context.Phd2Logs.Add(log);
        context.Phd2Sessions.AddRange(sessions);
        context.Phd2Calibrations.AddRange(calibrations);
        context.SaveChanges();
        context.ChangeTracker.Clear();

        InsertFrames(context, frames);

        transaction.Commit();
    }

    /// <summary>
    /// How many frame rows one <c>SaveChanges</c> carries. A few thousand is large enough that the
    /// per-statement overhead is amortised and small enough that the tracker's peak is bounded by
    /// this figure rather than by the size of the log.
    /// </summary>
    public const int FrameBatchSize = 5000;

    /// <summary>
    /// The chunked frame write <see cref="Insert"/> performs, on the caller's context and inside
    /// whatever transaction the caller has opened.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than private so <c>Phd2RepositoryTests</c> can watch the change
    /// tracker across a chunk boundary, which is the only place the bound is observable from
    /// outside: <see cref="Insert"/> owns its own context and nothing can reach into it. One
    /// declaration, so the member the case watches is the member <see cref="Insert"/> runs.
    /// </remarks>
    internal static void InsertFrames(GalactiLogContext context, IReadOnlyList<Phd2Frame> frames)
    {
        foreach (var batch in frames.Chunk(FrameBatchSize))
        {
            context.Phd2Frames.AddRange(batch);
            context.SaveChanges();

            // The chunk is on disk (inside the transaction) and is never read back through this
            // context, so keeping it tracked would only grow the tracker for the next chunk.
            context.ChangeTracker.Clear();
        }
    }

    // A trailing separator, so "C:\Astro2" is not read as lying under "C:\Astro".
    private static string WithTrailingSeparator(string root)
        => root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(connectionString, tracking: true));

    private GalactiLogContext OpenReadOnly()
        => new(GalactiLogContextOptions.Create(DatabasePaths.AsReadOnly(connectionString)));
}
