using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GalactiLog.App.ViewModels.Mosaics;

// Spec 12.17's zoom, pan and Fit: view state, never saved.
public sealed partial class ArrangerViewModel
{
    private bool _fitted;
    private double _viewportWidth;
    private double _viewportHeight;

    /// <summary>0.1 to 3.0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText), nameof(ViewMatrix))]
    public partial double Zoom { get; private set; } = 1;

    /// <summary>The canvas's translation in viewport pixels from the viewport's top left corner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewMatrix))]
    public partial double OffsetX { get; private set; }

    /// <inheritdoc cref="OffsetX"/>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewMatrix))]
    public partial double OffsetY { get; private set; }

    /// <summary>Scale then translate: the view's <c>MatrixTransform</c>, after the rotation.</summary>
    public Matrix ViewMatrix => Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(OffsetX, OffsetY);

    /// <summary>"&lt;n&gt;%", the zoom times 100, rounded.</summary>
    public string ZoomText => Percent(Zoom * 100);

    /// <summary>The view reports the viewport's size; the first size with a tile fits.</summary>
    public void SetViewportSize(double width, double height)
    {
        var first = _viewportWidth <= 0 || _viewportHeight <= 0;
        (_viewportWidth, _viewportHeight) = (width, height);
        if (first)
        {
            FitOnce();
        }
    }

    /// <summary>The wheel: one step per event by the sign of <paramref name="steps"/> (the wheel's
    /// <c>Delta.Y</c>) about the pointer, in viewport pixels.</summary>
    public void ZoomAt(double steps, double pointerX, double pointerY)
    {
        if (steps != 0)
        {
            StepZoom(Math.Sign(steps) * ZoomStep, pointerX, pointerY);
        }
    }

    [RelayCommand]
    private void ZoomIn() => StepZoom(ZoomStep, _viewportWidth / 2, _viewportHeight / 2);

    [RelayCommand]
    private void ZoomOut() => StepZoom(-ZoomStep, _viewportWidth / 2, _viewportHeight / 2);

    /// <summary>A drag on empty canvas, in viewport pixels.</summary>
    public void Pan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    /// <summary>Scales the tiles' bounding box into the viewport less the padding, and centres it.
    /// Does nothing with no tile or no size.</summary>
    [RelayCommand]
    private void Fit()
    {
        if (Bounds() is not { } box || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            return;
        }

        var scale = Math.Clamp(
            Math.Min((_viewportWidth - 2 * FitPadding) / box.Width, (_viewportHeight - 2 * FitPadding) / box.Height),
            MinZoom, MaxZoom);
        Zoom = scale;
        OffsetX = _viewportWidth / 2 - box.Center.X * scale;
        OffsetY = _viewportHeight / 2 - box.Center.Y * scale;
        _fitted = true;
    }

    private void FitOnce()
    {
        if (!_fitted)
        {
            Fit();
        }
    }

    // PreviewModalViewModel.Zoom's rule in viewport pixels from the top left corner: a step the
    // clamp leaves unchanged moves nothing.
    private void StepZoom(double step, double pointerX, double pointerY)
    {
        var old = Zoom;
        var next = Math.Clamp(old + step, MinZoom, MaxZoom);
        if (next == old)
        {
            return;
        }

        var ratio = next / old;
        OffsetX = pointerX - (pointerX - OffsetX) * ratio;
        OffsetY = pointerY - (pointerY - OffsetY) * ratio;
        Zoom = next;
    }

    // Every tile's 250 by 160 box; the rotated footprint is not considered.
    private Rect? Bounds()
    {
        if (Tiles.Count == 0)
        {
            return null;
        }

        var left = Tiles.Min(tile => tile.X);
        var top = Tiles.Min(tile => tile.Y);
        return new Rect(left, top, Tiles.Max(tile => tile.X) + TileWidth - left, Tiles.Max(tile => tile.Y) + TileHeight - top);
    }
}
