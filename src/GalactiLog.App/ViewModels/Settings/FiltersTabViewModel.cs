using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Design-spec 12.7's Filters tab: the canonical filter table (colour swatch plus alias list,
/// one <see cref="GroupingEditorViewModel"/> with <c>ShowColorPicker</c> true), the discovered
/// filter names with frame counts, suggested groupings the user can accept or dismiss, and an
/// add-a-filter row. Saving invalidates the alias map cache, through
/// <c>SettingsStore.SaveFilters</c> and nothing else (design-lessons rule 2: the event is the
/// choke point, never a direct cache poke).
/// </summary>
/// <remarks>
/// Every collaborator is a delegate, not <c>SettingsStore</c> (design-spec 18.3), so the tab
/// constructs in a unit test with lambdas and no database, the rule every Settings tab in this
/// phase follows.
/// </remarks>
public sealed partial class FiltersTabViewModel : ObservableObject, IDisposable
{
    private readonly Func<Dictionary<string, FilterSetting>> _loadFilters;
    private readonly Action<Dictionary<string, FilterSetting>> _saveFilters;
    private readonly Func<List<List<string>>> _loadDismissed;
    private readonly Action<List<List<string>>> _saveDismissed;
    private readonly Func<IReadOnlyList<(string Name, int Count)>> _discoveredFilters;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _lifetime = new();
    private int _generation;
    private bool _disposed;

    private List<List<string>> _dismissed = [];
    private IReadOnlyList<(string Name, int Count)> _lastDiscovered = [];

    /// <param name="loadFilters">Normally <c>SettingsStore.GetFilters</c>. Called off the UI
    /// thread.</param>
    /// <param name="saveFilters">Normally <c>SettingsStore.SaveFilters</c>, which raises
    /// <c>AliasSourcesChanged</c> and so invalidates <c>AliasMapCache</c>. Called off the UI
    /// thread, inside <see cref="SaveAsync"/>.</param>
    /// <param name="loadDismissed">Normally <c>SettingsStore.GetDismissedSuggestions</c>.</param>
    /// <param name="saveDismissed">Normally <c>SettingsStore.SaveDismissedSuggestions</c>, which
    /// raises nothing (matching the web, which does not invalidate the alias cache for a
    /// dismissal).</param>
    /// <param name="discoveredFilters">Normally <c>DiscoveredNamesQuery.Read(Filters)</c>, mapped
    /// to the tuple shape.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load or save is logged, never rethrown on the UI
    /// thread.</param>
    public FiltersTabViewModel(
        Func<Dictionary<string, FilterSetting>> loadFilters,
        Action<Dictionary<string, FilterSetting>> saveFilters,
        Func<List<List<string>>> loadDismissed,
        Action<List<List<string>>> saveDismissed,
        Func<IReadOnlyList<(string Name, int Count)>> discoveredFilters,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _loadFilters = loadFilters;
        _saveFilters = saveFilters;
        _loadDismissed = loadDismissed;
        _saveDismissed = saveDismissed;
        _discoveredFilters = discoveredFilters;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        Editor = new GroupingEditorViewModel(showColorPicker: true);
        Editor.RenameRefused += (_, name) => ErrorMessage = $"'{name}' is already a canonical filter name.";

        NewFilterName = "";

        Load();
    }

    /// <summary>Spec 12.7's canonical filter table and its discovered/ungrouped side (the web's
    /// <c>GroupingEditor</c> with <c>showColorPicker</c> true).</summary>
    public GroupingEditorViewModel Editor { get; }

    /// <summary>Spec 12.7's suggested groupings, in <c>SuggestionGrouper.Group</c>'s order.
    /// </summary>
    public ObservableCollection<SuggestionViewModel> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>The web's <c>SuggestionsBanner</c> heading, verbatim: "Found N possible
    /// duplicate(s)".</summary>
    public string SuggestionsHeading
        => Suggestions.Count == 1 ? "Found 1 possible duplicate" : $"Found {Suggestions.Count} possible duplicates";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>A read that threw. Spec 12.10's rule: one neutral line, never a silent empty
    /// list.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>Spec 12.10's failure line for this tab's discovered read.</summary>
    public string LoadFailedMessage => "The discovered filter names could not be loaded.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; private set; }

    public bool HasErrorMessage => ErrorMessage is not null;

    public bool HasStatusMessage => StatusMessage is not null;

    /// <summary>The add-a-filter box (the web's "New filter name").</summary>
    [ObservableProperty]
    public partial string NewFilterName { get; set; }

