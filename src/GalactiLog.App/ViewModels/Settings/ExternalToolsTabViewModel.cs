using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One row of the AstroBin filter id map (spec amendment 4c): a filter name from
/// <see cref="ExternalToolsTabViewModel"/>'s known-filters union and its stored AstroBin id, or
/// blank when unset. Clearing the box removes the key rather than storing <c>0</c> (spec 5.8.1
/// amendment 1b); a value that is not a positive integer is refused and the box reverts, the
/// shape the Diagnostics retention editors use.
/// </summary>
public sealed partial class FilterIdRowViewModel : ObservableObject
{
    private readonly ExternalToolsTabViewModel _owner;
    private readonly bool _constructed;

    internal FilterIdRowViewModel(ExternalToolsTabViewModel owner, string filterName, int? id)
    {
        _owner = owner;
        FilterName = filterName;
        IdText = id is { } value ? value.ToString(CultureInfo.InvariantCulture) : "";
        _constructed = true;
    }

    /// <summary>The filter name exactly as the Filters tab spells it.</summary>
    public string FilterName { get; }

    [ObservableProperty]
    public partial string IdText { get; set; }

    /// <summary>The refusal message, or null. Set only when a non-blank entry was not a positive
    /// integer.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    partial void OnIdTextChanged(string value)
    {
        if (_constructed)
        {
            _owner.CommitFilterId(this);
        }
    }
}

/// <summary>
/// Spec amendment 4c's External Tools tab, section 12.16: the AstroBin filter id map, the Bortle
/// class, and the NINA and Stellarium instance lists. Built on <see cref="GeneralSettingsTabViewModel"/>
/// like every other Settings tab that edits the <c>general</c> document: every write goes through
/// <c>SettingsStore.MutateGeneral</c> and rewrites exactly one key, leaving the other three
/// intact, because each field's commit builds its own <c>with</c> expression over the freshest
/// document <c>MutateGeneral</c> hands it rather than a snapshot taken at edit time.
/// </summary>
/// <remarks>
/// The filter id map and both instance lists rebuild whole on every publish; a row commits back
/// through <see cref="CommitFilterId"/> or <see cref="CommitInstance"/>, both guarded on
/// <see cref="GeneralSettingsTabViewModel.IsApplying"/> so a publish never re-enters as a write.
/// </remarks>
public sealed partial class ExternalToolsTabViewModel : GeneralSettingsTabViewModel
{
    private readonly Func<IReadOnlyList<string>> _knownFilters;
    private readonly Action? _unfollowAliasSources;
    private readonly Action? _unfollowDerivedData;

    /// <param name="load">Normally <c>SettingsStore.GetGeneral</c>.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>.</param>
    /// <param name="knownFilters">Normally <c>() =&gt; filtersTab.KnownFilters()</c>. Ruling B2:
    /// the External Tools tab reaches the Filters tab's canonical-plus-ungrouped union through
    /// this delegate, never by constructing a second <c>FiltersTabViewModel</c> or a second union
    /// rule.</param>
    /// <param name="subscribeAliasSourcesChanged">Ruling B32: normally
    /// <c>handler =&gt; settingsStore.AliasSourcesChanged += handler</c>. A filter group renamed
    /// or added on the Filters tab moves the known-filters union without writing the general
    /// document, so <see cref="FilterIdRows"/> would otherwise go stale for the life of the
    /// window.</param>
    /// <param name="unsubscribeAliasSourcesChanged">Its pair, called from <c>DisposeCore</c>.</param>
    /// <param name="subscribeFilterUnionRefresh">Ruling B32's second half: the same post-scan
    /// signal Statistics and Analysis refresh on, normally
    /// <c>handler =&gt; derivedDataNotifier.Changed += handler</c>, for a scan that discovers a
    /// filter name the union did not carry before. Named apart from the page-follow seam
    /// <c>Phd2ReRunHostWiringTests</c> counts (this is a Settings tab, not one of that census's
    /// three pages), so this tab's registration adds no fourth line to that count.</param>
    /// <param name="unsubscribeFilterUnionRefresh">Its pair, called from <c>DisposeCore</c>.</param>
    public ExternalToolsTabViewModel(
        Func<GeneralSettings> load,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Func<IReadOnlyList<string>> knownFilters,
        Action<Action>? post = null,
        ILogger? logger = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null,
        Action<EventHandler>? subscribeAliasSourcesChanged = null,
        Action<EventHandler>? unsubscribeAliasSourcesChanged = null,
        Action<EventHandler>? subscribeFilterUnionRefresh = null,
        Action<EventHandler>? unsubscribeFilterUnionRefresh = null)
        : base(load, mutateGeneral, post, logger, subscribeGeneralChanged, unsubscribeGeneralChanged)
    {
        ArgumentNullException.ThrowIfNull(knownFilters);
        _knownFilters = knownFilters;

        if (subscribeAliasSourcesChanged is not null && unsubscribeAliasSourcesChanged is not null)
        {
            subscribeAliasSourcesChanged(OnFilterUnionMaybeChanged);
            _unfollowAliasSources = () => unsubscribeAliasSourcesChanged(OnFilterUnionMaybeChanged);
        }

        if (subscribeFilterUnionRefresh is not null && unsubscribeFilterUnionRefresh is not null)
        {
            subscribeFilterUnionRefresh(OnFilterUnionMaybeChanged);
            _unfollowDerivedData = () => unsubscribeFilterUnionRefresh(OnFilterUnionMaybeChanged);
        }

        BortleText = "";
        Load();
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        _unfollowAliasSources?.Invoke();
        _unfollowDerivedData?.Invoke();
    }

