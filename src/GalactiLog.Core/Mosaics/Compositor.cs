using GalactiLog.Core.Imaging;
using SkiaSharp;

namespace GalactiLog.Core.Mosaics;

/// <summary>A built composite (spec 11.6): the JPEG bytes at quality 90 and their size in
/// pixels.</summary>
public sealed record CompositeResult(byte[] Jpeg, int Width, int Height);

/// <summary>
/// Spec 11.6's drawing, the port of the web's <c>generate_panel_thumbnail</c> and the drawing half
/// of <c>composite_panels</c> (<c>mosaic_composite.py</c>): each included panel's best frame
/// rendered by <see cref="ThumbnailRenderer"/> and decoded, placed by
/// <see cref="CompositeLayout.Arrange"/>, drawn turned and scaled onto one black canvas, encoded
/// as JPEG 90.
/// </summary>
/// <remarks>
/// Never touches the filesystem for writing and never hands Skia a path: frames are read by the
/// renderer through <c>UserFiles</c>, the tiles are decoded from the renderer's bytes, and the
/// bytes go back to the caller (spec 2.1.2).
/// </remarks>
public static class Compositor
{
    /// <summary>The renderer's width parameter for a tile (spec 11.6, the tile).</summary>
    public const int TileWidth = 1600;

    /// <summary>The JPEG quality of a tile and of the composite (spec 11.6, ruling R9).</summary>
    public const int JpegQuality = 90;

    /// <summary>Builds the composite of <paramref name="selection"/> (spec 11.6).</summary>
    /// <param name="selection">A possible selection from <see cref="CompositeLayout.Select"/>.</param>
    /// <param name="framePaths">The best frame of each included panel, parallel to
    /// <c>selection.Included</c>. Read only.</param>
    /// <param name="progress">Hears each included panel's label as its tile starts, in order.</param>
    /// <param name="ct">Checked between tiles and passed to the renderer; a cancelled build throws
    /// <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="InvalidOperationException">The selection is not possible, or the renderer
    /// skipped a frame: the message is "&lt;panel label&gt;: &lt;skip reason&gt;" (the port fails
    /// loudly where the web drops the panel).</exception>
    public static CompositeResult Build(
        CompositeSelection selection, IReadOnlyList<string> framePaths, Action<string>? progress, CancellationToken ct)
    {
        if (!selection.IsPossible) throw new InvalidOperationException($"No composite is possible: {selection.Block}.");
        if (framePaths.Count != selection.Included.Count)
            throw new ArgumentException($"Expected {selection.Included.Count} frames, got {framePaths.Count}.", nameof(framePaths));

        var tiles = new List<SKBitmap>();
        try
        {
            for (var i = 0; i < framePaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var label = selection.Included[i].Label;
                progress?.Invoke(label);
                var rendered = ThumbnailRenderer.Render(framePaths[i], TileWidth, JpegQuality, RenderMode.Thumbnail, ct);
                if (!rendered.Rendered) throw new InvalidOperationException($"{label}: {rendered.SkipReason}");
                tiles.Add(SKBitmap.Decode(rendered.Jpeg)
                    ?? throw new InvalidOperationException($"{label}: the rendered tile could not be decoded"));
            }

            ct.ThrowIfCancellationRequested();
            var layout = CompositeLayout.Arrange(selection, [.. tiles.Select(tile => new TileSize(tile.Width, tile.Height))]);
            return Draw(layout, tiles);
        }
        finally
        {
            foreach (var tile in tiles) tile.Dispose();
        }
    }

    // Tiles in sort_order, a later one over an earlier one, each its rotated rectangle and nothing
    // outside it (the transform clips for free), onto black: the image's data, not interface colour.
    private static CompositeResult Draw(CompositeLayoutResult layout, IReadOnlyList<SKBitmap> tiles)
    {
        using var canvasBitmap = new SKBitmap(new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.Black);
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            for (var i = 0; i < tiles.Count; i++)
            {
                var place = layout.Tiles[i];
                using var image = SKImage.FromBitmap(tiles[i]);
                canvas.Save();
                canvas.Translate((float)place.CentreX, (float)place.CentreY);
                canvas.RotateDegrees((float)place.Rotation);
                canvas.DrawImage(image, SKRect.Create(-place.Width / 2f, -place.Height / 2f, place.Width, place.Height), sampling);
                canvas.Restore();
            }
        }

        using var buffer = new MemoryStream();
        if (!canvasBitmap.Encode(buffer, SKEncodedImageFormat.Jpeg, JpegQuality))
            throw new InvalidOperationException("JPEG encode failed");
        return new CompositeResult(buffer.ToArray(), layout.Width, layout.Height);
    }
}
