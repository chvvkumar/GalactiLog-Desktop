using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;

namespace GalactiLog.Core.Tests.Fixtures;

// Synthetic XISF fixture generator (spec 6.2, fixture policy 18.2). Writes only to a
// MemoryStream: signature, header-length field, reserved bytes, UTF-8 XML header, and an
// optional attached data block, all assembled in memory.
//
// location defaults to "attachment" (an attached data block appended after the header)
// unless LocationOverride, InlineEncoding, or Embedded was called. Geometry must be set
// before Build() unless RawHeaderXml is used instead of the generated <Image> element.
public sealed class XisfBuilder
{
    private const string OffsetPlaceholder = "0000000000";

    private int? _width;
    private int? _height;
    private int? _channelCount;
    private string _sampleFormat = "UInt16";
    private string _colorSpace = "Gray";
    private string? _byteOrder = "little";
    private string? _imageType;
    private readonly List<(string Name, string Value, string? Comment)> _fitsKeywords = new();
    private readonly List<(string Id, string Type, string Value)> _properties = new();
    private (string Pattern, int Width, int Height)? _cfa;

    private float[,]? _monoPixels;
    private float[,,]? _rgbPixels;
    private string? _compressionCodec;
    private string? _locationOverride;
    private string? _inlineEncoding;
    private string? _embeddedEncoding;
    private byte[]? _rawSignature;
    private string? _rawImageXml;

    public XisfBuilder Geometry(int width, int height, int channelCount)
    {
        _width = width;
        _height = height;
        _channelCount = channelCount;
        return this;
    }

    public XisfBuilder SampleFormat(string format)
    {
        _sampleFormat = format;
        return this;
    }

    public XisfBuilder ColorSpace(string colorSpace)
    {
        _colorSpace = colorSpace;
        return this;
    }

    public XisfBuilder ByteOrderAttribute(string order)
    {
        _byteOrder = order;
        return this;
    }

    public XisfBuilder ImageTypeAttribute(string? imageType)
    {
        _imageType = imageType;
        return this;
    }

    // Value is written unquoted by default; a test needing the quoted form that
    // _strip_fits_string must strip passes a pre-quoted string, e.g. FitsKeyword("OBJECT", "'M 31'").
    public XisfBuilder FitsKeyword(string name, string value, string? comment = null)
    {
        _fitsKeywords.Add((name, value, comment));
        return this;
    }

    public XisfBuilder Property(string id, string type, string value)
    {
        _properties.Add((id, type, value));
        return this;
    }

    public XisfBuilder ColorFilterArray(string pattern, int width, int height)
    {
        _cfa = (pattern, width, height);
        return this;
    }

    // monoPhysicalValues is indexed [row, col] (height, width), matching FitsBuilder's
    // convention.
    public XisfBuilder Pixels(float[,] monoPhysicalValues)
    {
        _monoPixels = monoPhysicalValues;
        return this;
    }

    // channelFirstPhysicalValues is indexed [channel, row, col].
    public XisfBuilder PixelsPlanarRgb(float[,,] channelFirstPhysicalValues)
    {
        _rgbPixels = channelFirstPhysicalValues;
        return this;
    }

    public XisfBuilder Compression(string codec)
    {
        _compressionCodec = codec;
        return this;
    }

    // Sets the location attribute to exactly this string and suppresses the builder's own
    // attachment-block logic entirely: no data bytes are appended by Build(), even if
    // Pixels was called. Used for url:, path:, and out-of-range attachment: fixtures.
    public XisfBuilder LocationOverride(string rawLocationValue)
    {
        _locationOverride = rawLocationValue;
        return this;
    }

    public XisfBuilder InlineEncoding(string encoding)
    {
        _inlineEncoding = encoding;
        return this;
    }

    public XisfBuilder Embedded(string encoding)
    {
        _embeddedEncoding = encoding;
        return this;
    }

    public XisfBuilder RawSignature(byte[] eightBytes)
    {
        if (eightBytes.Length != 8)
        {
            throw new ArgumentException("RawSignature requires exactly 8 bytes.", nameof(eightBytes));
        }
        _rawSignature = eightBytes;
        return this;
    }

    // Escape hatch: replaces the entire generated <Image>...</Image> fragment with the
    // caller's exact XML, still wrapped in the <xisf> root by Build(). Build() does not
    // validate this fragment itself - it writes the bytes as given. No attachment block is
    // appended in this mode; the caller controls everything about the header.
    public XisfBuilder RawHeaderXml(string fullImageElementXml)
    {
        _rawImageXml = fullImageElementXml;
        return this;
    }

