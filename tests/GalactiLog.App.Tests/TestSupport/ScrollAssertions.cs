using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// "Is this control actually in view?", for the Settings tabs' two <c>BringIntoView</c> routes
/// (Phase 15B Task 5c, review P2-2).
/// </summary>
/// <remarks>
/// A scroll offset above zero is not the same question and does not answer it: on the Equipment
/// tab the PHD2 panel is visible from construction, so a scroll issued too early lands on a real
/// offset and the growing content above then pushes the panel straight past it. What the two
/// routes promise is that the named section is on screen when the user arrives, so that is what
/// these assert.
/// </remarks>
internal static class ScrollAssertions
{
    /// <summary>The nearest <see cref="ScrollViewer"/> the view scrolls inside.</summary>
    public static ScrollViewer Scroller(Control view)
        => view.GetVisualDescendants().OfType<ScrollViewer>().First();

    /// <summary>
    /// Whether <paramref name="target"/> sits inside <paramref name="scroller"/>'s viewport, which
    /// is what <c>BringIntoView</c> is asked for. False for a collapsed control, which has no
    /// position to translate.
    /// </summary>
    public static bool IsInView(Control target, ScrollViewer scroller)
    {
        if (!target.IsEffectivelyVisible || target.TranslatePoint(default, scroller) is not { } point)
        {
            return false;
        }

        return point.Y >= 0d && point.Y < scroller.Viewport.Height;
    }
}
