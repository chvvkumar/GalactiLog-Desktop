using GalactiLog.App.Services;
using GalactiLog.Core.Imaging;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// The roadmap's Phase 8 row 4 second Verify clause: eviction removes the least recently used
// previews down to the bound and never touches frames or reference.
//
// No frame is ever decoded here: the render delegate is a recording stub, so these tests assert
// the cache's own behaviour (path shapes, keys, hit and miss, relocation, eviction, file safety)
// and never SkiaSharp's. Test-only plain file I/O, which FileSafetyTest does not scan.
//
// Eviction fixtures are sized in whole megabytes because the bound has a 100 MB floor
// (questions.md Q8), and they are created with FileStream.SetLength, an NTFS metadata operation,
// rather than by writing the bytes.
public sealed class ThumbnailCacheTests : IDisposable
{
    private const long Mb = 1024 * 1024;

    private readonly string _appDataRoot = NewTempDirectory();
    private readonly string _cacheRoot = NewTempDirectory();
    private readonly string _frameRoot = NewTempDirectory();
    private readonly string _siblingRoot = NewTempDirectory();
    private readonly AppWriter _writer;
    private readonly List<(string Path, int Width, int Quality, RenderMode Mode)> _renders = [];
    private readonly string _framePath;

    private GeneralSettings _general = new();
    private int _renderBytes = 16;
    private bool _renderSkips;
    private bool _renderCancels;
    private Action? _renderGate;

