using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.AboutTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

/// <summary>
/// The roadmap's Phase 10 row 4 Verify clause for the view: the About tab renders every listed
/// field. Spec 12.7 lists six.
/// </summary>
public class AboutTabViewTests
{
    private static (Window Window, AboutTabView View) Show(AboutTabViewModel page)
    {
        var view = new AboutTabView { DataContext = page };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static List<string?> Texts(AboutTabView view)
        => [.. view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)];

    [AvaloniaFact]
    public void Constructs_AndLaysOutNonZero()
    {
        using var harness = Factory.Create();
        var (_, view) = Show(harness.ViewModel);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void View_RendersEverySpec127Field()
    {
        using var harness = Factory.Create(
            new BuildInfo("1.4.2.0", "0b1d3f5a9c", "alpha", isInstalled: true));
        var (_, view) = Show(harness.ViewModel);

        var texts = Texts(view);

        // The six fields spec 12.7 names, by name.
        Assert.Contains(AboutTabViewModel.VersionLabel, texts);
        Assert.Contains(AboutTabViewModel.GitShaLabel, texts);
        Assert.Contains(AboutTabViewModel.ChannelLabel, texts);
        Assert.Contains(AboutTabViewModel.ReleaseNotesLabel, texts);
        Assert.Contains(AboutTabViewModel.CheckForUpdatesLabel, texts);
        Assert.Contains(AboutTabViewModel.LogFolderLabel, texts);

        // And the three values behind them.
        Assert.Equal("1.4.2.0", view.GetControl<SelectableTextBlock>("VersionValue").Text);
        Assert.Equal("0b1d3f5a9c", view.GetControl<SelectableTextBlock>("GitShaValue").Text);
        Assert.Equal("alpha", view.GetControl<SelectableTextBlock>("ChannelValue").Text);
        Assert.Equal("v1.4.2", view.GetControl<SelectableTextBlock>("ReleaseNotesText").Text);
    }

    [AvaloniaFact]
    public void View_ShowsTheButtonsOwnFeedbackLine()
    {
        using var harness = Factory.Create(withUpdateService: false);
        var (_, view) = Show(harness.ViewModel);

        Assert.Equal(
            AboutTabViewModel.NotInstalledMessage,
            view.GetControl<SelectableTextBlock>("UpdateStatusText").Text);
        Assert.False(view.GetControl<Button>("CheckForUpdatesButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void View_MakesEveryValueSelectable()
    {
        using var harness = Factory.Create();
        var (_, view) = Show(harness.ViewModel);

        // A support request copies the SHA out of this tab, so every value is a
        // SelectableTextBlock rather than a plain one.
        foreach (var name in new[] { "VersionValue", "GitShaValue", "ChannelValue", "ReleaseNotesText" })
        {
            Assert.NotNull(view.GetControl<SelectableTextBlock>(name));
        }
    }

    [AvaloniaFact]
    public void View_DeclaresXDataType()
    {
        var markup = File.ReadAllText(Path.Combine(
            TestSupport.SourceScan.SrcRoot(),
            "GalactiLog.App", "Views", "Settings", "AboutTabView.axaml"));

        Assert.Contains(
            "x:DataType=\"settings:AboutTabViewModel\"", markup, StringComparison.Ordinal);
    }
}
