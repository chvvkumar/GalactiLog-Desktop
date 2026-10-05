namespace GalactiLog.Core.Io;

// The read-only half of the file safety choke point (spec 2.1.1). Every read of a scan
// root, or of any path discovered by scanning, goes through this class. It has no write
// members of any kind: no create, no overwrite, no delete, no move, no rename, no
// attribute or timestamp setter. There is nothing to call, so there is nothing to forget.
//
// Opens with FileAccess.Read and FileShare.ReadWrite so a file N.I.N.A. still holds open
// can still be read. Do not use File.ReadAllBytes/File.ReadAllText here: their default
// share mode is FileShare.Read, which does not match this requirement.
public static class UserFiles
{
    public static FileStream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    public static byte[] ReadAllBytes(string path)
    {
        using var stream = OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string ReadAllText(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>
    /// Whether this process can open <paramref name="path"/> for reading with no sharing at all,
    /// which is how the data root relocation learns that another GalactiLog process still holds
    /// the database (review finding I3). The handle is closed immediately.
    /// </summary>
    /// <remarks>
    /// A read like every other member here: <c>FileMode.Open</c> never creates a file, the access
    /// is <c>FileAccess.Read</c>, and nothing is written, truncated or deleted. False for a file
    /// another process holds, and for one this process may not read at all.
    /// </remarks>
    public static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool DirectoryExists(string path) => Directory.Exists(path);

    public static IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions options)
        => Directory.EnumerateFiles(path, searchPattern, options);

    public static IEnumerable<string> EnumerateDirectories(string path)
        => Directory.EnumerateDirectories(path);

    // For reading properties only (Length, LastWriteTimeUtc, and similar). Nothing in this
    // codebase may call .Delete(), .MoveTo(), .Create(), or .CreateText() on a FileInfo
    // obtained here; FileSafetyTest checks for exactly that.
    public static FileInfo GetFileInfo(string path) => new FileInfo(path);

    // Directory-entry enumeration for the scan walker (Phase 4 Task 3): one
    // EnumerationOptions-configured DirectoryInfo.EnumerateFileSystemInfos call per directory,
    // matching scanner.py's single os.scandir() per directory (files and subdirectories
    // returned together, so the walker tests each entry's FileAttributes itself instead of
    // making two separate EnumerateFiles/EnumerateDirectories calls). Non-recursive;
    // FileWalker recurses explicitly so directory pruning happens before descent. The returned
    // infos carry attributes, size and last-write time from the directory listing itself, so
    // the walker takes no further stat per entry.
    //
    // IgnoreInaccessible = false is deliberate, not an oversight: verified empirically that
    // with IgnoreInaccessible = true, enumerating a directory this process cannot list
    // returns an EMPTY sequence with no exception at all, rather than throwing. That would
    // make design-spec 10.3's "inaccessible entries are logged at warning and skipped"
    // impossible to implement -- there would be nothing to catch, and a permission-denied
    // subtree would silently look identical to an empty one. Setting it false makes the
    // enumeration call itself throw UnauthorizedAccessException/IOException for a
    // permission-denied or stale-handle directory, which is what lets FileWalker catch it,
    // emit a warning, and skip just that one directory.
    public static IEnumerable<FileSystemInfo> EnumerateFileSystemEntries(string path)
        => new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = 0,
        });

    // Resolves a directory reparse point (junction or symlink) to its final physical target.
    // Null when path is not itself a reparse point, or when the target cannot be resolved (a
    // broken link) -- both cases mean "nothing further to follow."
    public static string? ResolveDirectoryLinkTarget(string path)
    {
        try { return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName; }
        catch (IOException) { return null; }
    }
}
