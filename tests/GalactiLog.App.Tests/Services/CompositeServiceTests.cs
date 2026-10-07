using GalactiLog.App.Services;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Phase 19B Task 3. Spec 11.6's cache, job and Activity rows around a fake drawing: the fake counts
// its calls, reports each label, and can throw or wait on its token. The registry posts
// synchronously, so its lists are current the moment a call returns.
public class CompositeServiceTests
{
    private static readonly Guid Mosaic = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Panel1 = Guid.Parse("21111111-1111-1111-1111-111111111111");
    private static readonly Guid Panel2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Frame1 = Guid.Parse("31111111-1111-1111-1111-111111111111");
    private static readonly Guid Frame2 = Guid.Parse("32222222-2222-2222-2222-222222222222");

    private readonly JobRegistry _jobs = new(action => action());
    private readonly List<(string Severity, string EventType, string Message, object Details)> _rows = [];
    private int _builds;

    private CompositeService Service(
        Func<CompositeSelection, IReadOnlyList<string>, Action<string>, CancellationToken, CompositeResult>? build = null)
        => new(_jobs, (severity, eventType, message, details) => _rows.Add((severity, eventType, message, details)),
            build ?? ((selection, framePaths, progress, ct) =>
            {
                Interlocked.Increment(ref _builds);
                Assert.True(Thread.CurrentThread.IsThreadPoolThread, "the build must run off the caller's thread");
                Assert.Equal(["p1.fits", "p2.fits"], framePaths);
                foreach (var panel in selection.Included) progress(panel.Label);
                return new CompositeResult([1, 2, 3], 10, 20);
            }));

    private static PanelGeometry Positioned(double ra) => new(ra, 44.3, 64, 15.0, 0, "West");

