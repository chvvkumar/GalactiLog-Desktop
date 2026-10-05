using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

// Task 4 section 4.4 and 6: the one tab base (ruling A7) and the view-model half of the mixed
// plate scale warning (user ruling U3). Five tabs is one pattern five times, and these cases pin
// the pattern rather than any one tab: every case below drives the base directly through a test
// subclass, so a tab that re-implements the loop fails none of them and is caught by review
// instead.
//
// The post seam is a list rather than the dispatcher, which is what makes the publish half
// observable: an action sits in the list until the case drains it, so "a refresh keeps the last
// result" and "a stale generation does not publish" are ordinary assertions and not a race.
public class AnalysisTabBaseTests
{
    private static readonly AnalysisFilter AnyFilter =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    // ---- the test double -------------------------------------------------------------------

    private sealed class TestBody
    {
        public int Value { get; set; }
    }

    private sealed record TestResult(int Value, int PlateScales = 0, bool NoRows = false);

    private sealed class TestTab(
        string key,
        Func<AnalysisFilter, TestResult> query,
        Func<AnalysisFilter?> filter,
        Action<Action> post)
        : AnalysisTabViewModel<TestResult>(key, key, $"analysis.{key}", filter, post)
    {
        public int QueryCount { get; private set; }

        public int BodyCount { get; private set; }

        public bool PixelMetric { get; set; }

        // Compare's shape: a tab whose own controls name no query yet.
        public bool Queryable { get; set; } = true;

        public int CannotQueryCount { get; private set; }

        // A mapper that throws, which runs on the publish side and not inside the query's try.
        public bool ThrowOnMap { get; set; }

        // The one state a mapper alone can reach on this double, for the ShowsResult case.
        public bool TooFewOnNextMap { get; set; }

        public TestBody? TestBodyValue => (TestBody?)Body;

        protected override bool IsPixelMetric => PixelMetric;

        protected override bool CanQuery => Queryable;

        protected override string EmptyMessage => $"{Key}: no frames match the current filters.";

        protected override void PublishCannotQuery()
        {
            CannotQueryCount++;
            State = AnalysisTabState.Empty;
        }

        protected override object CreateBody()
        {
            BodyCount++;
            return new TestBody();
        }

        protected override TestResult? Query(AnalysisFilter filter)
        {
            QueryCount++;
            return query(filter);
        }

        protected override AnalysisTabState Map(TestResult? result)
        {
            if (ThrowOnMap)
            {
                throw new InvalidOperationException("the mapper failed");
            }

            if (TooFewOnNextMap)
            {
                return AnalysisTabState.TooFew;
            }

            if (result is null || result.NoRows)
            {
                return AnalysisTabState.Empty;
            }

            ((TestBody)Body!).Value = result.Value;
            DistinctPlateScales = result.PlateScales;
            return AnalysisTabState.Ready;
        }
    }

    // Spec 12.14's "When it is shown" for Correlation: the Y metric is hfr, whatever X is. The
    // plate scale cases drive this shape because the Y-only rule is the arm a port gets wrong.
    private sealed class CorrelationLikeTab(Func<AnalysisFilter?> filter, Action<Action> post)
        : AnalysisTabViewModel<TestResult>("correlation", "Correlation", "analysis.correlation", filter, post)
    {
        public AnalysisMetric X { get; set; } = AnalysisMetric.Humidity;

        public AnalysisMetric Y { get; set; } = AnalysisMetric.Hfr;

        public int Scales { get; set; }

        protected override bool IsPixelMetric => Y == AnalysisMetric.Hfr;

        protected override string EmptyMessage => "No frames match the current filters.";

        protected override object CreateBody() => new TestBody();

        protected override TestResult? Query(AnalysisFilter filter) => new(1, Scales);

        protected override AnalysisTabState Map(TestResult? result)
        {
            DistinctPlateScales = result!.PlateScales;
            return AnalysisTabState.Ready;
        }
    }

