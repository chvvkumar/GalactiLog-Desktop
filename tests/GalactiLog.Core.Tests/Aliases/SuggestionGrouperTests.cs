using GalactiLog.Core.Aliases;
using Xunit;

namespace GalactiLog.Core.Tests.Aliases;

// Port of backend/app/api/settings.py's similarity grouping (spec 12.7, Phase 9 Task 7). Every
// case here mirrors a line of _are_similar, _group_by_similarity, _group_already_merged or
// _group_is_dismissed; the two named counter-examples (ASI533MC/ASI533MM, Askar40/Askar140) are
// the reason the source uses no edit distance, and this file is where that reason is pinned.
public class SuggestionGrouperTests
{
    // ---- AreSimilar ------------------------------------------------------------------------

    [Fact]
    public void AreSimilar_IsTrue_ForCaseDifferences()
    {
        Assert.True(SuggestionGrouper.AreSimilar("Ha", "ha"));
        Assert.True(SuggestionGrouper.AreSimilar("OIII", "oiii"));
    }

    [Theory]
    [InlineData("H_alpha", "H-alpha")]
    [InlineData("H_alpha", "H alpha")]
    [InlineData("H-alpha", "H alpha")]
    [InlineData("ZWO_ASI2600MM", "ZWO-ASI2600MM")]
    public void AreSimilar_IsTrue_ForUnderscoreHyphenAndSpaceDifferences(string a, string b)
        => Assert.True(SuggestionGrouper.AreSimilar(a, b));

    [Fact]
    public void AreSimilar_IsTrue_ForContainment_WhenBothAreAtLeastFourCharacters()
        => Assert.True(SuggestionGrouper.AreSimilar("ZWO ASI533MM Pro (ASI533MM)", "ZWO ASI533MM Pro"));

    [Fact]
    public void AreSimilar_IsFalse_ForContainment_WhenEitherIsShorterThanFour()
    {
        // "Ha" is inside "Ha3" by pure substring, but the length-4 guard on rule 3 refuses it,
        // and neither of the first two rules matches either.
        Assert.False(SuggestionGrouper.AreSimilar("Ha", "Ha3"));
    }

    [Fact]
    public void AreSimilar_IsFalse_ForAsi533McAndAsi533Mm()
        => Assert.False(SuggestionGrouper.AreSimilar("ASI533MC", "ASI533MM"));

    [Fact]
    public void AreSimilar_IsFalse_ForAskar40AndAskar140()
        => Assert.False(SuggestionGrouper.AreSimilar("Askar40", "Askar140"));

    [Fact]
    public void AreSimilar_UsesNoEditDistance()
    {
        // Askar40 and Askar41: one character apart (a substitution), the same length, long
        // enough to clear rule 3's guard, but neither is a substring of the other and neither
        // the exact nor the normalized form matches. An edit-distance rule would group this
        // pair; the three-rule ladder does not.
        Assert.False(SuggestionGrouper.AreSimilar("Askar40", "Askar41"));
    }

    // ---- Group ------------------------------------------------------------------------------

    [Fact]
    public void Group_ReturnsOnlyGroupsOfTwoOrMore()
    {
        var counts = new Dictionary<string, int> { ["Ha"] = 5, ["ha"] = 3, ["OIII"] = 7 };

        var groups = SuggestionGrouper.Group(counts);

        var group = Assert.Single(groups);
        Assert.Equal(new[] { "Ha", "ha" }, group.Names);
    }

    [Fact]
    public void Group_NamesAreSorted()
    {
        var counts = new Dictionary<string, int> { ["ha"] = 1, ["Ha"] = 2, ["HA"] = 3 };

        var group = Assert.Single(SuggestionGrouper.Group(counts));

        Assert.Equal(new[] { "HA", "Ha", "ha" }, group.Names);
    }

