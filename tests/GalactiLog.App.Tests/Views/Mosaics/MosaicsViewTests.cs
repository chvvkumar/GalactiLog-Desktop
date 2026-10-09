using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.Views.Mosaics;
using GalactiLog.Core.Mosaics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.Views.Mosaics;

// Phase 18 Task 4, design-spec 18.3's view smoke test for spec 12.17's Mosaics page: the view
// constructs and lays out over a populated page with a suggestion and a mosaic row expanded.
public class MosaicsViewTests
{
    private static readonly Guid Target = Guid.NewGuid();
    private static readonly DateOnly Night = new(2026, 3, 1);

    private static readonly CustomColumnDefinition Owner = new(
        Guid.NewGuid(), "Owner", "custom_owner", CustomColumnType.Text, CustomColumnScope.Mosaic, [], 0, DateTime.UtcNow, 0);

    // A suggestion with one session, a second mosaic with no night (the faint dash) and one
    // mosaic-scope custom column, which the column gear switches on.
    private static MosaicsBackend Populated()
    {
        var panel = new PanelDetail(
            Guid.NewGuid(), "Panel 1", 0, null, null, 0, false, [Target], ["M 31"], 3600, 12, 1, 0, 0, [], [],
            new Dictionary<string, double>());
        return new MosaicsBackend
        {
            ListPending = () =>
            [
                new MosaicSuggestionRow(
                    Guid.NewGuid(), "NGC 7000", "NGC 7000", [new SuggestionPanel(Target, "Panel 1", "%", [Night])],
                    "low", "both", null, ["Panel 2 has no position"], "sig", DateTime.UtcNow),
            ],
            SuggestionSessions = _ => [new SuggestionSessionRow(Target, "Panel 1", "NGC 7000 P1", Night, "Ha", 1_204, 1200, true)],
            ListMosaics = () =>
            [
                new MosaicListRow(Guid.NewGuid(), "M 31", 1, 3600, 12, Night, Night, ["Ha"]),
                new MosaicListRow(Guid.NewGuid(), "NGC 1", 0, 0, 0, null, null, []),
            ],
            Detail = id => new MosaicDetail(id, "M 31", null, 0, 3600, 12, Night, Night, ["Ha"], [panel], []),
            CustomColumns = () => [Owner],
            WriteValue = (_, _, _) => new CustomWriteResult(CustomWriteStatus.Written, null),
        };
    }

