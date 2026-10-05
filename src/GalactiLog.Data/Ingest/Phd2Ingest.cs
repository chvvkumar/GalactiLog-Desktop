using System.Globalization;
using System.Text.Json;
using GalactiLog.Core.Io;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
// GalactiLog.Core.Phd2 and GalactiLog.Data.Entities each declare a Phd2Frame and a
// Phd2Calibration (Task 4a's entity remark names the collision), so this file, which is the one
// place that holds both, says which it means every time.
using CoreFrame = GalactiLog.Core.Phd2.Phd2Frame;
using EntityCalibration = GalactiLog.Data.Entities.Phd2Calibration;
using EntityFrame = GalactiLog.Data.Entities.Phd2Frame;

namespace GalactiLog.Data.Ingest;

/// <summary>One guide log the pass could not read or parse (spec 10.9's
/// <c>phd2_log_failed</c>).</summary>
/// <param name="Path">The file as it was walked.</param>
/// <param name="ParseStatus"><c>unreadable</c> or <c>failed</c> (spec 5.15).</param>
/// <param name="Reason">The exception message, truncated to 2000 characters.</param>
public sealed record Phd2LogFailure(string Path, string ParseStatus, string Reason);

/// <summary>
/// What the guide-log pass of spec 10.3 step 5 did. The first three members are
/// <c>scan_runs.phd2_found</c>, <c>phd2_ingested</c> and <c>phd2_failed</c> (spec 5.13).
/// </summary>
/// <param name="Found">Every guide log the walk discovered, whether or not the delta skip opened
/// it.</param>
/// <param name="Ingested">Logs this pass parsed and stored, that is those whose
/// <c>parse_status</c> is <c>ok</c>.</param>
/// <param name="Failed">Logs recorded <c>unreadable</c> or <c>failed</c>.</param>
/// <param name="SkippedUnchanged">Logs the delta skip never opened.</param>
/// <param name="Empty">Logs recorded <c>empty</c>. Neither ingested nor failed (spec 10.4).
/// </param>
/// <param name="Removed"><c>phd2_logs</c> rows the orphan drop deleted. Rows only, never files.
/// </param>
/// <param name="Failures">One entry per failed log, in candidate order.</param>
/// <param name="IngestedNights">The distinct imaging nights this pass stored a session for, which
/// is the re-derivation set spec 10.3 step 5 item 5 hands the correlation.</param>
/// <param name="TimezoneUnsetProfiles">Exactly the profiles this pass named in its own
/// <c>phd2_timezone_unset</c> event, capped as that event caps them, or empty when it raised
/// none.</param>
/// <param name="PixelScaleMissingProfiles">The same, for <c>phd2_pixel_scale_missing</c>.</param>
/// <remarks>
/// The two profile lists exist for one reason: spec 10.9 raises each of those two warnings <b>once
/// per pass</b>, and a scan runs two passes over one corpus, so the correlation that follows this
/// one names only what this one did not and says nothing when nothing is left. They are what this
/// pass NAMED rather than what it observed, so the subtraction cannot suppress a profile the feed
/// never printed. The out-of-scan re-run has no ingest beside it and passes none.
/// </remarks>
public sealed record Phd2PassResult(
    int Found,
    int Ingested,
    int Failed,
    int SkippedUnchanged,
    int Empty,
    int Removed,
    IReadOnlyList<Phd2LogFailure> Failures,
    IReadOnlyList<DateOnly> IngestedNights,
    IReadOnlyList<string> TimezoneUnsetProfiles,
    IReadOnlyList<string> PixelScaleMissingProfiles)
{
    /// <summary>What <c>general.phd2_scan_enabled</c> false produces: six zero counters and
    /// nothing read, written or pruned. Turning the key off is not a deletion (spec 10.3).
    /// </summary>
    public static readonly Phd2PassResult Disabled = new(0, 0, 0, 0, 0, 0, [], [], [], []);
}

/// <summary>
/// Spec 10.3 step 5, the guide-log pass: candidates, delta skip, ingest, failure record, orphan
/// drop, counters and the events of spec 10.9. Port of the ingest half of
/// <c>backend/app/worker/tasks_phd2.py</c>.
/// </summary>
/// <remarks>
/// THIS TYPE READS USER FILES AND DELETES DATABASE ROWS ONLY. IT NEVER WRITES, CREATES, MOVES,
/// RENAMES, TRUNCATES OR DELETES ANY FILE OR DIRECTORY ON DISK, UNDER ANY CIRCUMSTANCE
/// (spec 2.1, spec 7.6). Every read goes through <c>GalactiLog.Core.Io.UserFiles</c>, which has
/// no write member to call, in shared-read mode. <c>FileSafetyTest</c> (spec 2.1.3) source-scans
/// <c>src/**</c> and fails the build if a write-capable call appears here; this file needs no
/// allowlist exception and must never be given one.
/// <para>
/// It runs AFTER the header pass, never beside it: the writer of spec 5.1 owns the single write
/// connection until the frame ingest is done, and this is a second writer that takes it
/// afterwards.
/// </para>
/// <para>
/// It does not correlate. Spec 10.3 step 5 item 5's correlation call lands here as its own step,
/// with its own envelope task and its own job.
/// </para>
/// </remarks>
public static class Phd2Ingest
{
    /// <summary>Spec 5.15: <c>parse_error</c> is truncated to 2000 characters.</summary>
    private const int ReasonLimit = 2000;

