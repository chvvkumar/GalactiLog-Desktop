using System;
using System.Collections.Generic;
using System.Linq;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Phd2;

/// <summary>
/// One case per <see cref="Phd2Repository"/> member. The repository speaks entities only: nothing
/// here parses a file, and nothing here names a <c>GalactiLog.Core.Phd2</c> type.
/// </summary>
public class Phd2RepositoryTests
{
    private const string Root = @"C:\Astro\guide";

    private static Phd2Repository Repository(TestDatabaseHandle database)
        => new(database.ConnectionString);

    private static string PathAt(int index) => $@"{Root}\PHD2_GuideLog_{index:D4}.txt";

    /// <summary>
    /// The four guide-log row counts, read here rather than through a repository member.
    /// </summary>
    /// <remarks>
    /// <c>Phd2Repository.Counts</c> was a public member with no production caller, reached only by
    /// the assertions below (phase review set B item 3). Diagnostics reads its own
    /// <c>RowCountStatements</c>, which also carries spec 12.8's display order, so the repository
    /// member was a second counting path that existed for its own tests. Counting through a
    /// context here leaves one.
    /// </remarks>
    private static (long Logs, long Sessions, long Frames, long Calibrations) RowCounts(
        TestDatabaseHandle database)
    {
        using var context = Phd2SchemaTests.Open(database);
        return (
            context.Phd2Logs.LongCount(),
            context.Phd2Sessions.LongCount(),
            context.Phd2Frames.LongCount(),
            context.Phd2Calibrations.LongCount());
    }

    private static void SeedLogs(TestDatabaseHandle database, int count, string? root = null)
    {
        using var context = Phd2SchemaTests.Open(database);
        for (var index = 0; index < count; index++)
        {
            var path = root is null ? PathAt(index) : $@"{root}\PHD2_GuideLog_{index:D4}.txt";
            var log = Phd2SchemaTests.Log(Guid.NewGuid(), path);
            log.FileSize = 1000 + index;
            log.FileMtime = 1_700_000_000d + index;
            context.Phd2Logs.Add(log);
        }

        context.SaveChanges();
    }

    [Fact]
    public void StoredFiles_ReturnsThePathSizeAndMtimeOfEveryStoredLog()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedLogs(database, 3);

        var stored = Repository(database).StoredFiles()
            .OrderBy(file => file.FilePath, StringComparer.Ordinal)
            .ToList();

