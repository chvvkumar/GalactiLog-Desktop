using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Aliases;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One banner row (the web's <c>SuggestionsBanner.tsx</c> <c>MergeGroup</c>): a
/// <see cref="SuggestionGroup"/> the user can accept (the web's "Merge") or dismiss, with one
/// pill per candidate name showing <c>name (count)</c> and a clickable selection of which member
/// becomes the canonical name.
/// </summary>
public sealed partial class SuggestionViewModel : ObservableObject
{
    public SuggestionViewModel(SuggestionGroup group, string? section = null)
    {
        Names = group.Names;
        Counts = group.Counts;
        Section = section;
        Pills = [.. Names.Select(name => new SuggestionPillViewModel(name, Counts.GetValueOrDefault(name, 0)))];
        // The generated setter below invokes OnSelectedNameChanged, which marks the matching
        // pill selected, so no separate seeding call is needed here.
        SelectedName = PickHighestCount(Names, Counts);
    }

    /// <summary>The candidate names, sorted (as <see cref="SuggestionGrouper.Group"/> produces
    /// them).</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Frame count per candidate name.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; }

    /// <summary>"cameras" or "telescopes" for an equipment suggestion, null for a filter one.
    /// Unused by <c>FiltersTabViewModel</c>, which has only one section.</summary>
    public string? Section { get; }

    /// <summary>One pill per candidate name, in <see cref="Names"/>'s order, so the view can bind
    /// each one's own selected state without an ancestor lookup.</summary>
    public IReadOnlyList<SuggestionPillViewModel> Pills { get; }

    /// <summary>The pill currently chosen to become the canonical name on Accept. Defaults to
    /// the highest-count member, the web's own default.</summary>
    [ObservableProperty]
    public partial string SelectedName { get; set; }

    partial void OnSelectedNameChanged(string value) => MarkSelected(value);

    private void MarkSelected(string selected)
    {
        foreach (var pill in Pills)
        {
            pill.IsSelected = string.Equals(pill.Name, selected, StringComparison.Ordinal);
        }
    }

    [RelayCommand]
    private void Select(string name) => SelectedName = name;

    // Port of the web's `group.reduce((a, b) => (counts[a] >= counts[b] ? a : b))`: a left fold
    // that keeps the running choice on a tie, so among equal counts the earliest name in the
    // (already sorted) list wins.
    private static string PickHighestCount(IReadOnlyList<string> names, IReadOnlyDictionary<string, int> counts)
    {
        var selected = names[0];
        for (var i = 1; i < names.Count; i++)
        {
            var candidate = names[i];
            var selectedCount = counts.GetValueOrDefault(selected, 0);
            var candidateCount = counts.GetValueOrDefault(candidate, 0);
            selected = selectedCount >= candidateCount ? selected : candidate;
        }

        return selected;
    }
}

/// <summary>One clickable pill inside a <see cref="SuggestionViewModel"/>: a candidate name, its
/// frame count, and whether it is the currently selected canonical choice. A small
/// <see cref="ObservableObject"/> rather than a plain record, so <see cref="IsSelected"/> can be
/// bound directly by the view without an ancestor lookup back to the owning suggestion.</summary>
public sealed partial class SuggestionPillViewModel(string name, int count) : ObservableObject
{
    public string Name { get; } = name;

    public int Count { get; } = count;

    /// <summary>The web's pill caption, <c>name (count)</c>, verbatim.</summary>
    public string Label => $"{Name} ({Count})";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
