using GalactiLog.Core.Mosaics;
using Xunit;

namespace GalactiLog.Core.Tests.Mosaics;

// Phase 19B Task 2. Spec 11.6's projection, every hand-computed case of its table at
// c = 1/240 degree per canvas pixel, asserted within 0.01 pixel.
public class TangentProjectionTests
{
    private const double PixelTolerance = 0.01;
    private const double DegreeTolerance = 0.005;
    private const double PixelsPerDegree = 240;

    private static (double X, double Y) Pixels(double ra, double dec, double ra0, double dec0, double t)
    {
        var (sx, sy) = TangentProjection.Standard(ra, dec, ra0, dec0);
        var (x, y) = TangentProjection.Rotate(sx, sy, t);
        return (x * PixelsPerDegree, y * PixelsPerDegree);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 1.00, 0.00)]   // case 1
    [InlineData(0, 46, 0, 45, 0.00, 1.00)] // case 2
    [InlineData(2, 80, 0, 80, 0.35, 0.01)] // case 3
    public void Standard_MatchesTheHandComputedCases(double ra, double dec, double ra0, double dec0, double x, double y)
    {
        var (sx, sy) = TangentProjection.Standard(ra, dec, ra0, dec0);

        Assert.Equal(x, sx, DegreeTolerance);
        Assert.Equal(y, sy, DegreeTolerance);
    }

    [Theory]
    [InlineData(0, 1, 0, 0, 0, 240.02, 0.00)]   // case 1
    [InlineData(0, 0, 46, 0, 45, 0.00, 240.02)] // case 2
    [InlineData(0, 2, 80, 0, 80, 83.34, 1.43)]  // case 3
    [InlineData(90, 1, 0, 0, 0, 0.00, -240.02)] // case 4, the rotation case
    public void CanvasPixels_MatchTheHandComputedCases(double t, double ra, double dec, double ra0, double dec0, double x, double y)
    {
        var (px, py) = Pixels(ra, dec, ra0, dec0, t);

        Assert.Equal(x, px, PixelTolerance);
        Assert.Equal(y, py, PixelTolerance);
    }

    [Fact]
    public void Rotate_ByZero_IsTheIdentity()
    {
        Assert.Equal((0.3, -0.7), TangentProjection.Rotate(0.3, -0.7, 0));
    }

    // Case 5, the wrap case: reference at 359.5, second at 0.5, so a0 = 0 and the panels sit
    // half a degree either side of the centre.
    [Fact]
    public void WrapCase_CentresOnZero_AndPlacesThePanelsEitherSide()
    {
        var a0 = TangentProjection.UnwrapRa(359.5, 359.5) + TangentProjection.UnwrapRa(0.5, 359.5);
        a0 = TangentProjection.NormalizeRa(a0 / 2);
        Assert.Equal(0, a0, 1e-9);

        var (rx, ry) = Pixels(359.5, 0, a0, 0, 0);
        var (sx, sy) = Pixels(0.5, 0, a0, 0, 0);
        Assert.Equal(-120.00, rx, PixelTolerance);
        Assert.Equal(0.00, ry, PixelTolerance);
        Assert.Equal(120.00, sx, PixelTolerance);
        Assert.Equal(0.00, sy, PixelTolerance);
    }

    [Theory]
    [InlineData(1, 359, 361)]
    [InlineData(359, 1, -1)]
    [InlineData(180, 0, 180)]
    [InlineData(10, 20, 10)]
    public void UnwrapRa_BringsTheRaWithin180OfTheReference(double ra, double reference, double expected)
    {
        Assert.Equal(expected, TangentProjection.UnwrapRa(ra, reference), 1e-9);
    }

    // The pier table, cases 6 to 8: a differing pier adds 180; a null pier is West and pier sides
    // compare case insensitive; a null rotator is 0.
    [Theory]
    [InlineData(0.0, "East", 0.0, "West", -180)] // case 6
    [InlineData(350.0, "West", 10.0, "West", -20)] // case 7
    [InlineData(10.0, null, 10.0, "west", 0)]      // case 8
    [InlineData(10.0, "east", 10.0, "East", 0)]
    [InlineData(null, "West", 30.0, "West", -30)]
    [InlineData(30.0, "West", null, "East", -150)]
    public void TileRotation_MatchesThePierCases(double? rotator, string? pier, double? referenceRotator, string? referencePier, double expected)
    {
        Assert.Equal(expected, TangentProjection.TileRotation(rotator, pier, referenceRotator, referencePier), 1e-9);
    }

    // Cases 6 and 7 of the pier table, plus the normalisation edges.
    [Theory]
    [InlineData(180, -180)]  // case 6: 0 - 0 + 180
    [InlineData(340, -20)]   // case 7: 350 - 10
    [InlineData(-180, -180)]
    [InlineData(-190, 170)]
    [InlineData(179.5, 179.5)]
    [InlineData(720, 0)]
    public void NormalizeRotation_FallsIntoMinus180To180(double r, double expected)
    {
        Assert.Equal(expected, TangentProjection.NormalizeRotation(r), 1e-9);
    }
}
