using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using GalactiLog.App.Controls;
using GalactiLog.App.ViewModels.Stats;
using Xunit;

namespace GalactiLog.App.Tests.Controls;

// Spec 18.3's smoke shape for the one custom-drawn control this phase adds: it constructs, it lays
// out non-zero, and it reports a hovered cell for a pointer position inside a known cell. Pixel
// comparison and real rendering are out of scope.
public class CalendarHeatmapTests
{
    private static readonly IImmutableSolidColorBrush Swatch = new ImmutableSolidColorBrush(Colors.Teal);

    private static IReadOnlyList<CalendarCell> Grid(int weeks)
    {
        var start = new DateOnly(2025, 1, 6);
        var cells = new List<CalendarCell>();
        for (var column = 0; column < weeks; column++)
        {
            for (var row = 0; row < 7; row++)
            {
                var date = start.AddDays((column * 7) + row);
                cells.Add(new CalendarCell(date, column, row, row % 5, Swatch, $"tip {column}:{row}"));
            }
        }

        return cells;
    }

    // Review finding I3: the harness used to set HorizontalAlignment itself, which the shipped XAML
    // did not, so the smoke tests exercised a layout the page never used. The alignment now lives
    // on the control in StatisticsView.axaml and the harness reproduces the shipped shape, scroller
    // included.
    private static (Window Window, CalendarHeatmap Heatmap) Show(int weeks = 8)
    {
        var heatmap = new CalendarHeatmap
        {
            Cells = Grid(weeks),
            MonthLabels = [new CalendarMonthLabel(0, "Jan"), new CalendarMonthLabel(4, "Feb")],
            WeekCount = weeks,
            LabelBrush = Brushes.Gray,
        };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = heatmap,
        };

        var window = new Window { Width = 800, Height = 400, Content = scroller };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, heatmap);
    }

    [AvaloniaFact]
    public void Constructs_AndLaysOutNonZero()
    {
        var (_, heatmap) = Show();

        Assert.True(heatmap.Bounds.Width > 0);
        Assert.True(heatmap.Bounds.Height > 0);
    }

    [AvaloniaTheory]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(53)]
    public void Layout_HeightIsTheHeaderPlusSevenPitches_WhateverTheWeekCount(int weeks)
    {
        var (_, heatmap) = Show(weeks);

        // Review finding M13: the rule the old test's name claimed and never asserted. The
        // reserved size is the header plus seven pitches tall and the gutter plus one pitch per
        // week wide, whatever the week count.
        var drawnHeight = (CalendarHeatmap.HeaderPitches + 7) * heatmap.CurrentPitch;
        Assert.Equal(drawnHeight, heatmap.DesiredSize.Height, 3);
        Assert.Equal(
            (CalendarHeatmap.GutterPitches + weeks) * heatmap.CurrentPitch,
            heatmap.DesiredSize.Width,
            3);

        // And finding I3 itself: the grid is drawn at the pitch it was measured at, so it never
        // spills past the bounds it was arranged into, however much width or height the
        // ScrollContentPresenter hands a stretched child.
        Assert.True(
            drawnHeight <= heatmap.Bounds.Height + 0.001d,
            $"drawn height {drawnHeight} must fit the arranged {heatmap.Bounds.Height}");
        Assert.True(
            (CalendarHeatmap.GutterPitches + weeks) * heatmap.CurrentPitch <= heatmap.Bounds.Width + 0.001d,
            "drawn width must fit the arranged width");
    }

    [AvaloniaFact]
    public void Layout_AWiderGridIsWider_AndNoTaller()
    {
        var (_, narrow) = Show(weeks: 10);
        var (_, wide) = Show(weeks: 40);

        Assert.True(wide.DesiredSize.Width > narrow.DesiredSize.Width);
        Assert.Equal(narrow.DesiredSize.Height, wide.DesiredSize.Height, 3);
    }

    [AvaloniaFact]
    public void HoveredCell_IsTheCellUnderThePointer()
    {
        var (_, heatmap) = Show();

        // The middle of column 3, row 2, computed from the control's own pitch rather than from a
        // hard-coded pixel figure.
        var pitch = heatmap.CurrentPitch;
        var point = new Point(
            (CalendarHeatmap.GutterPitches + 3 + 0.4d) * pitch,
            (CalendarHeatmap.HeaderPitches + 2 + 0.4d) * pitch);

        var cell = heatmap.CellAt(point);

        Assert.NotNull(cell);
        Assert.Equal(3, cell!.Column);
        Assert.Equal(2, cell.Row);
        Assert.Equal("tip 3:2", cell.Tooltip);
    }

    [AvaloniaFact]
    public void HoveredCell_IsNull_InTheLabelGutters()
    {
        var (_, heatmap) = Show();

        Assert.Null(heatmap.CellAt(new Point(1, 1)));
        Assert.Null(heatmap.CellAt(new Point(-5, -5)));
    }

    [AvaloniaFact]
    public void HoveredCell_IsNull_InTheGapBetweenTwoCells()
    {
        var (_, heatmap) = Show();
        var pitch = heatmap.CurrentPitch;

        // Review finding M9: a cell covers thirteen sixteenths of its pitch. A point in the last
        // sixteenth belongs to the gap, not to the day above and to the left of it.
        var inGap = new Point(
            (CalendarHeatmap.GutterPitches + 3 + 0.95d) * pitch,
            (CalendarHeatmap.HeaderPitches + 2 + 0.4d) * pitch);
        var inCell = new Point(
            (CalendarHeatmap.GutterPitches + 3 + 0.4d) * pitch,
            (CalendarHeatmap.HeaderPitches + 2 + 0.4d) * pitch);

        Assert.Null(heatmap.CellAt(inGap));
        Assert.NotNull(heatmap.CellAt(inCell));

        // And the same rule on the other axis.
        Assert.Null(heatmap.CellAt(new Point(
            inCell.X,
            (CalendarHeatmap.HeaderPitches + 2 + 0.95d) * pitch)));
    }

    [AvaloniaFact]
    public void HoveredTooltip_FollowsTheHoveredCell()
    {
        var (window, heatmap) = Show();
        var pitch = heatmap.CurrentPitch;

        window.MouseMove(new Point(
            (CalendarHeatmap.GutterPitches + 1 + 0.4d) * pitch,
            (CalendarHeatmap.HeaderPitches + 0 + 0.4d) * pitch));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("tip 1:0", heatmap.HoveredTooltip);
    }

    [AvaloniaFact]
    public void NoCells_LaysOutWithoutThrowing()
    {
        var heatmap = new CalendarHeatmap { WeekCount = 0 };
        var window = new Window { Width = 400, Height = 200, Content = heatmap };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(heatmap.CellAt(new Point(50, 50)));
        Assert.Equal("", heatmap.HoveredTooltip);
    }
}
