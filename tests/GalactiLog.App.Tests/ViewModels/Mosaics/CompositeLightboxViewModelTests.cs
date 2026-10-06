using Avalonia.Headless.XUnit;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.Core.Io;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Mosaics;

// Phase 19B Task 4, spec 12.17's composite lightbox over a real CompositeService whose drawing is a
// fake the test releases: it reports each label, then parks on a TaskCompletionSource until the test
// completes it, observing the token it was handed. The post is synchronous. Bitmaps need the
// headless platform, so the cases that reach Ready are AvaloniaFacts.
public sealed class CompositeLightboxViewModelTests : IDisposable
{
    private static readonly Guid Mosaic = Guid.NewGuid();
    private static readonly Guid Panel1 = Guid.NewGuid();
    private static readonly Guid Panel2 = Guid.NewGuid();
    private static readonly Guid Panel3 = Guid.NewGuid();
    private static readonly Guid Frame1 = Guid.NewGuid();
    private static readonly Guid Frame2 = Guid.NewGuid();
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-composite-").FullName;
    private readonly List<CompositeLightboxViewModel> _pages = [];
    private readonly JobRegistry _jobs = new(action => action());
    private TaskCompletionSource<CompositeResult> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _builds;
    private CancellationToken _observed;

    public void Dispose()
    {
        _pages.ForEach(page => page.Dispose());
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Gray);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private CompositeService Service() => new(_jobs, (_, _, _, _) => { }, (selection, paths, progress, ct) =>
    {
        Interlocked.Increment(ref _builds);
        _observed = ct;
        foreach (var panel in selection.Included)
        {
            progress(panel.Label);
        }

        _started.TrySetResult();
        return _gate.Task.WaitAsync(ct).GetAwaiter().GetResult();
    });

    private static PanelGeometry Positioned(double ra) => new(ra, 44.3, 64, 15.0, 0, "West");

    // Panel 1 positioned, Panel 2 with no position, Panel 3 with no Ha frame, in sort_order; or
    // every panel in when allIn.
    private static CompositeRequest Request(bool allIn = false, string name = "M 31")
    {
        var best = new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>
        {
            [Panel1] = new Dictionary<string, BestFrame>(StringComparer.OrdinalIgnoreCase) { ["Ha"] = new(Frame1, "p1.fits", "Ha", 1) },
            [Panel2] = new Dictionary<string, BestFrame>(StringComparer.OrdinalIgnoreCase) { ["Ha"] = new(Frame2, "p2.fits", "Ha", 1) },
        };
        var geometry = new Dictionary<Guid, PanelGeometry> { [Frame1] = Positioned(10) };
        if (allIn)
        {
            geometry[Frame2] = Positioned(10.3);
        }

        List<(Guid, string)> panels = allIn
            ? [(Panel1, "Panel 1"), (Panel2, "Panel 2")]
            : [(Panel1, "Panel 1"), (Panel2, "Panel 2"), (Panel3, "Panel 3")];
        return new CompositeRequest(Mosaic, name, "Ha", panels, new PanelFrameSet(["Ha"], "Ha", best), geometry);
    }

    private Task Started() => _started.Task.WaitAsync(Budget);

    private CompositeLightboxViewModel Open(CompositeService? service = null, CompositeRequest? request = null)
    {
        var page = new CompositeLightboxViewModel(request ?? Request(), service ?? Service(), new AppWriter(_root), post: action => action());
        _pages.Add(page);
        return page;
    }

    private async Task Complete(CompositeLightboxViewModel page, CompositeResult result)
    {
        _gate.SetResult(result);
        await page.PendingBuild.WaitAsync(Budget);
    }

