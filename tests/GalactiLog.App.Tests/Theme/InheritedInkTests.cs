using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Theme;
using GalactiLog.App.Views;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// The application's default text ink. Until this landed, nothing declared one: the single Window
// style set FontFamily and no rule gave a bare TextBlock a Foreground, so every sentence and every
// figure without a class or an attribute of its own inherited FluentTheme's Dark default, pure
// white, which no GalactiLog dictionary declares. On red-light, the night-vision theme, the twelve
// Analysis stats card figures and both combo boxes were the brightest thing on the screen.
//
// The ink is declared once, where inheritance starts, and never per view: a setter each view has to
// remember is the shape that was forgotten 99 times.
public class InheritedInkTests
{
    private const string Dusk = "avares://GalactiLog/Theme/Themes/CivilDusk.axaml";
    private const string Ledger = "avares://GalactiLog/Theme/Themes/Luminance.axaml";
    private const string Glass = "avares://GalactiLog/Theme/Themes/DeepSky.axaml";
    private const string Red = "avares://GalactiLog/Theme/Themes/RedLight.axaml";
    private const string Atlas = "avares://GalactiLog/Theme/Themes/Atlas.axaml";
    private const string Logbook = "avares://GalactiLog/Theme/Themes/Logbook.axaml";

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color TokenOf(string source, string key)
    {
        var dictionary = new ResourceInclude((Uri?)null) { Source = new Uri(source) };
        Assert.True(dictionary.TryGetResource(key, ThemeVariant.Dark, out var value), $"{source} is missing '{key}'");
        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>
    /// The probes, inside a real <see cref="MainWindow"/>. Every window the application ships is a
    /// subclass of <c>Window</c>, and an Avalonia type selector matches on the control's StyleKey,
    /// so a case that showed a bare <c>new Window()</c> would pass even if the one style reached
    /// nothing that ships.
    /// </summary>
    private static (Window Window, TextBlock Bare, TextBlock Num, TextBlock Caption, TextBlock Filled) ShowProbes()
    {
        var bare = new TextBlock { Text = "a sentence with no class" };
        var num = new TextBlock { Text = "1.90 px", Classes = { "num" } };
        var caption = new TextBlock { Text = "a caption", Classes = { "t-caption" } };
        var filled = new TextBlock { Text = "Run scan" };
        var button = new Button { Classes = { "primary" }, Content = filled };

        var window = new MainWindow
        {
            Width = 400,
            Height = 300,
            Content = new StackPanel { Children = { bare, num, caption, button } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, bare, num, caption, filled);
    }

    // ---- the inherited ink -----------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("civil-dusk", Dusk)]
    [InlineData("luminance", Ledger)]
    [InlineData("red-light", Red)]
    [InlineData("atlas", Atlas)]
    public void ABareTextBlockAndANumFigure_TakeThatThemesPrimaryInk(string themeId, string source)
    {
        try
        {
            ThemeManager.Apply(themeId);
            var (window, bare, num, _, _) = ShowProbes();

            var expected = TokenOf(source, "ColorTextPrimary");
            Assert.Equal(expected, ColorOf(bare.Foreground));
            Assert.Equal(expected, ColorOf(num.Foreground));

            // The value this replaces, and the reason the assertion above is not a restatement of
            // the token: no shipped dictionary declares a pure white primary ink.
            Assert.NotEqual(Colors.White, ColorOf(bare.Foreground));

            window.Close();
        }
        finally
        {
            // Application.Current is process-wide under the harness; put the default back.
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // A DynamicResource and not a frozen brush: the ink has to follow the Settings picker, which is
    // what a StaticResource in this one place would silently fail to do.
    [AvaloniaFact]
    public void TheInheritedInk_FollowsAThemeSwap()
    {
        try
        {
            ThemeManager.Apply("luminance");
            var (window, bare, num, _, _) = ShowProbes();

            Assert.Equal(TokenOf(Ledger, "ColorTextPrimary"), ColorOf(bare.Foreground));

            ThemeManager.Apply("red-light");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(TokenOf(Red, "ColorTextPrimary"), ColorOf(bare.Foreground));
            Assert.Equal(TokenOf(Red, "ColorTextPrimary"), ColorOf(num.Foreground));
            Assert.NotEqual(TokenOf(Ledger, "ColorTextPrimary"), TokenOf(Red, "ColorTextPrimary"));

            window.Close();
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // The reason the declaration is on the Window and never a Selector="TextBlock" rule: a rule on
    // the type would outrank the ink a button, a badge or a callout gives its own content, and the
    // one filled control in the application would draw its label in the page ink over an accent
    // fill. Anything that sets its own Foreground keeps it.
    [AvaloniaFact]
    public void AFilledButtonsLabel_AndAClassedCaption_KeepTheirOwnInk()
    {
        var (window, bare, _, caption, filled) = ShowProbes();

        Assert.True(Application.Current!.TryFindResource("ColorBgSurface", out var surface));
        Assert.True(Application.Current!.TryFindResource("ColorTextTertiary", out var tertiary));
        Assert.True(Application.Current!.TryFindResource("ColorTextPrimary", out var primary));

        // Button.primary sets TextElement.Foreground on its ContentPresenter, so its label reads
        // the surface colour over the accent fill, not the inherited page ink.
        Assert.Equal(ColorOf((IBrush)surface!), ColorOf(filled.Foreground));
        Assert.Equal(ColorOf((IBrush)tertiary!), ColorOf(caption.Foreground));

        // Not vacuous: the two inks kept here differ from the one now inherited.
        Assert.Equal(ColorOf((IBrush)primary!), ColorOf(bare.Foreground));
        Assert.NotEqual(ColorOf((IBrush)primary!), ColorOf(filled.Foreground));
        Assert.NotEqual(ColorOf((IBrush)primary!), ColorOf(caption.Foreground));

        window.Close();
    }

    // The choke point itself. The style is declared once, on Window, and every shipped window is a
    // subclass; this is what fails if the declaration moves to a per-view setter or if a selector
    // stops reaching the subclasses. The interface face rides on the same style and is asserted
    // beside the ink for the same reason.
    [AvaloniaFact]
    public void TheOneWindowStyle_ReachesAShippedWindowSubclass()
    {
        var (window, _, _, _, _) = ShowProbes();

        Assert.True(Application.Current!.TryFindResource("ColorTextPrimary", out var primary));
        Assert.Equal(ColorOf((IBrush)primary!), ColorOf(window.Foreground));
        Assert.Equal("Atkinson Hyperlegible Next", window.FontFamily.Name);

        window.Close();
    }

    // ---- the two platform controls ---------------------------------------------------------------

    // The ComboBox text and the DatePicker placeholders are not inheritance: both control themes
    // set a Foreground of their own from a Fluent resource key, which outranks what the window
    // hands down. They are restyled through the keys the platform already reads, which is the
    // mechanism the ScrollBar was restyled with in Phase 14C and nothing new.
    [AvaloniaTheory]
    [InlineData("civil-dusk", Dusk)]
    [InlineData("luminance", Ledger)]
    [InlineData("red-light", Red)]
    [InlineData("atlas", Atlas)]
    public void AComboBoxAndADatePicker_TakeThatThemesOwnInks(string themeId, string source)
    {
        try
        {
            ThemeManager.Apply(themeId);

            var combo = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 };
            var picker = new DatePicker();
            var window = new MainWindow
            {
                Width = 400,
                Height = 300,
                Content = new StackPanel { Children = { combo, picker } },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(TokenOf(source, "ColorTextPrimary"), ColorOf(combo.Foreground));
            Assert.Equal(TokenOf(source, "ColorTextPrimary"), ColorOf(picker.Foreground));

            // The three placeholder blocks the look measured as neutral grey, (158,154,154), a
            // colour no dictionary declares. With no date selected the DatePicker is :hasnodate and
            // they take the platform's placeholder key.
            var placeholders = picker.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(block => block.Name is not null && block.Name.StartsWith("PART_", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(placeholders);
            foreach (var block in placeholders)
            {
                Assert.Equal(TokenOf(source, "ColorTextTertiary"), ColorOf(block.Foreground));
            }

            window.Close();
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // The OPEN drop-down's rows are a third ink and are not covered here. A ComboBoxItem carries
    // its own Foreground from ComboBoxItemForeground and seven state siblings, each paired with a
    // background this pass does not touch, so it is more than the ruled restyle and is carried with
    // its key names in the report rather than started. What the closed control shows, which is what
    // the launched look measured, is the case above.

    // Every dictionary aliases the same platform keys to its own tokens, for the same reason the
    // six ScrollBar aliases live there: a StaticResource in Theme/Controls.axaml would freeze on
    // whichever theme was merged when the styles were parsed, and a theme that forgot one key would
    // leave that control on the platform's default ink after a swap.
    [AvaloniaTheory]
    [InlineData(Dusk)]
    [InlineData(Ledger)]
    [InlineData(Glass)]
    [InlineData(Red)]
    [InlineData(Atlas)]
    [InlineData(Logbook)]
    public void EveryTheme_AliasesTheFluentComboBoxAndDatePickerKeys_ToItsOwnTwoTokens(string source)
    {
        (string Fluent, string Token)[] aliases =
        [
            ("ComboBoxForeground", "ColorTextPrimary"),
            ("ComboBoxForegroundFocused", "ColorTextPrimary"),
            ("ComboBoxForegroundFocusedPressed", "ColorTextPrimary"),
            ("ComboBoxForegroundDisabled", "ColorTextTertiary"),
            ("ComboBoxPlaceHolderForeground", "ColorTextTertiary"),
            ("ComboBoxPlaceHolderForegroundFocusedPressed", "ColorTextTertiary"),
            ("DatePickerButtonForeground", "ColorTextPrimary"),
            ("DatePickerButtonForegroundPressed", "ColorTextPrimary"),
            ("DatePickerButtonForegroundDisabled", "ColorTextTertiary"),
            ("TextControlPlaceholderForeground", "ColorTextTertiary"),
        ];

        foreach (var (fluent, token) in aliases)
        {
            Assert.Equal(TokenOf(source, token), TokenOf(source, fluent));
        }

        // Not vacuous: the two tokens differ in every shipped dictionary, so an alias pointing at
        // the wrong one fails above rather than passing by coincidence.
        Assert.NotEqual(TokenOf(source, "ColorTextPrimary"), TokenOf(source, "ColorTextTertiary"));
    }
}
