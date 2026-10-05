using GalactiLog.Core.Fits;
using GalactiLog.Core.Imaging;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Core.Xisf;
using Xunit;

namespace GalactiLog.Core.Tests.Imaging;

public class DebayerTests
{
    // The hand-built 4x4 array from the spec 18.1 Debayer row. Every element is distinct, so
    // a transposed offset or a swapped channel shows up as a wrong number rather than as a
    // coincidence.
    private static float[,] Grid4x4() => new float[,]
    {
        { 1, 2, 3, 4 },
        { 5, 6, 7, 8 },
        { 9, 10, 11, 12 },
        { 13, 14, 15, 16 },
    };

    // The two greens of each 2x2 cell of Grid4x4 are the same unordered pair whichever of the
    // four patterns is assumed, because the off-diagonal corners swap roles and not values.
    // So the green channel is the same for all four patterns on this fixture.
    private static readonly float[,] ExpectedGreen = { { 3.5f, 5.5f }, { 11.5f, 13.5f } };

    private static void AssertChannel(float[,,] actual, int channel, float[,] expected)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(1));
        Assert.Equal(expected.GetLength(1), actual.GetLength(2));
        for (var row = 0; row < expected.GetLength(0); row++)
        {
            for (var col = 0; col < expected.GetLength(1); col++)
            {
                Assert.Equal(expected[row, col], actual[channel, row, col]);
            }
        }
    }

    private static void AssertBitwiseEqual(float[,,] expected, float[,,] actual)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));
        Assert.Equal(expected.GetLength(2), actual.GetLength(2));
        for (var ch = 0; ch < expected.GetLength(0); ch++)
        {
            for (var row = 0; row < expected.GetLength(1); row++)
            {
                for (var col = 0; col < expected.GetLength(2); col++)
                {
                    Assert.Equal(expected[ch, row, col], actual[ch, row, col]);
                }
            }
        }
    }

    // Stands in for Task 3's FITS strip reader: hands back rows [rowStart, rowStart+rowCount)
    // as a [rowCount, width] buffer and, optionally, records what was asked for.
    private static Func<int, int, float[,]> Slicer(float[,] frame, List<(int Start, int Count)>? calls = null)
        => (rowStart, rowCount) =>
        {
            calls?.Add((rowStart, rowCount));
            var width = frame.GetLength(1);
            var strip = new float[rowCount, width];
            for (var row = 0; row < rowCount; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    strip[row, col] = frame[rowStart + row, col];
                }
            }
            return strip;
        };

    private static float[,] PseudoRandomFrame(int height, int width, int seed)
    {
        var random = new Random(seed);
        var frame = new float[height, width];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                frame[row, col] = (float)(random.NextDouble() * 65535.0);
            }
        }
        return frame;
    }

    private static IReadOnlyList<FitsCard> Cards(params (string Keyword, object? Value)[] cards)
        => cards.Select(card => new FitsCard(card.Keyword, card.Value, null)).ToList();

    private static XisfHeaderResult ReadXisfHeader(MemoryStream stream)
    {
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted, header.RejectionReason);
        return header;
    }

    private static FitsHeaderResult ReadFitsHeader(MemoryStream stream)
    {
        var header = FitsHeaderReader.Read(stream);
        Assert.True(header.Accepted, header.RejectionReason);
        return header;
    }

    [Fact]
    public void Superpixel_Rggb_PlacesRedBlueAndMeanedGreen()
    {
        var result = Debayer.Superpixel(Grid4x4(), "RGGB");

        AssertChannel(result, 0, new float[,] { { 1, 3 }, { 9, 11 } });
        AssertChannel(result, 1, ExpectedGreen);
        AssertChannel(result, 2, new float[,] { { 6, 8 }, { 14, 16 } });
    }

    [Fact]
    public void Superpixel_Grbg_PlacesRedBlueAndMeanedGreen()
    {
        var result = Debayer.Superpixel(Grid4x4(), "GRBG");

        AssertChannel(result, 0, new float[,] { { 2, 4 }, { 10, 12 } });
        AssertChannel(result, 1, ExpectedGreen);
        AssertChannel(result, 2, new float[,] { { 5, 7 }, { 13, 15 } });
    }

    [Fact]
    public void Superpixel_Gbrg_PlacesRedBlueAndMeanedGreen()
    {
        var result = Debayer.Superpixel(Grid4x4(), "GBRG");

        AssertChannel(result, 0, new float[,] { { 5, 7 }, { 13, 15 } });
        AssertChannel(result, 1, ExpectedGreen);
        AssertChannel(result, 2, new float[,] { { 2, 4 }, { 10, 12 } });
    }

    [Fact]
    public void Superpixel_Bggr_PlacesRedBlueAndMeanedGreen()
    {
        var result = Debayer.Superpixel(Grid4x4(), "BGGR");

        AssertChannel(result, 0, new float[,] { { 6, 8 }, { 14, 16 } });
        AssertChannel(result, 1, ExpectedGreen);
        AssertChannel(result, 2, new float[,] { { 1, 3 }, { 9, 11 } });
    }

    [Theory]
    [InlineData("RGGB")]
    [InlineData("GRBG")]
    [InlineData("GBRG")]
    [InlineData("BGGR")]
    public void Superpixel_BlueSitsAtTheDiagonalOppositeOfRed(string pattern)
    {
        Assert.True(Debayer.TryGetRedOffset(pattern, out var red));
        var frame = Grid4x4();

        var result = Debayer.Superpixel(frame, pattern);

        Assert.Equal(frame[1 - red.Row, 1 - red.Col], result[2, 0, 0]);
    }

    [Fact]
    public void Superpixel_GreenIsTheMeanOfTheTwoOffDiagonalCorners()
    {
        // GRBG puts red at (0,1) and blue at (1,0), so the greens are frame[0,0] and
        // frame[1,1]: 1 and 6, mean 3.5. An integer average would give 3.
        var result = Debayer.Superpixel(Grid4x4(), "GRBG");

        Assert.Equal(3.5f, result[1, 0, 0]);
    }

    [Fact]
    public void Superpixel_ChannelOrderIsRedGreenBlue()
    {
        // Channel 0 is the red site (1), channel 2 the blue site (6), channel 1 the mean of
        // the two greens (3.5). A [b, g, r] stack would put 6 first.
        var result = Debayer.Superpixel(Grid4x4(), "RGGB");

        Assert.Equal(1f, result[0, 0, 0]);
        Assert.Equal(3.5f, result[1, 0, 0]);
        Assert.Equal(6f, result[2, 0, 0]);
    }

    [Fact]
    public void Superpixel_OddHeightAndWidth_TruncatesDownToEven()
    {
        var result = Debayer.Superpixel(new float[5, 7], "RGGB");

        Assert.Equal(3, result.GetLength(0));
        Assert.Equal(2, result.GetLength(1));
        Assert.Equal(3, result.GetLength(2));
    }

    [Fact]
    public void Superpixel_OutputIsHalfResolution()
    {
        var result = Debayer.Superpixel(new float[64, 48], "BGGR");

        Assert.Equal(3, result.GetLength(0));
        Assert.Equal(32, result.GetLength(1));
        Assert.Equal(24, result.GetLength(2));
    }

    [Fact]
    public void Superpixel_FrameSmallerThanOneCell_YieldsAnEmptyResult()
    {
        var result = Debayer.Superpixel(new float[1, 1], "RGGB");

        Assert.Equal(3, result.GetLength(0));
        Assert.Equal(0, result.GetLength(1));
        Assert.Equal(0, result.GetLength(2));
    }

    [Theory]
    [InlineData("RGGB")]
    [InlineData("GRBG")]
    [InlineData("GBRG")]
    [InlineData("BGGR")]
    public void SuperpixelStriped_MatchesWholeFrameOutputExactly(string pattern)
    {
        var frame = PseudoRandomFrame(256, 256, seed: 20250911);

        var whole = Debayer.Superpixel(frame, pattern);
        var striped = Debayer.SuperpixelStriped(Slicer(frame), 256, 256, pattern);

        AssertBitwiseEqual(whole, striped);
    }

    [Theory]
    [InlineData("RGGB")]
    [InlineData("GRBG")]
    [InlineData("GBRG")]
    [InlineData("BGGR")]
    public void SuperpixelStriped_FrameTallerThanOneStrip_MatchesWholeFrame(string pattern)
    {
        // Three strips, so the Bayer phase is carried across two strip boundaries. Asserted for
        // each pattern: a boundary that lost the phase would show up on one pattern and not
        // another.
        var frame = PseudoRandomFrame(1300, 8, seed: 7);

        var whole = Debayer.Superpixel(frame, pattern);
        var striped = Debayer.SuperpixelStriped(Slicer(frame), 1300, 8, pattern);

        AssertBitwiseEqual(whole, striped);
    }

    [Fact]
    public void SuperpixelStriped_ShortStripFromTheReader_ThrowsNamingTheShape()
    {
        // A reader that hands back fewer rows than it was asked for is a bug in the reader, and
        // it fails here by name rather than as an IndexOutOfRangeException from the cell loop.
        var frame = new float[1300, 8];
        Func<int, int, float[,]> shortReader = (rowStart, rowCount) =>
            Slicer(frame)(rowStart, Math.Max(rowCount - 2, 0));

        var error = Assert.Throws<ArgumentException>(
            () => Debayer.SuperpixelStriped(shortReader, 1300, 8, "RGGB"));

        Assert.Contains("[510, 8]", error.Message);
        Assert.Contains("[512, 8]", error.Message);
    }

    [Fact]
    public void SuperpixelStriped_EveryStripStartsOnAnEvenRow()
    {
        var calls = new List<(int Start, int Count)>();

        Debayer.SuperpixelStriped(Slicer(new float[1300, 8], calls), 1300, 8, "RGGB");

        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Equal(0, call.Start % 2));
    }

    [Fact]
    public void SuperpixelStriped_RequestsAtMost512RowsPerStrip()
    {
        var calls = new List<(int Start, int Count)>();

        Debayer.SuperpixelStriped(Slicer(new float[1300, 8], calls), 1300, 8, "RGGB");

        Assert.All(calls, call => Assert.InRange(call.Count, 1, Debayer.StripRows));
    }

    [Fact]
    public void SuperpixelStriped_LastStripIsShortWhenHeightIsNotAMultipleOf512()
    {
        var calls = new List<(int Start, int Count)>();

        Debayer.SuperpixelStriped(Slicer(new float[1300, 8], calls), 1300, 8, "RGGB");

        Assert.Equal(new[] { (0, 512), (512, 512), (1024, 276) }, calls);
    }

    [Fact]
    public void SuperpixelStriped_OddHeight_TruncatesBeforeStripping()
    {
        var calls = new List<(int Start, int Count)>();

        var result = Debayer.SuperpixelStriped(Slicer(new float[1025, 8], calls), 1025, 8, "RGGB");

        Assert.Equal(1024, calls.Sum(call => call.Count));
        Assert.Equal(512, result.GetLength(1));
    }

    [Fact]
    public void PatternFromFits_ReadsBayerpat()
    {
        Assert.Equal("RGGB", Debayer.PatternFromFits(Cards(("BAYERPAT", "RGGB"))));
    }

    [Fact]
    public void PatternFromFits_TrimsAndUppercasesTheValue()
    {
        Assert.Equal("RGGB", Debayer.PatternFromFits(Cards(("BAYERPAT", " rggb "))));
    }

    [Theory]
    [InlineData("XTRANS")]
    [InlineData("")]
    [InlineData("RGGB2")]
    public void PatternFromFits_UnknownPattern_IsNull(string value)
    {
        Assert.Null(Debayer.PatternFromFits(Cards(("BAYERPAT", value))));
    }

    [Fact]
    public void PatternFromFits_MissingCard_IsNull()
    {
        Assert.Null(Debayer.PatternFromFits(Cards(("OBJECT", "M31"))));
    }

    [Fact]
    public void PatternFromFits_NonStringCard_IsNull()
    {
        Assert.Null(Debayer.PatternFromFits(Cards(("BAYERPAT", 1L))));
    }

    [Fact]
    public void PatternFromXisf_PrefersBayerpatFitsKeyword()
    {
        using var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .FitsKeyword("BAYERPAT", "'GRBG'")
            .ColorFilterArray("RGGB", 2, 2)
            .Build();

        Assert.Equal("GRBG", Debayer.PatternFromXisf(ReadXisfHeader(stream)));
    }

    [Fact]
    public void PatternFromXisf_FallsBackToColorFilterArrayPattern()
    {
        using var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .ColorFilterArray("bggr", 2, 2)
            .Build();

        Assert.Equal("BGGR", Debayer.PatternFromXisf(ReadXisfHeader(stream)));
    }

    [Fact]
    public void PatternFromXisf_NoBayerpatAndNoCfa_IsNull()
    {
        using var stream = new XisfBuilder().Geometry(2, 2, 1).Build();

        Assert.Null(Debayer.PatternFromXisf(ReadXisfHeader(stream)));
    }

    [Fact]
    public void PatternFromXisf_CfaPatternNotOneOfTheFour_IsNull()
    {
        // An X-Trans-shaped 4x4 CFA is mono, not an error.
        using var stream = new XisfBuilder()
            .Geometry(4, 4, 1)
            .ColorFilterArray("GGRGGBGGBGGRBRGRBGGGBGGRGGRGGBRBGB", 4, 4)
            .Build();

        Assert.Null(Debayer.PatternFromXisf(ReadXisfHeader(stream)));
    }

    [Fact]
    public void Superpixel_UnknownPattern_Throws()
    {
        Assert.Throws<ArgumentException>(() => Debayer.Superpixel(Grid4x4(), "XTRANS"));
    }

    [Fact]
    public void Superpixel_XbayroffAndYbayroffCards_DoNotShiftTheDecodedFrame()
    {
        // Spec 11.1's last line, at the level this task can prove it. Debayer takes an array and
        // a pattern string, so it cannot consult XBAYROFF/YBAYROFF and there is nothing in it to
        // ignore. What this asserts is the whole FITS path: two extra header cards move the data
        // segment to the next 2880-byte block but shift neither the decoded pixels nor the
        // resulting Bayer phase, so the debayered output is byte-identical. The header-aware
        // version of this test, where a renderer that has the cards in hand still declines to
        // apply them, belongs to Task 3, which owns the code that reads the header.
        var pixels = Grid4x4();

        using var withOffsets = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 4L).Card("NAXIS2", 4L)
            .Card("BAYERPAT", "RGGB").Card("XBAYROFF", 1L).Card("YBAYROFF", 1L)
            .Pixels(16, naxis1: 4, naxis2: 4, pixels)
            .Build();
        using var withoutOffsets = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 4L).Card("NAXIS2", 4L)
            .Card("BAYERPAT", "RGGB")
            .Pixels(16, naxis1: 4, naxis2: 4, pixels)
            .Build();

        var headerWith = ReadFitsHeader(withOffsets);
        var headerWithout = ReadFitsHeader(withoutOffsets);

        var patternWith = Debayer.PatternFromFits(headerWith.Cards);
        var patternWithout = Debayer.PatternFromFits(headerWithout.Cards);
        Assert.Equal("RGGB", patternWith);
        Assert.Equal("RGGB", patternWithout);

        var resultWith = Debayer.Superpixel(FitsImageReader.Read(withOffsets, headerWith).Mono!, patternWith!);
        var resultWithout = Debayer.Superpixel(FitsImageReader.Read(withoutOffsets, headerWithout).Mono!, patternWithout!);

        AssertBitwiseEqual(resultWithout, resultWith);
    }
}