    /// <summary>Spec amendment 4c's filter id map, one row per name in the known-filters union. A
    /// stored id for a filter the union no longer names is not dropped by this tab (it keeps its
    /// key on the next unrelated write); it is simply not rendered as a row here.</summary>
    public ObservableCollection<FilterIdRowViewModel> FilterIdRows { get; } = [];

    /// <summary>Spec amendment 4c's Bortle class box, watermarked "1 to 9". Blank means unset.
    /// </summary>
    [ObservableProperty]
    public partial string BortleText { get; set; }

    [ObservableProperty]
    public partial string? BortleError { get; set; }

    /// <summary>Spec amendment 4c's NINA instances list, in stored order.</summary>
    public ObservableCollection<InstanceRowViewModel> NinaInstances { get; } = [];

    /// <summary>Spec amendment 4c's Stellarium instances list, in stored order.</summary>
    public ObservableCollection<InstanceRowViewModel> StellariumInstances { get; } = [];

    /// <inheritdoc />
    protected override void ApplyDocument(GeneralSettings general)
    {
        RebuildFilterIdRows(IntegrationSettings.ReadFilterIds(general.AstroBinFilterIdsDocument));

        BortleText = IntegrationSettings.ReadBortle(general.AstroBinBortle)?.ToString(CultureInfo.InvariantCulture) ?? "";
        BortleError = null;

        RebuildInstances(NinaInstances, IntegrationSettings.ReadInstances(general.NinaInstancesDocument), isNina: true);
        RebuildInstances(StellariumInstances, IntegrationSettings.ReadInstances(general.StellariumInstancesDocument), isNina: false);
    }

    /// <summary>Rebuilds <see cref="FilterIdRows"/> from the current known-filters union.
    /// Ruling B32: called from <see cref="ApplyDocument"/> and again on
    /// <see cref="OnFilterUnionMaybeChanged"/>, since the union can move without a general
    /// document write.</summary>
    private void RebuildFilterIdRows(IReadOnlyDictionary<string, int> filterIds)
    {
        FilterIdRows.Clear();
        foreach (var name in _knownFilters())
        {
            FilterIdRows.Add(new FilterIdRowViewModel(
                this, name, filterIds.TryGetValue(name, out var id) ? id : null));
        }
    }

    // Ruling B32: AliasSourcesChanged (a filter renamed or added) and the post-scan derived-data
    // signal (a filter newly discovered) both move the known-filters union without writing the
    // general document, so this re-reads it directly rather than waiting for OnGeneralChanged.
    private void OnFilterUnionMaybeChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (IsDisposed || !IsReadyToSave)
        {
            return;
        }

