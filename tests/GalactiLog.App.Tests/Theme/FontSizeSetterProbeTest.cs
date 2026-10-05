using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// R5's probe, kept as the regression pin for the four type tier classes.
//
// Scales.axaml's FontSize* keys are ratios, so the only sanctioned way to a spec 14.4 tier is
// MultiplyConverter over $parent[Window].FontSize (Views/Settings/DisplayTabView.axaml:194). The
// open question R5 raised was whether that binding resolves from inside a Style Setter, where the
// styled element rather than the setter is the binding source. It does, so the four tiers are
// four classes in Theme/Controls.axaml and no call site repeats the attribute.
//
// The probe runs against the shipped class rather than an inline style loaded at runtime:
// AvaloniaRuntimeXamlLoader lives in the Avalonia.Markup.Xaml.Loader package, which this solution
// does not reference, and the shipped form is the stronger probe anyway. It exercises compiled
// XAML, the application-level StyleInclude and the real converter instance.
public class FontSizeSetterProbeTest
{
    [AvaloniaFact]
    public void FontSizeSetter_InControlsAxaml_ResolvesAgainstTheOwningWindow()
    {
        var probe = new TextBlock { Name = "Probe", Text = "probe" };
        probe.Classes.Add("t-label");

        var window = new Window { FontSize = 20, Width = 400, Height = 200, Content = probe };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 20 * 0.786 = 15.72. A failure renders 20 (the setter did not apply) or 0.786 (the
        // ratio reached FontSize raw); both are distinguishable from the pass.
        Assert.Equal(15.72, probe.FontSize, 2);

        window.Close();
    }

    // The two above-root tiers take their ratio as a string literal, which MultiplyConverter
    // parses, because Scales.axaml holds no key above 1.0. This pins that the literal form
    // resolves too, so a later edit cannot quietly drop the hero tier back to the root size.
    [AvaloniaFact]
    public void TheTwoAboveRootTiers_ResolveTheirLiteralRatios()
    {
        var hero = new TextBlock { Text = "hero" };
        hero.Classes.Add("t-hero");
        var stat = new TextBlock { Text = "stat" };
        stat.Classes.Add("t-stat");

        var window = new Window
        {
            FontSize = 20,
            Width = 400,
            Height = 200,
            Content = new StackPanel { Children = { hero, stat } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(30d, hero.FontSize, 2);
        Assert.Equal(24d, stat.FontSize, 2);

        window.Close();
    }

    [AvaloniaFact]
    public void TheCaptionTier_ResolvesTheCaptionRatio()
    {
        var caption = new TextBlock { Text = "caption" };
        caption.Classes.Add("t-caption");

        var window = new Window { FontSize = 20, Width = 400, Height = 200, Content = caption };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 20 * 0.714 = 14.28.
        Assert.Equal(14.28, caption.FontSize, 2);

        window.Close();
    }
}
