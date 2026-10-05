namespace GalactiLog.Core.Io;

// The write half of the file safety choke point (spec 2.1.1). Every filesystem write in
// the solution goes through this class. Exactly four authorized roots exist: the app
// data directory, the thumbnail cache directory, a one-shot export destination, and one
// staging root, valid for one operation, that takes new files and directories only. This
// class enforces the first two directly; BeginExport and BeginStagingCopy enforce the other
// two through nested scoped writers.
//
// Instance class, not static, because ThumbnailCacheRoot is mutable at runtime: the user
// can relocate the thumbnail cache from Settings (general.thumbnail_cache_dir), and
// relocating it does not grant write access anywhere else.
//
// Phase 10 Task 9 adds one named exception to the "exactly four roots" sentence above, and it
// is a file rather than a root: the data location pointer, %APPDATA%\GalactiLog\datapath.json
// (spec 2.1.1, 17.2). Its path is fixed at construction, WriteDataRootPointer takes no path
// argument, and Authorize is unchanged, so no caller-supplied path can reach it or anything else
// outside the two roots.
//
// This is the single file allowlisted by FileSafetyTest (spec 2.1.3): every write-capable
// call in the codebase must live here.
public sealed class AppWriter
{
    public string AppDataRoot { get; }

    /// <summary>
    /// The data location pointer, normally <see cref="DefaultDataRootPointerPath"/>. Fixed at
    /// construction so no caller can name a different one; a test passes a temp path instead.
    /// </summary>
    public string DataRootPointerPath { get; }

