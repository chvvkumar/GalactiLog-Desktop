using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views;

public partial class StatusBarView : UserControl
{
    public StatusBarView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
