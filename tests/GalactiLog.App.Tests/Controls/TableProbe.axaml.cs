using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Tests.Controls;

public partial class TableProbe : UserControl
{
    public TableProbe()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ItemsControl>("Rows")!.ItemsSource = new[] { "L", "Hydrogen alpha" };
    }
}
