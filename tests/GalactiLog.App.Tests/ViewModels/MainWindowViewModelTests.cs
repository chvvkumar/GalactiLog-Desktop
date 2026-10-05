using System.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;
using DetailFactory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Plain xunit facts, no window and no database: design-spec 18.3's rule that every view-model
// is unit-testable on its own is what these assert.
public class MainWindowViewModelTests : IDisposable
{
    // Phase 5 Task 5: MainWindowViewModel now composes a StatusBarViewModel, whose
    // ScanStatusService requires a concrete ScanCoordinator (sealed, no interface -- Phase 4
    // ruling Q4). ScanCoordinatorTestFactory.CreateBare() never opens a database, so this
    // stays a no-I/O construction exactly like the rest of this file (fix pass review item 5).
    private static StatusBarViewModel CreateStatusBar()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        return new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel);
    }

    // Phase 9 Task 3: the statistics destination is a real page now, so the shell takes one.
    // Built from the shared factory, which uses lambdas and no database, exactly like the
    // dashboard and settings pages above.
    // Review finding M12: every shell test builds a Statistics page, and each one holds nine
    // ChartTheme.Changed subscriptions. Left undisposed they accumulate across the run and every
    // later ChartTheme.Apply drives each leaked calendar into a background reload. xUnit builds one
    // instance of this class per test, so Dispose runs after each one.
    private readonly List<StatisticsViewModel> _statistics = [];

    public void Dispose()
    {
        foreach (var page in _statistics)
        {
            page.Dispose();
        }

        _statistics.Clear();

        // Phase 9 Task 4: the same rule for the activity pages this suite builds. Each one holds a
        // page-lifetime CancellationTokenSource and a scan subscription.
        foreach (var page in _activity)
        {
            page.Dispose();
        }

        _activity.Clear();

        // Phase 17 Task 4: the same rule for the Analysis pages. Each one holds five tabs whose
        // load loop publishes back through the post seam.
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

    // FIXER LIST F21: the shell takes factories for the Statistics and Activity pages and builds
    // neither until the rail entry is read, so these lambdas run only in the tests that navigate.
    private MainWindowViewModel Create(string? contentWidth = null)
        => new(
            contentWidth is null ? new GeneralSettings() : new GeneralSettings { ContentWidth = contentWidth },
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity);

    // Phase 9 Task 4: the activity destination is a real page now. Built from its shared factory,
    // which uses lambdas and no database, exactly like the statistics page above, and disposed with
    // the statistics pages so no background load outlives the test.
    private readonly List<ActivityViewModel> _activity = [];

    private ActivityViewModel CreateActivity()
    {
        var page = ActivityViewModelTestFactory.Create();
        _activity.Add(page);
        return page;
    }

    // Phase 17 Task 4: the Analysis destination, the sixth, on the same lazy footing.
    private readonly List<AnalysisViewModel> _analysis = [];

    private AnalysisViewModel CreateAnalysis()
    {
        var page = AnalysisViewModelTestFactory.Create();
        _analysis.Add(page);
        return page;
    }

    private MainWindowViewModel CreateWithActivity(ActivityViewModel activity)
        => new(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            () => activity);

    [Fact]
    public void ActivityDestination_IsTheInjectedPage_AndNoLongerAPlaceholder()
    {
        var activity = CreateActivity();
        var viewModel = CreateWithActivity(activity);

        // The seam Phase 5 Task 4 opened with "The activity log page arrives in Phase 9."
        Assert.Equal("activity", viewModel.Items[4].Key);
        Assert.Same(activity, viewModel.Items[4].Page);
        Assert.IsType<ActivityViewModel>(viewModel.Items[4].Page);
        viewModel.Dispose();
    }

    [Fact]
    public void ActivityDestination_Navigates()
    {
        var activity = CreateActivity();
        var viewModel = CreateWithActivity(activity);

        viewModel.Selected = viewModel.Items[4];

        Assert.Equal("activity", viewModel.Selected.Key);
        Assert.Same(activity, viewModel.CurrentPage);
        viewModel.Dispose();
    }

    [Fact]
    public void Items_AreTheSevenShellDestinationsInSpecOrder()
    {
        var viewModel = Create();

        // Spec 12.14, ruling A3: Analysis is the sixth destination, placed after Statistics. The
        // rail's ORDER is the contract spec 12 states, so this stays an exact positional set and
        // never a key lookup. Spec 12.17, ruling R3: Mosaics is second, after Dashboard.
        Assert.Equal(
            new[] { "dashboard", "mosaics", "statistics", "analysis", "activity", "diagnostics", "settings" },
            viewModel.Items.Select(item => item.Key));
    }

    [Fact]
    public void Items_HaveARailLabelEach()
    {
        var viewModel = Create();

        Assert.Equal(
            new[] { "Dashboard", "Mosaics", "Statistics", "Analysis", "Activity", "Diagnostics", "Settings" },
            viewModel.Items.Select(item => item.Title));
    }

    [Fact]
    public void Selected_DefaultsToDashboard()
    {
        var viewModel = Create();

        Assert.Equal("dashboard", viewModel.Selected.Key);
        Assert.Same(viewModel.Items[0].Page, viewModel.CurrentPage);
    }

    [Fact]
    // Phase 7 Task 3 made the settings destination real, Phase 9 Task 3 the statistics one, Phase
    // 9 Task 4 the activity one and Phase 10 Task 1 the diagnostics one. This case supplies no
    // diagnostics factory, which is the surface a shell built with no queries gets, and the
    // placeholder is what keeps the rail's five-destination contract asserted.
    public void Dashboard_Settings_AndStatistics_AreTheInjectedInstances_AndEveryOtherPageIsAPlaceholder()
    {
        var dashboard = DashboardViewModelTestFactory.Create();
        var settings = TabFactory.CreateSettingsPage();
        var statistics = CreateStatistics();
        var activity = CreateActivity();
        var viewModel = new MainWindowViewModel(
            new GeneralSettings(), dashboard, CreateStatusBar(), settings, () => statistics, CreateAnalysis,
            () => activity);

        Assert.Same(dashboard, viewModel.Items[0].Page);
        Assert.Same(statistics, viewModel.Items[2].Page);
        Assert.Equal("statistics", viewModel.Items[2].Key);
        Assert.Same(activity, viewModel.Items[4].Page);
        Assert.Same(settings, viewModel.Items[6].Page);
        Assert.Equal("settings", viewModel.Items[6].Key);

        // With no diagnostics factory the rail keeps a placeholder, the same way a null openDetail
        // leaves the detail route inert.
        Assert.IsType<PlaceholderPageViewModel>(viewModel.Items[5].Page);
        viewModel.Dispose();
    }

    // ---- Phase 10 Task 1: the Diagnostics rail destination -------------------------------------

    [Fact]
    public void Diagnostics_IsAFactoryEntry_AndIsNotConstructedUntilVisited()
    {
        var built = 0;
        DiagnosticsViewModel? page = null;
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            () =>
            {
                built++;
                page = DiagnosticsViewModelTestFactory.Create();
                return page;
            });

        // The page reads the whole spec 12.8 snapshot on its first refresh, so an application
        // start that never opens it must build nothing.
        Assert.Equal("diagnostics", shell.Items[5].Key);
        Assert.False(shell.Items[5].IsConstructed);
        Assert.Equal(0, built);

        shell.Selected = shell.Items[5];

        Assert.Equal(1, built);
        Assert.True(shell.Items[5].IsConstructed);
        Assert.Same(page, shell.CurrentPage);

        // Memoized: navigating away and back does not build a second page.
        shell.Selected = shell.Items[0];
        shell.Selected = shell.Items[5];
        Assert.Equal(1, built);

        shell.Dispose();
        page?.Dispose();
    }

    // ---- FIXER LIST F21: the two heavy rail destinations are built on first visit ---------------

    [Fact]
    public void ConstructingTheShell_BuildsNeitherTheStatisticsNorTheActivityPage()
    {
        var statistics = 0;
        var activity = 0;
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            () => { statistics++; return CreateStatistics(); },
            CreateAnalysis,
            () => { activity++; return CreateActivity(); });

        // Task 2's full-library aggregate and the activity first page both run in their page's own
        // constructor, so an application start that opens neither must build neither.
        Assert.Equal(0, statistics);
        Assert.Equal(0, activity);
        Assert.False(shell.Items[2].IsConstructed);
        Assert.False(shell.Items[4].IsConstructed);

        // Phase 17 Task 4: the sixth destination holds to F21's rule too. The Analysis page reads
        // the display document and resolves the cache in its own constructor, so an application
        // start that never opens it must build nothing.
        Assert.Equal("analysis", shell.Items[3].Key);
        Assert.False(shell.Items[3].IsConstructed);
        shell.Dispose();
    }

    [Fact]
    public void FirstNavigation_BuildsEachPageOnce_AndKeepsIt()
    {
        var statistics = 0;
        var activity = 0;
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            () => { statistics++; return CreateStatistics(); },
            CreateAnalysis,
            () => { activity++; return CreateActivity(); });

        shell.Selected = shell.Items[2];
        var firstStatistics = shell.CurrentPage;
        shell.Selected = shell.Items[4];
        var firstActivity = shell.CurrentPage;
        shell.Selected = shell.Items[0];
        shell.Selected = shell.Items[2];

        Assert.Equal(1, statistics);
        Assert.Equal(1, activity);
        Assert.IsType<StatisticsViewModel>(firstStatistics);
        Assert.IsType<ActivityViewModel>(firstActivity);
        Assert.Same(firstStatistics, shell.CurrentPage);
        Assert.Same(firstActivity, shell.Items[4].Page);
        shell.Dispose();
    }

    [Fact]
    public void TimelineBarClick_StillNavigates_AfterTheStatisticsPageIsBuiltLazily()
    {
        // questions.md Q16's route survives F21 because the DateRangeRequested subscription is
        // attached by the factory callback, on the first read of the rail entry, not in the
        // shell's constructor.
        var dashboard = DashboardViewModelTestFactory.Create();
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity);

        shell.Selected = shell.Items[2];
        var statistics = Assert.IsType<StatisticsViewModel>(shell.CurrentPage);
        statistics.Timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);
        statistics.Timeline.Granularity = TimelineGranularity.Monthly;
        var bar = statistics.Timeline.Bars[0];
        statistics.Timeline.SelectPeriodCommand.Execute(0);
        DashboardViewModelTestFactory.Settle(dashboard);

        Assert.Equal("dashboard", shell.Selected.Key);
        Assert.Equal(bar.Start, DateOnly.FromDateTime(dashboard.Filters.DateFrom!.Value.Date));
        Assert.Equal(bar.End, DateOnly.FromDateTime(dashboard.Filters.DateTo!.Value.Date));
        shell.Dispose();
    }

    [Fact]
    public void Dispose_DropsNoSubscription_WhenStatisticsWasNeverVisited()
    {
        var statistics = 0;
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            () => { statistics++; return CreateStatistics(); },
            CreateAnalysis,
            CreateActivity);

        shell.Dispose();

        // Disposing the shell must not build the page in order to unsubscribe from it, which is
        // the defect the lazy factory exists to avoid.
        Assert.Equal(0, statistics);
    }

    [Fact]
    public void StatisticsDestination_IsNoLongerAPlaceholder()
    {
        var viewModel = Create();

        // The seam Phase 5 Task 4 opened with "The statistics page arrives in Phase 9."
        Assert.IsType<StatisticsViewModel>(viewModel.Items[2].Page);
    }

    [Fact]
    public void TimelineBarClick_PutsThePeriodOnTheDashboardFilter_AndNavigatesThere()
    {
        // questions.md Q16, which amends spec 12.5. Asserted at the shell, because the shell is
        // what owns the rail and the dashboard's filter panel.
        var dashboard = DashboardViewModelTestFactory.Create();
        var statistics = CreateStatistics();
        var viewModel = new MainWindowViewModel(
            new GeneralSettings(), dashboard, CreateStatusBar(), TabFactory.CreateSettingsPage(), () => statistics,
            CreateAnalysis, CreateActivity);
        viewModel.Selected = viewModel.Items[2];

        statistics.Timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);
        statistics.Timeline.Granularity = TimelineGranularity.Monthly;
        var bar = statistics.Timeline.Bars[0];
        statistics.Timeline.SelectPeriodCommand.Execute(0);
        DashboardViewModelTestFactory.Settle(dashboard);

        Assert.Equal("dashboard", viewModel.Selected.Key);
        Assert.Equal(bar.Start, DateOnly.FromDateTime(dashboard.Filters.DateFrom!.Value.Date));
        Assert.Equal(bar.End, DateOnly.FromDateTime(dashboard.Filters.DateTo!.Value.Date));
        viewModel.Dispose();
    }

    [Fact]
    public void Dispose_DropsTheStatisticsDateRangeSubscription()
    {
        var dashboard = DashboardViewModelTestFactory.Create();
        var statistics = CreateStatistics();
        var viewModel = new MainWindowViewModel(
            new GeneralSettings(), dashboard, CreateStatusBar(), TabFactory.CreateSettingsPage(), () => statistics,
            CreateAnalysis, CreateActivity);
        viewModel.Selected = viewModel.Items[2];

        viewModel.Dispose();
        statistics.Timeline.SelectPresetCommand.Execute(TimelineRangePreset.All);
        statistics.Timeline.SelectPeriodCommand.Execute(0);
        DashboardViewModelTestFactory.Settle(dashboard);

        Assert.Null(dashboard.Filters.DateFrom);
        Assert.Equal("statistics", viewModel.Selected.Key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SelectingEachItem_ChangesCurrentPage(int index)
    {
        var viewModel = Create();

        // Move off the target first, so index 0 (already selected at construction) is a real
        // change like the rest and the PropertyChanged assertion below means something.
        viewModel.Selected = viewModel.Items[(index + 1) % viewModel.Items.Count];

        var raised = new List<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.Selected = viewModel.Items[index];

        Assert.Same(viewModel.Items[index].Page, viewModel.CurrentPage);
        Assert.Contains(nameof(MainWindowViewModel.CurrentPage), raised);
        Assert.Contains(nameof(MainWindowViewModel.Selected), raised);
    }

    [Theory]
    [InlineData("normal", 1200d)]
    [InlineData("wide", 1600d)]
    [InlineData("extra-wide", double.PositiveInfinity)]
    public void ContentMaxWidth_ForEachContentWidthValue(string contentWidth, double expected)
        => Assert.Equal(expected, Create(contentWidth).ContentMaxWidth);

    [Theory]
    [InlineData("")]
    [InlineData("ultra-wide")]
    [InlineData("NORMAL")]
    public void ContentMaxWidth_UnknownValue_FallsBackToExtraWide(string contentWidth)
        => Assert.Equal(double.PositiveInfinity, Create(contentWidth).ContentMaxWidth);

    [Fact]
    public void ContentMaxWidth_DefaultSettings_IsExtraWide()
    {
        // design-spec 5.8.1 makes extra-wide the default; guards against the default drifting.
        Assert.Equal("extra-wide", new GeneralSettings().ContentWidth);
        Assert.Equal(double.PositiveInfinity, Create().ContentMaxWidth);
    }

    [Fact]
    public void Constructor_RequiresNoDatabaseOrWindow()
    {
        var viewModel = new MainWindowViewModel(new GeneralSettings(), DashboardViewModelTestFactory.Create(), CreateStatusBar(),
            TabFactory.CreateSettingsPage(), CreateStatistics, CreateAnalysis, CreateActivity);

        Assert.Equal(7, viewModel.Items.Count);
        Assert.NotNull(viewModel.CurrentPage);
    }

    // Phase 6 Task 3: the detail route. Target detail is not a rail destination, it is one
    // nullable page pushed over whichever destination is showing (ruling Q9).

    private static readonly TargetRow SampleRow = new(
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

    // A shell whose dashboard has one clickable row and whose detail route builds pages from the
    // shared test factory. Returns the shell plus a way to click the row.
    private (MainWindowViewModel Shell, DashboardViewModel Dashboard, List<TargetDetailViewModel> Built) CreateRouted(
        Func<string, DateOnly?, TargetDetailViewModel>? openDetail = null)
    {
        var dashboard = DashboardViewModelTestFactory.Create(
            list: _ => new TargetListingPage([SampleRow], 1, 44_640d, 148, 1, 50));
        var built = new List<TargetDetailViewModel>();
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: openDetail ?? ((groupKey, sessionDate) =>
            {
                var page = DetailFactory
                    .Create(groupKey: groupKey, initialSessionDate: sessionDate)
                    .Settle()
                    .ViewModel;
                built.Add(page);
                return page;
            }));
        return (shell, dashboard, built);
    }

    // Phase 18 Task 4, spec 12.17 and ruling R3: Mosaics is a lazy rail page, and its target link
    // opens the Target detail overlay through the shell's one route.
    [Fact]
    public async Task TheMosaicsPage_IsBuiltOnFirstVisit_AndItsTargetLinkOpensTheTargetDetail()
    {
        var target = Guid.NewGuid();
        using var mosaics = new MosaicsPageHarness(new MosaicsBackend
        {
            ListPending = () =>
            [
                new MosaicSuggestionRow(
                    Guid.NewGuid(), "M 31", "M 31", [new GalactiLog.Core.Mosaics.SuggestionPanel(target, "Panel 1", "%", [])],
                    "high", "name", null, [], "sig", DateTime.UtcNow),
            ],
        });
        var opened = new List<string>();
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: (groupKey, sessionDate) =>
            {
                opened.Add(groupKey);
                return DetailFactory.Create(groupKey: groupKey, initialSessionDate: sessionDate).Settle().ViewModel;
            },
            mosaics: () => mosaics.Page);
        Assert.Equal("mosaics", shell.Items[1].Key);
        Assert.False(shell.Items[1].IsConstructed);

        shell.Selected = shell.Items[1];
        Assert.Same(mosaics.Page, shell.CurrentPage);
        await mosaics.Page.PendingLoad;

        var suggestion = Assert.Single(mosaics.Page.VisibleSuggestions);
        suggestion.OpenTargetCommand.Execute(suggestion.Targets[0]);

        Assert.Equal(new[] { target.ToString() }, opened);
        Assert.NotNull(shell.Detail);
        shell.Dispose();
    }

    // Phase 18 Task 5, spec 12 shell and 12.17: the overlay holds a target page or a mosaic page,
    // one at a time. A Mosaics table row opens the mosaic page; Back closes it; a target link on it
    // opens the target, which closes it; a rail click closes it.
    [Fact]
    public async Task AMosaicRow_OpensTheMosaicPageOnTheOverlay_UnderTheOneDetailRule()
    {
        var mosaicId = Guid.NewGuid();
        var target = Guid.NewGuid();
        var listReads = 0;
        var backend = new MosaicsBackend
        {
            ListMosaics = () =>
            {
                listReads++;
                return [new MosaicListRow(mosaicId, "M 31", 0, 0, 0, null, null, [])];
            },
            Detail = id => new MosaicDetail(id, "M 31", null, 0, 0, 0, null, null, [], [], []),
        };
        using var mosaics = new MosaicsPageHarness(backend);
        var built = new List<MosaicDetailViewModel>();
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: (groupKey, sessionDate) => DetailFactory.Create(groupKey: groupKey, initialSessionDate: sessionDate).Settle().ViewModel,
            mosaics: () => mosaics.Page,
            openMosaic: id =>
            {
                var page = new MosaicDetailViewModel(id, backend, new GalactiLog.Core.Io.AppWriter(Path.GetTempPath()), post: action => action());
                built.Add(page);
                return page;
            });
        shell.Selected = shell.Items[1];
        await mosaics.Page.PendingLoad;

        mosaics.Page.Table.Mosaics[0].OpenCommand.Execute(null);
        var page = Assert.IsType<MosaicDetailViewModel>(shell.Detail);
        Assert.Same(page, shell.CurrentPage);
        Assert.Equal(mosaicId, page.Id);

        // Back closes it, and the Mosaics table under it is re-read.
        var readsBefore = listReads;
        page.BackCommand.Execute(null);
        Assert.Null(shell.Detail);
        Assert.Same(mosaics.Page, shell.CurrentPage);
        await mosaics.Page.PendingLoad;
        Assert.True(listReads > readsBefore);

        // A target link on the mosaic page replaces it with the target's page.
        shell.OpenMosaic(mosaicId);
        var second = Assert.IsType<MosaicDetailViewModel>(shell.Detail);
        Assert.NotSame(page, second);
        second.RequestOpenTarget(target);
        Assert.IsType<TargetDetailViewModel>(shell.Detail);

        // Opening a mosaic closes the target page, and a rail click closes the mosaic page.
        shell.OpenMosaic(mosaicId);
        Assert.IsType<MosaicDetailViewModel>(shell.Detail);
        shell.Selected = shell.Items[0];
        Assert.Null(shell.Detail);
        Assert.Equal(3, built.Count);
        shell.Dispose();
    }

    // Phase 18 Task 6, spec 12.2: a dashboard row's mosaic link lands on the shell's one route to
    // the mosaic detail page, and does not open the row's Target detail.
    [Fact]
    public async Task ADashboardMosaicLink_OpensTheMosaicPageOnTheOverlay()
    {
        var mosaicId = Guid.NewGuid();
        var row = SampleRow with { Mosaics = [new MosaicLink(mosaicId, "Alpha")] };
        var dashboard = new DashboardViewModel(
            _ => new TargetListingPage([row], 1, 44_640d, 148, 1, 50),
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            (_, _) => Task.CompletedTask,
            post: action => action());
        await dashboard.Filters.PendingReload!.WaitAsync(TimeSpan.FromSeconds(30));
        DashboardViewModelTestFactory.Settle(dashboard);
        var backend = new MosaicsBackend { Detail = id => new MosaicDetail(id, "Alpha", null, 0, 0, 0, null, null, [], [], []) };
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: (groupKey, sessionDate) => DetailFactory.Create(groupKey: groupKey, initialSessionDate: sessionDate).Settle().ViewModel,
            openMosaic: id => new MosaicDetailViewModel(id, backend, new GalactiLog.Core.Io.AppWriter(Path.GetTempPath()), post: action => action()));

        dashboard.Targets.OpenMosaicCommand.Execute(dashboard.Targets.Rows[0]);

        var page = Assert.IsType<MosaicDetailViewModel>(shell.Detail);
        Assert.Equal(mosaicId, page.Id);
        shell.Dispose();
    }

    // Settled first: a detail close re-queries the dashboard, and with the inline post its Load
    // replaces the rows on a pool thread.
    private static void ClickRow(DashboardViewModel dashboard)
    {
        DashboardViewModelTestFactory.Settle(dashboard);
        dashboard.Targets.OpenTargetCommand.Execute(dashboard.Targets.Rows[0]);
    }

    [Fact]
    public async Task ClosingTheDetailPage_RefreshesTheNightValuesOfAnExpandedRow()
    {
        // Spec 12.15, ruling C27, and the journey the phase reviews found three times over: a
        // session-scope custom value is drawn on the Nights ledger of the Target page and on the
        // dashboard's night expander. Expand a row, open that target, set the night's value, press
        // Back: the expander kept the pre-edit text and the next keystroke in that cell wrote it
        // back over the newer value. The close re-queries the dashboard, and Load keeps the row
        // expanded and re-reads its nights.
        //
        // Red against a close that does not re-query: the second read never happens and the
        // assertion below reads the empty pre-edit text.
        var notes = CustomColumnTestFactory.Text("Notes", scope: CustomColumnScope.Session);
        var night = DetailFactory.FirstSession;
        var stored = new List<CustomValueRow>();

        var row = SampleRow with { Sessions = [new SessionSummary(night, 5, 1_800d)] };
        var dashboard = new DashboardViewModel(
            _ => new TargetListingPage([row], 1, 44_640d, 148, 1, 50),
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            (_, _) => Task.CompletedTask,
            post: action => action(),
            loadCustomColumns: () => [notes],
            loadTargetValues: _ => [],
            loadValuesForTarget: _ => [.. stored],
            writeCustomValue: (_, _, _) => CustomColumnTestFactory.Written);
        await dashboard.Filters.PendingReload!.WaitAsync(TimeSpan.FromSeconds(30));
        DashboardViewModelTestFactory.Settle(dashboard);

        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: (groupKey, sessionDate) => DetailFactory
                .Create(groupKey: groupKey, initialSessionDate: sessionDate)
                .Settle()
                .ViewModel);

        var listRow = dashboard.Targets.Rows[0];
        listRow.ToggleSessionsCommand.Execute(null);
        await (listRow.PendingSessionCells ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("", listRow.Sessions[0].CustomCells[0].Text!.Text);

        ClickRow(dashboard);

        // What the Nights ledger of the open page wrote into that night's slot.
        stored.Add(new CustomValueRow(notes.Id, CustomValueKey.ForSession(DetailFactory.TargetId, night), "windy"));

        shell.CloseDetailCommand.Execute(null);
        DashboardViewModelTestFactory.Settle(dashboard);
        var reloaded = dashboard.Targets.Rows[0];
        await (reloaded.PendingSessionCells ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(shell.Detail);
        Assert.True(reloaded.IsExpanded);
        Assert.Equal("windy", reloaded.Sessions[0].CustomCells[0].Text!.Text);

        shell.Dispose();
    }

    [Fact]
    public void RowClick_OpensTheDetailPage()
    {
        var (shell, dashboard, built) = CreateRouted();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        ClickRow(dashboard);

        var page = Assert.Single(built);
        Assert.Same(page, shell.Detail);
        Assert.Same(page, shell.CurrentPage);
        Assert.Equal(DetailFactory.ResolvedGroupKey, page.GroupKey);
        Assert.Contains(nameof(MainWindowViewModel.Detail), raised);
        Assert.Contains(nameof(MainWindowViewModel.CurrentPage), raised);

        // The rail's own selection is untouched: the page is pushed over it, not instead of it.
        Assert.Equal("dashboard", shell.Selected.Key);
        shell.Dispose();
    }

    // FIXER LIST F10. The dashboard listing carries the target's name, so a rename committed on
    // the detail page has to reach the row behind it. The shell answers the page's event on the
    // dashboard's existing debounced query; no new reload path was added for it.
    [Fact]
    public async Task DetailRename_RequeriesTheDashboardListing()
    {
        var queries = 0;
        var dashboard = DashboardViewModelTestFactory.Create(list: _ =>
        {
            queries++;
            return new TargetListingPage([SampleRow], 1, 44_640d, 148, 1, 50);
        });
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: (groupKey, sessionDate) => DetailFactory
                .Create(groupKey: groupKey, initialSessionDate: sessionDate)
                .Settle()
                .ViewModel);

        ClickRow(dashboard);
        var page = Assert.IsType<TargetDetailViewModel>(shell.Detail);
        var before = queries;

        page.BeginRenameCommand.Execute(null);
        page.RenameText = "Andromeda";
        await page.CommitRenameCommand.ExecuteAsync(null);
        DashboardViewModelTestFactory.Settle(dashboard);

        Assert.Equal(before + 1, queries);
        shell.Dispose();
    }

    [Fact]
    public void RailClick_WhileADetailIsOpen_ClosesIt()
    {
        var (shell, dashboard, built) = CreateRouted();
        ClickRow(dashboard);

        shell.Selected = shell.Items[2];

        Assert.Null(shell.Detail);
        Assert.Same(shell.Items[2].Page, shell.CurrentPage);
        Assert.True(built[0].IsDisposed);
        shell.Dispose();
    }

    [Fact]
    public void RailClick_OnTheSameDestination_AlsoClosesTheDetail()
    {
        // Re-selecting the destination the page was pushed over is also a request to see that
        // destination, so the overlay must clear even though Selected did not change.
        var (shell, dashboard, _) = CreateRouted();
        ClickRow(dashboard);

        shell.Selected = shell.Items[0];

        Assert.Null(shell.Detail);
        Assert.Same(shell.Items[0].Page, shell.CurrentPage);
        shell.Dispose();
    }

    [Fact]
    public void CloseDetail_ReturnsToTheSelectedDestination()
    {
        var (shell, dashboard, built) = CreateRouted();
        shell.Selected = shell.Items[4];
        ClickRow(dashboard);
        Assert.NotNull(shell.Detail);

        shell.CloseDetailCommand.Execute(null);

        Assert.Null(shell.Detail);
        Assert.Same(shell.Items[4].Page, shell.CurrentPage);
        Assert.True(built[0].IsDisposed);
        shell.Dispose();
    }

    [Fact]
    public void DetailBackCommand_ClosesThePage()
    {
        var (shell, dashboard, built) = CreateRouted();
        ClickRow(dashboard);

        // The page's own Back button raises BackRequested; the shell answers it by closing, which
        // keeps one owner of the content region.
        built[0].BackCommand.Execute(null);

        Assert.Null(shell.Detail);
        Assert.True(built[0].IsDisposed);
        shell.Dispose();
    }

    [Fact]
    public void OpeningASecondTarget_DisposesTheFirst()
    {
        var (shell, dashboard, built) = CreateRouted();
        ClickRow(dashboard);
        ClickRow(dashboard);

        Assert.Equal(2, built.Count);
        Assert.True(built[0].IsDisposed);
        Assert.False(built[1].IsDisposed);
        Assert.Same(built[1], shell.Detail);
        shell.Dispose();
    }

    [Fact]
    public void NoOpenDetailDelegate_LeavesTheRouteInert()
    {
        var dashboard = DashboardViewModelTestFactory.Create(
            list: _ => new TargetListingPage([SampleRow], 1, 44_640d, 148, 1, 50));
        var shell = new MainWindowViewModel(new GeneralSettings(), dashboard, CreateStatusBar(),
            TabFactory.CreateSettingsPage(), CreateStatistics, CreateAnalysis, CreateActivity);

        ClickRow(dashboard);

        // Which is what lets every other test in this file build a shell with no queries.
        Assert.Null(shell.Detail);
        Assert.Same(shell.Items[0].Page, shell.CurrentPage);
        shell.Dispose();
    }

    [Fact]
    public void Dispose_ClosesAnOpenDetailPage()
    {
        var (shell, dashboard, built) = CreateRouted();
        ClickRow(dashboard);

        // Which is what flushes a note the user typed and never navigated away from before
        // quitting.
        shell.Dispose();

        Assert.Null(shell.Detail);
        Assert.True(built[0].IsDisposed);

        // And the forwarded row-click subscription is gone, so a later click opens nothing.
        ClickRow(dashboard);
        Assert.Null(shell.Detail);
    }

    // ---- Phase 7 Task 5, ruling Q12: the merged-away callout's "Open <winner>" --------------

    private const string WinnerKey = "40000000-0000-0000-0000-000000000000";

    // A route whose first page is a merged-away target: it renders the callout and carries the
    // winner as its "Open <winner>" destination.
    private (MainWindowViewModel Shell, DashboardViewModel Dashboard, List<TargetDetailViewModel> Built)
        CreateMergedAwayRoute()
    {
        List<TargetDetailViewModel> built = [];
        var routed = CreateRouted((groupKey, sessionDate) =>
        {
            var harness = groupKey == DetailFactory.ResolvedGroupKey
                ? DetailFactory.Create(
                    get: _ => null,
                    groupKey: groupKey,
                    mergedInto: (Guid.Parse(WinnerKey), "M 31", "NGC 224"),
                    initialSessionDate: sessionDate)
                : DetailFactory.Create(groupKey: groupKey, initialSessionDate: sessionDate);

            var page = harness.Settle().ViewModel;
            built.Add(page);
            return page;
        });

        return (routed.Shell, routed.Dashboard, built);
    }

    [Fact]
    public void OpenTargetRequested_ReplacesTheOneOverlay()
    {
        var (shell, dashboard, built) = CreateMergedAwayRoute();
        ClickRow(dashboard);
        var first = Assert.Single(built);
        Assert.True(first.IsMergedAway);

        first.OpenMergedIntoCommand.Execute(null);

        // One overlay, replaced: never a second pushed over the first, and the outgoing page is
        // disposed exactly as Back disposes it.
        Assert.Equal(2, built.Count);
        Assert.True(first.IsDisposed);
        Assert.Same(built[1], shell.Detail);
        Assert.Same(built[1], shell.CurrentPage);
        Assert.Equal(WinnerKey, built[1].GroupKey);
        shell.Dispose();
    }

    [Fact]
    public void OpenTargetRequested_AfterTheDetailClosed_OpensNothing()
    {
        var (shell, dashboard, built) = CreateMergedAwayRoute();
        ClickRow(dashboard);
        var first = built[0];
        shell.CloseDetailCommand.Execute(null);
        Assert.Null(shell.Detail);

        first.OpenMergedIntoCommand.Execute(null);

        // The subscription went with the page, so an event from a closed page reaches nothing.
        Assert.Single(built);
        Assert.Null(shell.Detail);
        shell.Dispose();
    }

    // ---- the two shell preferences (Phase 9 Task 6, FIXER item 2) -----------------------------

    [Theory]
    [InlineData("small", 14d)]
    [InlineData("medium", 16d)]
    [InlineData("large", 18d)]
    [InlineData("x-large", 20d)]
    public void RootFontSize_ComesFromTheTextSizeSetting(string textSize, double expected)
    {
        var shell = new MainWindowViewModel(
            new GeneralSettings { TextSize = textSize },
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity);

        // Design-spec 14.4. MainWindow.axaml binds FontSize to this and every control inherits it.
        Assert.Equal(expected, shell.RootFontSize);
        shell.Dispose();
    }

    [Fact]
    public void ApplyGeneral_BringsBothShellPreferencesForward()
    {
        var shell = Create();
        Assert.Equal(14d, shell.RootFontSize);
        Assert.Equal(double.PositiveInfinity, shell.ContentMaxWidth);

        var changes = new List<string>();
        shell.PropertyChanged += (_, args) => changes.Add(args.PropertyName ?? "");

        // What AppHost's GeneralChanged subscription does. Both keys are written by the Settings
        // Display tab while this shell is alive, so both follow the document rather than the value
        // read at startup.
        shell.ApplyGeneral(new GeneralSettings { TextSize = "large", ContentWidth = "wide" });

        Assert.Equal(18d, shell.RootFontSize);
        Assert.Equal(1600d, shell.ContentMaxWidth);
        Assert.Contains(nameof(MainWindowViewModel.RootFontSize), changes);
        Assert.Contains(nameof(MainWindowViewModel.ContentMaxWidth), changes);
        shell.Dispose();
    }

    [Fact]
    public void ApplyGeneral_AfterDispose_ChangesNothing()
    {
        var shell = Create();
        shell.Dispose();

        shell.ApplyGeneral(new GeneralSettings { TextSize = "large", ContentWidth = "normal" });

        Assert.Equal(14d, shell.RootFontSize);
        Assert.Equal(double.PositiveInfinity, shell.ContentMaxWidth);
    }

    // The rail starts as the narrow strip (polish 1 ruling 4) and expands on request. Session
    // state on the shell, like the dashboard's filter panel flag: nothing persists it. A
    // regression is a shell that opens with the wide rail and its labels on a fresh launch.
    [Fact]
    public void NavRail_StartsCollapsed_AndTheToggleExpandsAndRestoresIt()
    {
        var shell = Create();

        Assert.True(shell.IsNavRailCollapsed);
        Assert.Equal(MainWindowViewModel.NavRailCollapsedWidth, shell.NavRailWidth);
        Assert.True(shell.NavRailWidth < MainWindowViewModel.NavRailExpandedWidth);

        shell.ToggleNavRailCommand.Execute(null);

        Assert.False(shell.IsNavRailCollapsed);
        Assert.Equal(MainWindowViewModel.NavRailExpandedWidth, shell.NavRailWidth);

        shell.ToggleNavRailCommand.Execute(null);

        Assert.True(shell.IsNavRailCollapsed);
        Assert.Equal(MainWindowViewModel.NavRailCollapsedWidth, shell.NavRailWidth);
    }

    [Fact]
    public void NavRail_Toggle_RaisesTheWidthAlongsideTheFlag()
    {
        var shell = Create();
        var raised = new List<string?>();
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        shell.ToggleNavRailCommand.Execute(null);

        Assert.Contains(nameof(MainWindowViewModel.IsNavRailCollapsed), raised);
        Assert.Contains(nameof(MainWindowViewModel.NavRailWidth), raised);
    }

    // ---- Phase 15B Task 5c: the one route to a Settings tab at a section -----------------------
    //
    // Spec 12.5's Guiding empty notice carries two links. The whole chain runs here: the notice's
    // command raises the page's OpenSettingsRequested, the shell switches on the destination, the
    // named tab is asked for its section, and the rail lands on Settings.
    //
    // Every tab but Library and Equipment is a factory that throws, so a route that built a tab it
    // does not land on fails this case rather than passing quietly (TRACKING section 6 item 16:
    // a tab is constructed on its first visit and never at page construction).

    private sealed class SettingsRouteHarness : IDisposable
    {
        private readonly LibraryTabViewModelTestFactory.Harness _libraryHarness
            = LibraryTabViewModelTestFactory.Create();

        private readonly TempDatabase _database = new("galactilog-settings-route");

        public SettingsRouteHarness()
        {
            var store = new SettingsStore(new SettingsRepository(_database.ConnectionString));
            Equipment = new EquipmentTabViewModel(
                store.GetEquipment,
                store.SaveEquipment,
                store.GetDismissedSuggestions,
                store.SaveDismissedSuggestions,
                () => [],
                () => [],
                post: action => action());
            Equipment.PendingLoad?.Wait(TimeSpan.FromSeconds(30));

            Page = new SettingsViewModel(
                () => Library,
                () => throw new InvalidOperationException("The Targets tab was built by a route that does not name it."),
                () => throw new InvalidOperationException("The Filters tab was built by a route that does not name it."),
                () => Equipment,
                () => throw new InvalidOperationException("The Maintenance tab was built by a route that does not name it."),
                () => throw new InvalidOperationException("The Location tab was built by a route that does not name it."),
                () => throw new InvalidOperationException("The Display tab was built by a route that does not name it."),
                () => throw new InvalidOperationException("The Storage tab was built by a route that does not name it."));
        }

        public LibraryTabViewModel Library => _libraryHarness.ViewModel;

        public EquipmentTabViewModel Equipment { get; }

        public SettingsViewModel Page { get; }

        public void Dispose()
        {
            Equipment.Dispose();
            _libraryHarness.Dispose();
            _database.Dispose();
        }
    }

    private MainWindowViewModel CreateWithSettings(SettingsViewModel settings, StatisticsViewModel statistics)
        => new(
            new GeneralSettings(),
            DashboardViewModelTestFactory.Create(),
            CreateStatusBar(),
            settings,
            () => statistics,
            CreateAnalysis,
            CreateActivity);

    private StatisticsViewModel CreateStatistics(StatsResponse response)
    {
        var page = StatisticsViewModelTestFactory.Create(loadStats: () => response);
        _statistics.Add(page);
        return page;
    }

    [Fact]
    public void TheGuidingNotice_WithNoGuideLogs_LandsOnTheLibraryTabsGuideLogSwitch()
    {
        using var tabs = new SettingsRouteHarness();
        var statistics = CreateStatistics(
            StatisticsViewModelTestFactory.Sample(guiding: StatisticsViewModelTestFactory.NoGuiding()));
        var shell = CreateWithSettings(tabs.Page, statistics);

        // Reading the rail entry is what builds the page and attaches the shell's subscription.
        shell.Selected = shell.Items.First(item => item.Key == "statistics");
        statistics.Guiding.OpenSettingsCommand.Execute(null);

        Assert.Equal("settings", shell.Selected.Key);
        Assert.Equal("library", tabs.Page.Selected.Key);
        Assert.True(tabs.Library.ConsumeGuideLogSwitchInViewRequest());

        // The other destination was not asked for, and no tab this route does not name was built.
        // The six factory tabs only: this surface's General, Diagnostics and About entries are
        // placeholders the Settings page constructs with itself, so IsConstructed says nothing
        // about them (SettingsViewModel's private constructor).
        Assert.False(tabs.Equipment.ConsumePhd2ProfilesInViewRequest());
        Assert.All(
            tabs.Page.Tabs.Where(tab => tab.Key
                is "targets" or "filters" or "maintenance" or "location" or "display" or "storage"),
            tab => Assert.False(tab.IsConstructed));
        shell.Dispose();
    }

    [Fact]
    public void TheGuidingNotice_WithUnmappedSessions_LandsOnTheEquipmentTabsPhd2Panel()
    {
        using var tabs = new SettingsRouteHarness();
        var statistics = CreateStatistics(
            StatisticsViewModelTestFactory.Sample(guiding: StatisticsViewModelTestFactory.NoGuiding(unmapped: 4)));
        var shell = CreateWithSettings(tabs.Page, statistics);

        shell.Selected = shell.Items.First(item => item.Key == "statistics");
        statistics.Guiding.OpenSettingsCommand.Execute(null);

        Assert.Equal("settings", shell.Selected.Key);
        Assert.Equal("equipment", tabs.Page.Selected.Key);
        Assert.True(tabs.Equipment.ConsumePhd2ProfilesInViewRequest());
        Assert.False(tabs.Library.ConsumeGuideLogSwitchInViewRequest());
        shell.Dispose();
    }

    // Review P3-3. Asserting only that nothing moved after Dispose cannot fail on a leaked
    // subscription, because the handler's own _disposed guard suppresses the effect: the case would
    // need BOTH the unsubscribe and the guard removed to go red, which is a case naming two
    // defects. The leak itself is what is asserted here, by reading the page event's invocation
    // list. The Statistics page is a rail page the shell does not own (BuildActivity's own comment
    // says a handler left on such a page outlives the shell), so the leak is a real one.
    private static int OpenSettingsSubscribers(StatisticsViewModel page)
    {
        var field = typeof(StatisticsViewModel).GetField(
            nameof(StatisticsViewModel.OpenSettingsRequested),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return ((Delegate?)field!.GetValue(page))?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void TheGuidingNotice_AfterTheShellIsDisposed_RoutesNowhere()
    {
        using var tabs = new SettingsRouteHarness();
        var statistics = CreateStatistics(
            StatisticsViewModelTestFactory.Sample(guiding: StatisticsViewModelTestFactory.NoGuiding()));
        var shell = CreateWithSettings(tabs.Page, statistics);
        shell.Selected = shell.Items.First(item => item.Key == "statistics");

        Assert.Equal(1, OpenSettingsSubscribers(statistics));

        shell.Dispose();

        Assert.Equal(0, OpenSettingsSubscribers(statistics));

        // And the guard behind the unsubscribe answers a raise that was already in flight.
        statistics.Guiding.OpenSettingsCommand.Execute(null);

        Assert.Equal("statistics", shell.Selected.Key);
        Assert.False(tabs.Library.ConsumeGuideLogSwitchInViewRequest());
    }

    // Phase 14B's Dashboard Review route, which shares the same body since Phase 15B Task 5c and
    // keeps its own event. It had no case of its own before this one.
    [Fact]
    public void TheDashboardReviewRoute_StillLandsOnTheLibraryTabsRuleEditor()
    {
        using var tabs = new SettingsRouteHarness();
        var dashboard = DashboardViewModelTestFactory.Create();
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateStatusBar(),
            tabs.Page,
            CreateStatistics,
            CreateAnalysis,
            CreateActivity);

        dashboard.ReviewScanFiltersCommand.Execute(null);

        Assert.Equal("settings", shell.Selected.Key);
        Assert.Equal("library", tabs.Page.Selected.Key);
        Assert.True(tabs.Library.ConsumeNameRulesInViewRequest());
        shell.Dispose();
    }
}
