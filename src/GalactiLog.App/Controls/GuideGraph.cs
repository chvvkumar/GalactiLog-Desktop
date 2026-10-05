using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Controls;

/// <summary>
/// The PHD2 guide graph (spec 12.4, section 13): one guiding session's RA and Dec error against
/// the time of night, with settle bands, star-lost points and dither lines.
/// </summary>
/// <remarks>
/// <para>
/// A drawn control in <see cref="NightStrip"/>'s manner and for its reason: a session is
/// thousands of frames, and a composed chart would be thousands of visuals. No <c>.axaml</c>
/// beside this file. Not LiveCharts.
/// </para>
/// <para>
/// Every colour arrives from the host through a styled property set with a
/// <c>DynamicResource</c>; this file names none.
/// <c>GuideGraphTests.GuideGraphSource_ContainsNoColourLiteral</c> is the enforcement.
/// </para>
/// <para>
/// Ruling G1: every handler below turns a pointer position into a data coordinate and calls one
/// member of <see cref="GuideGraphViewModel"/>. No clamp, no window arithmetic and no ported
/// figure lives here; <c>GuideGraphTests.GuideGraphSource_HandlersAreThin</c> is the enforcement.
/// </para>
/// </remarks>
public sealed class GuideGraph : Control
{
    /// <summary>The size reserved under an unbounded measure. The height is the web's default
    /// plot height, <c>Phd2GuideGraph.tsx:67</c>; the host normally sets both.</summary>
    internal const double DefaultWidth = 480d;
    internal const double DefaultHeight = 220d;

    /// <summary>Room at the left for the arcsecond labels, "-12.5" in the label size.</summary>
    internal const double LeftInset = 40d;

    /// <summary>Room at the bottom for the clock labels. With <see cref="TopInset"/> it is why
    /// spec 12.4 floors the height at 160.</summary>
    internal const double BottomInset = 20d;

    /// <summary>Keeps the top label and the last clock label inside the bounds.</summary>
    internal const double TopInset = 8d;
    internal const double RightInset = 16d;
    private const double ActiveMarkWidth = 2d;

    private const double LabelSize = 12d;
    private const double LabelGap = 4d;
    private const double PenWidth = 1d;
    private const double DropRadius = 2.5d;
    private const double DitherDash = 3d;
    private const double MinBandWidth = 2d;
    private const double FailedSettleOpacity = 0.15d;

    public static readonly StyledProperty<GuideGraphViewModel?> ModelProperty =
        AvaloniaProperty.Register<GuideGraph, GuideGraphViewModel?>(nameof(Model));

