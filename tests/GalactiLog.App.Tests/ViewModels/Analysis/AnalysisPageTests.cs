using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

/// <summary>
/// Spec 12.14's page shell: what the strip, the selection and the Matrix route do, and what the
/// page writes. The load loop itself is the base's and is pinned by <c>AnalysisTabBaseTests</c>;
/// nothing here re-asserts it.
/// </summary>
/// <remarks>
/// Every case drives the page with lambdas (ruling P1-1). There is no fake of
/// <c>AnalysisCache</c>: it is sealed with no interface and no virtual member, so the counters
/// below are three lines in this file.
/// </remarks>
public class AnalysisPageTests
{
    // One counter per tab query, so a case can say which tab ran and which did not.
    private sealed class Counts
    {
        public int Correlation;
        public int Distribution;
        public int BoxPlot;
        public int TimeSeries;
        public int Matrix;
        public int Compare;

        public int Total => Correlation + Distribution + BoxPlot + TimeSeries + Matrix + Compare;
    }

    private static AnalysisViewModel Page(
        Counts counts,
        DisplaySettings? display = null,
        Action<Func<DisplaySettings, DisplaySettings>>? writeDisplay = null)
        => AnalysisViewModelTestFactory.Create(
            correlation: (_, _, _) => { counts.Correlation++; return AnalysisViewModelTestFactory.Correlation(); },
            distribution: (_, _) => { counts.Distribution++; return AnalysisViewModelTestFactory.Distribution(); },
            boxPlot: (_, _, _) => { counts.BoxPlot++; return AnalysisViewModelTestFactory.Boxes(); },
            timeSeries: (_, _) => { counts.TimeSeries++; return AnalysisViewModelTestFactory.TimeSeries(); },
            matrix: _ => { counts.Matrix++; return AnalysisViewModelTestFactory.Matrix(); },
            compare: (_, _, _, _, _, _) => { counts.Compare++; return AnalysisViewModelTestFactory.Compare(); },
            display: display,
            writeDisplay: writeDisplay);

    private static void Settle(AnalysisViewModel page) => AnalysisViewModelTestFactory.Settle(page);

    [Fact]
    public void TheStripIsTheFiveTabsOfSpec1214_InOrder_WithTheirKeysTitlesAndHelpTopics()
    {
        var counts = new Counts();
        using var page = Page(counts);

        Assert.Equal(
            new[] { "correlation", "distributions", "timeseries", "matrix", "compare" },
            page.Tabs.Select(tab => tab.Key));
        Assert.Equal(
            new[] { "Correlation", "Distributions", "Time Series", "Matrix", "Compare" },
            page.Tabs.Select(tab => tab.Title));
        Assert.Equal(
            new[]
            {
                "analysis.correlation", "analysis.distributions", "analysis.timeseries",
                "analysis.matrix", "analysis.compare",
            },
            page.Tabs.Select(tab => tab.HelpTopicId));
        Assert.Equal("Analysis", page.Title);
    }

    [Fact]
    public void ConstructingThePage_BuildsNoBodyAndIssuesNoQueryOnAnyTab()
    {
        var counts = new Counts();
        using var page = Page(counts);

        // Spec 12.14's "A tab not yet visited": nothing is created and nothing is queried. The page
        // seeds the stored tab into its own field and writes no visibility, so the first query is
        // the view's own Activate.
        Assert.Equal(0, counts.Total);
        Assert.All(page.Tabs, tab => Assert.Null(tab.Body));
        Assert.All(page.Tabs, tab => Assert.Equal(AnalysisTabState.NotLoaded, tab.State));
    }

    [Fact]
    public void Activate_QueriesTheSelectedTabOnce_AndNoOther()
    {
        var counts = new Counts();
        using var page = Page(counts);

        page.Activate();
        Settle(page);

        Assert.Equal(1, counts.Correlation);
        Assert.Equal(1, counts.Total);
        Assert.NotNull(page.Tabs[0].Body);
        Assert.All(page.Tabs.Skip(1), tab => Assert.Null(tab.Body));

        // Idempotent: a detach and a re-attach redraw from the result the tab is already holding.
        page.Activate();
        Settle(page);
        Assert.Equal(1, counts.Total);
    }

