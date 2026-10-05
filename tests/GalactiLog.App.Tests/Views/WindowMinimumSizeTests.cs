using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.Views;
using GalactiLog.Core.Settings;
using Xunit;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Section 12 shell paragraph, roadmap Phase 14C row 5's Verify clause: "the window refuses a
// smaller size in a headless case."
public class WindowMinimumSizeTests
{
    [AvaloniaFact]
    public void MainWindow_RefusesToShrinkBelowTwelveEightyBySevenTwenty()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Width = 800;
        window.Height = 500;
        Dispatcher.UIThread.RunJobs();

        // The measured bounds, not MinWidth's declared value: this is what catches a Window whose
        // MinWidth is set but whose layout still shrinks past it.
        Assert.True(window.Bounds.Width >= 1280d, $"width was {window.Bounds.Width}");
        Assert.True(window.Bounds.Height >= 720d, $"height was {window.Bounds.Height}");
    }

    [AvaloniaFact]
    public void AtTheMinimum_TheContentRegionAndTheStatusBarBothLayOutAboveZero()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var statusBar = new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel);
        var viewModel = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            statusBar,
            TabFactory.CreateSettingsPage(),
            () => StatisticsViewModelTestFactory.Create(),
            () => AnalysisViewModelTestFactory.Create(),
            () => ActivityViewModelTestFactory.Create());
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Width = 800;
        window.Height = 500;
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.GetControl<ContentControl>("ContentRegion").Bounds.Width > 0);
        Assert.True(window.GetControl<ContentControl>("ContentRegion").Bounds.Height > 0);
        Assert.True(window.GetControl<Border>("StatusBar").Bounds.Width > 0);
        Assert.True(window.GetControl<Border>("StatusBar").Bounds.Height > 0);

        viewModel.Dispose();
    }
}
