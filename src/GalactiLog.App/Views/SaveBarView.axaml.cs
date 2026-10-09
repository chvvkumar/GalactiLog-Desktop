using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views;

public partial class SaveBarView : UserControl
{
    public SaveBarView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