    [Fact]
    public void TheStoredTabIsSelected_AndAnUnknownOneOpensOnCorrelation()
    {
        var counts = new Counts();
        using var stored = Page(counts, new DisplaySettings { Analysis = new AnalysisDisplaySettings { Tab = "matrix" } });
        Assert.Equal("matrix", stored.SelectedTab.Key);

        // The cache's own query vocabulary is NOT this one: "boxplot" is no tab, and reading it as
        // a tab would open the page on Correlation on every launch with no case noticing.
        using var junk = Page(counts, new DisplaySettings { Analysis = new AnalysisDisplaySettings { Tab = "boxplot" } });
        Assert.Equal("correlation", junk.SelectedTab.Key);
    }

    [Fact]
    public void SelectingATab_WritesOnlyTheTabKey_AndQueriesThatTabOnce()
    {
        var counts = new Counts();
        var document = new DisplaySettings();
        var targetPage = document.TargetPage;
        var columns = document.Columns;
        using var page = Page(counts, document, mutate => document = mutate(document));

        page.Activate();
        Settle(page);
        page.SelectedTab = page.Tabs[3];
        Settle(page);

        Assert.Equal("matrix", document.Analysis.Tab);

        // Ruling Q17: the write clobbers none of the other three keys, nor target_page, nor
        // columns. Asserted by identity, because the mutation is a nested `with` over the document
        // as loaded inside the queued write and every untouched member must come through it
        // unrebuilt.
        Assert.Equal("humidity", document.Analysis.XMetric);
        Assert.Equal("hfr", document.Analysis.YMetric);
        Assert.Equal("frame", document.Analysis.Granularity);
        Assert.Same(targetPage, document.TargetPage);
        Assert.Same(columns, document.Columns);

        Assert.Equal(1, counts.Matrix);
        Assert.False(page.Tabs[0].IsVisible);
        Assert.True(page.Tabs[3].IsVisible);
    }

    [Fact]
    public void ReturningToACleanTab_QueriesNothing()
    {
        var counts = new Counts();
        using var page = Page(counts);

        page.Activate();
        Settle(page);
        page.SelectedTab = page.Tabs[2];
        Settle(page);
        page.SelectedTab = page.Tabs[0];
        Settle(page);

        // Spec 12.14: a tab that is current and clean re-renders nothing. One query each, ever.
        Assert.Equal(1, counts.Correlation);
        Assert.Equal(1, counts.TimeSeries);
        Assert.Equal(2, counts.Total);
    }

    [Fact]
    public void AFilterChange_MarksEveryTabStale_AndRefreshesOnlyTheSelectedOne()
    {
        var counts = new Counts();
        using var page = Page(counts);

        page.Activate();
        Settle(page);
        page.SharedFilter.SelectedFilter = "Ha";
        Settle(page);

        // The whole of "switching the equipment combination does not fire five queries".
        Assert.Equal(2, counts.Correlation);
        Assert.Equal(2, counts.Total);
        Assert.All(page.Tabs.Skip(1), tab => Assert.True(tab.IsStale));

        // And the hidden tab refreshes when it is next selected, once.
        page.SelectedTab = page.Tabs[2];
        Settle(page);
        Assert.Equal(1, counts.TimeSeries);
    }

    [Fact]
    public void AReversedRange_QueriesNothingOnAnyTab_AndLeavesTheLastResultDrawn()
    {
        var counts = new Counts();
        using var page = Page(counts);

        page.Activate();
        Settle(page);
        var drawn = page.Tabs[0].Body;

        // One end alone is a legal one-sided bound, so that edit refreshes like any other; the
        // range only becomes reversed on the second.
        page.SharedFilter.DateTo = new DateTimeOffset(new DateTime(2025, 6, 1), TimeSpan.Zero);
        Settle(page);
        page.SharedFilter.DateFrom = new DateTimeOffset(new DateTime(2025, 6, 10), TimeSpan.Zero);
        Settle(page);
        var reversed = counts.Total;

        page.SelectedTab = page.Tabs[1];
        Settle(page);
        page.SelectedTab = page.Tabs[2];
        Settle(page);

        // DEPARTURE from the web, recorded rather than mirrored: AnalysisPage.tsx lines 83 to 90
        // pass undefined for both dates and the tabs' queries STILL FIRE over the whole library
        // while the error sentence is on screen. Spec 12.14 says "runs nothing" and is approved.
        Assert.True(page.SharedFilter.HasError);
        Assert.Null(page.SharedFilter.Current);
        Assert.Equal(reversed, counts.Total);
        Assert.Equal(0, counts.Distribution);
        Assert.Equal(0, counts.TimeSeries);
        Assert.Same(drawn, page.Tabs[0].Body);
        Assert.Equal(AnalysisTabState.Ready, page.Tabs[0].State);
    }