    // A tab that overrides nothing it does not have to, which is what all five shipped tabs are
    // for the no-rows sentence.
    private sealed class PlainTab(Func<AnalysisFilter?> filter, Action<Action> post)
        : AnalysisTabViewModel<TestResult>("plain", "Plain", "analysis.plain", filter, post)
    {
        protected override bool IsPixelMetric => false;

        protected override object CreateBody() => new TestBody();

        protected override TestResult? Query(AnalysisFilter filter) => null;

        protected override AnalysisTabState Map(TestResult? result) => AnalysisTabState.Empty;
    }

    private sealed class Harness
    {
        public List<Action> Posted { get; } = [];

        public AnalysisFilter? Filter { get; set; } = AnyFilter;

        public Action<Action> DeferredPost => Posted.Add;

        public static Action<Action> InlinePost => action => action();

        public void Drain()
        {
            var pending = Posted.ToArray();
            Posted.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }
    }

    private static TestTab[] FiveTabs(Harness harness, Action<Action> post, Func<AnalysisFilter, TestResult>? query = null)
        => [.. AnalysisDisplay.TabKeys.Select(key =>
            new TestTab(key, query ?? (_ => new TestResult(1)), () => harness.Filter, post))];

    // ---- section 4.4 -----------------------------------------------------------------------

    [Fact]
    public void ATabNeverSelected_BuildsNoBodyAndIssuesNoQuery()
    {
        // Case 1. Spec 12.14's States table, "A tab not yet visited": nothing is created and
        // nothing is queried. Red against a base that builds bodies eagerly: the body counter
        // reads 5.
        var harness = new Harness();
        var tabs = FiveTabs(harness, Harness.InlinePost);

        foreach (var tab in tabs)
        {
            Assert.Null(tab.Body);
            Assert.Equal(0, tab.BodyCount);
            Assert.Equal(0, tab.QueryCount);
            Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        }
    }

    [Fact]
    public async Task FirstSelection_BuildsTheBodyOnceAndQueriesOnce_ASecondSelectionQueriesNothing()
    {
        // Case 2. Spec 12.14's Tab behaviour: a tab is created lazily on its first selection and
        // is KEPT afterwards with its controls and its last result, so selecting it again
        // re-renders nothing. Red against a base that refreshes on every selection: the query
        // counter reads 2.
        var harness = new Harness();
        var tab = FiveTabs(harness, Harness.InlinePost)[0];

        tab.IsVisible = true;
        await tab.PendingLoad!;
        var body = tab.Body;
        var firstLoad = tab.PendingLoad;

        tab.IsVisible = false;
        tab.IsVisible = true;

        // The load task is the SAME object, so the second selection started nothing at all. The
        // counter alone would be a race: a second load runs on a background thread and its
        // increment need not have landed by the time the assertion reads it.
        Assert.Same(firstLoad, tab.PendingLoad);

        await tab.PendingLoad!;
        Assert.Equal(1, tab.QueryCount);
        Assert.Equal(1, tab.BodyCount);
        Assert.Same(body, tab.Body);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
    }

    [Fact]
    public async Task AFilterChangeWhileHidden_MarksStaleAndQueriesNothing_TheNextSelectionQueriesOnce()
    {
        // Case 3. Spec 12.14: "switching the equipment combination does not fire five queries".
        // Red against a base that refreshes hidden tabs: the counter reads 5 after one filter
        // change.
        var harness = new Harness();
        var tabs = FiveTabs(harness, Harness.InlinePost);

        // What the page does on a filter change: mark every tab, and ask each to refresh. A
        // hidden tab must answer that ask with nothing, which is rule 4.
        foreach (var tab in tabs)
        {
            tab.MarkStale();
            tab.Refresh();
        }

        Assert.Equal(0, tabs.Sum(tab => tab.QueryCount));
        Assert.All(tabs, tab => Assert.True(tab.IsStale));
        Assert.All(tabs, tab => Assert.Null(tab.Body));

        tabs[3].IsVisible = true;
        await tabs[3].PendingLoad!;

        Assert.Equal(1, tabs[3].QueryCount);
        Assert.False(tabs[3].IsStale);
        Assert.Equal(1, tabs.Sum(tab => tab.QueryCount));
    }

