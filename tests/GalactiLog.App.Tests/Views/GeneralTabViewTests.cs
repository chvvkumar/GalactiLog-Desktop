using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke test for design-spec 12.7's General tab and design-spec 12.11's
// five residency controls: it parses, lays out and binds against a populated view-model. Compiled
// bindings already turn a binding-path typo into a build error; this catches the rest (a missing
// resource, a section that never renders, a note that was quietly deleted).
public class GeneralTabViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    private static Task<GeneralTabViewModel> NewTabAsync(GeneralSettings? seed = null)
        => PreferenceTabViewModelTestFactory.NewGeneralTab(seed).SettleAsync();

    [AvaloniaFact]
    public void View_DeclaresXDataType()
    {
        // HANDOFF section 4 item 5: every view declares x:DataType, which is also what makes the
        // bindings above compiled rather than reflection based.
        Assert.Contains(
            "x:DataType=\"settings:GeneralTabViewModel\"", ReadMarkup(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task View_RendersAllFiveControls()
    {
        using var tab = await NewTabAsync(new GeneralSettings
        {
            CloseToTray = true,
            MinimizeToTray = true,
            StartMinimized = true,
        });
        var view = new GeneralTabView { DataContext = tab };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        Assert.True(view.GetControl<CheckBox>("CloseToTrayBox").IsChecked);
        Assert.True(view.GetControl<CheckBox>("MinimizeToTrayBox").IsChecked);
        Assert.False(view.GetControl<CheckBox>("StartWithWindowsBox").IsChecked);
        Assert.True(view.GetControl<CheckBox>("StartMinimizedBox").IsChecked);
        Assert.False(view.GetControl<CheckBox>("NotifyOnScanCompleteBox").IsChecked);

        var texts = VisibleTexts(view);
        Assert.Contains("Window behaviour", texts);
        Assert.Contains("Startup", texts);
        Assert.Contains("Notifications", texts);

        // Ruling Q2: the sentence that stands in for the one-time dialog this feature does not
        // have, verbatim.
        Assert.Contains(GeneralTabViewModel.CloseToTrayDescription, texts);

        // Ruling Q6: the control says what the notice actually is, so it cannot promise a toast
        // that never appears.
        Assert.Contains(GeneralTabViewModel.NotifyOnScanCompleteDescription, texts);
    }

    [AvaloniaFact]
    public async Task View_HidesTheEditor_WhileLoading()
    {
        // The read is parked, so the tab is in its loading state for the whole case.
        var gate = new TaskCompletionSource();
        using var tab = new GeneralTabViewModel(
            () =>
            {
                gate.Task.Wait(TimeSpan.FromSeconds(30));
                return new GeneralSettings();
            },
            mutate => mutate(new GeneralSettings()));
        var view = new GeneralTabView { DataContext = tab };
        Show(view);

        Assert.False(view.GetControl<Border>("WindowBehaviourSection").IsVisible);
        Assert.False(view.GetControl<Border>("StartupSection").IsVisible);
        Assert.False(view.GetControl<Border>("NotificationsSection").IsVisible);
        Assert.True(view.GetControl<TextBlock>("LoadingText").IsVisible);

        // Released and then awaited (review minor finding 5): without the await the parked read
        // publishes onto the dispatcher after this method has returned and the tab has been
        // disposed, handing a pending job to whichever AvaloniaFact runs next.
        gate.TrySetResult();
        if (tab.PendingLoad is { } load)
        {
            await load;
        }
    }

    [AvaloniaFact]
    public async Task View_HidesTheEditor_WhenTheReadFailed()
    {
        // Task 5 review finding I1: a live editor over a document that failed to read shows
        // defaults, and one click would write those defaults over whatever the user had.
        using var tab = new GeneralTabViewModel(
            () => throw new InvalidOperationException("no settings"),
            mutate => mutate(new GeneralSettings()),
            post: action => action());
        await tab.SettleAsync();

        var view = new GeneralTabView { DataContext = tab };
        Show(view);

        Assert.True(tab.LoadFailed);
        Assert.False(view.GetControl<Border>("WindowBehaviourSection").IsVisible);
        Assert.True(view.GetControl<TextBlock>("LoadFailedText").IsVisible);
    }

    [AvaloniaFact]
    public async Task View_DisablesStartWithWindows_WithNoSeam()
    {
        using var tab = await NewTabAsync();
        var view = new GeneralTabView { DataContext = tab };
        Show(view);

        Assert.False(view.GetControl<CheckBox>("StartWithWindowsBox").IsEffectivelyEnabled);

        // Spec 12.10: the reason is on screen, never a silent grey.
        Assert.True(view.GetControl<TextBlock>("StartWithWindowsUnavailableText").IsVisible);
        Assert.Contains(GeneralTabViewModel.StartWithWindowsUnavailableMessage, VisibleTexts(view));
    }

    [AvaloniaFact]
    public async Task View_ShowsTheSaveOutcome()
    {
        using var tab = await NewTabAsync();
        var view = new GeneralTabView { DataContext = tab };
        Show(view);

        Assert.False(view.GetControl<TextBlock>("StatusText").IsVisible);

        tab.MinimizeToTray = true;
        await tab.PendingWrite;
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.GetControl<TextBlock>("StatusText").IsVisible);
    }

    [AvaloniaFact]
    public void View_UsesOnlyThemeTokensForColour()
    {
        var markup = ReadMarkup();

        // Spec 14: no literal colour anywhere. Every brush is a DynamicResource token. The same
        // two patterns PreviewModalWindowTests uses, against this view's markup.
        Assert.DoesNotMatch(new Regex(@"=""#[0-9A-Fa-f]{3,8}"""), markup);
        Assert.DoesNotMatch(new Regex(@"(Background|Foreground|BorderBrush|Fill|Stroke)=""(?!\{)"), markup);
    }

    [AvaloniaFact]
    public void View_SetsNoFontSize()
    {
        // The repository-wide FontSizeTokenTest owns the ratio rule; this is the per-view
        // restatement, and it is stricter: this view sets no FontSize at all.
        Assert.DoesNotContain("FontSize", ReadMarkup(), StringComparison.Ordinal);
    }

    private static string ReadMarkup()
    {
        var path = Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Settings", "GeneralTabView.axaml");
        Assert.True(File.Exists(path), $"{path} was not found.");

        // Comments are stripped: this file's own comments explain why every colour is a token, and
        // a scan that read them would find the words it forbids.
        return Regex.Replace(File.ReadAllText(path), "<!--.*?-->", "", RegexOptions.Singleline);
    }
}
