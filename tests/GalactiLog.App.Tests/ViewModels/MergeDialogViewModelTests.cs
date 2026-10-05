using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.Data.Repositories;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.MergeDialogViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 7 Task 4. Spec 12.9's merge preview dialog, plus the roadmap Verify line: the dialog
// lists exactly the colliding dates for a seeded pair, and cancel writes nothing.
public class MergeDialogViewModelTests
{
    private static DateOnly Date(int day) => new(2025, 1, day);

    // ---- the preview load ---------------------------------------------------------------

    // [AvaloniaFact] and async: the load is awaited rather than blocked on (TRACKING section 2
    // item 8), because the headless UI thread is itself a thread-pool thread.
    [AvaloniaFact]
    public async Task Construction_LoadsThePreviewOffTheUiThread()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        using var harness = Factory.Create(onUiThread: () => Dispatcher.UIThread.CheckAccess());
        await harness.ViewModel.PendingPreview!;

        Assert.NotEmpty(harness.PreviewOnUiThread);
        Assert.All(harness.PreviewOnUiThread, Assert.False);
        Assert.Equal(
            (Factory.WinnerId, (Guid?)Factory.LoserId, (string?)null),
            Assert.Single(harness.Previews));
    }

    [AvaloniaFact]
    public async Task Preview_PublishesOnTheUiThread()
    {
        var publishedOnUiThread = new List<bool>();
        using var harness = Factory.Create(post: action => Dispatcher.UIThread.Post(action));
        harness.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MergeDialogViewModel.Winner))
            {
                publishedOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
            }
        };

        await harness.ViewModel.PendingPreview!;
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(publishedOnUiThread);
        Assert.All(publishedOnUiThread, Assert.True);
        Assert.NotNull(harness.ViewModel.Winner);
    }

    [Fact]
    public void Sides_RenderEveryFieldSpec129Lists()
    {
        using var harness = Factory.Create().Settle();
        var winner = harness.ViewModel.Winner;
        var loser = harness.ViewModel.Loser;

        Assert.NotNull(winner);
        Assert.Equal("NGC 7331", winner.Name);
        Assert.Equal("NGC7331", winner.CatalogId);
        Assert.Equal("Caldwell 30", winner.AliasesText);
        Assert.Equal("GiG,G", winner.ObjectType);
        Assert.Equal("Galaxy", winner.ObjectCategory);

        // The sexagesimal form TargetHeaderViewModel already produces, not a second formatter.
        Assert.Equal("22:37:04.1", winner.RaText);
        Assert.Equal("+34:24:57", winner.DecText);
        Assert.Equal("148", winner.FrameCountText);
        Assert.Equal("12.3 h", winner.IntegrationText);
        Assert.Equal("9", winner.SessionCountText);
        Assert.Equal("2024-08-03", winner.FirstSessionText);
        Assert.Equal("2025-01-17", winner.LastSessionText);

        Assert.NotNull(loser);
        Assert.Equal("Deer Lick", loser.Name);
        Assert.Equal("PGC69327", loser.CatalogId);
        Assert.Equal("Deer Lick Group", loser.AliasesText);
        Assert.Equal("24", loser.FrameCountText);

        Assert.Equal(24, harness.ViewModel.FramesToMove);
        Assert.Equal("Deer Lick, Deer Lick Group", harness.ViewModel.AliasesToAddText);
    }

    [Fact]
    public void Preview_ThatFindsNothing_ShowsAnErrorAndDisablesConfirm()
    {
        using var release = new ManualResetEventSlim(false);
        using var harness = Factory.Create(previewRelease: release);
        harness.PreviewResult = null;
        release.Set();
        harness.Settle();

        Assert.Null(harness.ViewModel.Winner);
        Assert.NotNull(harness.ViewModel.ErrorText);
        Assert.False(harness.ViewModel.ConfirmCommand.CanExecute(null));
    }

    // ---- the colliding dates (roadmap Verify) -------------------------------------------

    [Fact]
    public void CollidingDates_AreListed()
    {
        using var harness = Factory
            .Create(preview: Factory.Preview(collidingDates: [Date(3), Date(9), Date(21)]))
            .Settle();

        Assert.True(harness.ViewModel.HasCollidingSessions);
        Assert.Equal(
            new[] { "2025-01-03", "2025-01-09", "2025-01-21" },
            harness.ViewModel.CollidingSessionDates);
    }

    [Fact]
    public void CollidingDates_OverARealDatabase_AreExactlyTheSeededPairDates()
    {
        // The roadmap Verify line's first half, end to end through MergePreviewQuery rather than
        // a lambda: exactly the one date the seeded pair both have a note for.
        var (harness, database) = Factory.CreateOverDatabase();
        using var owned = harness;

        Assert.Equal(new[] { "2025-01-03" }, harness.ViewModel.CollidingSessionDates);
        Assert.Equal(2, harness.ViewModel.FramesToMove);
        Assert.Equal(2, database.FramesOnLoser);
    }

    [Fact]
    public void CollidingDates_CarryTheNoteMergeExplanation()
    {
        using var harness = Factory
            .Create(preview: Factory.Preview(collidingDates: [Date(3)]))
            .Settle();

        // Spec 9.7's rule, stated once, with the marker shown literally and the loser's own name
        // inside it.
        Assert.Equal(
            "On these dates the winner already has a note. The loser's text is appended after a "
            + "'--- merged from Deer Lick ---' marker, and the loser's own note is kept so it "
            + "reappears if you undo the merge.",
            harness.ViewModel.NoteMergeExplanation);
    }

    [Fact]
    public void NoCollidingDates_HidesTheSection()
    {
        using var harness = Factory.Create().Settle();

        Assert.False(harness.ViewModel.HasCollidingSessions);
        Assert.Empty(harness.ViewModel.CollidingSessionDates);
        Assert.Equal("", harness.ViewModel.NoteMergeExplanation);
    }

    // ---- the search box ------------------------------------------------------------------

    [Fact]
    public async Task Search_DebouncesBeforeQuerying()
    {
        using var harness = Factory.Create().Settle();
        harness.SearchResults = [Factory.TargetResult()];

        harness.ViewModel.SearchText = "M 3";
        harness.ViewModel.SearchText = "M 31";

        // Nothing has queried yet: both keystrokes are parked in the debounce window.
        Assert.Empty(harness.Searches);

        await harness.Delay.DrainAsync(harness.ViewModel.PendingSearch);

        Assert.Equal(new[] { "M 31" }, harness.Searches);
        Assert.Equal(DashboardViewModel.DebounceWindow, harness.Delay.Requested[^1]);
    }

    [AvaloniaFact]
    public async Task Search_RunsOffTheUiThread()
    {
        using var harness = Factory.Create(onUiThread: () => Dispatcher.UIThread.CheckAccess());
        await harness.ViewModel.PendingPreview!;
        harness.SearchResults = [Factory.TargetResult()];

        harness.ViewModel.SearchText = "M 31";
        await harness.Delay.DrainAsync(harness.ViewModel.PendingSearch);

        Assert.NotEmpty(harness.SearchOnUiThread);
        Assert.All(harness.SearchOnUiThread, Assert.False);
    }

    [Fact]
    public async Task Search_UsesTheSameQueryAsTheDashboard()
    {
        // The delegate is TargetSearchQuery.Search (asserted by the AppHost wiring test) and the
        // rows are the dashboard's own SearchResultViewModel: one ranking at one threshold.
        using var harness = Factory.Create().Settle();
        harness.SearchResults = [Factory.TargetResult("M 31"), Factory.UnresolvedResult()];

        harness.ViewModel.SearchText = "m3";
        await harness.Delay.DrainAsync(harness.ViewModel.PendingSearch);

        Assert.Equal(2, harness.ViewModel.SearchResults.Count);
        Assert.IsType<SearchResultViewModel>(harness.ViewModel.SearchResults[0]);

        // The order is the query's, unchanged.
        Assert.Equal(
            new[] { "M 31", "ngc7331 mosaic" },
            harness.ViewModel.SearchResults.Select(result => result.DisplayName));
        Assert.Equal("Unresolved", harness.ViewModel.SearchResults[1].CategoryText);
    }

    // ---- choosing a side ------------------------------------------------------------------

    [Fact]
    public async Task Choose_ReplacesTheSideTheToggleNames()
    {
        using var harness = Factory.Create().Settle();
        var newLoser = Factory.TargetResult("M 31");
        var newWinner = Factory.TargetResult("M 42", Guid.Parse("70000000-0000-0000-0000-000000000000"));

        // The toggle starts on the loser: a candidate arrives with its winner already suggested.
        Assert.False(harness.ViewModel.SearchChoosesWinner);
        await harness.ViewModel.ChooseCommand.ExecuteAsync(new SearchResultViewModel(newLoser));

        Assert.Equal(
            (Factory.WinnerId, (Guid?)newLoser.TargetId, (string?)null),
            harness.Previews[^1]);

        harness.ViewModel.SearchChoosesWinner = true;
        await harness.ViewModel.ChooseCommand.ExecuteAsync(new SearchResultViewModel(newWinner));

        Assert.Equal(
            (newWinner.TargetId!.Value, (Guid?)newLoser.TargetId, (string?)null),
            harness.Previews[^1]);
    }

    [Fact]
    public async Task Choose_UnresolvedResult_CannotBecomeTheWinner()
    {
        using var harness = Factory.Create().Settle();
        var unresolved = new SearchResultViewModel(Factory.UnresolvedResult());
        harness.ViewModel.SearchChoosesWinner = true;

        // The rule is structural: the command reports it cannot execute, which is what disables
        // the row in the dropdown.
        Assert.False(harness.ViewModel.ChooseCommand.CanExecute(unresolved));

        await harness.ViewModel.ChooseCommand.ExecuteAsync(unresolved);
        Assert.Single(harness.Previews);

        // As the loser it is accepted, and the preview is asked for the name shape.
        harness.ViewModel.SearchChoosesWinner = false;
        Assert.True(harness.ViewModel.ChooseCommand.CanExecute(unresolved));

        await harness.ViewModel.ChooseCommand.ExecuteAsync(unresolved);
        Assert.Equal((Factory.WinnerId, (Guid?)null, (string?)"ngc7331 mosaic"), harness.Previews[^1]);
    }

    // ---- swap -----------------------------------------------------------------------------

    [Fact]
    public void Swap_ExchangesTheSidesAndRePreviews()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.SwapCommand.Execute(null);
        harness.Settle();

        Assert.Equal((Factory.LoserId, (Guid?)Factory.WinnerId, (string?)null), harness.Previews[^1]);
    }

    [Fact]
    public void Swap_IsDisabledForAnUnresolvedNameLoser()
    {
        using var harness = Factory
            .Create(
                request: new MergeRequest(Factory.WinnerId, null, "ngc7331 mosaic", null),
                preview: Factory.Preview(loser: Factory.UnresolvedSide()))
            .Settle();

        // An unresolved name has no targets row, so it cannot be the winner.
        Assert.False(harness.ViewModel.SwapCommand.CanExecute(null));
    }

    [Fact]
    public void Swap_ExecutedDirectlyWhileBusy_DoesNothing()
    {
        // F7: ICommand.Execute does not consult CanExecute, so a CanExecute carrying a
        // correctness rule needs the same guard in the body. Here the rule is "not while a read
        // is in flight": swapping then would queue a second preview over the parked one.
        using var release = new ManualResetEventSlim(false);
        using var harness = Factory.Create(previewRelease: release);

        // The constructor's preview is parked inside the delegate, so both ids are set and the
        // command is disabled.
        Assert.True(SpinWait.SpinUntil(
            () => { lock (harness.Previews) return harness.Previews.Count == 1; }, TimeSpan.FromSeconds(30)));
        Assert.True(harness.ViewModel.IsBusy);
        Assert.False(harness.ViewModel.SwapCommand.CanExecute(null));

        harness.ViewModel.SwapCommand.Execute(null);

        release.Set();
        harness.Settle();

        // One preview, in the order the request named: the swap did not exchange the sides and
        // did not queue a second read.
        Assert.Equal(
            (Factory.WinnerId, (Guid?)Factory.LoserId, (string?)null), Assert.Single(harness.Previews));
    }

    // ---- confirm ---------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_CallsMergeWithTheChosenSides()
    {
        using var harness = Factory.Create().Settle();

        await harness.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal((Factory.WinnerId, Factory.LoserId), Assert.Single(harness.Merges));
        Assert.Empty(harness.UnresolvedMerges);
    }

    [Fact]
    public async Task Confirm_ReportsTheCounts()
    {
        using var harness = Factory.Create().Settle();

        await harness.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(
            "Moved 148 frames, added 3 aliases, merged 2 notes.",
            harness.ViewModel.ConfirmSummary);
    }

    [Fact]
    public async Task Confirm_SetsMergedAndRequestsClose()
    {
        using var harness = Factory.Create().Settle();

        await harness.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.Merged);
        Assert.Equal(new[] { true }, harness.Closes);
    }

    [Fact]
    public async Task Confirm_ThatFails_ShowsTheErrorAndKeepsTheDialogOpen()
    {
        using var harness = Factory.Create().Settle();
        harness.MergeResult = new MergeResult(MergeStatus.AlreadyMerged, 0, 0, 0, [], null);

        await harness.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.Merged);
        Assert.Empty(harness.Closes);
        Assert.Equal("That target has already been merged away.", harness.ViewModel.ErrorText);
        Assert.Null(harness.ViewModel.ConfirmSummary);
    }

    [Fact]
    public void Confirm_IsDisabledWhenBothSidesAreTheSameTarget()
    {
        using var harness = Factory
            .Create(
                request: new MergeRequest(Factory.WinnerId, Factory.WinnerId, null, null),
                preview: Factory.Preview(loser: Factory.Side()))
            .Settle();

        Assert.NotNull(harness.ViewModel.Winner);
        Assert.NotNull(harness.ViewModel.Loser);
        Assert.False(harness.ViewModel.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public void Confirm_IsDisabledWhileBusy()
    {
        using var release = new ManualResetEventSlim(false);
        using var harness = Factory.Create(previewRelease: release);

        // The constructor's own preview is still parked, so the dialog is busy.
        Assert.True(harness.ViewModel.IsBusy);
        Assert.False(harness.ViewModel.ConfirmCommand.CanExecute(null));

        release.Set();
        harness.Settle();

        Assert.False(harness.ViewModel.IsBusy);
        Assert.True(harness.ViewModel.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnresolvedNameRequest_ConfirmCallsMergeUnresolvedName()
    {
        using var harness = Factory
            .Create(
                request: new MergeRequest(Factory.WinnerId, null, "ngc7331 mosaic", null),
                preview: Factory.Preview(loser: Factory.UnresolvedSide()))
            .Settle();

        await harness.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal((Factory.WinnerId, "ngc7331 mosaic"), Assert.Single(harness.UnresolvedMerges));
        Assert.Empty(harness.Merges);
    }

    [Fact]
    public void Cancel_StaysAvailableWhileAPreviewReadIsInFlight()
    {
        // Phase 7 fixer item 2 (phase finding 2): Cancel used to grey on IsBusy, which is true
        // during every preview read as well as the write, so a slow or hanging preview left the
        // user with a dialog they could neither confirm nor close.
        using var release = new ManualResetEventSlim(false);
        using var harness = Factory.Create(previewRelease: release);

        // The constructor's own preview is still parked.
        Assert.True(harness.ViewModel.IsBusy);
        Assert.False(harness.ViewModel.IsWriting);
        Assert.True(harness.ViewModel.CancelCommand.CanExecute(null));

        harness.ViewModel.CancelCommand.Execute(null);
        Assert.Equal(new[] { false }, harness.Closes);

        // Nothing was written, and the parked read is released so the harness can dispose.
        Assert.Empty(harness.Merges);
        Assert.Empty(harness.UnresolvedMerges);
        release.Set();
        harness.Settle();
    }

    [Fact]
    public async Task Cancel_IsDisabledWhileAConfirmIsInFlight()
    {
        // Review finding 2. Cancelling during the write would close reporting Merged false while
        // the transaction commits anyway, so the caller would never reload.
        using var release = new ManualResetEventSlim(false);
        using var harness = Factory.Create().Settle();
        harness.MergeRelease = release;

        var confirm = harness.ViewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsWriting);
        Assert.False(harness.ViewModel.CancelCommand.CanExecute(null));

        harness.ViewModel.CancelCommand.Execute(null);
        Assert.Empty(harness.Closes);

        release.Set();
        await confirm;

        // IsWriting is cleared before the merge's own close, so that close is never blocked.
        Assert.False(harness.ViewModel.IsWriting);
        Assert.False(harness.ViewModel.IsBusy);
        Assert.True(harness.ViewModel.Merged);
        Assert.Equal(new[] { true }, harness.Closes);
    }

    // ---- cancel (roadmap Verify) -----------------------------------------------------------

    [Fact]
    public void Cancel_WritesNothing()
    {
        // The lambda half: no merge delegate of either shape is ever called.
        using var recorded = Factory.Create().Settle();
        recorded.ViewModel.CancelCommand.Execute(null);

        Assert.Empty(recorded.Merges);
        Assert.Empty(recorded.UnresolvedMerges);
        Assert.False(recorded.ViewModel.Merged);

        // The database half: no manifest row, no moved frame, no candidate status change, no
        // alias change and no merged_into_id.
        var (harness, database) = Factory.CreateOverDatabase();
        using var owned = harness;

        harness.ViewModel.CancelCommand.Execute(null);

        Assert.Equal(0, database.ManifestCount);
        Assert.Equal(2, database.FramesOnLoser);
        Assert.Equal(0, database.FramesOnWinner);
        Assert.Equal("pending", database.CandidateStatus);
        Assert.Equal(new[] { "Caldwell 30" }, database.WinnerAliases);
        Assert.Null(database.LoserMergedIntoId);
    }

    [Fact]
    public void Cancel_RequestsCloseWithFalse()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.CancelCommand.Execute(null);

        Assert.Equal(new[] { false }, harness.Closes);
    }

    // ---- lifetime ---------------------------------------------------------------------------

    [Fact]
    public void Dispose_CancelsAnInFlightPreview()
    {
        using var release = new ManualResetEventSlim(false);
        var harness = Factory.Create(previewRelease: release);

        harness.ViewModel.Dispose();
        release.Set();
        harness.Settle();

        // The publish was dropped: nothing reached the bindings after disposal.
        Assert.Null(harness.ViewModel.Winner);
        harness.Dispose();
    }
}
