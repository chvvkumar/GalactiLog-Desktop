using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// One row of spec 12.17's mosaics table: the figures, the custom cells, the selection, Delete
/// behind the two-press confirm, and the expanded part with Rename, the panel list and the add
/// panel form. A click on the row opens the mosaic detail page through the page's seam.
/// </summary>
public sealed partial class MosaicRowViewModel : ObservableObject, IDisposable
{
    /// <summary>The single Delete's confirm sentence.</summary>
    public const string DeleteConfirmText = "Delete this mosaic? Its panels and nights are removed; no frame is touched.";

    /// <summary>The panel list's empty state.</summary>
    public const string NoPanelsText = "No panels yet.";

    private readonly MosaicsTableViewModel _table;
    private readonly MosaicsPageViewModel _page;
    private IReadOnlyList<CustomValueRow> _values;
    private CustomCellGroup _cells = CustomCellGroup.Empty;
    private bool _disposed;

    internal MosaicRowViewModel(MosaicListRow row, MosaicsTableViewModel table, IReadOnlyList<CustomValueRow> values)
    {
        _table = table;
        _page = table.Page;
        _values = values;
        Id = row.Id;
        Name = row.Name;
        RenameText = "";
        ApplyFigures(row.Panels, row.IntegrationSeconds, row.Frames, row.FirstNight, row.LastNight);
        ReconcileCustomCells();
    }

    public Guid Id { get; }

    [ObservableProperty]
    public partial string Name { get; private set; }

    public int Panels { get; private set; }

    public double IntegrationSeconds { get; private set; }

    public int Frames { get; private set; }

    public DateOnly? FirstNight { get; private set; }

    public DateOnly? LastNight { get; private set; }

    public string PanelsText => MetricText.Count(Panels);

    public string IntegrationText => MetricText.Integration(IntegrationSeconds);

    public string FramesText => MetricText.Count(Frames);

    /// <summary>"yyyy-MM-dd to yyyy-MM-dd", one date when they are equal, empty with no frame.</summary>
    public string DateRangeText => (FirstNight, LastNight) switch
    {
        ({ } first, { } last) when first == last => MetricText.Date(first),
        ({ } first, { } last) => $"{MetricText.Date(first)} to {MetricText.Date(last)}",
        _ => "",
    };

    private void ApplyFigures(int panels, double seconds, int frames, DateOnly? first, DateOnly? last)
    {
        (Panels, IntegrationSeconds, Frames, FirstNight, LastNight) = (panels, seconds, frames, first, last);
        OnPropertyChanged(nameof(PanelsText));
        OnPropertyChanged(nameof(IntegrationText));
        OnPropertyChanged(nameof(FramesText));
        OnPropertyChanged(nameof(DateRangeText));
    }

    /// <summary>One cell per shown mosaic-scope custom column (spec 12.15's shared editor).</summary>
    public IReadOnlyList<CustomValueViewModel> CustomCells => _cells.Cells;

    internal void ReconcileCustomCells()
    {
        var columns = _table.ShownCustomColumns;
        _cells = CustomCellFactory.Reconcile(
            _cells,
            columns,
            CustomValueKey.ForMosaic(Id),
            Name,
            column => _values.FirstOrDefault(value => value.ColumnId == column.Id)?.Value,
            _page.Backend.WriteValue,
            _page.Delay,
            _page.Post,
            _page.Logger);
        OnPropertyChanged(nameof(CustomCells));
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _table.OnMosaicSelectionChanged();

    /// <summary>A click on the row outside its controls (spec 12.17).</summary>
    [RelayCommand]
    private void Open() => _page.RequestOpenMosaic(Id);

    // ---- Delete --------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool DeletePending { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    private bool CanDelete() => _page.CanAct;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (!_page.CanAct)
        {
            return;
        }

        if (!DeletePending)
        {
            DeletePending = true;
            return;
        }

        DeletePending = false;
        Error = _page.TryWrite(() => _page.Backend.Delete(Id));
        if (Error is null)
        {
            _table.RemoveMosaicRow(this);
        }
    }

    [RelayCommand]
    private void CancelDelete() => DeletePending = false;

