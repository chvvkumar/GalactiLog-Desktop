using GalactiLog.Core.Mosaics;
using Xunit;

namespace GalactiLog.Core.Tests.Mosaics;

// Phase 19B Task 2. Spec 11.6's inclusion rules, canvas, tile scale and output cap.
public class CompositeLayoutTests
{
    private const double Tolerance = 1e-6;

    // Degrees between two panels at RA -delta and +delta on the equator, centred on RA 0.
    private static double Separation(double delta) => 2 * Math.Tan(delta * Math.PI / 180) * 180 / Math.PI;

    // The arcsec_per_pixel that puts two panels at RA -delta and +delta exactly `pixels` canvas
    // pixels apart when width_px equals the tile width.
    private static double ScaleFor(double delta, double pixels) => Separation(delta) / pixels * 3600;

    private static CompositePanel Panel(string label, double? ra, double? dec, int? width = 100, double? arcsec = 1.0,
        double? rotator = null, string? pier = null) =>
        new(Guid.NewGuid(), label, new PanelGeometry(ra, dec, width, arcsec, rotator, pier));

    private static CompositePanel NoFrame(string label) => new(Guid.NewGuid(), label, null);

    private static CompositeLayoutResult Arrange(IReadOnlyList<CompositePanel> panels, params TileSize[] tiles) =>
        CompositeLayout.Arrange(CompositeLayout.Select(panels), tiles);

    [Fact]
    public void SinglePanel_CanvasIsItsTile_CentredAtHalfSize()
    {
        var panel = Panel("P1", 83.8, -5.4, width: 6000, arcsec: 1.2);

        var result = Arrange([panel], new TileSize(1600, 1000));

        Assert.Equal(1600, result.Width);
        Assert.Equal(1000, result.Height);
        Assert.Equal(1.0, result.OutputScale);
        var tile = Assert.Single(result.Tiles);
        Assert.Equal(panel.PanelId, tile.PanelId);
        Assert.Equal(800, tile.CentreX, Tolerance);
        Assert.Equal(500, tile.CentreY, Tolerance);
        Assert.Equal((1600, 1000), (tile.Width, tile.Height));
        Assert.Equal(0, tile.Rotation);
    }

    [Fact]
    public void TwoUnrotatedTiles_OneWidthApart_GiveA200By80Canvas()
    {
        var scale = ScaleFor(0.5, 100);
        var result = Arrange([Panel("P1", 359.5, 0, arcsec: scale), Panel("P2", 0.5, 0, arcsec: scale)],
            new TileSize(100, 80), new TileSize(100, 80));

        Assert.Equal((200, 80), (result.Width, result.Height));
        Assert.Equal(50, result.Tiles[0].CentreX, Tolerance);
        Assert.Equal(150, result.Tiles[1].CentreX, Tolerance);
        Assert.Equal(40, result.Tiles[0].CentreY, Tolerance);
        Assert.Equal(40, result.Tiles[1].CentreY, Tolerance);
    }

    // A panel east of the centre lands to the right at t = 0 (spec 11.6, tile centre).
    [Fact]
    public void RaWrapAcrossZero_CentresBetweenThePanels()
    {
        var scale = ScaleFor(1, 100);
        var result = Arrange([Panel("P1", 359, 0, arcsec: scale), Panel("P2", 1, 0, arcsec: scale)],
            new TileSize(100, 80), new TileSize(100, 80));

        Assert.Equal(200, result.Width);
        Assert.Equal(50, result.Tiles[0].CentreX, Tolerance);
        Assert.Equal(150, result.Tiles[1].CentreX, Tolerance);
    }

    [Fact]
    public void TileTurned90_SwapsItsExtents()
    {
        var result = Arrange([Panel("P1", 10, 20, rotator: 0), Panel("P2", 10, 20, rotator: 90)],
            new TileSize(100, 80), new TileSize(100, 20));

        Assert.Equal((100, 100), (result.Width, result.Height));
        Assert.Equal(90, result.Tiles[1].Rotation);
        Assert.Equal(50, result.Tiles[1].CentreX, Tolerance);
        Assert.Equal(50, result.Tiles[1].CentreY, Tolerance);
    }

    [Fact]
    public void TileTurned45_HasTheFormulaHalfExtents()
    {
        var result = Arrange([Panel("P1", 10, 20, rotator: 0), Panel("P2", 10, 20, rotator: 45)],
            new TileSize(100, 80), new TileSize(100, 80));

        var half = (100 + 80) * Math.Sqrt(0.5) / 2; // hw = hh = (w + h) |cos 45| / 2
        Assert.Equal((128, 128), (result.Width, result.Height));
        Assert.Equal(half, result.Tiles[1].CentreX, Tolerance);
        Assert.Equal(half, result.Tiles[1].CentreY, Tolerance);
    }

