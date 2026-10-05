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

namespace GalactiLog.App.Tests.ViewModels.Mosaics;

// Phase 18 Task 4. Spec 12.17's Mosaics page against delegate stubs: no database, a synchronous
// post and a registry the page's jobs and reloads run through.
public class MosaicsPageViewModelTests
{
    private static readonly Guid TargetA = Guid.NewGuid();
    private static readonly Guid TargetB = Guid.NewGuid();

    private static DateOnly Night(int day) => new(2026, 3, day);

    private static MosaicSuggestionRow Suggestion(string name, params string[] labels)
        => new(
            Guid.NewGuid(), name, name,
            [.. labels.Select((label, index) => new SuggestionPanel(index % 2 == 0 ? TargetA : TargetB, label, "%", [Night(1)]))],
            "high", "both", null, [], "sig-" + name, DateTime.UtcNow);

    private static MosaicListRow Mosaic(string name, int panels = 1, double seconds = 60, int frames = 1, int? first = null)
        => new(Guid.NewGuid(), name, panels, seconds, frames,
            first is { } day ? Night(day) : null, first is { } last ? Night(last) : null, []);

    // A mutable general document behind MutateGeneral, the way SettingsStore behaves.
    private sealed class GeneralStore
    {
        public GeneralSettings Value { get; set; } = new();

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate) => Value = mutate(Value);
    }

    private static async Task<MosaicsPageHarness> Ready(MosaicsBackend backend, DisplaySettings? display = null, ScanStatusService? scanStatus = null)
    {
        var harness = new MosaicsPageHarness(backend, display, scanStatus);
        await harness.Page.PendingLoad;
        return harness;
    }

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

    // ---- the mosaics table -------------------------------------------------------------------------

    [Fact]
    public async Task ASortHeaderClick_SortsReversesAndPersists()
    {
        var rows = new[] { Mosaic("b", frames: 5), Mosaic("A", frames: 9), Mosaic("c", frames: 1) };
        using var harness = await Ready(new MosaicsBackend { ListMosaics = () => rows });
        var page = harness.Page;
        Assert.Equal(new[] { "A", "b", "c" }, page.Mosaics.Select(row => row.Name));

        page.SortByCommand.Execute("frames");
        Assert.Equal(new[] { "c", "b", "A" }, page.Mosaics.Select(row => row.Name));
        page.SortByCommand.Execute("frames");
        Assert.Equal(new[] { "A", "b", "c" }, page.Mosaics.Select(row => row.Name));
        await harness.Writer.Pending;

        Assert.Equal(new TableSort { Key = "frames", Ascending = false }, harness.Display.MosaicsSort);

        // A custom slug does not sort.
        page.SortByCommand.Execute("custom_owner");
        Assert.Equal("frames", page.SortKey);
    }

    [Fact]
    public async Task TheStoredSort_IsRestored()
    {
        var display = new DisplaySettings().WithSort(DisplaySettings.MosaicsTableId, new TableSort { Key = "date_range", Ascending = true });
        var rows = new[] { Mosaic("a", first: 9), Mosaic("b"), Mosaic("c", first: 2) };
        using var harness = await Ready(new MosaicsBackend { ListMosaics = () => rows }, display);

        // A mosaic with no night sorts first ascending.
        Assert.Equal(new[] { "b", "c", "a" }, harness.Page.Mosaics.Select(row => row.Name));
        Assert.Equal("2026-03-02", harness.Page.Mosaics[1].DateRangeText);
    }

    [Fact]
    public async Task TheColumnPicker_HidesAndShows_AndListsMosaicScopeCustomColumns()
    {
        var owner = new CustomColumnDefinition(Guid.NewGuid(), "Owner", "custom_owner", CustomColumnType.Text, CustomColumnScope.Mosaic, [], 0, DateTime.UtcNow, 0);
        var other = new CustomColumnDefinition(Guid.NewGuid(), "Grade", "custom_grade", CustomColumnType.Text, CustomColumnScope.Target, [], 1, DateTime.UtcNow, 0);
        using var harness = await Ready(new MosaicsBackend
        {
            ListMosaics = () => [Mosaic("M 31")],
            CustomColumns = () => [owner, other],
            WriteValue = (_, _, _) => new CustomWriteResult(CustomWriteStatus.Written, null),
        });
        var picker = harness.Page.Picker;
        Assert.Equal(new[] { "name", "panels", "integration", "frames", "date_range", "custom_owner" }, picker.Columns.Select(column => column.Key));
        Assert.False(picker.Columns[0].CanHide);
        Assert.Empty(harness.Page.ShownCustomColumns);

        picker.ToggleCommand.Execute(picker.Columns[1]);
        picker.ToggleCommand.Execute(picker.Columns[5]);
        picker.ToggleCommand.Execute(picker.Columns[0]);
        await harness.Writer.Pending;

        Assert.False(picker.Columns[1].IsVisible);
        Assert.True(picker.Columns[0].IsVisible);
        Assert.Equal(new[] { "name", "integration", "frames", "date_range", "custom_owner" }, harness.Display.ColumnsFor("mosaics"));
        Assert.Equal(owner, Assert.Single(harness.Page.ShownCustomColumns));
        Assert.Single(harness.Page.Mosaics[0].CustomCells);

        picker.ToggleCommand.Execute(picker.Columns[1]);
        Assert.True(picker.Columns[1].IsVisible);
    }

    [Fact]
    public async Task Create_RefusesADuplicateInline_AndClosesOnSuccess()
    {
        var created = new List<string>();
        using var harness = await Ready(new MosaicsBackend
        {
            Create = name =>
            {
                if (name == "M 31")
                {
                    throw new DuplicateMosaicNameException(name);
                }

                created.Add(name);
                return Guid.NewGuid();
            },
        });
        var page = harness.Page;
        page.ToggleCreateCommand.Execute(null);
        Assert.True(page.IsCreateOpen);
        Assert.Equal("Cancel", page.CreateButtonText);
        Assert.False(page.CreateCommand.CanExecute(null));

        page.NewMosaicName = " M 31 ";
        page.CreateCommand.Execute(null);
        Assert.Equal("A mosaic named \"M 31\" already exists.", page.CreateError);
        Assert.True(page.IsCreateOpen);

        page.NewMosaicName = "M 33";
        Assert.Null(page.CreateError);
        page.CreateCommand.Execute(null);
        await page.PendingLoad;

        Assert.Equal(new[] { "M 33" }, created);
        Assert.False(page.IsCreateOpen);
    }

    [Fact]
    public async Task Rename_RefusesADuplicateInline_AndSavesOtherwise()
    {
        var row = Mosaic("M 31");
        var renamed = new List<string>();
        using var harness = await Ready(new MosaicsBackend
        {
            ListMosaics = () => [row, Mosaic("M 33")],
            Rename = (_, name) =>
            {
                if (name.Equals("m 33", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DuplicateMosaicNameException(name);
                }

                renamed.Add(name);
            },
        });
        var mosaic = harness.Page.Mosaics.Single(entry => entry.Id == row.Id);

        mosaic.BeginRenameCommand.Execute(null);
        Assert.Equal("M 31", mosaic.RenameText);
        mosaic.RenameText = "m 33";
        mosaic.SaveRenameCommand.Execute(null);
        Assert.Equal("A mosaic named \"m 33\" already exists.", mosaic.RenameError);
        Assert.True(mosaic.IsRenaming);

        mosaic.RenameText = " ";
        mosaic.SaveRenameCommand.Execute(null);
        Assert.Equal("Enter a name for the mosaic.", mosaic.RenameError);

        mosaic.RenameText = "Z 1";
        mosaic.SaveRenameCommand.Execute(null);
        Assert.Equal(new[] { "Z 1" }, renamed);
        Assert.Equal("Z 1", mosaic.Name);
        Assert.False(mosaic.IsRenaming);
        Assert.Same(mosaic, harness.Page.Mosaics[1]);
    }

    [Fact]
    public async Task Delete_NeedsTwoPresses()
    {
        var row = Mosaic("M 31");
        var deleted = new List<Guid>();
        using var harness = await Ready(new MosaicsBackend { ListMosaics = () => [row], Delete = deleted.Add });
        var mosaic = Assert.Single(harness.Page.Mosaics);

        mosaic.DeleteCommand.Execute(null);
        Assert.True(mosaic.DeletePending);
        Assert.Empty(deleted);

        mosaic.DeleteCommand.Execute(null);
        Assert.Equal(new[] { row.Id }, deleted);
        Assert.Empty(harness.Page.Mosaics);
        Assert.True(harness.Page.HasNoMosaics);
    }

    [Fact]
    public async Task DeleteSelected_ArmsThenRunsOneJob_OverTheCheckedRows()
    {
        var rows = new[] { Mosaic("A"), Mosaic("B"), Mosaic("C") };
        var deleted = new List<Guid>();
        using var harness = await Ready(new MosaicsBackend { ListMosaics = () => rows, Delete = deleted.Add });
        var page = harness.Page;
        Assert.False(page.DeleteSelectedCommand.CanExecute(null));
        page.Mosaics[0].IsSelected = true;
        page.Mosaics[2].IsSelected = true;
        Assert.Equal("Delete selected (2)", page.DeleteSelectedText);

        await page.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.True(page.DeleteSelectedPending);
        Assert.Equal("Delete 2 mosaics? Their panels and nights are removed; no frame is touched.", page.DeleteSelectedConfirmText);
        Assert.Empty(deleted);

        await page.DeleteSelectedCommand.ExecuteAsync(null);
        await page.PendingLoad;

        Assert.Equal(new[] { rows[0].Id, rows[2].Id }, deleted);
        var job = Assert.Single(harness.Jobs.Recent);
        Assert.Equal("mosaic_delete", job.Kind);
        Assert.Equal("Delete mosaics", job.Title);
        Assert.Equal("Deleted 2 of 2", job.Summary);
    }

    [Fact]
    public async Task TheExpandedRow_ListsPanels_AndRemovePanelNeedsTwoPresses()
    {
        var row = Mosaic("M 31");
        var panel = Panel("Panel 1", ["M 31"]);
        var removed = new List<PanelDetail>();
        var details = 0;
        using var harness = await Ready(new MosaicsBackend
        {
            ListMosaics = () => [row],
            Detail = id => { details++; return Detail(id, removed.Count == 0 ? [panel] : []); },
            RemovePanel = removed.Add,
        });
        var mosaic = Assert.Single(harness.Page.Mosaics);

        mosaic.ToggleExpandCommand.Execute(null);
        Assert.Equal("Collapse", mosaic.ExpandText);
        var line = Assert.Single(mosaic.PanelLines);
        Assert.Equal("M 31", line.TargetsText);
        Assert.Equal("Panel 2", mosaic.AddPanel!.Label);

        line.RemoveCommand.Execute(null);
        Assert.True(line.RemovePending);
        Assert.Equal("Remove panel Panel 1? Its nights leave this mosaic; no frame is touched.", line.RemoveConfirmText);
        line.RemoveCommand.Execute(null);

        Assert.Equal(new[] { panel }, removed);
        Assert.Empty(mosaic.PanelLines);
        Assert.True(mosaic.HasNoPanels);
        Assert.Equal("Panel 1", mosaic.AddPanel.Label);
        Assert.Equal(2, details);
    }

    [Fact]
    public async Task AddPanel_SearchesFromTwoCharacters_AndAddsTheChosenTarget()
    {
        var row = Mosaic("M 31");
        var searched = new List<string>();
        var added = new List<(Guid, Guid, string)>();
        using var harness = await Ready(new MosaicsBackend
        {
            ListMosaics = () => [row],
            Detail = id => Detail(id, added.Count == 0 ? [] : [Panel("Panel 1", ["M 31"])]),
            SearchTargets = term =>
            {
                searched.Add(term);
                return
                [
                    new TargetSearchResult(TargetA, null, "M 31", null, null, 0, 1),
                    new TargetSearchResult(null, "M31 P1", "M31 P1", null, null, 4, 0.9),
                ];
            },
            AddPanelWithTarget = (mosaicId, targetId, label) =>
            {
                added.Add((mosaicId, targetId, label));
                return new PanelAddResult(Guid.NewGuid(), 3, 2);
            },
        });
        var mosaic = Assert.Single(harness.Page.Mosaics);
        mosaic.ToggleExpandCommand.Execute(null);
        var form = mosaic.AddPanel!;
        Assert.Equal("Panel 1", form.Label);
        Assert.False(form.AddCommand.CanExecute(null));

        form.SearchText = "M";
        await form.PendingSearch!;
        Assert.Empty(searched);

        form.SearchText = "M 3";
        await form.PendingSearch!;
        Assert.Equal(new[] { "M 3" }, searched);
        var result = Assert.Single(form.SearchResults);

        form.ChooseCommand.Execute(result);
        Assert.True(form.AddCommand.CanExecute(null));
        form.AddCommand.Execute(null);

        Assert.Equal(new[] { (row.Id, TargetA, "Panel 1") }, added);
        Assert.Equal("2 nights already in another panel were skipped.", form.Caption);
        Assert.Null(form.Chosen);
        Assert.Single(mosaic.PanelLines);
        Assert.Equal("Panel 2", form.Label);
    }

    [Fact]
    public async Task ARowClick_AndATargetLink_RaiseTheShellsTwoRoutes()
    {
        var row = Mosaic("M 31");
        using var harness = await Ready(new MosaicsBackend
        {
            ListMosaics = () => [row],
            ListPending = () => [Suggestion("NGC 7000", "Panel 1")],
            TargetNames = ids => ids.ToDictionary(id => id, _ => "NGC 7000"),
        });
        var opened = new List<Guid>();
        var targets = new List<Guid>();
        harness.Page.MosaicOpenRequested += (_, id) => opened.Add(id);
        harness.Page.TargetOpenRequested += (_, id) => targets.Add(id);

        harness.Page.Mosaics[0].OpenCommand.Execute(null);
        var suggestion = Assert.Single(harness.Page.VisibleSuggestions);
        var link = Assert.Single(suggestion.Targets);
        Assert.Equal("NGC 7000", link.Name);
        suggestion.OpenTargetCommand.Execute(link);

        Assert.Equal(new[] { row.Id }, opened);
        Assert.Equal(new[] { TargetA }, targets);
    }

    [Fact]
    public async Task AFailedLoad_ShowsTheSentence_AndRetryRecovers()
    {
        var fail = true;
        using var harness = await Ready(new MosaicsBackend
        {
            ListMosaics = () => fail ? throw new InvalidOperationException("locked") : [Mosaic("M 31")],
        });
        Assert.True(harness.Page.LoadFailed);
        Assert.False(harness.Page.HasNoMosaics);

        fail = false;
        await harness.Page.RetryCommand.ExecuteAsync(null);
        await harness.Page.PendingLoad;

        Assert.False(harness.Page.LoadFailed);
        Assert.Single(harness.Page.Mosaics);
    }

    private static PanelDetail Panel(string label, IReadOnlyList<string> targets)
        => new(Guid.NewGuid(), label, 0, null, null, 0, false, [TargetA], targets, 600, 2, 1, 0, 0, [], [], new Dictionary<string, double>());

    private static MosaicDetail Detail(Guid id, IReadOnlyList<PanelDetail> panels)
        => new(id, "M 31", null, 0, panels.Sum(panel => panel.IntegrationSeconds), panels.Sum(panel => panel.Frames),
            null, null, [], panels, []);
}
