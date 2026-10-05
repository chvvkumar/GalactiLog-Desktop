using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 7 Task 5. Spec 12.9's merge history against a migrated temp database, with the real
// MergeRepository writing the manifests wherever the case is about what a merge actually
// recorded. Only the cases about an unreadable or hand-shaped payload insert a row directly.
public class MergeHistoryQueryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly MergeRepository _repository;
    private readonly MergeHistoryQuery _query;

    public MergeHistoryQueryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _repository = new MergeRepository(new DatabaseConnectionString(_db.ConnectionString));
        _query = new MergeHistoryQuery(new DatabaseConnectionString(_db.ConnectionString));
    }

    public void Dispose() => _db.Dispose();

    // ---- helpers -----------------------------------------------------------------------

    private static DateOnly Date(int day) => new(2025, 1, day);

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private Target NewTarget(string primaryName)
        => LibrarySeeder.AddTarget(_db.ConnectionString, primaryName);

    private Image NewFrame(Guid? targetId, int day = 1)
        => LibrarySeeder.AddFrame(_db.ConnectionString, targetId, Date(day));

    private MergeManifest AddManifest(Guid winnerId, Guid? loserId, string payload, DateTime createdAt)
    {
        var manifest = new MergeManifest
        {
            Id = Guid.NewGuid(),
            WinnerId = winnerId,
            LoserId = loserId,
            Payload = payload,
            CreatedAt = createdAt,
        };

        using var context = Open();
        context.MergeManifests.Add(manifest);
        context.SaveChanges();
        return manifest;
    }

    private static string Payload(int movedFrames, string? sourceName = null)
        => JsonSerializer.Serialize(new MergeManifestPayload(
            [.. Enumerable.Range(0, movedFrames).Select(_ => Guid.NewGuid())],
            [],
            [],
            [],
            sourceName,
            CustomValuesMoved: []));

    // ---- All ---------------------------------------------------------------------------

    [Fact]
    public void All_ReturnsOneRowPerManifest()
    {
        var winner = NewTarget("M 31");
        var firstLoser = NewTarget("NGC 224");
        var secondLoser = NewTarget("Andromeda");
        NewFrame(firstLoser.Id);
        NewFrame(secondLoser.Id);

        _repository.Merge(winner.Id, firstLoser.Id);
        _repository.Merge(winner.Id, secondLoser.Id);

        var rows = _query.All();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(winner.Id, row.WinnerId));
        Assert.All(rows, row => Assert.Equal("M 31", row.WinnerName));
        Assert.Equal(
            new[] { firstLoser.Id, secondLoser.Id }.Order(),
            rows.Select(row => row.LoserId!.Value).Order());
    }

    [Fact]
    public void All_IsNewestFirst()
    {
        var winner = NewTarget("M 31");

        var oldest = AddManifest(winner.Id, null, Payload(1, "A"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = AddManifest(winner.Id, null, Payload(1, "C"), new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = AddManifest(winner.Id, null, Payload(1, "B"), new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var rows = _query.All();

        Assert.Equal(
            new[] { newest.Id, middle.Id, oldest.Id },
            rows.Select(row => row.ManifestId));
    }

    [Fact]
    public void All_ReadsTheLoserNameFromTheTargetsRow()
    {
        var winner = NewTarget("M 31");
        var loser = NewTarget("NGC 224");
        NewFrame(loser.Id);

        _repository.Merge(winner.Id, loser.Id);

        var row = Assert.Single(_query.All());
        Assert.Equal(loser.Id, row.LoserId);
        Assert.Equal("NGC 224", row.LoserName);
    }

    /// <summary>
    /// Coordinator ruling on Q8 and the Task 2 review: a null <c>loser_id</c> row is labelled from
    /// the manifest's own <c>source_name</c>, not from an accepted <c>merge_candidates</c> row.
    /// The candidate can be dismissed, deleted or never have existed; the payload is written by
    /// the merge itself. (The brief's original name for this case named the candidate.)
    /// </summary>
    [Fact]
    public void All_NullLoserId_ReadsTheNameFromTheManifestPayload()
    {
        var winner = NewTarget("M 31");
        LibrarySeeder.AddFrame(_db.ConnectionString, null, Date(1), image =>
            image.RawHeaders = LibrarySeeder.RawHeadersWithObject("Andromeda field"));

        _repository.MergeUnresolvedName(winner.Id, "Andromeda field");

        var row = Assert.Single(_query.All());
        Assert.Null(row.LoserId);
        Assert.Equal("Andromeda field", row.LoserName);
    }

    [Fact]
    public void All_NullLoserIdAndNoSourceName_ReturnsNoName()
    {
        var winner = NewTarget("M 31");
        AddManifest(winner.Id, null, Payload(2), DateTime.UtcNow);

        var row = Assert.Single(_query.All());
        Assert.Null(row.LoserId);
        Assert.Null(row.LoserName);
    }

    [Fact]
    public void All_MovedFrameCount_IsTheManifestPayloadLength()
    {
        var winner = NewTarget("M 31");
        var loser = NewTarget("NGC 224");
        NewFrame(loser.Id, day: 1);
        NewFrame(loser.Id, day: 2);
        NewFrame(loser.Id, day: 3);

        var result = _repository.Merge(winner.Id, loser.Id);

        var row = Assert.Single(_query.All());
        Assert.Equal(3, result.FramesMoved);
        Assert.Equal(3, row.MovedFrameCount);
    }

    [Fact]
    public void All_CorruptPayload_ReportsAnUnknownCountAndDoesNotThrow()
    {
        var winner = NewTarget("M 31");
        var loser = NewTarget("NGC 224");
        var readable = AddManifest(winner.Id, null, Payload(4, "a name"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var corrupt = AddManifest(winner.Id, loser.Id, "{not json at all", new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var rows = _query.All();

        Assert.Equal(2, rows.Count);

        // The unreadable row is still listed, so its manifest can still be undone.
        var corruptRow = rows.Single(row => row.ManifestId == corrupt.Id);
        Assert.Equal(-1, corruptRow.MovedFrameCount);
        Assert.Equal("NGC 224", corruptRow.LoserName);

        // And it does not take the readable rows down with it.
        var readableRow = rows.Single(row => row.ManifestId == readable.Id);
        Assert.Equal(4, readableRow.MovedFrameCount);
        Assert.Equal("a name", readableRow.LoserName);
    }

    [Fact]
    public void All_ReportsTheMergeTimeFromCreatedAt()
    {
        var winner = NewTarget("M 31");
        var created = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        AddManifest(winner.Id, null, Payload(1, "a name"), created);

        Assert.Equal(created, Assert.Single(_query.All()).MergedAt);
    }

    [Fact]
    public void All_EmptyTable_ReturnsAnEmptyList() => Assert.Empty(_query.All());

    // ---- ForWinner ---------------------------------------------------------------------

    [Fact]
    public void ForWinner_ReturnsOnlyThatTargetsManifests()
    {
        var winner = NewTarget("M 31");
        var otherWinner = NewTarget("M 42");
        var loser = NewTarget("NGC 224");
        var otherLoser = NewTarget("NGC 1976");
        NewFrame(loser.Id);
        NewFrame(otherLoser.Id);

        _repository.Merge(winner.Id, loser.Id);
        _repository.Merge(otherWinner.Id, otherLoser.Id);

        Assert.Equal(2, _query.All().Count);

        var rows = _query.ForWinner(winner.Id);
        Assert.Equal(loser.Id, Assert.Single(rows).LoserId);
    }

    [Fact]
    public void ForWinner_UnknownTarget_ReturnsAnEmptyList()
        => Assert.Empty(_query.ForWinner(Guid.NewGuid()));

    // ---- the undo consumes the row -----------------------------------------------------

    [Fact]
    public void All_AfterAnUnmerge_DropsTheConsumedManifest()
    {
        var winner = NewTarget("M 31");
        var loser = NewTarget("NGC 224");
        NewFrame(loser.Id);
        _repository.Merge(winner.Id, loser.Id);

        Assert.Single(_query.All());

        Assert.Equal(UnmergeStatus.Unmerged, _repository.Unmerge(loser.Id).Status);

        Assert.Empty(_query.All());
    }
}
