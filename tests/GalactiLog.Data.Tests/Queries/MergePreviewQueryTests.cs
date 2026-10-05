using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 7 Task 4. Spec 12.9's merge preview read against a migrated temp database, which is the
// only place the json_extract group key and the (target_id, session_date) unique index are real.
// The roadmap Verify line's Data half is Get_CollidingSessionDates_AreExactlyTheDatesBothSidesHaveANoteFor.
public class MergePreviewQueryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly MergePreviewQuery _query;

    public MergePreviewQueryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _query = new MergePreviewQuery(new DatabaseConnectionString(_db.ConnectionString));
    }

    public void Dispose() => _db.Dispose();

    // ---- helpers -----------------------------------------------------------------------

    private static DateOnly Date(int day) => new(2025, 1, day);

    private Target NewTarget(string primaryName, Action<Target>? configure = null)
        => LibrarySeeder.AddTarget(_db.ConnectionString, primaryName, configure);

    private Image NewFrame(Guid? targetId, int day = 1, Action<Image>? configure = null)
        => LibrarySeeder.AddFrame(_db.ConnectionString, targetId, Date(day), configure);

    private Image NewUnresolvedFrame(string objectName, int day = 1, Action<Image>? configure = null)
        => LibrarySeeder.AddFrame(_db.ConnectionString, null, Date(day), image =>
        {
            image.RawHeaders = LibrarySeeder.RawHeadersWithObject(objectName);
            configure?.Invoke(image);
        });

    private void NewNote(Guid targetId, int day, string notes)
        => LibrarySeeder.AddSessionNote(_db.ConnectionString, targetId, Date(day), notes);

    private static Action<Target> WithAliases(params string[] aliases)
        => target => target.Aliases = JsonSerializer.Serialize(aliases);

    private MergeRepository Repository() => new(new DatabaseConnectionString(_db.ConnectionString));

    // ---- both sides, every spec 12.9 field ---------------------------------------------

    [Fact]
    public void Get_ReturnsEveryFieldSpec129ListsForBothSides()
    {
        var winner = NewTarget("NGC 7331", target =>
        {
            target.CatalogId = "NGC7331";
            target.Aliases = JsonSerializer.Serialize(new[] { "Caldwell 30" });
            target.ObjectType = "GiG,G";
            target.Ra = 339.2670d;
            target.Dec = 34.4158d;
        });
        var loser = NewTarget("Deer Lick", target =>
        {
            target.CatalogId = "PGC69327";
            target.Aliases = JsonSerializer.Serialize(new[] { "Deer Lick Group" });
            target.ObjectType = "G";
            target.Ra = 339.3d;
            target.Dec = 34.5d;
        });

        NewFrame(winner.Id, day: 3);
        NewFrame(winner.Id, day: 5);
        NewFrame(loser.Id, day: 7);

        var preview = _query.Get(winner.Id, loser.Id, null);

        Assert.NotNull(preview);
        Assert.Equal(winner.Id, preview.Winner.TargetId);
        Assert.Equal("NGC 7331", preview.Winner.PrimaryName);
        Assert.Equal("NGC7331", preview.Winner.CatalogId);
        Assert.Equal(new[] { "Caldwell 30" }, preview.Winner.Aliases);
        Assert.Equal("GiG,G", preview.Winner.ObjectType);
        Assert.Equal("Galaxy", preview.Winner.ObjectCategory);
        Assert.Equal(339.2670d, preview.Winner.Ra!.Value, 4);
        Assert.Equal(34.4158d, preview.Winner.Dec!.Value, 4);
        Assert.Equal(2, preview.Winner.FrameCount);
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, preview.Winner.IntegrationSeconds);
        Assert.Equal(2, preview.Winner.SessionCount);
        Assert.Equal(Date(3), preview.Winner.FirstSession);
        Assert.Equal(Date(5), preview.Winner.LastSession);

        Assert.NotNull(preview.Loser);
        Assert.Equal(loser.Id, preview.Loser.TargetId);
        Assert.Equal("Deer Lick", preview.Loser.PrimaryName);
        Assert.Equal("PGC69327", preview.Loser.CatalogId);
        Assert.Equal(new[] { "Deer Lick Group" }, preview.Loser.Aliases);
        Assert.Equal("G", preview.Loser.ObjectType);
        Assert.Equal("Galaxy", preview.Loser.ObjectCategory);
        Assert.Equal(1, preview.Loser.FrameCount);
        Assert.Equal(Date(7), preview.Loser.FirstSession);
        Assert.Equal(Date(7), preview.Loser.LastSession);
        Assert.Equal(1, preview.FramesToMove);
    }

    [Fact]
    public void Get_FrameCountAndIntegration_CoverLightFramesOnly()
    {
        var winner = NewTarget("M 31");
        var loser = NewTarget("Andromeda");

        NewFrame(loser.Id, day: 1);
        NewFrame(loser.Id, day: 1, configure: image => image.ImageType = "DARK");
        NewFrame(loser.Id, day: 1, configure: image => image.ImageType = "FLAT");

        var preview = _query.Get(winner.Id, loser.Id, null);

        Assert.NotNull(preview);
        Assert.Equal(1, preview.Loser!.FrameCount);
        Assert.Equal(LibrarySeeder.ExposureSeconds, preview.Loser.IntegrationSeconds);

        // FramesToMove is deliberately not this figure (review finding 1): it is the promise
        // about what the merge writes, and the merge moves the calibration rows too. See
        // Get_FramesToMove_CountsEveryLoserFrameIncludingCalibration.
        Assert.Equal(3, preview.FramesToMove);
    }

    [Fact]
    public void Get_SessionCount_CountsDatedSessionsOnly()
    {
        var winner = NewTarget("M 42");
        var loser = NewTarget("Orion");

        NewFrame(loser.Id, day: 2);
        NewFrame(loser.Id, day: 2);
        NewFrame(loser.Id, day: 4);
        LibrarySeeder.AddFrame(_db.ConnectionString, loser.Id, Date(6), image => image.SessionDate = null);

        var preview = _query.Get(winner.Id, loser.Id, null);

        Assert.NotNull(preview);
        Assert.Equal(4, preview.Loser!.FrameCount);

        // The undated frame belongs to no session (spec 12.4).
        Assert.Equal(2, preview.Loser.SessionCount);
    }

    [Fact]
    public void Get_FirstAndLastSession_AreTheGroupExtremes()
    {
        var winner = NewTarget("M 45");
        var loser = NewTarget("Pleiades");

        NewFrame(loser.Id, day: 9);
        NewFrame(loser.Id, day: 2);
        NewFrame(loser.Id, day: 17);

        var preview = _query.Get(winner.Id, loser.Id, null);

        Assert.NotNull(preview);
        Assert.Equal(Date(2), preview.Loser!.FirstSession);
        Assert.Equal(Date(17), preview.Loser.LastSession);
    }

    // ---- colliding session dates (roadmap Verify) ---------------------------------------

    [Fact]
    public void Get_CollidingSessionDates_AreExactlyTheDatesBothSidesHaveANoteFor()
    {
        var winner = NewTarget("NGC 7331");
        var loser = NewTarget("Deer Lick");

        NewNote(winner.Id, 3, "winner three");
        NewNote(winner.Id, 5, "winner five");
        NewNote(winner.Id, 9, "winner nine");
        NewNote(loser.Id, 5, "loser five");
        NewNote(loser.Id, 9, "loser nine");

        // A date only the loser has is re-keyed, not appended, so it is not a collision.
        NewNote(loser.Id, 11, "loser eleven");

        var preview = _query.Get(winner.Id, loser.Id, null);

        Assert.NotNull(preview);
        Assert.Equal(new[] { Date(5), Date(9) }, preview.CollidingSessionDates);
    }

    [Fact]
    public void Get_CollidingSessionDates_AreAscending()
    {
        var winner = NewTarget("NGC 7331");
        var loser = NewTarget("Deer Lick");

        foreach (var day in new[] { 21, 3, 14, 8 })
        {
            NewNote(winner.Id, day, $"winner {day}");
            NewNote(loser.Id, day, $"loser {day}");
        }

        var preview = _query.Get(winner.Id, loser.Id, null);

        Assert.NotNull(preview);
        Assert.Equal(new[] { Date(3), Date(8), Date(14), Date(21) }, preview.CollidingSessionDates);
    }

    [Fact]
    public void Get_NoCollidingDates_ReturnsAnEmptyList()
    {
        var winner = NewTarget("NGC 7331");
        var loser = NewTarget("Deer Lick");

        NewNote(winner.Id, 3, "winner three");
        NewNote(loser.Id, 5, "loser five");

        var preview = _query.Get(winner.Id, loser.Id, null);

        Assert.NotNull(preview);
        Assert.Empty(preview.CollidingSessionDates);
    }

    // ---- aliases to add: the shared-helper proof ----------------------------------------

    [Fact]
    public void Get_AliasesToAdd_MatchesWhatTheMergeWouldAdd()
    {
        var winner = NewTarget("NGC 7331", WithAliases("Caldwell 30"));
        var loser = NewTarget("Deer Lick", WithAliases("Deer Lick Group", "caldwell 30", "PGC 69327"));

        var preview = _query.Get(winner.Id, loser.Id, null);
        Assert.NotNull(preview);

        var result = Repository().Merge(winner.Id, loser.Id);

        Assert.Equal(MergeStatus.Merged, result.Status);
        Assert.Equal(result.AliasesAdded, preview.AliasesToAdd);
    }

    [Fact]
    public void Get_AliasesToAdd_SkipsTheWinnerOwnNameAndIsCaseInsensitive()
    {
        var winner = NewTarget("NGC 7331", WithAliases("Caldwell 30"));
        var loser = NewTarget("Deer Lick", WithAliases("ngc 7331", "CALDWELL 30", "PGC 69327"));

        var preview = _query.Get(winner.Id, loser.Id, null);

        Assert.NotNull(preview);
        Assert.Equal(new[] { "Deer Lick", "PGC 69327" }, preview.AliasesToAdd);
    }

    // ---- frames to move: the promise about the write, not an aggregate ------------------

    [Fact]
    public void Get_FramesToMove_CountsEveryLoserFrameIncludingCalibration()
    {
        // Review finding 1. MergeRepository.Merge moves every images row pointing at the loser,
        // and a calibration frame that carried an OBJECT card has a resolved_target_id too, so
        // the preview's promise must not be the LIGHT-only side count.
        var winner = NewTarget("NGC 7331");
        var loser = NewTarget("Deer Lick");

        NewFrame(loser.Id, day: 1);
        NewFrame(loser.Id, day: 2);
        NewFrame(loser.Id, day: 2, configure: image => image.ImageType = "DARK");
        NewFrame(loser.Id, day: 3, configure: image => image.ImageType = "FLAT");

        var preview = _query.Get(winner.Id, loser.Id, null);
        Assert.NotNull(preview);

        // The side still reports LIGHT only, like every other aggregate on the page set.
        Assert.Equal(2, preview.Loser!.FrameCount);
        Assert.Equal(4, preview.FramesToMove);

        var result = Repository().Merge(winner.Id, loser.Id);

        Assert.Equal(MergeStatus.Merged, result.Status);
        Assert.Equal(result.FramesMoved, preview.FramesToMove);
    }

    // ---- the unresolved-name loser ------------------------------------------------------

    [Fact]
    public void Get_UnresolvedNameLoser_CountsTheUnresolvedFramesOfThatName()
    {
        var winner = NewTarget("NGC 7331");
        var other = NewTarget("M 31");

        NewUnresolvedFrame("ngc7331 mosaic", day: 3);
        NewUnresolvedFrame("ngc7331 mosaic", day: 8);
        NewUnresolvedFrame("ngc7331 mosaic", day: 8, configure: image => image.ImageType = "DARK");
        NewUnresolvedFrame("something else", day: 8);

        // A frame already assigned to a target is not part of the unresolved group.
        NewFrame(other.Id, day: 8, configure: image =>
            image.RawHeaders = LibrarySeeder.RawHeadersWithObject("ngc7331 mosaic"));

        var preview = _query.Get(winner.Id, null, "ngc7331 mosaic");

        Assert.NotNull(preview);
        Assert.NotNull(preview.Loser);
        Assert.Null(preview.Loser.TargetId);
        Assert.Equal("ngc7331 mosaic", preview.Loser.PrimaryName);
        Assert.Null(preview.Loser.CatalogId);
        Assert.Empty(preview.Loser.Aliases);
        Assert.Null(preview.Loser.ObjectType);
        Assert.Equal(TargetListingCriteria.UnresolvedCategory, preview.Loser.ObjectCategory);
        Assert.Null(preview.Loser.Ra);
        Assert.Null(preview.Loser.Dec);
        Assert.Equal(2, preview.Loser.FrameCount);
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, preview.Loser.IntegrationSeconds);
        Assert.Equal(2, preview.Loser.SessionCount);
        Assert.Equal(Date(3), preview.Loser.FirstSession);
        Assert.Equal(Date(8), preview.Loser.LastSession);
        Assert.Equal(2, preview.FramesToMove);
        Assert.Equal(new[] { "ngc7331 mosaic" }, preview.AliasesToAdd);
    }

    [Fact]
    public void Get_UnresolvedNameLoser_MatchesANumericObjectCard()
    {
        // Phase 5 fix F1: json_extract yields a SQLite number for an unquoted numeric OBJECT
        // card, so the group scope forces both sides to text.
        var winner = NewTarget("NGC 7331");
        LibrarySeeder.AddFrame(_db.ConnectionString, null, Date(4), image =>
            image.RawHeaders = """{"OBJECT":7331}""");

        var preview = _query.Get(winner.Id, null, "7331");

        Assert.NotNull(preview);
        Assert.Equal(1, preview.Loser!.FrameCount);
        Assert.Equal(1, preview.FramesToMove);
    }

    [Fact]
    public void Get_UnresolvedNameLoser_HasNoCollidingDates()
    {
        var winner = NewTarget("NGC 7331");
        NewNote(winner.Id, 3, "winner three");
        NewUnresolvedFrame("ngc7331 mosaic", day: 3);

        var preview = _query.Get(winner.Id, null, "ngc7331 mosaic");

        Assert.NotNull(preview);

        // A note is keyed on a target id and an unresolved name has none.
        Assert.Empty(preview.CollidingSessionDates);
    }

    // ---- the null cases -----------------------------------------------------------------

    [Fact]
    public void Get_MergedAwayWinner_ReturnsNull()
    {
        var survivor = NewTarget("NGC 7331");
        var winner = NewTarget("Deer Lick", target =>
        {
            target.MergedIntoId = survivor.Id;
            target.MergedAt = DateTime.UtcNow;
        });
        var loser = NewTarget("Stephan");

        Assert.Null(_query.Get(winner.Id, loser.Id, null));
    }

    [Fact]
    public void Get_MergedAwayLoser_ReturnsNull()
    {
        var winner = NewTarget("NGC 7331");
        var survivor = NewTarget("M 31");
        var loser = NewTarget("Deer Lick", target =>
        {
            target.MergedIntoId = survivor.Id;
            target.MergedAt = DateTime.UtcNow;
        });

        Assert.Null(_query.Get(winner.Id, loser.Id, null));
    }

    [Fact]
    public void Get_UnknownWinner_ReturnsNull()
    {
        var loser = NewTarget("Deer Lick");

        Assert.Null(_query.Get(Guid.NewGuid(), loser.Id, null));
        Assert.Null(_query.Get(Guid.NewGuid(), null, "some name"));
    }

    [Fact]
    public void Get_UnknownLoser_ReturnsNull()
    {
        var winner = NewTarget("NGC 7331");

        Assert.Null(_query.Get(winner.Id, Guid.NewGuid(), null));
    }

    [Fact]
    public void Get_NoLoserChosenYet_ReturnsTheWinnerAloneWithNothingToDescribe()
    {
        // Deviation D1: Target detail opens the dialog with only the current target known.
        var winner = NewTarget("NGC 7331");
        NewFrame(winner.Id, day: 3);

        var preview = _query.Get(winner.Id, null, null);

        Assert.NotNull(preview);
        Assert.Equal(winner.Id, preview.Winner.TargetId);
        Assert.Null(preview.Loser);
        Assert.Equal(0, preview.FramesToMove);
        Assert.Empty(preview.CollidingSessionDates);
        Assert.Empty(preview.AliasesToAdd);
    }

    // ---- ruling Q18: which merge shape a candidate maps to -------------------------------

    [Fact]
    public void ActiveTargetIdByPrimaryName_FindsAPass2Loser()
    {
        var target = NewTarget("Deer Lick");

        Assert.Equal(target.Id, _query.ActiveTargetIdByPrimaryName("Deer Lick"));
    }

    [Fact]
    public void ActiveTargetIdByPrimaryName_IsCaseInsensitive()
    {
        // Review escalation ruling: MergeRepository matches source_name with OrdinalIgnoreCase,
        // so a case-sensitive lookup here would send a Pass 2 row down the unresolved-name path.
        var target = NewTarget("Deer Lick");

        Assert.Equal(target.Id, _query.ActiveTargetIdByPrimaryName("deer lick"));
        Assert.Equal(target.Id, _query.ActiveTargetIdByPrimaryName("DEER LICK"));
        Assert.Equal(target.Id, _query.ActiveTargetIdByPrimaryName("dEeR lIcK"));
    }

    [Fact]
    public void ActiveTargetIdByPrimaryName_IgnoresAMergedAwayTargetAndAnUnresolvedName()
    {
        var survivor = NewTarget("NGC 7331");
        NewTarget("Deer Lick", target =>
        {
            target.MergedIntoId = survivor.Id;
            target.MergedAt = DateTime.UtcNow;
        });

        Assert.Null(_query.ActiveTargetIdByPrimaryName("Deer Lick"));
        Assert.Null(_query.ActiveTargetIdByPrimaryName("ngc7331 mosaic"));
        Assert.Null(_query.ActiveTargetIdByPrimaryName(""));
    }
}
