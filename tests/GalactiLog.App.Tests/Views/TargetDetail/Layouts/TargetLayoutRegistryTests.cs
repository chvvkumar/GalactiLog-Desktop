using GalactiLog.App.Views.TargetDetail.Layouts;
using GalactiLog.App.Views.TargetDetail.Parts;
using Xunit;

namespace GalactiLog.App.Tests.Views.TargetDetail.Layouts;

public sealed class TargetLayoutRegistryTests
{
    [Fact]
    public void All_IsTheOneQuestionModesRow()
    {
        // A failure looks like a deleted layout's row still listed beside Question Modes.
        var row = Assert.Single(TargetLayoutRegistry.All);
        Assert.Equal("modes", row.Key);
        Assert.Equal(typeof(ModesLayoutView), row.ViewType);
    }

    [Fact]
    public void Default_IsTheFirstRow()
        => Assert.Same(TargetLayoutRegistry.All[0], TargetLayoutRegistry.Default);

    [Fact]
    public void All_Keys_AreUnique()
        => Assert.Equal(TargetLayoutRegistry.All.Count, TargetLayoutRegistry.All.Select(r => r.Key).Distinct().Count());

    [Fact]
    public void Parts_AreThirteen_AndCOpensTheTwoTablesFromTheirOpeners()
    {
        // Red if either table part is missing from the census list, C names no opener for it, the
        // findings part (deleted, P24 R22) is listed, or the notes part (moved to the Details
        // drawer, night pane round) is still counted as a layout part.
        Assert.Equal(13, TargetLayoutRegistry.Parts.Distinct().Count());
        Assert.DoesNotContain(TargetLayoutRegistry.Parts, part => part.Name == "FindingsPart");
        Assert.DoesNotContain(typeof(NightNotesPart), TargetLayoutRegistry.Parts);
        Assert.Contains(typeof(CompareTablePart), TargetLayoutRegistry.Parts);
        Assert.Contains(typeof(IntegrationTablesPart), TargetLayoutRegistry.Parts);
        var modes = TargetLayoutRegistry.Default.OneClickAway;
        Assert.Equal("CompareNightsButton", modes.GetValueOrDefault(typeof(CompareTablePart)));
        Assert.Equal("IntegrationButton", modes.GetValueOrDefault(typeof(IntegrationTablesPart)));
        Assert.False(modes.ContainsKey(typeof(NightNotesPart)));
    }
}
