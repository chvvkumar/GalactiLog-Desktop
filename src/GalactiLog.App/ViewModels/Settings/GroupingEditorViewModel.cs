using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Aliases;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Port of the web's <c>GroupingEditor.tsx</c>: a two-column editor over one section's discovered
/// names and canonical groups. One shared view-model, not three near-copies, configured only by
/// <see cref="ShowColorPicker"/>: <c>FiltersTabViewModel</c> builds one with it true,
/// <c>EquipmentTabViewModel</c> builds two (cameras, telescopes) with it false. Design-lessons
/// rule 1 at the third occurrence (collision map: "the grouping editor (GroupingEditorViewModel),
/// owner Task 7, reused by Task 7 three times").
/// </summary>
/// <remarks>
/// <para>
/// Left column: <see cref="Ungrouped"/>, discovered names not covered by any group's canonical
/// name or alias list, in the discovered query's own order (no client-side sort). Right column:
/// <see cref="Groups"/>, the canonical entries -- for a filters editor this collection rendered
/// as a table is spec 12.7's "table of canonical filters, each with a colour swatch and an alias
/// list editor" (there is no separate web-style "ungrouped colours" side list in this port: every
/// canonical filter, whether it has aliases or not, is one row of this same table, which is the
/// same data the web's <c>groups</c> and <c>ungroupedColors</c> signals hold between them, just
/// not split across two view states -- recorded as a deviation in the task report).
/// </para>
/// <para>
/// This type holds no notion of "dirty" or "saved": the owning tab view-model decides when to
/// persist. It only ever mutates its own <see cref="Groups"/> and <see cref="Ungrouped"/>
/// collections in memory.
/// </para>
/// </remarks>
public sealed partial class GroupingEditorViewModel : ObservableObject
{
    private IReadOnlyList<(string Name, int Count)> _discovered = [];

    public GroupingEditorViewModel(bool showColorPicker)
    {
        ShowColorPicker = showColorPicker;
    }

    /// <summary>True for the Filters editor, false for the Cameras and Telescopes editors --
    /// the web's <c>showColorPicker</c> prop, verbatim.</summary>
    public bool ShowColorPicker { get; }

    /// <summary>The canonical groups (the web's right column, "Groups (N)").</summary>
    public ObservableCollection<AliasGroupViewModel> Groups { get; } = [];

    /// <summary>Discovered names not covered by any group (the web's left column, "Ungrouped
    /// (N)"), in the discovered query's own order.</summary>
    public ObservableCollection<DiscoveredNameViewModel> Ungrouped { get; } = [];

    /// <summary>Renders the web's "All items are grouped" empty state.</summary>
    public bool HasNoUngrouped => Ungrouped.Count == 0;

    /// <summary>Renders the web's "No groups yet - select items on the left to create one" empty
    /// state.</summary>
    public bool HasNoGroups => Groups.Count == 0;

    /// <summary>How many ungrouped rows are checked right now, for the "Group Selected (N)" and
    /// "Add to..." captions.</summary>
    public int CheckedCount => Ungrouped.Count(row => row.IsChecked);

    /// <summary>At least one ungrouped row is checked. The web's own gate on the "Add to..."
    /// menu (review minor 4: <c>checkedCount() &gt; 0 &amp;&amp; props.groups.length &gt; 0</c>).
    /// </summary>
    public bool HasCheckedRows => CheckedCount > 0;

    /// <summary>Whether the "Add to..." affordance should show at all: at least one row checked
    /// and at least one group to add it to.</summary>
    public bool CanAddToExistingGroup => HasCheckedRows && !HasNoGroups;

    /// <summary>Raised when a group's rename would collide with another group's canonical name,
    /// so the owning tab can show why the rename was refused.</summary>
    public event EventHandler<string>? RenameRefused;

    /// <summary>Replaces the discovered-name set for this section (a fresh load or a reload) and
    /// recomputes <see cref="Ungrouped"/> against the current <see cref="Groups"/>.</summary>
    public void SetDiscovered(IReadOnlyList<(string Name, int Count)> discovered)
    {
        _discovered = discovered;
        RefreshUngrouped();
    }

