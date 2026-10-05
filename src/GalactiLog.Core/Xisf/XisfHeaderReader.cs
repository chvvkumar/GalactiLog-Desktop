using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace GalactiLog.Core.Xisf;

/// <summary>
/// Parsed result of reading a monolithic XISF file's XML header (spec 6.2.1-6.2.3, 6.2.6).
/// All fields except <see cref="Accepted"/>/<see cref="RejectionReason"/> are default/empty
/// when <see cref="Accepted"/> is false.
/// </summary>
public sealed record XisfHeaderResult(
    bool Accepted,
    string? RejectionReason,
    int Width,
    int Height,
    int ChannelCount,
    string SampleFormat,
    string ColorSpace,
    string ByteOrder,
    string? Compression,
    string Location,
    string? ImageTypeAttribute,
    IReadOnlyDictionary<string, string> FitsKeywords,
    IReadOnlyDictionary<string, string> Properties,
    (string Pattern, int Width, int Height)? ColorFilterArray,
    string? InlineOrEmbeddedEncoding,
    byte[]? InlineOrEmbeddedBytes,
    long HeaderTotalLength,
    string? Bounds);

/// <summary>
/// Reads the XML header of a monolithic XISF file (spec 6.2). Performs only header-level
/// validation; pixel/data-block decoding is Task 5's responsibility.
/// </summary>
public static class XisfHeaderReader
{
    private static readonly byte[] Signature = "XISF0100"u8.ToArray();
    private static readonly XNamespace Ns = "http://www.pixinsight.com/xisf";

    // Twin of FitsImageReader.MaxAxisLength: keep the two in step. Applied to width,
    // height and channel count alike so no geometry attribute can drive an allocation
    // or a size multiplication past what a real frame could need.
    private const int MaxAxisLength = 65536;

    // The fixed part of a monolithic XISF file: 8-byte signature, 4-byte header length,
    // 4 reserved bytes.
    private const long PrologueLength = 16L;

    public static XisfHeaderResult Read(Stream stream)
    {
        // Every length check below (and XisfDataBlock's seek to an attachment offset) needs
        // a seekable stream, and a file shorter than the prologue cannot carry one. Both
        // are returned as rejections rather than allowed to throw out of the reader.
        if (!stream.CanSeek)
        {
            return Rejected("XISF stream is not seekable");
        }
        if (stream.Length < PrologueLength)
        {
            return Rejected("not a valid XISF file");
        }

        var signature = new byte[8];
        stream.ReadExactly(signature);
        if (!signature.AsSpan().SequenceEqual(Signature))
        {
            return Rejected("not a valid XISF file");
        }

        Span<byte> lengthAndReserved = stackalloc byte[8];
        stream.ReadExactly(lengthAndReserved);
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthAndReserved[..4]);

        if (PrologueLength + headerLength > stream.Length)
        {
            return Rejected("XISF header length extends past end of file");
        }

