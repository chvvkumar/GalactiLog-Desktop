using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views.Settings;

/// <summary>
/// Design-spec 12.7's About tab. No logic: every value is bound and every action is a command on
/// <see cref="ViewModels.Settings.AboutTabViewModel"/>, which launches nothing itself and reaches
/// the shell through <c>ShellIntegration</c>.
/// </summary>
public partial class AboutTabView : UserControl
{
    public AboutTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
