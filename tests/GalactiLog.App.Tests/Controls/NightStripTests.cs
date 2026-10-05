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

// Spec 18.3's smoke shape for the phase's one new drawn control, plus the two rules that are not
// smoke: a pointer press over a tick raises FrameSelected with that tick's frame index, and every
// colour the control paints arrives from the host, so a theme swap repaints it.
public class NightStripTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "GalactiLog NightStrip Control Test (UTC+02)",
        TimeSpan.FromHours(2),
        "GalactiLog NightStrip Control Test",
        "GalactiLog NightStrip Control Test");

    private static readonly ImmutableSolidColorBrush Ink = new(Colors.Gainsboro);
    private static readonly ImmutableSolidColorBrush Worse = new(Colors.IndianRed);

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

    /// <summary>Two ticks at the two ends of the axis and one in the middle, so a press can land
    /// on a known fraction and a press half way between two of them cannot.</summary>
    private static NightStripViewModel Model() => new(
        [Frame(10, 19, 0), Frame(10, 23, 0), Frame(11, 3, 0)],
        new Dictionary<string, ImmutableSolidColorBrush> { ["L"] = Ink },
        Ink,
        null,
        Zone,
        use24Hour: true);

    /// <summary>A night with the band, two filters and one outlier, so a render pass runs the band
    /// fill, the band label, the pen cache over two inks, the outlier pen and all four label
    /// paths.</summary>
    private static NightStripViewModel BandedModel() => new(
        [
            Frame(10, 19, 5),
            Frame(10, 21, 0, "R"),
            Frame(10, 23, 30, "R", outlier: true),
            Frame(11, 1, 15),
            Frame(11, 3, 31, "R"),
        ],
        new Dictionary<string, ImmutableSolidColorBrush> { ["L"] = Ink, ["R"] = Worse },
        Ink,
        (new DateTime(2025, 3, 10, 20, 40, 0, DateTimeKind.Unspecified),
         new DateTime(2025, 3, 11, 4, 10, 0, DateTimeKind.Unspecified)),
        Zone,
        use24Hour: true);

    // Every brush set, because an unset one draws nothing and would skip the very paths a render
    // case exists to run.
    private static (Window Window, NightStrip Strip) Show(NightStripViewModel? model)
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
        };

        var window = new Window { Width = 1180, Height = 200, Content = strip };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // window.Show plus RunJobs pumps the dispatcher but does not drive the render timer, so
        // without this Render never executes and the control's whole output goes untested.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, strip);
    }

    /// <summary>A point in window coordinates over the strip at the given axis fraction.</summary>
    private static Point At(Window window, NightStrip strip, double fraction)
    {
        var local = new Point(strip.X(fraction), NightStrip.StripHeight / 2d);
        return strip.TranslatePoint(local, window) ?? local;
    }

    [AvaloniaFact]
    public void NightStrip_Constructs_AndLaysOutSeventyTwoPixelsTall()
    {
        var (_, strip) = Show(Model());

        Assert.Equal(NightStrip.StripHeight, strip.DesiredSize.Height, 3);
        Assert.Equal(72d, strip.DesiredSize.Height, 3);
        Assert.True(strip.Bounds.Width > 0);
        Assert.Equal(NightStrip.StripHeight, strip.Bounds.Height, 3);
    }

    [AvaloniaFact]
    public void NightStrip_WithNoModel_RendersNothingAndDoesNotThrow()
    {
        var (window, strip) = Show(null);

        Assert.Null(strip.Model);
        Assert.Equal(NightStrip.StripHeight, strip.DesiredSize.Height, 3);

        // A press with no model is a no-op rather than a null reference.
        window.MouseDown(At(window, strip, 0.5d), MouseButton.Left);
        window.MouseUp(At(window, strip, 0.5d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void NightStrip_PointerPressOnATick_RaisesFrameSelectedWithThatIndex()
    {
        var model = Model();
        var raised = new List<int>();
        model.FrameSelected += (_, index) => raised.Add(index);

        var (window, strip) = Show(model);
        var middle = model.Ticks[1];

        window.MouseDown(At(window, strip, middle.Fraction), MouseButton.Left);
        window.MouseUp(At(window, strip, middle.Fraction), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([middle.FrameIndex], raised);
    }

    [AvaloniaFact]
    public void NightStrip_PointerPressBetweenTicks_RaisesNothing()
    {
        var model = Model();
        var raised = new List<int>();
        model.FrameSelected += (_, index) => raised.Add(index);

        var (window, strip) = Show(model);

        // A quarter of the way along the axis: the nearest tick is a quarter of the strip away,
        // which is far outside the hit width.
        window.MouseDown(At(window, strip, 0.25d), MouseButton.Left);
        window.MouseUp(At(window, strip, 0.25d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(raised);
    }

    // Review P2-1. Render is the whole of this control's output, and a throw inside it is caught
    // and logged by Avalonia, so a defect there ships as a blank strip. This case drives a real
    // render pass over every branch: the band fill, the band label, the axis, the hour marks, the
    // pen cache over two filter inks, the outlier pen and the first and last labels.
    [AvaloniaFact]
    public void NightStrip_WithABandAndAnOutlier_RendersEveryDrawingPath()
    {
        var model = BandedModel();

        var (window, strip) = Show(model);

        // The model really does carry every branch the render pass needs.
        Assert.True(model.HasBand);
        Assert.Equal("astronomical night", model.BandLabel);
        Assert.Equal(1, model.OutlierCount);
        Assert.Equal(2, model.Ticks.Select(tick => tick.Brush).Distinct().Count());
        Assert.NotEmpty(model.AxisTicks);
        Assert.NotEqual("", model.FirstLabel);
        Assert.NotEqual("", model.LastLabel);

        // And a second pass after a resize, which is the other way Render is entered.
        window.Width = 900;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Assert.True(strip.Bounds.Width > 0);
        Assert.Equal(NightStrip.StripHeight, strip.DesiredSize.Height, 3);

        window.Close();
    }

    // A failure is a night too short to hold an even-hour mark drawing no clock label, only
    // "first" and "last".
    [AvaloniaFact]
    public void NightStrip_OnAOneHourNight_LabelsItsTwoEnds()
    {
        var model = new NightStripViewModel(
            [Frame(10, 19, 5), Frame(10, 19, 50)],
            new Dictionary<string, ImmutableSolidColorBrush> { ["L"] = Ink },
            Ink,
            null,
            Zone,
            use24Hour: true);

        var (window, strip) = Show(model);

        Assert.Empty(model.AxisTicks);
        Assert.Equal(["19:00", "20:00"], strip.LastAxisLabels);
        window.Close();
    }

    // A failure is the end label printed over the start label in a pane too narrow for both.
    [AvaloniaFact]
    public void NightStrip_OnAOneHourNight_InANarrowPane_DropsTheEndLabel()
    {
        var model = new NightStripViewModel(
            [Frame(10, 19, 5), Frame(10, 19, 50)],
            new Dictionary<string, ImmutableSolidColorBrush> { ["L"] = Ink },
            Ink,
            null,
            Zone,
            use24Hour: true);

        var (window, strip) = Show(model);
        window.Width = 40;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["19:00"], strip.LastAxisLabels);
        window.Close();
    }

    // Review P2-2, the first half of the brief's sentence: a click on a tick takes focus.
    [AvaloniaFact]
    public void NightStrip_PointerPressOnATick_TakesFocus()
    {
        var model = Model();
        var (window, strip) = Show(model);
        var middle = model.Ticks[1];

        Assert.False(strip.IsFocused);

        window.MouseDown(At(window, strip, middle.Fraction), MouseButton.Left);
        window.MouseUp(At(window, strip, middle.Fraction), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(strip.IsFocused);
        Assert.False(strip.IsTabStop);

        window.Close();
    }

    // Review P2-2, the second half: a press that misses every tick must leave focus where it was,
    // which on the shipped page is the frame table.
    [AvaloniaFact]
    public void NightStrip_PointerPressBetweenTicks_DoesNotTakeFocus()
    {
        var (window, strip) = Show(Model());

        window.MouseDown(At(window, strip, 0.25d), MouseButton.Left);
        window.MouseUp(At(window, strip, 0.25d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(strip.IsFocused);

        window.Close();
    }

    // ---- Phase 13 Task 6: R9's hover, raised from the same hit test the tooltip uses -----------
    //
    // Avalonia hit-tests a drawn control against the operations it recorded, not against its
    // bounds. Render therefore fills the whole 72 px band with Brushes.Transparent before anything
    // else (review P2-1), so the pointer is over the strip everywhere inside it and a point away
    // from every tick is a move that resolves no tick rather than a pointer that has left. The
    // heights below are still named, because two of these cases are about which ink is under the
    // pointer and one is about being off the control entirely.

    /// <summary>A point in window coordinates over the strip at the given axis fraction and the
    /// given height inside it.</summary>
    private static Point At(Window window, NightStrip strip, double fraction, double y)
    {
        var local = new Point(strip.X(fraction), y);
        return strip.TranslatePoint(local, window) ?? local;
    }

    /// <summary>Half way up a tick line, which runs from 26 to 42 in the control's own
    /// coordinates.</summary>
    private const double OnATick = 34d;

    /// <summary>Inside the astronomical-night band's fill, which runs from 10 to 44.</summary>
    private const double InTheBand = 20d;

    /// <summary>Inside the strip and below every tick, where the control paints nothing but the
    /// transparent pointer surface: 60 is under TickBottom at 42 and clear of the axis at 44.
    /// </summary>
    private const double BelowEveryTick = 60d;

    [AvaloniaFact]
    public void NightStrip_PointerMoveOverATick_RaisesFrameHoveredWithThatIndex()
    {
        var model = Model();
        var raised = new List<int?>();
        model.FrameHovered += (_, index) => raised.Add(index);

        var (window, strip) = Show(model);
        var middle = model.Ticks[1];

        window.MouseMove(At(window, strip, middle.Fraction, OnATick));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([middle.FrameIndex], raised);

        window.Close();
    }

    [AvaloniaFact]
    public void NightStrip_PointerMoveBetweenTicks_RaisesFrameHoveredWithNull()
    {
        // The banded night, because the band fill is the one place the strip is hit-testable well
        // away from a tick: this is a move that reaches the control and resolves no tick, rather
        // than a pointer that has left it.
        var model = BandedModel();
        var raised = new List<int?>();
        model.FrameHovered += (_, index) => raised.Add(index);

        var (window, strip) = Show(model);

        var between = (model.Ticks[1].Fraction + model.Ticks[2].Fraction) / 2d;
        Assert.True(
            Math.Abs(between - model.Ticks[1].Fraction) > NightStripViewModel.HitFraction,
            "the midpoint is inside the hit width, so this case would assert nothing");

        window.MouseMove(At(window, strip, model.Ticks[1].Fraction, OnATick));
        window.MouseMove(At(window, strip, between, InTheBand));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([model.Ticks[1].FrameIndex, null], raised);
        Assert.True(
            strip.IsPointerOver,
            "the pointer is still over the strip, so this is a miss rather than an exit");

        window.Close();
    }

    [AvaloniaFact]
    public void NightStrip_PointerInsideTheStripButOffEveryTick_StaysOver_AndRaisesNull()
    {
        // Review P2-1. Without the transparent fill at the top of Render this control answers the
        // pointer only on a 1.4 px tick line, so a pointer a few pixels below a tick has left the
        // control altogether and the tint goes out by way of PointerExited rather than by way of a
        // move that resolved no tick. Acceptance bar item 7 is what fails when that happens.
        //
        // The unbanded model on purpose: it paints no band fill, so 60 px down is bare pointer
        // surface and nothing else.
        var model = Model();
        var raised = new List<int?>();
        model.FrameHovered += (_, index) => raised.Add(index);

        var (window, strip) = Show(model);
        var middle = model.Ticks[1];

        window.MouseMove(At(window, strip, middle.Fraction, OnATick));
        // A quarter along the axis and below every tick: no ink in either direction, which is
        // exactly where the control was not under the pointer at all before the fill.
        window.MouseMove(At(window, strip, 0.25d, BelowEveryTick));
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            strip.IsPointerOver,
            "the pointer left the strip, so the whole band is not a pointer surface");
        Assert.Equal([middle.FrameIndex, null], raised);

        window.Close();
    }

    [AvaloniaFact]
    public void NightStrip_PointerExit_RaisesFrameHoveredWithNull()
    {
        // R9: leaving the strip clears the tint. The same handler that drops the tooltip.
        var model = Model();
        var raised = new List<int?>();
        model.FrameHovered += (_, index) => raised.Add(index);

        var (window, strip) = Show(model);

        window.MouseMove(At(window, strip, model.Ticks[1].Fraction, OnATick));

        // Well below the 72 px strip, which is what a pointer leaving it does.
        window.MouseMove(new Point(600d, 150d));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([model.Ticks[1].FrameIndex, null], raised);
        Assert.False(strip.IsPointerOver);

        window.Close();
    }

    [AvaloniaFact]
    public void NightStrip_TwoMovesOverTheSameTick_RaiseOnce()
    {
        // Pointer moves arrive by the hundred across one pass of the strip. The event is raised
        // only when the tick under the pointer changes, for the same reason the tooltip is
        // written only then. The two points are the same tick line at two heights, because the
        // tick is the only ink this model paints there.
        var model = Model();
        var raised = new List<int?>();
        model.FrameHovered += (_, index) => raised.Add(index);

        var (window, strip) = Show(model);
        var middle = model.Ticks[1];

        window.MouseMove(At(window, strip, middle.Fraction, 30d));
        window.MouseMove(At(window, strip, middle.Fraction, 40d));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([middle.FrameIndex], raised);

        window.Close();
    }

    [AvaloniaFact]
    public void NightStrip_AHover_RaisesNoFrameSelected()
    {
        // A hover highlights and scrolls; it never changes the selection.
        var model = Model();
        var selected = new List<int>();
        model.FrameSelected += (_, index) => selected.Add(index);

        var (window, strip) = Show(model);

        window.MouseMove(At(window, strip, model.Ticks[0].Fraction, OnATick));
        window.MouseMove(At(window, strip, model.Ticks[2].Fraction, OnATick));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(selected);

        window.Close();
    }

    [AvaloniaFact]
    public void NightStrip_ANewModel_ForgetsTheOldHover()
    {
        // Without the reset a new night inherits the previous night's hovered index, and the
        // first move over that same tick raises nothing at all.
        var first = Model();
        var (window, strip) = Show(first);

        window.MouseMove(At(window, strip, first.Ticks[1].Fraction, 30d));
        Dispatcher.UIThread.RunJobs();

        var second = Model();
        var raised = new List<int?>();
        second.FrameHovered += (_, index) => raised.Add(index);

        strip.Model = second;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        window.MouseMove(At(window, strip, second.Ticks[1].Fraction, 40d));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([second.Ticks[1].FrameIndex], raised);

        window.Close();
    }

    [AvaloniaFact]
    public void NightStrip_BrushProperties_ComeFromTheHostAndNotFromALiteral()
    {
        var first = new ImmutableSolidColorBrush(Colors.SlateGray);
        var second = new ImmutableSolidColorBrush(Colors.DarkOliveGreen);

        var strip = new NightStrip { Model = Model(), VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 1180, Height = 200, Content = strip };
        window.Resources["NightStripTestBand"] = first;
        window.Show();

        // The code form of BandBrush="{DynamicResource ...}", which is how Task 5's markup sets
        // all five. AvaloniaRuntimeXamlLoader is not referenced by this solution, so the binding
        // is built here rather than parsed from inline markup.
        strip.Bind(NightStrip.BandBrushProperty, strip.GetResourceObservable("NightStripTestBand"));
        Dispatcher.UIThread.RunJobs();

        Assert.Same(first, strip.BandBrush);

        // A theme swap replaces the object behind the key; a DynamicResource repaints, a literal
        // would not.
        window.Resources["NightStripTestBand"] = second;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(second, strip.BandBrush);

        // And the other five default to null rather than to a colour of their own.
        Assert.Null(strip.AxisBrush);
        Assert.Null(strip.LabelBrush);
        Assert.Null(strip.OutlierBrush);
        Assert.Null(strip.FirstLastLabelBrush);
        Assert.Null(strip.ActiveBrush);

        window.Close();
    }

    // The choke point, in the shape FontSizeTokenTest established: the control must contain no
    // colour of its own, so a literal cannot creep in on a later edit and survive a theme swap.
    [Fact]
    public void NightStripSource_ContainsNoColourLiteral()
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

    // ---- Phase 25 R5: the stitched strip's render. --------------------------------------------

    /// <summary>Two banded nights three days apart, stitched; the second night's frames start at
    /// merged index 5.</summary>
    private static NightStripViewModel StitchedModel() => NightStripViewModel.Stitched(
    [
        (new DateOnly(2025, 3, 10), BandedModel(), 0),
        (new DateOnly(2025, 3, 13), new NightStripViewModel(
            [Frame(13, 22, 0), Frame(14, 0, 30, "R")],
            new Dictionary<string, ImmutableSolidColorBrush> { ["L"] = Ink, ["R"] = Worse },
            Ink,
            (new DateTime(2025, 3, 13, 21, 50, 0, DateTimeKind.Unspecified),
             new DateTime(2025, 3, 14, 3, 5, 0, DateTimeKind.Unspecified)),
            Zone,
            use24Hour: true), 5),
    ]);

    // A failure is one band for two nights, no boundary line, or the band label drawn per band.
    [AvaloniaFact]
    public void NightStrip_OnAStitchedModel_DrawsEveryBand_OneDashedBoundary_AndEachDateOnce()
    {
        var model = StitchedModel();
        Assert.Equal(2, model.Bands.Count);

        var (window, strip) = Show(model);

        Assert.Equal(2, strip.LastBandCount);
        Assert.Equal(1, strip.LastBoundaryLineCount);
        Assert.Equal(["03-10", "03-13", "astronomical night"], strip.LastBandLabels);

        window.Close();
    }

    // A single night draws exactly what it drew before this phase.
    [AvaloniaFact]
    public void NightStrip_OnASingleNight_DrawsOneBand_NoBoundaryLine_AndTheBandLabelOnce()
    {
        var (window, strip) = Show(BandedModel());

        Assert.Equal(1, strip.LastBandCount);
        Assert.Equal(0, strip.LastBoundaryLineCount);
        Assert.Equal(["astronomical night"], strip.LastBandLabels);

        window.Close();
    }

    // D211: red while the strip or the timeline part still carries a brush property.
    [Fact]
    public void TheStripAndItsPart_HaveNoBrushProperty()
    {
        Assert.Null(typeof(NightStrip).GetProperty("BrushEnabled"));
        Assert.Null(typeof(NightStrip).GetProperty("BrushBrush"));
        Assert.Null(typeof(GalactiLog.App.Views.TargetDetail.Parts.NightTimelinePart).GetProperty("BrushEnabled"));
    }
}
