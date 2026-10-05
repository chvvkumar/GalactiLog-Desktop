using System.Text.RegularExpressions;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.CustomColumns;

/// <summary>
/// Spec 12.15, ruling C22 with C32: a definition change reaches the surfaces that are already open,
/// through one mechanism. The repository's own half is
/// <c>CustomColumnRepositoryTests</c>; this file is the dashboard's half and the composition root's.
/// </summary>
/// <remarks>
/// The dashboard is the one surface that is always alive when a definition changes: reaching a
/// Settings tab disposes any open Target detail page, and the Settings tabs that read definitions
/// exist only if they have been visited. So the surfaces here are the filter panel's Custom section
/// and the list's own column entries, both reached by the one page-level call the host route makes.
/// </remarks>
public class DefinitionChangeRouteTests
{
    private static readonly TargetListingPage OneRow = new(
        [
            new TargetRow(
                GroupKey: "ngc-7000",
                TargetId: Guid.NewGuid(),
                Name: "NGC 7000",
                CommonName: "North America Nebula",
                CatalogId: "NGC 7000",
                ObjectType: "EN",
                ObjectCategory: "Nebula",
                IntegrationSeconds: 3_600d,
                FrameCount: 12,
                SessionCount: 1,
                FirstSession: new DateOnly(2026, 3, 14),
                LastSession: new DateOnly(2026, 3, 14),
                Palette: [],
                Equipment: [],
                Aliases: [],
                Sessions: []),
        ],
        1,
        3_600d,
        12,
        1,
        50);

    private sealed record Harness(
        DashboardViewModel Dashboard,
        Func<int> DefinitionReads,
        Func<int> Queries,
        Action<IReadOnlyList<CustomColumnDefinition>> SetColumns);

    // A dashboard with the custom column delegates wired and a definition list a case can change
    // under it, which is exactly what a create on the Settings tab does to the live application.
    private static async Task<Harness> BuildAsync(IReadOnlyList<CustomColumnDefinition> columns)
    {
        var definitions = columns;
        var definitionReads = 0;
        var queries = 0;

        var dashboard = new DashboardViewModel(
            _ =>
            {
                queries++;
                return OneRow;
            },
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            (_, _) => Task.CompletedTask,
            post: action => action(),
            loadCustomColumns: () =>
            {
                definitionReads++;
                return definitions;
            },
            loadTargetValues: _ => [],
            loadValuesForTarget: _ => [],
            writeCustomValue: (_, _, _) => CustomColumnTestFactory.Written);

        await dashboard.Filters.PendingReload!.WaitAsync(TimeSpan.FromSeconds(30));
        DashboardViewModelTestFactory.Settle(dashboard);

        return new Harness(
            dashboard,
            () => definitionReads,
            () => queries,
            next => definitions = next);
    }

    private static async Task SettleAsync(DashboardViewModel dashboard)
    {
        await dashboard.Filters.PendingReload!.WaitAsync(TimeSpan.FromSeconds(30));
        DashboardViewModelTestFactory.Settle(dashboard);
    }

    // Journey 1, the whole of it: a column created in Settings reaches the filter panel and the
    // column list of a dashboard that was built before it existed, with no scan and no restart.
    //
    // Red against a page with no RefreshCustomColumns at all, which is what the tree carried: the
    // panel keeps its seven sections, Columns keeps its six entries, and the definition read count
    // never moves off the one the construction took.
    [Fact]
    public async Task ARefresh_AddsTheCustomSectionAndTheColumnEntry_WithNoScan()
    {
        var harness = await BuildAsync([]);
        using var dashboard = harness.Dashboard;

        var sectionsBefore = dashboard.Filters.Sections.Count;
        var columnsBefore = dashboard.Targets.Columns.Count;
        var readsBefore = harness.DefinitionReads();
        var queriesBefore = harness.Queries();
        Assert.DoesNotContain("Custom", dashboard.Filters.Sections.Select(section => section.Title));

        harness.SetColumns([CustomColumnTestFactory.Boolean("Processed")]);
        dashboard.RefreshCustomColumns();
        await SettleAsync(dashboard);

        // The panel's eighth section, appended by the panel's own publish.
        Assert.Equal(sectionsBefore + 1, dashboard.Filters.Sections.Count);
        Assert.Contains("Custom", dashboard.Filters.Sections.Select(section => section.Title));

        // The list's own entry, off by default (user choice 2), which is what puts it in the gear
        // and in the Settings picker rather than straight on screen.
        Assert.Equal(columnsBefore + 1, dashboard.Targets.Columns.Count);
        var entry = dashboard.Targets.Columns[^1];
        Assert.Equal("Processed", entry.Title);
        Assert.False(entry.IsVisible);

        // Both halves really re-read, and the list really re-queried: neither surface is showing a
        // cached answer.
        Assert.True(harness.DefinitionReads() > readsBefore);
        Assert.True(harness.Queries() > queriesBefore);
    }

