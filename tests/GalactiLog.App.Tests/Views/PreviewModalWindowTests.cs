using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.App.Views.Preview;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.PreviewModalViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for spec 11.5's preview modal: it parses, lays out, binds
// against a populated view-model, and answers CloseRequested. Compiled bindings already turn a
// binding-path typo into a build error; these catch the rest (a missing resource, a template that
// cannot realize, a key binding naming a command that is not there).
//
// Out of scope here and in the spec: pixel comparison, real rendering output, and drag gestures.
// The zoom and pan rules are asserted on the view-model, in PreviewModalViewModelTests.
public class PreviewModalWindowTests
{
    private static PreviewModalWindow Show(object page)
    {
        var window = new PreviewModalWindow { DataContext = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Window_ConstructsAndBindsToAPopulatedViewModel()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        Assert.NotNull(window.GetControl<Image>("PreviewImage"));
        Assert.NotNull(window.GetControl<Button>("PreviousButton"));
        Assert.NotNull(window.GetControl<Button>("NextButton"));
        Assert.NotNull(window.GetControl<Button>("RetryButton"));
        Assert.NotNull(window.GetControl<Border>("HeaderPanelHost"));
        Assert.Equal(harness.ViewModel.Title, window.GetControl<TextBlock>("FrameNameText").Text);
        Assert.Equal("1 of 3", window.GetControl<TextBlock>("PositionText").Text);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_ProducesANonZeroLayout()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);

        // The panel is hidden until H, and the placeholder shows while nothing has rendered.
        Assert.False(window.GetControl<Border>("HeaderPanelHost").IsVisible);
        Assert.True(window.GetControl<TextBlock>("PreviewPlaceholder").IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_ClosesOnCloseRequested()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        harness.ViewModel.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Window_EveryKeyBindingNamesACommandThatExists()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        // Spec 11.5's table, minus the two bare keys: H and 0 are handled in OnKeyDown behind a
        // focus check, because a window KeyBinding on a letter fires while the header panel's
        // filter TextBox has focus (ruling Q19).
        Assert.Equal(6, window.KeyBindings.Count);
        foreach (var binding in window.KeyBindings)
        {
            Assert.NotNull(binding.Command);
        }

        // The two the markup deliberately does not carry are still commands on the view-model,
        // which is where spec 18.3 asserts them.
        Assert.NotNull(harness.ViewModel.ToggleHeaderPanelCommand);
        Assert.NotNull(harness.ViewModel.FitCommand);

        window.Close();
    }

    // P13 R10: the window focuses its image host on open. PreviewModalWindow_OnOpen_FocusesTheViewport
    // is the one case that pins that focus change; measured, the headless harness delivers an
    // unfocused key press to the window regardless (Task 7 review), so the four navigation cases
    // below pass with or without the focus call and instead pin the Up and Down aliases (handled in
    // OnKeyDown beside H and 0, ruling Q22, not as KeyBindings) plus regression cover for the
    // existing Left and Right bindings, each with the same shape and no prior click: open the
    // window, press one key, assert the index moved.
    [AvaloniaFact]
    public void PreviewModalWindow_OnOpen_FocusesTheViewport()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
        Assert.Same(window.GetControl<Border>("Viewport"), focused);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_TheViewport_IsNotATabStop()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        Assert.False(window.GetControl<Border>("Viewport").IsTabStop);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_Right_AdvancesWithNoPriorClick()
    {
        using var harness = Factory.Create(index: 1);
        var window = Show(harness.ViewModel);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, harness.ViewModel.Index);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_Left_GoesBackWithNoPriorClick()
    {
        using var harness = Factory.Create(index: 1);
        var window = Show(harness.ViewModel);

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, harness.ViewModel.Index);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_Down_AdvancesWithNoPriorClick()
    {
        using var harness = Factory.Create(index: 1);
        var window = Show(harness.ViewModel);

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, harness.ViewModel.Index);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_Up_GoesBackWithNoPriorClick()
    {
        using var harness = Factory.Create(index: 1);
        var window = Show(harness.ViewModel);

        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, harness.ViewModel.Index);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_Down_OnTheLastFrame_DoesNothing()
    {
        using var harness = Factory.Create(index: 2);
        var window = Show(harness.ViewModel);

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, harness.ViewModel.Index);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_Up_OnTheFirstFrame_DoesNothing()
    {
        using var harness = Factory.Create(index: 0);
        var window = Show(harness.ViewModel);

        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, harness.ViewModel.Index);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_UpAndDown_AreIgnoredWhileTypingInTheHeaderFilter()
    {
        using var harness = Factory.Create(index: 1);
        var window = Show(harness.ViewModel);

        // Ruling Q19's mechanism, extended to Up and Down by ruling Q22: open the header panel and
        // focus its filter TextBox, the same guard IsTypingInATextBox() already gives H and 0.
        harness.ViewModel.ToggleHeaderPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var filterBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "FilterBox");
        filterBox.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, harness.ViewModel.Index);

