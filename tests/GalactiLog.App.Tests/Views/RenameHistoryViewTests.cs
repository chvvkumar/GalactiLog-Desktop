using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke scope for spec 12.7's rename history. The view had no test of its
// own through six phases and took a markup change in Phase 14A (the help glyph beside its
// heading), which is fixer-list item 45: a view nothing renders is a view whose template failure
// reaches the user first.
//
// Compiled bindings already turn a binding-path typo into a build error; what these catch is the
// rest, a missing resource, a template that cannot realize, and an empty state that never renders.
public class RenameHistoryViewTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly Guid TargetId = Guid.Parse("20000000-0000-0000-0000-000000000000");

    private static RenameHistoryRow Row(string previous = "M 31", string current = "Andromeda")
        => new(new DateTime(2025, 3, 4, 21, 6, 7, DateTimeKind.Utc), TargetId, previous, current, current);

    private static (Window Window, RenameHistoryView View, RenameHistoryViewModel Page) Show(
        IReadOnlyList<RenameHistoryRow> rows)
    {
        var page = new RenameHistoryViewModel(
            () => rows,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: action => action());

        page.PendingLoad?.Wait(Budget);

        var view = new RenameHistoryView { DataContext = page };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, page);
    }

    [AvaloniaFact]
    public void Constructs_AndLaysOutNonZero()
    {
        var (_, view, page) = Show([Row()]);
        using var scope = page;

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        Assert.True(view.GetControl<Border>("RenameHistorySection").IsVisible);
    }

    [AvaloniaFact]
    public void APopulatedHistory_RendersOneRowPerRename_AndNoEmptyState()
    {
        var (_, view, page) = Show([Row(), Row("NGC 224", "Andromeda Galaxy")]);
        using var scope = page;

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

        Assert.Equal(2, page.Rows.Count);
        Assert.Contains("M 31", texts);
        Assert.Contains("NGC 224", texts);
        Assert.False(view.GetControl<TextBlock>("NoRenamesText").IsVisible);
        Assert.False(view.GetControl<TextBlock>("LoadFailedText").IsVisible);
    }

    [AvaloniaFact]
    public void AnEmptyHistory_RendersTheEmptyStateAndNoRows()
    {
        var (_, view, page) = Show([]);
        using var scope = page;

        Assert.Empty(page.Rows);
        Assert.True(view.GetControl<TextBlock>("NoRenamesText").IsVisible);
        Assert.Contains(
            "No renames recorded.",
            view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
    }

    [AvaloniaFact]
    public void TheHeading_CarriesItsHelpGlyph()
    {
        // Spec 12.12's settings.targets.rename-history, which is the change this view took in
        // Phase 14A and the reason this file exists.
        var (_, view, page) = Show([Row()]);
        using var scope = page;

        var glyph = Assert.Single(view.GetVisualDescendants().OfType<HelpButton>());
        Assert.Equal("settings.targets.rename-history", glyph.Topic);
        Assert.True(glyph.Bounds.Width > 0);
    }
}
