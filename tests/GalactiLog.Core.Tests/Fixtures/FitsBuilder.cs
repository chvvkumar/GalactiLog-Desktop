using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace GalactiLog.Core.Tests.Fixtures;

// Synthetic FITS fixture generator (spec 6.1, fixture policy 18.2). Writes only to a
// MemoryStream, never to disk: every FITS fixture Phase 2's readers need, valid or
// deliberately malformed, is built here from a keyword list and an optional pixel array.
//
// Cards are appended in call order and emitted in that exact order; nothing is reordered or
// deduplicated by the builder itself (duplicate-key last-wins is the reader's job, not the
// builder's). Every helper that produces a card image funnels through AddCardText, which
// throws if the card text would not fit in 80 bytes - that is a test-authoring bug, not a
// fixture to build.
public sealed class FitsBuilder
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    private readonly List<byte[]> _cards = new();
    private bool _endCardCalled;
    private bool _noAutoEnd;
    private byte[]? _pixelDataRaw;

    public FitsBuilder Card(string keyword, long value, string? comment = null)
        => AddValueCard(keyword, value.ToString(CultureInfo.InvariantCulture).PadLeft(20), comment);

    public FitsBuilder Card(string keyword, double value, string? comment = null)
        => AddValueCard(keyword, value.ToString("G", CultureInfo.InvariantCulture).PadLeft(20), comment);

    public FitsBuilder Card(string keyword, bool value, string? comment = null)
        => AddValueCard(keyword, value ? "T" : "F", comment);

    public FitsBuilder Card(string keyword, string value, string? comment = null)
        => AddValueCard(keyword, Quote(value), comment);

    // COMMENT/HISTORY keywords (8 bytes, no '='); free text starts at byte 8.
    public FitsBuilder Comment(string text) => AddFreeTextCard("COMMENT", text);

    public FitsBuilder History(string text) => AddFreeTextCard("HISTORY", text);

    // HIERARCH <keyword> = 'value' / comment, starting at byte 0. "HIERARCH" is exactly 8
    // characters, so it fills the keyword field with no padding needed.
    public FitsBuilder Hierarch(string keyword, string value, string? comment = null)
    {
        var text = "HIERARCH " + keyword + " = " + Quote(value);
        if (comment is not null)
        {
            text += " / " + comment;
        }
        return AddCardText(text);
    }

    // "HIERARCH" followed by tailText verbatim, with no '=' anywhere in the card. Used to
    // build the "HIERARCH card with no '=' is skipped" tolerance fixture.
    public FitsBuilder HierarchNoEquals(string tailText) => AddCardText("HIERARCH " + tailText);

    // Splits fullValue into chunks of at most chunkLength characters. Emits one base card
    // "keyword= 'chunk0&'" followed by one "CONTINUE= 'chunkN&'" card per subsequent chunk;
    // only the final chunk's card omits the trailing '&'. The caller picks a chunkLength
    // that leaves room for the quotes and the '&' - this method does not auto-size.
    public FitsBuilder ContinuedString(string keyword, string fullValue, int chunkLength)
    {
        if (chunkLength <= 0)
        {
            throw new ArgumentException("chunkLength must be positive.", nameof(chunkLength));
        }

        var chunks = new List<string>();
        for (var i = 0; i < fullValue.Length; i += chunkLength)
        {
            chunks.Add(fullValue.Substring(i, Math.Min(chunkLength, fullValue.Length - i)));
        }
        if (chunks.Count == 0)
        {
            chunks.Add(string.Empty);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var isLast = i == chunks.Count - 1;
            var quoted = "'" + chunks[i].Replace("'", "''") + (isLast ? string.Empty : "&") + "'";
            AddValueCard(i == 0 ? keyword : "CONTINUE", quoted, null);
        }
        return this;
    }

    // Escape hatch for every malformation not covered by a named method above: a bad
    // SIMPLE, an out-of-set BITPIX, a ZIMAGE = T card, a malformed value field, etc.
    public FitsBuilder RawCard(string exact80CharText)
    {
        if (exact80CharText.Length != CardSize)
        {
            throw new ArgumentException($"RawCard text must be exactly {CardSize} characters, was {exact80CharText.Length}.", nameof(exact80CharText));
        }
        _cards.Add(Encoding.ASCII.GetBytes(exact80CharText));
        return this;
    }

    // 80 spaces. Used for the blank-keyword-exclusion fixture.
    public FitsBuilder BlankCard() => AddCardText(string.Empty);

    // "END" followed by 77 spaces; bytes 0-7 equal "END     " exactly.
    public FitsBuilder EndCard()
    {
        AddCardText("END");
        _endCardCalled = true;
        return this;
    }

    // Suppresses Build()'s automatic END-card append. A "missing END" fixture calls this
    // and never calls EndCard(), so the header genuinely has no END within its bytes.
    public FitsBuilder NoAutoEnd()
    {
        _noAutoEnd = true;
        return this;
    }

    // Encodes physical values into a mono pixel data block, big-endian, row-major with
    // NAXIS1 (columns) fastest-varying. Does not add SIMPLE/BITPIX/NAXIS*/BZERO/BSCALE
    // cards: the test author adds those explicitly via Card(...) before Build().
    public FitsBuilder Pixels(short bitpix, int naxis1, int naxis2, float[,] physicalValues, double bzero = 0, double bscale = 1)
    {
        _pixelDataRaw = EncodePixels(bitpix, naxis1, naxis2, channels: 1, (_, row, col) => physicalValues[row, col], bzero, bscale);
        return this;
    }

    // Same encoding, channel-first planar layout: channel 0 in full, then channel 1, then
    // channel 2.
    public FitsBuilder PixelsPlanarRgb(short bitpix, int naxis1, int naxis2, float[,,] channelFirstPhysicalValues, double bzero = 0, double bscale = 1)
    {
        _pixelDataRaw = EncodePixels(bitpix, naxis1, naxis2, channels: 3, (ch, row, col) => channelFirstPhysicalValues[ch, row, col], bzero, bscale);
        return this;
    }

    public MemoryStream Build() => new(Assemble());

    // Returns only the first totalByteLength bytes of the full assembly. Used for the "file
    // shorter than the header claims" fixture.
    public MemoryStream BuildTruncated(int totalByteLength)
    {
        var full = Assemble();
        if (totalByteLength > full.Length)
        {
            throw new ArgumentException($"Requested length {totalByteLength} exceeds full built length {full.Length}.", nameof(totalByteLength));
        }
        var truncated = new byte[totalByteLength];
        Array.Copy(full, truncated, totalByteLength);
        return new MemoryStream(truncated);
    }

    private byte[] Assemble()
    {
        using var header = new MemoryStream();
        foreach (var card in _cards)
        {
            header.Write(card, 0, card.Length);
        }
        if (!_endCardCalled && !_noAutoEnd)
        {
            header.Write(Encoding.ASCII.GetBytes("END".PadRight(CardSize)), 0, CardSize);
        }
        PadTo(header, BlockSize, (byte)' ');

        using var result = new MemoryStream();
        header.WriteTo(result);
        if (_pixelDataRaw is not null)
        {
            result.Write(_pixelDataRaw, 0, _pixelDataRaw.Length);
            PadTo(result, BlockSize, 0);
        }
        return result.ToArray();
    }

    private static void PadTo(MemoryStream stream, int blockSize, byte fill)
    {
        var remainder = (int)(stream.Length % blockSize);
        if (remainder == 0)
        {
            return;
        }
        var padding = new byte[blockSize - remainder];
        if (fill != 0)
        {
            Array.Fill(padding, fill);
        }
        stream.Write(padding, 0, padding.Length);
    }

    private FitsBuilder AddValueCard(string keyword, string valueField, string? comment)
    {
        // A keyword longer than 8 characters pushes the "= " past byte 8 and silently
        // produces a card no FITS reader will parse as a value card, so the fixture would
        // quietly test nothing. HIERARCH is the supported way to write a long keyword.
        if (keyword.Length > 8)
        {
            throw new ArgumentException(
                $"Keyword '{keyword}' exceeds 8 characters; use Hierarch(...).", nameof(keyword));
        }

        var text = keyword.PadRight(8) + "= " + valueField;
        if (comment is not null)
        {
            text += " / " + comment;
        }
        return AddCardText(text);
    }

    private FitsBuilder AddFreeTextCard(string keyword, string text)
    {
        if (text.Length > 72)
        {
            throw new ArgumentException($"{keyword} text exceeds 72 characters.", nameof(text));
        }
        return AddCardText(keyword.PadRight(8) + text);
    }

    private FitsBuilder AddCardText(string text)
    {
        if (text.Length > CardSize)
        {
            throw new ArgumentException($"Card text exceeds {CardSize} bytes: '{text}'", nameof(text));
        }
        _cards.Add(Encoding.ASCII.GetBytes(text.PadRight(CardSize)));
        return this;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static byte[] EncodePixels(short bitpix, int naxis1, int naxis2, int channels, Func<int, int, int, float> getValue, double bzero, double bscale)
    {
        var sampleSize = SampleSize(bitpix);
        var buffer = new byte[channels * naxis1 * naxis2 * sampleSize];
        var offset = 0;
        for (var ch = 0; ch < channels; ch++)
        {
            for (var row = 0; row < naxis2; row++)
            {
                for (var col = 0; col < naxis1; col++)
                {
                    WriteSample(buffer, offset, bitpix, getValue(ch, row, col), bzero, bscale);
                    offset += sampleSize;
                }
            }
        }
        return buffer;
    }

    private static int SampleSize(short bitpix) => bitpix switch
    {
        8 => 1,
        16 => 2,
        32 => 4,
        -32 => 4,
        -64 => 8,
        _ => throw new ArgumentException($"Unsupported BITPIX {bitpix}.", nameof(bitpix)),
    };

    // Physical value = BZERO + BSCALE * raw, so raw = (physical - BZERO) / BSCALE. Integer
    // BITPIX rounds away from zero and clamps to the sample type's range; floating-point
    // BITPIX ignores BZERO/BSCALE entirely (no fixture in this phase exercises them there).
    private static void WriteSample(byte[] buffer, int offset, short bitpix, float physical, double bzero, double bscale)
    {
        switch (bitpix)
        {
            case 8:
                buffer[offset] = (byte)Math.Clamp(Math.Round((physical - bzero) / bscale, MidpointRounding.AwayFromZero), byte.MinValue, byte.MaxValue);
                break;
            case 16:
                BinaryPrimitives.WriteInt16BigEndian(
                    buffer.AsSpan(offset, 2),
                    (short)Math.Clamp(Math.Round((physical - bzero) / bscale, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue));
                break;
            case 32:
                BinaryPrimitives.WriteInt32BigEndian(
                    buffer.AsSpan(offset, 4),
                    (int)Math.Clamp(Math.Round((physical - bzero) / bscale, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue));
                break;
            case -32:
                BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(offset, 4), physical);
                break;
            case -64:
                BinaryPrimitives.WriteDoubleBigEndian(buffer.AsSpan(offset, 8), physical);
                break;
            default:
                throw new ArgumentException($"Unsupported BITPIX {bitpix}.", nameof(bitpix));
        }
    }
}
