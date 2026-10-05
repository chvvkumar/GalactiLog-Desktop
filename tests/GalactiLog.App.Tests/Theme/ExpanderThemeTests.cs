using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// Phase 24 R18: one Expander header for the whole application. Theme/Controls.axaml's pill
// ToggleButton block reaches the Expander's own header toggle, which is what drew a 1 px outline
// at rest and an elevated fill when expanded on every section (Diagnostics, the dashboard
// filters, the Display groups, the target page's sections). These cases read the rendered header
// rather than reasoning about which block wins.
public class ExpanderThemeTests
{
    private static (Window Window, Expander Expander, ToggleButton Header) ShowExpander()
    {
        var expander = new Expander
        {
            Header = new TextBlock { Text = "Section" },
            Content = new TextBlock { Text = "Body" },
        };
        var window = new Window { Width = 300, Height = 200, Content = expander };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var header = expander.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "ExpanderHeader");
        return (window, expander, header);
    }

    private static Border HeaderFill(ToggleButton header)
        => header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ToggleButtonBackground");

    private static ContentPresenter HeaderPresenter(ToggleButton header)
        => header.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");

    private static Color ColorOf(IBrush? brush)
        => brush is null ? Colors.Transparent : Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color Token(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    [AvaloniaFact]
    public void TheHeader_HasNoOutlineAndNoFill_AtRestAndWhenExpanded_AndKeepsItsChevron()
    {
        // A failure looks like the header drawn as a pill: a 1 px outline at rest, an elevated
        // fill once expanded, or a content border under the open section.
        var (window, expander, header) = ShowExpander();

        Assert.Equal(new Thickness(0), header.BorderThickness);
        Assert.Equal(0, ColorOf(HeaderFill(header).Background).A);
        Assert.Equal(0, ColorOf(HeaderPresenter(header).Background).A);

        expander.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Thickness(0), header.BorderThickness);
        Assert.Equal(0, ColorOf(HeaderFill(header).Background).A);
        Assert.Equal(0, ColorOf(HeaderPresenter(header).Background).A);
        Assert.Equal(0, ColorOf(expander.BorderBrush).A);
        Assert.Contains(
            header.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
            path => path.Name == "ExpandCollapseChevron" && path.IsEffectivelyVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void TheHeader_UnderThePointer_TakesTheHoverTintOnce()
    {
        // A failure looks like Fluent's own hover fill on the header, or the pill's tint drawn a
        // second time inside it.
        var (window, _, header) = ShowExpander();

        ((IPseudoClasses)header.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Token("ColorBgHover"), ColorOf(HeaderFill(header).Background));
        Assert.Equal(0, ColorOf(HeaderPresenter(header).Background).A);

        window.Close();
    }
}
