using GalactiLog.Core.Io;

namespace GalactiLog.Core.Diagnostics;

/// <summary>
/// The keyset position a page continues from: the last entry of the previous page. Entries
/// strictly older than this point come back, where "older" is the pair comparison
/// (Timestamp &lt; Timestamp) OR (Timestamp == Timestamp AND Ordinal &lt; Ordinal).
/// </summary>
/// <remarks>
/// The same contract as <c>GalactiLog.Data.Queries.ActivityCursor</c>, deliberately:
/// HANDOFF section 5 note 3 and design-lessons rule 1. Same two-part predicate, same
/// newest-first ordering, same "a cursor only when the page came back exactly full" rule, same
/// limit constants. It is a separate record rather than a shared type because the two have no
/// shared storage: one is a SQL WHERE clause over activity_events, this one is an in-memory
/// comparison over parsed file entries, and GalactiLog.Core cannot reference
/// GalactiLog.Data.Queries.
/// </remarks>
public sealed record LogCursor(DateTimeOffset Timestamp, long Ordinal);

/// <summary>Spec 12.8's two log viewer filters.</summary>
/// <param name="MinimumLevel">The view filter: entries at this level or above. The enum is
/// ordered ascending so this is one comparison.</param>
/// <param name="Search">Free text, matched case-insensitively against the message and nothing
/// else (ruling Q27, matching <c>logs.py</c>'s <c>AppLog.message.ilike</c>). Null or blank means
/// no text filter.</param>
public sealed record LogFilters(
    LogLineLevel MinimumLevel = LogLineLevel.Verbose, string? Search = null);

/// <summary>One page of the log viewer's list.</summary>
/// <param name="Lines">Matching entries, newest first.</param>
/// <param name="NextCursor">Non-null only when the page came back exactly full, the identical
/// rule <c>ActivityQuery.Page</c> applies. A full final page therefore costs one extra empty
/// read, which is the price of not counting the remainder on every call.</param>
/// <param name="ReachedOldest">The walk exhausted every retained file without filling the page,
/// so there is nothing older to read.</param>
public sealed record LogPage(
    IReadOnlyList<LogLine> Lines, LogCursor? NextCursor, bool ReachedOldest);

/// <summary>
/// Spec 12.8's log viewer read: a reverse-chronological, keyset-paged view over the current
/// Serilog file and the retained rolled files beside it.
/// </summary>
/// <remarks>
/// <para>
/// Every file handle comes from <see cref="UserFiles.OpenRead"/>, which opens
/// <c>FileAccess.Read, FileShare.ReadWrite</c>. That share mode is what lets this read the file
/// the live sink currently holds open, and it is why nothing here constructs a
/// <c>FileStream</c>, calls <c>File.ReadAllLines</c>, or wraps a <c>StreamReader</c> around a path
/// string: the default share mode would fail against the live sink. Read-only throughout; the
/// application never deletes, moves or modifies a file (spec 2.1) and Serilog owns rotation and
/// retention.
/// </para>
/// <para>
/// The files are walked newest first (<see cref="LogFileSet.Newest"/>), and within a file the
/// entries are read <b>forward</b> and then walked in reverse, because a Serilog file is
/// append-only text with no index and a backwards scan over a 32 MB file for a UTF-8 line
/// boundary is a second parser.
/// </para>
/// <para>
/// <b>Accepted ceiling.</b> Reading one entry requires reading the whole file that contains it up
/// to that point, so paging back through a full 32 MB rolled file costs one forward pass of that
/// file per page. With 14 retained files at the 32 MB cap that is the worst case a user with
/// <c>log_level: Verbose</c> and a long session can reach. The upgrade path is a per-file line
/// offset index built once per file and memoized on (path, length), never a second reader.
/// </para>
/// <para>
/// <b>Second accepted ceiling, at a file boundary.</b> <see cref="LogLine.Ordinal"/> is assigned
/// per file, so the ordinal half of the cursor comparison is only meaningful within one file.
/// <c>rollOnFileSizeLimit: true</c> can split two entries written in the same millisecond across
/// <c>galactilog-20260915.log</c> and <c>galactilog-20260915_001.log</c>: if a page ends on the
/// oldest entry of the newer file and the newest entry of the older file carries the identical
/// millisecond with a higher ordinal, that one entry satisfies neither half of the predicate and
/// is skipped. It needs a same-millisecond write straddling a 32 MB size roll, and it costs one
/// line of a log file. The complete fix is to carry the file identity in <see cref="LogCursor"/>
/// and compare ordinals only within one file; the brief ruled out a global monotonic id, which is
/// the other way to close it (review finding M1).
/// </para>
/// <para>
/// <b>Nothing here logs.</b> A file that cannot be opened is skipped and the walk continues, a
/// line the parser does not recognize is a continuation rather than an error, and a missing
/// directory is an empty page. Logging from inside the log reader is a loop.
/// </para>
/// </remarks>
/// <param name="logDirectory">The rolling log directory, which <c>AppHost</c> composes from
/// <c>DiagnosticsService.LogDirectoryName</c> under the authorized app data root.</param>
public sealed class LogReader(string logDirectory)
{
    /// <summary><c>ActivityQuery.DefaultLimit</c>.</summary>
    public const int DefaultLimit = 50;

    /// <summary><c>ActivityQuery.MinLimit</c>.</summary>
    public const int MinLimit = 1;

    /// <summary><c>ActivityQuery.MaxLimit</c>.</summary>
    public const int MaxLimit = 200;

