using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Merge;

/// <summary>Spec 12.9's merge history list, with its undo.</summary>
/// <remarks>
/// <para>
/// One type renders on both surfaces spec 12.9 names: the Settings Targets tab shows every
/// manifest, Target detail shows the manifests whose winner is that target. That is the second
/// occurrence of the same list, so it is one view-model over two <c>load</c> delegates rather
/// than a copy (design-lessons rule 1); two lists that undo differently is the failure this
/// prevents.
/// </para>
/// <para>
/// No <c>ScanStatusService</c> subscription: a scan does not write a <c>merge_manifests</c> row.
/// Each host reloads this list on the events that do change it (a merge it opened, an undo it
/// raised), and the Settings tab's own scan subscription (Task 3) covers its candidate list.
/// </para>
/// </remarks>
public sealed partial class MergeHistoryViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<MergeHistoryRow>> _load;
    private readonly Func<Guid, UnmergeResult> _unmerge;
    private readonly Func<Guid, UnmergeResult> _undoUnresolved;
    private readonly TimeZoneInfo _zone;
    private readonly bool _use24Hour;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // One list-lifetime source every background window is linked to, the shape
    // DashboardViewModel established: disposing the host cancels a load that is still parked.
    private readonly CancellationTokenSource _lifetime = new();

    // Only the newest load may write to the bindings. The token alone is not enough: a load that
    // already finished can still be sitting in the dispatcher queue when a newer one is
    // requested, and posting it then would show a stale list.
    private int _generation;

    private bool _hasLoaded;
    private bool _disposed;

    // One undo at a time. Written and read on the UI thread only, which is where every command
    // execution and every posted publish runs, so it needs no interlock.
    private bool _undoing;

    /// <param name="load">Normally <c>MergeHistoryQuery.All</c> in Settings and a lambda over
    /// <c>MergeHistoryQuery.ForWinner</c> on Target detail.</param>
    /// <param name="unmerge">Normally <c>MergeRepository.Unmerge</c>, keyed on the loser target
    /// id.</param>
    /// <param name="undoUnresolved">Normally <c>MergeRepository.UndoUnresolvedNameMerge</c>,
    /// keyed on the manifest id, because that shape has no loser target to name.</param>
    /// <param name="general">For the merge time's rendering, by value. The same
    /// <see cref="GeneralSettings"/> shape every other view-model takes.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load or undo is logged, never rethrown on the UI
    /// thread.</param>
    public MergeHistoryViewModel(
        Func<IReadOnlyList<MergeHistoryRow>> load,
        Func<Guid, UnmergeResult> unmerge,
        Func<Guid, UnmergeResult> undoUnresolved,
        GeneralSettings general,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _load = load;
        _unmerge = unmerge;
        _undoUnresolved = undoUnresolved;
        _zone = SessionTimeFormat.Resolve(general.DisplayTimezoneId);
        _use24Hour = general.Use24HTime;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        Load();
    }

    /// <summary>The history, newest merge first, as the query ordered it.</summary>
    public ObservableCollection<MergeHistoryRowViewModel> Rows { get; } = [];

    /// <summary>No spec empty-state string exists for this list (spec 12.10 names the candidate
    /// and unresolved lists only), so it reads "No merges recorded." on both surfaces (ruling
    /// Q19) and questions.md Q20 asks for the spec line. True only once a load has completed, so
    /// the empty state does not flash before the rows arrive.</summary>
    public bool ShowEmptyState => _hasLoaded && Rows.Count == 0;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The last undo's outcome as one sentence, or null. A failure leaves the row in
    /// place, because the manifest is still there.</summary>
    [ObservableProperty]
    public partial string? LastOutcome { get; private set; }

    /// <summary>A load that threw, so the section renders one neutral line instead of a bare
    /// header (the rule Task 3's review applied to the candidate list). Carries no text from the
    /// exception; the log has that. Cleared by the next successful load.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>Raised after an undo succeeded, so the host page can reload whatever it shows
    /// beside this list. Target detail answers it by reloading itself; the Settings tab by
    /// reloading the candidate list, because an undo puts candidates back to pending.</summary>
    public event EventHandler? Undone;

    /// <summary>The in-flight load, so a test can await it instead of sleeping. Mirrors
    /// <c>TargetDetailViewModel.PendingLoad</c>.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>Re-reads the history. The path every host-driven refresh takes (a merge the host
    /// just confirmed, a page reload).</summary>
    internal void Reload() => Load();

    /// <summary>Re-reads the history and completes with that read, so the command settles with
    /// the load rather than before it (the shape Task 3's <c>ReloadAsync</c> established).
    /// </summary>
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
    /// Spec 12.9's undo: consumes the manifest and restores the loser. A target merge is undone
    /// by loser id, an unresolved-name merge by manifest id, because that shape has no loser
    /// target row to name.
    /// </summary>
    /// <remarks>
    /// The re-entrancy guard is in the body, not a <c>CanExecute</c>: <c>RelayCommand.Execute</c>
    /// runs regardless of <c>CanExecute</c>, so a second call arriving before the first
    /// <see cref="Task.Run(Action)"/> completes would issue a second unmerge whose
    /// <c>NotMerged</c> result then overwrites the first one's success sentence. One undo at a
    /// time is the whole rule; the list is small and the write is short.
    /// </remarks>
    [RelayCommand]
    private async Task UndoAsync(MergeHistoryRowViewModel row)
    {
        if (_undoing)
        {
            return;
        }

        _undoing = true;
        try
        {
            var result = await Task
                .Run(
                    () => row.LoserId is { } loserId ? _unmerge(loserId) : _undoUnresolved(row.ManifestId),
                    _lifetime.Token)
                .ConfigureAwait(false);

            _post(() => ApplyUndo(row, result));
        }
        catch (OperationCanceledException)
        {
            // The host went away while the write was in flight.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Undoing merge manifest {ManifestId} failed", row.ManifestId);
            _post(() => LastOutcome = "The undo could not be completed. See the log for details.");
        }
        finally
        {
            _undoing = false;
        }
    }

    // Runs on the UI thread through the post seam.
    private void ApplyUndo(MergeHistoryRowViewModel row, UnmergeResult result)
    {
        if (_disposed)
        {
            return;
        }

        switch (result.Status)
        {
            case UnmergeStatus.Unmerged:
                Remove(row);
                LastOutcome =
                    $"Undid the merge of {row.LoserName}: restored {Plural(result.FramesRestored, "frame")} "
                    + $"and {Plural(result.NotesRestored, "note")}.";
                Undone?.Invoke(this, EventArgs.Empty);
                break;

            // Not an error the user caused: the merge was already undone somewhere else, so the
            // row is stale rather than wrong. It goes, and the list says so.
            case UnmergeStatus.NotMerged:
                Remove(row);
                LastOutcome = "Already undone.";
                break;

            case UnmergeStatus.NotFound:
                LastOutcome = "That target no longer exists.";
                break;

            case UnmergeStatus.NoManifest:
                LastOutcome = "No merge record remains for that merge, so nothing was restored.";
                break;

            // F17: chained merges undo in reverse order only. The row stays, because this undo is
            // still available once the later one is undone.
            case UnmergeStatus.WinnerMergedAway:
                LastOutcome =
                    $"Undo the merge of \"{row.WinnerName}\" into \"{result.WinnerMergedIntoName}\" first.";
                break;

            default:
                LastOutcome = "That merge record could not be read, so nothing was restored.";
                break;
        }
    }

    private void Remove(MergeHistoryRowViewModel row)
    {
        if (Rows.Remove(row))
        {
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private static string Plural(int count, string noun)
        => count == 1
            ? "1 " + noun
            : count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + "s";

    // The same shape TargetDetailViewModel.Load uses: a generation counter plus the lifetime
    // token, and the read itself on a background thread because MergeHistoryQuery is a
    // synchronous SQLite read and both hosts construct this on the UI thread.
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
                    var rows = _load();
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, rows));
                }
                catch (OperationCanceledException)
                {
                    // The host went away while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the merge history failed");
                    _post(() => Publish(generation, null));
                }
            },
            token);
    }

    // Runs on the UI thread, through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Publish(int generation, IReadOnlyList<MergeHistoryRow>? rows)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (rows is not null)
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(new MergeHistoryRowViewModel(row, _zone, _use24Hour));
            }

            _hasLoaded = true;
        }

        LoadFailed = rows is null;
        IsLoading = false;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Cancels the lifetime source every background window is linked to, so nothing this
    /// list started can publish into a host whose database is gone.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
