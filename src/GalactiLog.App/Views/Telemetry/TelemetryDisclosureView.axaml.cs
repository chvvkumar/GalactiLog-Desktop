using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views.Telemetry;

/// <summary>The telemetry report preview and public statistics link. No logic of its own.</summary>
public partial class TelemetryDisclosureView : UserControl
{
    public TelemetryDisclosureView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
