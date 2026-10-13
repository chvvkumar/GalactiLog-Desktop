using Avalonia.Controls;
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

    // The content region swaps pages in one frame. Avalonia's TransitioningContentControl defaults
    // PageTransition to a 125 ms CrossFade when none is declared, which fades the outgoing page's
    // rows to grey on every navigation and reads as the refetch dim, so the region has to name
    // null explicitly rather than merely omit the property.
    [AvaloniaFact]
    public void ContentRegion_HasNoPageTransition()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Null(window.GetControl<TransitioningContentControl>("ContentRegion").PageTransition);
        Assert.Null(window.GetControl<TransitioningContentControl>("DetailRegion").PageTransition);
    }
}
