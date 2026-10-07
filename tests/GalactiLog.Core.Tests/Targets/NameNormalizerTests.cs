using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

public class NameNormalizerTests
{
    [Fact]
    public void Normalize_TrimsCollapsesAndUppercases()
    {
        Assert.Equal("M 31 NEBULA", NameNormalizer.Normalize("  m   31 nebula  "));
    }

    [Fact]
    public void NormalizeDisplay_PreservesCase()
    {
        Assert.Equal("M 31 Nebula", NameNormalizer.NormalizeDisplay("  M   31 Nebula  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeCatalogId_NullAndBlankInputYieldNull(string? input)
    {
        Assert.Null(NameNormalizer.NormalizeCatalogId(input));
    }

    [Fact]
    public void NormalizeCatalogId_NormalizesNonBlank()
    {
        Assert.Equal("NGC 7000", NameNormalizer.NormalizeCatalogId("  ngc   7000  "));
    }

    [Fact]
    public void StripPanel_RemovesTrailingPanelSuffix()
    {
        Assert.Equal("M31", NameNormalizer.StripPanel("M31 Panel 2"));
    }

    [Fact]
    public void StripPanel_CaseInsensitiveMatch()
    {
        Assert.Equal("M31", NameNormalizer.StripPanel("M31 panel 2"));
    }

    [Fact]
    public void StripPanel_NoSuffix_ReturnedTrimmedUnchanged()
    {
        Assert.Equal("NGC 7000", NameNormalizer.StripPanel("  NGC 7000  "));
    }

    // Task 6c: any default mosaic keyword, token-bounded at the end, case-insensitive; a name
    // with no number after the keyword, or no keyword, is returned trimmed.
    [Theory]
    [InlineData("IC 1396 P1", "IC 1396")]
    [InlineData("M 31 Panel 2", "M 31")]
    [InlineData("NGC 7000 panel 3", "NGC 7000")]
    [InlineData("Sh2-155 p 2", "Sh2-155")]
    [InlineData("PK 164+31.1", "PK 164+31.1")]
    [InlineData("NGC 7000 P", "NGC 7000 P")]
    [InlineData("Abell 2-1", "Abell 2-1")]
    [InlineData("HIP 12345", "HIP 12345")]
    [InlineData("C/2023 P1", "C/2023 P1")]
    [InlineData("M31_Panel_2", "M31")]
    public void StripPanel_StripsEveryDefaultKeyword(string name, string expected)
    {
        Assert.Equal(expected, NameNormalizer.StripPanel(name));
    }

    [Theory]
    [InlineData("SH 2-129", "SH2129")]
    [InlineData("ngc_7000", "NGC7000")]
    public void Compact_UppercasesAndStripsSpaceHyphenUnderscore(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Compact(input));
    }

    // Task 1's NormalizeNgcName tests already exist; add only the two cases the brief calls
    // out as possibly missing: the leading-zero-stripping IC case and a lowercase prefix.
    [Fact]
    public void NormalizeNgcName_LeadingZerosStripped_IC()
    {
        Assert.Equal("IC 2", NameNormalizer.NormalizeNgcName("IC0002"));
    }

    [Fact]
    public void NormalizeNgcName_LowercasePrefix_NormalizedToUppercase()
    {
        Assert.Equal("NGC 31", NameNormalizer.NormalizeNgcName("ngc0031"));
    }

    // Review fix, item 5: the one helper both readers of openngc_catalog.messier now share
    // (OfflineCatalogLookup.BuildIdentity and CatalogMembershipMatcher.MatchForTarget).
    [Theory]
    [InlineData("M 031", "31")]
    [InlineData("M 057", "57")]
    [InlineData("M 110", "110")]
    // An all-zero stored number keeps a single "0" rather than collapsing to an empty string.
    [InlineData("M 000", "0")]
    // Already-bare and unprefixed forms pass through untouched.
    [InlineData("31", "31")]
    [InlineData("M31", "M31")]
    public void MessierNumber_StripsPrefixAndLeadingZeros(string stored, string expected)
    {
        Assert.Equal(expected, NameNormalizer.MessierNumber(stored));
    }
}
