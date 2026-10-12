using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GalactiLog.App.ViewModels;

namespace GalactiLog.App.Views;

public partial class StatusBarView : UserControl
{
    public StatusBarView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Opening the jobs flyout is what "seen" means for the failed count beside the monitor, and
    // the moment the listed times are re-read against today.
    private void OnJobsFlyoutOpened(object? sender, EventArgs e)
    {
        if (DataContext is StatusBarViewModel status)
        {
            status.MarkJobsSeen();
            status.Jobs?.RefreshTimes();
        }
    }
}
