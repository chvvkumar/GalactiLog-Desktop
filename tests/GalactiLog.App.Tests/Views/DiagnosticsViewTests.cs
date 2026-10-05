using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Settings;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Spec 18.3's view smoke scope plus the roadmap's Phase 10 row 1 Verify clause for the App half:
// the page renders all seven groups with no null field where the spec names a value.
//
// Every case builds its page through Factory.CreateAsync, which awaits rather than blocks: an
// AvaloniaFact runs on the headless UI thread and that thread is a pool thread, so a blocking wait
// at construction can inline the refresh onto it (TRACKING section 2 item 8).
public class DiagnosticsViewTests
{
    private static (Window Window, DiagnosticsView View) Show(DiagnosticsViewModel page)
    {
        var view = new DiagnosticsView { DataContext = page };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    // The fields of a collapsed Expander are not in the visual tree, so every assertion about
    // values opens all seven first.
    private static void ExpandEveryGroup(DiagnosticsViewModel page)
    {
        foreach (var group in page.Groups)
        {
            group.IsExpanded = true;
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static List<string?> Texts(DiagnosticsView view)
        => [.. view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)];

    [AvaloniaFact]
    public async Task Constructs_AndLaysOutNonZero()
    {
        using var page = await Factory.CreateAsync();
        var (_, view) = Show(page);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public async Task View_RendersAllSevenGroups()
    {
        using var page = await Factory.CreateAsync();
        var (_, view) = Show(page);

        var texts = Texts(view);

        foreach (var title in DiagnosticsViewModel.GroupTitles)
        {
            Assert.Contains(title, texts);
        }

        Assert.Equal(7, view.GetVisualDescendants().OfType<Expander>().Count());
    }

    [AvaloniaFact]
    public async Task View_AGroupsHelpGlyph_DoesNotToggleTheGroup()
    {
        // Task 2B review P3, and the one "a button inside a button" shape in spec 12.12 with no
        // coverage. The bound glyph sits in Expander.Header, which Avalonia renders inside the
        // expander's own ToggleButton, so a click that reached the header would open or close the
        // group as well as the paragraph. Button.OnPointerPressed marks the press handled, which
        // is what stops it; this case is what says so.
        using var page = await Factory.CreateAsync();
        var (window, view) = Show(page);

        var expander = view.GetVisualDescendants().OfType<Expander>().First();
        var glyph = expander.GetVisualDescendants().OfType<HelpButton>().First();
        var before = expander.IsExpanded;

        var centre = glyph.TranslatePoint(
            new Point(glyph.Bounds.Width / 2d, glyph.Bounds.Height / 2d), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, expander.IsExpanded);
        Assert.True(glyph.Flyout?.IsOpen);
    }

    [AvaloniaFact]
    public async Task View_RendersNoEmptyValueCell()
    {
        using var page = await Factory.CreateAsync(() => Factory.Snapshot(
            scan: new ScanDiagnostics(
                true, "ingest", "Ingested 4/10 files", 40, true, Factory.Run(), [], null),
            unresolved: [new(@"M 31", "obj:M 31", 12)],
            recentErrors: [Factory.Warning("A watcher root went away")]));
        var (_, view) = Show(page);
        ExpandEveryGroup(page);

        // A walk, not seven hand-written cases, so a field added later is covered by construction.
        var cells = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();

        Assert.NotEmpty(cells);
        Assert.All(cells, cell => Assert.False(string.IsNullOrWhiteSpace(cell.Text)));

        // And the view-model side of the same rule, including the fields whose value is genuinely
        // absent and therefore renders the literal.
        Assert.All(
            page.Groups.SelectMany(group => group.Fields),
            field => Assert.False(string.IsNullOrWhiteSpace(field.Value)));
        Assert.Contains(
            page.Groups.SelectMany(group => group.Fields),
            field => field.Value == DiagnosticsViewModel.Unavailable);
    }

    [AvaloniaFact]
    public void View_DeclaresXDataType()
    {
        // Compiled bindings are on repository-wide (AvaloniaUseCompiledBindingsByDefault), and a
        // view without x:DataType silently falls back to reflection bindings.
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Views", "DiagnosticsView.axaml"));

        Assert.Contains(@"x:DataType=""diagnostics:DiagnosticsViewModel""", markup, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task View_HostsTheUnresolvedNamesRetryAction()
    {
        using var unresolved = Factory.Unresolved(() => [new(@"M 31", "obj:M 31", 12)]);
        using var page = await Factory.CreateAsync(unresolved: unresolved);
        var (_, view) = Show(page);
        ExpandEveryGroup(page);

        // The shared view-model, rendered through App.axaml's existing UnresolvedNamesViewModel
        // template, so spec 9.7's retry button here is the one Phase 7 built rather than a second
        // entry point into UnresolvedRetry.
        Assert.Contains(view.GetVisualDescendants(), visual => visual is UnresolvedNamesView);
        Assert.Contains(
            view.GetVisualDescendants().OfType<ContentControl>(),
            content => ReferenceEquals(content.Content, unresolved));
    }

    [AvaloniaFact]
    public async Task View_RendersOneRowPerWatcherRoot()
    {
        using var page = await Factory.CreateAsync(() => Factory.Snapshot(
            scan: new ScanDiagnostics(
                false, "", "Ready", 0, false, null,
                [new(@"D:\Astro", true, true), new(@"Z:\Archive", false, false)],
                null)));
        var (_, view) = Show(page);
        ExpandEveryGroup(page);

        var texts = view.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(cell => cell.Text)
            .ToList();

        Assert.Contains(@"D:\Astro", texts);
        Assert.Contains(@"Z:\Archive", texts);
        Assert.Contains("not watching, directory unreachable", Texts(view));
    }

    // Phase 14B Task 7's vocabulary move over this page. Section 6's cardinality assertions
    // restated here so they fail in the task-filtered run rather than only in the full suite.

    [AvaloniaFact]
    public async Task View_StillHasExactlySevenExpanders()
    {
        using var page = await Factory.CreateAsync();
        var (_, view) = Show(page);

        Assert.Equal(7, view.GetVisualDescendants().OfType<Expander>().Count());
    }

    [AvaloniaFact]
    public void View_DeclaresNoCornerRadius()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Views", "DiagnosticsView.axaml"));

        Assert.DoesNotContain("CornerRadius", markup, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task View_StillHasItsEightHelpGlyphs()
    {
        using var page = await Factory.CreateAsync();
        var (_, view) = Show(page);

        // page.diagnostics plus the seven group topics DiagnosticsGroupViewModel.TopicIdFor
        // derives from the group's own key.
        var topics = view.GetVisualDescendants().OfType<HelpButton>().Select(b => b.Topic).ToList();
        Assert.Equal(8, topics.Count);
        Assert.Contains("page.diagnostics", topics);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }
}
