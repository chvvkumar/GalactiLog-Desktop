using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using GalactiLog.App.ViewModels.Stats;

namespace GalactiLog.App.Controls;

/// <summary>
/// Spec 13's Guiding altitude arc: one rig's quarter dome, with the observer at the lower left, the
/// horizon to the right and the zenith at the top, carrying three wedges in horizon-first order.
/// </summary>
/// <remarks>
/// <para>
/// The fourth drawn control, after <see cref="NightStrip"/>, <see cref="CalendarHeatmap"/> and the
/// guide graph, and built on what those already share: a <see cref="Control"/> subclass in
/// <c>Controls/</c> with no markup beside it, drawn in <see cref="Render"/>, one
/// <c>StyledProperty</c> per ink handed in by the host through <c>DynamicResource</c> with no
/// colour literal anywhere in this file, a computed <see cref="MeasureOverride"/>, and one hit test
/// per pointer move.
/// </para>
/// <para>
/// <b>The tooltip takes <see cref="CalendarHeatmap"/>'s route and not <see cref="NightStrip"/>'s</b>,
/// for two reasons that both bind. It is a table of seven figures rather than a line of text, which
/// <c>ToolTip.SetTip(this, string)</c> cannot carry; and spec 12.5 requires it to answer focus as
/// well as hover, which an attached string tip cannot. So <see cref="HoveredWedge"/> is a
/// <c>DirectProperty</c> and the page binds a panel to it, exactly as it already binds the
/// heatmap's hovered cell one section away.
/// </para>
/// <para>
/// <b>This control is focusable at rest</b>, which is the opposite of
/// <see cref="NightStrip"/>'s rule. The night strip earns focus only on a press, because it sits
/// above a frame table that must keep focus; this one sits in a page body with nothing competing
/// for it, and spec 12.5 requires the figures behind a shape to be reachable without a pointer. A
/// reader who knows the strip's rule should read that difference as deliberate.
/// </para>
/// <para>
/// The geometry is the web's own (<c>GuidingAltitude.tsx:8-33</c>): a 190 by 172 field, the
/// observer at (12, 160), radius 140, and the three wedges at 0 to 30, 30 to 60 and 60 to 90
/// degrees. The wedge shades arrive on the wedge records, built from two theme tokens by the
/// view-model's ramp, so the shading rule and the colours both live where they can be asserted
/// without a window.
/// </para>
/// </remarks>
public sealed class AltitudeArc : Control
{
    /// <summary>The web's <c>viewBox</c> width, the unit the geometry below is written in.
    /// </summary>
    internal const double BaseWidth = 190d;

    /// <summary>The web's <c>viewBox</c> height.</summary>
    internal const double BaseHeight = 172d;

    private const double CentreX = 12d;
    private const double CentreY = 160d;
    private const double Radius = 140d;

    /// <summary>Where a wedge's own figures are centred, as a fraction of <see cref="Radius"/>.
    /// The web hard-codes three label points; this is the radius all three of them sit at.</summary>
    private const double LabelRadius = 0.743d;

    private const double MinScale = 0.6d;
    private const double MaxScale = 1.8d;

    /// <summary>The three band boundaries in degrees, so four edges bound three wedges.</summary>
    private static readonly double[] Edges = [0d, 30d, 60d, 90d];

    public static readonly StyledProperty<IReadOnlyList<AltitudeWedge>?> WedgesProperty =
        AvaloniaProperty.Register<AltitudeArc, IReadOnlyList<AltitudeWedge>?>(nameof(Wedges));

    /// <summary>The wedge outline and the horizon axis. Bound to a theme token in the page's XAML.
    /// </summary>
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<AltitudeArc, IBrush?>(nameof(OutlineBrush));

    /// <summary>The RMS figure each wedge prints. Bound to the primary ink.</summary>
    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<AltitudeArc, IBrush?>(nameof(ValueBrush));

    /// <summary>The degree ticks, the horizon word, the ratio and count lines, and the "no data"
    /// label. Bound to the tertiary ink.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<AltitudeArc, IBrush?>(nameof(LabelBrush));