        // The three the delta skip of spec 10.3 step 2 compares, and nothing else: a triple with a
        // null size would re-ingest a file that has not changed.
        Assert.Equal(3, stored.Count);
        Assert.Equal(PathAt(0), stored[0].FilePath);
        Assert.Equal(1000, stored[0].FileSize);
        Assert.Equal(1_700_000_000d, stored[0].FileMtime);
    }

    [Fact]
    public void StoredFiles_OnAnEmptyLibrary_IsEmpty()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        Assert.Empty(Repository(database).StoredFiles());
    }

    [Fact]
    public void StoredPathsUnder_ReturnsOnlyThePathsUnderTheWalkedRoots()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedLogs(database, 2);
        SeedLogs(database, 2, root: @"D:\Unplugged\guide");

        var under = Repository(database).StoredPathsUnder([Root]);

        // A row under a root that is not being walked is not an orphan: it belongs to a library
        // the user unplugged, and dropping it would delete that library's guiding history.
        Assert.Equal(2, under.Count);
        Assert.All(under, path => Assert.StartsWith(Root, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StoredPathsUnder_MatchesARootCaseInsensitively_AndNotASiblingWithTheSamePrefix()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedLogs(database, 1);
        SeedLogs(database, 1, root: Root + "2");

        var under = Repository(database).StoredPathsUnder([Root.ToUpperInvariant()]);

        // Windows paths are case-insensitive, so the root matches whatever its spelling. The
        // trailing separator is what keeps C:\Astro\guide2 out of C:\Astro\guide.
        Assert.Equal(PathAt(0), Assert.Single(under));
    }

    [Fact]
    public void StoredPathsUnder_WithNoRoots_IsEmpty()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedLogs(database, 2);

        // Not "every row": a run with no roots has walked nothing, and reading it as "everything
        // is missing" is how an orphan drop empties a library.
        Assert.Empty(Repository(database).StoredPathsUnder([]));
    }

    [Fact]
    public void DeleteByPath_RemovesTheLogAndEverythingUnderIt()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var logId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        using (var context = Phd2SchemaTests.Open(database))
        {
            context.Phd2Logs.Add(Phd2SchemaTests.Log(logId, PathAt(0)));
            context.Phd2Sessions.Add(Phd2SchemaTests.Session(sessionId, logId));
            context.Phd2Frames.Add(Phd2SchemaTests.Frame(sessionId, 1));
            context.Phd2Calibrations.Add(Phd2SchemaTests.Calibration(logId));
            context.SaveChanges();
        }

        var removed = Repository(database).DeleteByPath(PathAt(0));

        Assert.Equal(1, removed);
        Assert.Equal((0L, 0L, 0L, 0L), RowCounts(database));
    }

    [Fact]
    public void DeleteByPath_ForAPathThatIsNotStored_RemovesNothing()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedLogs(database, 1);

        Assert.Equal(0, Repository(database).DeleteByPath(@"C:\Astro\guide\absent.txt"));
        Assert.Equal(1, RowCounts(database).Logs);
    }

    /// <summary>
    /// The batch delete crosses the 500 row boundary.
    /// </summary>
    /// <remarks>
    /// A failure looks like only the first 500 rows being deleted while the pass reports a removal
    /// count that does not match what is gone, which reads as a database fault rather than as an
    /// off-by-one in a chunk loop.
    /// </remarks>
    [Fact]
    public void DeleteByPaths_AcrossTheBatchBoundary_DeletesEveryRowAndCountsThemAll()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        const int count = 600;
        SeedLogs(database, count);
        Assert.True(count > OrphanPruner.DeleteBatchSize);

        var paths = Enumerable.Range(0, count).Select(PathAt).ToList();
        var removed = Repository(database).DeleteByPaths(paths);

        Assert.Equal(count, removed);
        Assert.Equal(0, RowCounts(database).Logs);
    }

    [Fact]
    public void DeleteByPaths_WithNoPaths_RemovesNothing()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedLogs(database, 2);

        Assert.Equal(0, Repository(database).DeleteByPaths([]));
        Assert.Equal(2, RowCounts(database).Logs);
    }

    [Fact]
    public void Insert_WritesTheLogItsSessionsFramesAndCalibrations()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var logId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        Repository(database).Insert(
            Phd2SchemaTests.Log(logId, PathAt(0)),
            [Phd2SchemaTests.Session(sessionId, logId)],
            [Phd2SchemaTests.Frame(sessionId, 1), Phd2SchemaTests.Frame(sessionId, 2)],
            [Phd2SchemaTests.Calibration(logId)]);

        Assert.Equal((1L, 1L, 2L, 1L), RowCounts(database));
    }

    [Fact]
    public void Insert_ForAPathAlreadyStored_ReplacesItRatherThanDuplicatingIt()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var firstLog = Guid.NewGuid();
        var firstSession = Guid.NewGuid();
        var repository = Repository(database);
        repository.Insert(
            Phd2SchemaTests.Log(firstLog, PathAt(0)),
            [Phd2SchemaTests.Session(firstSession, firstLog)],
            [Phd2SchemaTests.Frame(firstSession, 1)],
            []);

        var secondLog = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        repository.Insert(
            Phd2SchemaTests.Log(secondLog, PathAt(0)),
            [Phd2SchemaTests.Session(secondSession, secondLog)],
            [Phd2SchemaTests.Frame(secondSession, 1), Phd2SchemaTests.Frame(secondSession, 2)],
            []);

        // Spec 5.15's unique path: a changed file re-parsed is one row, not two, and the old
        // session's frames go with it rather than lingering in the volume table.
        Assert.Equal((1L, 1L, 2L, 0L), RowCounts(database));
        using var context = Phd2SchemaTests.Open(database);
        Assert.Equal(secondLog, Assert.Single(context.Phd2Logs).Id);
    }

    [Fact]
    public void Insert_WhenAChildRowIsBad_WritesNothingAtAll()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var logId = Guid.NewGuid();

        // A frame whose session does not exist. Spec 10.3 step 3: one file is one transaction, so
        // a half-written log is not a state this path may leave behind. The exception type is
        // pinned rather than left as ThrowsAny<Exception>, which would accept a failure for the
        // wrong reason: this must be the foreign key refusing the frame (review P3-2).
        Assert.ThrowsAny<DbUpdateException>(() => Repository(database).Insert(
            Phd2SchemaTests.Log(logId, PathAt(0)),
            [],
            [Phd2SchemaTests.Frame(Guid.NewGuid(), 1)],
            []));

        Assert.Equal((0L, 0L, 0L, 0L), RowCounts(database));
    }

    /// <summary>
    /// A real desktop guide log is hundreds of thousands of frame rows. They are written in chunks
    /// and the change tracker is emptied between them, so its peak is the chunk size rather than
    /// the size of the log.
    /// </summary>
    /// <remarks>
    /// A failure looks like: one guide log of a few tens of megabytes is added to one tracking
    /// context whole, the change tracker reaches gigabytes, and the pass thrashes rather than
    /// failing, with one progress envelope per file so the application looks hung (phase review
    /// P2-1). The tracker is watched through <c>Phd2Repository.InsertFrames</c>, the one
    /// declaration <c>Insert</c> itself runs, because <c>Insert</c> owns its context and nothing
    /// can reach inside it.
    /// </remarks>
    [Fact]
    public void InsertFrames_HoldsAtMostOneChunkInTheChangeTracker()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        const int count = 50_000;
        var logId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        using var context = Phd2SchemaTests.Open(database);
        context.Phd2Logs.Add(Phd2SchemaTests.Log(logId, PathAt(0)));
        context.Phd2Sessions.Add(Phd2SchemaTests.Session(sessionId, logId));
        context.SaveChanges();
        context.ChangeTracker.Clear();

        // Sampled rather than measured on every entity: Entries() is a walk of the tracker, so
        // reading it 50,000 times would be quadratic. A sample every 500 rows cannot miss a
        // tracker that is never emptied, which is the failure being refused, and the peak is read
        // at the tracker's own high-water mark inside a chunk.
        var peak = 0;
        var tracked = 0;
        context.ChangeTracker.Tracked += (_, _) =>
        {
            if (++tracked % 500 == 0)
            {
                peak = Math.Max(peak, context.ChangeTracker.Entries().Count());
            }
        };

        var frames = Enumerable.Range(1, count)
            .Select(index => Phd2SchemaTests.Frame(sessionId, index))
            .ToList();
        Phd2Repository.InsertFrames(context, frames);

        Assert.Equal(count, RowCounts(database).Frames);
        Assert.True(count > Phd2Repository.FrameBatchSize);
        Assert.InRange(peak, 1, Phd2Repository.FrameBatchSize);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    /// <summary>
    /// Chunking the frame write does not cost the atomicity of spec 10.3 step 3: a throw in a later
    /// chunk leaves no row of that log at all, the chunks already saved included.
    /// </summary>
    /// <remarks>
    /// A failure looks like: a guide log whose last section is malformed leaves the first thousands
    /// of its frames stored under a log row that is rolled back, or under one that is not, so the
    /// catalogue holds half a file and the delta skip never re-reads it.
    /// </remarks>
    [Fact]
    public void Insert_WhenAFrameInALaterChunkIsBad_WritesNoRowOfThatLog()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var logId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var frames = Enumerable.Range(1, Phd2Repository.FrameBatchSize + 1000)
            .Select(index => Phd2SchemaTests.Frame(sessionId, index))
            .ToList();

        // The bad row is past the first chunk boundary, so the first chunk has already been saved
        // inside the transaction when the foreign key refuses this one.
        frames[^1].SessionId = Guid.NewGuid();

        Assert.ThrowsAny<DbUpdateException>(() => Repository(database).Insert(
            Phd2SchemaTests.Log(logId, PathAt(0)),
            [Phd2SchemaTests.Session(sessionId, logId)],
            frames,
            []));

        Assert.Equal((0L, 0L, 0L, 0L), RowCounts(database));
    }
}
