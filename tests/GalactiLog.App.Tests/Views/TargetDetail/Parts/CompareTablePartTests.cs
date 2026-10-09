using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using Rows = GalactiLog.App.Tests.ViewModels.NightFilterMatrixViewModelTests;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content and size cases of CompareTablePart, hosted on the part alone.
public class CompareTablePartTests
{
    private static IReadOnlyList<NightFilterOverview> TwoNights() =>
    [
        Rows.Row(Factory.LastSession, "Ha", 3_600d, 12, [(300d, 12)], hfr: 2.1d, stars: 1_500d),
        Rows.Row(Factory.LastSession, "OIII", 3_600d, 12, [(300d, 12)]),
        Rows.Row(Factory.FirstSession, "Ha", 1_800d, 6, [(300d, 6)], hfr: 2.4d),
    ];

    // The newest card opens selected and loads on a pool thread; its publish rebuilds the trend, so
    // it is joined before a case flips the chart's scope or a stale rebuild can land after the flip.
    private static Factory.Harness Page()
        => Factory.Create(get: _ => Factory.PopulatedDetail() with { NightFilters = TwoNights() }).Settle().SettleCards();

    private static (CompareTablePart View, Window Window) Host(Factory.Harness harness, double width = 1280, double height = 800)
    {
        var view = new CompareTablePart { DataContext = harness.ViewModel };
        return (view, Show(view, width, height));
    }

    private static List<TableRow> DataRows(Control view)
        => [.. view.Named<ItemsControl>("CompareRowList").GetVisualDescendants().OfType<TableRow>()];

    private static string? Cell(TableRow row, string key)
        => row.Children.OfType<TextBlock>().First(block => TableRow.GetCol(block) == key).Text;

    [AvaloniaTheory]
    [InlineData(14d)]
    [InlineData(16d)]
    [InlineData(18d)]
    [InlineData(20d)]
    public void AtEveryTextSize_TheTableKeepsTheConventions(double textSize)
    {
        // A failure is a header or a number drawn past its column, a row whose text is taller than
        // the row, or a cell off the table convention.
        using var harness = Page();
        harness.ViewModel.TargetChart.ShowAllSessions = true;
        var (view, window) = Host(harness);
        window.FontSize = textSize;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        TextFit.AssertTextFitsItsBox(view);
        TableAssert.Conventions(view);
    }

    [AvaloniaFact]
    public void RowsAreGroupedPerNight_NewestFirst_TheNightOnItsFirstRowOnly()
    {
        // A failure is the older night first, the date on every row, a row for OIII on the night
        // that shot none, or a blank for a missing median.
        using var harness = Page();
        harness.ViewModel.TargetChart.ShowAllSessions = true;
        var (view, _) = Host(harness);

        var rows = DataRows(view);
        Assert.Equal(["2025-12-07", "", "2024-01-05"], rows.Select(row => Cell(row, "night")));
        Assert.Equal(["Ha", "OIII", "Ha"], rows.Select(row => row.Children.OfType<DockPanel>().Single().Children.OfType<TextBlock>().Single().Text));
        Assert.All(["hfr", "ecc", "fwhm", "rms", "stars"], key => Assert.Equal("-", Cell(rows[1], key)));
        Assert.Equal("2.10", Cell(rows[0], "hfr"));
        Assert.Equal("2.40", Cell(rows[2], "hfr"));
    }

    [AvaloniaFact]
    public void NightGroupsAlternate_TheBandNotTheRow()
    {
        // A failure is per-row zebra, which would split one night's filters into two bands.
        using var harness = Page();
        harness.ViewModel.TargetChart.ShowAllSessions = true;
        var (view, _) = Host(harness);

        Assert.Equal([false, false, true], DataRows(view).Select(row => row.Classes.Contains("alt")));
    }

    [AvaloniaFact]
    public void TheHeaderStaysPut_TheBarIsAlwaysShown_AndNothingScrollsSideways()
    {
        // A failure is a header that scrolls away with the rows, a bar that hides at rest or at the
        // app's thin size, or a table wider than its viewport.
        DateOnly[] nights = [new(2025, 12, 7), new(2025, 11, 30), new(2025, 11, 23), new(2025, 11, 16)];
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(sessions: [.. nights.Select(Factory.Session)]) with
        {
            NightFilters =
            [
                .. nights.Select(night => Rows.Row(night, "Ha", 3_600d, 12, [(300d, 12)], hfr: 2.1d)),
                .. nights.Select(night => Rows.Row(night, "OIII", 3_600d, 12, [(300d, 12)], hfr: 2.3d)),
            ],
        }).Settle().SettleCards();
        harness.ViewModel.TargetChart.ShowAllSessions = true;
        var (view, window) = Host(harness, height: 120);

        var scroll = view.Named<ScrollViewer>("CompareScroll");
        Assert.Equal(ScrollBarVisibility.Visible, scroll.VerticalScrollBarVisibility);
        Assert.False(scroll.AllowAutoHide);
        var bar = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(each => each.Orientation == Orientation.Vertical);
        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(TableMetrics.ScrollBarSize, bar.Bounds.Width, 0.5);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"extent {scroll.Extent.Width} in a {scroll.Viewport.Width} viewport");

        var header = view.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == TableHeads.Night);
        var before = header.TranslatePoint(default, window)!.Value.Y;
        scroll.Offset = new Vector(0, 40);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(40d, scroll.Offset.Y);
        Assert.Equal(before, header.TranslatePoint(default, window)!.Value.Y, 0.5);
    }

    [AvaloniaFact]
    public void CheckedOnly_ShowsOnlyTheCheckedNights()
    {
        // A failure is the unchecked night's rows with the switch on Checked.
        using var harness = Page();
        var (view, _) = Host(harness);

        harness.ViewModel.Sessions.First(card => card.SessionDate == Factory.FirstSession).IsChecked = true;
        harness.ViewModel.TargetChart.CheckedOnly = true;
        Dispatcher.UIThread.RunJobs();

        var nights = DataRows(view).Select(row => Cell(row, "night")).ToList();
        Assert.Contains("2024-01-05", nights);
        Assert.DoesNotContain("2025-12-07", nights);
    }
}
