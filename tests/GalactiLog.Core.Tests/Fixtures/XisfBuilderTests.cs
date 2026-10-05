using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using K4os.Compression.LZ4;
using Xunit;

namespace GalactiLog.Core.Tests.Fixtures;

public class XisfBuilderTests
{
    private static readonly XNamespace Ns = "http://www.pixinsight.com/xisf";

    [Fact]
    public void Build_SignatureAndHeaderLength_MatchXmlByteLength()
    {
        var stream = new XisfBuilder().Geometry(2, 2, 1).Build();
        var bytes = stream.ToArray();

        Assert.Equal("XISF0100", Encoding.ASCII.GetString(bytes, 0, 8));

        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var xmlText = Encoding.UTF8.GetString(bytes, 16, headerLength);

        Assert.Equal(headerLength, Encoding.UTF8.GetByteCount(xmlText));
        // Parses cleanly, confirming the declared length lands exactly on well-formed XML.
        XDocument.Parse(xmlText);
    }

    [Fact]
    public void Pixels_Mono_LocationRoundTripsToAttachmentOffsetAndLength()
    {
        var values = new float[,] { { 1, 2 }, { 3, 4 } };
        var stream = new XisfBuilder().Geometry(2, 2, 1).Pixels(values).Build();

        var bytes = stream.ToArray();
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes, 16, headerLength));
        var location = (string)doc.Root!.Element(Ns + "Image")!.Attribute("location")!;

        var parts = location.Split(':');
        Assert.Equal("attachment", parts[0]);
        var offset = long.Parse(parts[1], CultureInfo.InvariantCulture);
        var length = long.Parse(parts[2], CultureInfo.InvariantCulture);

        Assert.Equal(16 + headerLength, offset);
        Assert.Equal(bytes.Length - offset, length);
    }

    [Fact]
    public void Compression_Zlib_DecompressesToOriginalSampleBytes()
    {
        var values = new float[,] { { 10, 20, 30 }, { 40, 50, 60 } };
        var stream = new XisfBuilder()
            .Geometry(3, 2, 1)
            .SampleFormat("UInt16")
            .Pixels(values)
            .Compression("zlib")
            .Build();

        var bytes = stream.ToArray();
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var attached = bytes.AsSpan(16 + headerLength).ToArray();

        using var compressed = new MemoryStream(attached);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);

        Assert.Equal(ExpectedUInt16Bytes(10, 20, 30, 40, 50, 60), decompressed.ToArray());
    }

    [Fact]
    public void Compression_Lz4Shuffled_DecodesAndUnshufflesToOriginalBytes()
    {
        var values = new float[,] { { 1000, 2000 }, { 3000, 4000 } };
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .Pixels(values)
            .Compression("lz4+sh")
            .Build();

        var bytes = stream.ToArray();
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes, 16, headerLength));
        var compression = (string)doc.Root!.Element(Ns + "Image")!.Attribute("compression")!;
        var parts = compression.Split(':');
        var uncompressedSize = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var itemSize = int.Parse(parts[2], CultureInfo.InvariantCulture);

        var attached = bytes.AsSpan(16 + headerLength).ToArray();
        var decoded = new byte[uncompressedSize];
        LZ4Codec.Decode(attached, decoded);

        // Inline unshuffle, independent of the builder's own (private) implementation:
        // double-checks the builder's shuffle is the exact inverse the spec's unshuffle
        // expects.
        var n = uncompressedSize / itemSize;
        var unshuffled = new byte[uncompressedSize];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < itemSize; j++)
            {
                unshuffled[i * itemSize + j] = decoded[j * n + i];
            }
        }
        for (var k = n * itemSize; k < uncompressedSize; k++)
        {
            unshuffled[k] = decoded[k];
        }

        Assert.Equal(ExpectedUInt16Bytes(1000, 2000, 3000, 4000), unshuffled);
    }

    [Fact]
    public void RawSignature_FirstEightBytesDifferFromDefault()
    {
        var custom = Encoding.ASCII.GetBytes("XISFBAD0");
        var stream = new XisfBuilder().Geometry(1, 1, 1).RawSignature(custom).Build();

        var bytes = stream.ToArray();
        Assert.Equal(custom, bytes[..8]);
        Assert.NotEqual("XISF0100", Encoding.ASCII.GetString(bytes, 0, 8));
    }

    [Fact]
    public void Embedded_Base64_DecodesToOriginalBytes()
    {
        var values = new float[,] { { 5, 6 }, { 7, 8 } };
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .Pixels(values)
            .Embedded("base64")
            .Build();

        var bytes = stream.ToArray();
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(bytes, 16, headerLength));
        var dataElement = doc.Root!.Element(Ns + "Image")!.Element(Ns + "Data")!;

        Assert.Equal("base64", (string)dataElement.Attribute("encoding")!);
        Assert.Equal(ExpectedUInt16Bytes(5, 6, 7, 8), Convert.FromBase64String(dataElement.Value));
    }

    private static byte[] ExpectedUInt16Bytes(params ushort[] values)
    {
        var expected = new byte[values.Length * 2];
        var offset = 0;
        foreach (var v in values)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(offset, 2), v);
            offset += 2;
        }
        return expected;
    }
}
