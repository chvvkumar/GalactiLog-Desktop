using GalactiLog.Core.Targets;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Spec 9.7's dashboard search, asserted here rather than in GalactiLog.App.Tests because the
// seeded library lives in this project and spec 18.3 wants the view-model tests free of a
// database (coordinator ruling Q8). The App-level tests assert the ranking is surfaced unchanged.
//
// The boundary cases below use short synthetic names whose trigram sets are small enough to work
// out by hand, so the 0.4 threshold is asserted against an exact figure rather than whatever the
// implementation happens to produce:
//   "ab" (3 trigrams) vs "abc"   (4): intersection 2, union 5 -> 0.4      exactly at
//   "ab" (3 trigrams) vs "abcd"  (5): intersection 2, union 6 -> 0.3333   just under
//   "abc" (4 trigrams) vs "abcde" (6): intersection 3, union 7 -> 0.42857 just over
public class TargetSearchQueryTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabaseHandle _db;

        public Fixture(bool seed = false)
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            if (seed)
            {
                LibrarySeeder.Seed(_db.ConnectionString);
            }

            Query = new TargetSearchQuery(new DatabaseConnectionString(_db.ConnectionString));
        }

        public string ConnectionString => _db.ConnectionString;

        public TargetSearchQuery Query { get; }

        public void Dispose() => _db.Dispose();
    }

    [Fact]
    public void Search_RanksOverASeededSet_BestMatchFirst()
    {
        using var fixture = new Fixture(seed: true);

        var results = fixture.Query.Search("Andromeda");

        var andromeda = LibrarySeeder.Targets.Single(target => target.PrimaryName == "M 31");
        Assert.Equal(andromeda.Id, results[0].TargetId);
        Assert.Equal("M 31", results[0].DisplayName);
        Assert.Equal(results.Select(result => result.Score).OrderByDescending(score => score), results.Select(result => result.Score));
    }

    [Fact]
    public void Search_MatchesAnAliasSeparately_NotAConcatenatedBlob()
    {
        using var fixture = new Fixture();
        var veil = LibrarySeeder.AddTarget(fixture.ConnectionString, "NGC 6960", target =>
        {
            target.Aliases = """["Western Veil","Witch's Broom"]""";
        });

        // A term that only half-matches two separate fields would clear the threshold against a
        // concatenation of them ("abc xyz" scores 0.5) but clears nothing on its own.
        LibrarySeeder.AddTarget(fixture.ConnectionString, "abc", target => target.CommonName = "xyz");

        var aliasHit = fixture.Query.Search("Western Veil").Single();
        Assert.Equal(veil.Id, aliasHit.TargetId);
        Assert.Equal("NGC 6960", aliasHit.DisplayName);
        Assert.Equal("Western Veil", aliasHit.MatchedOn);
        Assert.Equal(1.0, aliasHit.Score, 10);

        Assert.Empty(fixture.Query.Search("abcxyz"));
    }

    [Fact]
    public void Search_TakesTheMaximumAcrossFields()
    {
        using var fixture = new Fixture();
        LibrarySeeder.AddTarget(fixture.ConnectionString, "M 31", target =>
        {
            target.CatalogId = "M 31";
            target.CommonName = "Andromeda Galaxy";
            target.Aliases = """["NGC 224"]""";
        });

        var result = fixture.Query.Search("Andromeda").Single();

        var expected = new[] { "M 31", "M 31", "Andromeda Galaxy", "NGC 224" }
            .Max(candidate => Trigram.Similarity("Andromeda", candidate));
        Assert.Equal(expected, result.Score, 10);
        Assert.Equal("Andromeda Galaxy", result.MatchedOn);
    }

    [Fact]
    public void Search_MatchesCatalogIdAndCommonName()
    {
        using var fixture = new Fixture();
        LibrarySeeder.AddTarget(fixture.ConnectionString, "Dumbbell", target => target.CatalogId = "NGC 6853");
        LibrarySeeder.AddTarget(fixture.ConnectionString, "IC 434", target => target.CommonName = "Horsehead Nebula");

        Assert.Equal("NGC 6853", fixture.Query.Search("NGC 6853").Single().MatchedOn);
        Assert.Equal("Horsehead Nebula", fixture.Query.Search("Horsehead").Single().MatchedOn);
    }

    [Fact]
    public void Search_BelowThreshold_IsExcluded()
    {
        using var fixture = new Fixture();
        LibrarySeeder.AddTarget(fixture.ConnectionString, "abc");
        LibrarySeeder.AddTarget(fixture.ConnectionString, "abcd");
        LibrarySeeder.AddTarget(fixture.ConnectionString, "abcde");

        var atAndUnder = fixture.Query.Search("ab");
        Assert.Equal(["abc"], atAndUnder.Select(result => result.DisplayName));
        Assert.Equal(TargetSearchQuery.Threshold, atAndUnder[0].Score, 10);
        Assert.True(Trigram.Similarity("ab", "abcd") < TargetSearchQuery.Threshold);

        var justOver = fixture.Query.Search("abc");
        var abcde = justOver.Single(result => result.DisplayName == "abcde");
        Assert.True(abcde.Score > TargetSearchQuery.Threshold);
        Assert.Equal(3 / 7d, abcde.Score, 10);
    }

    [Fact]
    public void Search_ExcludesMergedTargets()
    {
        using var fixture = new Fixture();
        var survivor = LibrarySeeder.AddTarget(fixture.ConnectionString, "Andromeda Galaxy");
        LibrarySeeder.AddTarget(fixture.ConnectionString, "Andromeda Galaxy Duplicate", target =>
        {
            target.MergedIntoId = survivor.Id;
            target.MergedAt = DateTime.UtcNow;
        });

        var results = fixture.Query.Search("Andromeda");

        Assert.Equal([survivor.Id], results.Select(result => result.TargetId));
    }

    [Fact]
    public void Search_UnresolvedObjectStrings_AppearWithFrameCounts()
    {
        using var fixture = new Fixture();
        for (var day = 1; day <= 3; day++)
        {
            LibrarySeeder.AddFrame(fixture.ConnectionString, null, new DateOnly(2025, 3, day), frame =>
                frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Sh2-155"));
        }

        var result = fixture.Query.Search("Sh2-155").Single();

        Assert.Null(result.TargetId);
        Assert.Equal("Sh2-155", result.UnresolvedObject);
        Assert.Equal(3, result.FrameCount);
    }

    [Fact]
    public void Search_UnresolvedResult_CarriesTheRawObjectString()
    {
        using var fixture = new Fixture();
        LibrarySeeder.AddFrame(fixture.ConnectionString, null, new DateOnly(2025, 3, 1), frame =>
            frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Barnard 33"));

        var result = fixture.Query.Search("Barnard").Single();

        Assert.Equal("Barnard 33", result.UnresolvedObject);
        Assert.Equal("Barnard 33", result.DisplayName);
        Assert.Null(result.ObjectType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_EmptyOrWhitespaceTerm_ReturnsEmpty(string term)
    {
        using var fixture = new Fixture(seed: true);

        Assert.Empty(fixture.Query.Search(term));
    }

    [Fact]
    public void Search_RespectsTheLimit()
    {
        using var fixture = new Fixture();
        foreach (var suffix in new[] { "a", "b", "c", "d" })
        {
            LibrarySeeder.AddTarget(fixture.ConnectionString, $"Andromeda {suffix}");
        }

        Assert.Equal(4, fixture.Query.Search("Andromeda").Count);
        Assert.Equal(2, fixture.Query.Search("Andromeda", limit: 2).Count);
    }

    [Fact]
    public void Search_OrderIsStableForEqualScores()
    {
        using var fixture = new Fixture();
        // Inserted out of order; all three score 0.4 against "ab" (intersection 2, union 5).
        LibrarySeeder.AddTarget(fixture.ConnectionString, "abz");
        LibrarySeeder.AddTarget(fixture.ConnectionString, "abc");
        LibrarySeeder.AddTarget(fixture.ConnectionString, "abx");

        var results = fixture.Query.Search("ab");

        Assert.Equal(["abc", "abx", "abz"], results.Select(result => result.DisplayName));
        Assert.All(results, result => Assert.Equal(TargetSearchQuery.Threshold, result.Score, 10));
    }

    // The aliases column is NOT NULL, so a literal null is unreachable through the schema; the
    // reachable failures are an empty document and a hand-edited one that is not JSON.
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Search_NullAliasesJson_DoesNotThrow(string aliases)
    {
        using var fixture = new Fixture();
        LibrarySeeder.AddTarget(fixture.ConnectionString, "Andromeda Galaxy", target => target.Aliases = aliases);

        var result = fixture.Query.Search("Andromeda").Single();

        Assert.Equal("Andromeda Galaxy", result.MatchedOn);
    }
}
