using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using GalactiLog.App.Controls;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Controls;

// Task 7 (user ruling U1). A new file rather than an append to the 16-case NightStripTests.cs, so
// the geometry harness stays readable and that file's own figure stays quotable (brief section
// 10.2). U1's own verification bar: with 200 ticks at the shipped pane width, the active tick's
// drawn rectangle differs from an inactive tick's in both height and ink, asserted on the render
// geometry GeometryFor exposes, never on a property that merely holds the index.
public class NightStripActiveTickTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "GalactiLog NightStrip Active Tick Test (UTC+02)",
        TimeSpan.FromHours(2),
        "GalactiLog NightStrip Active Tick Test",
        "GalactiLog NightStrip Active Tick Test");

    private static readonly ImmutableSolidColorBrush Ink = new(Colors.Gainsboro);
    private static readonly ImmutableSolidColorBrush Worse = new(Colors.IndianRed);
    private static readonly ImmutableSolidColorBrush Active = new(Colors.DodgerBlue);

    private static DateTime Utc(int day, int hour, int minute)
        => TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2025, 3, day, hour, minute, 0, DateTimeKind.Unspecified),
            Zone);

    private static FrameRow Frame(int day, int hour, int minute, string filter = "L", bool outlier = false)
        => GalactiLog.App.Tests.ViewModels.FrameTableViewModelTests.Frame(
            captureDate: Utc(day, hour, minute),
            filterUsed: filter,
            medianHfr: 2.4) with
        {
            IsHfrOutlier = outlier,
        };

    /// <summary>Three dated ticks, one of them an outlier, so the outlier-and-active collision
    /// case has a frame to point at.</summary>
    private static NightStripViewModel Model() => new(
        [Frame(10, 19, 0), Frame(10, 21, 0, outlier: true), Frame(10, 23, 0)],
        new Dictionary<string, ImmutableSolidColorBrush> { ["L"] = Ink },
        Ink,
        null,
        Zone,
        use24Hour: true);

    /// <summary>An undated frame at index 1, so the strip has three frames but only two ticks: the
    /// gap <c>AnActiveFrameWithNoTick_DrawsNothing</c> needs.</summary>
    private static NightStripViewModel ModelWithAGap()
    {
        var undated = GalactiLog.App.Tests.ViewModels.FrameTableViewModelTests.Frame(captureDate: null);
        return new(
            [Frame(10, 19, 0), undated, Frame(10, 23, 0)],
            new Dictionary<string, ImmutableSolidColorBrush> { ["L"] = Ink },
            Ink,
            null,
            Zone,
            use24Hour: true);
    }

    /// <summary>200 dated ticks a minute apart, all one filter: enough to be "close-set" and no
    /// distraction from filter colour variety, since the case under test is height and ink against
    /// the active tick alone.</summary>
    private static NightStripViewModel TwoHundredTicksModel()
    {
        var frames = new List<FrameRow>(200);
        for (var i = 0; i < 200; i++)
        {
            frames.Add(Frame(10, 19 + (i / 60), i % 60));
        }

        return new NightStripViewModel(
            frames,
            new Dictionary<string, ImmutableSolidColorBrush> { ["L"] = Ink },
            Ink,
            null,
            Zone,
            use24Hour: true);
    }

    private static (Window Window, NightStrip Strip) Show(NightStripViewModel? model, double width = 1180)
    {
        var strip = new NightStrip
        {
            Model = model,
            VerticalAlignment = VerticalAlignment.Top,
            BandBrush = Ink,
            AxisBrush = Ink,
            LabelBrush = Ink,
            OutlierBrush = Worse,
            FirstLastLabelBrush = Ink,
            ActiveBrush = Active,
        };

        var window = new Window { Width = width, Height = 200, Content = strip };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The forced render tick that keeps this case honest alongside the geometry assertions
        // (brief section 6.3): if Render throws, this is where it is caught and logged.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, strip);
    }

    private static void Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Point At(Window window, NightStrip strip, double fraction, double y = 34d)
    {
        var local = new Point(strip.X(fraction), y);
        return strip.TranslatePoint(local, window) ?? local;
    }

    [AvaloniaFact]
    public void WithNoActiveFrame_NothingIsDrawnForIt()
    {
        var model = Model();
        var (_, strip) = Show(model);

        foreach (var tick in model.Ticks)
        {
            Assert.NotSame(Active, strip.GeometryFor(tick).Ink);
        }
    }

    [AvaloniaFact]
    public void TheHoveredTick_IsTheActiveOne()
    {
        var model = Model();
        var (window, strip) = Show(model);
        var middle = model.Ticks[1];

        window.MouseMove(At(window, strip, middle.Fraction));
        Render(window);

        // Review P1/P2: GeometryFor is a live computation off _hoveredIndex and would return the
        // active ink whether or not Render ran again, which is exactly what let the missing
        // InvalidateVisual through. LastDrawOrder is what Render actually painted, so this is the
        // assertion that fails without the fix.
        Assert.Equal(middle.FrameIndex, strip.LastDrawOrder[^1].FrameIndex);
        Assert.Same(Active, strip.GeometryFor(middle).Ink);
    }

    [AvaloniaFact]
    public void TheTableSelection_IsTheActiveOne_AtRest()
    {
        var model = Model();
        var (window, strip) = Show(model);
        var middle = model.Ticks[1];

        model.SetActiveFrame(middle.FrameIndex);
        Render(window);

        Assert.Same(Active, strip.GeometryFor(middle).Ink);
    }

    [AvaloniaFact]
    public void AHover_WinsOverTheTableSelection()
    {
        // 12.4's own ordering: "the tick under the pointer and, at rest, ...". A resting selection
        // must not out-rank a live hover.
        var model = Model();
        var (window, strip) = Show(model);
        var atRest = model.Ticks[0];
        var hovered = model.Ticks[2];

        model.SetActiveFrame(atRest.FrameIndex);
        window.MouseMove(At(window, strip, hovered.Fraction));
        Render(window);

        // Review P2: proves the hover's repaint, not just the accessor's live computation.
        Assert.Equal(hovered.FrameIndex, strip.LastDrawOrder[^1].FrameIndex);
        Assert.Same(Active, strip.GeometryFor(hovered).Ink);
        Assert.NotSame(Active, strip.GeometryFor(atRest).Ink);
    }

    [AvaloniaFact]
    public void LeavingTheStrip_FallsBackToTheTableSelection()
    {
        var model = Model();
        var (window, strip) = Show(model);
        var atRest = model.Ticks[0];
        var hovered = model.Ticks[2];

        model.SetActiveFrame(atRest.FrameIndex);
        window.MouseMove(At(window, strip, hovered.Fraction));
        Render(window);
        Assert.Equal(hovered.FrameIndex, strip.LastDrawOrder[^1].FrameIndex);
        Assert.Same(Active, strip.GeometryFor(hovered).Ink);

        // Well below the strip, which is what a pointer leaving it does (the existing hover cases
        // use the same shape).
        window.MouseMove(new Point(600d, 150d));
        Render(window);

        // Review P1/P2: the leave must repaint too, or the strip visually keeps the old hover's
        // mark until something unrelated happens to dirty it.
        Assert.Equal(atRest.FrameIndex, strip.LastDrawOrder[^1].FrameIndex);
        Assert.Same(Active, strip.GeometryFor(atRest).Ink);
        Assert.NotSame(Active, strip.GeometryFor(hovered).Ink);
    }

    [AvaloniaFact]
    public void AtTwoHundredTicks_TheActiveTickIsTallerAndInAnotherInk()
    {
        // U1's own bar. The cheaper of the two cases the brief asks for (section 7): this one runs
        // at the control's own 1180 comp width so a failure localizes to the control rather than
        // the layout; the layout-width case lives in ModesLayoutViewTests.
        var model = TwoHundredTicksModel();
        var (_, strip) = Show(model, width: 1180);

        var active = model.Ticks[100];
        var inactive = model.Ticks[10];
        model.SetActiveFrame(active.FrameIndex);

        var activeGeometry = strip.GeometryFor(active);
        var inactiveGeometry = strip.GeometryFor(inactive);

        Assert.True(
            activeGeometry.Rect.Height > inactiveGeometry.Rect.Height,
            $"active height {activeGeometry.Rect.Height} did not exceed inactive height {inactiveGeometry.Rect.Height}");
        Assert.NotSame(activeGeometry.Ink, inactiveGeometry.Ink);
    }

    [AvaloniaFact]
    public void TheActiveTick_ExtendsPastTheBandAtBothEnds()
    {
        var model = Model();
        var (_, strip) = Show(model);
        var active = model.Ticks[1];

        model.SetActiveFrame(active.FrameIndex);
        var geometry = strip.GeometryFor(active);

        Assert.True(
            geometry.Rect.Top < NightStrip.BandTop,
            $"the active tick's top {geometry.Rect.Top} did not clear the band's top {NightStrip.BandTop}");
        Assert.True(
            geometry.Rect.Bottom > NightStrip.AxisY,
            $"the active tick's bottom {geometry.Rect.Bottom} did not clear the band's bottom {NightStrip.AxisY}");
    }

    [AvaloniaFact]
    public void TheActiveTick_IsTwoPixelsWide()
    {
        // Spec 12.4's own literal, "at 2 pixels wide", the same width the outlier tick already
        // draws at.
        var model = Model();
        var (_, strip) = Show(model);
        var active = model.Ticks[1];

        model.SetActiveFrame(active.FrameIndex);

        Assert.Equal(2d, strip.GeometryFor(active).Rect.Width);
    }

    [AvaloniaFact]
    public void TheActiveTick_TakesTheActiveBrush_AndNotTheOutlierBrush_WhenItIsBoth()
    {
        // User ruling U2. The model's own outlier tick (index 1) is the one this case marks active.
        var model = Model();
        var outlierTick = model.Ticks[1];
        Assert.True(outlierTick.IsOutlier, "the fixture no longer names an outlier tick");

        var (_, strip) = Show(model);
        model.SetActiveFrame(outlierTick.FrameIndex);

        var geometry = strip.GeometryFor(outlierTick);
        Assert.Same(Active, geometry.Ink);
        Assert.NotSame(Worse, geometry.Ink);
    }

    [AvaloniaFact]
    public void EveryOtherTick_KeepsItsFilterInk_WhenOneIsActive()
    {
        var model = Model();
        var (_, strip) = Show(model);
        var active = model.Ticks[0];
        model.SetActiveFrame(active.FrameIndex);

        foreach (var tick in model.Ticks)
        {
            if (tick.FrameIndex == active.FrameIndex)
            {
                continue;
            }

            var geometry = strip.GeometryFor(tick);
            var expectedInk = tick.IsOutlier ? (IBrush)Worse : tick.Brush;
            Assert.Same(expectedInk, geometry.Ink);
            Assert.NotSame(Active, geometry.Ink);
        }
    }

    [AvaloniaFact]
    public void TheActiveTick_IsDrawnLast()
    {
        var model = Model();
        var (window, strip) = Show(model);
        var active = model.Ticks[0];

        model.SetActiveFrame(active.FrameIndex);
        Render(window);

        Assert.NotEmpty(strip.LastDrawOrder);
        Assert.Equal(active.FrameIndex, strip.LastDrawOrder[^1].FrameIndex);
    }

    [AvaloniaFact]
    public void ANewModel_ForgetsTheOldActiveFrame()
    {
        var first = Model();
        first.SetActiveFrame(first.Ticks[0].FrameIndex);
        var (window, strip) = Show(first);
        Assert.Same(Active, strip.GeometryFor(first.Ticks[0]).Ink);

        var second = Model();
        strip.Model = second;
        Render(window);

        foreach (var tick in second.Ticks)
        {
            Assert.NotSame(Active, strip.GeometryFor(tick).Ink);
        }
    }

    [AvaloniaFact]
    public void AnActiveFrameWithNoTick_DrawsNothing()
    {
        // The undated frame at index 1 produces no tick (spec's null-CaptureDate gap). Naming it
        // active must throw nothing and mark nothing.
        var model = ModelWithAGap();
        var (window, strip) = Show(model);

        model.SetActiveFrame(1);
        Render(window);

        foreach (var tick in model.Ticks)
        {
            Assert.NotSame(Active, strip.GeometryFor(tick).Ink);
        }
    }

    [AvaloniaFact]
    public void DetachingFromTheVisualTree_UnsubscribesFromTheOldModel()
    {
        // Review P3. The strip lives inside a ContentControl's DataTemplate, and a presenter that
        // rebuilds its child rather than rebinding it never changes the discarded control's Model,
        // so the ModelProperty swap alone cannot unsubscribe it. This is a lifetime assertion, not
        // a behaviour one: InvalidateVisual on a detached control paints nothing observable either
        // way, so the proof is that the view-model's event no longer names the control at all.
        var model = Model();
        var (window, strip) = Show(model);

        Assert.Equal(1, ActiveFrameChangedSubscriberCount(model));

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, ActiveFrameChangedSubscriberCount(model));

        window.Content = strip;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, ActiveFrameChangedSubscriberCount(model));

        window.Close();
    }

    private static int ActiveFrameChangedSubscriberCount(NightStripViewModel model)
    {
        var field = typeof(NightStripViewModel).GetField(
            "ActiveFrameChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        var handler = field?.GetValue(model) as MulticastDelegate;
        return handler?.GetInvocationList().Length ?? 0;
    }

    [AvaloniaFact]
    public void ChangingModelWhileDetached_DoesNotSubscribeToTheNewModel()
    {
        // Phase review P3 (NightStrip.cs:309). SyncActiveFrameSubscription used to run from the
        // Model property change unconditionally, alongside attach and detach, so a Model swap on
        // a control already removed from the visual tree re-subscribed with no matching detach
        // left to unwind it: the leak the attach/detach pair (above) was added to close, reopened
        // by the very change that closed it. Subscribing is now gated to the attached case, so a
        // Model swap while detached leaves the new model with zero subscribers until (and unless)
        // the control is reattached, at which point OnAttachedToVisualTree resyncs against
        // whatever Model is current then.
        var first = Model();
        var (window, strip) = Show(first);
        Assert.Equal(1, ActiveFrameChangedSubscriberCount(first));

        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, ActiveFrameChangedSubscriberCount(first));

        var second = Model();
        strip.Model = second;

        // The assertion that fails before the fix: today's code subscribes to `second` the
        // instant Model changes, attached or not.
        Assert.Equal(0, ActiveFrameChangedSubscriberCount(second));

        window.Content = strip;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, ActiveFrameChangedSubscriberCount(second));
        Assert.Equal(0, ActiveFrameChangedSubscriberCount(first));

        window.Close();
    }

    [AvaloniaFact]
    public void WithNoActiveBrushBound_TheActiveTickDegradesToItsOwnInk()
    {
        // Review P3. An unbound ActiveBrush must not make the active tick vanish: both earlier
        // passes still skip it because it is active, so if GeometryFor kept returning the active
        // shape in a null ink, the third pass would fill it with Brushes.Transparent. Only a host
        // that skips the binding reaches this; NightTimelinePart.axaml always binds it.
        var model = Model();
        var strip = new NightStrip
        {
            Model = model,
            VerticalAlignment = VerticalAlignment.Top,
            BandBrush = Ink,
            AxisBrush = Ink,
            LabelBrush = Ink,
            OutlierBrush = Worse,
            FirstLastLabelBrush = Ink,
            // ActiveBrush deliberately left unbound.
        };
        var window = new Window { Width = 1180, Height = 200, Content = strip };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var active = model.Ticks[0];
        model.SetActiveFrame(active.FrameIndex);
        Render(window);

        var geometry = strip.GeometryFor(active);
        Assert.NotNull(geometry.Ink);
        Assert.Same(active.Brush, geometry.Ink);
        Assert.Equal(16d, geometry.Rect.Height, 3);

        window.Close();
    }

    [AvaloniaFact]
    public void TheActiveTick_IsDrawnAfterTheEdgeLabels()
    {
        // Review P3. The first and last labels are drawn at the same x as the first and last
        // ticks' own fractions, so an active first tick's overshoot used to sit under whichever
        // label was drawn afterward. There is no pixel readback in this repository, so the order
        // is pinned through the two recorded ordinals rather than through what got painted over.
        var model = Model();
        var (window, strip) = Show(model);
        var first = model.Ticks[0];
        Assert.NotEqual("", model.FirstLabel);

        model.SetActiveFrame(first.FrameIndex);
        Render(window);

        Assert.NotNull(strip.LastEdgeLabelDrawOrdinal);
        Assert.NotNull(strip.LastActiveTickDrawOrdinal);
        Assert.True(
            strip.LastActiveTickDrawOrdinal > strip.LastEdgeLabelDrawOrdinal,
            $"active ordinal {strip.LastActiveTickDrawOrdinal} did not follow edge label ordinal {strip.LastEdgeLabelDrawOrdinal}");
    }

    [AvaloniaFact]
    public void WithNoFirstLastLabelBrushBound_TheEdgeLabelOrdinalStaysUnset()
    {
        // Phase review P3 (NightStrip.cs:504). LastEdgeLabelDrawOrdinal used to be assigned in
        // Render right after the two DrawLabel calls, "beside" the draw rather than inside it, so
        // it advanced even when DrawLabel's own null-brush guard skipped the actual paint, which
        // is what let the case above pass for the wrong reason: it proved two assignment
        // statements stayed in source order, not that the active tick painted after a label that
        // actually drew. The increment now lives inside DrawLabel's own draw branch, so a label
        // that never paints (no brush bound, here) leaves the ordinal unset.
        var model = Model();
        var (window, strip) = Show(model);
        strip.FirstLastLabelBrush = null;
        Render(window);

        Assert.Null(strip.LastEdgeLabelDrawOrdinal);
    }

    [AvaloniaFact]
    public void TheStripStillFitsSeventyTwoPixels_WithAnActiveTick()
    {
        var model = Model();
        var (_, strip) = Show(model);

        model.SetActiveFrame(model.Ticks[0].FrameIndex);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(NightStrip.StripHeight, strip.DesiredSize.Height, 3);
        Assert.Equal(72d, strip.DesiredSize.Height, 3);
        Assert.Equal(NightStrip.StripHeight, strip.Bounds.Height, 3);
    }

    // The same choke point NightStripTests.NightStripSource_ContainsNoColourLiteral runs, re-run
    // here because the original lives in the file this one deliberately does not append to.
    [Fact]
    public void NightStripSource_StillContainsNoColourLiteral()
    {
        var path = Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Controls", "NightStrip.cs");
        var source = File.ReadAllText(path);

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
            "NightStrip.cs takes every colour from a styled property set by the host. Found: "
                + string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }
}
