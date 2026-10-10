using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// The shared chart metric and filter selection both Target detail charts bind to, so a metric
/// toggled on the cross-session chart is also on in the per-session chart, which is what spec
/// 5.8.3's single <c>graph</c> document implies. Every change updates the in-memory state, raises
/// <see cref="Changed"/> synchronously so each chart rebuilds its series, and queues the write
/// independently: a chart must not wait on SQLite to re-render.
/// </summary>
public sealed partial class ChartSelectionViewModel : ObservableObject
{
    /// <summary>The sentinel in <c>graph.enabled_filters</c> meaning "one series over every frame,
    /// coloured by the metric token" (spec 13). Any other entry is a canonical filter name and
    /// produces an additional series restricted to that filter, tinted with the filter's
    /// configured colour. Both can be on at once, which is what the web application does.</summary>
    public const string OverallFilterKey = "overall";

    private readonly GraphSettingsWriter _writer;
    private readonly Func<AliasMap> _aliases;

    public ChartSelectionViewModel(
        GraphSettings initial,
        GraphSettingsWriter writer,
        Func<AliasMap> aliases)
    {
        _writer = writer;
        _aliases = aliases;

        // One toggle per ChartMetrics.All entry, in that order. A stored key that this build does
        // not chart has no entry here and is therefore ignored, not rendered.
        var enabledMetrics = initial.EnabledMetrics.ToHashSet(StringComparer.Ordinal);
        Metrics =
        [
            .. ChartMetrics.All.Select(metric => new ToggleOptionViewModel(metric.Key, metric.Label)
            {
                IsSelected = enabledMetrics.Contains(metric.Key),
            }),
        ];
        foreach (var pill in Metrics)
        {
            pill.PropertyChanged += OnMetricChanged;
        }

        // The sentinel first, then any canonical filter the document already had enabled, so a
        // stored selection survives until OfferFilters reconciles it against the loaded data.
        //
        // Untinted, deliberately: review finding 2. Resolving a filter colour needs the alias map,
        // and AliasMapCache.Current can fall through to a synchronous SQLite settings read. This
        // constructor runs on the UI thread at page construction, which is exactly where phase
        // review item 2 forbids a settings read. OfferFilters runs after the data has loaded and
        // is where every pill gets its tint.
        var enabledFilters = initial.EnabledFilters;
        AddFilter(OverallFilterKey, Selected(enabledFilters, OverallFilterKey), tinted: false);
        foreach (var name in enabledFilters
            .Where(entry => !IsOverall(entry))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddFilter(name, selected: true, tinted: false);
        }

        // Backing fields, not the properties: setting the properties here would queue a write of
        // the values just read back out of the document.
        _defaultChartSessions = Math.Max(1, initial.DefaultChartSessions);
        _showGuiding = initial.ShowGuiding;
    }

    private bool _showGuiding;

    /// <summary>Phase 24 R3's Guiding pill: <c>graph.show_guiding</c>, written like the metric
    /// and filter choices. A chart reads it through <c>PropertyChanged</c>; no <see cref="Changed"/>
    /// is raised, because the series behind the metric pills are unchanged by it.</summary>
    public bool ShowGuiding
    {
        get => _showGuiding;
        set
        {
            if (!SetProperty(ref _showGuiding, value))
            {
                return;
            }

            var on = value;
            _writer.Write(graph => graph with { ShowGuiding = on });
        }
    }

    /// <summary>One toggle per <see cref="ChartMetrics.All"/> entry, in that order. Fixed length:
    /// the metric list is a compile-time table, so only the selections change.</summary>
    public IReadOnlyList<ToggleOptionViewModel> Metrics { get; }

    /// <summary>The <see cref="OverallFilterKey"/> sentinel plus one entry per canonical filter
    /// present in the data. Replaced wholesale by <see cref="OfferFilters"/>, so it is observable
    /// rather than fixed.</summary>
    public ObservableCollection<ToggleOptionViewModel> Filters { get; } = [];

