using System.Runtime.InteropServices;
using GalactiLog.Core.Fits;
using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Xisf;
using SkiaSharp;

namespace GalactiLog.Core.Imaging;

/// <summary>Which of spec 11.2's two read strategies to use.</summary>
public enum RenderMode
{
    /// <summary>Spec 11.2's thumbnail path, a port of <c>generate_thumbnail</c>: block-bin a mono
    /// frame by <c>Resampler.BinStep</c> before anything else, and debayer a Bayer frame in
    /// 512-row strips.</summary>
    Thumbnail,

    /// <summary>Spec 11.5's preview path, a port of <c>generate_preview</c>: no block-binning,
    /// whole-frame debayer, and <c>maxWidth == 0</c> means native resolution with no resample at
    /// all.</summary>
    Preview,
}

/// <summary>What one render produced, or why it produced nothing.</summary>
/// <param name="Rendered">True when <paramref name="Jpeg"/> carries encoded bytes.</param>
/// <param name="SkipReason">Non-null exactly when <paramref name="Rendered"/> is false: a rejected
/// header, a header-only-degradation shape (spec 6.1.5, 6.2.6), an unreadable file, or an XISF
/// pixel outcome that is not <c>Decoded</c>. A reason, never an exception: a frame that cannot be
/// rendered is data, and the App layer renders a placeholder for it (spec 6.2.6).</param>
/// <param name="Jpeg">The encoded bytes, ready for <c>AppWriter.WriteAllBytes</c>. Null when
/// <paramref name="Rendered"/> is false. This type never touches the filesystem for writing; it
/// returns bytes and the caller decides where they go (spec 2.1.2).</param>
/// <param name="Width">Encoded width in pixels, 0 on a skip.</param>
/// <param name="Height">Encoded height in pixels, 0 on a skip.</param>
/// <param name="IsColor">True for a three-channel result (a debayered Bayer frame or a three-plane
/// colour frame), false for a single-channel grey result.</param>
public sealed record RenderResult(
    bool Rendered, string? SkipReason, byte[]? Jpeg, int Width, int Height, bool IsColor);

