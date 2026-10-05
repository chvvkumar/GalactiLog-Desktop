using System.Globalization;
using System.IO.Compression;
using K4os.Compression.LZ4;

namespace GalactiLog.Core.Xisf;

/// <summary>
/// Obtains the final (decompressed, unshuffled) raw sample bytes for a XISF image's pixel
/// data block (spec 6.2.3, 6.2.5, 6.2.6's declared-size-mismatch rejection). Does not decode
/// samples into physical values; that is <see cref="XisfImageReader"/>'s job.
/// </summary>
public static class XisfDataBlock
{
    // Hard cap on any single declared/allocated pixel buffer: 2 GB minus one byte (the
    // largest value that fits a signed 32-bit length without overflow). Anything the header
    // declares above this is rejected before an allocation is attempted.
    private const long MaxUncompressedSize = int.MaxValue;

    // Returns the outcome as data, not as a bool the caller has to string-match on: a
    // declared-size mismatch or an out-of-range attachment is a spec 6.2.6 rejection, while
    // an unsupported codec or an external reference is a skip. XisfImageReader forwards the
    // enum untouched.
    public static (XisfPixelOutcome Outcome, string? Reason, byte[]? Bytes) Read(
        Stream stream, XisfHeaderResult header, long expectedUncompressedByteLength)
    {
        byte[] asStoredBytes;

        if (header.Location.StartsWith("attachment:", StringComparison.Ordinal))
        {
            var parts = header.Location.Split(':');
            if (parts.Length != 3 ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) ||
                !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                return (XisfPixelOutcome.Skipped, "unrecognized location form", null);
            }

            if (position < 0 || size < 0 || size > stream.Length || position > stream.Length - size)
            {
                return (XisfPixelOutcome.Rejected, "attachment offset or size past end of file", null);
            }

            stream.Seek(position, SeekOrigin.Begin);
            asStoredBytes = new byte[size];
            stream.ReadExactly(asStoredBytes);
        }
        else if (header.Location.StartsWith("inline:", StringComparison.Ordinal) ||
                 header.Location.Equals("embedded", StringComparison.Ordinal))
        {
            asStoredBytes = header.InlineOrEmbeddedBytes ?? Array.Empty<byte>();
        }
        else if (header.Location.StartsWith("url:", StringComparison.Ordinal) ||
                 header.Location.StartsWith("path:", StringComparison.Ordinal))
        {
            return (XisfPixelOutcome.Skipped, "external data reference not supported", null);
        }
        else
        {
            return (XisfPixelOutcome.Skipped, "unrecognized location form", null);
        }

        if (header.Compression is null)
        {
            if (asStoredBytes.LongLength != expectedUncompressedByteLength)
            {
                return (XisfPixelOutcome.Rejected, "declared size does not match data length", null);
            }
            return (XisfPixelOutcome.Decoded, null, asStoredBytes);
        }

        var compressionParts = header.Compression.Split(':');
        var codec = compressionParts[0];
        var shuffle = codec.EndsWith("+sh", StringComparison.Ordinal);
        var baseCodec = shuffle ? codec[..^3] : codec;

        if (compressionParts.Length < 2 ||
            !long.TryParse(compressionParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uncompressedSize))
        {
            return (XisfPixelOutcome.Skipped, "unrecognized compression attribute", null);
        }

        // Validate before any allocation: a negative, zero, absurdly large, or
        // geometry-mismatched declared size is rejected here rather than fed to `new
        // byte[uncompressedSize]`.
        if (uncompressedSize <= 0 ||
            uncompressedSize > MaxUncompressedSize ||
            uncompressedSize != expectedUncompressedByteLength)
        {
            return (XisfPixelOutcome.Rejected, "declared size does not match data length", null);
        }

        int shuffleItemSize = 0;
        if (shuffle)
        {
            if (compressionParts.Length < 3 ||
                !int.TryParse(compressionParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out shuffleItemSize) ||
                shuffleItemSize is not (1 or 2 or 4 or 8))
            {
                return (XisfPixelOutcome.Skipped, "unrecognized compression attribute", null);
            }
        }

        byte[] decompressedBytes;

        switch (baseCodec)
        {
            case "zlib":
            {
                var target = new byte[uncompressedSize];
                using var input = new MemoryStream(asStoredBytes);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                int totalRead = 0;
                try
                {
                    while (totalRead < target.Length)
                    {
                        var read = zlib.Read(target, totalRead, target.Length - totalRead);
                        if (read == 0)
                        {
                            break;
                        }
                        totalRead += read;
                    }
                }
                catch (InvalidDataException)
                {
                    return (XisfPixelOutcome.Rejected, "declared size does not match data length", null);
                }
                if (totalRead != target.Length)
                {
                    return (XisfPixelOutcome.Rejected, "declared size does not match data length", null);
                }
                // Confirm the compressed payload doesn't decode to more than the declared
                // size: one more byte should not be available past the filled target.
                if (zlib.ReadByte() != -1)
                {
                    return (XisfPixelOutcome.Rejected, "declared size does not match data length", null);
                }
                decompressedBytes = target;
                break;
            }
            case "lz4":
            case "lz4hc":
            {
                var target = new byte[uncompressedSize];
                int written;
                try
                {
                    written = LZ4Codec.Decode(asStoredBytes.AsSpan(), target.AsSpan());
                }
                catch (ArgumentException)
                {
                    return (XisfPixelOutcome.Rejected, "declared size does not match data length", null);
                }
                if (written != uncompressedSize)
                {
                    return (XisfPixelOutcome.Rejected, "declared size does not match data length", null);
                }
                decompressedBytes = target;
                break;
            }
            default:
                return (XisfPixelOutcome.Skipped, $"unsupported compression codec: {codec}", null);
        }

        var finalBytes = shuffle ? Unshuffle(decompressedBytes, shuffleItemSize) : decompressedBytes;

        if (finalBytes.LongLength != expectedUncompressedByteLength)
        {
            return (XisfPixelOutcome.Rejected, "declared size does not match data length", null);
        }

        return (XisfPixelOutcome.Decoded, null, finalBytes);
    }

    /// <summary>
    /// Inverts the spec 6.2.5 byte-shuffle transform: a shuffled buffer of <c>n</c> items of
    /// <paramref name="itemSize"/> bytes stores all byte-0 values first, then all byte-1
    /// values, and so on. Output byte <c>i * itemSize + j</c> is input byte <c>j * n + i</c>.
    /// Trailing bytes beyond <c>n * itemSize</c> are copied verbatim.
    /// </summary>
    public static byte[] Unshuffle(ReadOnlySpan<byte> shuffled, int itemSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemSize);
        var n = shuffled.Length / itemSize;
        var output = new byte[shuffled.Length];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < itemSize; j++)
            {
                output[i * itemSize + j] = shuffled[j * n + i];
            }
        }
        for (var k = n * itemSize; k < shuffled.Length; k++)
        {
            output[k] = shuffled[k];
        }
        return output;
    }
}
