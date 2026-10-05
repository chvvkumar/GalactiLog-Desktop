using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Merge;

/// <summary>What the dialog was opened for.</summary>
/// <param name="WinnerId">The proposed winner. Null when a candidate carries no suggestion at
/// all, in which case the search box chooses the winner.</param>
/// <param name="LoserId">A target loser (a Pass 2 candidate, or Target detail's merge
/// action).</param>
/// <param name="LoserName">An unresolved <c>OBJECT</c> string loser (a Pass 1 candidate). Exactly
/// one of <see cref="LoserId"/> and <see cref="LoserName"/> is set, or neither when the dialog
/// opens from Target detail with nothing chosen yet.</param>
/// <param name="CandidateId">The <c>merge_candidates</c> row this dialog was opened from, so a
/// confirm can report which suggestion it satisfied. Null from Target detail.</param>
public sealed record MergeRequest(Guid? WinnerId, Guid? LoserId, string? LoserName, Guid? CandidateId);

/// <summary>
/// Spec 12.9's merge preview modal: the fuzzy alias-aware target search box, the side-by-side
/// comparison, the colliding session dates with the note-merge behaviour stated, the swap, the
/// what-will-happen block, and confirm and cancel.
/// </summary>
/// <remarks>
/// Every collaborator arrives as a delegate rather than as a query or a repository object, the
/// rule <c>DashboardViewModel</c>, <c>TargetDetailViewModel</c> and <c>TargetsTabViewModel</c>
/// all follow, so the dialog constructs in a unit test with lambdas and no database
/// (design-spec 18.3). Every read and every write runs off the UI thread through
/// <c>Task.Run</c> and publishes through the <c>post</c> seam; a response older than the newest
/// request is dropped by the generation counter rather than overwriting it.
/// <para>
/// Cancel writes nothing: there is no code path from <see cref="CancelCommand"/> to the merge
/// delegate, which is what the roadmap's Verify line asserts.
/// </para>
/// </remarks>
public sealed partial class MergeDialogViewModel : ObservableObject, IModalPageViewModel, IDisposable
{
    private readonly Func<Guid, Guid?, string?, MergePreview?> _preview;
    private readonly Func<string, IReadOnlyList<TargetSearchResult>> _searchTargets;
    private readonly Func<Guid, Guid, MergeResult> _merge;
    private readonly Func<Guid, string, MergeResult> _mergeUnresolved;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // One dialog-lifetime source every background window is linked to, the shape
    // DashboardViewModel established: disposing the dialog cancels a read still in flight.
    private readonly CancellationTokenSource _lifetime = new();

    // FIXER LIST F11: the target search window, on the shared Debouncer rather than a fourth
    // hand-rolled copy of cancel-previous, link-to-lifetime, bump-generation, await-the-window.
    private readonly Debouncer _searchWindow;

    // Only the newest preview may write to the bindings; the search has the window own its own.
    private int _generation;

    private Guid? _winnerId;
    private Guid? _loserId;
    private string? _loserName;
    private bool _disposed;

    /// <param name="request">What the dialog was opened for.</param>
    /// <param name="preview">Normally <c>MergePreviewQuery.Get</c>.</param>
    /// <param name="searchTargets">Normally <c>TargetSearchQuery.Search</c>: spec 12.9's "same
    /// fuzzy, alias-aware search as the dashboard", reached through the same query and rendered
    /// with the same <see cref="SearchResultViewModel"/>, so the two dropdowns cannot
    /// drift.</param>
    /// <param name="merge">Normally <c>MergeRepository.Merge</c>.</param>
    /// <param name="mergeUnresolved">Normally
    /// <c>MergeRepository.MergeUnresolvedName</c>.</param>
    /// <param name="delay">The search debounce seam, so a test does not sleep.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed preview, search or merge is logged and surfaced on
    /// the dialog, never rethrown on the UI thread.</param>
    public MergeDialogViewModel(
        MergeRequest request,
        Func<Guid, Guid?, string?, MergePreview?> preview,
        Func<string, IReadOnlyList<TargetSearchResult>> searchTargets,
        Func<Guid, Guid, MergeResult> merge,
        Func<Guid, string, MergeResult> mergeUnresolved,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        Request = request;
        _preview = preview;
        _searchTargets = searchTargets;
        _merge = merge;
        _mergeUnresolved = mergeUnresolved;
        _searchWindow = new Debouncer(_lifetime.Token, delay ?? Task.Delay, DashboardViewModel.DebounceWindow);
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        _winnerId = request.WinnerId;
        _loserId = request.LoserId;
        _loserName = request.LoserName;

        // Target detail opens the dialog with the current target as winner and the box choosing
        // the loser. A candidate with no suggestion at all has no winner, so the box chooses that
        // instead.
        SearchChoosesWinner = request.WinnerId is null;

        // Assigned before anything can observe it; the change handler ignores an empty box.
        SearchText = "";

        ReloadPreview();
    }

