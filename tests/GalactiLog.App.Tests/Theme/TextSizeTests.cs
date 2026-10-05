using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Converters;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.Views;
using GalactiLog.Core.Settings;
using Xunit;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Theme;

// Phase 9 FIXER item 2 and design-spec 14.4: "Root font size is user-selectable: Small 14,
// Medium 16, Large 18, Extra large 20, set as MainWindow.FontSize so every relative size
// scales." The four Scales.axaml FontSize* keys stay ratios and nothing binds one to a FontSize;
// FontSizeTokenTest (tests/GalactiLog.App.Tests/Views/FontSizeTokenTest.cs) is the build-time
// guard for that and is unchanged by this task, so it is referenced here rather than duplicated.
public class TextSizeTests : IDisposable
{
    // Each Statistics page holds nine ChartTheme.Changed subscriptions, so the ones these tests
    // build are disposed rather than left to accumulate across the run (Task 3 review item M12).
    private readonly List<StatisticsViewModel> _statistics = [];
    private readonly List<MainWindowViewModel> _shells = [];

    public void Dispose()
    {
        foreach (var shell in _shells)
        {
            shell.Dispose();
        }

        foreach (var page in _statistics)
        {
            page.Dispose();
        }

        _shells.Clear();
        _statistics.Clear();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("small", 14d)]
    [InlineData("medium", 16d)]
    [InlineData("large", 18d)]
    [InlineData("x-large", 20d)]
    public void RootFontSize_IsTheSpecsFourSizes(string textSize, double expected)
        => Assert.Equal(expected, MainWindowViewModel.ResolveRootFontSize(textSize));

    [Fact]
    public void RootFontSize_UnrecognizedValue_FallsBackToTheDocumentedDefault()
    {
        // A hand-edited settings document can hold anything. The fallback is the documented
        // default of 14, the fresh profile's small, not zero, exactly as ResolveContentMaxWidth
        // falls back.
        Assert.Equal(14d, MainWindowViewModel.ResolveRootFontSize(""));
        Assert.Equal(14d, MainWindowViewModel.ResolveRootFontSize("enormous"));
    }

    [AvaloniaFact]
    public void MainWindow_BindsFontSizeToTheRootFontSize()
    {
        var shell = NewShell(new GeneralSettings { TextSize = "small" });
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(14d, shell.RootFontSize);
        Assert.Equal(14d, window.FontSize);

        // Every control inherits it, which is the whole point of putting it on the Window rather
        // than on each control.
        var rail = window.GetControl<ListBox>("NavigationRail");
        Assert.Equal(14d, rail.FontSize);

        window.Close();
    }

    [AvaloniaFact]
    public void RootFontSize_ChangesLiveOnAGeneralSave()
    {
        var shell = NewShell(new GeneralSettings { TextSize = "large", ContentWidth = "extra-wide" });
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(18d, window.FontSize);
        Assert.Equal(double.PositiveInfinity, shell.ContentMaxWidth);

        // What AppHost's GeneralChanged subscription does, posted to the UI thread. Both keys are
        // written by the Settings Display tab while this shell is alive.
        shell.ApplyGeneral(new GeneralSettings { TextSize = "x-large", ContentWidth = "normal" });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(20d, shell.RootFontSize);
        Assert.Equal(20d, window.FontSize);
        Assert.Equal(1200d, shell.ContentMaxWidth);

        window.Close();
    }

    [Fact]
    public void TheDisplayTabsTextSizeOptions_AreTheSameMappingTheWindowUses()
    {
        // One mapping, not two: a picker that could disagree with the window it drives is what
        // sharing the definition avoids.
        foreach (var option in DisplayTabViewModel.TextSizes)
        {
            Assert.Equal(MainWindowViewModel.ResolveRootFontSize(option.Id), option.Pixels);
        }

        Assert.Equal(
            ["small", "medium", "large", "x-large"],
            DisplayTabViewModel.TextSizes.Select(option => option.Id));
    }

    [Fact]
    public void MultiplyConverter_ProjectsARatioOntoTheInheritedSize()
    {
        // Questions.md Q24: this is how a view reaches design-spec 14.4's tiers now that the root
        // size lives on the Window. Scales.axaml's FontSizeLabel is 0.786 and FontSizeMicro 0.500.
        var converter = new MultiplyConverter();

        Assert.Equal(18d * 0.786d, converter.Convert(18d, typeof(double), 0.786d, CultureInfo.InvariantCulture));
        Assert.Equal(14d * 0.5d, converter.Convert(14d, typeof(double), "0.500", CultureInfo.InvariantCulture));

        // A mistyped parameter leaves the inherited size in place rather than collapsing the text.
        Assert.Same(
            Avalonia.Data.BindingOperations.DoNothing,
            converter.Convert(18d, typeof(double), "caption", CultureInfo.InvariantCulture));
        Assert.Same(
            Avalonia.Data.BindingOperations.DoNothing,
            converter.Convert(null, typeof(double), 0.786d, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NoAxamlBindsAFontSizeRatioKeyToFontSize()
    {
        // The rule's enforcement lives in FontSizeTokenTest, which scans every .axaml under src/
        // and is unchanged by this task. Called here rather than re-implemented, so there is one
        // definition of the rule and this file records that the Phase 9 work kept it green.
        new Views.FontSizeTokenTest().NoAxamlBindsFontSizeToAFontSizeRatioKey();
    }

    private MainWindowViewModel NewShell(GeneralSettings general)
    {
        var statistics = StatisticsViewModelTestFactory.Create();
        _statistics.Add(statistics);

        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var shell = new MainWindowViewModel(
            general,
            DashboardViewModelTestFactory.Create(),
            new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel),
            TabFactory.CreateSettingsPage(),
            () => statistics,
            // Phase 17 Task 4: the sixth rail destination. Never invoked here, like the Activity
            // factory below, so no page is built.
            () => AnalysisViewModelTestFactory.Create(),
            // FIXER LIST F21: the Activity destination is a factory now. Nothing in this file
            // navigates there, so it is never invoked and no page is built.
            () => ActivityViewModelTestFactory.Create());

        _shells.Add(shell);
        return shell;
    }
}
