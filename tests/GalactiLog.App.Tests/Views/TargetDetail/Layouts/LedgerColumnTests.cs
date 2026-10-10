using GalactiLog.App.Views.TargetDetail.Layouts;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Views.TargetDetail.Layouts;

// The Nights list divider's width rule (spec.md, Nights list): between the date's right edge (the
// stop) and the table's own width (open).
public class LedgerColumnTests
{
    private const double Stop = 160;

    private const double Open = 400;

    [Theory]
    [InlineData(null, false, 160d, 400d, 400d)] // nothing stored is fully open
    [InlineData(250d, false, 160d, 400d, 250d)]
    [InlineData(90d, false, 160d, 400d, 160d)]
    [InlineData(900d, false, 160d, 400d, 400d)]
    [InlineData(250d, true, 160d, 400d, 160d)]
    [InlineData(null, false, 160d, 150d, 160d)] // open under the stop: no travel, the stop wins
    public void WidthOf(double? width, bool collapsed, double stop, double open, double expected)
        => Assert.Equal(expected, LedgerColumn.WidthOf(width, collapsed, stop, open));

    [Theory]
    [InlineData(150d)]
    [InlineData(160.4d)]
    public void Committed_AtTheStop_StoresCollapsed_AndKeepsTheLastOpenWidth(double width)
    {
        var state = LedgerColumn.Committed(new TargetLayoutState { SidebarWidth = 300 }, width, Stop, Open);

        Assert.True(state.SidebarCollapsed);
        Assert.Equal(300, state.SidebarWidth);
    }

    [Fact]
    public void Committed_InsideTheTravel_StoresTheWidth()
    {
        var state = LedgerColumn.Committed(new TargetLayoutState { SidebarWidth = 300 }, 250, Stop, Open);

        Assert.False(state.SidebarCollapsed);
        Assert.Equal(250, state.SidebarWidth);
    }

    [Theory]
    [InlineData(399.6d)]
    [InlineData(520d)]
    public void Committed_AtOpen_StoresFullyOpen(double width)
    {
        var state = LedgerColumn.Committed(new TargetLayoutState { SidebarWidth = 300, SidebarCollapsed = true }, width, Stop, Open);

        Assert.False(state.SidebarCollapsed);
        Assert.Null(state.SidebarWidth);
    }
}
