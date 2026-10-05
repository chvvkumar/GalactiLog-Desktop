using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Layouts;

namespace GalactiLog.App.Views.TargetDetail;

/// <summary>Spec 12.4's Target detail page. Code-behind carries what a view model cannot express:
/// the measured width for the responsive rule, Escape's ordering (design-spec 18.3), and the
/// layout view in <c>LayoutHost</c>.</summary>
public partial class TargetDetailView : UserControl
{
    public TargetDetailView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        ObjectTypeEditor.PropertyChanged += OnObjectTypeEditorPropertyChanged;

        // Review P2-1: Escape has to reach the object type editor from whatever the focus is,
        // and the Details SplitView eats it first in Overlay mode. See OnPreviewKeyDown.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private bool _attached;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ShowLayout();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ShowLayout();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    /// <summary>Puts the layout view in <c>LayoutHost</c> once a page is set and the shell is attached.</summary>
    private void ShowLayout()
    {
        // A data template hands the page over before this shell has a logical parent; a view built
        // then finds no window above it, and its bindings to the window are never asked again.
        if (!_attached || DataContext is not TargetDetailViewModel || LayoutHost.Content is not null)
        {
            return;
        }

        LayoutHost.Content = Activator.CreateInstance(TargetLayoutRegistry.Default.ViewType);
    }

    /// <summary>
    /// Review P2-1, first half: puts the keyboard on the object type combo box the moment spec
    /// 12.4's pencil opens it, so Escape reaches the box's own handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it, focus after a pencil click is on the pencil, Escape bubbles past the box to
    /// this control's <see cref="OnKeyDown"/>, and the drawer closes with the editor still open.
    /// The same call <c>PreviewModalWindow.OnOpened</c> makes for its viewport (P13 R10), and for
    /// the same reason: a control that takes a key has to be the control the keyboard is on.
    /// </para>
    /// <para>
    /// Driven off the box's own <c>IsVisible</c>, which is bound to
    /// <c>TargetHeaderViewModel.IsEditingObjectType</c>, rather than off a subscription to the
    /// header: the page replaces its header on every load, and a handler chased across those
    /// replacements would be a second lifetime to get wrong for no gain. The second half of the
    /// fix, the Escape term in <see cref="OnKeyDown"/>, does not depend on this one landing.
    /// </para>
    /// </remarks>
    private void OnObjectTypeEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true)
        {
            ObjectTypeEditor.Focus(NavigationMethod.Unspecified);
        }
    }

    /// <summary>
    /// Ruling Q12's breakpoint. The width is a layout fact that only the view has, and the
    /// bindings that answer it (the workbench's column set and the drawer's display mode) are on
    /// the view model, so the view reports and the view model decides.
    /// </summary>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is ViewModels.TargetDetail.TargetDetailViewModel page)
        {
            page.ApplyWidth(e.NewSize.Width);
        }
    }

    /// <summary>
    /// Spec 12.4's object type edit (PAR-009): Escape cancels and leaves the stored value alone,
    /// from wherever on the page the focus happens to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Review P2-1, second half, and the one place on this page a tunnelling handler is the right
    /// answer. It began as a bubbling <c>KeyDown</c> on the combo box itself, which works only
    /// while the box has focus. Measured under the headless harness after the pencil was clicked
    /// and focus left on the pencil: the editor sits inside the Details <c>SplitView</c>, whose own
    /// <c>OnKeyDown</c> closes a light dismissable pane on Escape and marks the key handled, so the
    /// key never left the pane and <see cref="OnKeyDown"/> below never saw it. The drawer closed
    /// with <c>IsEditingObjectType</c> still set. A term inside that method cannot fix it, because
    /// the method is not reached.
    /// </para>
    /// <para>
    /// A tunnelling handler on this control runs before the <c>SplitView</c>, before the combo box
    /// and before every other element in the route, which is what makes the rule hold in every
    /// focus state rather than in one. It is not a fourth page level <c>KeyBinding</c>: unlike a
    /// <c>KeyBinding</c> it consults <c>Handled</c>, it fires only while the editor is open, and it
    /// keeps spec 12.4's "Escape while a text box has focus belongs to the box" through the same
    /// <see cref="IsTypingInATextBox"/> guard the page's own Escape uses, so an open editor cannot
    /// take Escape away from the notes box beside it.
    /// </para>
    /// </remarks>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled
            || e.Key != Key.Escape
            || IsTypingInATextBox(e)
            || DataContext is not ViewModels.TargetDetail.TargetDetailViewModel
            {
                Header: { IsEditingObjectType: true } header,
            })
        {
            return;
        }

        header.CancelObjectTypeCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Ruling Q11's Escape: it closes the Details drawer when the drawer is open, and goes back
    /// otherwise. A <c>KeyBinding</c> cannot express that order, which is why this one key is
    /// here and the other three are in the markup.
    /// </summary>
    /// <remarks>
    /// The guard is Escape's alone, and the phase review's P3-6 (F2 beginning a rename over a
    /// half-typed note) stays open because the other three cannot have it: Avalonia evaluates a
    /// <c>KeyBinding</c> before this handler runs and without consulting <c>Handled</c>, which a
    /// guard here and a tunnelling handler on this control were both measured against and neither
    /// withheld, and moving the three gestures into a handler of this control's own loses Alt+Left
    /// to the access-key path. Phase 13 candidate, with the reproduction in the fixer report.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // An open object type editor has already taken this key in OnPreviewKeyDown above, which
        // runs before every element in the route, so by the time Escape reaches this method the
        // editor is closed and the drawer is the next thing it can mean.
        if (e.Key == Key.Escape
            && !IsTypingInATextBox(e)
            && DataContext is ViewModels.TargetDetail.TargetDetailViewModel page)
        {
            if (page.IsDetailsOpen)
            {
                page.IsDetailsOpen = false;
            }
            else
            {
                page.BackCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Whether the key belongs to a text editor rather than to the page. The rename editor's own
    /// Escape cancels the rename and a notes box's Escape belongs to the box, so a page-level
    /// handler that took every Escape would navigate the user off the page mid-edit (review P3-8,
    /// coordinator ruling 6). The same shape <c>PreviewModalWindow</c> already uses, plus the
    /// event's own source, because a routed key arriving from a control inside a
    /// <see cref="TextBox"/> template is the same fact and is what a test can produce.
    /// </summary>
    private bool IsTypingInATextBox(KeyEventArgs e)
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox
           || e.Source is TextBox
           || (e.Source is Visual visual && visual.FindAncestorOfType<TextBox>() is not null);
}
