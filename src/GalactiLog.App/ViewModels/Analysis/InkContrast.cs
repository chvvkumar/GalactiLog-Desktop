using SkiaSharp;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// Which of two held inks a mark takes over a given background, by WCAG contrast. Pure functions,
/// so the rule is asserted directly rather than through a rendered chart.
/// </summary>
/// <remarks>
/// Two charts on the Analysis page need the same rule. The Matrix grid picks each cell label's ink
/// against the ramp colour that cell is painted in, and the box plot picks its median mark's ink
/// against the box fill. Spec 13's row put both in <c>text-primary</c>, and on <c>red-light</c>
/// that is a contrast of about 1.1 against a saturated red, so the mark was unreadable in exactly
/// the theme where it carries the most. On <c>luminance</c> the same choice is poor in the
/// other direction, a pale fill under near-white ink, so the rule is general rather than one
/// theme's patch.
/// </remarks>
internal static class InkContrast
{
    /// <summary>
    /// Which of <paramref name="light"/> and <paramref name="dark"/> to draw over
    /// <paramref name="background"/>: whichever has the higher WCAG contrast ratio against it. The
    /// light ink wins a tie, so a background the two serve equally keeps the theme's own text ink.
    /// </summary>
    internal static SKColor Choose(SKColor background, SKColor light, SKColor dark)
        => Contrast(light, background) >= Contrast(dark, background) ? light : dark;

    /// <summary>The WCAG 2.x contrast ratio between two opaque colours, 1 to 21.</summary>
    internal static double Contrast(SKColor a, SKColor b)
    {
        var first = RelativeLuminance(a);
        var second = RelativeLuminance(b);
        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    /// <summary>
    /// WCAG 2.x relative luminance. Alpha is ignored, so a caller whose ink or background carries a
    /// declared alpha forces it opaque before it gets here.
    /// </summary>
    internal static double RelativeLuminance(SKColor colour)
        => (0.2126d * Channel(colour.Red))
            + (0.7152d * Channel(colour.Green))
            + (0.0722d * Channel(colour.Blue));

    private static double Channel(byte value)
    {
        var c = value / 255d;
        return c <= 0.03928d ? c / 12.92d : Math.Pow((c + 0.055d) / 1.055d, 2.4d);
    }
}
