using System.IO;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.RigRowKit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

/// <summary>
/// Spec 12.15's rig-scope cells rendered on the two tables' rig label rows (Phase 20 Task 6b):
/// the shipped layout is untouched on a library with no rig-scope column, the cells render on the
/// filter table only (ruling C23) with each cell captioned by its column's name, the
/// single-rig row carries exactly the label, the frame count and the captioned cells, and a press
/// inside a cell never reaches anything above it. No database anywhere: every delegate is a lambda
/// and the write is <see cref="CustomColumnTestFactory.WriteLog"/>.
/// </summary>
public class RigLabelRowTests
{
    // The two tables that draw a rig label row, over one card.
    private static (StackPanel Host, Cards.Harness Harness, Window Window) RigPane(
        SessionDetail detail, IReadOnlyList<CustomColumnDefinition>? columns = null)
        => RigHost(
            detail,
            card => new StackPanel
            {
                Children =
                {
                    new PerFilterTablePart { DataContext = card },
                    new RangesTablePart { DataContext = card },
                },
            },
            columns);

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ANightWithNoRigScopeColumn_LaysOutIdenticallyToTheShippedTables()
    {
        // No PublishCustomColumns call at all: the every-day case of a library that has never
        // created a custom column. Red against a template that reserves width for an empty strip
        // (the ItemsControl's IsVisible="{Binding HasCells}" gate Task 3 landed), which would widen
        // the label row or change the pane's measured height.
        var (pane, harness, _) = RigPane(MultiRigDetail(RigA, RigB));
        using var scope = harness;

        var header = pane.Named<TableRow>("FilterTableHeader");
        Assert.Equal(10, header.ColumnDefinitions.Count);

        // The label row spans its item between the two gutters, no wider.
        var labels = VisibleLabelRows(pane, "FilterRigLabelRow");
        Assert.Equal(2, labels.Count);
        Assert.All(labels, label => Assert.Equal(
            label.FindAncestorOfType<ContentPresenter>()!.Bounds.Width,
            label.Bounds.Width + TableMetrics.ColumnGutters,
            0.5));

        // A label row with no rig-scope column carries no CustomCellEditor: the strip is bound but
        // never realized, which is what an unedited RigLabelRowTemplate already proves empirically
        // here rather than by inspection.
        Assert.Empty(pane.GetVisualDescendants().OfType<CustomCellEditor>());
    }

    [AvaloniaFact]
    public void TheLabelRowsCells_RenderOnTheFirstTableOnly()
    {
        // Ruling C23 (fix pass 1): two editors over one stored value fall out of step, so the rig
        // cells are drawn on the rig line of the filter table only;
        // the second table's rig line (the ranges table) keeps its label and frame count and
        // carries no cells and no group. Red against RigLabelRow still being called with cells on
        // the ranges table's side.
        var column = RigScopeColumn();
        var (pane, harness, _) = RigPane(MultiRigDetail(RigA, RigB), [column]);
        using var scope = harness;

        var filterLabels = VisibleLabelRows(pane, "FilterRigLabelRow");
        var rangeLabels = VisibleLabelRows(pane, "RangeRigLabelRow");
        Assert.Equal(2, filterLabels.Count);
        Assert.Equal(2, rangeLabels.Count);

        // One CustomCellEditor per rig, on the filter table alone.
        Assert.Equal(2, filterLabels.SelectMany(label => label.GetVisualDescendants().OfType<CustomCellEditor>()).Count());
        Assert.Empty(rangeLabels.SelectMany(label => label.GetVisualDescendants().OfType<CustomCellEditor>()));

        Assert.All(
            pane.GetVisualDescendants().OfType<CheckBox>().Where(box => box.Name == "CheckEditor"),
            box => Assert.True(box.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public void TheSingleRigLabelRow_CarriesTheLabelTheFrameCountTheCaptionAndTheCell_AndNothingElse()
    {
        var column = RigScopeColumn("Done");
        var (pane, harness, _) = RigPane(SingleRigDetail(RigA), [column]);
        using var scope = harness;

        var label = Assert.Single(VisibleLabelRows(pane, "FilterRigLabelRow"));
        var texts = label.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")
            .ToList();

        Assert.Contains(RigA, texts);
        Assert.Contains("6 frames", texts);
        Assert.Contains("Done", texts);

        // Red against a row that also draws a block heading or a second frame count: exactly the
        // label, the frame count, the one cell's caption, and nothing textual besides those three.
        Assert.Equal(3, texts.Count);

        Assert.Single(label.GetVisualDescendants().OfType<CustomCellEditor>());

        // And the ranges table's copy of this same rig line carries no caption and no editor at all.
        var rangeLabel = Assert.Single(VisibleLabelRows(pane, "RangeRigLabelRow"));
        Assert.Empty(rangeLabel.GetVisualDescendants().OfType<CustomCellEditor>());
        Assert.DoesNotContain("Done", rangeLabel.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? ""));
    }

    [AvaloniaFact]
    public void AClickOnARigCell_DoesNotLeaveTheCell()
    {
        // The same probe shape task3-review's Facts section 7 pins for a row Button: a watcher
        // between the cell and the tables it sits in, counting presses that arrive unhandled. The
        // falsifiable assertion is that no unhandled press leaves the cell at all.
        var column = RigScopeColumn("Done");
        var (pane, harness, window) = RigPane(SingleRigDetail(RigA), [column]);
        using var scope = harness;

        var unhandled = 0;
        pane.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) => { if (!e.Handled) { unhandled++; } },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        // The row draws on both tables (section 3.1), so this is the filter table's own copy,
        // named rather than picked by position.
        var filterLabel = Assert.Single(VisibleLabelRows(pane, "FilterRigLabelRow"));
        var checkBox = filterLabel.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "CheckEditor");

        Click(window, checkBox);

        Assert.Equal(0, unhandled);
    }

    [Fact]
    public void TheTables_DeclareNoLocalButtonTagOrCalloutStyle()
    {
        // A source scan, the same shape TargetListViewTests.cs:458 takes over its own view: an edit
        // here (had one been needed for a wider column span) must not add a local style, which
        // ControlStyleScanTest's own walk already covers for every other view but is worth pinning
        // directly against the one file this task was allowed to touch.
        // The rig label rows this file covers live in the two table parts now, so the scan reads both.
        foreach (var part in new[] { "PerFilterTablePart.axaml", "RangesTablePart.axaml" })
        {
            var source = File.ReadAllText(FindAxaml("src/GalactiLog.App/Views/TargetDetail/Parts/" + part));

            Assert.DoesNotContain("<Style Selector=\"Button", source);
            Assert.DoesNotContain("<Style Selector=\"ToggleButton", source);
            Assert.DoesNotContain("<Style Selector=\"Border.tag", source);
            Assert.DoesNotContain("<Style Selector=\"Border.callout", source);
            Assert.DoesNotContain("<Style Selector=\"ScrollBar", source);
        }
    }

    // Same walk TargetListViewTests.FindTargetListViewAxaml takes, duplicated rather than shared
    // because that method is private to its own file.
    private static string FindAxaml(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new FileNotFoundException(relativePath);
    }
}
