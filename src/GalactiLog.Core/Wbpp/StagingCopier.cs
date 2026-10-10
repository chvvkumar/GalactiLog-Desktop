using System.Text.RegularExpressions;
using GalactiLog.Core.Io;

namespace GalactiLog.Core.Wbpp;

/// <summary>The copier's only contact with the disk. The source side reads through
/// <see cref="UserFiles"/>; the destination side writes through one
/// <see cref="AppWriter.StagingWriter"/>. A test passes its own delegates.</summary>
/// <param name="DestinationLength">Null when nothing is at the path, -1 when a folder is.</param>
public sealed record StagingIo(
    Func<string, Stream> OpenSource,
    Func<string, IEnumerable<FileSystemInfo>> Enumerate,
    Func<string, long?> DestinationLength,
    Action<string> CreateDirectory,
    Func<string, Stream> CreateDestination)
{
    public static StagingIo For(AppWriter.StagingWriter writer) => new(
        UserFiles.OpenRead,
        UserFiles.EnumerateFileSystemEntries,
        path =>
        {
            if (UserFiles.DirectoryExists(path))
            {
                return -1;
            }
            var info = UserFiles.GetFileInfo(path);
            return info.Exists ? info.Length : null;
        },
        writer.CreateDirectory,
        writer.CreateNewFile);
}

/// <summary>What to copy: every operation's source tree goes to
/// <c>StagingRoot\EntryName\&lt;relative path&gt;</c>, minus folders and files whose name matches
/// <paramref name="Exclusions"/> and minus each operation's excluded relative paths.</summary>
public sealed record StagingCopyRequest(
    IReadOnlyList<CopyOperation> Operations,
    string StagingRoot,
    IReadOnlyList<string> Exclusions);

public enum StagingSkipReason { ExistsSameSize, ExistsDifferentSize, ReparsePoint }

/// <summary>A destination file left alone (its destination path) or a source reparse-point
/// directory not followed (its source path).</summary>
public sealed record StagingSkip(string Path, StagingSkipReason Reason);

public sealed record StagingFailure(string SourcePath, string Message);

/// <param name="BytesWritten">Bytes this run wrote, so a speed figure leaves out skipped files.</param>
public sealed record StagingProgress(
    int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, long BytesWritten, string CurrentFile);

public enum StagingOutcome { Completed, Cancelled, Aborted }

public sealed record StagingCopyResult(
    StagingOutcome Outcome,
    int Copied,
    long BytesCopied,
    IReadOnlyList<StagingSkip> Skipped,
    IReadOnlyList<StagingFailure> Failed,
    IReadOnlyList<string> PartialPaths,
    string? AbortReason);

/// <summary>Copies source trees into the staging root as new files only, skipping any destination
/// that exists. A full or refusing destination aborts the run, other errors fail one file. A cancel
/// starts no new file and lets files in flight finish; an abort stops them after their current
/// buffer and lists them as partial.</summary>
public sealed class StagingCopier(StagingIo io)
{
    // ponytail: fixed pool of 4; make it a parameter if a measured link wants more.
    public const int PoolSize = 4;
    public const int BufferBytes = 1 << 20;

    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    private sealed record WorkItem(string Source, string Destination, long Length);

