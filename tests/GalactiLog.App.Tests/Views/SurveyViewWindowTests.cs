using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.App.Views.Preview;
using GalactiLog.Core.Survey;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// The shell, the keys, the combo and the drag's conversion into fractions of the drawn image.
// The fetch and zoom rules are asserted on the view-model.
public class SurveyViewWindowTests
{
    private static async Task<(SurveyViewWindow Window, SurveyViewViewModel Page, SurveyViewRig Rig)> Open()
    {
        var rig = new SurveyViewRig();
        var page = rig.Build();
        await page.Settled;
        var window = new SurveyViewWindow { DataContext = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, page, rig);
    }

    // Case 12. A failure is a keyboard user who cannot pan or close, or a missing control.
    [AvaloniaFact]
    public async Task TheShell_FocusesTheViewport_ListsFiveSurveys_AndPlacesItsHelpGlyph()
    {
        var (window, page, _) = await Open();

        Assert.Equal("Sky view: M 31", window.Title);
        Assert.Same(window.GetControl<Border>("Viewport"), TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
        Assert.Equal(
            ["DSS2 Color", "DSS2 Red", "2MASS Color", "PanSTARRS DR1", "AllWISE Color"],
            window.GetControl<ComboBox>("SurveyCombo").Items.Cast<SurveyOption>().Select(option => option.Label));
        Assert.Contains(window.GetVisualDescendants().OfType<HelpButton>(), glyph => glyph.Topic == "page.sky-view");
        Assert.Equal("Drag to pan, wheel or + and - to zoom", window.GetControl<TextBlock>("HintText").Text);
        Assert.Equal("Image: CDS hips2fits, DSS2 Color", window.GetControl<TextBlock>("CaptionText").Text);
        Assert.Equal("Close (Esc)", window.GetControl<Button>("CloseButton").Content);
        Assert.NotNull(window.GetControl<Button>("ResetButton"));
        Assert.NotNull(window.GetControl<Button>("RefreshButton"));
        Assert.Same(page.Image, window.GetControl<Image>("SurveyImage").Source);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
        page.Dispose();
    }

    // Case 12, the zoom inputs. A failure is a key row or a wheel that does not scale the image.
    [AvaloniaFact]
    public async Task PlusMinusAndTheWheel_ScaleTheDrawnImage()
    {
        var (window, page, _) = await Open();

        window.KeyPressQwerty(PhysicalKey.NumPadAdd, RawInputModifiers.None);
        Assert.Equal(1.5, page.Scale, 9);
        window.KeyPressQwerty(PhysicalKey.NumPadSubtract, RawInputModifiers.None);
        Assert.Equal(1d, page.Scale, 9);
        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Shift);
        Assert.Equal(1.5, page.Scale, 9);
        window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.None);
        Assert.Equal(1d, page.Scale, 9);

        var viewport = window.GetControl<Border>("Viewport");
        var centre = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(centre, new Vector(0, 1));
        Assert.Equal(1.5, page.Scale, 9);

        window.Close();
        page.Dispose();
    }

    // Tier 1. A failure is a touchpad's fractional deltas each taking a full step, which slams the
    // field to the clamp on one light scroll.
    [AvaloniaFact]
    public async Task FourQuarterWheelDeltas_ZoomOneStep()
    {
        var (window, page, _) = await Open();
        var viewport = window.GetControl<Border>("Viewport");
        var centre = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;

        for (var delta = 0; delta < 3; delta++)
        {
            window.MouseWheel(centre, new Vector(0, 0.25));
        }

        Assert.Equal(1d, page.Scale, 9);
        window.MouseWheel(centre, new Vector(0, 0.25));
        Assert.Equal(1.5, page.Scale, 9);

        window.Close();
        page.Dispose();
    }

    // Tier 1. A failure is a leftover part-notch absorbing a reverse notch, so the wheel does nothing.
    [AvaloniaFact]
    public async Task APartialNotchUpThenAWholeNotchDown_ZoomsOneStepOut()
    {
        var (window, page, _) = await Open();
        var viewport = window.GetControl<Border>("Viewport");
        var centre = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;

        window.MouseWheel(centre, new Vector(0, 0.75));
        window.MouseWheel(centre, new Vector(0, -1));

        // M 31's 4.45 degree field stops at the 5 degree clamp.
        Assert.Equal(4.45 / 5, page.Scale, 9);

        window.Close();
        page.Dispose();
    }

    // Case 13. A failure is a pan converted in window pixels rather than image fractions.
    [AvaloniaFact]
    public async Task ADragOfAQuarterOfTheImageSideToTheRight_CommitsAQuarterFraction()
    {
        var (window, page, rig) = await Open();
        var opening = page.View;
        var viewport = window.GetControl<Border>("Viewport");
        var side = Math.Min(viewport.Bounds.Width, viewport.Bounds.Height);
        var start = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
        var end = start + new Vector(side / 4, 0);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Assert.Equal(side / 4, page.OffsetX, 6);
        window.MouseUp(end, MouseButton.Left);
        await page.Settled;

        var expected = opening.Panned(0.25, 0);
        var fetched = rig.Fetches[^1].View;
        Assert.Equal(2, rig.Fetches.Count);
        Assert.Equal(expected.Ra, fetched.Ra, 9);
        Assert.Equal(expected.Dec, fetched.Dec, 9);

        window.Close();
        page.Dispose();
    }

    // Case 13, north up. A failure is a drag down that lowers the Dec instead of raising it.
    [AvaloniaFact]
    public async Task ADragOfAQuarterOfTheImageSideDown_CommitsAPositiveQuarterAndRaisesTheDec()
    {
        var (window, page, rig) = await Open();
        var opening = page.View;
        var viewport = window.GetControl<Border>("Viewport");
        var side = Math.Min(viewport.Bounds.Width, viewport.Bounds.Height);
        var start = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
        var end = start + new Vector(0, side / 4);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        await page.Settled;

        var expected = opening.Panned(0, 0.25);
        var fetched = rig.Fetches[^1].View;
        Assert.Equal(2, rig.Fetches.Count);
        Assert.True(fetched.Dec > opening.Dec);
        Assert.Equal(expected.Ra, fetched.Ra, 9);
        Assert.Equal(expected.Dec, fetched.Dec, 9);

        window.Close();
        page.Dispose();
    }

    // Case 13 after a zoom. A failure is a fraction taken of the unscaled side, or a pan measured
    // in the pending zoom's field rather than the drawn one.
    [AvaloniaFact]
    public async Task AfterOneZoom_ADragOfAQuarterOfTheScaledSide_PansAQuarterOfTheDrawnField()
    {
        var (window, page, rig) = await Open();
        var opening = page.View;
        page.Zoom(1);
        var viewport = window.GetControl<Border>("Viewport");
        var side = Math.Min(viewport.Bounds.Width, viewport.Bounds.Height) * page.Scale;
        var start = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
        var end = start + new Vector(side / 4, 0);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        await page.Settled;

        var expected = opening.Panned(0.25, 0);
        var fetched = rig.Fetches[^1].View;
        Assert.Equal(2, rig.Fetches.Count);
        Assert.Equal(expected.Ra, fetched.Ra, 9);
        Assert.Equal(opening.Fov / 1.5, fetched.Fov, 9);

        window.Close();
        page.Dispose();
    }
}
