using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels.Mosaics;

namespace GalactiLog.App.Views.Mosaics;

/// <summary>
/// Spec 12.17's composite lightbox over <see cref="CompositeLightboxViewModel"/>. The window owns
/// closing and Download's save dialog seam; the image gestures are the preview modal's own
/// (<see cref="ZoomPanGestures"/>), and every rule is on the view model.
/// </summary>
public partial class CompositeLightboxWindow : Window
{
    private readonly Border? _viewport;

    private readonly ZoomPanGestures? _gestures;

    private CompositeLightboxViewModel? _subscribed;

    public CompositeLightboxWindow()
    {
        InitializeComponent();

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

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
