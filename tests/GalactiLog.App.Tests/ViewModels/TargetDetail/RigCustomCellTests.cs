using System.Globalization;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.15's rig-scope cells on the session pane's rig label rows (Phase 20 Task 6b): one cell
/// per rig-scope column per rig, the four-part key each cell writes, that the cells are drawn on
/// the rig line of the first table of the pane only (ruling C23), one <c>CustomCellGroup</c> per
/// rig rather than per table row, and user choice 7's single-rig row. No database anywhere: every
/// delegate is a lambda and the write is <see cref="CustomColumnTestFactory.WriteLog"/>.
/// </summary>
public class RigCustomCellTests
{
    // Deliberately not "{Telescope} / {Camera}", so a case that recomposes the label from the
    // rig's own two halves rather than taking RigGroup.Label verbatim is red (case 2).
    private const string RigA = "Askar FMA180 / ASI2600MC";
    private const string RigB = "RedCat 51 / ASI2600MC";

    // The ranges table skips a rig's own label row entirely when it carries no measured range
    // (SessionCardViewModel.BuildRanges, "a heading over an empty block states a rig's ranges and
    // shows none"), so a multi-rig fixture needs a real Ranges array or the ranges-table half of
    // every case here would be vacuous.
    private static readonly IReadOnlyList<MetricRangeSummary> SomeRanges =
    [
        new MetricRangeSummary(1.9d, 3.1d, 2.3d),
        new MetricRangeSummary(0.3d, 0.5d, 0.4d),
        new MetricRangeSummary(1.6d, 2.4d, 1.9d),
        new MetricRangeSummary(0.3d, 0.7d, 0.45d),
        new MetricRangeSummary(-10.5d, -9.5d, -10d),
    ];

    private static RigGroup Rig(
        string label, int index = 0, int frameCount = 6, IReadOnlyList<MetricRangeSummary>? ranges = null)
        => new(
            index,
            label,
            "not-the-telescope-half",
            "not-the-camera-half",
            frameCount,
            frameCount * 300d,
            Guid.NewGuid(),
            label + ".fits",
            [label + ".fits"],
            ranges);

    // A multi-rig night's medians and details each carry a RigLabel, exactly as SplitPerRig
    // (SessionDetailQuery.cs) stamps them; a single-rig night's carry none, which BuildFilterRows
    // must still draw a full block under (the IsRigLabel null fallback).
    private static SessionDetail MultiRigDetail(params string[] labels)
        => Cards.PopulatedDetail() with
        {
            Rigs = [.. labels.Select((label, index) => Rig(label, index, ranges: SomeRanges))],
            FilterMedians =
                [.. labels.Select(label => new FilterMedians("Ha", 2.3d, 0.4d, 1.9d, 0.45d, 1400d, label))],
            FilterDetails =
                [.. labels.Select(label => new FilterDetailRow("Ha", 3, 900d, 2.3d, 0.4d, 300d, label))],
        };

    // One filter, one detail row, so a single-rig night's un-split shape is exactly one row: this
    // keeps the "no rig row" and "one rig row" cases comparing against a predictable row count
    // rather than PopulatedDetail's own two-filter default. Ranges stays null, matching the real
    // shape a single-rig night's own RigGroup carries (BuildRanges's single-rig branch reads the
    // session's own ranges, never this field, so the null is a fidelity choice, not a requirement).
    private static SessionDetail SingleRigDetail(string label)
        => Cards.PopulatedDetail() with
        {
            Rigs = [Rig(label)],
            FilterMedians = [new FilterMedians("Ha", 2.3d, 0.4d, 1.9d, 0.45d, 1400d)],
            FilterDetails = [new FilterDetailRow("Ha", 3, 900d, 2.3d, 0.4d, 300d)],
        };

    private static CustomColumnDefinition RigScopeColumn(string name = "Done", int order = 0)
        => CustomColumnTestFactory.Define(name, CustomColumnType.Boolean, CustomColumnScope.Rig, [], order);

    private static Cards.Harness Expanded(
        SessionDetail detail,
        IReadOnlyList<CustomColumnDefinition> columns,
        Guid? targetId = null,
        IReadOnlyDictionary<(Guid, CustomValueKey), string>? values = null,
        CustomColumnTestFactory.WriteLog? writes = null)
    {
        var harness = Cards.Create(detail: detail);
        harness.Card.PublishCustomColumns(
            targetId ?? Guid.NewGuid(),
            columns,
            [],
            values ?? new Dictionary<(Guid, CustomValueKey), string>(),
            writes is null ? (_, _, _) => CustomColumnTestFactory.Written : writes.Write);
        harness.Card.IsExpanded = true;
        harness.Settle();
        return harness;
    }