    [Fact]
    public async Task ARefresh_KeepsTheLastResultDrawn()
    {
        // Case 4. Spec 12.14's States table, "Loading, refresh": the last result stays drawn and
        // the controls stay live, which is the web's keepPreviousData. Red against a base that
        // clears Body on refresh: the value reads 0 rather than 7.
        var harness = new Harness();
        var next = 7;
        var tab = new TestTab("correlation", _ => new TestResult(next), () => harness.Filter, harness.DeferredPost);

        tab.IsVisible = true;
        await tab.PendingLoad!;
        harness.Drain();
        Assert.Equal(7, tab.TestBodyValue!.Value);
        Assert.Equal(AnalysisTabState.Ready, tab.State);

        next = 9;
        tab.Refresh();

        // The second query is in flight and its publish has not been drained.
        Assert.Equal(AnalysisTabState.Refreshing, tab.State);
        Assert.Equal(7, tab.TestBodyValue!.Value);

        await tab.PendingLoad!;
        harness.Drain();
        Assert.Equal(9, tab.TestBodyValue!.Value);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
    }

    [Fact]
    public async Task AStaleGeneration_DoesNotPublish()
    {
        // Case 5. Two refreshes in flight, the FIRST released last. Red against a base with no
        // generation token: the first refresh's result is what is drawn, so the value reads 1.
        var harness = new Harness();
        var next = 1;
        var tab = new TestTab("correlation", _ => new TestResult(next), () => harness.Filter, harness.DeferredPost);

        tab.IsVisible = true;
        await tab.PendingLoad!;

        next = 2;
        tab.Refresh();
        await tab.PendingLoad!;

        // Both publishes are queued, in order. Release the second, then the first.
        Assert.Equal(2, harness.Posted.Count);
        var queued = harness.Posted.ToArray();
        harness.Posted.Clear();
        queued[1]();
        queued[0]();

        Assert.Equal(2, tab.TestBodyValue!.Value);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
    }

    [Fact]
    public async Task AFailingQuery_LeavesOneTabFailedAndFourUntouched()
    {
        // Case 6. Spec 12.14's States table: a failure is per tab, not per page. Red against a
        // base that lets the exception escape: awaiting the load throws and the case never
        // asserts.
        //
        // The failing tab is ONE OF THE FIVE, not a sixth object beside them: built separately,
        // "the other four are untouched" would be true by C# object identity and no defect in the
        // base could falsify it.
        var harness = new Harness();
        var tabs = FiveTabs(harness, Harness.InlinePost);
        var failing = new TestTab(
            AnalysisDisplay.TabKeys[3],
            _ => throw new InvalidOperationException("the query failed"),
            () => harness.Filter,
            Harness.InlinePost);
        tabs[3] = failing;

        foreach (var tab in tabs)
        {
            tab.IsVisible = true;
            await tab.PendingLoad!;
        }

        Assert.Equal(AnalysisTabState.Failed, failing.State);
        Assert.Equal(AnalysisTabViewModel.FailureText, failing.StatusLine);
        Assert.All(
            tabs.Where(tab => !ReferenceEquals(tab, failing)),
            tab => Assert.Equal(AnalysisTabState.Ready, tab.State));
    }

    [Fact]
    public async Task AReversedRange_QueriesNothingOnAnyTabAndLeavesTheLastResultDrawn()
    {
        // Case 7. Spec 12.14: "no query on any tab. The last drawn result stays on screen." The
        // bar answers null exactly while the range is illegal. Red against a base that passes the
        // reversed dates through: the counter moves from 1 to 2 on every tab.
        var harness = new Harness();
        var tabs = FiveTabs(harness, Harness.InlinePost, _ => new TestResult(4));

        foreach (var tab in tabs)
        {
            tab.IsVisible = true;
            await tab.PendingLoad!;
        }

        harness.Filter = null;
        foreach (var tab in tabs)
        {
            tab.MarkStale();
            tab.Refresh();
        }

        Assert.All(tabs, tab => Assert.Equal(1, tab.QueryCount));
        Assert.All(tabs, tab => Assert.Equal(4, tab.TestBodyValue!.Value));
        Assert.All(tabs, tab => Assert.Equal(AnalysisTabState.Ready, tab.State));
    }

