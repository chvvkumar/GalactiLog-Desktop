using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Mosaics;

namespace GalactiLog.App.Views.Mosaics;

/// <summary>
/// Spec 12.17's composite lightbox over <see cref="CompositeLightboxViewModel"/>. The window owns
/// closing, the pointer geometry and Download's save dialog seam, the preview modal's split
/// (spec 11.5): every rule is on the view model.
/// </summary>
public partial class CompositeLightboxWindow : Window
{
    // The browser's per-notch deltaY, the unit PreviewModalViewModel.WheelZoomRate was tuned
    // against; the preview window scales a notch the same way.
    private const double WebWheelNotchDelta = 100d;

    private readonly Border? _viewport;

    private CompositeLightboxViewModel? _subscribed;
    private bool _panning;
    private Point _panOrigin;

    public CompositeLightboxWindow()
    {
        InitializeComponent();

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
            _subscribed.ExportDestinationPicker = null;
        }

        _subscribed = DataContext as CompositeLightboxViewModel;

        if (_subscribed is not null)
        {
            _subscribed.CloseRequested += OnCloseRequested;
            _subscribed.ExportDestinationPicker = PickDestinationAsync;
        }

        base.OnDataContextChanged(e);
    }

    // The keyboard on the image host from the moment the window opens (the preview's P13 R10).
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _viewport?.Focus(NavigationMethod.Unspecified);
    }

    private Task<string?> PickDestinationAsync(string suggestedFileName)
        => SaveDialogStart.PickPathAsync(this, "Download composite", suggestedFileName, "jpg", "JPEG");

    // Zoom at the pointer, measured from the viewport centre; Avalonia's Delta.Y is the opposite
    // sign from the web's deltaY.
    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not CompositeLightboxViewModel page || _viewport is null)
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

    // Fit on double-click; otherwise a left press starts a drag pan.
    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not CompositeLightboxViewModel page)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            page.ResetFit();
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

    // Tracked on the window and gated on the left button still being down, as the preview does.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_panning)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _panning = false;
            }
            else if (DataContext is CompositeLightboxViewModel page)
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

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
