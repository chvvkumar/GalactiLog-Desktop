using System;
using System.Linq;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Section 8.6 of the Phase 15A Task 6 brief: the read behind spec 12.7's PHD2 profiles panel.
public class Phd2ProfilesQueryTests
{
    private static Phd2ProfilesQuery Query(TestDatabaseHandle database)
        => new(new DatabaseConnectionString(database.ConnectionString));

    private static Phd2Log NewLog(GalactiLogContext context)
    {
        var log = new Phd2Log
        {
            Id = Guid.NewGuid(),
            FilePath = $@"C:\logs\{Guid.NewGuid():N}.txt",
            ParseStatus = "ok",
            ParsedAt = DateTime.UtcNow,
        };
        context.Phd2Logs.Add(log);
        return log;
    }

    private static Phd2Session NewSession(
        Phd2Log log,
        string? profile,
        string? guideCamera,
        double? focalLengthMm,
        double? pixelScaleArcsec,
        DateTime? startedAtUtc,
        DateTime startedAtLocal)
        => new()
        {
            Id = Guid.NewGuid(),
            LogId = log.Id,
            RunIndex = 0,
            SectionIndex = 0,
            StartedAtLocal = startedAtLocal,
            StartedAtUtc = startedAtUtc,
            DurationS = 60,
            EquipmentProfile = profile,
            GuideCamera = guideCamera,
            FocalLengthMm = focalLengthMm,
            PixelScaleArcsec = pixelScaleArcsec,
        };

    [Fact]
    public void Read_OrdersByLastSeenAndTakesTheMostRecentSessionsFacts()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            var log = NewLog(context);

            // "Rig A": two sessions, the older one with a retired camera, the newer with the
            // current one. Newest first over the whole result also depends on this profile's
            // last seen beating "Rig B"'s.
            context.Phd2Sessions.Add(NewSession(
                log, "Rig A", "Retired Camera", 500, 1.1,
                new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 20, 0, 0)));
            context.Phd2Sessions.Add(NewSession(
                log, "Rig A", "Current Camera", 500, 1.2,
                new DateTime(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 1, 20, 0, 0)));

            // "Rig B": one session, seen before Rig A's most recent one.
            context.Phd2Sessions.Add(NewSession(
                log, "Rig B", "Other Camera", 800, 0.8,
                new DateTime(2026, 2, 1, 2, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 20, 0, 0)));

            context.SaveChanges();
        }

        var rows = Query(database).Read();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Rig A", rows[0].Profile);
        Assert.Equal("Rig B", rows[1].Profile);

        var rigA = rows[0];
        Assert.Equal("Current Camera", rigA.GuideCamera);
        Assert.Equal(500, rigA.FocalLengthMm);
        Assert.Equal(1.2, rigA.PixelScaleArcsec);
        Assert.Equal(2, rigA.SessionCount);
        Assert.Equal(new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc), rigA.FirstSeen);
        Assert.Equal(new DateTime(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc), rigA.LastSeen);
    }

    [Fact]
    public void Read_AProfileWithNoResolvedZoneOnAnySession_StillGetsFirstAndLastSeenFromTheLocalClock()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            var log = NewLog(context);
            context.Phd2Sessions.Add(NewSession(
                log, "Rig C", "Camera", 500, 1.0,
                startedAtUtc: null, startedAtLocal: new DateTime(2026, 4, 1, 21, 0, 0)));
            context.Phd2Sessions.Add(NewSession(
                log, "Rig C", "Camera", 500, 1.0,
                startedAtUtc: null, startedAtLocal: new DateTime(2026, 4, 3, 21, 0, 0)));
            context.SaveChanges();
        }

        var row = Assert.Single(Query(database).Read());

        Assert.Equal("Rig C", row.Profile);
        Assert.Equal(new DateTime(2026, 4, 1, 21, 0, 0), row.FirstSeen);
        Assert.Equal(new DateTime(2026, 4, 3, 21, 0, 0), row.LastSeen);
        Assert.True(row.FirstSeenIsLogClock);
        Assert.True(row.LastSeenIsLogClock);
    }

    // Fix-wave review P2-1. The fallback is decided per session here, so the basis of each value
    // is known here and nowhere else: a reader that re-derived it from whatever zone resolves at
    // the moment it renders would convert a guide log's naive wall clock as though it were UTC the
    // instant a user set a zone, long before any re-derive had rewritten the stored row.
    [Fact]
    public void Read_AProfileMixingAZonedAndAnUnzonedSession_ReportsEachSeenValuesOwnBasis()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            var log = NewLog(context);

            // The earliest value comes from the unzoned session's local clock, the latest from the
            // zoned session's resolved instant, so the two halves of one row disagree on basis.
            context.Phd2Sessions.Add(NewSession(
                log, "Rig D", "Camera", 500, 1.0,
                startedAtUtc: null, startedAtLocal: new DateTime(2026, 5, 1, 21, 0, 0)));
            context.Phd2Sessions.Add(NewSession(
                log, "Rig D", "Camera", 500, 1.0,
                new DateTime(2026, 6, 1, 2, 0, 0, DateTimeKind.Utc), new DateTime(2026, 5, 31, 21, 0, 0)));
            context.SaveChanges();
        }

        var row = Assert.Single(Query(database).Read());

        Assert.Equal(new DateTime(2026, 5, 1, 21, 0, 0), row.FirstSeen);
        Assert.True(row.FirstSeenIsLogClock);
        Assert.Equal(new DateTime(2026, 6, 1, 2, 0, 0, DateTimeKind.Utc), row.LastSeen);
        Assert.False(row.LastSeenIsLogClock);
    }

    [Fact]
    public void Read_ANullPixelScale_ComesBackNull()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            var log = NewLog(context);
            context.Phd2Sessions.Add(NewSession(
                log, "ASIAIR Rig", "ASIAIR Guide Camera", null, null,
                new DateTime(2026, 5, 1, 2, 0, 0, DateTimeKind.Utc), new DateTime(2026, 5, 1, 20, 0, 0)));
            context.SaveChanges();
        }

        var row = Assert.Single(Query(database).Read());

        Assert.Null(row.PixelScaleArcsec);
        Assert.Null(row.FocalLengthMm);
    }

    [Fact]
    public void Read_ASessionWithNoEquipmentProfile_IsItsOwnRowKeyedByTheEmptyString()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            var log = NewLog(context);
            context.Phd2Sessions.Add(NewSession(
                log, null, "Camera", 500, 1.0,
                new DateTime(2026, 6, 1, 2, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 1, 20, 0, 0)));
            context.SaveChanges();
        }

        var row = Assert.Single(Query(database).Read());

        Assert.Equal("", row.Profile);
    }

    [Fact]
    public void Read_OnAnEmptyDatabase_ReturnsNoRows()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        Assert.Empty(Query(database).Read());
    }
}
