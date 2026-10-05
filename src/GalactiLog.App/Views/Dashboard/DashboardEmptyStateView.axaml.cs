using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GalactiLog.App.Views.Dashboard;

public partial class DashboardEmptyStateView : UserControl
{
    public DashboardEmptyStateView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
