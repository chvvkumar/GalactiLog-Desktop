using Avalonia.Data.Converters;

namespace GalactiLog.App.Converters;

/// <summary>
/// Two tiny stateless converters for spec 12.6's rail badge (PAR-017), reused from Avalonia's own
/// <see cref="FuncValueConverter{TIn, TOut}"/> rather than hand-written <c>IValueConverter</c>
/// types. <c>NavigationItem</c> stays a plain sealed class with no <c>INotifyPropertyChanged</c>
/// (its own remarks argue against that on identity grounds), so the rail's shared
/// <c>DataTemplate</c> gates the badge on these two conditions instead of on a property the item
/// itself would have to carry.
/// </summary>
internal static class NavigationBadgeConverters
{
    /// <summary>True when a <c>NavigationItem.Key</c> is the Activity destination's.</summary>
    public static readonly IValueConverter IsActivityKey =
        new FuncValueConverter<string?, bool>(key => key == "activity");

    /// <summary>True when a count is above zero: spec 12.6 says the rail carries the count "while
    /// that count is above zero", so zero means no badge at all, not a badge reading zero.
    /// </summary>
    public static readonly IValueConverter IsPositive =
        new FuncValueConverter<int, bool>(count => count > 0);
}
