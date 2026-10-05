using System.Text;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Core.Xisf;
using Xunit;

namespace GalactiLog.Core.Tests.Xisf;

public class XisfHeaderReaderTests
{
    [Fact]
    public void MinimalValidMonoFixture_IsAccepted()
    {
        var values = new float[,] { { 1, 2 }, { 3, 4 } };
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").Pixels(values).Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Null(result.RejectionReason);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(1, result.ChannelCount);
        Assert.Equal("UInt16", result.SampleFormat);
        Assert.Equal("Gray", result.ColorSpace);
        Assert.Equal("little", result.ByteOrder);
        Assert.StartsWith("attachment:", result.Location);
    }

    [Fact]
    public void WrongSignature_IsRejected()
    {
        var stream = new XisfBuilder()
            .Geometry(1, 1, 1)
            .RawSignature(Encoding.ASCII.GetBytes("XISFBAD0"))
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("not a valid XISF file", result.RejectionReason);
    }

    [Fact]
    public void HeaderLengthPastEndOfFile_IsRejected()
    {
        var stream = new XisfBuilder().Geometry(1, 1, 1).Build();
        var bytes = stream.ToArray();
        // Overwrite the header-length field (bytes 8..11, little-endian uint32) with a
        // value far larger than the actual file size.
        BitConverterOverwriteLength(bytes, (uint)(bytes.Length + 1_000_000));

        var result = XisfHeaderReader.Read(new MemoryStream(bytes));

        Assert.False(result.Accepted);
        Assert.Contains("past end of file", result.RejectionReason);
    }

    [Fact]
    public void InvalidXml_IsRejected()
    {
        var stream = new XisfBuilder().Geometry(1, 1, 1).RawHeaderXml("<Image geometry=\"1:1:1\"").Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("does not parse", result.RejectionReason);
    }

    [Fact]
    public void NoImageElement_IsRejected()
    {
        var stream = new XisfBuilder().Geometry(1, 1, 1).RawHeaderXml("<NotImage/>").Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("No Image element", result.RejectionReason);
    }

    [Fact]
    public void AttachmentLocationOverride_PastEndOfFile_IsRejected()
    {
        var stream = new XisfBuilder()
            .Geometry(1, 1, 1)
            .LocationOverride("attachment:999999999:10")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("past end of file", result.RejectionReason);
    }

    [Fact]
    public void AttachmentLocationOverride_PositionNearUlongMax_DoesNotWrapAndIsRejected()
    {
        var stream = new XisfBuilder()
            .Geometry(1, 1, 1)
            .LocationOverride($"attachment:{ulong.MaxValue}:10")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("past end of file", result.RejectionReason);
    }

    [Fact]
    public void InlineBase64_CorruptPayload_IsRejectedNotThrown()
    {
        var stream = new XisfBuilder()
            .Geometry(1, 1, 1)
            .RawHeaderXml("<Image geometry=\"1:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"inline:base64\">not-valid-base64!!!</Image>")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("does not decode", result.RejectionReason);
    }

    [Fact]
    public void ColorFilterArray_NonNumericWidthHeight_LeavesCfaNullWithoutRejectingFile()
    {
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .RawHeaderXml("<Image geometry=\"2:2:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"url:file:///x.xisf\">" +
                "<ColorFilterArray pattern=\"RGGB\" width=\"bad\" height=\"bad\"/></Image>")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Null(result.ColorFilterArray);
    }

    [Fact]
    public void UrlLocationOverride_IsAcceptedAsHeaderLevel()
    {
        var stream = new XisfBuilder()
            .Geometry(1, 1, 1)
            .LocationOverride("url:file:///elsewhere.xisf")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Equal("url:file:///elsewhere.xisf", result.Location);
    }

    [Fact]
    public void FitsKeyword_PreQuotedValue_Unquotes()
    {
        var stream = new XisfBuilder()
            .Geometry(1, 1, 1)
            .FitsKeyword("OBJECT", "'M 31'")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Equal("M 31", result.FitsKeywords["OBJECT"]);
    }

    [Fact]
    public void Property_AppearsInPropertiesById()
    {
        var stream = new XisfBuilder()
            .Geometry(1, 1, 1)
            .Property("Observation:Object:Name", "String", "M31")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Equal("M31", result.Properties["Observation:Object:Name"]);
    }

    [Fact]
    public void ColorFilterArray_RoundTrips()
    {
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .ColorFilterArray("RGGB", 2, 2)
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Equal(("RGGB", 2, 2), result.ColorFilterArray);
    }

