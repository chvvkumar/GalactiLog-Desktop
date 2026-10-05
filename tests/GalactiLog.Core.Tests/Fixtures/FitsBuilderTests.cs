using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace GalactiLog.Core.Tests.Fixtures;

public class FitsBuilderTests
{
    [Fact]
    public void Build_NoPixelData_HeaderLengthIsMultipleOf2880()
    {
        var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", (long)16)
            .Card("NAXIS", (long)0)
            .EndCard()
            .Build();

        Assert.Equal(0, stream.Length % 2880);
    }

    [Fact]
    public void Card_StringValue_QuotedTextStartsAtByte10()
    {
        var stream = new FitsBuilder()
            .Card("OBJECT", "M 31")
            .EndCard()
            .Build();

        var bytes = stream.ToArray();
        var cardText = Encoding.ASCII.GetString(bytes, 0, 80);

        Assert.Equal("= ", cardText.Substring(8, 2));
        Assert.Equal("'M 31'", cardText.Substring(10, 6));
    }

    [Fact]
    public void NoAutoEnd_WithoutEndCard_StreamContainsNoEndCard()
    {
        var stream = new FitsBuilder()
            .NoAutoEnd()
            .Card("SIMPLE", true)
            .Build();

        var text = Encoding.ASCII.GetString(stream.ToArray());
        Assert.DoesNotContain("END     ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pixels_Bitpix16_WritesBigEndianRawSamples()
    {
        var values = new float[,] { { 100f, 200f }, { 300f, 400f } };
        const double bzero = 32768;
        const double bscale = 1;

        var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", (long)16)
            .Card("NAXIS", (long)2)
            .Card("NAXIS1", (long)2)
            .Card("NAXIS2", (long)2)
            .EndCard()
            .Pixels(16, 2, 2, values, bzero, bscale)
            .Build();

        var bytes = stream.ToArray();
        const int dataOffset = 2880; // the header above is a single 2880-byte block

        short Read(int index) => BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(dataOffset + index * 2, 2));

        // Hand-inverted BZERO/BSCALE: raw = (physical - bzero) / bscale.
        Assert.Equal((short)((100 - bzero) / bscale), Read(0));
        Assert.Equal((short)((200 - bzero) / bscale), Read(1));
        Assert.Equal((short)((300 - bzero) / bscale), Read(2));
        Assert.Equal((short)((400 - bzero) / bscale), Read(3));
    }

    [Fact]
    public void BuildTruncated_ReturnsShorterStream_AndThrowsPastFullLength()
    {
        var builder = new FitsBuilder().Card("SIMPLE", true).EndCard();
        var fullLength = (int)builder.Build().Length;

        var truncated = builder.BuildTruncated(fullLength - 100);
        Assert.Equal(fullLength - 100, truncated.Length);
        Assert.True(truncated.Length < fullLength);

        Assert.Throws<ArgumentException>(() => builder.BuildTruncated(fullLength + 1));
    }

    [Fact]
    public void Build_WithCommentAndHistory_StaysCardAligned()
    {
        var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Comment("a short comment")
            .History("a short history line")
            .EndCard()
            .Build();

        // Every emitted card is exactly 80 bytes (enforced by the builder itself), so the
        // header - a whole number of cards plus space padding - must land on a 2880
        // boundary, and it must be at least one full block.
        Assert.Equal(0, stream.Length % 2880);
        Assert.True(stream.Length >= 2880);
    }

    // Review item 7: a keyword longer than 8 characters pushed the "= " past byte 8, so
    // the card silently stopped being a value card and the fixture tested nothing.
    [Fact]
    public void Card_KeywordLongerThanEightCharacters_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new FitsBuilder().Card("VERYLONGKEY", 1L));

        Assert.Contains("exceeds 8 characters", ex.Message);
    }

    [Fact]
    public void Card_KeywordOfExactlyEightCharacters_IsAccepted()
    {
        var stream = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", (long)16)
            .Card("NAXIS", (long)0)
            .Card("EXACTLY8", (long)7)
            .Build();

        var text = Encoding.ASCII.GetString(stream.ToArray());

        Assert.Contains("EXACTLY8= ", text);
    }
}
