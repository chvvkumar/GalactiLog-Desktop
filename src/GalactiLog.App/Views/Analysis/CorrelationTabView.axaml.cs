using Avalonia.Controls;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Analysis;

namespace GalactiLog.App.Views.Analysis;

/// <summary>
/// Spec 12.14's Correlation tab body, bound to
/// <see cref="ViewModels.Analysis.CorrelationTabViewModel"/>. All the behaviour is on the
/// view-model; the one thing here is the chart's height.
/// </summary>
public partial class CorrelationTabView : UserControl
{
    public CorrelationTabView()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => FitChart();
    }

    /// <summary>
    /// Gives the scatter the page viewport less everything else on the page, never below
    /// <see cref="CorrelationTabViewModel.ChartMinimumHeight"/>, so the stats cards end at the fold.
    /// </summary>
    /// <remarks>
    /// "Everything else" is the page's desired height less the chart's own, read after the pass
    /// that just ran. It does not depend on the chart's height, so one correction settles it and
    /// the half pixel dead band stops the pass that follows from writing again. The page's
    /// scroll bars overlay the content rather than taking width, so a scroll bar appearing cannot
    /// re-wrap text and move the figure back.
    /// </remarks>
    private void FitChart()
    {
        if (!Chart.IsEffectivelyVisible
            || this.FindAncestorOfType<ScrollViewer>() is not { } scroller
            || scroller.Presenter?.Child is not { } page)
        {
            return;
        }

        // The page's DESIRED height, measured with unbounded height, and not the scroller's
        // extent: a page shorter than the viewport is stretched to it, so the extent would report
        // the viewport and the chart would never grow.
        var spend = page.DesiredSize.Height - Chart.Bounds.Height;
        var target = Math.Max(
            CorrelationTabViewModel.ChartMinimumHeight, scroller.Viewport.Height - spend);

        if (double.IsFinite(target) && Math.Abs(target - Chart.Height) > 0.5d)
        {
            Chart.Height = target;
        }
    }
}
