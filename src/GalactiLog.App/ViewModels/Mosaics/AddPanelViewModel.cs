using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// Spec 12.17's add panel form, shared by an expanded Mosaics table row and the mosaic detail
/// page's header: a debounced target search, a panel label prefilled with the first free
/// "Panel n", and Add, which puts the chosen target's nights into the panel of that label.
/// </summary>
public sealed partial class AddPanelViewModel : ObservableObject, IDisposable
{
    private readonly Guid _mosaicId;
    private readonly Func<string, IReadOnlyList<TargetSearchResult>> _search;
    private readonly Func<Guid, Guid, string, PanelAddResult> _add;
    private readonly Action _added;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Debouncer _searchWindow;
    private bool _disposed;

    /// <param name="mosaicId">The mosaic the panel goes into.</param>
    /// <param name="labels">The mosaic's panel labels, for the prefill.</param>
    /// <param name="search">Normally <c>TargetSearchQuery.Search</c>.</param>
    /// <param name="add">Normally <c>MosaicRepository.AddPanelWithTarget</c>.</param>
    /// <param name="added">Called on the UI thread after a successful Add, so the host re-reads the
    /// mosaic and hands the new labels back through <see cref="SetLabels"/>.</param>
    /// <param name="delay">The debounce seam; the dashboard's own window.</param>
    /// <param name="post">How to reach the UI thread.</param>
    /// <param name="logger">A failed search is logged and leaves the list as it was.</param>
    public AddPanelViewModel(
        Guid mosaicId,
        IReadOnlyList<string> labels,
        Func<string, IReadOnlyList<TargetSearchResult>> search,
        Func<Guid, Guid, string, PanelAddResult> add,
        Action added,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _mosaicId = mosaicId;
        _search = search;
        _add = add;
        _added = added;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _searchWindow = new Debouncer(_lifetime.Token, delay ?? Task.Delay, DashboardViewModel.DebounceWindow);
        Label = "";
        SearchText = "";
        SetLabels(labels);
    }

    /// <summary>The search box, watermarked "Search targets".</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>The results under the box: resolved targets only, since a panel needs one.</summary>
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = [];

    /// <summary>The chosen target, or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial SearchResultViewModel? Chosen { get; private set; }

    /// <summary>The "Panel label" box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string Label { get; set; }

    /// <summary>"n nights already in another panel were skipped.", or null.</summary>
    [ObservableProperty]
    public partial string? Caption { get; private set; }

    /// <summary>A refused or failed Add, shown inline.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The last search, so a test awaits it rather than sleeping.</summary>
    internal Task? PendingSearch { get; private set; }

    /// <summary>Replaces the mosaic's labels and re-prefills the label box.</summary>
    public void SetLabels(IReadOnlyList<string> labels) => Label = NextLabel(labels);

    /// <summary>"Panel n" for the smallest n of 1 or more that no label carries, case insensitively.</summary>
    internal static string NextLabel(IReadOnlyList<string> labels)
    {
        var taken = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (taken.Contains(string.Create(CultureInfo.InvariantCulture, $"Panel {n}")))
        {
            n++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"Panel {n}");
    }

    [RelayCommand]
    private void Choose(SearchResultViewModel? result)
    {
        if (result?.TargetId is null)
        {
            return;
        }

        Chosen = result;
        SearchResults.Clear();
    }

    private bool CanAdd() => Chosen?.TargetId is not null && !string.IsNullOrWhiteSpace(Label);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (!CanAdd() || _disposed)
        {
            return;
        }

        Caption = null;
        Error = null;
        try
        {
            var result = _add(_mosaicId, Chosen!.TargetId!.Value, Label.Trim());
            Caption = result.Skipped switch
            {
                0 => null,
                1 => "1 night already in another panel was skipped.",
                var skipped => $"{skipped} nights already in another panel were skipped.",
            };
            Chosen = null;
            SearchText = "";
            _added();
        }
        catch (MosaicWriteException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adding a panel to a mosaic failed");
            Error = MosaicMessages.CouldNotSave;
        }
    }

    // Spec 12.17: the query runs with the search's own debounce once two characters are typed.
    partial void OnSearchTextChanged(string value)
    {
        if (_disposed)
        {
            return;
        }

        // A pick belongs to the text it was made from: typing again drops it.
        Chosen = null;
        PendingSearch = _searchWindow.Restart((generation, token) => RunSearchAsync(generation, value, token));
    }

    private async Task RunSearchAsync(int generation, string term, CancellationToken cancellationToken)
    {
        try
        {
            if (term.Trim().Length < 2)
            {
                _post(() => Publish(generation, []));
                return;
            }

            await _searchWindow.Wait(cancellationToken).ConfigureAwait(false);
            var results = await Task.Run(() => _search(term.Trim()), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _post(() => Publish(
                generation,
                [.. results.Where(result => result.TargetId is not null).Select(result => new SearchResultViewModel(result))]));
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this window.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The add panel form's target search failed");
        }
    }

    private void Publish(int generation, IReadOnlyList<SearchResultViewModel> results)
    {
        if (_disposed || !_searchWindow.IsCurrent(generation))
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _searchWindow.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
