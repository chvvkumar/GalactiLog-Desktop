using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;

namespace GalactiLog.App.Views.TargetDetail.Layouts;

/// <summary>The handle on the nights sidebar's right edge, the shape of <see cref="LanesHandle"/>
/// turned sideways. A drag or Left and Right set the sidebar's width live and store it on release;
/// the form follows the width (<see cref="LedgerColumn"/>), a width under the compact floor stores
/// collapsed and keeps the last open width for the chevron to restore.</summary>
public sealed class SidebarHandle : Border
{
    public const double Thickness = 28d;

    public const double KeyStep = 24d;

    private readonly Border _line = new() { Width = 1d, HorizontalAlignment = HorizontalAlignment.Center };

    private Control? _column;

    private NightsLedgerPart? _ledger;

    private Func<TargetPageState?>? _state;

    private string _key = "";

    private double? _dragStartX;

    private double _dragStartWidth;

    private double? _dragWidth;

    private double? _keyWidth;

    public SidebarHandle()
    {
        Width = Thickness;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Focusable = true;
        Child = _line;
        AutomationProperties.SetName(this, "Resize the nights sidebar");
        Paint();
    }

    public void Attach(Control column, NightsLedgerPart ledger, string layoutKey, Func<TargetPageState?> state)
    {
        _column = column;
        _ledger = ledger;
        _key = layoutKey;
        _state = state;
    }

    /// <summary>Places the sidebar from the stored width and collapsed flag.</summary>
    public void Refresh()
    {
        if (_state?.Invoke()?.Layout(_key) is { } stored)
        {
            Place(stored.SidebarWidth, stored.SidebarCollapsed);
        }
    }

    /// <summary>The chevron's toggle: collapses, or expands back to the last open width.</summary>
    public void ToggleCollapsed()
    {
        _state?.Invoke()?.SetLayout(_key, state => state with { SidebarCollapsed = !state.SidebarCollapsed });
        Refresh();
    }

    private void Place(double? width, bool collapsed)
    {
        if (_column is not null && _ledger is not null)
        {
            LedgerColumn.Apply(_column, _ledger, width, collapsed);
        }
    }

    // A width under the compact floor collapses and leaves the open width as it was.
    private void Commit(double width)
    {
        _state?.Invoke()?.SetLayout(_key, state => width < LedgerColumn.CompactWidth
            ? state with { SidebarCollapsed = true }
            : state with { SidebarWidth = width, SidebarCollapsed = false });
        Refresh();
    }

    // A drag or a key out of the collapsed form starts at the compact floor, so the first step
    // rightwards is the compact form and the sidebar does not stay shut while the pointer crosses
    // the hidden columns.
    private double CurrentWidth()
        => _ledger is { IsCollapsed: true } || _column is null ? LedgerColumn.CompactWidth : _column.Bounds.Width;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_column is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        Focus();
        EndKeys();
        _dragStartX = e.GetPosition(this.GetVisualRoot() as Visual).X;
        _dragStartWidth = CurrentWidth();
        _dragWidth = null;
        e.Pointer.Capture(this);
        Paint();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStartX is { } startX)
        {
            _dragWidth = _dragStartWidth + e.GetPosition(this.GetVisualRoot() as Visual).X - startX;
            Place(_dragWidth, collapsed: false);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragStartX is null)
        {
            return;
        }

        var width = _dragWidth;
        _dragStartX = null;
        _dragWidth = null;
        Paint();
        if (width is { } value)
        {
            Commit(value);
        }
        else
        {
            Refresh();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_column is null || e.Key is not (Key.Left or Key.Right))
        {
            return;
        }

        e.Handled = true;
        if (_dragStartX is not null)
        {
            return;
        }

        _keyWidth = (_keyWidth ?? CurrentWidth()) + (e.Key == Key.Left ? -KeyStep : KeyStep);
        Place(_keyWidth, collapsed: false);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            EndKeys();
        }
    }

    /// <summary>Commits any pending key width or drag in progress; the layout calls it before it lets go of its page.</summary>
    public void Flush()
    {
        EndKeys();
        EndDrag();
    }

    private void EndKeys()
    {
        if (_keyWidth is { } width)
        {
            _keyWidth = null;
            Commit(width);
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        Paint(hover: true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Paint();
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        Paint();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        EndKeys();
        Paint();
    }

    private void Paint(bool hover = false)
    {
        var emphasised = hover || IsFocused || _dragStartX is not null;
        _line.Bind(BackgroundProperty, this.GetResourceObservable(emphasised ? "ColorAccent" : "ColorBorderDefault"));
    }
}
