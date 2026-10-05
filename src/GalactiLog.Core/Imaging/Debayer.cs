using GalactiLog.Core.Fits;
using GalactiLog.Core.Xisf;

namespace GalactiLog.Core.Imaging;

/// <summary>
/// Spec 11.1's superpixel 2x2 debayer, a port of
/// <c>backend/app/services/thumbnail.py::debayer_superpixel</c> and
/// <c>_read_bayer_debayered</c>. Each 2x2 cell yields one red sample, one blue sample and the mean
/// of its two greens, giving a half-resolution channel-first <c>[3, H/2, W/2]</c> RGB buffer in
/// R, G, B order.
/// </summary>
/// <remarks>
/// <para>
/// <c>XBAYROFF</c> and <c>YBAYROFF</c> are deliberately not applied, matching the web application
/// (spec 11.1's last line). Do not "fix" this: applying them would shift the Bayer phase relative
/// to every thumbnail the web application ever produced, and the decision is recorded.
/// </para>
/// <para>
/// Pure arithmetic over arrays. No stream, no path, no logger: Core reports outcomes as data.
/// </para>
/// </remarks>
public static class Debayer
{
    /// <summary>Spec 11.1's strip height. 512 and even: an odd strip height would start alternate
    /// strips on the wrong Bayer phase and make strip-wise output differ from whole-frame output,
    /// which is exactly what <see cref="SuperpixelStriped"/>'s test asserts cannot happen.</summary>
    public const int StripRows = 512;

    // The four supported patterns, uppercase, giving the red sample's (row, column) inside each
    // 2x2 cell. Ported verbatim from thumbnail.py's _BAYER_OFFSETS; a transposed entry is
    // invisible on a star field and wrong on everything else.
    private static readonly Dictionary<string, (int Row, int Col)> RedOffsets = new(StringComparer.Ordinal)
    {
        ["RGGB"] = (0, 0),
        ["GRBG"] = (0, 1),
        ["GBRG"] = (1, 0),
        ["BGGR"] = (1, 1),
    };

    /// <summary>Resolves a pattern name to the red sample's (row, column) inside each 2x2 cell.
    /// The name is trimmed and uppercased first, so this is the single place the spec's
    /// trim-and-uppercase rule lives. False means "not one of the four", which the callers treat
    /// as mono.</summary>
    public static bool TryGetRedOffset(string? pattern, out (int Row, int Col) redOffset)
    {
        redOffset = default;
        return pattern is not null && RedOffsets.TryGetValue(pattern.Trim().ToUpperInvariant(), out redOffset);
    }

    /// <summary>Spec 11.1's pattern resolution for a FITS frame: the <c>BAYERPAT</c> card,
    /// trimmed and uppercased. Null when the card is absent or its value is not one of the four,
    /// which means "treat this frame as mono".</summary>
    public static string? PatternFromFits(IReadOnlyList<FitsCard> cards)
        // `as string` rather than a cast: a numeric BAYERPAT is a malformed header, not an
        // exception, and a malformed header means mono.
        => Normalize(FitsHeaderReader.GetValue(cards, "BAYERPAT") as string);

    /// <summary>Spec 11.1's pattern resolution for an XISF frame: <c>BAYERPAT</c> from the
    /// header's FITS keyword map first, then the <c>ColorFilterArray</c> element's
    /// <c>pattern</c> attribute. Same trim-and-uppercase rule, same null-means-mono contract.
    /// A CFA element whose pattern is not one of the four (a 4x4 CFA, say) is mono.</summary>
    public static string? PatternFromXisf(XisfHeaderResult header)
    {
        header.FitsKeywords.TryGetValue("BAYERPAT", out var keyword);
        return Normalize(keyword) ?? Normalize(header.ColorFilterArray?.Pattern);
    }

    /// <summary>Whole-frame debayer. Height and width are first truncated down to even values;
    /// the returned buffer is <c>[3, h/2, w/2]</c>.</summary>
    /// <exception cref="ArgumentException">The pattern is not one of the four after trim and
    /// uppercase. Callers resolve the pattern with <see cref="PatternFromFits"/> /
    /// <see cref="PatternFromXisf"/> first and take the mono path when that returns null, exactly
    /// as <c>generate_thumbnail</c> does.</exception>
    public static float[,,] Superpixel(float[,] frame, string pattern)
    {
        var red = RequireRedOffset(pattern);
        var height = Truncate(frame.GetLength(0));
        var width = Truncate(frame.GetLength(1));

        var result = new float[3, height / 2, width / 2];
        FillCells(result, destRowStart: 0, frame, height, width, red);
        return result;
    }