/// <summary>
/// Spec 11.2's thumbnail pipeline, a port of <c>thumbnail.generate_thumbnail</c>,
/// <c>preview.generate_preview</c> and <c>xisf_parser.generate_xisf_thumbnail</c> as one function
/// with two modes. Pipeline order is read, block-bin, normalize, flip (FITS only), resize on linear
/// data, stretch, encode, and it is not interchangeable (spec 11.2).
/// </summary>
/// <remarks>
/// <para>
/// One renderer, three callers (frame thumbnail, preview, reference thumbnail), per design-lessons
/// rule 1. Three copies of this pipeline is exactly the shape the web application has, and it is
/// why its preview and its thumbnail disagree about JPEG quality.
/// </para>
/// <para>
/// SkiaSharp is used for the resample (Task 2) and the encode, and is never given a path: the
/// encode goes to a <c>MemoryStream</c> whose bytes the caller hands to <c>AppWriter</c>
/// (spec 2.1.2). There is no overload here that takes an output path, deliberately.
/// </para>
/// <para>
/// <b>Peak memory.</b> ponytail: there is deliberately no pixel-count gate here, and this is the
/// largest known ceiling in the thumbnail subsystem. Upgrade path: a maximum megapixel count,
/// refused as a skip, which is a spec change (spec 11.2) rather than a local decision. Spec 11.2
/// puts the normalize and the flip before the resize, so a preview at native resolution or at
/// 2400 px holds full-resolution <c>float</c> buffers for as long as the pipeline runs, whatever
/// the output width is. For a 100 megapixel three-plane master that is
/// roughly 1.2 GB for the planar buffer plus the raw sample block plus one extracted plane and its
/// normalized copy: about 3 GB of managed heap, and about 6 GB at spec 10.6's concurrency of 2,
/// which <c>ThumbnailCache</c> enforces for every caller including the reference pass. Spec
/// 11.1's strip-wise debayer removes one full-frame buffer from the FITS Bayer thumbnail path and
/// nothing else here; <see cref="FlipVerticalInPlace"/> removes another. The only caps are
/// <c>FitsImageReader</c>'s existing <c>MaxPixelBytes</c> and <c>XisfDataBlock</c>'s declared-size
/// check, and a frame that still exhausts the heap comes back as a skip rather than an exception
/// (see <see cref="Render"/>).
/// </para>
/// </remarks>
public static class ThumbnailRenderer
{
    /// <param name="framePath">A user file, opened read-only through
    /// <c>GalactiLog.Core.Io.UserFiles.OpenRead</c> and never written.</param>
    /// <param name="maxWidth">Target width in pixels. 0 means native resolution: no block-bin and
    /// no resample (spec 11.3, 11.5's preview case).</param>
    /// <param name="jpegQuality">85 for frame and reference thumbnails, 90 for previews
    /// (spec 11.3, questions.md Q5).</param>
    /// <param name="mode">Which of spec 11.2's two read strategies to use.</param>
    /// <param name="ct">Cancellation. A cancelled render throws
    /// <see cref="OperationCanceledException"/>; it does not return a skip, because cancellation is
    /// not a property of the frame. It is the only exception raised for a frame: every other
    /// failure, an unreadable file and an exhausted heap included, comes back as a skip. A null
    /// <paramref name="framePath"/> throws <see cref="ArgumentNullException"/>, which is an
    /// argument guard at the trust boundary and not a property of any frame.</param>
    public static RenderResult Render(
        string framePath, int maxWidth, int jpegQuality, RenderMode mode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(framePath);

        try
        {
            return RenderCore(framePath, maxWidth, jpegQuality, mode, ct);
        }
        catch (OutOfMemoryException)
        {
            // A frame this machine cannot hold is still a frame, and the skip contract Tasks 4 and
            // 6 rely on is what keeps one such frame from failing a whole scan or a worker. There
            // is no pixel-count gate in front of this: the reachable ceiling depends on the
            // installed memory and the concurrency the caller chose, not on a constant this class
            // could pick. Nothing is caught and retried, and nothing partially allocated survives:
            // every buffer in the pipeline is local, so the throw unwinds them all before this
            // returns.
            return Skip("frame too large for pixel reading");
        }
    }

