using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Phase 7 Task 3. Spec 12.9's two candidate status writes, and the roadmap Verify line's
// database half: dismiss sets status dismissed.
public class MergeCandidateRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly MergeCandidateRepository _repository;

    public MergeCandidateRepositoryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _repository = new MergeCandidateRepository(new DatabaseConnectionString(_db.ConnectionString));
    }

    public void Dispose() => _db.Dispose();

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private MergeCandidate NewCandidate(Action<MergeCandidate>? configure = null)
        => LibrarySeeder.AddMergeCandidate(_db.ConnectionString, "Andromeda", configure);

    private MergeCandidate Read(Guid id)
    {
        using var context = Open();
        return context.MergeCandidates.Single(row => row.Id == id);
    }

    [Fact]
    public void Dismiss_SetsStatusDismissed()
    {
        var candidate = NewCandidate();

        Assert.True(_repository.Dismiss(candidate.Id));

        Assert.Equal("dismissed", Read(candidate.Id).Status);
    }

    [Fact]
    public void Dismiss_StampsResolvedAt()
    {
        var candidate = NewCandidate();
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.True(_repository.Dismiss(candidate.Id));

        var resolvedAt = Read(candidate.Id).ResolvedAt;
        Assert.NotNull(resolvedAt);
        Assert.InRange(resolvedAt.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Dismiss_AlreadyDismissed_ReturnsFalseAndWritesNothing()
    {
        var stamped = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var candidate = NewCandidate(row =>
        {
            row.Status = "dismissed";
            row.ResolvedAt = stamped;
        });

        Assert.False(_repository.Dismiss(candidate.Id));

        var read = Read(candidate.Id);
        Assert.Equal("dismissed", read.Status);
        Assert.Equal(stamped, read.ResolvedAt);
    }

    [Fact]
    public void Dismiss_UnknownId_ReturnsFalse()
    {
        Assert.False(_repository.Dismiss(Guid.NewGuid()));
    }

    [Fact]
    public void Retarget_ChangesTheSuggestedTargetAndKeepsTheRowPending()
    {
        var first = LibrarySeeder.AddTarget(_db.ConnectionString, "M 31");
        var second = LibrarySeeder.AddTarget(_db.ConnectionString, "NGC 224");
        var candidate = NewCandidate(row => row.SuggestedTargetId = first.Id);

        Assert.True(_repository.Retarget(candidate.Id, second.Id));

        var read = Read(candidate.Id);
        Assert.Equal(second.Id, read.SuggestedTargetId);
        Assert.Equal("pending", read.Status);
        Assert.Null(read.ResolvedAt);
    }

    [Fact]
    public void Retarget_UnknownCandidate_ReturnsFalse()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "M 31");

        Assert.False(_repository.Retarget(Guid.NewGuid(), target.Id));
    }
}
