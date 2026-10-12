using GalactiLog.App.Services.Telemetry;
using GalactiLog.App.ViewModels.Telemetry;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Telemetry;

// The one-time telemetry question, its shared disclosure part, and the startup gate. No window.
public class TelemetryPromptViewModelTests
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

    private static TelemetryDisclosureViewModel Disclosure(Func<string>? build = null, List<string>? opened = null)
        => new(build ?? (() => "{}"), url => opened?.Add(url));

    private static void AssertLowercaseGuid(string? id)
    {
        Assert.True(Guid.TryParse(id, out _), $"'{id}' is not a GUID.");
        Assert.Equal(id!.ToLowerInvariant(), id);
    }

    [Fact]
    public void TheCheckbox_StartsChecked_AndNothingIsWrittenBeforeAnAnswer()
    {
        var store = new Store();
        var page = new TelemetryPromptViewModel(store.Mutate, Disclosure());

        Assert.True(page.SendStatistics);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Dismiss_ByXOrEscape_WritesNo_AndPrompted()
    {
        var store = new Store();
        var page = new TelemetryPromptViewModel(store.Mutate, Disclosure());

        await page.Dismiss();

        Assert.False(store.Current.TelemetryEnabled);
        Assert.True(store.Current.TelemetryPrompted);
        Assert.Null(store.Current.TelemetryId);
    }

    [Fact]
    public async Task Continue_WithTheBoxChecked_WritesYes_WithAGuidId()
    {
        var store = new Store();
        var page = new TelemetryPromptViewModel(store.Mutate, Disclosure());
        var closed = (bool?)null;
        page.CloseRequested += (_, result) => closed = result;

        page.ContinueCommand.Execute(null);
        await page.PendingDecision!;

        Assert.True(store.Current.TelemetryEnabled);
        Assert.True(store.Current.TelemetryPrompted);
        AssertLowercaseGuid(store.Current.TelemetryId);
        Assert.True(closed);
    }

    [Fact]
    public async Task Continue_WithTheBoxCleared_WritesNo()
    {
        var store = new Store();
        var page = new TelemetryPromptViewModel(store.Mutate, Disclosure()) { SendStatistics = false };

        page.ContinueCommand.Execute(null);
        await page.PendingDecision!;

        Assert.False(store.Current.TelemetryEnabled);
        Assert.True(store.Current.TelemetryPrompted);
        Assert.Null(store.Current.TelemetryId);
    }

    [Fact]
    public async Task TheCloseAfterContinue_DoesNotOverwriteTheAnswer()
    {
        var store = new Store();
        var page = new TelemetryPromptViewModel(store.Mutate, Disclosure());

        page.ContinueCommand.Execute(null);
        await page.Dismiss();

        Assert.True(store.Current.TelemetryEnabled);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task AFailedWrite_DoesNotThrow()
    {
        var page = new TelemetryPromptViewModel(
            _ => throw new InvalidOperationException("locked"), Disclosure());

        await page.Dismiss();
    }

    [Fact]
    public void WithDecision_KeepsTheIdWhileStayingOn_AndMintsOneWhenTurnedOn()
    {
        var on = new GeneralSettings { TelemetryEnabled = true, TelemetryId = "kept" };
        Assert.Equal("kept", TelemetryDisclosureViewModel.WithDecision(on, true).TelemetryId);

        // A stale id on a disabled document is never reused.
        var off = new GeneralSettings { TelemetryEnabled = false, TelemetryId = "stale" };
        AssertLowercaseGuid(TelemetryDisclosureViewModel.WithDecision(off, true).TelemetryId);
    }

    [Fact]
    public async Task ThePreview_LoadsOnFirstExpand_AndOnlyOnce()
    {
        var builds = 0;
        var disclosure = Disclosure(() => $"report {++builds}");

        disclosure.IsPreviewExpanded = true;
        await disclosure.PendingPreview!;
        disclosure.IsPreviewExpanded = false;
        disclosure.IsPreviewExpanded = true;

        Assert.Equal("report 1", disclosure.PreviewText);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task ThePreview_ShowsAFallback_WhenTheBuilderThrows()
    {
        var disclosure = Disclosure(() => throw new InvalidOperationException("unreadable"));

        disclosure.LoadPreviewCommand.Execute(null);
        await disclosure.PendingPreview!;

        Assert.Equal(TelemetryDisclosureViewModel.PreviewFailedText, disclosure.PreviewText);
    }

    [Fact]
    public void TheLink_OpensThePublicStatisticsPage()
    {
        var opened = new List<string>();
        var disclosure = Disclosure(opened: opened);

        disclosure.OpenPublicStatsCommand.Execute(null);

        Assert.Equal([TelemetryEndpoints.PublicStatsUrl], opened);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)] // a dev or test build shows nothing
    [InlineData(true, false, false, false)] // the wizard runs instead
    [InlineData(true, true, true, false)] // already answered
    public void TheStartupGate(bool installed, bool setupComplete, bool prompted, bool expected)
    {
        var general = new GeneralSettings { SetupComplete = setupComplete, TelemetryPrompted = prompted };

        Assert.Equal(expected, App.ShouldShowTelemetryPrompt(general, installed));
    }

    [Fact]
    public void TheDecisionKeys_AreInert()
    {
        // A decision must not trigger the derived-data rebuilds a general change otherwise does.
        var before = new GeneralSettings();
        var after = TelemetryDisclosureViewModel.WithDecision(before, true);

        Assert.True(AppHost.OnlyInertKeysMoved(before, after));
    }
}