    [Fact]
    public void OpenCorrelationOn_WritesBothMetricKeys_AndSelectsTheCorrelationTab()
    {
        var counts = new Counts();
        var document = new DisplaySettings();
        using var page = Page(counts, document, mutate => document = mutate(document));

        page.Activate();
        Settle(page);
        page.SelectedTab = page.Tabs[3];
        Settle(page);

        page.OpenCorrelationOn(AnalysisMetric.DewPoint, AnalysisMetric.Eccentricity);
        Settle(page);

        // This case pins only what this task owns: the two display.analysis writes and the tab
        // selection. The Correlation pickers are CorrelationTabViewModel's and are Task 5's case.
        Assert.Equal("dew_point", document.Analysis.XMetric);
        Assert.Equal("eccentricity", document.Analysis.YMetric);
        Assert.Equal("correlation", document.Analysis.Tab);
        Assert.Same(page.Tabs[0], page.SelectedTab);
        Assert.True(page.Tabs[0].IsVisible);
    }

    [Fact]
    public void OpenCorrelationOn_WhileCorrelationIsAlreadyCurrent_StillRequeries()
    {
        var counts = new Counts();
        using var page = Page(counts);

        page.Activate();
        Settle(page);
        Assert.Equal(1, counts.Correlation);

        page.OpenCorrelationOn(AnalysisMetric.Pressure, AnalysisMetric.Fwhm);
        Settle(page);

        // A click that lands on the tab already showing changes no selection, so without the
        // explicit refresh the stale mark would sit there unanswered and the grid's own pair would
        // never be drawn.
        Assert.Equal(2, counts.Correlation);
        Assert.False(page.Tabs[0].IsStale);
    }

    [Fact]
    public void TheMatrixTab_CarriesThePagesOwnCorrelationRoute()
    {
        var counts = new Counts();
        var document = new DisplaySettings();
        using var page = Page(counts, document, mutate => document = mutate(document));

        // Ruling B9: the route reaches the Matrix as a constructor delegate and never as a page
        // reference, which is what lets Task 5 fill that file without opening AnalysisViewModel.cs.
        var matrix = Assert.IsType<MatrixTabViewModel>(page.Tabs[3]);
        matrix.OpenCorrelationOn(AnalysisMetric.Airmass, AnalysisMetric.DetectedStars);
        Settle(page);

        Assert.Equal("airmass", document.Analysis.XMetric);
        Assert.Equal("detected_stars", document.Analysis.YMetric);
        Assert.Same(page.Tabs[0], page.SelectedTab);
    }

    [Fact]
    public void TheGranularitySegment_WritesItsOwnKey_AndRefreshesTheSelectedTab()
    {
        var counts = new Counts();
        var document = new DisplaySettings();
        using var page = Page(counts, document, mutate => document = mutate(document));

        page.Activate();
        Settle(page);
        page.SharedFilter.Granularity = AnalysisGranularity.Session;
        Settle(page);

        Assert.Equal("session", document.Analysis.Granularity);
        Assert.Equal("correlation", document.Analysis.Tab);
        Assert.Equal(2, counts.Correlation);
    }