    /// <summary>Spec 11.1's strip-wise debayer, over a strip reader so the full raw frame and the
    /// full debayered result never coexist in memory. <paramref name="readStrip"/> is called with
    /// (rowStart, rowCount) and returns that strip as <c>[rowCount, width]</c>; every call's
    /// rowStart is even and every rowCount except possibly the last is
    /// <see cref="StripRows"/>. Output is byte-identical to <see cref="Superpixel"/> over the
    /// whole frame.</summary>
    /// <exception cref="ArgumentException">The pattern is not one of the four after trim and
    /// uppercase, or <paramref name="readStrip"/> returned a strip whose shape is not
    /// <c>[rowCount, width]</c>. A short strip is a reader bug (a mis-clamped row count, a
    /// truncated file), and failing it by name here beats an <see cref="IndexOutOfRangeException"/>
    /// from the middle of the cell loop with no mention of which strip was wrong.</exception>
    public static float[,,] SuperpixelStriped(
        Func<int, int, float[,]> readStrip, int height, int width, string pattern)
    {
        var red = RequireRedOffset(pattern);
        var evenHeight = Truncate(height);
        var evenWidth = Truncate(width);

        var result = new float[3, evenHeight / 2, evenWidth / 2];
        for (var rowStart = 0; rowStart < evenHeight; rowStart += StripRows)
        {
            // StripRows is even and evenHeight is even, so every rowCount is even and every
            // rowStart is a Bayer phase boundary. That is the whole reason the two paths agree.
            var rowCount = Math.Min(StripRows, evenHeight - rowStart);
            var strip = readStrip(rowStart, rowCount);
            if (strip.GetLength(0) != rowCount || strip.GetLength(1) != width)
            {
                throw new ArgumentException(
                    $"readStrip({rowStart}, {rowCount}) returned a " +
                    $"[{strip.GetLength(0)}, {strip.GetLength(1)}] strip; expected " +
                    $"[{rowCount}, {width}].",
                    nameof(readStrip));
            }
            FillCells(result, rowStart / 2, strip, rowCount, evenWidth, red);
        }
        return result;
    }

    // The arithmetic, in the one place it exists. Blue sits at the diagonal opposite of red and
    // the two greens at the off-diagonal corners (spec 11.1); source may be the whole frame or one
    // strip, which is the only difference between the two public entry points.
    private static void FillCells(
        float[,,] destination, int destRowStart, float[,] source, int height, int width, (int Row, int Col) red)
    {
        var blueRow = 1 - red.Row;
        var blueCol = 1 - red.Col;
        for (var oy = 0; oy < height / 2; oy++)
        {
            var y = 2 * oy;
            var destRow = destRowStart + oy;
            for (var ox = 0; ox < width / 2; ox++)
            {
                var x = 2 * ox;
                destination[0, destRow, ox] = source[y + red.Row, x + red.Col];
                // `/ 2.0f` on float, matching NumPy's float32 mean. Not `>> 1` and not a double
                // intermediate: the stretch that follows normalizes to unit range, so a half-LSB
                // difference here moves the output byte on a low-dynamic-range frame.
                destination[1, destRow, ox] =
                    (source[y + red.Row, x + blueCol] + source[y + blueRow, x + red.Col]) / 2.0f;
                destination[2, destRow, ox] = source[y + blueRow, x + blueCol];
            }
        }
    }

    private static (int Row, int Col) RequireRedOffset(string pattern)
        => TryGetRedOffset(pattern, out var red)
            ? red
            : throw new ArgumentException($"Unsupported Bayer pattern '{pattern}'.", nameof(pattern));

    private static string? Normalize(string? pattern)
        => TryGetRedOffset(pattern, out _) ? pattern!.Trim().ToUpperInvariant() : null;

    private static int Truncate(int length) => (length / 2) * 2;
}
