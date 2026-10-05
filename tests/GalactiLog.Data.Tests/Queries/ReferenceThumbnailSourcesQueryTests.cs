using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Spec 11.4's source selection: the target's most recent LIGHT frames that have a capture date,
// newest first, for targets that are not merged away and (unless forced) have no
// reference_thumbnail_path yet.
//
// Small hand-built cases rather than the seeded library: every rule here is about which frame of
// which target is picked, and a fixture of 900 frames says nothing about that.
public class ReferenceThumbnailSourcesQueryTests
{
    private static readonly DateOnly Night = new(2025, 3, 15);

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabaseHandle _db;

        public Fixture()
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            Query = new ReferenceThumbnailSourcesQuery(new DatabaseConnectionString(_db.ConnectionString));
        }

        public string ConnectionString => _db.ConnectionString;

        public ReferenceThumbnailSourcesQuery Query { get; }

        public void Dispose() => _db.Dispose();
    }

    // A LIGHT frame with a known capture date and a known path, so a test asserts on the path the
    // pass would be handed rather than on a generated one.
    private static void AddLight(Fixture fixture, Guid targetId, string path, DateTime? captureDate, string imageType = "LIGHT")
        => LibrarySeeder.AddFrame(fixture.ConnectionString, targetId, Night, image =>
        {
            image.FilePath = path;
            image.CaptureDate = captureDate;
            image.ImageType = imageType;
        });

    [Fact]
    public void Get_ReturnsTheMostRecentLightFramePerTarget()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 31");
        AddLight(fixture, target.Id, @"C:\Astro\old.fits", Night.ToDateTime(new TimeOnly(21, 0)));
        AddLight(fixture, target.Id, @"C:\Astro\newest.fits", Night.ToDateTime(new TimeOnly(23, 0)));

        var source = Assert.Single(fixture.Query.Get(force: false));

        Assert.Equal(target.Id, source.TargetId);
        Assert.Equal("M 31", source.PrimaryName);
        Assert.Equal(@"C:\Astro\newest.fits", source.FramePaths[0]);
    }

    [Fact]
    public void Get_ReturnsAtMostThreeFramesNewestFirst()
    {
        // Questions.md Q14's bound: the pass falls back frame by frame when a render fails, and
        // the query is what bounds that walk at three.
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 31");
        for (var hour = 18; hour <= 23; hour++)
        {
            AddLight(fixture, target.Id, $@"C:\Astro\{hour}.fits", Night.ToDateTime(new TimeOnly(hour, 0)));
        }

        var source = Assert.Single(fixture.Query.Get(force: false));

        Assert.Equal(ReferenceThumbnailSourcesQuery.MaxFramesPerTarget, source.FramePaths.Count);
        Assert.Equal(
            [@"C:\Astro\23.fits", @"C:\Astro\22.fits", @"C:\Astro\21.fits"],
            source.FramePaths);
    }

    [Fact]
    public void Get_IgnoresCalibrationFrames()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 31");
        AddLight(fixture, target.Id, @"C:\Astro\light.fits", Night.ToDateTime(new TimeOnly(21, 0)));
        AddLight(fixture, target.Id, @"C:\Astro\flat.fits", Night.ToDateTime(new TimeOnly(23, 0)), imageType: "FLAT");

        var source = Assert.Single(fixture.Query.Get(force: false));

        Assert.Equal([@"C:\Astro\light.fits"], source.FramePaths);
    }

    [Fact]
    public void Get_IgnoresFramesWithNoCaptureDate()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 31");
        AddLight(fixture, target.Id, @"C:\Astro\dated.fits", Night.ToDateTime(new TimeOnly(21, 0)));
        AddLight(fixture, target.Id, @"C:\Astro\undated.fits", captureDate: null);

        var source = Assert.Single(fixture.Query.Get(force: false));

        Assert.Equal([@"C:\Astro\dated.fits"], source.FramePaths);
    }

    [Fact]
    public void Get_TargetWithNoEligibleFrame_IsNotReturned()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 31");
        AddLight(fixture, target.Id, @"C:\Astro\undated.fits", captureDate: null);
        LibrarySeeder.AddTarget(fixture.ConnectionString, "M 42");

        Assert.Empty(fixture.Query.Get(force: false));
    }

    [Fact]
    public void Get_IgnoresMergedAwayTargets()
    {
        using var fixture = new Fixture();
        var winner = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 31");
        var loser = LibrarySeeder.AddTarget(
            fixture.ConnectionString, "NGC 224", target => target.MergedIntoId = winner.Id);
        AddLight(fixture, winner.Id, @"C:\Astro\winner.fits", Night.ToDateTime(new TimeOnly(21, 0)));
        AddLight(fixture, loser.Id, @"C:\Astro\loser.fits", Night.ToDateTime(new TimeOnly(21, 0)));

        var source = Assert.Single(fixture.Query.Get(force: false));

        Assert.Equal(winner.Id, source.TargetId);
    }

    [Fact]
    public void Get_NotForced_SkipsTargetsThatAlreadyHaveAPath()
    {
        using var fixture = new Fixture();
        var done = LibrarySeeder.AddTarget(
            fixture.ConnectionString, "M 31", target => target.ReferenceThumbnailPath = "reference/a.jpg");
        var todo = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 42");
        AddLight(fixture, done.Id, @"C:\Astro\done.fits", Night.ToDateTime(new TimeOnly(21, 0)));
        AddLight(fixture, todo.Id, @"C:\Astro\todo.fits", Night.ToDateTime(new TimeOnly(21, 0)));

        var source = Assert.Single(fixture.Query.Get(force: false));

        Assert.Equal(todo.Id, source.TargetId);
    }

    [Fact]
    public void Get_Forced_ReturnsEveryTargetIncludingThoseWithAPath()
    {
        using var fixture = new Fixture();
        var done = LibrarySeeder.AddTarget(
            fixture.ConnectionString, "M 31", target => target.ReferenceThumbnailPath = "reference/a.jpg");
        var todo = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 42");
        AddLight(fixture, done.Id, @"C:\Astro\done.fits", Night.ToDateTime(new TimeOnly(21, 0)));
        AddLight(fixture, todo.Id, @"C:\Astro\todo.fits", Night.ToDateTime(new TimeOnly(21, 0)));

        var sources = fixture.Query.Get(force: true);

        // Ordered by primary name, so "M 31" (the one that already has a path) comes first.
        Assert.Equal([done.Id, todo.Id], sources.Select(source => source.TargetId).ToList());
    }

    [Fact]
    public void Get_TargetWithoutCoordinates_IsStillReturned()
    {
        // Questions.md Q12: the web application requires ra and dec because it queries SkyView.
        // This port renders the target's own frames, so an unresolved target with no coordinates
        // is exactly the one most in need of a visual identity.
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "some unresolved string");
        Assert.Null(target.Ra);
        Assert.Null(target.Dec);
        AddLight(fixture, target.Id, @"C:\Astro\a.fits", Night.ToDateTime(new TimeOnly(21, 0)));

        var source = Assert.Single(fixture.Query.Get(force: false));

        Assert.Equal(target.Id, source.TargetId);
    }

    [Fact]
    public void Get_TieOnCaptureDate_IsDeterministic()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 31");
        var sameMoment = Night.ToDateTime(new TimeOnly(22, 0));
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Night, image =>
        {
            image.Id = Guid.Parse("20000000-0000-0000-0000-000000000001");
            image.FilePath = @"C:\Astro\lower-id.fits";
            image.CaptureDate = sameMoment;
        });
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Night, image =>
        {
            image.Id = Guid.Parse("20000000-0000-0000-0000-000000000002");
            image.FilePath = @"C:\Astro\higher-id.fits";
            image.CaptureDate = sameMoment;
        });

        // The id DESC tie-break, so two runs over the same library pick the same frame.
        Assert.Equal(@"C:\Astro\higher-id.fits", fixture.Query.Get(force: false)[0].FramePaths[0]);
        Assert.Equal(@"C:\Astro\higher-id.fits", fixture.Query.Get(force: false)[0].FramePaths[0]);
    }

    [Fact]
    public void Get_BindsEveryOperand()
    {
        // Every operand is a parameter: force and the frame limit both arrive as @names, and the
        // only interpolated text is SqlFragments.LightFrameOnly, a constant of the Data assembly.
        Assert.Contains("@force", ReferenceThumbnailSourcesQuery.Sql, StringComparison.Ordinal);
        Assert.Contains("@limit", ReferenceThumbnailSourcesQuery.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt <= 3", ReferenceThumbnailSourcesQuery.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@force = 0", ReferenceThumbnailSourcesQuery.Sql, StringComparison.Ordinal);
    }
}
