using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GalactiLog.App.Controls;

/// <summary>
/// The star row a scrolling page cannot have: the control is given the page viewport less
/// everything else on the page, never below <c>FillViewport.MinHeight</c>, so the page ends at the
/// fold and the control takes whatever is left.
/// </summary>
/// <remarks>
/// <para>
/// A chart on an Analysis tab hangs off the page's one ScrollViewer over a StackPanel, which
/// measures its content with unbounded height, so a <c>*</c> row is not available. This is the
/// Correlation tab's own fit (Phase 1 Task 4), lifted here the moment a second tab needed it
/// (design lesson 1), so every tab's chart fills its card through one rule.
/// </para>
/// <para>
/// "Everything else" is the page's DESIRED height less the control's own, read after the pass that
/// just ran. It does not depend on the control's height, so one correction settles it and the half
/// pixel dead band stops the pass that follows from writing again. A control that is not
/// effectively visible is skipped, so two charts that take turns in one region (the Distributions
/// histogram and box plot) each fit only while shown.
/// </para>
/// </remarks>
public static class FillViewport
{
    public static readonly AttachedProperty<double> MinHeightProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("MinHeight", typeof(FillViewport), double.NaN);

    static FillViewport()
        => MinHeightProperty.Changed.AddClassHandler<Control>(OnMinHeightChanged);

    public static double GetMinHeight(Control control) => control.GetValue(MinHeightProperty);

    public static void SetMinHeight(Control control, double value) => control.SetValue(MinHeightProperty, value);

    private static void OnMinHeightChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        // Subscribed once, on the first value; the floor is read on every pass, so a later change
        // to it needs no second subscription.
        if (e.OldValue is double old && double.IsNaN(old) && e.NewValue is double now && !double.IsNaN(now))
        {
            control.Height = now;
            control.LayoutUpdated += (_, _) => Fit(control);
        }
    }

    private static void Fit(Control control)
    {
        var floor = GetMinHeight(control);
        if (double.IsNaN(floor)
            || !control.IsEffectivelyVisible
            || control.FindAncestorOfType<ScrollViewer>() is not { } scroller
            || scroller.Presenter?.Child is not { } page)
        {
            return;
        }

        // The page's DESIRED height, measured with unbounded height, and not the scroller's extent:
        // a page shorter than the viewport is stretched to it, so the extent would report the
        // viewport and the control would never grow.
        var spend = page.DesiredSize.Height - control.Bounds.Height;
        var target = Math.Max(floor, scroller.Viewport.Height - spend);

        if (double.IsFinite(target) && Math.Abs(target - control.Height) > 0.5d)
        {
            control.Height = target;
        }
    }
}
