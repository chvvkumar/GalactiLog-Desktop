using Avalonia;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Mosaics;

// Phase 19A Task 3, spec 12.17's arranger as a view-model: the plan's verify list against delegate
// stubs, a synchronous post and, where the debounce is under test, a delay the test releases.
public sealed class ArrangerViewModelTests : IDisposable
{
    private static readonly Guid MosaicId = Guid.NewGuid();
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly List<IDisposable> _owned = [];
    private readonly ManualResetEventSlim _renders = new(initialState: true);
    private readonly ThumbnailWorker _worker;

    public ArrangerViewModelTests()
    {
        _worker = new ThumbnailWorker(
            (path, ct) =>
            {
                _renders.Wait(ct);
                return $"frames/{path}.jpg";
            },
            (_, _) => null,
            post: action => action());
    }

    public void Dispose()
    {
        _renders.Set();
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _worker.Dispose();
        _renders.Dispose();
    }

    // ---- fixtures ----------------------------------------------------------------------------

    private static PanelDetail Panel(string label, int order, double seconds = 600, double deficit = 0, double? x = null, double? y = null)
        => new(Guid.NewGuid(), label, order, x, y, 0, false, [], [], seconds, 1, 1, 0, deficit, [], [], new Dictionary<string, double>());

    private static MosaicDetail Detail(IReadOnlyList<PanelDetail> panels, double rotation = 0)
        => new(MosaicId, "M 31", null, rotation, panels.Sum(panel => panel.IntegrationSeconds), panels.Count, null, null, [], panels, []);

    private static List<PanelDetail> Panels(int count)
        => [.. Enumerable.Range(0, count).Select(index => Panel($"Panel {index + 1}", index))];

    // Spec 12.17's deficit is leader minus panel, as MosaicQueries computes it.
    private static List<PanelDetail> WithDeficits(params double[] seconds)
    {
        var leader = seconds.Max();
        return [.. seconds.Select((value, index) => Panel($"Panel {index + 1}", index, value, leader > 0 ? leader - value : 0))];
    }

    private ThumbnailSlotViewModel Slot(string path)
        => new(path, _worker, _ => [0x01], decode: _ => null, post: action => action());

    private ArrangerViewModel Arranger(
        MosaicsBackend? backend = null, bool readOnly = false, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var arranger = new ArrangerViewModel(
            MosaicId,
            backend ?? new MosaicsBackend(),
            readOnly,
            post: action => action(),
            delay: delay ?? ((_, _) => Task.CompletedTask),
            NullLogger.Instance);
        _owned.Add(arranger);
        return arranger;
    }

    private sealed class LayoutLog
    {
        public List<(double Rotation, IReadOnlyList<(Guid PanelId, double? X, double? Y, int Rotation, bool FlipH)> Panels)> Writes { get; } = [];

        public bool Fail { get; set; }

        public void Write(Guid mosaic, double rotation, IReadOnlyList<(Guid PanelId, double? X, double? Y, int Rotation, bool FlipH)> panels)
        {
            Assert.Equal(MosaicId, mosaic);
            if (Fail)
            {
                throw new InvalidOperationException("locked");
            }

            lock (Writes)
            {
                Writes.Add((rotation, panels));
            }
        }
    }

    // A delay the test releases: every window parks until Release, and a cancelled window ends
    // cancelled, the way Task.Delay does.
    private sealed class ManualDelay
    {
        private readonly List<TaskCompletionSource> _windows = [];

        public Task Wait(TimeSpan window, CancellationToken cancellationToken)
        {
            Assert.Equal(ArrangerViewModel.SaveDebounce, window);
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            _windows.Add(source);
            return source.Task;
        }

        public void Release() => _windows.ForEach(window => window.TrySetResult());
    }

    private static PanelFrameSet Frames(IReadOnlyList<string> filters, string? defaultFilter, params (Guid Panel, string Filter, string Path)[] best)
    {
        var byPanel = best.GroupBy(entry => entry.Panel).ToDictionary(
            group => group.Key,
            group => (IReadOnlyDictionary<string, BestFrame>)group.ToDictionary(
                entry => entry.Filter, entry => new BestFrame(Guid.NewGuid(), entry.Path, entry.Filter, 1), StringComparer.OrdinalIgnoreCase));
        return new PanelFrameSet(filters, defaultFilter, byPanel);
    }

    // ---- the deficit badge -------------------------------------------------------------------

