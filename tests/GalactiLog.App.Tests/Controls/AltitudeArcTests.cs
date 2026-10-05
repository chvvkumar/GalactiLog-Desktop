using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Controls;

// Spec 18.3's smoke shape for the fourth drawn control: it constructs, it lays out non-zero, it
// reports the wedge under a pointer, and it reports one under keyboard focus as well, which spec
// 12.5 requires by name and which no pointer-driven case can see.
public class AltitudeArcTests
{
    private static readonly IImmutableSolidColorBrush Swatch = new ImmutableSolidColorBrush(Colors.Teal);

    private static IReadOnlyList<AltitudeWedge> Wedges(bool withData = true) =>
    [
        Wedge(GuidingAltitudeBand.Below30, withData, "0.75", "x1.50", "2"),
        Wedge(GuidingAltitudeBand.From30To60, withData, "0.60", "x1.20", "3"),
        Wedge(GuidingAltitudeBand.Above60, withData, "0.50", "x1.00", "5"),
    ];

    private static AltitudeWedge Wedge(
        GuidingAltitudeBand band, bool hasData, string total, string ratio, string sessions)
        => new(
            "RC8",
            band,
            GuidingViewModel.BandLabel(band),
            hasData,
            total,
            "0.33",
            "0.38",
            sessions,
            ratio,
            Swatch);

    private static (Window Window, AltitudeArc Arc) Show(IReadOnlyList<AltitudeWedge>? wedges = null)
    {
        var arc = new AltitudeArc
        {
            Wedges = wedges ?? Wedges(),
            OutlineBrush = Brushes.Gray,
            ValueBrush = Brushes.Black,
            LabelBrush = Brushes.Gray,
            Width = AltitudeArc.BaseWidth,
        };

        var window = new Window { Width = 400, Height = 300, Content = arc };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, arc);
    }

    [AvaloniaFact]
    public void Constructs_AndLaysOutNonZero()
    {
        var (_, arc) = Show();

        Assert.True(arc.Bounds.Width > 0);
        Assert.True(arc.Bounds.Height > 0);
        Assert.Equal(AltitudeArc.BaseHeight * arc.CurrentScale, arc.DesiredSize.Height, 3);
    }

    [AvaloniaFact]
    public void HoveredWedge_IsTheWedgeUnderThePointer_AndLeavingClearsIt()
    {
        var (window, arc) = Show();

        // Just above the horizon on the right, which is the low band by construction: the observer
        // is at the lower left and altitude rises anticlockwise.
        var low = arc.WedgeAt(new Point(120d * arc.CurrentScale, 150d * arc.CurrentScale));
        Assert.NotNull(low);
        Assert.Equal(GuidingAltitudeBand.Below30, low!.Band);

        // Straight up from the observer is the high band.
        var high = arc.WedgeAt(new Point(20d * arc.CurrentScale, 40d * arc.CurrentScale));
        Assert.NotNull(high);
        Assert.Equal(GuidingAltitudeBand.Above60, high!.Band);

        // The arc is narrower than the window and is centred in it, so the synthetic pointer takes
        // window coordinates and the control's own point has to be translated into them.
        window.MouseMove(arc.TranslatePoint(
            new Point(120d * arc.CurrentScale, 150d * arc.CurrentScale), window)!.Value);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(GuidingAltitudeBand.Below30, arc.HoveredWedge?.Band);

        // And leaving the control clears it: the far corner of the window is off the dome.
        window.MouseMove(new Point(window.Width - 5, window.Height - 5));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(arc.HoveredWedge);
        window.Close();
    }

    [AvaloniaFact]
    public void WedgeAt_IsNull_OutsideTheDome()
    {
        var (_, arc) = Show();

        // Beyond the radius, and behind the observer, which is off the quarter entirely.
        Assert.Null(arc.WedgeAt(new Point(185d * arc.CurrentScale, 155d * arc.CurrentScale)));
        Assert.Null(arc.WedgeAt(new Point(2d * arc.CurrentScale, 168d * arc.CurrentScale)));
    }

    [AvaloniaFact]
    public void Focus_OpensTheTooltip_AndBlurClosesIt()
    {
        // Spec 12.5: the figures behind a shape have to be reachable without a pointer. This is the
        // half no pointer-driven case can see, and it is the opposite of NightStrip's rule, which
        // earns focus only on a press.
        var arc = new AltitudeArc { Wedges = Wedges(), Width = AltitudeArc.BaseWidth };
        var other = new Button { Content = "elsewhere" };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new StackPanel { Children = { arc, other } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(arc.Focusable);
        arc.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(arc.HoveredWedge);
        Assert.Equal(GuidingAltitudeBand.Below30, arc.HoveredWedge!.Band);

        other.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(arc.HoveredWedge);
        window.Close();
    }

    [AvaloniaFact]
    public void ArrowKeys_StepBetweenTheThreeWedges()
    {
        var (window, arc) = Show();
        arc.Focus();
        Dispatcher.UIThread.RunJobs();

        Step(arc, Key.Right);
        Assert.Equal(GuidingAltitudeBand.From30To60, arc.HoveredWedge?.Band);

        Step(arc, Key.Right);
        Assert.Equal(GuidingAltitudeBand.Above60, arc.HoveredWedge?.Band);

        // And it stops at the end rather than wrapping.
        Step(arc, Key.Right);
        Assert.Equal(GuidingAltitudeBand.Above60, arc.HoveredWedge?.Band);

        Step(arc, Key.Left);
        Assert.Equal(GuidingAltitudeBand.From30To60, arc.HoveredWedge?.Band);
        window.Close();
    }

    [AvaloniaFact]
    public void NoWedges_LaysOutWithoutThrowing_AndDividesByNothing()
    {
        var arc = new AltitudeArc { Width = AltitudeArc.BaseWidth };
        var window = new Window { Width = 400, Height = 300, Content = arc };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(arc.WedgeAt(new Point(60, 120)));
        Assert.Null(arc.HoveredWedge);
        arc.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(arc.HoveredWedge);
        window.Close();
    }

    [AvaloniaFact]
    public void AWedgeWithNoData_DrawsWithoutThrowing()
    {
        var (_, arc) = Show(Wedges(withData: false));

        Assert.All(arc.Wedges!, wedge => Assert.Equal(AltitudeWedge.NoDataText, wedge.ValueText));
        Assert.True(arc.Bounds.Height > 0);
    }

    private static void Step(AltitudeArc arc, Key key)
    {
        arc.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void AltitudeArcSource_ContainsNoColourLiteral()
    {
        // Spec 14.5 and ruling G5: every ink arrives from the host through a DynamicResource and
        // the wedge shades arrive on the wedge records. The needle is NightStripTests'.
        var source = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Controls", "AltitudeArc.cs"));

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

        Assert.Empty(offenders);
    }
}
