using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Help;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Phase 22 unit B: the Survey downloads block on the General tab (spec 12.7 amendment 8a) and its
// help topic. A sibling of GeneralTabViewTests, kept separate because this brief owns only this
// one block and not the tab's other three.
public class GeneralTabSurveyDownloadsViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public async Task View_PlacesTheSwitchAfterNotifyOnScanComplete_WithBothNotesAndTheGlyph()
    {
        using var tab = await PreferenceTabViewModelTestFactory.NewGeneralTab().SettleAsync();
        var view = new GeneralTabView { DataContext = tab };
        Show(view);

        var descendants = view.GetVisualDescendants().ToList();
        var notifyIndex = descendants.IndexOf(view.GetControl<CheckBox>("NotifyOnScanCompleteBox"));
        var surveyIndex = descendants.IndexOf(view.GetControl<CheckBox>("SurveyDownloadsBox"));
        Assert.True(notifyIndex >= 0 && surveyIndex >= 0 && surveyIndex > notifyIndex);

        Assert.True(view.GetControl<TextBlock>("SurveyDownloadsNote").IsEffectivelyVisible);
        Assert.True(view.GetControl<TextBlock>("SurveyDownloadsHostNote").IsEffectivelyVisible);
        Assert.Equal(
            GeneralTabViewModel.SurveyDownloadsDescription,
            view.GetControl<TextBlock>("SurveyDownloadsNote").Text);
        Assert.Equal(
            GeneralTabViewModel.SurveyDownloadsHostDescription,
            view.GetControl<TextBlock>("SurveyDownloadsHostNote").Text);

        var glyph = view.GetVisualDescendants().OfType<HelpButton>()
            .First(button => button.Topic == "settings.general.survey-downloads");
        Assert.NotNull(glyph);
    }

    [AvaloniaFact]
    public async Task View_TogglingTheBox_UpdatesTheViewModel()
    {
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(new GeneralSettings { SurveyDownloadsEnabled = true })
            .SettleAsync();
        var view = new GeneralTabView { DataContext = tab };
        Show(view);

        var box = view.GetControl<CheckBox>("SurveyDownloadsBox");
        Assert.True(box.IsChecked);

        box.IsChecked = false;

        Assert.False(tab.SurveyDownloadsEnabled);
    }

    [Fact]
    public void HelpTopic_CarriesItsTitleAndNamesTheHost()
    {
        var topic = HelpTopics.Get("settings.general.survey-downloads");

        Assert.Equal("Survey downloads", topic.Title);
        Assert.Contains("alasky.cds.unistra.fr", topic.Paragraph, StringComparison.Ordinal);
    }
}
