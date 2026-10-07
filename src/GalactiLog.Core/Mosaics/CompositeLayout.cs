namespace GalactiLog.Core.Mosaics;

/// <summary>A best frame's geometry from its six <c>images</c> columns (spec 11.6, ruling R10).
/// Every member may be null.</summary>
public sealed record PanelGeometry(
    double? RaDeg, double? DecDeg, int? WidthPx, double? ArcsecPerPixel, double? RotatorPosition, string? PierSide);

/// <summary>One panel of the mosaic as the composite sees it (spec 11.6): its id, its label and
/// its best frame's geometry in the filter, null when it has no best frame there.</summary>
public sealed record CompositePanel(Guid PanelId, string Label, PanelGeometry? Frame);

/// <summary>Why a panel is not drawn (spec 11.6, 12.17): "no &lt;filter&gt; frames" or
/// "no position".</summary>
public enum LeftOutReason { NoFrames, NoPosition }

/// <summary>A panel the composite leaves out, with its reason (spec 11.6).</summary>
public sealed record LeftOutPanel(Guid PanelId, string Label, LeftOutReason Reason);

/// <summary>Why no composite is possible (spec 12.17, the Composite button's tooltips, the
/// first that applies): "No panel carries a plate scale", then "No panel has a positioned frame
/// in &lt;filter&gt;". <see cref="None"/> when a composite is possible.</summary>
public enum CompositeBlock { None, NoPlateScale, NoPositionedPanel }

/// <summary>Spec 11.6's inclusion rules applied: the included panels and the left-out ones, each
/// in <c>sort_order</c>; the shared plate scale in arcseconds per pixel (null when none); and
/// what blocks a composite.</summary>
public sealed record CompositeSelection(
    IReadOnlyList<CompositePanel> Included, IReadOnlyList<LeftOutPanel> LeftOut, double? PlateScale, CompositeBlock Block)
{
    /// <summary>At least one panel is included and a plate scale exists (spec 11.6).</summary>
    public bool IsPossible => Block == CompositeBlock.None;
}

/// <summary>A decoded tile's size in pixels, <c>w_p</c> by <c>h_p</c> (spec 11.6, the tile).</summary>
public readonly record struct TileSize(int Width, int Height);

/// <summary>Where one tile is drawn (spec 11.6): its centre in canvas pixels, its drawn size in
/// whole pixels after <c>g</c> and <c>f</c>, and its clockwise turn <c>r</c> in degrees.</summary>
public sealed record TilePlacement(Guid PanelId, double CentreX, double CentreY, int Width, int Height, double Rotation);

/// <summary>The composite's layout (spec 11.6): the canvas in whole pixels, the output-cap factor
/// <c>f</c> (1 when uncapped), one placement per included panel in <c>sort_order</c>, and the
/// selection it was built from.</summary>
public sealed record CompositeLayoutResult(
    CompositeSelection Selection, int Width, int Height, double OutputScale, IReadOnlyList<TilePlacement> Tiles);

/// <summary>
/// Spec 11.6's layout, the port of the web's <c>compute_panel_layout</c> and the placement half of
/// <c>composite_panels</c> (<c>mosaic_composite.py</c>), amended by rulings R9 and R15:
/// <see cref="Select"/> runs the inclusion rules (the Composite button and the build share it),
/// <see cref="Arrange"/> places the rendered tiles on the canvas.
/// </summary>
public static class CompositeLayout
{
    /// <summary>The longer side of the output, in pixels (spec 11.6, the output cap).</summary>
    public const int OutputCap = 6000;

    /// <summary>Spec 11.6's inclusion rules over every panel in <c>sort_order</c>: no best frame
    /// leaves a panel out as <see cref="LeftOutReason.NoFrames"/>; a frame lacking
    /// <c>ra_deg</c>, <c>dec_deg</c> or a positive <c>width_px</c> as
    /// <see cref="LeftOutReason.NoPosition"/>. The plate scale is the first positive
    /// <c>arcsec_per_pixel</c> over every panel, included or not.</summary>
    public static CompositeSelection Select(IReadOnlyList<CompositePanel> panels)
    {
        var included = new List<CompositePanel>();
        var leftOut = new List<LeftOutPanel>();
        foreach (var panel in panels)
        {
            if (panel.Frame is not { } f) leftOut.Add(new(panel.PanelId, panel.Label, LeftOutReason.NoFrames));
            else if (f.RaDeg is null || f.DecDeg is null || f.WidthPx is not > 0) leftOut.Add(new(panel.PanelId, panel.Label, LeftOutReason.NoPosition));
            else included.Add(panel);
        }

        var plateScale = panels.Select(p => p.Frame?.ArcsecPerPixel).FirstOrDefault(a => a > 0);
        var block = plateScale is null ? CompositeBlock.NoPlateScale
            : included.Count == 0 ? CompositeBlock.NoPositionedPanel
            : CompositeBlock.None;
        return new CompositeSelection(included, leftOut, plateScale, block);
    }