    [Fact]
    public void PierFlip_TurnsTheTileHalfWay()
    {
        var result = Arrange([Panel("P1", 10, 20, pier: "West"), Panel("P2", 10, 20, pier: "east")],
            new TileSize(100, 80), new TileSize(100, 80));

        Assert.Equal(0, result.Tiles[0].Rotation);
        Assert.Equal(-180, result.Tiles[1].Rotation);
    }

    // Spec 11.6's own example: a 1,600 by 1,000 tile at (0, 0) and a second at (2000, 0) turned 90.
    [Fact]
    public void SpecCanvasExample_Gives3300By1600()
    {
        var scale = ScaleFor(0.5, 2000);
        var result = Arrange([Panel("P1", 359.5, 0, width: 1600, arcsec: scale, rotator: 0),
                Panel("P2", 0.5, 0, width: 1600, arcsec: scale, rotator: 90)],
            new TileSize(1600, 1000), new TileSize(1600, 1000));

        Assert.Equal((3300, 1600), (result.Width, result.Height));
        Assert.Equal(800, result.Tiles[0].CentreX, Tolerance);
        Assert.Equal(800, result.Tiles[0].CentreY, Tolerance);
        Assert.Equal(2800, result.Tiles[1].CentreX, Tolerance);
        Assert.Equal(800, result.Tiles[1].CentreY, Tolerance);
    }

    // Spec 11.6's cap example, with tile heights of 1,001 so the shorter side and the tile
    // heights land on a half and round to the nearest whole pixel, halves away from zero.
    [Fact]
    public void OutputCap_HalvesA12000Layout()
    {
        var scale = ScaleFor(1, 10400);
        var result = Arrange([Panel("P1", 359, 0, width: 1600, arcsec: scale), Panel("P2", 1, 0, width: 1600, arcsec: scale)],
            new TileSize(1600, 1001), new TileSize(1600, 1001));

        Assert.Equal(0.5, result.OutputScale, 1e-12);
        Assert.Equal((6000, 501), (result.Width, result.Height));
        Assert.Equal((800, 501), (result.Tiles[0].Width, result.Tiles[0].Height));
        Assert.Equal((800, 501), (result.Tiles[1].Width, result.Tiles[1].Height));
        Assert.Equal(400, result.Tiles[0].CentreX, Tolerance);
        Assert.Equal(5600, result.Tiles[1].CentreX, Tolerance);
        Assert.Equal(250.25, result.Tiles[0].CentreY, Tolerance);
    }

    [Fact]
    public void OutputCap_ExactlyAt6000_IsNotCapped()
    {
        var scale = ScaleFor(1, 4400);
        var result = Arrange([Panel("P1", 359, 0, width: 1600, arcsec: scale), Panel("P2", 1, 0, width: 1600, arcsec: scale)],
            new TileSize(1600, 1000), new TileSize(1600, 1000));

        Assert.Equal(1.0, result.OutputScale);
        Assert.Equal((6000, 1000), (result.Width, result.Height));
        Assert.Equal((1600, 1000), (result.Tiles[1].Width, result.Tiles[1].Height));
    }

    // A panel binned 2x: half the width_px, twice the arcsec_per_pixel, a tile half as wide.
    [Fact]
    public void MixedBinning_LandsAtTheSameOffset_AndDrawsAtTwice()
    {
        var reference = Panel("P1", 359.9, 0, width: 4000, arcsec: 1.0);
        var same = Panel("P2", 0.1, 0, width: 4000, arcsec: 1.0);
        var binned = Panel("P2", 0.1, 0, width: 2000, arcsec: 2.0);

        var sameRig = Arrange([reference, same], new TileSize(1600, 1000), new TileSize(1600, 1000));
        var mixed = Arrange([reference, binned], new TileSize(1600, 1000), new TileSize(800, 500));

        Assert.Equal(sameRig.Tiles[1].CentreX, mixed.Tiles[1].CentreX, Tolerance);
        Assert.Equal(sameRig.Tiles[1].CentreY, mixed.Tiles[1].CentreY, Tolerance);
        Assert.Equal((1600, 1000), (mixed.Tiles[1].Width, mixed.Tiles[1].Height));
        Assert.Equal((sameRig.Width, sameRig.Height), (mixed.Width, mixed.Height));
    }

