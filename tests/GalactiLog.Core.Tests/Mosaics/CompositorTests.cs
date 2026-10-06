using GalactiLog.Core.Mosaics;
using GalactiLog.Core.Tests.Fixtures;
using SkiaSharp;
using Xunit;

namespace GalactiLog.Core.Tests.Mosaics;

/// <summary>
/// Spec 11.6's drawing, over a generated 2 by 2 mosaic that mirrors the Data tests'
/// <c>MosaicFixtureLibrary</c>: 64 by 64 FITS frames carrying its gradient (1000 plus 40 per column
/// plus 10 per row), one field apart, written to this class's own temp directory. Every assertion
/// on the image is a pixel probe on the decoded JPEG, never a committed image.
/// </summary>
public sealed class CompositorTests : IDisposable
{
    private const int Width = 64;
    private const double Ra = 314.75;
    private const double Dec = 44.3;

    // The library's plate scale (206.265 * XPIXSZ 4 / FOCALLEN 55) and its 2 by 2 step: one field
    // (64 pixels of arc) in declination, and the same arc in RA divided by cos(dec).
    private static readonly double Scale = 206.265 * 4.0 / 55.0;
    private static readonly double Step = Width * Scale / 3600;
    private static readonly double StepRa = Step / Math.Cos(Dec * Math.PI / 180);

