using System.Text;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Mosaics;

// Phase 18 Task 5, spec 12.17's mosaic detail page. Every collaborator is a delegate over
// FakeMosaic, an in-memory mosaic that applies the repository's rules (the one-triple refusal,
// Delete panel's refusal, Available hiding a triple any panel includes), so each test drives the
// page the way the reader would and asserts what the page then shows.
public sealed class MosaicDetailViewModelTests : IDisposable
{
    private static readonly Guid M31 = Guid.NewGuid();
    private static readonly Guid M32 = Guid.NewGuid();
    private static readonly Guid Ngc7000 = Guid.NewGuid();

    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-mosaic-detail-").FullName;
    private readonly List<MosaicDetailViewModel> _pages = [];

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DateOnly Night(int day) => new(2026, 3, day);

    // Two panels of M 31 on the same night, kept apart by frame label (ruling R19a), plus nights
    // of M 32 and NGC 7000 that no panel holds yet.
    private static FakeMosaic TwoPanels()
    {
        var mosaic = new FakeMosaic();
        mosaic.Catalogue.AddRange(
        [
            new(M31, "M 31", Night(1), "Panel 1", 4, 1200, "Ha"),
            new(M31, "M 31", Night(2), "Panel 1", 6, 1800, "OIII"),
            new(M31, "M 31", Night(1), "Panel 2", 2, 600, "Ha"),
            new(M32, "M 32", Night(4), null, 3, 900, "Ha"),
            new(Ngc7000, "NGC 7000", Night(5), null, 5, 1500, "SII"),
        ]);
        var one = mosaic.AddPanel("Panel 1");
        var two = mosaic.AddPanel("Panel 2");
        mosaic.Row(one, M31, Night(1), "Panel 1", included: true);
        mosaic.Row(one, M31, Night(2), "Panel 1", included: false);
        mosaic.Row(two, M31, Night(1), "Panel 2", included: true);
        return mosaic;
    }

    private async Task<MosaicDetailViewModel> Open(
        FakeMosaic mosaic, JobRegistry? jobs = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        MosaicsBackend? backend = null)
    {
        var page = new MosaicDetailViewModel(
            mosaic.Id, backend ?? mosaic.Backend(), new AppWriter(_root), jobs,
            post: action => action(), delay: delay ?? ((_, _) => Task.CompletedTask));
        _pages.Add(page);
        await page.PendingLoad;
        return page;
    }

    [Fact]
    public async Task Opening_ShowsTheNameTheSummaryLineAndOnePanelRowPerPanel()
    {
        var page = await Open(TwoPanels());

        Assert.Equal("M 31 mosaic", page.Name);
        Assert.Equal($"2 panels, {MetricText.Integration(1800)} total, 6 frames", page.SummaryText);
        Assert.Equal(new[] { "Panel 1", "Panel 2" }, page.Panels.Select(panel => panel.Label));
        Assert.False(page.LoadFailed);
        Assert.False(page.CompositeCommand.CanExecute(null));
    }

