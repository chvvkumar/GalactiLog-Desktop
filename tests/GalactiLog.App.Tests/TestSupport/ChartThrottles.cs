using System.Reflection;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using Xunit;

namespace GalactiLog.App.Tests.TestSupport;

// LiveCharts measures on a 50 ms throttle whose last step is a pool thread calling Dispatcher.UIThread;
// one that lands after a headless test ends builds the next test's dispatcher before its platform is
// set up, so that test fails in PushFrame or never lays out. JoinChartThrottlesAttribute joins them after every test.
internal sealed class ChartThrottles
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly FieldInfo UpdateThrottler = typeof(Chart).GetField("_updateThrottler", Private)
        ?? throw new MissingFieldException(nameof(Chart), "_updateThrottler");

    // The pointer's tooltip and panning throttles hop through the pool the same way as the update.
    private static readonly FieldInfo[] PointerThrottlers =
    [
        typeof(Chart).GetField("_tooltipThrottler", Private) ?? throw new MissingFieldException(nameof(Chart), "_tooltipThrottler"),
        typeof(Chart).GetField("_panningThrottler", Private) ?? throw new MissingFieldException(nameof(Chart), "_panningThrottler"),
    ];

    private static readonly FieldInfo IsWaiting = typeof(ActionThrottler).GetField("_isWaiting", Private)
        ?? throw new MissingFieldException(nameof(ActionThrottler), "_isWaiting");

    private static readonly FieldInfo TargetAction = typeof(ActionThrottler).GetField("<targetAction>P", Private)
        ?? throw new MissingFieldException(nameof(ActionThrottler), "targetAction");

    private static int s_watching;

    /// <summary>Every chart any headless test has put into a visual tree since the last <see cref="Release"/>.</summary>
    public static ChartThrottles Mounted { get; } = new();

    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly HashSet<IChartView> _charts = [];

    private readonly List<Task> _hops = [];

    public int Count => _charts.Count;

    public bool Tracks(IChartView chart) => _charts.Contains(chart);

    public static bool IsArmed(IChartView chart) => (bool)IsWaiting.GetValue(UpdateThrottler.GetValue(chart.CoreChart))!;

    /// <summary>True while the update or a pointer throttle of the chart has a hop pending.</summary>
    public static bool IsAnyArmed(IChartView chart)
        => IsArmed(chart) || PointerThrottlers.Any(field => (bool)IsWaiting.GetValue(field.GetValue(chart.CoreChart))!);

    /// <summary>Tracks every chart that gains a visual parent from now on; the handler is process-wide, so once only.</summary>
    public static void Watch()
    {
        if (Interlocked.Exchange(ref s_watching, 1) == 0)
        {
            Visual.VisualParentProperty.Changed.AddClassHandler<Visual>((visual, _) =>
            {
                if (visual is IChartView chart)
                {
                    Mounted.Add(chart);
                }
            });
        }
    }

    /// <summary>Joins and forgets every tracked chart; touches no dispatcher when nothing was mounted.</summary>
    public void Release()
    {
        if (_charts.Count == 0)
        {
            lock (_hops)
            {
                _hops.Clear();
            }

            return;
        }

        try
        {
            Join(Budget);
        }
        finally
        {
            _charts.Clear();
            lock (_hops)
            {
                _hops.Clear();
            }
        }
    }

    /// <summary>Starts recording the pool hop of every chart under the root not already tracked.</summary>
    public void Track(Visual root)
    {
        foreach (var chart in root.GetVisualDescendants().OfType<IChartView>())
        {
            Add(chart);
        }
    }

    private void Add(IChartView chart)
    {
        if (!_charts.Add(chart))
        {
            return;
        }

        foreach (var field in PointerThrottlers.Prepend(UpdateThrottler))
        {
            var throttler = field.GetValue(chart.CoreChart)!;
            var hop = (Func<Task>)TargetAction.GetValue(throttler)!;

            // A chart tracked again in a later test keeps its first wrapper, so each hop is recorded once.
            if (hop.Target is not Recorder)
            {
                TargetAction.SetValue(throttler, (Func<Task>)new Recorder(hop, _hops).Invoke);
            }
        }
    }

    private sealed class Recorder(Func<Task> hop, List<Task> hops)
    {
        public Task Invoke()
        {
            var task = hop();
            lock (hops)
            {
                hops.Add(task);
            }

            return task;
        }
    }

    /// <summary>Stops new updates, runs the dispatcher until no tracked chart has one armed, then
    /// joins the hops those updates started and runs what they posted.</summary>
    public void Join(TimeSpan budget)
    {
        foreach (var chart in _charts)
        {
            chart.AutoUpdateEnabled = false;
        }

        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return !_charts.Any(IsAnyArmed);
            }, budget),
            "a chart update was still armed after the budget");

        Task[] hops;
        lock (_hops)
        {
            hops = [.. _hops];
        }

        Assert.True(Task.WaitAll(hops, budget), "a chart's measure hop did not finish within the budget");
        Dispatcher.UIThread.RunJobs();
    }
}
