using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GalactiLog.App.ViewModels.Stats;
using LiveChartsCore.SkiaSharpView.Avalonia;

namespace GalactiLog.App.Views;

/// <summary>
/// Spec 12.5's Statistics page, bound to
/// <see cref="ViewModels.Stats.StatisticsViewModel"/>. Most behaviour is in the view-model,
/// including the timeline's bar click, which reaches it through
/// <c>CartesianChart.DataPointerDownCommand</c>. This class carries the timeline's wheel and drag
/// (Phase 14C, spec 12.5's "Wheel zoom and drag pan"): the pixel arithmetic stays here, because a
/// view-model that took pixels could not be tested headless, and the period arithmetic it resolves
/// to lives on <see cref="ImagingTimelineViewModel"/> instead.
/// </summary>
public partial class StatisticsView : UserControl
{
    // 3 pixels, the same drag threshold the web's didDrag flag uses (ImagingTimeline.tsx), so a
    // click that trembles by a pixel or two still reaches PointClicked.
    private const double DragThreshold = 3d;

    private Point? _dragStart;
    private bool _didDrag;

    public StatisticsView()
    {
        InitializeComponent();

        // Subscribed here with handledEventsToo rather than as a markup attribute: LiveCharts'
        // CartesianChart subscribes to PointerWheelChanged in its own constructor and marks every
        // wheel handled before it reads ZoomMode (rc5.4 CartesianChart.axaml.cs:812), so a handler
        // attached after it in markup never ran.
        TimelineChart.AddHandler(PointerWheelChangedEvent, OnTimelineWheel, handledEventsToo: true);
    }

    private ImagingTimelineViewModel? Timeline => (DataContext as StatisticsViewModel)?.Timeline;

    private void OnTimelineWheel(object? sender, PointerWheelEventArgs e)
    {
        // The one rule WheelPassthrough enforces on the page scroller above: a plain wheel scrolls
        // the page, Ctrl with the wheel steps the granularity. LiveCharts has already marked the
        // event handled, and the scroller's passthrough reads the Ctrl key to tell the two apart.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;

        if (sender is CartesianChart chart)
        {
            HandleWheel(chart, e.GetPosition(chart).X, e.Delta.Y);
        }
    }

    /// <summary>The pixel-to-period conversion the wheel handler resolves to, taken out to a
    /// plain-argument method so a test can drive it without constructing a LiveCharts pointer
    /// wheel event.</summary>
    internal void HandleWheel(CartesianChart chart, double pointerX, double deltaY)
    {
        // Phase-review P3: deltaY > 0 ? 1 : -1 read a gesture with no vertical component at all
        // (a horizontal wheel, a tilt wheel, a trackpad sideways swipe, deltaY == 0) as a step
        // down. Zero must step neither way.
        if (deltaY == 0)
        {
            return;
        }

        if (Timeline is not { } timeline)
        {
            return;
        }

        var anchor = PeriodAt(chart, timeline, pointerX);
        timeline.StepGranularity(deltaY > 0 ? 1 : -1, anchor);
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not CartesianChart chart || !e.GetCurrentPoint(chart).Properties.IsLeftButtonPressed)
        {
            return;
        }

