using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// Spec 14's splitter handle: the one GridSplitter style in Theme/Controls.axaml, an 8 pixel grab
// zone with three grip dots centred along it. Before it, both splitters in the application drew a
// transparent 4 or 6 pixel bar and the user could not see what to grab. Each expectation is the
// token brush the merged dictionary resolves, compared by reference, so the cases fail only when
// the style stops naming the token.
public class GridSplitterThemeTests
{
    // Layout rounding snaps a 3 pixel dot centred in an 8 pixel bar to a whole pixel offset, so
    // its centre lands at 3.5 rather than 4: centred to within half a pixel is the honest claim.
    private const double HalfPixel = 0.5;

    private static IBrush Token(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
        return Assert.IsAssignableFrom<IBrush>(value);
    }

    private static (Window Window, GridSplitter Splitter) ShowSplitter(GridResizeDirection direction)
    {
        var splitter = new GridSplitter { ResizeDirection = direction };
        var grid = new Grid();

        if (direction == GridResizeDirection.Columns)
        {
            grid.ColumnDefinitions = new ColumnDefinitions("*,Auto,*");
            Grid.SetColumn(splitter, 1);
        }
        else
        {
            grid.RowDefinitions = new RowDefinitions("*,Auto,*");
            Grid.SetRow(splitter, 1);
        }

        grid.Children.Add(splitter);
        var window = new Window { Width = 400, Height = 300, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, splitter);
    }

    private static List<Ellipse> DotsOf(GridSplitter splitter)
        => splitter.GetVisualDescendants().OfType<Ellipse>().ToList();

    private static Border ZoneOf(GridSplitter splitter)
        => splitter.GetVisualDescendants().OfType<Border>().First();

    [AvaloniaFact]
    public void AColumnSplitter_IsEightWide_WithThreeDotsStackedDownItsMiddle()
    {
        var (window, splitter) = ShowSplitter(GridResizeDirection.Columns);

        Assert.Equal(8d, splitter.Bounds.Width, 3);

        var dots = DotsOf(splitter);
        Assert.Equal(3, dots.Count);
        var centres = dots.Select(dot => dot.TranslatePoint(new Point(1.5, 1.5), splitter)!.Value).ToList();
        Assert.All(centres, centre => Assert.Equal(4d, centre.X, HalfPixel));
        Assert.True(centres[0].Y < centres[1].Y && centres[1].Y < centres[2].Y);
        Assert.Equal(splitter.Bounds.Height / 2d, centres[1].Y, HalfPixel);

        window.Close();
    }

    [AvaloniaFact]
    public void ARowSplitter_IsEightHigh_WithThreeDotsInARowAcrossItsMiddle()
    {
        var (window, splitter) = ShowSplitter(GridResizeDirection.Rows);

        Assert.Equal(8d, splitter.Bounds.Height, 3);

        var dots = DotsOf(splitter);
        Assert.Equal(3, dots.Count);
        var centres = dots.Select(dot => dot.TranslatePoint(new Point(1.5, 1.5), splitter)!.Value).ToList();
        Assert.All(centres, centre => Assert.Equal(4d, centre.Y, HalfPixel));
        Assert.True(centres[0].X < centres[1].X && centres[1].X < centres[2].X);
        Assert.Equal(splitter.Bounds.Width / 2d, centres[1].X, HalfPixel);

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(GridResizeDirection.Columns, ":pointerover")]
    [InlineData(GridResizeDirection.Rows, ":pointerover")]
    [InlineData(GridResizeDirection.Columns, ":pressed")]
    public void TheHandle_IsBareWithTertiaryDotsAtRest_AndTintsUnderThePointerOrADrag(
        GridResizeDirection direction, string state)
    {
        var (window, splitter) = ShowSplitter(direction);
        var zone = ZoneOf(splitter);

        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(zone.Background).Color.A);
        Assert.All(DotsOf(splitter), dot => Assert.Same(Token("ColorTextTertiary"), dot.Fill));
        Assert.All(DotsOf(splitter), dot => Assert.False(dot.IsHitTestVisible));

        ((IPseudoClasses)splitter.Classes).Set(state, true);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(Token("ColorBgHover"), zone.Background);
        Assert.All(DotsOf(splitter), dot => Assert.Same(Token("ColorTextPrimary"), dot.Fill));

        window.Close();
    }
}