    private static async Task<(MosaicsPageHarness Harness, MosaicsView View, Window Window)> ShowPopulated()
    {
        var harness = new MosaicsPageHarness(Populated());
        await harness.Page.PendingLoad;
        harness.Page.VisibleSuggestions[0].IsExpanded = true;
        harness.Page.Table.Mosaics[0].ToggleExpandCommand.Execute(null);

        var view = new MosaicsView { DataContext = harness.Page };
        var window = new Window { Width = 1280, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (harness, view, window);
    }

    private static TableRow Header(MosaicsView view, string id)
        => view.GetVisualDescendants().OfType<TableRow>().First(row => row.Kind == RowKind.Header && row.Columns!.Id == id);

    private static IReadOnlyList<TableRow> DataRows(MosaicsView view, string id)
        => [.. view.GetVisualDescendants().OfType<TableRow>().Where(row => row.Kind == RowKind.Data && row.Columns!.Id == id)];

    [AvaloniaFact]
    public async Task ThePopulatedPage_LaysOut_WithAScrollerPerColumn()
    {
        var (harness, view, window) = await ShowPopulated();
        using var _ = harness;

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        var scrollers = view.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(scroller => scroller.FindAncestorOfType<ScrollViewer>() is null)
            .ToList();
        Assert.Equal(2, scrollers.Count);
        Assert.All(scrollers, scroller => Assert.True(scroller.Bounds.Right <= view.Bounds.Width + 0.5));
        Assert.Contains(
            view.GetVisualDescendants().OfType<Button>(),
            button => button.Content as string == "Run Detection");
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Panel 2 has no position" && block.IsEffectivelyVisible);

        // Both tables against the spine's census (spine-spec 6.2), and again at the x-large text
        // size.
        TableAssert.Conventions(view);
        window.FontSize = 20;
        Dispatcher.UIThread.RunJobs();
        TableAssert.Conventions(view);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheSuggestionSessionHeader_IsSentenceCase()
    {
        var (harness, view, window) = await ShowPopulated();
        using var _ = harness;

        var heads = Header(view, "SuggSess").GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();

        Assert.Equal(["Panel", "Object", "Night", "Filter", "Frames", "Integration"], heads);
        Assert.DoesNotContain(heads, text => text == text!.ToUpperInvariant());
        window.Close();
    }

    [AvaloniaFact]
    public async Task AMosaicCustomCell_IsAsWideAsItsHeading()
    {
        // The fixed 140 the strip used to carry is gone: a heading and its cells are measured from
        // the heading, as on the dashboard and the Nights list.
        var (harness, view, window) = await ShowPopulated();
        using var _ = harness;
        var picker = harness.Page.Table.Picker;
        picker.ToggleCommand.Execute(picker.Columns.Single(column => column.Key == Owner.Slug));
        await harness.Writer.Pending;
        Dispatcher.UIThread.RunJobs();

        var expected = CustomCellWidths.ForHeading(Owner.Name, Owner.Type, view.FontSize, view.FontFamily);
        var heading = Header(view, "Mosaic").GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == Owner.Name);
        var cell = DataRows(view, "Mosaic")[0].GetVisualDescendants().OfType<CustomCellEditor>().Single().Parent as Control;

        Assert.Equal(expected, heading.Bounds.Width, 1);
        Assert.Equal(expected, cell!.Bounds.Width, 1);
        Assert.NotEqual(140d, expected, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HidingAColumn_CollapsesItsWidth()
    {
        // A hidden column leaves its shared size group, so it gives its width back instead of
        // keeping an empty gap in the header and every row.
        var (harness, view, window) = await ShowPopulated();
        using var _ = harness;
        Assert.True(Header(view, "Mosaic").ColumnDefinitions[2].ActualWidth > 0);

        var picker = harness.Page.Table.Picker;
        picker.ToggleCommand.Execute(picker.Columns.Single(column => column.Key == "panels"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0d, Header(view, "Mosaic").ColumnDefinitions[2].ActualWidth);
        Assert.All(DataRows(view, "Mosaic"), row => Assert.Equal(0d, row.ColumnDefinitions[2].ActualWidth));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheMosaicFigures_UseTabularDigits()
    {
        var (harness, view, window) = await ShowPopulated();
        using var _ = harness;

        foreach (var key in new[] { "panels", "integration", "frames" })
        {
            var cell = (TextBlock)DataRows(view, "Mosaic")[0].Children.Single(child => TableRow.GetCol(child) == key);
            Assert.Contains("tc-num", cell.Classes);
            Assert.Contains(cell.FontFeatures!, feature => feature.Tag == "tnum");
        }

        // And a mosaic with no night reads the faint dash.
        Assert.Contains(
            DataRows(view, "Mosaic")[1].Children.OfType<TextBlock>(),
            block => TableRow.GetCol(block) == "daterange" && block.Text == "-");
        window.Close();
    }

    // Phase 19A Task 4, spec 12.17's read-only preview: an expanded suggestion row shows the
    // arranger under "Tile preview", not hit-testable and with no toolbar, one tile per checked
    // label, and "No panels selected." once every label is unchecked.
    [AvaloniaFact]
    public async Task AnExpandedSuggestion_ShowsTheReadOnlyTilePreview()
    {
        var target = Guid.NewGuid();
        var night = new DateOnly(2026, 3, 1);
        using var harness = new MosaicsPageHarness(new MosaicsBackend
        {
            ListPending = () =>
            [
                new MosaicSuggestionRow(
                    Guid.NewGuid(), "NGC 7000", "NGC 7000",
                    [new SuggestionPanel(target, "Panel 1", "%", [night]), new SuggestionPanel(target, "Panel 2", "%", [night])],
                    "high", "both", null, [], "sig", DateTime.UtcNow),
            ],
            SuggestionSessions = _ =>
            [
                new SuggestionSessionRow(target, "Panel 1", "NGC 7000 P1", night, "Ha", 4, 1200, true),
                new SuggestionSessionRow(target, "Panel 2", "NGC 7000 P2", night, "Ha", 2, 600, true),
            ],
        });
        await harness.Page.PendingLoad;
        var row = harness.Page.VisibleSuggestions[0];
        row.IsExpanded = true;
        await row.Preview.PendingFrames;

        var view = new MosaicsView { DataContext = harness.Page };
        var window = new Window { Width = 1280, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var preview = Assert.Single(view.GetVisualDescendants().OfType<ArrangerView>());
        Assert.Same(row.Preview, preview.DataContext);
        Assert.False(preview.IsHitTestVisible);
        Assert.True(preview.IsEffectivelyVisible);
        Assert.Equal(200, preview.Bounds.Height, 1);
        Assert.False(preview.FindControl<Control>("Toolbar")!.IsEffectivelyVisible);
        Assert.Equal(2, preview.FindControl<ItemsControl>("TileItems")!.ItemCount);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Tile preview" && block.IsEffectivelyVisible);
        Assert.Contains(
            preview.GetVisualDescendants().OfType<Control>(),
            control => AutomationProperties.GetName(control) is { } name && name.StartsWith("Panel 2", StringComparison.Ordinal)
                && control.IsEffectivelyVisible);

        row.AllPanelsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(preview.IsEffectivelyVisible);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == ArrangerViewModel.NoPanelsText && block.IsEffectivelyVisible);
        window.Close();
    }
}