        var headerBytes = new byte[headerLength];
        stream.ReadExactly(headerBytes);
        var xmlText = System.Text.Encoding.UTF8.GetString(headerBytes);

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlText);
        }
        catch (XmlException ex)
        {
            return Rejected($"XISF header XML does not parse: {ex.Message}");
        }

        var imageElem = doc.Root?.Element(Ns + "Image");
        if (imageElem is null)
        {
            return Rejected("No Image element in XISF header");
        }

        var geometryAttr = (string?)imageElem.Attribute("geometry");
        var geometryParts = geometryAttr?.Split(':');
        if (geometryParts is not { Length: 3 } ||
            !int.TryParse(geometryParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(geometryParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            !int.TryParse(geometryParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelCount))
        {
            return Rejected("invalid or missing geometry attribute");
        }

        if (width < 1 || height < 1 || channelCount < 1 ||
            width > MaxAxisLength || height > MaxAxisLength || channelCount > MaxAxisLength)
        {
            return Rejected($"geometry out of bounds (1..{MaxAxisLength}): {geometryAttr}");
        }

        var sampleFormat = (string?)imageElem.Attribute("sampleFormat");
        if (string.IsNullOrEmpty(sampleFormat))
        {
            return Rejected("invalid or missing sampleFormat attribute");
        }

        var location = (string?)imageElem.Attribute("location");
        if (string.IsNullOrEmpty(location))
        {
            return Rejected("invalid or missing location attribute");
        }

        var colorSpace = (string?)imageElem.Attribute("colorSpace") ?? "Gray";
        var byteOrder = (string?)imageElem.Attribute("byteOrder") ?? "little";
        var compression = (string?)imageElem.Attribute("compression");
        var imageType = (string?)imageElem.Attribute("imageType");
        // Recorded, not applied (spec 6.2.2): the pixel reader does not rescale by it.
        var bounds = (string?)imageElem.Attribute("bounds");

        var fitsKeywords = new Dictionary<string, string>();
        foreach (var kw in imageElem.Elements(Ns + "FITSKeyword"))
        {
            var name = (string?)kw.Attribute("name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            var rawValue = (string?)kw.Attribute("value") ?? string.Empty;
            fitsKeywords[name] = StripFitsString(rawValue);
        }

        var properties = new Dictionary<string, string>();
        foreach (var prop in imageElem.Elements(Ns + "Property"))
        {
            var id = (string?)prop.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }
            var value = (string?)prop.Attribute("value") ?? prop.Value;
            properties[id] = value.Trim();
        }

        (string Pattern, int Width, int Height)? cfa = null;
        var cfaElem = imageElem.Element(Ns + "ColorFilterArray");
        if (cfaElem is not null)
        {
            var pattern = (string?)cfaElem.Attribute("pattern") ?? string.Empty;
            if (int.TryParse((string?)cfaElem.Attribute("width"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cfaWidth) &&
                int.TryParse((string?)cfaElem.Attribute("height"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cfaHeight))
            {
                cfa = (pattern, cfaWidth, cfaHeight);
            }
        }

        string? inlineOrEmbeddedEncoding = null;
        byte[]? inlineOrEmbeddedBytes = null;

        if (location.StartsWith("attachment:", StringComparison.Ordinal))
        {
            // Split from the bounds test below on purpose: folded into one condition, a
            // location whose offset or size does not parse short-circuits the whole `if`
            // and the file is accepted unchecked.
            var parts = location.Split(':');
            if (parts.Length != 3 ||
                !ulong.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) ||
                !ulong.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                return Rejected("unrecognized attachment location");
            }

            // Overflow-safe: subtracting size from Length first, never adding to position.
            if (size > (ulong)stream.Length || position > (ulong)stream.Length - size)
            {
                return Rejected("attachment offset or size past end of file");
            }
        }
        else if (location.StartsWith("inline:", StringComparison.Ordinal))
        {
            var encoding = location["inline:".Length..];
            var encodedText = imageElem.Value.Trim();
            byte[] decoded;
            try
            {
                decoded = DecodeEncodedText(encodedText, encoding);
            }
            catch (FormatException ex)
            {
                return Rejected($"XISF inline data does not decode: {ex.Message}");
            }
            inlineOrEmbeddedEncoding = encoding;
            inlineOrEmbeddedBytes = decoded;
        }
        else if (location.Equals("embedded", StringComparison.Ordinal))
        {
            var dataElem = imageElem.Element(Ns + "Data");
            if (dataElem is not null)
            {
                var encoding = (string?)dataElem.Attribute("encoding") ?? string.Empty;
                var encodedText = dataElem.Value.Trim();
                byte[] decoded;
                try
                {
                    decoded = DecodeEncodedText(encodedText, encoding);
                }
                catch (FormatException ex)
                {
                    return Rejected($"XISF embedded data does not decode: {ex.Message}");
                }
                inlineOrEmbeddedEncoding = encoding;
                inlineOrEmbeddedBytes = decoded;
            }
        }
        // "url:"/"path:" and anything else: no further action; header-level accepted, Task 5
        // decides whether/how to read pixel data.

        return new XisfHeaderResult(
            Accepted: true,
            RejectionReason: null,
            Width: width,
            Height: height,
            ChannelCount: channelCount,
            SampleFormat: sampleFormat,
            ColorSpace: colorSpace,
            ByteOrder: byteOrder,
            Compression: compression,
            Location: location,
            ImageTypeAttribute: imageType,
            FitsKeywords: fitsKeywords,
            Properties: properties,
            ColorFilterArray: cfa,
            InlineOrEmbeddedEncoding: inlineOrEmbeddedEncoding,
            InlineOrEmbeddedBytes: inlineOrEmbeddedBytes,
            HeaderTotalLength: PrologueLength + headerLength,
            Bounds: bounds);
    }

    /// <summary>
    /// Flattens an accepted header's FITSKeyword and Property entries into the single
    /// raw_headers object (spec 7.1.2). Shared by MetadataExtractor and the CLI's
    /// dump-headers verb so the two can never drift.
    /// </summary>
    public static JsonObject BuildRawHeaders(XisfHeaderResult header)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in header.FitsKeywords)
        {
            obj[key] = JsonValue.Create(value);
        }
        foreach (var (key, value) in header.Properties)
        {
            obj[key] = JsonValue.Create(value);
        }
        return obj;
    }

    private static byte[] DecodeEncodedText(string text, string encoding) => encoding switch
    {
        "base64" => Convert.FromBase64String(text),
        "base16" => Convert.FromHexString(text),
        _ => Array.Empty<byte>(),
    };

    // Mirrors xisf_parser._strip_fits_string: trim, then if both ends are a single quote
    // and the trimmed length is at least 2, strip exactly one leading and trailing quote
    // and trim again.
    private static string StripFitsString(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1].Trim();
        }
        return trimmed;
    }

    private static XisfHeaderResult Rejected(string reason) => new(
        Accepted: false,
        RejectionReason: reason,
        Width: 0,
        Height: 0,
        ChannelCount: 0,
        SampleFormat: string.Empty,
        ColorSpace: string.Empty,
        ByteOrder: string.Empty,
        Compression: null,
        Location: string.Empty,
        ImageTypeAttribute: null,
        FitsKeywords: new Dictionary<string, string>(),
        Properties: new Dictionary<string, string>(),
        ColorFilterArray: null,
        InlineOrEmbeddedEncoding: null,
        InlineOrEmbeddedBytes: null,
        HeaderTotalLength: 0,
        Bounds: null);
}
