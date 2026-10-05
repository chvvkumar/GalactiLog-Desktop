using Avalonia.Headless.XUnit;
using GalactiLog.App.Views;
using Xunit;

namespace GalactiLog.App.Tests;

public class MainWindowSmokeTest
{
    // Deliberately no DataContext. App.axaml.cs assigns
    // Services?.GetRequiredService<MainWindowViewModel>(), which is null under the headless
    // harness, so the shell must parse and lay out with an unbound DataContext; this is the
    // only test that asserts it. The bound case, with every rail item and the content region
    // exercised, is Views/ShellNavigationTests.
    [AvaloniaFact]
    public void MainWindow_Constructs_AndProducesNonZeroLayout()
    {
        var window = new MainWindow();
        window.Show();

        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);
    }
}
