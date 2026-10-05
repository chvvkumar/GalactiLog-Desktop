using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LiveChartsCore.SkiaSharpView.Avalonia;

namespace GalactiLog.App.Controls;

/// <summary>
/// One rule for every page scroller that holds a chart: a plain wheel scrolls the page, Ctrl with
/// the wheel zooms the chart under the pointer.
/// </summary>
/// <remarks>
/// LiveCharts' <see cref="CartesianChart"/> marks every wheel event handled before it looks at its
/// ZoomMode (rc5.4 CartesianChart.axaml.cs:812), so Avalonia's ScrollContentPresenter, which skips a
/// handled event, never scrolls while the pointer rests on a chart. This attached property is the
/// structural choke point (design-lessons rule 2): the scroller subscribes with
/// <c>handledEventsToo</c>, and when a chart below it swallowed a plain wheel, the scroller moves by
/// what the presenter would have moved (ScrollContentPresenter.OnPointerWheelChanged, Avalonia
/// release/11.3.0: 50 px per notch, Shift alone turns a vertical wheel horizontal, chaining leaves
/// the event for an outer scroller when the offset did not change). A nested Avalonia scroller
/// that consumed the wheel is not a chart, so the ancestor test leaves it alone.
/// </remarks>
public static class WheelPassthrough
{
    private const double PixelsPerNotch = 50d;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(WheelPassthrough));

    static WheelPassthrough()
        => IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnIsEnabledChanged);

    public static bool GetIsEnabled(ScrollViewer scroller) => scroller.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer scroller, bool value) => scroller.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(ScrollViewer scroller, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            scroller.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        else
        {
            scroller.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        }
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller
            || !e.Handled
            || e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || !IsUnderAChart(e.Source as Visual, scroller))
        {
            return;
        }

        var extent = scroller.Extent;
        var viewport = scroller.Viewport;
        if (extent.Height <= viewport.Height && extent.Width <= viewport.Width)
        {
            e.Handled = false;
            return;
        }

        var delta = e.Delta;
        if (e.KeyModifiers == KeyModifiers.Shift && delta.X == 0d)
        {
            delta = new Vector(delta.Y, delta.X);
        }

        var x = scroller.Offset.X;
        var y = scroller.Offset.Y;
        if (extent.Height > viewport.Height)
        {
            y = Math.Clamp(y - (delta.Y * PixelsPerNotch), 0d, extent.Height - viewport.Height);
        }

        if (extent.Width > viewport.Width)
        {
            x = Math.Clamp(x - (delta.X * PixelsPerNotch), 0d, extent.Width - viewport.Width);
        }

        var offset = new Vector(x, y);
        var changed = offset != scroller.Offset;
        scroller.Offset = offset;
        e.Handled = changed;
    }

    /// <summary>Whether a chart that swallows the wheel sits between <paramref name="source"/> and the
    /// scroller with no other scroller between them; a nested scroller has already had the notch.
    /// The guide graph leaves a plain wheel unhandled, so it is not one.</summary>
    private static bool IsUnderAChart(Visual? source, ScrollViewer scroller)
    {
        var chart = false;
        for (var visual = source; visual is not null && visual != scroller; visual = visual.GetVisualParent())
        {
            if (visual is ScrollViewer)
            {
                return false;
            }

            chart |= visual is CartesianChart;
        }

        return chart;
    }
}
