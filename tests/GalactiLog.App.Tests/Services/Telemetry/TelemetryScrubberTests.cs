using GalactiLog.App.Services.Telemetry;
using Xunit;

namespace GalactiLog.App.Tests.Services.Telemetry;

public class TelemetryScrubberTests
{
    [Fact]
    public void Null_ReadsAsEmpty() => Assert.Equal("", TelemetryScrubber.Scrub(null));

    [Fact]
    public void PlainText_IsUnchanged()
        => Assert.Equal("Sequence contains no elements", TelemetryScrubber.Scrub("Sequence contains no elements"));

    [Theory]
    [InlineData(@"C:\Users\kumar\Pictures\M31\light.fits")]
    [InlineData("D:/data/x.xisf")]
    [InlineData(@"\\nas\astro\2026\m31.fits")]
    [InlineData("file:///C:/x/y.fits")]
    public void APath_BecomesThePlaceholder(string path)
        => Assert.Equal("Could not open '<path>'.", TelemetryScrubber.Scrub($"Could not open '{path}'."));

    [Fact]
    public void ALongString_TruncatesTo200()
        => Assert.Equal(new string('a', 200), TelemetryScrubber.Scrub(new string('a', 300)));

    [Fact]
    public void TwoPaths_AreBothReplaced()
    {
        var scrubbed = TelemetryScrubber.Scrub(@"Cannot move C:\a\b.fits to \\nas\share\b.fits now");

        Assert.Equal("Cannot move <path> to <path> now", scrubbed);
    }
}
