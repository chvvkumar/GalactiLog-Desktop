using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Threading;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using GalactiLog.App.Controls;
using GalactiLog.App.Controls.Table;
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

    [Fact]
    public void RangeCell_WithAnAbsentFigure_DashesIt()
    {
        var cell = new RangeCellViewModel("HFR px", new MetricRangeSummary(1.0d, 1.5d, null), "0.00");

        Assert.Equal(MetricText.Missing, cell.MedianText);
        Assert.Equal("1.00", cell.MinText);
        Assert.True(cell.HasValues);
    }

    [Fact]
    public void RangeCell_WithNoFigureAtAll_HasNoValues()
    {
        var cell = new RangeCellViewModel("HFR px", new MetricRangeSummary(null, null, null), "0.00");

        Assert.False(cell.HasValues);
        Assert.Equal(MetricText.Missing, cell.MinText);
        Assert.Equal(MetricText.Missing, cell.MedianText);
        Assert.Equal(MetricText.Missing, cell.MaxText);
    }

    private const double WideFigure = 123456.789d;

    private const double RangeMinMinWidthCell = 66d - 16d;

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

        // HFR is the first range; the Min heading right-aligns over its figures.
        var header = TableCellAt(rows[0], "min");
        var first = TableCellAt(rows[1], "min");
        Assert.True(
            first.Bounds.Width > RangeMinMinWidthCell,
            $"the seeded minimum is not wider than the column minimum ({first.Bounds.Width}).");
        Assert.Equal(header.Bounds.Right, first.Bounds.Right, 0.5);
    }

    [AvaloniaFact]
    public void RangesTablePart_ARigLabelRow_SpansTheRow()
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
        Assert.All(rangeLabels, label => Assert.Equal(
            label.FindAncestorOfType<ContentPresenter>()!.Bounds.Width,
            label.Bounds.Width + 2 * TableMetrics.Gutter,
            0.5));

        var header = pane.Named<TableRow>("RangesTableHeader");
        Assert.Equal(6, header.Columns!.Count);
        Assert.Equal(6, header.ColumnDefinitions.Count);

        Assert.Contains(ThumbnailKit.RigA, VisibleTexts(pane));
        Assert.Contains(ThumbnailKit.RigB, VisibleTexts(pane));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RangesTablePart_MeetsTableConventions(bool extraLarge)
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail());

        harness.Card.IsExpanded = true;
        harness.Settle();

        var pane = new RangesTablePart { DataContext = harness.Card };
        var window = Show(pane, 1280, 800);
        if (extraLarge)
        {
            window.FontSize = 20d;
        }

        Dispatcher.UIThread.RunJobs();

        TableAssert.Conventions(pane);
        TextFit.AssertTextFitsItsBox(pane);
    }
}