    private static RenderResult RenderCore(
        string framePath, int maxWidth, int jpegQuality, RenderMode mode, CancellationToken ct)
    {
        var format = FrameReader.FormatOf(framePath);
        if (format == FrameReader.FrameFormat.Unsupported)
        {
            return Skip($"unsupported file extension: {Path.GetExtension(framePath)}");
        }

        var isFits = format == FrameReader.FrameFormat.Fits;

        // Spec 6.2.8: FITS pixel data is bottom-up and every channel is flipped; XISF is top-down
        // and none is. The C# renderer preserves both behaviors exactly.
        var flip = isFits;

        string? skipReason;
        float[,]? mono;
        float[,,]? colour;
        try
        {
            using var stream = UserFiles.OpenRead(framePath);
            (skipReason, mono, colour) = isFits
                ? ReadFits(stream, maxWidth, mode)
                : ReadXisf(stream);
        }
        catch (IOException ex)
        {
            // A missing file, a locked file, a truncated pixel block. All of them are frames the
            // caller renders a placeholder for, not failures that may kill a scan or a worker.
            return Skip($"cannot read frame: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Skip($"cannot read frame: {ex.Message}");
        }

        if (skipReason is not null)
        {
            return Skip(skipReason);
        }

        ct.ThrowIfCancellationRequested();

        var isColor = colour is not null;
        var planes = new byte[isColor ? 3 : 1][,];
        for (var plane = 0; plane < planes.Length; plane++)
        {
            // Per channel, independently and unlinked (spec 11.2). A linked stretch would share one
            // median and one MAD across three channels and shift the colour balance of every frame.
            var channel = isColor ? ExtractPlane(colour!, plane) : mono!;
            var normalized = MtfStretch.NormalizeToUnit(channel);
            if (flip)
            {
                // Safe in place: NormalizeToUnit always allocates and never returns its input, so
                // this buffer is owned by this iteration and read by nothing else.
                FlipVerticalInPlace(normalized);
            }

            if (maxWidth > 0)
            {
                normalized = Resampler.ResizeArray(normalized, maxWidth);
            }

            planes[plane] = MtfStretch.StretchChannel(normalized);
            ct.ThrowIfCancellationRequested();
        }

        var height = planes[0].GetLength(0);
        var width = planes[0].GetLength(1);
        if (width == 0 || height == 0)
        {
            // A frame smaller than one Bayer cell, or a degenerate axis. Skia refuses a zero
            // dimension, and a zero-pixel thumbnail is not worth an exception.
            return Skip("frame has no pixels to render");
        }

        ct.ThrowIfCancellationRequested();
        var jpeg = Encode(planes, width, height, isColor, jpegQuality);
        return jpeg is null
            ? Skip("JPEG encode failed")
            : new RenderResult(true, null, jpeg, width, height, isColor);
    }

    // Spec 11.2's FITS branch. The Bayer path and the block-bin path are exclusive: a debayered
    // frame is already half resolution and is never additionally block-binned, exactly as
    // generate_thumbnail's if/else does.
    private static (string? SkipReason, float[,]? Mono, float[,,]? Colour) ReadFits(
        Stream stream, int maxWidth, RenderMode mode)
    {
        var header = FitsHeaderReader.Read(stream);
        if (!header.Accepted)
        {
            return (header.RejectionReason, null, null);
        }

        var pattern = Debayer.PatternFromFits(header.Cards);

        // Spec 11.1's strip-wise debayer, so the full raw frame and the full debayered result never
        // coexist in memory. The zero-row probe is how this asks FitsImageReader whether the header
        // is a 2D shape it decodes at all: a null there (BITPIX 64, a 3-plane frame, an out-of-range
        // axis) falls through to the full read below, which carries the matching skip reason.
        if (pattern is not null && mode == RenderMode.Thumbnail &&
            FitsHeaderReader.GetValue(header.Cards, "NAXIS1") is long naxis1 &&
            FitsHeaderReader.GetValue(header.Cards, "NAXIS2") is long naxis2 &&
            FitsImageReader.ReadMonoStrip(stream, header, 0, 0) is not null)
        {
            // NAXIS2 verbatim: SuperpixelStriped truncates to an even height itself and validates
            // the shape every readStrip call returns.
            return (null, null, Debayer.SuperpixelStriped(
                (rowStart, rowCount) => FitsImageReader.ReadMonoStrip(stream, header, rowStart, rowCount)!,
                (int)naxis2, (int)naxis1, pattern));
        }

        var image = FitsImageReader.Read(stream, header);
        if (!image.HasPixelData)
        {
            return (image.SkipReason, null, null);
        }

        if (image.Mono is null)
        {
            // A three-plane colour frame is never block-binned in either mode: _read_binned falls
            // back to a full read for len(dims) != 2.
            return (null, null, image.PlanarRgb);
        }

        if (pattern is not null)
        {
            // Preview mode's whole-frame debayer, matching generate_preview.
            return (null, null, Debayer.Superpixel(image.Mono, pattern));
        }

        var data = mode == RenderMode.Thumbnail && maxWidth > 0
            ? Resampler.ReadBinned(image.Mono, maxWidth)
            : image.Mono;
        return (null, data, null);
    }

    // Spec 11.2's XISF branch, a port of generate_xisf_thumbnail plus spec 11.1's ColorFilterArray
    // rule (questions.md Q6: the web application debayers no XISF at all, so an OSC frame renders
    // there as a green mosaic). No block-binning and no flip, matching that function.
    private static (string? SkipReason, float[,]? Mono, float[,,]? Colour) ReadXisf(Stream stream)
    {
        var header = XisfHeaderReader.Read(stream);
        if (!header.Accepted)
        {
            return (header.RejectionReason, null, null);
        }

        var pixels = XisfImageReader.Read(stream, header);
        if (pixels.Outcome != XisfPixelOutcome.Decoded)
        {
            return (pixels.Reason, null, null);
        }

        if (pixels.Mono is null)
        {
            return (null, null, pixels.PlanarRgb);
        }

        var pattern = Debayer.PatternFromXisf(header);
        // ponytail: whole-frame debayer for XISF in both modes, with no strip variant. Ceiling:
        // an XISF Bayer frame peaks at the raw frame plus its debayered result in memory, which a
        // FITS frame of the same size does not. Upgrade path: none worth taking while
        // XisfDataBlock.Read decompresses the whole block before any sample is decoded, so strips
        // would save nothing here; a streaming block decoder is the prerequisite, not a strip loop.
        return pattern is not null
            ? (null, null, Debayer.Superpixel(pixels.Mono, pattern))
            : (null, pixels.Mono, null);
    }

    /// <summary><c>np.flipud</c> on a <c>[H, W]</c> buffer: row <c>r</c> becomes row
    /// <c>H - 1 - r</c>, columns unchanged. Swaps rows in place over the caller's buffer.</summary>
    /// <remarks>
    /// <para>
    /// In place because the only caller hands it a buffer <c>MtfStretch.NormalizeToUnit</c> just
    /// allocated for that one channel. A copying flip would hold a second full-resolution
    /// <c>float</c> buffer alongside the first at exactly the point in the pipeline where the frame
    /// is still at native resolution: 400 MB for one plane of a 100 megapixel frame, which the
    /// class remarks above count.
    /// </para>
    /// <para>
    /// Deliberately its own method and deliberately not folded into the resize or the encode. Spec
    /// 11.2 fixes its position in the pipeline order and the roadmap's Verify line tests exactly
    /// that position: a flip applied after the resize crops a different row off the bottom once the
    /// prefilter runs.
    /// </para>
    /// </remarks>
    private static void FlipVerticalInPlace(float[,] data)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        for (var top = 0; top < height / 2; top++)
        {
            var bottom = height - 1 - top;
            for (var col = 0; col < width; col++)
            {
                (data[top, col], data[bottom, col]) = (data[bottom, col], data[top, col]);
            }
        }
    }

