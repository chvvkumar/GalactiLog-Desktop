using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14A Task 6: spec 12.4's "Opening the page" block (PAR-018). There is no launched-app
// route until Phase 14B wires the dashboard's session rows, so the headless case is the proof for
// this phase, which is the roadmap's own sentence.
public class TargetDetailDeepLinkTests : IDisposable
{
    private readonly List<StatisticsViewModel> _statistics = [];
    private readonly List<ActivityViewModel> _activity = [];
    private readonly List<AnalysisViewModel> _analysis = [];

    public void Dispose()
    {
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

        _statistics.Clear();
        _activity.Clear();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void OpenWithNoDate_SelectsTheNewestNight()
    {
        // Every route that exists today, and unchanged behaviour: the overview list is newest
        // first, so the newest night is Sessions[0].
        using var harness = Factory.Create().Settle();

        Assert.Equal(Factory.LastSession, harness.ViewModel.SelectedSession?.SessionDate);
        Assert.Null(harness.ViewModel.LedgerScrollTarget);
    }

    [Fact]
    public void OpenWithADate_SelectsThatNight()
    {
        using var harness = Factory.Create(initialSessionDate: Factory.FirstSession).Settle();

        Assert.Equal(Factory.FirstSession, harness.ViewModel.SelectedSession?.SessionDate);
    }

    [Fact]
    public void OpenWithADateTheTargetDoesNotHave_FallsBackToTheNewest()
    {
        using var harness = Factory.Create(initialSessionDate: new DateOnly(1999, 1, 1)).Settle();

        Assert.Equal(Factory.LastSession, harness.ViewModel.SelectedSession?.SessionDate);
    }

    [Fact]
    public void OpenWithADateTheTargetDoesNotHave_ShowsNoError()
    {
        // Spec 12.4: no error is shown, because the caller is another surface of this same
        // application and a stale date there is not the user's mistake.
        using var harness = Factory.Create(initialSessionDate: new DateOnly(1999, 1, 1)).Settle();

        Assert.Null(harness.ViewModel.LastFailure);
        Assert.False(harness.ViewModel.IsMissing);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Null(harness.ViewModel.LedgerScrollTarget);
    }

    [Fact]
    public void OpenWithADate_ScrollsTheLedgerToIt()
    {
        // The seam only. ScrollIntoView is a control call, so the view subscribes to this and
        // scrolls; the shape is FrameTableViewModel.HighlightedRow's (P13 R9).
        using var harness = Factory.Create(initialSessionDate: Factory.FirstSession).Settle();

        Assert.Same(harness.ViewModel.SelectedSession, harness.ViewModel.LedgerScrollTarget);
        Assert.Equal(Factory.FirstSession, harness.ViewModel.LedgerScrollTarget?.SessionDate);
    }

    [Fact]
    public void OpenWithADate_IssuesExactlyOneSessionDetailQuery()
    {
        // Spec 12.4: exactly one query runs for the page's first paint. Opening on an older night
        // must not expand the newest one on the way.
        using var harness = Factory.Create(initialSessionDate: Factory.FirstSession).Settle();
        harness.SettleCards();

        var query = Assert.Single(harness.SessionQueries);
        Assert.Equal(Factory.FirstSession, query.SessionDate);
        Assert.Equal(Factory.ResolvedGroupKey, query.GroupKey);
    }

    [Fact]
    public async Task AReloadAfterADeepLink_KeepsTheNightTheUserIsOn()
    {
        // The "consumed once" rule: open on a date, move to another night, fire a scan-finished
        // reload, and the page stays where the user put it.
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var scanStatus = new ScanStatusService(coordinator, action => action());

        using var harness = Factory
            .Create(scanStatus: scanStatus, initialSessionDate: Factory.FirstSession)
            .Settle();
        Assert.Equal(Factory.FirstSession, harness.ViewModel.SelectedSession?.SessionDate);

        harness.ViewModel.SelectedSession = harness.ViewModel.Sessions
            .Single(card => card.SessionDate == Factory.LastSession);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        harness.Settle();

        Assert.Equal(2, harness.Loads);
        Assert.Equal(Factory.LastSession, harness.ViewModel.SelectedSession?.SessionDate);
    }

    // ---- the shell route ---------------------------------------------------------------------

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

    private (MainWindowViewModel Shell, List<(string GroupKey, DateOnly? SessionDate)> Routes) CreateShell()
    {
        List<(string GroupKey, DateOnly? SessionDate)> routes = [];
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
                routes.Add((groupKey, sessionDate));
                return Factory
                    .Create(groupKey: groupKey, initialSessionDate: sessionDate)
                    .Settle()
                    .ViewModel;
            });

        return (shell, routes);
    }

    [Fact]
    public void TheShellRoute_PassesTheDateThrough()
    {
        var (shell, routes) = CreateShell();
        using var _ = shell;

        shell.OpenDetail(Factory.ResolvedGroupKey, Factory.FirstSession);

        var route = Assert.Single(routes);
        Assert.Equal(Factory.ResolvedGroupKey, route.GroupKey);
        Assert.Equal(Factory.FirstSession, route.SessionDate);

        var page = Assert.IsType<TargetDetailViewModel>(shell.Detail);
        Assert.Equal(Factory.FirstSession, page.SelectedSession?.SessionDate);
    }

    [Fact]
    public void TheShellRoute_WithNoDate_BehavesExactlyAsBefore()
    {
        var (shell, routes) = CreateShell();
        using var _ = shell;

        shell.OpenDetail(Factory.ResolvedGroupKey);

        var route = Assert.Single(routes);
        Assert.Null(route.SessionDate);

        var page = Assert.IsType<TargetDetailViewModel>(shell.Detail);
        Assert.Equal(Factory.LastSession, page.SelectedSession?.SessionDate);
        Assert.Null(page.LedgerScrollTarget);
    }
}
