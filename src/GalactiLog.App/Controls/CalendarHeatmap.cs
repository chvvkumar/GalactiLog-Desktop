using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using GalactiLog.App.ViewModels.Stats;

namespace GalactiLog.App.Controls;

/// <summary>
/// Spec 13's Imaging calendar: a GitHub-style year heatmap, drawn directly rather than composed
/// out of controls.
/// </summary>
/// <remarks>
/// <para>
/// Spec 13's row says "a custom <c>CalendarHeatmap</c> control rather than LiveCharts", and this is
/// it. A year is 53 columns by 7 rows, so a <c>UserControl</c> of 371 <c>Border</c>s would be 371
/// visuals, 371 layout entries and 371 bindings for a grid that never changes shape;
/// <see cref="Render"/> draws the same thing in one pass. There is no XAML file for the same
/// reason: nothing here is composed, so a markup shell would be an empty partial.
/// </para>
/// <para>
/// Every colour arrives from outside. The cell brushes come from the view-model's ramp, which is
/// interpolated between two theme tokens; <see cref="LabelBrush"/> is bound to a token in the
/// page's XAML. Nothing below spells one.
/// </para>
/// </remarks>
public sealed class CalendarHeatmap : Control
{
    /// <summary>The web's <c>CELL</c> and <c>GAP</c>, 13 and 3, so the pitch is 16 and a cell
    /// covers 13 sixteenths of it. Used as the natural size; <see cref="Render"/> scales the pitch
    /// to whatever width the control is arranged at and keeps that ratio.</summary>
    internal const double BasePitch = 16d;

    private const double CellRatio = 13d / 16d;

    /// <summary>Room for the three-letter day labels, in pitches.</summary>
    internal const double GutterPitches = 2.5d;

    /// <summary>Room for the month labels above the grid, in pitches.</summary>
    internal const double HeaderPitches = 1.25d;

    /// <summary>Below this a cell is smaller than the gap between two of them.</summary>
    private const double MinPitch = 8d;

    /// <summary>Above this a short range turns into a wall of squares.</summary>
    private const double MaxPitch = 28d;

    public static readonly StyledProperty<IReadOnlyList<CalendarCell>?> CellsProperty =
        AvaloniaProperty.Register<CalendarHeatmap, IReadOnlyList<CalendarCell>?>(nameof(Cells));

    public static readonly StyledProperty<IReadOnlyList<CalendarMonthLabel>?> MonthLabelsProperty =
        AvaloniaProperty.Register<CalendarHeatmap, IReadOnlyList<CalendarMonthLabel>?>(nameof(MonthLabels));

    public static readonly StyledProperty<int> WeekCountProperty =
        AvaloniaProperty.Register<CalendarHeatmap, int>(nameof(WeekCount));

    /// <summary>The month and day label colour. Bound to a theme token in the page's XAML, never
    /// defaulted to a literal here.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<CalendarHeatmap, IBrush?>(nameof(LabelBrush));

    public static readonly DirectProperty<CalendarHeatmap, CalendarCell?> HoveredCellProperty =
        AvaloniaProperty.RegisterDirect<CalendarHeatmap, CalendarCell?>(
            nameof(HoveredCell), control => control.HoveredCell);

    public static readonly DirectProperty<CalendarHeatmap, string> HoveredTooltipProperty =
        AvaloniaProperty.RegisterDirect<CalendarHeatmap, string>(
            nameof(HoveredTooltip), control => control.HoveredTooltip);

    private CalendarCell? _hoveredCell;
    private string _hoveredTooltip = "";

    // Review finding I3: one pitch, computed in MeasureOverride and used by Render and CellAt.
    // Deriving it a second time from Bounds.Width made the drawn grid taller than the size the
    // control had reserved whenever arrange handed it more width than it asked for, which is what
    // a ScrollContentPresenter does for a stretched child.
    private double _pitch = BasePitch;

    static CalendarHeatmap()
    {
        AffectsRender<CalendarHeatmap>(CellsProperty, MonthLabelsProperty, LabelBrushProperty);
        AffectsMeasure<CalendarHeatmap>(WeekCountProperty);
    }

