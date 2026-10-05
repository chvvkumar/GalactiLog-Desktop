using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Controls;

/// <summary>
/// Phase 12's signature graphic: one observing night drawn dusk to dawn, one tick per exposure in
/// its filter's ink, outliers taller and in the worse ink, the astronomical-night band behind it.
/// </summary>
/// <remarks>
/// <para>
/// Drawn in <see cref="Render"/> rather than composed, for the same reason
/// <see cref="CalendarHeatmap"/> is: a four hundred frame night would be four hundred visuals,
/// four hundred layout entries and four hundred bindings for a graphic that is a few hundred
/// one-pixel lines. There is no <c>.axaml</c> beside this file, because there are no templated
/// children for a <c>ControlTemplate</c> to hold.
/// </para>
/// <para>
/// Every colour arrives from outside. The tick inks come from the view-model, which built them
/// from the application's one filter-colour resolution; the five brush properties below are set by
/// the host with a <c>DynamicResource</c>, so a theme swap repaints the strip and this file holds
/// no colour of its own. <c>NightStripTests.NightStripSource_ContainsNoColourLiteral</c> is the
/// enforcement.
/// </para>
/// <para>
/// The strip is a pointer affordance and carries no key handling: the ledger and the frame table
/// carry the keyboard. It is not focusable at rest and never a tab stop; focus is granted for the
/// one press that lands on a tick and withdrawn again in <c>OnLostFocus</c>, so a press between
/// ticks leaves the frame table's focus where it was.
/// </para>
/// </remarks>
public sealed class NightStrip : Control
{
    /// <summary>The comp's fixed height, read from its <c>viewBox="0 0 1180 72"</c>. The width is
    /// fluid; every horizontal figure below is a fraction of it.</summary>
    internal const double StripHeight = 72d;

    /// <summary>Left and right inset, so a tick at fraction 0 or 1 is not clipped.</summary>
    internal const double Inset = 8d;

    // Internal rather than private: Task 7's active-tick case asserts "past the band at both
    // ends" against the band's own edges, not against a literal copy of them (brief section 6.2).
    internal const double BandTop = 10d;
    private const double BandHeight = 34d;
    internal const double AxisY = 44d;
    private const double TickTop = 26d;
    private const double TickBottom = 42d;
    private const double TickWidth = 1.4d;
    private const double OutlierTop = 14d;
    private const double OutlierWidth = 2d;
    private const double AxisTickBottom = 49d;
    private const double AxisPenWidth = 1d;
    private const double AxisLabelBaseline = 64d;
    private const double EdgeLabelBaseline = 10d;
    private const double LabelSize = 12d;

    // "Past the band at both ends": the band runs BandTop (10) to AxisY
    // (44), so the active tick has to clear both, and the two figures below are the band's own
    // edges pushed out by 4 rather than two new numbers picked on their own. ActiveWidth matches
    // the spec's "2 pixels wide", the same width the outlier tick already uses.
    private const double ActiveTop = BandTop - 4d;
    private const double ActiveBottom = AxisY + 4d;
    private const double ActiveWidth = 2d;

    // The caret sits where the hour ticks already end (AxisTickBottom) and drops the same 5 px an
    // hour tick drops below the axis (AxisTickBottom - AxisY), so its size is read off the strip's
    // own axis geometry rather than invented. Its half width borrows the outlier tick's own width.
    private const double CaretTop = AxisTickBottom;
    private const double CaretHeight = AxisTickBottom - AxisY;
    private const double CaretHalfWidth = OutlierWidth;

    public static readonly StyledProperty<NightStripViewModel?> ModelProperty =
        AvaloniaProperty.Register<NightStrip, NightStripViewModel?>(nameof(Model));

    /// <summary>The astronomical-night band's fill. A fill only; the comp draws no border on it.</summary>
    public static readonly StyledProperty<IBrush?> BandBrushProperty =
        AvaloniaProperty.Register<NightStrip, IBrush?>(nameof(BandBrush));

