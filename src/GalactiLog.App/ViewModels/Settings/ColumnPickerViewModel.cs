using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>One heading group of <see cref="ColumnPickerViewModel.Groups"/>: "Built-in" or
/// "Custom" (spec 12.15). Declared beside the picker since nothing else needs it.</summary>
public sealed record ColumnGroupViewModel(string Heading, IReadOnlyList<ColumnViewModel> Columns);

/// <summary>
/// Design-spec 12.7's "a column picker per table, writing <c>display.columns</c>". One instance
/// per persisted table on the Display tab, over the same <see cref="ColumnViewModel"/> rows the
/// dashboard and the frame table already use.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes, because the two tables have different lifetimes and the difference is load
/// bearing. <see cref="ForDashboard"/> is handed the live target list's own column rows and its
/// own toggle: the dashboard is a DI singleton, so there is exactly one dashboard column state in
/// the process and the Settings picker and the in-header picker are the same thing rather than two
/// copies that have to be kept in step. <see cref="ForFrames"/> builds its own rows, because frame
/// tables are transient and there can be several of them at once; it writes through
/// <see cref="DisplayColumnWriter"/> and follows that writer's <c>Changed</c> event, which is the
/// same mechanism a live frame table already uses to adopt another one's toggle.
/// </para>
/// <para>
/// Every write goes through <see cref="DisplayColumnWriter"/>, never
/// <c>SettingsStore.SaveDisplay</c> directly: that writer is the one serialized chain for
/// <c>display.columns</c> and two chains would drop one of two nearly simultaneous toggles.
/// </para>
/// <para>
/// Design-spec 5.8.2: an empty <c>columns.frames</c> array is a legal state that must not throw.
/// Unchecking every frame column is allowed and writes an empty list; the picker is the way back
/// out of it. The dashboard's <c>name</c> column is the one column that cannot be hidden
/// (design-spec 12.2).
/// </para>
/// <para>
/// Phase 20 Task 6c adds a third shape, <see cref="ForLedger"/>, for the Nights ledger's own
/// column list, and one grouping every shape shares: <see cref="Groups"/> splits whichever columns
/// a picker holds into "Built-in" and "Custom" (spec 12.15), so a fourth picker inherits the split
/// for free instead of a fifth ad hoc filter (design lesson 1).
/// </para>
/// </remarks>
public sealed partial class ColumnPickerViewModel : ObservableObject, IDisposable
{
    private readonly Action<ColumnViewModel> _toggle;
    private readonly Action<Action<string, string[]>>? _unsubscribeChanged;
    private readonly ObservableCollection<ColumnGroupViewModel> _groups = [];
    private readonly INotifyCollectionChanged? _liveColumns;

    // True for ForLedger, whose table id lists the slugs switched OFF, so a written list is read
    // inverted in OnColumnsChanged.
    private readonly bool _keysAreHidden;
    private bool _disposed;

    private ColumnPickerViewModel(
        string tableId,
        string title,
        IReadOnlyList<ColumnViewModel> columns,
        Action<ColumnViewModel> toggle,
        Action<Action<string, string[]>>? subscribeChanged = null,
        Action<Action<string, string[]>>? unsubscribeChanged = null,
        bool keysAreHidden = false)
    {
        TableId = tableId;
        _keysAreHidden = keysAreHidden;
        Title = title;
        Columns = columns;
        RebuildGroups();
        _toggle = toggle;
        _unsubscribeChanged = unsubscribeChanged;
        subscribeChanged?.Invoke(OnColumnsChanged);

        // ForDashboard is handed the live target list's own collection, and a custom column defined
        // while the application runs is appended to it. The groups are a projection of it, so they
        // follow it; the other two factories hand in a materialized array and this is null.
        if (columns is INotifyCollectionChanged live)
        {
            _liveColumns = live;
            live.CollectionChanged += OnLiveColumnsChanged;
        }
    }

    /// <summary>The <c>display.columns</c> table id this picker writes.</summary>
    public string TableId { get; }

    /// <summary>The section heading.</summary>
    public string Title { get; }

    /// <summary>Every column of the table, in its documented order, whatever its visibility or
    /// metric-group gate.</summary>
    public IReadOnlyList<ColumnViewModel> Columns { get; }

