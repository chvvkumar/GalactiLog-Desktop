using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail.Parts;

public partial class IntegrationTablesPart : UserControl
{
    private TargetDetailViewModel? _page;

    public IntegrationTablesPart() => InitializeComponent();

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

    // A shared column only grows, so a narrower matrix or a smaller text size renames the groups
    // and measures them afresh (spine-spec 3.3). An inherited text size can arrive before
    // InitializeComponent has loaded the resources.
    private void ResetColumns()
    {
        foreach (var key in (string[])["OverallCols", "MatrixCols"])
        {
            if (Resources.TryGetValue(key, out var columns) && columns is TableColumns set)
            {
                set.Reset();
            }
        }
    }
}

/// <summary>A column width or row height pinned at the 14 px text size, growing
/// in step with the text above it so no text size overruns its column or clips its row. The parameter
/// is the pinned value.</summary>
public sealed class FontScale : IValueConverter
{
    public static readonly FontScale Instance = new();

    private const double BaseFontSize = 14d;

    public static double Of(double pinned, double fontSize) => pinned * Math.Max(1d, fontSize / BaseFontSize);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Of(double.Parse((string)parameter!, CultureInfo.InvariantCulture), (double)value!);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
