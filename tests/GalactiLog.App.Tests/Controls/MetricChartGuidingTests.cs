using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.Views.TargetDetail.Parts;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Controls;

// Phase 24 R3: the night chart's Guiding pill. The guide graph lives in the chart's plot slot, the
// five-layer legend in the filter pills' slot, and the night's figures on one line under the pills.
// The GuidingPart cases that still apply (the legend, the figures, the gestures) moved here.
public class MetricChartGuidingTests
{
    private static readonly DateOnly Date = Page.LastSession;
    private static readonly DateTime FirstFrameUtc = Date.ToDateTime(new TimeOnly(21, 0), DateTimeKind.Utc);

    private sealed class Night : IDisposable
    {
        public required Cards.Harness Harness { get; init; }
        public required Window Window { get; init; }
        public required MetricChartView View { get; init; }
        public required List<GraphSettings> Written { get; init; }

        /// <summary>The order the two publications landed in: "chart" when the detail query's
        /// publish built the chart (which hands the section to it), "graph" when the night
        /// query's publish built the guide graph. The harness posts inline from two pool threads,
        /// so either order happens.</summary>
        public required List<string> Order { get; init; }

        public string OrderText
        {
            get { lock (Order) { return string.Join(",", Order); } }
        }

        public SessionChartViewModel Chart => (SessionChartViewModel)Harness.Card.Chart!;
        public GuideGraph Graph => View.GetControl<GuideGraph>("GuidePlot");
        public ToggleButton Pill => View.GetControl<ToggleButton>("GuidingPill");
        public Border PlotHost => View.GetControl<Border>("PlotHost");

        public void PillOn(bool on)
        {
            Pill.IsChecked = on;
            Tick();
        }

        public void Dispose()
        {
            Window.Close();
            Harness.Dispose();
        }
    }

    private static Phd2SessionSummary SessionAt(DateTime startUtc)
        => NightPartsTestKit.GuideSession(0) with { StartedAtUtc = startUtc };

    // A night of 40 frames from 21:00 with no observer position, so the shared axis is the
    // frames' own span; the chart is the card's, wired to its Guiding section as production wires it.
    private static Night Mount(
        bool anyGuideLogs = true, bool showGuiding = false, bool viaPart = false, params Phd2SessionSummary[] sessions)
    {
        ChartTheme.Apply();
        var frames = NightPartsTestKit.Frames(40, Date);
        var detail = Cards.PopulatedDetail(sessionDate: Date) with { Frames = frames };
        var harness = Cards.Create(
            detail: detail,
            general: new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            anyGuideLogs: anyGuideLogs);
        var stored = new GraphSettings { EnabledMetrics = ["hfr", "fwhm"], EnabledFilters = ["overall"], ShowGuiding = showGuiding };
        var written = new List<GraphSettings>();
        var selection = new ChartSelectionViewModel(
            stored,
            new GraphSettingsWriter(() => stored, graph => { lock (written) { written.Add(graph); } }),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));
        harness.FrameTableResult = NightPartsTestKit.Table(frames);
        harness.ChartResult = new SessionChartViewModel(harness.Detail!, selection);

