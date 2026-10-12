using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Telemetry;
using GalactiLog.App.Views.Telemetry;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// The one-time telemetry question's window: what it shows, and which closes count as "no".
public class TelemetryPromptViewTests
{
    private sealed class Store
    {
        public GeneralSettings Current = new() { SetupComplete = true };
        public int Writes;

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate)
        {
            Writes++;
            return Current = mutate(Current);
        }
    }

    private static (TelemetryPromptView Window, TelemetryPromptViewModel Page, Store Store) Open()
    {
        var store = new Store();
        var page = new TelemetryPromptViewModel(
            store.Mutate, new TelemetryDisclosureViewModel(() => "{\"schema\": 1}", _ => { }));
        var window = new TelemetryPromptView { DataContext = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, page, store);
    }

    [AvaloniaFact]
    public void Window_RendersTheCopy_TheCheckedBox_ThePreviewAndTheLink()
    {
        var (window, _, _) = Open();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains(TelemetryPromptViewModel.Title, texts);
        Assert.Contains(TelemetryDisclosureViewModel.Description, texts);
        Assert.True(window.GetControl<CheckBox>("SendStatisticsBox").IsChecked);
        Assert.NotNull(window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(button => button.Name == "TelemetryPublicStatsButton"));
        Assert.NotNull(window.GetVisualDescendants().OfType<Expander>()
            .SingleOrDefault(expander => expander.Name == "TelemetryPreviewExpander"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task TheTitleBarClose_RecordsNo()
    {
        var (window, page, store) = Open();

        Assert.False(WindowClosingProbe.RaiseClosing(window, WindowCloseReason.WindowClosing).Cancel);
        await page.PendingDecision!;

        Assert.False(store.Current.TelemetryEnabled);
        Assert.True(store.Current.TelemetryPrompted);
        Assert.Null(store.Current.TelemetryId);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_ClosesAndRecordsNo()
    {
        var (window, page, store) = Open();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        await page.PendingDecision!;

        Assert.False(window.IsVisible);
        Assert.True(store.Current.TelemetryPrompted);
        Assert.False(store.Current.TelemetryEnabled);
    }

    [AvaloniaFact]
    public void AShutdownClose_RecordsNothing()
    {
        var (window, page, store) = Open();

        WindowClosingProbe.RaiseClosing(window, WindowCloseReason.ApplicationShutdown);
        WindowClosingProbe.RaiseClosing(window, WindowCloseReason.OSShutdown);

        Assert.Null(page.PendingDecision);
        Assert.Equal(0, store.Writes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Continue_ClosesWithTheCheckboxAnswer()
    {
        var (window, page, store) = Open();

        window.GetControl<Button>("ContinueButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await page.PendingDecision!;

        Assert.False(window.IsVisible);
        Assert.True(store.Current.TelemetryEnabled);
        Assert.NotNull(store.Current.TelemetryId);
        Assert.Equal(1, store.Writes);
    }
}
