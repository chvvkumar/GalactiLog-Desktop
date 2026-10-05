using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views;

public partial class PlaceholderPageView : UserControl
{
    public PlaceholderPageView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
