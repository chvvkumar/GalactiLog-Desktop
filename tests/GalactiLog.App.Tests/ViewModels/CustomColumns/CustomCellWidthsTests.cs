using GalactiLog.App.ViewModels.CustomColumns;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.CustomColumns;

// Ruling C31: the per-kind cell width table has one home. It was written out twice, in
// TargetDetailViewModel for the Nights ledger and in TargetListView for the dashboard, and the two
// copies had already disagreed: a text cell was 120 on one surface and 160 on the other. Every
// surface now reads CustomCellWidths directly.
public class CustomCellWidthsTests
{
    [Fact]
    public void TheTextCell_IsTheRuledFigure()
    {
        // Ruling C31 fixes the text cell at 120, the figure the dashboard's 160 had drifted from.
        Assert.Equal(120d, CustomCellWidths.Text);
    }
}