    /// <summary>Replaces every group (a fresh load) with the given ones, wiring each to this
    /// editor's rename and alias-emptied handling.</summary>
    public void SetGroups(IEnumerable<AliasGroupViewModel> groups)
    {
        foreach (var group in Groups)
        {
            Detach(group);
        }

        Groups.Clear();
        foreach (var group in groups)
        {
            Attach(group);
            Groups.Add(group);
        }

        RefreshUngrouped();
    }

    /// <summary>Adds one new group (an accepted suggestion, a "Group Selected", or a brand new
    /// canonical name added by the tab), wiring it the same way <see cref="SetGroups"/> does.
    /// </summary>
    public void AddGroup(AliasGroupViewModel group)
    {
        Attach(group);
        Groups.Add(group);
        RefreshUngrouped();
        NotifyCounts();
    }

    /// <summary>Removes one group outright, with no alias-preservation, for the web's accept-time
    /// cleanup (review I2): a group whose canonical name is about to become another entry's alias
    /// is dropped wholesale, exactly as <c>prev.filter((g) =&gt; !aliases.includes(g.canonical))</c>
    /// does, so the saved document never holds one raw name as both a canonical key and an alias.
    /// </summary>
    public void RemoveGroup(AliasGroupViewModel group)
    {
        if (!Groups.Contains(group))
        {
            return;
        }

        Detach(group);
        Groups.Remove(group);
        RefreshUngrouped();
        NotifyCounts();
    }

    private void Attach(AliasGroupViewModel group)
    {
        group.AliasesEmptied += OnAliasesEmptied;
        group.RenameRequested += OnRenameRequested;
        group.Aliases.CollectionChanged += OnGroupAliasesChanged;
    }

    private void Detach(AliasGroupViewModel group)
    {
        group.AliasesEmptied -= OnAliasesEmptied;
        group.RenameRequested -= OnRenameRequested;
        group.Aliases.CollectionChanged -= OnGroupAliasesChanged;
    }

    private void OnGroupAliasesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RefreshUngrouped();

    private void OnAliasesEmptied(object? sender, EventArgs e)
    {
        if (sender is not AliasGroupViewModel group)
        {
            return;
        }

        Detach(group);
        Groups.Remove(group);
        RefreshUngrouped();
        NotifyCounts();
    }

    private void OnRenameRequested(object? sender, string newName)
    {
        if (sender is not AliasGroupViewModel group)
        {
            return;
        }

        if (Groups.Any(other => other != group && string.Equals(other.Canonical, newName, StringComparison.Ordinal)))
        {
            RenameRefused?.Invoke(this, newName);
            return;
        }

        group.Canonical = newName;
        RefreshUngrouped();
    }

    // ---- ungrouped: check, group-selected, add-to-group ---------------------------------------

    [RelayCommand(CanExecute = nameof(CanGroupSelected))]
    private void GroupSelected()
    {
        var checkedRows = Ungrouped.Where(row => row.IsChecked).OrderByDescending(row => row.Count).ToList();
        // TRACKING item 13: CanExecute is the affordance, the body repeats the guard.
        if (checkedRows.Count < 2)
        {
            return;
        }

        var canonical = checkedRows[0].Name;
        var aliases = checkedRows.Skip(1).Select(row => row.Name);
        // The web clears every checked row on group creation, not only the ones just consumed
        // (GroupingEditor.tsx: setChecked(new Set())). Explicit, ahead of AddGroup's own refresh,
        // which otherwise preserves checked state by name for every other kind of refresh
        // (review minor 6).
        ClearChecked();
        // No stored colour (P13 R2a): a new group named "Ha" resolves the seeded red rather than
        // being pinned grey at birth.
        AddGroup(new AliasGroupViewModel(canonical, null, aliases));
    }

