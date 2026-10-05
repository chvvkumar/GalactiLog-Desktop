using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views.Settings;

public partial class UnresolvedNamesView : UserControl
{
    public UnresolvedNamesView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
