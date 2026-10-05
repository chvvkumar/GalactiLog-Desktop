using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views.Settings;

/// <summary>
/// Design-spec 12.7's Display tab. No code behind beyond loading the XAML: the tab has no picker
/// and no dialog, and every control binds straight to the view-model.
/// </summary>
public partial class DisplayTabView : UserControl
{
    public DisplayTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