    /// <summary>What the dialog was opened for, kept so Task 5's caller can correlate a confirmed
    /// merge with the suggestion it satisfied.</summary>
    public MergeRequest Request { get; }

    /// <summary>Spec 12.9's search box. Debounced on
    /// <c>DashboardViewModel.DebounceWindow</c>.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>The dropdown's rows, in the query's ranking. Nothing here re-ranks or
    /// filters.</summary>
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    /// <summary>Which side the search box is choosing. Target detail opens the dialog with the
    /// current target as winner and the box choosing the loser; a candidate opens it with both
    /// sides known and the box able to replace either.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    public partial bool SearchChoosesWinner { get; set; }

    /// <summary>The surviving side. Null while the first preview is in flight, and after a
    /// preview that found nothing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwapCommand))]
    public partial MergeSideViewModel? Winner { get; private set; }

    /// <summary>The side that is merged away. Null until one is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoserFateText))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwapCommand))]
    public partial MergeSideViewModel? Loser { get; private set; }

    /// <summary>Spec 12.9's colliding session dates, formatted with the same
    /// <c>MetricText.Date</c> rendering the session cards use.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollidingSessions))]
    public partial IReadOnlyList<string> CollidingSessionDates { get; private set; } = [];

    public bool HasCollidingSessions => CollidingSessionDates.Count > 0;

    /// <summary>The sentence spec 12.9 asks the list to carry, stating section 9.7's rule exactly
    /// once, with the marker shown literally and the loser's own name inside it.</summary>
    [ObservableProperty]
    public partial string NoteMergeExplanation { get; private set; } = "";

    /// <summary>The strings the winner's aliases array would gain, in the order the merge would
    /// add them. Computed by <c>MergeRepository.AbsorbAliases</c>, so the dialog cannot promise an
    /// alias the merge does not add.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAliasesToAdd), nameof(AliasesToAddText))]
    public partial IReadOnlyList<string> AliasesToAdd { get; private set; } = [];

    public bool HasAliasesToAdd => AliasesToAdd.Count > 0;