    /// <summary>One page of entries, newest first. Blocking file IO; every caller runs it off
    /// the UI thread.</summary>
    /// <param name="filters">The minimum level and the free-text term.</param>
    /// <param name="before">The previous page's <see cref="LogPage.NextCursor"/>, or null for the
    /// first page.</param>
    /// <param name="limit">Clamped to <see cref="MinLimit"/>..<see cref="MaxLimit"/> rather than
    /// validated, the identical clamp <c>ActivityQuery.Page</c> applies: the caller is a UI
    /// control, and a page size out of range is a bug to correct silently, not a reason to fail a
    /// read of the log.</param>
    public LogPage Page(LogFilters filters, LogCursor? before = null, int limit = DefaultLimit)
    {
        var pageSize = Math.Clamp(limit, MinLimit, MaxLimit);
        var lines = Collect(filters, before, pageSize, out var reachedOldest);

        // ActivityQuery.Page's rule, verbatim: a cursor only when the page came back exactly full.
        var nextCursor = lines.Count == pageSize && lines.Count > 0
            ? new LogCursor(lines[^1].Timestamp, lines[^1].Ordinal)
            : null;

        return new LogPage(lines, nextCursor, reachedOldest);
    }

    /// <summary>Every entry matching the filters, newest first, capped at
    /// <paramref name="cap"/>. Backs "copy all" (ruling Q26's 50,000) and Phase 10 Task 3's
    /// bundle, which asks for 500.</summary>
    public IReadOnlyList<LogLine> All(LogFilters filters, int cap)
        => cap <= 0 ? [] : Collect(filters, null, cap, out _);

    // The one walk both members use. Files newest first, entries within a file in reverse read
    // order, stopping at the first file that fills the request, so the ordinary case reads exactly
    // one file.
    private List<LogLine> Collect(
        LogFilters filters, LogCursor? before, int max, out bool reachedOldest)
    {
        var search = string.IsNullOrWhiteSpace(filters.Search) ? null : filters.Search.Trim();
        var collected = new List<LogLine>(Math.Min(max, 256));

        foreach (var path in LogFileSet.Newest(logDirectory))
        {
            var entries = ReadEntries(path);
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries[index];
                if (!Matches(entry, filters.MinimumLevel, search) || !IsOlderThan(entry, before))
                {
                    continue;
                }

                collected.Add(entry);
                if (collected.Count == max)
                {
                    reachedOldest = false;
                    return collected;
                }
            }
        }

        reachedOldest = true;
        return collected;
    }

    private static bool Matches(LogLine line, LogLineLevel minimum, string? search)
        => line.Level >= minimum
            && (search is null || line.Message.Contains(search, StringComparison.OrdinalIgnoreCase));

    // The two-part keyset comparison, the same shape ActivityQuery.Page binds into SQL.
    private static bool IsOlderThan(LogLine line, LogCursor? before)
        => before is null
            || line.Timestamp < before.Timestamp
            || (line.Timestamp == before.Timestamp && line.Ordinal < before.Ordinal);

    // One file, parsed forward into whole entries, oldest first.
    //
    // Ordinal is assigned per file, ascending from 0 in read order, and is only ever compared
    // against another Ordinal carrying the same timestamp. Two entries in different files can
    // therefore share an ordinal; they cannot share a timestamp to the millisecond AND an ordinal
    // and be different entries in practice, and the pair is a tiebreaker rather than a key. A
    // global monotonic id would require reading every file to page the newest one, which is
    // exactly the cost this reader exists to avoid.
    //
    // The one case this loses is stated in the class comment: a same-millisecond pair straddling a
    // size roll, where the older file's entry carries the higher ordinal, is skipped by the cursor
    // predicate. Accepted, not overlooked.
    private static List<LogLine> ReadEntries(string path)
    {
        var entries = new List<LogLine>();

        try
        {
            // UserFiles.OpenRead, never a FileStream or a path-taking StreamReader: the share mode
            // is the whole point (spec 2.1.1).
            using var stream = UserFiles.OpenRead(path);
            using var reader = new StreamReader(stream);

            long ordinal = 0;
            string? header = null;
            var timestamp = default(DateTimeOffset);
            var level = default(LogLineLevel);
            var sourceContext = "";
            var message = "";
            List<string>? continuation = null;

            while (reader.ReadLine() is { } line)
            {
                if (LogLineParser.TryParseHeader(
                        line, out var nextTimestamp, out var nextLevel,
                        out var nextSourceContext, out var nextMessage))
                {
                    if (header is not null)
                    {
                        entries.Add(Build(
                            timestamp, level, sourceContext, message, header, continuation, ordinal++));
                    }

                    header = line;
                    timestamp = nextTimestamp;
                    level = nextLevel;
                    sourceContext = nextSourceContext;
                    message = nextMessage;
                    continuation = null;
                }
                else if (header is not null)
                {
                    (continuation ??= []).Add(line);
                }

                // Otherwise: a line before the first entry start, which is the tail of an entry
                // this reader never saw the head of. Dropped, not reported.
            }

            if (header is not null)
            {
                entries.Add(Build(
                    timestamp, level, sourceContext, message, header, continuation, ordinal));
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Deleted between enumeration and open, or held exclusively by a backup tool. Whatever
            // was read before the failure is kept and the walk continues with the next file.
        }

        return entries;
    }

    private static LogLine Build(
        DateTimeOffset timestamp, LogLineLevel level, string sourceContext, string message,
        string header, List<string>? continuation, long ordinal)
    {
        var exception = continuation is null
            ? null
            : string.Join(Environment.NewLine, continuation);

        return new LogLine(
            timestamp,
            level,
            sourceContext,
            message,
            exception,
            ordinal,
            exception is null ? header : header + Environment.NewLine + exception);
    }
}
