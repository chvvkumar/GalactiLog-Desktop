using System;
using System.IO;
using System.Linq;
using System.Text;
using GalactiLog.Core.Io;
using Xunit;

namespace GalactiLog.Core.Tests.Io;

// Spec 17.2's copy-and-verify procedure (Phase 10 Task 9).
//
// Both roots are temp directories created per case, and the destination AppWriter is constructed
// over one of them with an explicit dataRootPointerPath that also lives there. Nothing here reads
// or writes %LOCALAPPDATA%\GalactiLog, %LOCALAPPDATA%\GalactiLogData or %APPDATA%\GalactiLog.
public class AppDataRelocationTests : IDisposable
{
    private readonly string _scratch;
    private readonly string _source;
    private readonly string _destination;

    private static readonly byte[] DatabaseBytes = Encoding.ASCII.GetBytes("SQLite format 3\0 pretend catalogue");
    private static readonly byte[] WalBytes = Encoding.ASCII.GetBytes("write ahead log");
    private static readonly byte[] ShmBytes = Encoding.ASCII.GetBytes("shared memory");
    private static readonly byte[] LogBytes = Encoding.ASCII.GetBytes("2026-09-15 [INF] GalactiLog starting");
    private static readonly byte[] FrameBytes = Encoding.ASCII.GetBytes("frame thumbnail bytes");
    private static readonly byte[] PreviewBytes = Encoding.ASCII.GetBytes("preview thumbnail bytes");
    private static readonly byte[] BinaryBytes = Encoding.ASCII.GetBytes("MZ pretend executable");

    private const string LogRelative = @"logs\galactilog-20260915.log";
    private const string FrameRelative = @"thumbnails\frames\a.jpg";
    private const string PreviewRelative = @"thumbnails\previews\b.jpg";

