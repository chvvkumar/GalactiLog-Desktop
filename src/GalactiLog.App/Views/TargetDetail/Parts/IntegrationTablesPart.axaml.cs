using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace GalactiLog.App.Views.TargetDetail.Parts;

public partial class IntegrationTablesPart : UserControl
{
    public IntegrationTablesPart() => InitializeComponent();
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