    // Phase 19A Task 3: the page feeds its arranger from every read, a layout save reaches the
    // repository without re-reading the page, and closing the page flushes a pending save.
    [Fact]
    public async Task TheArranger_IsFedFromTheDetail_AndItsSaveDoesNotReReadThePage()
    {
        var mosaic = TwoPanels();
        mosaic.RotationAngle = 30;
        var reads = 0;
        var backend = mosaic.Backend() with { Detail = _ => { reads++; return mosaic.Read(); } };
        var page = await Open(mosaic, backend: backend);

        Assert.Equal(new[] { "Panel 1", "Panel 2" }, page.Arranger.Tiles.Select(tile => tile.Label));
        Assert.Equal(30, page.Arranger.GlobalRotation);
        Assert.False(page.Arranger.IsReadOnly);

        page.Arranger.Flip(page.Arranger.Tiles[1]);
        await page.Arranger.PendingSave;

        Assert.Equal(1, mosaic.LayoutWrites);
        Assert.True(mosaic.Layouts[page.Arranger.Tiles[1].PanelId].FlipH);
        Assert.Equal((254d, 0d), (mosaic.Layouts[page.Arranger.Tiles[1].PanelId].X, mosaic.Layouts[page.Arranger.Tiles[1].PanelId].Y));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ClosingThePage_RunsAPendingLayoutSave()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic, delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));

        page.Arranger.Rotate(page.Arranger.Tiles[0]);
        Assert.Equal(0, mosaic.LayoutWrites);

        page.Dispose();

        Assert.Equal(1, mosaic.LayoutWrites);
        Assert.Equal(90, mosaic.Layouts[page.Arranger.Tiles[0].PanelId].Rotation);
    }

    [Fact]
    public async Task AMosaicThatIsGone_ShowsTheLoadFailure()
    {
        var mosaic = TwoPanels();
        mosaic.Deleted = true;

        var page = await Open(mosaic);

        Assert.True(page.LoadFailed);
    }

    [Fact]
    public async Task Rename_RefusesATakenNameInline_AndSavesAFreeOne()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic);

        page.BeginRenameCommand.Execute(null);
        page.RenameText = "  andromeda  ";
        page.SaveRenameCommand.Execute(null);

        Assert.Equal("A mosaic named \"andromeda\" already exists.", page.RenameError);
        Assert.True(page.IsRenaming);
        Assert.Equal("M 31 mosaic", mosaic.Name);

        page.RenameText = "";
        page.SaveRenameCommand.Execute(null);
        Assert.Equal(MosaicMessages.EmptyName, page.RenameError);

        page.RenameText = "Great Andromeda";
        page.SaveRenameCommand.Execute(null);
        await page.PendingLoad;

        Assert.Null(page.RenameError);
        Assert.False(page.IsRenaming);
        Assert.Equal("Great Andromeda", page.Name);
        Assert.Equal("Great Andromeda", mosaic.Name);
    }

    [Fact]
    public async Task Notes_SaveOnceAfterTheIdleWindow_WithTheLastText()
    {
        var mosaic = TwoPanels();
        var gate = new TaskCompletionSource();
        var page = await Open(mosaic, delay: (_, token) => gate.Task.WaitAsync(token));

        page.Notes.Text = "P";
        page.Notes.Text = "Panel 2 needs Ha";
        Assert.Empty(mosaic.NotesWrites);

        gate.SetResult();
        await page.Notes.PendingSave;

        Assert.Equal(new[] { "Panel 2 needs Ha" }, mosaic.NotesWrites);
        Assert.Equal(TimeSpan.FromSeconds(1), AutosaveField.IdleWindow);
    }

    [Fact]
    public async Task ExportPanels_WithACancelledDialog_WritesNothing()
    {
        var page = await Open(TwoPanels());
        string? suggested = null;
        page.ExportDestinationPicker = name =>
        {
            suggested = name;
            return Task.FromResult<string?>(null);
        };

        await page.ExportPanelsCommand.ExecuteAsync(null);

        Assert.Equal("M_31_mosaic_panels.csv", suggested);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Null(page.Error);
    }

    [Fact]
    public async Task ExportPanels_WritesOneCsvAtTheChosenPath_MatchingTheGoldenText()
    {
        var mosaic = TwoPanels();
        mosaic.Panels[1] = (mosaic.Panels[1].Id, "Panel \"2\", east");
        var page = await Open(mosaic);
        var destination = Path.Combine(_root, "chosen.csv");
        page.ExportDestinationPicker = _ => Task.FromResult<string?>(destination);

        await page.ExportPanelsCommand.ExecuteAsync(null);

        const string Golden =
            "panel_label,targets,frames,integration_seconds,filters\n"
            + "Panel 1,M 31,4,1200,Ha: 1200\n"
            + "\"Panel \"\"2\"\", east\",M 31,2,600,Ha: 600\n";
        Assert.Equal(new[] { destination }, Directory.EnumerateFileSystemEntries(_root));
        var bytes = await File.ReadAllBytesAsync(destination);
        Assert.Equal(Encoding.UTF8.GetBytes(Golden), bytes);
        Assert.Null(page.Error);
    }

    [Fact]
    public void BuildCsv_JoinsTargetsAndFilters_RoundsSeconds_AndQuotesOnlyWhatNeedsIt()
    {
        var panel = new PanelDetail(
            Guid.NewGuid(), "Panel 1", 0, null, null, 0, false, [M31, M32], ["M 31", "M 32"], 3600.6, 12, 3, 0, 0, [], [],
            new Dictionary<string, double> { ["OIII"] = 1200.5, ["Ha"] = 2400.1 });
        var detail = new MosaicDetail(Guid.NewGuid(), "x", null, 0, 3600.6, 12, null, null, [], [panel], []);

        Assert.Equal(
            "panel_label,targets,frames,integration_seconds,filters\nPanel 1,M 31; M 32,12,3601,Ha: 2400; OIII: 1201\n",
            MosaicDetailViewModel.BuildCsv(detail));
    }

    [Fact]
    public async Task ExportPanels_AFailedWrite_SaysSoUnderTheHeader()
    {
        var page = await Open(TwoPanels());
        page.ExportDestinationPicker = _ => Task.FromResult<string?>(Path.Combine(_root, "missing", "folder", "x.csv"));

        await page.ExportPanelsCommand.ExecuteAsync(null);

        Assert.Equal(MosaicDetailViewModel.ExportFailedText, page.Error);
    }

    [Fact]
    public async Task DeleteMosaic_ArmsOnTheFirstPress_DeletesAndClosesOnTheSecond()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic);
        var closed = 0;
        page.BackRequested += (_, _) => closed++;

        page.DeleteMosaicCommand.Execute(null);
        Assert.True(page.DeletePending);
        Assert.False(mosaic.Deleted);

        page.CancelDeleteCommand.Execute(null);
        Assert.False(page.DeletePending);

        page.DeleteMosaicCommand.Execute(null);
        page.DeleteMosaicCommand.Execute(null);

        Assert.True(mosaic.Deleted);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task IncludeAndRemove_RefreshTheSummaryAndThePanelFigures()
    {
        var page = await Open(TwoPanels());
        var panel = page.Panels[0];
        Assert.Equal("1 available", panel.AvailableText);

        panel.Available.Single(night => night.Night.Date == Night(2)).IncludeCommand.Execute(null);
        await page.PendingLoad;

        Assert.Equal($"2 panels, {MetricText.Integration(3600)} total, 12 frames", page.SummaryText);
        Assert.Equal(MetricText.Integration(3000), page.Panels[0].IntegrationText);
        Assert.Equal("2", page.Panels[0].NightsText);
        Assert.False(page.Panels[0].HasAvailable);

        page.Panels[0].Included.Single(night => night.Night.Date == Night(1)).RemoveCommand.Execute(null);
        await page.PendingLoad;

        Assert.Equal(MetricText.Integration(1800), page.Panels[0].IntegrationText);
        Assert.Equal("1 available", page.Panels[0].AvailableText);
        Assert.Same(panel, page.Panels[0]);
    }

    [Fact]
    public async Task IncludeAll_AndIncludeAllAvailable_IncludeEveryAvailableTriple()
    {
        var mosaic = TwoPanels();
        mosaic.Row(mosaic.Panels[1].Id, M32, Night(4), null, included: false);
        var page = await Open(mosaic);
        Assert.True(page.IncludeAllAvailableCommand.CanExecute(null));

        page.Panels[0].IncludeAllCommand.Execute(null);
        await page.PendingLoad;
        Assert.False(page.Panels[0].IncludeAllCommand.CanExecute(null));
        Assert.True(page.Panels[1].HasAvailable);

        page.IncludeAllAvailableCommand.Execute(null);
        await page.PendingLoad;

        Assert.All(page.Panels, panel => Assert.False(panel.HasAvailable));
        Assert.False(page.IncludeAllAvailableCommand.CanExecute(null));
    }

    [Fact]
    public async Task AsNewPanel_PrefillsTheNextSuffix_AndCreatesThePanelHoldingTheTriple()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic);
        var row = page.Panels[0].Available.Single();

        row.BeginNewPanelCommand.Execute(null);
        Assert.True(row.IsNamingNewPanel);
        Assert.Equal("Panel 1 (b)", row.NewPanelLabel);

        row.CreateNewPanelCommand.Execute(null);
        await page.PendingLoad;

        Assert.Equal(new[] { "Panel 1", "Panel 2", "Panel 1 (b)" }, page.Panels.Select(panel => panel.Label));
        var created = Assert.Single(page.Panels[2].Included);
        Assert.Equal("Panel 1", created.LabelText);
        Assert.Equal(Night(2), created.Night.Date);
    }

    [Fact]
    public async Task AsNewPanel_RefusesABlankOrTakenLabelInline()
    {
        var page = await Open(TwoPanels());
        var row = page.Panels[0].Available.Single();
        row.BeginNewPanelCommand.Execute(null);

        row.NewPanelLabel = " ";
        row.CreateNewPanelCommand.Execute(null);
        Assert.Equal(MosaicMessages.EmptyLabel, row.Error);

        row.NewPanelLabel = "panel 2";
        row.CreateNewPanelCommand.Execute(null);
        Assert.Equal("A panel named \"panel 2\" already exists in this mosaic.", row.Error);
        Assert.True(row.IsNamingNewPanel);
    }

    [Fact]
    public async Task DeletePanel_IsDisabledWhileANightIsIncluded_AndTwoPressOtherwise()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic);
        var panel = page.Panels[1];

        Assert.False(panel.DeletePanelCommand.CanExecute(null));
        Assert.Equal(PanelViewModel.DeleteDisabledTooltip, panel.DeleteTooltip);

        panel.Included.Single().RemoveCommand.Execute(null);
        await page.PendingLoad;
        Assert.True(panel.DeletePanelCommand.CanExecute(null));
        Assert.Null(panel.DeleteTooltip);

        panel.DeletePanelCommand.Execute(null);
        Assert.True(panel.DeletePending);
        Assert.Equal("Delete panel Panel 2?", panel.DeleteConfirmText);
        Assert.Equal(2, mosaic.Panels.Count);

        panel.DeletePanelCommand.Execute(null);
        await page.PendingLoad;

        Assert.Equal(new[] { "Panel 1" }, page.Panels.Select(row => row.Label));
    }

    [Fact]
    public async Task AddNightsFromAnyTarget_ListsThatTargetsTriplesInAvailable()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic);
        var form = page.Panels[0].AddNights;

        form.SearchText = "NGC";
        await form.PendingSearch!;
        form.ChooseCommand.Execute(Assert.Single(form.SearchResults));
        await page.PendingLoad;

        var added = Assert.Single(page.Panels[0].Available, night => night.Night.TargetId == Ngc7000);
        Assert.Equal("NGC 7000", added.TargetName);
        Assert.Equal(PanelSessionViewModel.NoLabelText, added.LabelText);
        Assert.Equal("SII 5", added.FiltersText);
        Assert.Equal("", form.SearchText);
    }

    [Fact]
    public async Task ATripleIncludedInASiblingPanel_IsAbsentFromAvailable_AndARefusalShowsInline()
    {
        // Panel 2's target is M 31 too, so it offers Panel 1's triple until Panel 1 includes it.
        var page = await Open(TwoPanels());
        var stale = page.Panels[1].Available.Single(night => night.Night.Date == Night(2));

        page.Panels[0].Available.Single().IncludeCommand.Execute(null);
        await page.PendingLoad;

        Assert.DoesNotContain(page.Panels[1].Available, night => night.Night.Date == Night(2));

        // A stale row still on screen is refused by the repository with the triple sentence.
        stale.IncludeCommand.Execute(null);
        Assert.Equal(MosaicMessages.TripleInPanel(Night(2), "M 31", "Panel 1", "Panel 1"), stale.Error);
    }

    [Fact]
    public async Task TheDeficit_ReadsBehindTheLeadingPanel_AndIsEmptyOnTheLeader()
    {
        var page = await Open(TwoPanels());

        Assert.Equal("", page.Panels[0].DeficitText);
        Assert.Equal(MetricText.Integration(600) + " behind", page.Panels[1].DeficitText);
    }

    [Fact]
    public async Task TheDeficit_IsEmptyEverywhereWhileEveryPanelIsZero()
    {
        var mosaic = new FakeMosaic();
        mosaic.AddPanel("Panel 1");
        mosaic.AddPanel("Panel 2");

        var page = await Open(mosaic);

        Assert.All(page.Panels, panel => Assert.Equal("", panel.DeficitText));
    }

    [Fact]
    public async Task TheLabelsBanner_AddsAPanelForTheChipsTargetAndLabel()
    {
        var mosaic = TwoPanels();
        mosaic.Catalogue.Add(new(M31, "M 31", Night(6), "Panel 3", 2, 400, "Ha"));
        mosaic.AvailableLabels.Add(new AvailableLabel(M31, "M 31", "Panel 3"));
        var page = await Open(mosaic);
        Assert.True(page.HasAvailableLabels);
        var chip = Assert.Single(page.AvailableLabels);
        Assert.Equal("Panel 3 on M 31", chip.Text);

        chip.AddPanelCommand.Execute(null);
        await page.PendingLoad;

        Assert.Equal("Panel 3", page.Panels[2].Label);
        Assert.Equal("Panel 3", Assert.Single(page.Panels[2].Included).LabelText);
        Assert.False(page.HasAvailableLabels);
    }

    [Fact]
    public async Task AFinishedScanOrDetectionJob_RereadsThePage()
    {
        var mosaic = TwoPanels();
        var jobs = new JobRegistry(action => action());
        var page = await Open(mosaic, jobs);
        mosaic.Name = "Renamed elsewhere";

        jobs.Begin("survey_image_fetch", "Unrelated").Finish(JobResult.Succeeded, "");
        await page.PendingLoad;
        Assert.Equal("M 31 mosaic", page.Name);

        jobs.Begin(ScanStatusService.ScanJobKind, "Scan").Finish(JobResult.Succeeded, "");
        await page.PendingLoad;
        Assert.Equal("Renamed elsewhere", page.Name);

        mosaic.Name = "Again";
        jobs.Begin(ScanStatusService.MosaicDetectionJobKind, "Detection").Finish(JobResult.Succeeded, "");
        await page.PendingLoad;
        Assert.Equal("Again", page.Name);
    }

    [Fact]
    public async Task ATargetLink_AsksToOpenThatTarget()
    {
        var page = await Open(TwoPanels());
        Guid? opened = null;
        page.OpenTargetRequested += (_, id) => opened = id;

        page.Panels[0].Included.Single().OpenTargetCommand.Execute(null);

        Assert.Equal(M31, opened);
    }

    // Fix round 1. The overflow entry only arms the strip: choosing it twice deletes nothing.
    [Fact]
    public async Task TheDeleteMosaicMenuEntry_OnlyArmsTheStrip()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic);

        page.ArmDeleteMosaicCommand.Execute(null);
        page.ArmDeleteMosaicCommand.Execute(null);

        Assert.True(page.DeletePending);
        Assert.False(mosaic.Deleted);
    }

    // Fix round 1. Closing after a confirmed delete flushes no note into the deleted mosaic.
    [Fact]
    public async Task ClosingAfterADelete_FlushesNoNote()
    {
        var mosaic = TwoPanels();
        var gate = new TaskCompletionSource();
        var page = await Open(mosaic, delay: (_, token) => gate.Task.WaitAsync(token));
        page.Notes.Text = "typed, never saved";

        page.DeleteMosaicCommand.Execute(null);
        page.DeleteMosaicCommand.Execute(null);
        page.Dispose();

        Assert.True(mosaic.Deleted);
        Assert.Empty(mosaic.NotesWrites);
    }

    // Fix round 1. An open As new panel row keeps its label and its inline refusal across a write
    // elsewhere on the page and across a detection job's re-read, and the panel containers are
    // updated rather than removed and re-added.
    [Fact]
    public async Task AnOpenAsNewPanelRow_SurvivesAWriteElsewhereAndADetectionReread()
    {
        var mosaic = TwoPanels();
        var jobs = new JobRegistry(action => action());
        var page = await Open(mosaic, jobs);
        var panel = page.Panels[0];
        var row = panel.Available.Single();
        row.BeginNewPanelCommand.Execute(null);
        row.NewPanelLabel = " ";
        row.CreateNewPanelCommand.Execute(null);
        row.NewPanelLabel = "Panel 1 east";
        var removals = 0;
        page.Panels.CollectionChanged += (_, e) =>
        {
            if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                or System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                removals++;
            }
        };

        page.Panels[1].Included.Single().RemoveCommand.Execute(null);
        await page.PendingLoad;
        jobs.Begin(ScanStatusService.MosaicDetectionJobKind, "Detection").Finish(JobResult.Succeeded, "");
        await page.PendingLoad;

        Assert.Same(panel, page.Panels[0]);
        Assert.Same(row, page.Panels[0].Available.Single(night => night.Night.Date == Night(2)));
        Assert.True(row.IsNamingNewPanel);
        Assert.Equal("Panel 1 east", row.NewPanelLabel);
        Assert.Equal(MosaicMessages.EmptyLabel, row.Error);
        Assert.Equal(0, removals);
    }

    // Fix round 1. A label being typed in the add panel form survives a re-read; an untouched
    // box still follows the prefill.
    [Fact]
    public async Task TheAddPanelLabelBeingTyped_SurvivesAReread()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic);
        Assert.Equal("Panel 3", page.AddPanel.Label);

        mosaic.AddPanel("Panel 3");
        await page.ReloadAsync();
        Assert.Equal("Panel 4", page.AddPanel.Label);

        page.AddPanel.Label = "East strip";
        page.Panels[1].Included.Single().RemoveCommand.Execute(null);
        await page.PendingLoad;

        Assert.Equal("East strip", page.AddPanel.Label);
    }

    // Fix round 1. A panel row's refusal clears on the next successful re-read.
    [Fact]
    public async Task APanelRowsError_ClearsOnTheNextReread()
    {
        var mosaic = TwoPanels();
        var page = await Open(mosaic, backend: mosaic.Backend() with { IncludeAll = _ => throw new InvalidOperationException("locked") });
        var panel = page.Panels[0];

        panel.IncludeAllCommand.Execute(null);
        Assert.Equal(MosaicMessages.CouldNotSave, panel.Error);

        page.Panels[1].Included.Single().RemoveCommand.Execute(null);
        await page.PendingLoad;

        Assert.Null(panel.Error);
    }

    // Fix round 1. Spec 12.17's header cells: every mosaic-scope column in display order with its
    // stored value, and an edit writes under this mosaic's key.
    [Fact]
    public async Task TheCustomCells_AreTheMosaicColumnsInDisplayOrder_AndWriteUnderTheMosaicKey()
    {
        var mosaic = TwoPanels();
        var owner = new CustomColumnDefinition(Guid.NewGuid(), "Owner", "custom_owner", CustomColumnType.Text, CustomColumnScope.Mosaic, [], 1, DateTime.UtcNow, 0);
        var site = new CustomColumnDefinition(Guid.NewGuid(), "Site", "custom_site", CustomColumnType.Text, CustomColumnScope.Mosaic, [], 0, DateTime.UtcNow, 0);
        var grade = new CustomColumnDefinition(Guid.NewGuid(), "Grade", "custom_grade", CustomColumnType.Text, CustomColumnScope.Target, [], 2, DateTime.UtcNow, 0);
        var writes = new List<(Guid Column, CustomValueKey Key, string? Value)>();
        IReadOnlyCollection<Guid>? asked = null;
        var page = await Open(mosaic, backend: mosaic.Backend() with
        {
            CustomColumns = () => [owner, grade, site],
            MosaicValues = ids =>
            {
                asked = ids;
                return
                [
                    new CustomValueRow(owner.Id, CustomValueKey.ForMosaic(mosaic.Id), "Kumar"),
                    new CustomValueRow(site.Id, CustomValueKey.ForMosaic(mosaic.Id), "Backyard"),
                ];
            },
            WriteValue = (column, key, value) =>
            {
                writes.Add((column, key, value));
                return new CustomWriteResult(CustomWriteStatus.Written, null);
            },
        });

        Assert.Equal(new[] { mosaic.Id }, asked);
        Assert.Equal(new[] { "Site", "Owner" }, page.CustomCells.Select(cell => cell.Label));
        Assert.Equal(new[] { "Backyard", "Kumar" }, page.CustomCells.Select(cell => cell.Text!.Text));

        page.CustomCells[1].Text!.Text = "Someone else";
        await page.CustomCells[1].Text!.PendingSave;

        Assert.Equal(new[] { (owner.Id, CustomValueKey.ForMosaic(mosaic.Id), (string?)"Someone else") }, writes);
    }

    [Theory]
    [InlineData("M 31", "M_31_panels.csv")]
    [InlineData("NGC 7000 (2026)", "NGC_7000__2026__panels.csv")]
    [InlineData("Ünïcode", "_n_code_panels.csv")]
    public void ExportFileName_ReplacesEveryCharacterOutsideAsciiLettersAndDigits(string name, string expected)
        => Assert.Equal(expected, MosaicDetailViewModel.ExportFileName(name));
}
