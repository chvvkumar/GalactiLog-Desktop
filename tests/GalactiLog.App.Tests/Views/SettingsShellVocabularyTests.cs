using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Views.Settings;
using Xunit;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Phase 14B Task 9: SettingsView.axaml's own cases, split from SettingsViewTests.cs because
// section 4 of the brief forbids appending there (Tasks 3, 4 and 5 edit three of its tabs in the
// same wave). Task 9's own survey found the shell already at grep -c CornerRadius 0, so these
// cases pin that outcome rather than a change: DESIGN.md sections 4, 5, 6 and 8, and spec 14.5's
// "ColorBgSurface is a top-level card, table, or panel" for the tab strip's own fill.
//
// CreateSettingsPage() is the shell-only constructor (SettingsViewModel(TargetsTabViewModel))
// every other suite that needs a Settings page but not its tabs already uses
// (ShellNavigationTests, MainWindowViewModelTests, TextSizeTests): it keeps the other tabs as
// placeholders, which is fine here because none of these cases select one.
public class SettingsShellVocabularyTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string ReadSource()
        => File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Settings", "SettingsView.axaml"));

    [AvaloniaFact]
    public void View_DeclaresNoCornerRadius()
    {
        var source = ReadSource();
        Assert.DoesNotContain("CornerRadius", source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void View_DeclaresNoButtonTagOrCalloutStyleOfItsOwn()
    {
        // The five local ListBoxItem styles (section 5.2 item 2) are correct and deliberate, and
        // ListBoxItem is not one of ControlStyleScanTest's four needled selectors. This pins the
        // four selectors that are.
        var source = ReadSource();
        foreach (var needle in new[]
                 {
                     "<Style Selector=\"Button",
                     "<Style Selector=\"ToggleButton",
                     "<Style Selector=\"Border.tag",
                     "<Style Selector=\"Border.callout",
                 })
        {
            Assert.DoesNotContain(needle, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [AvaloniaFact]
    public void View_TheTabStripColumn_KeepsItsPanelSurfaceAndItsVerticalRule()
    {
        using var page = TabFactory.CreateSettingsPage();
        var view = new SettingsView { DataContext = page };
        Show(view);

        var column = view.GetControl<Border>("SettingsTabStripColumn");
        Assert.Equal(180, column.Width);
        Assert.Equal(new Thickness(0, 0, 1, 0), column.BorderThickness);

        Assert.True(Avalonia.Application.Current!.TryFindResource("ColorBgSurface", out var resource));
        var surface = Assert.IsAssignableFrom<ISolidColorBrush>(resource);
        var actual = Assert.IsAssignableFrom<ISolidColorBrush>(column.Background);
        Assert.Equal(surface.Color, actual.Color);
    }

    [AvaloniaFact]
    public void View_StillCarriesThePageSettingsGlyph()
    {
        using var page = TabFactory.CreateSettingsPage();
        var view = new SettingsView { DataContext = page };
        Show(view);

        var glyph = Assert.Single(view.GetVisualDescendants().OfType<HelpButton>());
        Assert.Equal("page.settings", glyph.Topic);
    }

    [AvaloniaFact]
    public void View_TheTabRegion_IsStillReachableByName()
    {
        using var page = TabFactory.CreateSettingsPage();
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");
        Assert.Same(page.CurrentTab, region.Content);
    }

    [AvaloniaFact]
    public void View_EveryTextBlock_RendersAtAReadableSize()
    {
        using var page = TabFactory.CreateSettingsPage();
        var view = new SettingsView { DataContext = page };
        Show(view);

        var blocks = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }
}
