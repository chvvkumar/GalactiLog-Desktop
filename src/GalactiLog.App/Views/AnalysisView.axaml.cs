using Avalonia;
using Avalonia.Controls;
using GalactiLog.App.ViewModels.Analysis;

namespace GalactiLog.App.Views;

/// <summary>
/// Spec 12.14's Analysis page, bound to <see cref="AnalysisViewModel"/>.
/// </summary>
/// <remarks>
/// The one thing this class does beyond giving the markup a partial to compile into is ruling
/// A12's seam: the page constructs without querying anything, and the first query is the selected
/// tab's own first refresh, which runs when the view binds. Both overrides call the same
/// idempotent member, because a view can be handed its view-model before it attaches or after it,
/// and a page that activated on only one of the two orders would open blank in the other.
/// </remarks>
public partial class AnalysisView : UserControl
{
    public AnalysisView() => InitializeComponent();

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Activate();
    }

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Activate();
    }

    private void Activate() => (DataContext as AnalysisViewModel)?.Activate();
}