    /// <summary>
    /// Spec 12.15: the picker lists custom columns under a "Custom" heading below the built-in
    /// ones. Two groups at most, and the second is absent while no custom column is shown, so a
    /// reader who has defined none sees exactly what this picker showed before Phase 20. Built over
    /// the same <see cref="ColumnViewModel"/> instances <see cref="Columns"/> holds, so a toggle
    /// raised through either group reaches the one object the other holds too (design lesson: no
    /// second enumeration to keep in step), and rebuilt whenever that collection is a live one and
    /// it changes, so a column created while the application runs reaches the heading it belongs
    /// under.
    /// </summary>
    public IReadOnlyList<ColumnGroupViewModel> Groups => _groups;

    /// <summary>Whether the view should draw a group heading at all: false while there is only
    /// one group, which is every picker's state before Phase 20 and every picker with no custom
    /// column today.</summary>
    public bool ShowGroupHeadings => Groups.Count > 1;

    /// <summary>True while this picker holds no column at all. Only <see cref="ForLedger"/> can be
    /// empty: the Nights ledger has no un-hideable built-in column the way the dashboard's
    /// <c>name</c> and every frame column do, so a library with no session-scope custom column
    /// leaves it with nothing to show (amendment 2.9).</summary>
    public bool IsEmpty => Columns.Count == 0;

    /// <summary>Amendment 2.9's empty-state sentence, verbatim, for the one picker
    /// <see cref="IsEmpty"/> can be true for.</summary>
    public const string EmptyMessage = "No custom columns yet.";

    /// <summary>Instance access to <see cref="EmptyMessage"/>, because a compiled binding's
    /// property path resolves against an instance and cannot reach a <c>const</c> member.</summary>
    public string EmptyStateText => EmptyMessage;

    /// <summary>
    /// Turns one column on or off. The guard is repeated here and not only in
    /// <c>CanExecute</c>, because <c>RelayCommand.Execute</c> ignores <c>CanExecute</c>
    /// (TRACKING.md section 6 item 13).
    /// </summary>
    [RelayCommand]
    private void Toggle(ColumnViewModel? column)
    {
        if (column is null || _disposed)
        {
            return;
        }

        _toggle(column);
    }

    /// <summary>
    /// Design-spec 12.2's dashboard target list, over the live list's own rows and its own toggle.
    /// </summary>
    /// <param name="liveColumns"><c>TargetListViewModel.Columns</c>.</param>
    /// <param name="toggle"><c>TargetListViewModel.ToggleColumnCommand.Execute</c>, which applies
    /// design-spec 12.2's un-hideable <c>name</c> rule, raises the list's own
    /// <c>VisibleColumns</c> and persists through the shared writer.</param>
    public static ColumnPickerViewModel ForDashboard(IReadOnlyList<ColumnViewModel> liveColumns, Action<ColumnViewModel> toggle)
        => new(DisplaySettings.DashboardTableId, "Dashboard columns", liveColumns, toggle);

    /// <summary>
    /// Design-spec 12.4's frame table. Builds its own rows from
    /// <c>FrameColumns.All</c>, seeded from the writer's last queued list when this process has
    /// written one and from the display document otherwise, and gated by
    /// <c>FrameColumns.IsGroupEnabled</c>.
    /// </summary>
    public static ColumnPickerViewModel ForFrames(DisplaySettings display, DisplayColumnWriter writer)
    {
        // LastWritten first, for the reason FrameTableViewModel states: the display document a tab
        // read is a snapshot, and it is stale the moment any table toggles a column.
        var visible = writer.LastWritten(DisplaySettings.FramesTableId)
            ?? display.ColumnsFor(DisplaySettings.FramesTableId);

        // Ruling Q15: every frame column is hideable, and an empty list is legal.
        var columns = FrameColumns.All
            .Select(column => new ColumnViewModel(
                column.Key,
                column.Title,
                visible.Contains(column.Key, StringComparer.Ordinal),
                canHide: true,
                isGroupEnabled: FrameColumns.IsGroupEnabled(column, display)))
            .ToArray();

        ColumnPickerViewModel? picker = null;
        picker = new ColumnPickerViewModel(
            DisplaySettings.FramesTableId,
            "Frame table columns",
            columns,
            column =>
            {
                // A column whose metric group is off renders nothing, so toggling it would be a
                // click with no visible effect. The frame table's own header picker refuses it for
                // the same reason.
                if (!column.IsGroupEnabled)
                {
                    return;
                }

                column.IsVisible = !column.IsVisible;
                writer.Write(
                    DisplaySettings.FramesTableId,
                    [.. picker!.Columns.Where(entry => entry.IsVisible).Select(entry => entry.Key)]);
            },
            subscribeChanged: handler => writer.Changed += handler,
            unsubscribeChanged: handler => writer.Changed -= handler);

        return picker;
    }

