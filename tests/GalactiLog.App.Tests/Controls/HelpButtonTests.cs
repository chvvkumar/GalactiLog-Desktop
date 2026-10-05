using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.Core.Help;
using Xunit;

namespace GalactiLog.App.Tests.Controls;

// Spec 12.12's gesture table, made executable. The table is narrower than the web's on purpose:
// the web opens the popover on hover after 150 ms and the port does not (UI layout ruling 10),
// because a reader moving the pointer across a page of headings would otherwise walk through a
// corridor of opening panels. APointerEnter_OpensNothing is that ruling and is the case a reviewer
// will look for: it raises a real pointer move over the control and runs the dispatcher, because a
// test that only asserts "no handler is subscribed" proves nothing about a style trigger.
public class HelpButtonTests
{
    private const string Topic = "target.frames";

    private static (Window Window, HelpButton Button) Show(string topic = Topic)
    {
        var button = new HelpButton
        {
            Topic = topic,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var window = new Window { Width = 600, Height = 400, Content = button };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, button);
    }

    private static Point Centre(Window window, HelpButton button)
    {
        var local = new Point(button.Bounds.Width / 2d, button.Bounds.Height / 2d);
        return button.TranslatePoint(local, window) ?? local;
    }

    private static void Click(Window window, HelpButton button)
    {
        var point = Centre(window, button);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsOpen(HelpButton button) => button.Flyout?.IsOpen == true;

    /// <summary>The flyout's popup host, once it is open, so its text can be read. Headless hosts
    /// a flyout in the window's overlay layer rather than in a second top level, so this is an
    /// <c>OverlayPopupHost</c> here and a <c>PopupRoot</c> on a desktop backend; both are visuals
    /// and the case only reads text out of one.</summary>
    private static Visual FlyoutHost(HelpButton button)
    {
        var host = ((IPopupHostProvider)button.Flyout!).PopupHost;
        Assert.NotNull(host);
        return Assert.IsAssignableFrom<Visual>(host);
    }

    [AvaloniaFact]
    public void TheStyle_GivesTheGlyphItsContentAndItsClass()
    {
        var (_, button) = Show();

        // The one style in Theme/Controls.axaml selects on Button.help, which the control adds to
        // itself. Without it the glyph has no drawn content at all.
        Assert.Contains(HelpButton.GlyphClass, button.Classes);
        Assert.NotNull(button.Flyout);
        Assert.True(button.Bounds.Width > 0);
        Assert.True(button.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void AClick_OpensTheFlyout()
    {
        var (window, button) = Show();

        Assert.False(IsOpen(button));
        Click(window, button);

        Assert.True(IsOpen(button));
    }

    [AvaloniaFact]
    public void AHeaderHeldByAContentControl_OpensInTheFlyoutWithoutThrowing()
    {
        // A failure is "already has a parent" on the first open, the header still held by its holder.
        var header = new TextBlock { Text = "legend" };
        var holder = new ContentControl { Content = header };
        var button = new HelpButton { Topic = Topic, FlyoutHeader = header };
        var window = new Window { Width = 600, Height = 400, Content = new StackPanel { Children = { holder, button } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Click(window, button);

        Assert.True(IsOpen(button));
        Assert.Contains(FlyoutHost(button), header.GetVisualAncestors());
    }

    [AvaloniaFact]
    public void ASecondClick_ClosesIt()
    {
        var (window, button) = Show();

        Click(window, button);
        Assert.True(IsOpen(button));

        Click(window, button);
        Assert.False(IsOpen(button));
    }

    [AvaloniaFact]
    public void SpaceWhileFocused_OpensTheFlyout()
    {
        var (window, button) = Show();

        button.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(IsOpen(button));
    }

    [AvaloniaFact]
    public void EnterWhileFocused_OpensTheFlyout()
    {
        var (window, button) = Show();

        button.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(IsOpen(button));
    }

    [AvaloniaFact]
    public void Escape_ClosesIt()
    {
        var (window, button) = Show();

        Click(window, button);
        Assert.True(IsOpen(button));

        // Spec 12.12 says the open flyout takes focus, so the paragraph is reachable and
        // dismissable from the keyboard alone: the key goes to the window and has to reach
        // wherever focus went.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(IsOpen(button));
    }

    [AvaloniaFact]
    public void AClickOutside_ClosesIt()
    {
        var (window, button) = Show();

        Click(window, button);
        Assert.True(IsOpen(button));

        var away = new Point(window.Width - 20d, window.Height - 20d);
        window.MouseDown(away, MouseButton.Left);
        window.MouseUp(away, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(IsOpen(button));
    }

    [AvaloniaFact]
    public void APointerEnter_OpensNothing()
    {
        var (window, button) = Show();

        // A real pointer move over the control, then the dispatcher, then a render tick, so a
        // style trigger or a delayed open would have had every chance to run.
        window.MouseMove(Centre(window, button));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Assert.True(button.IsPointerOver);
        Assert.False(IsOpen(button));
    }

    [AvaloniaFact]
    public void TheFlyout_CarriesTheTopicTitleAndParagraph()
    {
        var (window, button) = Show();
        var expected = HelpTopics.Get(Topic);

        Click(window, button);

        var texts = FlyoutHost(button)
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .ToList();

        Assert.Contains(expected.Title, texts);
        Assert.Contains(expected.Paragraph, texts);
    }

    [AvaloniaFact]
    public void TheTopicProjections_FollowTheTopic()
    {
        var (_, button) = Show("page.activity");

        Assert.Equal("Activity", button.TopicTitle);
        Assert.Equal(HelpTopics.Get("page.activity").Paragraph, button.TopicParagraph);

        button.Topic = "page.merge";

        Assert.Equal("Preview merge", button.TopicTitle);
        Assert.Equal(HelpTopics.Get("page.merge").Paragraph, button.TopicParagraph);
    }

    [AvaloniaFact]
    public void TheAccessibleName_IsAboutThisSectionPlusTheTitle()
    {
        var (_, button) = Show("stats.storage");

        Assert.Equal("About this section Storage", AutomationProperties.GetName(button));
        Assert.Equal(HelpButton.AccessibleNamePrefix + button.TopicTitle, AutomationProperties.GetName(button));
    }

    [AvaloniaFact]
    public void AnUnknownTopic_Throws()
    {
        // Not swallowed and no empty panel rendered in its place. HelpPlacementCensusTest is what
        // keeps a build from reaching this throw, and the throw is what makes the census load
        // bearing.
        Assert.Throws<KeyNotFoundException>(() => new HelpButton { Topic = "page.nowhere" });
    }

    [AvaloniaFact]
    public void TheButton_IsATabStop()
    {
        var (_, button) = Show();

        Assert.True(button.Focusable);
        Assert.True(button.IsTabStop);

        button.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsFocused);
    }

    [AvaloniaFact]
    public void TheOpenFlyout_TakesTheFocus()
    {
        // Spec 12.12's gesture table, the row that had no direct case (Task 2A review P3). The
        // control states FlyoutShowMode.Standard rather than leaving it to the default, and what
        // that buys is a paragraph the keyboard can reach and dismiss without the pointer.
        var (window, button) = Show();

        Click(window, button);
        Assert.True(IsOpen(button));

        var host = FlyoutHost(button);
        var focused = TopLevel.GetTopLevel(button)?.FocusManager?.GetFocusedElement();

        Assert.NotNull(focused);
        Assert.True(
            ReferenceEquals(focused, host)
                || (focused is Visual visual && visual.GetVisualAncestors().Any(a => ReferenceEquals(a, host))),
            $"the focus is on {focused!.GetType().Name}, outside the open flyout");
    }

    [AvaloniaFact]
    public void F1_OnAFocusedGlyph_OpensNothing()
    {
        // The other row of the table with no direct case. F1 is not a gesture this application
        // binds (ruling C1 refuses it), and the glyph must not grow one by accident.
        var (window, button) = Show();

        button.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsFocused);

        window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(IsOpen(button));
    }

    [AvaloniaFact]
    public void ATopicChangedWhileDetached_RebuildsTheFlyout()
    {
        // Task 2A review P3. OnAttachedToVisualTree rebuilt the flyout only when there was none at
        // all, and BuildFlyout on a detached control cannot reach the keyed template, so a Topic
        // that changed off the tree left the previous topic's paragraph in place. Unreachable in
        // the shipped markup, where the one bound site is an ItemsControl that rebuilds its items,
        // and the test is on the control rather than on any view for that reason.
        var (window, button) = Show("target.frames");
        var first = Assert.IsType<Flyout>(button.Flyout);
        Assert.Same(HelpTopics.Get("target.frames"), first.Content);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        button.Topic = "target.trend";
        window.Content = button;
        Dispatcher.UIThread.RunJobs();

        var rebuilt = Assert.IsType<Flyout>(button.Flyout);
        Assert.Same(HelpTopics.Get("target.trend"), rebuilt.Content);
    }

    [AvaloniaFact]
    public void TheControl_ComposesWithTheChevronShape()
    {
        // Task 2A review P3: Button.help restated Button.chevron's four setters rather than
        // composing with the class. The control adds both classes itself, so no call site can
        // forget either and the two styles cannot drift.
        var (_, button) = Show();

        Assert.Contains(HelpButton.ShapeClass, button.Classes);
        Assert.Contains(HelpButton.GlyphClass, button.Classes);
        Assert.Equal(new Thickness(6, 0), button.Padding);
        Assert.Equal(new Thickness(0), button.BorderThickness);
    }
}