    [Fact]
    public async Task RetryFromFailed_RequeriesAndCanSucceed()
    {
        // Case 8. Spec 12.14: the tab offers a Retry and the Retry works. Red against a
        // RetryCommand bound to nothing: the state stays Failed and the counter stays at 1.
        var harness = new Harness();
        var throwNow = true;
        var tab = new TestTab(
            "correlation",
            _ => throwNow ? throw new InvalidOperationException("the query failed") : new TestResult(3),
            () => harness.Filter,
            Harness.InlinePost);

        tab.IsVisible = true;
        await tab.PendingLoad!;
        Assert.Equal(AnalysisTabState.Failed, tab.State);
        Assert.True(tab.RetryCommand.CanExecute(null));

        throwNow = false;
        tab.RetryCommand.Execute(null);
        await tab.PendingLoad!;

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(2, tab.QueryCount);
        Assert.Equal(3, tab.TestBodyValue!.Value);

        // RelayCommand.Execute ignores CanExecute, so the rule lives in the command body too: a
        // second press on a tab that is now drawing a result queries nothing.
        Assert.False(tab.RetryCommand.CanExecute(null));
        tab.RetryCommand.Execute(null);
        Assert.Equal(2, tab.QueryCount);
    }

    [Fact]
    public async Task AResultArrivingAfterDisposal_PublishesNothing()
    {
        // The load loop's disposal guard. The page is built by the shell and rebuilt on a
        // navigation, so a query started just before a rebuild must not write into a view-model
        // nothing is bound to any more, the reason StatisticsViewModel holds a host-lifetime
        // token. Red against a loop with no guard: the value reads 5 and the state reads Ready.
        var harness = new Harness();
        var tab = new TestTab("matrix", _ => new TestResult(5), () => harness.Filter, harness.DeferredPost);

        tab.IsVisible = true;
        await tab.PendingLoad!;

        tab.Dispose();
        harness.Drain();

        Assert.Equal(AnalysisTabState.Loading, tab.State);
        Assert.Equal(0, tab.TestBodyValue!.Value);

        // And a refresh after disposal starts no query at all.
        tab.Refresh();
        Assert.Equal(1, tab.QueryCount);
    }

    // ---- section 6, the mixed plate scale warning ------------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task ThePlateScaleWarning_NeedsTwoOrMoreDistinctScales(int scales, bool expected)
    {
        // Section 6 case 1. User ruling U3 and spec 12.14: two or more raises it; one, or zero,
        // does not, and nulls are ignored entirely before the count ever reaches here. Red
        // against a >= 1 threshold: the one-train case warns.
        var harness = new Harness();
        var tab = new CorrelationLikeTab(() => harness.Filter, Harness.InlinePost) { Scales = scales };

        tab.IsVisible = true;
        await tab.PendingLoad!;

        Assert.Equal(expected, tab.PlateScaleWarningVisible);
    }

    [Fact]
    public async Task ThePlateScaleWarning_NeedsAPixelMetric_AndOnCorrelationThatIsTheYMetric()
    {
        // Section 6 case 2. Spec 12.14's "When it is shown": on the Correlation tab it is the Y
        // metric that has to be hfr. Red against a rule that reads either axis, or X: the second
        // arm below warns although the plotted Y is FWHM in arcseconds.
        var harness = new Harness();
        var tab = new CorrelationLikeTab(() => harness.Filter, Harness.InlinePost)
        {
            Scales = 2,
            X = AnalysisMetric.Humidity,
            Y = AnalysisMetric.Hfr,
        };

        tab.IsVisible = true;
        await tab.PendingLoad!;
        Assert.True(tab.PlateScaleWarningVisible);

        // An hfr X with a non-hfr Y: the arm the Y-only rule gets wrong first. The state is
        // UNREACHABLE through the shipped pickers, because the X picker offers AnalysisMetrics.X
        // plus Phd2X and Hfr is in neither list, so this arm is kept as a guard against a future X
        // list that admits it and not as evidence about the shipped rule.
        tab.X = AnalysisMetric.Hfr;
        tab.Y = AnalysisMetric.Fwhm;
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;

        Assert.False(tab.PlateScaleWarningVisible);
    }