    // Journey 4's other direction, and the reason P1-1 had to land in the same wave: a delete
    // removes the section again and re-queries, so the rows on screen are the rows the criteria
    // describe.
    //
    // Red against a refresh that reloads the option lists and does not query: the section goes and
    // the query count stays where it was.
    [Fact]
    public async Task ARefreshAfterADelete_TakesTheSectionAwayAndReQueries()
    {
        var harness = await BuildAsync([CustomColumnTestFactory.Boolean("Processed")]);
        using var dashboard = harness.Dashboard;

        Assert.Contains("Custom", dashboard.Filters.Sections.Select(section => section.Title));
        var queriesBefore = harness.Queries();

        harness.SetColumns([]);
        dashboard.RefreshCustomColumns();
        await SettleAsync(dashboard);

        Assert.DoesNotContain("Custom", dashboard.Filters.Sections.Select(section => section.Title));
        Assert.True(harness.Queries() > queriesBefore);
    }

    // ---- the composition root's own half ------------------------------------------------------

    private static string AppHostSource()
        => File.ReadAllText(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs"));

    // Trap 1 and trap 2 of the journeys review, pinned where they can be read: the route posts, and
    // it does not go through the local function that drops StatsCache and AnalysisCache first. A
    // rename must not make the Statistics and Analysis pages re-run a full library pass for data
    // neither of them reads (ruling C32).
    //
    // Red against a route written as `Changed += (_, _) => RaiseDerivedDataChanged()` and red
    // against one that calls the dashboard straight from the raising thread: the first match fails
    // on the second needle, the second on the first.
    [Fact]
    public void TheHostRoute_PostsToTheUiThread_AndKeepsTheDerivedCaches()
    {
        var code = SourceScan.StripComments(AppHostSource());

        var route = Regex.Match(
            code,
            @"GetRequiredService<CustomColumnRepository>\(\)\.Changed \+= \(_, _\) =>\s*UiPost\.Default\(\(\) =>\s*serviceProvider\.GetRequiredService<DashboardViewModel>\(\)\.RefreshCustomColumns\(\)\);");

        Assert.True(
            route.Success,
            "The custom column route must post to the UI thread: the repository raises Changed on "
            + "whichever thread wrote, and the refresh touches collections the UI is bound to.");

        Assert.DoesNotContain(
            "CustomColumnRepository>().Changed += (_, _) => RaiseDerivedDataChanged",
            code,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CustomColumnRepository>().Changed += (_, _) => InvalidateDerivedCaches",
            code,
            StringComparison.Ordinal);
    }

    // Ruling C32's second half. The Display tab takes a subscribe and unsubscribe delegate pair,
    // wired at its own registration, so a tab nobody has visited subscribes nothing and is never
    // built by the handler. Resolving the tab inside a handler would build it, and its construction
    // reads four documents.
    //
    // Red against a host that attached a handler of its own by resolving the tab.
    [Fact]
    public void TheHostRoute_NeverResolvesTheDisplayTabToDeliverTheEvent()
    {
        var code = SourceScan.StripComments(AppHostSource());

        Assert.Contains(
            "subscribeCustomColumnsChanged: handler =>",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "unsubscribeCustomColumnsChanged: handler =>",
            code,
            StringComparison.Ordinal);

        // Every Changed subscription made from the host itself, and the one thing none of them may
        // resolve.
        foreach (var subscription in Regex.Matches(
            code, @"GetRequiredService<CustomColumnRepository>\(\)\.Changed \+=[^;]*;").Select(match => match.Value))
        {
            Assert.DoesNotContain("DisplayTabViewModel", subscription, StringComparison.Ordinal);
        }
    }

    // Journeys item 7. The reset truncates both tables without passing through the repository, so
    // the completed branch announces it; without this the dashboard keeps rows and cells for columns
    // that are gone and every toggle in one of them is refused.
    //
    // Red against the reset branch that only invalidates the derived caches, which is what the tree
    // carried.
    [Fact]
    public void ACompletedReset_AnnouncesTheDefinitionChange()
    {
        var code = SourceScan.StripComments(AppHostSource());

        var resetBranch = Regex.Match(
            code,
            @"GetRequiredService<DatabaseReset>\(\)\.Run\(cancellationToken\);\s*if \(outcome\.Status == DatabaseReset\.ResetStatus\.Completed\)\s*\{(?<body>[^}]*)\}");

        Assert.True(resetBranch.Success, "The reset's completed branch is no longer where AppHost.cs declares it.");
        Assert.Contains(
            "GetRequiredService<CustomColumnRepository>().NotifyDefinitionsChanged()",
            resetBranch.Groups["body"].Value,
            StringComparison.Ordinal);
    }
}
