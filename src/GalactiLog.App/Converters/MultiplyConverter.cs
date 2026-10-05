using System.Globalization;
using Avalonia.Data.Converters;

namespace GalactiLog.App.Converters;

/// <summary>
/// Multiplies a bound double by a double converter parameter. The one way a view reaches
/// design-spec 14.4's font size tiers now that <c>MainWindow.FontSize</c> carries the root size
/// (questions.md Q24, FIXER item 2).
/// </summary>
/// <remarks>
/// <para>
/// <c>Scales.axaml</c>'s four <c>FontSize*</c> keys are ratios to the root size (0.500, 0.643,
/// 0.714, 0.786), not point sizes, so binding one straight to a <c>FontSize</c> renders text at
/// well under a pixel. <c>FontSizeTokenTest</c> fails the build on exactly that and is unchanged
/// by this task: its two patterns match a <c>FontSize</c> attribute whose whole value is
/// <c>{DynamicResource FontSize*}</c> or <c>{StaticResource FontSize*}</c>, and the form below
/// starts with <c>{Binding</c>, so it passes.
/// </para>
/// <para>
/// Usage, verbatim:
/// <code>
/// FontSize="{Binding $parent[Window].FontSize,
///                    Converter={StaticResource Multiply},
///                    ConverterParameter={StaticResource FontSizeLabel}}"
/// </code>
/// A view declares one instance in its own <c>Resources</c> rather than in <c>App.axaml</c>: the
/// converter is stateless, so an instance per view costs nothing, and the application resource
/// dictionary stays the theme's.
/// </para>
/// <para>
/// A value or parameter that is not a number converts to <c>BindingOperations.DoNothing</c>, so a
/// mistyped parameter leaves the inherited size in place rather than collapsing the text to zero.
/// </para>
/// </remarks>
public sealed class MultiplyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double size || !TryRatio(parameter, out var ratio))
        {
            return Avalonia.Data.BindingOperations.DoNothing;
        }

        return size * ratio;
    }

    /// <summary>Not supported: a font size tier is a one-way projection of the root size.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;

    private static bool TryRatio(object? parameter, out double ratio)
    {
        switch (parameter)
        {
            case double value:
                ratio = value;
                return true;
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                ratio = parsed;
                return true;
            default:
                ratio = 0d;
                return false;
        }
    }
}
