using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using GalactiLog.App.Controls;
using Xunit;

namespace GalactiLog.App.Tests.Controls;

// The Nights list divider's viewport: the child keeps its own width whatever the viewport is given.
public class SlideOverViewportTests
{
    private static Window Show(Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto"), HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(content);
        var window = new Window { Width = 800, Height = 600, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Unconstrained_TakesTheChildsWidth()
    {
        var viewport = new SlideOverViewport { Child = new Border { Width = 300, Height = 20 } };
        Show(viewport);

        Assert.Equal(300, viewport.Bounds.Width, 0.5);
        Assert.Equal(300, viewport.NaturalWidth, 0.5);
    }

    [AvaloniaFact]
    public void Narrowed_KeepsTheChildAtItsWidth_AndClips()
    {
        var child = new Border { Width = 300, Height = 20 };
        var viewport = new SlideOverViewport { Width = 120, Child = child };
        Show(viewport);

        Assert.Equal(300, child.Bounds.Width, 0.5);
        Assert.Equal(0, child.Bounds.X, 0.5);
        Assert.True(viewport.ClipToBounds);
        Assert.Equal(120, viewport.Bounds.Width, 0.5);
    }

    [AvaloniaFact]
    public void Wider_StretchesTheChild()
    {
        var child = new Grid { ColumnDefinitions = new ColumnDefinitions("*") };
        child.Children.Add(new Border { Width = 300, Height = 20 });
        var viewport = new SlideOverViewport { Width = 400, Child = child };
        Show(viewport);

        Assert.Equal(400, child.Bounds.Width, 0.5);
    }
}