    public ThumbnailCacheTests()
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
        var path = Path.Combine(Path.GetTempPath(), $"galactilog-thumbcache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private ThumbnailCache Create() => new(_writer, () => _general, Render);

    private RenderResult Render(string path, int width, int quality, RenderMode mode, CancellationToken ct)
    {
        lock (_renders)
        {
            _renders.Add((path, width, quality, mode));
        }
        _renderGate?.Invoke();
        if (_renderCancels)
        {
            throw new OperationCanceledException();
        }
        return _renderSkips
            ? new RenderResult(false, "unsupported file extension: .xyz", null, 0, 0, false)
            : new RenderResult(true, null, new byte[_renderBytes], 10, 10, false);
    }

    private string WriteFrame(string name, int bytes)
    {
        var path = Path.Combine(_frameRoot, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    // A cache file of an exact size, without writing its bytes: SetLength moves end-of-file and
    // NTFS zero-fills lazily, so a 40 MB fixture costs a metadata update.
    private string SeedCacheFile(string relativePath, long bytes)
    {
        var path = Path.Combine(_cacheRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(bytes);
        }
        return path;
    }

    private static long ExpectedMtime(string path)
        => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeSeconds();

    private string ExpectedKey(string path, int width)
        => ThumbnailKey.For(Path.GetFullPath(path), new FileInfo(path).Length, ExpectedMtime(path), width);

    private static int FileCount(string root)
        => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length : 0;

    // Path shapes and kinds (spec 11.3's table).

    [Fact]
    public void EnsureFrame_WritesUnderFramesWithTheKeyAsTheFilename()
    {
        var cache = Create();

        var relative = cache.EnsureFrame(_framePath, CancellationToken.None);

        Assert.Equal($"frames/{ExpectedKey(_framePath, _general.ThumbnailWidth)}.jpg", relative);
        Assert.True(File.Exists(Path.Combine(_cacheRoot, "frames", $"{ExpectedKey(_framePath, _general.ThumbnailWidth)}.jpg")));
    }

    [Fact]
    public void EnsurePreview_WritesUnderPreviews()
    {
        var cache = Create();

        var relative = cache.EnsurePreview(_framePath, CancellationToken.None);

        Assert.Equal($"previews/{ExpectedKey(_framePath, _general.PreviewResolution)}.jpg", relative);
    }

    [Fact]
    public void EnsureReference_WritesUnderReferenceNamedByTheTargetId()
    {
        var cache = Create();
        var targetId = Guid.NewGuid();

        var relative = cache.EnsureReference(targetId, _framePath, force: false, CancellationToken.None);

        Assert.Equal($"reference/{targetId:D}.jpg", relative);
        Assert.True(File.Exists(Path.Combine(_cacheRoot, "reference", $"{targetId:D}.jpg")));
    }

    [Fact]
    public void EnsureFrame_ReturnsAPathRelativeToTheCacheRoot()
    {
        var cache = Create();

        var relative = cache.EnsureFrame(_framePath, CancellationToken.None)!;

        Assert.False(Path.IsPathRooted(relative));
        Assert.DoesNotContain('\\', relative);
        Assert.StartsWith("frames/", relative, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureFrame_UsesGeneralThumbnailWidth()
    {
        _general = _general with { ThumbnailWidth = 640 };
        var cache = Create();

        cache.EnsureFrame(_framePath, CancellationToken.None);

        Assert.Equal(640, Assert.Single(_renders).Width);
    }

    [Fact]
    public void EnsurePreview_UsesGeneralPreviewResolution()
    {
        _general = _general with { PreviewResolution = 1600 };
        var cache = Create();

        cache.EnsurePreview(_framePath, CancellationToken.None);

        Assert.Equal(1600, Assert.Single(_renders).Width);
    }

    [Fact]
    public void EnsurePreview_ResolutionZero_PassesZeroToTheRenderer()
    {
        _general = _general with { PreviewResolution = 0 };
        var cache = Create();

        cache.EnsurePreview(_framePath, CancellationToken.None);

        Assert.Equal(0, Assert.Single(_renders).Width);
    }

    [Fact]
    public void EnsureReference_AlwaysUsesWidth800()
    {
        _general = _general with { ThumbnailWidth = 320 };
        var cache = Create();

        cache.EnsureReference(Guid.NewGuid(), _framePath, force: false, CancellationToken.None);

        Assert.Equal(ThumbnailCache.ReferenceWidth, Assert.Single(_renders).Width);
        Assert.Equal(800, ThumbnailCache.ReferenceWidth);
    }

    [Fact]
    public void EnsureFrame_UsesQuality85()
    {
        var cache = Create();

        cache.EnsureFrame(_framePath, CancellationToken.None);

        Assert.Equal(85, Assert.Single(_renders).Quality);
    }

    [Fact]
    public void EnsurePreview_UsesQuality90()
    {
        var cache = Create();

        cache.EnsurePreview(_framePath, CancellationToken.None);

        Assert.Equal(90, Assert.Single(_renders).Quality);
    }

    [Fact]
    public void EnsureReference_UsesQuality85()
    {
        var cache = Create();

        cache.EnsureReference(Guid.NewGuid(), _framePath, force: false, CancellationToken.None);

        Assert.Equal(85, Assert.Single(_renders).Quality);
    }

    [Fact]
    public void EnsurePreview_UsesPreviewRenderMode()
    {
        var cache = Create();

        cache.EnsureFrame(_framePath, CancellationToken.None);
        cache.EnsurePreview(_framePath, CancellationToken.None);

        Assert.Equal(RenderMode.Thumbnail, _renders[0].Mode);
        Assert.Equal(RenderMode.Preview, _renders[1].Mode);
    }

    // Hit and miss.

    [Fact]
    public void EnsureFrame_SecondCall_DoesNotRenderAgain()
    {
        var cache = Create();

        var first = cache.EnsureFrame(_framePath, CancellationToken.None);
        var second = cache.EnsureFrame(_framePath, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Single(_renders);
    }

    [Fact]
    public void EnsureFrame_FileChangedOnDisk_RendersAgainUnderANewKey()
    {
        var cache = Create();
        var original = cache.EnsureFrame(_framePath, CancellationToken.None);

        File.WriteAllBytes(_framePath, new byte[128]);
        var afterSizeChange = cache.EnsureFrame(_framePath, CancellationToken.None);

        Assert.NotEqual(original, afterSizeChange);

        File.SetLastWriteTimeUtc(_framePath, File.GetLastWriteTimeUtc(_framePath).AddHours(1));
        var afterMtimeChange = cache.EnsureFrame(_framePath, CancellationToken.None);

        Assert.NotEqual(afterSizeChange, afterMtimeChange);
        Assert.Equal(3, _renders.Count);
    }

    [Fact]
    public void EnsureFrame_ThumbnailWidthChanged_RendersAgainUnderANewKey()
    {
        var cache = Create();
        var atDefault = cache.EnsureFrame(_framePath, CancellationToken.None);

        _general = _general with { ThumbnailWidth = 400 };
        var atNewWidth = cache.EnsureFrame(_framePath, CancellationToken.None);

        Assert.NotEqual(atDefault, atNewWidth);
        Assert.Equal(2, _renders.Count);
    }

    [Fact]
    public void EnsureFrame_MissingFrameFile_ReturnsNullAndDoesNotRender()
    {
        var cache = Create();

        var relative = cache.EnsureFrame(Path.Combine(_frameRoot, "absent.fits"), CancellationToken.None);

        Assert.Null(relative);
        Assert.Empty(_renders);
    }

    [Fact]
    public void EnsureFrame_RendererSkips_ReturnsNullAndWritesNothing()
    {
        _renderSkips = true;
        var cache = Create();

        var relative = cache.EnsureFrame(_framePath, CancellationToken.None);

        Assert.Null(relative);
        Assert.Equal(0, FileCount(_cacheRoot));
    }

    [Fact]
    public void EnsureFrame_RendererThrowsOperationCancelled_Propagates()
    {
        _renderCancels = true;
        var cache = Create();

        Assert.Throws<OperationCanceledException>(() => cache.EnsureFrame(_framePath, CancellationToken.None));
    }

    // Relocation (spec 11.3: changing the root moves nothing).

    [Fact]
    public void Relocation_DoesNotMoveOrDeleteExistingFiles()
    {
        var cache = Create();
        var relative = cache.EnsureFrame(_framePath, CancellationToken.None)!;
        var oldAbsolute = Path.Combine(_cacheRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var bytesBefore = File.ReadAllBytes(oldAbsolute);

        var newRoot = NewTempDirectory();
        try
        {
            _writer.ThumbnailCacheRoot = newRoot;
            cache.EnsureFrame(_framePath, CancellationToken.None);

            Assert.True(File.Exists(oldAbsolute));
            Assert.Equal(bytesBefore, File.ReadAllBytes(oldAbsolute));
        }
        finally
        {
            _writer.ThumbnailCacheRoot = _cacheRoot;
            Directory.Delete(newRoot, recursive: true);
        }
    }

    [Fact]
    public void Relocation_NewRequestsPopulateTheNewRoot()
    {
        var cache = Create();
        cache.EnsureFrame(_framePath, CancellationToken.None);

        var newRoot = NewTempDirectory();
        try
        {
            _writer.ThumbnailCacheRoot = newRoot;
            var relative = cache.EnsureFrame(_framePath, CancellationToken.None)!;

            Assert.True(File.Exists(Path.Combine(newRoot, relative.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(2, _renders.Count);
        }
        finally
        {
            _writer.ThumbnailCacheRoot = _cacheRoot;
            Directory.Delete(newRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveExisting_FileUnderTheOldRoot_ReturnsNull()
    {
        var cache = Create();
        var relative = cache.EnsureFrame(_framePath, CancellationToken.None)!;

        var newRoot = NewTempDirectory();
        try
        {
            _writer.ThumbnailCacheRoot = newRoot;

            Assert.Null(cache.ResolveExisting(relative));
        }
        finally
        {
            _writer.ThumbnailCacheRoot = _cacheRoot;
            Directory.Delete(newRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveExisting_ExistingFile_ReturnsAnAbsolutePath()
    {
        var cache = Create();
        var relative = cache.EnsureFrame(_framePath, CancellationToken.None)!;

        var absolute = cache.ResolveExisting(relative)!;

        Assert.True(Path.IsPathRooted(absolute));
        Assert.True(File.Exists(absolute));
        Assert.StartsWith(_cacheRoot, absolute, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveExisting_PathEscapingTheCacheRoot_ReturnsNull()
    {
        var cache = Create();

        Assert.Null(cache.ResolveExisting(Path.Combine("..", "escaped.jpg")));
    }

    // Eviction (spec 11.3, questions.md Q7 and Q8).

    [Fact]
    public void EvictPreviews_OverTheBound_RemovesTheLeastRecentlyUsedFirst()
    {
        _general = _general with { PreviewCacheMb = 100 };
        var usedSecond = SeedCacheFile("previews/a.jpg", 40 * Mb);
        var usedLast = SeedCacheFile("previews/b.jpg", 40 * Mb);
        var usedFirst = SeedCacheFile("previews/c.jpg", 40 * Mb);
        var cache = Create();
        // Use order, not write order and not name order: c, then a, then b. c is therefore the
        // least recently used even though it was written last and sorts last by name.
        cache.ResolveExisting("previews/c.jpg");
        cache.ResolveExisting("previews/a.jpg");
        cache.ResolveExisting("previews/b.jpg");

        var deleted = cache.EvictPreviews();

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(usedFirst));
        Assert.True(File.Exists(usedSecond));
        Assert.True(File.Exists(usedLast));
    }

    [Fact]
    public void EvictPreviews_EvictsDownToTheBoundAndNoFurther()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 40 * Mb);
        SeedCacheFile("previews/b.jpg", 40 * Mb);
        SeedCacheFile("previews/c.jpg", 40 * Mb);
        var cache = Create();

        var deleted = cache.EvictPreviews();

        Assert.Equal(1, deleted);
        Assert.Equal(2, FileCount(Path.Combine(_cacheRoot, "previews")));
    }

    [Fact]
    public void EvictPreviews_NeverTouchesFrames()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 60 * Mb);
        SeedCacheFile("previews/b.jpg", 60 * Mb);
        var frames = new[]
        {
            SeedCacheFile("frames/one.jpg", 60 * Mb),
            SeedCacheFile("frames/two.jpg", 60 * Mb),
            SeedCacheFile("frames/three.jpg", 60 * Mb),
        };
        var cache = Create();

        cache.EvictPreviews();

        Assert.All(frames, path => Assert.True(File.Exists(path), $"{path} was deleted"));
    }

    [Fact]
    public void EvictPreviews_NeverTouchesReference()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 60 * Mb);
        SeedCacheFile("previews/b.jpg", 60 * Mb);
        var reference = new[]
        {
            SeedCacheFile($"reference/{Guid.NewGuid():D}.jpg", 60 * Mb),
            SeedCacheFile($"reference/{Guid.NewGuid():D}.jpg", 60 * Mb),
        };
        var cache = Create();

        cache.EvictPreviews();

        Assert.All(reference, path => Assert.True(File.Exists(path), $"{path} was deleted"));
    }

    [Fact]
    public void EvictPreviews_UnderTheBound_DeletesNothing()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 1 * Mb);
        SeedCacheFile("previews/b.jpg", 1 * Mb);
        var cache = Create();

        Assert.Equal(0, cache.EvictPreviews());
        Assert.Equal(2, FileCount(Path.Combine(_cacheRoot, "previews")));
    }

    [Fact]
    public void EvictPreviews_EmptyCache_ReturnsZero()
    {
        var cache = Create();

        Assert.Equal(0, cache.EvictPreviews());
    }

    [Fact]
    public void EvictPreviews_RunsAfterEachPreviewGeneration()
    {
        _general = _general with { PreviewCacheMb = 100 };
        var oldest = SeedCacheFile("previews/a.jpg", 40 * Mb);
        SeedCacheFile("previews/b.jpg", 40 * Mb);
        SeedCacheFile("previews/c.jpg", 40 * Mb);
        var cache = Create();
        cache.ResolveExisting("previews/a.jpg");
        cache.ResolveExisting("previews/b.jpg");
        cache.ResolveExisting("previews/c.jpg");

        // No separate EvictPreviews call: the generation itself has to trigger the sweep.
        var relative = cache.EnsurePreview(_framePath, CancellationToken.None)!;

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(Path.Combine(_cacheRoot, relative.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void EvictPreviews_DoesNotRunAfterAFrameOrReferenceGeneration()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 40 * Mb);
        SeedCacheFile("previews/b.jpg", 40 * Mb);
        SeedCacheFile("previews/c.jpg", 40 * Mb);
        var cache = Create();

        cache.EnsureFrame(_framePath, CancellationToken.None);
        cache.EnsureReference(Guid.NewGuid(), _framePath, force: false, CancellationToken.None);

        Assert.Equal(3, FileCount(Path.Combine(_cacheRoot, "previews")));
    }

    [Fact]
    public void EvictPreviews_NoProgress_StopsInsteadOfLooping()
    {
        _general = _general with { PreviewCacheMb = 100 };
        var paths = new[]
        {
            SeedCacheFile("previews/a.jpg", 40 * Mb),
            SeedCacheFile("previews/b.jpg", 40 * Mb),
            SeedCacheFile("previews/c.jpg", 40 * Mb),
        };
        var cache = Create();
        var locks = paths
            .Select(path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            .ToList();
        try
        {
            // Every candidate refuses deletion, so the sweep can make no progress at all. It must
            // return, not spin re-picking the same oldest entry (preview_cache.py's guard).
            Assert.Equal(0, cache.EvictPreviews());
            Assert.Equal(3, FileCount(Path.Combine(_cacheRoot, "previews")));
        }
        finally
        {
            foreach (var stream in locks)
            {
                stream.Dispose();
            }
        }
    }

    [Fact]
    public void EvictPreviews_UndeletableFile_SkipsItAndContinues()
    {
        _general = _general with { PreviewCacheMb = 100 };
        var locked = SeedCacheFile("previews/a.jpg", 40 * Mb);
        var second = SeedCacheFile("previews/b.jpg", 40 * Mb);
        var third = SeedCacheFile("previews/c.jpg", 40 * Mb);
        var fourth = SeedCacheFile("previews/d.jpg", 40 * Mb);
        var cache = Create();
        cache.ResolveExisting("previews/a.jpg");
        cache.ResolveExisting("previews/b.jpg");
        cache.ResolveExisting("previews/c.jpg");
        cache.ResolveExisting("previews/d.jpg");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var deleted = cache.EvictPreviews();

            Assert.Equal(2, deleted);
            Assert.True(File.Exists(locked));
            Assert.False(File.Exists(second));
            Assert.False(File.Exists(third));
            Assert.True(File.Exists(fourth));
        }
    }

    [Fact]
    public void EvictPreviews_HonoursTheHundredMegabyteFloor()
    {
        // A preview_cache_mb of 1 would evict everything on sight without the floor; with it, the
        // bound is 100 MB and 3 MB of previews survive (questions.md Q8).
        _general = _general with { PreviewCacheMb = 1 };
        SeedCacheFile("previews/a.jpg", 1 * Mb);
        SeedCacheFile("previews/b.jpg", 1 * Mb);
        SeedCacheFile("previews/c.jpg", 1 * Mb);
        var cache = Create();

        Assert.Equal(0, cache.EvictPreviews());
        Assert.Equal(3, FileCount(Path.Combine(_cacheRoot, "previews")));
    }

    // TRACKING.md section 2 item 8: off-UI-thread work is awaited, never blocked on.
    [Fact]
    public async Task EvictPreviews_ConcurrentSweeps_DeleteEachFileAtMostOnce()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 40 * Mb);
        SeedCacheFile("previews/b.jpg", 40 * Mb);
        SeedCacheFile("previews/c.jpg", 40 * Mb);
        SeedCacheFile("previews/d.jpg", 40 * Mb);
        var cache = Create();

        var sweeps = await Task.WhenAll(
            Task.Run(cache.EvictPreviews),
            Task.Run(cache.EvictPreviews));

        var remaining = FileCount(Path.Combine(_cacheRoot, "previews"));
        Assert.Equal(2, remaining);
        Assert.Equal(4 - remaining, sweeps.Sum());
    }

    // Fix pass item 1: File.WriteAllBytes opens with FileMode.Create and FileShare.Read, so two
    // callers that computed one key must not both render and write. The second waits on the
    // per-key gate and takes the hit.
    [Fact]
    public async Task EnsurePreview_TwoConcurrentCallersForOneKey_RenderOnceAndAgreeOnThePath()
    {
        var firstRenderEntered = new TaskCompletionSource();
        using var release = new ManualResetEventSlim(false);
        _renderGate = () =>
        {
            firstRenderEntered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        var cache = Create();

        var first = Task.Run(() => cache.EnsurePreview(_framePath, CancellationToken.None));
        await firstRenderEntered.Task;

        // The second caller reaches the gate while the first is still inside the renderer, so
        // without the gate it would see no file yet and render the same frame again. There is no
        // signal for "has reached the gate" and none is needed: both orderings satisfy every
        // assertion below. A second caller already blocked on the gate takes the hit when the
        // first releases it, and one that has not reached the gate yet finds the file on its own
        // hit check. Either way the render runs once and both callers agree on the path.
        var second = Task.Run(() => cache.EnsurePreview(_framePath, CancellationToken.None));
        release.Set();

        var paths = await Task.WhenAll(first, second);

        Assert.Single(_renders);
        Assert.NotNull(paths[0]);
        Assert.Equal(paths[0], paths[1]);
        Assert.True(File.Exists(Path.Combine(_cacheRoot, paths[0]!.Replace('/', Path.DirectorySeparatorChar))));
    }

    // Fix pass item 2: the sweep a generation triggers counts the new file but must never delete
    // it, or the caller is handed a path to a file that no longer exists.
    [Fact]
    public void EnsurePreview_GenerationSweep_DoesNotEvictTheFileItJustWrote()
    {
        _general = _general with { PreviewCacheMb = 100 };
        // One undeletable preview already over the whole bound, so the sweep cannot get under the
        // cap and the newly written file is the only other candidate it has.
        var blocking = SeedCacheFile("previews/blocking.jpg", 101 * Mb);
        var cache = Create();

        using (new FileStream(blocking, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var relative = cache.EnsurePreview(_framePath, CancellationToken.None)!;

            Assert.True(File.Exists(Path.Combine(_cacheRoot, relative.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(relative, cache.EnsurePreview(_framePath, CancellationToken.None));
            Assert.Single(_renders);
        }
    }

    // Second fix pass: the key is stamped before the render and therefore before the write, so a
    // sweep that enumerates the moment the file appears sees it as the newest entry rather than as
    // an unstamped one that sorts oldest. The render delegate is the hook: it makes the file appear
    // (as the writer is about to) and runs a start-up-style sweep with no just-written exemption,
    // from inside the window a concurrent sweeper would occupy.
    [Fact]
    public void EnsurePreview_SweepSeeingTheFileAsItAppears_DoesNotEvictIt()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 40 * Mb);
        SeedCacheFile("previews/b.jpg", 40 * Mb);
        SeedCacheFile("previews/c.jpg", 40 * Mb);
        var cache = Create();
        // Used this session, so an unstamped newcomer would sort oldest of the four and go first.
        cache.ResolveExisting("previews/a.jpg");
        cache.ResolveExisting("previews/b.jpg");
        cache.ResolveExisting("previews/c.jpg");

        var expected = $"previews/{ExpectedKey(_framePath, _general.PreviewResolution)}.jpg";
        var expectedAbsolute = Path.Combine(_cacheRoot, expected.Replace('/', Path.DirectorySeparatorChar));
        var survivedTheSweep = false;
        var sweptFiles = 0;
        _renderGate = () =>
        {
            SeedCacheFile(expected, 1 * Mb);
            sweptFiles = cache.EvictPreviews();
            survivedTheSweep = File.Exists(expectedAbsolute);
        };

        var relative = cache.EnsurePreview(_framePath, CancellationToken.None);

        Assert.True(sweptFiles > 0, "the sweep had no work, so the test proved nothing");
        Assert.True(survivedTheSweep, "a sweep evicted the preview being written");
        Assert.Equal(expected, relative);
        Assert.True(File.Exists(expectedAbsolute));
    }

    // Fix pass item 5: the sweep filters on the extension rather than trusting the search pattern.
    // Directory.EnumerateFiles defaults to MatchType.Simple on .NET 10, which does not apply the
    // Win32 three-character rule, so "*.jpg" does not match c.jpga today; this test guards the
    // observable contract, which is that the cache deletes nothing it did not write, whichever
    // match type the enumeration uses.
    [Fact]
    public void EvictPreviews_NeverDeletesAFileWhoseExtensionOnlyLooksLikeJpg()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 60 * Mb);
        SeedCacheFile("previews/b.jpg", 60 * Mb);
        var notAJpeg = SeedCacheFile("previews/c.jpga", 60 * Mb);
        var cache = Create();

        cache.EvictPreviews();

        Assert.True(File.Exists(notAJpeg));
    }

    // ------------------------------------------- spec 10.6's budget, enforced at the choke point

    // Phase 8 phase review finding 1: the budget used to live in ThumbnailWorker's pump count, so
    // the reference pass, which renders on the scan thread and never enters the worker, made a
    // third concurrent decode reachable. It is enforced here now, which is the one line every
    // caller passes through.
    [Fact]
    public async Task Ensure_ThreeConcurrentCallers_RunAtMostTwoRendersAtOnce()
    {
        var frames = new[] { WriteFrame("budget-a.fits", 64), WriteFrame("budget-b.fits", 65), WriteFrame("budget-c.fits", 66) };
        var peakGate = new object();
        var inFlight = 0;
        var peak = 0;
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        _renderGate = () =>
        {
            lock (peakGate)
            {
                inFlight++;
                peak = Math.Max(peak, inFlight);
            }
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
            lock (peakGate)
            {
                inFlight--;
            }
        };
        var cache = Create();

        // Three different frames, so three different keys: the per-key gate does not serialize
        // them and only the budget can.
        var calls = frames
            .Select(frame => Task.Run(() => cache.EnsureFrame(frame, CancellationToken.None)))
            .ToArray();

        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)), "the first render never started");
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)), "the second render never started");

        // A negative assertion, so it is the one place here that has to wait out a duration rather
        // than a signal: with the budget in place the third render cannot start until a permit is
        // returned, and nothing returns one while the gate is held.
        Assert.False(
            await entered.WaitAsync(TimeSpan.FromMilliseconds(500)),
            "a third render started while two were already in flight");

        release.Set();
        var paths = await Task.WhenAll(calls);

        Assert.Equal(ThumbnailWorker.MaxConcurrency, peak);
        Assert.Equal(3, _renders.Count);
        Assert.All(paths, Assert.NotNull);
    }

    // ----------------------------------------------------------------- forced reference renders

    // Phase 8 phase review finding 2: reference/<target id>.jpg is a stable path, so without force
    // the hit check inside the key gate serves last run's file and spec 12.7's regenerate action
    // produces no new pixels while reporting a generated count.
    [Fact]
    public void EnsureReference_Forced_RendersAgainOverTheExistingFile()
    {
        var cache = Create();
        var targetId = Guid.NewGuid();
        var relative = cache.EnsureReference(targetId, _framePath, force: false, CancellationToken.None)!;
        var absolute = Path.Combine(_cacheRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(16, new FileInfo(absolute).Length);

        // A different render output, so "the file was rewritten" is an assertion about bytes and
        // not about a timestamp.
        _renderBytes = 32;
        var forced = cache.EnsureReference(targetId, _framePath, force: true, CancellationToken.None);

        Assert.Equal(relative, forced);
        Assert.Equal(2, _renders.Count);
        Assert.Equal(32, new FileInfo(absolute).Length);
    }

    [Fact]
    public void EnsureReference_NotForced_ServesTheExistingFileWithoutRendering()
    {
        var cache = Create();
        var targetId = Guid.NewGuid();
        var relative = cache.EnsureReference(targetId, _framePath, force: false, CancellationToken.None);

        _renderBytes = 32;
        var second = cache.EnsureReference(targetId, _framePath, force: false, CancellationToken.None);

        Assert.Equal(relative, second);
        Assert.Single(_renders);
    }

    // File safety, restated per task (spec 2.1, 2.1.1).

    [Fact]
    public void Cache_WritesOnlyUnderTheConfiguredRoot()
    {
        var cache = Create();
        var siblingBefore = FileCount(_siblingRoot);
        var frameRootBefore = FileCount(_frameRoot);
        var appDataBefore = FileCount(_appDataRoot);

        cache.EnsureFrame(_framePath, CancellationToken.None);
        cache.EnsurePreview(_framePath, CancellationToken.None);
        cache.EnsureReference(Guid.NewGuid(), _framePath, force: false, CancellationToken.None);

        Assert.Equal(siblingBefore, FileCount(_siblingRoot));
        Assert.Equal(frameRootBefore, FileCount(_frameRoot));
        Assert.Equal(appDataBefore, FileCount(_appDataRoot));
        Assert.Equal(3, FileCount(_cacheRoot));
    }

    // The cache composes its own relative paths, so a traversal cannot originate inside it; the
    // assertion belongs at the choke point it routes every path through.
    [Fact]
    public void Cache_PathOutsideTheRoot_ThrowsUnauthorizedPathAndWritesNothing()
    {
        var escaping = Path.Combine("..", $"{Guid.NewGuid():N}.jpg");
        var siblingBefore = FileCount(_siblingRoot);

        Assert.Throws<UnauthorizedPathException>(() => _writer.ResolveThumbnailPath(escaping));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_cacheRoot, escaping))));
        Assert.Equal(siblingBefore, FileCount(_siblingRoot));
    }

