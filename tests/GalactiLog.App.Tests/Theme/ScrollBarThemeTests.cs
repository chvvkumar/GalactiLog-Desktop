using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// Spec 14's thin scrollbar. Ruling E3 is reversed (phase-review P1 and P2, fixer-list item 1): the
// hand-written ScrollBar control theme is withdrawn and the platform Fluent theme is restyled
// through the resource keys it already reads, six brush aliases in each theme dictionary plus
// ScrollBarSize in Theme/Controls.axaml. This file is what the withdrawal is pinned by.
//
// The two findings that shipped green before it: the hand-written Track declared
// IsDirectionReversed="False" for both orientations, so every vertical bar rendered upside down,
// and the template replaced Fluent's collapsed and expanded states, so a full strength thumb was
// drawn permanently over the trailing content of every scroller. Nothing here read where the thumb
// was or whether it collapsed at rest, which is why three reviews and a theme census missed both.
// The geometry cases below are the ones that fail against that template.
public class ScrollBarThemeTests
{
    private static (Window Window, ScrollBar Bar) ShowScrollBar(Orientation orientation)
    {
        // Aligned so the bar is not stretched to fill the window on its own cross axis: a
        // vertical bar's width and a horizontal bar's height must come from ScrollBarSize, the key
        // the Fluent theme reads for both, not from whatever the host hands it, the same as every
        // real ScrollBar arranged inside a ScrollViewer's own template.
        var bar = new ScrollBar
        {
            Orientation = orientation,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            ViewportSize = 20,
            HorizontalAlignment = orientation == Orientation.Vertical ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
            VerticalAlignment = orientation == Orientation.Horizontal ? VerticalAlignment.Top : VerticalAlignment.Stretch,
        };

        var window = new Window { Width = 200, Height = 200, Content = bar };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, bar);
    }

    private static (Window Window, ScrollBar Bar) ShowVerticalScrollBar() => ShowScrollBar(Orientation.Vertical);

    private static Thumb ThumbOf(ScrollBar bar) => bar.GetVisualDescendants().OfType<Thumb>().Single();

    // The Fluent thumb theme's template is one Border bound to the thumb's own Background, and its
    // pointer-over and pressed states set that Border rather than the Thumb, so a state case reads
    // the Border and a rest case may read either.
    private static Border ThumbFillOf(ScrollBar bar)
        => ThumbOf(bar).GetVisualDescendants().OfType<Border>().Single();

    private static Track TrackOf(ScrollBar bar) => bar.GetVisualDescendants().OfType<Track>().Single();

    private static Avalonia.Controls.Shapes.Rectangle TrackRectOf(ScrollBar bar)
        => bar.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>()
            .Single(rectangle => rectangle.Name == "TrackRect");

    private static RepeatButton PageDownButtonOf(ScrollBar bar)
        => bar.GetVisualDescendants().OfType<RepeatButton>().Single(button => button.Name == "PART_PageDownButton");

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color TokenOf(string source, string key)
    {
        var dictionary = new ResourceInclude((Uri?)null) { Source = new Uri(source) };
        Assert.True(dictionary.TryGetResource(key, ThemeVariant.Dark, out var value), $"{source} is missing '{key}'");
        return ((ISolidColorBrush)value!).Color;
    }

    [AvaloniaFact]
    public void EveryThemeResolves_TheThreeScrollbarKeys()
    {
        string[] sources =
        [
            "avares://GalactiLog/Theme/Themes/CivilDusk.axaml",
            "avares://GalactiLog/Theme/Themes/Luminance.axaml",
            "avares://GalactiLog/Theme/Themes/DeepSky.axaml",
            "avares://GalactiLog/Theme/Themes/RedLight.axaml",
            "avares://GalactiLog/Theme/Themes/Atlas.axaml",
            "avares://GalactiLog/Theme/Themes/Logbook.axaml",
        ];
        string[] keys = ["ColorScrollbarThumb", "ColorScrollbarThumbHover", "ColorScrollbarTrack"];

        foreach (var source in sources)
        {
            var dictionary = new ResourceInclude((Uri?)null) { Source = new Uri(source) };
            foreach (var key in keys)
            {
                Assert.True(
                    dictionary.TryGetResource(key, ThemeVariant.Dark, out var value) && value is not null,
                    $"{source} is missing '{key}'");
            }
        }
    }

    // The six platform keys the restyle works through. Each dictionary has to alias all six, or a
    // theme swap would leave the bar on whatever Fluent's own default resolved to for that state.
    [AvaloniaTheory]
    [InlineData("avares://GalactiLog/Theme/Themes/CivilDusk.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Luminance.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/DeepSky.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/RedLight.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Atlas.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Logbook.axaml")]
    public void EveryTheme_AliasesTheFluentScrollBarKeys_ToItsOwnThreeTokens(string source)
    {
        (string Fluent, string Token)[] aliases =
        [
            ("ScrollBarPanningThumbBackground", "ColorScrollbarThumb"),
            ("ScrollBarThumbBackgroundColor", "ColorScrollbarThumb"),
            ("ScrollBarThumbFillPointerOver", "ColorScrollbarThumbHover"),
            ("ScrollBarThumbFillPressed", "ColorScrollbarThumbHover"),
            ("ScrollBarTrackFill", "ColorScrollbarTrack"),
            ("ScrollBarTrackFillPointerOver", "ColorScrollbarTrack"),
        ];

        foreach (var (fluent, token) in aliases)
        {
            Assert.Equal(TokenOf(source, token), TokenOf(source, fluent));
        }

        // Not vacuous: the thumb and the track tokens differ in every shipped dictionary, so an
        // alias pointing at the wrong one of the three fails above rather than passing by
        // coincidence.
        Assert.NotEqual(TokenOf(source, "ColorScrollbarThumb"), TokenOf(source, "ColorScrollbarThumbHover"));
        Assert.NotEqual(TokenOf(source, "ColorScrollbarThumb"), TokenOf(source, "ColorScrollbarTrack"));
    }

    [AvaloniaFact]
    public void TheScrollBar_IsEightPixelsWide()
    {
        // The rendered bar, not the setter: a theme that declared ScrollBarSize 8 and then set an
        // explicit Width elsewhere would still pass a resource-only assertion.
        var (window, bar) = ShowVerticalScrollBar();

        Assert.Equal(8d, bar.Bounds.Width, 1);

        window.Close();
    }

    [AvaloniaFact]
    public void TheScrollBar_IsEightPixelsTall_Horizontal()
    {
        var (window, bar) = ShowScrollBar(Orientation.Horizontal);

        Assert.Equal(8d, bar.Bounds.Height, 1);

        window.Close();
    }

    // ---- direction (phase-review P1) -----------------------------------------------------------

    // The P1 in two cases. Avalonia's Track, like WPF's, maps an increase in y to a decrease in
    // value, which is why the platform's vertical template sets IsDirectionReversed true; the
    // withdrawn control theme kept the default, so at offset zero the thumb sat at the bottom of
    // its track and dragging it down scrolled up. Both of these fail against that template.
    [AvaloniaFact]
    public void AVerticalBar_AtItsMinimum_PutsTheThumbAtTheTopOfTheTrack()
    {
        var (window, bar) = ShowVerticalScrollBar();
        var thumb = ThumbOf(bar);
        var track = TrackOf(bar);

        // The thumb is arranged by the Track, so its bounds are already in track coordinates.
        Assert.Equal(0d, thumb.Bounds.Y, 1);
        Assert.True(
            thumb.Bounds.Height < track.Bounds.Height,
            $"The thumb fills the whole track ({thumb.Bounds.Height} of {track.Bounds.Height}), so its position proves nothing.");

        window.Close();
    }

    [AvaloniaFact]
    public void AVerticalBar_AtItsMaximum_PutsTheThumbAtTheBottomOfTheTrack()
    {
        var (window, bar) = ShowVerticalScrollBar();
        bar.Value = bar.Maximum;
        Dispatcher.UIThread.RunJobs();

        var thumb = ThumbOf(bar);
        var track = TrackOf(bar);

        Assert.Equal(track.Bounds.Height, thumb.Bounds.Bottom, 1);
        Assert.True(thumb.Bounds.Y > 0d, "The thumb never left the top of the track.");

        window.Close();
    }

    [AvaloniaFact]
    public void AHorizontalBar_RunsFromTheLeadingEdgeToTheTrailingOne()
    {
        var (window, bar) = ShowScrollBar(Orientation.Horizontal);

        Assert.Equal(0d, ThumbOf(bar).Bounds.X, 1);

        bar.Value = bar.Maximum;
        Dispatcher.UIThread.RunJobs();

        var thumb = ThumbOf(bar);
        var track = TrackOf(bar);
        Assert.Equal(track.Bounds.Width, thumb.Bounds.Right, 1);
        Assert.True(thumb.Bounds.X > 0d, "The thumb never left the leading edge of the track.");

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(Orientation.Vertical)]
    [InlineData(Orientation.Horizontal)]
    public void DraggingTheThumbForward_IncreasesTheOffset(Orientation orientation)
    {
        // Track.ValueFromDistance is the mapping the Thumb's own drag handler uses, and it is where
        // the inverted axis lived: with IsDirectionReversed wrong, dragging down or right returned
        // a negative delta and the content scrolled the other way.
        var (window, bar) = ShowScrollBar(orientation);
        var track = TrackOf(bar);

        var delta = orientation == Orientation.Vertical
            ? track.ValueFromDistance(0d, 10d)
            : track.ValueFromDistance(10d, 0d);

        Assert.True(delta > 0d, $"Dragging forward changed the value by {delta}.");

        window.Close();
    }

    [AvaloniaFact]
    public void PagingDown_IsTheRegionBelowTheThumb_AndIncreasesTheOffset()
    {
        var (window, bar) = ShowVerticalScrollBar();
        var thumb = ThumbOf(bar);
        var pageDown = PageDownButtonOf(bar);

        // The geometry half is what fails against the withdrawn template: with the axis inverted,
        // Track arranged the increase region above the thumb, so a click below the thumb paged up.
        Assert.True(
            pageDown.Bounds.Y >= thumb.Bounds.Bottom - 0.5d,
            $"The page-down region starts at {pageDown.Bounds.Y}, above the thumb's bottom edge at {thumb.Bounds.Bottom}.");

        pageDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(bar.Value > 0d, "Paging down did not increase the offset.");

        window.Close();
    }

    // ---- the two states (phase-review P2) ------------------------------------------------------

    // The state the withdrawn template dropped. Fluent collapses the thumb to a hairline at rest
    // through a RenderTransform and takes the transform off while the bar is expanded, which is
    // what keeps a permanent 8 pixel thumb off the trailing content of every scroller. AllowAutoHide
    // is the seam that drives it without a pointer: ScrollBar.UpdateIsExpandedState expands and
    // stays expanded while it is false.
    [AvaloniaFact]
    public void TheThumb_IsCollapsedAtRest_AndFullWidthWhileTheBarIsExpanded()
    {
        var (window, bar) = ShowVerticalScrollBar();

        var thumb = ThumbOf(bar);
        Assert.False(bar.IsExpanded);
        Assert.NotNull(thumb.RenderTransform);
        Assert.NotEqual(Matrix.Identity, thumb.RenderTransform!.Value);

        // Fluent gives the thumb a 0.1 second TransformOperationsTransition, and the headless
        // harness has no clock to run it out: read live a tick after the toggle it settles about a
        // third of the way. Dropping the transitions makes the setter land at once, which is the
        // end state this case is about and not a behaviour it changes.
        thumb.Transitions = null;
        bar.AllowAutoHide = false;
        Dispatcher.UIThread.RunJobs();

        Assert.True(bar.IsExpanded);
        Assert.Equal(Matrix.Identity, thumb.RenderTransform!.Value);
        Assert.Equal(8d, thumb.Bounds.Width, 1);

        window.Close();
    }

    // Review P2, the case that would have caught Q11's own regression: the shipped bar is an
    // overlay, so a scroller's content is never measured narrower just because a vertical bar can
    // be drawn. Two scrollers at the same declared size, one with content that fits (no bar needed)
    // and one with content that overflows (a bar drawn), must give their content the same width.
    [AvaloniaFact]
    public void AScrollViewersContentWidth_IsTheSameWithAndWithoutAVisibleBar()
    {
        var shortScroller = new ScrollViewer { Width = 100, Height = 200, Content = new Border { Width = 80, Height = 50 } };
        var shortWindow = new Window { Width = 200, Height = 300, Content = shortScroller };
        shortWindow.Show();
        Dispatcher.UIThread.RunJobs();

        var tallScroller = new ScrollViewer { Width = 100, Height = 200, Content = new Border { Width = 80, Height = 500 } };
        var tallWindow = new Window { Width = 200, Height = 300, Content = tallScroller };
        tallWindow.Show();
        Dispatcher.UIThread.RunJobs();

        // Not vacuous: the tall scroller must actually need a vertical bar, or this proves
        // nothing about the overlay contract either way.
        Assert.True(
            tallScroller.Extent.Height > tallScroller.Viewport.Height,
            $"extent {tallScroller.Extent.Height} did not exceed viewport {tallScroller.Viewport.Height}; the tall scroller has nothing to scroll");
        Assert.Equal(shortScroller.Viewport.Width, tallScroller.Viewport.Width, 1);

        shortWindow.Close();
        tallWindow.Close();
    }

    // ---- the three tokens ----------------------------------------------------------------------

    [AvaloniaFact]
    public void TheThumb_TakesTheThumbToken_AtRest()
    {
        var (window, bar) = ShowVerticalScrollBar();

        Assert.True(Application.Current!.TryFindResource("ColorScrollbarThumb", out var expected));
        Assert.Equal(ColorOf((IBrush)expected!), ColorOf(ThumbFillOf(bar).Background));

        window.Close();
    }

    // Review P2: the case above only ever reads whichever theme the harness starts on. This
    // drives all six themes through ThemeManager.Apply and checks the live control against each
    // theme's own value read straight from its dictionary file, independent of Application.Current,
    // so a theme switch that silently failed to re-resolve DynamicResource would fail here. It is
    // also what the six aliases exist for: they live in the swapped dictionary precisely because a
    // StaticResource in Theme/Controls.axaml would freeze on the theme merged at parse time, and
    // this case is what that freeze fails.
    [AvaloniaTheory]
    [InlineData("civil-dusk", "avares://GalactiLog/Theme/Themes/CivilDusk.axaml")]
    [InlineData("luminance", "avares://GalactiLog/Theme/Themes/Luminance.axaml")]
    [InlineData("deep-sky", "avares://GalactiLog/Theme/Themes/DeepSky.axaml")]
    [InlineData("red-light", "avares://GalactiLog/Theme/Themes/RedLight.axaml")]
    [InlineData("atlas", "avares://GalactiLog/Theme/Themes/Atlas.axaml")]
    [InlineData("logbook", "avares://GalactiLog/Theme/Themes/Logbook.axaml")]
    public void TheThumb_ResolvesToThatThemesOwnToken(string themeId, string source)
    {
        try
        {
            ThemeManager.Apply(themeId);
            var (window, bar) = ShowVerticalScrollBar();

            var expected = TokenOf(source, "ColorScrollbarThumb");
            Assert.Equal(expected, ColorOf(ThumbFillOf(bar).Background));

            window.Close();
        }
        finally
        {
            // Application.Current is process-wide under the harness; restore the default so a
            // theme applied here does not leak into whatever runs next.
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    [AvaloniaFact]
    public void TheThumb_TakesTheHoverToken_UnderThePointer()
    {
        var (window, bar) = ShowVerticalScrollBar();
        var thumb = ThumbOf(bar);
        var pseudo = (IPseudoClasses)thumb.Classes;

        pseudo.Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Application.Current!.TryFindResource("ColorScrollbarThumbHover", out var expected));
        Assert.Equal(ColorOf((IBrush)expected!), ColorOf(ThumbFillOf(bar).Background));

        window.Close();
    }

    [AvaloniaFact]
    public void TheTrack_IsTransparent()
    {
        var (window, bar) = ShowVerticalScrollBar();

        Assert.True(Application.Current!.TryFindResource("ColorScrollbarTrack", out var expected));
        var track = ColorOf((IBrush)expected!);
        Assert.Equal(0, track.A);
        Assert.Equal(track, ColorOf(TrackRectOf(bar).Fill));

        window.Close();
    }

    // 14's own words: the web has no :active scrollbar rule, so the bar shows no third colour. The
    // platform theme does declare a pressed rule, and the withdrawal keeps the contract by aliasing
    // ScrollBarThumbFillPressed to the hover token rather than by having no rule at all, which is
    // the same ink a drag already shows: a Thumb keeps pointer capture through a drag, so a pressed
    // thumb is a hovered one. Re-pointed from the former "pressed reads the rest token", which
    // described a template that no longer ships (fixer-list item 1).
    [AvaloniaFact]
    public void TheScrollBar_ShowsNoThirdColour_WhilePressed()
    {
        var (window, bar) = ShowVerticalScrollBar();
        var thumb = ThumbOf(bar);
        var pseudo = (IPseudoClasses)thumb.Classes;

        Assert.True(Application.Current!.TryFindResource("ColorScrollbarThumbHover", out var hover));
        Assert.True(Application.Current!.TryFindResource("ColorScrollbarThumb", out var rest));
        pseudo.Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();

        var pressed = ColorOf(ThumbFillOf(bar).Background);
        Assert.Equal(ColorOf((IBrush)hover!), pressed);
        Assert.NotEqual(ColorOf((IBrush)rest!), pressed);

        window.Close();
    }

    // The fifth needle case (questions.md Q9), kept here as the standalone proof rather than only
    // as an entry in ControlStyleScanTest.ViewStyleNeedles: fails when any .axaml under src/
    // contains a ScrollBar style or control theme of its own. Theme/Controls.axaml is no longer
    // exempt from the walk, because since the withdrawal it declares neither: the whole restyle is
    // ScrollBarSize there and six aliases in the three theme dictionaries.
    [Fact]
    public void NoViewUnderSrc_DeclaresItsOwnScrollBarStyle()
    {
        var root = SourceScan.SrcRoot();
        string[] needles = ["<Style Selector=\"ScrollBar", "<ControlTheme TargetType=\"ScrollBar"];
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            scanned++;
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file);

            foreach (var needle in needles)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{relative}: {needle}");
                }
            }
        }

        Assert.True(scanned > 0, $"No .axaml files were scanned under {root}.");
        Assert.True(
            offenders.Count == 0,
            "The platform ScrollBar theme is restyled through its resource keys and replaced nowhere. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // The restyle's own declaration sites, so the two needles above are not guarding a vocabulary
    // that no longer ships: ScrollBarSize in the control file, the six aliases in every theme
    // dictionary.
    [Fact]
    public void TheRestyle_IsDeclaredWhereTheNeedlesSayItIs()
    {
        var root = SourceScan.SrcRoot();
        var controls = File.ReadAllText(Path.Combine(root, "GalactiLog.App", "Theme", "Controls.axaml"));

        Assert.Contains("<x:Double x:Key=\"ScrollBarSize\">8</x:Double>", controls, StringComparison.Ordinal);

        foreach (var theme in new[] { "CivilDusk.axaml", "Luminance.axaml", "DeepSky.axaml", "RedLight.axaml", "Atlas.axaml", "Logbook.axaml" })
        {
            var text = File.ReadAllText(Path.Combine(root, "GalactiLog.App", "Theme", "Themes", theme));
            foreach (var key in new[]
            {
                "ScrollBarPanningThumbBackground",
                "ScrollBarThumbBackgroundColor",
                "ScrollBarThumbFillPointerOver",
                "ScrollBarThumbFillPressed",
                "ScrollBarTrackFill",
                "ScrollBarTrackFillPointerOver",
            })
            {
                Assert.Contains($"<StaticResource x:Key=\"{key}\"", text, StringComparison.Ordinal);
            }
        }
    }
}
