using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 7 Task 3. Spec 12.9's candidate list read against a migrated temp database: the
// LEFT JOIN's merged_into_id predicate and the pending-only filter are the two rules that
// cannot be asserted against an in-memory fake.
public class MergeCandidateQueryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly MergeCandidateQuery _query;

    public MergeCandidateQueryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _query = new MergeCandidateQuery(new DatabaseConnectionString(_db.ConnectionString));
    }

    public void Dispose() => _db.Dispose();

    private Target NewTarget(string primaryName, Action<Target>? configure = null)
        => LibrarySeeder.AddTarget(_db.ConnectionString, primaryName, configure);

    private MergeCandidate NewCandidate(string sourceName, Action<MergeCandidate>? configure = null)
        => LibrarySeeder.AddMergeCandidate(_db.ConnectionString, sourceName, configure);

    [Fact]
    public void Pending_EmptyTable_ReturnsAnEmptyList()
    {
        Assert.Empty(_query.Pending());
    }

    [Fact]
    public void Pending_ReturnsOnlyPendingRows()
    {
        NewCandidate("Pending one");
        NewCandidate("Accepted one", candidate => candidate.Status = "accepted");
        NewCandidate("Dismissed one", candidate => candidate.Status = "dismissed");

        var rows = _query.Pending();

        Assert.Equal("Pending one", Assert.Single(rows).SourceName);
    }

    [Fact]
    public void Pending_JoinsTheSuggestedTargetName()
    {
        var winner = NewTarget("M 31");
        NewCandidate("Andromeda", candidate =>
        {
            candidate.SuggestedTargetId = winner.Id;
            candidate.Method = "simbad";
            candidate.SimilarityScore = 1.0;
        });

        var row = Assert.Single(_query.Pending());

        Assert.Equal(winner.Id, row.SuggestedTargetId);
        Assert.Equal("M 31", row.SuggestedTargetName);
    }

    [Fact]
    public void Pending_OrphanCandidate_HasNoSuggestedTarget()
    {
        NewCandidate("Zzyzx Blob 42", candidate => candidate.Method = "orphan");

        var row = Assert.Single(_query.Pending());

        Assert.Null(row.SuggestedTargetId);
        Assert.Null(row.SuggestedTargetName);
        Assert.Equal("orphan", row.Method);
    }

    [Fact]
    public void Pending_SuggestedTargetMergedAway_ReturnsNoName()
    {
        var survivor = NewTarget("M 31");
        var mergedAway = NewTarget("Andromeda Galaxy", target =>
        {
            target.MergedIntoId = survivor.Id;
            target.MergedAt = DateTime.UtcNow;
        });
        NewCandidate("Andromeda", candidate => candidate.SuggestedTargetId = mergedAway.Id);

        var row = Assert.Single(_query.Pending());

        // The id survives so the accept action can say the suggested target is gone; the name
        // does not, because the dashboard no longer offers that target.
        Assert.Equal(mergedAway.Id, row.SuggestedTargetId);
        Assert.Null(row.SuggestedTargetName);
    }

    [Fact]
    public void Pending_ReturnsEveryColumnSpec129Lists()
    {
        var winner = NewTarget("NGC 7331");
        var created = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var candidate = NewCandidate("NGC7331 field", row =>
        {
            row.SourceImageCount = 12;
            row.SuggestedTargetId = winner.Id;
            row.SimilarityScore = 0.875;
            row.Method = "trigram";
            row.ReasonText = "Name is 87% similar to \"NGC 7331\"";
            row.CreatedAt = created;
        });

        var read = Assert.Single(_query.Pending());

        Assert.Equal(candidate.Id, read.Id);
        Assert.Equal("NGC7331 field", read.SourceName);
        Assert.Equal(12, read.SourceImageCount);
        Assert.Equal(winner.Id, read.SuggestedTargetId);
        Assert.Equal("NGC 7331", read.SuggestedTargetName);
        Assert.Equal(0.875, read.SimilarityScore);
        Assert.Equal("trigram", read.Method);
        Assert.Equal("Name is 87% similar to \"NGC 7331\"", read.ReasonText);
        Assert.Equal(created, read.CreatedAt);
    }

    [Fact]
    public void Pending_IsNewestFirstThenBySourceName()
    {
        var older = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        NewCandidate("older", candidate => candidate.CreatedAt = older);
        NewCandidate("bravo", candidate => candidate.CreatedAt = newer);
        NewCandidate("alpha", candidate => candidate.CreatedAt = newer);

        Assert.Equal(
            new[] { "alpha", "bravo", "older" },
            _query.Pending().Select(row => row.SourceName));
    }
}