    public MemoryStream Build()
    {
        if (_rawImageXml is null && (_width is null || _height is null || _channelCount is null))
        {
            throw new InvalidOperationException("Geometry must be set before Build() (unless RawHeaderXml is used).");
        }

        string imageXml;
        var attachedBytes = Array.Empty<byte>();
        var appendAttachment = false;

        if (_rawImageXml is not null)
        {
            imageXml = _rawImageXml;
        }
        else
        {
            var rawBytes = _monoPixels is not null ? EncodeMono(_monoPixels)
                : _rgbPixels is not null ? EncodeRgb(_rgbPixels)
                : Array.Empty<byte>();

            string location;
            string? compressionAttr = null;
            string? inlineText = null;
            string? embeddedText = null;
            string? embeddedEncodingAttr = null;

            if (_locationOverride is not null)
            {
                location = _locationOverride;
            }
            else if (_inlineEncoding is not null)
            {
                location = $"inline:{_inlineEncoding}";
                inlineText = EncodeText(rawBytes, _inlineEncoding);
            }
            else if (_embeddedEncoding is not null)
            {
                location = "embedded";
                embeddedEncodingAttr = _embeddedEncoding;
                embeddedText = EncodeText(rawBytes, _embeddedEncoding);
            }
            else
            {
                var toWrite = rawBytes;
                if (_compressionCodec is not null)
                {
                    (toWrite, compressionAttr) = Compress(rawBytes, _compressionCodec);
                }
                attachedBytes = toWrite;
                appendAttachment = true;
                location = $"attachment:{OffsetPlaceholder}:{attachedBytes.Length}";
            }

            imageXml = BuildImageXml(location, compressionAttr, inlineText, embeddedText, embeddedEncodingAttr);
        }

        var placeholderXml = $"<xisf xmlns=\"http://www.pixinsight.com/xisf\">{imageXml}</xisf>";
        var headerLength = Encoding.UTF8.GetByteCount(placeholderXml);
        var finalXml = placeholderXml;

        if (appendAttachment)
        {
            var realOffset = 16 + headerLength;
            var placeholderAttr = $"attachment:{OffsetPlaceholder}:{attachedBytes.Length}";
            var finalAttr = $"attachment:{realOffset.ToString("D10", CultureInfo.InvariantCulture)}:{attachedBytes.Length}";
            finalXml = placeholderXml.Replace(placeholderAttr, finalAttr);
        }

        var finalHeaderBytes = Encoding.UTF8.GetBytes(finalXml);

        using var result = new MemoryStream();
        result.Write(_rawSignature ?? Encoding.ASCII.GetBytes("XISF0100"), 0, 8);
        Span<byte> lengthField = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthField, (uint)finalHeaderBytes.Length);
        result.Write(lengthField);
        result.Write(new byte[4], 0, 4); // reserved
        result.Write(finalHeaderBytes, 0, finalHeaderBytes.Length);
        if (appendAttachment)
        {
            result.Write(attachedBytes, 0, attachedBytes.Length);
        }
        return new MemoryStream(result.ToArray());
    }

    private string BuildImageXml(string location, string? compressionAttr, string? inlineText, string? embeddedText, string? embeddedEncodingAttr)
    {
        var sb = new StringBuilder();
        sb.Append("<Image");
        sb.Append($" geometry=\"{_width}:{_height}:{_channelCount}\"");
        sb.Append($" sampleFormat=\"{Escape(_sampleFormat)}\"");
        sb.Append($" colorSpace=\"{Escape(_colorSpace)}\"");
        sb.Append($" location=\"{Escape(location)}\"");
        if (compressionAttr is not null)
        {
            sb.Append($" compression=\"{Escape(compressionAttr)}\"");
        }
        if (_byteOrder is not null)
        {
            sb.Append($" byteOrder=\"{Escape(_byteOrder)}\"");
        }
        if (_imageType is not null)
        {
            sb.Append($" imageType=\"{Escape(_imageType)}\"");
        }
        sb.Append('>');

        foreach (var (name, value, comment) in _fitsKeywords)
        {
            sb.Append($"<FITSKeyword name=\"{Escape(name)}\" value=\"{Escape(value)}\"");
            if (comment is not null)
            {
                sb.Append($" comment=\"{Escape(comment)}\"");
            }
            sb.Append("/>");
        }
        foreach (var (id, type, value) in _properties)
        {
            sb.Append($"<Property id=\"{Escape(id)}\" type=\"{Escape(type)}\" value=\"{Escape(value)}\"/>");
        }
        if (_cfa is { } cfa)
        {
            sb.Append($"<ColorFilterArray pattern=\"{Escape(cfa.Pattern)}\" width=\"{cfa.Width}\" height=\"{cfa.Height}\"/>");
        }
        if (embeddedText is not null)
        {
            sb.Append($"<Data encoding=\"{Escape(embeddedEncodingAttr!)}\">{embeddedText}</Data>");
        }
        if (inlineText is not null)
        {
            sb.Append(inlineText);
        }
        sb.Append("</Image>");
        return sb.ToString();
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("\"", "&quot;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private static string EncodeText(byte[] bytes, string encoding) => encoding switch
    {
        "base64" => Convert.ToBase64String(bytes),
        "base16" => Convert.ToHexString(bytes),
        _ => throw new ArgumentException($"Unsupported encoding '{encoding}'.", nameof(encoding)),
    };

    // Applies the shuffle transform (the inverse of the spec 6.2.5 unshuffle) when the
    // codec name ends in "+sh", then compresses with the codec named by the (unsuffixed)
    // base name. zstd and unrecognized codec names are written uncompressed under the
    // requested label, by design: nothing in this phase decodes zstd, so the reader must
    // reject/skip on the codec name alone.
    private (byte[] Bytes, string Attribute) Compress(byte[] rawBytes, string codec)
    {
        var shuffle = codec.EndsWith("+sh", StringComparison.Ordinal);
        var baseCodec = shuffle ? codec[..^3] : codec;
        var itemSize = ItemSize(_sampleFormat);
        var input = shuffle ? ShuffleBytes(rawBytes, itemSize) : rawBytes;

        var output = baseCodec switch
        {
            "zlib" => ZlibCompress(input),
            "lz4" or "lz4hc" => Lz4Compress(input),
            _ => input,
        };

        var attribute = shuffle
            ? $"{codec}:{rawBytes.Length}:{itemSize}"
            : $"{codec}:{rawBytes.Length}";
        return (output, attribute);
    }

    private static byte[] ZlibCompress(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Lz4Compress(byte[] input)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(input.Length)];
        var written = LZ4Codec.Encode(input.AsSpan(), target.AsSpan());
        return target[..written];
    }

    // Shuffled buffer of n items of itemSize bytes: output byte j*n+i = input byte
    // i*itemSize+j. Trailing bytes beyond n*itemSize are copied verbatim. This is what
    // Task 4's unshuffle must invert.
    private static byte[] ShuffleBytes(byte[] input, int itemSize)
    {
        var n = input.Length / itemSize;
        var output = new byte[input.Length];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < itemSize; j++)
            {
                output[j * n + i] = input[i * itemSize + j];
            }
        }
        for (var k = n * itemSize; k < input.Length; k++)
        {
            output[k] = input[k];
        }
        return output;
    }

    private static int ItemSize(string sampleFormat) => sampleFormat switch
    {
        "UInt8" => 1,
        "UInt16" => 2,
        "UInt32" => 4,
        "Float32" => 4,
        "Float64" => 8,
        _ => throw new ArgumentException($"Unsupported sample format '{sampleFormat}' for pixel byte-size calculation.", nameof(sampleFormat)),
    };

    private byte[] EncodeMono(float[,] values)
    {
        var height = values.GetLength(0);
        var width = values.GetLength(1);
        var itemSize = ItemSize(_sampleFormat);
        var buffer = new byte[(long)height * width * itemSize];
        var little = _byteOrder != "big";
        var offset = 0;
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                WriteSample(buffer, offset, _sampleFormat, values[row, col], little);
                offset += itemSize;
            }
        }
        return buffer;
    }

    private byte[] EncodeRgb(float[,,] values)
    {
        var channels = values.GetLength(0);
        var height = values.GetLength(1);
        var width = values.GetLength(2);
        var itemSize = ItemSize(_sampleFormat);
        var buffer = new byte[(long)channels * height * width * itemSize];
        var little = _byteOrder != "big";
        var offset = 0;
        for (var ch = 0; ch < channels; ch++)
        {
            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    WriteSample(buffer, offset, _sampleFormat, values[ch, row, col], little);
                    offset += itemSize;
                }
            }
        }
        return buffer;
    }

    private static void WriteSample(byte[] buffer, int offset, string sampleFormat, float value, bool little)
    {
        switch (sampleFormat)
        {
            case "UInt8":
                buffer[offset] = (byte)Math.Clamp(Math.Round((double)value, MidpointRounding.AwayFromZero), 0, 255);
                break;
            case "UInt16":
            {
                var v = (ushort)Math.Clamp(Math.Round((double)value, MidpointRounding.AwayFromZero), 0, ushort.MaxValue);
                if (little)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), v);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), v);
                }
                break;
            }
            case "UInt32":
            {
                var v = (uint)Math.Clamp(Math.Round((double)value, MidpointRounding.AwayFromZero), 0, uint.MaxValue);
                if (little)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), v);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), v);
                }
                break;
            }
            case "Float32":
                if (little)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), value);
                }
                else
                {
                    BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(offset, 4), value);
                }
                break;
            case "Float64":
                if (little)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(offset, 8), value);
                }
                else
                {
                    BinaryPrimitives.WriteDoubleBigEndian(buffer.AsSpan(offset, 8), value);
                }
                break;
            default:
                throw new ArgumentException($"Unsupported sample format '{sampleFormat}' for pixel encoding.", nameof(sampleFormat));
        }
    }
}
