using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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

    private static (CompareTablePart View, Window Window) Host(Factory.Harness harness, double width = 1100)
    {
        var view = new CompareTablePart { DataContext = harness.ViewModel };
        return (view, Show(view, width, 400));
    }

    [AvaloniaTheory]
    // 14 px is left out: its widths are pinned, and the headless font draws "HFR px" 2 px past 64.
    [InlineData(16d)]
    [InlineData(18d)]
    [InlineData(20d)]
    public void AtEveryTextSize_NoLabelRunsPastItsColumn_AndNoRowClips(double textSize)
    {
        // A failure is a header or a number drawn past its column, or a row whose text is taller
        // than the row.
        using var harness = Page();
        var (view, window) = Host(harness);
        window.FontSize = textSize;
        window.UpdateLayout();

        TextFit.AssertTextFitsItsBox(view);
        Assert.Equal(110d * textSize / 14d, view.Named<Grid>("CompareTable").ColumnDefinitions[0].Width.Value, 0.01);
    }

    [AvaloniaFact]
    public void FilterColumnIsPinnedAt110_AndEachNightGroupIs289WithItsLabel()
    {
        // A failure is a filter column that is not 110 wide, a group that is not 289 wide, or a
        // group with no night label above it.
        using var harness = Page();
        harness.ViewModel.TargetChart.ShowAllSessions = true;
        var (view, _) = Host(harness);

        var table = view.Named<Grid>("CompareTable");
        Assert.Equal(110d, table.ColumnDefinitions[0].Width.Value);
        var headers = view.Named<ItemsControl>("CompareNightHeaders")
            .GetVisualDescendants().OfType<StackPanel>().Where(panel => panel.Width == 289d).ToList();
        Assert.Equal(2, headers.Count);
        Assert.All(headers, header => Assert.Equal(289d, header.Bounds.Width));
        var texts = VisibleTexts(view);
        Assert.Contains("2025-12-07", texts);
        Assert.Contains("2024-01-05", texts);
        Assert.Contains("Ha", texts);
    }

    [AvaloniaFact]
    public void CellWidthsAre64_52_62_57_54_AndANullMedianIsAnEmptyCell()
    {
        // A failure is another width, or text in the OIII cell of the night it has no row on.
        using var harness = Page();
        harness.ViewModel.TargetChart.ShowAllSessions = true;
        var (view, _) = Host(harness);

        var firstRow = view.Named<ItemsControl>("CompareRowList")
            .GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Width == 289d && panel.Height == 26d);
        Assert.Equal([64d, 52d, 62d, 57d, 54d], firstRow.Children.OfType<TextBlock>().Select(block => block.Width));
        Assert.Equal("2.40", firstRow.Children.OfType<TextBlock>().First().Text);

        var rows = view.Named<ItemsControl>("CompareRowList")
            .GetVisualDescendants().OfType<StackPanel>().Where(panel => panel.Width == 289d && panel.Height == 26d).ToList();
        Assert.Equal(4, rows.Count);
        Assert.All(rows[2].Children.OfType<TextBlock>(), block => Assert.Equal("", block.Text));
    }

    [AvaloniaFact]
    public void FewNightsFitAndManyScrollSideways_InsideThePart()
    {
        // A failure is a scroller that is not the part width less the 110 px filter column, no
        // horizontal range, or a Filter header that moves when the groups scroll.
        using var harness = Page();
        var (view, window) = Host(harness, width: 330);

        var scroll = view.Named<ScrollViewer>("CompareScroll");
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
        Assert.Equal(view.Bounds.Width - 110d, scroll.Viewport.Width, 0.5);
        var header = view.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "Filter");
        var before = header.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.X;
        scroll.Offset = new Avalonia.Vector(60, 0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(60d, scroll.Offset.X);
        Assert.Equal(before, header.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value.X, 0.5);
    }

    [AvaloniaFact]
    public void CheckedOnly_ShowsOnlyTheCheckedNights()
    {
        // A failure is two night groups with the switch on Checked.
        using var harness = Page();
        var (view, _) = Host(harness);

        harness.ViewModel.Sessions.First(card => card.SessionDate == Factory.FirstSession).IsChecked = true;
        harness.ViewModel.TargetChart.CheckedOnly = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var headers = view.Named<ItemsControl>("CompareNightHeaders")
            .GetVisualDescendants().OfType<StackPanel>().Where(panel => panel.Width == 289d).ToList();
        Assert.Single(headers);
        Assert.Contains("2024-01-05", VisibleTexts(view));
        Assert.DoesNotContain("2025-12-07", VisibleTexts(view));
    }
}