    /// <summary>Places the included panels' tiles, <paramref name="tiles"/> one per included
    /// panel in the same order (spec 11.6): centre, canvas scale from the reference panel, tile
    /// centres, draw factor <c>g</c>, per-tile rotation, the bounding box shifted to 0 and the
    /// output cap. A drawn dimension is rounded to the nearest whole pixel, halves away from zero,
    /// and at least 1, under <c>g</c> and again under <c>f</c>.</summary>
    /// <exception cref="InvalidOperationException">The selection is not possible.</exception>
    /// <exception cref="ArgumentException">The tile count differs from the included count.</exception>
    public static CompositeLayoutResult Arrange(CompositeSelection selection, IReadOnlyList<TileSize> tiles)
    {
        if (!selection.IsPossible) throw new InvalidOperationException($"No composite is possible: {selection.Block}.");
        if (tiles.Count != selection.Included.Count)
            throw new ArgumentException($"Expected {selection.Included.Count} tiles, got {tiles.Count}.", nameof(tiles));

        var frames = selection.Included.Select(p => p.Frame!).ToList();
        var reference = frames[0];
        var raRef = reference.RaDeg!.Value;
        var a0 = TangentProjection.NormalizeRa(frames.Average(f => TangentProjection.UnwrapRa(f.RaDeg!.Value, raRef)));
        var d0 = frames.Average(f => f.DecDeg!.Value);
        var t = reference.RotatorPosition ?? 0;

        double Scale(PanelGeometry f) => (f.ArcsecPerPixel is > 0 and var a ? a : selection.PlateScale!.Value) / 3600;
        var c = Scale(reference) * reference.WidthPx!.Value / tiles[0].Width;

        var placed = new List<(double X, double Y, int W, int H, double R)>();
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var (sx, sy) = TangentProjection.Standard(f.RaDeg!.Value, f.DecDeg!.Value, a0, d0);
            var (x, y) = TangentProjection.Rotate(sx, sy, t);
            var g = Scale(f) * f.WidthPx!.Value / tiles[i].Width / c;
            var r = TangentProjection.TileRotation(f.RotatorPosition, f.PierSide, reference.RotatorPosition, reference.PierSide);
            placed.Add((x / c, y / c, Whole(tiles[i].Width * g), Whole(tiles[i].Height * g), r));
        }

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in placed)
        {
            var (sin, cos) = Math.SinCos(p.R * Math.PI / 180);
            var hw = (p.W * Math.Abs(cos) + p.H * Math.Abs(sin)) / 2;
            var hh = (p.W * Math.Abs(sin) + p.H * Math.Abs(cos)) / 2;
            (minX, maxX) = (Math.Min(minX, p.X - hw), Math.Max(maxX, p.X + hw));
            (minY, maxY) = (Math.Min(minY, p.Y - hh), Math.Max(maxY, p.Y + hh));
        }

        var width = (int)Math.Ceiling(Math.Round(maxX - minX, 6));
        var height = (int)Math.Ceiling(Math.Round(maxY - minY, 6));
        var longer = Math.Max(width, height);
        var scale = 1.0;
        if (longer > OutputCap)
        {
            scale = (double)OutputCap / longer;
            (width, height) = width >= height ? (OutputCap, Whole(height * scale)) : (Whole(width * scale), OutputCap);
        }

        var result = selection.Included.Select((panel, i) =>
        {
            var p = placed[i];
            var (w, h) = scale == 1.0 ? (p.W, p.H) : (Whole(p.W * scale), Whole(p.H * scale));
            return new TilePlacement(panel.PanelId, (p.X - minX) * scale, (p.Y - minY) * scale, w, h, p.R);
        }).ToList();
        return new CompositeLayoutResult(selection, width, height, scale, result);
    }

    private static int Whole(double v) => Math.Max(1, (int)Math.Round(v, MidpointRounding.AwayFromZero));
}
