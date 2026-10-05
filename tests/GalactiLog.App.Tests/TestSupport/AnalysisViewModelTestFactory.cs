using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one place App.Tests builds an Analysis page and the seam results behind it, so a later
/// constructor or read-model change is one edit rather than a dozen. No database, no window and no
/// dispatcher: every delegate is a lambda and the post seam runs its closure inline (design-spec
/// 18.3).
/// </summary>
/// <remarks>
/// There is deliberately no fake of <c>AnalysisCache</c> (ruling P1-1): it is sealed with no
/// interface and no virtual member, so it cannot be substituted for at all. Every query is a
/// delegate, and a test that counts calls writes its own counter over one of the defaults below.
/// </remarks>
internal static class AnalysisViewModelTestFactory
{
    public static EquipmentCombination Combination(
        string telescope = "RC8", string camera = "ASI2600MM", bool grouped = false)
        => new(telescope, camera, grouped);

    public static SummaryStats Stats(int count = 12)
        => new(count, 1.1d, 3.3d, 2.2d, 2.1d, 0.4d);

    /// <summary>A correlation answer with <paramref name="points"/> points, all on one plate scale
    /// unless <paramref name="plateScales"/> says otherwise.</summary>
    public static CorrelationResult Correlation(int points = 3, int plateScales = 1)
        => new(
            [.. Enumerable.Range(0, points).Select(index => new CorrelationPoint(
                index, index * 2d, new DateOnly(2025, 6, 1).AddDays(index), null, false))],
            points >= 3
                ? new TrendLine(2d, 0d, 1d, 1d, 1d, [], [])
                : null,
            points >= 2 ? Stats(points) : null,
            points >= 2 ? Stats(points) : null,
            new Dictionary<Guid, string>(),
            points >= 3 ? CorrelationQuality.Ok : CorrelationQuality.TooFewPoints,
            plateScales,
            points,
            points);

    public static DistributionResult Distribution(int bins = 4, int plateScales = 1)
        => new(
            [.. Enumerable.Range(0, bins).Select(index => new HistogramBin(index, index + 1d, index))],
            Stats(),
            0.2d,
            plateScales);

    /// <summary>A box plot answer. <paramref name="rows"/> defaults to eight rows per group, which
    /// is the shape a real read has: an empty <c>Groups</c> with a non-zero row count is spec
    /// 12.14's "Box plot with no group left" and a case that wants it says so.</summary>
    public static BoxPlotResult Boxes(int groups = 2, int plateScales = 1, int? rows = null)
        => new(
            [.. Enumerable.Range(0, groups).Select(index => new BoxPlot(
                $"Group {index}", 1d, 1.5d, 2d, 2.5d, 3d, [], 8))],
            plateScales,
            rows ?? (groups * 8));

    public static TimeSeriesResult TimeSeries(int points = 5, int plateScales = 1)
        => new(
            [.. Enumerable.Range(0, points).Select(index => new TimeSeriesPoint(
                new DateOnly(2025, 6, 1).AddDays(index), index + 1d, "M 31", 1, 10))],
            [],
            [],
            [],
            plateScales);

    public static MatrixResult Matrix(int plateScales = 1)
        => new(
            [.. AnalysisMetrics.X.SelectMany(x => AnalysisMetrics.Y.Select(
                y => new MatrixCell(x, y, 0.5d, 40)))],
            plateScales);

    public static CompareResult Compare(CompareState state = CompareState.Ok)
        => new(
            state,
            "A",
            "B",
            12,
            12,
            state == CompareState.Ok ? new CompareGroup("A", Boxes().Groups[0], Stats()) : null,
            state == CompareState.Ok ? new CompareGroup("B", Boxes().Groups[1], Stats()) : null,
            state == CompareState.Ok ? "A is 12 per cent better than B" : null,
            true,
            1.1d,
            1.3d);

