namespace GalactiLog.Core.Io;

/// <summary>
/// What a relocation attempt did. <paramref name="Moved"/> is false for "nothing to do" and for a
/// refusal; <paramref name="Failure"/> is non-null only for a refusal or an error.
/// </summary>
/// <param name="Moved">Whether the data set now sits at <paramref name="To"/>.</param>
/// <param name="From">The source root, or null when nothing was attempted.</param>
/// <param name="To">The destination root, or null when nothing was attempted.</param>
/// <param name="FilesCopied">How many files were copied and verified.</param>
/// <param name="BytesCopied">The sum of the verified lengths.</param>
/// <param name="Skipped">Relative paths a destination file already occupied.</param>
/// <param name="Failure">One line naming the refusal or the error, or null.</param>
public sealed record RelocationOutcome(
    bool Moved,
    string? From,
    string? To,
    int FilesCopied,
    long BytesCopied,
    IReadOnlyList<string> Skipped,
    string? Failure)
{
    /// <summary>Nothing was attempted on this start.</summary>
    public static RelocationOutcome None { get; } = new(false, null, null, 0, 0, [], null);
}

/// <summary>
/// Copies one app data root's contents into another (spec 17.2). A copy, never a move: the source
/// root is left exactly as it was, at every step, on every path, including the failure paths.
/// Nothing here deletes anything.
/// </summary>
public static class AppDataRelocation
{
    /// <summary>
    /// The database file name. <c>GalactiLog.Core</c> does not reference <c>GalactiLog.Data</c>,
    /// so this constant cannot read <c>DatabasePaths.DatabaseFileName</c>, which is the
    /// definition. <c>AppDataRootStartupTests.DatabaseFileName_MatchesDatabasePaths</c> asserts
    /// the two literals are equal.
    /// </summary>
    public const string DatabaseFileName = "galactilog.db";

    /// <summary>
    /// Spec 17.2's paths table, as the exact set this copies. Nothing else moves: the source root
    /// can be the Velopack install root on the adoption path, and <c>current\</c>,
    /// <c>packages\</c> and Velopack's own files must stay where they are.
    /// </summary>
    /// <remarks>
    /// <c>galactilog.db-shm</c> is deliberately absent. SQLite rebuilds the shared-memory file
    /// from the write-ahead log, and a stale one copied beside a fresh database is worse than
    /// none.
    /// </remarks>
    public static readonly IReadOnlyList<string> RelocatedFiles = [DatabaseFileName, DatabaseFileName + "-wal"];

    /// <summary>The two directories that move with the database.</summary>
    public static readonly IReadOnlyList<string> RelocatedDirectories = ["logs", "thumbnails"];

    /// <summary>
    /// The state file this procedure writes into the destination before it copies anything and
    /// rewrites when it finishes (review finding I4). It is what tells a retry apart from another
    /// library: a destination holding a database and an unfinished marker is this application's own
    /// aborted copy, and a destination holding a database with no marker, or a finished one, is
    /// somebody's library. Never deleted, only rewritten.
    /// </summary>
    public const string MarkerFileName = ".galactilog-relocation";

    /// <summary>The marker's first word while a copy is in flight.</summary>
    public const string MarkerInProgress = "in-progress";

    /// <summary>The marker's first word once a copy has been verified.</summary>
    public const string MarkerComplete = "complete";

    /// <summary>Refusal shown when another GalactiLog process still holds the database (review
    /// finding I3).</summary>
    public const string AlreadyRunningFailure =
        "GalactiLog is already running; the move will be performed the next time it starts alone.";

    /// <summary>Refusal shown when the destination holds an earlier attempt's partial copy
    /// (review finding I4). Nothing is deleted, here or anywhere.</summary>
    public const string IncompleteCopyFormat =
        "'{0}' holds an incomplete copy from an earlier move that did not finish. Nothing was "
        + "deleted. Empty that folder yourself, or choose a different one.";

    /// <summary>Refusal shown when the destination is another library.</summary>
    public const string OccupiedDestinationFormat =
        "'{0}' already holds a {1}, and two catalogues are never merged.";

