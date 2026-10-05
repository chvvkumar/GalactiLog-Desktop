using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Preview;

namespace GalactiLog.App.Views.Preview;

/// <summary>
/// Spec 11.5's preview modal. The window owns closing, the pointer geometry, the focus placed on
/// open (P13 R10) and the four bare-key shortcuts, and holds no logic of its own: every shortcut
/// is a command on
/// <see cref="PreviewModalViewModel"/> (spec 18.3), and the view only supplies the geometry the
/// view-model cannot know.
/// </summary>
public partial class PreviewModalWindow : Window
{
    /// <summary>
    /// The per-notch <c>deltaY</c> a browser reports, which is the unit
    /// <see cref="PreviewModalViewModel.WheelZoomRate"/> was tuned against in
    /// <c>FilePreviewModal.tsx</c>. Avalonia reports wheel movement in notches, so a notch is
    /// scaled to the web's figure here and the view-model's contract stays the web's.
    /// </summary>
    private const double WebWheelNotchDelta = 100d;

    private readonly Border? _viewport;

    private PreviewModalViewModel? _subscribed;
    private bool _panning;
    private Point _panOrigin;

    public PreviewModalWindow()
    {
        InitializeComponent();

        // The zoom and the double-click fit belong to the image area alone, so they are attached
        // to that control rather than overridden on the window.
        _viewport = this.FindControl<Border>("Viewport");
        if (_viewport is not null)
        {
            _viewport.PointerWheelChanged += OnViewportWheel;
            _viewport.PointerPressed += OnViewportPressed;
        }
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

    /// <summary>Spec 11.5's pointer-centred wheel zoom. Attached to the viewport, not to the
    /// window: a wheel over the header panel or the navigation bar would otherwise zoom the image
    /// with an offset measured from a rectangle the pointer is not inside. The pointer's offset
    /// from the viewport centre is the geometry the view-model asks for, and Avalonia's
    /// <c>Delta.Y</c> is the opposite sign from the web's <c>e.deltaY</c>, so it is negated
    /// here.</summary>
    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PreviewModalViewModel page || _viewport is null)
        {
            return;
        }

        var position = e.GetPosition(_viewport);
        page.Zoom(
            -e.Delta.Y * WebWheelNotchDelta,
            position.X - (_viewport.Bounds.Width / 2d),
            position.Y - (_viewport.Bounds.Height / 2d));
        e.Handled = true;
    }

    /// <summary>Fit on double-click, and the start of a drag pan. Attached to the viewport for the
    /// same reason the wheel is: a double-click on the caption or the navigation bar must not
    /// reset the transform.</summary>
    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not PreviewModalViewModel page)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            // Spec 11.5's fit on double-click, the same command the 0 key runs.
            page.FitCommand.Execute(null);
            _panning = false;
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            _panning = true;
            _panOrigin = e.GetPosition(this);
        }
    }

    /// <summary>Drag pan, the web's <c>onPointerMove</c>. The move is tracked on the window rather
    /// than the viewport so a drag that wanders over the header panel keeps working, and it is
    /// gated on the left button still being down rather than on a pointer capture: a button
    /// released outside the window never delivers a release here, and without the gate the image
    /// would keep following the pointer afterwards. The view-model refuses the move unless the
    /// image is zoomed in, so that rule lives in one place.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_panning)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _panning = false;
            }
            else if (DataContext is PreviewModalViewModel page)
            {
                var position = e.GetPosition(this);
                page.Pan(position.X - _panOrigin.X, position.Y - _panOrigin.Y);
                _panOrigin = position;
            }
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _panning = false;
        base.OnPointerReleased(e);
    }

    private bool IsTypingInATextBox()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