    private static CompositeRequest Request(Guid? mosaic = null, string filter = "Ha", Guid? frame2 = null)
    {
        var second = frame2 ?? Frame2;
        var best = new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>
        {
            [Panel1] = new Dictionary<string, BestFrame>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ha"] = new(Frame1, "p1.fits", "Ha", 1), ["OIII"] = new(Frame1, "p1.fits", "OIII", 1),
            },
            [Panel2] = new Dictionary<string, BestFrame>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ha"] = new(second, "p2.fits", "Ha", 1), ["OIII"] = new(second, "p2.fits", "OIII", 1),
            },
        };
        return new CompositeRequest(
            mosaic ?? Mosaic, "M 31", filter, [(Panel1, "Panel 1"), (Panel2, "Panel 2")],
            new PanelFrameSet(["Ha", "OIII"], "Ha", best),
            new Dictionary<Guid, PanelGeometry> { [Frame1] = Positioned(10), [second] = Positioned(10.3) });
    }

    [Fact]
    public async Task Success_FinishesTheJob_WritesTheBuiltRow_AndCaches()
    {
        var service = Service();

        var result = await service.BuildAsync(Request(), CancellationToken.None);

        Assert.Equal((10, 20), (result.Width, result.Height));
        var job = Assert.Single(_jobs.Recent);
        Assert.Equal((CompositeService.BuildJobKind, "Composite: M 31, Ha"), (job.Kind, job.Title));
        Assert.Equal((JobResult.Succeeded, "10 by 20 pixels"), (job.Result, job.Summary));
        Assert.Equal(("Decoding Panel 2", 50.0), (job.Message, job.Percent));
        var row = Assert.Single(_rows);
        Assert.Equal(("info", "mosaic_composite_built", "M 31, Ha: 10 by 20 pixels from 2 panels"), (row.Severity, row.EventType, row.Message));
        Assert.Equivalent(new { mosaic_id = Mosaic, filter = "Ha" }, row.Details, strict: true);
        Assert.True(service.TryGetCached(Request(), out var cached));
        Assert.Same(result, cached);
    }

    [Fact]
    public async Task ASecondBuildOfTheSameRequest_IsACacheHit_WithNoJobAndNoRow()
    {
        var service = Service();
        var first = await service.BuildAsync(Request(), CancellationToken.None);

        var second = await service.BuildAsync(Request(), CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, _builds);
        Assert.Single(_jobs.Recent);
        Assert.Single(_rows);
    }

    [Fact]
    public async Task AnotherFilterOrAnotherBestFrame_IsAMiss()
    {
        var service = Service();
        await service.BuildAsync(Request(), CancellationToken.None);

        Assert.False(service.TryGetCached(Request(filter: "OIII"), out _));
        Assert.False(service.TryGetCached(Request(frame2: Guid.NewGuid()), out _));
        await service.BuildAsync(Request(filter: "OIII"), CancellationToken.None);
        Assert.Equal(2, _builds);
    }

    [Fact]
    public void CacheKey_IsTheSha256OfTheIdsAndTheirGeometry()
    {
        // Ruling R16a: the web's shape with each id followed by its six geometry values, invariant
        // and round-trip, null empty, joined by "|". The ids go in unsorted and upper case and come
        // out sorted and lower case, each keeping its own geometry. The hashed string is:
        const string hashed = "11111111-1111-1111-1111-111111111111:Ha:"
            + "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa|10.25|-44.3||1.5||,"
            + "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb|0.1|44.3|6000|15|90.5|West";
        var key = CompositeService.CacheKey(
            Mosaic, "Ha",
            [
                (Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"), new PanelGeometry(0.1, 44.3, 6000, 15.0, 90.5, "West")),
                (Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"), new PanelGeometry(10.25, -44.3, null, 1.5, null, null)),
            ]);

        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashed))), key);
    }

    [Fact]
    public async Task TheSameFramesWithOneGeometryValueChanged_IsAMiss()
    {
        var service = Service();
        await service.BuildAsync(Request(), CancellationToken.None);
        var request = Request();

        var flipped = request with
        {
            Geometry = new Dictionary<Guid, PanelGeometry>(request.Geometry) { [Frame2] = Positioned(10.3) with { PierSide = "East" } },
        };

        Assert.True(service.TryGetCached(request, out _));
        Assert.False(service.TryGetCached(flipped, out _));
    }

    [Fact]
    public async Task ACancelArrivingDuringACompletedDraw_CachesNothing_WritesNoRow()
    {
        using var cancel = new CancellationTokenSource();
        var draw = Service((_, _, _, _) =>
        {
            cancel.Cancel();
            return new CompositeResult([1], 1, 1);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => draw.BuildAsync(Request(), cancel.Token));

        Assert.Equal(JobResult.Cancelled, Assert.Single(_jobs.Recent).Result);
        Assert.Empty(_rows);
        Assert.False(draw.TryGetCached(Request(), out _));
    }

    [Fact]
    public async Task CancelThroughTheJob_FinishesCancelled_CachesNothing_WritesNoRow()
    {
        using var started = new ManualResetEventSlim();
        var observed = false;
        var service = Service((_, _, _, ct) =>
        {
            started.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            observed = ct.IsCancellationRequested;
            ct.ThrowIfCancellationRequested();
            return new CompositeResult([], 1, 1);
        });

        var build = service.BuildAsync(Request(), CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        Assert.Single(_jobs.Running).CancelCommand.Execute(null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        Assert.True(observed);
        Assert.Equal(JobResult.Cancelled, Assert.Single(_jobs.Recent).Result);
        Assert.Empty(_rows);
        Assert.False(service.TryGetCached(Request(), out _));
    }

    [Fact]
    public async Task AThrowingBuild_FinishesFailed_WritesTheFailedRow_AndPropagates()
    {
        var service = Service((_, _, _, _) => throw new InvalidOperationException("Panel 2: cannot read frame: gone"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(Request(), CancellationToken.None));

        Assert.Equal("Panel 2: cannot read frame: gone", ex.Message);
        var job = Assert.Single(_jobs.Recent);
        Assert.Equal((JobResult.Failed, ex.Message), (job.Result, job.Summary));
        var row = Assert.Single(_rows);
        Assert.Equal(("error", "mosaic_composite_failed", ex.Message), (row.Severity, row.EventType, row.Message));
        Assert.Equivalent(new { mosaic_id = Mosaic, filter = "Ha", reason = ex.Message }, row.Details, strict: true);
        Assert.False(service.TryGetCached(Request(), out _));
    }

    [Fact]
    public async Task TwentyOneRequests_KeepTwenty_TheFirstEvicted()
    {
        var service = Service();
        var mosaics = Enumerable.Range(0, CompositeService.CacheCap + 1).Select(_ => Guid.NewGuid()).ToList();

        foreach (var mosaic in mosaics)
        {
            await service.BuildAsync(Request(mosaic), CancellationToken.None);
        }

        Assert.False(service.TryGetCached(Request(mosaics[0]), out _));
        Assert.All(mosaics.Skip(1), mosaic => Assert.True(service.TryGetCached(Request(mosaic), out _)));
    }

    [Fact]
    public void Select_LeavesOutAPanelWithNoFrameInTheFilter_AndOneWithNoGeometry()
    {
        var request = Request() with
        {
            Panels = [(Panel1, "Panel 1"), (Panel2, "Panel 2"), (Guid.NewGuid(), "Panel 3")],
            Geometry = new Dictionary<Guid, PanelGeometry> { [Frame1] = Positioned(10) },
        };

        var selection = Service().Select(request);

        Assert.Equal([Panel1], selection.Included.Select(panel => panel.PanelId));
        Assert.Equal(
            [("Panel 2", LeftOutReason.NoPosition), ("Panel 3", LeftOutReason.NoFrames)],
            selection.LeftOut.Select(panel => (panel.Label, panel.Reason)));
        Assert.True(selection.IsPossible);
    }

    [Fact]
    public void Select_LooksTheFilterUpCaseInsensitive()
    {
        Assert.Equal(2, Service().Select(Request(filter: "ha")).Included.Count);
    }
}