    /// <summary>
    /// Copies the relocated set from <paramref name="sourceRoot"/> into
    /// <paramref name="destination"/>'s app data root, verifies every copied file's length, and
    /// reports what it did. Never throws: every refusal and every error becomes a
    /// <see cref="RelocationOutcome"/> with <c>Moved</c> false and a <c>Failure</c> line.
    /// </summary>
    public static RelocationOutcome Run(string sourceRoot, AppWriter destination)
    {
        var source = Normalize(sourceRoot);
        var target = Normalize(destination.AppDataRoot);

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(source, target, "the new location is the same folder as the current one");
        }

        if (PathConfinement.IsUnderOrEqual(source, target) || PathConfinement.IsUnderOrEqual(target, source))
        {
            return Refused(
                source, target, "the two locations are nested, so copying one into the other is not possible");
        }

        if (!UserFiles.DirectoryExists(source))
        {
            return Refused(source, target, $"the current location '{source}' does not exist");
        }

        // Review finding I3. There is no single-instance guard in this application and ruling Q9.9
        // makes every CLI verb that builds the host perform a pending move, so a verb started while
        // the GUI is running would otherwise copy a live database and its write-ahead log; SQLite
        // shares the file for read and write, so File.Copy succeeds and the per-file length check
        // cannot see a torn copy. An exclusive open is the signal that nobody else holds it.
        // galactilog.db-shm is not used for this: it survives a crash and would refuse forever.
        var sourceDatabase = System.IO.Path.Combine(source, DatabaseFileName);
        if (UserFiles.Exists(sourceDatabase) && !UserFiles.CanOpenExclusively(sourceDatabase))
        {
            return Refused(source, target, AlreadyRunningFailure);
        }

        if (UserFiles.Exists(System.IO.Path.Combine(target, DatabaseFileName)))
        {
            // Review finding I4: an aborted copy of this application's own is not another library,
            // and telling the user their destination is a second catalogue would name a cause that
            // is not the real one.
            return Refused(
                source,
                target,
                MarkerState(target) == MarkerInProgress
                    ? string.Format(IncompleteCopyFormat, target)
                    : string.Format(OccupiedDestinationFormat, target, DatabaseFileName));
        }

