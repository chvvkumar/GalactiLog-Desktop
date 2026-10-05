using GalactiLog.Core.Io;

namespace GalactiLog.Core.Diagnostics;

/// <summary>
/// The retained Serilog rolling files in one directory, newest first. Read-only: nothing here
/// creates, deletes or touches a file. Serilog owns rotation and retention (spec 16.1), and
/// <c>retainedFileCountLimit: 14</c> is its rule, not this class's.
/// </summary>
public static class LogFileSet
{
    /// <summary>The glob the sink's rolled files match. <c>galactilog-.log</c> with
    /// <c>RollingInterval.Day</c> produces <c>galactilog-20260915.log</c>, and
    /// <c>rollOnFileSizeLimit</c> adds <c>_001</c>, <c>_002</c> within a day:
    /// <c>galactilog-20260915_001.log</c>.</summary>
    /// <remarks>
    /// Built from <see cref="FileNamePrefix"/>, which is the one definition of the stem:
    /// <c>DiagnosticsService.LogFileNamePrefix</c> reads it from here, and <c>AppHost</c> composes
    /// the sink path from that. One name, one spelling, three consumers.
    /// </remarks>
    public const string SearchPattern = FileNamePrefix + "*" + NameSuffix;

    /// <summary>The rolling log file's stem, before Serilog appends the date and the extension:
    /// <c>galactilog-20260915.log</c>. The App layer reads this constant rather than repeating the
    /// literal (review round 1, ruled escalation).</summary>
    public const string FileNamePrefix = "galactilog-";

    private const string NameSuffix = ".log";

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,

        // A log directory the process cannot list is reported as no files rather than as an
        // exception: a diagnostics reader must degrade, never take the page down.
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>
    /// Every retained log file in the directory, newest first, by the date and sequence encoded in
    /// the name and not by last-write time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered by the file name, descending, with <see cref="StringComparer.Ordinal"/>. The names
    /// are fixed width (<c>yyyyMMdd</c>, then an optional <c>_NNN</c>), so an ordinal descending
    /// sort is chronological descending, and within a day the higher sequence number is the newer
    /// file. Deliberately NOT ordered by <c>LastWriteTimeUtc</c>: a file copied or restored carries
    /// a misleading timestamp, and the sink itself touches only the newest file.
    /// </para>
    /// <para>
    /// A missing directory returns an empty list rather than throwing. A GUI start that logged
    /// nothing before the first read is a real case, even though <c>AppHost</c> writes one
    /// unconditional startup line precisely to avoid it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Newest(string logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !UserFiles.DirectoryExists(logDirectory))
        {
            return [];
        }

        try
        {
            return
            [
                .. UserFiles.EnumerateFiles(logDirectory, SearchPattern, Options)
                    .Where(Matches)
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The directory went away, or a network path stopped answering, between the guard
            // above and the enumeration. No files is the honest answer; nothing is logged from
            // here, because logging from the log reader is a loop.
            return [];
        }
    }

    /// <summary>
    /// The file the rolling sink currently holds open, or null for an empty set. Identified from
    /// the sink's own naming, never by position in <paramref name="files"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification E1. <c>LogViewerViewModel.IdentifyLogFiles</c> took <c>files[0]</c>, which is
    /// right only for as long as the caller passes a list <see cref="Newest"/> produced and
    /// <see cref="Newest"/> keeps its ordering. Clear log deletes everything this member does not
    /// name, so "the first element of whatever list arrives" is the wrong contract for a delete:
    /// the rule is read off the name here, and the answer does not change if the list is shuffled
    /// or reversed.
    /// </para>
    /// <para>
    /// The sink's name is <c>galactilog-yyyyMMdd.log</c>, plus <c>_NNN</c> within a day once
    /// <c>rollOnFileSizeLimit</c> has fired. The open file is the greatest (date, sequence) pair,
    /// compared as a date and an integer rather than as text. Ordinal text order happens to agree
    /// while Serilog zero-pads the sequence to three digits, and stops agreeing at <c>_1000</c>;
    /// this does not depend on the padding at all.
    /// </para>
    /// <para>
    /// A set in which nothing carries a parseable stamp (a hand-renamed file, a restored backup)
    /// falls back to the newest <c>LastWriteTimeUtc</c>, which is a worse rule than the name and a
    /// far better one than position. A file whose timestamp cannot be read is treated as the
    /// oldest, so it is never mistaken for the live one and never spared by accident.
    /// </para>
    /// </remarks>
    public static string? Live(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return null;
        }

        string? live = null;
        (DateOnly Date, int Sequence) newest = default;

        foreach (var path in files)
        {
            if (!TryParseStamp(Path.GetFileName(path), out var stamp))
            {
                continue;
            }

            if (live is null || stamp.CompareTo(newest) > 0)
            {
                live = path;
                newest = stamp;
            }
        }

        return live ?? files.OrderByDescending(LastWriteUtc).First();
    }

    /// <summary>The date and the within-day sequence encoded in a sink file's name.</summary>
    internal static bool TryParseStamp(string fileName, out (DateOnly Date, int Sequence) stamp)
    {
        stamp = default;

        if (!fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(NameSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var body = fileName[FileNamePrefix.Length..^NameSuffix.Length];
        var sequence = 0;
        var underscore = body.IndexOf('_', StringComparison.Ordinal);
        if (underscore >= 0)
        {
            if (!int.TryParse(
                    body[(underscore + 1)..],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out sequence))
            {
                return false;
            }

            body = body[..underscore];
        }

        if (!DateOnly.TryParseExact(
                body,
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var date))
        {
            return false;
        }

        stamp = (date, sequence);
        return true;
    }

    private static DateTime LastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    // Windows narrows a three-character extension in a search pattern to a prefix match, so
    // "galactilog-*.log" alone would also return galactilog-20260915.log.bak through the shell's
    // matching rules. The explicit check makes the pattern mean exactly what it reads as.
    private static bool Matches(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(NameSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