    [Fact]
    public void ThePlateScaleWarningSentence_MatchesSpec1214Verbatim()
    {
        // Section 6 case 3. Spec 12.14's "The mixed plate scale warning" block quote, byte exact.
        // Red against a re-worded sentence. The sentence lives on the base and not on four tabs,
        // because four tabs show it in the same place (design lesson 1).
        const string expected =
            "This selection mixes optical trains with different plate scales. HFR is measured in "
            + "pixels, so these figures are not comparable across them. Choose one equipment "
            + "combination in the filter bar.";

        Assert.Equal(expected, AnalysisTabViewModel.PlateScaleWarningText);
    }

    [Fact]
    public async Task TheStatusLine_IsTheStatesSentenceAndIsEmptyWhileAChartIsDrawn()
    {
        // The base owns the rows that are the same on every tab (section 4.3). Loading shows the
        // loading line, a refresh shows nothing because the last result stays drawn, an empty
        // result shows the tab's own sentence.
        var harness = new Harness();
        var rows = true;
        var tab = new TestTab(
            "distributions",
            _ => new TestResult(1, NoRows: !rows),
            () => harness.Filter,
            harness.DeferredPost);

        tab.IsVisible = true;
        Assert.Equal(AnalysisTabState.Loading, tab.State);
        Assert.Equal(AnalysisTabViewModel.DefaultLoadingText, tab.StatusLine);

        await tab.PendingLoad!;
        harness.Drain();
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(string.Empty, tab.StatusLine);

        rows = false;
        tab.Refresh();
        Assert.Equal(AnalysisTabState.Refreshing, tab.State);
        Assert.Equal(string.Empty, tab.StatusLine);

        await tab.PendingLoad!;
        harness.Drain();
        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.Equal("distributions: no frames match the current filters.", tab.StatusLine);
    }

    // ---- state and body semantics -----------------------------------------------------------

    [Fact]
    public void ATabThatCannotQuery_QueriesNothing_PublishesItsOwnStateAndStillBuildsItsBody()
    {
        // Spec 12.14's States table, "Compare with fewer than two
        // groups chosen, or the same group twice ... Nothing is queried". The body is the tab's
        // live pickers, and without it the user can never choose the two groups that would make
        // CanQuery true, so the tab is structurally unreachable. Red against a base that builds the
        // body below the CanQuery return: Body is null.
        var harness = new Harness();
        var tab = new TestTab("compare", _ => new TestResult(1), () => harness.Filter, Harness.InlinePost)
        {
            Queryable = false,
        };

        tab.IsVisible = true;

        Assert.Null(tab.PendingLoad);
        Assert.Equal(0, tab.QueryCount);
        Assert.Equal(1, tab.CannotQueryCount);
        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.NotNull(tab.Body);
        Assert.Equal(1, tab.BodyCount);
    }

    [Fact]
    public async Task AFirstSelectionUnderAReversedRange_ShowsTheRangeSentenceOverItsOwnControls()
    {
        // The coordinator's ruling: a tab first selected while the
        // range is reversed has no last drawn result to keep, so it draws its own controls and says
        // spec 12.14's date range sentence rather than standing blank and silent. Red against a
        // base that returns before building a body or writing a status: Body is null and StatusLine
        // is empty.
        var harness = new Harness { Filter = null };
        var tab = new TestTab("correlation", _ => new TestResult(1), () => harness.Filter, Harness.InlinePost);

        tab.IsVisible = true;

        Assert.Equal(0, tab.QueryCount);
        Assert.NotNull(tab.Body);
        Assert.Equal("From date must be on or before To date.", tab.StatusLine);
        Assert.Equal(AnalysisTabViewModel.ReversedRangeText, tab.StatusLine);

        // With a result already drawn the States table's own row applies instead: the last drawn
        // result stays and the tab says nothing over it.
        harness.Filter = AnyFilter;
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;
        Assert.Equal(AnalysisTabState.Ready, tab.State);

        harness.Filter = null;
        tab.MarkStale();
        tab.Refresh();

        Assert.Equal(1, tab.QueryCount);
        Assert.Equal(1, tab.TestBodyValue!.Value);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(string.Empty, tab.StatusLine);
    }