    [Fact]
    public void TheDeficitBand_FollowsTheShareOfTheLeader()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(WithDeficits(3600, 2900, 2000, 1000, 3570)));

        var tiles = arranger.Tiles;
        Assert.Equal(DeficitBand.None, tiles[0].Deficit);
        Assert.Null(tiles[0].DeficitText);
        Assert.Equal(DeficitBand.Success, tiles[1].Deficit);
        Assert.Equal("-" + MetricText.Integration(700), tiles[1].DeficitText);
        Assert.Equal(DeficitBand.Warning, tiles[2].Deficit);
        Assert.Equal(DeficitBand.Error, tiles[3].Deficit);
        Assert.Equal("-" + MetricText.Integration(2600), tiles[3].DeficitText);
        Assert.Equal(DeficitBand.None, tiles[4].Deficit);
        Assert.Equal(MetricText.Integration(2900), tiles[1].IntegrationText);
    }

    [Fact]
    public void TheDeficitBand_AtItsBoundaries()
    {
        var arranger = Arranger();

        // 60 s behind shows nothing, 61 s shows a badge; exactly 0.8 of the leader is Success and
        // exactly 0.4 is Warning.
        arranger.Apply(Detail(WithDeficits(3600, 3540, 3539, 2880, 1440)));

        var tiles = arranger.Tiles;
        Assert.Equal(DeficitBand.None, tiles[1].Deficit);
        Assert.Null(tiles[1].DeficitText);
        Assert.Equal(DeficitBand.Success, tiles[2].Deficit);
        Assert.Equal("-" + MetricText.Integration(61), tiles[2].DeficitText);
        Assert.Equal(DeficitBand.Success, tiles[3].Deficit);
        Assert.Equal(DeficitBand.Warning, tiles[4].Deficit);
    }

    [Fact]
    public void NoBadge_WhileTheLeaderIsZero()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(WithDeficits(0, 0, 0)));

        Assert.All(arranger.Tiles, tile =>
        {
            Assert.Equal(DeficitBand.None, tile.Deficit);
            Assert.Null(tile.DeficitText);
        });
    }

    // ---- rotate, flip, reset -------------------------------------------------------------------

    [Fact]
    public void Rotate_CyclesThroughTheQuarterTurns_AndTheBadgeFollows()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(Panels(1)));
        var tile = arranger.Tiles[0];
        Assert.False(arranger.RotateSelectedCommand.CanExecute(null));
        Assert.Null(tile.StateBadge);

        arranger.Select(tile);
        Assert.True(arranger.RotateSelectedCommand.CanExecute(null));
        var seen = new List<(int, string?)>();
        for (var turn = 0; turn < 4; turn++)
        {
            arranger.RotateSelectedCommand.Execute(null);
            seen.Add((tile.Rotation, tile.StateBadge));
        }

        Assert.Equal(new (int, string?)[] { (90, "90°"), (180, "180°"), (270, "270°"), (0, null) }, seen);
    }

    [Fact]
    public void Flip_Toggles_AndCombinesWithTheRotationInTheBadge()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(Panels(1)));
        var tile = arranger.Tiles[0];

        arranger.Flip(tile);
        Assert.True(tile.FlipH);
        Assert.Equal("flipped", tile.StateBadge);

        arranger.Rotate(tile);
        Assert.Equal("90° · flipped", tile.StateBadge);

        arranger.Flip(tile);
        Assert.False(tile.FlipH);
        Assert.Equal("90°", tile.StateBadge);
    }

    [Fact]
    public void ResetAll_ZeroesRotationFlipAndGlobalRotation_AndKeepsPositions()
    {
        var arranger = Arranger();
        arranger.Apply(Detail([Panel("Panel 1", 0, x: 100, y: 50), Panel("Panel 2", 1)], rotation: 30));
        var (one, two) = (arranger.Tiles[0], arranger.Tiles[1]);
        arranger.Rotate(one);
        arranger.Flip(two);
        var positions = arranger.Tiles.Select(tile => (tile.X, tile.Y)).ToList();

        arranger.ResetAllCommand.Execute(null);

        Assert.All(arranger.Tiles, tile =>
        {
            Assert.Equal(0, tile.Rotation);
            Assert.False(tile.FlipH);
            Assert.Null(tile.StateBadge);
        });
        Assert.Equal(0, arranger.GlobalRotation);
        Assert.Equal("0°", arranger.RotationText);
        Assert.Equal(positions, arranger.Tiles.Select(tile => (tile.X, tile.Y)));
    }

    // ---- the save rule -----------------------------------------------------------------------

    [Fact]
    public async Task ABurstOfChanges_SavesOnce_WithEveryTileAndTheGlobalRotation()
    {
        var log = new LayoutLog();
        var delay = new ManualDelay();
        var arranger = Arranger(new MosaicsBackend { UpdateLayout = log.Write }, delay: delay.Wait);
        arranger.Apply(Detail(Panels(3)));
        var (dragged, unplaced) = (arranger.Tiles[0], arranger.Tiles[2]);

        arranger.Select(dragged);
        arranger.BeginDrag(dragged, 10, 10);
        for (var step = 1; step <= 6; step++)
        {
            arranger.Drag(dragged, 10 + step * 100, 10 + step * 20);
        }

        arranger.EndDrag(dragged);
        arranger.Rotate(dragged);
        arranger.GlobalRotation = 15;
        Assert.Empty(log.Writes);

        delay.Release();
        await arranger.PendingSave.WaitAsync(Budget);

        var (rotation, panels) = Assert.Single(log.Writes);
        Assert.Equal(15, rotation);
        Assert.Equal(arranger.Tiles.Select(tile => tile.PanelId), panels.Select(panel => panel.PanelId));
        Assert.Equal((600d, 120d, 90, false), (panels[0].X!.Value, panels[0].Y!.Value, panels[0].Rotation, panels[0].FlipH));

        // Three unplaced tiles take two columns: the third sits at the start of the second row.
        Assert.Equal((0d, 164d), (panels[2].X!.Value, panels[2].Y!.Value));
        Assert.Equal((unplaced.X, unplaced.Y), (panels[2].X!.Value, panels[2].Y!.Value));
        Assert.False(arranger.IsSaving);
    }

    [Fact]
    public void APressWithNoMovement_SavesNothing()
    {
        var log = new LayoutLog();
        var arranger = Arranger(new MosaicsBackend { UpdateLayout = log.Write });
        arranger.Apply(Detail(Panels(2)));

        arranger.BeginDrag(arranger.Tiles[0], 5, 5);
        arranger.EndDrag(arranger.Tiles[0]);

        Assert.Empty(log.Writes);
    }

    [Fact]
    public async Task TheNotSavedList_NeverReachesTheRepository()
    {
        var log = new LayoutLog();
        var panels = Panels(2);
        var arranger = Arranger(new MosaicsBackend
        {
            UpdateLayout = log.Write,
            PanelFrames = _ => Frames(["Ha", "OIII"], "Ha"),
        });
        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);
        arranger.SetViewportSize(800, 600);

        arranger.Select(arranger.Tiles[0]);
        arranger.SelectedOpacity = 40;
        arranger.ZoomInCommand.Execute(null);
        arranger.ZoomOutCommand.Execute(null);
        arranger.ZoomAt(1, 100, 100);
        arranger.FitCommand.Execute(null);
        arranger.Pan(20, -10);
        arranger.ShowLabels = false;
        arranger.SelectedFilter = "OIII";
        arranger.ToggleSelect(arranger.Tiles[0]);
        arranger.Select(arranger.Tiles[1]);
        arranger.BeginDrag(arranger.Tiles[1], 0, 0);
        await arranger.PendingSave.WaitAsync(Budget);

        Assert.Empty(log.Writes);
    }

    [Fact]
    public async Task AFailedSave_ShowsTheSentence_AndTheNextSuccessClearsIt()
    {
        var log = new LayoutLog { Fail = true };
        var arranger = Arranger(new MosaicsBackend { UpdateLayout = log.Write });
        arranger.Apply(Detail(Panels(1)));

        arranger.Rotate(arranger.Tiles[0]);
        await arranger.PendingSave.WaitAsync(Budget);
        Assert.Equal(ArrangerViewModel.SaveFailedText, arranger.SaveError);
        Assert.Equal("The layout could not be saved.", arranger.SaveError);
        Assert.Equal(90, arranger.Tiles[0].Rotation);

        log.Fail = false;
        arranger.Flip(arranger.Tiles[0]);
        await arranger.PendingSave.WaitAsync(Budget);
        Assert.Null(arranger.SaveError);
        Assert.Equal(90, Assert.Single(log.Writes).Panels[0].Rotation);
    }

    [Fact]
    public async Task APendingWrite_DropsAPanelThatAReReadRemoved()
    {
        var log = new LayoutLog();
        var delay = new ManualDelay();
        var arranger = Arranger(new MosaicsBackend { UpdateLayout = log.Write }, delay: delay.Wait);
        var panels = Panels(3);
        arranger.Apply(Detail(panels));
        arranger.Flip(arranger.Tiles[0]);

        arranger.Apply(Detail([panels[0], panels[1]]));
        delay.Release();
        await arranger.PendingSave.WaitAsync(Budget);

        var write = Assert.Single(log.Writes);
        Assert.Equal(new[] { panels[0].Id, panels[1].Id }, write.Panels.Select(panel => panel.PanelId));
        Assert.True(write.Panels[0].FlipH);
    }

    [Fact]
    public async Task AFailedSave_KeepsTheReadersRotationAcrossAReRead()
    {
        var log = new LayoutLog { Fail = true };
        var arranger = Arranger(new MosaicsBackend { UpdateLayout = log.Write });
        var panels = Panels(1);
        arranger.Apply(Detail(panels, rotation: 10));

        arranger.GlobalRotation = 70;
        await arranger.PendingSave.WaitAsync(Budget);
        Assert.NotNull(arranger.SaveError);
        arranger.Apply(Detail(panels, rotation: 10));

        Assert.Equal(70, arranger.GlobalRotation);
    }

    [Fact]
    public void Disposing_WithASavePending_RunsIt()
    {
        var log = new LayoutLog();
        var delay = new ManualDelay();
        var arranger = Arranger(new MosaicsBackend { UpdateLayout = log.Write }, delay: delay.Wait);
        arranger.Apply(Detail(Panels(2)));
        arranger.Flip(arranger.Tiles[1]);
        Assert.Empty(log.Writes);

        arranger.Dispose();

        var write = Assert.Single(log.Writes);
        Assert.True(write.Panels[1].FlipH);
    }

    [Fact]
    public void Disposing_WithNothingPending_WritesNothing()
    {
        var log = new LayoutLog();
        var arranger = Arranger(new MosaicsBackend { UpdateLayout = log.Write });
        arranger.Apply(Detail(Panels(2)));

        arranger.Dispose();

        Assert.Empty(log.Writes);
    }

    // The real post queues onto the UI thread, which Dispose is blocking. A write that waited on
    // anything it posted would hold the close for the whole 2 second bound; here every post is
    // held in a queue drained only after Dispose returns.
    [Fact]
    public void TheCloseFlush_NeverWaitsOnTheUiThread()
    {
        var log = new LayoutLog();
        var delay = new ManualDelay();
        var queued = new List<Action>();
        var arranger = new ArrangerViewModel(
            MosaicId,
            new MosaicsBackend { UpdateLayout = log.Write },
            readOnly: false,
            post: action =>
            {
                lock (queued)
                {
                    queued.Add(action);
                }
            },
            delay: delay.Wait,
            NullLogger.Instance);
        _owned.Add(arranger);
        arranger.Apply(Detail(Panels(2)));
        arranger.Flip(arranger.Tiles[1]);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        arranger.Dispose();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Dispose took {clock.Elapsed}");
        var write = Assert.Single(log.Writes);
        Assert.True(write.Panels[1].FlipH);
        lock (queued)
        {
            queued.ForEach(action => action());
        }
    }

    // ---- zoom and pan ------------------------------------------------------------------------

    [Fact]
    public void TheZoomSteps_ClampAtTheRange()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(Panels(1)));
        arranger.SetViewportSize(400, 300);

        // Fit lands at min(320 / 250, 220 / 160) = 1.28, so the steps have room to climb.
        Assert.Equal(1.28, arranger.Zoom, 9);
        var climbed = new List<double>();
        for (var step = 0; step < 40; step++)
        {
            arranger.ZoomInCommand.Execute(null);
            climbed.Add(arranger.Zoom);
        }

        Assert.Equal(1.38, climbed[0], 9);
        // 1.28 plus 17 steps is 2.98; the 18th clamps to 3.0.
        Assert.Equal(17, climbed.Count(zoom => zoom < ArrangerViewModel.MaxZoom - 1e-9));

        Assert.Equal(ArrangerViewModel.MaxZoom, arranger.Zoom, 9);
        Assert.Equal("300%", arranger.ZoomText);
        var (x, y) = (arranger.OffsetX, arranger.OffsetY);
        arranger.ZoomInCommand.Execute(null);
        Assert.Equal((x, y), (arranger.OffsetX, arranger.OffsetY));

        for (var step = 0; step < 40; step++)
        {
            arranger.ZoomOutCommand.Execute(null);
        }

        Assert.Equal(ArrangerViewModel.MinZoom, arranger.Zoom, 9);
        Assert.Equal("10%", arranger.ZoomText);
    }

    [Fact]
    public void AWheelNotch_KeepsTheCanvasPointUnderThePointer()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(Panels(4)));
        arranger.SetViewportSize(1000, 600);
        var (zoom, ox, oy) = (arranger.Zoom, arranger.OffsetX, arranger.OffsetY);
        var under = arranger.ViewMatrix.Invert().Transform(new Point(300, 200));

        arranger.ZoomAt(120, 300, 200);

        var next = zoom + ArrangerViewModel.ZoomStep;
        Assert.Equal(next, arranger.Zoom, 9);
        Assert.Equal(300 - (300 - ox) * (next / zoom), arranger.OffsetX, 9);
        Assert.Equal(200 - (200 - oy) * (next / zoom), arranger.OffsetY, 9);
        var shown = arranger.ViewMatrix.Transform(under);
        Assert.Equal(300, shown.X, 6);
        Assert.Equal(200, shown.Y, 6);

        // A touchpad's fractional delta steps as a notch does; a zero delta does nothing.
        arranger.ZoomAt(-0.25, 300, 200);
        Assert.Equal(zoom, arranger.Zoom, 9);
        arranger.ZoomAt(0, 300, 200);
        Assert.Equal(zoom, arranger.Zoom, 9);
    }

    [Fact]
    public void Fit_ScalesTheBoundingBoxIntoTheViewportLessThePadding_AndCentresIt()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(Panels(4)));

        // No size yet: nothing fits.
        Assert.Equal(1, arranger.Zoom);
        arranger.SetViewportSize(1000, 600);

        // Four tiles in two columns: a box 504 by 324, centred on (252, 162).
        var scale = Math.Min((1000 - 80) / 504d, (600 - 80) / 324d);
        Assert.Equal(scale, arranger.Zoom, 9);
        Assert.Equal(500 - 252 * scale, arranger.OffsetX, 9);
        Assert.Equal(300 - 162 * scale, arranger.OffsetY, 9);
        Assert.Equal(new Matrix(scale, 0, 0, scale, 500 - 252 * scale, 300 - 162 * scale), arranger.ViewMatrix);
        Assert.Equal((252d, 162d), (arranger.RotationCentreX, arranger.RotationCentreY));

        // Fit runs once by itself; afterwards only the command refits.
        arranger.Pan(30, 40);
        arranger.SetViewportSize(1200, 700);
        Assert.Equal(500 - 252 * scale + 30, arranger.OffsetX, 9);
        arranger.FitCommand.Execute(null);
        Assert.Equal(600 - 252 * arranger.Zoom, arranger.OffsetX, 9);
    }

    [Fact]
    public void Pan_MovesTheOffsets()
    {
        var arranger = Arranger();
        arranger.Pan(12, -7);
        arranger.Pan(3, 2);

        Assert.Equal((15d, -5d), (arranger.OffsetX, arranger.OffsetY));
    }

    // ---- auto layout and re-reads ------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public void TheAutoLayout_FillsANearSquareGrid(int count, int columns)
    {
        var arranger = Arranger();
        arranger.Apply(Detail(Panels(count)));

        var expected = Enumerable.Range(0, count).Select(index => ((index % columns) * 254d, (index / columns) * 164d));
        Assert.Equal(expected, arranger.Tiles.Select(tile => (tile.X, tile.Y)));
        Assert.All(arranger.Tiles, tile => Assert.False(tile.IsPlaced));
    }

    [Fact]
    public void AStoredPosition_IsUsed_AndOnlyTheUnplacedTilesTakeTheGrid()
    {
        var arranger = Arranger();
        arranger.Apply(Detail([Panel("Panel 1", 0, x: 900, y: 40), Panel("Panel 2", 1), Panel("Panel 3", 2)]));

        Assert.Equal((900d, 40d), (arranger.Tiles[0].X, arranger.Tiles[0].Y));
        Assert.True(arranger.Tiles[0].IsPlaced);
        Assert.Equal((0d, 0d), (arranger.Tiles[1].X, arranger.Tiles[1].Y));
        Assert.Equal((254d, 0d), (arranger.Tiles[2].X, arranger.Tiles[2].Y));
    }

    [Fact]
    public void AReRead_KeepsADraggedTile_TakesTheNewFigures_AndClearsTheSelectionWhenItsPanelGoes()
    {
        var arranger = Arranger(delay: new ManualDelay().Wait);
        var panels = Panels(3);
        arranger.Apply(Detail(panels));
        var (first, second, third) = (arranger.Tiles[0], arranger.Tiles[1], arranger.Tiles[2]);
        arranger.BeginDrag(first, 0, 0);
        arranger.Drag(first, 700, 300);
        arranger.Rotate(first);
        arranger.Select(third);

        arranger.Apply(Detail([panels[0] with { Label = "P1", IntegrationSeconds = 1200 }, panels[1]]));

        Assert.Equal(new[] { first, second }, arranger.Tiles);
        Assert.Equal((700d, 300d, 90), (first.X, first.Y, first.Rotation));
        Assert.Equal("P1", first.Label);
        Assert.Equal(MetricText.Integration(1200), first.IntegrationText);
        Assert.Null(arranger.Selected);
        Assert.False(arranger.HasSelection);
    }

    [Fact]
    public void ANewPanel_AppearsUnplaced_AndTheUnplacedTilesReflow()
    {
        var arranger = Arranger();
        var panels = Panels(4);
        arranger.Apply(Detail(panels));

        arranger.Apply(Detail([.. panels, Panel("Panel 5", 4)]));

        Assert.Equal(5, arranger.Tiles.Count);
        Assert.Equal((508d, 0d), (arranger.Tiles[2].X, arranger.Tiles[2].Y));
        Assert.Equal((254d, 164d), (arranger.Tiles[4].X, arranger.Tiles[4].Y));
    }

    [Fact]
    public void TheGlobalRotation_IsTakenFromTheDetail_UnlessASaveIsPending()
    {
        var arranger = Arranger(delay: new ManualDelay().Wait);
        var panels = Panels(2);
        arranger.Apply(Detail(panels, rotation: 45));
        Assert.Equal(45, arranger.GlobalRotation);
        Assert.Equal("45°", arranger.RotationText);

        arranger.GlobalRotation = -20;
        arranger.Apply(Detail(panels, rotation: 45));

        Assert.Equal(-20, arranger.GlobalRotation);
    }

    // ---- selection and opacity ----------------------------------------------------------------

    [Fact]
    public async Task TheOpacity_AppliesToTheSelectedTile_AndIsKeptForTheNext()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(Panels(2)));
        await arranger.PendingFrames.WaitAsync(Budget);
        var (one, two) = (arranger.Tiles[0], arranger.Tiles[1]);
        Assert.Equal(100, arranger.SelectedOpacity);
        Assert.True(arranger.ShowLabels);

        arranger.Select(one);
        arranger.SelectedOpacity = 40;
        Assert.Equal(0.4, one.Opacity, 9);
        Assert.Equal("40%", arranger.OpacityText);
        // A mosaic with no frame at all: every tile is the empty tile, and the names combine.
        Assert.Equal("Panel 1, selected, no thumbnail", one.AutomationName);

        arranger.Select(two);
        Assert.Equal(1, one.Opacity);
        Assert.False(one.IsSelected);
        Assert.Equal(0.4, two.Opacity, 9);

        arranger.ToggleSelect(two);
        Assert.Null(arranger.Selected);
        Assert.Equal(1, two.Opacity);
        Assert.Equal("Panel 2, no thumbnail", two.AutomationName);
    }

    [Fact]
    public void BeginDrag_RaisesTheTileToTheTopOfTheStackingOrder()
    {
        var arranger = Arranger();
        arranger.Apply(Detail(Panels(3)));

        arranger.BeginDrag(arranger.Tiles[0], 0, 0);
        arranger.EndDrag(arranger.Tiles[0]);
        arranger.BeginDrag(arranger.Tiles[1], 0, 0);

        Assert.True(arranger.Tiles[1].ZIndex > arranger.Tiles[0].ZIndex);
        Assert.True(arranger.Tiles[0].ZIndex > arranger.Tiles[2].ZIndex);
        Assert.Equal(new[] { "Panel 1", "Panel 2", "Panel 3" }, arranger.Tiles.Select(tile => tile.Label));
    }

    // ---- the filter selector and the thumbnails -----------------------------------------------

    [Fact]
    public async Task TheFilterSelector_StartsAtTheDefault_SurvivesAReRead_AndFallsBack()
    {
        var panels = Panels(2);
        var frames = Frames(["Ha", "OIII", "SII"], "ha");
        var arranger = Arranger(new MosaicsBackend { PanelFrames = _ => frames });
        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);

        Assert.Equal(new[] { "Ha", "OIII", "SII" }, arranger.Filters);
        Assert.Equal("Ha", arranger.SelectedFilter);
        Assert.True(arranger.HasFilters);

        arranger.SelectedFilter = "SII";
        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);
        Assert.Equal("SII", arranger.SelectedFilter);

        frames = Frames(["Ha", "OIII"], "OIII");
        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);
        Assert.Equal("OIII", arranger.SelectedFilter);
    }

    // The Phase 15B defect: a ComboBox writes null back through SelectedItem while its items
    // change. The still-available choice survives and no tile loses its slot.
    [Fact]
    public async Task ANullWriteBack_WhileTheFiltersChange_KeepsTheChoiceAndTheSlots()
    {
        var panels = Panels(2);
        var frames = Frames(["Ha", "OIII"], "Ha", (panels[0].Id, "OIII", "a.fits"), (panels[1].Id, "OIII", "b.fits"));
        var arranger = Arranger(new MosaicsBackend { PanelFrames = _ => frames, ThumbnailFor = Slot });
        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);
        arranger.SelectedFilter = "OIII";
        var slots = arranger.Tiles.Select(tile => tile.Thumbnail).ToList();
        Assert.All(slots, Assert.NotNull);
        arranger.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArrangerViewModel.Filters))
            {
                arranger.SelectedFilter = null;
            }
        };

        frames = Frames(["OIII", "Ha", "SII"], "Ha", (panels[0].Id, "OIII", "a.fits"), (panels[1].Id, "OIII", "b.fits"));
        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);

        Assert.Equal(new[] { "OIII", "Ha", "SII" }, arranger.Filters);
        Assert.Equal("OIII", arranger.SelectedFilter);
        Assert.Equal(slots, arranger.Tiles.Select(tile => tile.Thumbnail));

        // A null write outside a publish is refused too while a filter is available.
        arranger.SelectedFilter = null;
        Assert.Equal("OIII", arranger.SelectedFilter);
        Assert.Equal(slots, arranger.Tiles.Select(tile => tile.Thumbnail));
    }

    [Fact]
    public async Task AFilterChange_RePointsEveryTile_AndIsLoadingHoldsUntilTheSlotsSettle()
    {
        var panels = Panels(3);
        var frames = Frames(
            ["Ha", "OIII"], "Ha",
            (panels[0].Id, "Ha", "a-ha.fits"), (panels[1].Id, "Ha", "b-ha.fits"),
            (panels[0].Id, "OIII", "a-oiii.fits"), (panels[1].Id, "OIII", "b-oiii.fits"), (panels[2].Id, "OIII", "c-oiii.fits"));
        var built = new List<string>();
        var arranger = Arranger(new MosaicsBackend
        {
            PanelFrames = _ => frames,
            ThumbnailFor = path =>
            {
                built.Add(path);
                return Slot(path);
            },
        });
        _renders.Reset();
        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);

        Assert.Equal(new[] { "a-ha.fits", "b-ha.fits" }, built);
        Assert.True(arranger.IsLoading);
        var third = arranger.Tiles[2];
        Assert.Null(third.Thumbnail);
        Assert.True(third.IsEmpty);
        Assert.Equal("No Ha frames", third.EmptyText);
        Assert.Equal("Panel 3, no frames in Ha", third.AutomationName);
        var before = arranger.Tiles[0].Thumbnail!;

        arranger.SelectedFilter = "OIII";

        Assert.Equal(new[] { "a-ha.fits", "b-ha.fits", "a-oiii.fits", "b-oiii.fits", "c-oiii.fits" }, built);
        Assert.NotSame(before, arranger.Tiles[0].Thumbnail);
        Assert.False(before.IsLoading);
        Assert.True(arranger.IsLoading);

        _renders.Set();
        foreach (var tile in arranger.Tiles)
        {
            await tile.Thumbnail!.PendingLoad!.WaitAsync(Budget);
        }

        Assert.False(arranger.IsLoading);
        Assert.All(arranger.Tiles, tile =>
        {
            Assert.True(tile.IsEmpty);
            Assert.False(tile.HasImage);
            Assert.Equal("No thumbnail", tile.EmptyText);
        });
        Assert.Equal("Panel 1, no thumbnail", arranger.Tiles[0].AutomationName);
    }

    [Fact]
    public async Task AReRead_KeepsATileThumbnail_WhenItsBestFrameIsUnchanged()
    {
        var panels = Panels(1);
        var frames = Frames(["Ha"], "Ha", (panels[0].Id, "Ha", "a.fits"));
        var arranger = Arranger(new MosaicsBackend { PanelFrames = _ => frames, ThumbnailFor = Slot });
        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);
        var slot = arranger.Tiles[0].Thumbnail;

        arranger.Apply(Detail(panels));
        await arranger.PendingFrames.WaitAsync(Budget);

        Assert.Same(slot, arranger.Tiles[0].Thumbnail);
    }

    [Fact]
    public async Task AMosaicWithNoPanel_IsEmpty_AndHidesTheFilter()
    {
        var arranger = Arranger();
        arranger.Apply(Detail([]));
        await arranger.PendingFrames.WaitAsync(Budget);
        arranger.SetViewportSize(800, 600);

        Assert.True(arranger.IsEmpty);
        Assert.False(arranger.HasFilters);
        Assert.Equal(1, arranger.Zoom);
    }

    [Fact]
    public async Task IsLoading_HoldsWhileTheFrameSetReadIsInFlight()
    {
        using var gate = new ManualResetEventSlim();
        var arranger = Arranger(new MosaicsBackend
        {
            PanelFrames = _ =>
            {
                gate.Wait(Budget);
                return Frames(["Ha"], "Ha");
            },
        });
        arranger.Apply(Detail(Panels(2)));

        Assert.True(arranger.IsLoading);

        gate.Set();
        await arranger.PendingFrames.WaitAsync(Budget);
        Assert.False(arranger.IsLoading);
        Assert.All(arranger.Tiles, tile => Assert.Equal("No Ha frames", tile.EmptyText));
    }

    [Fact]
    public async Task AFailedFrameSetRead_ResolvesEveryTileToNoThumbnail()
    {
        var arranger = Arranger(new MosaicsBackend { PanelFrames = _ => throw new InvalidOperationException("locked") });
        arranger.Apply(Detail(Panels(2)));
        await arranger.PendingFrames.WaitAsync(Budget);

        Assert.False(arranger.IsLoading);
        Assert.All(arranger.Tiles, tile =>
        {
            Assert.True(tile.IsEmpty);
            Assert.Equal("No thumbnail", tile.EmptyText);
        });
    }

    // ---- the read-only preview ---------------------------------------------------------------

    [Fact]
    public async Task ThePreview_LaysOutTheGivenTiles_WithNoBadge_AndSavesNothing()
    {
        var log = new LayoutLog();
        var arranger = Arranger(new MosaicsBackend { UpdateLayout = log.Write, ThumbnailFor = Slot }, readOnly: true);
        arranger.SetViewportSize(600, 200);

        arranger.ApplyPreview(
        [
            new PreviewTile("Panel 1", 3600, () => new BestFrame(Guid.NewGuid(), "a.fits", "Ha", 1)),
            new PreviewTile("Panel 2", 600, () => null),
        ]);
        await arranger.PendingFrames.WaitAsync(Budget);

        Assert.True(arranger.IsReadOnly);
        Assert.Equal(new[] { (0d, 0d), (254d, 0d) }, arranger.Tiles.Select(tile => (tile.X, tile.Y)));
        Assert.All(arranger.Tiles, tile => Assert.Equal(DeficitBand.None, tile.Deficit));
        Assert.Equal(MetricText.Integration(600), arranger.Tiles[1].IntegrationText);
        Assert.NotNull(arranger.Tiles[0].Thumbnail);
        Assert.Null(arranger.Tiles[1].Thumbnail);
        var fitted = arranger.Zoom;
        Assert.Equal(Math.Min((600 - 80) / 504d, (200 - 80) / 160d), fitted, 9);

        arranger.Rotate(arranger.Tiles[0]);
        arranger.BeginDrag(arranger.Tiles[0], 0, 0);
        arranger.Drag(arranger.Tiles[0], 50, 50);
        arranger.EndDrag(arranger.Tiles[0]);
        arranger.Dispose();
        Assert.Empty(log.Writes);
    }

    [Fact]
    public void ThePreview_RefitsOnEveryChange_AndShowsNothingWithNoLabel()
    {
        var arranger = Arranger(readOnly: true);
        arranger.SetViewportSize(600, 400);
        arranger.ApplyPreview([new PreviewTile("Panel 1", 60, () => null)]);
        var one = arranger.Zoom;

        arranger.ApplyPreview([new PreviewTile("Panel 1", 60, () => null), new PreviewTile("Panel 2", 60, () => null)]);
        Assert.NotEqual(one, arranger.Zoom);

        arranger.ApplyPreview([]);
        Assert.True(arranger.IsEmpty);
        Assert.Empty(arranger.Tiles);
    }
}
