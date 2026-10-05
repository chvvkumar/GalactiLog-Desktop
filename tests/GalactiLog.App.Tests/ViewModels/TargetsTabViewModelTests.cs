using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using GalactiLog.Data.Ingest;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 7 Task 3. Spec 12.9's candidate list and spec 12.10's empty state, plus the roadmap
// Verify line's App half: dismiss sets status dismissed and removes the row, and edit-target
// opens target search.
public class TargetsTabViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public void Construction_LoadsThePendingCandidates()
    {
        using var harness = Factory.Create([Factory.Candidate(), Factory.Orphan()]).Settle();

        Assert.Equal(1, harness.Loads);
        Assert.Equal(
            new[] { "NGC7331 field", "Zzyzx Blob 42" },
            harness.ViewModel.Candidates.Select(row => row.SourceName));
    }

    // [AvaloniaFact], not [Fact]: Dispatcher.UIThread means nothing without a real UI thread.
    // async, and it awaits the load rather than blocking on it (TRACKING section 2 item 8): the
    // headless UI thread is itself a thread-pool thread, so a blocking wait from it on a Task.Run
    // queued from it lets the pool inline that work onto the very thread this asserts it is not
    // on.
    [AvaloniaFact]
    public async Task Load_RunsOffTheUiThread()
    {
        // MergeCandidateQuery.Pending is a synchronous SQLite read; it must never run on the
        // dispatcher. The load is awaited rather than blocked on (TRACKING section 2 item 8).
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        using var harness = Factory.Create(
            [Factory.Candidate()],
            onUiThread: () => Dispatcher.UIThread.CheckAccess());
        await harness.ViewModel.PendingLoad!;

        Assert.NotEmpty(harness.LoadOnUiThread);
        Assert.All(harness.LoadOnUiThread, Assert.False);
    }

    [AvaloniaFact]
    public async Task Load_PublishesOnTheUiThread()
    {
        // The production post seam, not the inline one: the query runs on the pool and every
        // binding write reaches the dispatcher first.
        using var harness = Factory.Create(
            [Factory.Candidate()],
            post: action => Dispatcher.UIThread.Post(action));
        await harness.ViewModel.PendingLoad!;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(harness.ViewModel.Candidates);

        var publishedOnUiThread = new List<bool>();
        harness.ViewModel.Candidates.CollectionChanged +=
            (_, _) => publishedOnUiThread.Add(Dispatcher.UIThread.CheckAccess());

        harness.Pending = [Factory.Candidate(), Factory.Orphan()];
        await harness.ViewModel.ReloadCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(publishedOnUiThread);
        Assert.All(publishedOnUiThread, Assert.True);
        Assert.Equal(2, harness.ViewModel.Candidates.Count);
    }

    [Fact]
    public void EmptyList_ShowsTheNoDuplicateSuggestionsState()
    {
        using var harness = Factory.Create().Settle();

        Assert.Empty(harness.ViewModel.Candidates);
        Assert.True(harness.ViewModel.ShowNoCandidates);
    }

    [Fact]
    public void NonEmptyList_HidesTheEmptyState()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();

        Assert.False(harness.ViewModel.ShowNoCandidates);
    }

    // Review minor 3. A query that threw left the section a bare header: no rows, and the empty
    // state suppressed because the load never completed.
    [Fact]
    public async Task LoadThatThrows_ShowsTheFailedState_AndClearsItOnTheNextLoad()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        harness.LoadThrows = new InvalidOperationException("the database is gone");

        await harness.ViewModel.ReloadCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.LoadFailed);
        Assert.False(harness.ViewModel.IsLoading);
        Assert.False(harness.ViewModel.ShowNoCandidates);

        harness.LoadThrows = null;
        harness.Pending = [];
        await harness.ViewModel.ReloadCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.LoadFailed);
        Assert.True(harness.ViewModel.ShowNoCandidates);
    }

    [Fact]
    public void EmptyStateIsHiddenWhileTheFirstLoadIsInFlight()
    {
        using var release = new ManualResetEventSlim(false);

        // The constructor's own load is parked inside the query delegate, so nothing has
        // published yet and the empty state must not be showing.
        using var harness = Factory.Create(loadRelease: release);

        Assert.False(harness.ViewModel.ShowNoCandidates);
        Assert.True(harness.ViewModel.IsLoading);

        release.Set();
        harness.Settle();
        Assert.True(harness.ViewModel.ShowNoCandidates);
        Assert.False(harness.ViewModel.IsLoading);
    }

    [Fact]
    public async Task Dismiss_RemovesTheRow()
    {
        using var harness = Factory.Create([Factory.Candidate(), Factory.Orphan()]).Settle();
        var row = harness.ViewModel.Candidates[0];

        await harness.ViewModel.DismissCommand.ExecuteAsync(row);

        Assert.Equal(
            new[] { "Zzyzx Blob 42" },
            harness.ViewModel.Candidates.Select(candidate => candidate.SourceName));
    }

    [Fact]
    public async Task Dismiss_CallsTheRepositoryWithTheRowId()
    {
        var candidate = Factory.Candidate();
        using var harness = Factory.Create([candidate]).Settle();

        await harness.ViewModel.DismissCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);

        Assert.Equal(candidate.Id, Assert.Single(harness.Dismissed));
    }

    [Fact]
    public async Task Dismiss_ThatFails_LeavesTheRow()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        harness.DismissResult = false;

        await harness.ViewModel.DismissCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);

        Assert.Single(harness.ViewModel.Candidates);
    }

    [Fact]
    public async Task Accept_OpensTheMergeDialogForThatRow()
    {
        var candidate = Factory.Candidate();
        using var harness = Factory.Create([candidate]).Settle();

        await harness.ViewModel.AcceptCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);

        Assert.Equal(candidate, Assert.Single(harness.OpenedMerges));
    }

    [Fact]
    public async Task Accept_ThatMerged_ReloadsTheList()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        harness.MergeResult = "Moved 4 frames, added 1 alias, merged 0 notes.";
        harness.Pending = [];

        await harness.ViewModel.AcceptCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);
        harness.Settle();

        Assert.Equal(2, harness.Loads);
        Assert.Empty(harness.ViewModel.Candidates);
    }

    // Phase 7 FIXER item 17: the dialog sets spec 12.9's count sentence and raises CloseRequested
    // in the next statement, so the counts were never on screen. They arrive with the result and
    // are reported here instead.
    [Fact]
    public async Task Accept_ThatMerged_ReportsTheCounts()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        harness.MergeResult = "Moved 4 frames, added 1 alias, merged 0 notes.";

        await harness.ViewModel.AcceptCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);
        harness.Settle();

        Assert.Equal("Moved 4 frames, added 1 alias, merged 0 notes.", harness.ViewModel.MergeSummary);
    }

    [Fact]
    public async Task Accept_ThatWasCancelled_ReportsNoCounts()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        harness.MergeResult = null;

        await harness.ViewModel.AcceptCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);
        harness.Settle();

        Assert.Null(harness.ViewModel.MergeSummary);
    }

    [Fact]
    public async Task Accept_ThatWasCancelled_DoesNotReload()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        harness.MergeResult = null;

        await harness.ViewModel.AcceptCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);

        Assert.Equal(1, harness.Loads);
        Assert.Single(harness.ViewModel.Candidates);
    }

    // Roadmap Verify: "edit-target opens target search".
    [Fact]
    public void BeginRetarget_OpensTargetSearch()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        var row = harness.ViewModel.Candidates[0];

        harness.ViewModel.BeginRetargetCommand.Execute(row);

        Assert.Same(row, harness.ViewModel.RetargetingRow);
        Assert.Equal("", harness.ViewModel.RetargetSearchText);
        Assert.Empty(harness.ViewModel.RetargetResults);
    }

    [Fact]
    public async Task Retarget_SearchDebouncesBeforeQuerying()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        harness.SearchResults = [Factory.TargetResult()];
        var tab = harness.ViewModel;
        tab.BeginRetargetCommand.Execute(tab.Candidates[0]);

        tab.RetargetSearchText = "and";

        // Parked in the debounce: no query yet.
        Assert.Empty(harness.Searches);
        Assert.Equal(DashboardViewModel.DebounceWindow, Assert.Single(harness.Delay.Requested));

        harness.Delay.Release();
        await tab.PendingSearch!.WaitAsync(Budget);

        Assert.Equal("and", Assert.Single(harness.Searches));
        Assert.Single(tab.RetargetResults);
    }

    [AvaloniaFact]
    public async Task Retarget_SearchRunsOffTheUiThread()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        using var harness = Factory.Create(
            [Factory.Candidate()],
            onUiThread: () => Dispatcher.UIThread.CheckAccess());
        harness.Settle();
        var tab = harness.ViewModel;
        tab.BeginRetargetCommand.Execute(tab.Candidates[0]);

        tab.RetargetSearchText = "and";
        harness.Delay.Release();
        await tab.PendingSearch!.WaitAsync(Budget);

        Assert.NotEmpty(harness.SearchOnUiThread);
        Assert.All(harness.SearchOnUiThread, Assert.False);
    }

    [Fact]
    public async Task Retarget_ChoosingATarget_CallsRetargetAndReloads()
    {
        var candidate = Factory.Candidate();
        using var harness = Factory.Create([candidate]).Settle();
        var tab = harness.ViewModel;
        tab.BeginRetargetCommand.Execute(tab.Candidates[0]);
        var result = new SearchResultViewModel(Factory.TargetResult());

        await tab.ChooseRetargetCommand.ExecuteAsync(result);
        harness.Settle();

        Assert.Equal((candidate.Id, Factory.WinnerId), Assert.Single(harness.Retargets));
        Assert.Equal(2, harness.Loads);
        Assert.Null(tab.RetargetingRow);
    }

    [Fact]
    public void Retarget_UnresolvedSearchResult_IsNotSelectable()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        var tab = harness.ViewModel;
        tab.BeginRetargetCommand.Execute(tab.Candidates[0]);

        Assert.False(tab.ChooseRetargetCommand.CanExecute(
            new SearchResultViewModel(Factory.UnresolvedResult())));
        Assert.True(tab.ChooseRetargetCommand.CanExecute(
            new SearchResultViewModel(Factory.TargetResult())));
    }

    [Fact]
    public async Task CancelRetarget_ClearsTheRowAndTheResults()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        harness.SearchResults = [Factory.TargetResult()];
        var tab = harness.ViewModel;
        tab.BeginRetargetCommand.Execute(tab.Candidates[0]);
        tab.RetargetSearchText = "and";
        harness.Delay.Release();
        await tab.PendingSearch!.WaitAsync(Budget);
        Assert.Single(tab.RetargetResults);

        tab.CancelRetargetCommand.Execute(null);

        Assert.Null(tab.RetargetingRow);
        Assert.Equal("", tab.RetargetSearchText);
        Assert.Empty(tab.RetargetResults);
    }

    // The dedup pass runs at the end of a scan (Task 1), so the list has to re-read when one
    // finishes. The one App-layer subscriber to the coordinator is ScanStatusService, and its
    // ScanFinished cannot be raised from outside it, so these three drive a real (empty) scan the
    // way ScanStatusServiceTests and TargetDetailViewModelTests do.
    private static (ScanCoordinator Coordinator, ScanStatusService Status) EmptyScan(SettingsFixture settings)
    {
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        return (coordinator, new ScanStatusService(coordinator, action => action()));
    }

    [Fact]
    public async Task ScanFinished_ReloadsTheList()
    {
        using var settings = new SettingsFixture();
        var (coordinator, scanStatus) = EmptyScan(settings);
        using var status = scanStatus;

        using var harness = Factory.Create([Factory.Candidate()], status).Settle();
        harness.Pending = [Factory.Candidate(), Factory.Orphan()];

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        harness.Settle();

        Assert.Equal(2, harness.Loads);
        Assert.Equal(2, harness.ViewModel.Candidates.Count);
    }

    [Fact]
    public async Task ScanFinished_AfterDispose_DoesNothing()
    {
        using var settings = new SettingsFixture();
        var (coordinator, scanStatus) = EmptyScan(settings);
        using var status = scanStatus;

        var harness = Factory.Create([Factory.Candidate()], status).Settle();
        harness.ViewModel.Dispose();

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, harness.Loads);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromScanStatus()
    {
        using var settings = new SettingsFixture();
        var (coordinator, scanStatus) = EmptyScan(settings);
        using var status = scanStatus;

        var harness = Factory.Create([], status).Settle();

        harness.ViewModel.Dispose();

        // A second dispose is a no-op, and nothing the tab started can publish afterwards.
        harness.ViewModel.Dispose();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, harness.Loads);
    }

    [Fact]
    public void RowViewModel_ScoreText_TruncatesThePercentage()
    {
        // 0.879 is 87.9%: truncated to 87, so it agrees with the trigram reason text
        // DuplicateDetector wrote for the same row.
        Assert.Equal("87%", new MergeCandidateRowViewModel(Factory.Candidate(score: 0.879d)).ScoreText);
        Assert.Equal("100%", new MergeCandidateRowViewModel(Factory.Candidate(score: 1d)).ScoreText);
        Assert.Equal("0%", new MergeCandidateRowViewModel(Factory.Candidate(score: 0d)).ScoreText);
    }

    [Fact]
    public void RowViewModel_FrameCountText_SingularAndPlural()
    {
        Assert.Equal("1 frame", new MergeCandidateRowViewModel(Factory.Candidate(frameCount: 1)).FrameCountText);
        Assert.Equal("12 frames", new MergeCandidateRowViewModel(Factory.Candidate(frameCount: 12)).FrameCountText);
        Assert.Equal("0 frames", new MergeCandidateRowViewModel(Factory.Candidate(frameCount: 0)).FrameCountText);
    }

    [Fact]
    public void RowViewModel_OrphanCandidate_ReadsNoSuggestion()
    {
        var orphan = new MergeCandidateRowViewModel(Factory.Orphan());

        Assert.Equal("No suggestion", orphan.SuggestedText);
        Assert.False(orphan.HasSuggestion);
        Assert.Equal("orphan", orphan.Method);
        Assert.True(orphan.HasReason);
    }

    // ---- Phase 7 Task 5: the merge history region ---------------------------------------

    [Fact]
    public async Task MergeHistory_UndoneReloadsTheCandidateList()
    {
        var row = new MergeHistoryRow(
            Guid.NewGuid(),
            Factory.WinnerId,
            "NGC 7331",
            Guid.NewGuid(),
            "NGC7331 field",
            new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            12);

        using var history = new MergeHistoryViewModel(
            () => [row],
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 12, 0, ["NGC7331 field"]),
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 12, 0, ["NGC7331 field"]),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: action => action());
        Settle(history);

        using var harness = Factory.Create(mergeHistory: history).Settle();
        Assert.Same(history, harness.ViewModel.MergeHistory);

        // An undo reverts the merge's candidate rows to pending (spec 9.7), so they belong back
        // in the list above the history.
        harness.Pending = [Factory.Candidate()];
        var before = harness.Loads;

        await history.UndoCommand.ExecuteAsync(history.Rows[0]);
        harness.Settle();

        Assert.Equal(before + 1, harness.Loads);
        Assert.Single(harness.ViewModel.Candidates);
    }

    [Fact]
    public async Task AcceptedMerge_ReloadsTheMergeHistoryToo()
    {
        var rows = new List<MergeHistoryRow>();
        using var history = new MergeHistoryViewModel(
            () => rows,
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 0, 0, []),
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 0, 0, []),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: action => action());
        Settle(history);

        using var harness = Factory.Create([Factory.Candidate()], mergeHistory: history).Settle();
        harness.MergeResult = "Moved 4 frames, added 1 alias, merged 0 notes.";

        // The merge writes a manifest, so the history region below is stale the moment it returns.
        rows.Add(new MergeHistoryRow(
            Guid.NewGuid(),
            Factory.WinnerId,
            "NGC 7331",
            Guid.NewGuid(),
            "NGC7331 field",
            new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            12));

        await harness.ViewModel.AcceptCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);
        harness.Settle();
        Settle(history);

        Assert.Single(history.Rows);
    }

    // ---- Phase 7 Task 6: the unresolved-name region ------------------------------------

    [Fact]
    public async Task AcceptedMerge_ReloadsTheUnresolvedNamesToo()
    {
        var loads = 0;
        using var unresolved = Factory.UnresolvedNames(() => loads++, Factory.UnresolvedName());
        using var harness = Factory
            .Create([Factory.Candidate()], unresolvedNames: unresolved)
            .Settle();
        harness.MergeResult = "Moved 4 frames, added 1 alias, merged 0 notes.";
        var before = loads;

        // A merge that absorbed an unresolved name took that name and its frames out of the
        // unresolved list, so the region is stale the moment the dialog returns true.
        await harness.ViewModel.AcceptCommand.ExecuteAsync(harness.ViewModel.Candidates[0]);
        harness.Settle();
        unresolved.PendingLoad?.Wait(Budget);

        Assert.Equal(before + 1, loads);
    }

    [Fact]
    public async Task MergeHistory_UndoneReloadsTheUnresolvedNamesToo()
    {
        var loads = 0;
        using var unresolved = Factory.UnresolvedNames(() => loads++, Factory.UnresolvedName());

        var row = new MergeHistoryRow(
            Guid.NewGuid(),
            Factory.WinnerId,
            "NGC 7331",
            null,
            "NGC7331 field",
            new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            12);

        using var history = new MergeHistoryViewModel(
            () => [row],
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 12, 0, ["NGC7331 field"]),
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 12, 0, ["NGC7331 field"]),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: action => action());
        Settle(history);

        using var harness = Factory
            .Create(mergeHistory: history, unresolvedNames: unresolved)
            .Settle();
        var before = loads;

        // Undoing an unresolved-name merge puts that name's frames back where they were, so the
        // name is unresolved again and belongs back in the list.
        await history.UndoCommand.ExecuteAsync(history.Rows[0]);
        harness.Settle();
        unresolved.PendingLoad?.Wait(Budget);

        Assert.Equal(before + 1, loads);
    }

    [Fact]
    public void Dispose_DisposesTheMergeHistory()
    {
        var rows = new List<MergeHistoryRow>();
        var history = new MergeHistoryViewModel(
            () => rows,
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 0, 0, []),
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 0, 0, []),
            new GeneralSettings(),
            post: action => action());
        Settle(history);

        var harness = Factory.Create(mergeHistory: history).Settle();
        harness.ViewModel.Dispose();

        // A leaked list keeps a query alive past the tab.
        rows.Add(new MergeHistoryRow(
            Guid.NewGuid(), Factory.WinnerId, "NGC 7331", null, "a name",
            DateTime.UtcNow, 1));
        history.Reload();
        Settle(history);

        Assert.Empty(history.Rows);
    }

    /// <summary>Joins a hand-built history's in-flight load. A helper, not a test method, which is
    /// what xunit's own analyzer asks of a blocking wait; nothing here asserts what thread the
    /// load ran on.</summary>
    private static void Settle(MergeHistoryViewModel history)
        => history.PendingLoad?.Wait(TimeSpan.FromSeconds(30));
}