    [Fact]
    public async Task AResultThatReadsNoRows_ClearsThePlateScaleCount_AndSoDoesAFailure()
    {
        // Spec 12.14 counts the plate scales "over the rows the tab actually read", so
        // a mapping that reads none answers 0 and the callout goes. The count is cleared by the
        // loop and not by a rule every mapper's early arm has to remember (design lesson 2). Red
        // against a loop that leaves the count to the mapper: the count reads 2 and the callout
        // stays drawn over a tab that read nothing.
        var harness = new Harness();
        var rows = true;
        var throwNow = false;
        var tab = new TestTab(
            "distributions",
            _ => throwNow
                ? throw new InvalidOperationException("the query failed")
                : new TestResult(1, PlateScales: 2, NoRows: !rows),
            () => harness.Filter,
            Harness.InlinePost)
        {
            PixelMetric = true,
        };

        tab.IsVisible = true;
        await tab.PendingLoad!;
        Assert.Equal(2, tab.DistinctPlateScales);
        Assert.True(tab.PlateScaleWarningVisible);

        rows = false;
        tab.Refresh();
        await tab.PendingLoad!;

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.Equal(0, tab.DistinctPlateScales);
        Assert.False(tab.PlateScaleWarningVisible);

        // And a failure clears it too: a tab showing "The figures could not be read." read no rows
        // either. The throw is the QUERY's, not the mapper's, so Publish never runs and this half
        // can only pass because Fail clears the count itself.
        rows = true;
        tab.Refresh();
        await tab.PendingLoad!;
        Assert.Equal(2, tab.DistinctPlateScales);

        throwNow = true;
        tab.Refresh();
        await tab.PendingLoad!;

        Assert.Equal(AnalysisTabState.Failed, tab.State);
        Assert.Equal(0, tab.DistinctPlateScales);
        Assert.False(tab.PlateScaleWarningVisible);
    }

    [Fact]
    public async Task AMapperThatThrowsOnTheDeferredPost_FailsTheTabAndLetsNothingEscape()
    {
        // Under UiPost.Default the publish closure is queued and runs later on the
        // dispatcher, OUTSIDE the query's try, so a mapper that throws was an unhandled exception
        // on the UI thread. The deferred post seam is what reproduces that here: the closure runs
        // from Drain and not inside Task.Run. Red against a base whose only try wraps the query and
        // the post call together: Drain rethrows and the case never asserts.
        var harness = new Harness();
        var tab = new TestTab("matrix", _ => new TestResult(1), () => harness.Filter, harness.DeferredPost)
        {
            ThrowOnMap = true,
        };

        tab.IsVisible = true;
        await tab.PendingLoad!;

        harness.Drain();

        Assert.Equal(AnalysisTabState.Failed, tab.State);
        Assert.Equal(AnalysisTabViewModel.FailureText, tab.StatusLine);
        Assert.True(tab.RetryCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TheQuery_RunsOffTheUiThread_AndThePublishRunsOnIt()
    {
        // Ruling A8. TRACKING section 5's shape, which
        // MergeDialogViewModelTests.Preview_PublishesOnTheUiThread already ships: the real
        // dispatcher as the post seam, CheckAccess read on both sides, awaited and never blocked.
        // Red against a loop that calls the query inline instead of through Task.Run: the query
        // reads true.
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        var harness = new Harness();
        var queryOnUiThread = new List<bool>();
        var publishedOnUiThread = new List<bool>();
        var tab = new TestTab(
            "correlation",
            _ =>
            {
                queryOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
                return new TestResult(1);
            },
            () => harness.Filter,
            action => Dispatcher.UIThread.Post(action));

        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AnalysisTabViewModel.State))
            {
                publishedOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
            }
        };

