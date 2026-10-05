using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Views.TargetDetail.Parts;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Painting;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// The night's three lanes on one time axis. The fixture night has a dusk and dawn band
// from an invented observer position, so the domain is wider than the frames.
public sealed class NightLaneAxisTests
{
    private const double LaneWidth = 740;
    private const int Probe = 20;
    private const string RigA = "Alpha / Cam";
    private const string RigB = "Bravo / Cam";

    private static readonly DateTime GuideStartUtc =
        Page.LastSession.ToDateTime(new TimeOnly(20, 50), DateTimeKind.Utc);

    private sealed class Night : IDisposable
    {
        public required Cards.Harness Harness { get; init; }
        public required IReadOnlyList<FrameRow> Frames { get; init; }
        public required Window Window { get; init; }
        public required Grid Root { get; init; }

        public NightStrip Strip => Root.GetVisualDescendants().OfType<NightStrip>().Single();
        public CartesianChart Chart => Root.GetVisualDescendants().OfType<CartesianChart>().Single();
        public GuideGraph Graph => Root.GetVisualDescendants().OfType<GuideGraph>().Single();
        public SessionChartViewModel ChartModel => (SessionChartViewModel)Harness.Card.Chart!;

        /// <summary>Phase 24 R3: under the timeline the guide graph is the chart's own and draws
        /// while the Guiding pill is on, so a case that reads its pixels turns the pill on first.
        /// </summary>
        public void GuidingOn()
        {
            ChartModel.ShowGuiding = true;
            Tick();
        }

        public void Dispose()
        {
            Harness.Card.PendingLoad?.Wait(GalactiLog.App.Tests.TestSupport.TargetPartHost.Budget);
            Dispatcher.UIThread.RunJobs();
            Window.Close();
            Harness.Dispose();
        }
    }

