using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GalactiLog.App.ViewModels.Analysis;
using SkiaSharp;

namespace GalactiLog.App.Converters;

/// <summary>
/// A filter chip's label ink: the filter's own colour when it reads against the surface the chip
/// sits on, at a WCAG contrast of at least 3:1, and otherwise no value at all, so the label falls
/// back to the theme's primary ink its host provides. The wash and the edge keep the filter colour
/// either way. One rule for the filter panel's pills and the target list's palette badges.
/// </summary>
/// <remarks>
/// The default L colour (#e0e0e0) is about 1.3:1 on Atlas's surface and 1.1:1 on Logbook's, so a
/// label in the tint alone vanished on both light themes. The surface arrives as the second value
/// of a MultiBinding, a DynamicResource the host carries on its Tag, so a theme swap re-runs the
/// choice.
/// </remarks>
public sealed class TintInkConverter : IMultiValueConverter
{
    /// <summary>The large-text and UI-component floor in WCAG 2.x, the ruling's figure.</summary>
    public const double MinimumContrast = 3d;

    public static readonly TintInkConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count == 2 && values[0] is ISolidColorBrush tint && values[1] is ISolidColorBrush surface
            && Reads(tint.Color, surface.Color)
            ? tint
            : AvaloniaProperty.UnsetValue;

    /// <summary>Whether <paramref name="tint"/> is legible as text over <paramref name="surface"/>.</summary>
    public static bool Reads(Color tint, Color surface)
        => InkContrast.Contrast(Opaque(tint), Opaque(surface)) >= MinimumContrast;

    private static SKColor Opaque(Color colour) => new(colour.R, colour.G, colour.B);
}
