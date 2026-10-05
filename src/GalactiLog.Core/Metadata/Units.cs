namespace GalactiLog.Core.Metadata;

// Plate-scale conversion (spec 7.1's arcsec_per_pixel row; port of units.arcsec_per_pixel).
// to_arcsec/plate_scale_from_headers/the SQL builders from units.py are not needed anywhere
// in Phase 2 and are deliberately not ported here (see task6.md's out-of-scope note).
public static class Units
{
    public const double ArcsecFactor = 206.265;

    public static double? ArcsecPerPixel(double? xpixszUm, double? focallenMm)
    {
        if (xpixszUm is not { } x || focallenMm is not { } f) return null;
        // Written as !(x > 0) rather than x <= 0 so NaN (which fails every direct
        // comparison) is also rejected, not silently propagated into the multiply.
        if (!(x > 0) || !(f > 0)) return null;
        return ArcsecFactor * x / f;
    }
}