    /// <summary>
    /// Spec 12.4 and 12.15's "Nights ledger columns" (ruling C4). Writes
    /// <see cref="DisplaySettings.LedgerHiddenTableId"/>, the custom slugs switched off: the
    /// ledger's built-in columns are not hideable and are not in it (spec 5.8.2's Phase 20 note).
    /// Every column starts shown, because <see cref="CustomColumnSet.IsShown"/> answers true for a
    /// slug in no hidden list.
    /// </summary>
    /// <param name="all">Every custom column definition, of every scope. Filtered to session scope
    /// here through <see cref="CustomColumnSet.NightExpander"/>, the same filter the ledger row
    /// itself uses to choose which columns exist at all, so a column deleted since this picker was
    /// built simply has no row here and its slug is not in the next list this picker writes (spec
    /// 5.8.2's Phase 20 note on an inert slug: it needs no code of its own beyond building rows
    /// from the live definitions).</param>
    public static ColumnPickerViewModel ForLedger(
        DisplaySettings display, DisplayColumnWriter writer, IReadOnlyList<CustomColumnDefinition> all)
    {
        // LastWritten first, the same reason ForFrames states: the display document a tab read is
        // a snapshot, and it is stale the moment any table toggles a column.
        var hidden = writer.LastWritten(DisplaySettings.LedgerHiddenTableId)
            ?? display.ColumnsFor(DisplaySettings.LedgerHiddenTableId);

        var columns = CustomColumnSet.NightExpander(all)
            .Select(column => new ColumnViewModel(
                column.Slug,
                column.Name,
                CustomColumnSet.IsShown(hidden, column.Slug),
                canHide: true))
            .ToArray();

        ColumnPickerViewModel? picker = null;
        picker = new ColumnPickerViewModel(
            DisplaySettings.LedgerHiddenTableId,
            "Nights list columns",
            columns,
            column =>
            {
                column.IsVisible = !column.IsVisible;
                writer.Write(
                    DisplaySettings.LedgerHiddenTableId,
                    [.. picker!.Columns.Where(entry => !entry.IsVisible).Select(entry => entry.Key)]);
            },
            subscribeChanged: handler => writer.Changed += handler,
            unsubscribeChanged: handler => writer.Changed -= handler,
            keysAreHidden: true);

        return picker;
    }