    /// <summary>The RA trace. The host binds <c>ColorMetricGuiding</c>.</summary>
    public static readonly StyledProperty<IBrush?> RaBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(RaBrush));

    /// <summary>The Dec trace. The host binds <c>ColorMetricFwhm</c>.</summary>
    public static readonly StyledProperty<IBrush?> DecBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(DecBrush));

    /// <summary>The star-lost points and, at 0.15 opacity, a failed settle. The host binds
    /// <c>ColorMetricWorst</c>.</summary>
    public static readonly StyledProperty<IBrush?> DropBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(DropBrush));

    /// <summary>The dither lines. The host binds <c>ColorMetricEccentricity</c>.</summary>
    public static readonly StyledProperty<IBrush?> DitherBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(DitherBrush));

    /// <summary>An ordinary settle band. The host binds <c>ColorBgHover</c>.</summary>
    public static readonly StyledProperty<IBrush?> SettleBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(SettleBrush));

    /// <summary>The zero line and the two axis lines. The host binds <c>ColorBorderEmphasis</c>.
    /// </summary>
    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(AxisBrush));

    /// <summary>The grid. The host binds <c>ColorBorderDefault</c>.</summary>
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(GridBrush));

    /// <summary>The axis labels. The host binds <c>ColorTextTertiary</c>. Unset draws no label
    /// rather than inventing an ink (spec 14.5).</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(LabelBrush));

    private string? _tipText;
    private Point? _dragLast;
    private bool _isAttachedToVisualTree;
    private GuideGraphViewModel? _subscribedModel;
    private NightStripViewModel? _subscribedStrip;

    static GuideGraph()
    {
        AffectsRender<GuideGraph>(
            ModelProperty,
            RaBrushProperty,
            DecBrushProperty,
            DropBrushProperty,
            DitherBrushProperty,
            SettleBrushProperty,
            AxisBrushProperty,
            GridBrushProperty,
            LabelBrushProperty,
            LaneAxisProperty,
            StripProperty,
            ActiveBrushProperty,
            ShowsClockLabelsProperty);
    }

    public GuideGraph()
    {
        // A pointer surface that handles no key: not focusable and never a tab stop, so a press
        // on the plot leaves focus where it was.
        Focusable = false;
        IsTabStop = false;

        // The timeline's active ink unless a host sets another, so every lane marks a frame alike.
        Bind(ActiveBrushProperty, this.GetResourceObservable("ColorTextPrimary"));
    }

    /// <summary>The selected frame's mark ink, the timeline's active tick's token.</summary>
    public static readonly StyledProperty<IBrush?> ActiveBrushProperty =
        AvaloniaProperty.Register<GuideGraph, IBrush?>(nameof(ActiveBrush));

    public IBrush? ActiveBrush
    {
        get => GetValue(ActiveBrushProperty);
        set => SetValue(ActiveBrushProperty, value);
    }

    public GuideGraphViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public IBrush? RaBrush
    {
        get => GetValue(RaBrushProperty);
        set => SetValue(RaBrushProperty, value);
    }

    public IBrush? DecBrush
    {
        get => GetValue(DecBrushProperty);
        set => SetValue(DecBrushProperty, value);
    }

    public IBrush? DropBrush
    {
        get => GetValue(DropBrushProperty);
        set => SetValue(DropBrushProperty, value);
    }

    public IBrush? DitherBrush
    {
        get => GetValue(DitherBrushProperty);
        set => SetValue(DitherBrushProperty, value);
    }

    public IBrush? SettleBrush
    {
        get => GetValue(SettleBrushProperty);
        set => SetValue(SettleBrushProperty, value);
    }

    public IBrush? AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <summary>On the lane axis, whether the night's hour labels are drawn under the ticks; off where
    /// the timeline above already labels them.</summary>
    public static readonly StyledProperty<bool> ShowsClockLabelsProperty =
        AvaloniaProperty.Register<GuideGraph, bool>(nameof(ShowsClockLabels));

    public bool ShowsClockLabels
    {
        get => GetValue(ShowsClockLabelsProperty);
        set => SetValue(ShowsClockLabelsProperty, value);
    }

    /// <summary>The clock labels the last render drew under the plot.</summary>
    internal IReadOnlyList<string> LastClockLabels { get; private set; } = [];

    /// <summary>The night's shared time axis. Null draws the graph as it always has.</summary>
    public static readonly StyledProperty<NightLaneAxis?> LaneAxisProperty =
        AvaloniaProperty.Register<GuideGraph, NightLaneAxis?>(nameof(LaneAxis));

    public NightLaneAxis? LaneAxis
    {
        get => GetValue(LaneAxisProperty);
        set => SetValue(LaneAxisProperty, value);
    }

    /// <summary>The night's timeline model, the one owner of the selected frame. Null draws no
    /// selected-frame mark.</summary>
    public static readonly StyledProperty<NightStripViewModel?> StripProperty =
        AvaloniaProperty.Register<GuideGraph, NightStripViewModel?>(nameof(Strip));

    public NightStripViewModel? Strip
    {
        get => GetValue(StripProperty);
        set => SetValue(StripProperty, value);
    }

    /// <summary>The selected frame's mark x, at its timeline tick's fraction of the plot; null
    /// off the shared axis or with no frame selected.</summary>
    internal double? ActiveX
    {
        get
        {
            if (Model is not { IsOnLaneAxis: true } || LaneAxis is null
                || Strip is not { ActiveFrame: { } index } strip
                || strip.Ticks.FirstOrDefault(tick => tick.FrameIndex == index) is not { } active)
            {
                return null;
            }

            var plot = PlotRect;
            return plot.X + (active.Fraction * plot.Width);
        }
    }

    /// <summary>The x of an elapsed second in the current window, in this control's coordinates.
    /// </summary>
    internal double XAt(double seconds) => Model is { } model ? XOf(model, PlotRect, seconds) : double.NaN;

    /// <summary>What the most recent <see cref="Render"/> painted, per layer. A recorded count
    /// rather than a pixel readback, which the headless host does not offer (the idiom
    /// <c>NightStrip.LastDrawOrder</c> set).</summary>
    internal readonly record struct DrawCounts(int Bands, int FailedBands, int RaPoints, int DecPoints, int Drops, int Dithers);

    internal DrawCounts LastDraw { get; private set; }

    /// <summary>The ink of the most recent render's selected-frame mark; null when none was drawn.
    /// </summary>
    internal IBrush? LastMarkBrush { get; private set; }

    /// <summary>The rectangle the data is drawn in, inside the label insets. Empty, never
    /// negative, when the control is smaller than its insets.</summary>
    internal Rect PlotRect
    {
        get
        {
            var left = LaneAxis?.PlotLeft ?? LeftInset;
            var width = Bounds.Width - left - (LaneAxis?.PlotRight ?? RightInset);
            var height = Bounds.Height - TopInset - BottomInset;
            return new Rect(left, TopInset, width > 0d ? width : 0d, height > 0d ? height : 0d);
        }
    }

    /// <summary>The elapsed seconds under a pointer x, in the model's current time window.
    /// </summary>
    internal double SecondsAt(GuideGraphViewModel model, double x)
    {
        var plot = PlotRect;
        return model.TimeAt(plot.Width > 0d ? (x - plot.X) / plot.Width : 0d);
    }

    /// <summary>The arcseconds under a pointer y. Pixels run down and arcseconds run up.</summary>
    internal double ArcsecAt(GuideGraphViewModel model, double y)
    {
        var plot = PlotRect;
        return GuideGraphViewModel.ValueAt(model.ArcsecView, plot.Height > 0d ? (plot.Bottom - y) / plot.Height : 0d);
    }

    private static double XOf(GuideGraphViewModel model, Rect plot, double seconds)
        => plot.X + (model.TimeFractionOf(seconds) * plot.Width);

    private static double YOf(GuideGraphViewModel model, Rect plot, double arcsec)
        => plot.Bottom - (GuideGraphViewModel.FractionOf(model.ArcsecView, arcsec) * plot.Height);

    protected override Size MeasureOverride(Size availableSize)
        => new(
            double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? DefaultHeight : availableSize.Height);

    // ---------------------------------------------------------------- the gestures, all thin

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // One rule for every chart under a page scroller (WheelPassthrough): a plain wheel is left
        // unhandled so the pane scrolls; Ctrl with the wheel zooms, and only that is handled.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;

        if (Model is not { } model)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            model.ZoomArcsec(ArcsecAt(model, point.Y), e.Delta.Y);
        }
        else
        {
            model.ZoomTime(SecondsAt(model, point.X), e.Delta.Y);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Model is not { } model || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _dragLast = null;
            model.ResetView();
        }
        else
        {
            _dragLast = e.GetPosition(this);
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (Model is not { } model)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_dragLast is { } last)
        {
            // The data follows the pointer: the window moves by what the pointer left behind.
            model.Pan(
                SecondsAt(model, last.X) - SecondsAt(model, point.X),
                ArcsecAt(model, last.Y) - ArcsecAt(model, point.Y));
            _dragLast = point;
            return;
        }

        SetTip(model.HoverText(SecondsAt(model, point.X)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragLast is not null)
        {
            _dragLast = null;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragLast = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetTip(null);
    }

    private void SetTip(string? text) => DrawnTip.SetTip(this, ref _tipText, text);

    // ---------------------------------------------------------------- the model link

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModelProperty || change.Property == LaneAxisProperty || change.Property == ShowsClockLabelsProperty)
        {
            // A graph that labels its own clock is one that does not sit under the timeline.
            Model?.UseLaneAxis(LaneAxis, ShowsClockLabels);
        }

        if (change.Property == ModelProperty)
        {
            // A new model must not inherit the previous one's hover or a drag in flight.
            SetTip(null);
            _dragLast = null;

            // Subscribed only while attached, as NightStrip does, so a control discarded by its
            // presenter never stays alive on a live view-model's handler list.
            if (_isAttachedToVisualTree)
            {
                SyncSubscription(change.GetNewValue<GuideGraphViewModel?>());
            }
        }

        if (change.Property == StripProperty && _isAttachedToVisualTree)
        {
            SyncStrip(change.GetNewValue<NightStripViewModel?>());
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        SyncSubscription(Model);
        SyncStrip(Strip);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttachedToVisualTree = false;
        SyncSubscription(null);
        SyncStrip(null);
    }

    private void SyncStrip(NightStripViewModel? desired)
    {
        if (ReferenceEquals(_subscribedStrip, desired))
        {
            return;
        }

        if (_subscribedStrip is { } old)
        {
            old.ActiveFrameChanged -= OnActiveFrameChanged;
        }

        _subscribedStrip = desired;

        if (desired is { } strip)
        {
            strip.ActiveFrameChanged += OnActiveFrameChanged;
        }
    }

    private void OnActiveFrameChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void SyncSubscription(GuideGraphViewModel? desired)
    {
        if (ReferenceEquals(_subscribedModel, desired))
        {
            return;
        }

        if (_subscribedModel is { } old)
        {
            old.PropertyChanged -= OnModelChanged;
        }

        _subscribedModel = desired;

        if (desired is { } model)
        {
            model.PropertyChanged += OnModelChanged;
        }
    }

    // Every change the model raises (a window, a legend toggle, a landed session) repaints.
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // ---------------------------------------------------------------- the drawing

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // The whole bounds are the pointer surface: Avalonia hit-tests a drawn visual against the
        // operations it recorded, so without this the gestures answer only on a drawn line.
        // Brushes.Transparent paints nothing and is not a colour of this control's own.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var plot = PlotRect;
        LastMarkBrush = null;
        if (Model is not { HasFrames: true } model || plot.Width <= 0d || plot.Height <= 0d)
        {
            LastDraw = default;
            LastClockLabels = [];
            return;
        }

        var time = model.TimeView;
        int bands = 0, failedBands = 0, raPoints = 0, decPoints = 0, drops = 0, dithers = 0;

        // 1. The settle bands, clipped so a zoomed-in view never paints over the axis labels.
        if (model.IsShown(GuideLayer.Settling))
        {
            using (context.PushClip(plot))
            {
                foreach (var window in model.SettleBands)
                {
                    if (window.End < time.Min || window.Start > time.Max)
                    {
                        continue;
                    }

                    var left = XOf(model, plot, window.Start);
                    var width = XOf(model, plot, window.End) - left;
                    var rect = new Rect(left, plot.Y, width < MinBandWidth ? MinBandWidth : width, plot.Height);

                    if (window.Failed)
                    {
                        // The opacity is applied to the bound brush; no colour is named here.
                        using (context.PushOpacity(FailedSettleOpacity))
                        {
                            context.FillRectangle(DropBrush ?? Brushes.Transparent, rect);
                        }

                        failedBands++;
                    }
                    else
                    {
                        context.FillRectangle(SettleBrush ?? Brushes.Transparent, rect);
                    }

                    bands++;
                }
            }
        }

        // 2. The grid, the labels, the two axis lines and the zero line.
        var gridPen = new Pen(GridBrush ?? Brushes.Transparent, PenWidth);
        var axisPen = new Pen(AxisBrush ?? Brushes.Transparent, PenWidth);
        var typeface = new Typeface(TextElement.GetFontFamily(this));

        var clockLabels = new List<string>();
        var clear = double.NegativeInfinity;
        void ClockLabel(string text, double x)
        {
            var next = DrawLabel(context, text, typeface, x, plot.Bottom + LabelGap, centred: true, clear);
            if (next > clear)
            {
                clockLabels.Add(text);
                clear = next;
            }
        }

        if (model.IsOnLaneAxis && LaneAxis is { } lane)
        {
            foreach (var fraction in lane.TickFractions)
            {
                var x = plot.X + (fraction * plot.Width);
                context.DrawLine(gridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            }

            // The strip's axis ticks are the lane axis's ticks with their clock labels; a night
            // too short to hold one is labelled at its two ends.
            if (ShowsClockLabels && Strip is { } strip)
            {
                IReadOnlyList<AxisTick> marks = strip.AxisTicks.Count > 0
                    ? strip.AxisTicks
                    : [new(0d, model.ClockLabel(lane.StartLocal)), new(1d, model.ClockLabel(lane.EndLocal))];
                foreach (var tick in marks)
                {
                    ClockLabel(tick.Label, plot.X + (tick.Fraction * plot.Width));
                }
            }
        }
        else
        {
            foreach (var tick in model.TimeTicks)
            {
                var x = XOf(model, plot, tick.Value);
                context.DrawLine(gridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
                ClockLabel(tick.Label, x);
            }
        }

        foreach (var tick in model.ArcsecTicks)
        {
            var y = YOf(model, plot, tick.Value);
            context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
            DrawLabel(context, tick.Label, typeface, plot.X - LabelGap, y, centred: false, double.NegativeInfinity);
        }

        context.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);
        context.DrawLine(axisPen, plot.TopLeft, plot.BottomLeft);

        var zeroY = YOf(model, plot, 0d);
        if (zeroY >= plot.Y && zeroY <= plot.Bottom)
        {
            context.DrawLine(axisPen, new Point(plot.X, zeroY), new Point(plot.Right, zeroY));
        }

        // 3 and 4. The traces and the star-lost points, clipped: the slice carries one frame
        // either side of the window so the line reaches both edges.
        var plotted = model.Plotted;
        using (context.PushClip(plot))
        {
            if (model.IsShown(GuideLayer.Ra))
            {
                raPoints = DrawTrace(context, model, plot, plotted, RaBrush, f => f.Ra);
            }

            if (model.IsShown(GuideLayer.Dec))
            {
                decPoints = DrawTrace(context, model, plot, plotted, DecBrush, f => f.Dec);
            }

            if (model.IsShown(GuideLayer.StarLost) && zeroY >= plot.Y && zeroY <= plot.Bottom)
            {
                foreach (var frame in plotted)
                {
                    if (frame.Dropped)
                    {
                        context.DrawEllipse(
                            DropBrush ?? Brushes.Transparent,
                            null,
                            new Point(XOf(model, plot, frame.T), zeroY),
                            DropRadius,
                            DropRadius);
                        drops++;
                    }
                }
            }

            // 5. The dither lines, dashed 3 on and 3 off.
            if (model.IsShown(GuideLayer.Dither))
            {
                var ditherPen = new Pen(
                    DitherBrush ?? Brushes.Transparent,
                    PenWidth,
                    new DashStyle([DitherDash, DitherDash], 0d));

                foreach (var t in model.DitherTimes)
                {
                    if (t < time.Min || t > time.Max)
                    {
                        continue;
                    }

                    var x = XOf(model, plot, t);
                    context.DrawLine(ditherPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
                    dithers++;
                }
            }
        }

        // The selected frame, in the timeline's active ink and width, over every layer.
        if (ActiveX is { } activeX)
        {
            LastMarkBrush = ActiveBrush ?? Brushes.Transparent;
            context.DrawLine(new Pen(LastMarkBrush, ActiveMarkWidth), new Point(activeX, plot.Y), new Point(activeX, plot.Bottom));
        }

        LastDraw = new DrawCounts(bands, failedBands, raPoints, decPoints, drops, dithers);
        LastClockLabels = clockLabels;
    }

    // One pixel wide, no markers, and no gap spanning: a null value ends the figure, so the line
    // breaks rather than being interpolated across. A lone point between two nulls draws nothing,
    // as a line chart with no markers draws nothing for it.
    private static int DrawTrace(
        DrawingContext context,
        GuideGraphViewModel model,
        Rect plot,
        IReadOnlyList<Phd2FramePoint> frames,
        IBrush? brush,
        Func<Phd2FramePoint, double?> value)
    {
        var geometry = new StreamGeometry();
        var points = 0;
        using (var figure = geometry.Open())
        {
            var open = false;
            foreach (var frame in frames)
            {
                if (value(frame) is not { } arcsec || !double.IsFinite(arcsec))
                {
                    if (open)
                    {
                        figure.EndFigure(false);
                        open = false;
                    }

                    continue;
                }

                var point = new Point(XOf(model, plot, frame.T), YOf(model, plot, arcsec));
                if (open)
                {
                    figure.LineTo(point);
                }
                else
                {
                    figure.BeginFigure(point, isFilled: false);
                    open = true;
                }

                points++;
            }

            if (open)
            {
                figure.EndFigure(false);
            }
        }

        context.DrawGeometry(null, new Pen(brush ?? Brushes.Transparent, PenWidth), geometry);
        return points;
    }

    // A clock label hangs centred under its mark, kept inside the right edge, and is skipped when
    // it would start left of clear; an arcsecond label sits right-aligned against the axis and
    // vertically centred on its grid line. Returns where the next clock label is clear to start.
    private double DrawLabel(
        DrawingContext context, string text, Typeface typeface, double x, double y, bool centred,
        double clear)
    {
        if (LabelBrush is not { } brush || string.IsNullOrEmpty(text))
        {
            return clear;
        }

        // Phase 15B fixer F9. InvariantCulture, the culture AltitudeArc.Text already builds its
        // labels under: every string that reaches here was formatted invariantly upstream, and the
        // culture picks digit shaping, so the two drawn controls of this phase would otherwise
        // shape digits differently on a machine with a non-Latin digit locale.
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, LabelSize, brush);

        if (!centred)
        {
            context.DrawText(formatted, new Point(x - formatted.Width, y - (formatted.Height / 2d)));
            return clear;
        }

        var left = x - (formatted.Width / 2d);
        if (left + formatted.Width > Bounds.Width)
        {
            left = Bounds.Width - formatted.Width;
        }

        if (left < clear)
        {
            return clear;
        }

        context.DrawText(formatted, new Point(left, y));
        return left + formatted.Width + LabelGap;
    }
}
