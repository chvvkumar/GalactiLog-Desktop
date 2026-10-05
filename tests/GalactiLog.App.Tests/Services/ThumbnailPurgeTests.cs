using System.Text.RegularExpressions;
using GalactiLog.App.Services;
using GalactiLog.Core.Imaging;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// The roadmap's Phase 9 row 7 third Verify clause: "thumbnail purge deletes nothing outside the
// cache root". The clause names GalactiLog.Data.Tests, but ThumbnailCache lives in
// GalactiLog.App.Services (spec 4.3) and ThumbnailCacheTests is in App.Tests, exactly as Phase 8's
// row 4 already split the key test and the eviction test across the two projects. Recorded as a
// deviation in the task report.
//
// No frame is ever decoded: the render delegate is a recording stub. Test-only plain file I/O,
// which FileSafetyTest does not scan.
public sealed class ThumbnailPurgeTests : IDisposable
{
    private const long Mb = 1024 * 1024;

    private readonly string _appDataRoot = NewTempDirectory();
    private readonly string _cacheRoot = NewTempDirectory();
    private readonly string _frameRoot = NewTempDirectory();
    private readonly string _siblingRoot = NewTempDirectory();
    private readonly AppWriter _writer;
    private readonly string _framePath;

    private GeneralSettings _general = new();
    private int _renderBytes = 16;
    private Action? _renderGate;

