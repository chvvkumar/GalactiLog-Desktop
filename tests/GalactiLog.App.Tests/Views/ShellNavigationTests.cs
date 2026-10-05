using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Analysis;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.Views.Settings;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;
using DetailFactory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Roadmap Phase 5 row 4, first clause of the Verify line: each rail item navigates. Plus the
// design-spec 18.3 view smoke tests for the two page views this task introduces.
public class ShellNavigationTests : IDisposable
{
    // Review finding M12: each Statistics page holds nine ChartTheme.Changed subscriptions, so the
    // ones these tests build are disposed rather than left to accumulate across the run.
    private readonly List<StatisticsViewModel> _statistics = [];
    private readonly List<ActivityViewModel> _activity = [];
    private readonly List<AnalysisViewModel> _analysis = [];

    public void Dispose()
    {
        foreach (var page in _statistics)
        {
            page.Dispose();
        }

        _statistics.Clear();

        foreach (var page in _activity)
        {
            page.Dispose();
        }

        _activity.Clear();

        // Phase 17 Task 4: the same rule for the Analysis pages. Each holds five tabs whose load
        // loop publishes back through the post seam.
        foreach (var page in _analysis)
        {
            page.Dispose();
        }

        _analysis.Clear();
        GC.SuppressFinalize(this);
    }

    private StatisticsViewModel CreateStatistics()
    {
        var page = StatisticsViewModelTestFactory.Create();
        _statistics.Add(page);
        return page;
    }

    // FIXER LIST F21: the shell takes a factory for the Activity destination and calls it on the
    // first read of that rail entry, so the cases that never navigate there build no page at all.
    // The two cases that do navigate pass a page built through CreateAsync instead, because an
    // AvaloniaFact runs on the headless UI thread and this form blocks on the first load.
    // Phase 17 Task 4: the sixth rail destination, built by a factory that really constructs the
    // page, because EachRailItem_Navigates visits every rail item and asserts a real view type for
    // each one.
    private AnalysisViewModel CreateAnalysis()
    {
        var page = AnalysisViewModelTestFactory.Create();
        _analysis.Add(page);
        return page;
    }

    private ActivityViewModel CreateActivity()
    {
        var page = ActivityViewModelTestFactory.Create();
        _activity.Add(page);
        return page;
    }

