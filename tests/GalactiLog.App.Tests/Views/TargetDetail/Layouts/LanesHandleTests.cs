using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Layouts;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetLayoutProbes;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Layouts;

// The handle between the night lanes and the frames table, the one handle since the night pane
// round: the session chart takes what the cap leaves under the timeline, so a drag sizes both.
public sealed class LanesHandleTests
{
    private const double Tolerance = 1.5d;

    private sealed class Mounted : IDisposable
    {
        public required Factory.Harness Harness { get; init; }
        public required UserControl View { get; init; }
        public required Window Window { get; init; }
        public required Cards.Harness Night { get; init; }
        public required string Key { get; init; }

        public ScrollViewer Lanes => View.Named<ScrollViewer>("LanesRegion");
        public LanesHandle Handle => View.Named<LanesHandle>("LanesHandle");
        public Control Frames => View.Named<Control>("FramesRegion");
        public double FramesMin => ((ModesLayoutView)View).FramesMinHeight;
        public double LanesFloor => ((ModesLayoutView)View).LanesFloor;
        public Control Timeline => View.Named<Control>("NightTimelinePart");
        public Control Chart => View.Named<Control>("NightMetricsPart");

        public void Settle()
        {
            TargetPartHost.SettleLoads(Harness.ViewModel, [Night.Card]);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public Point HandleCentre(LanesHandle? handle = null)
        {
            handle ??= Handle;
            return handle.TranslatePoint(new Point(handle.Bounds.Width / 2d, handle.Bounds.Height / 2d), Window) ?? default;
        }

        public void Drag(double dy, LanesHandle? handle = null)
        {
            var from = HandleCentre(handle);
            TargetPartHost.Press(Window, from);
            Window.MouseMove(from + new Point(0, dy / 2d));
            Window.MouseMove(from + new Point(0, dy));
            Dispatcher.UIThread.RunJobs();
            Midway = Lanes.Bounds.Height;
            Window.MouseUp(from + new Point(0, dy), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Settle();
        }

        public void DoubleClick(LanesHandle? handle = null)
        {
            var at = HandleCentre(handle);
            for (var i = 0; i < 2; i++)
            {
                TargetPartHost.Press(Window, at);
                Window.MouseUp(at, MouseButton.Left);
            }

            Dispatcher.UIThread.RunJobs();
            Settle();
        }

        // The lanes height while the pointer was still down, read at the end of a Drag.
        public double Midway { get; private set; }

        public void Dispose()
        {
            Settle();
            Window.Close();
            Settle();
            Night.Dispose();
        }
    }

    private static Factory.Harness Page(TargetPageSettings? stored = null)
        => Factory.Create(targetPage: stored, fullNight: true, post: action => Dispatcher.UIThread.Post(action)).Settle();

    private static Mounted Mount(Factory.Harness harness, string key, double width = 1280, double height = 720)
    {
        UserControl view = new ModesLayoutView();
        view.DataContext = harness.ViewModel;
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        TargetPartHost.SettleLoads(harness.ViewModel, []);
        var night = RealNight(harness);
        view.Named<Control>("NightReviewRegion").DataContext = night.Card;
        var mounted = new Mounted { Harness = harness, View = view, Window = window, Night = night, Key = key };
        mounted.Settle();
        return mounted;
    }

    private static TargetPageSettings Stored(string key, double height)
        => new() { Layouts = new() { [key] = new TargetLayoutState { LanesHeight = height } } };

    // The Session metrics section closed, so the chart is in the lanes viewport at rest.
    private static TargetPageSettings StoredClosed(string key, double? lanes = null, double? chart = null)
        => new() { Layouts = new() { [key] = new TargetLayoutState { ChartHeight = chart, LanesHeight = lanes, NightMetricsOpen = false } } };

    private static DisplaySettings Replay(IEnumerable<Func<DisplaySettings, DisplaySettings>> writes)
        => writes.Aggregate(new DisplaySettings(), (document, write) => write(document));

    [AvaloniaTheory]
    [InlineData("modes")]
    public void NoStoredValue_TheLanesRegionKeepsTheAutomaticRule(string key)
    {
        // A failure looks like a lanes region fixed to a number, or without its cap, when nothing is stored.
        using var harness = Page();
        using var mounted = Mount(harness, key);

        Assert.True(double.IsNaN(mounted.Lanes.Height), $"height {mounted.Lanes.Height}");
        Assert.False(double.IsPositiveInfinity(mounted.Lanes.MaxHeight));

        Assert.Empty(harness.DisplayWrites);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void ADrag_MovesTheSplitLive_AndWritesOnceOnRelease(string key)
    {
        // A failure looks like a handle that moves nothing, a write per pointer move, or no write on release.
        using var harness = Page();
        using var mounted = Mount(harness, key);
        var start = mounted.Lanes.Bounds.Height;
        var framesBefore = mounted.Frames.Bounds.Height;
        var chartBefore = mounted.Chart.Bounds.Height;

        mounted.Drag(-40);

        Assert.InRange(mounted.Midway, start - 40 - Tolerance, start - 40 + Tolerance);
        Assert.InRange(mounted.Lanes.Bounds.Height, start - 40 - Tolerance, start - 40 + Tolerance);
        Assert.True(mounted.Frames.Bounds.Height > framesBefore + 30d);
        // The one handle sizes both: the chart gave up what the frames gained.
        Assert.InRange(mounted.Chart.Bounds.Height, chartBefore - 40 - Tolerance, chartBefore - 40 + Tolerance);
        var write = Assert.Single(harness.DisplayWrites);
        Assert.InRange(write(new DisplaySettings()).TargetPage.Layouts[key].LanesHeight!.Value, start - 40 - Tolerance, start - 40 + Tolerance);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void ADrag_WritesNothingWhileThePointerIsDown(string key)
    {
        // A failure looks like the document written on every move.
        using var harness = Page();
        using var mounted = Mount(harness, key);
        var from = mounted.HandleCentre();

        TargetPartHost.Press(mounted.Window, from);
        mounted.Window.MouseMove(from + new Point(0, -20));
        mounted.Window.MouseMove(from + new Point(0, -30));
        Dispatcher.UIThread.RunJobs();
        var duringDrag = harness.DisplayWrites.Count;
        mounted.Window.MouseUp(from + new Point(0, -30), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, duringDrag);
        Assert.Single(harness.DisplayWrites);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void ADoubleClick_ClearsTheValue_AndReturnsToTheAutomaticRule(string key)
    {
        // A failure looks like a stored height that stays after the double click, or a write that is not a clear.
        var stored = Stored(key, 300);
        using var harness = Page(stored);
        using var mounted = Mount(harness, key);
        Assert.Equal(300d, mounted.Lanes.MaxHeight);

        mounted.DoubleClick();

        Assert.True(double.IsNaN(mounted.Lanes.Height), $"height {mounted.Lanes.Height}");
        Assert.Null(harness.TargetPage.LanesHeight(key));
        var write = Assert.Single(harness.DisplayWrites);
        Assert.Null(write(Replay([])).TargetPage.Layouts[key].LanesHeight);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void TheKeyboard_UpAndDownMoveTheSplit_AndCommitOnceOnRelease(string key)
    {
        // A failure looks like a handle that cannot be reached or moved without a pointer.
        using var harness = Page();
        using var mounted = Mount(harness, key);
        var start = mounted.Lanes.Bounds.Height;

        mounted.Handle.Focus();
        for (var repeat = 0; repeat < 2; repeat++)
        {
            mounted.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Empty(harness.DisplayWrites);
        mounted.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        mounted.Settle();

        var expected = start - (2 * LanesHandle.KeyStep);
        Assert.InRange(mounted.Lanes.Bounds.Height, expected - Tolerance, expected + Tolerance);
        Assert.Single(harness.DisplayWrites);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void AKeyHeldWhenADragStarts_WritesItsHeightAtTheStart_AndTheDragWritesOnRelease(string key)
    {
        // Starting a drag ends the held key input, so its release mid drag adds nothing. A failure
        // looks like the key's write landing mid drag, after the press instead of at it.
        using var harness = Page();
        using var mounted = Mount(harness, key);
        var start = mounted.Lanes.Bounds.Height;
        mounted.Handle.Focus();
        mounted.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        var from = mounted.HandleCentre();

        TargetPartHost.Press(mounted.Window, from);
        Assert.Single(harness.DisplayWrites);
        mounted.Window.MouseMove(from + new Point(0, -40));
        mounted.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(harness.DisplayWrites);
        mounted.Window.MouseUp(from + new Point(0, -40), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, harness.DisplayWrites.Count);
        var keyWrite = harness.DisplayWrites[0](new DisplaySettings()).TargetPage.Layouts[key].LanesHeight!.Value;
        Assert.InRange(keyWrite, start - LanesHandle.KeyStep - Tolerance, start - LanesHandle.KeyStep + Tolerance);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void DetachingMidKeyHold_WritesTheHeldHeightOnce(string key)
    {
        // A failure looks like a held key height lost when the page goes away, so the placed split reverts.
        using var harness = Page();
        using var mounted = Mount(harness, key);
        var start = mounted.Lanes.Bounds.Height;
        mounted.Handle.Focus();
        mounted.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        mounted.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.Empty(harness.DisplayWrites);

        mounted.Window.Content = null;
        Dispatcher.UIThread.RunJobs();

        var write = Assert.Single(harness.DisplayWrites);
        Assert.InRange(write(new DisplaySettings()).TargetPage.Layouts[key].LanesHeight!.Value, start - (2 * LanesHandle.KeyStep) - Tolerance, start - (2 * LanesHandle.KeyStep) + Tolerance);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void DetachingMidDrag_WritesThePlacedHeightOnce(string key)
    {
        // A failure looks like a drag in progress lost when the page goes away.
        using var harness = Page();
        using var mounted = Mount(harness, key);
        var start = mounted.Lanes.Bounds.Height;
        var from = mounted.HandleCentre();
        TargetPartHost.Press(mounted.Window, from);
        mounted.Window.MouseMove(from + new Point(0, -30));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(harness.DisplayWrites);

        mounted.Window.Content = null;
        Dispatcher.UIThread.RunJobs();

        var write = Assert.Single(harness.DisplayWrites);
        Assert.InRange(write(new DisplaySettings()).TargetPage.Layouts[key].LanesHeight!.Value, start - 30 - Tolerance, start - 30 + Tolerance);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void AKeyDuringADrag_IsIgnored_AndTheReleaseWritesTheDragOnce(string key)
    {
        // A failure looks like the key moving the split under the pointer, or a write for the key
        // on top of the drag's.
        using var harness = Page();
        using var mounted = Mount(harness, key);
        var start = mounted.Lanes.Bounds.Height;
        var from = mounted.HandleCentre();
        TargetPartHost.Press(mounted.Window, from);
        mounted.Window.MouseMove(from + new Point(0, -30));
        mounted.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        mounted.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(harness.DisplayWrites);

        mounted.Window.MouseUp(from + new Point(0, -30), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var write = Assert.Single(harness.DisplayWrites);
        Assert.InRange(write(new DisplaySettings()).TargetPage.Layouts[key].LanesHeight!.Value, start - 30 - Tolerance, start - 30 + Tolerance);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void HoldingPastTheMaximum_ThenTheOppositeKey_MovesAtOnce(string key)
    {
        // A failure looks like an accumulator that runs past the limit, so the opposite key shows no move until it has wound back.
        using var harness = Page();
        using var mounted = Mount(harness, key);
        mounted.Handle.Focus();
        for (var i = 0; i < 60; i++)
        {
            mounted.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        }

        mounted.Settle();
        var largest = mounted.Lanes.Bounds.Height;
        mounted.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        mounted.Settle();

        Assert.InRange(mounted.Lanes.Bounds.Height, largest - LanesHandle.KeyStep - Tolerance, largest - LanesHandle.KeyStep + Tolerance);
        mounted.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void ATooLargeStoredValue_KeepsTheFramesChromeAndOneRow_AtAnySize_AndIsNotRewritten(string key)
    {
        // A failure looks like frames pushed off the page by a value from a taller window, on load or on shrinking.
        using var harness = Page(Stored(key, 100_000));
        using var mounted = Mount(harness, key, 1600, 900);
        mounted.Window.Height = 720;
        mounted.Settle();

        var rows = mounted.View.Named<FramesPart>("FramesPart").Named<ListBox>("FrameRows");
        var row = rows.ContainerFromIndex(0)!.Bounds.Height;
        var chrome = mounted.Frames.Bounds.Height - rows.Bounds.Height;
        Assert.True(rows.Bounds.Height >= row - Tolerance, $"no whole first row: list {rows.Bounds.Height}, row {row}");
        Assert.InRange(mounted.FramesMin, chrome + row - Tolerance, chrome + row + Tolerance);
        Assert.True(mounted.Lanes.Bounds.Height < 720d);
        Assert.Equal(100_000, harness.TargetPage.LanesHeight(key));
        Assert.Empty(harness.DisplayWrites);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void ATooSmallStoredValue_KeepsTheTimelineLaneAndTheChartFloor_AndIsNotRewritten(string key)
    {
        // A failure looks like the timeline lane cut off, or the chart squashed under 120, by a
        // value from a smaller layout, or the stored value rewritten by the clamp.
        using var harness = Page(Stored(key, 1));
        using var mounted = Mount(harness, key);

        // The Session metrics section's open body is left out of the least: it scrolls (Phase 24 R2).
        var section = mounted.View.Named<Expander>("NightMetricsSection");
        var body = section.Bounds.Height - section.GetVisualDescendants().OfType<ToggleButton>().First().Bounds.Height;
        var least = mounted.Timeline.Bounds.Bottom - body + mounted.Chart.Margin.Top + ModesLayoutView.ChartFloor;
        Assert.InRange(mounted.Lanes.Bounds.Height, least - Tolerance, least + Tolerance);
        Assert.InRange(mounted.Chart.Bounds.Height, ModesLayoutView.ChartFloor - Tolerance, ModesLayoutView.ChartFloor + Tolerance);
        Assert.Equal(1, harness.TargetPage.LanesHeight(key));
        Assert.Empty(harness.DisplayWrites);
    }

    [AvaloniaFact]
    public void TheValue_SurvivesARemountTwice_AndARelaunch()
    {
        // A failure looks like a value lost when the page is mounted again or relaunched.
        using var harness = Page();
        double modesDragged;
        using (var modes = Mount(harness, "modes", 1600, 900))
        {
            modes.Drag(-40);
            modesDragged = modes.Lanes.Bounds.Height;
        }

        for (var round = 0; round < 2; round++)
        {
            using var modes = Mount(harness, "modes", 1600, 900);
            Assert.InRange(modes.Lanes.Bounds.Height, modesDragged - Tolerance, modesDragged + Tolerance);
        }

        Assert.Single(harness.DisplayWrites);
        var relaunched = new TargetPageState(Replay(harness.DisplayWrites).TargetPage);
        Assert.Equal(harness.TargetPage.LanesHeight("modes"), relaunched.LanesHeight("modes"));
    }

    // ---- The chart under the one handle (night pane round; Phase 24 R31's second handle retired) --

    [AvaloniaTheory]
    [InlineData("modes")]
    public void NoStoredValue_TheChartIsAtItsDefault_AndEndsAtTheLanesBottom(string key)
    {
        // A failure looks like a chart left at its markup height while the cap moved, or a cap
        // taller than the chart's bottom with nothing stored (the frames would sit lower than the
        // content), or a second handle back under the chart.
        using var harness = Page(StoredClosed(key));
        using var mounted = Mount(harness, key, 1600, 900);

        Assert.InRange(mounted.Chart.Bounds.Height, ModesLayoutView.ChartHeightDefault - Tolerance, ModesLayoutView.ChartHeightDefault + Tolerance);
        Assert.InRange(BoundsIn(mounted.Chart, mounted.Lanes).Bottom, mounted.Lanes.Bounds.Height - Tolerance, mounted.Lanes.Bounds.Height + 0.5);
        Assert.InRange(mounted.Lanes.Bounds.Height, mounted.LanesFloor - Tolerance, mounted.LanesFloor + Tolerance);
        Assert.Null(mounted.View.NamedOrNull<LanesHandle>("ChartHandle"));
        Assert.Empty(harness.DisplayWrites);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void AStoredLanesHeight_GivesTheChartTheRemainderUnderTheTimeline(string key)
    {
        // A failure looks like a stored lanes height applied to the cap but not to the chart, so
        // the region shows empty space under a 180 chart.
        using var harness = Page(StoredClosed(key, lanes: 400));
        using var mounted = Mount(harness, key, 1600, 900);

        Assert.InRange(mounted.Lanes.Bounds.Height, 400 - Tolerance, 400 + Tolerance);
        Assert.InRange(BoundsIn(mounted.Chart, mounted.Lanes).Bottom, 400 - Tolerance, 400 + 0.5);
        Assert.True(mounted.Chart.Bounds.Height > ModesLayoutView.ChartHeightDefault + 20d, $"the chart is {mounted.Chart.Bounds.Height} tall under a 400 cap");
        Assert.Empty(harness.DisplayWrites);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void ADragDown_GrowsTheChartByTheSameAmount_AndWritesTheLanesHeightOnly(string key)
    {
        // A failure looks like a drag that moves the split but leaves the chart at its height, or a
        // write under chart_height, the retired key.
        using var harness = Page(StoredClosed(key));
        using var mounted = Mount(harness, key, 1600, 900);
        var start = mounted.Chart.Bounds.Height;
        var lanes = mounted.Lanes.Bounds.Height;

        mounted.Drag(60);

        Assert.InRange(mounted.Chart.Bounds.Height, start + 60 - Tolerance, start + 60 + Tolerance);
        Assert.InRange(mounted.Lanes.Bounds.Height, lanes + 60 - Tolerance, lanes + 60 + Tolerance);
        var write = Assert.Single(harness.DisplayWrites);
        var layout = write(new DisplaySettings()).TargetPage.Layouts[key];
        Assert.InRange(layout.LanesHeight!.Value, lanes + 60 - Tolerance, lanes + 60 + Tolerance);
        Assert.Null(layout.ChartHeight);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void ADragPastTheChartFloor_StopsAtTheFloor_AndWritesTheFloorHeight(string key)
    {
        // A failure looks like a chart squashed under 120 by a drag, or the write carrying the pointer's height rather than the clamped one.
        using var harness = Page(StoredClosed(key));
        using var mounted = Mount(harness, key, 1600, 900);
        var lanes = mounted.Lanes.Bounds.Height;

        mounted.Drag(-300);

        Assert.InRange(mounted.Chart.Bounds.Height, ModesLayoutView.ChartFloor - Tolerance, ModesLayoutView.ChartFloor + Tolerance);
        var floor = lanes - (ModesLayoutView.ChartHeightDefault - ModesLayoutView.ChartFloor);
        Assert.InRange(mounted.Lanes.Bounds.Height, floor - Tolerance, floor + Tolerance);
        var write = Assert.Single(harness.DisplayWrites);
        Assert.InRange(write(new DisplaySettings()).TargetPage.Layouts[key].LanesHeight!.Value, floor - Tolerance, floor + Tolerance);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void TheHandlesDoubleClick_ReturnsTheChartTo180(string key)
    {
        // A failure looks like a stored lanes height cleared while the chart keeps the grown height.
        using var harness = Page(StoredClosed(key, lanes: 400));
        using var mounted = Mount(harness, key, 1600, 900);
        Assert.True(mounted.Chart.Bounds.Height > ModesLayoutView.ChartHeightDefault + 20d);

        mounted.DoubleClick();

        Assert.InRange(mounted.Chart.Bounds.Height, ModesLayoutView.ChartHeightDefault - Tolerance, ModesLayoutView.ChartHeightDefault + Tolerance);
        Assert.Null(harness.TargetPage.LanesHeight(key));
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void AStoredChartHeight_IsIgnored_AndNotRewritten(string key)
    {
        // A failure looks like the retired chart_height key still sizing the chart, or being cleared on load.
        using var harness = Page(StoredClosed(key, chart: 300));
        using var mounted = Mount(harness, key, 1600, 900);

        Assert.InRange(mounted.Chart.Bounds.Height, ModesLayoutView.ChartHeightDefault - Tolerance, ModesLayoutView.ChartHeightDefault + Tolerance);
        Assert.Equal(300, harness.TargetPage.Layout(key).ChartHeight);
        Assert.Empty(harness.DisplayWrites);
    }

    [AvaloniaTheory]
    [InlineData("modes")]
    public void TheLanesScroll_WhenTheMetricsSectionOpensPastTheirStoredHeight(string key)
    {
        // A failure looks like the Session metrics section's open body charged to the chart (the chart
        // shrinks) or to the cap (the lanes grow) instead of scrolling (Phase 24 R2 and R15).
        using var harness = Page(Stored(key, 330));
        using var mounted = Mount(harness, key, 1600, 900);
        var section = mounted.View.Named<Expander>("NightMetricsSection");
        Assert.True(section.IsExpanded);

        Assert.InRange(mounted.Lanes.Bounds.Height, 330 - Tolerance, 330 + Tolerance);
        Assert.InRange(mounted.Chart.Bounds.Height, ModesLayoutView.ChartFloor - Tolerance, 330d);
        Assert.True(mounted.Lanes.Extent.Height > mounted.Lanes.Viewport.Height + 50d, $"extent {mounted.Lanes.Extent.Height}, viewport {mounted.Lanes.Viewport.Height}");

        section.IsExpanded = false;
        mounted.Settle();
        Assert.InRange(mounted.Lanes.Bounds.Height, 330 - Tolerance, 330 + Tolerance);
        Assert.True(mounted.Lanes.Extent.Height <= mounted.Lanes.Viewport.Height + 0.5, $"extent {mounted.Lanes.Extent.Height}, viewport {mounted.Lanes.Viewport.Height} with the section closed");
    }
}
