using System.Buffers.Binary;

namespace GalactiLog.Core.Xisf;

public enum XisfPixelOutcome { Decoded, Skipped, Rejected }

public sealed record XisfPixelResult(
    XisfPixelOutcome Outcome,
    string? Reason,
    float[,]? Mono,
    float[,,]? PlanarRgb);

/// <summary>
/// Decodes XISF pixel data into physical-value float buffers (spec 6.2.4, 6.2.6). Assumes
/// <paramref name="header"/> came from an accepted <see cref="XisfHeaderReader.Read"/> call.
/// Does not apply any orientation flip: XISF is top-down (spec 6.2.7); the vertical flip
/// applied to FITS-only output lives in Phase 8's ThumbnailRenderer, not here.
/// </summary>
public static class XisfImageReader
{
    public static XisfPixelResult Read(Stream stream, XisfHeaderResult header)
    {
        if (header.ColorSpace == "CIELab")
        {
            return new XisfPixelResult(XisfPixelOutcome.Skipped, "CIELab color space not supported", null, null);
        }

        if (header.ChannelCount != 1 && header.ChannelCount != 3)
        {
            return new XisfPixelResult(XisfPixelOutcome.Skipped, $"unsupported channel count: {header.ChannelCount}", null, null);
        }

        var bytesPerSample = header.SampleFormat switch
        {
            "UInt8" => 1,
            "UInt16" => 2,
            "UInt32" => 4,
            "Float32" => 4,
            "Float64" => 8,
            "UInt64" => 8,
            "Complex32" => 8,
            "Complex64" => 16,
            _ => -1,
        };

        if (bytesPerSample == -1)
        {
            return new XisfPixelResult(XisfPixelOutcome.Skipped, $"unrecognized sample format: {header.SampleFormat}", null, null);
        }

        if (header.SampleFormat is "UInt64" or "Complex32" or "Complex64")
        {
            return new XisfPixelResult(XisfPixelOutcome.Skipped, $"unsupported sample format: {header.SampleFormat}", null, null);
        }

        if (header.Location.StartsWith("url:", StringComparison.Ordinal) ||
            header.Location.StartsWith("path:", StringComparison.Ordinal))
        {
            return new XisfPixelResult(XisfPixelOutcome.Skipped, "external data reference not supported", null, null);
        }

        long expectedSize;
        try
        {
            // Bounded by XisfHeaderReader's geometry check, so this cannot overflow today;
            // checked keeps it that way if either cap ever moves.
            expectedSize = checked((long)header.Width * header.Height * header.ChannelCount * bytesPerSample);
        }
        catch (OverflowException)
        {
            return new XisfPixelResult(XisfPixelOutcome.Rejected, "pixel data size overflows", null, null);
        }

        // The outcome comes back as an enum: no string comparison decides skip vs reject.
        var (outcome, reason, bytes) = XisfDataBlock.Read(stream, header, expectedSize);
        if (outcome != XisfPixelOutcome.Decoded)
        {
            return new XisfPixelResult(outcome, reason, null, null);
        }

        var span = bytes.AsSpan();
        var bigEndian = header.ByteOrder == "big";
        var width = header.Width;
        var height = header.Height;

        float[,]? mono = null;
        float[,,]? planarRgb = null;

        if (header.ChannelCount == 1)
        {
            mono = new float[height, width];
            var offset = 0;
            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    mono[row, col] = ReadSample(span, offset, header.SampleFormat, bigEndian);
                    offset += bytesPerSample;
                }
            }
        }
        else
        {
            planarRgb = new float[header.ChannelCount, height, width];
            var offset = 0;
            for (var ch = 0; ch < header.ChannelCount; ch++)
            {
                for (var row = 0; row < height; row++)
                {
                    for (var col = 0; col < width; col++)
                    {
                        planarRgb[ch, row, col] = ReadSample(span, offset, header.SampleFormat, bigEndian);
                        offset += bytesPerSample;
                    }
                }
            }
        }

        return new XisfPixelResult(XisfPixelOutcome.Decoded, null, mono, planarRgb);
    }

    private static float ReadSample(ReadOnlySpan<byte> span, int offset, string sampleFormat, bool bigEndian) =>
        sampleFormat switch
        {
            "UInt8" => span[offset],
            "UInt16" => bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(span[offset..])
                : BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]),
            "UInt32" => bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(span[offset..])
                : BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]),
            "Float32" => bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(span[offset..])
                : BinaryPrimitives.ReadSingleLittleEndian(span[offset..]),
            "Float64" => (float)(bigEndian
                ? BinaryPrimitives.ReadDoubleBigEndian(span[offset..])
                : BinaryPrimitives.ReadDoubleLittleEndian(span[offset..])),
            _ => throw new InvalidOperationException($"Unreachable: unsupported sample format '{sampleFormat}' reached ReadSample."),
        };
}