        window.Close();
    }

    [AvaloniaFact]
    public void PreviewModalWindow_Escape_StillCloses()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
    }

    [Fact]
    public void Window_UsesOnlyThemeTokens()
    {
        var markup = ReadMarkup();

        // Spec 14: no literal colour anywhere. Every brush is a DynamicResource token.
        Assert.DoesNotMatch(new Regex(@"=""#[0-9A-Fa-f]{3,8}"""), markup);
        Assert.DoesNotMatch(new Regex(@"(Background|Foreground|BorderBrush|Fill|Stroke)=""(?!\{)"), markup);
    }

    [Fact]
    public void Window_SetsNoFontSize()
    {
        // The repository-wide FontSizeTokenTest owns the ratio rule; this is the per-view
        // restatement, and it is stricter: this view sets no FontSize at all.
        Assert.DoesNotContain("FontSize", ReadMarkup(), StringComparison.Ordinal);
    }

    // ---- spec 11.5's metadata strip and render-on-navigation checkbox (PAR-011) ---------------

    [AvaloniaFact]
    public void Window_TheMetadataStrip_IsUnderThePathLine()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        var name = window.GetControl<TextBlock>("FrameNameText");
        var strip = window.GetControl<ItemsControl>("MetadataStrip");

        var nameBottom = name.TranslatePoint(new Point(0, name.Bounds.Height), window)!.Value.Y;
        var stripTop = strip.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        Assert.True(stripTop >= nameBottom, $"The strip's top {stripTop} is above the path line's bottom {nameBottom}.");
        Assert.True(strip.Bounds.Height > 0);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_TheStrip_Wraps()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        var strip = window.GetControl<ItemsControl>("MetadataStrip");

        Assert.IsType<WrapPanel>(strip.ItemsPanelRoot);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_TheBadges_AreBorderTag()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        var strip = window.GetControl<ItemsControl>("MetadataStrip");
        var badges = strip.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("tag"))
            .ToList();

        // Border.tag is the shared badge vocabulary, declared once in Theme/Controls.axaml.
        Assert.NotEmpty(harness.ViewModel.Current.Badges);
        Assert.Equal(harness.ViewModel.Current.Badges.Count, badges.Count);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_TheCheckBox_IsBesideTheStrip()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);

        var strip = window.GetControl<ItemsControl>("MetadataStrip");
        var box = window.GetControl<CheckBox>("RenderOnNavigateCheckBox");

        Assert.Same(strip.Parent, box.Parent);
        Assert.Equal("Render full preview on navigation", box.Content);

        var stripLeft = strip.TranslatePoint(new Point(0, 0), window)!.Value.X;
        var boxLeft = box.TranslatePoint(new Point(0, 0), window)!.Value.X;
        Assert.True(boxLeft > stripLeft, $"The checkbox at {boxLeft} is not beside the strip at {stripLeft}.");

        // Shown always, including for a single-frame list: the web hides it below two frames, and
        // a control that appears and disappears is worse than one that is inert.
        Assert.True(box.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_TheCheckBox_IsShownForASingleFrameList()
    {
        using var harness = Factory.Create(frames: [Factory.Frame(1)]);
        var window = Show(harness.ViewModel);

        Assert.True(window.GetControl<CheckBox>("RenderOnNavigateCheckBox").IsVisible);

        window.Close();
    }

    // Task 3's warning, proven rather than reasoned about: a control-level style in a view's own
    // style host beats an application-level one in Avalonia whatever the selector looks like, which
    // is why FrameTableView had to re-declare the three band rules locally. This window declares no
    // style at all (Window_DeclaresNoBadgeStyleOfItsOwn), so Theme/Controls.axaml's rules are the
    // only ones in play. These two cases measure the ink that actually lands, so a later local
    // style in this file would turn them red instead of silently greying the strip.
    [AvaloniaFact]
    public void Window_TheGradedBadges_RenderInTheBandInks()
    {
        using var harness = Factory.Create(frames: [GradedFrame()]);
        var window = Show(harness.ViewModel);

        Assert.True(Application.Current!.TryFindResource("ColorSuccess", out var success));
        Assert.True(Application.Current!.TryFindResource("ColorWarning", out var warning));
        Assert.True(Application.Current!.TryFindResource("ColorError", out var error));

        Assert.Equal(
            ((ISolidColorBrush)success!).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(BadgeValue(window, "HFR").Foreground).Color);
        Assert.Equal(
            ((ISolidColorBrush)warning!).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(BadgeValue(window, "Ecc").Foreground).Color);

        // A reject badge carries both band-reject and worse. The band class is declared after
        // TextBlock.worse in Theme/Controls.axaml and therefore wins, and ColorMetricWorst and
        // ColorErrorValue are the same colour in all three themes, so the two names spec 11.5 uses
        // resolve to one ink here.
        Assert.Equal(
            ((ISolidColorBrush)error!).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(BadgeValue(window, "FWHM").Foreground).Color);
        Assert.True(Application.Current!.TryFindResource("ColorMetricWorst", out var worst));
        Assert.Equal(((ISolidColorBrush)error).Color, ((ISolidColorBrush)worst!).Color);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_AnUngradedBadge_KeepsTheBadgesOwnInk()
    {
        using var harness = Factory.Create(frames: [GradedFrame()]);
        var window = Show(harness.ViewModel);

        Assert.True(Application.Current!.TryFindResource("ColorTextTertiary", out var tertiary));

        // Filter is never graded and Stars was left ungraded, so neither takes a band ink: the
        // absence of the three classes is the neutral mark.
        foreach (var label in (string[])["Filter", "Exp", "Stars"])
        {
            Assert.Equal(
                ((ISolidColorBrush)tertiary!).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(BadgeValue(window, label).Foreground).Color);
        }

        window.Close();
    }

    /// <summary>The value run of the badge carrying <paramref name="label"/>: each badge is a
    /// Border.tag over a label TextBlock and a value TextBlock, and the marks go on the value.
    /// </summary>
    private static TextBlock BadgeValue(PreviewModalWindow window, string label)
    {
        var badge = window.GetControl<ItemsControl>("MetadataStrip")
            .GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("tag"))
            .Single(border => border.GetVisualDescendants().OfType<TextBlock>().First().Text == label);

        return badge.GetVisualDescendants().OfType<TextBlock>().Last();
    }

    /// <summary>One frame whose HFR is better, eccentricity watch, FWHM reject and detected stars
    /// ungraded, so one window shows all four bands at once.</summary>
    private static PreviewFrameViewModel GradedFrame()
    {
        var grading = new FrameGrading(
            SessionHfr: new(-2.0, 2.0), RigHfr: new(-2.0, 2.0),
            SessionEccentricity: new(2.0, 0.4), RigEccentricity: new(2.0, 0.4),
            SessionFwhm: new(4.0, 1.8), RigFwhm: new(4.0, 1.8),
            DetectedStars: new(null, null),
            AduMedian: new(null, null),
            GuidingRms: new(null, null));

        var row = Factory.Row(
            Guid.NewGuid(), @"C:\Astro\M 31\2025-12-07\frame_0001.fits", "frame_0001.fits", grading);
        return PreviewFrameViewModel.From(Factory.Rows(row)[0]);
    }

    [Fact]
    public void Window_DeclaresNoBadgeStyleOfItsOwn()
    {
        // ControlStyleScanTest's Border.tag needle: Theme/Controls.axaml is the only legal home
        // for it. This view declares no style at all.
        Assert.DoesNotContain("<Style", ReadMarkup(), StringComparison.Ordinal);
        Assert.DoesNotContain("Styles>", ReadMarkup(), StringComparison.Ordinal);
    }

    private static string ReadMarkup()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(
            directory!.FullName, "src", "GalactiLog.App", "Views", "Preview", "PreviewModalWindow.axaml");
        Assert.True(File.Exists(path), $"{path} was not found.");

        // Comments are stripped: this file's own comments explain why it sets no FontSize and why
        // every colour is a token, and a scan that read them would find what it forbids.
        return Regex.Replace(File.ReadAllText(path), "<!--.*?-->", "", RegexOptions.Singleline);
    }

    // Phase 19B Task 4 fix round 1: the gestures moved into the shared ZoomPanGestures, and the
    // preview still zooms on the wheel over its viewport and fits on a double-click.
    [AvaloniaFact]
    public void Window_TheWheelOverTheViewportZooms_AndADoubleClickFits()
    {
        using var harness = Factory.Create();
        var window = Show(harness.ViewModel);
        var viewport = window.GetControl<Border>("Viewport");
        var centre = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;

        window.MouseWheel(centre, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.True(harness.ViewModel.Scale > 1d);

        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1d, harness.ViewModel.Scale);

        window.Close();
    }
}