    /// <summary>The in-flight load, so a test awaits it instead of sleeping.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>The in-flight save, so a test awaits it instead of sleeping.</summary>
    internal Task? PendingSave { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Re-runs the suggestion computation against the current groups and the current
    /// dismissed list, with no new read. Used after a load, and by a test proving a dismissal
    /// does not reappear.</summary>
    public void Reload() => RefreshSuggestions();

    /// <summary>Core-shapes 2.9's one declaration of the filter-name union, in the shape
    /// <see cref="EquipmentTabViewModel.KnownTelescopes"/> already has: the External Tools tab
    /// reads this rather than rebuilding the union itself (ruling B2), so the two lists cannot
    /// drift.</summary>
    public IReadOnlyList<string> KnownFilters() => FilterNameUnion.From(Editor);

    /// <summary>Spec 12.7's add row: a non-blank name that is not already a canonical name,
    /// storing no colour and an empty alias list. Storing no colour is what lets the seeded
    /// category default resolve (P13 R2a): a filter added as "Ha" reads red, and the document
    /// records nothing until the user picks a colour of their own.</summary>
    [RelayCommand]
    private void AddFilter()
    {
        var name = NewFilterName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (Editor.Groups.Any(group => string.Equals(group.Canonical, name, StringComparison.Ordinal)))
        {
            ErrorMessage = $"'{name}' is already a canonical filter name.";
            return;
        }

        ErrorMessage = null;
        Editor.AddGroup(new AliasGroupViewModel(name, null, []));
        NewFilterName = "";
    }

    /// <summary>The web's "Merge": folds the suggestion's other names into the selected
    /// canonical's alias list (creating the group if the canonical is not one yet) and drops the
    /// suggestion. Nothing is written until <see cref="SaveAsync"/>.</summary>
    [RelayCommand]
    private void AcceptSuggestion(SuggestionViewModel suggestion)
    {
        // TRACKING item 13: repeats the same "still in the list" guard a direct Execute needs.
        if (!Suggestions.Contains(suggestion))
        {
            return;
        }

        var canonical = suggestion.SelectedName;
        var aliases = suggestion.Names.Where(name => !string.Equals(name, canonical, StringComparison.Ordinal)).ToList();

        // The web's cleanup (FiltersTab.tsx:92, `prev.filter((g) => !aliases.includes(g.canonical))`,
        // review I2): a raw name about to become an alias may itself already be an existing
        // canonical group's name. Dropping that group wholesale first is what keeps the saved
        // document from ever holding one raw name as both a canonical key and another entry's
        // alias, which AliasMap would otherwise fold two different ways.
        foreach (var absorbed in Editor.Groups.Where(group => aliases.Contains(group.Canonical, StringComparer.Ordinal)).ToList())
        {
            Editor.RemoveGroup(absorbed);
        }

        var existing = Editor.Groups.FirstOrDefault(group => string.Equals(group.Canonical, canonical, StringComparison.Ordinal));
        if (existing is not null)
        {
            foreach (var alias in aliases)
            {
                if (!existing.Aliases.Contains(alias))
                {
                    existing.Aliases.Add(alias);
                }
            }
        }
        else
        {
            // No stored colour (P13 R2a), the same rule AddFilter and GroupSelected follow.
            Editor.AddGroup(new AliasGroupViewModel(canonical, null, aliases));
        }

        Suggestions.Remove(suggestion);
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionsHeading));
    }

    /// <summary>The web's "Dismiss": appends the sorted name list to the dismissed list and
    /// drops the suggestion. Persists only on <see cref="SaveAsync"/> (the web's own rule).
    /// </summary>
    [RelayCommand]
    private void DismissSuggestion(SuggestionViewModel suggestion)
    {
        if (!Suggestions.Contains(suggestion))
        {
            return;
        }

        // suggestion.Names is already sorted (SuggestionGrouper.Group's own contract).
        _dismissed.Add([.. suggestion.Names]);
        Suggestions.Remove(suggestion);
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionsHeading));
    }

    /// <summary>Writes the filter document, then the dismissed list, in that order (roadmap
    /// review focus item 6: the alias document lands before the dismissed list, because the
    /// first is what makes a suggestion stop being generated and the second is what makes a
    /// dismissal stick). Two writes through <c>SettingsStore</c>; nothing here pokes
    /// <c>AliasMapCache</c> directly.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        // TRACKING item 13: the body repeats the CanExecute guard.
        if (!CanSave())
        {
            return;
        }

        IsSaving = true;
        // group.Color, the stored value, and deliberately never group.ResolvedColor. It reads
        // like an oversight and is not: writing the resolved value back would pin every seeded
        // colour into the document on the first unrelated save, and a later change to the seeded
        // palette would then be invisible to the user (P13 R2a, ruling Q2).
        // FilterColor.AsStored is also what stops "#808080" being written back as a stored value
        // (review P3-5): that string is what every pre-Phase-13 build put into every group it
        // created, so it records an old default rather than a choice, and a group resolving to the
        // grey fallback stores null.
        var document = Editor.Groups.ToDictionary(
            group => group.Canonical,
            group => new FilterSetting
            {
                Color = FilterColor.AsStored(group.Color),
                Aliases = [.. group.Aliases],
            },
            StringComparer.Ordinal);
        var dismissed = _dismissed.Select(group => new List<string>(group)).ToList();

        var task = Task.Run(() =>
        {
            try
            {
                _saveFilters(document);
                _saveDismissed(dismissed);
                _post(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    IsSaving = false;
                    ErrorMessage = null;
                    StatusMessage = "Filter settings saved";
                    RefreshSuggestions();
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Saving the filter settings failed");
                _post(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    IsSaving = false;
                    ErrorMessage = "The filter settings could not be saved. See the log for details.";
                });
            }
        });
        PendingSave = task;
        await task.ConfigureAwait(false);
    }

    private bool CanSave() => !IsSaving;

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
                    var filters = _loadFilters();
                    var dismissed = _loadDismissed();
                    var discovered = _discoveredFilters();
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, filters, dismissed, discovered));
                }
                catch (OperationCanceledException)
                {
                    // The tab went away while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the filter settings failed");
                    _post(() => Publish(generation, null, null, null));
                }
            },
            token);
    }

    private void Publish(
        int generation,
        Dictionary<string, FilterSetting>? filters,
        List<List<string>>? dismissed,
        IReadOnlyList<(string Name, int Count)>? discovered)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (filters is not null && dismissed is not null && discovered is not null)
        {
            _dismissed = dismissed;
            // Suggestions run over the raw, unfolded counts (settings.py's suggest_filters reads
            // the same ungrouped query the discovered endpoint does, with no fold), so the raw
            // list is what RefreshSuggestions below uses.
            _lastDiscovered = discovered;
            Editor.SetDiscovered(FoldDiscovered(discovered, filters));
            Editor.SetGroups(filters.Select(entry => new AliasGroupViewModel(entry.Key, entry.Value.Color, entry.Value.Aliases)));
            RefreshSuggestions();
        }

        LoadFailed = filters is null;
        IsLoading = false;
    }

    // Port of settings.py:572-578's discovered-endpoint fold: raw names normalized through the
    // current alias map, counts summed per canonical, re-sorted by count descending (review
    // minor 3, ruled "implement", not just declare). Built from an AliasMap over the just-loaded
    // filters document, not the shared AliasMapCache: this tab must reflect the exact document it
    // holds in memory, including edits not yet saved, and never reach into the cache the brief
    // forbids touching directly.
    private static IReadOnlyList<(string Name, int Count)> FoldDiscovered(
        IReadOnlyList<(string Name, int Count)> discovered,
        Dictionary<string, FilterSetting> filters)
    {
        var map = new AliasMap(filters, new EquipmentSettings());
        var order = new List<string>();
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, count) in discovered)
        {
            var canonical = map.CanonicalFilter(name) ?? name;
            if (merged.TryGetValue(canonical, out var existing))
            {
                merged[canonical] = existing + count;
            }
            else
            {
                merged[canonical] = count;
                order.Add(canonical);
            }
        }

        return [.. order
            .Select(name => (Name: name, Count: merged[name]))
            .OrderByDescending(item => item.Count)];
    }

    private void RefreshSuggestions()
    {
        var counts = _lastDiscovered.ToDictionary(item => item.Name, item => item.Count, StringComparer.Ordinal);
        var groups = SuggestionGrouper.Group(counts);

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Editor.Groups)
        {
            known.Add(group.Canonical);
            foreach (var alias in group.Aliases)
            {
                known.Add(alias);
            }
        }

        Suggestions.Clear();
        foreach (var group in groups)
        {
            if (SuggestionGrouper.AlreadyMerged(group, known))
            {
                continue;
            }

            if (SuggestionGrouper.IsDismissed(group, _dismissed))
            {
                continue;
            }

            Suggestions.Add(new SuggestionViewModel(group));
        }

        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionsHeading));
    }

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
