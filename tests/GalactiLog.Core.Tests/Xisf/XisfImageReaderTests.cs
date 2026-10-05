using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Core.Xisf;
using Xunit;

namespace GalactiLog.Core.Tests.Xisf;

public class XisfImageReaderTests
{
    [Theory]
    [InlineData("UInt8")]
    [InlineData("UInt16")]
    [InlineData("UInt32")]
    [InlineData("Float32")]
    [InlineData("Float64")]
    public void SupportedSampleFormat_RoundTripsMonoFixture(string sampleFormat)
    {
        var values = new float[,] { { 1, 2 }, { 3, 4 } };
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat(sampleFormat).Pixels(values).Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Decoded, result.Outcome);
        Assert.Null(result.PlanarRgb);
        Assert.NotNull(result.Mono);
        Assert.Equal(values, result.Mono);
    }

    [Theory]
    [InlineData("UInt64")]
    [InlineData("Complex32")]
    [InlineData("Complex64")]
    public void UnsupportedSampleFormat_IsSkippedWithFormatNameInReason(string sampleFormat)
    {
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat(sampleFormat).Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Skipped, result.Outcome);
        Assert.Contains(sampleFormat, result.Reason);
    }

    [Fact]
    public void UnsupportedCompressionCodec_IsSkipped()
    {
        var values = new float[,] { { 1, 2 }, { 3, 4 } };
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").Pixels(values).Compression("zstd").Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Skipped, result.Outcome);
        Assert.Contains("unsupported compression", result.Reason);
    }

    [Theory]
    [InlineData("url:http://example.com/data.bin")]
    [InlineData("path:/some/external/file.raw")]
    public void ExternalLocationForm_IsSkipped(string location)
    {
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").LocationOverride(location).Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Skipped, result.Outcome);
        Assert.Equal("external data reference not supported", result.Reason);
    }

    [Fact]
    public void CIELabColorSpace_IsSkipped()
    {
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").ColorSpace("CIELab").Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Skipped, result.Outcome);
        Assert.Contains("CIELab", result.Reason);
    }

    [Fact]
    public void UnsupportedChannelCount_IsSkipped()
    {
        var stream = new XisfBuilder().Geometry(2, 2, 2).SampleFormat("UInt16").Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Skipped, result.Outcome);
        Assert.Contains("2", result.Reason);
    }

    [Fact]
    public void ThreeChannelFixture_ProducesPlanarRgbNotMono()
    {
        var channels = new float[,,]
        {
            { { 1, 2 }, { 3, 4 } },
            { { 5, 6 }, { 7, 8 } },
            { { 9, 10 }, { 11, 12 } },
        };
        var stream = new XisfBuilder().Geometry(2, 2, 3).SampleFormat("UInt16").PixelsPlanarRgb(channels).Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Decoded, result.Outcome);
        Assert.Null(result.Mono);
        Assert.NotNull(result.PlanarRgb);
        Assert.Equal(channels, result.PlanarRgb);
    }

    [Fact]
    public void OneChannelFixture_ProducesMonoNotPlanarRgb()
    {
        var values = new float[,] { { 1, 2 }, { 3, 4 } };
        var stream = new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16").Pixels(values).Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Decoded, result.Outcome);
        Assert.NotNull(result.Mono);
        Assert.Null(result.PlanarRgb);
    }

    [Fact]
    public void BigEndianByteOrder_DecodesCorrectly()
    {
        var values = new float[,] { { 1, 2 }, { 300, 4000 } };
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .ByteOrderAttribute("big")
            .Pixels(values)
            .Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted);
        Assert.Equal("big", header.ByteOrder);

        var result = XisfImageReader.Read(stream, header);

        Assert.Equal(XisfPixelOutcome.Decoded, result.Outcome);
        Assert.Equal(values, result.Mono);
    }

    [Fact]
    public void DeclaredSizeMismatch_IsRejectedNotSkipped()
    {
        var values = new float[,] { { 1, 2, 3, 4 }, { 5, 6, 7, 8 }, { 9, 10, 11, 12 }, { 13, 14, 15, 16 } };
        var stream = new XisfBuilder().Geometry(4, 4, 1).SampleFormat("UInt16").Pixels(values).Compression("zlib").Build();
        var mutatedStream = MutateDeclaredUncompressedSizeSmaller(stream);
        var header = XisfHeaderReader.Read(mutatedStream);
        Assert.True(header.Accepted);

        var result = XisfImageReader.Read(mutatedStream, header);

        Assert.Equal(XisfPixelOutcome.Rejected, result.Outcome);
        Assert.Contains("declared size does not match", result.Reason);
        Assert.Null(result.Mono);
        Assert.Null(result.PlanarRgb);
    }

    // Rewrites the compression attribute's uncompressedSize field to a smaller value of the
    // same digit-length (zero-padded), so the header's byte length - and therefore the
    // attachment offset that follows it - does not shift. Mirrors XisfDataBlockTests's helper
    // of the same shape.
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
}