        try
        {
            WriteMarker(destination, MarkerInProgress, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or UnauthorizedPathException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refused(source, target, $"'{target}' could not be prepared: {ex.Message}");
        }

        List<(string SourcePath, string Relative, long Length)> entries;
        try
        {
            entries = BuildCopyList(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or PathTooLongException)
        {
            return Refused(source, target, $"the current location could not be read: {ex.Message}");
        }

        var skipped = new List<string>();
        var copied = new List<(string Relative, long Length)>();
        foreach (var entry in entries)
        {
            try
            {
                destination.CopyFileInto(entry.SourcePath, entry.Relative);
                copied.Add((entry.Relative, entry.Length));
            }
            catch (IOException ex)
            {
                // Review finding M1: File.Copy reports a disk-full, a sharing violation and several
                // path errors as IOException too, so the destination decides which this was. A
                // destination file that is already there is left exactly as it is, because this
                // procedure overwrites nothing and deletes nothing; anything else is an abort with
                // the copy's own reason rather than the verification's.
                if (!UserFiles.Exists(System.IO.Path.Combine(target, entry.Relative)))
                {
                    return Refused(source, target, $"copying '{entry.Relative}' failed: {ex.Message}");
                }

                skipped.Add(entry.Relative);
            }
            catch (Exception ex)
            {
                return Refused(source, target, $"copying '{entry.Relative}' failed: {ex.Message}");
            }
        }

        // Every entry is verified, the skipped ones included: a destination file this procedure
        // refused to overwrite still has to be the same size as the one it stands in for, or the
        // new root does not hold the data set. Only the files this run actually copied are counted
        // into BytesCopied.
        var bytes = 0L;
        var copiedRelatives = new HashSet<string>(copied.Select(entry => entry.Relative), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            long length;
            try
            {
                length = UserFiles.GetFileInfo(System.IO.Path.Combine(target, entry.Relative)).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refused(source, target, $"'{entry.Relative}' could not be verified: {ex.Message}");
            }

            if (length != entry.Length)
            {
                return Refused(
                    source,
                    target,
                    $"'{entry.Relative}' is {length} bytes at the new location and {entry.Length} bytes at the "
                    + "current one, so the copy was not complete");
            }

            if (copiedRelatives.Contains(entry.Relative))
            {
                bytes += length;
            }
        }

        if (skipped.Contains(DatabaseFileName, StringComparer.OrdinalIgnoreCase))
        {
            // A relocation that did not carry the database is not a relocation.
            return Refused(
                source, target, $"'{DatabaseFileName}' was already present at the new location and was not copied");
        }

        try
        {
            WriteMarker(destination, MarkerComplete, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or UnauthorizedPathException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            // The data is copied and every length is verified, so this is not a failed relocation:
            // reporting one would leave the pointer unpromoted while the destination holds a
            // complete library, and the next retry would then call it an incomplete copy. The only
            // cost of a marker stuck at in-progress is that wording on a later move into this same
            // folder, so it is swallowed here rather than allowed to undo a verified move.
        }

        // The source root is left exactly as it was. Nothing was deleted, at any step.
        return new RelocationOutcome(true, source, target, copied.Count, bytes, skipped, null);
    }

    // The marker is written through AppWriter like every other write into app data, so it is
    // authorized and reparse-checked exactly as the copied files are.
    private static void WriteMarker(AppWriter destination, string state, string sourceRoot)
        => destination.WriteAllBytes(
            destination.ResolveAppDataPath(MarkerFileName),
            System.Text.Encoding.UTF8.GetBytes($"{state} {sourceRoot}"));

    // The marker's first word, or null when there is no readable marker.
    private static string? MarkerState(string root)
    {
        var path = System.IO.Path.Combine(root, MarkerFileName);
        if (!UserFiles.Exists(path))
        {
            return null;
        }

        try
        {
            var text = UserFiles.ReadAllText(path).Trim();
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            return space < 0 ? text : text[..space];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The enumeration rule the copy list uses under each relocated directory. Review finding M2:
    /// <c>AttributesToSkip</c> carries <c>ReparsePoint</c>, so a junction or a symlink placed under
    /// <c>logs\</c> or <c>thumbnails\</c> is neither descended into nor copied; without it a
    /// reparse point would pull files from outside the data root into the new one, which is the
    /// same rule the scan walker applies. The defaults <c>Hidden</c> and <c>System</c> are
    /// deliberately dropped: a hidden cache file is still a cache file and still moves.
    /// </summary>
    /// <remarks>A method rather than a field so no caller can mutate the shared instance. Public
    /// so the reparse-point rule is assertable on a machine where a test may not create a
    /// junction.</remarks>
    public static EnumerationOptions CopyEnumeration() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static List<(string SourcePath, string Relative, long Length)> BuildCopyList(string source)
    {
        var entries = new List<(string SourcePath, string Relative, long Length)>();

        foreach (var name in RelocatedFiles)
        {
            var path = System.IO.Path.Combine(source, name);
            if (UserFiles.Exists(path))
            {
                entries.Add((path, name, UserFiles.GetFileInfo(path).Length));
            }
        }

        foreach (var directory in RelocatedDirectories)
        {
            var root = System.IO.Path.Combine(source, directory);
            if (!UserFiles.DirectoryExists(root))
            {
                continue;
            }

            foreach (var path in UserFiles.EnumerateFiles(root, "*", CopyEnumeration()))
            {
                var relative = System.IO.Path.GetRelativePath(source, path);
                entries.Add((path, relative, UserFiles.GetFileInfo(path).Length));
            }
        }

        return entries;
    }

    private static RelocationOutcome Refused(string? from, string? to, string reason)
        => new(false, from, to, 0, 0, [], reason);

    private static string Normalize(string path)
        => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
}
