using CommunityToolkit.Mvvm.ComponentModel;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// One collapsible section of the filter panel (design-spec 12.2). Holds presentation state only.
/// <see cref="IsActive"/> is a predicate over the panel's own values rather than a duplicated
/// flag, so a section can never show an active marker while its inputs are empty;
/// <see cref="FilterPanelViewModel"/> calls <see cref="RaiseIsActive"/> on every section whenever
/// anything changes.
/// </summary>
public sealed partial class FilterSectionViewModel : ObservableObject, IStripItem
{
    private readonly Func<bool> _isActive;

    public FilterSectionViewModel(string key, string title, string shortLabel, string iconKey, Func<bool> isActive)
    {
        Key = key;
        Title = title;
        ShortLabel = shortLabel;
        IconKey = iconKey;
        _isActive = isActive;
    }

    /// <summary>"search", "object_type", "date_range", "filters", "equipment", "metrics",
    /// "header_query".</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>The collapsed strip's label (spec 12.2, Phase 14C, questions.md Q1). Positional
    /// rather than trailing and optional, because a section with no short label cannot render on
    /// the strip and a default would hide that at the one site that forgot. At most four
    /// characters: carried fixer-list item 18 records that the 48 pixel strip clips the fifth at
    /// the Extra Large text size.</summary>
    public string ShortLabel { get; }

    /// <summary>The strip item's tooltip, which is the section's own heading.</summary>
    public string FullLabel => Title;

    /// <summary>The icon the collapsed strip draws for this section (polish wave 1 ruling 6).
    /// Positional and non-null for the same reason <see cref="ShortLabel"/> is.</summary>
    public string? IconKey { get; }

    /// <summary>False: a short label is prose, not a tabular figure. The ledger strip's dates are
    /// the monospace case.</summary>
    public bool IsMonospace => false;

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsActive => _isActive();

    public void RaiseIsActive() => OnPropertyChanged(nameof(IsActive));
}