    /// <summary>The axis line and its hour marks.</summary>
    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<NightStrip, IBrush?>(nameof(AxisBrush));

    /// <summary>The hour labels and the band label.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<NightStrip, IBrush?>(nameof(LabelBrush));

    /// <summary>The worse ink an outlier tick is drawn in.</summary>
    public static readonly StyledProperty<IBrush?> OutlierBrushProperty =
        AvaloniaProperty.Register<NightStrip, IBrush?>(nameof(OutlierBrush));

    /// <summary>The first and last labels, which sit a tier above the hour labels.</summary>
    public static readonly StyledProperty<IBrush?> FirstLastLabelBrushProperty =
        AvaloniaProperty.Register<NightStrip, IBrush?>(nameof(FirstLastLabelBrush));

    /// <summary>The active-tick ink. The host binds
    /// <c>ColorTextPrimary</c>: spec 12.4 names no new token, because the active mark is the
    /// primary ink rather than an accent hue carrying state (DESIGN.md section 8's refusal).
    /// </summary>
    public static readonly StyledProperty<IBrush?> ActiveBrushProperty =
        AvaloniaProperty.Register<NightStrip, IBrush?>(nameof(ActiveBrush));

    private string? _tipText;
    private int? _hoveredIndex;
    private bool _isAttachedToVisualTree;

    static NightStrip()
    {
        AffectsRender<NightStrip>(
            ModelProperty,
            BandBrushProperty,
            AxisBrushProperty,
            LabelBrushProperty,
            OutlierBrushProperty,
            FirstLastLabelBrushProperty,
            ActiveBrushProperty,
            LaneAxisProperty);
        AffectsMeasure<NightStrip>(ModelProperty);
    }

    public NightStrip()
    {
        // Not focusable at rest, and never a tab stop. The whole band is a pointer surface now
        // (review P2-1), and the focus manager gives focus to whichever focusable element a press
        // lands on, so a strip that were focusable at rest would take focus from the frame table on
        // a press between ticks, which P12 review P2-2 ruled out. Focus is granted for the one
        // press that hits a tick and dropped again when it leaves, so the two rules hold together.
        // Never a tab stop either way: the strip draws no focus adorner and handles no key, so a
        // keyboard user tabbing across the session pane would land on a dead stop.
        Focusable = false;
        IsTabStop = false;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        Focusable = false;
    }

    public NightStripViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public IBrush? BandBrush
    {
        get => GetValue(BandBrushProperty);
        set => SetValue(BandBrushProperty, value);
    }

    public IBrush? AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public IBrush? OutlierBrush
    {
        get => GetValue(OutlierBrushProperty);
        set => SetValue(OutlierBrushProperty, value);
    }

    public IBrush? FirstLastLabelBrush
    {
        get => GetValue(FirstLastLabelBrushProperty);
        set => SetValue(FirstLastLabelBrushProperty, value);
    }

    public IBrush? ActiveBrush
    {
        get => GetValue(ActiveBrushProperty);
        set => SetValue(ActiveBrushProperty, value);
    }

    /// <summary>The night's shared time axis. Null keeps the strip's own insets.</summary>
    public static readonly StyledProperty<NightLaneAxis?> LaneAxisProperty =
        AvaloniaProperty.Register<NightStrip, NightLaneAxis?>(nameof(LaneAxis));

    public NightLaneAxis? LaneAxis
    {
        get => GetValue(LaneAxisProperty);
        set => SetValue(LaneAxisProperty, value);
    }

    /// <summary>The x of an axis fraction, in this control's coordinates.</summary>
    internal double X(double fraction) => Left + (fraction * AxisWidth);

    /// <summary>The inverse of <see cref="X"/>, which is what the hit test runs on.</summary>
    internal double FractionAt(double x)
    {
        var width = AxisWidth;
        return width <= 0d ? 0d : (x - Left) / width;
    }