    [Fact]
    public void Cache_NeverDeletesOutsideThePreviewsSubdirectory()
    {
        _general = _general with { PreviewCacheMb = 100 };
        SeedCacheFile("previews/a.jpg", 60 * Mb);
        SeedCacheFile("previews/b.jpg", 60 * Mb);
        var survivors = new[]
        {
            SeedCacheFile("frames/one.jpg", 60 * Mb),
            SeedCacheFile($"reference/{Guid.NewGuid():D}.jpg", 60 * Mb),
            SeedCacheFile("loose.jpg", 60 * Mb),
        };
        var sibling = Path.Combine(_siblingRoot, "outside.jpg");
        File.WriteAllBytes(sibling, new byte[16]);
        var cache = Create();

        cache.EvictPreviews();

        Assert.All(survivors, path => Assert.True(File.Exists(path), $"{path} was deleted"));
        Assert.True(File.Exists(sibling));
    }

    /// <summary>
    /// Fixer list code item 7. <c>AppHost.ThumbnailCacheBytes</c> sums spec 12.5's cache figure
    /// over these directories, and it used to repeat the three names as literals with a note
    /// saying a fourth kind would be silently omitted. The list is now derived from the one
    /// kind-to-directory mapping, so this case is what proves the derivation covers every kind
    /// rather than a list that happens to have three entries today.
    /// </summary>
    [Fact]
    public void Directories_CoverEveryThumbnailKind()
    {
        Assert.Equal(Enum.GetValues<ThumbnailKind>().Length, ThumbnailCache.Directories.Count);
        Assert.Equal(
            new[] { "frames", "previews", "reference" },
            ThumbnailCache.Directories.Order(StringComparer.Ordinal).ToArray());

        // Each is a plain relative segment, because AppWriter.EnumerateThumbnailFiles resolves
        // them under the authorized cache root.
        Assert.All(
            ThumbnailCache.Directories,
            directory => Assert.False(Path.IsPathRooted(directory)));
    }
}