    /// <summary>
    /// The largest guide log this pass will open, in bytes. Checked against the size the walk
    /// already stated, so the ceiling costs no second stat and no open.
    /// </summary>
    /// <remarks>
    /// ponytail: one ceiling, refused through the ordinary failure row, at the figure spec 7.6
    /// states. A log past it is read whole into one UTF-16 string (twice its byte size in memory),
    /// then one entity per CSV row into one tracking context, so a single file can demand far more
    /// memory than its own size; 64 MiB is roughly 550,000 log lines, which is already an order of
    /// magnitude past the reference corpus's largest desktop log and is the figure the pipeline is
    /// bounded at rather than the figure the read throws at. The upgrade path, when a real corpus
    /// needs it, is a streaming line reader feeding batched inserts, at which point this constant
    /// goes away rather than growing. Raising it is a change to spec 7.6's sentence and nothing
    /// else.
    /// </remarks>
    private const long MaxLogBytes = 64L * 1024 * 1024;

    /// <summary>Spec 10.9: <c>phd2_timezone_unset</c> and <c>phd2_pixel_scale_missing</c> cap
    /// their profile list at 50. A corpus with hundreds of profiles is a settings problem, and a
    /// details document nobody can read is not evidence of it.</summary>
    private const int ProfileCap = 50;

    /// <summary>Spec 10.9's "no profile" label and the setting <c>phd2_timezone_unset</c> names,
    /// both declared once on <see cref="Phd2CorrelationEvents"/> because the correlation's own
    /// copies of these two events print them too, and two spellings would put two different names
    /// in one feed.</summary>
    private const string NoProfileLabel = Phd2CorrelationEvents.NoProfileLabel;

    private const string ProfileMapSetting = Phd2CorrelationEvents.ProfileMapSetting;

    // The steps array's keys are the spec 5.18 document's own: {"direction", "step", "dx", "dy",
    // "x", "y", "dist"}. Phd2CalibrationStep carries no [JsonPropertyName], unlike Phd2Event, so
    // the naming policy is what produces them. Phd2Event's attributes win over a policy, so the
    // events array is serialized with the default options instead and matches the web byte for
    // byte.
    private static readonly JsonSerializerOptions StepOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// Runs the whole pass and returns its counters. Takes what it needs as arguments, the way
    /// <see cref="OrphanPruner.Prune"/> does: it reads no setting of its own and holds no
    /// coordinator reference.
    /// </summary>
    /// <param name="connectionString">The library database.</param>
    /// <param name="candidates">The guide logs the walk reported through its callback, with the
    /// size and modification time it already stated. The walk is not repeated (spec 10.3 step 5
    /// item 1).</param>
    /// <param name="general">The settings snapshot the coordinator already read.</param>
    /// <param name="walkedRoots">The run's effective roots, which is what the orphan guards are
    /// evaluated per. A root this run did not walk prunes nothing.</param>
    /// <param name="forceOrphanCleanup">Spec 10.3's per-run override, which is not a second
    /// control: it reaches the 50 percent limit only and never the zero-discovery guard.</param>
    /// <param name="parentActivityId">The run's <c>scan_started</c> event id, so every event
    /// below collapses into the scan. Null outside a scan.</param>
    /// <param name="report">(step, totalSteps, message, terminal) for the <c>phd2_ingest</c>
    /// envelope of spec 10.4.</param>
    /// <param name="warn">Where a parser warning or an unreadable file goes. Plain data, because
    /// this assembly's ingest types carry no logger of their own.</param>
    /// <param name="ct">A cancelled pass stops opening candidates and prunes nothing: a partial
    /// discovery set must never reach the orphan guards.</param>
    public static Phd2PassResult Run(
        string connectionString,
        IReadOnlyList<DiscoveredFile> candidates,
        GeneralSettings general,
        IReadOnlyList<string> walkedRoots,
        bool forceOrphanCleanup,
        int? parentActivityId,
        Action<int, int, string, bool>? report,
        Action<string>? warn,
        CancellationToken ct)
    {
        // Spec 10.3: when the key is off the pass does not run at all, the three counters are
        // written as zero, nothing in phd2_* is read, written or pruned, and neither envelope
        // task is emitted. Turning the key off is not a deletion, which is what spec 12.7's own
        // checkbox text promises.
        if (!general.Phd2ScanEnabled)
        {
            return Phd2PassResult.Disabled;
        }

        var started = DateTime.UtcNow;
        var repository = new Phd2Repository(connectionString);
        var stored = repository.StoredFiles()
            .GroupBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        // Built once for the whole pass and handed to every section, not rebuilt per file: a log
        // with two hundred sections would otherwise coerce the same stored map two hundred
        // times, which is the Python's own stated reason.
        var profileMap = Phd2Profiles.Normalize(general.Phd2ProfileMap);
        var resolveZone = Phd2Profiles.ZoneResolver(general.Phd2ProfileMap, general.ObserverTimezone);
        var telescopes = Phd2Profiles.TelescopeMap(general.Phd2ProfileMap);
        // One loader shared between PassNotices, the longitude resolver's step 3 and every
        // section. PassNotices resolves the global zone in its own initialiser, so a second loader
        // built for it would compile, pass every case, and quietly double the platform lookups on
        // a corpus with hundreds of sections. ZoneResolver builds its own internally, so this is
        // not the only loader the pass holds.
        var loadZone = Phd2Profiles.ZoneLoader();
        var resolveLongitude = Phd2Profiles.LongitudeResolver(
            general.Phd2ProfileMap, general.ObserverLongitude, general.ObserverTimezone, loadZone);

        var notices = new PassNotices(general, loadZone);
        var failures = new List<Phd2LogFailure>();
        var nights = new HashSet<DateOnly>();
        int ingested = 0, failed = 0, skippedUnchanged = 0, empty = 0, checkedFiles = 0;
        var found = candidates.Count;
        var cancelled = false;

        if (found > 0)
        {
            report?.Invoke(0, found, "Reading PHD2 guide logs...", true);
        }

        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            checkedFiles++;

            // Spec 10.3 step 5 item 2. PHD2 never rewrites a closed log, so a stored log whose
            // size matches and whose mtime is within the tolerance images already uses is never
            // opened again. A stored row with no mtime fails the test and is re-read, matching
            // the Python's `existing.file_mtime is not None and ...`.
            if (stored.TryGetValue(candidate.Path, out var row)
                && row.FileSize == candidate.FileSize
                && row.FileMtime is { } mtime
                && Math.Abs(mtime - candidate.FileMtimeUnixSeconds) <= FileWalker.MtimeToleranceSeconds)
            {
                skippedUnchanged++;
                report?.Invoke(checkedFiles, found, Progress(checkedFiles, found), false);
                continue;
            }

            // The ceiling, on the walk's own figure, before anything opens the file. The row it
            // writes is an ordinary failure row, so the counters, the feed and the delta skip all
            // treat it like any other unusable log and it is not offered again until it changes.
            var outcome = candidate.FileSize > MaxLogBytes
                ? RecordFailure(
                    repository, candidate, StatusFailed,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The guide log is {candidate.FileSize} bytes, past the {MaxLogBytes} byte " +
                        $"ceiling this pass will open, and was not read."))
                : IngestOne(
                    repository, candidate, general, profileMap, resolveZone, resolveLongitude,
                    telescopes, loadZone, notices, nights, warn,
                    // Spec 10.3 step 3: progress per guiding SECTION within a log, not only per
                    // file. One real log holds hundreds of sections and several hundred thousand
                    // frame rows, and a per-file numerator leaves a reader watching a motionless
                    // counter for minutes. The file counters stay the envelope's step and total,
                    // because they are what the job's percentage is built from and a mid-file
                    // total would make the bar jump; the section count goes in the message.
                    (done, total) => report?.Invoke(
                        checkedFiles - 1, found, Sections(checkedFiles, found, done, total), false));

