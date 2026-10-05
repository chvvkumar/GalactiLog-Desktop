using System.Buffers.Binary;

namespace GalactiLog.Core.Fits;

// Result of decoding a FITS primary HDU's pixel data (spec 6.1.4). Exactly one of
// Mono/PlanarRgb is non-null when HasPixelData is true; both are null when it is false.
public sealed record FitsImageResult(
    bool HasPixelData,
    string? SkipReason,
    float[,]? Mono,
    float[,,]? PlanarRgb);

// Decodes FITS pixel data into physical-value float buffers (spec 6.1.4). Static reader
// over a Stream, per the ponytail rule: one output shape pair, no generics over sample
// type. Never rejects a file - that is FitsHeaderReader's job (spec 6.1.5); this reader
// only decides whether pixel data is available for an already-accepted header, declining
// via HasPixelData: false for the two documented header-only-degradation shapes.
public static class FitsImageReader
{
    private const long BlockSize = 2880L;

    // Per-axis cap: generous for any astro camera sensor (largest consumer sensors top out
    // in the low tens of thousands of pixels per side).
    private const long MaxAxisLength = 65536L;

    // Total decoded pixel-byte cap: 2 GiB minus one, comfortably above any real single-HDU
    // astro frame and small enough that width*height*planes*bytesPerSample can't silently
    // wrap a 32-bit allocation size.
    private const long MaxPixelBytes = (2L * 1024 * 1024 * 1024) - 1;

    // Everything Read and ReadMonoStrip both need to know about a header's pixel segment.
    // Produced once by Describe so the two framings share one set of shape rules and one set
    // of skip reasons.
    private sealed record PixelGeometry(
        long Naxis,
        int Width,
        int Height,
        int Channels,
        long Bitpix,
        int BytesPerSample,
        long PixelByteCount,
        double BZero,
        double BScale,
        long? Blank);

