using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 22 unit B: the Survey downloads switch on the General tab (spec 12.7 amendment 8a). A
// sibling of GeneralTabViewModelTests, kept separate because this brief owns only this one key
// and not the tab's other five.
public class GeneralTabSurveyDownloadsTests
{
    [Fact]
    public async Task Load_SeedsTheSwitchOn()
    {
        // Phase 24 R4: a profile that never wrote the key reads on. A failure looks like the
        // Phase 22 default, the box clear on a fresh profile.
        using var tab = await PreferenceTabViewModelTestFactory.NewGeneralTab().SettleAsync();

        Assert.True(tab.SurveyDownloadsEnabled);
    }

    [Fact]
    public async Task TurningItOn_WritesTrueOnce()
    {
        var written = new List<GeneralSettings>();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(new GeneralSettings { SurveyDownloadsEnabled = false }, written: written)
            .SettleAsync();

        tab.SurveyDownloadsEnabled = true;
        await tab.PendingWrite;

        var document = Assert.Single(written);
        Assert.True(document.SurveyDownloadsEnabled);
    }

    [Fact]
    public async Task TurningItOff_WritesFalseOnce()
    {
        var written = new List<GeneralSettings>();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(new GeneralSettings { SurveyDownloadsEnabled = true }, written: written)
            .SettleAsync();

        tab.SurveyDownloadsEnabled = false;
        await tab.PendingWrite;

        var document = Assert.Single(written);
        Assert.False(document.SurveyDownloadsEnabled);
    }

    [Fact]
    public async Task ARefusedSave_RollsTheSwitchBack_AndShowsTheReason()
    {
        var current = new GeneralSettings { SurveyDownloadsEnabled = false };
        using var tab = new GeneralTabViewModel(
            () => current,
            _ => throw new SettingsValidationException("survey_downloads_enabled was refused"),
            post: action => action());
        await tab.SettleAsync();

        tab.SurveyDownloadsEnabled = true;
        await tab.PendingWrite;

        Assert.False(tab.SurveyDownloadsEnabled);
        Assert.Equal("survey_downloads_enabled was refused", tab.ErrorMessage);
        Assert.Null(tab.StatusMessage);
    }

    [Fact]
    public void TheTwoDescriptions_MatchTheSpecSentencesByteForByte()
    {
        Assert.Equal(
            "Sky view on a target's page shows survey images of the sky around that target, "
            + "fetched from the internet at alasky.cds.unistra.fr.",
            GeneralTabViewModel.SurveyDownloadsDescription);
        Assert.Equal(
            "While this is off, GalactiLog makes no request to that host, and nothing else in "
            + "the application changes.",
            GeneralTabViewModel.SurveyDownloadsHostDescription);
    }
}