            switch (outcome.Status)
            {
                case StatusOk:
                    ingested++;
                    break;
                case StatusEmpty:
                    empty++;
                    break;
                default:
                    failed++;
                    failures.Add(new Phd2LogFailure(candidate.Path, outcome.Status, outcome.Reason ?? ""));
                    warn?.Invoke($"PHD2 guide log {candidate.Path} is {outcome.Status}: {outcome.Reason}");
                    break;
            }

            report?.Invoke(checkedFiles, found, Progress(checkedFiles, found), false);
        }

        // Spec 10.3 step 5 item 4, through the frame side's own guard arithmetic. Skipped
        // outright on a cancelled pass: the candidate list is then a partial view of the disk,
        // and a partial view is exactly what the guards exist to refuse.
        var drop = cancelled
            ? new OrphanPrunePlan([], [], [], 0)
            : OrphanPruner.Plan(
                walkedRoots,
                repository.StoredFiles().Select(file => file.FilePath),
                candidates.Select(file => file.Path),
                forceOrphanCleanup);

        // The audit record goes in BEFORE the rows go out, and unguarded. Spec 10.9 exists so
        // that "a forced pass that emptied half the catalogue is in the Activity feed and not
        // only in the run counters"; a delete that outlived the record of it would defeat that
        // exactly. A feed this pass cannot write therefore stops the deletion instead, which is
        // what the frame side already does by not guarding its own emit at all.
        EmitOrphanEvents(connectionString, parentActivityId, drop, forceOrphanCleanup);
        var removed = drop.ToDelete.Count == 0 ? 0 : repository.DeleteByPaths(drop.ToDelete);

        var durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;

        // Capped here, once, and carried on the result: these are exactly the two lists the events
        // below print, and the correlation that follows in the same scan subtracts them from its
        // own so spec 10.9's "once per pass" holds across both passes. Computing them twice would
        // let the row and the subtraction disagree.
        var timezoneUnsetProfiles = Cap(notices.UnsetZoneProfiles);
        var pixelScaleMissingProfiles = Cap(notices.NoPixelScaleProfiles);

        var result = new Phd2PassResult(
            found, ingested, failed, skippedUnchanged, empty, removed, failures, [.. nights.Order()],
            timezoneUnsetProfiles, pixelScaleMissingProfiles);

        if (!EmitEvents(connectionString, parentActivityId, result, notices, durationMs, cancelled, warn))
        {
            // The feed never saw this pass's two once-per-pass warnings, so the correlation that
            // follows in the same scan must not subtract them from its own: otherwise a failed
            // emit here silently suppresses BOTH copies and neither warning reaches the user
            // (fix-wave review P3-6). The counters are unaffected; only what the correlation may
            // treat as already said is.
            result = result with { TimezoneUnsetProfiles = [], PixelScaleMissingProfiles = [] };
        }

        report?.Invoke(checkedFiles, found, Terminal(result), true);
        return result;
    }

    private const string StatusOk = "ok";
    private const string StatusEmpty = "empty";
    private const string StatusUnreadable = "unreadable";
    private const string StatusFailed = "failed";

    private readonly record struct IngestOutcome(string Status, string? Reason);

    // One file, one transaction (spec 10.3 step 5 item 3): Phd2Repository.Insert deletes the old
    // rows and inserts the new ones together, so an interrupted re-parse can never leave a log
    // half replaced. A failure writes a row too, which is what stops the file being read again
    // until its size or its modification time changes.
    private static IngestOutcome IngestOne(
        Phd2Repository repository,
        DiscoveredFile candidate,
        GeneralSettings general,
        IReadOnlyDictionary<string, Phd2ProfileEntry> profileMap,
        Func<string?, (string Zone, string Source)> resolveZone,
        Func<string?, DateTime?, double?> resolveLongitude,
        IReadOnlyDictionary<string, string> telescopes,
        Func<string, TimeZoneInfo?> loadZone,
        PassNotices notices,
        HashSet<DateOnly> nights,
        Action<string>? warn,
        Action<int, int>? reportSection = null)
    {
        string? text;
        try
        {
            // Shared-read, through the read-only gateway, whole, then closed (spec 7.6). The
            // file's own bytes are handed to the parser as they are: PHD2 on Windows writes
            // CRLF and the parser splits on it.
            text = UserFiles.ReadAllText(candidate.Path);
        }
        catch (Exception ex)
        {
            return RecordFailure(repository, candidate, StatusFailed, ex.Message);
        }

        IReadOnlyList<Phd2Run> runs;
        try
        {
            runs = Phd2LogParser.Parse(text);
        }
        catch (Phd2UnreadableLogException ex)
        {
            // Caught BY NAME, and never as its FormatException base: the base would also swallow
            // an unrelated number-format failure and file it under the wrong status. This one is
            // the parser saying it recognises a guide log and cannot place a line, which is a
            // statement about the file rather than a crash.
            return RecordFailure(repository, candidate, StatusUnreadable, ex.Message);
        }
        catch (Exception ex)
        {
            // One bad file never stops the pass (spec 10.3 step 5 item 3).
            return RecordFailure(repository, candidate, StatusFailed, ex.Message);
        }
        finally
        {
            // The text is the largest single thing this method holds, at twice the file's byte
            // size, and the mapping below allocates one entity per CSV row. Dropping the reference
            // the moment the parser is done with it is what stops the two peaks adding: this
            // method's own frame is the only thing that keeps the string alive, so the collector
            // can take it while the entities are still being built.
            text = null;
        }

        var logId = Guid.NewGuid();
        var sessions = new List<Phd2Session>();
        var frames = new List<EntityFrame>();
        var calibrations = new List<EntityCalibration>();

        var sectionsInLog = runs.Sum(run => run.Sections.Count);
        var sectionsDone = 0;

        for (var runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            var run = runs[runIndex];
            foreach (var warning in run.Warnings)
            {
                // Warning, not information: these are the parser saying it threw data away.
                warn?.Invoke($"PHD2 guide log {candidate.Path}: {warning}");
            }

            for (var sectionIndex = 0; sectionIndex < run.Sections.Count; sectionIndex++)
            {
                var section = run.Sections[sectionIndex];
                var profile = section.Header.EquipmentProfile;
                var (zoneName, source) = resolveZone(profile);
                var zone = loadZone(zoneName);
                // Spec 8.3 step 3 needs the session's own start instant, and ComputeSessionMetrics
                // derives that instant inside itself from section.StartedAtLocal. The two lines
                // below MIRROR it exactly, field for field, so the offset step 3 takes is the
                // offset in force at the instant the session date is computed at. LocalToUtc is
                // public and pure, so the second call costs a conversion and nothing else.
                var startedUtc = Phd2Metrics.LocalToUtc(section.StartedAtLocal ?? default, zone);
                var metrics = Phd2Metrics.ComputeSessionMetrics(
                    section, zone, resolveLongitude(profile, startedUtc), general.UseImagingNight);

                var sessionId = Guid.NewGuid();
                sessions.Add(ToSession(
                    metrics, section.EndedAtLocal, sessionId, logId, runIndex, sectionIndex,
                    telescopes));
                foreach (var frame in section.Frames)
                {
                    frames.Add(ToFrame(frame, sessionId));
                }

                notices.Observe(profile, source, metrics.PixelScaleArcsec, profileMap);
                if (metrics.SessionDate is { } night)
                {
                    nights.Add(night);
                }

                reportSection?.Invoke(++sectionsDone, sectionsInLog);
            }

            foreach (var calibration in run.Calibrations)
            {
                var profile = calibration.Header.EquipmentProfile;
                var (zoneName, _) = resolveZone(profile);
                var zone = loadZone(zoneName);
                // The calibration twin of the two lines above, mirroring BuildCalibrationMetrics's
                // own derivation: a calibration block with no start line has no instant at all.
                var startedUtc = calibration.StartedAtLocal is { } calibrationStart
                    ? Phd2Metrics.LocalToUtc(calibrationStart, zone)
                    : null;
                var metrics = Phd2Metrics.BuildCalibrationMetrics(
                    calibration, zone, resolveLongitude(profile, startedUtc), general.UseImagingNight);
                calibrations.Add(ToCalibration(metrics, logId, telescopes));
            }
        }

        var first = runs.Count > 0 ? runs[0] : null;
        var status = sessions.Count > 0 || calibrations.Count > 0 ? StatusOk : StatusEmpty;
        var log = new Phd2Log
        {
            Id = logId,
            FilePath = candidate.Path,
            FileSize = candidate.FileSize,
            FileMtime = candidate.FileMtimeUnixSeconds,
            ParseStatus = status,
            // Spec 5.15: null when the status is ok or empty.
            ParseError = null,
            Phd2Version = first?.Phd2Version,
            LogVersion = first?.LogVersion,
            RunCount = runs.Count,
            SessionCount = sessions.Count,
            CalibrationCount = calibrations.Count,
            ParsedAt = DateTime.UtcNow,
        };

        repository.Insert(log, sessions, frames, calibrations);
        return new IngestOutcome(status, null);
    }

    // A file with no usable content still gets a row, with zero counts, so it is not re-read on
    // every scan forever (spec 5.15).
    private static IngestOutcome RecordFailure(
        Phd2Repository repository, DiscoveredFile candidate, string status, string reason)
    {
        var truncated = reason.Length <= ReasonLimit ? reason : reason[..ReasonLimit];
        repository.Insert(
            new Phd2Log
            {
                Id = Guid.NewGuid(),
                FilePath = candidate.Path,
                FileSize = candidate.FileSize,
                FileMtime = candidate.FileMtimeUnixSeconds,
                ParseStatus = status,
                ParseError = truncated,
                ParsedAt = DateTime.UtcNow,
            },
            [], [], []);
        return new IngestOutcome(status, truncated);
    }

    // ---- record mapping ------------------------------------------------------------------
    // Field for field onto spec 5.16 to 5.18, with two columns resolved here rather than by the
    // metrics. `telescope` comes from general.phd2_profile_map at ingest (spec 5.16), which is a
    // settings concern the pure Core metrics deliberately knows nothing of. `ended_at_local` comes
    // from the parsed section, because Phd2SessionMetrics does not carry it and the parser record
    // does.

    private static Phd2Session ToSession(
        Phd2SessionMetrics metrics, DateTime? endedAtLocal, Guid sessionId, Guid logId,
        int runIndex, int sectionIndex, IReadOnlyDictionary<string, string> telescopes)
        => new()
        {
            Id = sessionId,
            LogId = logId,
            RunIndex = runIndex,
            SectionIndex = sectionIndex,
            StartedAtLocal = metrics.StartedAtLocal,
            StartedAtUtc = metrics.StartedAtUtc,
            EndedAtUtc = metrics.EndedAtUtc,
            EndedAtLocal = endedAtLocal,
            DurationS = metrics.DurationS,
            SessionDate = metrics.SessionDate,
            EquipmentProfile = metrics.EquipmentProfile,
            Telescope = Telescope(metrics.EquipmentProfile, telescopes),
            PixelScaleArcsec = metrics.PixelScaleArcsec,
            FocalLengthMm = metrics.FocalLengthMm,
            GuideCamera = metrics.GuideCamera,
            ExposureMs = metrics.ExposureMs,
            MountName = metrics.MountName,
            DecGuideMode = metrics.DecGuideMode,
            AlgoRa = metrics.AlgoRa,
            AlgoDec = metrics.AlgoDec,
            MinMoveRa = metrics.MinMoveRa,
            MinMoveDec = metrics.MinMoveDec,
            AggressionRa = metrics.AggressionRa,
            OrthoErrorDeg = metrics.OrthoErrorDeg,
            LastCalIssue = metrics.LastCalIssue,
            PierSide = metrics.PierSide,
            AltDeg = metrics.AltDeg,
            AzDeg = metrics.AzDeg,
            DecDeg = metrics.DecDeg,
            HourAngleHr = metrics.HourAngleHr,
            FrameCount = metrics.FrameCount,
            DropCount = metrics.DropCount,
            MaxDropRun = metrics.MaxDropRun,
            UnguidedSeconds = metrics.UnguidedSeconds,
            RmsRaArcsec = metrics.RmsRaArcsec,
            RmsDecArcsec = metrics.RmsDecArcsec,
            RmsTotalArcsec = metrics.RmsTotalArcsec,
            RmsRaFilteredArcsec = metrics.RmsRaFilteredArcsec,
            RmsDecFilteredArcsec = metrics.RmsDecFilteredArcsec,
            RmsTotalFilteredArcsec = metrics.RmsTotalFilteredArcsec,
            PeakRaArcsec = metrics.PeakRaArcsec,
            PeakDecArcsec = metrics.PeakDecArcsec,
            SnrMean = metrics.SnrMean,
            SnrMin = metrics.SnrMin,
            StarMassMean = metrics.StarMassMean,
            PulseCountRaWest = metrics.PulseCountRaWest,
            PulseCountRaEast = metrics.PulseCountRaEast,
            PulseCountDecNorth = metrics.PulseCountDecNorth,
            PulseCountDecSouth = metrics.PulseCountDecSouth,
            PulseTotalMsRa = metrics.PulseTotalMsRa,
            PulseTotalMsDec = metrics.PulseTotalMsDec,
            DitherCount = metrics.DitherCount,
            SettleCount = metrics.SettleCount,
            SettleFailedCount = metrics.SettleFailedCount,
            SettleMedianS = metrics.SettleMedianS,
            StarLostReasons = JsonSerializer.Serialize(metrics.StarLostReasons),
            Events = JsonSerializer.Serialize(metrics.Events),
            Truncated = metrics.Truncated,
            DiscardedRows = metrics.DiscardedRows,
        };

    private static EntityFrame ToFrame(CoreFrame frame, Guid sessionId)
        => new()
        {
            SessionId = sessionId,
            FrameIndex = frame.FrameIndex,
            TimeOffset = frame.TimeOffset,
            Dx = frame.Dx,
            Dy = frame.Dy,
            RaRaw = frame.RaRaw,
            DecRaw = frame.DecRaw,
            RaGuide = frame.RaGuide,
            DecGuide = frame.DecGuide,
            RaDurationMs = frame.RaDurationMs,
            RaDirection = frame.RaDirection,
            DecDurationMs = frame.DecDurationMs,
            DecDirection = frame.DecDirection,
            StarMass = frame.StarMass,
            Snr = frame.Snr,
            ErrorCode = frame.ErrorCode,
            Dropped = frame.Dropped,
        };

    private static EntityCalibration ToCalibration(
        Phd2CalibrationMetrics metrics, Guid logId, IReadOnlyDictionary<string, string> telescopes)
        => new()
        {
            Id = Guid.NewGuid(),
            LogId = logId,
            // A calibration block opens on `Calibration Begins at <timestamp>`, whose pattern
            // requires the timestamp, so this is null only for a record no parser path can
            // produce. Spec 5.18's column is NOT NULL, so the unreachable case takes the
            // default rather than dropping a block the log_id count says is there.
            StartedAtLocal = metrics.StartedAtLocal ?? default,
            StartedAtUtc = metrics.StartedAtUtc,
            SessionDate = metrics.SessionDate,
            EquipmentProfile = metrics.EquipmentProfile,
            Telescope = Telescope(metrics.EquipmentProfile, telescopes),
            PixelScaleArcsec = metrics.PixelScaleArcsec,
            FocalLengthMm = metrics.FocalLengthMm,
            GuideCamera = metrics.GuideCamera,
            MountName = metrics.MountName,
            RaGuideSpeed = metrics.RaGuideSpeed,
            DecGuideSpeed = metrics.DecGuideSpeed,
            DecDeg = metrics.DecDeg,
            HourAngleHr = metrics.HourAngleHr,
            PierSide = metrics.PierSide,
            AltDeg = metrics.AltDeg,
            AzDeg = metrics.AzDeg,
            WestAngleDeg = metrics.WestAngleDeg,
            WestRatePxS = metrics.WestRatePxS,
            WestParity = metrics.WestParity,
            NorthAngleDeg = metrics.NorthAngleDeg,
            NorthRatePxS = metrics.NorthRatePxS,
            NorthParity = metrics.NorthParity,
            Completed = metrics.Completed,
            Steps = JsonSerializer.Serialize(metrics.Steps, StepOptions),
        };

    // The profile map keys on the empty string for "no profile" (questions-a Q15), so a null and
    // an empty profile name resolve to the same entry.
    private static string? Telescope(string? profile, IReadOnlyDictionary<string, string> telescopes)
        => telescopes.GetValueOrDefault(profile ?? "");

    // ---- pass notices --------------------------------------------------------------------

    /// <summary>
    /// The three once-per-pass warnings of spec 10.9, accumulated as sets rather than written per
    /// section. An ASIAIR corpus has hundreds of offending sections and one warning per section
    /// would bury everything else in the Activity feed.
    /// </summary>
    private sealed class PassNotices(GeneralSettings general, Func<string, TimeZoneInfo?> loadZone)
    {
        private readonly string _globalZone = general.ObserverTimezone;
        private readonly bool _globalLoads =
            general.ObserverTimezone.Length == 0 || loadZone(general.ObserverTimezone) is not null;

        public SortedSet<string> UnsetZoneProfiles { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> NoPixelScaleProfiles { get; } = new(StringComparer.Ordinal);

        public int NoPixelScaleSessions { get; private set; }

        /// <summary>Profiles whose sessions relied on <c>general.observer_timezone</c>, so a bad
        /// global value is a fact about them.</summary>
        public SortedSet<string> GlobalZoneProfiles { get; } = new(StringComparer.Ordinal);

        /// <summary>Unloadable per-profile zone names, to the profiles that named them.</summary>
        public SortedDictionary<string, SortedSet<string>> InvalidProfileZones { get; } =
            new(StringComparer.Ordinal);

        /// <summary>The configured global zone when it will not load and at least one section
        /// this pass relied on it, else null. Preferred over a bad profile zone in spec 10.9's
        /// one <c>phd2_timezone_invalid</c> event: it stands behind every profile that names no
        /// zone of its own, so it is the larger fact and the one to fix first.</summary>
        public string? InvalidGlobalZone =>
            !_globalLoads && GlobalZoneProfiles.Count > 0 ? _globalZone : null;

        public void Observe(
            string? profile,
            string zoneSource,
            double? pixelScaleArcsec,
            IReadOnlyDictionary<string, Phd2ProfileEntry> profileMap)
        {
            var label = Label(profile);

            if (zoneSource == Phd2ResolutionSource.Unset)
            {
                UnsetZoneProfiles.Add(label);
            }

            if (zoneSource != Phd2ResolutionSource.Profile)
            {
                // ResolveTimezone reports `profile` only for an own zone that is non-empty AND
                // loadable, so an own zone here is by construction one that will not load.
                var own = profileMap.GetValueOrDefault(profile ?? "")?.Timezone ?? "";
                if (own.Length > 0)
                {
                    if (!InvalidProfileZones.TryGetValue(own, out var named))
                    {
                        named = new SortedSet<string>(StringComparer.Ordinal);
                        InvalidProfileZones[own] = named;
                    }
                    named.Add(label);
                }

                if (_globalZone.Length > 0)
                {
                    GlobalZoneProfiles.Add(label);
                }
            }

            if (pixelScaleArcsec is null)
            {
                NoPixelScaleProfiles.Add(label);
                NoPixelScaleSessions++;
            }
        }

        private static string Label(string? profile)
            => string.IsNullOrEmpty(profile) ? NoProfileLabel : profile;
    }

    // ---- activity events (spec 10.9) -------------------------------------------------------
    // Written on one short-lived tracking context and parented to the run's scan_started event,
    // exactly as ScanCoordinator.RunOrphanPruning writes the frame side's three. The guide-log
    // pass runs after the frame ingest, so ScanWriter's writer task no longer exists by the time
    // any of these is raised; ActivityRepository.Emit is the one spine either way.
    //
    // Every event type and every key of every details document is snake_case.
    /// <summary>
    /// The three <c>phd2_orphan_prune_*</c> events of spec 10.9, written and saved BEFORE the
    /// deletion they describe and deliberately NOT guarded: a failure here propagates, the run is
    /// recorded failed, and no guide-log row goes. This is the audit half of the pass and the one
    /// record spec 10.9 requires of a forced prune, so it is not allowed to be the half that gets
    /// swallowed. <see cref="EmitEvents"/> keeps its swallow for the informational half.
    /// </summary>
    private static void EmitOrphanEvents(
        string connectionString, int? parentActivityId, OrphanPrunePlan drop, bool force)
    {
        if (drop.SkippedRoots.Count == 0 && drop.LimitedRoots.Count == 0)
        {
            return;
        }

        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(connectionString, tracking: true));

        foreach (var skipped in drop.SkippedRoots)
        {
            ActivityRepository.Emit(
                context, "scan", "warning", "phd2_orphan_prune_skipped",
                $"Guide-log cleanup skipped for {skipped.Root}: the scan discovered no guide " +
                $"log there, but {Count(skipped.KnownRows, "guide log")} " +
                $"{(skipped.KnownRows == 1 ? "remains" : "remain")} catalogued under it",
                new { root = skipped.Root, known_rows = skipped.KnownRows },
                parentId: parentActivityId);
        }

        foreach (var limited in drop.LimitedRoots)
        {
            var percent = limited.KnownRows == 0
                ? 0d
                : Math.Round(100d * limited.MissingRows / limited.KnownRows, 1);

            if (force)
            {
                ActivityRepository.Emit(
                    context, "scan", "warning", "phd2_orphan_prune_forced",
                    // Invariant, explicitly, exactly as the frame side's own copy of this message
                    // is: a plain interpolation of a double takes the current culture, so on a
                    // comma-decimal machine the feed would read "12,5%" beside a details document
                    // whose JSON double always reads 12.5.
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Forced cleanup removed {limited.MissingRows} of {limited.KnownRows} " +
                        $"guide-log rows under {limited.Root} ({percent}% of that root), past the " +
                        $"safety limit"),
                    new
                    {
                        root = limited.Root,
                        removed = limited.MissingRows,
                        known_rows = limited.KnownRows,
                        percent_removed = percent,
                        forced = true,
                    },
                    parentId: parentActivityId);
                continue;
            }

            ActivityRepository.Emit(
                context, "scan", "warning", "phd2_orphan_prune_limited",
                $"Guide-log cleanup skipped for {limited.Root}: {limited.MissingRows} of " +
                $"{limited.KnownRows} catalogued guide logs were missing, at or past the " +
                "safety limit",
                new
                {
                    root = limited.Root,
                    missing_rows = limited.MissingRows,
                    known_rows = limited.KnownRows,
                },
                parentId: parentActivityId);
        }

        context.SaveChanges();
    }

    /// <summary>Writes the pass's activity rows. Returns false when the write failed, which is
    /// what stops the correlation subtracting warnings the feed never received (fix-wave review
    /// P3-6).</summary>
    private static bool EmitEvents(
        string connectionString,
        int? parentActivityId,
        Phd2PassResult result,
        PassNotices notices,
        int durationMs,
        bool cancelled,
        Action<string>? warn)
    {
        try
        {
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(connectionString, tracking: true));

            foreach (var failure in result.Failures)
            {
                ActivityRepository.Emit(
                    context, "scan", "warning", "phd2_log_failed",
                    $"PHD2 guide log {failure.Path} could not be read ({failure.ParseStatus})",
                    new { path = failure.Path, parse_status = failure.ParseStatus, reason = failure.Reason },
                    parentId: parentActivityId);
            }

            if (notices.UnsetZoneProfiles.Count > 0)
            {
                ActivityRepository.Emit(
                    context, "scan", "warning", "phd2_timezone_unset",
                    // Counted BEFORE the cap (review P3-7): the cap bounds what the details
                    // document lists, and a corpus with more than 50 unzoned profiles is exactly
                    // the one whose message must not understate how many there are.
                    $"No timezone resolved for {Count(notices.UnsetZoneProfiles.Count, "equipment profile")} " +
                    "in this pass, so those guiding sessions carry no UTC instant and are not " +
                    "correlated",
                    new { profiles = result.TimezoneUnsetProfiles, setting = ProfileMapSetting },
                    parentId: parentActivityId);
            }

            // Once per pass, one event, whatever the mix (spec 10.9's own "once per pass").
            var globalZone = notices.InvalidGlobalZone;
            if (globalZone is not null)
            {
                ActivityRepository.Emit(
                    context, "scan", "warning", "phd2_timezone_invalid",
                    $"The observer timezone {globalZone} is not a zone this machine can load; " +
                    "guide-log timestamps were left without a UTC instant",
                    new { zone = globalZone, scope = "global", profiles = Cap(notices.GlobalZoneProfiles) },
                    parentId: parentActivityId);
            }
            else if (notices.InvalidProfileZones.Count > 0)
            {
                var first = notices.InvalidProfileZones.First();
                var affected = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var named in notices.InvalidProfileZones.Values)
                {
                    affected.UnionWith(named);
                }

                ActivityRepository.Emit(
                    context, "scan", "warning", "phd2_timezone_invalid",
                    $"The profile timezone {first.Key} is not a zone this machine can load and was " +
                    "ignored",
                    new { zone = first.Key, scope = "profile", profiles = Cap(affected) },
                    parentId: parentActivityId);
            }

            if (notices.NoPixelScaleProfiles.Count > 0)
            {
                ActivityRepository.Emit(
                    context, "scan", "warning", "phd2_pixel_scale_missing",
                    $"{Count(notices.NoPixelScaleSessions, "guiding session")} carried no pixel " +
                    "scale, so their guiding can never be converted to arcseconds and they are " +
                    "never correlated",
                    new
                    {
                        profiles = result.PixelScaleMissingProfiles,
                        session_count = notices.NoPixelScaleSessions,
                    },
                    parentId: parentActivityId);
            }

            // COORDINATOR RULING (fixer-p15a-e escalation): a library with nothing to say about
            // guide logs stays silent in the feed. The key ships ON, so before this ruling every
            // scan of every library that has never seen PHD2 wrote this row, for ever, saying
            // "No PHD2 guide logs found". That is one line per scan interval telling a user about
            // a feature they do not use, which is how an Activity feed stops being read.
            //
            // "Something to say" is the pass's own five counters: a log found under a walked root,
            // one ingested, one failed, one skipped as unchanged, or a stored row dropped. Found
            // covers the discovery side and SkippedUnchanged plus Removed cover the stored side,
            // so a library that HAS a guide-log corpus keeps its row on every scan even when
            // nothing changed, which is the reader's evidence that the pass is still running.
            // A pass the user stopped says so in its message; the run's own scan_cancelled carries
            // the rest of the truth.
            if (result.Found > 0 || result.Ingested > 0 || result.Failed > 0
                || result.SkippedUnchanged > 0 || result.Removed > 0)
            {
                ActivityRepository.Emit(
                    context, "scan", "info", "phd2_pass_complete",
                    cancelled ? $"PHD2 guide log pass stopped: {Terminal(result)}" : Terminal(result),
                    new
                    {
                        found = result.Found,
                        ingested = result.Ingested,
                        skipped_unchanged = result.SkippedUnchanged,
                        empty = result.Empty,
                        failed = result.Failed,
                        removed = result.Removed,
                        duration_ms = durationMs,
                    },
                    parentId: parentActivityId, durationMs: durationMs);
            }

            context.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            // The rows the pass stored are the durable record of it; these events are the feed's
            // readable copy. A failure writing the copy must not undo the pass or replace what
            // the scan is already reporting.
            warn?.Invoke($"Could not write the PHD2 pass activity events: {ex.Message}");
            return false;
        }
    }

    private static IReadOnlyList<string> Cap(IEnumerable<string> profiles)
        => [.. profiles.Take(ProfileCap)];

    private static string Count(int value, string noun)
        => string.Create(CultureInfo.InvariantCulture, $"{value} {noun}{(value == 1 ? "" : "s")}");

    private static string Progress(int checkedFiles, int found)
        => string.Create(CultureInfo.InvariantCulture, $"Read {checkedFiles}/{found} PHD2 guide logs");

    // Spec 10.3 step 3's per-section progress line. The file numerator stays so a reader keeps
    // their place in the pass; the section numerator is what moves inside one large log.
    private static string Sections(int checkedFiles, int found, int done, int total)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Reading PHD2 guide log {checkedFiles}/{found}: guiding section {done}/{total}");

    private static string Terminal(Phd2PassResult result)
        => result.Found == 0
            ? "No PHD2 guide logs found"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"PHD2 guide logs: {result.Ingested} ingested, {result.SkippedUnchanged} unchanged, " +
                $"{result.Empty} empty, {result.Failed} failed, {result.Removed} removed");
}
