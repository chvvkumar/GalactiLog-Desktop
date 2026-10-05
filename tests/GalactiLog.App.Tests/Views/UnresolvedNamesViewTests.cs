using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke scope for spec 9.7 and 12.7's unresolved names section, which the
// Settings Targets tab and Phase 10's Diagnostics page both render. The view had no test of its
// own and took a markup change in Phase 14A (the help glyph beside its heading), which is
// fixer-list item 45.
public class UnresolvedNamesViewTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static (Window Window, UnresolvedNamesView View, UnresolvedNamesViewModel Page) Show(
        IReadOnlyList<UnresolvedNameRow> rows)
    {
        var page = new UnresolvedNamesViewModel(
            () => rows,
            (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
            post: action => action(),
            // Phase 14B Task 3's create target row action, bound so the button renders enabled the
            // way it does in production.
            createTarget: _ => { });

        page.PendingLoad?.Wait(Budget);

        var view = new UnresolvedNamesView { DataContext = page };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, page);
    }

    [AvaloniaFact]
    public void Constructs_AndLaysOutNonZero()
    {
        var (_, view, page) = Show([new("M 31", "obj:M 31", 12)]);
        using var scope = page;

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        // A StackPanel, not a Border: Phase 14B Task 3's vocabulary move replaced the section card
        // with a hairline rule under the heading (DESIGN.md section 8). The x:Name is unchanged.
        Assert.True(view.GetControl<StackPanel>("UnresolvedNamesSection").IsVisible);
    }

    [AvaloniaFact]
    public void ThePopulatedSection_RendersOneRowPerNameAndTheRetryAction()
    {
        var (_, view, page) = Show([new("M 31", "obj:M 31", 12), new("Bubble", "obj:Bubble", 3)]);
        using var scope = page;

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

        Assert.Equal(2, page.Names.Count);
        Assert.Contains("M 31", texts);
        Assert.Contains("Bubble", texts);
        Assert.True(view.GetControl<Button>("RetryUnresolvedButton").IsVisible);
        Assert.False(view.GetControl<TextBlock>("AllResolvedText").IsVisible);
        Assert.False(view.GetControl<TextBlock>("LoadFailedText").IsVisible);
    }

    [AvaloniaFact]
    public void AnEmptyList_RendersSpec1210sSentence()
    {
        var (_, view, page) = Show([]);
        using var scope = page;

        Assert.Empty(page.Names);
        Assert.True(view.GetControl<TextBlock>("AllResolvedText").IsVisible);
        Assert.Contains(
            "Every OBJECT name resolved.",
            view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
    }

    [AvaloniaFact]
    public void TheHeading_CarriesItsHelpGlyph()
    {
        // Spec 12.12's settings.targets.unresolved, which is the change this view took in Phase
        // 14A and the reason this file exists.
        var (_, view, page) = Show([new("M 31", "obj:M 31", 12)]);
        using var scope = page;

        var glyph = Assert.Single(view.GetVisualDescendants().OfType<HelpButton>());
        Assert.Equal("settings.targets.unresolved", glyph.Topic);
        Assert.True(glyph.Bounds.Width > 0);
    }

    // ---- Phase 14B Task 3 -------------------------------------------------------------------

    [AvaloniaFact]
    public void View_EachRowHasBothActions()
    {
        // Spec 12.7: create target (PAR-001) joins assign to target as a row action, and both are
        // enabled when their delegate is bound.
        var (_, view, page) = Show([new("Zzyzx Blob 42", "obj:Zzyzx Blob 42", 12)]);
        using var scope = page;

        var labels = view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button is not HelpButton)
            .Select(button => button.Content as string)
            .ToList();

        Assert.Contains("Create target", labels);
        Assert.Contains("Assign to target", labels);
        Assert.True(view.GetVisualDescendants().OfType<Button>()
            .Single(button => (button.Content as string) == "Create target").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void View_DeclaresNoCornerRadius()
    {
        // The one mechanical measure of the Observing Ledger move (roadmap section 0). This file
        // declared two before Phase 14B Task 3.
        var source = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Settings", "UnresolvedNamesView.axaml"));

        Assert.DoesNotContain("CornerRadius", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Style", source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void View_StillHasExactlyOneHelpGlyph()
    {
        // U1: no new help topic in this phase. If this moves, a glyph was placed and both
        // HelpPlacementCensusTest and HelpTopicsTests go red with it.
        var (_, view, page) = Show([new("M 31", "obj:M 31", 12), new("Bubble", "obj:Bubble", 3)]);
        using var scope = page;

        Assert.Single(view.GetVisualDescendants().OfType<HelpButton>());
    }
}
