using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace GalactiLog.App.Tests.TestSupport;

// The join runs between tests, so a case that leaves a chart behind is followed by the case that
// reads what the assembly's after step did to it; the cases run in name order for that reason.
[TestCaseOrderer("GalactiLog.App.Tests.TestSupport.ChartThrottlesTests+ByName", "GalactiLog.App.Tests")]
public sealed class ChartThrottlesTests
{
    // Long enough that a throttle armed at a test's end is still armed when the next test starts unjoined.
    private static readonly TimeSpan LongThrottle = TimeSpan.FromMilliseconds(500);

    private static CartesianChart? s_leftArmed;
    private static CartesianChart? s_leftTracked;
    private static PieChart? s_leftPie;

    public sealed class ByName : ITestCaseOrderer
    {
        public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
            where TTestCase : ITestCase
            => testCases.OrderBy(testCase => testCase.TestMethod.Method.Name, StringComparer.Ordinal);
    }

    // The constructor arms a throttle at the default span before UpdaterThrottler is set, so the
    // mount waits that one out; the next update then arms at the long span.
    private static CartesianChart Mount() => Mount(new CartesianChart
    {
        UpdaterThrottler = LongThrottle,
        Series = [new LineSeries<double> { Values = [1, 3, 2] }],
    });

    private static TChart Mount<TChart>(TChart chart)
        where TChart : Control, IChartView
    {
        new Window { Width = 400, Height = 300, Content = chart }.Show();
        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return !ChartThrottles.IsArmed(chart);
            }, ChartThrottles.Budget),
            "setup: the mounted chart never went idle");
        return chart;
    }

    // Red: the chart is missing from the tracking, so no after step would ever join it.
    [AvaloniaFact]
    public void Case1_AChartMountedWithNoHarness_IsTracked()
    {
        var chart = Mount();

        Assert.True(ChartThrottles.Mounted.Tracks(chart), "the chart a test mounted on its own is not tracked");
    }

    [AvaloniaFact]
    public void Case2a_LeavesAChartWithAnUpdateArmed()
    {
        s_leftArmed = Mount();

        // Armed from a dispatcher job as the app's own updates are; xunit waits out an update armed
        // in the test body itself, so only a job-armed one outlives the test.
        Dispatcher.UIThread.Post(() => s_leftArmed.CoreChart.Update());
        Dispatcher.UIThread.RunJobs();

        Assert.True(ChartThrottles.IsArmed(s_leftArmed), "setup: the chart has no update armed at the test's end");
    }

    // Red: the update the previous test left armed is still waiting to fire into this test.
    [AvaloniaFact]
    public void Case2b_AfterTheAfterStep_NoTrackedChartHasAnUpdateArmed()
    {
        Assert.NotNull(s_leftArmed);
        Assert.False(ChartThrottles.IsArmed(s_leftArmed), "the previous test's chart still has an update armed");
    }

    [AvaloniaFact]
    public void Case3a_LeavesAChartTracked()
    {
        s_leftTracked = Mount();

        Assert.True(ChartThrottles.Mounted.Tracks(s_leftTracked), "setup: the chart is not tracked at the test's end");
    }

    // Red: the previous test's chart is still tracked, so this test would join a chart from a torn down session.
    [AvaloniaFact]
    public void Case3b_AtTheNextTestsStart_TheTrackingIsEmpty()
    {
        Assert.NotNull(s_leftTracked);
        Assert.True(ChartThrottles.Mounted.Count == 0, $"{ChartThrottles.Mounted.Count} charts still tracked when this test began");
    }

    [AvaloniaFact]
    public void Case4a_LeavesAPieChartWithAnUpdateArmed()
    {
        s_leftPie = Mount(new PieChart
        {
            UpdaterThrottler = LongThrottle,
            Series = [new PieSeries<double> { Values = [3] }, new PieSeries<double> { Values = [5] }],
        });
        Dispatcher.UIThread.Post(() => s_leftPie.CoreChart.Update());
        Dispatcher.UIThread.RunJobs();

        Assert.True(ChartThrottles.IsArmed(s_leftPie), "setup: the pie chart has no update armed at the test's end");

        // Red: the handler takes cartesian charts only.
        Assert.True(ChartThrottles.Mounted.Tracks(s_leftPie), "the pie chart a test mounted is not tracked");
    }

    // Red: the pie chart was never tracked, so its update is still waiting to fire into this test.
    [AvaloniaFact]
    public void Case4b_APieChartIsTrackedAndJoinedLikeACartesianOne()
    {
        Assert.NotNull(s_leftPie);
        Assert.False(ChartThrottles.IsArmed(s_leftPie), "the previous test's pie chart still has an update armed");
    }
}
