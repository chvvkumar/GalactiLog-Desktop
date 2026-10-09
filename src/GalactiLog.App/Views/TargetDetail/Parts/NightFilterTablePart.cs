using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail.Parts;

/// <summary>
/// A part whose tables read <see cref="TargetDetailViewModel.NightFilterMatrix"/> (Integration and
/// Compare nights). A shared column only grows, so every <see cref="TableColumns"/> in the part's
/// resources is reset when the matrix is replaced or the text size changes, and a column shrinks
/// after a narrower matrix or a smaller text size (spine-spec 3.3).
/// </summary>
public class NightFilterTablePart : UserControl
{
    private TargetDetailViewModel? _page;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPagePropertyChanged;
        }

        _page = DataContext as TargetDetailViewModel;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPagePropertyChanged;
        }

        ResetColumns();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty)
        {
            ResetColumns();
        }
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetDetailViewModel.NightFilterMatrix))
        {
            ResetColumns();
        }
    }

    // Read through TryGetValue, which resolves a deferred resource; an inherited text size can
    // arrive before InitializeComponent has loaded any.
    private void ResetColumns()
    {
        foreach (var key in Resources.Keys.ToList())
        {
            if (Resources.TryGetValue(key, out var value) && value is TableColumns columns)
            {
                columns.Reset();
            }
        }
    }
}
