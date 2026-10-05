using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.Views;
using GalactiLog.Data.Queries;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.StatisticsViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Spec 12.5's "Wheel zoom and drag pan" (Phase 14C, UI layout ruling 13), at the view level: the
// four handlers StatisticsView.axaml.cs adds to the TimelineChart element. The period arithmetic
// itself is ImagingTimelineZoomPanTests.cs's surface; this file proves the pixel side reaches it.
public class StatisticsTimelineInputTests
{
    // Two years of consecutive months, so a chart at any reasonable test window width has a small
    // enough bar pitch that a 200 pixel drag or a single wheel step is unambiguous.
    private static IReadOnlyList<TimelineEntry> TwoYearsOfMonths()
    {
        var entries = new List<TimelineEntry>();
        var start = new DateOnly(2023, 7, 1);
        for (var i = 0; i < 24; i++)
        {
            entries.Add(new TimelineEntry(start.AddMonths(i).ToString("yyyy-MM", CultureInfo.InvariantCulture), 3_600d));
        }

        return entries;
    }

    private static (Window Window, StatisticsView View, StatisticsViewModel Page) Show()
    {
        ChartTheme.Apply();
        var page = Factory.Create(loadStats: () => Factory.Sample(monthly: TwoYearsOfMonths()));
        var view = new StatisticsView { DataContext = page };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        // window.Show plus RunJobs pumps the dispatcher but does not drive the render timer, and
        // the chart's own bounds do not settle without one (the same reason NightStripTests and
        // HelpButtonTests force a tick before reading a pointer-dependent control's geometry).
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, view, page);
    }

    // The Timeline card sits well down the page's nine sections, well below a 900 pixel window,
    // and a translated point that has scrolled out of the page's own ScrollViewer lands nowhere:
    // the synthetic pointer event hits whatever is actually on screen at that point, not the
    // chart. BringIntoView scrolls it into the viewport first, the same way a real pointer
    // reaches it only after the page is scrolled to show it.
    private static Point CenterOf(Control control, Visual window)
    {
        control.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("the chart is not in the window's visual tree");
    }

    // Fix pass, review P2: both types the fallback comment used to cite are public with public
    // constructors in Avalonia 11.3 (Avalonia.Input.Pointer, Avalonia.Input.PointerPointProperties),
    // confirmed by building a real Pointer and PointerPointProperties and raising a genuine
    // PointerWheelEventArgs on the chart through CartesianChart.RaiseEvent. That is a materially
    // different, fuller attempt than the one this comment used to describe, and it still does not
    // work in this harness: OnTimelineWheel's own diagnostic hit counter stayed at zero on every
    // raise, while the raised args.Handled read back true regardless, which means Handled is not a
    // reliable signal here either and a case built on it would have been exactly as vacuous as the
    // one this finding named. Both cases below use the fallback the coordinator named for exactly
    // this outcome: a markup scan for the handler attributes on TimelineChart specifically (not
    // merely present somewhere in the file), which fails if PointerWheelChanged="OnTimelineWheel"
    // or any of its three siblings is removed from that element, plus the existing source-scan for
    // e.Handled's unconditional placement. HandleWheel itself, the pixel-to-period logic the
    // attribute wires to, is ImagingTimelineZoomPanTests.cs's proven surface by way of
    // StepGranularity, and is exercised directly here too so a deleted attribute is the only way
    // left for these two cases to pass without the real behaviour.
    private static string TimelineChartMarkup()
    {
        var path = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "StatisticsView.axaml");
        var source = SourceScan.StripComments(File.ReadAllText(path));

        var elementStart = source.IndexOf("x:Name=\"TimelineChart\"", StringComparison.Ordinal);
        Assert.True(elementStart >= 0, "TimelineChart was not found in StatisticsView.axaml");
        var tagStart = source.LastIndexOf("<lvc:CartesianChart", elementStart, StringComparison.Ordinal);
        Assert.True(tagStart >= 0, "TimelineChart's own opening tag was not found");
        var tagEnd = source.IndexOf("/>", elementStart, StringComparison.Ordinal);
        Assert.True(tagEnd >= 0, "TimelineChart's own closing /> was not found");

        return source[tagStart..(tagEnd + 2)];
    }

    [AvaloniaFact]
    public void AWheelOverTheChart_StepsTheGranularity()
    {
        var (window, view, page) = Show();
        var chart = view.GetControl<CartesianChart>("TimelineChart");
        var point = CenterOf(chart, window);
        var before = page.Timeline.Granularity;

        view.HandleWheel(chart, point.X, deltaY: 1);

        Assert.NotEqual(before, page.Timeline.Granularity);
        page.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public void AWheelWithNoVerticalComponent_DoesNotStepTheGranularity()
    {
        // Phase-review P3: HandleWheel maps deltaY > 0 ? 1 : -1, so a gesture with no vertical
        // component at all (a horizontal wheel, a tilt wheel, a trackpad sideways swipe) fell
        // into the -1 branch and stepped the granularity down. Zero must step neither way.
        //
        // Starting from Monthly (index 0 of the three granularities) would let this case pass
        // for the wrong reason: StepGranularity(-1, ...) from the lowest index clamps to the
        // same index regardless of whether the -1 step should have run at all. Weekly (index 1)
        // is not at that floor, so a spurious -1 is a real, detectable move to Monthly.
        var (window, view, page) = Show();
        var chart = view.GetControl<CartesianChart>("TimelineChart");
        var point = CenterOf(chart, window);
        page.Timeline.Granularity = TimelineGranularity.Weekly;
        var before = page.Timeline.Granularity;

        view.HandleWheel(chart, point.X, deltaY: 0);

        Assert.Equal(before, page.Timeline.Granularity);
        page.Dispose();
        window.Close();
    }

    [Fact]
    public void TheTimelineChart_WiresAllFourPointerHandlers()
    {
        var markup = TimelineChartMarkup();

        // The wheel is subscribed in code-behind with handledEventsToo: LiveCharts marks every
        // wheel handled in its own constructor-time subscription, so a markup handler never ran.
        var codeBehind = SourceScan.StripComments(File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "StatisticsView.axaml.cs")));
        Assert.Contains(
            "TimelineChart.AddHandler(PointerWheelChangedEvent, OnTimelineWheel, handledEventsToo: true);",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PointerWheelChanged=", markup, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"OnTimelinePointerPressed\"", markup, StringComparison.Ordinal);
        Assert.Contains("PointerMoved=\"OnTimelinePointerMoved\"", markup, StringComparison.Ordinal);
        Assert.Contains("PointerReleased=\"OnTimelinePointerReleased\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ACtrlWheelOverTheChart_IsHandled_AndAPlainOneIsLeftToThePage()
    {
        // WheelPassthrough's rule: the Ctrl guard returns before e.Handled, so a plain wheel
        // reaches the page scroller and Ctrl with the wheel steps the granularity.
        var path = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "StatisticsView.axaml.cs");
        var source = SourceScan.StripComments(File.ReadAllText(path));

        var bodyStart = source.IndexOf("void OnTimelineWheel", StringComparison.Ordinal);
        Assert.True(bodyStart >= 0, "OnTimelineWheel was not found in StatisticsView.axaml.cs");
        var body = source[bodyStart..(bodyStart + 400)];

        var guardIndex = body.IndexOf("if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))", StringComparison.Ordinal);
        var handledIndex = body.IndexOf("e.Handled = true;", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "OnTimelineWheel no longer guards on Ctrl.");
        Assert.True(handledIndex > guardIndex, "e.Handled is set before the Ctrl guard, so a plain wheel is swallowed.");
    }

    [AvaloniaFact]
    public void ALeftDragOverTheChart_PansTheRange()
    {
        var (window, view, page) = Show();
        var chart = view.GetControl<CartesianChart>("TimelineChart");
        var start = CenterOf(chart, window);
        var end = start + new Vector(200, 0);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(0, page.Timeline.PanPeriods);
        page.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public void ARightDragOverTheChart_DoesNothing()
    {
        var (window, view, page) = Show();
        var chart = view.GetControl<CartesianChart>("TimelineChart");
        var start = CenterOf(chart, window);
        var end = start + new Vector(200, 0);

        window.MouseDown(start, MouseButton.Right);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, page.Timeline.PanPeriods);
        page.Dispose();
        window.Close();
    }

    [AvaloniaFact]
    public void TheGranularitySelectorAndThePresets_StillWork()
    {
        var (window, view, page) = Show();

        var quarter = view.GetControl<StackPanel>("PresetButtons")
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button => (button.Content?.ToString()) == "Q");
        quarter.Command!.Execute(quarter.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TimelineRangePreset.Quarter, page.Timeline.RangePreset);
        Assert.Equal(TimelineGranularity.Weekly, page.Timeline.Granularity);

        var selector = view.GetControl<ComboBox>("GranularitySelector");
        selector.SelectedItem = page.Timeline.GranularityOptions.Single(option => option.Value == TimelineGranularity.Daily);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TimelineGranularity.Daily, page.Timeline.Granularity);

        page.Dispose();
        window.Close();
    }
}
