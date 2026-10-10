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
/// turned sideways. A drag or Left and Right set the width live between the stop and open
/// (<see cref="LedgerColumn"/>) and store it on release; a width at the stop stores collapsed and
/// keeps the last open width for the chevron, a width at open stores fully open.</summary>
public sealed class SidebarHandle : Border
{
    public const double Thickness = 28d;

    public const double KeyStep = 24d;

    private readonly Border _line = new() { Width = 1d, HorizontalAlignment = HorizontalAlignment.Center };

    private Control? _column;

    private NightsLedgerPart? _ledger;

    private Func<TargetPageState?>? _state;

    private Func<double> _room = () => double.PositiveInfinity;

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

    public void Attach(Control column, NightsLedgerPart ledger, string layoutKey, Func<TargetPageState?> state, Func<double> room)
    {
        _column = column;
        _ledger = ledger;
        _key = layoutKey;
        _state = state;
        _room = room;
        // The stop and open move with the type size and the custom columns; a gesture in flight
        // keeps its own width until release.
        ledger.ExtentsChanged += (_, _) =>
        {
            if (_dragStartX is null && _keyWidth is null)
            {
                Refresh();
            }
        };
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
            LedgerColumn.Apply(_column, _ledger, width, collapsed, _room());
        }
    }

    private void Commit(double width)
    {
        if (_ledger is { } ledger)
        {
            _state?.Invoke()?.SetLayout(_key, state => LedgerColumn.Committed(
                state, width, LedgerColumn.StopOf(ledger), LedgerColumn.OpenOf(ledger, _room())));
        }

        Refresh();
    }

    private double CurrentWidth() => _column?.Bounds.Width ?? 0d;

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
        // Held past either end, the next step the other way moves at once.
        if (double.IsFinite(_column.Width))
        {
            _keyWidth = _column.Width;
        }
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
