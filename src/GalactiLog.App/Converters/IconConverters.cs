using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GalactiLog.App.Converters;

/// <summary>Resolves a <c>StreamGeometry</c> resource key to its geometry for a bound Path (polish
/// ruling 6); null in, null out, which is how the strip template tells an icon item from a text one.</summary>
internal static class IconConverters
{
    public static readonly IValueConverter KeyToGeometry =
        new FuncValueConverter<string?, Geometry?>(key =>
            key is not null && Application.Current is { } app && app.TryFindResource(key, out var resource)
                ? resource as Geometry
                : null);
}
