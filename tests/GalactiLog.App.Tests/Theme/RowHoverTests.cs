using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// A table row's hover fill, as rendered once the pointer comes to rest.
//
// The brush fades Theme/Controls.axaml once declared on the presenters never settled: Avalonia
// 11.3's BrushTransition advances only with a redraw, so a row lit by the pointer froze at the
// first blend frame the moment the mouse stopped moving, which read as the highlight switching
// off. These cases read the live Background after the clock has run well past any fade, so a
// fade that stalls fails them where a base-value read would not.
public class RowHoverTests
{
    private static Color Token(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    // Real elapsed time between render ticks, so a transition that does follow the clock finishes.
    private static void Rest(int milliseconds)
    {
        for (var elapsed = 0; elapsed < milliseconds; elapsed += 25)
        {
            Thread.Sleep(25);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Point Centre(Visual visual, Window window)
        => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    private static Window Show(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void ATableRowUnderARestingPointer_ShowsTheWholeHoverFill()
    {
        var list = new ListBox { ItemsSource = new[] { "a", "b", "c" }, Width = 300, Height = 120 };
        list.Classes.Add("table");
        var window = Show(list, 300, 120);
        var item = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First();

        window.MouseMove(Centre(item, window));
        Dispatcher.UIThread.RunJobs();
        Rest(300);

        Assert.True(item.IsPointerOver);
        Assert.Equal(Token("ColorBgHover"), ColorOf(presenter.Background));
        window.Close();
    }

    // The dashboard's full-row open button sits under the cells of a ListBoxItem that already
    // lights on hover, so a button hover of its own would stack a second fill, an outline and a
    // brighter ink on the same row.
    [AvaloniaFact]
    public void TheRowOpenButton_AddsNoHoverOfItsOwn()
    {
        var list = new ListBox { Width = 300, Height = 120 };
        list.Classes.Add("table");
        var open = new Button { Width = 280, Height = 28 };
        open.Classes.Add("tc-rowopen");
        list.Items.Add(open);
        var window = Show(list, 300, 120);
        var presenter = open.GetVisualDescendants().OfType<ContentPresenter>().First();

        window.MouseMove(Centre(open, window));
        Dispatcher.UIThread.RunJobs();
        Rest(300);

        Assert.True(open.IsPointerOver);
        Assert.Equal(0, ColorOf(presenter.Background).A);
        Assert.Equal(0, ColorOf(presenter.BorderBrush).A);
        window.Close();
    }
}
