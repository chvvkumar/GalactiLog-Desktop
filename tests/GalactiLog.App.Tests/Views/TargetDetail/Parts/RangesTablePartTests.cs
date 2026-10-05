using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Threading;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content cases of RangesTablePart: the range cell figures.
public class RangesTablePartTests
{
    [Fact]
    public void MedianPosition_ForASkewedRange_IsNearZero()
    {
        var cell = new RangeCellViewModel("Ecc", new MetricRangeSummary(0.28d, 0.84d, 0.34d), "0.00");

        Assert.True(cell.HasPosition);
        Assert.InRange(cell.MedianPosition, 0.10d, 0.12d);
    }

    [Fact]
    public void MedianPosition_WhenMinEqualsMax_IsAHalf()
    {
        var cell = new RangeCellViewModel("Sensor temp", new MetricRangeSummary(0d, 0d, 0d), "0.0");

        Assert.True(cell.HasPosition);
        Assert.Equal(0.5d, cell.MedianPosition);
    }

    [Fact]
    public void MedianPosition_WithAnAbsentFigure_HasNoBar()
    {
        var cell = new RangeCellViewModel("HFR", new MetricRangeSummary(1.0d, null, 1.5d), "0.00");

        Assert.False(cell.HasPosition);
        Assert.Equal(0d, cell.MedianPosition);
    }

    private const double WideFigure = 123456.789d;

    private const double RangeMinMinWidthCell = 62d - 12d;

    [AvaloniaFact]
    public void RangesTablePart_RangesTableColumns_ReportEqualWidthsAfterLayout()
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            Hfr = new MetricRangeSummary(-WideFigure, 3.10d, 2.30d),
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        var pane = new RangesTablePart { DataContext = harness.Card };
        Show(pane);
        Dispatcher.UIThread.RunJobs();

        var rows = TableRows(pane, "RangesTable");
        Assert.True(rows.Count >= 2, "the ranges table rendered no rows under its header");

        // Column 1 is Min on every row, header included, and HFR is the first range.
        var header = TableCellAt(rows[0], 1);
        var first = TableCellAt(rows[1], 1);
        Assert.True(
            first.Bounds.Width > RangeMinMinWidthCell,
            $"the seeded minimum is not wider than the column minimum ({first.Bounds.Width}).");
        Assert.Equal(header.Bounds.Width, first.Bounds.Width, 3);
    }

    [AvaloniaFact]
    public void RangesTablePart_ARigLabelRow_SpansTheFiveColumns()
    {
        var (pane, harness) = ThumbnailKit.RigHost(card => new RangesTablePart { DataContext = card }, [ThumbnailKit.RigA, ThumbnailKit.RigB]);
        using var scope = harness;
        Dispatcher.UIThread.RunJobs();

        var rangeLabels = pane.Named<ItemsControl>("RangeRowsList")
            .GetVisualDescendants()
            .OfType<ContentControl>()
            .Where(panel => panel.Name == "RangeRigLabelRow" && panel.IsEffectivelyVisible)
            .ToList();

        Assert.Equal(2, rangeLabels.Count);
        Assert.All(rangeLabels, label => Assert.Equal(5, Grid.GetColumnSpan(label)));

        Assert.Contains(ThumbnailKit.RigA, VisibleTexts(pane));
        Assert.Contains(ThumbnailKit.RigB, VisibleTexts(pane));
    }
}