    private readonly string _dir = Directory.CreateTempSubdirectory("galactilog-composite-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A handle a failed test left open is not worth failing the run over.
        }
    }

    [Fact]
    public void FourTiles_LandOnA128Canvas_InTheCameraOrientation()
    {
        var (selection, frames) = Grid();

        var result = Compositor.Build(selection, frames, null, CancellationToken.None);
        var layout = Layout(selection);

        Assert.InRange(result.Width, 126, 130);
        Assert.InRange(result.Height, 126, 130);
        Assert.Equal((layout.Width, layout.Height), (result.Width, result.Height));
        Assert.Equal(1, layout.OutputScale);

        // The camera's view (11.6, R9): Panel 2 (higher RA) lands right of Panel 1, Panel 3
        // (higher Dec) lands below it.
        var t = layout.Tiles;
        Assert.True(t[1].CentreX > t[0].CentreX + 60 && Math.Abs(t[1].CentreY - t[0].CentreY) < 2);
        Assert.True(t[2].CentreY > t[0].CentreY + 60 && Math.Abs(t[2].CentreX - t[0].CentreX) < 2);
        Assert.True(t[3].CentreX > t[2].CentreX + 60 && t[3].CentreY > t[1].CentreY + 60);

        using var image = SKBitmap.Decode(result.Jpeg);
        Assert.Equal((result.Width, result.Height), (image.Width, image.Height));
        Assert.All(t, tile => Assert.True(Probe(image, tile.CentreX, tile.CentreY) > 0, "a tile centre is black"));

        // The same orientation everywhere: the gradient's brightest corner (top right once the
        // FITS rows are flipped) is the brightest corner of every tile region.
        Assert.All(t, tile => Assert.Equal(Corner.TopRight, BrightestCorner(image, tile.CentreX, tile.CentreY)));
    }

    [Fact]
    public void RotatedTiles_TurnClockwise_AndTheProbesFindEachPanelWhereTheSkyPutsIt()
    {
        // Panel 2 turned a quarter, Panel 3 a half: each is now recognisable by its corner, so a
        // probe at a fixed canvas quadrant (not at a placement) says which panel landed there.
        var (selection, frames) = Grid(panel2Rotator: 90, panel3Rotator: 180);

        var result = Compositor.Build(selection, frames, null, CancellationToken.None);
        var layout = Layout(selection);

        Assert.Equal([0.0, 90.0, -180.0, 0.0], layout.Tiles.Select(tile => tile.Rotation));
        using var image = SKBitmap.Decode(result.Jpeg);
        double left = result.Width / 4.0, right = result.Width * 3 / 4.0, top = result.Height / 4.0, bottom = result.Height * 3 / 4.0;
        Assert.Equal(Corner.TopRight, BrightestCorner(image, left, top));
        Assert.Equal(Corner.BottomRight, BrightestCorner(image, right, top));
        Assert.Equal(Corner.BottomLeft, BrightestCorner(image, left, bottom));
        Assert.Equal(Corner.TopRight, BrightestCorner(image, right, bottom));
    }

    [Fact]
    public void ASkippedFrame_FailsTheBuild_WithTheLabelAndTheReason()
    {
        var (selection, frames) = Grid();
        var broken = Path.Combine(_dir, "naxis1.fits");
        using (var stream = new FitsBuilder()
                   .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 1L).Card("NAXIS1", 10L)
                   .EndCard()
                   .Pixels(16, naxis1: 10, naxis2: 1, new float[1, 10])
                   .Build())
        {
            File.WriteAllBytes(broken, stream.ToArray());
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => Compositor.Build(selection, [frames[0], frames[1], broken, frames[3]], null, CancellationToken.None));

        Assert.StartsWith("Panel 3: ", ex.Message);
        Assert.Contains("unsupported NAXIS shape", ex.Message);
    }

    [Fact]
    public void CancellingAfterTheFirstTile_ThrowsBeforeTheSecond()
    {
        var (selection, frames) = Grid();
        using var cts = new CancellationTokenSource();
        var heard = new List<string>();

        // The cancel lands after the first tile rendered (the build checks the token between tiles).
        Assert.ThrowsAny<OperationCanceledException>(() => Compositor.Build(selection, frames, label =>
        {
            heard.Add(label);
            if (heard.Count == 2)
            {
                cts.Cancel();
            }
        }, cts.Token));

        Assert.Equal(["Panel 1", "Panel 2"], heard);
    }

    [Fact]
    public void Progress_HearsEveryIncludedLabel_InOrder()
    {
        var (selection, frames) = Grid();
        var heard = new List<string>();

        Compositor.Build(selection, frames, heard.Add, CancellationToken.None);

        Assert.Equal(["Panel 1", "Panel 2", "Panel 3", "Panel 4"], heard);
    }

    private enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

    private static CompositeLayoutResult Layout(CompositeSelection selection)
        => CompositeLayout.Arrange(selection, [.. Enumerable.Repeat(new TileSize(Width, Width), selection.Included.Count)]);

    // The four corners of the 64 pixel region centred at (x, y), each a 3 by 3 mean inset 6
    // pixels, so neither the JPEG edge nor a neighbour's seam reaches the probe.
    private static Corner BrightestCorner(SKBitmap image, double x, double y)
    {
        var half = Width / 2.0 - 6;
        (Corner Corner, double Value)[] probes =
        [
            (Corner.TopLeft, Probe(image, x - half, y - half)),
            (Corner.TopRight, Probe(image, x + half, y - half)),
            (Corner.BottomRight, Probe(image, x + half, y + half)),
            (Corner.BottomLeft, Probe(image, x - half, y + half)),
        ];
        return probes.MaxBy(probe => probe.Value).Corner;
    }

    private static double Probe(SKBitmap image, double x, double y)
    {
        var (cx, cy) = ((int)Math.Round(x), (int)Math.Round(y));
        var sum = 0.0;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                sum += image.GetPixel(cx + dx, cy + dy).Red;
            }
        }
        return sum / 9;
    }

    private (CompositeSelection Selection, IReadOnlyList<string> Frames) Grid(double panel2Rotator = 0, double panel3Rotator = 0)
    {
        (double Ra, double Dec, double Rotator)[] positions =
        [
            (Ra, Dec, 0), (Ra + StepRa, Dec, panel2Rotator), (Ra, Dec + Step, panel3Rotator), (Ra + StepRa, Dec + Step, 0),
        ];
        var panels = positions.Select((p, i) => new CompositePanel(
            Guid.NewGuid(), $"Panel {i + 1}", new PanelGeometry(p.Ra, p.Dec, Width, Scale, p.Rotator, "West"))).ToList();
        var frames = panels.Select((p, i) => WriteFrame($"panel{i + 1}.fits", positions[i].Ra, positions[i].Dec)).ToList();
        return (CompositeLayout.Select(panels), frames);
    }

    private string WriteFrame(string name, double ra, double dec)
    {
        var values = new float[Width, Width];
        for (var row = 0; row < Width; row++)
        {
            for (var col = 0; col < Width; col++)
            {
                values[row, col] = 1000 + 40 * col + 10 * row;
            }
        }

        var target = Path.Combine(_dir, name);
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 2L)
            .Card("NAXIS1", (long)Width)
            .Card("NAXIS2", (long)Width)
            .Card("IMAGETYP", "LIGHT")
            .Card("RA", Math.Round(ra, 6))
            .Card("DEC", Math.Round(dec, 6))
            .Card("FOCALLEN", 55.0)
            .Card("XPIXSZ", 4.0)
            .Pixels(16, Width, Width, values)
            .EndCard()
            .Build();
        File.WriteAllBytes(target, stream.ToArray());
        return target;
    }
}
