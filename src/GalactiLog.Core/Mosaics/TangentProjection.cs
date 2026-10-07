using GalactiLog.Core.Sessions;

namespace GalactiLog.Core.Mosaics;

/// <summary>
/// Spec 11.6's projection, the port of the web's <c>compute_panel_layout</c>
/// (<c>mosaic_composite.py</c>): gnomonic standard coordinates about a centre, the reference
/// rotator turn, the RA unwrap and the per-tile rotation. Every angle is in degrees.
/// </summary>
public static class TangentProjection
{
    private const double Rad = Math.PI / 180;

    /// <summary>The gnomonic standard coordinates <c>(X, Y)</c>, in degrees, of <c>(ra, dec)</c>
    /// about the centre <c>(ra0, dec0)</c> (spec 11.6, standard coordinates).</summary>
    public static (double X, double Y) Standard(double ra, double dec, double ra0, double dec0)
    {
        var (d, d0, da) = (dec * Rad, dec0 * Rad, (ra - ra0) * Rad);
        var cosc = Math.Sin(d0) * Math.Sin(d) + Math.Cos(d0) * Math.Cos(d) * Math.Cos(da);
        var x = Math.Cos(d) * Math.Sin(da) / cosc;
        var y = (Math.Cos(d0) * Math.Sin(d) - Math.Sin(d0) * Math.Cos(d) * Math.Cos(da)) / cosc;
        return (x / Rad, y / Rad);
    }

    /// <summary>Standard coordinates turned by the reference rotator angle <paramref name="t"/>:
    /// <c>(cos t X + sin t Y, -sin t X + cos t Y)</c>, the web's CD matrix inverted with its x
    /// negation folded in (spec 11.6, tile centre). Divide by the canvas scale for pixels.</summary>
    public static (double X, double Y) Rotate(double x, double y, double t)
    {
        var (sin, cos) = Math.SinCos(t * Rad);
        return (cos * x + sin * y, -sin * x + cos * y);
    }

    /// <summary>The RA brought to within 180 degrees of <paramref name="reference"/> by adding or
    /// subtracting 360 (spec 11.6, centre).</summary>
    public static double UnwrapRa(double ra, double reference) =>
        ra - reference > 180 ? ra - 360 : ra - reference < -180 ? ra + 360 : ra;

    /// <summary>An RA taken into [0, 360) (spec 11.6, centre).</summary>
    public static double NormalizeRa(double ra) => AstroNight.Mod360(ra);

    /// <summary>A rotation taken into [-180, 180) by <c>((r + 180) mod 360) - 180</c>, the modulo
    /// taking the sign of the divisor (spec 11.6, per-tile rotation).</summary>
    public static double NormalizeRotation(double r) => AstroNight.Mod360(r + 180) - 180;

    /// <summary>A tile's clockwise turn <c>r</c>: its rotator less the reference's, plus 180 when
    /// the pier sides differ, normalised. A null rotator is 0 and a null pier side "West", pier
    /// sides compared ordinal case insensitive (spec 11.6, per-tile rotation).</summary>
    public static double TileRotation(double? rotator, string? pierSide, double? referenceRotator, string? referencePierSide)
    {
        var r = (rotator ?? 0) - (referenceRotator ?? 0);
        if (!string.Equals(pierSide ?? "West", referencePierSide ?? "West", StringComparison.OrdinalIgnoreCase)) r += 180;
        return NormalizeRotation(r);
    }
}
