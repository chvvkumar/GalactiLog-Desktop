using System.Text;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Sessions;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 10.3 step 5, the guide-log pass: the delta skip, the failure records, the key, the three
// counters and the once-per-pass warnings of spec 10.9. This is tests/**, so writing fixture
// files here is fine; nothing the pass itself runs writes a byte to disk.
public class Phd2IngestTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-phd2ingest-").FullName;

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // --- fixtures -------------------------------------------------------------------------
    // The CSV header is compared byte for byte by the parser (spec 7.6), so it is written out in
    // full rather than assembled.

    private const string GuideCsvHeader =
        "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance," +
        "RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode";

    private const string DesktopLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2025-03-19 21:30:00
        Guiding Begins at 2025-03-19 21:31:00
        Equipment Profile = TestRig
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        Mount = EQ6-R Pro, xAngle = 90.0, xRate = 3.000, yAngle = 0.0, yRate = 3.000, parity = +/+
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        2,1.000,"Mount",0.10,0.20,-0.400,-0.300,0.100,0.100,10,E,12,S,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-19 21:31:02
        Log Summary: calcnt:0 gcnt:1 gdur:2 gacnt:2
        Log closed at 2025-03-19 21:32:00

        """;

    // Three sections, each naming a profile and none carrying a Pixel scale line: the ASIAIR
    // shape, and the fixture spec 10.9's three once-per-pass warnings are counted over.
    private static readonly string NoPixelScaleLog = BuildNoPixelScaleLog();

    private static string BuildNoPixelScaleLog()
    {
        var text = new StringBuilder();
        text.AppendLine("PHD2 version, Log version 2.5. Log enabled at 2025-02-13 21:03:00");
        var profiles = new[] { "RigA", "RigB", "RigA" };
        for (var i = 0; i < profiles.Length; i++)
        {
            text.AppendLine($"Guiding Begins at 2025-02-13 21:0{i + 3}:00");
            text.AppendLine($"Equipment Profile = {profiles[i]}");
            text.AppendLine("Camera = ASI120MM");
            text.AppendLine("Exposure = 500 ms");
            text.AppendLine(GuideCsvHeader);
            text.AppendLine("1,0.500,\"Mount\",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0");
            text.AppendLine($"Guiding Ends at 2025-02-13 21:0{i + 3}:02");
        }

        text.AppendLine("Log closed at 2025-02-13 21:10:00");
        return text.ToString();
    }

    // Two sections, the first closed by a Guiding Ends line and the second running off the end of
    // the file, which is spec 5.16's truncated section: no end line, so no ended_at_local.
    private const string TwoSectionLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2025-03-19 21:30:00
        Guiding Begins at 2025-03-19 21:31:00
        Equipment Profile = TestRig
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        2,1.000,"Mount",0.10,0.20,-0.400,-0.300,0.100,0.100,10,E,12,S,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-19 21:31:02
        Guiding Begins at 2025-03-19 22:05:00
        Equipment Profile = TestRig
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0

        """;

    // The same shape with no Equipment Profile line at all, which is every ASIAIR log.
    private const string NoProfileLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2025-03-19 21:30:00
        Guiding Begins at 2025-03-19 21:31:00
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-19 21:31:02
        Log closed at 2025-03-19 21:32:00

        """;

    // Four sections around a spring-forward transition, in wall clock. Sections 1 and 3 sit a day
    // either side of it, so step 3 reads a different offset for each and any call site that took
    // the offset once for the whole pass is caught. Section 2 starts before the gap and ends after
    // it, so its two wall clocks convert under different offsets. Section 4 is TRUNCATED, with no
    // Guiding Ends line and nothing after it, which is the only shape that makes the wall-clock
    // FIELD the call site reads load bearing: it has a start and no end at all.
    private const string DstBoundaryLog = $"""
        PHD2 version 2.6.13 [Windows], Log version 2.5. Log enabled at 2025-03-08 20:00:00
        Guiding Begins at 2025-03-08 21:00:00
        Equipment Profile = TestRig
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-08 21:00:02
        Guiding Begins at 2025-03-09 01:30:00
        Equipment Profile = TestRig
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-09 03:30:00
        Guiding Begins at 2025-03-09 21:00:00
        Equipment Profile = TestRig
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0
        Guiding Ends at 2025-03-09 21:00:02
        Guiding Begins at 2025-03-09 22:10:00
        Equipment Profile = TestRig
        Pixel scale = 1.50 arc-sec/px, Binning = 1, Focal length = 500 mm
        Camera = Test Camera
        Exposure = 500 ms
        {GuideCsvHeader}
        1,0.500,"Mount",0.10,0.20,0.400,0.300,0.100,0.100,10,W,12,N,0,0,12000.0,25.0,0

        """;

    // A file the parser recognises as a guide log (the banner marker) and cannot place (the
    // platform tag has lost its brackets), which is spec 7.6's `unreadable`.
    private const string UnreadableLog =
        "PHD2 version 2.6.13 Windows, Log version 2.5. Log enabled at 2025-03-19 21:30:00\n";

    // No marker line anywhere, so the parser yields no run and throws nothing: spec 5.15's
    // `empty`.
    private const string EmptyLog = "These are my observing notes.\nNothing to see here.\n";

    // --- harness --------------------------------------------------------------------------

    private static GeneralSettings Settings(bool enabled = true, string timezone = "")
        => new()
        {
            Phd2ScanEnabled = enabled,
            ObserverTimezone = timezone,
            ScanFilters = ScanFilterConfig.Empty,
        };

    private string WriteLog(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static DiscoveredFile Stat(string path)
    {
        var info = new FileInfo(path);
        return new DiscoveredFile(
            path, info.Length, (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
    }

    private Phd2PassResult Run(
        IReadOnlyList<DiscoveredFile> candidates,
        GeneralSettings? general = null,
        bool force = false,
        List<ScanProgress>? envelopes = null)
        => Phd2Ingest.Run(
            _db.ConnectionString, candidates, general ?? Settings(), [_root], force,
            parentActivityId: null,
            envelopes is null
                ? null
                : (step, total, message, _) =>
                    envelopes.Add(new ScanProgress(ScanTaskNames.Phd2Ingest, step, total, message)),
            warn: null,
            CancellationToken.None);

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private static JsonElement Details(GalactiLogContext context, string eventType)
    {
        var row = context.ActivityEvents.Single(e => e.EventType == eventType);
        return JsonDocument.Parse(row.Details!).RootElement.Clone();
    }

    // --- 8.2 the delta skip -----------------------------------------------------------------

    [Fact]
    public void ADeltaSkip_WithinTheTolerance_DoesNotOpenTheFile()
    {
        var path = WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        var first = Stat(path);
        Assert.Equal(1, Run([first]).Ingested);

        DateTime parsedAt;
        using (var context = OpenRead())
        {
            parsedAt = context.Phd2Logs.Single().ParsedAt;
        }

        // Half a second away, which is inside FileWalker.MtimeToleranceSeconds. A failure here
        // means every rescan re-parses the whole guide-log corpus, which on a real library is the
        // slowest thing in the scan and produces no new data.
        var nudged = first with { FileMtimeUnixSeconds = first.FileMtimeUnixSeconds + 0.5 };
        var result = Run([nudged]);

        Assert.Equal(1, result.Found);
        Assert.Equal(0, result.Ingested);
        Assert.Equal(1, result.SkippedUnchanged);
        using var after = OpenRead();
        Assert.Equal(parsedAt, after.Phd2Logs.Single().ParsedAt);
    }

    [Fact]
    public void AnMtimeBeyondTheTolerance_IsReIngested()
    {
        var path = WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        var first = Stat(path);
        Run([first]);

        // Two seconds away, past the tolerance. A failure here means an edited or re-written log
        // is never re-read and its stored sessions stay frozen at the old content.
        var moved = first with { FileMtimeUnixSeconds = first.FileMtimeUnixSeconds + 2.0 };
        var result = Run([moved]);

        Assert.Equal(1, result.Found);
        Assert.Equal(1, result.Ingested);
        Assert.Equal(0, result.SkippedUnchanged);

        // Replaced, not duplicated: one file is one transaction and the path is the identity.
        using var context = OpenRead();
        Assert.Equal(1, context.Phd2Logs.Count());
        Assert.Equal(1, context.Phd2Sessions.Count());
        Assert.Equal(2, context.Phd2Frames.Count());
    }

    [Fact]
    public void AnIngestedLog_StoresItsSessionsFramesAndTheParsedColumns()
    {
        var path = WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);

        Assert.Equal(1, Run([Stat(path)]).Ingested);

        using var context = OpenRead();
        var log = context.Phd2Logs.Single();
        Assert.Equal("ok", log.ParseStatus);
        Assert.Null(log.ParseError);
        Assert.Equal("2.6.13", log.Phd2Version);
        Assert.Equal("2.5", log.LogVersion);
        Assert.Equal(1, log.RunCount);
        Assert.Equal(1, log.SessionCount);
        Assert.Equal(0, log.CalibrationCount);

        var session = context.Phd2Sessions.Single();
        Assert.Equal(log.Id, session.LogId);
        Assert.Equal(0, session.RunIndex);
        Assert.Equal(0, session.SectionIndex);
        Assert.Equal(new DateTime(2025, 3, 19, 21, 31, 0), session.StartedAtLocal);
        Assert.Equal("TestRig", session.EquipmentProfile);
        Assert.Equal(1.5, session.PixelScaleArcsec);
        Assert.Equal(2, session.FrameCount);
        Assert.Equal("{}", session.StarLostReasons);
        Assert.Equal("[]", session.Events);

        // Ruling F1: no zone was configured, so the session keeps its wall clock and carries no
        // UTC instant at all rather than borrowing the machine's zone.
        Assert.Null(session.StartedAtUtc);
        Assert.Null(session.SessionDate);

        var frames = context.Phd2Frames.OrderBy(f => f.FrameIndex).ToList();
        Assert.Equal(2, frames.Count);
        Assert.Equal(0.4, frames[0].RaRaw);
        Assert.Equal("W", frames[0].RaDirection);
        Assert.All(frames, frame => Assert.Equal(session.Id, frame.SessionId));
    }

    /// <summary>
    /// Spec 5.16's <c>ended_at_local</c>: the wall clock of the <c>Guiding Ends</c> line, stored
    /// exactly as written and never converted, and null for a section that has no end line.
    /// </summary>
    /// <remarks>
    /// A failure looks like the value never leaving the parser, because <c>ToSession</c> maps from
    /// <c>Phd2SessionMetrics</c> and that record does not carry it: the column exists, every row is
    /// null, the re-derive of spec 7.6 falls into its null branch forever, and carried item 54 is
    /// not fixed at all while every other case stays green.
    /// </remarks>
    [Fact]
    public void AnIngestedSection_StoresTheWallClockOfItsGuidingEndsLine()
    {
        var path = WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", TwoSectionLog);

        Assert.Equal(1, Run([Stat(path)]).Ingested);

        using var context = OpenRead();
        var sessions = context.Phd2Sessions.OrderBy(s => s.SectionIndex).ToList();
        Assert.Equal(2, sessions.Count);

        Assert.Equal(new DateTime(2025, 3, 19, 21, 31, 2), sessions[0].EndedAtLocal);
        Assert.Equal(DateTimeKind.Unspecified, sessions[0].EndedAtLocal!.Value.Kind);
        Assert.False(sessions[0].Truncated);

        // No Guiding Ends line, so there is no end to store and none is invented.
        Assert.Null(sessions[1].EndedAtLocal);
        Assert.True(sessions[1].Truncated);
    }

    // --- 8.3 failure records ----------------------------------------------------------------

    [Fact]
    public void AnUnreadableLog_WritesTheStatusTheMessageAndOnePhd2LogFailed()
    {
        var path = WriteLog("PHD2_GuideLog_bad.txt", UnreadableLog);
        var candidate = Stat(path);

        var result = Run([candidate]);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Ingested);
        using (var context = OpenRead())
        {
            var log = context.Phd2Logs.Single();
            Assert.Equal("unreadable", log.ParseStatus);
            Assert.False(string.IsNullOrEmpty(log.ParseError));

            var details = Details(context, "phd2_log_failed");
            Assert.Equal(path, details.GetProperty("path").GetString());
            Assert.Equal("unreadable", details.GetProperty("parse_status").GetString());
            Assert.False(string.IsNullOrEmpty(details.GetProperty("reason").GetString()));
        }

        // The row is what stops the re-read. A failure here means the pass re-reads and re-fails
        // the same file on every scan, and the Activity feed reports the same warning forever.
        var second = Run([candidate]);
        Assert.Equal(1, second.Found);
        Assert.Equal(1, second.SkippedUnchanged);
        Assert.Equal(0, second.Failed);
    }

    [Fact]
    public void AReadThatThrows_WritesFailedAndThePassContinues()
    {
        // A file the walk stated and that is gone by the time the pass opens it. This is the
        // only reachable shape of "the read or the parse threw something other than
        // Phd2UnreadableLogException": every parse path in the parser is TryParse-based.
        var vanished = WriteLog("PHD2_GuideLog_vanished.txt", DesktopLog);
        var vanishedCandidate = Stat(vanished);
        var good = WriteLog("PHD2_GuideLog_good.txt", DesktopLog);
        File.Delete(vanished);

        var result = Run([vanishedCandidate, Stat(good)]);

        // A failure here means one corrupt log ends the pass, so every log after it in the
        // candidate order is silently never ingested.
        Assert.Equal(2, result.Found);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Ingested);

        using var context = OpenRead();
        var failedRow = context.Phd2Logs.Single(row => row.FilePath == vanished);
        Assert.Equal("failed", failedRow.ParseStatus);
        Assert.False(string.IsNullOrEmpty(failedRow.ParseError));
        Assert.Equal("ok", context.Phd2Logs.Single(row => row.FilePath == good).ParseStatus);
    }

    [Fact]
    public void AFileWithNoGuideLogContent_IsEmptyAndIsNeitherIngestedNorFailed()
    {
        var path = WriteLog("PHD2_GuideLog_notes.txt", EmptyLog);

        var result = Run([Stat(path)]);

        // A failure here means an unrelated .txt named like a guide log is reported as a failure
        // on every scan.
        Assert.Equal(1, result.Found);
        Assert.Equal(1, result.Empty);
        Assert.Equal(0, result.Ingested);
        Assert.Equal(0, result.Failed);

        using var context = OpenRead();
        var log = context.Phd2Logs.Single();
        Assert.Equal("empty", log.ParseStatus);
        Assert.Null(log.ParseError);
        Assert.Equal(0, log.RunCount);
        Assert.Equal(0, log.SessionCount);
        Assert.DoesNotContain(context.ActivityEvents.ToList(), e => e.EventType == "phd2_log_failed");
    }

    // --- 8.4 the key, the counters and the events -------------------------------------------

    [Fact]
    public void TheKeyOff_ReadsWritesAndDeletesNothingAndEmitsNoEnvelope()
    {
        var path = WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        var candidate = Stat(path);
        Assert.Equal(1, Run([candidate]).Ingested);
        File.Delete(path);

        // Everything the enabled pass already wrote is the before state; what matters is that the
        // disabled pass adds nothing to it and takes nothing away.
        int lastEventId;
        using (var before = OpenRead())
        {
            lastEventId = before.ActivityEvents.Max(e => e.Id);
        }

        var envelopes = new List<ScanProgress>();
        var result = Run([], Settings(enabled: false), envelopes: envelopes);

        // A failure here means clearing a checkbox deletes the user's stored guiding data, which
        // spec 12.7's own checkbox text promises it does not.
        Assert.Same(Phd2PassResult.Disabled, result);
        Assert.Equal(0, result.Found);
        Assert.Equal(0, result.Ingested);
        Assert.Equal(0, result.Failed);
        Assert.Empty(envelopes);

        using var context = OpenRead();
        Assert.Equal(1, context.Phd2Logs.Count());
        Assert.Equal(1, context.Phd2Sessions.Count());
        Assert.Equal(2, context.Phd2Frames.Count());
        Assert.Empty(context.ActivityEvents.Where(e => e.Id > lastEventId).ToList());
    }

    [Fact]
    public void APassOverALibraryWithNoGuideLogs_EmitsItsEnvelopeAndWritesNoActivityRowAtAll()
    {
        // COORDINATOR RULING, from fixer-p15a-e's escalation: a library with nothing to say about
        // guide logs stays silent in the feed. `phd2_scan_enabled` ships ON, so before the ruling
        // every scan of every library that has never seen PHD2 wrote "No PHD2 guide logs found"
        // into the Activity feed, at every scan interval, for ever. A failure looks like exactly
        // that: one line per scan telling a user about a feature they do not use, which is how an
        // Activity feed stops being read.
        //
        // The ENVELOPE is deliberately still raised. Spec 10.4's phase vocabulary is what lets a
        // reader tell "the pass ran and found nothing" from "the pass did not run", and that
        // question is answered in the status bar and the run counters rather than in the feed.
        var envelopes = new List<ScanProgress>();

        var result = Run([], envelopes: envelopes);

        var envelope = Assert.Single(envelopes);
        Assert.Equal(ScanTaskNames.Phd2Ingest, envelope.Task);
        Assert.Equal(0, envelope.TotalSteps);
        Assert.Equal(0, result.Found);

        using var context = OpenRead();
        Assert.Empty(context.ActivityEvents.ToList());
    }

    [Fact]
    public void APassThatOpenedNothingButHoldsAStoredGuideLog_StillWritesPassComplete()
    {
        // The other side of the ruling, and the reason it is the five counters rather than
        // `Found` alone. A library that HAS a guide-log corpus keeps its row on every scan even
        // when the delta skip opened nothing, because that row is the reader's evidence that the
        // pass is still running over their guide logs.
        //
        // A failure looks like a PHD2 user's feed going silent about guiding the moment their
        // corpus stops changing, which is most scans.
        var path = WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        Assert.Equal(1, Run([Stat(path)]).Ingested);

        var second = Run([Stat(path)]);
        Assert.Equal(1, second.SkippedUnchanged);
        Assert.Equal(0, second.Ingested);

        using var context = OpenRead();
        Assert.Equal(2, context.ActivityEvents.Count(e => e.EventType == "phd2_pass_complete"));
    }

    [Fact]
    public void Phd2PassComplete_CarriesTheSevenSpecKeysAllSnakeCase()
    {
        var path = WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);

        Run([Stat(path)]);

        using var context = OpenRead();
        var row = context.ActivityEvents.Single(e => e.EventType == "phd2_pass_complete");
        Assert.Equal("scan", row.Category);
        Assert.Equal("info", row.Severity);

        using var payload = JsonDocument.Parse(row.Details!);
        var keys = payload.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(
            new[] { "duration_ms", "empty", "failed", "found", "ingested", "removed", "skipped_unchanged" },
            keys);
        Assert.All(keys, key => Assert.Matches("^[a-z][a-z0-9_]*$", key));
        Assert.Equal(1, payload.RootElement.GetProperty("found").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("ingested").GetInt32());
    }

    [Fact]
    public void TheThreeWarnings_AreEachWrittenOncePerPassAcrossSeveralOffendingSections()
    {
        var path = WriteLog("PHD2_GuideLog_asiair.txt", NoPixelScaleLog);

        Run([Stat(path)], Settings(timezone: "Not/AZone"));

        using var context = OpenRead();
        var events = context.ActivityEvents.ToList();

        // Three sections offend on all three counts. A failure here means one warning per
        // section, so an ASIAIR corpus writes hundreds of identical rows into the Activity feed
        // and buries everything else.
        Assert.Equal(1, events.Count(e => e.EventType == "phd2_timezone_unset"));
        Assert.Equal(1, events.Count(e => e.EventType == "phd2_timezone_invalid"));
        Assert.Equal(1, events.Count(e => e.EventType == "phd2_pixel_scale_missing"));
        Assert.Equal(3, context.Phd2Sessions.Count());

        var unset = Details(context, "phd2_timezone_unset");
        Assert.Equal(
            new[] { "RigA", "RigB" },
            unset.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()).ToArray());
        Assert.Equal("phd2_profile_map", unset.GetProperty("setting").GetString());

        var invalid = Details(context, "phd2_timezone_invalid");
        Assert.Equal("Not/AZone", invalid.GetProperty("zone").GetString());
        Assert.Equal("global", invalid.GetProperty("scope").GetString());

        var scale = Details(context, "phd2_pixel_scale_missing");
        Assert.Equal(3, scale.GetProperty("session_count").GetInt32());
        Assert.Equal(
            new[] { "RigA", "RigB" },
            scale.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

    [Fact]
    public void ASectionWithNoEquipmentProfile_IsNamedByTheNoProfileLabel()
    {
        // The ASIAIR case: the header line is trimmed before matching, so an empty profile can
        // never reach the record, and a null one folds onto the same label a stored empty string
        // would. Tested with string.IsNullOrEmpty, never `is null`.
        var path = WriteLog("PHD2_GuideLog_noprofile.txt", NoProfileLog);

        Run([Stat(path)]);

        using var context = OpenRead();
        Assert.Null(context.Phd2Sessions.Single().EquipmentProfile);
        var unset = Details(context, "phd2_timezone_unset");
        Assert.Equal(
            new[] { "(no equipment profile)" },
            unset.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()).ToArray());
    }

    // --- fix pass, P2-1: the per-log ceiling ------------------------------------------------

    [Fact]
    public void ACandidateOverTheSizeCeiling_IsRecordedFailedAndIsNeverOpened()
    {
        // The file on disk is a perfectly good guide log; only the size the WALK recorded is over
        // the ceiling. That is what makes this case discriminating: without the ceiling the read
        // succeeds and the log ingests, so a green result here can only mean the file was opened.
        var path = WriteLog("PHD2_GuideLog_huge.txt", DesktopLog);
        var oversized = Stat(path) with { FileSize = (64L * 1024 * 1024) + 1 };

        var result = Run([oversized]);

        Assert.Equal(1, result.Found);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Ingested);

        using (var context = OpenRead())
        {
            var log = context.Phd2Logs.Single();
            Assert.Equal("failed", log.ParseStatus);

            // The reason names both figures, so a reader can tell a refused log from a corrupt
            // one without opening it either. 64 MiB, spec 7.6's stated ceiling: the file's size
            // and the ceiling, in that order.
            Assert.Contains("67108865", log.ParseError);
            Assert.Contains("67108864", log.ParseError);

            // Never opened: nothing of the file's real content reached the database.
            Assert.Empty(context.Phd2Sessions.ToList());
            Assert.Empty(context.Phd2Frames.ToList());

            var details = Details(context, "phd2_log_failed");
            Assert.Equal("failed", details.GetProperty("parse_status").GetString());
        }

        // And the row stops the re-offer while the size and mtime are unchanged, exactly as any
        // other failure row does.
        var second = Run([oversized]);
        Assert.Equal(1, second.SkippedUnchanged);
        Assert.Equal(0, second.Failed);
    }

    // --- phase-review P2-1a: progress per guiding section, not only per file --------------------

    [Fact]
    public void ALogWithSeveralSections_ReportsProgressPerSection_NotOnlyPerFile()
    {
        // Spec 10.3 step 3 and 7.6: one real log holds hundreds of guiding sections and several
        // hundred thousand frame rows, and the pass reported one envelope per FILE. A failure
        // looks like a user scanning a library with one large guide log watching "Read 0/1 PHD2
        // guide logs" for minutes, with nothing anywhere saying the application is still working.
        var path = WriteLog("PHD2_GuideLog_sections.txt", NoPixelScaleLog);
        var envelopes = new List<ScanProgress>();

        Assert.Equal(1, Run([Stat(path)], envelopes: envelopes).Ingested);

        // Three sections in the fixture, so three section lines, each naming its own numerator.
        var sections = envelopes
            .Where(e => e.Message.Contains("guiding section", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ToList();
        Assert.Equal(3, sections.Count);
        Assert.Equal("Reading PHD2 guide log 1/1: guiding section 1/3", sections[0]);
        Assert.Equal("Reading PHD2 guide log 1/1: guiding section 3/3", sections[2]);

        // The file counters stay the envelope's step and total throughout, because the job's
        // percentage is built from them: a mid-file total would make the bar jump backwards.
        Assert.All(envelopes, e => Assert.Equal(1, e.TotalSteps));
    }

    // --- fix pass, P2-2: the audit record goes in before the rows go out ----------------------

    [Fact]
    public void AFeedThePassCannotWrite_StopsTheForcedDeleteInsteadOfOutlivingIt()
    {
        // Four stored rows under the walked root, two of them gone from disk, which is exactly the
        // 50 percent boundary, and the override on. Without the fix the rows are deleted and the
        // phd2_orphan_prune_forced event that spec 10.9 requires of a forced prune is swallowed,
        // leaving the deletion with no record of it anywhere in the feed.
        var present = new[] { SeededLog("a.txt"), SeededLog("b.txt") };
        SeedMissingRow("gone1.txt");
        SeedMissingRow("gone2.txt");

        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            // The one deterministic way to make the feed write fail while the guide-log tables
            // stay perfectly deletable.
            context.Database.ExecuteSqlRaw("DROP TABLE activity_events");
        }

        Assert.ThrowsAny<Exception>(() => Phd2Ingest.Run(
            _db.ConnectionString, present, Settings(), [_root], forceOrphanCleanup: true,
            parentActivityId: null, report: null, warn: null, CancellationToken.None));

        using var after = OpenRead();
        Assert.Equal(4, after.Phd2Logs.Count());
    }

    // A guide log that exists on disk with a stored row matching it exactly, so the delta skip
    // never opens it and it counts as discovered for the orphan guard.
    private DiscoveredFile SeededLog(string name)
    {
        var path = WriteLog(name, DesktopLog);
        var candidate = Stat(path);
        SeedRow(candidate.Path, candidate.FileSize, candidate.FileMtimeUnixSeconds);
        return candidate;
    }

    private void SeedMissingRow(string name) => SeedRow(Path.Combine(_root, name), 1, 0);

    private void SeedRow(string path, long size, double mtime)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        context.Phd2Logs.Add(new Phd2Log
        {
            Id = Guid.NewGuid(),
            FilePath = path,
            FileSize = size,
            FileMtime = mtime,
            ParseStatus = "ok",
            ParsedAt = DateTime.UtcNow,
        });
        context.SaveChanges();
    }

    // --- fix pass, P3-6a: the two pulse totals are 64 bit -------------------------------------

    [Fact]
    public void APulseTotalAboveIntMaxValue_RoundTrips()
    {
        // The metrics record carries these as long because a long session at a large maximum
        // duration approaches int.MaxValue. SQLite's INTEGER is 64 bit, so the column already
        // holds it; what moved is the entity property. A saturating clamp would read
        // 2147483647 here, and an int property would not compile at all.
        const long total = 3_000_000_000L;
        var logId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        new Phd2Repository(_db.ConnectionString).Insert(
            new Phd2Log
            {
                Id = logId,
                FilePath = Path.Combine(_root, "PHD2_GuideLog_pulses.txt"),
                ParseStatus = "ok",
                ParsedAt = DateTime.UtcNow,
            },
            [
                new Phd2Session
                {
                    Id = sessionId,
                    LogId = logId,
                    StartedAtLocal = new DateTime(2025, 3, 19, 21, 31, 0),
                    PulseTotalMsRa = total,
                    PulseTotalMsDec = total + 1,
                },
            ],
            [], []);

        using var context = OpenRead();
        var stored = context.Phd2Sessions.Single();
        Assert.Equal(total, stored.PulseTotalMsRa);
        Assert.Equal(total + 1, stored.PulseTotalMsDec);
    }

    // --- 8.4, through a real scan: the envelope, the parent and scan_complete -----------------

    [Fact]
    public async Task EveryPhd2EventRaisedDuringAScan_IsParentedToScanStarted()
    {
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        var coordinator = MakeCoordinator();

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        var events = context.ActivityEvents.OrderBy(e => e.Id).ToList();
        var started = events.Last(e => e.EventType == "scan_started");
        var phd2Events = events
            .Where(e => e.EventType.StartsWith("phd2_", StringComparison.Ordinal))
            .ToList();

        // A failure here means the Activity feed cannot collapse the scan and the guide-log
        // warnings float at the top level as if they were unrelated.
        Assert.NotEmpty(phd2Events);
        Assert.All(phd2Events, e => Assert.Equal(started.Id, e.ParentId));
    }

    [Fact]
    public async Task AScan_CarriesTheThreeCountersOnTheOutcomeTheRowAndScanComplete()
    {
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        WriteLog("PHD2_GuideLog_bad.txt", UnreadableLog);
        var coordinator = MakeCoordinator();
        var envelopes = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, progress) => envelopes.Add(progress);

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(2, outcome.Phd2Found);
        Assert.Equal(1, outcome.Phd2Ingested);
        Assert.Equal(1, outcome.Phd2Failed);

        // The guide logs are NOT frames: they never reach scan_runs.discovered.
        Assert.Equal(0, outcome.Discovered);
        Assert.Contains(envelopes, e => e.Task == ScanTaskNames.Phd2Ingest);

        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal(2, run.Phd2Found);
        Assert.Equal(1, run.Phd2Ingested);
        Assert.Equal(1, run.Phd2Failed);

        var complete = Details(context, "scan_complete");
        Assert.Equal(2, complete.GetProperty("phd2_found").GetInt32());
        Assert.Equal(1, complete.GetProperty("phd2_ingested").GetInt32());
        Assert.Equal(1, complete.GetProperty("phd2_failed").GetInt32());
    }

    [Fact]
    public async Task AScanWithTheKeyOff_StillCarriesTheThreeMembersOnScanCompleteAsZero()
    {
        WriteLog("PHD2_GuideLog_2025-03-19_213000.txt", DesktopLog);
        var coordinator = MakeCoordinator(phd2Enabled: false);

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(0, outcome.Phd2Found);
        using var context = OpenRead();
        var complete = Details(context, "scan_complete");
        Assert.Equal(0, complete.GetProperty("phd2_found").GetInt32());
        Assert.Equal(0, complete.GetProperty("phd2_ingested").GetInt32());
        Assert.Equal(0, complete.GetProperty("phd2_failed").GetInt32());
        Assert.Empty(context.Phd2Logs.ToList());
    }

    // --- spec 8.3 step 3 at the ingest call site (Phase 17 ruling U4) ------------------------

    /// <summary>
    /// The ingest computes the session's start instant at the call site and passes the resolved
    /// longitude in, while <c>Phd2Metrics.ComputeSessionMetrics</c> computes that same instant
    /// inside itself for the session date. The two must agree, or the offset step 3 takes is
    /// taken at a different instant from the one the date is computed at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure looks like a call site that converts with a different zone or a different wall
    /// clock: across this transition the two evening longitudes are 15 degrees apart, -75 and -60,
    /// and the stored night of at least one section is a day out. The assertions name both.
    /// </para>
    /// <para>
    /// The four sections cover the three ways the call site can be wrong. Sections 0 and 2 are 24
    /// hours apart and catch a zone or an instant taken once for the whole pass. Section 1 starts
    /// before the gap and ends after it, so its two wall clocks convert under different offsets.
    /// Section 3 is truncated and has no local end at all, which is what makes the FIELD the call
    /// site reads load bearing: a call site reading <c>EndedAtLocal</c> passes a null instant, step
    /// 3 is skipped, and an evening session falls back to UTC-midnight grouping a day late.
    /// </para>
    /// </remarks>
    [Fact]
    public void AcrossADstBoundary_TheCallSitesInstantAndTheMetricsAgree()
    {
        var path = WriteLog("PHD2_GuideLog_2025-03-08_200000.txt", DstBoundaryLog);

        // A zone and no longitude anywhere, which is the only configuration step 3 answers in.
        Assert.Equal(1, Run([Stat(path)], Settings(timezone: DstZone)).Ingested);

        using var context = OpenRead();
        var sessions = context.Phd2Sessions.OrderBy(session => session.StartedAtLocal).ToList();
        Assert.Equal(4, sessions.Count);

        // The four instants, converted by the same member the metrics converts by.
        Assert.Equal(new DateTime(2025, 3, 9, 2, 0, 0), sessions[0].StartedAtUtc);
        Assert.Equal(new DateTime(2025, 3, 9, 6, 30, 0), sessions[1].StartedAtUtc);
        Assert.Equal(new DateTime(2025, 3, 10, 1, 0, 0), sessions[2].StartedAtUtc);
        Assert.Equal(new DateTime(2025, 3, 10, 2, 10, 0), sessions[3].StartedAtUtc);

        // The truncated section stored no local end, which is what section 3 is here to provide.
        Assert.Null(sessions[3].EndedAtLocal);

        // Each stored night is the one computed from that section's OWN instant and OWN offset,
        // which is what pins the call site to the metrics. SpecifyKind is load bearing: SQLite
        // hands these columns back Unspecified, and TimeZoneInfo.GetUtcOffset reads an Unspecified
        // value as a wall clock IN the zone rather than as an instant. At section 1's own start,
        // 2025-03-09 06:30, the two readings give -75 and -60, so an oracle that skipped the Kind
        // would be evaluating a different rule from the one production evaluates.
        foreach (var session in sessions)
        {
            var instant = DateTime.SpecifyKind(session.StartedAtUtc!.Value, DateTimeKind.Utc);
            Assert.Equal(
                SessionDate.Compute(
                    session.StartedAtUtc, true, SessionDate.LongitudeFromTimezone(DstZone, instant)),
                session.SessionDate);
        }

        // The two offsets in force across the transition, named, and the literal nights they give.
        Assert.Equal(
            -75.0,
            SessionDate.LongitudeFromTimezone(
                DstZone, DateTime.SpecifyKind(sessions[0].StartedAtUtc!.Value, DateTimeKind.Utc))!.Value,
            6);
        Assert.Equal(
            -60.0,
            SessionDate.LongitudeFromTimezone(
                DstZone, DateTime.SpecifyKind(sessions[2].StartedAtUtc!.Value, DateTimeKind.Utc))!.Value,
            6);

        Assert.Equal(new DateOnly(2025, 3, 8), sessions[0].SessionDate);
        Assert.Equal(new DateOnly(2025, 3, 8), sessions[1].SessionDate);
        Assert.Equal(new DateOnly(2025, 3, 9), sessions[2].SessionDate);
        Assert.Equal(new DateOnly(2025, 3, 9), sessions[3].SessionDate);
    }

    private const string DstZone = "America/New_York";

    // --- coordinator harness, the shape OrphanPruneEventsTests already uses ------------------

    private ScanCoordinator MakeCoordinator(bool phd2Enabled = true)
    {
        var settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        settings.SaveGeneral(new GeneralSettings
        {
            ScanRoots = [_root],
            ScanFilters = ScanFilterConfig.Empty,
            Phd2ScanEnabled = phd2Enabled,
        });
        var resolver = new TargetResolver(
            _db.ConnectionString, StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(new ThrowingHandler()), new SesameClient(new ThrowingHandler()));
        return new ScanCoordinator(
            _db.ConnectionString, settings, resolver, new ScanRunRepository(_db.ConnectionString),
            NullLogger<ScanCoordinator>.Instance);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException($"network access attempted: {request.RequestUri}");
    }
}