    /// <summary>The built-in titles of spec 12.17's mosaics table, by key.</summary>
    internal static readonly IReadOnlyDictionary<string, string> MosaicColumnTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["name"] = "Name",
        ["panels"] = "Panels",
        ["integration"] = "Integration",
        ["frames"] = "Frames",
        ["date_range"] = "Date range",
    };

    /// <summary>
    /// Spec 12.17's mosaics table gear (Phase 18): the five built-in columns with Name ticked and
    /// disabled, then the mosaic-scope custom columns under "Custom". Writes
    /// <c>display.columns.mosaics</c> through the shared writer, the shape <see cref="ForLedger"/>
    /// has, so a column deleted since the picker was built has no row and drops out of the next list.
    /// </summary>
    public static ColumnPickerViewModel ForMosaics(
        DisplaySettings display, DisplayColumnWriter writer, IReadOnlyList<CustomColumnDefinition> all)
    {
        var visible = writer.LastWritten(DisplaySettings.MosaicsTableId)
            ?? display.ColumnsFor(DisplaySettings.MosaicsTableId);

        var columns = DisplaySettings.MosaicColumnKeys
            .Select(key => new ColumnViewModel(
                key,
                MosaicColumnTitles[key],
                visible.Contains(key, StringComparer.Ordinal),
                canHide: key != "name",
                isNumeric: key is "panels" or "integration" or "frames"))
            .Concat(CustomColumnSet.MosaicRow(all).Select(column => new ColumnViewModel(
                column.Slug,
                column.Name,
                visible.Contains(column.Slug, StringComparer.Ordinal))))
            .ToArray();

        ColumnPickerViewModel? picker = null;
        picker = new ColumnPickerViewModel(
            DisplaySettings.MosaicsTableId,
            "Mosaics columns",
            columns,
            column =>
            {
                if (!column.CanHide)
                {
                    return;
                }

                column.IsVisible = !column.IsVisible;
                writer.Write(
                    DisplaySettings.MosaicsTableId,
                    [.. picker!.Columns.Where(entry => entry.IsVisible).Select(entry => entry.Key)]);
            },
            subscribeChanged: handler => writer.Changed += handler,
            unsubscribeChanged: handler => writer.Changed -= handler);

        return picker;
    }

    // Split by the slug prefix alone (design lesson 1: no second definition list, no lookup). At
    // most two non-empty groups, "Built-in" first: ForLedger's rows are all custom, so it never
    // produces a "Built-in" group at all, and ForFrames's are all built-in today, so it never
    // produces a "Custom" one. ForDashboard is the one factory that can hold both, once Task 5a
    // appends the dashboard's own target-scope custom columns to the live list it hands in here.
    private static IReadOnlyList<ColumnGroupViewModel> BuildGroups(IReadOnlyList<ColumnViewModel> columns)
    {
        var builtIn = columns.Where(column => !CustomColumnSlug.IsCustom(column.Key)).ToArray();
        var custom = columns.Where(column => CustomColumnSlug.IsCustom(column.Key)).ToArray();

        List<ColumnGroupViewModel> groups = [];
        if (builtIn.Length > 0)
        {
            groups.Add(new ColumnGroupViewModel("Built-in", builtIn));
        }

        if (custom.Length > 0)
        {
            groups.Add(new ColumnGroupViewModel("Custom", custom));
        }

        return groups;
    }

    // Replaced in place rather than reassigned, because the views bind the one collection instance.
    // ShowGroupHeadings is computed from the group count, so it is raised here by hand: it has no
    // backing field for the generator to notice.
    private void RebuildGroups()
    {
        _groups.Clear();
        foreach (var group in BuildGroups(Columns))
        {
            _groups.Add(group);
        }

        OnPropertyChanged(nameof(ShowGroupHeadings));
    }

    private void OnLiveColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_disposed)
        {
            RebuildGroups();
        }
    }

    /// <summary>
    /// Re-seeds the persisted half of design-spec 5.8.2's two gates from an ordered visible-key
    /// list. Touches only <see cref="ColumnViewModel.IsVisible"/>, so the metric-group gate is
    /// left where <see cref="ApplyGroupGates"/> put it.
    /// </summary>
    public void ApplyVisible(IReadOnlyList<string> keys)
    {
        foreach (var column in Columns)
        {
            column.IsVisible = keys.Contains(column.Key, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Re-applies design-spec 5.8.2's metric-group gate after the Display tab saves
    /// <c>display.groups</c>, or after another writer does. Touches only
    /// <see cref="ColumnViewModel.IsGroupEnabled"/>: the persisted visible list carries gated keys
    /// too, and the two gates stay separate.
    /// </summary>
    public void ApplyGroupGates(DisplaySettings display)
    {
        foreach (var column in FrameColumns.All)
        {
            var row = Columns.FirstOrDefault(entry => entry.Key == column.Key);
            if (row is not null)
            {
                row.IsGroupEnabled = FrameColumns.IsGroupEnabled(column, display);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _unsubscribeChanged?.Invoke(OnColumnsChanged);
        if (_liveColumns is not null)
        {
            _liveColumns.CollectionChanged -= OnLiveColumnsChanged;
        }
    }

    // A live frame table (or another picker) queued a write for this table. Adopt its list
    // verbatim: it is the document as it will be persisted, and the persisted list carries gated
    // keys too, so IsGroupEnabled is left alone and the gate re-applies itself through IsShown.
    // Only the columns half is touched here; the groups half arrives through ApplyGroupGates, so
    // a subscriber to both does not apply the same click twice.
    private void OnColumnsChanged(string tableId, string[] keys)
    {
        if (_disposed || !string.Equals(tableId, TableId, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var column in Columns)
        {
            column.IsVisible = _keysAreHidden
                ? CustomColumnSet.IsShown(keys, column.Key)
                : keys.Contains(column.Key, StringComparer.Ordinal);
        }
    }
}
