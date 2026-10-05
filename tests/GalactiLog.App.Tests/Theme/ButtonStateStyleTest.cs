using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// The rendered state of the one filled control (Button.primary, Run scan in the status bar).
//
// Theme/Controls.axaml declares the base Button:pressed and Button:disabled blocks above the
// unconditional Button.primary presenter setters. Both sides carry a selector activator, so the
// later declaration wins while both are active, which is why .primary needs its own state blocks
// and why they must be declared after its rest and hover blocks. That is an ordering argument, and
// an ordering argument is exactly the kind of thing that is wrong quietly, so these cases read the
// brushes off the rendered ContentPresenter instead of reasoning about frame priority.
//
// Every expectation is the token brush resolved from the merged dictionary at assert time rather
// than a hex literal, so the cases hold under whichever theme is applied and fail only when the
// style stops selecting the token it names.
public class ButtonStateStyleTest
{
    private static Color Token(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static (Window Window, Button Button, ContentPresenter Presenter) ShowPrimaryButton()
    {
        var button = new Button { Name = "Primary", Content = "Run scan" };
        button.Classes.Add("primary");

        var window = new Window { Width = 400, Height = 200, Content = button };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        button.Measure(new Size(400, 200));
        button.Arrange(new Rect(0, 0, 400, 200));
        Dispatcher.UIThread.RunJobs();

        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
        return (window, button, presenter);
    }

    // The comp draws the disabled control flat (comp-observing-ledger.html:258): no fill, the 1 px
    // emphasis outline, tertiary ink. The state is reachable in the shipped application, since
    // StatusBarViewModel.CanRunScan is false while a name resolution holds the lease and
    // StatusBarView hides the button only while a scan is running.
    [AvaloniaFact]
    public void ADisabledPrimaryButton_RendersTheFlatDisabledForm()
    {
        var (window, button, presenter) = ShowPrimaryButton();

        // The rest state first, so a pass on the disabled assertion cannot come from the fill
        // never having been there.
        Assert.Equal(Token("ColorAccent"), ColorOf(presenter.Background));

        button.IsEnabled = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, ColorOf(presenter.Background).A);
        Assert.Equal(Token("ColorBorderEmphasis"), ColorOf(presenter.BorderBrush));
        Assert.Equal(Token("ColorTextTertiary"), ColorOf(TextElement.GetForeground(presenter)));

        window.Close();
    }

    // Pressed is raised through the pseudo-class rather than through a synthesised pointer press:
    // the selector matches the pseudo-class, and setting it directly keeps the case free of hit
    // test coordinates.
    [AvaloniaFact]
    public void APressedPrimaryButton_TakesTheThirdRungOfTheAccentRamp()
    {
        var (window, button, presenter) = ShowPrimaryButton();
        var pseudo = (IPseudoClasses)button.Classes;

        pseudo.Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Token("ColorAccentHover"), ColorOf(presenter.Background));

        pseudo.Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Token("ColorAccentPressed"), ColorOf(presenter.Background));

        // The ink is the surface colour throughout: the pressed block changes the fill only.
        Assert.Equal(Token("ColorBgSurface"), ColorOf(TextElement.GetForeground(presenter)));

        window.Close();
    }

    // Coordinator item 2. A keyboard activation raises :pressed with no :pointerover, so while the
    // accent ramp had two rungs and the pressed fill was the rest token, a space bar press on the
    // application's one filled button changed nothing the user could see. The third rung is what
    // makes the keyboard path visible, and this is the case that pins it: the fill under :pressed
    // alone differs from the rest fill.
    [AvaloniaFact]
    public void AKeyboardPressOnThePrimaryButton_ChangesTheFillWithNoPointerOver()
    {
        var (window, button, presenter) = ShowPrimaryButton();
        var pseudo = (IPseudoClasses)button.Classes;

        var rest = ColorOf(presenter.Background);
        Assert.Equal(Token("ColorAccent"), rest);

        pseudo.Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(rest, ColorOf(presenter.Background));
        Assert.Equal(Token("ColorAccentPressed"), ColorOf(presenter.Background));

        window.Close();
    }
}