    public ThumbnailPurgeTests()
    {
        // Review finding M6: an explicit temp pointer path, never the constructor default, which is
        // the real %APPDATA%GalactiLogdatapath.json.
        _writer = new AppWriter(
            _appDataRoot, _cacheRoot, dataRootPointerPath: Path.Combine(_appDataRoot, "datapath.json"));
        _framePath = WriteFrame("frame.fits", 64);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _appDataRoot, _cacheRoot, _frameRoot, _siblingRoot })
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leftover temp directory does not fail a test.
            }
        }
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"galactilog-thumbpurge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private ThumbnailCache Create() => new(_writer, () => _general, Render);

    private RenderResult Render(string path, int width, int quality, RenderMode mode, CancellationToken ct)
    {
        _renderGate?.Invoke();
        return new RenderResult(true, null, new byte[_renderBytes], 10, 10, false);
    }

    private string WriteFrame(string name, int bytes)
    {
        var path = Path.Combine(_frameRoot, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    // A cache file of an exact size without writing its bytes, the fixture shape
    // ThumbnailCacheTests uses.
    private string SeedCacheFile(string relativePath, long bytes = 8)
    {
        var path = Path.Combine(_cacheRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(bytes);
        }

        return path;
    }

    private static int FileCount(string root)
        => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length : 0;

    private string ExpectedFrameRelativePath()
    {
        var info = new FileInfo(_framePath);
        var key = ThumbnailKey.For(
            Path.GetFullPath(_framePath),
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
            _general.ThumbnailWidth);
        return $"frames/{key}.jpg";
    }

    // ---- what it deletes -------------------------------------------------------------------

    [Fact]
    public void Purge_Frame_DeletesEveryFrameThumbnail()
    {
        SeedCacheFile("frames/a.jpg");
        SeedCacheFile("frames/b.jpg");
        SeedCacheFile("frames/c.jpg");

        var deleted = Create().Purge(ThumbnailKind.Frame);

        Assert.Equal(3, deleted);
        Assert.Equal(0, FileCount(Path.Combine(_cacheRoot, "frames")));
    }

    [Fact]
    public void Purge_ReturnsTheDeletedCount()
    {
        SeedCacheFile("frames/a.jpg");
        SeedCacheFile("frames/b.jpg");

        Assert.Equal(2, Create().Purge(ThumbnailKind.Frame));
        // Everything is gone, so a second purge deletes nothing and says so.
        Assert.Equal(0, Create().Purge(ThumbnailKind.Frame));
    }

    [Fact]
    public void Purge_Frame_LeavesReferenceAndPreviewFilesAlone()
    {
        SeedCacheFile("frames/a.jpg");
        var preview = SeedCacheFile("previews/p.jpg");
        var reference = SeedCacheFile("reference/r.jpg");

        var deleted = Create().Purge(ThumbnailKind.Frame);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(preview));
        Assert.True(File.Exists(reference));
    }

    [Fact]
    public void Purge_Reference_DeletesOnlyReferenceFiles()
    {
        var frame = SeedCacheFile("frames/a.jpg");
        SeedCacheFile("reference/r.jpg");

        Assert.Equal(1, Create().Purge(ThumbnailKind.Reference));
        Assert.True(File.Exists(frame));
    }

    [Fact]
    public void Purge_LeavesAFileItDidNotWriteAlone()
    {
        // The same rule the eviction sweep applies: this cache writes nothing but .jpg, and a file
        // it did not write is not its to delete.
        SeedCacheFile("frames/a.jpg");
        var foreign = SeedCacheFile("frames/notes.txt");

        Assert.Equal(1, Create().Purge(ThumbnailKind.Frame));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Purge_AFileAlreadyGone_IsNotAnError()
    {
        var doomed = SeedCacheFile("frames/a.jpg");
        SeedCacheFile("frames/b.jpg");
        var cache = Create();

        // Removed between the enumeration and the delete, which is what a concurrent eviction or
        // a user emptying the folder by hand looks like.
        File.Delete(doomed);

        Assert.Equal(1, cache.Purge(ThumbnailKind.Frame));
    }

    [Fact]
    public void Purge_AnUndefinedKind_Throws_AndDeletesNothing()
    {
        // Review minor 1: DirectoryFor's default arm used to map anything that was not Frame or
        // Preview to the reference directory, so a kind nobody defined silently deleted every
        // reference thumbnail. A bulk delete refuses what it does not recognize.
        SeedCacheFile("frames/a.jpg");
        SeedCacheFile("previews/p.jpg");
        SeedCacheFile("reference/r.jpg");
        var cache = Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Purge((ThumbnailKind)7));

        Assert.Equal(3, FileCount(_cacheRoot));
    }

    [Fact]
    public void Purge_Cancelled_KeepsWhatItDeleted_AndStops()
    {
        // Review minor 5: Task.Run checks a token only before the delegate is scheduled, so a
        // purge already walking a large directory has to check it itself.
        for (var i = 0; i < 6; i++)
        {
            SeedCacheFile($"frames/file{i:00}.jpg");
        }

        using var cancellation = new CancellationTokenSource();
        var cache = Create();
        cancellation.Cancel();

        // The check is the first statement of the per-file loop and there is no other check before
        // it, so a token already cancelled when the loop is entered is what proves it is there:
        // six files are enumerated and none of them is deleted.
        Assert.Throws<OperationCanceledException>(() => cache.Purge(ThumbnailKind.Frame, cancellation.Token));

        Assert.Equal(6, FileCount(Path.Combine(_cacheRoot, "frames")));
    }

    [Fact]
    public void Purge_AnEmptyOrMissingDirectory_IsNotAnError()
    {
        Assert.Equal(0, Create().Purge(ThumbnailKind.Frame));
        Assert.Equal(0, Create().Purge(ThumbnailKind.Preview));
        Assert.Equal(0, Create().Purge(ThumbnailKind.Reference));
    }

    // ---- the roadmap's third named assertion ------------------------------------------------

    [Fact]
    public void Purge_DeletesNothingOutsideTheCacheRoot()
    {
        SeedCacheFile("frames/a.jpg");
        var outside = Path.Combine(_siblingRoot, "a.jpg");
        File.WriteAllBytes(outside, new byte[8]);

        var deleted = Create().Purge(ThumbnailKind.Frame);

        // Behavioural: the enumeration never left the cache root.
        Assert.Equal(1, deleted);
        Assert.True(File.Exists(outside));

        // Structural, and the half that does not depend on the enumeration being right: every
        // delete goes through AppWriter, whose path check makes anything outside the app data
        // directory and the cache root unreachable regardless of what a caller asks for.
        Assert.Throws<UnauthorizedPathException>(() => _writer.Delete(outside));
        Assert.Throws<UnauthorizedPathException>(
            () => _writer.Delete(Path.Combine(_siblingRoot, "frames", "a.jpg")));
    }

    [Fact]
    public void Purge_GoesThroughAppWriter()
    {
        // By construction. FileSafetyTest is the backstop over all of src/**; this asserts the one
        // member the roadmap names, so a future edit to Purge fails here with the rule it broke.
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Services", "ThumbnailCache.cs"));
        var purge = Regex.Match(source, @"public int Purge\(ThumbnailKind kind[^)]*\)[\s\S]*?\r?\n    \}\r?\n");
        Assert.True(purge.Success, "Purge(ThumbnailKind, ...) not found in ThumbnailCache.cs");

        Assert.Contains("writer.Delete(", purge.Value, StringComparison.Ordinal);
        Assert.Contains("writer.EnumerateThumbnailFiles(", purge.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", purge.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", purge.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("FileInfo", purge.Value, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found.");
    }

    // ---- the in-process LRU ------------------------------------------------------------------

    [Fact]
    public void Purge_ClearsTheInProcessLruEntriesForThatKind()
    {
        // Observable through the sweep's ordering, which is the only thing the stamps are for. A
        // stamp left behind for a deleted preview would make a file later written at that same
        // relative path sort as recently used, and the sweep would evict a genuinely older
        // unstamped file instead of it.
        _general = _general with { PreviewCacheMb = 100 };
        var cache = Create();

        var relative = cache.EnsurePreview(_framePath, CancellationToken.None)!;
        Assert.Equal(1, cache.Purge(ThumbnailKind.Preview));

        // The same relative path again, older than the other candidate, both unstamped if the
        // purge cleared correctly.
        var reborn = SeedCacheFile(relative, 60 * Mb);
        var other = SeedCacheFile("previews/other.jpg", 60 * Mb);
        File.SetLastWriteTimeUtc(reborn, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(other, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, cache.EvictPreviews());

        Assert.False(File.Exists(reborn), "the stale last-use stamp survived the purge");
        Assert.True(File.Exists(other));
    }

    // ---- the per-key gate --------------------------------------------------------------------

    [Fact]
    public async Task Purge_DoesNotRaceAConcurrentRender()
    {
        // The render delegate is the hook, the shape ThumbnailCacheTests' own sweep-racing-write
        // test uses: it makes the file appear at the key the render is about to write, from inside
        // the window a concurrent purge would occupy, and starts the purge from there. The purge
        // enumerates a file that definitely exists and then has to take the key gate this render
        // is holding.
        var relative = ExpectedFrameRelativePath();
        var cache = Create();
        Task<int>? purge = null;
        var purgeFinishedEarly = false;

        _renderGate = () =>
        {
            SeedCacheFile(relative);
            purge = Task.Run(() => cache.Purge(ThumbnailKind.Frame));

            // Can only fail when the gate is missing: a purge that has not reached the gate yet
            // does not complete either, so this never fails a correct implementation.
            purgeFinishedEarly = purge.Wait(TimeSpan.FromMilliseconds(250));
        };

        var written = await Task.Run(() => cache.EnsureFrame(_framePath, CancellationToken.None));

        Assert.False(purgeFinishedEarly, "the purge did not wait for the in-flight render");
        Assert.Equal(relative, written);

        var deleted = await purge!;
        Assert.Equal(1, deleted);
        Assert.Equal(0, FileCount(Path.Combine(_cacheRoot, "frames")));
    }
}
