using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Phd2;
using GalactiLog.Data.Queries;
using Xunit;
using Vm = GalactiLog.App.Tests.ViewModels.GuideGraphViewModelTests;

namespace GalactiLog.App.Tests.Controls;

// Phase 15B Task 4a, brief sections 9.8 to 9.10: the headless render tick in NightStripTests'
// idiom, the thin-handler source case, and the colour-literal source case.
public class GuideGraphTests
{
    private static readonly ImmutableSolidColorBrush Ink = new(Colors.Gainsboro);
    private static readonly ImmutableSolidColorBrush Worse = new(Colors.IndianRed);

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    // Loaded before it is handed to a control: the frames read publishes from a pool thread under
    // this inline post, and a control must only ever hear a change on the UI thread.
    private static GuideGraphViewModel Model(IReadOnlyList<Phd2FramePoint> frames, params Phd2Event[] events)
    {
        var vm = new GuideGraphViewModel(
            [Vm.Session(Guid.NewGuid())],
            _ => Vm.Frames(frames, events),
            Zone,
            true,
            a => a(),
            s => FormattableString.Invariant($"{s:0}s"));
        vm.PendingLoad.GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>121 frames over 600 seconds, one of them dropped, with one dither, one ordinary
    /// settle and one failed settle, so a render pass runs all five layers.</summary>
    private static GuideGraphViewModel Populated()
    {
        var frames = Vm.Every(121, 5, i => Math.Sin(i / 6d));
        frames[60] = Vm.F(300, null, null, dropped: true);
        return Model(
            frames,
            new Phd2Event(Phd2EventTypes.Dither, 100, ""),
            new Phd2Event(Phd2EventTypes.SettleStart, 100, ""),
            new Phd2Event(Phd2EventTypes.SettleDone, 120, ""),
            new Phd2Event(Phd2EventTypes.SettleStart, 300, ""),
            new Phd2Event(Phd2EventTypes.SettleFailed, 330, ""));
    }

    // Every brush set, because an unset one draws nothing and would skip the paths a render case
    // exists to run.
    private static (Window Window, GuideGraph Graph) Show(GuideGraphViewModel? model)
    {
        var graph = new GuideGraph
        {
            Model = model,
            RaBrush = Ink,
            DecBrush = Ink,
            DropBrush = Worse,
            DitherBrush = Ink,
            SettleBrush = Ink,
            AxisBrush = Ink,
            GridBrush = Ink,
            LabelBrush = Ink,
        };

        var window = new Window { Width = 800, Height = 260, Content = graph };
        window.Show();
        Tick();
        return (window, graph);
    }

    // window.Show plus RunJobs pumps the dispatcher but does not drive the render timer, so
    // without the forced tick Render never executes.
    private static void Tick()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Point At(Window window, GuideGraph graph, double xFraction, double yFraction = 0.5)
    {
        var plot = graph.PlotRect;
        var local = new Point(plot.X + (xFraction * plot.Width), plot.Y + (yFraction * plot.Height));
        return graph.TranslatePoint(local, window) ?? local;
    }

    // ------------------------------------------------------------------ 9.8 the render tick

    [AvaloniaFact]
    public void GuideGraph_RendersAllFiveLayers()
    {
        var (_, graph) = Show(Populated());

        Assert.True(graph.Bounds.Width > 0 && graph.PlotRect.Width > 0);
        Assert.Equal(new GuideGraph.DrawCounts(2, 1, 120, 120, 1, 1), graph.LastDraw);
        Assert.False(graph.Focusable);
        Assert.False(graph.IsTabStop);
    }

    [AvaloniaFact]
    public void GuideGraph_AHiddenLayer_IsNotDrawnAtAll_AndAToggleRepaints()
    {
        var model = Populated();
        var (_, graph) = Show(model);

        model.Legend[(int)GuideLayer.Settling].IsShown = false;
        model.Legend[(int)GuideLayer.Ra].IsShown = false;
        model.Legend[(int)GuideLayer.Dither].IsShown = false;
        Tick();

        Assert.Equal(new GuideGraph.DrawCounts(0, 0, 0, 120, 1, 0), graph.LastDraw);
    }

    [AvaloniaFact]
    public void GuideGraph_ZoomedIn_DrawsOnlyTheOverlaysInsideTheWindow()
    {
        var model = Populated();
        var (_, graph) = Show(model);

        model.ZoomTime(320, 12);
        Tick();

        Assert.True(model.TimeView.Min > 120 && model.TimeView.Max < 600);
        Assert.Equal(1, graph.LastDraw.Bands);
        Assert.Equal(1, graph.LastDraw.FailedBands);
        Assert.Equal(0, graph.LastDraw.Dithers);
        Assert.True(graph.LastDraw.DecPoints is > 0 and < 120);
    }

    /// <summary>A failure looks like: a division by a zero span, or an index into an empty list.
    /// </summary>
    [AvaloniaFact]
    public void GuideGraph_WithNoModel_NoFrames_AllNullValues_OrOneFrame_DoesNotThrow()
    {
        var (window, none) = Show(null);
        Assert.Equal(default, none.LastDraw);
        window.MouseMove(At(window, none, 0.5));
        window.MouseDown(At(window, none, 0.5), MouseButton.Left);
        window.MouseUp(At(window, none, 0.5), MouseButton.Left);
        window.Close();

        var (w0, empty) = Show(Model([]));
        Assert.Equal(default, empty.LastDraw);
        w0.MouseMove(At(w0, empty, 0.5));
        w0.Close();

        var (w1, nulls) = Show(Model([.. Enumerable.Range(0, 50).Select(i => Vm.F(i, null, null))]));
        Assert.Equal(new GuideGraph.DrawCounts(0, 0, 0, 0, 0, 0), nulls.LastDraw);
        w1.Close();

        var (w2, one) = Show(Model([Vm.F(0, 0.4, -0.4)]));
        Assert.Equal(1, one.LastDraw.RaPoints);
        Assert.Equal(1, one.LastDraw.DecPoints);
        w2.MouseMove(At(w2, one, 0.25));
        Tick();
        Assert.StartsWith("19:45:00", (string?)ToolTip.GetTip(one), StringComparison.Ordinal);
        w2.Close();
    }

    [AvaloniaFact]
    public void GuideGraph_UnderAnUnboundedMeasure_ReservesItsNamedDefault()
    {
        var graph = new GuideGraph();
        graph.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(new Size(GuideGraph.DefaultWidth, GuideGraph.DefaultHeight), graph.DesiredSize);

        graph.Measure(new Size(640, 180));
        Assert.Equal(new Size(640, 180), graph.DesiredSize);
    }

    // ------------------------------------------------------------------ the gestures reach the model

    [AvaloniaFact]
    public void GuideGraph_Hover_NamesTheNearestFrame_AndAModelSwapClearsIt()
    {
        var (window, graph) = Show(Populated());

        window.MouseMove(At(window, graph, 0.5));
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("19:50:00", (string?)ToolTip.GetTip(graph), StringComparison.Ordinal);
        Assert.Contains("Star lost", (string?)ToolTip.GetTip(graph), StringComparison.Ordinal);

        graph.Model = Model([Vm.F(0)]);
        Assert.Null(ToolTip.GetTip(graph));
    }

    [AvaloniaFact]
    public void GuideGraph_ADrag_PansAZoomedWindow_SoTheDataFollowsThePointer()
    {
        var model = Populated();
        var (window, graph) = Show(model);

        // No slack while both axes show everything. Pressed away from the second press below,
        // which would otherwise arrive as a double click and reset the view.
        window.MouseDown(At(window, graph, 0.1, 0.1), MouseButton.Left);
        window.MouseMove(At(window, graph, 0.3, 0.1));
        window.MouseUp(At(window, graph, 0.3, 0.1), MouseButton.Left);
        Assert.Null(model.TimeOverride);

        model.ZoomTime(300, 5);
        var before = model.TimeView;
        var secondsUnderThePress = graph.SecondsAt(model, graph.PlotRect.X + (0.5 * graph.PlotRect.Width));

        window.MouseDown(At(window, graph, 0.5), MouseButton.Left);
        window.MouseMove(At(window, graph, 0.7));
        window.MouseUp(At(window, graph, 0.7), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Dragging right moves the window earlier, and the second that was under the press is
        // now under the release.
        Assert.True(model.TimeView.Min < before.Min);
        Assert.Equal(before.Max - before.Min, model.TimeView.Max - model.TimeView.Min, 6);
        Assert.Equal(
            secondsUnderThePress,
            graph.SecondsAt(model, graph.PlotRect.X + (0.7 * graph.PlotRect.Width)),
            6);

        // A released drag is over: a further move pans nothing.
        var after = model.TimeView;
        window.MouseMove(At(window, graph, 0.2));
        Assert.Equal(after, model.TimeView);
    }

    [AvaloniaFact]
    public void GuideGraph_CtrlWheel_ZoomsTime_AndCtrlShiftWheel_ZoomsArcseconds()
    {
        // Ctrl is the zoom modifier since WheelPassthrough: a plain wheel is left to the pane.
        var model = Populated();
        var (window, graph) = Show(model);

        window.MouseWheel(At(window, graph, 0.5), new Vector(0, 1));
        Assert.Null(model.TimeOverride);

        window.MouseWheel(At(window, graph, 0.5), new Vector(0, 1), RawInputModifiers.Control);
        Assert.NotNull(model.TimeOverride);
        Assert.Null(model.ArcsecOverride);
        Assert.Equal(600 * GuideGraphViewModel.ZoomStep, model.TimeView.Max - model.TimeView.Min, 6);

        // A sideways wheel arrives with a zero Y delta and steps nothing.
        var held = model.TimeView;
        window.MouseWheel(At(window, graph, 0.5), new Vector(1, 0), RawInputModifiers.Control);
        Assert.Equal(held, model.TimeView);

        window.MouseWheel(At(window, graph, 0.5), new Vector(0, 1), RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.NotNull(model.ArcsecOverride);
    }

    [AvaloniaFact]
    public void GuideGraph_ADoubleClick_ResetsBothAxes()
    {
        var model = Populated();
        var (window, graph) = Show(model);
        model.ZoomTime(300, 5);
        model.ZoomArcsec(0, -2);

        var point = At(window, graph, 0.5);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Null(model.TimeOverride);
        Assert.Null(model.ArcsecOverride);
    }

    [AvaloniaFact]
    public void GuideGraph_PixelToData_RunsArcsecondsUpward()
    {
        var model = Populated();
        var (_, graph) = Show(model);
        var plot = graph.PlotRect;

        Assert.Equal(model.TimeView.Min, graph.SecondsAt(model, plot.X), 9);
        Assert.Equal(model.TimeView.Max, graph.SecondsAt(model, plot.Right), 9);
        Assert.Equal(model.ArcsecView.Max, graph.ArcsecAt(model, plot.Y), 9);
        Assert.Equal(model.ArcsecView.Min, graph.ArcsecAt(model, plot.Bottom), 9);
    }

    // ------------------------------------------------------------------ 9.9 the handlers are thin

    private static string Source() => File.ReadAllText(
        Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Controls", "GuideGraph.cs"));

    /// <summary>A failure looks like: a helpful clamp added in a handler because a drag felt
    /// loose, which passes every transform case (they never run the handler) and drifts the first
    /// time the view-model's own clamp is tuned.</summary>
    [Fact]
    public void GuideGraphSource_HandlersAreThin()
    {
        var code = SourceScan.StripComments(Source());

        Assert.DoesNotContain("Math.Clamp", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Min", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max", code, StringComparison.Ordinal);
        Assert.DoesNotContain("0.82", code, StringComparison.Ordinal);
        Assert.DoesNotContain("2000", code, StringComparison.Ordinal);

        // Each gesture is one call into the view-model, and each of them is there.
        Assert.Single(Regex.Matches(code, @"model\.ZoomTime\("));
        Assert.Single(Regex.Matches(code, @"model\.ZoomArcsec\("));
        Assert.Single(Regex.Matches(code, @"model\.Pan\("));
        Assert.Single(Regex.Matches(code, @"model\.ResetView\("));
        Assert.Single(Regex.Matches(code, @"model\.HoverText\("));
        Assert.Contains("e.Handled = true;", code, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- Phase 15B fixer F9, one label culture

    /// <summary>
    /// Phase 15B fixer F9. The two drawn controls this phase added build their <c>FormattedText</c>
    /// under one culture, and it is the invariant one, which is what every string handed to either
    /// was already formatted under.
    /// </summary>
    /// <remarks>
    /// A failure looks like a machine whose locale shapes digits drawing the guide graph's clock
    /// and arcsecond labels in one numeral set and the altitude arc's degree labels beside it in
    /// another, on the same page. Written over both files because the defect is a disagreement
    /// between them, not a property of either alone.
    /// </remarks>
    [Fact]
    public void NeitherDrawnControl_FormatsItsLabelsUnderTheCurrentCulture()
    {
        var altitudeArc = File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Controls", "AltitudeArc.cs"));

        foreach (var code in new[] { SourceScan.StripComments(Source()), SourceScan.StripComments(altitudeArc) })
        {
            Assert.DoesNotContain("CurrentCulture", code, StringComparison.Ordinal);
            Assert.Contains("CultureInfo.InvariantCulture", code, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ 9.10 no colour literal

    // The needle is NightStripTests.NightStripSource_ContainsNoColourLiteral's, line for line,
    // over the raw source with comments included.
    [Fact]
    public void GuideGraphSource_ContainsNoColourLiteral()
    {
        var source = Source();
        var offenders = new List<string>();

        if (source.Contains("Color.FromArgb", StringComparison.Ordinal)
            || source.Contains("Color.FromRgb", StringComparison.Ordinal)
            || source.Contains("Color.Parse", StringComparison.Ordinal))
        {
            offenders.Add("a Color factory call");
        }

        if (source.Contains("Colors.", StringComparison.Ordinal))
        {
            offenders.Add("a Colors.* member");
        }

        foreach (Match match in Regex.Matches(source, @"Brushes\.(\w+)"))
        {
            if (match.Groups[1].Value != "Transparent")
            {
                offenders.Add(match.Value);
            }
        }

        foreach (Match match in Regex.Matches(source, @"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})\b"))
        {
            offenders.Add(match.Value);
        }

        Assert.True(
            offenders.Count == 0,
            "GuideGraph.cs takes every colour from a styled property set by the host. Found: "
                + string.Join(", ", offenders));
    }

    // The strip owns the selected frame; a failure is a handler a disposed chart or a detached
    // guide graph leaves on it, read off the event's invocation list.
    [AvaloniaFact]
    public void TheStrip_HoldsNoHandler_OfADisposedChartOrADetachedGuideGraph()
    {
        var date = new DateOnly(2025, 3, 9);
        var frames = GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit.Frames(40, date);
        var strip = new NightStripViewModel(frames, new Dictionary<string, ImmutableSolidColorBrush>(), Ink, null, Zone, use24Hour: true);
        static int Handlers(NightStripViewModel model)
            => ((Delegate?)typeof(NightStripViewModel)
                .GetField(nameof(NightStripViewModel.ActiveFrameChanged), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(model))?.GetInvocationList().Length ?? 0;

        GalactiLog.Core.Settings.GraphSettings stored = new();
        var selection = new ChartSelectionViewModel(
            stored,
            new GalactiLog.App.Services.GraphSettingsWriter(() => stored, _ => { }),
            () => new GalactiLog.Core.Aliases.AliasMap(new Dictionary<string, GalactiLog.Core.Settings.FilterSetting>(), new GalactiLog.Core.Settings.EquipmentSettings()));
        var chart = new SessionChartViewModel(SessionCardViewModelTestFactory.PopulatedDetail(sessionDate: date) with { Frames = frames }, selection);
        chart.UseLaneAxis(strip.LaneAxis, Zone, strip);
        Assert.Equal(1, Handlers(strip));
        chart.Dispose();
        Assert.Equal(0, Handlers(strip));

        var (window, graph) = Show(Populated());
        graph.Strip = strip;
        Assert.Equal(1, Handlers(strip));
        window.Content = null;
        Tick();
        Assert.Equal(0, Handlers(strip));
        window.Content = graph;
        Tick();
        Assert.Equal(1, Handlers(strip));
        graph.Strip = null;
        Assert.Equal(0, Handlers(strip));
        window.Close();
    }
}