    private static IReadOnlyList<FilterTableRowViewModel> LabelRows(Cards.Harness harness)
        => [.. harness.Card.FilterRows.Where(row => row.IsRigLabel)];

    [Fact]
    public void AMultiRigNight_CarriesOneCellPerRigScopeColumnPerRig()
    {
        var done = RigScopeColumn("Done", order: 0);
        var note = RigScopeColumn("Note", order: 1);
        using var harness = Expanded(MultiRigDetail(RigA, RigB), [done, note]);

        var labels = LabelRows(harness);
        Assert.Equal(2, labels.Count);
        Assert.Equal(RigA, labels[0].RigLabel);
        Assert.Equal(RigB, labels[1].RigLabel);

        foreach (var row in labels)
        {
            Assert.Equal(["Done", "Note"], row.LabelRow!.CellList.Select(cell => cell.Column.Name));
        }
    }

    [Fact]
    public void ACellsKeyIsThisTargetThisNightAndThatRigsLabel()
    {
        var column = RigScopeColumn();
        var targetId = Guid.NewGuid();
        using var harness = Expanded(MultiRigDetail(RigA, RigB), [column], targetId: targetId);

        var row = LabelRows(harness).Single(row => row.RigLabel == RigA);
        var cell = Assert.Single(row.LabelRow!.CellList);

        Assert.Equal(targetId, cell.Key.TargetId);
        Assert.Null(cell.Key.MosaicId);
        Assert.Equal(harness.Card.SessionDate, cell.Key.SessionDate);
        Assert.Equal(RigA, cell.Key.RigLabel, StringComparer.Ordinal);
    }

    [Fact]
    public void ASingleRigNightWithARigScopeColumn_DrawsOneRigRow()
    {
        var column = RigScopeColumn();
        using var harness = Expanded(SingleRigDetail(RigA), [column]);

        // Red against the shipped `rigs.Count <= 1` branch, which returns before any label row is
        // built (user choice 7's whole point).
        var label = Assert.Single(LabelRows(harness));
        Assert.Equal(RigA, label.RigLabel);
        Assert.Single(label.LabelRow!.CellList);

        // The row carries the cells and nothing the pane does not already state: the same single
        // filter row a single-rig night without a rig-scope column would show, not an empty block
        // under the label (the IsRigLabel null-carried fallback for a night SplitPerRig never
        // stamps a label onto).
        Assert.Equal(2, harness.Card.FilterRows.Count);
        var filterRow = harness.Card.FilterRows[1];
        Assert.False(filterRow.IsRigLabel);
        Assert.Equal("Ha", filterRow.FilterName);

        var rangeLabel = Assert.Single(harness.Card.Ranges, range => range.IsRigLabel);
        Assert.Equal(RigA, rangeLabel.RigLabel);
        Assert.True(harness.Card.Ranges.Count > 1);

        // Ruling C23 (fix pass 1): the ranges table is the second table of the pane and carries no
        // cells of its own, even on the single-rig row.
        Assert.Empty(rangeLabel.LabelRow!.CellList);
    }

    [Fact]
    public void ASingleRigNightWithNoRigScopeColumn_DrawsNoRigRow()
    {
        // No rig-scope column at all: AnyRigScope is false, so the library reads exactly as it did
        // before this phase. Red against a predicate that is HasRigs rather than AnyRigScope.
        using var harness = Expanded(SingleRigDetail(RigA), []);

        Assert.Empty(LabelRows(harness));
        Assert.DoesNotContain(harness.Card.Ranges, range => range.IsRigLabel);
        Assert.Single(harness.Card.FilterRows);
    }

    [Fact]
    public void ARigScopeColumnWithNoValueOnThisNight_StillDrawsTheRow()
    {
        // Red against a predicate built over the card's own values rather than over the whole
        // definition list: the column is reachable even though nothing has been written to it yet.
        var column = RigScopeColumn();
        using var harness = Expanded(
            SingleRigDetail(RigA), [column], values: new Dictionary<(Guid, CustomValueKey), string>());

        var label = Assert.Single(LabelRows(harness));
        var cell = Assert.Single(label.LabelRow!.CellList);
        Assert.False(cell.IsChecked);
    }