    /// <summary>R2(c): choosing a colour for an ungrouped discovered name promotes it to a
    /// one-name group carrying that colour, which is the shape the web's <c>ungroupedColors</c>
    /// saves (<c>FiltersTab.tsx:133-138</c>, <c>{ color, aliases: [] }</c>). A name already
    /// covered by a group is a no-op: its colour belongs to that group's own swatch.</summary>
    public void SetUngroupedColor(string name, string color)
    {
        // TRACKING item 13: the coverage guard is repeated here and not left at the call site,
        // because a direct call is what a test and a second view both reach this through.
        if (IsCovered(name))
        {
            return;
        }

        // AddGroup runs RefreshUngrouped and NotifyCounts, which is what removes the promoted row
        // from Ungrouped.
        AddGroup(new AliasGroupViewModel(name, color, []));
    }

    private bool IsCovered(string name)
        => Groups.Any(group =>
            string.Equals(group.Canonical, name, StringComparison.Ordinal)
            || group.Aliases.Contains(name, StringComparer.Ordinal));

    private bool CanGroupSelected() => Ungrouped.Count(row => row.IsChecked) >= 2;

    [RelayCommand]
    private void AddToGroup(AliasGroupViewModel group)
    {
        var checkedNames = Ungrouped.Where(row => row.IsChecked).Select(row => row.Name).ToList();
        // TRACKING item 13 again: the web's own menu only offers this action while at least one
        // row is checked, and the body still refuses an empty selection reached any other way.
        if (checkedNames.Count == 0)
        {
            return;
        }

        foreach (var name in checkedNames)
        {
            if (!group.Aliases.Contains(name))
            {
                group.Aliases.Add(name);
            }
        }

        // Same rule as GroupSelected: every checked row clears, not only the ones just added
        // (review minor 6). group.Aliases.CollectionChanged already triggers one RefreshUngrouped
        // per add; clearing first means that refresh sees nothing left checked to preserve.
        ClearChecked();
        RefreshUngrouped();
    }

    private void ClearChecked()
    {
        foreach (var row in Ungrouped)
        {
            row.IsChecked = false;
        }
    }

    private void RefreshUngrouped()
    {
        // Captured by name, not by reference, because every row below is recreated: an incidental
        // refresh (an alias added elsewhere, a rename, a reload) must not silently clear a
        // checkbox the user has not acted on yet (review minor 6). GroupSelected and AddToGroup
        // clear explicitly instead, which is the web's own rule.
        var checkedNames = new HashSet<string>(
            Ungrouped.Where(row => row.IsChecked).Select(row => row.Name),
            StringComparer.Ordinal);

        foreach (var row in Ungrouped)
        {
            row.PropertyChanged -= OnUngroupedRowChanged;
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups)
        {
            covered.Add(group.Canonical);
            foreach (var alias in group.Aliases)
            {
                covered.Add(alias);
            }
        }

        Ungrouped.Clear();
        foreach (var (name, count) in _discovered)
        {
            if (covered.Contains(name))
            {
                continue;
            }

            // Seeded through the fold rather than by exact name (ruling Q4): one resolution means
            // "lum" and "L" get the same white. The web seeds this swatch by exact name only
            // (FiltersTab.tsx:74) and so leaves "lum" blank, which disagrees with its own resolver.
            //
            // Through FilterColor.Resolve, not FilterCategory.DefaultFor: HANDOFF 5.2 item 12 makes
            // Resolve the one resolution, and a second entry point to the palette would not follow
            // a step added to it later (phase review P3-6). An ungrouped row has no stored colour
            // and no aliases yet, so the other two arguments are null and the grey is what a name
            // with no category seeds, which is what ParseTint drew for the null anyway.
            var row = new DiscoveredNameViewModel(name, count)
            {
                IsChecked = checkedNames.Contains(name),
                Color = FilterColor.Resolve(null, name, null),
            };
            row.PropertyChanged += OnUngroupedRowChanged;
            Ungrouped.Add(row);
        }

        NotifyCounts();
    }

    private void OnUngroupedRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoveredNameViewModel.IsChecked))
        {
            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(HasCheckedRows));
            OnPropertyChanged(nameof(CanAddToExistingGroup));
            GroupSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(HasNoUngrouped));
        OnPropertyChanged(nameof(HasNoGroups));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasCheckedRows));
        OnPropertyChanged(nameof(CanAddToExistingGroup));
        GroupSelectedCommand.NotifyCanExecuteChanged();
    }
}
