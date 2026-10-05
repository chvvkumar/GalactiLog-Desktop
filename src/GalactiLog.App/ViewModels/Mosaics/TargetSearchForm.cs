using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// The target search spec 12.17's two mosaic forms share: the add panel form and a panel's Add
/// nights from any target. A box that queries <c>TargetSearchQuery</c> with the dashboard's own
/// debounce once two characters are typed, the resolved results under it, and Choose, which hands
/// the picked result to <see cref="OnChosen"/>.
/// </summary>
/// <remarks>Built at the second occurrence of the pattern (design lesson 1): each form adds only
/// what it does with a pick.</remarks>
public abstract partial class TargetSearchForm : ObservableObject, IDisposable
{
    private readonly Func<string, IReadOnlyList<TargetSearchResult>> _search;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Debouncer _searchWindow;

    /// <param name="search">Normally <c>TargetSearchQuery.Search</c>.</param>
    /// <param name="delay">The debounce seam; the dashboard's own window.</param>
    /// <param name="post">How to reach the UI thread.</param>
    /// <param name="logger">A failed search is logged and leaves the list as it was.</param>
    protected TargetSearchForm(
        Func<string, IReadOnlyList<TargetSearchResult>> search,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Action<Action>? post,
        ILogger? logger)
    {
        _search = search;
        Post = post ?? UiPost.Default;
        Logger = logger ?? NullLogger.Instance;
        _searchWindow = new Debouncer(_lifetime.Token, delay ?? Task.Delay, DashboardViewModel.DebounceWindow);
        SearchText = "";
    }

    /// <summary>How to reach the UI thread.</summary>
    protected Action<Action> Post { get; }

    /// <summary>Where a failed search or write is logged.</summary>
    protected ILogger Logger { get; }

    /// <summary>True once <see cref="Dispose()"/> has run.</summary>
    protected bool IsDisposed { get; private set; }

    /// <summary>The search box, watermarked "Search targets".</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>The results under the box: resolved targets only, since both forms need one.</summary>
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    /// <summary>The last search, so a test awaits it rather than sleeping.</summary>
    internal Task? PendingSearch { get; private set; }

    /// <summary>What the form does with a picked target.</summary>
    protected abstract void OnChosen(SearchResultViewModel result);

    /// <summary>Called on every edit of the box, before its search starts.</summary>
    protected virtual void OnSearchEdited()
    {
    }

    [RelayCommand]
    private void Choose(SearchResultViewModel? result)
    {
        if (result?.TargetId is null || IsDisposed)
        {
            return;
        }

        SearchResults.Clear();
        OnChosen(result);
    }

    // Spec 12.17: the query runs with the search's own debounce once two characters are typed.
    partial void OnSearchTextChanged(string value)
    {
        if (IsDisposed)
        {
            return;
        }

        OnSearchEdited();
        PendingSearch = _searchWindow.Restart((generation, token) => RunSearchAsync(generation, value, token));
    }

    private async Task RunSearchAsync(int generation, string term, CancellationToken cancellationToken)
    {
        try
        {
            if (term.Trim().Length < 2)
            {
                Post(() => Publish(generation, []));
                return;
            }

            await _searchWindow.Wait(cancellationToken).ConfigureAwait(false);
            var results = await Task.Run(() => _search(term.Trim()), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Post(() => Publish(
                generation,
                [.. results.Where(result => result.TargetId is not null).Select(result => new SearchResultViewModel(result))]));
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this window.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "A mosaic form's target search failed");
        }
    }

    private void Publish(int generation, IReadOnlyList<SearchResultViewModel> results)
    {
        if (IsDisposed || !_searchWindow.IsCurrent(generation))
        {
            return;
        }

        SearchResults.Clear();
        foreach (var result in results)
        {
            SearchResults.Add(result);
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _searchWindow.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
