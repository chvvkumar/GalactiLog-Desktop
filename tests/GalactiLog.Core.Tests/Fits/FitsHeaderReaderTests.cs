using GalactiLog.Core.Fits;
using GalactiLog.Core.Tests.Fixtures;
using Xunit;

namespace GalactiLog.Core.Tests.Fits;

public class FitsHeaderReaderTests
{
    // SIMPLE/BITPIX/NAXIS=0 satisfies every header-level acceptance rule with no pixel
    // data, so feature tests that don't care about pixel data can build on this.
    private static FitsBuilder MinimalValid() =>
        new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 0L);

    [Fact]
    public void Read_IntegerFloatBoolStringCards_ParseToCorrectClrType()
    {
        using var stream = MinimalValid()
            .Card("INTVAL", 42L)
            .Card("FLTVAL", 3.14)
            .Card("BOOLVAL", true)
            .Card("STRVAL", "hello")
            .Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Equal(42L, FitsHeaderReader.GetValue(result.Cards, "INTVAL"));
        Assert.Equal(3.14, FitsHeaderReader.GetValue(result.Cards, "FLTVAL"));
        Assert.Equal(true, FitsHeaderReader.GetValue(result.Cards, "BOOLVAL"));
        Assert.Equal("hello", FitsHeaderReader.GetValue(result.Cards, "STRVAL"));
    }

    [Fact]
    public void Read_DoubledQuoteInString_ParsesToSingleEmbeddedQuote()
    {
        using var stream = MinimalValid().Card("NAME", "O'Brien").Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.Equal("O'Brien", FitsHeaderReader.GetValue(result.Cards, "NAME"));
    }

    [Fact]
    public void Read_DExponentFloat_ParsesAsDouble()
    {
        var cardText = "FOO     = 1.5D+02".PadRight(80);
        using var stream = MinimalValid().RawCard(cardText).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.Equal(150.0, FitsHeaderReader.GetValue(result.Cards, "FOO"));
    }

    [Fact]
    public void Read_BlankKeywordCard_ContributesNothing()
    {
        using var stream = MinimalValid().BlankCard().Card("AFTER", 1L).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.DoesNotContain(result.Cards, c => c.Keyword.Length == 0);
        Assert.Equal(1L, FitsHeaderReader.GetValue(result.Cards, "AFTER"));
    }

    [Fact]
    public void BuildRawHeaders_RepeatedCommentAndHistory_CollectIntoArraysInOrder()
    {
        using var stream = MinimalValid()
            .Comment("first comment")
            .Comment("second comment")
            .History("first history")
            .History("second history")
            .History("third history")
            .Build();

        var result = FitsHeaderReader.Read(stream);
        var rawHeaders = FitsHeaderReader.BuildRawHeaders(result.Cards);

        var comments = Assert.IsType<System.Text.Json.Nodes.JsonArray>(rawHeaders["COMMENT"]);
        Assert.Equal(new[] { "first comment", "second comment" }, comments.Select(n => n!.GetValue<string>()));

        var history = Assert.IsType<System.Text.Json.Nodes.JsonArray>(rawHeaders["HISTORY"]);
        Assert.Equal(
            new[] { "first history", "second history", "third history" },
            history.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Read_HierarchCard_RoundTripsFullKeywordAndValue()
    {
        using var stream = MinimalValid().Hierarch("ESO TEL FOCU LEN", "1234.5").Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.Equal("1234.5", FitsHeaderReader.GetValue(result.Cards, "ESO TEL FOCU LEN"));
    }

    [Fact]
    public void Read_HierarchWithNoEquals_SkippedWithoutRejectingFile()
    {
        using var stream = MinimalValid().HierarchNoEquals("TEL FOCU LEN 1234.5").Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.DoesNotContain(result.Cards, c => c.Keyword.Contains("HIERARCH") || c.Keyword.Contains("TEL"));
    }

    [Fact]
    public void Read_ContinuedStringAcrossThreeChunks_ReconstructsFullString()
    {
        const string fullValue = "The quick brown fox jumps over lazy dog!!";
        using var stream = MinimalValid().ContinuedString("LONGSTR", fullValue, 15).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.Equal(fullValue, FitsHeaderReader.GetValue(result.Cards, "LONGSTR"));
        Assert.DoesNotContain(result.Cards, c => c.Keyword == "CONTINUE");
    }

    [Fact]
    public void GetValueAndBuildRawHeaders_DuplicateKeys_LastCardWins()
    {
        using var stream = MinimalValid().Card("FILTER", "R").Card("FILTER", "G").Build();

        var result = FitsHeaderReader.Read(stream);
        var rawHeaders = FitsHeaderReader.BuildRawHeaders(result.Cards);

        Assert.Equal("G", FitsHeaderReader.GetValue(result.Cards, "FILTER"));
        Assert.Equal("G", rawHeaders["FILTER"]!.GetValue<string>());
    }

    [Fact]
    public void BuildRawHeaders_NonFiniteDouble_SerializesAsJsonString()
    {
        var cards = new List<FitsCard> { new("FOO", double.NaN, null) };

        var rawHeaders = FitsHeaderReader.BuildRawHeaders(cards);

        Assert.Equal("NaN", rawHeaders["FOO"]!.GetValue<string>());
    }

    [Fact]
    public void Read_MissingSimple_Rejected()
    {
        using var stream = new FitsBuilder().Card("SIMPLE", false).Card("BITPIX", 16L).Card("NAXIS", 0L).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("simple", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Cards);
    }

    [Fact]
    public void Read_BitpixAbsent_Rejected()
    {
        using var stream = new FitsBuilder().Card("SIMPLE", true).Card("NAXIS", 0L).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("BITPIX", result.RejectionReason);
    }

    [Fact]
    public void Read_BitpixUnsupportedValue_Rejected()
    {
        using var stream = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 12L).Card("NAXIS", 0L).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("BITPIX", result.RejectionReason);
    }

    [Fact]
    public void Read_Bitpix64_Accepted()
    {
        using var stream = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 64L).Card("NAXIS", 0L).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.NotEmpty(result.Cards);
    }

    [Fact]
    public void Read_MissingNaxis_Rejected()
    {
        using var stream = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("NAXIS", result.RejectionReason);
    }

    [Fact]
    public void Read_NaxisNotAnInteger_Rejected()
    {
        using var stream = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", "x").Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("NAXIS", result.RejectionReason);
        Assert.Empty(result.Cards);
    }

    [Fact]
    public void Read_NoEndWithin200Blocks_Rejected()
    {
        var builder = new FitsBuilder();
        // 200 full blocks of blank cards (36 cards/block), no END anywhere.
        for (var i = 0; i < 200 * 36; i++)
        {
            builder.BlankCard();
        }
        builder.NoAutoEnd();
        using var stream = builder.Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("not terminated", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_TruncatedBeforeDeclaredDataEnd_Rejected()
    {
        var physical = new float[4, 4];
        var builder = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 2L)
            .Card("NAXIS1", 4L)
            .Card("NAXIS2", 4L)
            .Pixels(16, 4, 4, physical);
        using var full = builder.Build();
        var fullLength = (int)full.Length;

        // Header occupies exactly one 2880-byte block; truncate partway into the pixel
        // block so the header itself is intact but the declared data is not.
        using var truncated = builder.BuildTruncated(2880 + 100);
        Assert.True(2880 + 100 < fullLength);

        var result = FitsHeaderReader.Read(truncated);

        Assert.False(result.Accepted);
        Assert.Contains("truncated", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_PrimaryZImageTrue_RejectedAsCompressed()
    {
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 0L)
            .Card("ZIMAGE", true)
            .Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Contains("compressed", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_Naxis0WithCompressedBintableExtension_RejectedAsCompressed()
    {
        using var primary = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 0L)
            .Build();
        using var extension = new FitsBuilder()
            .Card("XTENSION", "BINTABLE")
            .Card("ZCMPTYPE", "RICE_1")
            .Build();

        using var combined = new MemoryStream();
        primary.WriteTo(combined);
        extension.WriteTo(combined);
        combined.Position = 0;

        var result = FitsHeaderReader.Read(combined);

        Assert.False(result.Accepted);
        Assert.Contains("compressed", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_Normal2DFileNoCompressionNoNaxis3_Accepted()
    {
        var physical = new float[3, 5];
        using var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 2L)
            .Card("NAXIS1", 5L)
            .Card("NAXIS2", 3L)
            .Card("OBJECT", "M31")
            .Pixels(16, 5, 3, physical)
            .Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.True(result.Accepted);
        Assert.Null(result.RejectionReason);
        Assert.Null(FitsHeaderReader.GetValue(result.Cards, "NAXIS3"));
        Assert.Equal("M31", FitsHeaderReader.GetValue(result.Cards, "OBJECT"));
        Assert.Equal(5L, FitsHeaderReader.GetValue(result.Cards, "NAXIS1"));
        Assert.Equal(3L, FitsHeaderReader.GetValue(result.Cards, "NAXIS2"));
        Assert.True(result.HeaderBlockCount > 0);
    }

    // Review item 1: an unbounded NAXIS made the truncation loop run once per declared
    // axis, so this header hung the reader instead of rejecting.
    [Theory]
    [InlineData(999999999999L)]
    [InlineData(1000L)]
    [InlineData(-1L)]
    public void NaxisOutsideFitsRange_IsRejectedWithoutHanging(long naxis)
    {
        var stream = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", naxis).Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Equal("unsupported NAXIS", result.RejectionReason);
    }

    [Fact]
    public void NaxisAtFitsCap_IsNotRejectedForBeingOutOfRange()
    {
        var stream = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 999L).Build();

        var result = FitsHeaderReader.Read(stream);

        // Every NAXISi is absent so each dimension defaults to 1: the file is short, not
        // out of range. The point is that the reason is not "unsupported NAXIS".
        Assert.False(result.Accepted);
        Assert.Equal("truncated file", result.RejectionReason);
    }

    // Review item 14: three large NAXISi values wrapped past long.MaxValue, producing a
    // tiny required length that any file satisfied, so a 3-block file was "accepted".
    [Fact]
    public void ThreeHugeAxisLengths_OverflowIsRejectedAsTruncated()
    {
        var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", 16L)
            .Card("NAXIS", 3L)
            .Card("NAXIS1", 4000000000L)
            .Card("NAXIS2", 4000000000L)
            .Card("NAXIS3", 4000000000L)
            .Build();

        var result = FitsHeaderReader.Read(stream);

        Assert.False(result.Accepted);
        Assert.Equal("truncated file", result.RejectionReason);
    }
}
