using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// The right column of spec 12.17's Mosaics page: the mosaics table with its sort, column gear,
/// selection, Delete selected and Create mosaic. Owned by <see cref="MosaicsPageViewModel"/>,
/// which loads it, gates it and runs its bulk job.
/// </summary>
public sealed partial class MosaicsTableViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.17's empty table sentence.</summary>
    public const string NoMosaicsText = "No mosaics yet.";

    /// <summary>Spec 12.17's failed load sentence, shown in place of the rows.</summary>
    public const string LoadFailedText = "The mosaics could not be loaded.";

    private readonly MosaicsPageViewModel _page;
    private readonly DisplayColumnWriter _columnWriter;
    private DisplaySettings _display;
    private IReadOnlyList<CustomColumnDefinition> _definitions = [];
    private bool _disposed;

    internal MosaicsTableViewModel(MosaicsPageViewModel page, DisplaySettings display, DisplayColumnWriter columnWriter)
    {
        _page = page;
        _display = display;
        _columnWriter = columnWriter;
        NewMosaicName = "";
        var sort = display.MosaicsSort;
        SortKey = sort.Key;
        SortAscending = sort.Ascending;
        Picker = ColumnPickerViewModel.ForMosaics(display, columnWriter, []);
        UpdateSortGlyphs();
        columnWriter.Changed += OnColumnsWritten;
    }

    internal MosaicsPageViewModel Page => _page;

    /// <summary>The rows, in the current sort.</summary>
    public ObservableCollection<MosaicRowViewModel> Mosaics { get; } = [];

    /// <summary>The column gear's picker over <c>display.columns.mosaics</c>; its rows also carry
    /// the header cells' visibility and sort glyph.</summary>
    [ObservableProperty]
    public partial ColumnPickerViewModel Picker { get; private set; }

    /// <summary>The shown mosaic-scope custom columns, in display order: the header strip.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CustomColumnDefinition> ShownCustomColumns { get; private set; } = [];

    public string MosaicsHeading => $"Mosaics ({Mosaics.Count})";

    /// <summary>The empty sentence: no rows, and the load did not fail.</summary>
    public bool HasNoMosaics => Mosaics.Count == 0 && !LoadFailed;

    /// <summary>The header and the rows: shown while there are rows and the last load succeeded.</summary>
    public bool ShowRows => Mosaics.Count > 0 && !LoadFailed;

    public bool ShowSelectAllMosaics => Mosaics.Count >= 2 && !LoadFailed;

    /// <summary>True when the last read threw: "The mosaics could not be loaded." with Retry
    /// replaces the rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMosaics), nameof(ShowRows), nameof(ShowSelectAllMosaics))]
    public partial bool LoadFailed { get; private set; }

    [RelayCommand]
    private Task Retry() => _page.ReloadAsync();

    // ---- sorting ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial string SortKey { get; private set; }

    [ObservableProperty]
    public partial bool SortAscending { get; private set; }

    /// <summary>A header click: ascending on a new key, reversed on the same key, written to
    /// <c>display.sort.mosaics</c>. A key outside the five built-ins does not sort.</summary>
    [RelayCommand]
    private void SortBy(string? key)
    {
        if (key is null || !DisplaySettings.MosaicColumnKeys.Contains(key) || _disposed)
        {
            return;
        }

        SortAscending = key != SortKey || !SortAscending;
        SortKey = key;
        UpdateSortGlyphs();
        ApplySort();

        var sort = new TableSort { Key = SortKey, Ascending = SortAscending };
        _display = _display.WithSort(DisplaySettings.MosaicsTableId, sort);
        _columnWriter.Write(display => display.WithSort(DisplaySettings.MosaicsTableId, sort));
    }

    private void UpdateSortGlyphs()
    {
        foreach (var column in Picker.Columns)
        {
            column.SortGlyph = column.Key == SortKey ? (SortAscending ? "▲" : "▼") : "";
        }
    }

    /// <summary>Puts the rows back in the current order: after a rename, an added or removed panel,
    /// or a reload.</summary>
    internal void ApplySort()
    {
        var sorted = Sorted(Mosaics, SortKey, SortAscending);
        for (var index = 0; index < sorted.Count; index++)
        {
            var current = Mosaics.IndexOf(sorted[index]);
            if (current != index)
            {
                Mosaics.Move(current, index);
            }
        }
    }

    /// <summary>Spec 12.17's order: the key ascending or descending, ties by name; Date range by
    /// the first night, a mosaic with none first ascending.</summary>
    internal static IReadOnlyList<MosaicRowViewModel> Sorted(IEnumerable<MosaicRowViewModel> rows, string key, bool ascending)
    {
        Comparison<MosaicRowViewModel> byKey = key switch
        {
            "panels" => (a, b) => a.Panels.CompareTo(b.Panels),
            "integration" => (a, b) => a.IntegrationSeconds.CompareTo(b.IntegrationSeconds),
            "frames" => (a, b) => a.Frames.CompareTo(b.Frames),
            "date_range" => (a, b) => Nullable.Compare(a.FirstNight, b.FirstNight),
            _ => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
        };

        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            var order = byKey(a, b);
            if (!ascending)
            {
                order = -order;
            }

            return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        return list;
    }

    // ---- selection and Delete selected -------------------------------------------------------

    public int SelectedMosaicCount => Mosaics.Count(row => row.IsSelected);

    public bool HasSelectedMosaics => SelectedMosaicCount > 0;

    public string DeleteSelectedText => $"Delete selected ({SelectedMosaicCount})";

    public string DeleteSelectedConfirmText
        => SelectedMosaicCount == 1
            ? "Delete 1 mosaic? Its panels and nights are removed; no frame is touched."
            : $"Delete {SelectedMosaicCount} mosaics? Their panels and nights are removed; no frame is touched.";

    public bool AllMosaicsSelected
    {
        get => Mosaics.Count > 0 && Mosaics.All(row => row.IsSelected);
        set
        {
            foreach (var row in Mosaics)
            {
                row.IsSelected = value;
            }
        }
    }

    [ObservableProperty]
    public partial bool DeleteSelectedPending { get; private set; }

    private bool CanDeleteSelected() => _page.CanAct && HasSelectedMosaics;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedAsync()
    {
        if (!CanDeleteSelected())
        {
            return Task.CompletedTask;
        }

        if (!DeleteSelectedPending)
        {
            DeleteSelectedPending = true;
            return Task.CompletedTask;
        }

        DeleteSelectedPending = false;
        var items = Mosaics.Where(row => row.IsSelected).Select(row => (row.Id, row.Name)).ToList();
        return _page.RunBulkAsync(
            MosaicsPageViewModel.DeleteJobKind, "Delete mosaics", "delete", "Deleted", items,
            item => item.Name, item => _page.Backend.Delete(item.Id));
    }

    [RelayCommand]
    private void CancelDeleteSelected() => DeleteSelectedPending = false;

    internal void OnMosaicSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedMosaicCount));
        OnPropertyChanged(nameof(HasSelectedMosaics));
        OnPropertyChanged(nameof(DeleteSelectedText));
        OnPropertyChanged(nameof(DeleteSelectedConfirmText));
        OnPropertyChanged(nameof(AllMosaicsSelected));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        if (!HasSelectedMosaics)
        {
            DeleteSelectedPending = false;
        }
    }

    /// <summary>A single Delete removed this row (spec 12.17).</summary>
    internal void RemoveMosaicRow(MosaicRowViewModel row)
    {
        Mosaics.Remove(row);
        row.Dispose();
        OnMosaicsChanged();
    }

    // ---- Create mosaic ---------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateButtonText))]
    public partial bool IsCreateOpen { get; private set; }

    public string CreateButtonText => IsCreateOpen ? "Cancel" : "Create mosaic";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string NewMosaicName { get; set; }

    [ObservableProperty]
    public partial string? CreateError { get; private set; }

    partial void OnNewMosaicNameChanged(string value) => CreateError = null;

    [RelayCommand]
    private void ToggleCreate()
    {
        IsCreateOpen = !IsCreateOpen;
        NewMosaicName = "";
        CreateError = null;
    }

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewMosaicName);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        if (!CanCreate() || _disposed)
        {
            return;
        }

        var name = NewMosaicName.Trim();
        CreateError = _page.TryWrite(() => _page.Backend.Create(name));
        if (CreateError is null)
        {
            IsCreateOpen = false;
            NewMosaicName = "";
            _ = _page.ReloadAsync(suggestions: false);
        }
    }

    // ---- loading -------------------------------------------------------------------------

    /// <summary>What one table read returned; null <see cref="Rows"/> means the read threw.</summary>
    internal sealed record Loaded(
        IReadOnlyList<MosaicListRow>? Rows,
        IReadOnlyList<CustomColumnDefinition> Definitions,
        IReadOnlyList<CustomValueRow> Values);

    /// <summary>The table's read, off the UI thread. Never throws.</summary>
    internal Loaded Read()
    {
        try
        {
            var rows = _page.Backend.ListMosaics();
            var definitions = _page.Backend.CustomColumns();
            var values = rows.Count == 0 ? [] : _page.Backend.MosaicValues([.. rows.Select(row => row.Id)]);
            return new Loaded(rows, definitions, values);
        }
        catch (Exception ex)
        {
            _page.Logger.LogWarning(ex, "The mosaics table could not be loaded");
            return new Loaded(null, _definitions, []);
        }
    }

    /// <summary>Replaces the rows, on the UI thread. A failed read hides the old rows behind the
    /// failure sentence.</summary>
    internal void Apply(Loaded loaded)
    {
        if (_disposed)
        {
            return;
        }

        ApplyDefinitions(loaded.Definitions);
        foreach (var row in Mosaics)
        {
            row.Dispose();
        }

        Mosaics.Clear();
        DeleteSelectedPending = false;
        LoadFailed = loaded.Rows is null;
        var values = loaded.Values
            .Where(value => value.Key.MosaicId is not null)
            .ToLookup(value => value.Key.MosaicId!.Value);
        foreach (var row in Sorted((loaded.Rows ?? []).Select(row => new MosaicRowViewModel(row, this, values[row.Id].ToList())), SortKey, SortAscending))
        {
            Mosaics.Add(row);
        }

        OnMosaicsChanged();
    }

    private void OnMosaicsChanged()
    {
        OnPropertyChanged(nameof(MosaicsHeading));
        OnPropertyChanged(nameof(HasNoMosaics));
        OnPropertyChanged(nameof(ShowRows));
        OnPropertyChanged(nameof(ShowSelectAllMosaics));
        OnMosaicSelectionChanged();
    }

    // The picker is rebuilt when the mosaic-scope definitions changed, so a column created on the
    // Custom columns tab reaches the gear on the next reload.
    private void ApplyDefinitions(IReadOnlyList<CustomColumnDefinition> definitions)
    {
        var mosaicScope = CustomColumnSet.MosaicRow(definitions);
        if (!mosaicScope.Select(column => (column.Id, column.Name)).SequenceEqual(
                CustomColumnSet.MosaicRow(_definitions).Select(column => (column.Id, column.Name))))
        {
            Picker.Dispose();
            Picker = ColumnPickerViewModel.ForMosaics(_display, _columnWriter, definitions);
            UpdateSortGlyphs();
        }

        _definitions = definitions;
        RefreshShownCustomColumns();
    }

    private void RefreshShownCustomColumns()
    {
        var visible = Picker.Columns.Where(column => column.IsVisible).Select(column => column.Key).ToHashSet(StringComparer.Ordinal);
        ShownCustomColumns = [.. CustomColumnSet.MosaicRow(_definitions).Where(column => visible.Contains(column.Slug))];
    }

    // A picker toggle (here or another surface) was queued for this table: the custom cells follow.
    private void OnColumnsWritten(string tableId, string[] keys)
    {
        if (_disposed || tableId != DisplaySettings.MosaicsTableId)
        {
            return;
        }

        _display = _display with
        {
            Columns = new Dictionary<string, string[]>(_display.Columns) { [tableId] = keys },
        };
        RefreshShownCustomColumns();
        foreach (var row in Mosaics)
        {
            row.ReconcileCustomCells();
        }
    }

    internal void NotifyGates()
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        foreach (var row in Mosaics)
        {
            row.NotifyGates();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _columnWriter.Changed -= OnColumnsWritten;
        Picker.Dispose();
        foreach (var row in Mosaics)
        {
            row.Dispose();
        }
    }
}
