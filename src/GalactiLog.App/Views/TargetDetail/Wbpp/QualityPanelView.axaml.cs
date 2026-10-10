using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GalactiLog.App.Views.TargetDetail.Wbpp;

/// <summary>
/// design-spec 12.13's quality filter panel: the chips toolbar and the verdict table.
/// </summary>
/// <remarks>
/// The one thing the markup cannot say is how tall the verdict rows may be. The panel sits inside
/// the export wizard's own body scroller, which offers unbounded height, so the rows' scroller needs
/// a finite bound or its virtualising panel realises every frame. A fixed bound left most of a tall
/// window empty; the bound is instead whatever the body's viewport has left below the rows' top
/// edge, less the footer note, and never under <see cref="MinRowsHeight"/>.
/// </remarks>
public partial class QualityPanelView : UserControl
{
    /// <summary>The rows' floor, about six rows, for a window too short to give them more.</summary>
    internal const double MinRowsHeight = 200d;

    public QualityPanelView()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => FitRowsToViewport();
    }

    private void FitRowsToViewport()
    {
        var body = this.FindAncestorOfType<ScrollViewer>();
        if (body is null || !RowsScroller.IsVisible || RowsScroller.TranslatePoint(default, body) is not { } top)
        {
            return;
        }

        var below = FooterNote.Bounds.Height + FooterNote.Margin.Top;
        var fit = Math.Max(MinRowsHeight, body.Viewport.Height - (top.Y + body.Offset.Y) - below);

        // Setting MaxHeight lays out again and raises LayoutUpdated, so only a real change writes.
        if (Math.Abs(RowsScroller.MaxHeight - fit) > 0.5d)
        {
            RowsScroller.MaxHeight = fit;
        }
    }
}