    [AvaloniaFact]
    public async Task TheStates_RunBuildingThenReady_AndDownloadIsEnabledInReadyAlone()
    {
        var page = Open();
        Assert.True(page.IsBuilding);
        await Started();

        Assert.True(page.IsBuilding);
        Assert.Equal("M 31, Ha composite", page.Title);
        Assert.Equal("Composite of M 31, Ha", page.ImageName);
        Assert.Equal("Decoding Panel 1", page.ProgressText);
        Assert.Null(page.Image);
        Assert.False(page.CanDownload);
        Assert.False(page.DownloadCommand.CanExecute(null));
        Assert.Null(page.LeftOutText);

        var jpeg = Jpeg(7, 5);
        await Complete(page, new CompositeResult(jpeg, 7, 5));

        Assert.False(page.IsBuilding);
        Assert.Null(page.ProgressText);
        // The headless platform stubs every bitmap at 1 by 1, so the bytes are pinned by the
        // decode seam in Ready_DecodesTheResultsBytesFromMemory rather than by the size here.
        Assert.NotNull(page.Image);
        Assert.True(page.CanDownload);
        Assert.True(page.DownloadCommand.CanExecute(null));
        Assert.False(page.HasError);
        Assert.Equal(1, _builds);
    }

    [AvaloniaFact]
    public void Ready_DecodesTheResultsBytesFromMemory()
    {
        var jpeg = Jpeg(7, 5);
        var service = Service();
        _gate.SetResult(new CompositeResult(jpeg, 7, 5));
        service.BuildAsync(Request(), CancellationToken.None).Wait(Budget);
        byte[]? decoded = null;

        var page = new CompositeLightboxViewModel(Request(), service, new AppWriter(_root), post: action => action(),
            decode: bytes => { decoded = bytes; return new Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes)); });
        _pages.Add(page);

        Assert.Same(jpeg, decoded);
        Assert.NotNull(page.Image);
    }

    [AvaloniaFact]
    public void ACacheHit_OpensReady_WithNoBuildAndNoJob()
    {
        var service = Service();
        _gate.SetResult(new CompositeResult(Jpeg(4, 4), 4, 4));
        service.BuildAsync(Request(), CancellationToken.None).Wait(Budget);
        var jobsBefore = _jobs.Recent.Count;

        var page = Open(service);

        Assert.Equal(1, _builds);
        Assert.Equal(jobsBefore, _jobs.Recent.Count);
        Assert.False(page.IsBuilding);
        Assert.NotNull(page.Image);
        Assert.True(page.CanDownload);
    }

    [Fact]
    public async Task AFailingBuild_ShowsTheReason_AndRetryRebuilds()
    {
        var page = Open();
        _gate.SetException(new InvalidOperationException("Panel 1: unsupported NAXIS shape"));
        await page.PendingBuild.WaitAsync(Budget);

        Assert.False(page.IsBuilding);
        Assert.True(page.HasError);
        Assert.Equal("Panel 1: unsupported NAXIS shape", page.ErrorText);
        Assert.False(page.CanDownload);

        _gate = new TaskCompletionSource<CompositeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.RetryCommand.Execute(null);
        await Started();

        Assert.True(page.IsBuilding);
        Assert.False(page.HasError);
        Assert.Equal("Decoding Panel 1", page.ProgressText);
        Assert.Equal(2, _builds);
        Assert.Equal(2, _jobs.Running.Count + _jobs.Recent.Count);
    }

    [Fact]
    public async Task ACancelFromTheFlyout_ShowsFailed_WithTheCancelledReason()
    {
        var page = Open();
        await Started();

        Assert.Single(_jobs.Running).CancelCommand.Execute(null);
        await page.PendingBuild.WaitAsync(Budget);

        Assert.Equal(CompositeLightboxViewModel.CancelledText, page.ErrorText);
        Assert.False(page.IsBuilding);
    }

    [Fact]
    public async Task Closing_CancelsTheBuild_AndShowsNothing()
    {
        var service = Service();
        var page = Open(service);
        await Started();

        page.Dispose();
        await page.PendingBuild.WaitAsync(Budget);

        Assert.True(_observed.IsCancellationRequested);
        Assert.Null(page.Image);
        Assert.Null(page.ErrorText);
        Assert.False(service.TryGetCached(Request(), out _));
        Assert.Equal(JobResult.Cancelled, Assert.Single(_jobs.Recent).Result);
    }

    [AvaloniaFact]
    public async Task TheLeftOutSentence_NamesEveryLeftOutPanelInSortOrder()
    {
        var page = Open();
        await Complete(page, new CompositeResult(Jpeg(4, 4), 4, 4));

        Assert.Equal("Not in this composite: Panel 2 (no position), Panel 3 (no Ha frames).", page.LeftOutText);
    }

    [AvaloniaFact]
    public async Task TheLeftOutSentence_IsNull_WhenEveryPanelIsIn()
    {
        var page = Open(request: Request(allIn: true));
        await Complete(page, new CompositeResult(Jpeg(4, 4), 4, 4));

        Assert.Null(page.LeftOutText);
    }

    [AvaloniaFact]
    public async Task ACancelledDialog_WritesNothing()
    {
        var page = Open();
        await Complete(page, new CompositeResult(Jpeg(4, 4), 4, 4));
        string? suggested = null;
        page.ExportDestinationPicker = name =>
        {
            suggested = name;
            return Task.FromResult<string?>(null);
        };

        await page.DownloadCommand.ExecuteAsync(null);

        Assert.Equal("M_31-Ha.jpg", suggested);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Null(page.DownloadError);
    }

    [AvaloniaFact]
    public async Task AChosenPath_WritesOneNewFile_ByteForByte()
    {
        var page = Open();
        var jpeg = Jpeg(6, 3);
        await Complete(page, new CompositeResult(jpeg, 6, 3));
        var path = Path.Combine(_root, "out", "composite.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        page.ExportDestinationPicker = _ => Task.FromResult<string?>(path);

        await page.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(path, Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)));
        Assert.Equal(jpeg, File.ReadAllBytes(path));
        Assert.Null(page.DownloadError);
    }

    [AvaloniaFact]
    public async Task AFailedWrite_ShowsTheSentence_AndDoesNotThrow()
    {
        var page = Open();
        await Complete(page, new CompositeResult(Jpeg(4, 4), 4, 4));
        page.ExportDestinationPicker = _ => Task.FromResult<string?>(Path.Combine(_root, "missing", "composite.jpg"));

        await page.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(CompositeLightboxViewModel.DownloadFailedText, page.DownloadError);
    }

    [Theory]
    [InlineData("NGC 7000", "Ha", "NGC_7000-Ha.jpg")]
    [InlineData("M 31", "L-Pro", "M_31-L_Pro.jpg")]
    public void TheDownloadName_PassesTheNameAndTheFilterThroughTheExportRule(string name, string filter, string expected)
        => Assert.Equal(expected, CompositeLightboxViewModel.DownloadFileName(name, filter));

    [Fact]
    public void Zoom_ClampsToTheRange_AndPanIsRefusedAtFitAndBelow()
    {
        var page = Open();

        page.Pan(10, 10);
        Assert.Equal((0d, 0d), (page.OffsetX, page.OffsetY));

        page.Zoom(1000, 50, 50);
        Assert.True(page.Scale < 1d);
        page.Pan(10, 10);
        Assert.Equal((0d, 0d), (page.OffsetX, page.OffsetY));

        for (var notch = 0; notch < 50; notch++)
        {
            page.Zoom(1000, 0, 0);
        }

        Assert.Equal(0.1, page.Scale, 9);

        for (var notch = 0; notch < 100; notch++)
        {
            page.Zoom(-1000, 20, 0);
        }

        Assert.Equal(8d, page.Scale, 9);
        var before = page.OffsetX;
        page.Pan(5, -3);
        Assert.Equal(before + 5, page.OffsetX, 9);

        page.ResetFit();
        Assert.Equal((1d, 0d, 0d), (page.Scale, page.OffsetX, page.OffsetY));
    }

    [AvaloniaFact]
    public async Task AThrowingPicker_IsShownAsTheWriteFailure_AndWritesNothing()
    {
        var page = Open();
        await Complete(page, new CompositeResult(Jpeg(4, 4), 4, 4));
        page.ExportDestinationPicker = _ => throw new InvalidOperationException("no dialog");

        await page.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(CompositeLightboxViewModel.DownloadFailedText, page.DownloadError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }
}