    private double Left => LaneAxis?.PlotLeft ?? Inset;

    private double AxisWidth => Math.Max(0d, Bounds.Width - Left - (LaneAxis?.PlotRight ?? Inset));

    protected override Size MeasureOverride(Size availableSize)
    {
        // A fixed 72 tall, whatever width the host offers. An infinite offer (an unbounded
        // horizontal panel) reserves nothing rather than an infinite width; the host stretches it.
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0d;
        return new Size(width, StripHeight);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Model is not { } model || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (model.HitTest(FractionAt(e.GetPosition(this).X)) is { } tick)
        {
            // Focus on a hit and nothing on a miss: a press that lands between ticks must leave
            // the frame table holding focus (P12 review P2-2). The control is not focusable at
            // rest, so the grant is made here and withdrawn again in OnLostFocus.
            Focusable = true;
            Focus(NavigationMethod.Pointer);
            model.SelectFrame(tick.FrameIndex);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // One tip on the control, retargeted as the pointer crosses ticks. There is no per-tick
        // visual to attach a tip to, which is the cost of drawing rather than composing, and it is
        // the right trade at four hundred ticks.
        //
        // R9's hover rides the same hit test: one resolve per move, a tooltip and an event out of
        // it. The tint itself is put on the frame row by the host, not here, so this control still
        // paints nothing it was not already painting.
        var hit = Model?.HitTest(FractionAt(e.GetPosition(this).X));
        SetTip(hit?.ToolTipText);
        SetHover(hit?.FrameIndex);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetTip(null);
        SetHover(null);
    }

    // The change guard lives in DrawnTip, shared with GuideGraph.
    private void SetTip(string? text) => DrawnTip.SetTip(this, ref _tipText, text);

    // R9. Pointer moves arrive by the hundred across one pass of the strip, so the event is raised
    // only when the tick under the pointer changes, for the same reason the tooltip is written
    // only then.
    private void SetHover(int? frameIndex)
    {
        if (frameIndex == _hoveredIndex)
        {
            return;
        }

        _hoveredIndex = frameIndex;

        // Review P1: _hoveredIndex is a plain field, not a styled property, so nothing in
        // AffectsRender's list dirties the visual when it changes. Without this, the hover half
        // (the half the user actually asked for) computes the right active tick and never paints
        // it: the pointer can cross every tick with no repaint at all.
        InvalidateVisual();

        Model?.HoverFrame(frameIndex);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModelProperty)
        {
            // A new night must not inherit the previous night's hovered index: the de-duplication
            // above would then swallow the first move over that same tick.
            _hoveredIndex = null;

            // Task 7's active-frame link runs model to control, the opposite direction of every
            // other wire on this control, so it needs its own subscribe and unsubscribe rather
            // than piggybacking on AffectsRender.
            //
            // Phase review P3 (NightStrip.cs:309): this used to call SyncActiveFrameSubscription
            // unconditionally, so a Model swap on a control already removed from the visual tree
            // (the common construction shape "new NightStrip { Model = vm }" sets Model before
            // attach, and a discarded control can still be referenced and rebound after its own
            // detach) subscribed with no detach left to unwind it, reopening the leak the
            // attach/detach pair below was added to close. Gated to the attached case: a swap
            // while detached is picked up once, correctly, when OnAttachedToVisualTree next runs
            // and reads whatever Model is current then.
            if (_isAttachedToVisualTree)
            {
                SyncActiveFrameSubscription(change.GetNewValue<NightStripViewModel?>());
            }
        }
    }

    // Review P3. The strip lives inside a ContentControl's DataTemplate; when the presenter
    // rebuilds its child instead of rebinding it, the discarded control's Model never changes, so
    // the swap above never fires for it and a live view-model keeps a strong handler on a dead
    // control. OnAttachedToVisualTree and OnDetachedFromVisualTree close that gap; this one method
    // is the single place that (un)subscribes, guarded by reference equality, so a Model swap while
    // attached and an attach or detach around an unchanged Model can never double-subscribe.
    private NightStripViewModel? _subscribedModel;

