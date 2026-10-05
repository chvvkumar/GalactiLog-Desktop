using System.Text.Json;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Repositories;

// Phase 20 Task 2, ruling C3. The merge's sixth table: the loser's custom values move to the
// winner where the winner holds no value in the same slot, the moved ids go into the manifest, and
// the unmerge restores exactly those ids and no others.
public class CustomColumnMergeTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly MergeRepository _merge;
    private readonly CustomColumnRepository _columns;

    public CustomColumnMergeTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        var connectionString = new DatabaseConnectionString(_db.ConnectionString);
        _merge = new MergeRepository(connectionString);
        _columns = new CustomColumnRepository(connectionString);
    }

    public void Dispose() => _db.Dispose();

    // Case 24. Red against a merge that moves nothing (the loser's values vanish from every
    // surface, because every surface reads by the winner's id) and against one that records no
    // ids, which would leave the unmerge with nothing to restore.
    [Fact]
    public void Merge_MovesTheLosersValuesWhereTheWinnerHasNone_AndRecordsTheIds()
    {
        var winner = NewTarget("NGC 7000");
        var loser = NewTarget("Sh2-117");
        var column = Column("Priority", CustomColumnScope.Target);
        var nightly = Column("Seeing", CustomColumnScope.Session);

        Set(column, CustomValueKey.ForTarget(loser), "high");
        Set(nightly, CustomValueKey.ForSession(loser, Date(4)), "good");

        var result = _merge.Merge(winner, loser);

        Assert.Equal(MergeStatus.Merged, result.Status);
        Assert.Equal(
            new[] { "good", "high" },
            ValuesOf(winner).Select(row => row.Value).Order(StringComparer.Ordinal).ToList());
        Assert.Empty(ValuesOf(loser));

        var recorded = PayloadOf(result.ManifestId!.Value).CustomValuesMoved;
        Assert.Equal(2, recorded.Count);
        Assert.Equal(IdsOf(winner).Order().ToList(), recorded.Order().ToList());
    }

    // Case 25. User choice 21 and the web's own rule (target_merge.py lines 77 to 99). Red against
    // a re-key that overwrites: the winner's own value is the one the user sees afterwards, and it
    // must be byte identical to what it was before the merge.
    [Fact]
    public void Merge_AConflictingSlot_LeavesTheLosersValueWhereItIs()
    {
        var winner = NewTarget("NGC 7000");
        var loser = NewTarget("Sh2-117");
        var contested = Column("Priority", CustomColumnScope.Target);
        var free = Column("Notes", CustomColumnScope.Target);

        Set(contested, CustomValueKey.ForTarget(winner), "winner keeps this");
        Set(contested, CustomValueKey.ForTarget(loser), "loser keeps this");
        Set(free, CustomValueKey.ForTarget(loser), "this one moves");

        var result = _merge.Merge(winner, loser);

        Assert.Equal(MergeStatus.Merged, result.Status);
        Assert.Equal("winner keeps this", ValueOf(winner, contested));
        Assert.Equal("this one moves", ValueOf(winner, free));

        // The conflicting value stays on the loser, which is what makes the unmerge exact.
        Assert.Equal("loser keeps this", ValueOf(loser, contested));
        Assert.Single(PayloadOf(result.ManifestId!.Value).CustomValuesMoved);
    }

    // A rig-scope slot is (column_id, session_date, rig_label), not target_id alone: two values of
    // the same column on the same night under different rigs both move.
    [Fact]
    public void Merge_TheSlotIsTheColumnTheNightAndTheRig()
    {
        var winner = NewTarget("NGC 7000");
        var loser = NewTarget("Sh2-117");
        var column = Column("Rig note", CustomColumnScope.Rig);

        Set(column, CustomValueKey.ForRig(winner, Date(4), "Askar 120 / ASI2600MC"), "winner's rig");
        Set(column, CustomValueKey.ForRig(loser, Date(4), "Askar 120 / ASI2600MC"), "same slot");
        Set(column, CustomValueKey.ForRig(loser, Date(4), "RedCat 51 / ASI533MC"), "free slot");
        Set(column, CustomValueKey.ForRig(loser, Date(5), "Askar 120 / ASI2600MC"), "other night");

        var result = _merge.Merge(winner, loser);

        Assert.Equal(2, PayloadOf(result.ManifestId!.Value).CustomValuesMoved.Count);
        Assert.Equal(
            new[] { "free slot", "other night", "winner's rig" },
            ValuesOf(winner).Select(row => row.Value).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(new[] { "same slot" }, ValuesOf(loser).Select(row => row.Value).ToList());
    }

    // Case 26. Red against a restore derived from the loser's current values rather than from the
    // manifest: the winner's own values, both the one it held before the merge and the one it
    // acquires after it, would then be dragged back to the loser too. Exactly the recorded id
    // moves, and both targets hold a row afterwards.
    [Fact]
    public void Unmerge_RestoresExactlyTheRecordedIds()
    {
        var winner = NewTarget("NGC 7000");
        var loser = NewTarget("Sh2-117");
        var moving = Column("Priority", CustomColumnScope.Target);
        var held = Column("Notes", CustomColumnScope.Target);
        var acquired = Column("Grade", CustomColumnScope.Target);

        Set(moving, CustomValueKey.ForTarget(loser), "the loser's");
        Set(held, CustomValueKey.ForTarget(winner), "the winner's own, never moved");

        var merged = _merge.Merge(winner, loser);
        var recorded = PayloadOf(merged.ManifestId!.Value).CustomValuesMoved;
        Assert.Single(recorded);

        // A value the winner acquires between the two operations, in a slot the merge did not
        // move. The unmerge must not see it: it restores ids, not slots.
        Set(acquired, CustomValueKey.ForTarget(winner), "acquired since");

        Assert.Equal(UnmergeStatus.Unmerged, _merge.Unmerge(loser).Status);

        Assert.Equal("the loser's", ValueOf(loser, moving));
        Assert.Equal(recorded.Single(), Assert.Single(ValuesOf(loser)).Id);
        Assert.Equal("the winner's own, never moved", ValueOf(winner, held));
        Assert.Equal("acquired since", ValueOf(winner, acquired));
        Assert.Equal(2, ValuesOf(winner).Count);
    }

    // The restore can never trip uq_custom_column_value, because the merge only moved slots the
    // winner did not hold and the loser's own slot is free by construction: the loser is merged
    // away and no surface writes to it. The brief's stated shape for this, "the winner writes a
    // value in the MOVED slot between the merge and the unmerge", is not reachable through the
    // repository (see task2-report.md): after the merge that slot IS the moved row, so SetValue
    // upserts onto it rather than inserting a second row. This pins what actually happens, which
    // is that the edit rides back to the loser on the row it was made on, and that nothing throws.
    [Fact]
    public void Unmerge_AMovedValueEditedOnTheWinner_RidesBackOnTheSameRow()
    {
        var winner = NewTarget("NGC 7000");
        var loser = NewTarget("Sh2-117");
        var column = Column("Priority", CustomColumnScope.Target);

        Set(column, CustomValueKey.ForTarget(loser), "the loser's");
        var merged = _merge.Merge(winner, loser);
        var recorded = PayloadOf(merged.ManifestId!.Value).CustomValuesMoved;

        Set(column, CustomValueKey.ForTarget(winner), "edited on the winner");
        Assert.Equal(recorded.Single(), Assert.Single(ValuesOf(winner)).Id);

        Assert.Equal(UnmergeStatus.Unmerged, _merge.Unmerge(loser).Status);

        Assert.Equal("edited on the winner", ValueOf(loser, column));
        Assert.Empty(ValuesOf(winner));
    }

    // Case 27. The column may have been deleted since the merge, which cascades its values away.
    // That is not an error and nothing is restored for it.
    [Fact]
    public void Unmerge_AValueWhoseColumnWasDeleted_IsSkippedAndNothingThrows()
    {
        var winner = NewTarget("NGC 7000");
        var loser = NewTarget("Sh2-117");
        var doomed = Column("Priority", CustomColumnScope.Target);
        var kept = Column("Notes", CustomColumnScope.Target);

        Set(doomed, CustomValueKey.ForTarget(loser), "goes away");
        Set(kept, CustomValueKey.ForTarget(loser), "comes back");

        var merged = _merge.Merge(winner, loser);
        Assert.Equal(2, PayloadOf(merged.ManifestId!.Value).CustomValuesMoved.Count);

        Assert.Equal(CustomWriteStatus.Written, _columns.Delete(doomed).Status);

        var unmerged = _merge.Unmerge(loser);

        Assert.Equal(UnmergeStatus.Unmerged, unmerged.Status);
        Assert.Equal(new[] { "comes back" }, ValuesOf(loser).Select(row => row.Value).ToList());
        Assert.Empty(ValuesOf(winner));
    }

    // Case 28. An unresolved-name merge has no loser target row and therefore no values to move:
    // it records an empty list and undoes cleanly. Red against a MergeUnresolvedName that reached
    // for a loser's values at all.
    [Fact]
    public void AnUnresolvedNameMerge_RecordsAnEmptyCustomValueList_AndUndoesCleanly()
    {
        var winner = NewTarget("NGC 7000");
        var column = Column("Priority", CustomColumnScope.Target);
        Set(column, CustomValueKey.ForTarget(winner), "the winner's own");

        var merged = _merge.MergeUnresolvedName(winner, "North America Neb");

        Assert.Equal(MergeStatus.Merged, merged.Status);
        Assert.Empty(PayloadOf(merged.ManifestId!.Value).CustomValuesMoved);

        Assert.Equal(UnmergeStatus.Unmerged, _merge.UndoUnresolvedNameMerge(merged.ManifestId.Value).Status);
        Assert.Equal("the winner's own", ValueOf(winner, column));
    }

    // An older manifest, written before this phase, carries no custom_values_moved key at all. It
    // reads as null and coalesces to empty exactly as the four existing arrays do, so an unmerge of
    // a merge made by an earlier version still runs.
    [Fact]
    public void AnOlderManifestWithNoCustomValuesKey_UnmergesWithoutThrowing()
    {
        var winner = NewTarget("NGC 7000");
        var loser = NewTarget("Sh2-117");
        var merged = _merge.Merge(winner, loser);

        using (var context = Open())
        {
            var manifest = context.MergeManifests.Single(row => row.Id == merged.ManifestId!.Value);
            manifest.Payload = JsonSerializer.Serialize(new
            {
                moved_image_ids = Array.Empty<Guid>(),
                notes_rekeyed = Array.Empty<Guid>(),
                notes_appended = Array.Empty<object>(),
                aliases_added = Array.Empty<string>(),
            });
            context.SaveChanges();
        }

        Assert.Equal(UnmergeStatus.Unmerged, _merge.Unmerge(loser).Status);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static DateOnly Date(int day) => new(2026, 1, day);

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private Guid NewTarget(string primaryName)
        => LibrarySeeder.AddTarget(
            _db.ConnectionString, primaryName, target => target.Aliases = JsonSerializer.Serialize(Array.Empty<string>())).Id;

    private Guid Column(string name, CustomColumnScope scope)
    {
        var result = _columns.Create(name, CustomColumnType.Text, scope, []);
        Assert.Equal(CustomWriteStatus.Written, result.Status);
        return result.Column!.Id;
    }

    private void Set(Guid columnId, CustomValueKey key, string value)
        => Assert.Equal(CustomWriteStatus.Written, _columns.SetValue(columnId, key, value).Status);

    private List<CustomColumnValue> ValuesOf(Guid targetId)
    {
        using var context = Open();
        return context.CustomColumnValues.Where(row => row.TargetId == targetId).ToList();
    }

    private List<Guid> IdsOf(Guid targetId) => ValuesOf(targetId).Select(row => row.Id).ToList();

    private string ValueOf(Guid targetId, Guid columnId)
    {
        using var context = Open();
        return context.CustomColumnValues
            .Single(row => row.TargetId == targetId && row.ColumnId == columnId)
            .Value;
    }

    private MergeManifestPayload PayloadOf(Guid manifestId)
    {
        using var context = Open();
        var json = context.MergeManifests.Single(row => row.Id == manifestId).Payload;
        return JsonSerializer.Deserialize<MergeManifestPayload>(json)!;
    }
}