    [Fact]
    public void InlineBase64_DecodesBytesExactly()
    {
        var values = new float[,] { { 5, 6 }, { 7, 8 } };
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .Pixels(values)
            .InlineEncoding("base64")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Equal("inline:base64", result.Location);
        Assert.Equal(ExpectedUInt16Bytes(5, 6, 7, 8), result.InlineOrEmbeddedBytes);
    }

    [Fact]
    public void Embedded_Base16_DecodesBytesExactly()
    {
        var values = new float[,] { { 9, 10 }, { 11, 12 } };
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .Pixels(values)
            .Embedded("base16")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Equal("embedded", result.Location);
        Assert.Equal("base16", result.InlineOrEmbeddedEncoding);
        Assert.Equal(ExpectedUInt16Bytes(9, 10, 11, 12), result.InlineOrEmbeddedBytes);
    }

    [Fact]
    public void ImageTypeAttribute_RoundTripsWhenSet()
    {
        var stream = new XisfBuilder().Geometry(1, 1, 1).ImageTypeAttribute("Flat").Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Equal("Flat", result.ImageTypeAttribute);
    }

    [Fact]
    public void ImageTypeAttribute_IsNullWhenNeverSet()
    {
        var stream = new XisfBuilder().Geometry(1, 1, 1).Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Null(result.ImageTypeAttribute);
    }

    private static byte[] ExpectedUInt16Bytes(params ushort[] values)
    {
        var expected = new byte[values.Length * 2];
        var offset = 0;
        foreach (var v in values)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(offset, 2), v);
            offset += 2;
        }
        return expected;
    }

    private static void BitConverterOverwriteLength(byte[] bytes, uint newLength)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), newLength);
    }

    // Review item 3: ReadExactly threw EndOfStreamException on anything shorter than the
    // 16-byte prologue, so a 5-byte ".xisf" crashed the CLI instead of being rejected.
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    public void StreamShorterThanPrologue_IsRejectedWithoutThrowing(int length)
    {
        var result = XisfHeaderReader.Read(new MemoryStream(new byte[length]));

        Assert.False(result.Accepted);
        Assert.Equal("not a valid XISF file", result.RejectionReason);
    }

    [Fact]
    public void NonSeekableStream_IsRejectedWithoutThrowing()
    {
        var result = XisfHeaderReader.Read(new NonSeekableStream(new byte[64]));

        Assert.False(result.Accepted);
        Assert.Equal("XISF stream is not seekable", result.RejectionReason);
    }

    // Review item 4: geometry was parsed but never range-checked, so a negative or
    // int.MaxValue dimension reached the pixel reader's size arithmetic.
    [Theory]
    [InlineData(-2, -2, 1)]
    [InlineData(2147483647, 1, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(65537, 1, 1)]
    public void GeometryOutOfBounds_IsRejectedWithoutThrowing(int width, int height, int channels)
    {
        var stream = new XisfBuilder().Geometry(width, height, channels).SampleFormat("UInt16").Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("geometry out of bounds", result.RejectionReason);
    }

    // Review item 5: folded into one condition, a location whose offset or size did not
    // parse short-circuited the whole test and the file was accepted unchecked.
    [Theory]
    [InlineData("attachment:5:-3")]
    [InlineData("attachment:notanumber:8")]
    [InlineData("attachment:5")]
    public void UnparseableAttachmentLocation_IsRejectedAtHeaderLevel(string location)
    {
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .LocationOverride(location)
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Equal("unrecognized attachment location", result.RejectionReason);
    }

    // Review item 12: the bounds attribute (spec 6.2.2) is recorded, not applied.
    [Fact]
    public void BoundsAttribute_IsRecordedOnTheResult()
    {
        var stream = new XisfBuilder()
            .RawHeaderXml("<Image geometry=\"2:2:1\" sampleFormat=\"Float32\" colorSpace=\"Gray\" " +
                          "location=\"attachment:64:16\" bounds=\"0:65535\"/>")
            .Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted, result.RejectionReason);
        Assert.Equal("0:65535", result.Bounds);
    }

    [Fact]
    public void NoBoundsAttribute_LeavesBoundsNull()
    {
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").Pixels(new float[,] { { 1, 2 }, { 3, 4 } }).Build();

        var result = XisfHeaderReader.Read(stream);

        Assert.True(result.Accepted, result.RejectionReason);
        Assert.Null(result.Bounds);
    }

    // Minimal forward-only stream: CanSeek false, Length/Seek unsupported.
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] bytes) => _inner = new MemoryStream(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
