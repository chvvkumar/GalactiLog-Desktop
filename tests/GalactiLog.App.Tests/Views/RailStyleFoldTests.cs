using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Settings;
using Xunit;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Carried fixer-list item 32: MainWindow.axaml's navigation rail and SettingsView.axaml's tab
// strip declared byte-identical ListBoxItem style blocks, differing only in one padding value.
// Theme/Controls.axaml now carries six ListBox.rail selectors once; this file is the proof that
// the fold changed the source, not the render (the whole point of a fold, per the brief).
public class RailStyleFoldTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel) ShowShell()
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
        return (window, viewModel);
    }

    private static Window ShowSettingsView(out SettingsView view)
    {
        view = new SettingsView { DataContext = TabFactory.CreateSettingsPage() };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ListBoxItem FirstItemOf(ListBox list)
        => list.GetVisualDescendants().OfType<ListBoxItem>().First();

    [AvaloniaFact]
    public void TheNavigationRail_AndTheSettingsTabStrip_ShareOneStyleSource()
    {
        var (shellWindow, _) = ShowShell();
        var rail = shellWindow.GetControl<ListBox>("NavigationRail");
        Assert.Contains("rail", rail.Classes);

        var settingsWindow = ShowSettingsView(out var settingsView);
        var strip = settingsView.GetControl<ListBox>("SettingsTabStrip");
        Assert.Contains("rail", strip.Classes);
        Assert.Contains("compact", strip.Classes);

        shellWindow.Close();
        settingsWindow.Close();
    }

    [AvaloniaFact]
    public void TheNavigationRailItem_KeepsItsSixteenByTenPadding()
    {
        var (window, _) = ShowShell();
        var rail = window.GetControl<ListBox>("NavigationRail");

        Assert.Equal(new Thickness(16, 10), FirstItemOf(rail).Padding);

        window.Close();
    }

    [AvaloniaFact]
    public void TheSettingsTabStripItem_KeepsItsFourteenByEightPadding()
    {
        var window = ShowSettingsView(out var view);
        var strip = view.GetControl<ListBox>("SettingsTabStrip");

        Assert.Equal(new Thickness(14, 8), FirstItemOf(strip).Padding);

        window.Close();
    }

    [AvaloniaFact]
    public void TheSelectedItem_TakesTheAccentInk_InBothRails()
    {
        Assert.True(Application.Current!.TryFindResource("ColorAccent", out var resource));
        var accent = Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color;

        var (shellWindow, viewModel) = ShowShell();
        var rail = shellWindow.GetControl<ListBox>("NavigationRail");
        var railSelected = FirstItemOf(rail).GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(railSelected.Foreground).Color);

        var settingsWindow = ShowSettingsView(out var settingsView);
        var strip = settingsView.GetControl<ListBox>("SettingsTabStrip");
        var stripSelected = FirstItemOf(strip).GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(stripSelected.Foreground).Color);

        shellWindow.Close();
        settingsWindow.Close();
        viewModel.Dispose();
    }

    // A collapsed rail item draws an icon, not the title's initial. A
    // template that keeps the initial's TextBlock, or whose selected Path misses the accent ink,
    // fails here.
    [AvaloniaFact]
    public void TheCollapsedRailItem_PresentsAPath_NotATextBlock_InTheAccentInkWhenSelected()
    {
        Assert.True(Application.Current!.TryFindResource("ColorAccent", out var resource));
        var accent = Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color;

        var (window, viewModel) = ShowShell();
        viewModel.IsNavRailCollapsed = true;
        Dispatcher.UIThread.RunJobs();

        var item = FirstItemOf(window.GetControl<ListBox>("NavigationRail"));
        var icon = Assert.Single(item.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), p => p.IsEffectivelyVisible);
        Assert.NotNull(icon.Data);
        Assert.DoesNotContain(item.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible);
        Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(icon.Stroke).Color);

        window.Close();
        viewModel.Dispose();
    }

    // The hover label covers the whole 48 px entry, so the tip sits on the ListBoxItem itself, and
    // only while the rail is collapsed; an expanded item already shows its title. A tip left on
    // the glyph or the Panel, or one that stays while expanded, fails here.
    [AvaloniaFact]
    public void TheRailItem_CarriesTheTitleAsItsTip_OnlyWhileCollapsed()
    {
        var (window, viewModel) = ShowShell();
        viewModel.IsNavRailCollapsed = true;
        Dispatcher.UIThread.RunJobs();

        var rail = window.GetControl<ListBox>("NavigationRail");
        var item = FirstItemOf(rail);
        Assert.Equal(viewModel.Items[0].Title, ToolTip.GetTip(item));
        Assert.DoesNotContain(item.GetVisualDescendants().OfType<Control>(), c => ToolTip.GetTip(c) is not null);

        viewModel.IsNavRailCollapsed = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(ToolTip.GetTip(item));

        window.Close();
        viewModel.Dispose();
    }

    // The other half of carried item 32: a fold that also restyles a page is two changes reported
    // as one, so neither surface may keep a ListBoxItem style of its own once the six selectors
    // in Theme/Controls.axaml carry the shape.
    [Fact]
    public void NeitherViewDeclaresAListBoxItemStyleOfItsOwn()
    {
        var root = SourceScan.SrcRoot();
        var mainWindow = SourceScan.StripComments(
            File.ReadAllText(Path.Combine(root, "GalactiLog.App", "Views", "MainWindow.axaml")));
        var settingsView = SourceScan.StripComments(
            File.ReadAllText(Path.Combine(root, "GalactiLog.App", "Views", "Settings", "SettingsView.axaml")));

        Assert.DoesNotContain("<Style Selector=\"ListBoxItem", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<ListBox.Styles>", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<Style Selector=\"ListBoxItem", settingsView, StringComparison.Ordinal);
        Assert.DoesNotContain("<ListBox.Styles>", settingsView, StringComparison.Ordinal);
    }
}
