using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Spec 12.7's Targets tab and spec 12.9's candidate list: one row per pending
/// <c>merge_candidates</c> row, with accept, dismiss and edit-target per row.
/// </summary>
/// <remarks>
/// Every collaborator arrives as a delegate rather than as a query or a repository object, the
/// rule <c>DashboardViewModel</c> and <c>TargetDetailViewModel</c> both follow, so the tab
/// constructs in a unit test with lambdas and no database (design-spec 18.3). The tab is
/// constructed on the UI thread, so the load runs on a background thread and publishes through
/// the <c>post</c> seam.
/// <para>
/// Phase 7 fills this type across three tasks. Task 3 built the candidate list and left three
/// seams (<see cref="MergeHistory"/>, <see cref="UnresolvedNames"/>,
/// <see cref="RenameHistory"/>); Task 4 replaced the <c>openMerge</c> registration with the real
/// dialog, leaving the delegate's shape unchanged; Task 5 fills the merge history seam; Task 6
/// fills the other two.
/// </para>
/// </remarks>
public sealed partial class TargetsTabViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<MergeCandidateRow>> _pending;
    private readonly Func<Guid, bool> _dismiss;
    private readonly Func<MergeCandidateRow, Task<string?>> _openMerge;
    private readonly Func<string, IReadOnlyList<TargetSearchResult>> _searchTargets;
    private readonly Func<Guid, Guid, bool> _retarget;
    private readonly ScanStatusService? _scanStatus;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // One tab-lifetime source every background window is linked to, the shape
    // DashboardViewModel established (F6): disposing the tab cancels a load that is still parked.
    private readonly CancellationTokenSource _lifetime = new();

    // The retarget search debounce window. Only the search has one; a reload is not driven by
    // typing. FIXER LIST F11: on the shared Debouncer rather than a hand-rolled copy of
    // cancel-previous, link-to-lifetime, bump-generation, await-the-window.
    private readonly Debouncer _searchWindow;

    // Only the newest load (or search) may write to the bindings. The token alone is not enough:
    // work that already finished can still be sitting in the dispatcher queue when a newer
    // request is made, and posting it then would show a stale list.
    private int _generation;

    private bool _hasLoaded;
    private bool _disposed;

    /// <param name="pending">Normally <c>MergeCandidateQuery.Pending</c>. A delegate, so the tab
    /// builds in a unit test with no database (design-spec 18.3).</param>
    /// <param name="dismiss">Normally <c>MergeCandidateRepository.Dismiss</c>.</param>
    /// <param name="openMerge">Opens spec 12.9's modal for a candidate and completes with spec
    /// 12.9's count sentence when a merge happened, or null when none did, so the list can reload
    /// and the counts can be shown (Phase 7 FIXER item 17: the dialog closes on the statement
    /// after it sets them, so this surface is where they become visible). Normally <c>AppHost</c>'s lambda over
    /// <c>MergeDialogService.ShowAsync</c> (Task 4), which turns the candidate into a
    /// <c>MergeRequest</c> by asking <c>MergePreviewQuery</c> whether its <c>source_name</c> is
    /// an active target's <c>primary_name</c> (ruling Q18). The delegate's shape is unchanged
    /// from Task 3: the merge shape is decided in Data, never here.</param>
    /// <param name="searchTargets">Normally <c>TargetSearchQuery.Search</c>, for the edit-target
    /// action's dropdown. The same fuzzy, alias-aware search the dashboard uses (spec 12.9),
    /// reached through the same query and rendered with the same
    /// <see cref="SearchResultViewModel"/>: one ranking at one threshold, never a second.</param>
    /// <param name="retarget">Normally <c>MergeCandidateRepository.Retarget</c>.</param>
    /// <param name="mergeHistory">Spec 12.9's merge history with undo (Task 5), over every
    /// manifest. The same view-model type Target detail renders over one winner's manifests;
    /// only the load delegate differs. Owned by this tab, so it is disposed here. Null leaves the
    /// region empty, which is what a test that is not about the history wants.</param>
    /// <param name="unresolvedNames">Spec 9.7's unresolved-name list with its retry (Task 6).
    /// A container-owned singleton in production, because Phase 10's Diagnostics page binds the
    /// same instance, so this tab renders it without disposing it. Null leaves the region
    /// empty.</param>
    /// <param name="renameHistory">Spec 12.7's rename history (Task 6). Same ownership rule as
    /// <paramref name="unresolvedNames"/>. Null leaves the region empty.</param>
    /// <param name="scanStatus">Reloads the list after a scan, because the dedup pass runs at the
    /// end of one (Task 1). Subscribed here, never to <c>ScanCoordinator</c>.</param>
    /// <param name="delay">The search debounce seam, so a test does not sleep.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load, dismiss, merge or search is logged, never
    /// rethrown on the UI thread.</param>
    /// <param name="createTarget">Spec 12.7's Create target form (PAR-001), rendered inline in
    /// this tab's own markup rather than in a region of its own. A container-owned singleton in
    /// production, because the unresolved-name list opens the same instance, so this tab renders
    /// it without disposing it. Null leaves the section's form unavailable.</param>
    public TargetsTabViewModel(
        Func<IReadOnlyList<MergeCandidateRow>> pending,
        Func<Guid, bool> dismiss,
        Func<MergeCandidateRow, Task<string?>> openMerge,
        Func<string, IReadOnlyList<TargetSearchResult>> searchTargets,
        Func<Guid, Guid, bool> retarget,
        MergeHistoryViewModel? mergeHistory = null,
        UnresolvedNamesViewModel? unresolvedNames = null,
        RenameHistoryViewModel? renameHistory = null,
        ScanStatusService? scanStatus = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        CreateTargetViewModel? createTarget = null)
    {
        _pending = pending;
        _dismiss = dismiss;
        _openMerge = openMerge;
        _searchTargets = searchTargets;
        _retarget = retarget;
        _scanStatus = scanStatus;
        MergeHistory = mergeHistory;
        UnresolvedNames = unresolvedNames;
        RenameHistory = renameHistory;
        CreateTarget = createTarget;

        if (mergeHistory is not null)
        {
            // An undo puts the merge's candidate rows back to pending, so the list above this
            // region is stale the moment it succeeds (spec 9.7, Task 2's Unmerge).
            mergeHistory.Undone += OnMergeUndone;
        }

        _searchWindow = new Debouncer(_lifetime.Token, delay ?? Task.Delay, DashboardViewModel.DebounceWindow);
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        // Assigned before anything can observe it. The change handler ignores a write made while
        // no row is being retargeted, so this does not open a search window.
        RetargetSearchText = "";

        if (scanStatus is not null)
        {
            // ScanStatusService has already marshalled onto the UI thread; do not post again.
            scanStatus.ScanFinished += OnScanFinished;
        }

        Load();
    }

    /// <summary>Spec 12.9's candidate list, newest first, as the query ordered it.</summary>
    public ObservableCollection<MergeCandidateRowViewModel> Candidates { get; } = [];

    /// <summary>Spec 12.10: "No duplicate suggestions." True when the load has completed and the
    /// list is empty; false while the first load is in flight, so the empty state does not flash
    /// before the rows arrive.</summary>
    public bool ShowNoCandidates => _hasLoaded && Candidates.Count == 0;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>A load that threw. The section renders one neutral line instead of a bare header
    /// (spec 12.10: a failure is reported, never a silent empty list). Carries no text from the
    /// exception; the log has that. Cleared by the next successful load.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>The edit-target dropdown's state, shared by every row: one row is being
    /// re-targeted at a time.</summary>
    [ObservableProperty]
    public partial MergeCandidateRowViewModel? RetargetingRow { get; private set; }

    /// <summary>Spec 12.9's confirm counts, from the last merge accepted on this tab. Null until
    /// one happens, and replaced by the next one.</summary>
    [ObservableProperty]
    public partial string? MergeSummary { get; private set; }

    [ObservableProperty]
    public partial string RetargetSearchText { get; set; }

    /// <summary>The edit-target dropdown's rows, in the query's ranking. Rendered with the
    /// dashboard's own <see cref="SearchResultViewModel"/>; nothing here re-ranks or filters.
    /// </summary>
    public ObservableCollection<SearchResultViewModel> RetargetResults { get; } = [];

    /// <summary>Spec 12.9's merge history with undo, over every manifest (Task 5). Null leaves
    /// the region empty.</summary>
    public MergeHistoryViewModel? MergeHistory { get; }

    /// <summary>Spec 9.7's unresolved-name list with its retry action (Task 6). Null leaves the
    /// region empty. Not disposed here: it is a container-owned singleton that Phase 10's
    /// Diagnostics page binds as well, so this tab is not its owner.</summary>
    public UnresolvedNamesViewModel? UnresolvedNames { get; }

    /// <summary>Spec 12.7's rename history (Task 6). Null leaves the region empty, and it is not
    /// disposed here for the same reason as <see cref="UnresolvedNames"/>.</summary>
    public RenameHistoryViewModel? RenameHistory { get; }

    /// <summary>Spec 12.7's Create target form (PAR-001, Phase 14B Task 3). Not disposed here for
    /// the same reason as <see cref="UnresolvedNames"/>: the unresolved-name list opens the same
    /// instance, so the container owns it.</summary>
    public CreateTargetViewModel? CreateTarget { get; }

    /// <summary>The in-flight load, so a test can await it instead of sleeping. Mirrors
    /// <c>TargetDetailViewModel.PendingLoad</c>.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>The in-flight debounce-and-search, so a test can await it instead of sleeping.
    /// </summary>
    internal Task? PendingSearch { get; private set; }

    /// <summary>Re-reads the pending candidates. Bound to nothing in the view yet; it is the
    /// path every refresh takes (a scan finishing, an accepted merge, a retarget).</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        Load();
        if (PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Spec 12.9's accept: opens the merge preview for this candidate and reloads the list when a
    /// merge happened. Which merge shape the dialog performs is Task 4's decision (questions.md
    /// Q18).
    /// </summary>
    [RelayCommand]
    private async Task AcceptAsync(MergeCandidateRowViewModel row)
    {
        MergeSummary = null;
        try
        {
            // Not wrapped in Task.Run: the dialog is a window, and a window is opened on the UI
            // thread. Task 4's delegate completes when the modal closes.
            var summary = await _openMerge(row.Row).ConfigureAwait(false);
            if (summary is not null)
            {
                _post(() =>
                {
                    // Spec 12.9's "Confirm ... reports the counts", on the surface that opened the
                    // dialog, because the dialog itself closes as soon as it has them.
                    MergeSummary = summary;

                    // The merge just wrote a manifest, so the history region below is stale too,
                    // and a merge that absorbed an unresolved name took that name and its frames
                    // out of the unresolved list.
                    ReloadRegions();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was dismissed, or the tab went away underneath it.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The merge dialog for candidate {CandidateId} failed", row.Id);
        }
    }

    /// <summary>Spec 12.9's dismiss: sets status <c>dismissed</c> and drops the row. A dismiss
    /// that wrote nothing (the row was already resolved elsewhere) leaves the row alone.</summary>
    [RelayCommand]
    private async Task DismissAsync(MergeCandidateRowViewModel row)
    {
        try
        {
            var dismissed = await Task
                .Run(() => _dismiss(row.Id), _lifetime.Token)
                .ConfigureAwait(false);
            if (!dismissed)
            {
                return;
            }

            _post(() =>
            {
                if (Candidates.Remove(row))
                {
                    OnPropertyChanged(nameof(ShowNoCandidates));
                }
            });
        }
        catch (OperationCanceledException)
        {
            // The tab was disposed while the write was in flight.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dismissing candidate {CandidateId} failed", row.Id);
        }
    }

    /// <summary>Spec 12.9's edit target: opens the target search for this row. The dropdown is
    /// shared by every row, because one row is being re-targeted at a time.</summary>
    [RelayCommand]
    private void BeginRetarget(MergeCandidateRowViewModel row)
    {
        RetargetingRow = row;
        RetargetSearchText = "";
        RetargetResults.Clear();
    }

    /// <summary>
    /// Points the candidate at the chosen target and reloads. The row stays pending; only the
    /// suggestion changes (questions.md Q17).
    /// </summary>
    /// <remarks>
    /// An unresolved <c>OBJECT</c> result cannot be chosen: there is no target id to point at.
    /// The command's <c>CanExecute</c> is what disables it in the dropdown, so the rule lives in
    /// one place rather than in a per-row visibility binding.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanChooseRetarget))]
    private async Task ChooseRetargetAsync(SearchResultViewModel result)
    {
        if (RetargetingRow is not { } row || result.TargetId is not { } targetId)
        {
            return;
        }

        try
        {
            var retargeted = await Task
                .Run(() => _retarget(row.Id, targetId), _lifetime.Token)
                .ConfigureAwait(false);

            _post(() =>
            {
                CancelRetarget();
                if (retargeted)
                {
                    Load();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // The tab was disposed while the write was in flight.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-pointing candidate {CandidateId} failed", row.Id);
        }
    }

    /// <summary>
    /// The candidate list and the two regions a write below it can invalidate, re-read together.
    /// An accepted merge and spec 9.7's manual create (PAR-001) both leave exactly these three
    /// stale, so they share one reload rather than each spelling out the same three calls.
    /// </summary>
    internal void ReloadRegions()
    {
        Load();
        MergeHistory?.Reload();
        UnresolvedNames?.Reload();
    }

    private static bool CanChooseRetarget(SearchResultViewModel result)
        => result is { IsUnresolved: false };

    /// <summary>Closes the edit-target dropdown.</summary>
    [RelayCommand]
    private void CancelRetarget()
    {
        RetargetingRow = null;
        RetargetSearchText = "";
        RetargetResults.Clear();
    }

    // Typing in the dropdown. Debounced on the dashboard's own window rather than a second
    // figure, and ignored entirely while no row is being re-targeted, which is also what keeps
    // the constructor's initial assignment from opening a window.
    partial void OnRetargetSearchTextChanged(string value)
    {
        if (_disposed || RetargetingRow is null)
        {
            return;
        }

        PendingSearch = _searchWindow.Restart((generation, token) => RunSearchAsync(generation, value, token));
    }

    private async Task RunSearchAsync(int generation, string term, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                // An emptied box closes the dropdown immediately; there is nothing to wait for.
                _post(() => PublishResults(generation, []));
                return;
            }

            await _searchWindow.Wait(cancellationToken).ConfigureAwait(false);

            var results = await Task
                .Run(() => _searchTargets(term), cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // The order is the query's, unchanged: this surfaces the ranking, it does not re-rank.
            _post(() => PublishResults(
                generation,
                [.. results.Select(result => new SearchResultViewModel(result))]));
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this window.
        }
        catch (Exception ex)
        {
            // A failed search leaves the dropdown as it was; it must not take the tab down.
            _logger.LogWarning(ex, "The edit-target search failed");
        }
    }

    private void PublishResults(int generation, IReadOnlyList<SearchResultViewModel> results)
    {
        if (!_searchWindow.IsCurrent(generation) || _disposed)
        {
            return;
        }

        RetargetResults.Clear();
        foreach (var result in results)
        {
            RetargetResults.Add(result);
        }
    }

    // The dedup pass runs at the end of a scan (Task 1), so the list a scan just rewrote is
    // re-read here. ScanStatusService has already marshalled onto the UI thread.
    private void OnScanFinished(object? sender, EventArgs e) => Load();

    // An undo reverts the merge's candidate rows to pending (spec 9.7), so they belong back in
    // the list above, and it puts an unresolved name's frames back where they were, so that list
    // is stale too. Raised on the UI thread by the history's own post seam.
    private void OnMergeUndone(object? sender, EventArgs e)
    {
        Load();
        UnresolvedNames?.Reload();
    }

    // The same shape TargetDetailViewModel.Load uses: a generation counter plus the tab-lifetime
    // token, and the read itself on a background thread because MergeCandidateQuery.Pending is a
    // synchronous SQLite read and this tab is constructed on the UI thread.
    private void Load()
    {
        if (_disposed)
        {
            return;
        }

        IsLoading = true;
        var generation = ++_generation;
        var token = _lifetime.Token;
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    var rows = _pending();
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, rows));
                }
                catch (OperationCanceledException)
                {
                    // The tab went away while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the merge candidate list failed");
                    _post(() => Publish(generation, null));
                }
            },
            token);
    }

    // Runs on the UI thread, through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Publish(int generation, IReadOnlyList<MergeCandidateRow>? rows)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (rows is not null)
        {
            Candidates.Clear();
            foreach (var row in rows)
            {
                Candidates.Add(new MergeCandidateRowViewModel(row));
            }

            _hasLoaded = true;
        }

        LoadFailed = rows is null;
        IsLoading = false;
        OnPropertyChanged(nameof(ShowNoCandidates));
    }

    /// <summary>
    /// The tab is owned by <see cref="SettingsViewModel"/>, which is a DI singleton, so the host
    /// owns its lifetime. Cancels the tab-lifetime source every background window is linked to,
    /// then drops the scan subscription, so nothing it started can publish into a tab whose
    /// database is gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        if (_scanStatus is not null)
        {
            _scanStatus.ScanFinished -= OnScanFinished;
        }

        if (MergeHistory is not null)
        {
            MergeHistory.Undone -= OnMergeUndone;
            MergeHistory.Dispose();
        }

        _searchWindow.Dispose();
        _lifetime.Dispose();
    }
}
