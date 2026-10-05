using GalactiLog.Core.Fits;
using GalactiLog.Core.Tests.Fixtures;
using Xunit;

namespace GalactiLog.Core.Tests.Fits;

public class FitsImageReaderTests
{
    private static FitsHeaderResult ReadHeader(Stream stream)
    {
        var header = FitsHeaderReader.Read(stream);
        Assert.True(header.Accepted, header.RejectionReason);
        return header;
    }

    // FitsBuilder.Pixels only encodes BITPIX values it can round-trip (8/16/32/-32/-64), so
    // header-only-degradation fixtures for shapes/BITPIX values it can't produce (here,
    // BITPIX 64 and NAXIS=1 with no matching Pixels call) pad in a raw zero-filled data
    // block themselves to satisfy FitsHeaderReader's truncation check.
    private static MemoryStream AppendZeroDataBlock(MemoryStream stream)
    {
        var bytes = stream.ToArray();
        var padded = new byte[bytes.Length + 2880];
        Array.Copy(bytes, padded, bytes.Length);
        return new MemoryStream(padded);
    }

    [Fact]
    public void Read_Bitpix8_IdentityScaling_DecodesRawBytesAsPhysicalValues()
    {
        var pixels = new float[,] { { 0, 1 }, { 128, 255 } };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 8L).Card("NAXIS", 2L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L)
            .Pixels(8, naxis1: 2, naxis2: 2, pixels)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.True(result.HasPixelData);
        Assert.NotNull(result.Mono);
        Assert.Null(result.PlanarRgb);
        Assert.Equal(pixels[0, 0], result.Mono![0, 0]);
        Assert.Equal(pixels[0, 1], result.Mono[0, 1]);
        Assert.Equal(pixels[1, 0], result.Mono[1, 0]);
        Assert.Equal(pixels[1, 1], result.Mono[1, 1]);
    }

    [Fact]
    public void Read_Bitpix16_UnsignedEncoding_MapsRawToPhysicalCorrectly()
    {
        // raw -32768 (0x8000) -> 0.0, raw 0 -> 32768.0, raw 32767 -> 65535.0
        var physicalInput = new float[,] { { 0.0f, 32768.0f, 65535.0f }, { 0, 0, 0 } };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 3L).Card("NAXIS2", 2L)
            .Card("BZERO", 32768.0).Card("BSCALE", 1.0)
            .Pixels(16, naxis1: 3, naxis2: 2, physicalInput, bzero: 32768, bscale: 1)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.True(result.HasPixelData);
        Assert.Equal(0.0f, result.Mono![0, 0]);
        Assert.Equal(32768.0f, result.Mono[0, 1]);
        Assert.Equal(65535.0f, result.Mono[0, 2]);
    }

    [Fact]
    public void Read_Bitpix32_RoundTripsThroughBuilder()
    {
        var pixels = new float[,] { { -100000, 0 }, { 42, 2000000 } };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 32L).Card("NAXIS", 2L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L)
            .Pixels(32, naxis1: 2, naxis2: 2, pixels)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.True(result.HasPixelData);
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                Assert.Equal(pixels[row, col], result.Mono![row, col], 3);
            }
        }
    }

    [Fact]
    public void Read_BitpixMinus32_RoundTripsThroughBuilder()
    {
        var pixels = new float[,] { { 1.5f, -2.25f }, { 0.0f, 3.75f } };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", -32L).Card("NAXIS", 2L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L)
            .Pixels(-32, naxis1: 2, naxis2: 2, pixels)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.True(result.HasPixelData);
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                Assert.Equal(pixels[row, col], result.Mono![row, col], 3);
            }
        }
    }

    [Fact]
    public void Read_BitpixMinus64_RoundTripsThroughBuilder()
    {
        var pixels = new float[,] { { 1.5f, -2.25f }, { 0.0f, 3.75f } };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", -64L).Card("NAXIS", 2L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L)
            .Pixels(-64, naxis1: 2, naxis2: 2, pixels)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.True(result.HasPixelData);
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                Assert.Equal(pixels[row, col], result.Mono![row, col], 3);
            }
        }
    }

    [Fact]
    public void Read_Bitpix16_BigEndianByteOrder_NotLittleEndian()
    {
        // raw 0x0102 = 258 big-endian; if misread little-endian it would be 0x0201 = 513.
        var pixels = new float[,] { { 258 } };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 1L).Card("NAXIS2", 1L)
            .Pixels(16, naxis1: 1, naxis2: 1, pixels)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.Equal(258.0f, result.Mono![0, 0]);
        Assert.NotEqual(513.0f, result.Mono[0, 0]);
    }

    [Fact]
    public void Read_BlankOnIntegerBitpix_DecodesToZeroRegardlessOfScaling()
    {
        var pixels = new float[,] { { -1, 5 } };
        // -1 will be written as raw sample -1 via BZERO/BSCALE=identity, then we mark
        // BLANK=-1 so the reader must zero it despite BZERO/BSCALE.
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 1L)
            .Card("BLANK", -1L).Card("BZERO", 100.0).Card("BSCALE", 2.0)
            .Pixels(16, naxis1: 2, naxis2: 1, pixels, bzero: 0, bscale: 1)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.Equal(0.0f, result.Mono![0, 0]);
        Assert.Equal(100.0 + 2.0 * 5, result.Mono[0, 1], 3);
    }

    [Fact]
    public void Read_Naxis3Equals3_DecodesPlanarRgbAndLeavesMonoNull()
    {
        var r = new float[,] { { 1, 2 }, { 3, 4 } };
        var g = new float[,] { { 5, 6 }, { 7, 8 } };
        var b = new float[,] { { 9, 10 }, { 11, 12 } };
        var channelFirst = new float[3, 2, 2];
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                channelFirst[0, row, col] = r[row, col];
                channelFirst[1, row, col] = g[row, col];
                channelFirst[2, row, col] = b[row, col];
            }
        }

        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 3L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L).Card("NAXIS3", 3L)
            .PixelsPlanarRgb(16, naxis1: 2, naxis2: 2, channelFirst)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.True(result.HasPixelData);
        Assert.Null(result.Mono);
        Assert.NotNull(result.PlanarRgb);
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                Assert.Equal(r[row, col], result.PlanarRgb![0, row, col]);
                Assert.Equal(g[row, col], result.PlanarRgb[1, row, col]);
                Assert.Equal(b[row, col], result.PlanarRgb[2, row, col]);
            }
        }
    }

    [Fact]
    public void Read_UnsupportedNaxisShape_ReturnsNoPixelDataWithoutThrowing()
    {
        using var stream = AppendZeroDataBlock(new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 1L)
            .Card("NAXIS1", 4L)
            .Build());
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.False(result.HasPixelData);
        Assert.Contains("NAXIS", result.SkipReason);
        Assert.Null(result.Mono);
        Assert.Null(result.PlanarRgb);
    }

    [Fact]
    public void Read_Bitpix64_ReturnsNoPixelDataWithoutThrowing()
    {
        using var stream = AppendZeroDataBlock(new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 64L).Card("NAXIS", 2L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L)
            .Build());
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.False(result.HasPixelData);
        Assert.Contains("BITPIX 64", result.SkipReason);
        Assert.Null(result.Mono);
        Assert.Null(result.PlanarRgb);
    }

    [Fact]
    public void Read_NonSquareShape_AddressesRowThenColumnCorrectly()
    {
        // 4 wide by 2 high, every position distinct - a transposed read would fail this.
        var pixels = new float[,]
        {
            { 0, 1, 2, 3 },
            { 10, 11, 12, 13 },
        };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 4L).Card("NAXIS2", 2L)
            .Pixels(16, naxis1: 4, naxis2: 2, pixels)
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.Equal(2, result.Mono!.GetLength(0));
        Assert.Equal(4, result.Mono.GetLength(1));
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                Assert.Equal(pixels[row, col], result.Mono[row, col]);
            }
        }
    }

    // Hand-builds an accepted FitsHeaderResult with the given cards, bypassing
    // FitsHeaderReader entirely. Used for axis/size bound fixtures whose declared pixel
    // data would be gigabytes in size - too large to actually write into a real fixture
    // stream - since FitsImageReader must decline before ever seeking/reading such a
    // stream.
    private static FitsHeaderResult BuildAcceptedHeader(params FitsCard[] cards)
        => new(true, null, cards, HeaderBlockCount: 1);

    [Fact]
    public void Read_Naxis1ExceedsIntRange_DeclinesWithoutException()
    {
        var header = BuildAcceptedHeader(
            new FitsCard("SIMPLE", true, null),
            new FitsCard("BITPIX", 8L, null),
            new FitsCard("NAXIS", 2L, null),
            new FitsCard("NAXIS1", (1L << 31) + 5, null),
            new FitsCard("NAXIS2", 1L, null));
        using var stream = new MemoryStream();

        var result = FitsImageReader.Read(stream, header);

        Assert.False(result.HasPixelData);
        Assert.NotNull(result.SkipReason);
        Assert.Null(result.Mono);
        Assert.Null(result.PlanarRgb);
    }

    [Fact]
    public void Read_PixelByteCountExceedsCap_DeclinesWithoutException()
    {
        // Both axes are within the per-axis cap individually, but their product (times
        // bytesPerSample) exceeds MaxPixelBytes (2 GiB minus one).
        var header = BuildAcceptedHeader(
            new FitsCard("SIMPLE", true, null),
            new FitsCard("BITPIX", 32L, null),
            new FitsCard("NAXIS", 2L, null),
            new FitsCard("NAXIS1", 65536L, null),
            new FitsCard("NAXIS2", 65536L, null));
        using var stream = new MemoryStream();

        var result = FitsImageReader.Read(stream, header);

        Assert.False(result.HasPixelData);
        Assert.Contains("exceeds cap", result.SkipReason);
        Assert.Null(result.Mono);
        Assert.Null(result.PlanarRgb);
    }

    [Fact]
    public void Read_HeaderNotAccepted_ThrowsArgumentException()
    {
        var rejected = new FitsHeaderResult(false, "not a simple FITS file", Array.Empty<FitsCard>(), 0);
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentException>(() => FitsImageReader.Read(stream, rejected));
    }

    // Review item 2: NAXIS1/NAXIS2 were unboxed with a hard cast, so a header the header
    // reader accepts but that lacks (or misspells the type of) either card threw out of
    // the pixel reader instead of degrading to header-only.
    [Fact]
    public void MissingNaxis1_ReturnsHeaderOnlyResultWithoutThrowing()
    {
        // NAXIS2 alone, and enough declared bytes that the header reader accepts: NAXIS1
        // is absent so its dimension defaults to 1 during the truncation check.
        var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 2L)
            .Card("NAXIS2", 2L)
            .Pixels(16, 2, 2, new float[,] { { 1, 2 }, { 3, 4 } })
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.False(result.HasPixelData);
        Assert.Equal("missing or non-integer NAXIS1/NAXIS2", result.SkipReason);
    }

    [Fact]
    public void StringNaxis1_ReturnsHeaderOnlyResultWithoutThrowing()
    {
        var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 2L)
            .Card("NAXIS1", "two")
            .Card("NAXIS2", 2L)
            .Pixels(16, 2, 2, new float[,] { { 1, 2 }, { 3, 4 } })
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.False(result.HasPixelData);
        Assert.Equal("missing or non-integer NAXIS1/NAXIS2", result.SkipReason);
    }

    [Fact]
    public void StringNaxis3_ReturnsUnsupportedShapeWithoutThrowing()
    {
        var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 3L)
            .Card("NAXIS1", 2L)
            .Card("NAXIS2", 2L)
            .Card("NAXIS3", "three")
            .Pixels(16, 2, 2, new float[,] { { 1, 2 }, { 3, 4 } })
            .Build();
        var header = ReadHeader(stream);

        var result = FitsImageReader.Read(stream, header);

        Assert.False(result.HasPixelData);
        Assert.Contains("unsupported NAXIS shape", result.SkipReason);
    }

    // ReadMonoStrip (spec 11.1's strip rule). Additive beside Read: every test above passes
    // unchanged, which is the proof the addition forked no decoder.

    // A 6-row by 4-column ramp, 0..23. Small enough for BITPIX 8 to carry every value exactly.
    private static float[,] Ramp6x4()
    {
        var pixels = new float[6, 4];
        for (var row = 0; row < 6; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                pixels[row, col] = (row * 4) + col;
            }
        }
        return pixels;
    }

    private static MemoryStream Ramp6x4Stream(int bitpix) => new FitsBuilder()
        .Card("SIMPLE", true).Card("BITPIX", (long)bitpix).Card("NAXIS", 2L)
        .Card("NAXIS1", 4L).Card("NAXIS2", 6L)
        .Pixels((short)bitpix, naxis1: 4, naxis2: 6, Ramp6x4())
        .Build();

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(-32)]
    [InlineData(-64)]
    public void ReadMonoStrip_MatchesTheCorrespondingRowsOfAFullRead(int bitpix)
    {
        using var stream = Ramp6x4Stream(bitpix);
        var header = ReadHeader(stream);
        var full = FitsImageReader.Read(stream, header);

        var strip = FitsImageReader.ReadMonoStrip(stream, header, rowStart: 2, rowCount: 3);

        Assert.NotNull(strip);
        Assert.Equal(3, strip!.GetLength(0));
        Assert.Equal(4, strip.GetLength(1));
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                Assert.Equal(full.Mono![2 + row, col], strip[row, col]);
            }
        }
    }

    [Fact]
    public void ReadMonoStrip_AppliesBzeroAndBscale()
    {
        // raw -32768 (0x8000) -> 0.0, raw 0 -> 32768.0, raw 32767 -> 65535.0
        var pixels = new float[,] { { 0, 0, 0 }, { 0, 0, 0 }, { 0.0f, 32768.0f, 65535.0f }, { 0, 0, 0 } };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 3L).Card("NAXIS2", 4L)
            .Card("BZERO", 32768.0).Card("BSCALE", 1.0)
            .Pixels(16, naxis1: 3, naxis2: 4, pixels, bzero: 32768, bscale: 1)
            .Build();
        var header = ReadHeader(stream);

        var strip = FitsImageReader.ReadMonoStrip(stream, header, rowStart: 2, rowCount: 1);

        Assert.NotNull(strip);
        Assert.Equal(0.0f, strip![0, 0]);
        Assert.Equal(32768.0f, strip[0, 1]);
        Assert.Equal(65535.0f, strip[0, 2]);
    }

    [Fact]
    public void ReadMonoStrip_AppliesBlankAsZero()
    {
        var pixels = new float[,] { { 1, 2 }, { -5, 7 } };
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L)
            .Card("BLANK", -5L)
            .Pixels(16, naxis1: 2, naxis2: 2, pixels)
            .Build();
        var header = ReadHeader(stream);

        var strip = FitsImageReader.ReadMonoStrip(stream, header, rowStart: 1, rowCount: 1);

        Assert.NotNull(strip);
        Assert.Equal(0.0f, strip![0, 0]);
        Assert.Equal(7.0f, strip[0, 1]);
    }

    [Fact]
    public void ReadMonoStrip_RowCountPastTheEnd_IsClamped()
    {
        using var stream = Ramp6x4Stream(16);
        var header = ReadHeader(stream);

        var strip = FitsImageReader.ReadMonoStrip(stream, header, rowStart: 4, rowCount: 512);

        Assert.NotNull(strip);
        Assert.Equal(2, strip!.GetLength(0));
        Assert.Equal(16.0f, strip[0, 0]);
        Assert.Equal(23.0f, strip[1, 3]);
    }

    [Fact]
    public void ReadMonoStrip_ThreePlaneColourFrame_IsNull()
    {
        var pixels = new float[3, 2, 2];
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 3L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L).Card("NAXIS3", 3L)
            .PixelsPlanarRgb(16, naxis1: 2, naxis2: 2, pixels)
            .Build();
        var header = ReadHeader(stream);

        Assert.Null(FitsImageReader.ReadMonoStrip(stream, header, rowStart: 0, rowCount: 2));
    }

    [Fact]
    public void ReadMonoStrip_Bitpix64_IsNull()
    {
        using var stream = AppendZeroDataBlock(new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 64L).Card("NAXIS", 2L)
            .Card("NAXIS1", 2L).Card("NAXIS2", 2L)
            .Build());
        var header = ReadHeader(stream);

        Assert.Null(FitsImageReader.ReadMonoStrip(stream, header, rowStart: 0, rowCount: 2));
    }

    [Fact]
    public void ReadMonoStrip_DoesNotDisturbTheStreamForASubsequentRead()
    {
        using var stream = Ramp6x4Stream(16);
        var header = ReadHeader(stream);

        // Out of order on purpose: every call seeks absolutely, so a caller can interleave.
        var last = FitsImageReader.ReadMonoStrip(stream, header, rowStart: 4, rowCount: 2);
        var first = FitsImageReader.ReadMonoStrip(stream, header, rowStart: 0, rowCount: 2);
        var full = FitsImageReader.Read(stream, header);

        Assert.Equal(16.0f, last![0, 0]);
        Assert.Equal(0.0f, first![0, 0]);
        Assert.True(full.HasPixelData);
        Assert.Equal(23.0f, full.Mono![5, 3]);
    }
}