    /// <summary>
    /// Spec 13's rig pills (PAR-004): one entry per rig present in the loaded data, offered by
    /// <see cref="OfferRigs"/>, all checked when first offered. Empty until something offers a
    /// list, and one entry long on the ordinary single-rig target, where no pill row is drawn.
    /// </summary>
    /// <remarks>
    /// Deliberately not persisted, and there is no <c>graph</c> key for it (spec 13): it names
    /// which of tonight's rigs the reader is looking at, which is not a preference. That is the one
    /// way this list differs from <see cref="Filters"/>, whose every change is written.
    /// </remarks>
    public ObservableCollection<ToggleOptionViewModel> Rigs { get; } = [];

    private int _defaultChartSessions;

    /// <summary>Spec 5.8.3's <c>default_chart_sessions</c>: how many of the newest sessions the
    /// per-session chart opens with. Clamped to at least one, because zero sessions
    /// is a chart with nothing in it and the document is user-editable JSON.</summary>
    public int DefaultChartSessions
    {
        get => _defaultChartSessions;
        set
        {
            if (!SetProperty(ref _defaultChartSessions, Math.Max(1, value)))
            {
                return;
            }

            var sessions = _defaultChartSessions;
            _writer.Write(graph => graph with { DefaultChartSessions = sessions });
        }
    }

    /// <summary>Raised when the selection changed, so each chart rebuilds its series. The write to
    /// settings is queued independently.</summary>
    public event EventHandler? Changed;

    /// <summary>The enabled metrics, in <see cref="ChartMetrics.All"/> order. Bindable: a
    /// <c>PropertyChanged</c> is raised for it whenever a metric toggle changes (review
    /// finding 7).</summary>
    public IReadOnlyList<ChartMetric> EnabledMetrics =>
        [.. Metrics.Where(pill => pill.IsSelected).Select(pill => ChartMetrics.ByKey(pill.Key)!)];

    /// <summary>The enabled filter keys, including <see cref="OverallFilterKey"/> when the
    /// sentinel is on. Bindable: a <c>PropertyChanged</c> is raised for it whenever a filter
    /// toggle changes and whenever <see cref="OfferFilters"/> replaces the list.</summary>
    public IReadOnlyList<string> EnabledFilters =>
        [.. Filters.Where(pill => pill.IsSelected).Select(pill => pill.Key)];

