using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Views.Merge;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.MergeDialogViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for spec 12.9's merge preview modal: it parses, lays out,
// and binds against a populated view-model. Compiled bindings already turn a binding-path typo
// into a build error; these catch the rest (a missing resource, a template that cannot realize,
// a section that never renders).
public class MergeDialogWindowTests
{
    private static DateOnly Date(int day) => new(2025, 1, day);

    private static MergeDialogWindow Show(object page)
    {
        var window = new MergeDialogWindow { DataContext = page };
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
    public void MergeDialogWindow_Constructs_AndLaysOut()
    {
        using var harness = Factory.Create().Settle();
        var window = Show(harness.ViewModel);

        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);
        Assert.Contains("Preview merge", VisibleTexts(window));
        Assert.NotNull(window.GetControl<TextBox>("SearchBox"));
        Assert.NotNull(window.GetControl<Button>("SwapButton"));
        Assert.NotNull(window.GetControl<Button>("CancelButton"));
        Assert.NotNull(window.GetControl<Button>("MergeButton"));

        window.Close();
    }

    [AvaloniaFact]
    public void MergeDialogWindow_RendersBothSides()
    {
        using var harness = Factory.Create().Settle();
        var window = Show(harness.ViewModel);

        var texts = VisibleTexts(window);
        Assert.Contains("Survives", texts);
        Assert.Contains("Merged away", texts);
        Assert.Contains("NGC 7331", texts);
        Assert.Contains("NGC7331", texts);
        Assert.Contains("Caldwell 30", texts);
        Assert.Contains("GiG,G", texts);
        Assert.Contains("Galaxy", texts);
        Assert.Contains("22:37:04.1", texts);
        Assert.Contains("+34:24:57", texts);
        Assert.Contains("148", texts);
        Assert.Contains("12.3 h", texts);
        Assert.Contains("2024-08-03", texts);
        Assert.Contains("2025-01-17", texts);
        Assert.Contains("Deer Lick", texts);
        Assert.Contains("PGC69327", texts);

        // The what-will-happen block.
        Assert.Contains("What will happen", texts);
        Assert.Contains("24", texts);
        Assert.Contains("Deer Lick, Deer Lick Group", texts);

        window.Close();
    }

    [AvaloniaFact]
    public void MergeDialogWindow_RendersTheCollidingDatesAndTheExplanation()
    {
        using var harness = Factory
            .Create(preview: Factory.Preview(collidingDates: [Date(3), Date(9)]))
            .Settle();
        var window = Show(harness.ViewModel);

        var texts = VisibleTexts(window);
        Assert.Contains("2025-01-03", texts);
        Assert.Contains("2025-01-09", texts);
        Assert.Contains(harness.ViewModel.NoteMergeExplanation, texts);

        window.Close();
    }

    [AvaloniaFact]
    public void MergeDialogWindow_NoCollidingDates_HidesThatSection()
    {
        using var harness = Factory.Create().Settle();
        var window = Show(harness.ViewModel);

        Assert.False(window.GetControl<ContentControl>("CollidingSessionsPanel").IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task MergeDialogWindow_SearchResultRow_BindsTheChooseCommandAcrossTemplateScope()
    {
        // Review finding 5. The results DataTemplate reaches the dialog's ChooseCommand through
        // #Root, a cross-scope compiled binding that only fails once a row is actually realised.
        using var harness = Factory.Create().Settle();
        harness.SearchResults = [Factory.TargetResult("M 31")];
        harness.ViewModel.SearchText = "M 31";
        await harness.Delay.DrainAsync(harness.ViewModel.PendingSearch);

        var window = Show(harness.ViewModel);
        Dispatcher.UIThread.RunJobs();

        var row = Assert.Single(window
            .GetControl<ItemsControl>("SearchResultList")
            .GetVisualDescendants()
            .OfType<Button>());

        Assert.NotNull(row.Command);
        Assert.Same(harness.ViewModel.ChooseCommand, row.Command);
        Assert.True(row.IsEffectivelyEnabled);
        Assert.True(row.Command!.CanExecute(row.CommandParameter));

        window.Close();
    }

    [AvaloniaFact]
    public void MergeDialogWindow_CloseRequested_ClosesWithTheResult()
    {
        using var harness = Factory.Create().Settle();
        var window = Show(harness.ViewModel);
        Assert.True(window.IsVisible);

        // The view-model asks; the window owns closing, the split
        // TargetDetailViewModel.BackRequested uses.
        harness.ViewModel.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
        Assert.Equal(new[] { false }, harness.Closes);
    }

    [AvaloniaFact]
    public void MergeDialogWindow_LongAliasList_WrapsInsideTheSurvivesCard()
    {
        // A five-alias survivor once ran across the Swap button into the
        // other card; every value now lives in a star column bounded by its card. A failure is an
        // alias block whose right edge passes the card's, or one that never wraps.
        var winner = Factory.Side(aliases:
            ["IC 1396A", "LBN 098.87+03.97", "LBN 452", "Elephant Trunk Nebula", "Sh 2-131"]);
        using var harness = Factory.Create(preview: Factory.Preview(winner: winner)).Settle();
        var window = Show(harness.ViewModel);

        var card = window.GetControl<Border>("WinnerColumn");
        var aliases = Assert.Single(
            card.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == harness.ViewModel.Winner!.AliasesText);

        var right = aliases.TranslatePoint(new Point(0, 0), card)!.Value.X + aliases.Bounds.Width;
        Assert.True(right <= card.Bounds.Width,
            $"alias text right edge {right:F1} passes the card's {card.Bounds.Width:F1}");
        Assert.True(aliases.TextLayout.TextLines.Count > 1, "alias text did not wrap");
        Assert.Equal(aliases.Text, ToolTip.GetTip(aliases));

        window.Close();
    }
}
