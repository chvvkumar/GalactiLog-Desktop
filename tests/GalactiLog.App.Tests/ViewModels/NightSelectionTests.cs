using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.4's night selection column (PAR-006, ruling C5): the checked set, its ledger order,
/// the tri-state header box, and the Copy frame list command that now opens a dialog over it.
/// <c>SelectedNights</c> is the spine Phases 16, 18 and 21 consume, so these are the cases those
/// phases inherit.
/// </summary>
public class NightSelectionTests
{
    // The lightest reload the page offers: an enriched re-resolve re-reads the detail, which is
    // the path ReplaceSessions runs down. Nothing here is about re-resolution.
    private static async Task Reload(Factory.Harness harness)
    {
        var before = harness.Loads;
        harness.ReResolveOutcome = (true, "reloaded");
        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);
        harness.Settle();
        Assert.Equal(before + 1, harness.Loads);
    }

    [Fact]
    public void AFreshPage_HasNoCheckedNight()
    {
        using var harness = Factory.Create().Settle();

        Assert.Equal(2, harness.ViewModel.Sessions.Count);
        Assert.Empty(harness.ViewModel.SelectedNights);
        Assert.All(harness.ViewModel.Sessions, card => Assert.False(card.IsChecked));
        Assert.False(harness.ViewModel.AreAllNightsSelected);
    }

    [Fact]
    public void CheckingANight_AddsItToSelectedNights()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.Sessions[0].IsChecked = true;

        Assert.Equal(
            [harness.ViewModel.Sessions[0].SessionDate],
            harness.ViewModel.SelectedNights);
    }

    [Fact]
    public void SelectedNights_AreInLedgerOrder()
    {
        using var harness = Factory.Create().Settle();

        // Checked oldest first; the set still reports the ledger's own order, which is newest
        // first, because it is rebuilt from Sessions rather than appended to.
        harness.ViewModel.Sessions[1].IsChecked = true;
        harness.ViewModel.Sessions[0].IsChecked = true;

        Assert.Equal(
            [Factory.LastSession, Factory.FirstSession],
            harness.ViewModel.SelectedNights);
    }

    [Fact]
    public void UncheckingANight_RemovesIt()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.Sessions[0].IsChecked = true;
        harness.ViewModel.Sessions[1].IsChecked = true;
        harness.ViewModel.Sessions[0].IsChecked = false;

        Assert.Equal([Factory.FirstSession], harness.ViewModel.SelectedNights);
    }

    [Fact]
    public void ToggleAll_FromClear_ChecksEveryNight()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.ToggleAllNightsCommand.Execute(null);

        Assert.All(harness.ViewModel.Sessions, card => Assert.True(card.IsChecked));
        Assert.Equal(2, harness.ViewModel.SelectedNights.Count);
        Assert.True(harness.ViewModel.AreAllNightsSelected);
    }

    [Fact]
    public void ToggleAll_FromPartial_ClearsEveryNight()
    {
        // Spec 12.4's wording, and deliberately not a plain invert: a partial selection clears.
        using var harness = Factory.Create().Settle();
        harness.ViewModel.Sessions[0].IsChecked = true;

        harness.ViewModel.ToggleAllNightsCommand.Execute(null);

        Assert.All(harness.ViewModel.Sessions, card => Assert.False(card.IsChecked));
        Assert.Empty(harness.ViewModel.SelectedNights);
    }

    [Fact]
    public void ToggleAll_FromFull_ClearsEveryNight()
    {
        using var harness = Factory.Create().Settle();
        harness.ViewModel.ToggleAllNightsCommand.Execute(null);

        harness.ViewModel.ToggleAllNightsCommand.Execute(null);

        Assert.Empty(harness.ViewModel.SelectedNights);
        Assert.False(harness.ViewModel.AreAllNightsSelected);
    }

    [Fact]
    public void AreAllNightsSelected_IsTriState()
    {
        using var harness = Factory.Create().Settle();

        Assert.False(harness.ViewModel.AreAllNightsSelected);

        harness.ViewModel.Sessions[0].IsChecked = true;
        Assert.Null(harness.ViewModel.AreAllNightsSelected);

        harness.ViewModel.Sessions[1].IsChecked = true;
        Assert.True(harness.ViewModel.AreAllNightsSelected);
    }

    [Fact]
    public void CheckingANight_DoesNotChangeTheSelectedSession()
    {
        // Spec 12.4: the lit row names the night the pane is showing, and the checks name the
        // nights an action will act on. Two marks, two questions.
        using var harness = Factory.Create().Settle();
        var lit = harness.ViewModel.SelectedSession;
        Assert.Same(harness.ViewModel.Sessions[0], lit);

        harness.ViewModel.Sessions[1].IsChecked = true;

        Assert.Same(lit, harness.ViewModel.SelectedSession);
        Assert.False(harness.ViewModel.Sessions[1].IsExpanded);
    }

    [Fact]
    public void ALitRowMove_ClearsEveryCheck()
    {
        // P25 R1, Explorer semantics: a plain move of the lit row empties the checked set.
        using var harness = Factory.Create().Settle();
        harness.ViewModel.Sessions[0].IsChecked = true;
        harness.ViewModel.Sessions[1].IsChecked = true;

        harness.ViewModel.SelectedSession = harness.ViewModel.Sessions[1];

        Assert.All(harness.ViewModel.Sessions, card => Assert.False(card.IsChecked));
        Assert.Empty(harness.ViewModel.SelectedNights);
        Assert.False(harness.ViewModel.AreAllNightsSelected);
    }

    [Fact]
    public async Task AReloadThatKeepsTheNights_KeepsEveryCheck()
    {
        // P25 R1: ReplaceSessions keeps its guard, so the write of the kept night back into
        // SelectedSession clears nothing, on the lit night and the other one alike.
        using var harness = Factory.Create().Settle();
        harness.ViewModel.Sessions[0].IsChecked = true;
        harness.ViewModel.Sessions[1].IsChecked = true;

        await Reload(harness);

        Assert.All(harness.ViewModel.Sessions, card => Assert.True(card.IsChecked));
        Assert.Equal([Factory.LastSession, Factory.FirstSession], harness.ViewModel.SelectedNights);
    }

    [Fact]
    public async Task AReloadThatKeepsTheNight_KeepsItsCheck()
    {
        using var harness = Factory.Create().Settle();
        harness.ViewModel.Sessions[1].IsChecked = true;

        await Reload(harness);


        Assert.True(harness.ViewModel.Sessions[1].IsChecked);
        Assert.Equal([Factory.FirstSession], harness.ViewModel.SelectedNights);
    }

    [Fact]
    public async Task AReloadThatDropsTheNight_DropsItFromSelectedNights()
    {
        var sessions = new List<GalactiLog.Data.Queries.SessionOverview>
        {
            Factory.Session(Factory.LastSession),
            Factory.Session(Factory.FirstSession),
        };

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(sessions: [.. sessions])).Settle();
        harness.ViewModel.Sessions[1].IsChecked = true;
        Assert.Equal([Factory.FirstSession], harness.ViewModel.SelectedNights);

        // The scan pruned the older night away.
        sessions.RemoveAt(1);
        await Reload(harness);

        Assert.Single(harness.ViewModel.Sessions);
        Assert.Empty(harness.ViewModel.SelectedNights);
    }

    [Fact]
    public async Task AVanishedCard_LeavesNoHandlerBehind()
    {
        // The page subscribes to every card's PropertyChanged to rebuild the set. A card the scan
        // pruned away must be detached in the same loop that disposes it, or the page leaks a
        // handler per scan and a disposed card can still write to the live set.
        var sessions = new List<GalactiLog.Data.Queries.SessionOverview>
        {
            Factory.Session(Factory.LastSession),
            Factory.Session(Factory.FirstSession),
        };

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(sessions: [.. sessions])).Settle();
        var vanishing = harness.ViewModel.Sessions[1];

        sessions.RemoveAt(1);
        await Reload(harness);

        // The detached card is no longer able to put its date back into the page's set.
        vanishing.IsChecked = true;

        Assert.Empty(harness.ViewModel.SelectedNights);
        Assert.False(harness.ViewModel.AreAllNightsSelected);
    }

    [Fact]
    public void CopyFrameList_IsDisabledWithNothingChecked()
    {
        using var harness = Factory.Create().Settle();

        Assert.False(harness.ViewModel.CopyFrameListCommand.CanExecute(null));

        harness.ViewModel.Sessions[0].IsChecked = true;

        Assert.True(harness.ViewModel.CopyFrameListCommand.CanExecute(null));
    }

    [Fact]
    public async Task CopyFrameList_ExecutedDirectlyWithNothingChecked_DoesNothing()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the guard is in
        // the body as well.
        using var harness = Factory.Create().Settle();

        await harness.ViewModel.CopyFrameListCommand.ExecuteAsync(null);

        Assert.Empty(harness.OpenedFrameLists);
        Assert.Empty(harness.Clipboard);
    }

    [Fact]
    public async Task CopyFrameList_OpensTheDialogOverTheCheckedNights_InLedgerOrder()
    {
        using var harness = Factory.Create().Settle();
        harness.ViewModel.Sessions[1].IsChecked = true;
        harness.ViewModel.Sessions[0].IsChecked = true;

        await harness.ViewModel.CopyFrameListCommand.ExecuteAsync(null);

        var (groupKey, nights) = Assert.Single(harness.OpenedFrameLists);
        Assert.Equal(Factory.ResolvedGroupKey, groupKey);
        Assert.Equal(
            [Factory.LastSession, Factory.FirstSession],
            nights.Select(night => night.Date));

        // The page writes no clipboard of its own any more: the dialog does.
        Assert.Empty(harness.Clipboard);
    }

    [Fact]
    public async Task CopyFrameList_CarriesTheDetailTheCardHasAlreadyLoaded()
    {
        using var harness = Factory.Create().Settle();
        harness.SettleCards();

        // The lit night's card loaded its own detail on selection; the other night's did not.
        harness.ViewModel.ToggleAllNightsCommand.Execute(null);
        await harness.ViewModel.CopyFrameListCommand.ExecuteAsync(null);

        var nights = Assert.Single(harness.OpenedFrameLists).Nights;
        Assert.NotNull(nights.Single(night => night.Date == Factory.LastSession).Loaded);
        Assert.Null(nights.Single(night => night.Date == Factory.FirstSession).Loaded);
    }

    [Fact]
    public void AnEmptyLedger_ReportsNoSelectionRatherThanAll()
    {
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(sessions: [])).Settle();

        Assert.Empty(harness.ViewModel.Sessions);
        Assert.False(harness.ViewModel.AreAllNightsSelected);
        Assert.False(harness.ViewModel.CopyFrameListCommand.CanExecute(null));
    }

    [Fact]
    public void TheHeaderBox_OnAnEmptyLedger_ReportsUncheckedAfterAToggle()
    {
        // Task 4 review P3. ToggleAllNights changes no card on an empty ledger, so no card raised,
        // RebuildSelectedNights never ran, and the OneWay binding never overwrote the value the
        // click had already left on the box: the header box could be drawn checked over a ledger
        // with no nights in it. The raise is what puts the binding back in charge.
        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(sessions: [])).Settle();

        List<string?> raised = [];
        harness.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        harness.ViewModel.ToggleAllNightsCommand.Execute(null);

        Assert.Contains(nameof(harness.ViewModel.AreAllNightsSelected), raised);
        Assert.False(harness.ViewModel.AreAllNightsSelected);
        Assert.Empty(harness.ViewModel.SelectedNights);
    }
}
