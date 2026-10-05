using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Controls;

// Design-spec 18.3's view smoke tests for spec 13's metric chart: it parses, lays out, and binds
// against both of its view-models. Compiled bindings already turn a binding-path typo into a build
// error; these catch the rest (a missing resource, a template that cannot realize, a ratio key
// bound to FontSize). Nothing here compares pixels, which spec 18.3 puts out of scope.
public class MetricChartViewTests
{
    private static ChartSelectionViewModel Selection(
        string[]? metrics = null, string[]? filters = null, GraphSettings? document = null)
    {
        var stored = document ?? new GraphSettings
        {
            EnabledMetrics = metrics ?? ["hfr", "fwhm"],
            EnabledFilters = filters ?? ["overall"],
            DefaultChartSessions = 1,
        };

        return new ChartSelectionViewModel(
            stored,
            new GraphSettingsWriter(() => stored, _ => { }),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));
    }

    private static SessionDetail SessionWithFrames()
    {
        var detail = Cards.PopulatedDetail();
        FrameRow[] frames =
        [
            FrameAt(detail.SessionDate, 1, 2.1d),
            FrameAt(detail.SessionDate, 2, 2.4d),
            FrameAt(detail.SessionDate, 3, 2.2d),
        ];

        return detail with { Frames = frames };
    }

    private static FrameRow FrameAt(DateOnly date, int index, double hfr) => new(
        ImageId: Guid.NewGuid(),
        FilePath: $@"C:\Astro\M 31\frame_{index:0000}.fits",
        FileName: $"frame_{index:0000}.fits",
        CaptureDate: date.ToDateTime(new TimeOnly(21, 0)).AddMinutes(index * 5),
        FilterUsed: "Ha",
        ExposureTime: 300d,
        MedianHfr: hfr,
        Eccentricity: 0.4d,
        Fwhm: 1.9d,
        DetectedStars: 1490,
        GuidingRmsArcsec: 0.45d,
        GuidingRmsRaArcsec: null,
        GuidingRmsDecArcsec: null,
        GuidingRmsSource: null,
        AduMean: null,
        AduMedian: null,
        AduStdev: null,
        AduMin: null,
        AduMax: null,
        FocuserPosition: null,
        FocuserTemp: null,
        AmbientTemp: null,
        DewPoint: null,
        Humidity: null,
        Pressure: null,
        WindSpeed: null,
        WindDirection: null,
        WindGust: null,
        CloudCover: null,
        SkyQuality: null,
        Airmass: null,
        PierSide: null,
        RotatorPosition: null,
        SensorTemp: null,
        CameraGain: 100,
        Rig: "RC8 / ASI2600MM",
        IsHfrOutlier: false,
        IsEccentricityOutlier: false);

    private static Window Show(MetricChartView view)
    {
        var window = new Window { Width = 1600, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(MetricChartView view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    [AvaloniaFact]
    public void MetricChartView_Constructs_AndBindsAPopulatedTargetChart()
    {
        ChartTheme.Apply();
        using var card = Cards.Create(overview: Page.Session(Page.LastSession));
        ObservableCollection<SessionCardViewModel> sessions = [card.Card];
        using var chart = new TargetChartViewModel(sessions, Selection());

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        // The collapse Expander is gone and so is the header it carried. The band that
        // hosts this control is the disclosure and names the row ("Trend across nights"), so the
        // chart's own Title is no longer rendered here and the page does not title it twice.
        Assert.Equal("Cross-session metric trend", chart.Title);
        Assert.DoesNotContain("Cross-session metric trend", VisibleTexts(view));

        // The five metric pills and the two filter pills are realized (the overall sentinel plus
        // the one filter the session actually used), and the chart control has the series the
        // view-model built.
        Assert.Equal(5, view.GetControl<ItemsControl>("MetricPills").GetVisualDescendants().OfType<ToggleButton>().Count());
        Assert.Equal(2, view.GetControl<ItemsControl>("FilterPills").GetVisualDescendants().OfType<ToggleButton>().Count());
        Assert.Equal(chart.Series, view.GetControl<CartesianChart>("Chart").Series);
        Assert.False(view.GetControl<TextBlock>("EmptyState").IsVisible);
    }

    [AvaloniaFact]
    public void MetricChartView_Constructs_AndBindsAPopulatedSessionChart()
    {
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection());

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        Assert.Equal("Per-session frame metrics", chart.Title);
        Assert.DoesNotContain("Per-session frame metrics", VisibleTexts(view));
        Assert.Equal(chart.Series, view.GetControl<CartesianChart>("Chart").Series);
        Assert.Equal(chart.Sections, view.GetControl<CartesianChart>("Chart").Sections);

        // The per-session chart has no session scope to widen (that scope belongs to the cross-session
        // chart's), so that row is absent rather than present and inert.
        Assert.False(view.GetControl<StackPanel>("SessionScope").IsVisible);
    }

    [AvaloniaFact]
    public void MetricChartView_ChartBackground_IsTransparent()
    {
        // Spec 13's first global-configuration bullet, "chart background transparent so the page
        // gradient shows through". It is an Avalonia control property, so it cannot be asserted in
        // ChartTheme's tests and is asserted here rather than assumed.
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection());

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        var background = view.GetControl<CartesianChart>("Chart").Background;
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(background).Color);
    }

    [AvaloniaFact]
    public void MetricChartView_Legend_IsHidden()
    {
        // The pills are the legend. The box under the chart listed the same series in the
        // same colours a second time, so it is off, and off is stated in the markup rather than
        // left to rc5.4's default.
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection());

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        Assert.Equal(LegendPosition.Hidden, view.GetControl<CartesianChart>("Chart").LegendPosition);
    }

    [AvaloniaFact]
    public void MetricChartView_PillRow_WrapsInsteadOfClippingAtNarrowWidth()
    {
        // A failure here is a pill whose right edge lies past the pill row's width.
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(
            SessionWithFrames(),
            Selection(
                metrics: ["hfr", "fwhm", "eccentricity", "snr", "background", "stars", "rms"],
                filters: ["overall", "Luminance", "Red", "Green", "Blue", "Ha", "OIII", "SII"]));

        var view = new MetricChartView { DataContext = chart };
        var window = new Window { Width = 600, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var row = view.GetControl<Control>("PillRow");
        var pills = row.GetVisualDescendants().OfType<ToggleButton>().ToList();
        Assert.True(pills.Count >= 8, $"{pills.Count} pills");
        foreach (var pill in pills)
        {
            var right = pill.TranslatePoint(new Point(pill.Bounds.Width, 0), row)!.Value.X;
            Assert.True(right <= row.Bounds.Width + 0.5, $"{pill.Bounds} right {right} past row {row.Bounds.Width}");
        }
    }

    [AvaloniaFact]
    public void MetricChartView_PillRow_HoldsBothPillStrips()
    {
        // One row: the two strips keep their names, their sources and their templates,
        // and sit side by side instead of stacking.
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection());

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        var row = view.GetControl<WrapPanel>("PillRow");
        Assert.Equal(Orientation.Horizontal, row.Orientation);
        Assert.Contains(view.GetControl<ItemsControl>("MetricPills"), row.GetVisualDescendants().OfType<ItemsControl>());
        Assert.Contains(view.GetControl<ItemsControl>("FilterPills"), row.GetVisualDescendants().OfType<ItemsControl>());
    }

    [AvaloniaFact]
    public void MetricChartView_APill_CarriesADotAndNoTintedBorder()
    {
        // A pill loses its 2 px tinted border and carries a 7 px filled dot in the same
        // colour instead, which is the direction's rule that colour is spent on data and the
        // control itself stays a flat outline.
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection());

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        var pills = view.GetControl<ItemsControl>("MetricPills")
            .GetVisualDescendants()
            .OfType<ToggleButton>()
            .ToList();

        Assert.NotEmpty(pills);
        Assert.All(pills, pill => Assert.Equal(new Thickness(1), pill.BorderThickness));

        var dots = pills[0].GetVisualDescendants().OfType<Ellipse>().ToList();
        var dot = Assert.Single(dots);
        Assert.Equal(7d, dot.Width);
        Assert.Equal(
            ((ISolidColorBrush)chart.MetricPills[0].Tint).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);

        // The "overall" filter sentinel carries no tint, so it gets no dot rather than a grey one.
        var overall = view.GetControl<ItemsControl>("FilterPills")
            .GetVisualDescendants()
            .OfType<ToggleButton>()
            .First();

        Assert.All(
            overall.GetVisualDescendants().OfType<Ellipse>(),
            ellipse => Assert.False(ellipse.IsVisible));
    }

    [AvaloniaFact]
    public void MetricChartView_EmptySelection_RendersTheEmptyState()
    {
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection(metrics: []));

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        Assert.True(view.GetControl<TextBlock>("EmptyState").IsVisible);
        Assert.Contains("No data for the selected metrics.", VisibleTexts(view));

        // An empty grid with axes and no series is worse than a sentence, so the chart is hidden,
        // and its host with it so the plot's floor is not spent on nothing.
        Assert.False(view.GetControl<CartesianChart>("Chart").IsEffectivelyVisible);
        Assert.Equal(0d, view.GetControl<Border>("PlotHost").Bounds.Height);
    }

    [AvaloniaFact]
    public void MetricChartView_ThePlot_Is260InAnAutoHost_AndFillsAStarHostMarkedFills()
    {
        // The plot has no fixed height: it is the control's starred row. In
        // an Auto host such as the page's "Trend across nights" band (a StackPanel) the row
        // measures to the plot's 260 floor, so that band is unchanged; in the session pane's
        // starred zone, which marks the view with the "fills" class, the floor is lifted and the
        // plot takes what the zone has. A failure looks like 260 in the star host (the fixed
        // Height still there) or a 260 floor in the pane (the chart overflowing a 160 row).
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection());

        var auto = new MetricChartView { DataContext = chart };
        var stack = new StackPanel();
        stack.Children.Add(auto);
        var window = new Window { Width = 1600, Height = 900, Content = stack };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(double.IsNaN(auto.GetControl<CartesianChart>("Chart").Height), "the plot still declares a fixed Height");
        Assert.Equal(260d, auto.GetControl<Border>("PlotHost").Bounds.Height, 0);

        var fills = new MetricChartView { DataContext = chart, Classes = { "fills" } };
        var star = new Grid { RowDefinitions = new RowDefinitions("*"), Height = 160 };
        star.Children.Add(fills);
        var short_ = new Window { Width = 1600, Height = 900, Content = star };
        short_.Show();
        Dispatcher.UIThread.RunJobs();

        var host = fills.GetControl<Border>("PlotHost");
        Assert.True(host.Bounds.Height < 260d, $"the plot host is {host.Bounds.Height} px in a 160 px star host");
        Assert.True(host.Bounds.Height > 0d, "the plot host has no height in the star host");
        Assert.Equal(160d, fills.Bounds.Height, 0);
    }

    [AvaloniaFact]
    public void MetricChartView_EveryTextBlock_RendersAtAReadableSize()
    {
        // Scales.axaml's FontSize* keys are ratios (0.500 to 0.786), not point sizes: binding one
        // to FontSize renders text at well under a pixel. Nothing in this control sets FontSize,
        // and this is what fails if someone binds one of those keys.
        ChartTheme.Apply();
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection());

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        var blocks = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }

    [AvaloniaFact]
    public void MetricChartView_TemplatedCommandBindings_Resolve()
    {
        // The control carries no command of its own (its actions are the Expander, the two pill
        // strips and the scope toggle), so the equivalent assertion is that the two-way bindings
        // are live: toggling a pill in the visual tree has to reach the shared selection, which is
        // what makes graph.enabled_metrics change from a click rather than only from a setter.
        ChartTheme.Apply();
        var selection = Selection(metrics: ["hfr"]);
        using var chart = new SessionChartViewModel(SessionWithFrames(), selection);

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        var pills = view.GetControl<ItemsControl>("MetricPills")
            .GetVisualDescendants()
            .OfType<ToggleButton>()
            .ToList();

        Assert.Equal(5, pills.Count);
        Assert.True(pills[0].IsChecked);

        pills[2].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(selection.Metrics[2].IsSelected);
        Assert.Equal(["HFR (px)", "FWHM"], chart.Series.Select(series => series.Name));
    }

    [AvaloniaFact]
    public void MetricChartView_TargetChart_RendersTheSessionScopeAffordance()
    {
        // default_chart_sessions is 1, the target has two nights, so the scope control
        // is present and says what it is hiding.
        ChartTheme.Apply();
        using var newest = Cards.Create(overview: Page.Session(Page.LastSession));
        using var oldest = Cards.Create(overview: Page.Session(Page.FirstSession));
        ObservableCollection<SessionCardViewModel> sessions = [newest.Card, oldest.Card];
        using var chart = new TargetChartViewModel(sessions, Selection());

        var view = new MetricChartView { DataContext = chart };
        Show(view);

        Assert.True(view.GetControl<StackPanel>("SessionScope").IsVisible);
        Assert.Contains("newest 1 of 2 sessions", VisibleTexts(view));

        var toggle = view.GetControl<StackPanel>("SessionScope")
            .GetVisualDescendants()
            .OfType<CheckBox>()
            .Single();

        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, chart.PlottedSessionCount);
        Assert.Contains("all 2 sessions", VisibleTexts(view));
    }

    // A failure is a lane under 96 px, or two lanes whose plots start or end at different x.
    [AvaloniaFact]
    public async Task LanedForm_StacksOneLanePerMetric_AtLeast96Tall_WithTheSamePlotEdges()
    {
        ChartTheme.Apply();
        using var card = Cards.Create(overview: Page.Session(Page.LastSession));
        ObservableCollection<SessionCardViewModel> sessions = [card.Card];
        using var chart = new TargetChartViewModel(sessions, Selection(metrics: ["hfr", "fwhm", "guiding_rms"]));

        var view = new MetricChartView { DataContext = chart, IsLaned = true };
        var window = new Window { Width = 740, Height = 506, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var lanes = view.GetControl<ItemsControl>("Lanes").GetVisualDescendants().OfType<CartesianChart>().ToList();
        for (var step = 0; step < 120 && lanes.Any(lane => ((LiveChartsCore.Chart)lane.CoreChart).DrawMarginSize.Width <= 0f); step++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(chart.IsLaned);
        Assert.False(view.GetControl<Border>("PlotHost").IsVisible);
        Assert.Equal(3, lanes.Count);
        Assert.All(lanes, lane => Assert.True(lane.Bounds.Height >= 96d, $"lane height {lane.Bounds.Height}"));
        var edges = lanes.Select(lane =>
        {
            var core = (LiveChartsCore.Chart)lane.CoreChart;
            return (Math.Round(core.DrawMarginLocation.X, 1), Math.Round(core.DrawMarginLocation.X + core.DrawMarginSize.Width, 1));
        });
        Assert.Single(edges.Distinct());
        window.Close();
    }

    // A failure is lanes that hold their 96 px floor and leave the viewport partly empty, or lanes
    // squeezed under 96 px instead of scrolling.
    [AvaloniaTheory]
    [InlineData(2, 428d)]
    [InlineData(5, 300d)]
    public void LanedForm_TheLanesShareTheViewport_AndScrollOnlyPastNinetySixEach(int count, double viewport)
    {
        ChartTheme.Apply();
        using var card = Cards.Create(overview: Page.Session(Page.LastSession));
        ObservableCollection<SessionCardViewModel> sessions = [card.Card];
        string[] keys = ["hfr", "eccentricity", "fwhm", "guiding_rms", "detected_stars"];
        using var chart = new TargetChartViewModel(sessions, Selection(metrics: keys[..count]));

        var view = new MetricChartView { DataContext = chart, IsLaned = true };
        var window = new Window { Width = 740, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var scroll = view.GetControl<ScrollViewer>("LanesScroll");
        window.Height += viewport - scroll.Viewport.Height;
        Dispatcher.UIThread.RunJobs();

        var lanes = view.GetControl<ItemsControl>("Lanes").GetVisualDescendants().OfType<CartesianChart>().ToList();
        Assert.Equal(viewport, scroll.Viewport.Height, 0.5d);
        Assert.Equal(count, lanes.Count);
        if (count * 96d <= viewport)
        {
            Assert.Equal(viewport, lanes.Sum(lane => lane.Bounds.Height), 1d);
            Assert.True(scroll.Extent.Height <= viewport + 0.5d, $"the lanes need {scroll.Extent.Height} in {viewport}");
        }
        else
        {
            Assert.All(lanes, lane => Assert.Equal(96d, lane.Bounds.Height, 0.5d));
            Assert.Equal(count * 96d, scroll.Extent.Height, 0.5d);
        }

        Assert.DoesNotContain(scroll, view.GetControl<WrapPanel>("PillRow").GetVisualAncestors());
        window.Close();
    }

    // A failure is a lane drawn under 96 px, lanes that do not overflow into a scroll, or a
    // plain wheel over a lane that LiveCharts swallows so LanesScroll never moves.
    [AvaloniaFact]
    public void LanedForm_FourLanesIn260_KeepTheirFloor_AndAWheelOverALaneScrollsThem()
    {
        ChartTheme.Apply();
        using var card = Cards.Create(overview: Page.Session(Page.LastSession));
        ObservableCollection<SessionCardViewModel> sessions = [card.Card];
        using var chart = new TargetChartViewModel(sessions, Selection(metrics: ["hfr", "eccentricity", "fwhm", "guiding_rms"]));

        var view = new MetricChartView { DataContext = chart, IsLaned = true };
        var window = new Window { Width = 740, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var scroll = view.GetControl<ScrollViewer>("LanesScroll");
        window.Height += 260d - scroll.Viewport.Height;
        Dispatcher.UIThread.RunJobs();

        var lanes = view.GetControl<ItemsControl>("Lanes").GetVisualDescendants().OfType<CartesianChart>().ToList();
        Assert.Equal(4, lanes.Count);
        Assert.All(lanes, lane => Assert.True(lane.Bounds.Height >= 96d, $"lane height {lane.Bounds.Height}"));
        Assert.True(scroll.Extent.Height >= 384d, $"extent {scroll.Extent.Height}");
        Assert.Contains(scroll.GetVisualDescendants().OfType<ScrollBar>(), bar => bar.Orientation == Orientation.Vertical && bar.IsVisible);

        var over = lanes[0].TranslatePoint(new Point(lanes[0].Bounds.Width / 2d, lanes[0].Bounds.Height / 2d), window)!.Value;
        window.MouseWheel(over, new Vector(0d, -1d));
        Dispatcher.UIThread.RunJobs();

        Assert.True(scroll.Offset.Y > 0d, $"offset {scroll.Offset.Y} after a wheel over a lane");
        window.Close();
    }

    // A failure is a lane title drawn as a rotated axis name that runs into the next lane, a
    // horizontal title that leaves its lane, meets the next title or covers a tick label, or, with
    // every lane at its 96 px floor, the bottom lane's x labels drawn past the lane's bottom.
    [AvaloniaTheory]
    [InlineData(900d)]
    [InlineData(420d)]
    public async Task LanedForm_EachTitleSitsAboveItsPlot_InsideItsLane_ClearOfTicksAndTheNextTitle(double height)
    {
        ChartTheme.Apply();
        using var card = Cards.Create(overview: Page.Session(Page.LastSession));
        ObservableCollection<SessionCardViewModel> sessions = [card.Card];
        using var chart = new TargetChartViewModel(sessions, Selection(metrics: [.. ChartMetrics.All.Select(metric => metric.Key)]));

        var view = new MetricChartView { DataContext = chart, IsLaned = true };
        var window = new Window { Width = 740, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var lanes = view.GetControl<ItemsControl>("Lanes").GetVisualDescendants().OfType<CartesianChart>().ToList();
        if (height < 900d)
        {
            Assert.All(lanes, lane => Assert.Equal(96d, lane.Bounds.Height, 0.5d));
        }

        for (var step = 0; step < 120 && lanes.Any(lane => ((LiveChartsCore.Chart)lane.CoreChart).DrawMarginSize.Width <= 0f); step++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        var titles = view.GetControl<ItemsControl>("Lanes").GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "LaneTitle").ToList();
        Assert.Equal(ChartMetrics.All.Length, lanes.Count);
        Assert.True(titles.Count == lanes.Count, $"{titles.Count} horizontal lane titles for {lanes.Count} lanes");
        Rect In(Visual visual, Rect rect) => new(visual.TranslatePoint(rect.Position, view)!.Value, rect.Size);
        for (var index = 0; index < lanes.Count; index++)
        {
            var lane = lanes[index];
            var core = (LiveChartsCore.Chart)lane.CoreChart;
            var y = Assert.IsType<LiveChartsCore.SkiaSharpView.Axis>(Assert.Single(chart.Lanes[index].YAxes));
            var laneRect = In(lane, new Rect(lane.Bounds.Size));
            var title = In(titles[index], new Rect(titles[index].Bounds.Size));
            var half = y.TextSize / 2d;
            var ticks = In(lane, new Rect(0d, core.DrawMarginLocation.Y - half, core.DrawMarginLocation.X, core.DrawMarginSize.Height + (2d * half)));

            Assert.Null(y.Name);
            Assert.True(laneRect.Contains(title), $"{chart.Lanes[index].Title}: title {title} outside lane {laneRect}");
            Assert.False(title.Intersects(ticks), $"{chart.Lanes[index].Title}: title {title} meets the tick labels {ticks}");
            if (index + 1 < titles.Count)
            {
                Assert.False(title.Intersects(In(titles[index + 1], new Rect(titles[index + 1].Bounds.Size))), $"{chart.Lanes[index].Title} meets the next title");
            }
        }

        var bottom = lanes[^1];
        var bottomCore = (LiveChartsCore.Chart)bottom.CoreChart;
        var x = Assert.IsType<LiveChartsCore.SkiaSharpView.Axis>(Assert.Single(chart.Lanes[^1].XAxes));
        var labelsEnd = bottomCore.DrawMarginLocation.Y + bottomCore.DrawMarginSize.Height + (1.5d * x.TextSize);
        Assert.True(labelsEnd <= bottom.Bounds.Height + 0.5d, $"the bottom lane's x labels end about {labelsEnd} in a {bottom.Bounds.Height} lane");

        window.Close();
    }

    // A failure is a star count tick that reads with decimals, such as "1,490.0005 count", or
    // a count axis that still steps finer than one star.
    [AvaloniaTheory]
    [InlineData("hfr", 1)]
    [InlineData("detected_stars", 0)]
    public void ACountAxis_ReadsWholeNumbers_AndNeverStepsBelowOne(string first, int index)
    {
        using var chart = new SessionChartViewModel(SessionWithFrames(), Selection(metrics: [.. new[] { first, "detected_stars" }.Distinct()]));
        var count = Assert.IsType<LiveChartsCore.SkiaSharpView.Axis>(chart.YAxes[index]);

        Assert.Equal("1,490 count", count.Labeler(1490.0005d));
        Assert.Equal(1d, count.MinStep);
    }

    private static SessionChartViewModel NightChart(params string[] metrics)
    {
        var chart = new SessionChartViewModel(SessionWithFrames(), Selection(metrics: metrics));
        var start = DateTime.Today;
        chart.UseLaneAxis(
            new NightLaneAxis(start, start.AddHours(8), NightLaneAxis.SharedPlotLeft, NightLaneAxis.SharedPlotRight),
            TimeZoneInfo.Utc);
        return chart;
    }

    // A failure is a star count tick with decimals ("400.00") when a decimal metric shares the right axis.
    [AvaloniaFact]
    public void OnTheNightAxis_ACountReadsWhole_WhenADecimalMetricSharesTheRightAxis()
    {
        using var chart = NightChart("hfr", "fwhm", "detected_stars");
        var right = Assert.IsType<LiveChartsCore.SkiaSharpView.Axis>(chart.YAxes[1]);

        Assert.Equal("400", right.Labeler(400d));
        Assert.Equal(1d, right.MinStep);
    }

    // A failure is a right axis floor left to the engine, which pads below zero for metrics that cannot be negative.
    [AvaloniaFact]
    public void OnTheNightAxis_TheRightAxisFloorIsZero()
    {
        using var chart = NightChart("hfr", "fwhm", "detected_stars");

        Assert.Equal(0d, chart.YAxes[1].MinLimit);
    }

    // A failure is a y axis label, with or without its unit, drawn over the plot of the night's
    // chart, where it covers the first or the last plotted points.
    [AvaloniaTheory]
    [InlineData(540d, "hfr", "fwhm")]
    [InlineData(708d, "hfr", "fwhm")]
    [InlineData(540d, "hfr", "detected_stars")]
    [InlineData(708d, "eccentricity", "fwhm")]
    [InlineData(540d, "fwhm", "guiding_rms")]
    [InlineData(540d, "detected_stars", "detected_stars")]
    [InlineData(540d, "hfr", "detected_stars", 23456)]
    public async Task OnTheNightAxis_NoYAxisLabel_MeetsThePlot(double width, string first, string second, int stars = 1490)
    {
        ChartTheme.Apply();
        var detail = SessionWithFrames();
        detail = detail with { Frames = [.. detail.Frames.Select(frame => frame with { DetectedStars = stars })] };
        var start = detail.SessionDate.ToDateTime(new TimeOnly(20, 0));
        using var chart = new SessionChartViewModel(detail, Selection(metrics: [.. new[] { first, second }.Distinct()]));
        chart.UseLaneAxis(
            new NightLaneAxis(start, start.AddHours(8), NightLaneAxis.SharedPlotLeft, NightLaneAxis.SharedPlotRight),
            TimeZoneInfo.Utc);

        var view = new MetricChartView { DataContext = chart };
        var window = new Window { Width = width, Height = 320, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var plot = view.GetControl<CartesianChart>("Chart");
        var core = (LiveChartsCore.Chart)plot.CoreChart;
        for (var step = 0; step < 120 && core.DrawMarginSize.Width <= 0f; step++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        var area = new Rect(core.DrawMarginLocation.X, core.DrawMarginLocation.Y, core.DrawMarginSize.Width, core.DrawMarginSize.Height);
        Assert.True(area.Width > 0d, "the chart never measured");
        Assert.All(chart.YAxes, axis =>
        {
            var labels = new Rect(axis.LabelsDesiredSize.X, axis.LabelsDesiredSize.Y, axis.LabelsDesiredSize.Width, axis.LabelsDesiredSize.Height);
            Assert.False(labels.Intersects(area), $"{axis.Position} labels {labels} meet the plot {area} at width {width}");
        });
        window.Close();
    }

    // A failure is a trend axis the engine leaves to its own tick step, which on a small plot
    // prints a lone "0.00 px" beside points near 2.7: the axis must label the data's true floor
    // and ceiling and keep them inside its limits.
    [AvaloniaFact]
    public void TheTrendAxes_OverOneSession_PinAFlatRange_AndLabelWithTheMetricsDecimals()
    {
        ChartTheme.Apply();
        using var card = Cards.Create(overview: Page.Session(Page.LastSession));
        ObservableCollection<SessionCardViewModel> sessions = [card.Card];
        using var chart = new TargetChartViewModel(sessions, Selection(metrics: ["hfr", "eccentricity"]));

        foreach (var (axis, metric) in new[] { (chart.YAxes[0], "hfr"), (chart.YAxes[1], "eccentricity") })
        {
            var value = ChartMetrics.ByKey(metric)!.SessionValue(card.Card.Overview)!.Value;
            var pinned = Assert.IsType<LiveChartsCore.SkiaSharpView.Axis>(axis);
            var ticks = pinned.CustomSeparators!.ToList();
            Assert.Equal(3, ticks.Count);
            Assert.Equal(pinned.MinLimit!.Value, ticks[0], 9);
            Assert.Equal(pinned.MaxLimit!.Value, ticks[^1], 9);
            Assert.Equal(metric == "hfr" ? "1.35 px" : "0.35", pinned.Labeler(metric == "hfr" ? 1.3473d : 0.3473d));
            Assert.True(pinned.MinLimit < value && value < pinned.MaxLimit, $"{metric} {value} outside {pinned.MinLimit} to {pinned.MaxLimit}");
        }
    }

    // A failure is a lane chart with no point-press command, so a press on a lane point selects
    // no frame (U13, U26).
    [AvaloniaFact]
    public void LanedForm_ALanePointPress_BindsThePressCommand()
    {
        ChartTheme.Apply();
        using var card = Cards.Create(overview: Page.Session(Page.LastSession));
        ObservableCollection<SessionCardViewModel> sessions = [card.Card];
        using var chart = new TargetChartViewModel(sessions, Selection(metrics: ["hfr", "fwhm"]));

        var view = new MetricChartView { DataContext = chart, IsLaned = true };
        var window = Show(view);

        var lanes = view.GetControl<ItemsControl>("Lanes").GetVisualDescendants().OfType<CartesianChart>().ToList();
        Assert.Equal(2, lanes.Count);
        Assert.All(lanes, lane => Assert.Same(chart.PointPressedCommand, lane.DataPointerDownCommand));
        window.Close();
    }

    // Gate 4's flake: a pointer over a lane arms the chart's tooltip throttle, whose pool hop the
    // join did not wait for, so it landed in the next test and broke its dispatcher. A failure is
    // any throttle of a lane still armed once the join returns.
    [AvaloniaFact]
    public async Task LanedForm_APointerOverALane_LeavesNoThrottleArmed_AfterTheJoin()
    {
        ChartTheme.Apply();
        using var card = Cards.Create(overview: Page.Session(Page.LastSession));
        ObservableCollection<SessionCardViewModel> sessions = [card.Card];
        using var chart = new TargetChartViewModel(sessions, Selection(metrics: ["hfr", "fwhm"]));
        var view = new MetricChartView { DataContext = chart, IsLaned = true };
        var window = new Window { Width = 740, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var lanes = view.GetControl<ItemsControl>("Lanes").GetVisualDescendants().OfType<CartesianChart>().ToList();
        for (var step = 0; step < 120 && lanes.Any(lane => ((LiveChartsCore.Chart)lane.CoreChart).DrawMarginSize.Width <= 0f); step++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        var at = lanes[0].TranslatePoint(new Point(lanes[0].Bounds.Width / 2d, lanes[0].Bounds.Height / 2d), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        ChartThrottles.Mounted.Join(ChartThrottles.Budget);

        Assert.All(lanes, lane => Assert.False(ChartThrottles.IsAnyArmed(lane), "a lane throttle is still armed after the join"));
        window.Close();
    }
}
