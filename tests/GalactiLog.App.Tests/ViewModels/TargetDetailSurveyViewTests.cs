using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.4's Sky view button, on the same no-window no-database shape as
// TargetDetailViewModelTests.
public class TargetDetailSurveyViewTests
{
    // A target group with no target id, no RA or no Dec shows no button at all, absent rather than disabled.
    [Fact]
    public void ShowsSurveyViewButton_NoTargetId_IsFalse()
    {
        using var harness = Factory
            .Create(get: _ => Factory.PopulatedDetail(header: Factory.PopulatedHeader() with { TargetId = null }))
            .Settle();

        Assert.False(harness.ViewModel.ShowsSurveyViewButton);
    }

    [Fact]
    public void ShowsSurveyViewButton_NoRa_IsFalse()
    {
        using var harness = Factory
            .Create(get: _ => Factory.PopulatedDetail(header: Factory.PopulatedHeader() with { Ra = null }))
            .Settle();

        Assert.False(harness.ViewModel.ShowsSurveyViewButton);
    }

    [Fact]
    public void ShowsSurveyViewButton_NoDec_IsFalse()
    {
        using var harness = Factory
            .Create(get: _ => Factory.PopulatedDetail(header: Factory.PopulatedHeader() with { Dec = null }))
            .Settle();

        Assert.False(harness.ViewModel.ShowsSurveyViewButton);
    }

    [Fact]
    public void ShowsSurveyViewButton_FullCoordinates_IsTrue()
    {
        using var harness = Factory.Create().Settle();

        Assert.True(harness.ViewModel.ShowsSurveyViewButton);
    }

    // A switch flip reaches an open page with no reload, and with the delegate present a failure
    // is a button enabled while the switch is off.
    [Fact]
    public void GeneralChanged_SwitchTurnedOn_EnablesWithNoTooltipAndNoReload()
    {
        using var harness = Factory
            .Create(
                general: new GeneralSettings { SurveyDownloadsEnabled = false },
                openSurveyView: _ => Task.CompletedTask)
            .Settle();
        var before = harness.Loads;

        Assert.False(harness.ViewModel.SurveyDownloadsEnabled);
        Assert.Equal(SurveyMessages.DisabledTooltip, harness.ViewModel.SurveyViewTooltip);
        Assert.False(harness.ViewModel.OpenSurveyViewCommand.CanExecute(null));

        harness.General = harness.General with { SurveyDownloadsEnabled = true };
        harness.RaiseGeneralChanged();
        harness.Settle();

        Assert.True(harness.ViewModel.SurveyDownloadsEnabled);
        Assert.True(harness.ViewModel.OpenSurveyViewCommand.CanExecute(null));
        Assert.Null(harness.ViewModel.SurveyViewTooltip);
        Assert.Equal(before, harness.Loads);
    }

    // The command builds SurveyTarget from the header block's own values, never re-derived, and
    // awaits the delegate.
    [Fact]
    public async Task OpenSurveyViewCommand_Pressed_CallsOpenSurveyViewOnceWithTheHeadersValues()
    {
        var calls = new List<SurveyTarget>();
        using var harness = Factory
            .Create(
                general: new GeneralSettings { SurveyDownloadsEnabled = true },
                openSurveyView: target =>
                {
                    calls.Add(target);
                    return Task.CompletedTask;
                })
            .Settle();

        Assert.True(harness.ViewModel.OpenSurveyViewCommand.CanExecute(null));
        await harness.ViewModel.OpenSurveyViewCommand.ExecuteAsync(null);

        var target = Assert.Single(calls);
        var header = Factory.PopulatedHeader();
        Assert.Equal(Factory.TargetId, target.TargetId);
        Assert.Equal(header.PrimaryName, target.PrimaryName);
        Assert.Equal(header.Ra, target.Ra);
        Assert.Equal(header.Dec, target.Dec);
        Assert.Equal(header.SizeMajor, target.SizeMajor);
    }

    // Tier 1. A write on the raising thread instead of through _post shows the new value here
    // before Drain() runs.
    [Fact]
    public async Task GeneralChanged_RunsOnAPoolThread_AndSurveyDownloadsEnabledLandsThroughPost()
    {
        var posted = new List<Action>();
        var postThreads = new List<int>();
        void Drain()
        {
            while (posted.Count > 0)
            {
                var next = posted[0];
                posted.RemoveAt(0);
                next();
            }
        }

        using var harness = Factory
            .Create(
                get: _ => Factory.PopulatedDetail(sessions: []),
                general: new GeneralSettings { SurveyDownloadsEnabled = false },
                post: action =>
                {
                    postThreads.Add(Environment.CurrentManagedThreadId);
                    posted.Add(action);
                })
            .Settle();
        Drain();
        postThreads.Clear();

        Assert.False(harness.ViewModel.SurveyDownloadsEnabled);

        var raiseThread = -1;
        await Task.Run(() =>
        {
            raiseThread = Environment.CurrentManagedThreadId;
            harness.General = harness.General with { SurveyDownloadsEnabled = true };
            harness.RaiseGeneralChanged();
        });

        // Posted but not drained: a direct write on the raising thread would already read true.
        Assert.False(harness.ViewModel.SurveyDownloadsEnabled);
        Assert.Single(postThreads);
        Assert.Equal(raiseThread, postThreads[0]);

        Drain();

        Assert.True(harness.ViewModel.SurveyDownloadsEnabled);
    }
}