    /// <summary>
    /// Offers the filters the loaded data actually contains, keeping any already-enabled entry
    /// that is still present and dropping ones that are not. Port of the web application's
    /// rig-list reconciliation, applied to filters. The sentinel is always offered.
    /// <para>
    /// An empty offer is refused, here rather than at each call site (FIXER LIST F17). An empty
    /// list is what a page that has not finished loading its sessions looks like, and taking it
    /// literally would drop every enabled filter, persist that loss to <c>graph</c>, and lose the
    /// user's selection permanently. The cost is that a target whose frames carry no FILTER card
    /// at all keeps offering the pills the document already had, which is a stale offer rather
    /// than a destroyed setting.
    /// </para>
    /// </summary>
    public void OfferFilters(IReadOnlyList<string> canonicalFilterNames)
    {
        if (canonicalFilterNames.Count == 0)
        {
            return;
        }

        var selected = Filters
            .Where(pill => pill.IsSelected)
            .Select(pill => pill.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pill in Filters)
        {
            pill.PropertyChanged -= OnFilterChanged;
        }

        Filters.Clear();

        // Tinted from here on: this runs after the data has loaded, off the construction path, so
        // resolving the alias map is safe (review finding 2).
        AddFilter(OverallFilterKey, selected.Contains(OverallFilterKey), tinted: true);
        foreach (var name in canonicalFilterNames
            .Where(entry => !IsOverall(entry))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddFilter(name, selected.Contains(name), tinted: true);
        }

        OnPropertyChanged(nameof(EnabledFilters));

        // Offering the list is not itself a selection change. Changed and the write happen only
        // when the reconciliation actually dropped an enabled entry, so the first load and every
        // post-scan reload cost nothing.
        if (selected.SetEquals(EnabledFilters))
        {
            return;
        }

        PersistFilters();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The enabled rig labels, in the offered order. Bindable, in the shape
    /// <see cref="EnabledFilters"/> has.</summary>
    public IReadOnlyList<string> EnabledRigs =>
        [.. Rigs.Where(pill => pill.IsSelected).Select(pill => pill.Key)];

    /// <summary>True when there is more than one rig to choose between, which is the only case
    /// spec 13 draws a rig pill row in.</summary>
    public bool HasRigPills => Rigs.Count > 1;

    /// <summary>
    /// Offers the rigs the loaded data actually contains, keeping any already-enabled entry that is
    /// still present and defaulting a newly discovered rig to on, which is the same reconciliation
    /// <see cref="OfferFilters"/> performs and the same one the web application's
    /// <c>TargetMetricsChart</c> performs for its own rig list.
    /// </summary>
    /// <remarks>
    /// An empty offer is refused for <see cref="OfferFilters"/>'s reason: an empty list is what a
    /// page that has not finished loading its sessions looks like, and taking it literally would
    /// drop every pill mid-reload. Nothing is persisted either way, so the cost of the refusal is a
    /// stale offer rather than a destroyed setting.
    /// </remarks>
    public void OfferRigs(IReadOnlyList<string> rigLabels)
    {
        if (rigLabels.Count == 0)
        {
            return;
        }

        var offered = rigLabels.Distinct(StringComparer.Ordinal).ToList();

        // Phase review P3-2. Since the Task 5 fix pass this runs on every card Detail publication,
        // which on the ordinary single-rig target is every night the reader opens. Rebuilding the
        // list raises Rigs, and every live chart answers that by rebuilding its pill row and its
        // whole series set, so an unconditional offer cost one rebuild per chart per expansion for
        // a list that had not changed. Ordinal and in order, because the dash pattern is the rig's
        // position and a reordered list is a different offer.
        if (SameRigs(offered))
        {
            return;
        }

        var turnedOff = Rigs
            .Where(pill => !pill.IsSelected)
            .Select(pill => pill.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var pill in Rigs)
        {
            pill.PropertyChanged -= OnRigChanged;
        }

        Rigs.Clear();

        // Untinted: spec 13 fixes the colour to the metric and gives the rig its dash pattern
        // alone, so a rig pill carries a drawn line rather than a dot and needs no brush here.
        foreach (var label in offered)
        {
            var pill = new ToggleOptionViewModel(label, label)
            {
                IsSelected = !turnedOff.Contains(label),
            };
            pill.PropertyChanged += OnRigChanged;
            Rigs.Add(pill);
        }

        OnPropertyChanged(nameof(Rigs));
        OnPropertyChanged(nameof(EnabledRigs));
        OnPropertyChanged(nameof(HasRigPills));
    }

    // Whether the offered list is the one already held: same length, same labels, same order. It
    // deliberately ignores which pills are checked, because a rebuild would reconcile those to the
    // same values anyway and an offer is not a selection change.
    private bool SameRigs(IReadOnlyList<string> offered)
    {
        if (offered.Count != Rigs.Count)
        {
            return false;
        }

        for (var index = 0; index < offered.Count; index++)
        {
            if (!string.Equals(offered[index], Rigs[index].Key, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a rig's series is drawn. A label with no pill of its own reads as enabled,
    /// so a chart built before anything offered a list still draws every rig it has rather than
    /// none.</summary>
    public bool IsRigEnabled(string rigLabel)
    {
        foreach (var pill in Rigs)
        {
            if (string.Equals(pill.Key, rigLabel, StringComparison.Ordinal))
            {
                return pill.IsSelected;
            }
        }

        return true;
    }

    /// <summary>
    /// One canonical filter's configured colour, as the brush the chart pills already carry.
    /// </summary>
    /// <remarks>
    /// The Target detail page draws the same 7 px dot in its log line and in the session pane's
    /// filter table (P12, Task 4 and Task 5). Those surfaces resolve the colour through this method rather than each rebuilding
    /// <c>ParseTint(aliases.FilterColor(name))</c>, which is the fifth occurrence of that pair in
    /// the solution and the point at which it becomes a shared spine (design lesson 1).
    /// <para>
    /// The result is a fresh <see cref="ImmutableSolidColorBrush"/> rather than
    /// <c>ParseTint</c>'s interface-typed instance, so a caller's field can be the concrete type
    /// spec 14.5 names with no cast. Resolving the alias map can fall through to a synchronous
    /// settings read, so this is called after a page's data has loaded, never on the construction
    /// path, which is the same rule <see cref="OfferFilters"/> follows.
    /// </para>
    /// </remarks>
    public ImmutableSolidColorBrush FilterTint(string canonicalFilterName)
        => new(TargetRowViewModel.ParseTint(_aliases().FilterColor(canonicalFilterName)).Color);

    /// <summary>The filter display order over the same alias map <see cref="FilterTint"/> reads,
    /// so a nonstandard canonical name sorts where its dot's colour says it belongs. The map is
    /// resolved once per read, at the sort site, never at construction.</summary>
    public IComparer<string?> FilterComparer => FilterOrder.Comparer(_aliases());

    /// <summary>The fallback dot for a page constructed without a selection to ask, which is a
    /// unit test. The same grey <c>AliasMap.FilterColor</c> hands back for a filter with no
    /// configured colour, so it is user-data space rather than a theme token.</summary>
    public static ImmutableSolidColorBrush FallbackFilterTint { get; } =
        new(Color.Parse(FilterColor.Fallback));

    /// <summary>The tail of the settings write chain, for tests.</summary>
    internal Task PendingPersist => _writer.Pending;

    private static bool IsOverall(string key)
        => string.Equals(key, OverallFilterKey, StringComparison.OrdinalIgnoreCase);

    private static bool Selected(string[] enabled, string key)
        => enabled.Contains(key, StringComparer.OrdinalIgnoreCase);

    // tinted: false skips the alias map entirely, for the construction path. The sentinel never
    // carries a tint even when tinted: its series is coloured by the metric token. A canonical
    // filter is tinted with its configured colour, which is user data rather than a theme token,
    // through the one filter-tint parser (FIXER LIST F10).
    private void AddFilter(string key, bool selected, bool tinted)
    {
        var pill = new ToggleOptionViewModel(
            key,
            key,
            tinted && !IsOverall(key) ? TargetRowViewModel.ParseTint(_aliases().FilterColor(key)) : null)
        {
            IsSelected = selected,
        };
        pill.PropertyChanged += OnFilterChanged;
        Filters.Add(pill);
    }

    private void OnMetricChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToggleOptionViewModel.IsSelected))
        {
            return;
        }

        string[] keys = [.. Metrics.Where(pill => pill.IsSelected).Select(pill => pill.Key)];
        _writer.Write(graph => graph with { EnabledMetrics = keys });
        OnPropertyChanged(nameof(EnabledMetrics));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToggleOptionViewModel.IsSelected))
        {
            return;
        }

        PersistFilters();
        OnPropertyChanged(nameof(EnabledFilters));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Spec 13: the rig selection is per visit, so this raises Changed and writes nothing. There is
    // no graph key for it and no writer call here on purpose.
    private void OnRigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToggleOptionViewModel.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(EnabledRigs));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PersistFilters()
    {
        string[] keys = [.. EnabledFilters];
        _writer.Write(graph => graph with { EnabledFilters = keys });
    }
}
