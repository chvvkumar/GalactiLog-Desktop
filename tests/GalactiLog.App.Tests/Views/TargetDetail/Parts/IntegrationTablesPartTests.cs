using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views.TargetDetail.Parts;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using Rows = GalactiLog.App.Tests.ViewModels.NightFilterMatrixViewModelTests;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content and size cases of IntegrationTablesPart, hosted on the part alone.
public class IntegrationTablesPartTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string[] Tables = ["OverallTable", "ExposureTable", "HoursTable"];

    private static Factory.Harness Page()
        => Factory.Create(get: _ => Factory.PopulatedDetail() with
        {
            NightFilters =
            [
                Rows.Row(Factory.LastSession, "Ha", 3_600d, 12, [(300d, 12)]),
                Rows.Row(Factory.LastSession, "OIII", 7_200d, 24, [(300d, 24)]),
                Rows.Row(Factory.FirstSession, "Ha", 1_800d, 6, [(300d, 3), (600d, 3)]),
            ],
        }).Settle();

    private static IntegrationTablesPart Host(Factory.Harness harness)
    {
        var view = new IntegrationTablesPart { DataContext = harness.ViewModel };
        Show(view, 1100, 600);
        return view;
    }

    private static List<string?> Texts(Control view, string table)
        => [.. view.Named<Control>(table).GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)];

    // The label column's width, from the table's header row.
    private static double LabelColumn(Control view, string table)
        => view.Named<Control>(table).GetVisualDescendants().OfType<TableRow>().First().ColumnDefinitions[0].ActualWidth;

    [AvaloniaTheory]
    [InlineData(14d)]
    [InlineData(16d)]
    [InlineData(18d)]
    [InlineData(20d)]
    public void AtEveryTextSize_NoLabelRunsPastItsColumn_AndNoRowClips(double textSize)
    {
        // A failure is a night label drawn past its label column, three tables with label columns
        // of different widths, or a row whose text is taller than the row.
        using var harness = Page();
        var view = new IntegrationTablesPart { DataContext = harness.ViewModel };
        var window = Show(view, 1100, 600);
        window.FontSize = textSize;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        TextFit.AssertTextFitsItsBox(view);
        var overall = LabelColumn(view, "OverallTable");
        Assert.All(Tables, table => Assert.Equal(overall, LabelColumn(view, table), 0.01));
        output.WriteLine($"label column at {textSize} px: {overall}");
    }

    [AvaloniaFact]
    public void ExposureTable_IsOneRowPerLengthWithFrameCountsAndTotals()
    {
        // A failure is a missing 600 row, a count that is not summed over the nights, no total
        // row, a unit left on the labels, or a blank where OIII shot no 600 s frame.
        using var harness = Page();
        var view = Host(harness);

        var texts = Texts(view, "ExposureTable");
        Assert.Contains(TableHeads.Exposure, texts);
        Assert.Contains("300", texts);
        Assert.Contains("600", texts);
        Assert.DoesNotContain("300 s", texts);
        Assert.Contains("Total", texts);
        Assert.Contains("15", texts);
        Assert.Contains("24", texts);
        Assert.Contains("42", texts);
        Assert.Contains("-", texts);
        var total = view.Named<Control>("ExposureTable").GetVisualDescendants().OfType<TableRow>()
            .Single(row => row.Kind != RowKind.Header && row.Children.OfType<TextBlock>().Any(block => block.Text == "Total"));
        Assert.Equal(RowKind.Total, total.Kind);
    }

    [AvaloniaFact]
    public void ATargetWithNoFilterRow_StillShowsItsOverallMetrics()
    {
        // A failure is the target-wide means hidden with the matrices: frames with no FILTER card
        // (a one-shot-colour rig) or no date are in no matrix row, and the means are shown nowhere
        // else.
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail() with { NightFilters = [] }).Settle();
        var view = Host(harness);

        Assert.True(view.Named<Control>("OverallTable").IsEffectivelyVisible);
        Assert.Contains(view.Named<Control>("OverallTable").GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "All frames" && block.IsEffectivelyVisible);
        Assert.False(view.Named<Control>("ExposureTable").IsEffectivelyVisible);
        Assert.False(view.Named<Control>("HoursTable").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void HoursTable_IsOneRowPerNightWithATotalColumnAndRow_ToOneDecimal()
    {
        // A failure is a second decimal, a missing total column or row, or a blank for the night
        // with no OIII.
        using var harness = Page();
        var view = Host(harness);

        var texts = Texts(view, "HoursTable");
        Assert.Contains(TableHeads.Night, texts);
        Assert.Contains("2025-12-07", texts);
        Assert.Contains("2024-01-05", texts);
        Assert.Contains("3.0", texts);
        Assert.Contains("0.5", texts);
        Assert.Contains("3.5", texts);
        Assert.Contains("-", texts);
    }

    [AvaloniaFact]
    public void FilterColumnsLineUpAcrossBothMatrices()
    {
        // A failure is a filter column that sits at another x in the hours table than in the
        // exposure table, or two filters sharing one column.
        string[] names = ["L", "R", "G", "B", "SII", "Ha", "OIII"];
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(
            totals: Factory.PopulatedTotals() with
            {
                FiltersUsed = names,
                IntegrationSecondsByFilter = names.ToDictionary(name => name, _ => 3_600d),
            }) with
        {
            NightFilters =
            [
                .. names.Select(name => Rows.Row(Factory.LastSession, name, 3_600d, 12, [(300d, 12)])),
                .. names.Select(name => Rows.Row(Factory.FirstSession, name, 3_600d, 12, [(600d, 12)])),
            ],
        }).Settle();
        var view = Host(harness);

        Dictionary<string, double> Xs(string table) => view.Named<Control>(table)
            .GetVisualDescendants().OfType<TableStripCell>()
            .GroupBy(cell => cell.Group!)
            .ToDictionary(group => group.Key, group => group.Select(cell => cell.TranslatePoint(default, view)!.Value.X).Distinct().Single());

        var exposure = Xs("ExposureTable");
        Assert.Equal(7, exposure.Values.Distinct().Count());
        Assert.Equal(exposure, Xs("HoursTable"));
    }

    [AvaloniaFact]
    public void OverallMetrics_IsAboveTheExposureTable_WithAllFramesThenOneRowPerFilter()
    {
        // A failure is the section below the matrices, no All frames row, filters out of bar
        // order, a heading in the retired units, or All frames drawn as an ordinary row.
        using var harness = Page();
        var view = Host(harness);

        var overall = view.Named<Control>("OverallTable");
        var exposure = view.Named<Control>("ExposureTable");
        Assert.True(overall.TranslatePoint(new Point(0, overall.Bounds.Height), view)!.Value.Y
            <= exposure.TranslatePoint(default, view)!.Value.Y + 0.5);

        var texts = Texts(view, "OverallTable");
        Assert.Contains("Overall metrics", texts);
        Assert.Contains(TableHeads.Hfr, texts);
        Assert.Contains(TableHeads.Fwhm, texts);
        Assert.Contains(TableHeads.Rms, texts);
        var order = harness.ViewModel.Totals!.FilterSwatches.Select(swatch => swatch.FilterName).ToList();
        Assert.Equal(["All frames", .. order], texts.Where(text => text == "All frames" || order.Contains(text!)));

        var allFrames = overall.GetVisualDescendants().OfType<TableRow>()
            .Single(row => row.Children.OfType<TextBlock>().Any(block => block.Text == "All frames"));
        Assert.Equal(RowKind.LeadTotal, allFrames.Kind);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("x-large")]
    public void Conventions_HoldAtDefaultAndExtraLargeText(string textSize)
    {
        using var harness = Page();
        var view = new IntegrationTablesPart { DataContext = harness.ViewModel };
        var window = Show(view);
        window.FontSize = MainWindowViewModel.ResolveRootFontSize(textSize);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        TableAssert.Conventions(view);
    }
}
