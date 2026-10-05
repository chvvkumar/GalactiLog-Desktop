using SkiaSharp;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// The two rules a confidence band drawn as an upper area plus an opaque mask has to get right
/// (ruling A23, chart spike section 3). Written once because this is the pattern's second
/// occurrence on the Analysis page, and the two copies had already disagreed about both of them
/// (design lesson 1).
/// </summary>
/// <remarks>
/// <para>
/// rc5.4 has no series that fills the region between two curves, so the band is an area filled to
/// the upper bound at a low alpha with a second area, painted in the card's own colour, covering
/// everything below the lower bound. Two things make that picture right, and neither is visible in
/// the shape of the code that draws it:
/// </para>
/// <para>
/// <b>The floor.</b> A LiveCharts area fill closes to the series' <c>Pivot</c>, whose default is 0,
/// so with a lower bound below zero the mask fills upward from the zero line instead of downward to
/// the bottom of the plot: the band's bottom edge is pinned to the Y=0 gridline rather than to the
/// bound the statistics computed, and with both bounds below zero the mask's region contains the
/// band's and swallows it completely. Both areas therefore take one shared floor at or below
/// everything drawn. On data that is wholly positive the floor is the lower bound's own minimum and
/// nothing moves, because the region under it is painted in the background token either way.
/// </para>
/// <para>
/// <b>The alpha.</b> The mask has to be opaque, which is spec 13's and ruling A23's word. Of the
/// three themes only <c>deep-sky</c> declares <c>ColorBgElevated</c> with an alpha below full
/// (<c>#E6181B26</c>), and a mask painted at the token's own alpha there passes about a tenth of
/// the band's fill through the half it exists to hide. Pinning the alpha does not make the mask and
/// the card name two colours: it is one channel of one colour, and the card keeps the token exactly
/// as the markup declares it.
/// </para>
/// <para>
/// Both members are plain: sequences of double in and a double out, a colour in and a colour out.
/// Neither names a series type, a result record or a metric, so a second chart folds onto them
/// without this file learning anything about it.
/// </para>
/// </remarks>
public static class BandAreas
{
    /// <summary>Full alpha, which is what "opaquely" means.</summary>
    private const byte Opaque = 0xFF;

    /// <summary>
    /// The one value both areas close to: at or below every value the chart draws, which is the
    /// smaller of the band's own lowest lower bound and the lowest plotted value. Assign it as
    /// <c>Pivot</c> on the band AND on the mask, never on one of them.
    /// </summary>
    /// <param name="lowerBounds">The band's lower bound, one value per point of it.</param>
    /// <param name="drawnValues">Every value plotted on the same axis, so the floor cannot sit
    /// above a point the reader can see.</param>
    /// <exception cref="InvalidOperationException">Either sequence is empty, which a caller that
    /// has already established it has a band and points cannot produce.</exception>
    public static double Floor(IEnumerable<double> lowerBounds, IEnumerable<double> drawnValues)
        => Math.Min(lowerBounds.Min(), drawnValues.Min());

    /// <summary>
    /// The mask's fill: the card's own colour with its alpha pinned to full. The red, green and
    /// blue channels are the caller's, so the mask and the card can never name two colours.
    /// </summary>
    /// <param name="cardColour">The resolved <c>ColorBgElevated</c> the chart control's own
    /// background is painted with.</param>
    public static SKColor OpaqueMask(SKColor cardColour) => cardColour.WithAlpha(Opaque);
}
