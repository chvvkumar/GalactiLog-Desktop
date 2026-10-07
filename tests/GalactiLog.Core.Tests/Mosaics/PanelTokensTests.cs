using GalactiLog.Core.Mosaics;
using Xunit;

namespace GalactiLog.Core.Tests.Mosaics;

public class PanelTokensTests
{
    private static readonly string[] Default = ["Panel", "P"];

    // Spec 7.7's token table, then the web's panel_suggestion_corpus TOKEN_CASES.
    [Theory]
    [InlineData("M 31 Panel 2", "Panel|P", "M 31", "Panel", "2")]
    [InlineData("NGC 7000 P3", "Panel|P", "NGC 7000", "P", "3")]
    [InlineData("IC 1396 P1", "Panel|P", "IC 1396", "P", "1")]
    [InlineData("Sh2-155 Panel 1", "Panel|P", "Sh2-155", "Panel", "1")]
    [InlineData("M31 panel2", "Panel|P", "M31", "panel", "2")]
    [InlineData("NGC 7000_Panel_4", "Panel|P", "NGC 7000", "Panel", "4")]
    [InlineData("IC1805_1-2", "Panel|P", "IC1805", null, "1-2")]
    [InlineData("IC1805 Panel 12", "Panel|P", "IC1805", "Panel", "12")]
    [InlineData("IC1805 P 12", "Panel|P", "IC1805", "P", "12")]
    [InlineData("Veil_P3", "Panel|P", "Veil", "P", "3")]
    [InlineData("Sh2-119 Tile 3", "Tile", "Sh2-119", "Tile", "3")]
    [InlineData("CustomTile Tile 1", "Panel|P|Tile", "CustomTile", "Tile", "1")]
    [InlineData("  M31 Panel 2 ", "Panel|P", "M31", "Panel", "2")]
    [InlineData("m31 panel 3", "Panel|P", "m31", "panel", "3")]
    [InlineData("IC1805-Panel-4", "Panel|P", "IC1805", "Panel", "4")]
    [InlineData("M 31 Panel 02", "Panel|P", "M 31", "Panel", "02")]
    [InlineData("IC1805_2-3", "", "IC1805", null, "2-3")]
    public void Match_FindsBaseKeywordAndNumber(string name, string keywords, string baseName, string? keyword, string number)
    {
        var match = PanelTokens.Match(name, Split(keywords));

        Assert.Equal(new PanelMatch(baseName, keyword, number), match);
    }

    [Theory]
    [InlineData("North America Nebula", "Panel|P")]
    [InlineData("M 31 Tile 2", "Panel|P")]
    [InlineData("NGC 7000 Panel 1", "P")]
    [InlineData("M 31 Panel", "Panel|P")]
    [InlineData("Pelican 2", "P")]
    [InlineData("IC1805 Panel 7", "")]
    [InlineData("M31", "Panel|P")]
    [InlineData("", "Panel|P")]
    public void Match_NoToken_IsNull(string name, string keywords)
    {
        Assert.Null(PanelTokens.Match(name, Split(keywords)));
        Assert.Null(PanelTokens.Strip(name, Split(keywords)));
    }

    [Fact]
    public void Match_NullName_IsNull() => Assert.Null(PanelTokens.Match(null, Default));

    [Fact]
    public void Strip_ReturnsBase()
    {
        Assert.Equal("Heart Nebula", PanelTokens.Strip("Heart Nebula Panel 1", Default));
        Assert.Equal("IC1805", PanelTokens.Strip("IC1805_1-1", Default));
    }

    [Fact]
    public void BuildPattern_KeywordAndTile()
    {
        Assert.Equal("%Sh2 119%Panel%1%", PanelTokens.BuildPattern("Sh2 119", "Panel", "1"));
        Assert.Equal("%IC1805%1-2%", PanelTokens.BuildPattern("IC1805", null, "1-2"));
    }

    [Fact]
    public void Label_AndNumberFromLabel_RoundTrip()
    {
        Assert.Equal("Panel 3", PanelTokens.Label("3"));
        Assert.Equal("1-2", PanelTokens.NumberFromLabel("Panel 1-2"));
        Assert.Equal("Custom", PanelTokens.NumberFromLabel("Custom"));
    }

    [Fact]
    public void ObjectMatchesPanel_ExactNumberAndBaseOnly()
    {
        Assert.True(PanelTokens.ObjectMatchesPanel("sh2 119 panel 1", "Sh2 119", "1", Default));
        Assert.False(PanelTokens.ObjectMatchesPanel("Sh2 119 Panel 12", "Sh2 119", "1", Default));
        Assert.False(PanelTokens.ObjectMatchesPanel("Sh2 120 Panel 1", "Sh2 119", "1", Default));
        Assert.False(PanelTokens.ObjectMatchesPanel(null, "Sh2 119", "1", Default));
    }

    [Fact]
    public void NextLabelSuffix_FollowsSpecRuleAndSkipsTakenLabels()
    {
        Assert.Equal("Panel 1 (b)", PanelTokens.NextLabelSuffix("Panel 1", []));
        Assert.Equal("Panel 1 (c)", PanelTokens.NextLabelSuffix("Panel 1 (b)", []));
        // Gaps: (b) and (c) are taken, (d) is free although (e) exists.
        Assert.Equal("Panel 1 (d)", PanelTokens.NextLabelSuffix("Panel 1", ["Panel 1", "Panel 1 (b)", "panel 1 (c)", "Panel 1 (e)"]));
    }

    private static string[] Split(string keywords) =>
        keywords.Length == 0 ? [] : keywords.Split('|');
}
