using Avalonia.Threading;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.RigRowKit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content cases of PerFilterTablePart: the per-filter rows and the rig label row cells.
public class PerFilterTablePartTests
{
    private static (PerFilterTablePart Host, Cards.Harness Harness, Window Window) RigPart(
        SessionDetail detail, IReadOnlyList<CustomColumnDefinition>? columns = null)
        => RigHost(detail, card => new PerFilterTablePart { DataContext = card }, columns);

    [Fact]
    public void FilterRows_OneFilterAtOneExposure_IsOneRow()
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            FilterMedians = [new FilterMedians("Ha", 2.28d, 0.39d, 1.88d, 0.44d, 1490d)],
            FilterDetails = [new FilterDetailRow("Ha", 40, 12_000d, 2.28d, 0.39d, 300d)],
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        var row = Assert.Single(harness.Card.FilterRows);
        Assert.Equal("Ha", row.FilterName);
        Assert.False(row.IsSubRow);
        Assert.NotNull(row.Swatch);
        Assert.Equal("2.28", row.MedianHfrText);
        Assert.Equal("1.88", row.MedianFwhmText);
        Assert.Equal("300", row.ExposureTimeText);
        Assert.Equal("40", row.FrameCountText);
        Assert.Equal("3.3", row.IntegrationText);
    }

    [Fact]
    public void FilterRows_OneFilterAtTwoExposures_IsARowPlusTwoSubRows()
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            FilterMedians = [new FilterMedians("R", 1.75d, 0.34d, 3.41d, 0.61d, 312d)],
            FilterDetails =
            [
                new FilterDetailRow("R", 15, 450d, 1.69d, 0.36d, 30d),
                new FilterDetailRow("R", 19, 5700d, 1.78d, 0.33d, 300d),
            ],
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(3, harness.Card.FilterRows.Count);
        var parent = harness.Card.FilterRows[0];
        Assert.False(parent.IsSubRow);
        Assert.Equal("R", parent.FilterName);

        // The Exp cell reads how many exposures there are, in tertiary ink, because a filter shot
        // at two exposures has no single exposure to print. Frames and Int are the sums of the two
        // sub-rows below, so a reader can add the visible rows up and get the
        // number above them: 15 plus 19, and 450 s plus 5700 s as hours.
        Assert.Equal("2", parent.ExposureTimeText);
        Assert.True(parent.IsExposureCount);
        Assert.Equal("34", parent.FrameCountText);
        Assert.Equal("1.7", parent.IntegrationText);

        Assert.True(harness.Card.FilterRows[1].IsSubRow);
        Assert.Equal("30 s", harness.Card.FilterRows[1].FilterName);
        Assert.Equal("15", harness.Card.FilterRows[1].FrameCountText);
        Assert.True(harness.Card.FilterRows[2].IsSubRow);
        Assert.Equal("300 s", harness.Card.FilterRows[2].FilterName);
    }

    [Fact]
    public void FilterRows_ASubRow_HasNoSwatchAndDashesForFwhmRmsAndStars()
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            FilterMedians = [new FilterMedians("R", 1.75d, 0.34d, 3.41d, 0.61d, 312d)],
            FilterDetails =
            [
                new FilterDetailRow("R", 15, 450d, 1.69d, 0.36d, 30d),
                new FilterDetailRow("R", 19, 5700d, 1.78d, 0.33d, 300d),
            ],
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        var sub = harness.Card.FilterRows[1];
        Assert.Null(sub.Swatch);
        Assert.Equal(MetricText.Missing, sub.MedianFwhmText);
        Assert.Equal(MetricText.Missing, sub.MedianGuidingRmsText);
        Assert.Equal(MetricText.Missing, sub.MedianDetectedStarsText);

        // A FilterDetailRow does carry HFR and eccentricity, so those two are real figures.
        Assert.Equal("1.69", sub.MedianHfrText);
        Assert.Equal("0.36", sub.MedianEccentricityText);
    }

    [Fact]
    public void FilterRows_ADetailRowWithNoMediansRow_StillRenders()
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            FilterMedians = [new FilterMedians("Ha", 2.28d, 0.39d, 1.88d, 0.44d, 1490d)],
            FilterDetails =
            [
                new FilterDetailRow("Ha", 40, 12_000d, 2.28d, 0.39d, 300d),
                new FilterDetailRow("SII", 12, 3600d, 2.50d, 0.41d, 300d),
            ],
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(2, harness.Card.FilterRows.Count);
        var orphan = harness.Card.FilterRows[1];
        Assert.Equal("SII", orphan.FilterName);
        Assert.False(orphan.IsSubRow);
        Assert.NotNull(orphan.Swatch);
        Assert.Equal("12", orphan.FrameCountText);
        Assert.Equal(MetricText.Missing, orphan.MedianFwhmText);
    }

    [Fact]
    public void FilterRows_AMediansRowWithNoDetailRows_DashesExpFramesAndHours()
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            FilterMedians = [new FilterMedians("Ha", 2.28d, 0.39d, 1.88d, 0.44d, 1490d)],
            FilterDetails = [],
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        var row = Assert.Single(harness.Card.FilterRows);
        Assert.Equal(MetricText.Missing, row.ExposureTimeText);
        Assert.Equal(MetricText.Missing, row.FrameCountText);
        Assert.Equal(MetricText.Missing, row.IntegrationText);
        Assert.False(row.IsExposureCount);
    }

    [AvaloniaFact]
    public void EachRigCell_ShowsItsColumnsNameAsACaption()
    {
        // Ruling C23 (fix pass 1): a rig line has no header row, so a bare editor has no visible
        // name. Two rig-scope columns of different kinds, so the caption is proven for both a check
        // box and a list rather than only the kind every other case in this file already uses. Red
        // against a cell strip with no caption at all, or a caption that repeats the label or the
        // frame count instead of the column's own name.
        var done = CustomColumnTestFactory.Define("Done", CustomColumnType.Boolean, CustomColumnScope.Rig, [], order: 0);
        var quality = CustomColumnTestFactory.Define(
            "Quality", CustomColumnType.Dropdown, CustomColumnScope.Rig, ["Good", "Poor"], order: 1);
        var (part, harness, _) = RigPart(SingleRigDetail(RigA), [done, quality]);
        using var scope = harness;

        var label = Assert.Single(VisibleLabelRows(part, "FilterRigLabelRow"));
        var captionTexts = label.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")
            .ToList();

        Assert.Contains("Done", captionTexts);
        Assert.Contains("Quality", captionTexts);

        // Each CustomCellEditor realizes all three of its own kinds in one Panel and shows one by
        // IsVisible (Task 3's design), so a plain OfType<CheckBox>() walk over two editors finds
        // one visible box and one hidden one belonging to the dropdown cell; visibility is what
        // distinguishes the realized editor from its two siblings.
        Assert.Equal(2, label.GetVisualDescendants().OfType<CustomCellEditor>().Count());
        Assert.Single(
            label.GetVisualDescendants().OfType<CheckBox>(),
            box => box.Name == "CheckEditor" && box.IsEffectivelyVisible);
        Assert.Single(
            label.GetVisualDescendants().OfType<ComboBox>(),
            box => box.Name == "ChoiceEditor" && box.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TheRigCellsCaption_IsNotAnnouncedTwiceToAScreenReader()
    {
        // Phase review, target P3-5. The caption before each rig cell is decoration: the editor's own
        // automation name already begins with the same column name, so a reader heard "Done" and then
        // "Done, 2026-03-14, <rig label>". The file's own LedgerRmsMarkCell precedent is the answer.
        //
        // Red against the caption with no AccessibilityView: it reads Content, which is the default,
        // so the duplicate is in the tree the screen reader walks.
        var done = CustomColumnTestFactory.Define("Done", CustomColumnType.Boolean, CustomColumnScope.Rig, [], order: 0);
        var (part, harness, _) = RigPart(SingleRigDetail(RigA), [done]);
        using var scope = harness;

        var label = Assert.Single(VisibleLabelRows(part, "FilterRigLabelRow"));
        var caption = Assert.Single(
            label.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Done");

        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(caption));

        // The name itself is still on the realized editor, so nothing was made unreachable: only the
        // visible duplicate left the reader's tree. The name sits on each kind's own control, which
        // is where CustomCellEditor.axaml binds it.
        var box = Assert.Single(
            label.GetVisualDescendants().OfType<CheckBox>(),
            control => control.Name == "CheckEditor" && control.IsEffectivelyVisible);
        Assert.StartsWith("Done", AutomationProperties.GetName(box), StringComparison.Ordinal);
    }

    private const double WideFigure = 123456.789d;

    private const double FilterHfrMinWidthCell = 82d - 16d;

    [AvaloniaFact]
    public void PerFilterTablePart_FilterTableColumns_ReportEqualWidthsAfterLayout()
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            FilterMedians = [new FilterMedians("Ha", WideFigure, 0.39d, 1.88d, 0.44d, 1490d)],
            FilterDetails = [new FilterDetailRow("Ha", 40, 12_000d, WideFigure, 0.39d, 300d)],
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        var pane = new PerFilterTablePart { DataContext = harness.Card };
        Show(pane);
        Dispatcher.UIThread.RunJobs();

        var rows = TableRows(pane, "FilterTable");
        Assert.True(rows.Count >= 2, "the filter table rendered no rows under its header");

        // A Number heading right-aligns over its figures, so the header and the wide figure end on
        // one edge.
        var header = TableCellAt(rows[0], "hfr");
        var first = TableCellAt(rows[1], "hfr");
        Assert.True(
            first.Bounds.Width > FilterHfrMinWidthCell,
            $"the seeded HFR figure is not wider than the column minimum ({first.Bounds.Width}).");
        Assert.Equal(header.Bounds.Right, first.Bounds.Right, 0.5);
    }

    [AvaloniaFact]
    public void PerFilterTablePart_ASubRow_IsIndentedUnderItsFilter()
    {
        // An exposure reads as belonging to the filter above it. The indent is a spacer inside the
        // label cell, not a margin, so the cell's own gutter stays the table's.
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            FilterMedians = [new FilterMedians("R", 1.75d, 0.34d, 3.41d, 0.61d, 312d)],
            FilterDetails =
            [
                new FilterDetailRow("R", 15, 450d, 1.69d, 0.36d, 30d),
                new FilterDetailRow("R", 19, 5700d, 1.78d, 0.33d, 300d),
            ],
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        var pane = new PerFilterTablePart { DataContext = harness.Card };
        Show(pane);
        Dispatcher.UIThread.RunJobs();

        var rows = TableRows(pane, "FilterTable");
        Assert.Equal(4, rows.Count);

        var parentCell = (Panel)TableCellAt(rows[1], "name");
        var subCell = (Panel)TableCellAt(rows[2], "name");
        Assert.Equal(TableMetrics.Gutter, parentCell.Bounds.X);
        Assert.Equal(TableMetrics.Gutter, subCell.Bounds.X);

        // 12 is the parent's 7 px dot and its 5 px margin, which a sub-row has no swatch for.
        var parentName = parentCell.Children[^1];
        var subName = subCell.Children[^1];
        Assert.Equal(TableMetrics.SubRowIndent - 12d, subName.Bounds.X - parentName.Bounds.X);
    }

    [AvaloniaFact]
    public void PerFilterTablePart_ARigLabelRow_SpansTheRow()
    {
        var (pane, harness) = ThumbnailKit.RigHost(card => new PerFilterTablePart { DataContext = card }, [ThumbnailKit.RigA, ThumbnailKit.RigB]);
        using var scope = harness;
        Dispatcher.UIThread.RunJobs();

        // The label row is the item's second child, shown instead of its TableRow, so it spans the
        // item between the two gutters.
        var labels = pane.Named<ItemsControl>("FilterRowsList")
            .GetVisualDescendants()
            .OfType<ContentControl>()
            .Where(panel => panel.Name == "FilterRigLabelRow" && panel.IsEffectivelyVisible)
            .ToList();

        Assert.Equal(2, labels.Count);
        Assert.All(labels, label => Assert.Equal(
            label.FindAncestorOfType<ContentPresenter>()!.Bounds.Width,
            label.Bounds.Width + 2 * TableMetrics.Gutter,
            0.5));

        var header = pane.Named<TableRow>("FilterTableHeader");
        Assert.Equal(10, header.Columns!.Count);
        Assert.Equal(10, header.ColumnDefinitions.Count);

        Assert.Contains(ThumbnailKit.RigA, VisibleTexts(pane));
        Assert.Contains(ThumbnailKit.RigB, VisibleTexts(pane));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PerFilterTablePart_MeetsTableConventions(bool extraLarge)
    {
        using var harness = Cards.Create(detail: Cards.PopulatedDetail() with
        {
            FilterMedians = [new FilterMedians("R", 1.75d, 0.34d, 3.41d, 0.61d, 312d)],
            FilterDetails =
            [
                new FilterDetailRow("R", 15, 450d, 1.69d, 0.36d, 30d),
                new FilterDetailRow("R", 19, 5700d, 1.78d, 0.33d, 300d),
                new FilterDetailRow("SII", 12, 3600d, 2.50d, 0.41d, 300d),
            ],
        });

        harness.Card.IsExpanded = true;
        harness.Settle();

        var pane = new PerFilterTablePart { DataContext = harness.Card };
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