    public static readonly DirectProperty<AltitudeArc, AltitudeWedge?> HoveredWedgeProperty =
        AvaloniaProperty.RegisterDirect<AltitudeArc, AltitudeWedge?>(
            nameof(HoveredWedge), control => control.HoveredWedge);

    private AltitudeWedge? _hoveredWedge;
    private double _scale = 1d;

    static AltitudeArc()
    {
        AffectsRender<AltitudeArc>(
            WedgesProperty, OutlineBrushProperty, ValueBrushProperty, LabelBrushProperty);

        // Spec 12.5: the wedge is focusable and the tooltip answers focus as well as hover.
        FocusableProperty.OverrideDefaultValue<AltitudeArc>(true);
    }

    /// <summary>The rig's three wedges, horizon first. Fewer than three draws fewer; none draws the
    /// empty dome and never divides by anything.</summary>
    public IReadOnlyList<AltitudeWedge>? Wedges
    {
        get => GetValue(WedgesProperty);
        set => SetValue(WedgesProperty, value);
    }

    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public IBrush? ValueBrush
    {
        get => GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <summary>
    /// The wedge under the pointer, or the focused one, or null. Read-only from outside: the
    /// control owns it and the page binds its tooltip panel to it.
    /// </summary>
    public AltitudeWedge? HoveredWedge
    {
        get => _hoveredWedge;
        private set => SetAndRaise(HoveredWedgeProperty, ref _hoveredWedge, value);
    }

    /// <summary>The scale the field is drawn at, one at the web's own 190 unit width. For the
    /// layout case.</summary>
    internal double CurrentScale => _scale;

    /// <summary>
    /// The wedge at a point in this control's coordinates, or null outside the dome. Internal so
    /// the hit test is asserted directly rather than through a synthetic pointer event.
    /// </summary>
    internal AltitudeWedge? WedgeAt(Point point)
    {
        if (Wedges is not { Count: > 0 } wedges)
        {
            return null;
        }

        var dx = (point.X / _scale) - CentreX;
        var dy = CentreY - (point.Y / _scale);
        if (dx < 0 || dy < 0)
        {
            return null;
        }

        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance > Radius)
        {
            return null;
        }

        var degrees = Math.Atan2(dy, dx) * 180d / Math.PI;
        for (var index = 0; index < wedges.Count && index < Edges.Length - 1; index++)
        {
            if (degrees >= Edges[index] && (degrees < Edges[index + 1] || index == Edges.Length - 2))
            {
                return wedges[index];
            }
        }

        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The field scales with the width the control is given and the aspect follows, so a card in
        // the wrapping grid draws a square-cornered dome at every column width. An unbounded
        // measure falls back to the web's own unit scale. Stored rather than derived again in
        // Render, which is the defect CalendarHeatmap's own I3 finding records.
        _scale = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? 1d
            : Math.Clamp(availableSize.Width / BaseWidth, MinScale, MaxScale);

        return new Size(BaseWidth * _scale, BaseHeight * _scale);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HoveredWedge = WedgeAt(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredWedge = null;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        if (HoveredWedge is null && Wedges is { Count: > 0 } wedges)
        {
            HoveredWedge = wedges[0];
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        HoveredWedge = null;
    }

    /// <summary>Left and right, or up and down, step between the three wedges, which is what makes
    /// every figure on this card reachable from the keyboard.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Wedges is not { Count: > 0 } wedges)
        {
            return;
        }

        var step = e.Key switch
        {
            Key.Right or Key.Up => 1,
            Key.Left or Key.Down => -1,
            _ => 0,
        };
        if (step == 0)
        {
            return;
        }

        var current = HoveredWedge is null ? -1 : IndexOf(wedges, HoveredWedge);
        var next = Math.Clamp(current + step, 0, wedges.Count - 1);
        HoveredWedge = wedges[next];
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // The whole field answers the pointer, not only the drawn arcs, so a hover that lands a
        // pixel outside a wedge edge still reaches this control's hit test rather than the panel
        // behind it. NightStrip carries the same first operation for the same reason.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        var scale = _scale;
        var centre = new Point(CentreX * scale, CentreY * scale);
        var radius = Radius * scale;
        var outline = OutlineBrush is null ? null : new Pen(OutlineBrush, Math.Max(1d, 2d * scale));
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var wedges = Wedges ?? [];

        for (var index = 0; index < wedges.Count && index < Edges.Length - 1; index++)
        {
            context.DrawGeometry(
                wedges[index].Fill,
                outline,
                Wedge(centre, radius, Edges[index], Edges[index + 1]));
        }

        if (outline is not null)
        {
            context.DrawLine(outline, centre, new Point(centre.X + radius, centre.Y));
        }

        for (var index = 0; index < wedges.Count && index < Edges.Length - 1; index++)
        {
            DrawReadings(
                context,
                typeface,
                wedges[index],
                Polar(centre, radius * LabelRadius, (Edges[index] + Edges[index + 1]) / 2d),
                scale);
        }

        if (LabelBrush is not { } labels)
        {
            return;
        }

        // The four degree ticks just outside the dome, and the word under the horizon.
        foreach (var degrees in Edges)
        {
            var at = Polar(centre, radius + (12d * scale), degrees);
            Text(context, typeface, degrees.ToString("0", CultureInfo.InvariantCulture), 8.4d * scale, labels, at);
        }

        Text(
            context,
            typeface,
            "horizon",
            7.6d * scale,
            labels,
            new Point(centre.X + radius, centre.Y + (4d * scale)),
            align: 1d);
    }

    private void DrawReadings(
        DrawingContext context,
        Typeface typeface,
        AltitudeWedge wedge,
        Point at,
        double scale)
    {
        if (!wedge.HasData)
        {
            if (LabelBrush is { } empty)
            {
                Text(context, typeface, wedge.ValueText, 8.4d * scale, empty, at);
            }

            return;
        }

        if (ValueBrush is { } value)
        {
            Text(context, typeface, wedge.ValueText, 12d * scale, value, at);
        }

        if (LabelBrush is not { } secondary)
        {
            return;
        }

        var line = at.WithY(at.Y + (12d * scale));
        if (wedge.HasRatio)
        {
            Text(context, typeface, wedge.Ratio, 8.4d * scale, secondary, line);
        }

        Text(context, typeface, wedge.CountText, 8.4d * scale, secondary, at.WithY(at.Y + (22d * scale)));
    }

    // A pie slice of the dome between two altitudes, drawn counter-clockwise from the lower edge,
    // which is the direction a rising altitude runs in screen coordinates.
    private static StreamGeometry Wedge(Point centre, double radius, double from, double to)
    {
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(centre, true);
            sink.LineTo(Polar(centre, radius, from));
            sink.ArcTo(
                Polar(centre, radius, to),
                new Size(radius, radius),
                0d,
                false,
                SweepDirection.CounterClockwise);
            sink.EndFigure(true);
        }

        return geometry;
    }

    private static Point Polar(Point centre, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return new Point(
            centre.X + (radius * Math.Cos(radians)),
            centre.Y - (radius * Math.Sin(radians)));
    }

    // align 0 centres the text on the point, 1 ends it there.
    private static void Text(
        DrawingContext context,
        Typeface typeface,
        string value,
        double size,
        IBrush brush,
        Point at,
        double align = 0d)
    {
        var text = new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(6d, size),
            brush);
        context.DrawText(text, new Point(at.X - (text.Width * (align == 0d ? 0.5d : 1d)), at.Y - (text.Height / 2d)));
    }

    private static int IndexOf(IReadOnlyList<AltitudeWedge> wedges, AltitudeWedge wedge)
    {
        for (var index = 0; index < wedges.Count; index++)
        {
            if (ReferenceEquals(wedges[index], wedge))
            {
                return index;
            }
        }

        return -1;
    }
}