        tab.IsVisible = true;
        await tab.PendingLoad!;
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(queryOnUiThread);
        Assert.All(queryOnUiThread, Assert.False);
        Assert.NotEmpty(publishedOnUiThread);
        Assert.All(publishedOnUiThread, Assert.True);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
    }

    [Fact]
    public void IsCurrent_AnswersIsVisibleAndIsRaisedWithIt()
    {
        // The page's tab strip marks the current tab. One answer under the strip's own word, so no
        // second flag can drift from the one the loop reads. Red against a member that is not
        // raised: the name never appears.
        var harness = new Harness();
        var tab = new TestTab("correlation", _ => new TestResult(1), () => harness.Filter, Harness.InlinePost);
        var raised = new List<string?>();
        tab.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        Assert.False(tab.IsCurrent);

        tab.IsVisible = true;
        Assert.True(tab.IsCurrent);
        Assert.Contains(nameof(AnalysisTabViewModel.IsCurrent), raised);

        raised.Clear();
        tab.IsVisible = false;

        Assert.False(tab.IsCurrent);
        Assert.Contains(nameof(AnalysisTabViewModel.IsCurrent), raised);
    }

    [Fact]
    public async Task ShowsResult_IsTrueInReadyAndRefreshingAlone_AndIsRaisedWithTheState()
    {
        // Spec 12.14's States table gates the CHART and not the
        // tab body: "Loading, first result" replaces the body with a loading line, "No row matches
        // the filters" draws no chart, and "Loading, refresh" keeps the last result drawn, which is
        // why Refreshing is in the set and Loading is not. The five tab views bind one ResultRegion
        // to this. Red against a member that answers Ready alone, which fails the Refreshing arm,
        // and against one the State setter never raises, which fails the notification arm.
        var harness = new Harness();
        var rows = true;
        var tab = new TestTab(
            "correlation",
            _ => new TestResult(1, NoRows: !rows),
            () => harness.Filter,
            harness.DeferredPost);
        var raised = new List<string?>();
        tab.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        Assert.Equal(AnalysisTabState.NotLoaded, tab.State);
        Assert.False(tab.ShowsResult);

        tab.IsVisible = true;
        Assert.Equal(AnalysisTabState.Loading, tab.State);
        Assert.False(tab.ShowsResult);
        Assert.Contains(nameof(AnalysisTabViewModel.ShowsResult), raised);

        await tab.PendingLoad!;
        harness.Drain();
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.True(tab.ShowsResult);

        // The refresh row: the last result stays drawn, so the region stays on screen.
        tab.Refresh();
        Assert.Equal(AnalysisTabState.Refreshing, tab.State);
        Assert.True(tab.ShowsResult);

        rows = false;
        await tab.PendingLoad!;
        harness.Drain();
        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.False(tab.ShowsResult);

        // TooFew and Failed, the two remaining states that draw no chart. TooFew is a mapper's
        // answer on every real tab, so it is reached here the same way.
        rows = true;
        tab.TooFewOnNextMap = true;
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;
        harness.Drain();
        Assert.Equal(AnalysisTabState.TooFew, tab.State);
        Assert.False(tab.ShowsResult);

        tab.ThrowOnMap = true;
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;
        harness.Drain();
        Assert.Equal(AnalysisTabState.Failed, tab.State);
        Assert.False(tab.ShowsResult);
    }

    [Fact]
    public async Task TheNoRowsSentence_IsWrittenOnceOnTheBaseAndNoTabRepeatsIt()
    {
        // Design lesson 1, and the reason the three sentences beside it are constants already: all
        // five tabs show this one and a wording edit that lands in four of them makes the page say
        // two different things about one state. Red against the five overrides this replaced: each
        // one declares the member, so DeclaringType answers the tab and the sweep names it.
        Assert.Equal(
            "No frames match the current filters. Widen them in the filter bar above.",
            AnalysisTabViewModel.EmptyText);

        Type[] shipped =
        [
            typeof(CorrelationTabViewModel),
            typeof(DistributionsTabViewModel),
            typeof(TimeSeriesTabViewModel),
            typeof(MatrixTabViewModel),
            typeof(CompareTabViewModel),
        ];

        foreach (var tabType in shipped)
        {
            var member = tabType.GetProperty(
                "EmptyMessage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.NotNull(member);
            Assert.Equal(typeof(AnalysisTabViewModel), member!.DeclaringType);
        }

        // And the member the five inherit answers the constant on a live tab, in the state that
        // puts it on screen. The too-few line falls through to it as well, which is the Matrix's
        // own row of the States table.
        var harness = new Harness();
        var plain = new PlainTab(() => harness.Filter, Harness.InlinePost);

        plain.IsVisible = true;
        await plain.PendingLoad!;

        Assert.Equal(AnalysisTabState.Empty, plain.State);
        Assert.Equal(AnalysisTabViewModel.EmptyText, plain.StatusLine);
    }
}
