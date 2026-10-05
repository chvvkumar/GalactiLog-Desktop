using System.Text.Json;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Design-spec 12.4's raw header panel, and the roadmap Verify line's data half: a repeated
// COMMENT arrives as every line and the spec 7.3 provenance example round-trips key by key.
public class FrameHeadersQueryTests
{
    private static readonly DateOnly Day = new(2025, 3, 1);

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabaseHandle _db;

        public Fixture()
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            Query = new FrameHeadersQuery(new DatabaseConnectionString(_db.ConnectionString));
        }

        public string ConnectionString => _db.ConnectionString;
        public FrameHeadersQuery Query { get; }

        public void Dispose() => _db.Dispose();
    }

    private static string Headers(params (string Key, object Value)[] pairs)
        => JsonSerializer.Serialize(pairs.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Fact]
    public void Get_ReturnsEveryHeaderKeySorted()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 1");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.RawHeaders = Headers(("OBJECT", "M1"), ("GAIN", 100), ("ALTITUDE", 45.5)));

        var result = fixture.Query.Get(frame.Id);

        Assert.NotNull(result);
        Assert.Equal(["ALTITUDE", "GAIN", "OBJECT"], result!.RawHeaders.Select(entry => entry.Key));
    }

    [Fact]
    public void Get_RepeatedComment_ArrivesAsEveryLine()
    {
        // Review finding 6: three identical lines, not three distinct ones, so a parse that
        // de-duplicates would still fail this. The stored array is the truth; nothing here folds
        // two equal cards into one.
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 2");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.RawHeaders = Headers(
                ("OBJECT", "M2"),
                ("COMMENT", new[] { "same comment", "same comment", "same comment" })));

        var result = fixture.Query.Get(frame.Id)!;

        var comment = result.RawHeaders.Single(entry => entry.Key == "COMMENT");
        Assert.Equal(3, comment.Lines.Count);
        Assert.All(comment.Lines, line => Assert.Equal("same comment", line));
    }

    [Fact]
    public void Get_RepeatedHistory_ArrivesAsEveryLine()
    {
        // Review finding 6: identical lines, same reasoning as the COMMENT test above.
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 3");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.RawHeaders = Headers(("HISTORY", new[] { "calibrated", "calibrated" })));

        var result = fixture.Query.Get(frame.Id)!;

        var history = Assert.Single(result.RawHeaders);
        Assert.Equal("HISTORY", history.Key);
        Assert.Equal(2, history.Lines.Count);
        Assert.All(history.Lines, line => Assert.Equal("calibrated", line));
    }

    [Fact]
    public void Get_NonStringJsonValue_IsRenderedNotDropped()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 4");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.RawHeaders = """{"NAXIS":2,"SIMPLE":true,"BITPIX":16}""");

        var result = fixture.Query.Get(frame.Id)!;

        var byKey = result.RawHeaders.ToDictionary(entry => entry.Key, entry => entry.Lines[0]);
        Assert.Equal("2", byKey["NAXIS"]);
        Assert.Equal("True", byKey["SIMPLE"]);
        Assert.Equal("16", byKey["BITPIX"]);
    }

    [Fact]
    public void Get_MalformedRawHeadersJson_YieldsAnEmptyListWithoutThrowing()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 5");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.RawHeaders = "not json");

        var result = fixture.Query.Get(frame.Id)!;

        Assert.Empty(result.RawHeaders);
    }

    [Fact]
    public void Get_NullRawHeaders_YieldsAnEmptyList()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 6");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.RawHeaders = null);

        var result = fixture.Query.Get(frame.Id)!;

        Assert.Empty(result.RawHeaders);
    }

    [Fact]
    public void Get_Provenance_RoundTripsTheSevenPointThreeExample()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 7");
        const string provenance =
            """{"exposure_time":"EXPTIME","median_hfr":"csv:HFR","eccentricity":"ellipticity","altitude_deg":"CENTALT","arcsec_per_pixel":"XPIXSZ+FOCALLEN"}""";
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.Provenance = provenance);

        var result = fixture.Query.Get(frame.Id)!;

        Assert.Equal("EXPTIME", result.Provenance["exposure_time"]);
        Assert.Equal("csv:HFR", result.Provenance["median_hfr"]);
        Assert.Equal("ellipticity", result.Provenance["eccentricity"]);
        Assert.Equal("CENTALT", result.Provenance["altitude_deg"]);
        Assert.Equal("XPIXSZ+FOCALLEN", result.Provenance["arcsec_per_pixel"]);
    }

    [Fact]
    public void Get_MalformedProvenanceJson_YieldsAnEmptyMap()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 8");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.Provenance = "{not valid json");

        var result = fixture.Query.Get(frame.Id)!;

        Assert.Empty(result.Provenance);
    }

    [Fact]
    public void Get_ReturnsBothFwhmColumns()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 9");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, image =>
        {
            image.MedianFwhm = 3.10;
            image.Fwhm = 2.85;
        });

        var result = fixture.Query.Get(frame.Id)!;

        Assert.Equal(3.10, result.MedianFwhm);
        Assert.Equal(2.85, result.Fwhm);
    }

    [Fact]
    public void Get_UnknownImageId_ReturnsNull()
    {
        using var fixture = new Fixture();

        Assert.Null(fixture.Query.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Get_XisfPropertyIds_AppearAsKeys()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 10");
        var frame = LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day,
            image => image.RawHeaders = Headers(
                ("Observation:Object:Name", "M10"),
                ("OBJECT", "M10")));

        var result = fixture.Query.Get(frame.Id)!;

        Assert.Equal(
            ["OBJECT", "Observation:Object:Name"],
            result.RawHeaders.Select(entry => entry.Key));
    }
}
