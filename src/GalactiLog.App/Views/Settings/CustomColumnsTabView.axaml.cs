using Avalonia;
using Avalonia.Controls;
using GalactiLog.App.ViewModels.Settings;

namespace GalactiLog.App.Views.Settings;

/// <summary>
/// Spec 12.15's Custom Columns settings tab. The view-model's own constructor does not start its
/// first read (<see cref="CustomColumnsTabViewModel"/>'s remarks record why), so this attach hook
/// is what does: the same shape every other lazily-constructed tab in this application uses to
/// read its document on first visit, except that here it is the view rather than the constructor
/// that asks, because the tab's writes can raise a property change before this view exists.
/// </summary>
public partial class CustomColumnsTabView : UserControl
{
    public CustomColumnsTabView() => InitializeComponent();

    /// <summary>
    /// Review P3-2: Avalonia's own reparenting during a measure pass can fire this hook more than
    /// once for what is conceptually one visit, and a re-attach while the first load is still in
    /// flight must not start a second. A completed (or never-started) load still refreshes on
    /// re-attach, which is what lets a later, genuine revisit of the tab pick up a change made
    /// elsewhere.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is CustomColumnsTabViewModel viewModel && viewModel.PendingLoad is not { IsCompleted: false })
        {
            _ = viewModel.RefreshAsync();
        }
    }
}
