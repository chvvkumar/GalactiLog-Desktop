using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.Dashboard;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.CustomColumns;

// Ruling C31: the per-kind cell width table has one home. It was written out twice, in
// TargetDetailViewModel for the Nights ledger and in TargetListView for the dashboard, and the two
// copies had already disagreed: a text cell was 120 on one surface and 160 on the other, and the
// heading measurement a long column name needs landed in one copy only. These cases are what fails
// if a third surface, or an edit to one of the two, forks the table again.
public class CustomCellWidthsTests
{
    public static TheoryData<CustomColumnType> EveryKind =>
        [CustomColumnType.Boolean, CustomColumnType.Text, CustomColumnType.Dropdown];

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void TheTwoSurfaces_SizeACellFromOneTable(CustomColumnType type)
    {
        // Red against the tree before ruling C31: the dashboard answered 160 for a text cell while
        // the ledger answered 120. The theory covers all three kinds rather than the one that
        // differed, because the point is that the two surfaces cannot answer differently at all.
        Assert.Equal(CustomCellWidths.For(type), TargetDetailViewModel.CustomCellWidth(type));
        Assert.Equal(CustomCellWidths.For(type), TargetListView.WidthFor(type));
    }

    [Fact]
    public void TheFiguresThemselves_AreStatedOnceAndAreTheRuledOnes()
    {
        // The named constants both surfaces' markup and arithmetic read are aliases of the table,
        // not second copies of the figures. Ruling C31 fixes the text cell at 120.
        Assert.Equal(120d, CustomCellWidths.Text);
        Assert.Equal(CustomCellWidths.Check, TargetDetailViewModel.CustomCellCheckWidth);
        Assert.Equal(CustomCellWidths.Choice, TargetDetailViewModel.CustomCellChoiceWidth);
        Assert.Equal(CustomCellWidths.Text, TargetDetailViewModel.CustomCellTextWidth);
        Assert.Equal(CustomCellWidths.Check, TargetListView.BooleanCellWidth);
        Assert.Equal(CustomCellWidths.Choice, TargetListView.DropdownCellWidth);
        Assert.Equal(CustomCellWidths.Text, TargetListView.TextCellWidth);
    }
}
