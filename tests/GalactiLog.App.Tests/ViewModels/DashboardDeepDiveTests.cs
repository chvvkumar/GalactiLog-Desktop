using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using DetailFactory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 6: spec 12.2's Deep dive action (PAR-010). TargetListViewModel.DeepDiveCommand
// raises the widened TargetOpened event (task6.md 6.2); MainWindowViewModel.OnTargetOpened turns
// it into MainWindowViewModel.OpenDetail's two-argument call, which Phase 14A's nine
// TargetDetailDeepLinkTests cases already cover on arrival. This file is the missing link: the
// event payload, and the two existing routes that must keep passing no date.
public class DashboardDeepDiveTests : IDisposable
{
    private readonly List<TargetDetailViewModel> _detail = [];
    private readonly List<StatisticsViewModel> _statistics = [];
    private readonly List<ActivityViewModel> _activity = [];
    private readonly List<AnalysisViewModel> _analysis = [];

    public void Dispose()
    {
        foreach (var page in _detail)
        {
            page.Dispose();
        }

        foreach (var page in _statistics)
        {
            page.Dispose();
        }

        foreach (var page in _activity)
        {
            page.Dispose();
        }

        foreach (var page in _analysis)
        {
            page.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ---- the view-model: TargetListViewModel.DeepDiveCommand ------------------------------

    private static TargetListViewModel CreateList()
    {
        var display = new DisplaySettings();
        return new TargetListViewModel(display, () => display, _ => { }, 50);
    }

    private static TargetRow Row(string groupKey, IReadOnlyList<SessionSummary> sessions)
        => new(
            GroupKey: groupKey,
            TargetId: null,
            Name: "M 31",
            CommonName: null,
            CatalogId: null,
            ObjectType: null,
            ObjectCategory: "Galaxy",
            IntegrationSeconds: 0,
            FrameCount: 0,
            SessionCount: sessions.Count,
            FirstSession: sessions.Count > 0 ? sessions[^1].SessionDate : null,
            LastSession: sessions.Count > 0 ? sessions[0].SessionDate : null,
            Palette: [],
            Equipment: [],
            Aliases: [],
            Sessions: sessions);

    [Fact]
    public void DeepDive_RaisesTheEventWithTheGroupKeyAndTheLinesDate()
    {
        var list = CreateList();
        var date = new DateOnly(2025, 12, 7);
        list.Load(new TargetListingPage(
            [Row("10000000-0000-0000-0000-000000000000", [new SessionSummary(date, 10, 3_000d)])], 1, 0, 0, 1, 50));

        var opened = new List<TargetOpenRequest>();
        list.TargetOpened += (_, request) => opened.Add(request);

        list.DeepDiveCommand.Execute(list.Rows[0].Sessions[0]);

        var request = Assert.Single(opened);
        Assert.Equal("10000000-0000-0000-0000-000000000000", request.GroupKey);
        Assert.Equal(date, request.SessionDate);
    }

    [Fact]
    public void DeepDive_PassesNothingElse()
    {
        // TargetOpenRequest carries exactly the group key and the date: a reflection check that a
        // third member cannot sneak in unnoticed (task6.md 6.2).
        var properties = typeof(TargetOpenRequest).GetProperties();
        Assert.Equal(2, properties.Length);
        Assert.Contains(properties, property => property.Name == nameof(TargetOpenRequest.GroupKey));
        Assert.Contains(properties, property => property.Name == nameof(TargetOpenRequest.SessionDate));
    }

    [Fact]
    public void DeepDive_OnAnUnresolvedObjGroup_PassesTheObjKey()
    {
        var list = CreateList();
        var date = new DateOnly(2025, 12, 7);
        list.Load(new TargetListingPage(
            [Row("obj:Bubble Neb", [new SessionSummary(date, 10, 3_000d)])], 1, 0, 0, 1, 50));

        var opened = new List<TargetOpenRequest>();
        list.TargetOpened += (_, request) => opened.Add(request);

        list.DeepDiveCommand.Execute(list.Rows[0].Sessions[0]);

        Assert.Equal("obj:Bubble Neb", Assert.Single(opened).GroupKey);
    }

    [Fact]
    public void DeepDiveCommand_ExecutedPastCanExecute_WithANullRow_DoesNothing()
    {
        // TRACKING item 13: RelayCommand.Execute ignores CanExecute; the guard is the body's.
        var list = CreateList();
        var opened = new List<TargetOpenRequest>();
        list.TargetOpened += (_, request) => opened.Add(request);

        list.DeepDiveCommand.Execute(null);

        Assert.Empty(opened);
    }

    // ---- the shell route --------------------------------------------------------------------

    private StatisticsViewModel CreateStatistics()
    {
        var page = StatisticsViewModelTestFactory.Create();
        _statistics.Add(page);
        return page;
    }

    private ActivityViewModel CreateActivity()
    {
        var page = ActivityViewModelTestFactory.Create();
        _activity.Add(page);
        return page;
    }

    // Phase 17 Task 4: the sixth rail destination. A lazy factory like the two above, and the pages
    // it builds are disposed with them so no tab load outlives the test.
    private AnalysisViewModel CreateAnalysis()
    {
        var page = AnalysisViewModelTestFactory.Create();
        _analysis.Add(page);
        return page;
    }

    private static StatusBarViewModel CreateStatusBar()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        return new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel);
    }

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
        Sessions:
        [
            new SessionSummary(DetailFactory.LastSession, 88, 26_400d),
            new SessionSummary(DetailFactory.FirstSession, 60, 18_240d),
        ]);

    private (MainWindowViewModel Shell, DashboardViewModel Dashboard, List<(string GroupKey, DateOnly? SessionDate)> Routes)
        CreateShell()
    {
        List<(string GroupKey, DateOnly? SessionDate)> routes = [];
        var dashboard = DashboardViewModelTestFactory.Create(
            list: _ => new TargetListingPage([SampleRow], 1, 44_640d, 148, 1, 50));
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: (groupKey, sessionDate) =>
            {
                routes.Add((groupKey, sessionDate));
                var page = DetailFactory.Create(groupKey: groupKey, initialSessionDate: sessionDate).Settle().ViewModel;
                _detail.Add(page);
                return page;
            });

        return (shell, dashboard, routes);
    }

    [Fact]
    public void TheShell_OpensTheDetailOnThatNight()
    {
        var (shell, dashboard, routes) = CreateShell();
        using var _ = shell;

        // The older session line: its own date, not the row's last session.
        dashboard.Targets.DeepDiveCommand.Execute(dashboard.Targets.Rows[0].Sessions[1]);

        var route = Assert.Single(routes);
        Assert.Equal(DetailFactory.ResolvedGroupKey, route.GroupKey);
        Assert.Equal(DetailFactory.FirstSession, route.SessionDate);

        var page = Assert.IsType<TargetDetailViewModel>(shell.Detail);
        Assert.Equal(DetailFactory.FirstSession, page.SelectedSession?.SessionDate);
    }

    [Fact]
    public void ARowClick_StillOpensWithNoDate()
    {
        var (shell, dashboard, routes) = CreateShell();
        using var _ = shell;

        dashboard.Targets.OpenTargetCommand.Execute(dashboard.Targets.Rows[0]);

        var route = Assert.Single(routes);
        Assert.Equal(DetailFactory.ResolvedGroupKey, route.GroupKey);
        Assert.Null(route.SessionDate);

        var page = Assert.IsType<TargetDetailViewModel>(shell.Detail);
        Assert.Equal(DetailFactory.LastSession, page.SelectedSession?.SessionDate);
    }

    [Fact]
    public void TheMergedAwayCalloutRoute_StillOpensWithNoDate()
    {
        // Phase 7 Task 5, ruling Q12: the merged-away callout's "Open <winner>" route, unaffected
        // by the widened TargetOpened payload, still passes null.
        var winnerId = Guid.Parse("40000000-0000-0000-0000-000000000000");
        List<(string GroupKey, DateOnly? SessionDate)> routes = [];
        var dashboard = DashboardViewModelTestFactory.Create(
            list: _ => new TargetListingPage([SampleRow], 1, 44_640d, 148, 1, 50));
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            CreateStatusBar(),
            TabFactory.CreateSettingsPage(),
            CreateStatistics,
            CreateAnalysis,
            CreateActivity,
            openDetail: (groupKey, sessionDate) =>
            {
                routes.Add((groupKey, sessionDate));
                var harness = groupKey == DetailFactory.ResolvedGroupKey
                    ? DetailFactory.Create(
                        get: _ => null,
                        groupKey: groupKey,
                        mergedInto: (winnerId, "M 31", "NGC 224"),
                        initialSessionDate: sessionDate)
                    : DetailFactory.Create(groupKey: groupKey, initialSessionDate: sessionDate);
                var page = harness.Settle().ViewModel;
                _detail.Add(page);
                return page;
            });
        using var _ = shell;

        dashboard.Targets.OpenTargetCommand.Execute(dashboard.Targets.Rows[0]);
        var first = Assert.Single(routes);
        Assert.Null(first.SessionDate);

        var firstPage = Assert.IsType<TargetDetailViewModel>(shell.Detail);
        Assert.True(firstPage.IsMergedAway);

        firstPage.OpenMergedIntoCommand.Execute(null);

        Assert.Equal(2, routes.Count);
        Assert.Null(routes[1].SessionDate);
    }
}
