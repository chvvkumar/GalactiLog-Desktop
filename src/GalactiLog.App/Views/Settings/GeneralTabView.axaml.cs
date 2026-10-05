using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views.Settings;

/// <summary>
/// Design-spec 12.7's General tab and design-spec 12.11's five residency controls. No code behind
/// beyond loading the XAML: the tab has no picker and no dialog, and every control binds straight
/// to the view-model.
/// </summary>
public partial class GeneralTabView : UserControl
{
    public GeneralTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