        IReadOnlyList<Phd2SessionSummary> list = sessions.Length == 0 ? [SessionAt(FirstFrameUtc.AddMinutes(-10))] : sessions;
        harness.GuidingResult = new Phd2NightGuiding(
            new Phd2NightSummary
            {
                SessionCount = list.Count,
                RmsTotalArcsec = 0.66d,
                RmsRaArcsec = 0.42d,
                RmsDecArcsec = 0.51d,
                DitherCount = 3,
                SettleMedianS = 2.5d,
            },
            list);
        harness.FramesResult = id => new Phd2SessionFrames(
            1.5d,
            list.First(session => session.Id == id).StartedAtUtc,
            [.. Enumerable.Range(0, 100).Select(i => new Phd2FramePoint(i * 60d, 0.3d, -0.2d, 0, "", 0, "", null, null, false))],
            []);
        var order = new List<string>();
        harness.Card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionCardViewModel.Chart) && harness.Card.Chart is not null)
            {
                lock (order) { order.Add("chart"); }
            }
        };
        if (harness.Card.Guiding is { } section)
        {
            section.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GuidingSectionViewModel.Graph) && section.Graph is not null)
                {
                    lock (order) { order.Add("graph"); }
                }
            };
        }

        harness.Card.IsExpanded = true;
        harness.Settle();
        harness.SettleGuiding().SettleFrames();

        // One window per case: a second window with a chart in it is what the headless chart
        // backend refuses ("SkiaSharp is not supported"), so the part form hosts in the same root.
        Control content;
        if (viaPart)
        {
            content = new NightMetricsPart { DataContext = harness.Card };
        }
        else
        {
            var view = new MetricChartView { DataContext = harness.Card.Chart };
            view.Classes.Add("fills");
            content = view;
        }

        var root = new Grid { RowDefinitions = new RowDefinitions("180"), Width = 740 };
        root.Children.Add(content);
        var window = NightPartsTestKit.Show(root, 740, 400);
        Tick();
        var hosted = root.GetVisualDescendants().OfType<MetricChartView>().Single();
        return new Night { Harness = harness, Window = window, View = hosted, Written = written, Order = order };
    }

    private static void Tick()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Point At(Window window, GuideGraph graph, double xFraction)
    {
        var plot = graph.PlotRect;
        var local = new Point(plot.X + (xFraction * plot.Width), plot.Y + (0.5 * plot.Height));
        return graph.TranslatePoint(local, window) ?? local;
    }

    private static IReadOnlyList<string> VisibleTexts(Control root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text ?? "")];

    // A failure is the pill on the trend chart, or enabled on a night with no guide log, or a
    // stored flag drawing a trace the night does not have.
    [AvaloniaFact]
    public void ThePill_IsOnTheNightChartOnly_AndDisabledWithoutAGuideLog()
    {
        using var night = Mount(anyGuideLogs: false, showGuiding: true);
        Assert.True(night.Pill.IsVisible);
        Assert.False(night.Pill.IsEnabled);
        Assert.True(night.Chart.ShowGuiding);
        Assert.False(night.Chart.ShowsGuiding);
        Assert.True(night.PlotHost.IsVisible);
        Assert.False(night.Graph.IsVisible);

        // The same view over the trend chart: the pill goes with the night chart, not the control.
        using var trend = new TargetChartViewModel(new ObservableCollection<SessionCardViewModel> { night.Harness.Card }, night.Chart.Selection);
        night.View.DataContext = trend;
        Tick();

        Assert.False(trend.HasGuidingPill);
        Assert.False(night.Pill.IsVisible);
        Assert.False(night.Graph.IsVisible);
        Assert.True(night.PlotHost.IsVisible);
    }

    // A failure is the metric dots still drawn under the trace, the metric set changed by the
    // pill, the filter pills still in the row, or the help glyph on the wrong topic.
    [AvaloniaFact]
    public async Task ThePill_SwapsTheDotsForTheTrace_KeepsTheMetricSet_AndSwapsTheLegendAndStats()
    {
        using var night = Mount();
        var view = night.View;
        var metricPills = view.GetControl<ItemsControl>("MetricPills");
        var filterPills = view.GetControl<ItemsControl>("FilterPills");
        var legend = view.GetControl<WrapPanel>("GuideLegend");
        var stats = view.GetControl<StackPanel>("GuidingStats");

        Assert.True(night.Pill.IsEnabled);
        Assert.False(night.Pill.IsChecked);
        Assert.True(night.PlotHost.IsVisible);
        Assert.False(night.Graph.IsVisible);
        Assert.False(legend.IsVisible);
        Assert.False(stats.IsVisible);
        Assert.Equal("target.session-metrics", night.Chart.HelpTopicId);

        night.PillOn(true);

        Assert.True(night.Chart.ShowsGuiding);
        Assert.False(night.PlotHost.IsVisible);
        Assert.True(night.Graph.IsVisible);
        Assert.False(metricPills.IsEnabled);
        Assert.False(filterPills.IsVisible);
        Assert.True(legend.IsVisible);
        Assert.True(stats.IsVisible);
        Assert.False(view.GetControl<TextBlock>("EmptyState").IsVisible);
        Assert.Equal(["hfr", "fwhm"], night.Chart.Selection.EnabledMetrics.Select(metric => metric.Key));
        Assert.Equal("target.guiding", night.Chart.HelpTopicId);
        Assert.Same(night.Harness.Card.Guiding!.Graph, night.Graph.Model);

        var texts = VisibleTexts(view);
        Assert.Contains("0.66 arcsec", texts);
        Assert.Contains("Sessions", texts);
        Assert.Contains("Dithers", texts);
        Assert.Contains("2.5s", texts);
        Assert.Empty(view.GetVisualDescendants().OfType<ComboBox>());

        night.PillOn(false);

        Assert.True(night.PlotHost.IsVisible);
        Assert.False(night.Graph.IsVisible);
        Assert.True(metricPills.IsEnabled);
        Assert.True(filterPills.IsVisible);
        Assert.False(legend.IsVisible);
        Assert.False(stats.IsVisible);
        Assert.Equal(["hfr", "fwhm"], night.Chart.Selection.EnabledMetrics.Select(metric => metric.Key));

        await night.Chart.Selection.PendingPersist;
        lock (night.Written)
        {
            Assert.Equal([true, false], night.Written.Select(graph => graph.ShowGuiding));
            Assert.All(night.Written, graph => Assert.Equal(["hfr", "fwhm"], graph.EnabledMetrics));
        }
    }

    // A failure is a profile that stored the pill on opening with the dots.
    [AvaloniaFact]
    public void TheStoredFlag_OpensWithTheTrace()
    {
        using var night = Mount(showGuiding: true);

        Assert.True(night.Pill.IsChecked);
        Assert.True(night.Graph.IsVisible);
        Assert.False(night.PlotHost.IsVisible);
        Assert.True(night.Graph.LastDraw.RaPoints > 0);
    }

    // Moved from GuidingPartTests: a hidden entry's layer is not drawn and its label is struck
    // through, and the entries keep their state across the pill.
    [AvaloniaFact]
    public void ALegendEntry_TogglesItsLayer_IsStruckThroughWhileHidden_AndSurvivesThePill()
    {
        using var night = Mount(showGuiding: true);
        var graph = night.Harness.Card.Guiding!.Graph!;
        string[] names = ["GuideLegendRa", "GuideLegendDec", "GuideLegendStarLost", "GuideLegendDither", "GuideLegendSettling"];
        var toggles = names.Select(name => night.View.GetControl<ToggleButton>(name)).ToList();
        Assert.All(toggles, toggle => Assert.True(toggle.IsChecked));

        toggles[0].IsChecked = false;
        Tick();

        Assert.False(graph.IsShown(GuideLayer.Ra));
        Assert.True(graph.IsShown(GuideLayer.Dec));
        Assert.Equal(0, night.Graph.LastDraw.RaPoints);
        Assert.True(night.Graph.LastDraw.DecPoints > 0);
        var struck = Assert.Single(toggles[0].GetVisualDescendants().OfType<TextBlock>(), block => block.IsVisible);
        Assert.NotNull(struck.TextDecorations);
        Assert.Equal("RA", struck.Text);

        night.PillOn(false);
        night.PillOn(true);
        Assert.False(graph.IsShown(GuideLayer.Ra));
        Assert.False(toggles[0].IsChecked);
    }

    // A failure is only the selected session drawn, a session before the first frame pulled onto
    // the axis instead of clipped past its left edge, or the two sessions' traces joined.
    [AvaloniaFact]
    public void EverySessionOfTheNight_IsDrawnOnTheNightAxis_AndClippedOutsideIt()
    {
        var early = SessionAt(FirstFrameUtc.AddHours(-3));
        var late = SessionAt(FirstFrameUtc.AddMinutes(30));
        using var night = Mount(showGuiding: true, sessions: [early, late]);
        var graph = night.Graph;
        var model = graph.Model!;

        Assert.True(model.DrawsWholeNight, $"not whole night; order {night.OrderText}");
        Assert.True(model.IsOnLaneAxis, $"off the lane axis; order {night.OrderText}");
        Assert.Equal(200, graph.LastDraw.RaPoints);
        Assert.Contains(GuideGraphViewModel.Break, model.Plotted);
        Assert.Equal(0d, model.TimeRange.Min);
        Assert.Equal((late.StartedAtUtc - early.StartedAtUtc).TotalSeconds + (99 * 60d), model.TimeRange.Max, 6);
        Assert.True(graph.XAt(model.TimeRange.Min) < graph.PlotRect.X, "the early session is not clipped past the plot's left edge");
        Assert.True(graph.XAt(model.TimeRange.Max) > graph.PlotRect.X, "the late session is off the plot");
        Assert.True(night.Harness.FrameRequests.SequenceEqual([early.Id, late.Id]), $"reads {night.Harness.FrameRequests.Count}; order {night.OrderText}");
    }

    // Moved from GuideGraphTests' shape onto the hosted graph: the night axis's gestures, and a
    // plain wheel left to the pane.
    [AvaloniaFact]
    public void TheHostedGraph_AnswersTheGestures_AndAPlainWheelScrollsThePane()
    {
        using var night = Mount(showGuiding: true);
        var graph = night.Graph;
        var model = graph.Model!;
        var window = night.Window;

        window.MouseWheel(At(window, graph, 0.5), new Vector(0, 1));
        Assert.Null(model.TimeOverride);

        window.MouseWheel(At(window, graph, 0.5), new Vector(0, 1), RawInputModifiers.Control);
        Assert.NotNull(model.TimeOverride);
        Assert.False(model.IsOnLaneAxis);

        window.MouseWheel(At(window, graph, 0.5), new Vector(0, 1), RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.NotNull(model.ArcsecOverride);

        var before = model.TimeView;
        window.MouseDown(At(window, graph, 0.5), MouseButton.Left);
        window.MouseMove(At(window, graph, 0.7));
        window.MouseUp(At(window, graph, 0.7), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.TimeView.Min < before.Min);

        var point = At(window, graph, 0.3);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Null(model.TimeOverride);
        Assert.Null(model.ArcsecOverride);
        Assert.True(model.IsOnLaneAxis);
    }

    // Ruling R10: the host's one help glyph reads the guiding topic while the pill is on.
    [AvaloniaFact]
    public void TheHelpGlyph_FollowsThePill()
    {
        using var night = Mount(viaPart: true);
        var help = night.View.GetVisualDescendants().OfType<HelpButton>().Single();

        Assert.Equal("target.session-metrics", help.Topic);

        night.PillOn(true);
        Assert.Equal("target.guiding", help.Topic);

        night.PillOn(false);
        Assert.Equal("target.session-metrics", help.Topic);
    }
}