    /// <summary>The default pointer path, <c>%APPDATA%\GalactiLog\datapath.json</c>. Outside the
    /// Velopack install root, so an uninstall does not reach it, and outside the data root, so it
    /// survives a move of the data root (spec 17.2).</summary>
    public static string DefaultDataRootPointerPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GalactiLog",
        "datapath.json");

    private string _thumbnailCacheRoot = null!;

    // Normalizes and rejects a drive root or UNC share root: either would make "under this
    // root" match the entire drive or share, which defeats the whole point of scoping
    // writes to a specific directory.
    public string ThumbnailCacheRoot
    {
        get => _thumbnailCacheRoot;
        set
        {
            var full = Path.GetFullPath(value);
            if (IsDriveOrShareRoot(full))
            {
                throw new UnauthorizedPathException(full);
            }
            _thumbnailCacheRoot = full;
        }
    }

    public AppWriter(string appDataRoot, string? thumbnailCacheRoot = null, string? dataRootPointerPath = null)
    {
        var appDataFull = Path.GetFullPath(appDataRoot);
        if (IsDriveOrShareRoot(appDataFull))
        {
            throw new UnauthorizedPathException(appDataFull);
        }
        AppDataRoot = appDataFull;
        ThumbnailCacheRoot = thumbnailCacheRoot ?? Path.Combine(AppDataRoot, "thumbnails");
        DataRootPointerPath = Path.GetFullPath(dataRootPointerPath ?? DefaultDataRootPointerPath);
    }

    // Combines a relative path under AppDataRoot and returns the resulting absolute path.
    // Does not touch disk. Throws if the combined path would escape AppDataRoot (a ".."
    // segment, or an absolute relativePath pointing elsewhere).
    public string ResolveAppDataPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(AppDataRoot, relativePath));
        if (!PathConfinement.IsUnderOrEqual(AppDataRoot, full))
        {
            throw new UnauthorizedPathException(full);
        }
        return full;
    }

    // Combines a relative path under ThumbnailCacheRoot and returns the absolute path. The twin
    // of ResolveAppDataPath for the second authorized root, and the one place the thumbnail cache
    // root is combined with a relative path: nothing outside this class may compose that path
    // itself (design-lessons rule 2, enforcement at the choke point).
    //
    // Throws UnauthorizedPathException if the combined path would escape the cache root (a ".."
    // segment, or an absolute relativePath pointing elsewhere, including one under AppDataRoot),
    // or if a reparse point sits anywhere between the root and the leaf. A read at most: the
    // reparse walk reads attributes and nothing here creates, writes or deletes.
    public string ResolveThumbnailPath(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(ThumbnailCacheRoot, relativePath));
        if (!PathConfinement.IsUnderOrEqual(ThumbnailCacheRoot, combined))
        {
            throw new UnauthorizedPathException(combined);
        }
        // Authorize would also accept AppDataRoot, which the check above has already excluded;
        // calling it keeps the reparse-point rule in exactly one place.
        return Authorize(combined);
    }

    // Enumerates the files directly under one subdirectory of the thumbnail cache, as
    // (absolute path, length in bytes, last write time UTC). A read, not a write: nothing here
    // creates, deletes or modifies anything. Exists so ThumbnailCache's eviction sweep resolves
    // the root through the same authorization every write goes through, instead of composing the
    // path itself. Returns an empty sequence for a subdirectory that does not exist.
    //
    // Materialized rather than lazy so an unauthorized relativeSubdirectory throws here, at the
    // call, rather than on the caller's first MoveNext.
    public IReadOnlyList<(string Path, long Length, DateTime LastWriteUtc)> EnumerateThumbnailFiles(
        string relativeSubdirectory, string searchPattern)
    {
        var directory = ResolveThumbnailPath(relativeSubdirectory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = new List<(string Path, long Length, DateTime LastWriteUtc)>();
        foreach (var path in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            files.Add((path, info.Length, info.LastWriteTimeUtc));
        }
        return files;
    }

    // Creates a directory. path must already be an absolute path under AppDataRoot or
    // ThumbnailCacheRoot (typically the output of ResolveAppDataPath, or ThumbnailCacheRoot
    // itself, or a path under it).
    public void CreateDirectory(string path)
    {
        var full = Authorize(path);
        Directory.CreateDirectory(full);
    }

    // Writes bytes to path, creating the file and any missing parent directories. path must
    // already be an absolute path under AppDataRoot or ThumbnailCacheRoot.
    public void WriteAllBytes(string path, byte[] bytes)
    {
        var full = Authorize(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    // Deletes a file. path must already be an absolute path under AppDataRoot or
    // ThumbnailCacheRoot. A missing file is not an error (idempotent).
    public void Delete(string path)
    {
        var full = Authorize(path);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    /// <summary>
    /// Writes the data location pointer, and nothing else. Takes no path: the only destination is
    /// <see cref="DataRootPointerPath"/>, fixed at construction. Creates the parent directory.
    /// </summary>
    /// <remarks>
    /// Not an authorized root (spec 2.1.1). <c>Authorize</c> is unchanged, so
    /// <see cref="WriteAllBytes"/>, <see cref="Delete"/> and <see cref="CreateDirectory"/> still
    /// reach only the app data root and the thumbnail cache root, and no caller-supplied path can
    /// reach <c>%APPDATA%</c>. Reads of the pointer need no authorization and go through
    /// <c>UserFiles.ReadAllText</c>.
    /// </remarks>
    public void WriteDataRootPointer(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataRootPointerPath)!);

        // Review finding I2: written to a sibling temp file and then moved over the target, never
        // truncate-then-write. A process killed part way through a direct write leaves a truncated
        // document, which the next start reads as "no pointer" and answers by opening the default
        // root, destroying the only record of the root the user chose. File.Move with
        // overwrite: true is one rename on the same directory, so a reader sees the old document or
        // the new one and never a half-written one. Both calls are inside this file, which is the
        // allowlisted one; no FileSafetyTest entry changes.
        var staging = DataRootPointerPath + ".tmp";
        File.WriteAllText(staging, contents);
        File.Move(staging, DataRootPointerPath, overwrite: true);
    }

    /// <summary>
    /// Copies one file from anywhere on disk to a relative path under
    /// <see cref="AppDataRoot"/>. The destination is authorized like every other write; the source
    /// is a read. Refuses to overwrite: an existing destination throws
    /// <see cref="IOException"/>, which the relocation reports as a skip.
    /// </summary>
    /// <remarks>The one copy into app data, and the reason <c>File.Copy</c> stays allowlisted to
    /// this file alone (spec 2.1.3). Nothing here deletes anything, on any path.</remarks>
    public void CopyFileInto(string sourcePath, string relativeDestination)
    {
        var full = ResolveAppDataPath(relativeDestination);
        Authorize(full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(sourcePath, full, overwrite: false);
    }

    // The sole route to a write destination outside AppDataRoot and ThumbnailCacheRoot:
    // one path the user picked in a save dialog (spec 2.1.1, third row). destination is
    // used exactly as given (already absolute, from the dialog).
    public ExportWriter BeginExport(string destination) => new ExportWriter(Path.GetFullPath(destination));

    // The sole route to the staging root the user picked in the export wizard (spec 2.1.1,
    // fourth row). Refuses a drive or share root for the same reason ThumbnailCacheRoot does.
    public StagingWriter BeginStagingCopy(string root)
    {
        var full = Path.GetFullPath(root);
        if (IsDriveOrShareRoot(full))
        {
            throw new UnauthorizedPathException(full);
        }
        return new StagingWriter(full);
    }

    private string Authorize(string path)
    {
        var full = Path.GetFullPath(path);
        string? matchedRoot = PathConfinement.IsUnderOrEqual(AppDataRoot, full) ? AppDataRoot
            : PathConfinement.IsUnderOrEqual(ThumbnailCacheRoot, full) ? ThumbnailCacheRoot
            : null;
        if (matchedRoot is null)
        {
            throw new UnauthorizedPathException(full);
        }

        ThrowIfReparsePointBetween(matchedRoot, full);
        return full;
    }

    // Public so other trust-boundary checks (SettingsStore's thumbnail_cache_dir validation)
    // can reuse the same "is this an entire drive or share" rule instead of re-deriving it.
    public static bool IsDriveOrShareRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrEmpty(root)
            && string.Equals(TrimTrailingSeparator(fullPath), TrimTrailingSeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    // Coordinator ruling: a junction or symlink anywhere between an authorized root and
    // the candidate path (leaf included, so a file symlink at the leaf is also caught)
    // could make a path that textually resolves under the root actually land on a scan
    // root or other unauthorized location on disk. Reject any reparse point in that chain
    // rather than follow it. Reading attributes is not a write, so this is allowed in this
    // allowlisted file.
    //
    // ponytail: hard links carry no ReparsePoint attribute and are undetectable by this
    // walk; known ceiling, accepted.
    private static void ThrowIfReparsePointBetween(string root, string full)
    {
        var normalizedRoot = TrimTrailingSeparator(root);
        var dir = full;
        while (!string.IsNullOrEmpty(dir))
        {
            if ((File.Exists(dir) || Directory.Exists(dir)) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedPathException(full);
            }
            if (string.Equals(TrimTrailingSeparator(dir), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static string TrimTrailingSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public sealed class ExportWriter : IDisposable
    {
        private readonly string _destination;
        private bool _disposed;

        internal ExportWriter(string destination) => _destination = destination;

        // path must equal the destination this writer was opened for (Path.GetFullPath
        // compared case-insensitively); any other path throws UnauthorizedPathException.
        // Creates a new file, overwriting if one exists (the save dialog's own overwrite
        // confirmation is the only sanctioned replacement path, per spec 16.3).
        public void WriteAllBytes(string path, byte[] bytes)
        {
            ThrowIfDisposed();
            AuthorizeExact(path);
            File.WriteAllBytes(_destination, bytes);
        }

        public void WriteAllText(string path, string contents)
        {
            ThrowIfDisposed();
            AuthorizeExact(path);
            File.WriteAllText(_destination, contents);
        }

        private void AuthorizeExact(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.Equals(_destination, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedPathException(full);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ExportWriter));
            }
        }

        public void Dispose() => _disposed = true;
    }

    // Creates new directories and new files under one root, and nothing else: no member
    // deletes, moves, renames or opens an existing file, so a copy can never replace data.
    public sealed class StagingWriter : IDisposable
    {
        private bool _disposed;

        internal StagingWriter(string root) => Root = root;

        public string Root { get; }

        public void CreateDirectory(string path) => Directory.CreateDirectory(AuthorizeUnderRoot(path));

        // FileMode.CreateNew throws IOException when the path already exists.
        public Stream CreateNewFile(string path)
            => new FileStream(AuthorizeUnderRoot(path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

        private string AuthorizeUnderRoot(string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var full = Path.GetFullPath(path);
            // A colon past the volume names an NTFS alternate data stream of an existing file.
            if (!PathConfinement.IsUnderOrEqual(Root, full) || full.IndexOf(':', Path.GetPathRoot(full)!.Length) >= 0)
            {
                throw new UnauthorizedPathException(full);
            }
            ThrowIfReparsePointBetween(Root, full);
            return full;
        }

        public void Dispose() => _disposed = true;
    }
}
