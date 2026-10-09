using Avalonia;
using Avalonia.Controls;

namespace GalactiLog.App.Controls;

/// <summary>
/// Lays its child out at the child's own width whatever width it is given, and shows the left part
/// of it: a narrower viewport covers the child's right side instead of reflowing it (the Nights
/// list divider, spec.md). It never scrolls, so the child's left edge cannot slide out of view.
/// </summary>
public sealed class SlideOverViewport : Decorator
{
    public SlideOverViewport() => ClipToBounds = true;

    /// <summary>The child's own width, measured unconstrained.</summary>
    public double NaturalWidth => Child?.DesiredSize.Width ?? 0d;

    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(availableSize.WithWidth(double.PositiveInfinity));
        return new Size(Math.Min(availableSize.Width, NaturalWidth), Child?.DesiredSize.Height ?? 0d);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, Math.Max(finalSize.Width, NaturalWidth), finalSize.Height));
        return finalSize;
    }
}
