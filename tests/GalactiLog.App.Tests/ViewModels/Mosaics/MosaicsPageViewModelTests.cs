using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.Core.Mosaics;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;
using static GalactiLog.App.Tests.ViewModels.Mosaics.MosaicsTestData;

namespace GalactiLog.App.Tests.ViewModels.Mosaics;

// Phase 18 Task 4. Spec 12.17's Mosaics page against delegate stubs: no database, a synchronous
// post and a registry the page's jobs and reloads run through.
public class MosaicsPageViewModelTests
{
    // ---- detection keywords ----------------------------------------------------------------------

    [Fact]
    public async Task AddingAndRemovingAKeyword_WritesTheKey_AndShowsTheApplyCaption()
    {
        var general = new GeneralStore();
        using var harness = await Ready(new MosaicsBackend { General = () => general.Value, MutateGeneral = general.Mutate });
        var page = harness.Page;
        Assert.Equal(new[] { "Panel", "P" }, page.Keywords);
        Assert.False(page.ShowApplyCaption);

        page.NewKeyword = "  Tile ";
        page.AddKeywordCommand.Execute(null);

        Assert.Equal(new[] { "Panel", "P", "Tile" }, general.Value.MosaicKeywords);
        Assert.Equal(new[] { "Panel", "P", "Tile" }, page.Keywords);
        Assert.Equal("", page.NewKeyword);
        Assert.True(page.ShowApplyCaption);

        page.RemoveKeywordCommand.Execute("P");
        Assert.Equal(new[] { "Panel", "Tile" }, general.Value.MosaicKeywords);
        Assert.Equal(new[] { "Panel", "Tile" }, page.Keywords);
    }

