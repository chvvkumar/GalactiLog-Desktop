using System.Text.Json;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

public class DistinctHeaderKeysQueryTests
{
    private static readonly DateOnly Day = new(2025, 3, 1);

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabaseHandle _db;

        public Fixture()
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            Query = new DistinctHeaderKeysQuery(new DatabaseConnectionString(_db.ConnectionString));
        }

        public string ConnectionString => _db.ConnectionString;
        public DistinctHeaderKeysQuery Query { get; }

        public void Dispose() => _db.Dispose();
    }

    private static string Headers(params (string Key, object Value)[] pairs)
        => JsonSerializer.Serialize(pairs.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Fact]
    public void Load_ReturnsDistinctKeysSorted()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 1");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            frame => frame.RawHeaders = Headers(("OBJECT", "M1"), ("GAIN", 100)));
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            frame => frame.RawHeaders = Headers(("OBJECT", "M1")));

        var keys = fixture.Query.Load();

        Assert.Equal(["GAIN", "OBJECT"], keys);
    }

    // FIXER LIST F15. SQLite's default BINARY collation puts every upper-case key ahead of every
    // lower-case one, which offered the filter panel two alphabets in one list while the raw
    // header panel of spec 12.4 showed the same keys folded together. Both surfaces now order
    // OrdinalIgnoreCase then Ordinal, and the second comparison is what keeps two keys differing
    // only in case from tying.
    [Fact]
    public void Load_MixedCaseKeys_SortsCaseInsensitivelyThenOrdinal()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 3");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            frame => frame.RawHeaders = Headers(
                ("OBJECT", "M3"), ("airmass", 1.2), ("AIRMASS", 1.1), ("Gain", 100), ("gain", 101)));

        var keys = fixture.Query.Load();

        Assert.Equal(["AIRMASS", "airmass", "Gain", "gain", "OBJECT"], keys);
    }

    [Fact]
    public void Load_IgnoresNonLightFramesAndNullRawHeaders()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 2");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            frame => frame.RawHeaders = Headers(("OBJECT", "M2")));
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame =>
        {
            frame.ImageType = "FLAT";
            frame.RawHeaders = Headers(("EXPTIME", 1));
        });
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.RawHeaders = null);

        var keys = fixture.Query.Load();

        Assert.Equal(["OBJECT"], keys);
    }

    [Fact]
    public void Load_ExcludesKeysThatFailTheKeyGate()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 3");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame =>
            frame.RawHeaders = Headers(("OBJECT", "M3"), ("a b", "invalid"), ("$.OBJECT", "invalid")));

        var keys = fixture.Query.Load();

        Assert.Equal(["OBJECT"], keys);
    }

    [Fact]
    public void Load_RespectsTheMaxKeysCap()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 4");
        var pairs = Enumerable.Range(0, 10).Select(i => ($"KEY{i}", (object)i)).ToArray();
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.RawHeaders = Headers(pairs));

        var keys = fixture.Query.Load(maxKeys: 3);

        Assert.Equal(3, keys.Count);
        Assert.Equal(["KEY0", "KEY1", "KEY2"], keys);
    }

    [Fact]
    public void Load_EmptyLibrary_ReturnsEmpty()
    {
        using var fixture = new Fixture();

        var keys = fixture.Query.Load();

        Assert.Empty(keys);
    }

    // Review fix 2: the key gate must run in SQL before DISTINCT/ORDER BY/LIMIT, or invalid keys
    // that sort ahead of the valid ones can consume every cap slot. " junk0".." junk599" (a
    // leading space is outside the gate's character set) sort before any letter under SQLite's
    // default binary collation, so a post-LIMIT filter would return zero rows here.
    [Fact]
    public void Load_InvalidKeysDoNotConsumeTheCapBeforeValidKeysAreCounted()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 5");
        var junk = Enumerable.Range(0, 600).Select(i => ($" junk{i}", (object)i));
        var valid = new[] { ("ZZZ0", (object)0), ("ZZZ1", (object)1), ("ZZZ2", (object)2) };
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            frame => frame.RawHeaders = Headers([.. junk, .. valid]));

        var keys = fixture.Query.Load(maxKeys: 3);

        Assert.Equal(["ZZZ0", "ZZZ1", "ZZZ2"], keys);
    }

    // Review fix 3: json_each throws if raw_headers is not a JSON object (a bare scalar or
    // malformed text); json_valid/json_type must keep it from ever being called on such a row.
    [Fact]
    public void Load_SkipsScalarAndMalformedRawHeadersWithoutThrowing()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 6");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            frame => frame.RawHeaders = Headers(("OBJECT", "M6")));
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.RawHeaders = "\"x\"");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.RawHeaders = "not json");

        var keys = fixture.Query.Load();

        Assert.Equal(["OBJECT"], keys);
    }
}