    [Fact]
    public void TheRangeTablesRigLine_CarriesNoCells()
    {
        // Ruling C23 (fix pass 1): two editors over one stored value would fall out of step, so the
        // rig cells are drawn on the rig line of the first table of the pane (the filter table)
        // only. Red against RangeCellViewModel.RigLabelRow still being called with RigCells(...).
        var column = RigScopeColumn();
        using var harness = Expanded(MultiRigDetail(RigA, RigB), [column]);

        Assert.Equal(2, LabelRows(harness).Count(row => row.LabelRow!.HasCells));

        var rangeLabels = harness.Card.Ranges.Where(range => range.IsRigLabel).ToList();
        Assert.Equal(2, rangeLabels.Count);
        Assert.All(rangeLabels, range => Assert.Empty(range.LabelRow!.CellList));
        Assert.All(rangeLabels, range => Assert.False(range.LabelRow!.HasCells));
    }

    [Fact]
    public void ExactlyOneGroupExistsPerRigOnATwoRigNight()
    {
        // "One CustomCellGroup per rig, not per table row" (ruling C23, fix pass 1). Not directly
        // observable as a count from outside the card, so this proves it the way the rest of this
        // file already proves cell identity: the same two cell instances, one per rig, are handed
        // back across a refresh rather than a fresh pair being built each time BuildFilterRows runs.
        // Two groups per rig (one leaked per rebuild) would fail this on object identity; two
        // groups per rig (one per table) was already ruled out by TheRangeTablesRigLine_CarriesNoCells,
        // which is why this case is about rebuild identity rather than the two-tables comparison the
        // now-removed TheTwoTablesShareOneCellInstancePerSlot case made.
        var column = RigScopeColumn();
        var detail = MultiRigDetail(RigA, RigB);
        using var harness = Expanded(detail, [column]);

        var before = LabelRows(harness).ToDictionary(row => row.RigLabel, row => row.LabelRow!.CellList[0]);
        Assert.Equal(2, before.Count);
        Assert.NotSame(before[RigA], before[RigB]);

        // A refresh over the same night and the same rig set reseeds rather than rebuilding: the
        // Refresh/Load path is exercised again through Invalidate, the same mechanism a scan-driven
        // reload uses. The card is already expanded, so Invalidate alone re-queries immediately.
        harness.Card.Invalidate();
        harness.Settle();

        var after = LabelRows(harness).ToDictionary(row => row.RigLabel, row => row.LabelRow!.CellList[0]);
        Assert.Same(before[RigA], after[RigA]);
        Assert.Same(before[RigB], after[RigB]);
    }

    [Fact]
    public void AMultiRigNightWithNoRigScopeColumn_IsUnchangedByThisFixPass()
    {
        // With no rig column the rows are what they were before (ruling C23's own acceptance line):
        // a multi-rig night with no rig-scope column still draws its two label rows (ruling C4, pre
        // Phase 20) and neither carries a caption or a cell.
        using var harness = Expanded(MultiRigDetail(RigA, RigB), []);

        var labels = LabelRows(harness);
        Assert.Equal(2, labels.Count);
        Assert.All(labels, row => Assert.False(row.LabelRow!.HasCells));
        Assert.All(labels, row => Assert.Empty(row.LabelRow!.CellList));

        var rangeLabels = harness.Card.Ranges.Where(range => range.IsRigLabel).ToList();
        Assert.Equal(2, rangeLabels.Count);
        Assert.All(rangeLabels, range => Assert.False(range.LabelRow!.HasCells));
    }

    [Fact]
    public void RigLabelRowEquality_IgnoresCells_AndComparesLabelAndFrameCount()
    {
        // Ruling C26: Cells takes no part in RigLabelRowViewModel's equality, because a rig line's
        // identity is its label and its frame count, not the list of live editors currently drawn
        // on it (this is what let the pre-existing pin
        // RigPresentationTests.ARigLabelRow_IsOneSharedRowTypeInBothTables go green unedited once
        // the filter table's row started carrying cells the ranges table's row does not).
        var column = RigScopeColumn("Done");
        var cellsA = new CustomCellGroup([CustomColumnTestFactory.Cell(column)]);
        var cellsB = new CustomCellGroup([CustomColumnTestFactory.Cell(column)]);

        var withCells = RigLabelRowViewModel.For(RigA, 6, cellsA.Cells);
        var withDifferentCells = RigLabelRowViewModel.For(RigA, 6, cellsB.Cells);
        var withNoCells = RigLabelRowViewModel.For(RigA, 6);

        Assert.Equal(withCells, withDifferentCells);
        Assert.Equal(withCells, withNoCells);
        Assert.Equal(withCells.GetHashCode(), withNoCells.GetHashCode());

        var differentLabel = RigLabelRowViewModel.For(RigB, 6, cellsA.Cells);
        var differentFrameCount = RigLabelRowViewModel.For(RigA, 22, cellsA.Cells);
        Assert.NotEqual(withCells, differentLabel);
        Assert.NotEqual(withCells, differentFrameCount);
    }

