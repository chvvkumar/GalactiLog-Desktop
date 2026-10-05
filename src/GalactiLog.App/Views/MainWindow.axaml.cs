using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;

namespace GalactiLog.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Spec 12.11's residency owner. Assigned by <see cref="WindowResidencyService.Attach"/>; null
    /// under the headless test harness and in any surface that builds this window without a host,
    /// in which case the window closes and minimizes normally.
    /// </summary>
    internal WindowResidencyService? Residency { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The guard is here and not only on the caller (TRACKING item 13's reasoning applied to an
        // override): a close arrives from the title bar, from Alt+F4, and from the lifetime's own
        // shutdown, and only the first two may be converted into a hide or into the one shutdown.
        // The service answers which of the three this is; this override only obeys it.
        //
        // Review minor finding 1: the reason is mapped here rather than inside the service, which
        // keeps WindowResidencyService free of Avalonia types outside Attach. An OS shutdown or a
        // log-off can reach this override without ShutdownRequested having been raised first, so
        // the flag alone is not enough to recognize a close that must never become a hide.
        var platformIsShuttingDown =
            e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown;

        if (Residency is { } residency && residency.HandleUserClose(platformIsShuttingDown))
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Spec 12.11 behaviour 6. The service puts the state back to Normal as part of the hide,
        // so the assignment it makes re-enters here with Normal and stops at this test.
        //
        // Review minor finding 3, recorded rather than changed: the hide happens from inside
        // Avalonia's own WindowState change notification, and the state is then written back on a
        // window that is already hidden. The headless platform stores that write, so the manual
        // bar on an installed build is what confirms the Win32 backend does the same: minimize
        // with minimize-to-tray on must leave no taskbar button, and Open must restore the window
        // at its previous size and not maximized.
        if (change.Property == WindowStateProperty
            && change.GetNewValue<WindowState>() == WindowState.Minimized)
        {
            Residency?.HandleMinimized();
        }
    }

    // A click on the rail row that is already selected never changes SelectedItem, so the
    // two-way binding never reaches the Selected setter and its detail-closing rule (ruling Q9).
    // Any rail tap is a request to see that destination; when the selection did change, the
    // setter has already closed the detail and this is a no-op.
    private void OnNavigationRailTapped(object? sender, TappedEventArgs e)
        => (DataContext as MainWindowViewModel)?.CloseDetailCommand.Execute(null);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