    public AppDataRelocationTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "GalactiLogRelocationTests_" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_scratch, "source");
        _destination = Path.Combine(_scratch, "destination");
        Directory.CreateDirectory(_destination);
        SeedSource();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }

        GC.SuppressFinalize(this);
    }

    private void SeedSource()
    {
        Write(_source, AppDataRelocation.DatabaseFileName, DatabaseBytes);
        Write(_source, AppDataRelocation.DatabaseFileName + "-wal", WalBytes);
        Write(_source, AppDataRelocation.DatabaseFileName + "-shm", ShmBytes);
        Write(_source, LogRelative, LogBytes);
        Write(_source, FrameRelative, FrameBytes);
        Write(_source, PreviewRelative, PreviewBytes);
        // The Velopack install root's own content, on the adoption path. It must never move.
        Write(_source, @"current\GalactiLog.exe", BinaryBytes);
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private AppWriter Writer(string? root = null)
        => new(
            root ?? _destination,
            dataRootPointerPath: Path.Combine(_scratch, "pointer", "datapath.json"));

    private string Destination(string relative) => Path.Combine(_destination, relative);

    [Fact]
    public void Run_CopiesTheDatabaseTheWalTheLogsAndTheThumbnails()
    {
        var outcome = AppDataRelocation.Run(_source, Writer());

        Assert.True(outcome.Moved, outcome.Failure);
        foreach (var (relative, bytes) in new[]
        {
            (AppDataRelocation.DatabaseFileName, DatabaseBytes),
            (AppDataRelocation.DatabaseFileName + "-wal", WalBytes),
            (LogRelative, LogBytes),
            (FrameRelative, FrameBytes),
            (PreviewRelative, PreviewBytes),
        })
        {
            Assert.True(File.Exists(Destination(relative)), relative);
            Assert.Equal(bytes.Length, new FileInfo(Destination(relative)).Length);
        }
    }

    [Fact]
    public void Run_SkipsTheSharedMemoryFile()
    {
        AppDataRelocation.Run(_source, Writer());

        Assert.False(File.Exists(Destination(AppDataRelocation.DatabaseFileName + "-shm")));
    }

    [Fact]
    public void Run_CopiesNothingOutsideTheRelocatedSet()
    {
        AppDataRelocation.Run(_source, Writer());

        Assert.False(File.Exists(Destination(@"current\GalactiLog.exe")));
        Assert.False(Directory.Exists(Destination("current")));
    }

    [Fact]
    public void Run_LeavesEverySourceFileInPlace()
    {
        var before = Directory
            .EnumerateFiles(_source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => new FileInfo(path).Length, StringComparer.OrdinalIgnoreCase);

        AppDataRelocation.Run(_source, Writer());

        foreach (var (path, length) in before)
        {
            Assert.True(File.Exists(path), path);
            Assert.Equal(length, new FileInfo(path).Length);
        }

        Assert.Equal(
            before.Count,
            Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void Run_ReportsTheFileAndByteCounts()
    {
        var outcome = AppDataRelocation.Run(_source, Writer());

        Assert.Equal(5, outcome.FilesCopied);
        Assert.Equal(
            DatabaseBytes.Length + WalBytes.Length + LogBytes.Length + FrameBytes.Length + PreviewBytes.Length,
            outcome.BytesCopied);
        Assert.Empty(outcome.Skipped);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public void Run_SameRoot_IsRefused()
    {
        var outcome = AppDataRelocation.Run(_source, Writer(_source));

        Assert.False(outcome.Moved);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Failure));
        Assert.False(Directory.Exists(Destination("logs")));
    }

    [Fact]
    public void Run_DestinationInsideTheSource_IsRefused()
    {
        var inside = Path.Combine(_source, "nested");
        Directory.CreateDirectory(inside);

        var outcome = AppDataRelocation.Run(_source, Writer(inside));

        Assert.False(outcome.Moved);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Failure));
        Assert.False(File.Exists(Path.Combine(inside, AppDataRelocation.DatabaseFileName)));
    }

    [Fact]
    public void Run_SourceInsideTheDestination_IsRefused()
    {
        var outer = Path.Combine(_scratch, "outer");
        var inner = Path.Combine(outer, "inner");
        Directory.CreateDirectory(inner);
        Write(inner, AppDataRelocation.DatabaseFileName, DatabaseBytes);

        var outcome = AppDataRelocation.Run(inner, Writer(outer));

        Assert.False(outcome.Moved);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Failure));
        Assert.False(File.Exists(Path.Combine(outer, AppDataRelocation.DatabaseFileName)));
    }

    [Fact]
    public void Run_DestinationHoldingADatabase_IsRefused()
    {
        Write(_destination, AppDataRelocation.DatabaseFileName, Encoding.ASCII.GetBytes("another library"));

        var outcome = AppDataRelocation.Run(_source, Writer());

        Assert.False(outcome.Moved);
        Assert.Contains(AppDataRelocation.DatabaseFileName, outcome.Failure!, StringComparison.Ordinal);
        Assert.Equal("another library".Length, new FileInfo(Destination(AppDataRelocation.DatabaseFileName)).Length);
    }

    [Fact]
    public void Run_MissingSourceRoot_IsRefused()
    {
        var missing = Path.Combine(_scratch, "not-there");

        var outcome = AppDataRelocation.Run(missing, Writer());

        Assert.False(outcome.Moved);
        Assert.Contains(missing, outcome.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Run_ExistingDestinationFile_IsSkippedAndReported()
    {
        // The same length as the source's, so the verification pass is satisfied and the case is
        // about the skip alone. Different bytes, so "not overwritten" is observable.
        var standIn = new byte[LogBytes.Length];
        Array.Fill(standIn, (byte)'x');
        Write(_destination, LogRelative, standIn);

        var outcome = AppDataRelocation.Run(_source, Writer());

        Assert.True(outcome.Moved, outcome.Failure);
        Assert.Contains(LogRelative, outcome.Skipped);
        Assert.Equal(standIn, File.ReadAllBytes(Destination(LogRelative)));
        Assert.Equal(4, outcome.FilesCopied);
    }

    [Fact]
    public void Run_ExistingDestinationDatabase_NeverProducesAMovedOutcome()
    {
        // Step 5's refusal: the destination holds a database before anything is copied.
        Write(_destination, AppDataRelocation.DatabaseFileName, DatabaseBytes);
        var refusedUpFront = AppDataRelocation.Run(_source, Writer());
        Assert.False(refusedUpFront.Moved);

        // And no second arrangement of a destination that already holds a database produces a
        // moved outcome either. Step 5 catches this one too, which is why step 9's post-copy
        // check never fires in practice; it stays as the second guard on the same rule.
        var second = Path.Combine(_scratch, "second");
        Directory.CreateDirectory(second);
        Write(second, AppDataRelocation.DatabaseFileName, DatabaseBytes);
        var refusedAfterCopy = AppDataRelocation.Run(_source, Writer(second));
        Assert.False(refusedAfterCopy.Moved);
        Assert.False(string.IsNullOrWhiteSpace(refusedAfterCopy.Failure));
    }

    // Review finding I3: another process holding the database means another GalactiLog is running,
    // and copying a live database with its write-ahead log produces a copy the length check cannot
    // tell apart from a good one.
    [Fact]
    public void Run_WhileTheSourceDatabaseIsHeldOpen_IsRefused()
    {
        // The share mode SQLite itself uses, so File.Copy would have succeeded.
        using var held = new FileStream(
            Path.Combine(_source, AppDataRelocation.DatabaseFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        var outcome = AppDataRelocation.Run(_source, Writer());

        Assert.False(outcome.Moved);
        Assert.Equal(AppDataRelocation.AlreadyRunningFailure, outcome.Failure);
        Assert.False(File.Exists(Destination(AppDataRelocation.DatabaseFileName)));
        Assert.False(File.Exists(Destination(AppDataRelocation.MarkerFileName)));
    }

    [Fact]
    public void Run_WhileTheSourceDatabaseIsHeldOpen_LeavesTheSourceExactlyAsItWas()
    {
        var before = Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).ToArray();

        using (var held = new FileStream(
            Path.Combine(_source, AppDataRelocation.DatabaseFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite))
        {
            Assert.False(AppDataRelocation.Run(_source, Writer()).Moved);
        }

        Assert.Equal(before, Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).ToArray());
    }

    // Review finding I4: a failure after the database was copied must not make every retry read as
    // "that folder is another library". Nothing is deleted on either pass.
    [Fact]
    public void Run_AfterAFailedCopy_TheRetryNamesTheIncompleteCopy()
    {
        // A shorter destination file fails verification, and it fails it after galactilog.db has
        // already been copied, which is the partial state the finding is about.
        Write(_destination, FrameRelative, Encoding.ASCII.GetBytes("short"));
        var first = AppDataRelocation.Run(_source, Writer());
        Assert.False(first.Moved);
        Assert.True(File.Exists(Destination(AppDataRelocation.DatabaseFileName)));

        var retry = AppDataRelocation.Run(_source, Writer());

        Assert.False(retry.Moved);
        Assert.Equal(
            string.Format(AppDataRelocation.IncompleteCopyFormat, Path.GetFullPath(_destination)),
            retry.Failure);
        Assert.Contains("Nothing was deleted", retry.Failure!, StringComparison.Ordinal);
        Assert.Contains(_destination, retry.Failure!, StringComparison.OrdinalIgnoreCase);
        // Both passes left every source file where it was.
        Assert.True(File.Exists(Path.Combine(_source, AppDataRelocation.DatabaseFileName)));
    }

    [Fact]
    public void Run_ADestinationHoldingAnotherLibrary_IsStillNamedAsSuch()
    {
        Write(_destination, AppDataRelocation.DatabaseFileName, Encoding.ASCII.GetBytes("another library"));

        var outcome = AppDataRelocation.Run(_source, Writer());

        Assert.False(outcome.Moved);
        Assert.Equal(
            string.Format(
                AppDataRelocation.OccupiedDestinationFormat,
                Path.GetFullPath(_destination),
                AppDataRelocation.DatabaseFileName),
            outcome.Failure);
    }

    [Fact]
    public void Run_ASuccessfulMove_LeavesTheMarkerComplete()
    {
        Assert.True(AppDataRelocation.Run(_source, Writer()).Moved);

        var marker = File.ReadAllText(Destination(AppDataRelocation.MarkerFileName));

        Assert.StartsWith(AppDataRelocation.MarkerComplete, marker, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(_source), marker, StringComparison.OrdinalIgnoreCase);
    }

    // Review finding M2, the structural half: the rule holds on every machine, including one where
    // a test may not create a reparse point at all.
    [Fact]
    public void CopyEnumeration_SkipsReparsePoints()
    {
        var options = AppDataRelocation.CopyEnumeration();

        Assert.True(options.RecurseSubdirectories);
        Assert.False(options.IgnoreInaccessible);
        Assert.Equal(FileAttributes.ReparsePoint, options.AttributesToSkip & FileAttributes.ReparsePoint);
    }

    // Review finding M2, the behavioural half: a link under thumbnails\ must not pull files from
    // outside the data root into the new one. Returns without asserting on a machine where this
    // process may not create a directory symlink, which needs elevation or developer mode; the
    // case above covers the rule there.
    [Fact]
    public void Run_AReparsePointUnderARelocatedDirectory_IsNotFollowed()
    {
        var outside = Path.Combine(_scratch, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "secret.jpg"), Encoding.ASCII.GetBytes("outside the root"));

        var link = Path.Combine(_source, "thumbnails", "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No symlink privilege on this machine; the rule is still enforced in the code.
            return;
        }

        var outcome = AppDataRelocation.Run(_source, Writer());

        Assert.True(outcome.Moved, outcome.Failure);
        Assert.False(File.Exists(Destination(@"thumbnails\linked\secret.jpg")));
        Assert.False(Directory.Exists(Destination(@"thumbnails\linked")));
        // The link itself is untouched, like everything else under the source.
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void Run_AShortenedCopy_FailsVerification()
    {
        // A destination file shorter than its source: the copy refuses to overwrite it, and the
        // length check is what catches that the new root does not hold the data set.
        Write(_destination, FrameRelative, Encoding.ASCII.GetBytes("short"));

        var outcome = AppDataRelocation.Run(_source, Writer());

        Assert.False(outcome.Moved);
        Assert.Contains(FrameRelative, outcome.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, new FileInfo(Destination(FrameRelative)).Length);
    }
}