    public IReadOnlyList<CalendarCell>? Cells
    {
        get => GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    public IReadOnlyList<CalendarMonthLabel>? MonthLabels
    {
        get => GetValue(MonthLabelsProperty);
        set => SetValue(MonthLabelsProperty, value);
    }

    public int WeekCount
    {
        get => GetValue(WeekCountProperty);
        set => SetValue(WeekCountProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <summary>The cell under the pointer, or null. Read-only from outside: the control owns it.
    /// </summary>
    public CalendarCell? HoveredCell
    {
        get => _hoveredCell;
        private set
        {
            if (SetAndRaise(HoveredCellProperty, ref _hoveredCell, value))
            {
                HoveredTooltip = value?.Tooltip ?? "";
            }
        }
    }

    /// <summary>The hovered cell's tooltip text, or empty. A plain string so the page can bind a
    /// caption line to it, which is more reliable than a tooltip that has to re-open on every
    /// pointer move.</summary>
    public string HoveredTooltip
    {
        get => _hoveredTooltip;
        private set => SetAndRaise(HoveredTooltipProperty, ref _hoveredTooltip, value);
    }

    /// <summary>
    /// The cell at a point in this control's coordinates, or null when the point is outside the
    /// grid. Internal so the hit test is asserted directly rather than through a synthetic pointer
    /// event.
    /// </summary>
    internal CalendarCell? CellAt(Point point)
    {
        if (Cells is not { Count: > 0 } cells)
        {
            return null;
        }

        var pitch = _pitch;
        var x = point.X - (GutterPitches * pitch);
        var y = point.Y - (HeaderPitches * pitch);
        if (x < 0 || y < 0)
        {
            return null;
        }

        var column = (int)(x / pitch);
        var row = (int)(y / pitch);
        if (row is < 0 or > 6)
        {
            return null;
        }

        // Review finding M9: a cell covers CellRatio of its pitch and the rest is the gap between
        // two days. A pointer resting in that gutter belongs to no cell, so it reports none rather
        // than claiming the day above and to the left of it.
        var cellSize = pitch * CellRatio;
        if (x % pitch > cellSize || y % pitch > cellSize)
        {
            return null;
        }

        foreach (var cell in cells)
        {
            if (cell.Column == column && cell.Row == row)
            {
                return cell;
            }
        }

        return null;
    }

    /// <summary>The pitch the grid is measured and drawn at, for the layout test.</summary>
    internal double CurrentPitch => _pitch;

    protected override Size MeasureOverride(Size availableSize)
    {
        // The pitch scales with the width the control is given and the aspect follows, so the grid
        // is square cells at every window size. An unbounded measure (inside a horizontal
        // scroller) falls back to the web's own 16 unit pitch. Stored rather than recomputed
        // later: see the _pitch field.
        var columns = Math.Max(1, WeekCount);
        _pitch = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? BasePitch
            : Math.Clamp(availableSize.Width / (GutterPitches + columns), MinPitch, MaxPitch);

        return new Size((GutterPitches + columns) * _pitch, (HeaderPitches + 7) * _pitch);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HoveredCell = CellAt(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredCell = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var pitch = _pitch;
        var size = pitch * CellRatio;
        var radius = size * 0.2d;
        var gutter = GutterPitches * pitch;
        var header = HeaderPitches * pitch;
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var labelSize = Math.Max(6d, pitch * 0.6d);

        foreach (var cell in Cells ?? [])
        {
            var rect = new Rect(
                gutter + (cell.Column * pitch),
                header + (cell.Row * pitch),
                size,
                size);
            context.DrawRectangle(cell.Brush, null, new RoundedRect(rect, radius));
        }

        // No fallback colour: an unset LabelBrush draws no labels rather than inventing a grey
        // that is not in the theme (spec 14.5). The page binds it to a token.
        if (LabelBrush is not { } brush)
        {
            return;
        }

        foreach (var label in MonthLabels ?? [])
        {
            var text = new FormattedText(
                label.Text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                labelSize,
                brush);
            context.DrawText(text, new Point(gutter + (label.Column * pitch), 0));
        }

        // Rows 0, 2, 4 and 6: Mon, Wed, Fri, Sun. The web labels every other row for the same
        // reason, that seven three-letter labels at a 16 unit pitch overlap.
        foreach (var row in ImagingCalendarViewModel.LabelledDayRows)
        {
            var text = new FormattedText(
                ImagingCalendarViewModel.DayNames[row],
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                labelSize,
                brush);
            context.DrawText(text, new Point(0, header + (row * pitch)));
        }
    }

}
