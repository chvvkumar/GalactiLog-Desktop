using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail.Layouts;

/// <summary>The horizontal drag handle between a layout's night lanes and its frames table: it caps
/// the lanes region, and the layout gives the session chart whatever the cap leaves under the
/// timeline, so the one handle trades chart height for frame rows. A drag or Up and Down is stored
/// on release only; a double click or no stored value leaves the automatic rule the layout's limits
/// give.</summary>
public sealed class LanesHandle : Border
{
    public const double Thickness = 12d;

    public const double KeyStep = 24d;

    /// <summary>What a layout reports about its geometry: the least the lanes keep, the most the frames
    /// leave them, and the height the automatic rule gives.</summary>
    public readonly record struct Limits(double Min, double Max, double Auto);

    private readonly Border _line = new() { Height = 1d, VerticalAlignment = VerticalAlignment.Center };

    private Control? _target;

    private Action<double>? _place;

    private Func<double?>? _stored;

    private Action<double?>? _store;

    private Func<Limits>? _limits;

    private double? _dragStartY;

    private double _dragStartHeight;

    private double? _dragHeight;

    private double? _keyHeight;

    public LanesHandle()
    {
        Height = Thickness;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        Focusable = true;
        Child = _line;
        AutomationProperties.SetName(this, "Resize the night lanes");
        Paint();
    }

    /// <summary>The greatest of the least and the smaller of the value and the most, so the least wins on a window too short for both.</summary>
    public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));

    /// <summary>The lanes attachment: the stored lanes height caps the region (MaxHeight, never Height), so it
    /// is never taller than its content and shrinks when a section closes and grows back when one opens (R20).</summary>
    public void Attach(ScrollViewer lanes, string layoutKey, Func<TargetPageState?> state, Func<Limits> limits)
        => Attach(lanes, height => lanes.MaxHeight = height, () => state()?.LanesHeight(layoutKey), height => state()?.SetLanesHeight(layoutKey, height), limits);

    /// <summary>The general attachment: the control the handle sizes, how a height is applied to it, where
    /// the stored value is read and written, and the limits that clamp it.</summary>
    public void Attach(Control target, Action<double> place, Func<double?> stored, Action<double?> store, Func<Limits> limits)
    {
        _target = target;
        _place = place;
        _stored = stored;
        _store = store;
        _limits = limits;
    }

    /// <summary>Places the target from the stored value, or from the automatic rule when there is none.
    /// A stored value is clamped for display and never rewritten.</summary>
    public void Refresh() => Place(_stored?.Invoke());

    private ListBox? _frameRows;

    /// <summary>The frames region's chrome (its height less the rows list) and the live row height, or
    /// null until a night with frames is laid out. Called on every layout of the region, so the rows
    /// list is searched for only when the cached one has left the tree.</summary>
    public (double Chrome, double Row)? MeasureFrames(Control framesRegion, Control framesPart)
    {
        if (_frameRows is null || !_frameRows.IsAttachedToVisualTree())
        {
            _frameRows = framesPart.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(list => list.Name == "FrameRows");
        }

        return _frameRows?.ContainerFromIndex(0) is { Bounds.Height: > 0d } row
            ? (framesRegion.Bounds.Height - _frameRows.Bounds.Height, row.Bounds.Height)
            : null;
    }

    private void Place(double? height)
    {
        if (_place is null || _limits is null)
        {
            return;
        }

        var limits = _limits();
        _place(height is { } value ? Clamp(value, limits.Min, limits.Max) : limits.Auto);
    }

    private void Commit(double? height)
    {
        _store?.Invoke(height is { } value && _limits?.Invoke() is { } limits ? Clamp(value, limits.Min, limits.Max) : null);
        Refresh();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_target is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        Focus();
        EndKeys();
        if (e.ClickCount == 2)
        {
            Commit(null);
            return;
        }

        _dragStartY = e.GetPosition(this.GetVisualRoot() as Visual).Y;
        _dragStartHeight = _target.Bounds.Height;
        _dragHeight = null;
        e.Pointer.Capture(this);
        Paint();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStartY is { } startY)
        {
            _dragHeight = _dragStartHeight + e.GetPosition(this.GetVisualRoot() as Visual).Y - startY;
            Place(_dragHeight);
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
        if (_dragStartY is null)
        {
            return;
        }

        var height = _dragHeight;
        _dragStartY = null;
        _dragHeight = null;
        Paint();
        if (height is not null)
        {
            Commit(height);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_target is null || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        e.Handled = true;

        // The drag owns the split until release, so a key during it leaves nothing pending.
        if (_dragStartY is not null)
        {
            return;
        }

        var next = (_keyHeight ?? _target.Bounds.Height) + (e.Key == Key.Up ? -KeyStep : KeyStep);
        _keyHeight = _limits is { } limits ? Clamp(next, limits().Min, limits().Max) : next;
        Place(_keyHeight);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            EndKeys();
        }
    }

    /// <summary>Commits any pending key height or drag in progress; a layout calls it before it lets go of its page.</summary>
    public void Flush()
    {
        EndKeys();
        EndDrag();
    }

    private void EndKeys()
    {
        if (_keyHeight is { } height)
        {
            _keyHeight = null;
            Commit(height);
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
        var emphasised = hover || IsFocused || _dragStartY is not null;
        _line.Bind(BackgroundProperty, this.GetResourceObservable(emphasised ? "ColorAccent" : "ColorBorderDefault"));
    }
}