    [Fact]
    public void TheStoredGranularityAndMetrics_SeedTheBarAndTheCorrelationTab()
    {
        var counts = new Counts();
        using var page = Page(counts, new DisplaySettings
        {
            Analysis = new AnalysisDisplaySettings
            {
                Granularity = "SESSION",
                XMetric = "phd2_rms_total",

                // Spec 5.8.2 names this one: a PHD2 key stored in y_metric is a value the tab
                // would reject at query time, so it reads as hfr.
                YMetric = "phd2_rms_ra",
            },
        });

        Assert.Equal(AnalysisGranularity.Session, page.SharedFilter.Granularity);
        var correlation = Assert.IsType<CorrelationTabViewModel>(page.Tabs[0]);
        Assert.Equal(AnalysisMetric.Phd2RmsTotal, correlation.XMetric);
        Assert.Equal(AnalysisMetric.Hfr, correlation.YMetric);
    }

    // ---- the bar's list read is started last, and by the page ------------------------------------

    // The defect: SharedFilterViewModel started its one list read in its own constructor, which is
    // the FIRST statement of the page's constructor, and the page then built CompareTabViewModel
    // out of the bar's two collections while that read could publish into them from its own
    // thread-pool thread. Spine B saw the throw once, out of
    // TheStoredGranularityAndMetrics_SeedTheBarAndTheCorrelationTab above: "Collection was
    // modified" from CompareTabViewModel.Desired.
    //
    // WHY THIS CASE IS STRUCTURAL AND NOT TIMED. No assertion about when a Task.Run body is
    // entered can be deterministic in both directions: the pool thread may start at once or after
    // the constructor has long returned, so "the tabs already existed when the delegate ran" is
    // guaranteed under the fix and merely likely under the defect, which is the flake itself
    // rather than a test for it. The ordering is therefore pinned where it is decided, in the two
    // source files, in the idiom this assembly already uses for a rule that has no runtime
    // witness. The runtime half below still pins that the read is started, exactly once, and
    // lands.
    [Fact]
    public void ThePageStartsTheBarsListRead_ExactlyOnce_AndOnlyAfterEveryTabExists()
    {
        var reads = 0;
        using var page = AnalysisViewModelTestFactory.Create(
            loadEquipment: () =>
            {
                Interlocked.Increment(ref reads);
                return [AnalysisViewModelTestFactory.Combination()];
            });

        Settle(page);

        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Equal(2, page.SharedFilter.EquipmentChoices.Count);

        var analysis = Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Analysis");
        var bar = SourceScan.StripComments(
            File.ReadAllText(Path.Combine(analysis, "SharedFilterViewModel.cs")));
        var shell = SourceScan.StripComments(
            File.ReadAllText(Path.Combine(analysis, "AnalysisViewModel.cs")));

        // The bar's constructor schedules nothing and starts nothing. The one Task.Run in that file
        // belongs to Load, which is declared below the constructor, and the constructor does not
        // call Load either: both spellings put the read back in front of the five tabs.
        var constructor = bar.IndexOf("public SharedFilterViewModel(", StringComparison.Ordinal);
        var load = bar.IndexOf("public void Load()", StringComparison.Ordinal);
        Assert.True(constructor >= 0 && load > constructor, "SharedFilterViewModel has no Load below its constructor.");
        Assert.DoesNotContain("Task.Run", bar[constructor..load], StringComparison.Ordinal);
        Assert.DoesNotContain("Load()", bar[constructor..load], StringComparison.Ordinal);

        // And the page calls it below the five tabs, below the seeded selection and below the
        // Changed subscription, as the LAST statement of its own constructor.
        const string call = "SharedFilter.Load();";
        var called = shell.IndexOf(call, StringComparison.Ordinal);
        Assert.True(called > shell.IndexOf("new CompareTabViewModel(", StringComparison.Ordinal));
        Assert.True(called > shell.IndexOf("_selectedTab = Tabs.First(", StringComparison.Ordinal));
        Assert.True(called > shell.IndexOf("SharedFilter.Changed += OnFilterChanged;", StringComparison.Ordinal));

        // Nothing but the constructor's closing brace follows it.
        Assert.Equal('}', shell[(called + call.Length)..].TrimStart()[0]);
    }

    // ---- the host's refresh notification ---------------------------------------------------------

