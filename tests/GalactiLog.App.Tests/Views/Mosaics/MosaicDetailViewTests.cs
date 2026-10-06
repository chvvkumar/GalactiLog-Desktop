using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.Views.Mosaics;
using GalactiLog.Core.Io;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views.Mosaics;

// Phase 18 Task 5, design-spec 18.3's view smoke test for spec 12.17's mosaic detail page: the
// view constructs and lays out over a populated page with the labels banner showing and a panel
// expanded, as the five-row workbench the spec draws, with Phase 19A's arranger in row 2.
public class MosaicDetailViewTests
{
    [AvaloniaFact]
    public async Task ThePopulatedPage_LaysOutAsTheWorkbench()
    {
        var target = Guid.NewGuid();
        var mosaic = new FakeMosaic();
        mosaic.Catalogue.Add(new(target, "M 31", new DateOnly(2026, 3, 1), "Panel 1", 4, 1200, "Ha"));
        mosaic.Catalogue.Add(new(target, "M 31", new DateOnly(2026, 3, 2), "Panel 1", 2, 600, "OIII"));
        var panel = mosaic.AddPanel("Panel 1");
        mosaic.Row(panel, target, new DateOnly(2026, 3, 1), "Panel 1", included: true);
        mosaic.AvailableLabels.Add(new AvailableLabel(target, "M 31", "Panel 2"));
        using var page = new MosaicDetailViewModel(
            mosaic.Id, mosaic.Backend(), new AppWriter(Path.GetTempPath()), post: action => action());
        await page.PendingLoad;
        page.Panels[0].ToggleExpandCommand.Execute(null);

        var view = new MosaicDetailView { DataContext = page };
        var window = new Window { Width = 1280, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Bounds.Width > 0);
        var grid = Assert.IsType<Grid>(view.Content);
        Assert.Equal(5, grid.RowDefinitions.Count);
        // Phase 19A: the arranger fills the starred row 2 in place of Phase 18's placeholder band.
        var arranger = Assert.Single(view.GetVisualDescendants().OfType<ArrangerView>());
        Assert.Equal(2, Grid.GetRow(arranger));
        Assert.Same(page.Arranger, arranger.DataContext);
        Assert.True(arranger.Bounds.Height > 0);
        // The handle's height is the GridSplitter style's (spec 14.5), not a local value.
        Assert.Equal(8d, view.FindControl<GridSplitter>("ArrangerSplitter")!.Bounds.Height, 3);

        // One scroller, the sessions region's: the page itself does not scroll (ruling R7).
        var scrollers = view.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(scroller => scroller.FindAncestorOfType<ScrollViewer>() is null && scroller.FindAncestorOfType<TextBox>() is null)
            .ToList();
        Assert.Equal("SessionsScroller", Assert.Single(scrollers).Name);
        Assert.True(view.FindControl<ScrollViewer>("SessionsScroller")!.Bounds.Bottom <= view.Bounds.Height + 0.5);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text).ToList();
        Assert.Contains("Panels", texts);
        Assert.Contains("Panel 2 on M 31", texts);
        Assert.Contains("Panels and nights", texts);
        Assert.Contains("Included", texts);
        Assert.Contains("Available", texts);

        var topics = view.GetVisualDescendants().OfType<HelpButton>().Select(button => button.Topic).ToList();
        Assert.Equal(new[] { "mosaic.about", "mosaic.notes", "mosaic.labels", "mosaic.arranger", "mosaic.sessions" }, topics);

        var composite = view.FindControl<Button>("CompositeButton")!;
        Assert.False(composite.IsEffectivelyEnabled);
        Assert.Equal(MosaicDetailViewModel.NoFramesTooltip, ToolTip.GetTip(composite));
        window.Close();
    }
}
