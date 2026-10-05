using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views.Merge;

public partial class MergeHistoryView : UserControl
{
    public MergeHistoryView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
