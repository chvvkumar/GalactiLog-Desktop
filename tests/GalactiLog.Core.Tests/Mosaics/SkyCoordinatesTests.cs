using GalactiLog.Core.Mosaics;
using Xunit;

namespace GalactiLog.Core.Tests.Mosaics;

public class SkyCoordinatesTests
{
    [Fact]
    public void ParseRa_DecimalIsDegrees() => Assert.Equal(187.69, SkyCoordinates.ParseRa(" 187.69 "));

    [Fact]
    public void ParseRa_SexagesimalIsHours() =>
        Assert.Equal((12 + 30 / 60.0 + 45.6 / 3600) * 15, SkyCoordinates.ParseRa("12 30 45.6")!.Value, 9);

    // Spec 7.1: colon-separated values are not parsed, as in the web.
    [Fact]
    public void ParseRa_ColonsAreNotParsed() => Assert.Null(SkyCoordinates.ParseRa("12:30:45.6"));

    [Fact]
    public void ParseDec_SexagesimalIsDegreesWithSign()
    {
        Assert.Equal(-5.5, SkyCoordinates.ParseDec("-05 30 00"));
        Assert.Equal(-0.5, SkyCoordinates.ParseDec("-00 30 00"));
        Assert.Equal(41.25, SkyCoordinates.ParseDec("+41 15 00"));
        Assert.Equal(-12.5, SkyCoordinates.ParseDec("-12.5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("12 30")]
    [InlineData("12 xx 30")]
    [InlineData("nan")]
    public void Garbage_IsNull(string? value)
    {
        Assert.Null(SkyCoordinates.ParseRa(value));
        Assert.Null(SkyCoordinates.ParseDec(value));
    }

    [Fact]
    public void FramePosition_FallsBackPerAxisAndNormalisesRa()
    {
        var p = SkyCoordinates.FramePosition("-10", null, "1 0 0", "+20 0 0");
        Assert.Equal(new FramePosition(350, 20, "RA", "OBJCTDEC"), p);
    }

    [Fact]
    public void FramePosition_DeclinationOutOfRangeOrMissing_IsNull()
    {
        Assert.Null(SkyCoordinates.FramePosition("10", "91", null, null));
        Assert.Null(SkyCoordinates.FramePosition("10", null, null, null));
    }
}