    public async Task<StagingCopyResult> RunAsync(
        StagingCopyRequest request, IProgress<StagingProgress>? progress, CancellationToken ct)
    {
        var isExcluded = PathComponentExcluder(request.Exclusions);
        var skipped = new List<StagingSkip>();
        var failed = new List<StagingFailure>();
        var partial = new List<string>();
        var directories = new List<string>();
        var work = new List<WorkItem>();
        var filesTotal = 0;
        var bytesTotal = 0L;
        var filesDone = 0;
        var bytesDone = 0L;

        var unsafeEntry = request.Operations.FirstOrDefault(o => IsUnsafeEntryName(o.EntryName));
        if (unsafeEntry is not null)
        {
            return new StagingCopyResult(
                StagingOutcome.Aborted, 0, 0, skipped, failed, partial,
                $"The export folder name '{unsafeEntry.EntryName}' is not one plain folder name, so nothing was copied.");
        }

        // Pass 1: enumerate, total, and decide skips. Nothing is written here.
        foreach (var operation in request.Operations)
        {
            var excludedFiles = new HashSet<string>(operation.ExcludedRelativePaths, StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<(string Source, string Relative)>();
            pending.Push((operation.SourcePath, ""));
            while (pending.Count > 0)
            {
                if (ct.IsCancellationRequested)
                {
                    return new StagingCopyResult(StagingOutcome.Cancelled, 0, 0, skipped, failed, partial, null);
                }

                var (directory, relativeDirectory) = pending.Pop();
                directories.Add(Path.Combine(request.StagingRoot, operation.EntryName, relativeDirectory));
                List<FileSystemInfo> entries;
                try
                {
                    entries = io.Enumerate(directory).ToList();
                }
                catch (Exception ex)
                {
                    failed.Add(new StagingFailure(directory, ex.Message));
                    continue;
                }

                foreach (var entry in entries)
                {
                    var relative = Path.Combine(relativeDirectory, entry.Name);
                    if (entry is DirectoryInfo)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skipped.Add(new StagingSkip(entry.FullName, StagingSkipReason.ReparsePoint));
                        }
                        else if (!isExcluded(entry.Name))
                        {
                            pending.Push((entry.FullName, relative));
                        }
                        continue;
                    }

                    if (entry is not FileInfo file || isExcluded(entry.Name) || excludedFiles.Contains(relative))
                    {
                        continue;
                    }

                    filesTotal++;
                    bytesTotal += file.Length;
                    var destination = Path.Combine(request.StagingRoot, operation.EntryName, relative);
                    long? existing;
                    try
                    {
                        existing = io.DestinationLength(destination);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new StagingFailure(file.FullName, ex.Message));
                        filesDone++;
                        continue;
                    }

                    if (existing is null)
                    {
                        work.Add(new WorkItem(file.FullName, destination, file.Length));
                    }
                    else if (existing < 0)
                    {
                        failed.Add(new StagingFailure(file.FullName, $"A folder already exists at '{destination}'."));
                        filesDone++;
                    }
                    else
                    {
                        skipped.Add(new StagingSkip(
                            destination,
                            existing == file.Length ? StagingSkipReason.ExistsSameSize : StagingSkipReason.ExistsDifferentSize));
                        filesDone++;
                        bytesDone += file.Length;
                    }
                }
            }
        }

        // Pass 2: directories, then files through the pool.
        var gate = new object();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? abortReason = null;
        var copied = 0;
        var bytesCopied = 0L;
        var bytesWritten = 0L;

        void Abort(Exception ex)
        {
            lock (gate)
            {
                abortReason ??= AbortText(ex);
                stop.Cancel();
            }
        }

        void Fail(string path, Exception ex)
        {
            lock (gate)
            {
                failed.Add(new StagingFailure(path, ex.Message));
            }
        }

        foreach (var directory in directories)
        {
            if (stop.IsCancellationRequested)
            {
                break;
            }
            try
            {
                io.CreateDirectory(directory);
            }
            catch (Exception ex) when (AbortsRun(ex) || ex is not IOException)
            {
                Abort(ex);
            }
            catch (IOException ex)
            {
                Fail(directory, ex);
            }
        }

        void Finish(WorkItem item, bool completed, long written)
        {
            lock (gate)
            {
                filesDone++;
                bytesDone += written;
                bytesWritten += written;
                if (completed)
                {
                    copied++;
                    bytesCopied += written;
                }
                progress?.Report(new StagingProgress(filesDone, filesTotal, bytesDone, bytesTotal, bytesWritten, item.Source));
            }
        }

        async ValueTask CopyOne(WorkItem item, CancellationToken token)
        {
            Stream? source = null;
            Stream? destination = null;

            // Starting a file is serialized with Abort, so no file starts after an abort is recorded.
            lock (gate)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    source = io.OpenSource(item.Source);
                    destination = io.CreateDestination(item.Destination);
                }
                catch (Exception ex)
                {
                    source?.Dispose();
                    if (source is not null && AbortsRun(ex))
                    {
                        abortReason ??= AbortText(ex);
                        stop.Cancel();
                        return;
                    }
                    failed.Add(new StagingFailure(item.Source, ex.Message));
                    source = null;
                }
            }

            if (source is null || destination is null)
            {
                Finish(item, completed: false, written: 0);
                return;
            }

            var written = 0L;
            var completed = false;
            Exception? sourceError = null;
            Exception? destinationError = null;
            var buffer = new byte[BufferBytes];
            // A cancel lets the file finish; only an abort (full or refusing destination) cuts it short.
            while (!(Volatile.Read(ref abortReason) is not null && written < item.Length))
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    sourceError = ex;
                    break;
                }
                if (read == 0)
                {
                    completed = true;
                    break;
                }
                try
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    destinationError = ex;
                    break;
                }
                written += read;
            }

            try
            {
                await destination.DisposeAsync();
            }
            catch (Exception ex)
            {
                destinationError ??= ex;
                completed = false;
            }
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                sourceError ??= ex;
            }

            if (sourceError is not null)
            {
                completed = false;
                Fail(item.Source, sourceError);
            }
            else if (destinationError is not null)
            {
                completed = false;
                if (AbortsRun(destinationError))
                {
                    Abort(destinationError);
                }
                else
                {
                    Fail(item.Source, destinationError);
                }
            }

            if (!completed)
            {
                lock (gate)
                {
                    partial.Add(item.Destination);
                }
            }
            Finish(item, completed, written);
        }

        try
        {
            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = PoolSize, CancellationToken = stop.Token },
                CopyOne);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }

        var outcome = abortReason is not null ? StagingOutcome.Aborted
            : ct.IsCancellationRequested ? StagingOutcome.Cancelled
            : StagingOutcome.Completed;
        return new StagingCopyResult(outcome, copied, bytesCopied, skipped, failed, partial, abortReason);
    }

    // Destination-side only: a full disk, a refused access, or the staging writer refusing a path.
    private static bool AbortsRun(Exception ex)
        => ex is UnauthorizedAccessException or UnauthorizedPathException
            || (ex is IOException && ex.HResult is ErrorDiskFull or ErrorHandleDiskFull);

    private static string AbortText(Exception ex) => ex is UnauthorizedPathException refused
        ? $"The staging folder refused '{refused.Path}': it lies outside the staging folder or behind a junction or link. Pick another staging folder."
        : ex.Message;

    /// <summary>The script generator's entry name rule: one folder name, never empty, rooted, holding
    /// a separator or a colon, or a relative-path component.</summary>
    internal static bool IsUnsafeEntryName(string name)
        => name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0 || name is "." or "..";

    /// <summary>A path-component matcher mirroring the web's <c>makeExcluder</c>, applied to every
    /// folder and file name as the PowerShell script's predicate is: each pattern matches a whole
    /// component, <c>*</c> matches any run of characters, ignore case like <c>-match</c>.</summary>
    internal static Func<string, bool> PathComponentExcluder(IReadOnlyList<string> patterns)
    {
        var alternatives = patterns
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => Regex.Escape(p).Replace(@"\*", ".*"))
            .ToList();
        if (alternatives.Count == 0)
        {
            return _ => false;
        }
        var regex = new Regex(
            "^(?:" + string.Join("|", alternatives) + ")$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.IsMatch;
    }
}