    private static ChartSelectionViewModel Selection(string[]? metrics = null)
    {
        var stored = new GraphSettings { EnabledMetrics = metrics ?? ["hfr"], EnabledFilters = ["overall"] };
        return new ChartSelectionViewModel(
            stored,
            new GraphSettingsWriter(() => stored, _ => { }),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));
    }

    private static Night Mount(
        string timezone = "UTC",
        double? latitude = 45d,
        double? longitude = 0d,
        DateTime? guideStart = null,
        TimeSpan shift = default,
        string[]? metrics = null,
        bool twoRigs = false,
        bool apart = false,
        int count = 40,
        double width = LaneWidth)
    {
        ChartTheme.Apply();
        var date = Page.LastSession;
        var start = guideStart ?? GuideStartUtc;
        IReadOnlyList<FrameRow> frames =
            [.. NightPartsTestKit.Frames(count, date).Select((frame, i) => frame with
            {
                CaptureDate = frame.CaptureDate!.Value + shift,
                Eccentricity = 0.3d + ((i % 5) * 0.02d),
                Rig = twoRigs ? (i < 20 ? RigA : RigB) : frame.Rig,
            })];
        var detail = Cards.PopulatedDetail(sessionDate: date) with { Frames = frames };
        if (twoRigs)
        {
            detail = detail with
            {
                Rigs = [new RigGroup(0, RigA, "Alpha", "Cam", 20, 6000d, null, null), new RigGroup(1, RigB, "Bravo", "Cam", 20, 6000d, null, null)],
            };
        }

        var harness = Cards.Create(
            detail: detail,
            general: new GeneralSettings
            {
                Timezone = timezone,
                Use24HTime = true,
                ObserverLatitude = latitude,
                ObserverLongitude = longitude,
            },
            anyGuideLogs: true);
        harness.FrameTableResult = NightPartsTestKit.Table(frames);
        harness.ChartResult = new SessionChartViewModel(harness.Detail!, Selection(metrics));
        harness.GuidingResult = new Phd2NightGuiding(
            new Phd2NightSummary { SessionCount = 1 },
            [NightPartsTestKit.GuideSession(0) with { StartedAtUtc = start }]);
        harness.FramesResult = _ => new Phd2SessionFrames(
            1.5d,
            start,
            [.. Enumerable.Range(0, 100).Select(i => new Phd2FramePoint(i * 60d, 0.3d, -0.2d, 0, "", 0, "", null, null, false))],
            []);
        harness.Card.IsExpanded = true;
        harness.Settle();
        harness.SettleGuiding().SettleFrames();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,180,320"), Width = width };

        // Under the timeline the guide graph is the night chart's own (Phase 24 R3); apart from
        // it, a bare graph labels its own clock, as GuidingPart did before the section went.
        Control[] lanes = apart
            ? [new NightTimelinePart { DataContext = harness.Card }, BareGraph(harness.Card)]
            : [new NightTimelinePart { DataContext = harness.Card }, new NightMetricsPart { DataContext = harness.Card }];
        for (var row = 0; row < lanes.Length; row++)
        {
            Grid.SetRow(lanes[row], row);
            root.Children.Add(lanes[row]);
        }

        var window = NightPartsTestKit.Show(root, width, 900);
        Dispatcher.UIThread.RunJobs();
        return new Night { Harness = harness, Frames = frames, Window = window, Root = root };
    }

    private static GuideGraph BareGraph(SessionCardViewModel card)
    {
        var ink = new ImmutableSolidColorBrush(Colors.Gainsboro);
        var graph = new GuideGraph
        {
            Model = card.Guiding!.Graph,
            LaneAxis = card.LaneAxis,
            Strip = card.NightStrip,
            ShowsClockLabels = true,
            RaBrush = ink,
            DecBrush = ink,
            DropBrush = ink,
            DitherBrush = ink,
            SettleBrush = ink,
            AxisBrush = ink,
            GridBrush = ink,
            LabelBrush = ink,
        };
        return graph;
    }

    // rc5.4 measures on its own throttle, not in the layout pass (MatrixTabViewTests.Measured).
    private static async Task Measured(CartesianChart chart)
    {
        for (var step = 0; step < 120 && ((Chart)chart.CoreChart).DrawMarginSize.Width <= 0f; step++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheFixtureNight_HasABand_AndADomainWiderThanItsFrames()
    {
        using var night = Mount();
        var strip = night.Harness.Card.NightStrip!;
        var axis = night.Harness.Card.LaneAxis!;

        Assert.True(strip.HasBand);
        Assert.True(axis.FractionOf(NightStripViewModel.ToLocal(night.Frames[0].CaptureDate!.Value, TimeZoneInfo.Utc)) > 0.05d);
        Assert.NotEmpty(axis.TickFractions);
    }

    // A failure here is the three lanes placing one instant at three different x positions.
    [AvaloniaFact]
    public async Task OneFrameTime_SitsAtOneX_InTheTimelineTheChartAndTheGuideGraph()
    {
        using var night = Mount();
        await Measured(night.Chart);
        var axis = night.Harness.Card.LaneAxis!;
        var utc = night.Frames[Probe].CaptureDate!.Value;
        var fraction = axis.FractionOf(NightStripViewModel.ToLocal(utc, TimeZoneInfo.Utc));

        var strip = night.Strip;
        var stripX = strip.TranslatePoint(new Point(strip.X(fraction), 0), night.Root)!.Value.X;

        var chart = night.Chart;
        var series = night.ChartModel.Series.OfType<LineSeries<double?>>().First();
        var point = ((ISeries)series).Fetch((Chart)chart.CoreChart).Single(entry => entry.Index == Probe);
        var pixel = chart.ScaleDataToPixels(new LvcPointD(point.Coordinate.SecondaryValue, point.Coordinate.PrimaryValue));
        var chartX = chart.TranslatePoint(new Point(pixel.X, 0), night.Root)!.Value.X;

        night.GuidingOn();
        var graph = night.Graph;
        var graphX = graph.TranslatePoint(new Point(graph.XAt((utc - GuideStartUtc).TotalSeconds), 0), night.Root)!.Value.X;

        Assert.InRange(chartX, stripX - 1d, stripX + 1d);
        Assert.InRange(graphX, stripX - 1d, stripX + 1d);
    }

    // A failure is the chart still spending its own x axis' labels and name under the plot.
    [AvaloniaFact]
    public void TheChart_DrawsGridAtTheTimelineMarks_WithNoLabelsOrName()
    {
        using var night = Mount();
        var axis = night.Harness.Card.LaneAxis!;
        var x = Assert.IsType<Axis>(Assert.Single(night.ChartModel.XAxes));

        Assert.Null(x.Name);
        Assert.Equal(axis.TickFractions, x.CustomSeparators);
        Assert.Equal("", x.Labeler(axis.TickFractions[0]));
    }

    // A failure is a lane reading the clock in a zone other than the display zone, or
    // losing a frame past midnight, or a guide log that starts after the first frame drifting.
    [AvaloniaFact]
    public async Task InANonUtcZone_AcrossMidnight_WithAGuideLogStartingLate_TheLanesStillAgree()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var shift = TimeSpan.FromHours(7.5);
        var first = Page.LastSession.ToDateTime(new TimeOnly(21, 0), DateTimeKind.Utc) + shift;
        var guideStart = first.AddMinutes(10);
        using var night = Mount(timezone: "America/New_York", guideStart: guideStart, shift: shift);
        await Measured(night.Chart);
        var axis = night.Harness.Card.LaneAxis!;
        var utc = night.Frames[Probe].CaptureDate!.Value;
        var local = NightStripViewModel.ToLocal(utc, zone);
        var fraction = axis.FractionOf(local);

        var stripX = night.Strip.TranslatePoint(new Point(night.Strip.X(fraction), 0), night.Root)!.Value.X;
        var series = night.ChartModel.Series.OfType<LineSeries<double?>>().First();
        var point = ((ISeries)series).Fetch((Chart)night.Chart.CoreChart).Single(entry => entry.Index == Probe);
        var pixel = night.Chart.ScaleDataToPixels(new LvcPointD(point.Coordinate.SecondaryValue, point.Coordinate.PrimaryValue));
        var chartX = night.Chart.TranslatePoint(new Point(pixel.X, 0), night.Root)!.Value.X;
        night.GuidingOn();
        var graphX = night.Graph.TranslatePoint(new Point(night.Graph.XAt((utc - guideStart).TotalSeconds), 0), night.Root)!.Value.X;

        Assert.Equal(Page.LastSession.AddDays(1), DateOnly.FromDateTime(local));
        Assert.True(NightStripViewModel.ToLocal(night.Frames[0].CaptureDate!.Value, zone).Day == Page.LastSession.Day);
        Assert.InRange(chartX, stripX - 1d, stripX + 1d);
        Assert.InRange(graphX, stripX - 1d, stripX + 1d);
    }

    // A failure is guide data before an hour-floored night start that no zoom or drag
    // can reach.
    [AvaloniaFact]
    public void TheGuideGraph_ZoomedAndDragged_ReachesGuideDataBeforeTheNightsStart()
    {
        using var night = Mount(latitude: null, longitude: null);
        var model = night.Graph.Model!;
        Assert.True(model.TimeView.Min > model.TimeRange.Min);

        model.ZoomTime(model.TimeView.Min, 2d);
        model.Pan(-1_000_000d, 0d);

        Assert.Equal(model.TimeRange.Min, model.TimeView.Min, 6);
    }

    // A failure is a card reload handing an equal axis and dropping the reader's zoom.
    [AvaloniaFact]
    public void AnEqualAxis_FromAReload_KeepsAHeldZoom()
    {
        using var night = Mount();
        var axis = night.Harness.Card.LaneAxis!;
        var model = night.Graph.Model!;
        model.ZoomTime(3000d, 2d);

        model.UseLaneAxis(axis with { TickFractions = [.. axis.TickFractions] });

        Assert.NotNull(model.TimeOverride);
    }

    // A failure is the plot squeezed to about 35 px by an axis it no longer needs.
    [AvaloniaFact]
    public async Task TheChartPlot_InIts180Lane_IsAtLeast100Tall()
    {
        using var night = Mount();
        await Measured(night.Chart);
        var core = (Chart)night.Chart.CoreChart;

        Assert.True(core.DrawMarginSize.Height >= 100f, $"plot height {core.DrawMarginSize.Height}");
    }

    // A failure is an outlier frame with no ring, or a ring that fills or recolours the point.
    [AvaloniaFact]
    public void EachOutlierPoint_CarriesARing_InTheOutlierInk_WithNoFill()
    {
        using var night = Mount();
        var ring = Assert.Single(night.ChartModel.Series, s => s.Name?.EndsWith(" outliers", StringComparison.Ordinal) == true);
        var scatter = Assert.IsType<ScatterSeries<ObservablePoint>>(ring);

        Assert.Equal(night.Frames.Count(f => f.IsHfrOutlier || f.IsEccentricityOutlier), scatter.Values!.Count());
        Assert.Null(scatter.Fill);
        var ink = Assert.IsType<SolidColorPaint>(scatter.Stroke).Color;
        Assert.Equal(ChartTheme.Palette.Outlier, ink);
        Assert.NotEqual(ChartTheme.Fallback, ink);
    }

    // A failure is the palette handing the ring the neutral grey instead of the outlier ink.
    [AvaloniaFact]
    public void ThePalettesOutlierInk_ResolvesFromTheShippedDictionary()
    {
        ChartTheme.Apply();

        Assert.Equal(ChartTheme.Read("ColorMetricWorst", ChartTheme.Fallback), ChartTheme.Palette.Outlier);
        Assert.NotEqual(ChartTheme.Fallback, ChartTheme.Palette.Outlier);
    }

    // A failure is the graph opening on its own log, or a zoom it cannot reset.
    [AvaloniaFact]
    public void TheGuideGraph_OpensOnTheNightsDomain_AndItsZoomAndResetStillWork()
    {
        using var night = Mount();
        var axis = night.Harness.Card.LaneAxis!;
        var model = night.Graph.Model!;
        var night0 = (axis.StartLocal - GuideStartUtc).TotalSeconds;
        var night1 = (axis.EndLocal - GuideStartUtc).TotalSeconds;

        Assert.Equal(night0, model.TimeView.Min, 6);
        Assert.Equal(night1, model.TimeView.Max, 6);

        model.ZoomTime(3000d, 2d);
        Assert.NotNull(model.TimeOverride);
        Assert.True(model.TimeView.Max - model.TimeView.Min < night1 - night0);

        model.ResetView();
        Assert.Null(model.TimeOverride);
        Assert.Equal(night0, model.TimeView.Min, 6);
    }

    // A failure is two neighbouring ticks of a fine step reading the same.
    [AvaloniaFact]
    public void YAxisLabels_OfNeighbouringFineTicks_NeverReadTheSame()
    {
        using var night = Mount();
        var y = Assert.IsType<Axis>(night.ChartModel.YAxes[0]);

        Assert.NotEqual(y.Labeler(2.11d), y.Labeler(2.1125d));
        Assert.Equal("2.00 px", y.Labeler(2d));
    }

    // A failure is a press on one frame's point selecting another frame, which is what a
    // series whose every point claims the whole plot width as its hover area does.
    [AvaloniaTheory]
    [InlineData(false, "HFR (px)", 10)]
    [InlineData(false, "Ecc", 33)]
    [InlineData(false, "HFR (px) outliers", 25)]
    [InlineData(true, "HFR (px) [Bravo / Cam]", 30)]
    [InlineData(true, "Ecc [Alpha / Cam]", 7)]
    [InlineData(true, "HFR (px) outliers", 25)]
    public async Task APressOnAPlottedPoint_SelectsThatPointsOwnFrame(bool twoRigs, string seriesName, int frame)
    {
        using var night = Mount(metrics: ["hfr", "eccentricity"], twoRigs: twoRigs);
        await Measured(night.Chart);
        var model = night.ChartModel;
        var series = model.Series.Single(entry => entry.Name == seriesName);
        var axis = ((ICartesianSeries)series).ScalesYAt;
        var point = series.Fetch((Chart)night.Chart.CoreChart)
            .Single(entry => !entry.Coordinate.IsEmpty && Math.Abs(entry.Coordinate.SecondaryValue - XOf(night, frame)) < 1e-9);
        var pixel = night.Chart.ScaleDataToPixels(new LvcPointD(point.Coordinate.SecondaryValue, point.Coordinate.PrimaryValue), 0, axis);
        var at = night.Chart.TranslatePoint(new Point(pixel.X, pixel.Y), night.Window)!.Value;

        night.Window.MouseDown(at, MouseButton.Left);
        night.Window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(frame, night.Harness.Card.NightStrip!.ActiveFrame);
    }

    private static double XOf(Night night, int frame)
        => night.Harness.Card.LaneAxis!.FractionOf(NightStripViewModel.ToLocal(night.Frames[frame].CaptureDate!.Value, TimeZoneInfo.Utc));

    // A failure is the per-frame chart's help glyph on a row of its own above the pills.
    [AvaloniaFact]
    public void TheMetricsHelpGlyph_SitsInThePillRow()
    {
        using var night = Mount();
        var part = night.Root.GetVisualDescendants().OfType<NightMetricsPart>().Single();
        var help = part.GetVisualDescendants().OfType<HelpButton>().Single();
        var row = part.GetVisualDescendants().OfType<WrapPanel>().Single(panel => panel.Name == "PillRow");

        Assert.Contains(row, help.GetVisualAncestors());
    }

    // A failure is the guide graph marking the selected frame in another ink than the
    // timeline's active tick, or keeping the mark after its strip is taken away.
    [AvaloniaFact]
    public void TheGuideGraphsMark_UsesTheTimelinesActiveInk_AndGoesWithItsStrip()
    {
        using var night = Mount();
        night.GuidingOn();
        night.Harness.Card.NightStrip!.SelectFrame(Probe);
        Tick();
        var active = Assert.IsAssignableFrom<ISolidColorBrush>(night.Strip.ActiveBrush).Color;

        Assert.Equal(active, Assert.IsAssignableFrom<ISolidColorBrush>(night.Graph.LastMarkBrush).Color);

        night.Graph.Strip = null;
        Tick();
        Assert.Null(night.Graph.LastMarkBrush);
    }

    // A failure is an emptied chart writing its old median line back on the next selection.
    [AvaloniaFact]
    public void AnEmptiedChart_KeepsNoMedianLine_WhenAFrameIsSelected()
    {
        using var night = Mount();
        night.ChartModel.Selection.Metrics.Single(metric => metric.Key == "hfr").IsSelected = false;
        Assert.True(night.ChartModel.IsEmpty);

        night.Harness.Card.NightStrip!.SelectFrame(Probe);

        Assert.DoesNotContain(night.ChartModel.Sections, section => section.Yi is not null);
    }

    // A failure is a guide sample after the clock change sitting an hour off the frame above
    // it, or a reset that does not bring the graph back onto the shared axis.
    [AvaloniaTheory]
    [InlineData("2026-03-29T00:00:00")]
    [InlineData("2026-10-25T00:30:00")]
    public async Task OnADaylightSavingNight_AGuideSampleAfterTheChange_SitsAtTheFramesX(string firstUtc)
    {
        const int before = 5, probe = 35;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var first = DateTime.SpecifyKind(DateTime.Parse(firstUtc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var shift = first - Page.LastSession.ToDateTime(new TimeOnly(21, 0), DateTimeKind.Utc);
        var guideStart = first.AddMinutes(-10);
        using var night = Mount(timezone: "Europe/London", latitude: null, longitude: null, guideStart: guideStart, shift: shift);
        await Measured(night.Chart);
        var axis = night.Harness.Card.LaneAxis!;
        var series = night.ChartModel.Series.OfType<LineSeries<double?>>().First();
        var points = ((ISeries)series).Fetch((Chart)night.Chart.CoreChart).ToList();
        double Seconds(int frame) => (night.Frames[frame].CaptureDate!.Value - guideStart).TotalSeconds;
        double StripX(int frame)
        {
            var fraction = axis.FractionOf(NightStripViewModel.ToLocal(night.Frames[frame].CaptureDate!.Value, zone));
            return night.Strip.TranslatePoint(new Point(night.Strip.X(fraction), 0), night.Root)!.Value.X;
        }

        double ChartX(int frame)
        {
            var point = points.Single(entry => entry.Index == frame);
            var pixel = night.Chart.ScaleDataToPixels(new LvcPointD(point.Coordinate.SecondaryValue, point.Coordinate.PrimaryValue));
            return night.Chart.TranslatePoint(new Point(pixel.X, 0), night.Root)!.Value.X;
        }

        double GraphX(int frame) => night.Graph.TranslatePoint(new Point(night.Graph.XAt(Seconds(frame)), 0), night.Root)!.Value.X;

        var utc = DateTime.SpecifyKind(night.Frames[probe].CaptureDate!.Value, DateTimeKind.Utc);
        var stripX = StripX(probe);
        var seconds = Seconds(probe);
        Assert.True(zone.IsDaylightSavingTime(first) != zone.IsDaylightSavingTime(utc));
        foreach (var frame in new[] { before, probe })
        {
            Assert.InRange(ChartX(frame), StripX(frame) - 1d, StripX(frame) + 1d);
        }

        // The chart and the guide graph share the slot, so the graph is read once the chart has been.
        night.GuidingOn();
        foreach (var frame in new[] { before, probe })
        {
            Assert.InRange(GraphX(frame), StripX(frame) - 1d, StripX(frame) + 1d);
        }

        Assert.True(night.Graph.Model!.TimeView.Min <= Seconds(0), $"window starts at {night.Graph.Model!.TimeView.Min}");
        Assert.Equal(seconds, night.Graph.SecondsAt(night.Graph.Model!, night.Graph.XAt(seconds)), 3);

        night.Harness.Card.NightStrip!.SelectFrame(probe);
        Tick();
        var markX = night.Graph.TranslatePoint(new Point(night.Graph.ActiveX!.Value, 0), night.Root)!.Value.X;
        Assert.InRange(markX, stripX - 1d, stripX + 1d);

        var model = night.Graph.Model!;
        model.ZoomTime(seconds, 2d);
        Assert.False(model.IsOnLaneAxis);
        model.ResetView();
        Assert.True(model.IsOnLaneAxis);
        Assert.InRange(GraphX(probe), stripX - 1d, stripX + 1d);
    }

    // A failure is an ordinary night drawing its guide lane anywhere other than where the
    // linear elapsed-seconds window placed it before clock time was read.
    [AvaloniaFact]
    public void OnAnOrdinaryNight_TheGuideFractions_MatchTheElapsedSecondsWindow()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        using var night = Mount(timezone: "America/New_York");
        var axis = night.Harness.Card.LaneAxis!;
        var model = night.Graph.Model!;
        var startLocal = NightStripViewModel.ToLocal(GuideStartUtc, zone);
        var before = new GuideView((axis.StartLocal - startLocal).TotalSeconds, (axis.EndLocal - startLocal).TotalSeconds);

        Assert.True(model.IsOnLaneAxis);
        Assert.Equal(before.Min, model.TimeView.Min, 6);
        Assert.Equal(before.Max, model.TimeView.Max, 6);
        for (var seconds = -86400d; seconds <= 86400d; seconds += 900d)
        {
            Assert.Equal(GuideGraphViewModel.FractionOf(before, seconds), model.TimeFractionOf(seconds), 9);
            Assert.Equal(GuideGraphViewModel.ValueAt(before, seconds / 43200d), model.TimeAt(seconds / 43200d), 6);
        }
    }

    // A failure is the clamped and raw fractions disagreeing inside the domain, or the raw one
    // clamping (or the clamped one not) outside it.
    [Fact]
    public void FractionOf_IsTheClampOfRawFractionOf_DifferingOnlyOutsideTheDomain()
    {
        var start = new DateTime(2026, 1, 10, 18, 0, 0);
        var axis = new NightLaneAxis(start, start.AddHours(12), NightLaneAxis.SharedPlotLeft, NightLaneAxis.SharedPlotRight);

        for (var minutes = 0; minutes <= 720; minutes += 30)
        {
            Assert.Equal(axis.RawFractionOf(start.AddMinutes(minutes)), axis.FractionOf(start.AddMinutes(minutes)), 12);
        }

        Assert.Equal(-0.25d, axis.RawFractionOf(start.AddHours(-3)), 12);
        Assert.Equal(0d, axis.FractionOf(start.AddHours(-3)));
        Assert.Equal(1.5d, axis.RawFractionOf(start.AddHours(18)), 12);
        Assert.Equal(1d, axis.FractionOf(start.AddHours(18)));
    }

    private const string FixedZone = "Etc/GMT+12";
    private static readonly DateTime FirstFrameUtc = Page.LastSession.ToDateTime(new TimeOnly(21, 0), DateTimeKind.Utc);

    // A failure is a guide session the picker offers, lying wholly before or after the night's
    // axis, drawn as an empty plot where the graph stands apart from the timeline.
    [AvaloniaTheory]
    [InlineData(-4)]
    [InlineData(5)]
    public void AGuideSessionOutsideTheNight_ApartFromTheTimeline_IsDrawnOnItsOwnSpan(int hoursFromFirstFrame)
    {
        using var night = Mount(timezone: FixedZone, latitude: null, longitude: null, guideStart: FirstFrameUtc.AddHours(hoursFromFirstFrame), apart: true);
        Tick();
        var graph = night.Graph;
        var model = graph.Model!;
        var plot = graph.PlotRect;

        Assert.False(model.IsOnLaneAxis, "the graph is still on the night's axis");
        Assert.Equal(new GuideView(model.TimeRange.Min, model.TimeRange.Max), model.TimeView);
        Assert.Equal(plot.X, graph.XAt(model.TimeRange.Min), 6);
        Assert.Equal(plot.Right, graph.XAt(model.TimeRange.Max), 6);
        Assert.Equal(100, graph.LastDraw.RaPoints);
        Assert.Equal(model.TimeTicks.Select(tick => tick.Label), graph.LastClockLabels);
        Assert.NotEmpty(graph.LastClockLabels);
        Assert.StartsWith("Error in arcseconds, ", model.Caption, StringComparison.Ordinal);
    }

    // A failure is the graph under the timeline leaving the shared axis, or an empty plot
    // whose caption still reads as if the session were drawn.
    [AvaloniaFact]
    public void AGuideSessionOutsideTheNight_UnderTheTimeline_KeepsTheSharedAxis_AndSaysSoInWords()
    {
        using var night = Mount(timezone: FixedZone, latitude: null, longitude: null, guideStart: FirstFrameUtc.AddHours(5));
        var model = night.Graph.Model!;

        Assert.True(model.IsOnLaneAxis);
        Assert.True(model.TimeFractionOf(model.TimeRange.Min) > 1d);
        Assert.Equal("Guide session 14:00 to 15:39 lies outside the timeline, 09:00 to 11:00.", model.Caption);

        model.ZoomTime(model.TimeRange.Min, 2d);
        Assert.StartsWith("Error in arcseconds, ", model.Caption, StringComparison.Ordinal);
    }

    // A failure is a session that overlaps the night being taken off the shared axis, so its
    // samples no longer sit at their own clock time on the night.
    [AvaloniaFact]
    public void AGuideSessionOverlappingTheNightsEnd_ApartFromTheTimeline_StaysOnTheSharedAxis()
    {
        using var night = Mount(timezone: FixedZone, latitude: null, longitude: null, guideStart: FirstFrameUtc.AddMinutes(90), apart: true);
        var model = night.Graph.Model!;

        Assert.True(model.IsOnLaneAxis);
        Assert.InRange(model.TimeFractionOf(model.TimeRange.Min), 0d, 1d);
        Assert.True(model.TimeFractionOf(model.TimeRange.Max) > 1d);
        Assert.StartsWith("Error in arcseconds, ", model.Caption, StringComparison.Ordinal);
    }

    // A failure is a night too short to hold an even-hour mark drawing no clock label at all.
    [AvaloniaFact]
    public void OnAOneHourNight_ApartFromTheTimeline_TheGraphLabelsTheNightsTwoEnds()
    {
        using var night = Mount(timezone: FixedZone, latitude: null, longitude: null, guideStart: FirstFrameUtc, apart: true, count: 20);
        Tick();

        Assert.Empty(night.Harness.Card.NightStrip!.AxisTicks);
        Assert.True(night.Graph.Model!.IsOnLaneAxis);
        Assert.Equal(["09:00", "10:00"], night.Graph.LastClockLabels);
    }

    // A failure is the end label printed over the start label when the plot is too narrow for both.
    [AvaloniaFact]
    public void OnAOneHourNight_InAVeryNarrowPane_TheEndLabelIsDropped_AndTheStartLabelStays()
    {
        using var night = Mount(timezone: FixedZone, latitude: null, longitude: null, guideStart: FirstFrameUtc, apart: true, count: 20, width: NightLaneAxis.SharedPlotLeft + NightLaneAxis.SharedPlotRight + 23d);
        Tick();

        Assert.True(night.Graph.PlotRect.Width > 0d, "no plot at this width");
        Assert.True(night.Graph.Model!.IsOnLaneAxis);
        Assert.Equal(["09:00"], night.Graph.LastClockLabels);
    }

    // A failure is the own-span clock labels printed over one another in a narrow pane.
    [AvaloniaFact]
    public void InANarrowPane_TheOwnSpanClockLabels_ThinOutRatherThanOverlap()
    {
        using var night = Mount(timezone: FixedZone, latitude: null, longitude: null, guideStart: FirstFrameUtc.AddHours(5), apart: true, width: 300);
        Tick();
        var all = night.Graph.Model!.TimeTicks.Select(tick => tick.Label).ToList();

        Assert.False(night.Graph.Model!.IsOnLaneAxis);
        Assert.NotEmpty(night.Graph.LastClockLabels);
        Assert.True(night.Graph.LastClockLabels.Count < all.Count, $"{night.Graph.LastClockLabels.Count} of {all.Count} labels drawn");
        Assert.All(night.Graph.LastClockLabels, label => Assert.Contains(label, all));
    }

    // ---- Phase 25 R4: the stitched axis. Plain arithmetic, no window. -------------------------
    //
    // Night A: 2025-09-02, 20:00 to 04:00, eight hours. Night B: 2025-09-05, 22:00 to 02:00, four
    // hours. Shares by duration are therefore two thirds and one third.

    private static DateTime At(int day, int hour)
        => new(2025, 9, day, hour, 0, 0, DateTimeKind.Unspecified);

    private static NightLaneAxis Axis(DateTime start, DateTime end, params double[] ticks)
        => new(start, end, NightLaneAxis.SharedPlotLeft, NightLaneAxis.SharedPlotRight) { TickFractions = ticks };

    private static NightLaneAxis TwoNights() => NightLaneAxis.Stitched(
    [
        (new DateOnly(2025, 9, 2), Axis(At(2, 20), At(3, 4), 0.25d, 0.5d)),
        (new DateOnly(2025, 9, 5), Axis(At(5, 22), At(6, 2), 0.5d)),
    ]);

    // A failure is two nights sharing the axis equally whatever their length.
    [Fact]
    public void Stitched_TwoNightsOfEightAndFourHours_ShareTwoThirdsAndOneThird()
    {
        var axis = TwoNights();

        Assert.Equal(2, axis.Segments.Count);
        Assert.Equal(0d, axis.Segments[0].FromFraction, 9);
        Assert.Equal(2d / 3d, axis.Segments[0].ToFraction, 9);
        Assert.Equal(2d / 3d, axis.Segments[1].FromFraction, 9);
        Assert.Equal(1d, axis.Segments[1].ToFraction, 9);
        Assert.Equal(At(2, 20), axis.StartLocal);
        Assert.Equal(At(6, 2), axis.EndLocal);

        // Every member's marks, mapped into its own share.
        Assert.Equal(3, axis.TickFractions.Count);
        Assert.Equal(1d / 6d, axis.TickFractions[0], 9);
        Assert.Equal(1d / 3d, axis.TickFractions[1], 9);
        Assert.Equal(5d / 6d, axis.TickFractions[2], 9);
    }

    // A failure is a time inside the second night placed as if the night began at 0.
    [Fact]
    public void Stitched_FractionOf_InsideASegment_MapsIntoThatSegmentsOwnRange()
    {
        var axis = TwoNights();

        Assert.Equal(1d / 3d, axis.FractionOf(At(3, 0)), 9);
        Assert.Equal(5d / 6d, axis.FractionOf(At(6, 0)), 9);
        Assert.Equal(1d / 3d, axis.RawFractionOf(At(3, 0)), 9);
        Assert.Equal(5d / 6d, axis.RawFractionOf(At(6, 0)), 9);
    }

    // A failure is a time between the nights mapped through the first segment regardless of
    // date, or clamped to the whole axis instead of to its own segment's edge.
    [Fact]
    public void Stitched_FractionOf_BetweenSegments_UsesTheNearestNightByDate_ClampedToItsEdge()
    {
        var axis = TwoNights();

        // Noon on the 3rd is one day from night A and two from night B: A's end, 2/3.
        Assert.Equal(2d / 3d, axis.FractionOf(At(3, 12)), 9);
        Assert.Equal(4d / 3d, axis.RawFractionOf(At(3, 12)), 9);

        // Noon on the 5th is night B's own date: B's start, also 2/3, reached from below.
        Assert.Equal(2d / 3d, axis.FractionOf(At(5, 12)), 9);
        Assert.Equal(-1d / 6d, axis.RawFractionOf(At(5, 12)), 9);
    }

    [Fact]
    public void Stitched_FractionOf_BeforeTheFirstAndAfterTheLast_Clamps_AndRawDoesNot()
    {
        var axis = TwoNights();

        Assert.Equal(0d, axis.FractionOf(At(2, 18)), 9);
        Assert.Equal(-1d / 6d, axis.RawFractionOf(At(2, 18)), 9);
        Assert.Equal(1d, axis.FractionOf(At(6, 4)), 9);
        Assert.Equal(7d / 6d, axis.RawFractionOf(At(6, 4)), 9);
    }

    [Fact]
    public void Stitched_Boundaries_SitAtEachNightsStart_WithMonthDayLabels()
    {
        var axis = TwoNights();

        Assert.Equal(2, axis.Boundaries.Count);
        Assert.Equal(0d, axis.Boundaries[0].Fraction, 9);
        Assert.Equal("09-02", axis.Boundaries[0].Label);
        Assert.Equal(2d / 3d, axis.Boundaries[1].Fraction, 9);
        Assert.Equal("09-05", axis.Boundaries[1].Label);
    }

    // The trend chart's rule: the year appears only when the set crosses one.
    [Fact]
    public void Stitched_AcrossAYear_LabelsCarryTheYear()
    {
        var eve = new DateTime(2025, 12, 31, 20, 0, 0, DateTimeKind.Unspecified);
        var day = new DateTime(2026, 1, 1, 20, 0, 0, DateTimeKind.Unspecified);

        var axis = NightLaneAxis.Stitched(
        [
            (new DateOnly(2025, 12, 31), Axis(eve, eve.AddHours(8))),
            (new DateOnly(2026, 1, 1), Axis(day, day.AddHours(8))),
        ]);

        Assert.Equal(["2025-12-31", "2026-01-01"], axis.Boundaries.Select(boundary => boundary.Label).ToArray());
    }

    // A failure is anything that binds a single night reading differently than before this phase.
    [Fact]
    public void ASingleNight_IsOneSegmentWithNoBoundary_AndFractionOfReadsAsBefore()
    {
        var axis = Axis(At(2, 20), At(3, 4), 0.5d);

        var segment = Assert.Single(axis.Segments);
        Assert.Equal((At(2, 20), At(3, 4), 0d, 1d), (segment.StartLocal, segment.EndLocal, segment.FromFraction, segment.ToFraction));
        Assert.Empty(axis.Boundaries);
        Assert.Equal(0.5d, axis.FractionOf(At(3, 0)), 9);
        Assert.Equal(0d, axis.FractionOf(At(2, 18)), 9);
        Assert.Equal(-0.25d, axis.RawFractionOf(At(2, 18)), 9);
        Assert.Equal(1.25d, axis.RawFractionOf(At(3, 6)), 9);

        // Stitched over that one night: the same mapping, one segment, one label-only boundary.
        var stitched = NightLaneAxis.Stitched([(new DateOnly(2025, 9, 2), axis)]);
        Assert.Single(stitched.Segments);
        Assert.Equal([(0d, "09-02")], stitched.Boundaries.Select(boundary => (boundary.Fraction, boundary.Label)).ToArray());
        Assert.Equal(axis.FractionOf(At(3, 0)), stitched.FractionOf(At(3, 0)), 9);
        Assert.Equal(axis.RawFractionOf(At(3, 6)), stitched.RawFractionOf(At(3, 6)), 9);
        Assert.Equal([0.5d], stitched.TickFractions);
    }

    private static void Tick()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
