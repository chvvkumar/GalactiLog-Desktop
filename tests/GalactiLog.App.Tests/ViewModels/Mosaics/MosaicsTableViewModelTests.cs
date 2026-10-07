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

// Phase 18 Task 4. Spec 12.17's mosaics table (MosaicsTableViewModel, owned by the page) against
// delegate stubs: rows, sort, column gear, create, rename, delete, the expanded row and the add
// panel form.
public class MosaicsTableViewModelTests
{
    // ---- the mosaics table -------------------------------------------------------------------------

    [Fact]
    public async Task ASortHeaderClick_SortsReversesAndPersists()
    {
        var rows = new[] { Mosaic("b", frames: 5), Mosaic("A", frames: 9), Mosaic("c", frames: 1) };
        using var harness = await Ready(new MosaicsBackend { ListMosaics = () => rows });
        var page = harness.Page;
        Assert.Equal(new[] { "A", "b", "c" }, page.Table.Mosaics.Select(row => row.Name));

        page.Table.SortByCommand.Execute("frames");
        Assert.Equal(new[] { "c", "b", "A" }, page.Table.Mosaics.Select(row => row.Name));
        page.Table.SortByCommand.Execute("frames");
        Assert.Equal(new[] { "A", "b", "c" }, page.Table.Mosaics.Select(row => row.Name));
        await harness.Writer.Pending;

        Assert.Equal(new TableSort { Key = "frames", Ascending = false }, harness.Display.MosaicsSort);

        // A custom slug does not sort.
        page.Table.SortByCommand.Execute("custom_owner");
        Assert.Equal("frames", page.Table.SortKey);
    }

    [Fact]
    public async Task TheStoredSort_IsRestored()
    {
        var display = new DisplaySettings().WithSort(DisplaySettings.MosaicsTableId, new TableSort { Key = "date_range", Ascending = true });
        var rows = new[] { Mosaic("a", first: 9), Mosaic("b"), Mosaic("c", first: 2) };
        using var harness = await Ready(new MosaicsBackend { ListMosaics = () => rows }, display);

        // A mosaic with no night sorts first ascending.
        Assert.Equal(new[] { "b", "c", "a" }, harness.Page.Table.Mosaics.Select(row => row.Name));
        Assert.Equal("2026-03-02", harness.Page.Table.Mosaics[1].DateRangeText);
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
        var picker = harness.Page.Table.Picker;
        Assert.Equal(new[] { "name", "panels", "integration", "frames", "date_range", "custom_owner" }, picker.Columns.Select(column => column.Key));
        Assert.False(picker.Columns[0].CanHide);
        Assert.Empty(harness.Page.Table.ShownCustomColumns);

        picker.ToggleCommand.Execute(picker.Columns[1]);
        picker.ToggleCommand.Execute(picker.Columns[5]);
        picker.ToggleCommand.Execute(picker.Columns[0]);
        await harness.Writer.Pending;

        Assert.False(picker.Columns[1].IsVisible);
        Assert.True(picker.Columns[0].IsVisible);
        Assert.Equal(new[] { "name", "integration", "frames", "date_range", "custom_owner" }, harness.Display.ColumnsFor("mosaics"));
        Assert.Equal(owner, Assert.Single(harness.Page.Table.ShownCustomColumns));
        Assert.Single(harness.Page.Table.Mosaics[0].CustomCells);

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
        page.Table.ToggleCreateCommand.Execute(null);
        Assert.True(page.Table.IsCreateOpen);
        Assert.Equal("Cancel", page.Table.CreateButtonText);
        Assert.False(page.Table.CreateCommand.CanExecute(null));

        page.Table.NewMosaicName = " M 31 ";
        page.Table.CreateCommand.Execute(null);
        Assert.Equal("A mosaic named \"M 31\" already exists.", page.Table.CreateError);
        Assert.True(page.Table.IsCreateOpen);

        page.Table.NewMosaicName = "M 33";
        Assert.Null(page.Table.CreateError);
        page.Table.CreateCommand.Execute(null);
        await page.PendingLoad;

        Assert.Equal(new[] { "M 33" }, created);
        Assert.False(page.Table.IsCreateOpen);
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
        var mosaic = harness.Page.Table.Mosaics.Single(entry => entry.Id == row.Id);

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
        Assert.Same(mosaic, harness.Page.Table.Mosaics[1]);
    }

    [Fact]
    public async Task Delete_NeedsTwoPresses()
    {
        var row = Mosaic("M 31");
        var deleted = new List<Guid>();
        using var harness = await Ready(new MosaicsBackend { ListMosaics = () => [row], Delete = deleted.Add });
        var mosaic = Assert.Single(harness.Page.Table.Mosaics);

        mosaic.DeleteCommand.Execute(null);
        Assert.True(mosaic.DeletePending);
        Assert.Empty(deleted);

        mosaic.DeleteCommand.Execute(null);
        Assert.Equal(new[] { row.Id }, deleted);
        Assert.Empty(harness.Page.Table.Mosaics);
        Assert.True(harness.Page.Table.HasNoMosaics);
    }

