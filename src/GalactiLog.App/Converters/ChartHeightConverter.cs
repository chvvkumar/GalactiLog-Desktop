using System.Globalization;
using Avalonia.Data.Converters;

namespace GalactiLog.App.Converters;

/// <summary>
/// A chart's height from the height of the viewport it is read in: the viewport less everything
/// else on the page, never below a floor. The one way a chart on a scrolling page grows with the
/// window without being given a star row, which a <c>ScrollViewer</c> over a <c>StackPanel</c>
/// cannot supply because it measures its content with unbounded height.
/// </summary>
/// <remarks>
/// <para>
/// The bound value is the page <c>ScrollViewer</c>'s <c>Viewport.Height</c>, which is its arranged
/// height and does not depend on the content's height in the scroll direction, so the binding adds
/// one layout pass and no cycle. The parameter is what the page spends on everything that is not
/// this chart, which each caller measures for its own page and declares as a named constant beside
/// the binding.
/// </para>
/// <para>
/// Usage, verbatim:
/// <code>
/// Height="{Binding $parent[ScrollViewer].Viewport.Height,
///                  Converter={StaticResource ChartHeight},
///                  ConverterParameter={x:Static analysis:CorrelationTabViewModel.ChartFixedSpend}}"
/// </code>
/// A view declares one instance in its own <c>Resources</c>, which is what
/// <see cref="MultiplyConverter"/> does and for the reason its own remark gives: the converter is
/// stateless, so an instance per view costs nothing and the application resource dictionary stays
/// the theme's.
/// </para>
/// <para>
/// A value or parameter that is not a number converts to <c>BindingOperations.DoNothing</c>, which
/// is <see cref="MultiplyConverter"/>'s failure rule too: a mistyped parameter leaves whatever
/// height the control already had rather than collapsing the chart to nothing.
/// </para>
/// </remarks>
public sealed class ChartHeightConverter : IValueConverter
{
    /// <summary>The smallest height this converter will answer, whatever the arithmetic says. A
    /// chart below this is unreadable, and at the two smaller standard windows the subtraction
    /// lands under it. The figure is the smallest of the page's other charts: at the 180 it held
    /// before, a launched look read two tick labels on the value axis and could not tell the
    /// confidence band from the trend line.</summary>
    public const double MinimumHeight = 280d;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double viewport || !TryFixedSpend(parameter, out var spend))
        {
            return Avalonia.Data.BindingOperations.DoNothing;
        }

        // A viewport of zero or NaN is what a control reports before its first arrange, so the
        // floor is also what the chart is given on the way up.
        return double.IsFinite(viewport) ? Math.Max(MinimumHeight, viewport - spend) : MinimumHeight;
    }

    /// <summary>Not supported: a chart height is a one-way projection of the viewport.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;

    private static bool TryFixedSpend(object? parameter, out double spend)
    {
        switch (parameter)
        {
            case double value:
                spend = value;
                return true;
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                spend = parsed;
                return true;
            default:
                spend = 0d;
                return false;
        }
    }
}
