using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.Views.Mosaics;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.Views.Mosaics;

// Phase 18 Task 4, design-spec 18.3's view smoke test for spec 12.17's Mosaics page: the view
// constructs and lays out over a populated page with a suggestion and a mosaic row expanded.
public class MosaicsViewTests
{
    [AvaloniaFact]
    public async Task ThePopulatedPage_LaysOut_WithAScrollerPerColumn()
    {
        var target = Guid.NewGuid();
        var night = new DateOnly(2026, 3, 1);
        var mosaic = new MosaicListRow(Guid.NewGuid(), "M 31", 1, 3600, 12, night, night, ["Ha"]);
        var panel = new PanelDetail(
            Guid.NewGuid(), "Panel 1", 0, null, null, 0, false, [target], ["M 31"], 3600, 12, 1, 0, 0, [], [],
            new Dictionary<string, double>());
        using var harness = new MosaicsPageHarness(new MosaicsBackend
        {
            ListPending = () =>
            [
                new MosaicSuggestionRow(
                    Guid.NewGuid(), "NGC 7000", "NGC 7000", [new SuggestionPanel(target, "Panel 1", "%", [night])],
                    "low", "both", null, ["Panel 2 has no position"], "sig", DateTime.UtcNow),
            ],
            SuggestionSessions = _ => [new SuggestionSessionRow(target, "Panel 1", "NGC 7000 P1", night, "Ha", 4, 1200, true)],
            ListMosaics = () => [mosaic],
            Detail = id => new MosaicDetail(id, "M 31", null, 0, 3600, 12, night, night, ["Ha"], [panel], []),
        });
        await harness.Page.PendingLoad;
        harness.Page.VisibleSuggestions[0].IsExpanded = true;
        harness.Page.Table.Mosaics[0].ToggleExpandCommand.Execute(null);

        var view = new MosaicsView { DataContext = harness.Page };
        var window = new Window { Width = 1280, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

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
