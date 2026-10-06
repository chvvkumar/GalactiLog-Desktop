using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using GalactiLog.App.ViewModels.Mosaics;

namespace GalactiLog.App.Views.Mosaics;

/// <summary>
/// Spec 12.17's arranger, bound to <see cref="ArrangerViewModel"/>. The code-behind holds pointer
/// bookkeeping only: which tile a press took, whether the pointer moved before the release, the
/// pan's last point and the capture. Every rule (selection, the grab offset, the zoom step, the
/// save) lives in the view-model. Positions are read relative to the tile list, the viewport's
/// content, so they are viewport pixels from its top left corner; the tile's canvas position is
/// read relative to the Canvas, which undoes the zoom, the pan and the global rotation.
/// </summary>
public partial class ArrangerView : UserControl
{
    /// <summary>The image's <c>ScaleX</c>: -1 while the tile is flipped.</summary>
    public static readonly IValueConverter FlipScale = new FuncValueConverter<bool, double>(flip => flip ? -1 : 1);

    /// <summary>An opacity of 0 while the bound flag is set: a status caption keeps its place.</summary>
    public static readonly IValueConverter HiddenWhen = new FuncValueConverter<bool, double>(hidden => hidden ? 0 : 1);

    /// <summary>An opacity of 1 while the bound value is set (the save failure sentence).</summary>
    public static readonly IValueConverter ShownWhenSet = new FuncValueConverter<object?, double>(value => value is null ? 0 : 1);

    /// <summary>Leaves a faded caption out of the automation tree while the bound flag is set.</summary>
    public static readonly IValueConverter RawWhen = new FuncValueConverter<bool, AccessibilityView>(
        hidden => hidden ? AccessibilityView.Raw : AccessibilityView.Default);

    /// <summary>Leaves the save failure sentence out of the automation tree while there is none.</summary>
    public static readonly IValueConverter RawWhenUnset = new FuncValueConverter<object?, AccessibilityView>(
        value => value is null ? AccessibilityView.Raw : AccessibilityView.Default);

    private TileViewModel? _pressed;
    private bool _wasSelected;
    private bool _moved;
    private bool _panning;
    private Point _last;

    public ArrangerView()
    {
        InitializeComponent();
        TileItems.SizeChanged += (_, _) => ReportSize();
        TileItems.ContainerPrepared += OnContainerPrepared;
    }

    // The read-only preview carries IsHitTestVisible false from its host, so no pointer event
    // arrives; this is the second guard.
    private ArrangerViewModel? Arranger => DataContext is ArrangerViewModel { IsReadOnly: false } arranger ? arranger : null;

    private Visual TilePanel => TileItems.ItemsPanelRoot ?? (Visual)TileItems;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ReportSize();
    }

    // The container, not the template root, is the Canvas's child: it carries the tile's place
    // and stacking order, bound to the tile it shows.
    private static void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        e.Container.Bind(Avalonia.Controls.Canvas.LeftProperty, new Binding(nameof(TileViewModel.X)));
        e.Container.Bind(Avalonia.Controls.Canvas.TopProperty, new Binding(nameof(TileViewModel.Y)));
        e.Container.Bind(ZIndexProperty, new Binding(nameof(TileViewModel.ZIndex)));
    }

    private void ReportSize()
    {
        var size = TileItems.Bounds.Size;
        if (DataContext is ArrangerViewModel arranger && size.Width > 0 && size.Height > 0)
        {
            arranger.SetViewportSize(size.Width, size.Height);
        }
    }

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Arranger is not { } arranger)
        {
            return;
        }

        var point = e.GetCurrentPoint(TileItems);
        var tile = (e.Source as StyledElement)?.DataContext as TileViewModel;
        if (point.Properties.IsRightButtonPressed)
        {
            // Selects; the tile's ContextMenu opens on the release. No drag starts. A right press
            // while a left press drags or pans is ignored, so it never moves the selection.
            if (tile is not null && _pressed is null && !_panning)
            {
                arranger.Select(tile);
            }

            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _last = point.Position;
        _moved = false;
        if (tile is not null)
        {
            _pressed = tile;
            _wasSelected = tile.IsSelected;
            arranger.Select(tile);
            var canvas = e.GetPosition(TilePanel);
            arranger.BeginDrag(tile, canvas.X, canvas.Y);
        }
        else
        {
            // A drag here pans and keeps the selection; only a click deselects (on release).
            _panning = true;
        }

        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportMoved(object? sender, PointerEventArgs e)
    {
        if (Arranger is not { } arranger || (_pressed is null && !_panning))
        {
            return;
        }

        var position = e.GetPosition(TileItems);
        if (position == _last)
        {
            return;
        }

        if (_pressed is { } tile)
        {
            var canvas = e.GetPosition(TilePanel);
            arranger.Drag(tile, canvas.X, canvas.Y);
            _moved = true;
        }
        else
        {
            arranger.Pan(position.X - _last.X, position.Y - _last.Y);
            _moved = true;
        }

        _last = position;
    }

    private void OnViewportReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only the left button's own release ends the gesture the left press started. The kind
        // of update names the button released; InitialPressMouseButton names the sequence's first
        // press, which is still Left when the right button is released during a left drag.
        if (e.GetCurrentPoint(TileItems).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased
            || (_pressed is null && !_panning))
        {
            return;
        }

        if (Arranger is { } arranger)
        {
            if (_pressed is { } tile)
            {
                // A click with no movement on a tile that was already selected deselects it.
                arranger.EndDrag(tile);
                if (!_moved && _wasSelected)
                {
                    arranger.ToggleSelect(tile);
                }
            }
            else if (!_moved)
            {
                // A click on empty canvas deselects; a pan does not.
                arranger.Select(null);
            }
        }

        Reset();
        e.Pointer.Capture(null);
    }

    private void OnViewportCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pressed is { } tile)
        {
            Arranger?.EndDrag(tile);
        }

        Reset();
    }

    private void Reset()
    {
        _pressed = null;
        _panning = false;
        _moved = false;
    }

    // One step per event by the sign of Delta.Y alone; a zero delta does nothing.
    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Arranger is not { } arranger || e.Delta.Y == 0)
        {
            return;
        }

        var position = e.GetPosition(TileItems);
        arranger.ZoomAt(e.Delta.Y, position.X, position.Y);
        e.Handled = true;
    }

    private void OnRotateItem(object? sender, RoutedEventArgs e)
    {
        if (Arranger is { } arranger && (sender as StyledElement)?.DataContext is TileViewModel tile)
        {
            arranger.Select(tile);
            arranger.Rotate(tile);
        }
    }

    private void OnFlipItem(object? sender, RoutedEventArgs e)
    {
        if (Arranger is { } arranger && (sender as StyledElement)?.DataContext is TileViewModel tile)
        {
            arranger.Select(tile);
            arranger.Flip(tile);
        }
    }
}
