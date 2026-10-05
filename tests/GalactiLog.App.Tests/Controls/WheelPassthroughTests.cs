using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Xunit;

namespace GalactiLog.App.Tests.Controls;

// One rule for every chart under a page scroller: a plain wheel scrolls the page, Ctrl with the
// wheel zooms the chart. WheelPassthrough is the choke point; these cases pin both the behaviour
// and the markup census that keeps every chart-bearing scroller on it.
public class WheelPassthroughTests
{
    private const string Attribute = "WheelPassthrough.IsEnabled";

    /// <summary>
    /// A failure looks like: a new page scroller wrapping a chart, whose plain wheel then stops the
    /// page dead while the pointer rests on the plot, because LiveCharts marks every wheel handled
    /// before it reads ZoomMode.
    /// </summary>
    [Fact]
    public void EveryScrollerOverAChart_CarriesWheelPassthrough()
    {
        var documents = SourceScan.EnumerateMarkupFiles("GalactiLog.App")
            .Select(file => (File: file, Document: XDocument.Load(file, LoadOptions.SetLineInfo))).ToList();

        // A chart that arrives through a part or a control is a chart too: markup holding one, or
        // holding an element named for one, is itself a chart holder, to a fixed point.
        var holders = new HashSet<string>();
        bool IsChart(XElement e) => e.Name.LocalName == "GuideGraph"
            || e.GetPrefixOfNamespace(e.Name.Namespace) == "lvc" || holders.Contains(e.Name.LocalName);
        bool grew;
        do
        {
            grew = false;
            foreach (var (_, document) in documents)
            {
                var name = ((string?)document.Root?.Attributes().FirstOrDefault(a => a.Name.LocalName == "Class")?.Value)?.Split('.').Last();
                if (name is not null && !holders.Contains(name) && document.Descendants().Any(IsChart))
                {
                    holders.Add(name);
                    grew = true;
                }
            }
        }
        while (grew);

        var missing = new List<string>();
        foreach (var (file, document) in documents)
        {
            foreach (var scroller in document.Descendants().Where(e => e.Name.LocalName == "ScrollViewer"))
            {
                var holdsAChart = scroller.Descendants().Any(IsChart);
                var enabled = scroller.Attributes().Any(a =>
                    a.Name.LocalName == Attribute && string.Equals(a.Value, "True", StringComparison.OrdinalIgnoreCase));
                if (holdsAChart && !enabled)
                {
                    missing.Add($"{Path.GetFileName(file)}: ScrollViewer {scroller.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value} at line {((IXmlLineInfo)scroller).LineNumber}");
                }
            }
        }

        Assert.True(missing.Count == 0, "ScrollViewers over a chart without WheelPassthrough:\n" + string.Join('\n', missing));
    }

