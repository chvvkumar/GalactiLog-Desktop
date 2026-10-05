using Avalonia;
using Avalonia.Controls;
using GalactiLog.App.ViewModels.Settings;

namespace GalactiLog.App.Views.Settings;

/// <summary>Design-spec 12.7's Equipment tab. Both sections host
/// <see cref="GroupingEditorView"/>, which owns the one colour-box commit handler this shape
/// needs, and Equipment does not show a colour swatch at all. The only logic here is Phase 15B
/// Task 5c's scroll to the PHD2 profiles panel, which is a view gesture over a control rather
/// than view-model state, exactly as <see cref="LibraryTabView"/>'s rule editor scroll is.</summary>
public partial class EquipmentTabView : UserControl
{
    public EquipmentTabView() => InitializeComponent();

    /// <summary>
    /// Spec 12.5: the Statistics page's Guiding empty notice offers "Map profiles", and the
    /// shell's Settings route asks this tab for the panel.
    /// </summary>
    /// <remarks>
    /// The request lands on the view-model because the shell runs before this view is attached,
    /// and often before the tab is constructed at all.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is EquipmentTabViewModel viewModel && viewModel.ConsumePhd2ProfilesInViewRequest())
        {
            ScrollWhenLoaded(viewModel, ScrollPhd2ProfilesIntoView);
        }
    }

    private void ScrollPhd2ProfilesIntoView()
        => this.FindControl<Border>("Phd2ProfilesSection")?.BringIntoView();

    // The pending wait, so a detach drops a handler this view put on a tab view-model that
    // outlives it. Null when nothing is waiting.
    private Action? _cancelPendingScroll;

    /// <summary>
    /// Runs a scroll once this tab has finished its first read: <see cref="SettingsTabScroll"/>
    /// with this tab's gate, which is <c>IsLoading</c> falling rather than the Library tab's
    /// <c>IsReady</c>. The panel's own border is visible from construction, so what this waits
    /// for is content: the panel sits below the cameras and telescopes editors, which are empty
    /// until the tab's pool read publishes, and a scroll taken before that lands at an offset the
    /// growing content then pushes the panel past.
    /// </summary>
    private void ScrollWhenLoaded(EquipmentTabViewModel viewModel, Action scroll)
    {
        _cancelPendingScroll?.Invoke();
        _cancelPendingScroll = SettingsTabScroll.RunWhenReady(
            viewModel, () => !viewModel.IsLoading, nameof(EquipmentTabViewModel.IsLoading), scroll);
    }

    /// <summary>Drops a wait that never came good, so a failed read leaves no handler on the tab.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cancelPendingScroll?.Invoke();
        _cancelPendingScroll = null;
        base.OnDetachedFromVisualTree(e);
    }
}
