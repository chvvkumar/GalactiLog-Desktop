using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

public class CatalogPriorityTests
{
    [Theory]
    [InlineData("M 31", 0)]
    [InlineData("NGC 7000", 1)]
    [InlineData("IC 434", 2)]
    [InlineData("IC 434A", 2)]
    [InlineData("Caldwell 14", 3)]
    [InlineData("C 14", 3)]
    [InlineData("Sh2-155", 4)]
    [InlineData("SH 2-155", 4)]
    [InlineData("S2_155", 4)]
    [InlineData("Abell 21", 5)]
    [InlineData("PN A66 21", 5)]
    [InlineData("Arp 220", 6)]
    [InlineData("HCG 44", 7)]
    [InlineData("B 33", 8)]
    [InlineData("vdB 141", 9)]
    [InlineData("LBN 672", 10)]
    [InlineData("LDN 1622", 11)]
    [InlineData("Cr 399", 12)]
    [InlineData("Collinder 399", 12)]
    [InlineData("Mel 25", 13)]
    [InlineData("Melotte 25", 13)]
    [InlineData("RCW 38", 14)]
    [InlineData("Pal 1", 15)]
    [InlineData("Tr 37", 16)]
    [InlineData("Trumpler 37", 16)]
    [InlineData("Stock 2", 17)]
    [InlineData("Ced 214", 18)]
    [InlineData("Cederblad 214", 18)]
    [InlineData("Simeis 147", 19)]
    [InlineData("DWB 111", 20)]
    [InlineData("SNR G357.7+00.3", 21)]
    [InlineData("Cl Berkeley 59", 22)]
    [InlineData("Cl King 19", 23)]
    [InlineData("Gum 15", 24)]
    public void IndexOf_MatchesExpectedPattern(string name, int expectedIndex)
    {
        Assert.Equal(expectedIndex, CatalogPriority.IndexOf(name));
    }

    [Theory]
    [InlineData("Horsehead Nebula")]
    [InlineData("2MASS J00070878+0822598")]
    public void IndexOf_NoMatch_ReturnsNull(string name)
    {
        Assert.Null(CatalogPriority.IndexOf(name));
    }

    [Fact]
    public void ExtractCatalogId_PicksLowestPriorityMatchAmongCandidates()
    {
        var result = CatalogPriority.ExtractCatalogId(
            ["NGC 7000", "Sh2-117"],
            "NAME North America Nebula");

        Assert.Equal("NGC 7000", result);
    }

    [Fact]
    public void ExtractCatalogId_FallsBackAndStripsNamePrefix()
    {
        var result = CatalogPriority.ExtractCatalogId(
            [],
            "NAME Markarian Chain");

        Assert.Equal("Markarian Chain", result);
    }
}