    [Fact]
    public void Group_CarriesTheCountForEveryMember()
    {
        var counts = new Dictionary<string, int> { ["Ha"] = 5, ["ha"] = 3 };

        var group = Assert.Single(SuggestionGrouper.Group(counts));

        Assert.Equal(5, group.Counts["Ha"]);
        Assert.Equal(3, group.Counts["ha"]);
    }

    [Fact]
    public void Group_TransitiveChain_LandsInOneGroup()
    {
        // "asi294" and "ASI294MC" are similar by containment (rule 3): "asi294" is a substring
        // of "asi294mc". "ASI294MC" and "asi-294mc" are similar by normalization (rule 2): both
        // normalize to "asi294mc". "asi294" and "asi-294mc" are NOT directly similar under any
        // rule (the hyphen breaks the containment match, and the normalized forms differ by the
        // trailing "mc"), so the only way they land together is the union-find's transitivity
        // through the shared middle name, which is exactly what this test pins.
        Assert.False(SuggestionGrouper.AreSimilar("asi294", "asi-294mc"));

        var counts = new Dictionary<string, int> { ["asi294"] = 1, ["ASI294MC"] = 2, ["asi-294mc"] = 3 };

        var group = Assert.Single(SuggestionGrouper.Group(counts));

        Assert.Equal(3, group.Names.Count);
        Assert.Contains("asi294", group.Names);
        Assert.Contains("ASI294MC", group.Names);
        Assert.Contains("asi-294mc", group.Names);
    }

    [Fact]
    public void Group_IsDeterministic_WhateverTheInputOrder()
    {
        var forward = new Dictionary<string, int> { ["Ha"] = 1, ["ha"] = 2, ["OIII"] = 3, ["oiii"] = 4 };
        var backward = new Dictionary<string, int> { ["oiii"] = 4, ["OIII"] = 3, ["ha"] = 2, ["Ha"] = 1 };

        var groupsForward = SuggestionGrouper.Group(forward)
            .Select(g => string.Join(",", g.Names))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        var groupsBackward = SuggestionGrouper.Group(backward)
            .Select(g => string.Join(",", g.Names))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(groupsForward, groupsBackward);
    }

    // ---- AlreadyMerged ------------------------------------------------------------------------

    [Fact]
    public void AlreadyMerged_IsTrue_WhenEveryMemberIsACanonicalNameOrAnAlias()
    {
        var group = new SuggestionGroup(["Ha", "ha"], new Dictionary<string, int> { ["Ha"] = 1, ["ha"] = 1 });
        var known = new HashSet<string> { "Ha", "ha", "OIII" };

        Assert.True(SuggestionGrouper.AlreadyMerged(group, known));
    }

    [Fact]
    public void AlreadyMerged_IsFalse_WhenOneMemberIsUnknown()
    {
        var group = new SuggestionGroup(["Ha", "ha"], new Dictionary<string, int> { ["Ha"] = 1, ["ha"] = 1 });
        var known = new HashSet<string> { "Ha" };

        Assert.False(SuggestionGrouper.AlreadyMerged(group, known));
    }

    // ---- IsDismissed --------------------------------------------------------------------------

    [Fact]
    public void IsDismissed_ComparesTheSortedNameList()
    {
        var group = new SuggestionGroup(["Ha", "ha"], new Dictionary<string, int> { ["Ha"] = 1, ["ha"] = 1 });
        IReadOnlyList<IReadOnlyList<string>> dismissed = [["Ha", "ha"]];

        Assert.True(SuggestionGrouper.IsDismissed(group, dismissed));
    }

    [Fact]
    public void IsDismissed_IsFalse_ForADifferentGroup()
    {
        var group = new SuggestionGroup(["Ha", "ha"], new Dictionary<string, int> { ["Ha"] = 1, ["ha"] = 1 });
        IReadOnlyList<IReadOnlyList<string>> dismissed = [["OIII", "oiii"]];

        Assert.False(SuggestionGrouper.IsDismissed(group, dismissed));
    }
}
