using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Preview;

namespace GalactiLog.App.Views.Preview;

/// <summary>
/// Spec 11.5's preview modal. The window owns closing, the image gestures (through
/// <see cref="ZoomPanGestures"/>, shared with the composite lightbox), the focus placed on
/// open (P13 R10) and the four bare-key shortcuts, and holds no logic of its own: every shortcut
/// is a command on
/// <see cref="PreviewModalViewModel"/> (spec 18.3), and the view only supplies the geometry the
/// view-model cannot know.
/// </summary>
public partial class PreviewModalWindow : Window
{
    private readonly Border? _viewport;

    private readonly ZoomPanGestures? _gestures;

    private PreviewModalViewModel? _subscribed;

    public PreviewModalWindow()
    {
        InitializeComponent();

        // The zoom and the double-click fit belong to the image area alone, so they are attached
        // to that control rather than overridden on the window.
        _viewport = this.FindControl<Border>("Viewport");
        if (_viewport is not null)
        {
            _gestures = ZoomPanGestures.Attach(this, _viewport);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _gestures?.Detach();
        base.OnClosed(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.CloseRequested -= OnCloseRequested;
        }

        _subscribed = DataContext as PreviewModalViewModel;

        if (_subscribed is not null)
        {
            _subscribed.CloseRequested += OnCloseRequested;
        }

        base.OnDataContextChanged(e);
    }

    /// <summary>
    /// P13 R10. Before this call the window opens with no focused element inside it: on a real,
    /// launched window that leaves keyboard routing to whatever the OS hands focus to next, which
    /// is nothing until a Previous or Next click puts focus on a button, and the arrows do nothing
    /// until then. This puts the keyboard on a known element, the image host, from the moment the
    /// window opens, so routing no longer depends on an OS focus decision the window does not
    /// control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The viewport rather than the window: focusing a Window is not the same as having a focused
    /// element inside it, and the pointer zoom and pan are already attached to that Border, so it
    /// is the control the keyboard should be on too. It is Focusable and not a tab stop, so the Tab
    /// order is unchanged.
    /// </para>
    /// <para>
    /// Measured under Avalonia 11.3.21's headless harness (Task 7 review): a key press with no
    /// focused element is still delivered to the window itself, so the harness cannot reproduce the
    /// defect this call fixes; a headless case can pin that focus lands on <c>Viewport</c>, but the
    /// launched-app symptom this call answers is proven only by the verification agent's item 6.
    /// </para>
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _viewport?.Focus(NavigationMethod.Unspecified);
    }

    /// <summary>
    /// Ruling Q19's mechanism: spec 11.5's two bare-key shortcuts, H and 0, and P13 R10's two
    /// aliases, Up and Down, are handled here behind a focus check rather than as window
    /// <c>KeyBinding</c>s, because the header panel carries a filter <c>TextBox</c> and a
    /// window-level binding on <c>H</c> would toggle the panel four times while the user typed
    /// "hydrogen". The <c>Ctrl</c>-modified shortcuts and the arrows are plain <c>KeyBinding</c>s
    /// in the markup, where they are safe.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is PreviewModalViewModel page && !IsTypingInATextBox())
        {
            switch (e.Key)
            {
                case Key.H:
                    page.ToggleHeaderPanelCommand.Execute(null);
                    e.Handled = true;
                    return;

                // Both rows of the keyboard: spec 11.5 says the 0 key, not the main-row 0 key.
                case Key.D0:
                case Key.NumPad0:
                    page.FitCommand.Execute(null);
                    e.Handled = true;
                    return;

                // P13 R10: Up and Down are aliases of Previous and Next. Handled here rather than
                // as KeyBindings for the reason the markup comment already gives for H and 0
                // (ruling Q19): the header panel carries a filter TextBox, a window KeyBinding does
                // not consult Handled and fires before the focused control's own handler, and Up
                // and Down are the caret keys in a text box. Left and Right stay as KeyBindings,
                // unchanged, because R10 names only their focus defect: that they also fire inside
                // the filter box is a pre-existing limit and is reported, not fixed here.
                case Key.Up:
                    page.PreviousCommand.Execute(null);
                    e.Handled = true;
                    return;

                case Key.Down:
                    page.NextCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }

    private bool IsTypingInATextBox()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