    [Fact]
    public void IsMultiRig_IsUnchangedByARigScopeColumn()
    {
        // Red against a change that turns the frame table's Rig column on for every one-telescope
        // library: a single-rig night with a rig-scope column is not a multi-rig night.
        var column = RigScopeColumn();
        using var harness = Expanded(SingleRigDetail(RigA), [column]);

        Assert.False(harness.Card.IsMultiRig);
        Assert.True(harness.Card.HasRigs);
    }

    [Fact]
    public void TheAutomationNameCarriesTheNightThenTheRigLabel()
    {
        var column = RigScopeColumn("Done");
        using var harness = Expanded(SingleRigDetail(RigA), [column]);

        var cell = Assert.Single(Assert.Single(LabelRows(harness)).LabelRow!.CellList);
        var night = harness.Card.SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal($"Done, {night}, {RigA}", cell.AutomationName);
    }

    [Fact]
    public async Task ARigCellWrite_ReachesTheRepositoryDelegateOnce()
    {
        var column = RigScopeColumn("Done");
        var writes = new CustomColumnTestFactory.WriteLog();
        var targetId = Guid.NewGuid();
        using var harness = Expanded(SingleRigDetail(RigA), [column], targetId: targetId, writes: writes);

        var cell = Assert.Single(Assert.Single(LabelRows(harness)).LabelRow!.CellList);
        cell.IsChecked = true;
        await CustomColumnTestFactory.SettleAsync(cell);

        var call = Assert.Single(writes.Calls);
        Assert.Equal(column.Id, call.ColumnId);
        Assert.Equal(targetId, call.Key.TargetId);
        Assert.Equal(harness.Card.SessionDate, call.Key.SessionDate);
        Assert.Equal(RigA, call.Key.RigLabel);
        Assert.Equal(CustomColumnSlug.True, call.Value);
    }

    [Fact]
    public async Task ClosingThePage_DisposesEveryRigCell()
    {
        // Red against a dispose that does not reach the rig cell group, so a recycled row can still
        // write after the card is gone. The same shape LedgerCustomCellTests's own
        // ClosingThePage_DisposesEveryLedgerCell pins: asserted through behaviour rather than a
        // flag, because a disposed cell's OnIsCheckedChanged drops the write before it starts
        // (CustomValueViewModel.cs's own _disposed guard).
        var column = RigScopeColumn("Done");
        var writes = new CustomColumnTestFactory.WriteLog();
        var harness = Expanded(SingleRigDetail(RigA), [column], writes: writes);

        var cell = Assert.Single(Assert.Single(LabelRows(harness)).LabelRow!.CellList);

        harness.Dispose();

        cell.IsChecked = true;
        await CustomColumnTestFactory.SettleAsync(cell);

        Assert.Empty(writes.Calls);
    }

    [Fact]
    public void ACardAlreadyLoadedBeforeThePublish_PicksUpItsRigCellsOnceCalled()
    {
        // Spec 12.4's PAR-018 deep link can auto-expand a card inside
        // TargetDetailViewModel.ReplaceSessions, which runs before the page's own
        // PublishCustomColumns call, so that one card's first Refresh can run with every
        // custom-column field still at its constructor default. Red against a PublishCustomColumns
        // that does not call SessionCardViewModel.RebuildRigCells: the label row would stay absent
        // until a second, unrelated refresh happened to run BuildFilterRows again.
        var column = RigScopeColumn();
        var harness = Cards.Create(detail: SingleRigDetail(RigA));

        // Simulates the race: the card loads before anything about custom columns is published.
        harness.Card.IsExpanded = true;
        harness.Settle();
        Assert.Empty(LabelRows(harness));

        harness.Card.PublishCustomColumns(
            Guid.NewGuid(),
            [column],
            [],
            new Dictionary<(Guid, CustomValueKey), string>(),
            (_, _, _) => CustomColumnTestFactory.Written);

        var label = Assert.Single(LabelRows(harness));
        Assert.Equal(RigA, label.RigLabel);
    }
}