    [Fact]
    public async Task DeleteSelected_ArmsThenRunsOneJob_OverTheCheckedRows()
    {
        var rows = new[] { Mosaic("A"), Mosaic("B"), Mosaic("C") };
        var deleted = new List<Guid>();
        using var harness = await Ready(new MosaicsBackend { ListMosaics = () => rows, Delete = deleted.Add });
        var page = harness.Page;
        Assert.False(page.Table.DeleteSelectedCommand.CanExecute(null));
        page.Table.Mosaics[0].IsSelected = true;
        page.Table.Mosaics[2].IsSelected = true;
        Assert.Equal("Delete selected (2)", page.Table.DeleteSelectedText);

        await page.Table.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.True(page.Table.DeleteSelectedPending);
        Assert.Equal("Delete 2 mosaics? Their panels and nights are removed; no frame is touched.", page.Table.DeleteSelectedConfirmText);
        page.Table.Mosaics[2].IsSelected = false;
        Assert.Equal("Delete 1 mosaic? Its panels and nights are removed; no frame is touched.", page.Table.DeleteSelectedConfirmText);
        page.Table.Mosaics[2].IsSelected = true;
        Assert.Empty(deleted);

        await page.Table.DeleteSelectedCommand.ExecuteAsync(null);
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
        var removed = new List<Guid>();
        var details = 0;
        using var harness = await Ready(new MosaicsBackend
        {
            ListMosaics = () => [row],
            Detail = id => { details++; return Detail(id, removed.Count == 0 ? [panel] : []); },
            RemovePanel = removed.Add,
        });
        var mosaic = Assert.Single(harness.Page.Table.Mosaics);

        mosaic.ToggleExpandCommand.Execute(null);
        Assert.Equal("Collapse", mosaic.ExpandText);
        var line = Assert.Single(mosaic.PanelLines);
        Assert.Equal("M 31", line.TargetsText);
        Assert.Equal("Panel 2", mosaic.AddPanel!.Label);

        line.RemoveCommand.Execute(null);
        Assert.True(line.RemovePending);
        Assert.Equal("Remove panel Panel 1? Its nights leave this mosaic; no frame is touched.", line.RemoveConfirmText);
        line.RemoveCommand.Execute(null);

        Assert.Equal(new[] { panel.Id }, removed);
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
        var mosaic = Assert.Single(harness.Page.Table.Mosaics);
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

        // Typing again after a pick drops it; picking again restores it.
        form.SearchText = "M 31";
        Assert.Null(form.Chosen);
        Assert.False(form.AddCommand.CanExecute(null));
        await form.PendingSearch!;
        form.ChooseCommand.Execute(Assert.Single(form.SearchResults));
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

        harness.Page.Table.Mosaics[0].OpenCommand.Execute(null);
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
        Assert.True(harness.Page.Table.LoadFailed);
        Assert.False(harness.Page.Table.HasNoMosaics);
        Assert.False(harness.Page.Table.ShowRows);

        fail = false;
        await harness.Page.Table.RetryCommand.ExecuteAsync(null);
        await harness.Page.PendingLoad;

        Assert.False(harness.Page.Table.LoadFailed);
        Assert.Single(harness.Page.Table.Mosaics);
    }

    // Spec 12.17: a failed load shows its sentence IN PLACE of the rows, so the rows a previous
    // load drew do not stay on screen under it.
    [Fact]
    public async Task AFailedReload_HidesThePreviousRows()
    {
        var fail = false;
        using var harness = await Ready(new MosaicsBackend
        {
            ListMosaics = () => fail ? throw new InvalidOperationException("locked") : [Mosaic("M 31"), Mosaic("M 33")],
        });
        var table = harness.Page.Table;
        Assert.True(table.ShowRows);

        fail = true;
        await harness.Page.ReloadAsync();

        Assert.True(table.LoadFailed);
        Assert.Empty(table.Mosaics);
        Assert.False(table.ShowRows);
        Assert.False(table.ShowSelectAllMosaics);
        Assert.False(table.HasNoMosaics);
    }

    // Fix round item 10: an added panel moves the row's figures, and the table re-sorts on them.
    [Fact]
    public async Task AddingAPanel_ReappliesTheTableSort()
    {
        var display = new DisplaySettings().WithSort(DisplaySettings.MosaicsTableId, new TableSort { Key = "frames", Ascending = true });
        var small = Mosaic("small", frames: 1);
        var big = Mosaic("big", frames: 5);
        var grown = false;
        using var harness = await Ready(
            new MosaicsBackend
            {
                ListMosaics = () => [small, big],
                Detail = id => id == small.Id && grown
                    ? new MosaicDetail(id, "small", null, 0, 600, 9, null, null, [], [Panel("Panel 1", ["M 31"]) with { Frames = 9 }], [])
                    : Detail(id, []),
                SearchTargets = _ => [new TargetSearchResult(TargetA, null, "M 31", null, null, 0, 1)],
                AddPanelWithTarget = (_, _, _) => { grown = true; return new PanelAddResult(Guid.NewGuid(), 1, 0); },
            },
            display);
        var table = harness.Page.Table;
        Assert.Equal(new[] { "small", "big" }, table.Mosaics.Select(row => row.Name));
        var row = table.Mosaics[0];
        row.ToggleExpandCommand.Execute(null);
        Assert.Equal(new[] { "small", "big" }, table.Mosaics.Select(entry => entry.Name));

        row.AddPanel!.SearchText = "M 31";
        await row.AddPanel.PendingSearch!;
        row.AddPanel.ChooseCommand.Execute(Assert.Single(row.AddPanel.SearchResults));
        row.AddPanel.AddCommand.Execute(null);

        Assert.Equal(9, row.Frames);
        Assert.Equal(new[] { "big", "small" }, table.Mosaics.Select(entry => entry.Name));
        Assert.Same(row, table.Mosaics[1]);
    }
}