    internal void NotifyGates()
    {
        DeleteCommand.NotifyCanExecuteChanged();
        foreach (var panel in PanelLines)
        {
            panel.RemoveCommand.NotifyCanExecuteChanged();
        }
    }

    // ---- the expanded part -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandText))]
    public partial bool IsExpanded { get; private set; }

    public string ExpandText => IsExpanded ? "Collapse" : "Expand";

    /// <summary>The add panel form, built on the first expand.</summary>
    [ObservableProperty]
    public partial AddPanelViewModel? AddPanel { get; private set; }

    public ObservableCollection<PanelLineViewModel> PanelLines { get; } = [];

    public bool HasNoPanels => PanelLines.Count == 0;

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded)
        {
            AddPanel ??= new AddPanelViewModel(
                Id, [], _page.Backend.SearchTargets, _page.Backend.AddPanelWithTarget, RefreshDetail,
                _page.Delay, _page.Post, _page.Logger);
            RefreshDetail();
        }
    }

    /// <summary>Re-reads this mosaic: the panel list, the figures and the add form's prefill.</summary>
    internal void RefreshDetail()
    {
        MosaicDetail? detail = null;
        Error = _page.TryWrite(() => detail = _page.Backend.Detail(Id));
        if (detail is null)
        {
            return;
        }

        PanelLines.Clear();
        foreach (var panel in detail.Panels)
        {
            PanelLines.Add(new PanelLineViewModel(panel, this));
        }

        OnPropertyChanged(nameof(HasNoPanels));
        ApplyFigures(detail.Panels.Count, detail.IntegrationSeconds, detail.Frames, detail.FirstNight, detail.LastNight);
        AddPanel?.SetLabels([.. detail.Panels.Select(panel => panel.Label)]);

        // The figures may have moved, so the row's place in the table may have too.
        _table.ApplySort();
    }

    internal bool CanAct => _page.CanAct;

    internal string? RemovePanel(PanelDetail panel)
    {
        var error = _page.TryWrite(() => _page.Backend.RemovePanel(panel.Id));
        if (error is null)
        {
            RefreshDetail();
        }

        return error;
    }

    // Rename.

    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    public partial string RenameText { get; set; }

    [ObservableProperty]
    public partial string? RenameError { get; private set; }

    partial void OnRenameTextChanged(string value) => RenameError = null;

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Name;
        RenameError = null;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameError = null;
    }

    /// <summary>Enter or Save. The name sentences of spec 12.17 refuse a blank or taken name.</summary>
    [RelayCommand]
    private void SaveRename()
    {
        var name = RenameText.Trim();
        if (name.Length == 0)
        {
            RenameError = MosaicMessages.EmptyName;
            return;
        }

        RenameError = _page.TryWrite(() => _page.Backend.Rename(Id, name));
        if (RenameError is null)
        {
            Name = name;
            IsRenaming = false;
            _table.ApplySort();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cells.Dispose();
        AddPanel?.Dispose();
    }
}

/// <summary>One line of an expanded row's panel list (spec 12.17): the label, the targets, the
/// figures, and Remove behind the two-press confirm.</summary>
public sealed partial class PanelLineViewModel : ObservableObject
{
    private readonly MosaicRowViewModel _row;

    internal PanelLineViewModel(PanelDetail panel, MosaicRowViewModel row)
    {
        Panel = panel;
        _row = row;
    }

    public PanelDetail Panel { get; }

    public string Label => Panel.Label;

    public string TargetsText => string.Join(", ", Panel.TargetNames);

    public string IntegrationText => MetricText.Integration(Panel.IntegrationSeconds);

    public string FramesText => MosaicsPageViewModel.Plural(Panel.Frames, "frame");

    public string RemoveConfirmText => $"Remove panel {Panel.Label}? Its nights leave this mosaic; no frame is touched.";

    [ObservableProperty]
    public partial bool RemovePending { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    private bool CanRemove() => _row.CanAct;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (!_row.CanAct)
        {
            return;
        }

        if (!RemovePending)
        {
            RemovePending = true;
            return;
        }

        RemovePending = false;
        Error = _row.RemovePanel(Panel);
    }

    [RelayCommand]
    private void CancelRemove() => RemovePending = false;
}