    /// <summary>
    /// Phase review P1-A. The page follows the one process-level notification raised after the
    /// derived memos have been dropped: the bar re-reads both option lists, every tab is marked
    /// stale and the selected one refreshes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure looks like the reader opening Analysis, scanning a night taken with a second rig
    /// or mapping a PHD2 profile in Settings, coming back, and finding the new rig in neither the
    /// equipment picker nor Compare's two group pickers, over the figures read before the scan,
    /// with nothing on screen saying so and no way out but restarting the application.
    /// </para>
    /// <para>
    /// The Compare assertion is here rather than in <c>CompareTabTests</c> on purpose: that tab
    /// follows the bar's two collection INSTANCES through its own <c>CollectionChanged</c>
    /// subscription, so it is the page's re-read that has to reach it, and this case proves the
    /// whole route without editing that file.
    /// </para>
    /// <para>
    /// Seen red against the page taking no such pair, which is the tree this fix landed on: the
    /// equipment list keeps its two rows, Compare offers one group, no tab is stale and the
    /// correlation counter stays at 1.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADerivedDataNotification_ReReadsTheBar_MarksEveryTabStale_AndRefreshesOnlyTheSelectedOne()
    {
        var derived = new DerivedDataSource();
        var counts = new Counts();
        var equipment = new List<EquipmentCombination> { AnalysisViewModelTestFactory.Combination() };
        var page = AnalysisViewModelTestFactory.Create(
            loadEquipment: () => [.. equipment],
            correlation: (_, _, _) => { counts.Correlation++; return AnalysisViewModelTestFactory.Correlation(); },
            distribution: (_, _) => { counts.Distribution++; return AnalysisViewModelTestFactory.Distribution(); },
            boxPlot: (_, _, _) => { counts.BoxPlot++; return AnalysisViewModelTestFactory.Boxes(); },
            timeSeries: (_, _) => { counts.TimeSeries++; return AnalysisViewModelTestFactory.TimeSeries(); },
            matrix: _ => { counts.Matrix++; return AnalysisViewModelTestFactory.Matrix(); },
            compare: (_, _, _, _, _, _) => { counts.Compare++; return AnalysisViewModelTestFactory.Compare(); },
            derivedData: derived);

        page.Activate();
        Settle(page);
        Assert.Equal(1, counts.Correlation);
        Assert.Equal(2, page.SharedFilter.EquipmentChoices.Count);

        // The scan, the alias edit or the profile mapping that puts a second optical train in the
        // library, and the host's notification that follows the memo drop.
        equipment.Add(AnalysisViewModelTestFactory.Combination("Askar FMA180", "ASI533MC", grouped: true));
        derived.Raise();
        Settle(page);

        Assert.Equal(
            new[] { "All equipment", "RC8 + ASI2600MM", "Askar FMA180 + ASI533MC" },
            page.SharedFilter.EquipmentChoices.Select(choice => choice.Label));

        // The same rule a filter move follows: every tab stale, one query.
        Assert.Equal(2, counts.Correlation);
        Assert.Equal(2, counts.Total);
        Assert.All(page.Tabs.Skip(1), tab => Assert.True(tab.IsStale));

        // And the new rig is offered as a Compare group, which is the half a snapshot would lose.
        var compare = Assert.IsType<CompareTabViewModel>(page.Tabs[4]);
        Assert.Contains(compare.GroupChoices, choice => choice.Label == "Askar FMA180 + ASI533MC");

        // The hidden tab refreshes when it is next selected, once.
        page.SelectedTab = page.Tabs[2];
        Settle(page);
        Assert.Equal(1, counts.TimeSeries);

        page.Dispose();

        // The notifier outlives every page it speaks to, so the unfollow is the half that matters.
        Assert.False(derived.HasFollower);
        derived.Raise();
        Settle(page);
        Assert.Equal(3, counts.Total);
    }

    [Fact]
    public void Dispose_StopsEveryTabPublishing()
    {
        var counts = new Counts();
        var page = Page(counts);

        page.Activate();
        Settle(page);
        page.Dispose();

        page.SharedFilter.SelectedFilter = "Ha";
        Settle(page);

        // The bar's subscription is dropped and every tab is disposed, so a filter edit after the
        // page has gone starts nothing.
        Assert.Equal(1, counts.Total);
    }
}