    public string AliasesToAddText => string.Join(", ", AliasesToAdd);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FramesToMoveText))]
    public partial int FramesToMove { get; private set; }

    public string FramesToMoveText => MetricText.Count(FramesToMove);

    /// <summary>Spec 12.9's "the loser's fate": the loser row is never deleted, only marked, so an
    /// unmerge can bring it back.</summary>
    public string LoserFateText => Loser is { IsUnresolvedName: true } loser
        ? $"\"{loser.Name}\" becomes an alias of the winner and its frames are assigned to it."
        : Loser is { } target
            ? $"\"{target.Name}\" is kept as a merged-away target, so the merge can be undone."
            : "";

    /// <summary>The counts confirm reported, shown before the dialog closes.</summary>
    [ObservableProperty]
    public partial string? ConfirmSummary { get; private set; }

    /// <summary>A preview that found nothing, or a merge that did not happen. Disables confirm
    /// until a side changes.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string? ErrorText { get; private set; }

    /// <summary>True while any read or write the dialog started is in flight: the preview reads
    /// and the confirm write alike. Drives the busy indicator and greys confirm and swap. It does
    /// NOT gate cancelling (Phase 7 fixer item 2): a preview read is abandonable, so greying
    /// Cancel on it left the user with a dialog they could neither confirm nor close for the
    /// length of every preview, including a failing one.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwapCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>True only between <c>Confirm</c> and <c>PublishMerge</c>, the window in which a
    /// transaction is committing. The one thing Cancel and the window's <c>Closing</c> refuse on;
    /// nothing else gates on it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsWriting { get; private set; }

    /// <summary>True when the merge was performed. The window's result.</summary>
    public bool Merged { get; private set; }

    /// <summary>Raised when the dialog wants its window closed. The window owns closing, the same
    /// split <c>TargetDetailViewModel.BackRequested</c> uses.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The in-flight preview, so a test can await it instead of sleeping.</summary>
    internal Task? PendingPreview { get; private set; }

    /// <summary>The in-flight debounce-and-search, so a test can await it instead of
    /// sleeping.</summary>
    internal Task? PendingSearch { get; private set; }

    /// <summary>Exchanges the two sides and re-previews. Disabled for an unresolved-name loser,
    /// which has no <c>targets</c> row and therefore cannot be a winner.</summary>
    [RelayCommand(CanExecute = nameof(CanSwap))]
    private void Swap()
    {
        // The guard is in the body, not only in CanExecute (F7): ICommand.Execute does not consult
        // CanExecute, so a caller invoking the command directly would otherwise swap while a
        // preview or a merge write is in flight, and the reload below would race the response the
        // generation guard is counting. CanSwap is what greys the button; this enforces the rule.
        if (!CanSwap() || _winnerId is not { } winner || _loserId is not { } loser)
        {
            return;
        }

        _winnerId = loser;
        _loserId = winner;
        ReloadPreview();
    }

    private bool CanSwap() => _winnerId is not null && _loserId is not null && !IsBusy;

    /// <summary>Replaces whichever side <see cref="SearchChoosesWinner"/> names, and
    /// re-previews.</summary>
    /// <remarks>
    /// An unresolved <c>OBJECT</c> result may be chosen only as the loser: it has no
    /// <c>targets</c> row to absorb another target into. The command's <c>CanExecute</c> is what
    /// disables it in the dropdown, so the rule lives in one place rather than in a per-row
    /// visibility binding, which is what <c>TargetsTabViewModel.ChooseRetarget</c> already does.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task ChooseAsync(SearchResultViewModel result)
    {
        if (!CanChoose(result))
        {
            return;
        }

        if (SearchChoosesWinner)
        {
            _winnerId = result.TargetId;
        }
        else if (result.TargetId is { } loserId)
        {
            _loserId = loserId;
            _loserName = null;
        }
        else
        {
            _loserId = null;
            _loserName = result.UnresolvedObject ?? result.DisplayName;
        }

        SearchText = "";
        SearchResults.Clear();
        ReloadPreview();

        if (PendingPreview is { } preview)
        {
            await preview.ConfigureAwait(false);
        }
    }

    private bool CanChoose(SearchResultViewModel result)
        => result is not null && (!SearchChoosesWinner || result.TargetId is not null);

    /// <summary>Spec 12.9's confirm: writes the manifest, moves the frames and reports the
    /// counts. The write itself is <c>MergeRepository</c>'s; nothing here writes a
    /// manifest.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (_winnerId is not { } winnerId || !CanConfirm())
        {
            return;
        }

        var loserId = _loserId;
        var loserName = _loserName;

        IsBusy = true;
        IsWriting = true;
        ConfirmSummary = null;
        try
        {
            var result = await Task
                .Run(
                    () => loserId is { } loser
                        ? _merge(winnerId, loser)
                        : _mergeUnresolved(winnerId, loserName!),
                    _lifetime.Token)
                .ConfigureAwait(false);

            _post(() => PublishMerge(result));
        }
        catch (OperationCanceledException)
        {
            // The dialog went away while the write was in flight.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The merge failed");
            _post(() =>
            {
                IsBusy = false;
                IsWriting = false;
                ErrorText = "The merge failed. Nothing was written.";
            });
        }
    }

    private bool CanConfirm()
        => Winner is not null
            && Loser is not null
            && !IsBusy
            && ErrorText is null
            && _winnerId is { } winnerId
            && (_loserId is not { } loserId || loserId != winnerId);

    /// <summary>Closes the dialog without writing anything: there is no path from here to the
    /// merge delegate, to a manifest, or to a candidate status change.</summary>
    /// <remarks>
    /// Disabled while a confirm is in flight (review finding 2), and only then (Phase 7 fixer
    /// item 2: the flag used to be <see cref="IsBusy"/>, which covers the preview reads too).
    /// Cancelling during the write would close the dialog reporting <see cref="Merged"/> false
    /// while the transaction commits anyway, so the caller would never reload and the user would
    /// see a merge that "did not happen". A preview read carries no such risk: it writes nothing,
    /// and the token it runs under is cancelled by <c>Dispose</c>. <c>MergeDialogWindow</c>
    /// refuses its own <c>Closing</c> on the same flag, so the title-bar close cannot get around
    /// this: during a write the only exit is the merge's own close.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        // The guard is in the body, not only in CanExecute: ICommand.Execute does not consult
        // CanExecute, so a caller that invokes the command directly would otherwise walk straight
        // past it. CanCancel is what greys the button; this is what enforces the rule.
        if (!CanCancel())
        {
            return;
        }

        CloseRequested?.Invoke(this, false);
    }

    private bool CanCancel() => !IsWriting;

    // Runs on the UI thread through the post seam.
    private void PublishMerge(MergeResult result)
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = false;
        IsWriting = false;

        if (result.Status != MergeStatus.Merged)
        {
            ErrorText = result.Status switch
            {
                MergeStatus.WinnerNotFound => "The surviving target no longer exists.",
                MergeStatus.LoserNotFound => "The target being merged away no longer exists.",
                MergeStatus.SameTarget => "A target cannot be merged into itself.",
                MergeStatus.AlreadyMerged => "That target has already been merged away.",
                _ => "The merge did not happen.",
            };
            return;
        }

        // Spec 12.9: "Confirm ... reports the counts."
        ConfirmSummary =
            $"Moved {MetricText.Count(result.FramesMoved)} frames, "
            + $"added {MetricText.Count(result.AliasesAdded.Count)} aliases, "
            + $"merged {MetricText.Count(result.NotesRekeyed + result.NotesAppended)} notes.";
        Merged = true;
        CloseRequested?.Invoke(this, true);
    }

    // Typing in the search box. Debounced on the dashboard's own window rather than a second
    // figure, which is also what keeps the constructor's initial assignment from querying.
    partial void OnSearchTextChanged(string value)
    {
        if (_disposed)
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
            // A failed search leaves the dropdown as it was; it must not take the dialog down.
            _logger.LogWarning(ex, "The merge dialog's target search failed");
        }
    }

    private void PublishResults(int generation, IReadOnlyList<SearchResultViewModel> results)
    {
        if (!_searchWindow.IsCurrent(generation) || _disposed)
        {
            return;
        }

        SearchResults.Clear();
        foreach (var result in results)
        {
            SearchResults.Add(result);
        }
    }

    // MergePreviewQuery.Get is a synchronous SQLite read and the dialog is constructed on the UI
    // thread, so it runs on the pool and publishes through the post seam.
    private void ReloadPreview()
    {
        if (_disposed || _winnerId is not { } winnerId)
        {
            // Nothing to preview until the search box names a winner.
            Winner = null;
            Loser = null;
            ErrorText = null;
            return;
        }

        ErrorText = null;
        ConfirmSummary = null;
        IsBusy = true;
        SwapCommand.NotifyCanExecuteChanged();

        var generation = ++_generation;
        var loserId = _loserId;
        var loserName = _loserName;
        var token = _lifetime.Token;
        PendingPreview = Task.Run(
            () =>
            {
                try
                {
                    var preview = _preview(winnerId, loserId, loserName);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => PublishPreview(generation, preview, failed: false));
                }
                catch (OperationCanceledException)
                {
                    // The dialog was disposed while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the merge preview failed");
                    _post(() => PublishPreview(generation, null, failed: true));
                }
            },
            token);
    }

    // Runs on the UI thread, through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void PublishPreview(int generation, MergePreview? preview, bool failed)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        IsBusy = false;

        if (preview is null)
        {
            Winner = null;
            Loser = null;
            CollidingSessionDates = [];
            NoteMergeExplanation = "";
            AliasesToAdd = [];
            FramesToMove = 0;
            ErrorText = failed
                ? "The merge preview could not be loaded."
                : "One of these targets no longer exists or has already been merged away.";
            return;
        }

        Winner = new MergeSideViewModel(preview.Winner);
        Loser = preview.Loser is { } loser ? new MergeSideViewModel(loser) : null;
        FramesToMove = preview.FramesToMove;
        AliasesToAdd = preview.AliasesToAdd;

        // MetricText.Date is the rendering the session cards already use for a session_date.
        // Spec 5.8.1: general.timezone is display formatting for an instant and never touches a
        // session_date, so there is no second date renderer on this screen family.
        CollidingSessionDates = [.. preview.CollidingSessionDates.Select(MetricText.Date)];
        NoteMergeExplanation = CollidingSessionDates.Count == 0
            ? ""
            : "On these dates the winner already has a note. The loser's text is appended after a "
                + $"'--- merged from {Loser?.Name} ---' marker, and the loser's own note is kept "
                + "so it reappears if you undo the merge.";
    }

    /// <summary>
    /// Cancels the dialog's background work. The window owns closing; this owns only what it
    /// started.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _searchWindow.Dispose();
        _lifetime.Dispose();
    }
}