    private void SyncActiveFrameSubscription(NightStripViewModel? desired)
    {
        if (ReferenceEquals(_subscribedModel, desired))
        {
            return;
        }

        if (_subscribedModel is { } old)
        {
            old.ActiveFrameChanged -= OnModelActiveFrameChanged;
        }

        _subscribedModel = desired;

        if (desired is { } newModel)
        {
            newModel.ActiveFrameChanged += OnModelActiveFrameChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        SyncActiveFrameSubscription(Model);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttachedToVisualTree = false;
        SyncActiveFrameSubscription(null);
    }

    private void OnModelActiveFrameChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The one active tick's frame index: the hover when there is one, else the model's
    /// resting active frame (spec 12.4's own ordering). One member, read once per render and
    /// once per test, so the precedence between the two cannot drift between them.</summary>
    private int? ActiveFrameIndex => _hoveredIndex ?? Model?.ActiveFrame;

    /// <summary>The ticks in the order the most recent <see cref="Render"/> actually painted them,
    /// active tick last when there is one. A recorded list rather than a second computation,
    /// because <see cref="GeometryFor"/> answers what a tick looks like and this answers when it
    /// was painted, which is what <c>TheActiveTick_IsDrawnLast</c> needs (questions.md Q13).
    /// </summary>
    internal IReadOnlyList<NightTick> LastDrawOrder { get; private set; } = [];

    /// <summary>Review P3: a monotonic position for where in the most recent <see cref="Render"/>
    /// the edge labels drew, null when there were no ticks to anchor them to. Compared against
    /// <see cref="LastActiveTickDrawOrdinal"/> so a case can pin that the active tick draws after
    /// them without a pixel readback.</summary>
    internal int? LastEdgeLabelDrawOrdinal { get; private set; }

    /// <summary>The axis clock labels the last render drew, left to right.</summary>
    internal IReadOnlyList<string> LastAxisLabels { get; private set; } = [];

    /// <summary>Phase 25 R5: how many band fills the last render drew.</summary>
    internal int LastBandCount { get; private set; }

    /// <summary>The dashed night-boundary lines the last render drew, one per boundary after
    /// the first.</summary>
    internal int LastBoundaryLineCount { get; private set; }

    /// <summary>The labels the last render drew inside the band: every boundary's date, left to
    /// right, then the band label once.</summary>
    internal IReadOnlyList<string> LastBandLabels { get; private set; } = [];

    /// <summary>The matching position for the active tick and its caret, null when nothing was
    /// active or the active frame named no tick.</summary>
    internal int? LastActiveTickDrawOrdinal { get; private set; }

    /// <summary>One tick's drawn rectangle and ink, exactly as <see cref="Render"/> paints it.
    /// There is no render-geometry readback anywhere in this repository's headless test host, so
    /// <see cref="Render"/> calls this for every tick it draws and a case asserts against the same
    /// values rather than against a second copy of the maths kept beside it.</summary>
    internal readonly record struct TickGeometry(Rect Rect, IBrush? Ink);

    /// <summary>The rectangle and ink a tick draws in: the active shape when this tick is the
    /// active one, checked first so an active tick that is also an outlier still draws here and
    /// never in <see cref="OutlierBrush"/>; else the outlier shape; else the
    /// ordinary tick in its own filter's ink.</summary>
    /// <remarks>Review P3: an active tick with no <see cref="ActiveBrush"/> bound degrades to its
    /// own ordinary or outlier geometry rather than the active shape in a null ink, which
    /// <c>Brushes.Transparent</c> would otherwise paint as nothing. Only a host that skips the
    /// binding (a test harness, or a future one) reaches this; <c>NightTimelinePart.axaml</c>
    /// always binds it.</remarks>
    internal TickGeometry GeometryFor(NightTick tick)
    {
        var x = X(tick.Fraction);

        if (tick.FrameIndex == ActiveFrameIndex && ActiveBrush is not null)
        {
            return new TickGeometry(
                new Rect(x - (ActiveWidth / 2d), ActiveTop, ActiveWidth, ActiveBottom - ActiveTop),
                ActiveBrush);
        }

        if (tick.IsOutlier)
        {
            return new TickGeometry(
                new Rect(x - (OutlierWidth / 2d), OutlierTop, OutlierWidth, TickBottom - OutlierTop),
                OutlierBrush);
        }

        return new TickGeometry(
            new Rect(x - (TickWidth / 2d), TickTop, TickWidth, TickBottom - TickTop),
            tick.Brush);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // The whole 72 px band is this control's pointer surface. Avalonia hit-tests a drawn
        // visual against the operations it recorded, not against its bounds, so without this the
        // strip answers the pointer only on a 1.4 px tick line and R9's tint goes out on a
        // one-pixel drift. Before the null-model return, so a strip with no night still takes a
        // press. Brushes.Transparent paints nothing and is not a colour of this control's own,
        // which is why NightStripSource_ContainsNoColourLiteral exempts it by name.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var drawOrder = new List<NightTick>();

        if (Model is not { } model)
        {
            LastDrawOrder = drawOrder;
            LastEdgeLabelDrawOrdinal = null;
            LastActiveTickDrawOrdinal = null;
            LastAxisLabels = [];
            LastBandCount = 0;
            LastBoundaryLineCount = 0;
            LastBandLabels = [];
            return;
        }

        var typeface = new Typeface(TextElement.GetFontFamily(this));

        var bandCount = 0;
        foreach (var (start, end) in model.Bands)
        {
            var bandLeft = X(start);
            context.FillRectangle(
                BandBrush ?? Brushes.Transparent,
                new Rect(bandLeft, BandTop, X(end) - bandLeft, BandHeight));
            bandCount++;
        }

        LastBandCount = bandCount;

        var axisPen = new Pen(AxisBrush ?? Brushes.Transparent, AxisPenWidth) { LineCap = PenLineCap.Flat };
        context.DrawLine(axisPen, new Point(X(0d), AxisY), new Point(X(1d), AxisY));

        // R5: every night boundary after the first is a dashed line from the band's top to the
        // axis; the first boundary is the axis' own start and carries its label alone.
        var boundaryPen = new Pen(AxisBrush ?? Brushes.Transparent, AxisPenWidth, DashStyle.Dash);
        var boundaryLines = 0;
        foreach (var boundary in model.Boundaries.Skip(1))
        {
            var x = X(boundary.Fraction);
            context.DrawLine(boundaryPen, new Point(x, BandTop), new Point(x, AxisY));
            boundaryLines++;
        }

        LastBoundaryLineCount = boundaryLines;

        foreach (var mark in model.AxisTicks)
        {
            var x = X(mark.Fraction);
            context.DrawLine(axisPen, new Point(x, AxisY), new Point(x, AxisTickBottom));
        }

        // An end label that would overlap the one before it is dropped; the label is held inside
        // the control so an end mark is not cut off.
        var axisLabels = new List<string>();
        var clear = double.NegativeInfinity;
        foreach (var mark in model.AxisLabelTicks)
        {
            if (LabelBrush is null || string.IsNullOrEmpty(mark.Label))
            {
                continue;
            }

            var width = new FormattedText(
                mark.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, LabelSize, LabelBrush).Width;
            var centre = Math.Clamp(X(mark.Fraction), width / 2d, Math.Max(width / 2d, Bounds.Width - (width / 2d)));
            if (centre - (width / 2d) < clear)
            {
                continue;
            }

            DrawLabel(context, mark.Label, typeface, LabelBrush, centre, AxisLabelBaseline, TextAlign.Centre);
            axisLabels.Add(mark.Label);
            clear = centre + (width / 2d);
        }

        LastAxisLabels = axisLabels;

        // Every tick's shape and ink come from GeometryFor, the same accessor a case asserts
        // against, so Render never carries a second copy of this maths (questions.md Q13). Each
        // pass skips the active tick: it draws last of all, in the third pass below, so it is
        // never painted over by either an ordinary run or an outlier run.
        var activeIndex = ActiveFrameIndex;

        foreach (var tick in model.Ticks)
        {
            if (tick.IsOutlier || tick.FrameIndex == activeIndex)
            {
                continue;
            }

            var geometry = GeometryFor(tick);
            context.FillRectangle(geometry.Ink ?? Brushes.Transparent, geometry.Rect);
            drawOrder.Add(tick);
        }

        // Outliers next, so a dense run of ordinary ticks never paints over one.
        foreach (var tick in model.Ticks)
        {
            if (!tick.IsOutlier || tick.FrameIndex == activeIndex)
            {
                continue;
            }

            var geometry = GeometryFor(tick);
            context.FillRectangle(geometry.Ink ?? Brushes.Transparent, geometry.Rect);
            drawOrder.Add(tick);
        }

        var sequence = 0;
        int? edgeLabelOrdinal = null;
        int? activeTickOrdinal = null;

        // Review P3: the first and last labels are drawn at the same x as the first and last
        // ticks' own fractions, so an active first or last tick's overshoot used to sit under
        // whichever label was drawn afterward. Edge labels move ahead of the active pass here; the
        // active tick and its caret draw last of all so the mark is never the one painted over.
        // Do not move the band label: it is centred under the band, away from the axis ticks.
        //
        // Phase review P3 (NightStrip.cs:504): edgeLabelOrdinal used to be assigned here, right
        // after the two DrawLabel calls but outside them, so it advanced even on a call that drew
        // nothing (DrawLabel's own null-brush-or-empty-text guard), and a future edit that moved
        // only the draw calls without moving this assignment along with them could still leave
        // the case below green while painting in the old, wrong order. The increment now lives
        // inside DrawLabel's own ref-tracking overload, so it only ever advances at the point a
        // label actually draws.
        if (model.Ticks.Count > 0)
        {
            DrawLabel(
                context,
                model.FirstLabel,
                typeface,
                FirstLastLabelBrush,
                X(model.FirstFraction),
                EdgeLabelBaseline,
                TextAlign.Left,
                ref sequence,
                ref edgeLabelOrdinal);
            DrawLabel(
                context,
                model.LastLabel,
                typeface,
                FirstLastLabelBrush,
                X(model.LastFraction),
                EdgeLabelBaseline,
                TextAlign.Right,
                ref sequence,
                ref edgeLabelOrdinal);
        }

        // The third pass: the active tick and its caret, drawn last of all, now
        // including after the edge labels above. Matched on NightTick.FrameIndex, never on a
        // position in Ticks, because a frame with no CaptureDate produces no tick and Ticks is
        // shorter than the frame list. An active index with no matching tick draws nothing, which
        // is what "nothing is drawn for this" (spec 12.4) means when the active frame is not one
        // the strip has a place for.
        if (activeIndex is { } index)
        {
            NightTick? activeTick = null;
            foreach (var candidate in model.Ticks)
            {
                if (candidate.FrameIndex == index)
                {
                    activeTick = candidate;
                    break;
                }
            }

            if (activeTick is { } tick)
            {
                var geometry = GeometryFor(tick);
                context.FillRectangle(geometry.Ink ?? Brushes.Transparent, geometry.Rect);
                DrawActiveCaret(context, geometry.Rect.Center.X, geometry.Ink);
                drawOrder.Add(tick);
                activeTickOrdinal = ++sequence;
            }
        }

        LastDrawOrder = drawOrder;
        LastEdgeLabelDrawOrdinal = edgeLabelOrdinal;
        LastActiveTickDrawOrdinal = activeTickOrdinal;

        // R5: each boundary's date at its night's top left inside the band, then the band label
        // once, on the widest band.
        var bandLabels = new List<string>();
        foreach (var boundary in model.Boundaries)
        {
            if (TryDrawLabel(
                context,
                boundary.Label,
                typeface,
                LabelBrush,
                X(boundary.Fraction) + BoundaryLabelInset,
                BoundaryLabelBaseline,
                TextAlign.Left))
            {
                bandLabels.Add(boundary.Label);
            }
        }

        if (model.Bands.Count > 0)
        {
            var widest = model.Bands.MaxBy(band => band.End - band.Start);
            if (TryDrawLabel(
                context,
                model.BandLabel,
                typeface,
                LabelBrush,
                (X(widest.Start) + X(widest.End)) / 2d,
                EdgeLabelBaseline,
                TextAlign.Centre))
            {
                bandLabels.Add(model.BandLabel);
            }
        }

        LastBandLabels = bandLabels;
    }

    private const double BoundaryLabelInset = 4d;
    private const double BoundaryLabelBaseline = BandTop + LabelSize;

    // The caret: a small downward triangle on the axis beneath the active tick, in its own ink.
    // A filled StreamGeometry rather than a StaticResource path, because this control holds no
    // .axaml and every other drawn glyph on the strip is built the same way in code.
    private static void DrawActiveCaret(DrawingContext context, double x, IBrush? ink)
    {
        if (ink is null)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(x - CaretHalfWidth, CaretTop), isFilled: true);
            figure.LineTo(new Point(x + CaretHalfWidth, CaretTop));
            figure.LineTo(new Point(x, CaretTop + CaretHeight));
            figure.EndFigure(true);
        }

