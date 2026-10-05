using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Core.Xisf;
using Xunit;

namespace GalactiLog.Core.Tests.Xisf;

public class XisfDataBlockTests
{
    [Fact]
    public void Unshuffle_KnownVector_ProducesExactByteOrder()
    {
        // itemSize 2, 4 items. Raw items: [1,2] [3,4] [5,6] [7,8]. Shuffled buffer stores all
        // byte-0 values first (1,3,5,7), then all byte-1 values (2,4,6,8).
        byte[] shuffled = { 1, 3, 5, 7, 2, 4, 6, 8 };

        var result = XisfDataBlock.Unshuffle(shuffled, 2);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, result);
    }

    [Fact]
    public void Unshuffle_TrailingBytes_CopiedVerbatim()
    {
        // n = 3/2 = 1 whole item (2 bytes); 1 trailing byte copied as-is.
        byte[] shuffled = { 10, 20, 99 };

        var result = XisfDataBlock.Unshuffle(shuffled, 2);

        Assert.Equal(new byte[] { 10, 20, 99 }, result);
    }

    [Fact]
    public void Unshuffle_KnownVector_ItemSize4()
    {
        // itemSize 4, 3 items: [1,2,3,4] [5,6,7,8] [9,10,11,12]. Shuffled stores byte-0
        // group (1,5,9), byte-1 group (2,6,10), byte-2 group (3,7,11), byte-3 group (4,8,12).
        byte[] shuffled = { 1, 5, 9, 2, 6, 10, 3, 7, 11, 4, 8, 12 };

        var result = XisfDataBlock.Unshuffle(shuffled, 4);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, result);
    }

    [Fact]
    public void Unshuffle_KnownVector_ItemSize8()
    {
        // itemSize 8, 2 items: [1..8] [9..16]. Shuffled interleaves byte-j of each item:
        // (1,9), (2,10), (3,11), (4,12), (5,13), (6,14), (7,15), (8,16).
        byte[] shuffled = { 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15, 8, 16 };

        var result = XisfDataBlock.Unshuffle(shuffled, 8);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, result);
    }

    [Fact]
    public void Compression_ZlibShWithFloat64_DecodesToSameBytesAsBaseline()
    {
        // Exercises the +sh path at itemSize 8 (Float64), not just the itemSize-2 (UInt16)
        // cases covered by the codec theory below.
        var values = new float[,] { { 1.5f, 2.5f, 3.5f }, { 4.5f, 5.5f, 6.5f }, { 7.5f, 8.5f, 9.5f } };

        var baselineBytes = ReadDecodedBytes(
            new XisfBuilder().Geometry(3, 3, 1).SampleFormat("Float64").Pixels(values).Build(),
            bytesPerSample: 8);

        var shuffledBytes = ReadDecodedBytes(
            new XisfBuilder().Geometry(3, 3, 1).SampleFormat("Float64").Pixels(values).Compression("zlib+sh").Build(),
            bytesPerSample: 8);

        Assert.Equal(baselineBytes, shuffledBytes);
    }

    [Theory]
    [InlineData("zlib:99999999999999")]
    [InlineData("zlib:-1")]
    public void AbsurdOrNegativeUncompressedSize_ReturnsFailureWithoutException(string compressionAttr)
    {
        var stream = BuildAttachmentFixture("UInt16", compressionAttr, new byte[] { 1, 2, 3 });
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, 8);

        Assert.NotEqual(XisfPixelOutcome.Decoded, outcome);
        Assert.Null(bytes);
    }

    // Review item 5/6: the authoritative rejection for an unparseable attachment location is
    // now at header level; XisfDataBlock's own check survives as a defensive re-check, which
    // this test reaches by rewriting Location on an already-accepted header.
    [Fact]
    public void NegativeAttachmentSize_DefensiveRecheckReturnsFailureWithoutException()
    {
        var values = new float[,] { { 1, 2 }, { 3, 4 } };
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").Pixels(values).Build();
        var accepted = XisfHeaderReader.Read(stream);
        Assert.True(accepted.Accepted);

        var header = accepted with { Location = "attachment:5:-3" };
        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, 8);

        // XisfDataBlock parses the two fields as long, so "-3" parses and is caught by the
        // range test: a spec 6.2.6 rejection, not a skip.
        Assert.Equal(XisfPixelOutcome.Rejected, outcome);
        Assert.Null(bytes);
        Assert.Contains("attachment", failureReason);
    }

    [Fact]
    public void ShuffleItemSizeZero_ReturnsFailureWithoutException()
    {
        var stream = BuildAttachmentFixture("UInt16", "zlib+sh:1024:0", new byte[] { 1, 2, 3 }, width: 32, height: 16);
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, 1024);

        Assert.NotEqual(XisfPixelOutcome.Decoded, outcome);
        Assert.Null(bytes);
    }

    [Fact]
    public void ZlibPayloadDecodesToMoreThanDeclared_ReturnsFailureMentioningMismatch()
    {
        byte[] realPayload = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var compressed = ZlibCompressRaw(realPayload);
        // Declare only 5 of the real 10 decompressed bytes.
        var stream = BuildAttachmentFixture("UInt16", "zlib:5", compressed);
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, 5);

        Assert.Equal(XisfPixelOutcome.Rejected, outcome);
        Assert.Null(bytes);
        Assert.Contains("declared size does not match", failureReason);
    }

    [Fact]
    public void UncompressedAttachment_DecodesToExactStoredBytes()
    {
        var values = new float[,] { { 1, 2 }, { 3, 4 } };
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").Pixels(values).Build();
        var fileBytes = stream.ToArray();

        stream.Position = 0;
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var (position, size) = ParseAttachment(header.Location);
        var expectedBytes = fileBytes.AsSpan((int)position, (int)size).ToArray();

        stream.Position = 0;
        var expectedSize = (long)header.Width * header.Height * header.ChannelCount * 2;
        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, expectedSize);

        Assert.Equal(XisfPixelOutcome.Decoded, outcome);
        Assert.Equal(expectedBytes, bytes);
    }

    [Theory]
    [InlineData("zlib")]
    [InlineData("lz4")]
    [InlineData("lz4+sh")]
    [InlineData("zlib+sh")]
    public void CompressedFixture_DecodesToSameBytesAsUncompressedBaseline(string codec)
    {
        var values = new float[,] { { 10, 20, 30 }, { 40, 50, 60 }, { 70, 80, 90 } };

        var baselineBytes = ReadDecodedBytes(
            new XisfBuilder().Geometry(3, 3, 1).SampleFormat("UInt16").Pixels(values).Build());

        var compressedBytes = ReadDecodedBytes(
            new XisfBuilder().Geometry(3, 3, 1).SampleFormat("UInt16").Pixels(values).Compression(codec).Build());

        Assert.Equal(baselineBytes, compressedBytes);
    }

    [Fact]
    public void UnsupportedCodec_ReturnsFailureMentioningUnsupportedCompression()
    {
        var values = new float[,] { { 1, 2 }, { 3, 4 } };
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").Pixels(values).Compression("zstd").Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var expectedSize = (long)header.Width * header.Height * header.ChannelCount * 2;
        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, expectedSize);

        Assert.Equal(XisfPixelOutcome.Skipped, outcome);
        Assert.Null(bytes);
        Assert.Contains("unsupported compression", failureReason);
    }

    [Fact]
    public void DeclaredSizeSmallerThanActual_ReturnsFailureMentioningDeclaredSizeMismatch()
    {
        var values = new float[,] { { 1, 2, 3, 4 }, { 5, 6, 7, 8 }, { 9, 10, 11, 12 }, { 13, 14, 15, 16 } };
        var stream = new XisfBuilder().Geometry(4, 4, 1).SampleFormat("UInt16").Pixels(values).Compression("zlib").Build();
        var mutatedStream = MutateDeclaredUncompressedSizeSmaller(stream);

        var header = XisfHeaderReader.Read(mutatedStream);
        Assert.True(header.Accepted);

        var expectedSize = (long)header.Width * header.Height * header.ChannelCount * 2;
        var (outcome, failureReason, bytes) = XisfDataBlock.Read(mutatedStream, header, expectedSize);

        Assert.Equal(XisfPixelOutcome.Rejected, outcome);
        Assert.Null(bytes);
        Assert.Contains("declared size does not match", failureReason);
    }

    private static byte[] ReadDecodedBytes(MemoryStream stream, int bytesPerSample = 2)
    {
        stream.Position = 0;
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);
        stream.Position = 0;
        var expectedSize = (long)header.Width * header.Height * header.ChannelCount * bytesPerSample;
        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, expectedSize);
        Assert.Equal(XisfPixelOutcome.Decoded, outcome);
        return bytes!;
    }

    // Hand-assembles a minimal monolithic XISF stream with an explicit, already-correct
    // compression attribute string and attachment bytes, bypassing XisfBuilder's own
    // Compression()/Pixels() encoding entirely. Used for fixtures whose compression
    // attribute must carry a value XisfBuilder cannot itself produce (an absurd, negative,
    // or otherwise attacker-controlled uncompressedSize/itemSize).
    private static MemoryStream BuildAttachmentFixture(
        string sampleFormat, string compressionAttr, byte[] attachmentBytes, int width = 2, int height = 2, int channelCount = 1)
    {
        const string placeholder = "0000000000";
        var imageXml =
            $"<Image geometry=\"{width}:{height}:{channelCount}\" sampleFormat=\"{sampleFormat}\" colorSpace=\"Gray\" " +
            $"location=\"attachment:{placeholder}:{attachmentBytes.Length}\" compression=\"{compressionAttr}\" byteOrder=\"little\"></Image>";
        var placeholderXml = $"<xisf xmlns=\"http://www.pixinsight.com/xisf\">{imageXml}</xisf>";
        var headerLength = Encoding.UTF8.GetByteCount(placeholderXml);
        var realOffset = 16 + headerLength;
        var finalXml = placeholderXml.Replace(
            $"attachment:{placeholder}:{attachmentBytes.Length}",
            $"attachment:{realOffset.ToString("D10", CultureInfo.InvariantCulture)}:{attachmentBytes.Length}");
        var headerBytes = Encoding.UTF8.GetBytes(finalXml);

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("XISF0100"));
        Span<byte> lengthField = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthField, (uint)headerBytes.Length);
        ms.Write(lengthField);
        ms.Write(new byte[4]); // reserved
        ms.Write(headerBytes);
        ms.Write(attachmentBytes);
        return new MemoryStream(ms.ToArray());
    }

    private static byte[] ZlibCompressRaw(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }

    private static (long Position, long Size) ParseAttachment(string location)
    {
        var parts = location.Split(':');
        return (long.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    // Rewrites the compression attribute's uncompressedSize field to a smaller value of the
    // same digit-length (zero-padded), so the header's byte length - and therefore the
    // attachment offset that follows it - does not shift.
    private static MemoryStream MutateDeclaredUncompressedSizeSmaller(MemoryStream original)
    {
        var bytes = original.ToArray();
        var headerLength = BitConverter.ToUInt32(bytes, 8);
        var headerBytes = bytes.AsSpan(16, (int)headerLength).ToArray();
        var headerText = Encoding.UTF8.GetString(headerBytes);

        var newHeaderText = Regex.Replace(
            headerText,
            "(compression=\"[A-Za-z0-9+]+:)(\\d+)",
            m =>
            {
                var digits = m.Groups[2].Value;
                var value = long.Parse(digits, CultureInfo.InvariantCulture);
                var smaller = value / 2;
                return m.Groups[1].Value + smaller.ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0');
            });

        var newHeaderBytes = Encoding.UTF8.GetBytes(newHeaderText);
        Assert.Equal(headerBytes.Length, newHeaderBytes.Length);

        var result = (byte[])bytes.Clone();
        newHeaderBytes.CopyTo(result.AsSpan(16));
        return new MemoryStream(result);
    }

    // Review item 16: both malformed-compression-attribute paths reported "unrecognized
    // location form", which pointed at the wrong attribute entirely.
    [Theory]
    [InlineData("zlib")]
    [InlineData("zlib:notanumber")]
    public void MalformedCompressionAttribute_ReportsCompressionNotLocation(string compressionAttr)
    {
        var stream = BuildAttachmentFixture("UInt16", compressionAttr, new byte[] { 1, 2, 3 });
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted, header.RejectionReason);

        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, 8);

        Assert.Equal(XisfPixelOutcome.Skipped, outcome);
        Assert.Null(bytes);
        Assert.Equal("unrecognized compression attribute", failureReason);
    }

    [Fact]
    public void MalformedShuffleItemSize_ReportsCompressionNotLocation()
    {
        var stream = BuildAttachmentFixture("UInt16", "zlib+sh:8:3", new byte[] { 1, 2, 3 });
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted, header.RejectionReason);

        var (outcome, failureReason, bytes) = XisfDataBlock.Read(stream, header, 8);

        Assert.Equal(XisfPixelOutcome.Skipped, outcome);
        Assert.Null(bytes);
        Assert.Equal("unrecognized compression attribute", failureReason);
    }

    // Review item 17: a zero or negative itemSize divided by zero / produced a negative
    // item count inside the public Unshuffle helper.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Unshuffle_NonPositiveItemSize_Throws(int itemSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => XisfDataBlock.Unshuffle(new byte[] { 1, 2, 3, 4 }, itemSize));
    }
}