    /// <summary>The Analysis page hosts its charts through App.axaml DataTemplates, which the
    /// subtree census above cannot see, so its scroller is named here.</summary>
    [Fact]
    public void TheAnalysisPageScroller_CarriesWheelPassthrough()
    {
        var path = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "AnalysisView.axaml");
        var scrollers = XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "ScrollViewer").ToList();

        Assert.Single(scrollers);
        Assert.Contains(scrollers[0].Attributes(), a => a.Name.LocalName == Attribute && a.Value == "True");
    }

    private static (Window Window, ScrollViewer Scroller, CartesianChart Chart) Show()
    {
        ChartTheme.Apply();
        var chart = new CartesianChart { Height = 400 };
        var scroller = new ScrollViewer
        {
            Content = new StackPanel { Children = { chart, new Border { Height = 400 } } },
        };
        WheelPassthrough.SetIsEnabled(scroller, true);
        var window = new Window { Width = 600, Height = 300, Content = scroller };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, scroller, chart);
    }

    private static Point Centre(Window window, CartesianChart chart)
        => chart.TranslatePoint(new Point(chart.Bounds.Width / 2, chart.Bounds.Height / 2), window)
           ?? throw new InvalidOperationException("the chart is not in the window");

    [AvaloniaFact]
    public void APlainWheelOverAChart_ScrollsThePage()
    {
        var (window, scroller, chart) = Show();
        Assert.Equal(0d, scroller.Offset.Y);

        window.MouseWheel(Centre(window, chart), new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();

        // 50 px per notch, ScrollContentPresenter's own figure.
        Assert.Equal(50d, scroller.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public void ACtrlWheelOverAChart_LeavesThePageWhereItIs()
    {
        // The pair with the case above is what makes it non-vacuous: if the chart were not
        // swallowing the wheel, the presenter itself would scroll on Ctrl and this would fail.
        var (window, scroller, chart) = Show();

        window.MouseWheel(Centre(window, chart), new Vector(0, -1), RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0d, scroller.Offset.Y);
        window.Close();
    }

    // An outer page scroller holding an inner one, both on the passthrough, the inner holding
    // the given content between two fillers, so part of it shows at either end of the inner.
    private static (Window Window, ScrollViewer Outer, ScrollViewer Inner) ShowNested(Control content, double innerHeight = 200)
    {
        ChartTheme.Apply();
        var inner = new ScrollViewer { Height = innerHeight, Content = new StackPanel { Children = { new Border { Height = 100 }, content, new Border { Height = 100 } } } };
        var outer = new ScrollViewer { Content = new StackPanel { Children = { inner, new Border { Height = 800 } } } };
        WheelPassthrough.SetIsEnabled(inner, true);
        WheelPassthrough.SetIsEnabled(outer, true);
        var window = new Window { Width = 600, Height = 300, Content = outer };
        window.Show();
        Settle();
        return (window, outer, inner);
    }

    // Two render ticks: the hit test reads the last rendered frame, which a moved offset leaves stale.
    private static void Settle()
    {
        for (var tick = 0; tick < 2; tick++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    // The centre of the part of the control the inner scroller shows.
    private static Point CentreOf(Window window, Control control, ScrollViewer inner)
    {
        var top = control.TranslatePoint(default, inner)!.Value;
        var shown = new Rect(top, control.Bounds.Size).Intersect(new Rect(inner.Bounds.Size));
        Assert.True(shown.Height > 0, "the control is scrolled out of the inner scroller");
        return inner.TranslatePoint(shown.Center, window)!.Value;
    }

    [AvaloniaFact]
    public void NestedScrollers_OneNotchOverAChart_MovesOnlyTheInnermostThatIsNotAtItsEnd()
    {
        // One scroller per notch. A failure looks like the outer page moving on the same notch as
        // the inner scroller, or the page staying put once the inner has reached its end.
        var chart = new CartesianChart { Height = 120 };
        var (window, outer, inner) = ShowNested(chart);

        window.MouseWheel(CentreOf(window, chart, inner), new Vector(0, -1));
        Settle();
        Assert.Equal(50d, inner.Offset.Y);
        Assert.Equal(0d, outer.Offset.Y);

        inner.Offset = new Vector(0, inner.Extent.Height);
        Settle();
        var end = inner.Offset.Y;
        window.MouseWheel(CentreOf(window, chart, inner), new Vector(0, -1));
        Settle();
        Assert.Equal(end, inner.Offset.Y);
        Assert.Equal(50d, outer.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public void NestedScrollers_AnInnerThatDoesNotOverflow_PassesTheNotchToThePage()
    {
        // A failure looks like a chart in an inner scroller with nothing to scroll stopping the page.
        var chart = new CartesianChart { Height = 120 };
        var (window, outer, inner) = ShowNested(chart, innerHeight: 400);
        Assert.True(inner.Extent.Height <= inner.Viewport.Height, $"the inner overflows: {inner.Extent.Height} in {inner.Viewport.Height}");

        window.MouseWheel(CentreOf(window, chart, inner), new Vector(0, -1));
        Settle();
        Assert.Equal(50d, outer.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public void AGuideGraph_LeavesThePlainWheelToItsScroller_WhichMovesOneNotchAndTheOuterNone()
    {
        // The guide graph does not swallow a plain wheel, so its scroller's own presenter moves it.
        // A failure looks like the scroller moving two notches, or the page moving on the same notch.
        var graph = new GuideGraph { Height = 120 };
        var (window, outer, inner) = ShowNested(graph);
        var at = CentreOf(window, graph, inner);
        var hit = window.InputHitTest(at) as Visual;
        Assert.True(hit is not null && (hit == graph || graph.IsVisualAncestorOf(hit)), $"the pointer is over {hit?.GetType().Name}, not the graph");

        window.MouseWheel(at, new Vector(0, -1));
        Settle();
        Assert.Equal(50d, inner.Offset.Y);
        Assert.Equal(0d, outer.Offset.Y);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(-1d, false)]
    [InlineData(1d, false)]
    [InlineData(-1d, true)]
    [InlineData(1d, true)]
    public void APlainNotchOverAChart_MovesThePageOneNotchInItsDirection(double notch, bool inAnInnerThatDoesNotOverflow)
    {
        // A failure looks like the page moving against the notch, by other than one notch, or not at all,
        // whether the page moves itself or takes the notch an inner scroller passed on.
        ChartTheme.Apply();
        var chart = new CartesianChart { Height = 120 };
        Control middle = chart;
        if (inAnInnerThatDoesNotOverflow)
        {
            var inner = new ScrollViewer { Height = 300, Content = new StackPanel { Children = { chart } } };
            WheelPassthrough.SetIsEnabled(inner, true);
            middle = inner;
        }

        var outer = new ScrollViewer { Content = new StackPanel { Children = { new Border { Height = 200 }, middle, new Border { Height = 800 } } } };
        WheelPassthrough.SetIsEnabled(outer, true);
        var window = new Window { Width = 600, Height = 300, Content = outer };
        window.Show();
        Settle();
        outer.Offset = new Vector(0, 100);
        Settle();
        var at = chart.TranslatePoint(new Point(chart.Bounds.Width / 2, chart.Bounds.Height / 2), window)!.Value;
        Assert.True(window.InputHitTest(at) is Visual hit && (hit == chart || chart.IsVisualAncestorOf(hit)), $"the pointer at {at} is over {window.InputHitTest(at)?.GetType().Name}, not the chart at {chart.TranslatePoint(default, window)}");

        window.MouseWheel(at, new Vector(0, notch));
        Settle();

        Assert.Equal(100d - (notch * 50d), outer.Offset.Y);
        window.Close();
    }
}