    // g that is not whole: the drawn dimension rounds to the nearest pixel, at least 1.
    [Fact]
    public void DrawnDimension_RoundsToAWholePixel_AtLeastOne()
    {
        var result = Arrange([Panel("P1", 10, 20, width: 300, arcsec: 1.0), Panel("P2", 10, 20, width: 1, arcsec: 1.0)],
            new TileSize(300, 300), new TileSize(10, 10));

        // g = (1 * 1 / 10) / (1 * 300 / 300) = 0.1, so the second tile draws 1 by 1, not 0.
        Assert.Equal((1, 1), (result.Tiles[1].Width, result.Tiles[1].Height));

        var third = Arrange([Panel("P1", 10, 20, width: 300, arcsec: 1.0), Panel("P2", 10, 20, width: 300, arcsec: 1.0)],
            new TileSize(300, 300), new TileSize(200, 201));
        // g = 300 / 200 = 1.5: 200 * 1.5 = 300, 201 * 1.5 = 301.5 rounds to 302.
        Assert.Equal((300, 302), (third.Tiles[1].Width, third.Tiles[1].Height));
    }

    [Fact]
    public void LeftOut_InSortOrder_WithEachReason_AndTheyDoNotMoveTheCentre()
    {
        var noFrame = NoFrame("P1");
        var a = Panel("P2", 10, 20);
        var noDec = Panel("P3", 40, null);
        var noWidth = Panel("P4", 50, 30, width: null);
        var b = Panel("P5", 10.05, 20.05);

        var selection = CompositeLayout.Select([noFrame, a, noDec, noWidth, b]);

        Assert.True(selection.IsPossible);
        Assert.Equal(new[] { a, b }, selection.Included);
        Assert.Equal(
            new[] { (noFrame.PanelId, "P1", LeftOutReason.NoFrames), (noDec.PanelId, "P3", LeftOutReason.NoPosition), (noWidth.PanelId, "P4", LeftOutReason.NoPosition) },
            selection.LeftOut.Select(l => (l.PanelId, l.Label, l.Reason)));

        var tiles = new[] { new TileSize(100, 80), new TileSize(100, 80) };
        var withLeftOut = CompositeLayout.Arrange(selection, tiles);
        var alone = Arrange([a, b], tiles);
        Assert.Equal(alone.Tiles, withLeftOut.Tiles);
        Assert.Equal((alone.Width, alone.Height), (withLeftOut.Width, withLeftOut.Height));
    }

    [Fact]
    public void PlateScale_ComesFromTheFirstPanelCarryingOne_IncludedOrNot()
    {
        var selection = CompositeLayout.Select([
            Panel("P1", 10, 20, arcsec: null),
            Panel("P2", 10, null, arcsec: 3.5),
            Panel("P3", 10, 20, arcsec: 0),
            Panel("P4", 10, 20, arcsec: 1.2),
        ]);

        Assert.True(selection.IsPossible);
        Assert.Equal(3.5, selection.PlateScale);
        Assert.Equal(3, selection.Included.Count);
    }

    // Included panels without their own scale draw at the shared one: same rig, same tile, g = 1.
    [Fact]
    public void PanelWithoutItsOwnScale_UsesTheSharedPlateScale()
    {
        var result = Arrange([Panel("P0", 10, null, arcsec: 2.0), Panel("P1", 10, 20, arcsec: null), Panel("P2", 10, 20, arcsec: null)],
            new TileSize(100, 80), new TileSize(100, 80));

        Assert.Equal((100, 80), (result.Width, result.Height));
        Assert.Equal((100, 80), (result.Tiles[1].Width, result.Tiles[1].Height));
    }

    [Fact]
    public void NotPossible_NoPanelIncluded()
    {
        var selection = CompositeLayout.Select([NoFrame("P1"), Panel("P2", null, 20, arcsec: 1.0)]);

        Assert.False(selection.IsPossible);
        Assert.Equal(CompositeBlock.NoPositionedPanel, selection.Block);
        Assert.Throws<InvalidOperationException>(() => CompositeLayout.Arrange(selection, []));
    }

    [Fact]
    public void NotPossible_NoPlateScaleAnywhere_EvenWithPositionedFrames()
    {
        var selection = CompositeLayout.Select([Panel("P1", 10, 20, arcsec: null), Panel("P2", 11, 20, arcsec: 0)]);

        Assert.False(selection.IsPossible);
        Assert.Equal(CompositeBlock.NoPlateScale, selection.Block);
        Assert.Null(selection.PlateScale);
    }

    // The button's tooltips take the first that applies: no plate scale before no positioned panel.
    [Fact]
    public void NotPossible_BothReasons_ReportsNoPlateScale()
    {
        var selection = CompositeLayout.Select([NoFrame("P1"), Panel("P2", null, null, arcsec: null)]);

        Assert.Equal(CompositeBlock.NoPlateScale, selection.Block);
    }

    [Fact]
    public void Arrange_RefusesATileCountThatDoesNotMatch()
    {
        var selection = CompositeLayout.Select([Panel("P1", 10, 20)]);

        Assert.Throws<ArgumentException>(() => CompositeLayout.Arrange(selection, []));
    }
}
