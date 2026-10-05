using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Polish ruling 2: the base Button style centres its content for every class, because Fluent
// leaves VerticalContentAlignment at Stretch and the label sits high under the theme's 12,0
// padding. A failure reads as Expected Center, Actual Stretch on the rendered presenter.
public class ButtonContentCentringTests
{
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("primary")]
    [InlineData("sm")]
    [InlineData("pill")]
    [InlineData("quiet")]
    public void EveryButtonClass_CentresItsContentVertically(string buttonClass)
    {
        var button = new Button { Content = "Next" };
        if (buttonClass.Length > 0)
        {
            button.Classes.Add(buttonClass);
        }

        var window = new Window { Width = 400, Height = 200, Content = button };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(VerticalAlignment.Center, presenter.VerticalContentAlignment);

        window.Close();
    }
}
