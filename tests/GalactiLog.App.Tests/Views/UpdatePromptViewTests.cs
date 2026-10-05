using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Update;
using GalactiLog.App.Views.Update;
using Xunit;

namespace GalactiLog.App.Tests.Views;

/// <summary>
/// Spec 17.1's confirmation window: the fifth dialog on the one modal host, in the shape
/// <c>ModalPageWindow{TViewModel}</c> owns (FIXER LIST F20).
/// </summary>
public class UpdatePromptViewTests
{
    private static readonly WindowCloseReason[] EveryReason =
    [
        WindowCloseReason.WindowClosing,
        WindowCloseReason.OSShutdown,
        WindowCloseReason.ApplicationShutdown,
        WindowCloseReason.OwnerWindowClosing,
    ];

    private static UpdatePromptViewModel Page(Action? install = null)
        => new("2.0.0", "1.9.3.0", "stable", "What changed", install ?? (() => { }));

    [AvaloniaFact]
    public void Window_RendersTheVersionsTheNotesAndBothAnswers()
    {
        var window = new UpdatePromptView { DataContext = Page() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains(UpdatePromptViewModel.Title, texts);
        Assert.Contains(UpdatePromptViewModel.InstallLabel, texts);
        Assert.Contains(UpdatePromptViewModel.LaterLabel, texts);
        Assert.Equal(
            "What changed", window.GetControl<SelectableTextBlock>("ReleaseNotesText").Text);
        Assert.Contains("2.0.0", window.GetControl<SelectableTextBlock>("SummaryText").Text!, StringComparison.Ordinal);

        window.Close();
    }

    [AvaloniaFact]
    public void Window_ClosesWithTheResultThePageAsksFor()
    {
        var applies = 0;
        var window = new UpdatePromptView { DataContext = Page(() => applies++) };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.GetControl<Button>("InstallButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, applies);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Window_RefusesNoCloseReason()
    {
        var window = new UpdatePromptView { DataContext = Page() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Dismissing this dialog is the "Later" answer, so every close reason means the same
        // thing and none of them is vetoed.
        foreach (var reason in EveryReason)
        {
            Assert.False(WindowClosingProbe.RaiseClosing(window, reason).Cancel);
        }

        window.Close();
    }
}
