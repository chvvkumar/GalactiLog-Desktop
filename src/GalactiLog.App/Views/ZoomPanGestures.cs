using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GalactiLog.App.ViewModels.Preview;

namespace GalactiLog.App.Views;

/// <summary>
/// Spec 11.5's image gestures, shared by the preview modal and the composite lightbox (spec
/// 12.17): the wheel zooms at the pointer, a double-click fits, and a left drag pans. Every rule
/// is on the window's <see cref="IZoomPanSurface"/> data context; this only supplies the geometry
/// the view model cannot know.
/// </summary>
internal sealed class ZoomPanGestures
{
    /// <summary>
    /// The per-notch <c>deltaY</c> a browser reports, which is the unit
    /// <see cref="PreviewModalViewModel.WheelZoomRate"/> was tuned against in
    /// <c>FilePreviewModal.tsx</c>. Avalonia reports wheel movement in notches, so a notch is
    /// scaled to the web's figure here and the view model's contract stays the web's.
    /// </summary>
    public const double WebWheelNotchDelta = 100d;

    private readonly Window _window;
    private readonly Border _viewport;
    private bool _panning;
    private Point _panOrigin;

    private ZoomPanGestures(Window window, Border viewport)
    {
        _window = window;
        _viewport = viewport;
    }

    /// <summary>Wires the gestures. The zoom and the double-click fit belong to the viewport
    /// alone; the drag is tracked on the window so a drag that wanders off the image keeps
    /// working.</summary>
    public static ZoomPanGestures Attach(Window window, Border viewport)
    {
        var gestures = new ZoomPanGestures(window, viewport);
        viewport.PointerWheelChanged += gestures.OnWheel;
        viewport.PointerPressed += gestures.OnPressed;
        window.PointerMoved += gestures.OnMoved;
        window.PointerReleased += gestures.OnReleased;
        return gestures;
    }

    /// <summary>Unwires them, on the window's close.</summary>
    public void Detach()
    {
        _viewport.PointerWheelChanged -= OnWheel;
        _viewport.PointerPressed -= OnPressed;
        _window.PointerMoved -= OnMoved;
        _window.PointerReleased -= OnReleased;
        _panning = false;
    }

    private IZoomPanSurface? Surface => _window.DataContext as IZoomPanSurface;

    // The pointer's offset from the viewport centre is the geometry the view model asks for, and
    // Avalonia's Delta.Y is the opposite sign from the web's e.deltaY, so it is negated here.
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Surface is not { } surface)
        {
            return;
        }

        var position = e.GetPosition(_viewport);
        surface.Zoom(
            -e.Delta.Y * WebWheelNotchDelta,
            position.X - (_viewport.Bounds.Width / 2d),
            position.Y - (_viewport.Bounds.Height / 2d));
        e.Handled = true;
    }

    // Fit on double-click; otherwise a left press starts a drag pan.
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Surface is not { } surface)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            surface.ResetFit();
            _panning = false;
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            _panning = true;
            _panOrigin = e.GetPosition(_window);
        }
    }

    // Gated on the left button still being down rather than on a pointer capture: a button
    // released outside the window never delivers a release here, and without the gate the image
    // would keep following the pointer afterwards. The view model refuses the move unless the
    // image is zoomed in, so that rule lives in one place.
    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed)
        {
            _panning = false;
        }
        else if (Surface is { } surface)
        {
            var position = e.GetPosition(_window);
            surface.Pan(position.X - _panOrigin.X, position.Y - _panOrigin.Y);
            _panOrigin = position;
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e) => _panning = false;
}