        HandlePressed(e.GetPosition(chart));
        e.Pointer.Capture(chart);
    }

    internal void HandlePressed(Point position)
    {
        _dragStart = position;
        _didDrag = false;
    }

    private void OnTimelinePointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is CartesianChart chart)
        {
            HandleMoved(e.GetPosition(chart));
        }
    }

    internal void HandleMoved(Point position)
    {
        if (_dragStart is not { } start)
        {
            return;
        }

        if (!_didDrag && Math.Abs(position.X - start.X) > DragThreshold)
        {
            _didDrag = true;
        }
    }

    private void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not CartesianChart chart)
        {
            _dragStart = null;
            _didDrag = false;
            Timeline?.DiscardPendingClick();
            return;
        }

        // HandleReleased first, capture release last: Capture(null) raises PointerCaptureLost
        // synchronously, and OnTimelinePointerCaptureLost clears the same _dragStart, _didDrag and
        // pending click HandleReleased needs to read. Releasing capture before calling it made
        // every drag look like a lost capture, discarding the pan before it was ever computed
        // (fix pass: caught by ALeftDragOverTheChart_PansTheRange failing after the capture-lost
        // handler was added).
        HandleReleased(chart, e.GetPosition(chart));
        e.Pointer.Capture(null);
    }

    // Review P1: LiveCharts raises DataPointerDownCommand, and so
    // ImagingTimelineViewModel.PointClickedCommand, on pointer down (verified against
    // LiveChartsCore.SkiaSharpView.Avalonia.xml's own "Gets or sets a command to execute when the
    // pointer goes down on a data or data points"), not on a click or a release. A flag set here,
    // on release, to suppress the *next* click always arrives one gesture late: a drag that starts
    // over a bar has already navigated to that bar by the time any movement is measured, and the
    // flag then swallows the next genuine click instead. The fix is on the view-model's side
    // (ImagingTimelineViewModel.PointClicked records a pending index rather than navigating); this
    // method is where the gesture's outcome, drag or plain click, is decided and the pending click
    // is committed or discarded accordingly.
    internal void HandleReleased(CartesianChart chart, Point position)
    {
        if (_dragStart is not { } start)
        {
            _dragStart = null;
            Timeline?.DiscardPendingClick();
            return;
        }

        var moved = _didDrag;
        var deltaX = position.X - start.X;
        _dragStart = null;
        _didDrag = false;

        if (Timeline is not { } timeline)
        {
            return;
        }

        if (!moved)
        {
            // No drag: the press ImagingTimelineViewModel.PointClicked recorded was a plain click,
            // so it navigates now.
            timeline.CommitPendingClick();
            return;
        }

        // A drag that ends over a bar must not also navigate to that bar's date range (the web's
        // didDrag guard, applied here to the pending click the press recorded).
        timeline.DiscardPendingClick();

        var pitch = BarPitch(chart, timeline);
        if (pitch <= 0)
        {
            return;
        }

        // Dragging right moves the visible range toward earlier dates, the same "grab the chart
        // and pull" reading a drag-to-pan gesture always has.
        var periods = -(int)Math.Round(deltaX / pitch);
        if (periods != 0)
        {
            timeline.Pan(periods);
        }
    }

    // Review P3: a lost pointer capture (a window deactivation, a context menu) never reaches
    // HandleReleased, so without this a drag left half open by one of those would leave a stale
    // _didDrag for the next press to inherit and a pending click that never resolves. It
    // self-corrects on the very next press regardless, since HandlePressed resets both, but a
    // pending click otherwise sits unresolved for however long that takes. Also fires as part of
    // this class's own normal release path (Capture(null) raises it synchronously), which is why
    // OnTimelinePointerReleased calls HandleReleased first and releases capture last: by the time
    // this runs there, _dragStart and the pending click are already resolved, so clearing them
    // again here is a harmless no-op rather than a double answer.
    private void OnTimelinePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => HandleCaptureLost();

    internal void HandleCaptureLost()
    {
        _dragStart = null;
        _didDrag = false;
        Timeline?.DiscardPendingClick();
    }

    // Ceiling, recorded rather than chased: this divides the chart's whole Bounds.Width by the bar
    // count, so it ignores the Y axis labels and the chart's own drawn margin, and the true pitch
    // is narrower by that margin's width. The pitch is therefore slightly wide and the anchor
    // period the wheel resolves under the pointer can drift by roughly the axis width, about one
    // bar at 24 bars; the drag's own delta error is proportional and small enough not to matter.
    // CartesianChart.ScalePixelsToData and ScaleDataToPixels read the chart's own scaler and would
    // remove the drift, but both depend on the chart having drawn at least one real frame, which
    // ImagingTimelineZoomPanTests' rendered-chart cases cannot guarantee under headless drawing
    // (TestAppBuilder.cs's UseHeadlessDrawing), so this task leaves the ceiling named rather than
    // risking the working pixel arithmetic on an unverified fallback this late in the phase.
    private static double BarPitch(CartesianChart chart, ImagingTimelineViewModel timeline)
    {
        var count = timeline.Bars.Count;
        return count > 0 ? chart.Bounds.Width / count : 0d;
    }

    private static DateOnly? PeriodAt(CartesianChart chart, ImagingTimelineViewModel timeline, double x)
    {
        var pitch = BarPitch(chart, timeline);
        if (pitch <= 0 || timeline.Bars.Count == 0)
        {
            return null;
        }

        var index = (int)Math.Clamp(Math.Floor(x / pitch), 0, timeline.Bars.Count - 1);
        return timeline.Bars[index].Start;
    }
}