    public static FitsImageResult Read(Stream stream, FitsHeaderResult header)
    {
        if (!header.Accepted)
        {
            throw new ArgumentException("header must be Accepted", nameof(header));
        }

        var (geometry, skipReason) = Describe(header);
        if (geometry is null)
        {
            return new FitsImageResult(false, skipReason, null, null);
        }

        var width = geometry.Width;
        var height = geometry.Height;
        var channels = geometry.Channels;

        stream.Seek(header.HeaderBlockCount * BlockSize, SeekOrigin.Begin);

        var buffer = new byte[geometry.PixelByteCount];
        stream.ReadExactly(buffer);

        float[,]? mono = geometry.Naxis == 2 ? new float[height, width] : null;
        float[,,]? planarRgb = geometry.Naxis == 3 ? new float[3, height, width] : null;

        var offset = 0;
        for (var ch = 0; ch < channels; ch++)
        {
            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    var physical = DecodeSample(buffer, offset, geometry);
                    offset += geometry.BytesPerSample;

                    if (mono is not null)
                    {
                        mono[row, col] = physical;
                    }
                    else
                    {
                        planarRgb![ch, row, col] = physical;
                    }
                }
            }
        }

        return mono is not null
            ? new FitsImageResult(true, null, mono, null)
            : new FitsImageResult(true, null, null, planarRgb);
    }

    /// <summary>
    /// Reads rows <paramref name="rowStart"/> .. <paramref name="rowStart"/> +
    /// <paramref name="rowCount"/> of a 2D FITS primary HDU as physical-value floats, without
    /// materializing the whole frame (spec 11.1's strip rule). Seeks to
    /// <c>HeaderBlockCount * 2880 + rowStart * NAXIS1 * bytesPerSample</c> and reads exactly
    /// <paramref name="rowCount"/> rows. Same BZERO/BSCALE/BLANK/endianness decode as
    /// <see cref="Read"/>, through the same private <c>DecodeSample</c>: one decoder, two framings.
    /// </summary>
    /// <param name="rowStart">First row to read. Clamped to <c>0 .. NAXIS2</c>.</param>
    /// <param name="rowCount">Rows requested. Clamped to <c>0 .. NAXIS2 - rowStart</c>, so the last
    /// strip of a frame whose height is not a multiple of the strip height is short by
    /// construction and an over-long or negative request is not an exception.</param>
    /// <returns>A <c>[rows, NAXIS1]</c> buffer whose first dimension is the clamped row count and
    /// so can be shorter than <paramref name="rowCount"/> (zero when
    /// <paramref name="rowStart"/> is at or past <c>NAXIS2</c>): read it from
    /// <c>GetLength(0)</c>, never from the argument. Null when the header is not a 2D shape this
    /// reader decodes (the same two header-only-degradation cases <see cref="Read"/> declines).
    /// A 3-plane colour frame is never Bayer, so it is declined here too.</returns>
    public static float[,]? ReadMonoStrip(
        Stream stream, FitsHeaderResult header, int rowStart, int rowCount)
    {
        if (!header.Accepted)
        {
            throw new ArgumentException("header must be Accepted", nameof(header));
        }

        var (geometry, _) = Describe(header);
        if (geometry is null || geometry.Naxis != 2)
        {
            return null;
        }

        // The last strip of a frame whose height is not a multiple of the strip height is short
        // by construction, so an over-long request is clamped rather than thrown.
        var start = Math.Clamp(rowStart, 0, geometry.Height);
        var rows = Math.Clamp(rowCount, 0, geometry.Height - start);
        var rowBytes = (long)geometry.Width * geometry.BytesPerSample;

        stream.Seek((header.HeaderBlockCount * BlockSize) + (start * rowBytes), SeekOrigin.Begin);

        var buffer = new byte[rows * rowBytes];
        stream.ReadExactly(buffer);

        var strip = new float[rows, geometry.Width];
        var offset = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < geometry.Width; col++)
            {
                strip[row, col] = DecodeSample(buffer, offset, geometry);
                offset += geometry.BytesPerSample;
            }
        }
        return strip;
    }

    // Null geometry plus a reason means "header-only degradation": the header is accepted but this
    // reader does not decode its pixel data.
    private static (PixelGeometry? Geometry, string? SkipReason) Describe(FitsHeaderResult header)
    {
        var naxis = (long)FitsHeaderReader.GetValue(header.Cards, "NAXIS")!;
        // `as long?` rather than a cast: a NAXIS3 card carrying a string or a double is a
        // malformed header, not an exception. It falls through to the unsupported-shape
        // return below.
        var naxis3 = naxis == 3 ? FitsHeaderReader.GetValue(header.Cards, "NAXIS3") as long? : null;

        if (naxis != 2 && !(naxis == 3 && naxis3 == 3))
        {
            var reason = $"unsupported NAXIS shape: NAXIS={naxis}" + (naxis3 is { } n3 ? $", NAXIS3={n3}" : "");
            return (null, reason);
        }

        var bitpix = (long)FitsHeaderReader.GetValue(header.Cards, "BITPIX")!;
        if (bitpix == 64)
        {
            return (null, "BITPIX 64 outside decoded set");
        }

        // FitsHeaderReader only guarantees NAXIS and BITPIX are integers; NAXIS1/NAXIS2 may
        // be missing entirely or carry a string, so they are checked rather than unboxed.
        if (FitsHeaderReader.GetValue(header.Cards, "NAXIS1") is not long naxis1 ||
            FitsHeaderReader.GetValue(header.Cards, "NAXIS2") is not long naxis2)
        {
            return (null, "missing or non-integer NAXIS1/NAXIS2");
        }

        var channels = naxis3 ?? 1;

        if (!IsValidAxisLength(naxis1) || !IsValidAxisLength(naxis2) || !IsValidAxisLength(channels))
        {
            var reason = $"axis length out of bounds (1..{MaxAxisLength}): NAXIS1={naxis1}, NAXIS2={naxis2}" +
                (naxis3 is { } n3 ? $", NAXIS3={n3}" : "");
            return (null, reason);
        }

        var bytesPerSample = (int)(Math.Abs(bitpix) / 8);

        long pixelByteCount;
        try
        {
            pixelByteCount = checked(naxis1 * naxis2 * channels * bytesPerSample);
        }
        catch (OverflowException)
        {
            return (null, "pixel data size overflows");
        }

        if (pixelByteCount > MaxPixelBytes)
        {
            return (null, $"pixel data size {pixelByteCount} exceeds cap {MaxPixelBytes}");
        }

        return (new PixelGeometry(
            Naxis: naxis,
            Width: (int)naxis1,
            Height: (int)naxis2,
            Channels: (int)channels,
            Bitpix: bitpix,
            BytesPerSample: bytesPerSample,
            PixelByteCount: pixelByteCount,
            BZero: ToDouble(FitsHeaderReader.GetValue(header.Cards, "BZERO")) ?? 0.0,
            BScale: ToDouble(FitsHeaderReader.GetValue(header.Cards, "BSCALE")) ?? 1.0,
            Blank: FitsHeaderReader.GetValue(header.Cards, "BLANK") as long?), null);
    }

    private static float DecodeSample(byte[] buffer, int offset, PixelGeometry geometry)
    {
        var (bzero, bscale, blank) = (geometry.BZero, geometry.BScale, geometry.Blank);
        var span = buffer.AsSpan(offset);
        switch (geometry.Bitpix)
        {
            case 8:
            {
                long raw = span[0];
                return blank.HasValue && raw == blank.Value ? 0.0f : (float)(bzero + bscale * raw);
            }
            case 16:
            {
                long raw = BinaryPrimitives.ReadInt16BigEndian(span);
                return blank.HasValue && raw == blank.Value ? 0.0f : (float)(bzero + bscale * raw);
            }
            case 32:
            {
                long raw = BinaryPrimitives.ReadInt32BigEndian(span);
                return blank.HasValue && raw == blank.Value ? 0.0f : (float)(bzero + bscale * raw);
            }
            case -32:
            {
                double raw = BinaryPrimitives.ReadSingleBigEndian(span);
                return (float)(bzero + bscale * raw);
            }
            case -64:
            {
                double raw = BinaryPrimitives.ReadDoubleBigEndian(span);
                return (float)(bzero + bscale * raw);
            }
            default:
                throw new ArgumentException($"Unsupported BITPIX {geometry.Bitpix}.", nameof(geometry));
        }
    }

    private static bool IsValidAxisLength(long value) => value >= 1 && value <= MaxAxisLength;

    private static double? ToDouble(object? value) => value switch
    {
        long l => (double)l,
        double d => d,
        _ => null,
    };
}