    private static float[,] ExtractPlane(float[,,] planar, int plane)
    {
        var height = planar.GetLength(1);
        var width = planar.GetLength(2);
        var channel = new float[height, width];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                channel[row, col] = planar[plane, row, col];
            }
        }
        return channel;
    }

    // Spec 2.1.2: the encode goes to a MemoryStream and the bytes go back to the caller, which
    // hands them to AppWriter. SkiaSharp is never given a path here and must never be: no
    // SKFileWStream, no path-taking decode, and no output-path overload on this type "for
    // convenience". FileSafetyTest's existing scan looks for System.IO write members and cannot see
    // an SKFileWStream at all, so this rule holds the same way UserFiles and ShellIntegration hold
    // theirs: there is nothing to call, so there is nothing to forget. The companion architecture
    // test NoSkiaSharpPathBasedIoAnywhereInSrc is what makes that structural rather than
    // conventional.
    private static byte[]? Encode(byte[][,] planes, int width, int height, bool isColor, int quality)
    {
        var colorType = isColor ? SKColorType.Rgba8888 : SKColorType.Gray8;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, colorType, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            return null;
        }

        var rowBytes = bitmap.RowBytes;
        var row = new byte[isColor ? width * 4 : width];
        for (var y = 0; y < height; y++)
        {
            if (isColor)
            {
                // np.stack(channels, axis=-1): channel-first planes interleaved as R, G, B for the
                // encoder, with an opaque alpha.
                for (var x = 0; x < width; x++)
                {
                    var offset = x * 4;
                    row[offset] = planes[0][y, x];
                    row[offset + 1] = planes[1][y, x];
                    row[offset + 2] = planes[2][y, x];
                    row[offset + 3] = 255;
                }
            }
            else
            {
                for (var x = 0; x < width; x++)
                {
                    row[x] = planes[0][y, x];
                }
            }

            // (nint) before the multiply for the same reason Resampler does it: a native-resolution
            // preview of a large sensor passes 2 GB of row offsets through this loop.
            Marshal.Copy(row, 0, pixels + ((nint)y * rowBytes), row.Length);
        }

        using var buffer = new MemoryStream();
        return bitmap.Encode(buffer, SKEncodedImageFormat.Jpeg, quality) ? buffer.ToArray() : null;
    }

    private static RenderResult Skip(string? reason)
        => new(false, reason ?? "frame could not be rendered", null, 0, 0, false);
}
