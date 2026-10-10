using System.ComponentModel;
using Avalonia.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Views.Mosaics;

/// <summary>
/// Spec 12.17's Mosaics page, bound to <see cref="MosaicsPageViewModel"/>. All behaviour is in the
/// view-model; this file carries one rule the markup cannot express: a built-in column the gear
/// hides is dropped through <see cref="TableColumn.IsDropped"/>, which takes it out of its shared
/// size group as well as hiding its cells (a group only grows, so a hidden cell alone would leave
/// its width behind).
/// </summary>
public partial class MosaicsView : UserControl
{
    // ponytail: a deleted mosaic's wider cells keep their column width until the next text size
    // change or page visit; reset on a Mosaics removal if that is ever visible.
    private readonly TableColumns _cols;
    private MosaicsTableViewModel? _table;
    private ColumnPickerViewModel? _picker;

    public MosaicsView()
    {
        InitializeComponent();
        _cols = (TableColumns)Resources["MosaicCols"]!;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_table is not null)
        {
            _table.PropertyChanged -= OnTableChanged;
        }

        _table = (DataContext as MosaicsPageViewModel)?.Table;

        if (_table is not null)
        {
            _table.PropertyChanged += OnTableChanged;
        }

        FollowPicker();
        base.OnDataContextChanged(e);
    }

    // The picker is replaced when the custom columns load.
    private void OnTableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MosaicsTableViewModel.Picker))
        {
            FollowPicker();
        }
    }

    private void FollowPicker()
    {
        foreach (var column in _picker?.Columns ?? [])
        {
            column.PropertyChanged -= OnColumnChanged;
        }

        _picker = _table?.Picker;

        foreach (var column in _picker?.Columns ?? [])
        {
            column.PropertyChanged += OnColumnChanged;
        }

        Sync();
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        foreach (var column in (_picker?.Columns ?? [])
                     .Where(column => DisplaySettings.MosaicColumnKeys.Contains(column.Key)))
        {
            _cols[column.TableKey].IsDropped = !column.IsVisible;
        }
    }
}