    [Fact]
    public async Task ARepeatedKeyword_IsRefusedInline_AndWritesNothing()
    {
        var general = new GeneralStore();
        var writes = 0;
        using var harness = await Ready(new MosaicsBackend
        {
            General = () => general.Value,
            MutateGeneral = mutate => { writes++; return general.Mutate(mutate); },
        });

        harness.Page.NewKeyword = "panel";
        Assert.True(harness.Page.AddKeywordCommand.CanExecute(null));
        harness.Page.AddKeywordCommand.Execute(null);

        Assert.Equal("\"panel\" is already a keyword.", harness.Page.KeywordError);
        Assert.Equal(0, writes);

        harness.Page.NewKeyword = " ";
        Assert.False(harness.Page.AddKeywordCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheGapAndTheTolerance_AreWritten()
    {
        var general = new GeneralStore();
        using var harness = await Ready(new MosaicsBackend { General = () => general.Value, MutateGeneral = general.Mutate });
        var page = harness.Page;
        Assert.Equal(7, page.GapChoices.Count);
        Assert.Equal("No grouping", page.SelectedGap.Label);

        page.SelectedGap = page.GapChoices.Single(choice => choice.Label == "3 months");
        page.PositionTolerance = 45;

        Assert.Equal(90, general.Value.MosaicCampaignGapDays);
        Assert.Equal(45, general.Value.MosaicPositionToleranceArcmin);
        Assert.True(page.ShowApplyCaption);
    }

    // ---- Run Detection -------------------------------------------------------------------------

    [Fact]
    public async Task RunDetection_IsDisabledWhileAScanRuns_WithTheSpecTooltip()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = await Ready(new MosaicsBackend(), scanStatus: status);
        Assert.True(harness.Page.RunDetectionCommand.CanExecute(null));
        Assert.Null(harness.Page.RunDetectionTooltip);

        coordinator.RaiseProgress(ScanTaskNames.Classify, 1, 2, "Classifying...", force: true);

        Assert.False(harness.Page.RunDetectionCommand.CanExecute(null));
        Assert.Equal("A scan is running. Detection runs when it ends.", harness.Page.RunDetectionTooltip);
    }

    [Fact]
    public async Task RunDetection_RegistersTheJob_DisablesItselfMeanwhile_AndReloadsWhenItEnds()
    {
        var release = new TaskCompletionSource<MosaicDetectionResult?>();
        var lists = 0;
        IReadOnlyList<MosaicSuggestionRow> pending = [];
        using var harness = await Ready(new MosaicsBackend
        {
            RunDetection = (report, _) =>
            {
                report(1, 4, "Relabelling frames");
                return release.Task;
            },
            ListPending = () => { lists++; return pending; },
        });
        var page = harness.Page;
        Assert.Equal(1, lists);

        var run = page.RunDetectionCommand.ExecuteAsync(null);

        var job = Assert.Single(harness.Jobs.Running);
        Assert.Equal("mosaic_detection", job.Kind);
        Assert.Equal("Mosaic detection", job.Title);
        Assert.Equal("Relabelling frames", job.Message);
        Assert.True(page.DetectionRunning);
        Assert.Equal("Detection is running.", page.RunDetectionTooltip);

        pending = [Suggestion("M 31", "Panel 1", "Panel 2")];
        release.SetResult(new MosaicDetectionResult(0, 0, 1, 0, 0));
        await run;
        await page.PendingLoad;

        var finished = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(JobResult.Succeeded, finished.Result);
        Assert.Equal("1 suggestion", finished.Summary);
        Assert.Equal(2, lists);
        Assert.Equal("M 31", Assert.Single(page.VisibleSuggestions).Name);
        Assert.False(page.DetectionRunning);
    }

    // Ruling R5: a detection job started anywhere else (a scan's pass, wrapped by
    // ScanStatusService) reloads the page too, and clears the apply caption.
    [Fact]
    public async Task ADetectionJobFromElsewhere_ReloadsThePage()
    {
        var general = new GeneralStore();
        var lists = 0;
        using var harness = await Ready(new MosaicsBackend
        {
            General = () => general.Value,
            MutateGeneral = general.Mutate,
            ListPending = () => { lists++; return []; },
        });
        harness.Page.PositionTolerance = 10;
        Assert.True(harness.Page.ShowApplyCaption);

        harness.Jobs.Begin("scan", "Library scan").Finish(JobResult.Succeeded, "");
        await harness.Page.PendingLoad;
        Assert.Equal(1, lists);

        harness.Jobs.Begin(ScanStatusService.MosaicDetectionJobKind, "Mosaic detection").Finish(JobResult.Succeeded, "0 suggestions");
        await harness.Page.PendingLoad;

        Assert.Equal(2, lists);
        Assert.False(harness.Page.ShowApplyCaption);
    }

    // ---- suggestions -----------------------------------------------------------------------------

    [Fact]
    public async Task TheFilter_IsShownOnlyPastFourSuggestions_AndNarrowsTheHeading()
    {
        var five = new[] { "M 31", "M 33", "NGC 7000", "IC 1396", "M 42" }.Select(name => Suggestion(name, "Panel 1")).ToList();
        using var four = await Ready(new MosaicsBackend { ListPending = () => five.Take(4).ToList() });
        Assert.False(four.Page.ShowFilter);
        Assert.Equal("Suggestions (4)", four.Page.SuggestionsHeading);

        using var harness = await Ready(new MosaicsBackend { ListPending = () => five });
        Assert.True(harness.Page.ShowFilter);

        harness.Page.FilterText = "m 3";
        Assert.Equal(new[] { "M 31", "M 33" }, harness.Page.VisibleSuggestions.Select(row => row.Name));
        Assert.Equal("Suggestions (2 of 5)", harness.Page.SuggestionsHeading);
        Assert.Equal("Accept all (2)", harness.Page.AcceptAllText);
    }

    [Fact]
    public async Task TheTotalsLine_FollowsTheCheckedPanels()
    {
        var row = Suggestion("M 31", "Panel 1", "Panel 2");
        using var harness = await Ready(new MosaicsBackend
        {
            ListPending = () => [row],
            SuggestionSessions = _ =>
            [
                new(TargetA, "Panel 1", "M 31 Panel 1", Night(1), "Ha", 3, 900, true),
                new(TargetB, "Panel 2", "M 31 Panel 2", Night(1), "Ha", 2, 600, true),
                new(TargetB, "Panel 2", "M 31 Panel 2", Night(9), "Ha", 4, 1200, false),
            ],
        });
        var suggestion = Assert.Single(harness.Page.VisibleSuggestions);
        Assert.Equal("2 panels, ", suggestion.TotalsText);
        Assert.Equal("5 frames", suggestion.FramesText);
        Assert.Equal(", 00h 25m", suggestion.IntegrationText);
        Assert.Equal("+1 more night", suggestion.MoreNightsText);
        Assert.Equal(2, suggestion.Sessions.Count);

        suggestion.Sessions.Single(session => session.Data.Label == "Panel 2").IsChecked = false;

        Assert.Equal("1 panel, ", suggestion.TotalsText);
        Assert.Equal("3 frames", suggestion.FramesText);
        Assert.Equal("Accept (1 of 2)", suggestion.AcceptText);

        suggestion.AllPanelsChecked = false;
        Assert.Equal("0 frames", suggestion.FramesText);
        Assert.True(suggestion.FramesAreZero);
        Assert.False(suggestion.AcceptCommand.CanExecute(null));
        Assert.Equal("Select at least one panel to accept.", suggestion.AcceptTooltip);
    }

    [Fact]
    public async Task Accept_PassesOnlyTheCheckedLabels_AndRemovesTheRow()
    {
        var row = Suggestion("M 31", "Panel 1", "Panel 2", "Panel 3");
        var accepted = new List<(Guid, IReadOnlyList<string>)>();
        var mosaicLists = 0;
        using var harness = await Ready(new MosaicsBackend
        {
            ListPending = () => [row],
            ListMosaics = () => { mosaicLists++; return []; },
            Accept = (id, labels) => { accepted.Add((id, labels)); return Guid.NewGuid(); },
        });
        var suggestion = Assert.Single(harness.Page.VisibleSuggestions);

        suggestion.SetChecked("Panel 2", false);
        suggestion.AcceptCommand.Execute(null);
        await harness.Page.PendingLoad;

        var (id, labels) = Assert.Single(accepted);
        Assert.Equal(row.Id, id);
        Assert.Equal(new[] { "Panel 1", "Panel 3" }, labels);
        Assert.Empty(harness.Page.VisibleSuggestions);
        Assert.Equal(2, mosaicLists);
    }

    [Fact]
    public async Task Accept_ATakenName_IsRefusedUnderTheRow()
    {
        var row = Suggestion("M 31", "Panel 1");
        using var harness = await Ready(new MosaicsBackend
        {
            ListPending = () => [row],
            Accept = (_, _) => throw new DuplicateMosaicNameException("M 31"),
        });
        var suggestion = Assert.Single(harness.Page.VisibleSuggestions);

        suggestion.AcceptCommand.Execute(null);

        Assert.Equal("A mosaic named \"M 31\" already exists.", suggestion.AcceptError);
        Assert.Single(harness.Page.VisibleSuggestions);
    }

    [Fact]
    public async Task Dismiss_NeedsTwoPresses_AndCancelDisarms()
    {
        var row = Suggestion("M 31", "Panel 1");
        var dismissed = new List<Guid>();
        using var harness = await Ready(new MosaicsBackend { ListPending = () => [row], Dismiss = dismissed.Add });
        var suggestion = Assert.Single(harness.Page.VisibleSuggestions);

        suggestion.DismissCommand.Execute(null);
        Assert.True(suggestion.DismissPending);
        Assert.Empty(dismissed);

        suggestion.CancelDismissCommand.Execute(null);
        Assert.False(suggestion.DismissPending);

        suggestion.DismissCommand.Execute(null);
        suggestion.DismissCommand.Execute(null);

        Assert.Equal(new[] { row.Id }, dismissed);
        Assert.Empty(harness.Page.VisibleSuggestions);
    }

    [Fact]
    public async Task AcceptAll_RunsOneJob_WithEachRowsCheckedLabels_AndCountsFailures()
    {
        var rows = new[] { Suggestion("A", "Panel 1", "Panel 2"), Suggestion("B", "Panel 1"), Suggestion("C", "Panel 1") };
        var accepted = new List<(Guid Id, IReadOnlyList<string> Labels)>();
        var failures = new List<object>();
        using var harness = await Ready(new MosaicsBackend
        {
            ListPending = () => rows,
            Accept = (id, labels) =>
            {
                if (id == rows[2].Id)
                {
                    throw new DuplicateMosaicNameException("C");
                }

                accepted.Add((id, labels));
                return Guid.NewGuid();
            },
            EmitActionFailed = (_, details) => failures.Add(details),
        });
        harness.Page.VisibleSuggestions[0].SetChecked("Panel 2", false);

        await harness.Page.AcceptAllCommand.ExecuteAsync(null);
        await harness.Page.PendingLoad;

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal("mosaic_accept", job.Kind);
        Assert.Equal("Accept suggestions", job.Title);
        Assert.Equal("Accepted 2 of 3", job.Summary);
        Assert.Equal(JobResult.Succeeded, job.Result);
        Assert.Equal(new[] { "Panel 1" }, accepted[0].Labels);
        Assert.Single(failures);
        Assert.Contains("accept", failures[0].ToString(), StringComparison.Ordinal);
        Assert.False(harness.Page.BulkRunning);
    }

    [Fact]
    public async Task DismissAll_ArmsFirst_ThenRunsOneJob_AndFailsWhenEveryItemFails()
    {
        var rows = new[] { Suggestion("A", "Panel 1"), Suggestion("B", "Panel 1") };
        using var harness = await Ready(new MosaicsBackend
        {
            ListPending = () => rows,
            Dismiss = _ => throw new InvalidOperationException("locked"),
        });
        harness.Page.VisibleSuggestions[1].IsSelected = true;
        Assert.Equal("Dismiss (1)", harness.Page.DismissAllText);

        await harness.Page.DismissAllCommand.ExecuteAsync(null);
        Assert.True(harness.Page.DismissAllPending);
        Assert.Empty(harness.Jobs.Recent);
        Assert.Equal(
            "Dismiss 1 suggestions? Each comes back only if new nights of its panels are catalogued.",
            harness.Page.DismissAllConfirmText);

        await harness.Page.DismissAllCommand.ExecuteAsync(null);
        await harness.Page.PendingLoad;

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal("mosaic_dismiss", job.Kind);
        Assert.Equal("Dismissed 0 of 1", job.Summary);
        Assert.Equal(JobResult.Failed, job.Result);
    }

    // Fix round item 1: the filter narrows only while its box is shown. Five filtered to one,
    // accept that one: four remain, the box hides, and all four show.
    [Fact]
    public async Task AHiddenFilter_NarrowsNothing()
    {
        var rows = new[] { "M 31", "M 33", "NGC 7000", "IC 1396", "M 42" }.Select(name => Suggestion(name, "Panel 1")).ToList();
        using var harness = await Ready(new MosaicsBackend { ListPending = () => rows });
        var page = harness.Page;
        page.FilterText = "7000";
        var only = Assert.Single(page.VisibleSuggestions);

        only.AcceptCommand.Execute(null);
        await page.PendingLoad;

        Assert.False(page.ShowFilter);
        Assert.Equal("7000", page.FilterText);
        Assert.Equal(4, page.VisibleSuggestions.Count);
        Assert.Equal("Suggestions (4)", page.SuggestionsHeading);
    }

    // Fix round item 5: a failed suggestions read is not "No suggestions".
    [Fact]
    public async Task AFailedSuggestionsRead_ShowsItsSentence_NotTheEmptyOne()
    {
        var fail = true;
        using var harness = await Ready(new MosaicsBackend
        {
            ListPending = () => fail ? throw new InvalidOperationException("locked") : [Suggestion("M 31", "Panel 1")],
            ListMosaics = () => [Mosaic("M 33")],
        });
        var page = harness.Page;

        Assert.True(page.SuggestionsLoadFailed);
        Assert.False(page.HasNoSuggestions);
        Assert.Single(page.Table.Mosaics);

        fail = false;
        await page.RetrySuggestionsCommand.ExecuteAsync(null);
        await page.PendingLoad;
        Assert.False(page.SuggestionsLoadFailed);
        Assert.Single(page.VisibleSuggestions);
    }

    // Fix round item 8: a refusal by a lease holder that is not a scan names no scan.
    [Fact]
    public async Task ARunDetectionRefusedWithNoScanRunning_SaysAnotherTaskHoldsIt()
    {
        using var harness = await Ready(new MosaicsBackend());

        await harness.Page.RunDetectionCommand.ExecuteAsync(null);

        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal(JobResult.Cancelled, job.Result);
        Assert.Equal("Another catalogue task is running. Detection runs when it ends.", job.Summary);
    }

    // Fix round item 6: the select's seven days are GeneralSettings' own list.
    [Fact]
    public async Task TheGapChoices_AreTheSettingsList()
    {
        using var harness = await Ready(new MosaicsBackend());

        Assert.Equal(GeneralSettings.MosaicCampaignGapChoices, harness.Page.GapChoices.Select(choice => choice.Days));
        Assert.Equal("1 year", harness.Page.GapChoices[^1].Label);
    }

    // Fix round item 9: OBJECT and Filter sort ordinal and ignoring case.
    [Fact]
    public async Task TheSessionTable_SortsObjectAndFilterIgnoringCase()
    {
        var row = Suggestion("M 31", "Panel 1");
        using var harness = await Ready(new MosaicsBackend
        {
            ListPending = () => [row],
            SuggestionSessions = _ =>
            [
                new(TargetA, "Panel 1", "b obj", Night(1), "oiii", 1, 60, true),
                new(TargetA, "Panel 1", "A obj", Night(2), "Ha", 1, 60, true),
                new(TargetA, "Panel 1", "c obj", Night(3), "SII", 1, 60, true),
            ],
        });
        var suggestion = Assert.Single(harness.Page.VisibleSuggestions);

        suggestion.SortSessionsByCommand.Execute("object");
        Assert.Equal(new[] { "A obj", "b obj", "c obj" }, suggestion.Sessions.Select(session => session.Data.ObjectName));

        suggestion.SortSessionsByCommand.Execute("filter");
        Assert.Equal(new[] { "Ha", "oiii", "SII" }, suggestion.Sessions.Select(session => session.Data.Filter));
    }
}