    private (MainWindow Window, MainWindowViewModel ViewModel) ShowShell(
        string? contentWidth = null,
        StatusBarViewModel? statusBar = null,
        Func<ActivityViewModel>? activity = null,
        Func<DiagnosticsViewModel>? diagnostics = null)
    {
        var viewModel = new MainWindowViewModel(
            contentWidth is null ? new GeneralSettings() : new GeneralSettings { ContentWidth = contentWidth },
            DashboardViewModelTestFactory.Create(),
            statusBar ?? CreateBareStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            activity ?? CreateActivity,
            diagnostics);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    // Fix pass review item 5: ScanCoordinatorTestFactory.CreateBare() is the one place
    // App.Tests builds a ScanCoordinator; it never opens a database.
    private static StatusBarViewModel CreateBareStatusBar()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        return new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel);
    }

    [AvaloniaFact]
    public void MainWindow_WithViewModel_ProducesNonZeroLayout()
    {
        var (window, _) = ShowShell();

        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);
        Assert.True(window.GetControl<ListBox>("NavigationRail").Bounds.Width > 0);
        Assert.True(window.GetControl<Border>("StatusBar").Bounds.Height > 0);
    }

    // The rail carries no logo and its contents sit at the top. The window icon
    // stays on the packed .ico. A rail that keeps an Image, or a toggle that sits lower than its
    // own 8 px top margin, or a list that is not directly under the toggle, fails here.
    [AvaloniaFact]
    public void Window_HasAnIcon_AndTheRailHasNoLogo_ItsToggleAndListAtTheTop()
    {
        var (window, _) = ShowShell();

        Assert.NotNull(window.Icon);
        Assert.DoesNotContain(
            window.GetControl<Border>("NavigationColumn").GetVisualDescendants(),
            visual => visual is Image);

        var toggle = window.GetControl<Button>("NavRailToggle");
        var rail = window.GetControl<ListBox>("NavigationRail");
        Assert.Equal(8d, toggle.Bounds.Y, 1);
        Assert.True(rail.Bounds.Y >= toggle.Bounds.Bottom);
        Assert.True(rail.Bounds.Y <= toggle.Bounds.Bottom + 8);
    }

    [AvaloniaFact]
    public void NavigationRail_ListsTheSixDestinations()
    {
        var (window, viewModel) = ShowShell();
        var rail = window.GetControl<ListBox>("NavigationRail");

        Assert.Equal(viewModel.Items, rail.ItemsSource);
        Assert.Same(viewModel.Selected, rail.SelectedItem);
    }

    [AvaloniaFact]
    public async Task EachRailItem_Navigates_AndContentRegionShowsThatPage()
    {
        // The activity page is built through CreateAsync and handed to the shell's factory, so the
        // headless UI thread never blocks on its first load (Task 4 review finding 12).
        using var activity = await ActivityViewModelTestFactory.CreateAsync();
        using var diagnostics = await DiagnosticsViewModelTestFactory.CreateAsync();
        var (window, viewModel) = ShowShell(activity: () => activity, diagnostics: () => diagnostics);
        var contentRegion = window.GetControl<ContentControl>("ContentRegion");

        foreach (var item in viewModel.Items)
        {
            viewModel.Selected = item;
            Dispatcher.UIThread.RunJobs();

            Assert.Same(item.Page, contentRegion.Content);
            Assert.Same(item, window.GetControl<ListBox>("NavigationRail").SelectedItem);

            // Phase 7 Task 3 made the settings destination a real page, Phase 9 Task 3 the
            // statistics one, Phase 9 Task 4 the activity one and Phase 10 Task 1 the diagnostics
            // one. Every rail destination is a real page now, so the placeholder arm is dead and
            // a missing App.axaml template would fail here rather than silently fall back to it.
            var expectedView = item.Page switch
            {
                DashboardViewModel => typeof(DashboardView),
                SettingsViewModel => typeof(SettingsView),
                StatisticsViewModel => typeof(StatisticsView),
                ActivityViewModel => typeof(ActivityView),
                DiagnosticsViewModel => typeof(DiagnosticsView),

                // Phase 17 Task 4. Without this arm the Analysis iteration falls through to the
                // placeholder view and fails at the assertion below, which is what makes the
                // App.axaml template for the page load bearing here.
                AnalysisViewModel => typeof(AnalysisView),
                _ => typeof(PlaceholderPageView),
            };
            Assert.Contains(contentRegion.GetVisualDescendants(), visual => visual.GetType() == expectedView);
        }

        Assert.DoesNotContain(viewModel.Items, item => item.Page is PlaceholderPageViewModel);
    }

    // Phase 10 Task 1: the diagnostics destination is real, and the App.axaml DataTemplate is what
    // maps it to its view, so a missing template would fail here. Built through CreateAsync for
    // the reason the activity case is: an AvaloniaFact runs on the headless UI thread and that
    // thread is a pool thread.
    [AvaloniaFact]
    public async Task DiagnosticsRailItem_ShowsTheDiagnosticsView()
    {
        using var diagnostics = await DiagnosticsViewModelTestFactory.CreateAsync();
        var (window, viewModel) = ShowShell(diagnostics: () => diagnostics);
        var contentRegion = window.GetControl<ContentControl>("ContentRegion");

        viewModel.Selected = viewModel.Items[4];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("diagnostics", viewModel.Selected.Key);
        Assert.Same(diagnostics, contentRegion.Content);
        Assert.Contains(contentRegion.GetVisualDescendants(), visual => visual is DiagnosticsView);
        Assert.DoesNotContain(contentRegion.GetVisualDescendants(), visual => visual is PlaceholderPageView);
        viewModel.Dispose();
    }

    // Phase 9 Task 4: the activity destination is real too, and the App.axaml DataTemplate is what
    // maps it to its view, so a missing template would fail here. Built locally rather than in
    // ShowShell, because the page is built through CreateAsync, which awaits the page's first load
    // instead of blocking the headless UI thread on it (Task 4 review finding 12), and handed to
    // the shell's activity factory ready-made (FIXER LIST F21).
    [AvaloniaFact]
    public async Task ActivityRailItem_ShowsTheActivityView()
    {
        using var activity = await ActivityViewModelTestFactory.CreateAsync();
        var viewModel = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateBareStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            () => activity);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var contentRegion = window.GetControl<ContentControl>("ContentRegion");

        viewModel.Selected = viewModel.Items[3];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("activity", viewModel.Selected.Key);
        Assert.IsType<ActivityViewModel>(contentRegion.Content);
        Assert.Contains(contentRegion.GetVisualDescendants(), visual => visual is ActivityView);
        viewModel.Dispose();
    }

    // Phase 9 Task 3: the statistics destination is real, and the App.axaml DataTemplate is what
    // maps it to its view, so a missing template would fail here.
    [AvaloniaFact]
    public void StatisticsRailItem_ShowsTheStatisticsView()
    {
        var (window, viewModel) = ShowShell();
        var contentRegion = window.GetControl<ContentControl>("ContentRegion");

        viewModel.Selected = viewModel.Items[1];
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<StatisticsViewModel>(contentRegion.Content);
        Assert.Contains(contentRegion.GetVisualDescendants(), visual => visual is StatisticsView);
    }

    // The existing rail rule: a rail click closes an open detail page. Asserted for
    // the statistics destination because it is the first real page that rule can land on.
    [AvaloniaFact]
    public void StatisticsRailItem_ClosesAnOpenDetailPage()
    {
        var (window, viewModel, dashboard) = ShowRoutedShell();
        var contentRegion = window.GetControl<ContentControl>("ContentRegion");
        dashboard.Targets.OpenTargetCommand.Execute(dashboard.Targets.Rows[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(viewModel.Detail);

        viewModel.Selected = viewModel.Items[1];
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.Detail);
        Assert.Same(viewModel.Items[1].Page, contentRegion.Content);
        Assert.Contains(contentRegion.GetVisualDescendants(), visual => visual is StatisticsView);

        viewModel.Dispose();
    }

    // Clicking the rail row that is already selected never changes the ListBox's SelectedItem,
    // so the two-way binding never reaches the Selected setter and its detail-closing rule.
    // The window forwards that click itself: Dashboard row, detail open, click, dashboard back.
    [AvaloniaFact]
    public void ReclickingTheSelectedRailItem_ClosesAnOpenDetailPage()
    {
        var (window, viewModel, dashboard) = ShowRoutedShell();
        var rail = window.GetControl<ListBox>("NavigationRail");
        dashboard.Targets.OpenTargetCommand.Execute(dashboard.Targets.Rows[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(viewModel.Detail);
        Assert.Same(viewModel.Items[0], rail.SelectedItem);

        var row = (Control)rail.ContainerFromIndex(0)!;
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.Detail);
        Assert.Same(viewModel.Items[0].Page, window.GetControl<ContentControl>("ContentRegion").Content);

        viewModel.Dispose();
    }

    [AvaloniaFact]
    public void SelectingInTheRail_UpdatesTheViewModel()
    {
        var (window, viewModel) = ShowShell();
        var rail = window.GetControl<ListBox>("NavigationRail");

        rail.SelectedItem = viewModel.Items[4];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("diagnostics", viewModel.Selected.Key);
    }

    // Review item 1: ctrl-clicking the selected rail row makes ListBox write null through the
    // two-way SelectedItem binding. The shell always has a destination, so the selection and
    // the content must survive it. The rail's own highlight does not come back in the same
    // layout pass -- Avalonia ignores the re-entrant PropertyChanged raised from inside the
    // binding's own source update -- which is cosmetic, self-corrects on the next click, and
    // is not what this test pins.
    [AvaloniaFact]
    public async Task ClearingTheRailSelection_LeavesTheShellOnItsCurrentPage()
    {
        using var activity = await ActivityViewModelTestFactory.CreateAsync();
        var (window, viewModel) = ShowShell(activity: () => activity);
        var rail = window.GetControl<ListBox>("NavigationRail");
        var contentRegion = window.GetControl<ContentControl>("ContentRegion");

        viewModel.Selected = viewModel.Items[3];
        Dispatcher.UIThread.RunJobs();

        rail.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("activity", viewModel.Selected.Key);
        Assert.Same(viewModel.Items[3].Page, viewModel.CurrentPage);
        Assert.Same(viewModel.Items[3].Page, contentRegion.Content);

        // And the rail is still usable afterwards.
        rail.SelectedItem = viewModel.Items[5];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("settings", viewModel.Selected.Key);
    }

    [AvaloniaTheory]
    [InlineData("normal", 1200d)]
    [InlineData("wide", 1600d)]
    [InlineData("extra-wide", double.PositiveInfinity)]
    public void ContentRegion_MaxWidth_TracksContentWidthSetting(string contentWidth, double expected)
    {
        var (window, _) = ShowShell(contentWidth);

        Assert.Equal(expected, window.GetControl<ContentControl>("ContentRegion").MaxWidth);
    }

    [AvaloniaFact]
    public void DashboardView_Constructs_AndBindsToADashboardViewModel()
    {
        var view = new DashboardView { DataContext = DashboardViewModelTestFactory.Create() };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void PlaceholderPageView_Constructs()
    {
        var view = new PlaceholderPageView
        {
            DataContext = new PlaceholderPageViewModel("Statistics", "The statistics page arrives in Phase 9."),
        };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Statistics");
    }

    // Roadmap Phase 5 row 4, second and third clauses of the Verify line, at the view level:
    // the status bar renders a progress envelope pushed from a (fake, in the sense of never
    // touching a real database) coordinator, and the cancel button's enablement follows scan
    // state.
    [AvaloniaFact]
    public void StatusBar_RendersAProgressEnvelope_PushedFromAFakeCoordinator()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var status = new ScanStatusService(coordinator, action => action());
        var statusBar = new StatusBarViewModel(status, coordinator.Cancel);
        var (window, _) = ShowShell(statusBar: statusBar);
        var statusBarBorder = window.GetControl<Border>("StatusBar");

        coordinator.RaiseProgress(ScanTaskNames.Classify, 3, 10, "Classifying 3/10 files", force: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(
            statusBarBorder.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Classifying 3/10 files");

        // By name: Phase 7 FIXER item 19 added a second button to this bar, the manual Run scan,
        // and the two swap places rather than sharing the row.
        // By name, not by Single(): Phase 7 FIXER item 19 added a second button to this bar, the
        // manual Run scan, and the two swap places rather than sharing the row. The names live in
        // StatusBarView's own scope, so they are reached through the visual tree.
        var buttons = statusBarBorder.GetVisualDescendants().OfType<Button>().ToList();
        var cancelButton = buttons.Single(button => button.Name == "CancelButton");
        Assert.True(cancelButton.IsVisible, "the cancel button must be visible while a scan runs");
        Assert.False(
            buttons.Single(button => button.Name == "RunScanButton").IsVisible,
            "the run scan button must be hidden while a scan runs");
        // Review item 4: IsEnabled is the rendered control state; Command.CanExecute is the
        // command's own opinion and can be true even while a control bound to it is disabled
        // for an unrelated reason, so asserting the control is the stronger check.
        Assert.True(cancelButton.IsEnabled, "the cancel button must be enabled while a scan runs");
    }

    // Phase 6 Task 3, at the view level: a dashboard row click puts the Target detail page in the
    // shell's content region, and the page's Back button puts the rail destination back. The
    // App.axaml DataTemplate is what maps the view-model to the view, so this is also the test
    // that a missing template would fail.

    private static readonly TargetRow DetailRow = new(
        GroupKey: DetailFactory.ResolvedGroupKey,
        TargetId: DetailFactory.TargetId,
        Name: "M 31",
        CommonName: "Andromeda Galaxy",
        CatalogId: "M 31",
        ObjectType: "G",
        ObjectCategory: "Galaxy",
        IntegrationSeconds: 44_640d,
        FrameCount: 148,
        SessionCount: 2,
        FirstSession: DetailFactory.FirstSession,
        LastSession: DetailFactory.LastSession,
        Palette: [],
        Equipment: [],
        Aliases: [],
        Sessions: []);

    private (MainWindow Window, MainWindowViewModel ViewModel, DashboardViewModel Dashboard) ShowRoutedShell()
    {
        var dashboard = DashboardViewModelTestFactory.Create(
            list: _ => new TargetListingPage([DetailRow], 1, 44_640d, 148, 1, 50));
        var viewModel = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateBareStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: (groupKey, sessionDate) => DetailFactory
                .Create(groupKey: groupKey, initialSessionDate: sessionDate)
                .Settle()
                .ViewModel);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel, dashboard);
    }

    [AvaloniaFact]
    public void RowClick_PutsTheDetailPageInTheContentRegion()
    {
        var (window, viewModel, dashboard) = ShowRoutedShell();
        var contentRegion = window.GetControl<ContentControl>("ContentRegion");

        dashboard.Targets.OpenTargetCommand.Execute(dashboard.Targets.Rows[0]);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<TargetDetailViewModel>(contentRegion.Content);
        Assert.Contains(contentRegion.GetVisualDescendants(), visual => visual is TargetDetailView);
        Assert.Contains(
            contentRegion.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "M 31");

        viewModel.Dispose();
    }

    [AvaloniaFact]
    public void DetailBackButton_ReturnsToTheRailDestination()
    {
        var (window, viewModel, dashboard) = ShowRoutedShell();
        var contentRegion = window.GetControl<ContentControl>("ContentRegion");

        dashboard.Targets.OpenTargetCommand.Execute(dashboard.Targets.Rows[0]);
        Dispatcher.UIThread.RunJobs();

        var detailView = contentRegion.GetVisualDescendants().OfType<TargetDetailView>().Single();
        var back = detailView.Named<Button>("BackButton");
        Assert.NotNull(back.Command);

        back.Command.Execute(back.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.Detail);
        Assert.Same(viewModel.Items[0].Page, contentRegion.Content);
        Assert.Contains(contentRegion.GetVisualDescendants(), visual => visual is DashboardView);

        viewModel.Dispose();
    }

    [AvaloniaFact]
    public void NavigationColumn_OpensAsANarrowStrip_ExpandsOnRequest_AndKeepsEveryDestination()
    {
        // The strip is the launch state, and the toggle is what brings the
        // labels in. A regression is the labelled rail on a fresh launch.
        var (window, viewModel) = ShowShell();
        var column = window.GetControl<Border>("NavigationColumn");
        var rail = window.GetControl<ListBox>("NavigationRail");
        var toggle = window.GetControl<Button>("NavRailToggle").Command!;
        Assert.True(viewModel.IsNavRailCollapsed);
        Assert.Equal(MainWindowViewModel.NavRailCollapsedWidth, column.Bounds.Width);

        toggle.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var expandedWidth = column.Bounds.Width;
        Assert.Equal(MainWindowViewModel.NavRailExpandedWidth, expandedWidth);
        Assert.Contains(
            rail.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Dashboard" && block.IsEffectivelyVisible);

        toggle.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsNavRailCollapsed);
        Assert.Equal(MainWindowViewModel.NavRailCollapsedWidth, column.Bounds.Width);
        Assert.True(column.Bounds.Width < expandedWidth);

        // The six destinations are still there to click; only their labels have gone.
        Assert.Equal(6, rail.ItemCount);
        Assert.DoesNotContain(
            rail.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Dashboard" && block.IsEffectivelyVisible);

        viewModel.Selected = viewModel.Items[5];
        Dispatcher.UIThread.RunJobs();
        Assert.Same(viewModel.Items[5].Page, window.GetControl<ContentControl>("ContentRegion").Content);
    }

    // Phase 14B Task 7 (PAR-017, spec 12.6). The rail badge. Lines 472 and 484 to 485 above walk
    // every TextBlock in the rail by its Text content ("Dashboard"), which the badge's numeric
    // TextBlock never matches, so those two cases are untouched by the badge and needed no
    // re-pointing or scoping.

    [AvaloniaFact]
    public void Rail_TheActivityEntry_CarriesTheUnseenCount()
    {
        var (window, viewModel) = ShowShell(activity: () => ActivityViewModelTestFactory.Create(countUnseen: _ => 3));

        viewModel.Selected = viewModel.Items.First(item => item.Key == "activity");
        Dispatcher.UIThread.RunJobs();

        var rail = window.GetControl<ListBox>("NavigationRail");
        var badge = rail.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tag") && b.IsEffectivelyVisible);
        Assert.Equal("3", (badge.Child as TextBlock)?.Text);
    }

    [AvaloniaFact]
    public void Rail_WithNoUnseenRows_CarriesNoBadge()
    {
        var (window, viewModel) = ShowShell();

        viewModel.Selected = viewModel.Items.First(item => item.Key == "activity");
        Dispatcher.UIThread.RunJobs();

        var rail = window.GetControl<ListBox>("NavigationRail");
        var badges = rail.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tag"));
        Assert.All(badges, b => Assert.False(b.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public void Rail_CollapsedStillShowsTheBadge()
    {
        var (window, viewModel) = ShowShell(activity: () => ActivityViewModelTestFactory.Create(countUnseen: _ => 2));

        viewModel.Selected = viewModel.Items.First(item => item.Key == "activity");
        Dispatcher.UIThread.RunJobs();

        window.GetControl<Button>("NavRailToggle").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var rail = window.GetControl<ListBox>("NavigationRail");
        var badge = rail.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tag") && b.IsEffectivelyVisible);
        Assert.Equal("2", (badge.Child as TextBlock)?.Text);
    }

    [AvaloniaFact]
    public void Rail_TheBadge_IsOnlyOnTheActivityEntry()
    {
        var (window, viewModel) = ShowShell(activity: () => ActivityViewModelTestFactory.Create(countUnseen: _ => 4));

        viewModel.Selected = viewModel.Items.First(item => item.Key == "activity");
        Dispatcher.UIThread.RunJobs();

        var rail = window.GetControl<ListBox>("NavigationRail");
        var visibleBadges = rail.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Classes.Contains("tag") && b.IsEffectivelyVisible)
            .ToList();

        Assert.Single(visibleBadges);
    }
}
