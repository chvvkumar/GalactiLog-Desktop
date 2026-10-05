using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.App.Views.Merge;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for spec 12.9's merge history: it parses, lays out, and
// binds against a populated view-model. Compiled bindings already turn a binding-path typo into a
// build error; these catch the rest (a missing resource, a template that cannot realize, an empty
// state that never renders, a per-row command that cannot reach the list's view-model).
public class MergeHistoryViewTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly Guid WinnerId = Guid.Parse("20000000-0000-0000-0000-000000000000");

    private static readonly Guid LoserId = Guid.Parse("30000000-0000-0000-0000-000000000000");

    private static MergeHistoryRow Row() => new(
        Guid.NewGuid(),
        WinnerId,
        "M 31",
        LoserId,
        "NGC 224",
        new DateTime(2025, 3, 4, 21, 6, 7, DateTimeKind.Utc),
        148);

    private static MergeHistoryViewModel Create(params MergeHistoryRow[] rows)
    {
        var history = new MergeHistoryViewModel(
            () => rows,
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 148, 0, []),
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 148, 0, []),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: action => action());

        // A helper, not a test method: nothing here asserts what thread the load ran on.
        history.PendingLoad?.Wait(Budget);
        return history;
    }

    private static Window Show(Control view)
    {
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    [AvaloniaFact]
    public void MergeHistoryView_Constructs_AndLaysOut()
    {
        using var history = Create(Row());
        var view = new MergeHistoryView { DataContext = history };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        Assert.NotNull(view.GetControl<Border>("MergeHistorySection"));
    }

    [AvaloniaFact]
    public void MergeHistoryView_RendersARowWithItsUndoButton()
    {
        using var history = Create(Row());
        var view = new MergeHistoryView { DataContext = history };
        Show(view);

        var texts = VisibleTexts(view);
        Assert.Contains("NGC 224", texts);
        Assert.Contains("M 31", texts);
        Assert.Contains("2025-03-04 21:06", texts);
        Assert.Contains("148 frames", texts);
        Assert.DoesNotContain("No merges recorded.", texts);

        // The per-row Undo reaches the list's own command, with the row as its parameter.
        var undo = view.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => Equals(button.Content, "Undo"));
        Assert.Same(history.UndoCommand, undo.Command);
        Assert.Same(history.Rows[0], undo.CommandParameter);
    }

    [AvaloniaFact]
    public void MergeHistoryView_EmptyHistory_RendersTheEmptyState()
    {
        using var history = Create();
        var view = new MergeHistoryView { DataContext = history };
        Show(view);

        // Ruling Q19, verbatim including the full stop, on both surfaces.
        Assert.Contains("No merges recorded.", VisibleTexts(view));
        // Phase 14A Task 2: the section heading now carries spec 12.12's help glyph, which is a
        // Button. The rule this case exists for is that an empty history renders no undo button,
        // so the glyph is excluded by type rather than the assertion being weakened to a count.
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b is not HelpButton);
    }
}