    /// <summary>
    /// A page with every delegate defaulted. Nothing has been queried when this returns: the page
    /// constructs without a query and the first one is the selected tab's own first refresh, which
    /// the view triggers through <see cref="AnalysisViewModel.Activate"/>.
    /// </summary>
    /// <param name="post">
    /// <b>Which seam a case must pass.</b> A case that builds no window
    /// takes the default, the inline seam, and reads the published values straight after
    /// <see cref="Settle"/>. A case that SHOWS the bound view passes
    /// <c>GalactiLog.App.Services.UiPost.Default</c>, because the markup binds
    /// <c>PlateScaleWarningVisible</c>, <c>StatusLine</c> and <c>State</c>, and raising
    /// <c>PropertyChanged</c> for a bound member from the thread-pool thread the query ran on is an
    /// order the application can never produce: the publish throws, the load loop's own catch turns
    /// it into <see cref="AnalysisTabState.Failed"/>, and the case reads a state nothing chose.
    /// That is TRACKING section 5's first false-green shape, "a harness post seam that hides
    /// UI-thread affinity". The posted closure is drained by the
    /// <c>Dispatcher.UIThread.RunJobs()</c> such a case already runs after <see cref="Settle"/>,
    /// so no case body changes. Tasks 5 and 6 read this paragraph rather than rediscovering it.
    /// </param>
    public static AnalysisViewModel Create(
        Func<IReadOnlyList<EquipmentCombination>>? loadEquipment = null,
        Func<IReadOnlyList<string>>? loadFilters = null,
        Func<AnalysisMetric, AnalysisMetric, AnalysisFilter, CorrelationResult>? correlation = null,
        Func<AnalysisMetric, AnalysisFilter, DistributionResult?>? distribution = null,
        Func<AnalysisMetric, BoxPlotGrouping, AnalysisFilter, BoxPlotResult>? boxPlot = null,
        Func<AnalysisMetric, AnalysisFilter, TimeSeriesResult>? timeSeries = null,
        Func<AnalysisFilter, MatrixResult>? matrix = null,
        Func<AnalysisMetric, CompareMode, string, string, DateOnly?, DateOnly?, CompareResult>? compare = null,
        DisplaySettings? display = null,
        Action<Func<DisplaySettings, DisplaySettings>>? writeDisplay = null,
        Action<Action>? post = null,
        DerivedDataSource? derivedData = null)
    {
        var page = new AnalysisViewModel(
            loadEquipment ?? (() => [Combination(), Combination("Askar FMA180", "ASI533MC", grouped: true)]),
            loadFilters ?? (() => ["Ha", "OIII"]),
            correlation ?? ((_, _, _) => Correlation()),
            distribution ?? ((_, _) => Distribution()),
            boxPlot ?? ((_, _, _) => Boxes()),
            timeSeries ?? ((_, _) => TimeSeries()),
            matrix ?? (_ => Matrix()),
            compare ?? ((_, _, _, _, _, _) => Compare()),
            display,
            writeDisplay,
            post: post ?? (action => action()),
            // Null unless a case hands one over, so every existing case builds the page it built
            // before and only the cases that name the notification follow it.
            subscribeDerivedDataChanged: derivedData is null ? null : derivedData.Subscribe,
            unsubscribeDerivedDataChanged: derivedData is null ? null : derivedData.Unsubscribe);

        Settle(page);
        return page;
    }

    /// <summary>
    /// Joins the filter bar's one list read and whatever tab load is in flight.
    /// </summary>
    /// <remarks>
    /// Three rounds, because one round's publish can start a further load: the page's own filter
    /// handler refreshes the selected tab on every <c>Changed</c>, so a load that lands while the
    /// bar is still settling leaves a second one behind it. The bound is a harness bound and
    /// nothing reads it as a figure. Bounded and swallowing, because a harness join must not turn a
    /// background outcome into a test failure: the load loop turns a failing query into a tab state
    /// and that state is the case's own subject.
    /// </remarks>
    public static void Settle(AnalysisViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            Wait(page.SharedFilter.PendingLoad);
            foreach (var tab in page.Tabs)
            {
                Wait(tab.PendingLoad);
            }
        }
    }

    private static void Wait(Task? task)
    {
        try
        {
            task?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // The load loop turns a failing query into a tab state, so a faulted task here is a
            // case's own subject rather than this helper's problem.
        }
    }
}