        RebuildFilterIdRows(IntegrationSettings.ReadFilterIds(Saved.AstroBinFilterIdsDocument));
    });

    partial void OnBortleTextChanged(string value)
    {
        if (IsApplying)
        {
            return;
        }

        var trimmed = value.Trim();
        var previousText = IntegrationSettings.ReadBortle(Saved.AstroBinBortle)?.ToString(CultureInfo.InvariantCulture) ?? "";

        int? bortle;
        if (trimmed.Length == 0)
        {
            bortle = null;
        }
        else if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                 && parsed is >= 1 and <= 9)
        {
            bortle = parsed;
        }
        else
        {
            BortleError = "Enter a whole number from 1 to 9.";
            Apply(() => BortleText = previousText);
            return;
        }

        BortleError = null;
        ImmediateSave(
            general => general with { AstroBinBortle = bortle },
            bortle is null ? "Bortle class cleared" : $"Bortle class set to {bortle}",
            rollBack: () => BortleText = previousText);
    }

    /// <summary>Called by a <see cref="FilterIdRowViewModel"/> on every change to its
    /// <c>IdText</c>. Rewrites only <paramref name="row"/>'s own key, through
    /// <see cref="IntegrationSettings.WriteFilterId"/>, so a stranded key another row does not
    /// render is carried through untouched (ruling B11, required case 3).</summary>
    internal void CommitFilterId(FilterIdRowViewModel row)
    {
        if (IsApplying)
        {
            return;
        }

        var trimmed = row.IdText.Trim();
        var previousIds = IntegrationSettings.ReadFilterIds(Saved.AstroBinFilterIdsDocument);
        var previousText = previousIds.TryGetValue(row.FilterName, out var previousId)
            ? previousId.ToString(CultureInfo.InvariantCulture)
            : "";

        int? id;
        if (trimmed.Length == 0)
        {
            id = null;
        }
        else if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            id = parsed;
        }
        else
        {
            row.Error = "Enter a positive whole number.";
            Apply(() => row.IdText = previousText);
            return;
        }

        row.Error = null;
        var filterName = row.FilterName;
        ImmediateSave(
            general => general with
            {
                AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(general.AstroBinFilterIdsDocument, filterName, id),
            },
            id is null ? $"Cleared the AstroBin id for {filterName}" : $"AstroBin id for {filterName} set to {id}",
            rollBack: () => row.IdText = previousText);
    }

    /// <summary>Called by an <see cref="InstanceRowViewModel"/> on every change to its
    /// <c>Name</c>, <c>Url</c> or <c>Enabled</c>. A non-blank URL that fails
    /// <see cref="IntegrationUrl.TryParse"/> is refused before the whole list is rewritten and the
    /// box reverts (required case 5); a blank URL is stored, matching a blank name (required case
    /// 6).</summary>
    internal void CommitInstance(InstanceRowViewModel row)
    {
        if (IsApplying)
        {
            return;
        }

        var isNina = row.IsNina;
        var rows = isNina ? NinaInstances : StellariumInstances;
        if (!rows.Contains(row))
        {
            return;
        }

        // wave2-review :237: the previous value comes from the row's own last-committed fields,
        // never from Saved by position. Saved is this tab's last-landed write and can lag a
        // second edit made before the first one's queued write completes, at which point an
        // index into it does not name this row's own previous value.
        var previous = row.Committed;

        var trimmedUrl = row.Url.Trim();
        if (trimmedUrl.Length > 0 && !IntegrationUrl.TryParse(trimmedUrl, out _))
        {
            row.UrlError = IntegrationMessages.BadUrl;
            Apply(() => row.Url = previous.Url);
            return;
        }

        row.UrlError = null;
        row.MarkCommitted();
        SaveInstances(
            rows,
            isNina,
            isNina ? "NINA instance saved" : "Stellarium instance saved",
            rollBack: () =>
            {
                row.Name = previous.Name;
                row.Url = previous.Url;
                row.Enabled = previous.Enabled;
                row.MarkCommitted();
            });
    }

    /// <summary>Spec amendment 4c's "Add instance" <c>Button.sm</c>: appends a row with an empty
    /// name, an empty URL and On checked (required case 8).</summary>
    [RelayCommand]
    private void AddNinaInstance() => AddInstance(NinaInstances, isNina: true);

    [RelayCommand]
    private void AddStellariumInstance() => AddInstance(StellariumInstances, isNina: false);

    /// <summary>Removes exactly the row given, rewriting the key with the rest of the list
    /// untouched (required case 8: "a remove that renumbers or drops a second row").</summary>
    [RelayCommand]
    private void RemoveInstance(InstanceRowViewModel row)
    {
        var isNina = row.IsNina;
        var rows = isNina ? NinaInstances : StellariumInstances;
        var index = rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        rows.RemoveAt(index);
        SaveInstances(rows, isNina, "Instance removed", rollBack: () => rows.Insert(index, row));
    }

    private void AddInstance(ObservableCollection<InstanceRowViewModel> rows, bool isNina)
    {
        var row = new InstanceRowViewModel(this, isNina, "", "", true);
        rows.Add(row);
        SaveInstances(rows, isNina, "Instance added", rollBack: () => rows.Remove(row));
    }

    // wave2-review :248 / phase-review :293, design lesson 1: the snapshot-then-write block was
    // written three times (CommitInstance, RemoveInstance, AddInstance); this is the one spine.
    private void SaveInstances(
        ObservableCollection<InstanceRowViewModel> rows, bool isNina, string message, Action rollBack)
    {
        var snapshot = rows.Select(r => new IntegrationInstance(r.Name, r.Url, r.Enabled)).ToList();
        ImmediateSave(
            general => isNina
                ? general with { NinaInstancesDocument = IntegrationSettings.WriteInstances(snapshot) }
                : general with { StellariumInstancesDocument = IntegrationSettings.WriteInstances(snapshot) },
            message,
            rollBack);
    }

    private void RebuildInstances(
        ObservableCollection<InstanceRowViewModel> rows, IReadOnlyList<IntegrationInstance> instances, bool isNina)
    {
        rows.Clear();
        foreach (var instance in instances)
        {
            rows.Add(new InstanceRowViewModel(this, isNina, instance.Name, instance.Url, instance.Enabled));
        }
    }
}
