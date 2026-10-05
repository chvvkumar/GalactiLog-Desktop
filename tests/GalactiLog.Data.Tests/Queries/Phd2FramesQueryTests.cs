using System;
using System.Linq;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Tests.TestSupport;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// <see cref="Phd2FramesQuery"/>: the arcsecond conversion at read time, the null pixel scale as a
/// guard rather than an error, the one-session key and the stored event document.
/// </summary>
/// <remarks>
/// Rows are built in the database directly, as in <see cref="Phd2NightQueryTests"/> and for the
/// same reason.
/// </remarks>
public class Phd2FramesQueryTests
{
    private static readonly DateOnly Night = new(2025, 3, 19);

    private const string FourEvents =
        """
        [{"type":"dither","t":50.5,"detail":"dx=3.2 dy=-1.4"},
         {"type":"settle_done","t":55.5,"detail":"settle complete"},
         {"type":"star_lost","t":80.0,"detail":"low SNR"},
         {"type":"settle_failed","t":103.5,"detail":"timed out"}]
        """;

    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly Guid _logId = Guid.NewGuid();
        private int _added;

        public Library()
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            Query = new Phd2FramesQuery(new DatabaseConnectionString(_db.ConnectionString));

            using var context = Open();
            context.Phd2Logs.Add(new Phd2Log
            {
                Id = _logId,
                FilePath = @"C:\Astro\guide\PHD2_GuideLog_2025-03-19_213000.txt",
                FileSize = 1024,
                FileMtime = 1_700_000_000d,
                ParseStatus = "ok",
                RunCount = 1,
                SessionCount = 1,
                CalibrationCount = 0,
                ParsedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            });
            context.SaveChanges();
        }

        public Phd2FramesQuery Query { get; }

        public GalactiLogContext Open()
            => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

        public Guid AddSession(double? pixelScale, string events = "[]", bool zoned = true)
        {
            var id = Guid.NewGuid();
            var started = new DateTime(2025, 3, 19, 21, 0, 0, DateTimeKind.Utc).AddMinutes(_added++);

            using var context = Open();
            context.Phd2Sessions.Add(new Phd2Session
            {
                Id = id,
                LogId = _logId,
                RunIndex = 0,
                SectionIndex = _added,
                // Five hours off the UTC start, so a query that substituted the local wall clock
                // for a missing UTC one would be handing back a value the assertions can see.
                StartedAtLocal = DateTime.SpecifyKind(started.AddHours(-5), DateTimeKind.Unspecified),
                StartedAtUtc = zoned ? started : (DateTime?)null,
                EndedAtUtc = zoned ? started.AddMinutes(5) : (DateTime?)null,
                SessionDate = zoned ? Night : (DateOnly?)null,
                DurationS = 300,
                EquipmentProfile = "TestScope_TestCam",
                PixelScaleArcsec = pixelScale,
                FrameCount = 0,
                Events = events,
            });
            context.SaveChanges();
            return id;
        }

        public void AddFrames(Guid sessionId, int count, Action<Phd2Frame, int>? configure = null)
        {
            using var context = Open();
            for (var index = count - 1; index >= 0; index--)
            {
                // Inserted newest first, so a query that leaned on insertion order rather than on
                // time_offset would hand the graph a trace running backwards.
                var frame = new Phd2Frame
                {
                    SessionId = sessionId,
                    FrameIndex = index + 1,
                    TimeOffset = (index + 1) * 0.5,
                    RaRaw = 0.400000,
                    DecRaw = 0.300000,
                    RaDurationMs = 120,
                    RaDirection = "W",
                    DecDurationMs = 80,
                    DecDirection = "N",
                    Snr = 24.5,
                    StarMass = 12345.0,
                    Dropped = false,
                };
                configure?.Invoke(frame, index);
                context.Phd2Frames.Add(frame);
            }

            context.SaveChanges();
        }

        public void Dispose() => _db.Dispose();
    }

    /// <summary>
    /// A failure looks like: the conversion run against the session's focal length, or a frame read
    /// in the wrong session's scale on a night carrying two of them. The second session therefore
    /// stores identical pixel values at twice the scale, and the two answers have to differ.
    /// </summary>
    [Fact]
    public void Get_ConvertsEachFrameAtItsOwnSessionsPixelScale()
    {
        using var library = new Library();
        var fine = library.AddSession(1.50);
        var coarse = library.AddSession(3.00);
        library.AddFrames(fine, 1);
        library.AddFrames(coarse, 1);

        var fineFrame = Assert.Single(library.Query.Get(fine)!.Frames);
        var coarseFrame = Assert.Single(library.Query.Get(coarse)!.Frames);

        Assert.Equal(0.600000, fineFrame.Ra);
        Assert.Equal(0.450000, fineFrame.Dec);
        Assert.Equal(1.200000, coarseFrame.Ra);
        Assert.Equal(0.900000, coarseFrame.Dec);
    }

    [Fact]
    public void Get_NullsOneAxisOnlyWhenOnlyThatAxisIsMissing()
    {
        using var library = new Library();
        var session = library.AddSession(1.50);
        library.AddFrames(session, 1, (frame, _) => frame.RaRaw = null);

        var frame = Assert.Single(library.Query.Get(session)!.Frames);

        Assert.Null(frame.Ra);
        Assert.Equal(0.450000, frame.Dec);
    }

    /// <summary>
    /// A failure looks like: an empty frame list, a throw, or 1.0 substituted as a scale. Any of
    /// the three blanks the graph's no-scale notice, which needs the frames present and the scale
    /// null together to fire, so the frame count is asserted in the same case as the nulls.
    /// </summary>
    [Fact]
    public void Get_OnASessionWithNoPixelScale_ReportsItAndKeepsEveryOtherFieldOfEveryFrame()
    {
        using var library = new Library();
        var session = library.AddSession(null);
        library.AddFrames(session, 100, (frame, index) => frame.Dropped = index == 0);

        var answer = library.Query.Get(session);

        Assert.NotNull(answer);
        Assert.Null(answer.PixelScaleArcsec);
        Assert.Equal(100, answer.Frames.Count);
        Assert.All(answer.Frames, frame =>
        {
            Assert.Null(frame.Ra);
            Assert.Null(frame.Dec);
            Assert.True(frame.T > 0);
            Assert.Equal(120, frame.RaPulseMs);
            Assert.Equal("W", frame.RaDir);
            Assert.Equal(80, frame.DecPulseMs);
            Assert.Equal("N", frame.DecDir);
            Assert.Equal(24.5, frame.Snr);
            Assert.Equal(12345.0, frame.Mass);
        });

        // The drop flag is the one frame member with no distinguishing neighbour, and spec 12.4
        // draws a dot and prints "Star lost" from it, so it is stored true on exactly one frame.
        // A failure looks like false on every frame, which is a hardcoded literal in the
        // projection, or true on every frame, which is a crossed column.
        Assert.True(answer.Frames[0].Dropped);
        Assert.Single(answer.Frames, frame => frame.Dropped);
    }

    /// <summary>
    /// Ruling F1's unzoned session, which <see cref="Phd2NightQuery"/> cannot return because it
    /// selects on <c>session_date</c>, but which this query is keyed on the id for and can be asked
    /// about: <c>Phd2Correlation.RederiveSessionTimes</c> nulls <c>started_at_utc</c> and
    /// <c>session_date</c> together on a zone change while the band still holds the id.
    /// </summary>
    /// <remarks>
    /// A failure looks like <c>StartedAtUtc</c> reading the session's stored local wall clock, five
    /// hours off here. Spec 12.4 requires the range caption, the time axis and the hover heading to
    /// degrade to elapsed durations with no resolvable start rather than print a wrong clock time,
    /// and a substituted local clock makes that state unrepresentable at the seam. The frames and
    /// the pixel scale are asserted in the same case, because the graph still draws the trace.
    /// </remarks>
    [Fact]
    public void Get_OnAnUnzonedSession_ReportsANullStartAndNeverTheLocalWallClock()
    {
        using var library = new Library();
        var session = library.AddSession(1.50, zoned: false);
        library.AddFrames(session, 3);

        var answer = library.Query.Get(session);

        Assert.NotNull(answer);
        Assert.Null(answer.StartedAtUtc);
        Assert.Equal(3, answer.Frames.Count);
        Assert.Equal(1.50, answer.PixelScaleArcsec);

        // The local wall clock is still stored and still the one the correlation re-derives from;
        // it is simply not what this seam publishes.
        using var context = library.Open();
        var stored = context.Phd2Sessions.Single(s => s.Id == session);
        Assert.Equal(new DateTime(2025, 3, 19, 16, 0, 0, DateTimeKind.Unspecified), stored.StartedAtLocal);
        Assert.Null(stored.StartedAtUtc);
    }

    /// <summary>
    /// A failure looks like: the query reading every frame of the table and filtering in memory,
    /// which returns the right ten and would still be right on a library of 1.2 million rows at a
    /// cost no case would show. <see cref="TheSourceScan_ReadsFramesByTheSessionKey"/> pins the
    /// shape; this case pins the answer.
    /// </summary>
    [Fact]
    public void Get_ReturnsOnlyTheAskedSessionsFramesInTimeOrder()
    {
        using var library = new Library();
        var first = library.AddSession(1.50);
        var second = library.AddSession(1.50);
        library.AddFrames(first, 10, (frame, _) => frame.Snr = 11.0);
        library.AddFrames(second, 10, (frame, _) => frame.Snr = 22.0);

        var frames = library.Query.Get(first)!.Frames;

        Assert.Equal(10, frames.Count);
        Assert.All(frames, frame => Assert.Equal(11.0, frame.Snr));
        Assert.Equal(frames.Select(f => f.T).Order(), frames.Select(f => f.T));
        Assert.Equal(0.5, frames[0].T);
        Assert.Equal(5.0, frames[9].T);
    }

    [Fact]
    public void Get_OnAnUnknownSessionId_IsNull()
    {
        using var library = new Library();
        library.AddSession(1.50);

        Assert.Null(library.Query.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Get_OnASessionWithNoStoredFrame_IsAnEmptyListAndNotNull()
    {
        using var library = new Library();
        var session = library.AddSession(1.50);

        var answer = library.Query.Get(session);

        Assert.NotNull(answer);
        Assert.Empty(answer.Frames);
        Assert.Equal(1.50, answer.PixelScaleArcsec);
        DateTime? started = new DateTime(2025, 3, 19, 21, 0, 0, DateTimeKind.Utc);
        Assert.Equal(started, answer.StartedAtUtc);
    }

    /// <summary>
    /// A failure looks like: the detail string coming back empty, because the reader was copied
    /// from <c>Phd2Correlation.StoredEvents</c>, which yields type and time only and drops the
    /// detail the graph prints.
    /// </summary>
    [Fact]
    public void Get_ReadsTheStoredEventDocumentWholeAndInStoredOrder()
    {
        using var library = new Library();
        var session = library.AddSession(1.50, FourEvents);

        var events = library.Query.Get(session)!.Events;

        Assert.Equal(4, events.Count);
        Assert.Equal(["dither", "settle_done", "star_lost", "settle_failed"], events.Select(e => e.Type));
        Assert.Equal([50.5, 55.5, 80.0, 103.5], events.Select(e => e.TimeOffset));
        Assert.Equal("dx=3.2 dy=-1.4", events[0].Detail);
        Assert.Equal("timed out", events[3].Detail);
    }

    /// <summary>
    /// The one behaviour that changed when the two readers of the stored <c>events</c> document
    /// became one (task2-review.md P3-7, ruled in phase-review.md section 5): a document with one
    /// STRUCTURALLY malformed entry now yields nothing at all rather than the good entries around
    /// it, on this path and on the correlation's alike.
    /// </summary>
    /// <remarks>
    /// Pinned so the change is a decision rather than a drift. The survivor is the strict round
    /// trip of the ingest's own write, which is lenient per FIELD as the web is, so a missing
    /// <c>type</c> or <c>detail</c> still reads as the empty string; it is structure it will not
    /// guess at. The document is unreachable except by hand editing the database, since the
    /// ingest writes it and the column is required.
    /// <para>
    /// The correlation's half of this pair is
    /// <c>Phd2CorrelationTests.AMalformedEventsDocument_ExcludesNoWindowAtAll</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Get_OnADocumentWithOneMalformedEntry_IsAnEmptyList()
    {
        using var library = new Library();
        var session = library.AddSession(
            1.50,
            """[{"type":"dither","t":50.5,"detail":""},{"type":"settle_done","t":"oops"}]""");

        // The lenient JsonDocument walk this replaced kept the first entry and skipped the
        // second, so the two paths disagreed about what one document said.
        Assert.Empty(library.Query.Get(session)!.Events);
    }

    /// <summary>A missing <c>type</c> or <c>detail</c> is a FIELD the reader defaults, not a
    /// structure it refuses: the entry survives with the empty string, which is the web's own
    /// default (<c>api/phd2.py:152-154</c>).</summary>
    [Fact]
    public void Get_OnAnEntryMissingItsTypeOrDetail_KeepsTheEntryWithEmptyStrings()
    {
        using var library = new Library();
        var session = library.AddSession(1.50, """[{"t":50.5}]""");

        var single = Assert.Single(library.Query.Get(session)!.Events);

        Assert.Equal("", single.Type);
        Assert.Equal("", single.Detail);
        Assert.Equal(50.5, single.TimeOffset);
    }

    [Fact]
    public void Get_OnASessionWithNoEvent_IsAnEmptyList()
    {
        using var library = new Library();
        var session = library.AddSession(1.50);

        Assert.Empty(library.Query.Get(session)!.Events);
    }

    /// <summary>
    /// A failure looks like: the whole read throwing because one session's event document did not
    /// round trip, which would blank the graph rather than its event markers.
    /// </summary>
    [Fact]
    public void Get_OnAnUnreadableEventDocument_IsAnEmptyListAndDoesNotThrow()
    {
        using var library = new Library();
        var session = library.AddSession(1.50, "not json");
        library.AddFrames(session, 3);

        var answer = library.Query.Get(session);

        Assert.NotNull(answer);
        Assert.Empty(answer.Events);
        Assert.Equal(3, answer.Frames.Count);
    }

    /// <summary>
    /// A failure looks like: the frame statement materialising the table and filtering in memory.
    /// The answer would be identical and the cost would not, so the shape is pinned in the source
    /// as well as in <see cref="Get_ReturnsOnlyTheAskedSessionsFramesInTimeOrder"/>.
    /// </summary>
    [Fact]
    public void TheSourceScan_ReadsFramesByTheSessionKey()
    {
        var code = SourceScan.Read("src/GalactiLog.Data/Queries/Phd2FramesQuery.cs");

        // Phd2Events.Read is the one reader of the stored events document; this file must not
        // hold a second one (task2-review.md P3-7).
        Assert.Contains("Phd2Events.Read(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer", code, StringComparison.Ordinal);

        Assert.Contains("f.SessionId == sessionId", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AsEnumerable", code, StringComparison.Ordinal);

        var statement = code[code.IndexOf("context.Phd2Frames", StringComparison.Ordinal)..];
        var filtered = statement.IndexOf("f.SessionId == sessionId", StringComparison.Ordinal);
        var materialised = statement.IndexOf("ToList()", StringComparison.Ordinal);
        Assert.True(
            filtered >= 0 && filtered < materialised,
            "The frame statement must filter on session_id before it materialises anything.");

        // The one arcsecond conversion in the port, and no second multiply and round beside it.
        Assert.Contains("Phd2Metrics.ToArcsec", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Round", code, StringComparison.Ordinal);

        // Reads only (spec 5.1, single writer), and no file system.
        Assert.DoesNotContain("ExecuteUpdate", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteDelete", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO", code, StringComparison.Ordinal);
    }
}