        context.DrawGeometry(ink, null, geometry);
    }

    private enum TextAlign
    {
        Left,
        Centre,
        Right,
    }

    // No fallback ink: an unset label brush draws no label rather than inventing a grey the theme
    // does not carry, which is the rule CalendarHeatmap already follows (spec 14.5).
    private static void DrawLabel(
        DrawingContext context,
        string text,
        Typeface typeface,
        IBrush? brush,
        double x,
        double baseline,
        TextAlign align)
        => TryDrawLabel(context, text, typeface, brush, x, baseline, align);

    /// <summary>The edge-label overload (Phase review P3, NightStrip.cs:504): advances
    /// <paramref name="sequence"/> and records it into <paramref name="ordinal"/> at the exact
    /// point the label actually draws, inside the same call that draws it, so the ordinal can
    /// never drift out of step with a future edit that reorders the draw calls around it. A call
    /// that draws nothing (<see cref="TryDrawLabel"/>'s own null-brush-or-empty-text guard)
    /// leaves both untouched.</summary>
    private static void DrawLabel(
        DrawingContext context,
        string text,
        Typeface typeface,
        IBrush? brush,
        double x,
        double baseline,
        TextAlign align,
        ref int sequence,
        ref int? ordinal)
    {
        if (TryDrawLabel(context, text, typeface, brush, x, baseline, align))
        {
            ordinal = ++sequence;
        }
    }

    private static bool TryDrawLabel(
        DrawingContext context,
        string text,
        Typeface typeface,
        IBrush? brush,
        double x,
        double baseline,
        TextAlign align)
    {
        if (brush is null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            LabelSize,
            brush);

        var left = align switch
        {
            TextAlign.Centre => x - (formatted.Width / 2d),
            TextAlign.Right => x - formatted.Width,
            _ => x,
        };

        // FormattedText draws from its top-left; the comp's figures are SVG baselines.
        context.DrawText(formatted, new Point(left, baseline - formatted.Baseline));
        return true;
    }
}
