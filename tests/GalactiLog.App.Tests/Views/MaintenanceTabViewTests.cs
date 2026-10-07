using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Maintenance;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for spec 12.7's Maintenance tab and its typed confirmation:
// they parse, lay out, and bind against a populated view-model. Compiled bindings already turn a
// binding-path typo into a build error; these catch the rest (a missing resource, a template that
// cannot realize, a card that never renders).
public class MaintenanceTabViewTests
{
    private static MaintenanceTabViewModel NewTab() => new(
        (_, _) => new TargetRebuild.RebuildOutcome(0, 0, 0, 0, 0, 0),
        (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
        (_, _) => new SmartRebuild.SmartRebuildOutcome(0, 0, 0, 0, 0, 0, 0),
        (_, _) => new CatalogIdentityBackfill.BackfillOutcome(0, 0, 0),
        (_, _, _) => Task.FromResult<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>(
            new ReferenceThumbnailPass.ReferenceThumbnailOutcome(0, 0, 0, false)),
        _ => 0,
        () => 90,
        _ => 0,
        post: action => action());

    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    private static IReadOnlyList<string> ButtonTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .Select(button => button.Content as string ?? "")];

    [AvaloniaFact]
    public void MaintenanceTabView_Constructs_AndRendersEverySpecAction()
    {
        using var page = NewTab();
        var view = new MaintenanceTabView { DataContext = page };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        var texts = VisibleTexts(view);
        foreach (var action in page.Actions)
        {
            Assert.Contains(action.Title, texts);
        }

        var buttons = ButtonTexts(view);
        Assert.Contains("Rebuild targets", buttons);
        Assert.Contains("Retry unresolved", buttons);
        Assert.Contains("Missing only", buttons);
        Assert.Contains("Regenerate all", buttons);
        Assert.Contains("Purge and regenerate", buttons);
        Assert.Contains("Prune now", buttons);
        Assert.Contains("Reset database", buttons);
    }

    [AvaloniaFact]
    public void MaintenanceTabView_RendersTheRiskChips()
    {
        using var page = NewTab();
        var view = new MaintenanceTabView { DataContext = page };
        Show(view);

        var texts = VisibleTexts(view);
        Assert.Contains("Safe", texts);
        Assert.Contains("Moderate", texts);
        Assert.Contains("Destructive", texts);
    }

    [AvaloniaFact]
    public void MaintenanceTabView_TheDestructiveChipAndButton_LookDifferentFromTheSafeOnes()
    {
        // Review minor 2: IsDestructive was never bound, so the two cards that delete something
        // rendered exactly like the four that do not.
        using var page = NewTab();
        var view = new MaintenanceTabView { DataContext = page };
        Show(view);

        var chips = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && block.Text is "Safe" or "Destructive")
            .ToList();

        // The chip's word already states its state, so it carries weight rather than red ink.
        var safe = chips.First(block => block.Text == "Safe");
        var destructive = chips.First(block => block.Text == "Destructive");
        Assert.NotEqual(safe.FontWeight, destructive.FontWeight);

        var buttons = view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .ToList();
        var plain = buttons.First(button => (button.Content as string) == "Prune now");
        var dangerous = buttons.First(button => (button.Content as string) == "Reset database");
        Assert.NotEqual(Colour(plain.Foreground), Colour(dangerous.Foreground));
    }

    [AvaloniaFact]
    public void MaintenanceTabView_AFailureLine_IsLabelledApartFromAnOutcomeLine()
    {
        // Review minor 2's other half: Failed was never bound either, so spec 12.10's failure line
        // rendered exactly like a success line. The mark is the shared inline error callout's
        // label, not red ink on the sentence.
        using var page = NewTab();
        var view = new MaintenanceTabView { DataContext = page };
        Show(view);

        page.Action(MaintenanceTabViewModel.PruneActivityAction).Finish("Pruned 3 activity entries.");
        page.Action(MaintenanceTabViewModel.FrameThumbnailsAction)
            .Finish(MaintenanceTabViewModel.FailureMessage, failed: true);
        Dispatcher.UIThread.RunJobs();

        var outcome = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(block => block.IsEffectivelyVisible && block.Text == "Pruned 3 activity entries.");
        var failure = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(block => block.IsEffectivelyVisible && block.Text == MaintenanceTabViewModel.FailureMessage);

        var callout = Assert.Single(
            failure.GetVisualAncestors().OfType<ContentControl>(),
            control => control.Classes.Contains("callout"));
        Assert.Contains("error", callout.Classes);
        Assert.Contains("inline", callout.Classes);
        Assert.DoesNotContain(
            outcome.GetVisualAncestors().OfType<ContentControl>(),
            control => control.Classes.Contains("callout"));
    }

    private static Color? Colour(IBrush? brush)
        => brush is ISolidColorBrush solid ? solid.Color : null;

    [AvaloniaFact]
    public void MaintenanceTabView_TheFrameThumbnailCard_SaysWhyThereIsNoMissingOnlyOption()
    {
        using var page = NewTab();
        var view = new MaintenanceTabView { DataContext = page };
        Show(view);

        var card = page.Action(MaintenanceTabViewModel.FrameThumbnailsAction);
        Assert.Contains(card.Description, VisibleTexts(view));
    }

    [AvaloniaFact]
    public void MaintenanceTabView_TheInlineConfirm_AppearsOnlyWhenArmed()
    {
        using var page = NewTab();
        var view = new MaintenanceTabView { DataContext = page };
        Show(view);

        const string confirmText =
            "This rewrites every frame assignment. Press the button above again to confirm.";
        Assert.DoesNotContain(confirmText, VisibleTexts(view));

        page.Action(MaintenanceTabViewModel.RebuildTargetsAction).ConfirmPending = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(confirmText, VisibleTexts(view));
        Assert.Contains("Cancel", ButtonTexts(view));
    }

    [AvaloniaFact]
    public void ResetConfirmWindow_Constructs_AndItemizesWhatIsClearedAndKept()
    {
        var page = new ResetConfirmViewModel(
            _ => new DatabaseReset.ResetOutcome(9, 0, true),
            post: action => action());
        // The dialog is a Window in its own right, so it is shown directly rather than hosted.
        var dialog = new ResetConfirmWindow { DataContext = page, Width = 700, Height = 900 };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = VisibleTexts(dialog);
        foreach (var line in page.ClearedItems)
        {
            Assert.Contains(line, texts);
        }

        foreach (var line in page.KeptItems)
        {
            Assert.Contains(line, texts);
        }

        Assert.Contains(page.FilesUntouchedText, texts);
        Assert.Contains(page.PromptText, texts);
        Assert.Contains("Reset database", ButtonTexts(dialog));
        Assert.Contains("Cancel", ButtonTexts(dialog));

        dialog.Close();
    }

    [AvaloniaFact]
    public void ResetConfirmWindow_TypingThePhrase_EnablesTheResetButton()
    {
        var page = new ResetConfirmViewModel(
            _ => new DatabaseReset.ResetOutcome(9, 0, true),
            post: action => action());
        var dialog = new ResetConfirmWindow { DataContext = page, Width = 700, Height = 900 };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        var box = dialog.GetControl<TextBox>("PhraseBox");
        var reset = dialog.GetControl<Button>("ResetButton");
        Assert.False(reset.IsEffectivelyEnabled);

        box.Text = ResetConfirmViewModel.RequiredPhrase;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ResetConfirmViewModel.RequiredPhrase, page.Typed);
        Assert.True(reset.IsEffectivelyEnabled);

        dialog.Close();
    }
}
