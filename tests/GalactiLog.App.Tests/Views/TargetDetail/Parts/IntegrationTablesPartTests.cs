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

// Content and size cases of IntegrationTablesPart, hosted on the part alone.
public class IntegrationTablesPartTests(Xunit.Abstractions.ITestOutputHelper output)
{
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

    private static List<TextBlock> Cells(Control table, double width)
        => [.. table.GetVisualDescendants().OfType<TextBlock>().Where(block => block.Width == width && block.Classes.Contains("num"))];

    [AvaloniaTheory]
    [InlineData(14d)]
    [InlineData(16d)]
    [InlineData(18d)]
    [InlineData(20d)]
    public void AtEveryTextSize_NoLabelRunsPastItsColumn_AndNoRowClips(double textSize)
    {
        // A failure is "Frames by exposure" or a night label drawn past its label column, a label
        // column under 117 px at the default size, two tables with label columns of different
        // widths, or a row whose text is taller than the row.
        using var harness = Page();
        var view = new IntegrationTablesPart { DataContext = harness.ViewModel };
        var window = Show(view, 1100, 600);
        window.FontSize = textSize;
        window.UpdateLayout();

        TextFit.AssertTextFitsItsBox(view);
        var exposure = LabelColumn(view, "ExposureTable");
        Assert.Equal(exposure, LabelColumn(view, "HoursTable"), 0.01);
        Assert.True(exposure >= 117d * textSize / 14d - 0.01, $"the label column is {exposure} at {textSize} px");
        output.WriteLine($"label column at {textSize} px: {exposure}");
    }

    // The x of the first filter cell is where the label column ends.
    private static double LabelColumn(Control view, string table)
    {
        var root = view.Named<Control>(table);
        var cell = root.GetVisualDescendants().OfType<TextBlock>().First(block => block.Classes.Contains("t-label") && block.Classes.Contains("num"));
        return cell.TranslatePoint(new Point(0, 0), root)!.Value.X;
    }

    [AvaloniaFact]
    public void ExposureTable_IsOneRowPerLengthWithFrameCountsAndTotals()
    {
        // A failure is a missing 600 s row, a count that is not summed over the nights, or no
        // total row.
        using var harness = Page();
        var view = Host(harness);

        var texts = view.Named<Control>("ExposureTable").GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("300 s", texts);
        Assert.Contains("600 s", texts);
        Assert.Contains("Total", texts);
        Assert.Contains("15", texts);
        Assert.Contains("24", texts);
        Assert.Contains("42", texts);
    }

    [AvaloniaFact]
    public void HoursTable_IsOneRowPerNightWithATotalColumnAndRow_ToOneDecimal()
    {
        // A failure is a second decimal, a missing total column or a missing total row.
        using var harness = Page();
        var view = Host(harness);

        var texts = view.Named<Control>("HoursTable").GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("2025-12-07", texts);
        Assert.Contains("2024-01-05", texts);
        Assert.Contains("3.0", texts);
        Assert.Contains("0.5", texts);
        Assert.Contains("3.5", texts);
    }

    [AvaloniaFact]
    public void ColumnsAre64AndTheLabelColumnIsAtLeast117_AndTheTwoTablesLineUp()
    {
        // A failure is a column of another width, or a filter column that sits at another x in
        // the hours table than in the exposure table.
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

        var exposure = view.Named<Control>("ExposureTable");
        var hours = view.Named<Control>("HoursTable");
        Assert.Equal(7, Cells(hours, 64d).Select(cell => cell.TranslatePoint(new Point(0, 0), view)!.Value.X).Distinct().Count() - 1);
        Assert.True(LabelColumn(view, "ExposureTable") >= 117d);

        double X(Control table, Control cell) => cell.TranslatePoint(new Point(0, 0), view)!.Value.X;
        var exposureXs = Cells(exposure, 64d).Select(cell => X(exposure, cell)).Distinct().Order().ToList();
        var hoursXs = Cells(hours, 64d).Select(cell => X(hours, cell)).Distinct().Order().ToList();
        Assert.Equal(exposureXs, hoursXs);
    }
}
