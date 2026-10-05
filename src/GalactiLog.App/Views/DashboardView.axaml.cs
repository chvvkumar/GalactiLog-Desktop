using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views.Dashboard;

namespace GalactiLog.App.Views;

public partial class DashboardView : UserControl
{
    /// <summary>The splitter's own column while the panel is expanded. The markup declares the
    /// GridSplitter 4 wide and the platform control theme floors it at 6, which is what the column
    /// measures; the column is zero while the panel is the strip, and the bound below is only
    /// consulted in the expanded branch.</summary>
    private const double SplitterColumnWidth = 6d;

    /// <summary>The target list column's leading margin, declared 8,0,0,0 in the markup.</summary>
    private const double ListColumnMargin = 8d;

    /// <summary>What TargetListRegion costs the list inside it: Padding 16 and BorderThickness 1,
    /// both sides, as the markup declares them.</summary>
    private const double ListRegionChrome = (2 * 16d) + (2 * 1d);

    public DashboardView()
    {
        InitializeComponent();

        // Ruling E1's commit. GridSplitter.ShowsPreview drags a preview adorner and writes the
        // panel column's Width exactly once, when the drag is released, so this one handler is the
        // commit and there is no per-tick write to swallow. It reads the column rather than the
        // splitter's own DragCompleted, so the commit does not depend on the order that handler
        // runs in relative to the splitter's own resize.
        //
        // The clamp and the persist both live on the view-model; this hands it a figure and then
        // re-asserts whatever the view-model decided, which is review finding P2.
        //
        // The re-assertion is needed because the binding alone cannot carry it. Avalonia's binding
        // expression caches the last value it produced and skips publishing an unchanged one, so a
        // repeated over-drag escapes the clamp: 300 to 600 clamps to 480 and the binding pushes it,
        // but 480 to 680 clamps to 480 again, the binding produces the same 480 it last published
        // and stays silent, and the column keeps the 680 the GridSplitter wrote locally while the
        // view-model and the document both say 480. Every further drag then widens it again with no
        // bound, and the same holds below 220. Raising the property change on the view-model does
        // not help for the same reason, which was measured rather than assumed: with that raise in
        // place and this line absent, the second over-drag still rendered 680.
        //
        // The binding is not detached by the splitter's local write; the first clamped drag proves
        // that, because the binding pushes 480 over it correctly. It is only the unchanged-value
        // case that needs this.
        //
        // The write below re-enters this handler once, with a figure that is already clamped and a
        // column that now agrees, so the second pass returns at the equality test.
        var root = this.GetControl<Grid>("DashboardRoot");
        var column = root.ColumnDefinitions[0];

        column.PropertyChanged += (sender, args) =>
        {
            if (args.Property != ColumnDefinition.WidthProperty
                || DataContext is not DashboardViewModel page
                || page.IsFilterPanelShowingStrip)
            {
                return;
            }

            var definition = (ColumnDefinition)sender!;

            if (definition.Width is { IsAbsolute: true } width)
            {
                // The binding's own push, including the layout bound below, is not a drag and must
                // not be committed: the bound would otherwise be written back into
                // display.dashboard.filter_panel_width and the stored figure would shrink with the
                // window (fixer-list item 6). A drag that lands exactly on the rendered width
                // changes nothing and raises nothing, so nothing is lost by returning here.
                if (width == page.FilterPanelColumnWidth)
                {
                    return;
                }

                // A panel forced open under the bound renders at its 220 floor and cannot be
                // widened, so a drag there is re-asserted below but commits nothing: the stored
                // width belongs to the windows the panel actually fits in.
                if (!page.IsFilterPanelForcedOpen)
                {
                    page.FilterPanelWidth = width.Value;
                }

                if (definition.Width != page.FilterPanelColumnWidth)
                {
                    definition.Width = page.FilterPanelColumnWidth;
                }
            }
        };

        // fixer-list item 6, phase-review P2. Task 5's 1024 by 700 window minimum, Task 2's 480
        // panel maximum and Task 3's no-scroller trimming rule did not hold together: at the floor
        // the list was handed 448 pixels with the panel at its 300 default and 268 at 480, against
        // a row that needs TargetListView.ListMinWidth, so Last Session, the Expand button and the
        // pager's page-size select clipped off the trailing edge with no way to reach them.
        //
        // The panel's rendered width is therefore bounded by what the list needs. This computes
        // the bound from the grid's own arranged width and hands it to the view-model, which
        // resolves it against the stored width and the 220 floor; below that floor the panel
        // renders as its 48 pixel strip, and the page has no expanded panel state at that width.
        // Nothing here writes a setting, and the read is a straight Bounds read inside the layout
        // pass, on the UI thread, with no continuation (TRACKING section 5).
        root.PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.BoundsProperty && DataContext is DashboardViewModel page)
            {
                page.MaxRenderedPanelWidth = root.Bounds.Width
                    - SplitterColumnWidth - ListColumnMargin - ListRegionChrome - TargetListView.ListMinWidth;
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
