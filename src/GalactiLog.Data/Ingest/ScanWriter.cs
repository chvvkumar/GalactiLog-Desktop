using System.Text.Json;
using System.Threading.Channels;
using GalactiLog.Core;
using GalactiLog.Core.Scanning;
using GalactiLog.Data.Repositories;

namespace GalactiLog.Data.Ingest;

// The single DB-facing consumer of the scan pipeline (spec 5.1's "exactly one writer during
// a scan", spec 10.6). Drains the bounded ParsedRecord channel sequentially and performs,
// per record and in this order: image upsert, target resolution (offline lookup, cache read,
// online call if needed, cache write), target create if the identity is new, catalog
// membership rows (both inside TargetResolver), activity events. SaveChanges runs once per
// 200 records and once at the end.
//
// Because that whole sequence runs on one task, no two records can create the same target
// concurrently, which is why there is no per-name lock and no unique-violation retry here.
//
// Never touches the filesystem: a rejected or vanished file produces a row/counter/event,
// never a delete or a rename.
public sealed class ScanWriter
{
    // Spec 10.6. The bound between the parallel readers and this writer is the pipeline's
    // only backpressure: it is what stops readers outrunning a writer stalled on a slow
    // SIMBAD call and exhausting memory on a large library.
    public const int ChannelCapacity = 256;

    private const int SaveChangesBatchSize = 200;

    private readonly GalactiLogContext _context;
    private readonly TargetResolver _resolver;
    private readonly int? _scanStartedActivityId;
    private readonly Action<string>? _onWarning;

    // Spec 10.3 step 3: general.mosaic_keywords read once at the start of the run, so a change
    // made while a scan runs applies from the next run.
    private readonly IReadOnlyList<string> _mosaicKeywords;

    // Per-scan-run circuit breaker (Phase 3 ruling): the first transient network failure
    // disables SIMBAD/SESAME for the rest of the run and logs once. A OnceGate rather than
    // the bool alone because the warning must fire exactly once even though every subsequent
    // unresolvable name would otherwise re-trip it.
    private readonly OnceGate _circuitBreakerGate = new();
    private bool _onlineDisabled;

    public ScanWriter(
        GalactiLogContext context,
        TargetResolver resolver,
        int? scanStartedActivityId = null,
        Action<string>? onWarning = null,
        IReadOnlyList<string>? mosaicKeywords = null)
    {
        _context = context;
        _resolver = resolver;
        _scanStartedActivityId = scanStartedActivityId;
        _onWarning = onWarning;
        _mosaicKeywords = mosaicKeywords ?? new Core.Settings.GeneralSettings().MosaicKeywords;
    }

    // Read once by the coordinator after RunAsync completes, to build the scan_runs row
    // (spec 10.4). Written only on the writer task, so plain fields, no Interlocked.
    public int Completed { get; private set; }
    public int Failed { get; private set; }
    public int SkippedCalibration { get; private set; }

    // True once a transient network failure has tripped the breaker in this run.
    public bool OnlineResolutionDisabled => _onlineDisabled;

    /// <summary>
    /// Drains <paramref name="records"/> until the channel completes, or until
    /// <paramref name="ct"/> fires between records. On cancellation the partial batch is
    /// flushed and the loop stops: every record already processed stays processed and the
    /// database is left consistent (spec 10.5). Cancellation is not rethrown -- the caller
    /// owns the token and already knows.
    /// </summary>
    /// <param name="onRecordProcessed">Feeds the coordinator's `ingest` step counter.</param>
    public async Task RunAsync(ChannelReader<ParsedRecord> records, Action? onRecordProcessed, CancellationToken ct)
    {
        var sinceLastSave = 0;

        try
        {
            await foreach (var record in records.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Spec 10.5's per-file checkpoint, and not redundant: ReadAllAsync only
                // observes the token when it has to WAIT, so a channel already holding
                // hundreds of buffered records would otherwise drain to the end after a
                // cancellation. Breaking here falls through to the flush below.
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                switch (record.Kind)
                {
                    case ParsedRecordKind.Rejected:
                        Failed++;
                        EmitActivity("scan", "warning", "file_rejected",
                            $"Rejected {record.FileName}: {record.RejectionReason}",
                            new { path = record.FilePath, reason = record.RejectionReason });
                        break;

                    // Spec 7.5 and tasks_scan._do_ingest: a skipped calibration frame counts
                    // as completed AND as skipped_calibration, and writes no images row. Its
                    // skipped_files row (spec 5.21) is what keeps the next scan from reading it.
                    case ParsedRecordKind.SkippedCalibration:
                        Completed++;
                        SkippedCalibration++;
                        RecordSkipped(record);
                        break;

                    case ParsedRecordKind.Ingest:
                        WriteImage(record, ct);
                        ForgetSkipped(record.FilePath);
                        Completed++;
                        break;
                }

                onRecordProcessed?.Invoke();

                if (++sinceLastSave >= SaveChangesBatchSize)
                {
                    Flush();
                    sinceLastSave = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fall through to the final flush: spec 10.5's "every file already ingested
            // stays ingested". A genuine exception is deliberately NOT caught here -- it
            // propagates with the partial batch unsaved rather than committing work done in
            // an unknown state.
        }

        Flush();
    }

    private void Flush()
    {
        _context.SaveChanges();

        // Keeps the long-lived context's change tracker bounded to one batch. Without this a
        // 50k-frame library would leave 50k Image entities tracked for the whole run, which
        // both grows without limit and makes every subsequent DetectChanges pass slower.
        _context.ChangeTracker.Clear();
    }

    private void WriteImage(ParsedRecord record, CancellationToken ct)
    {
        // Resolution runs BEFORE the row is touched. Resolve can end in
        // OperationCanceledException (a cancelled online fetch or backoff), and RunAsync's
        // cancellation path flushes deliberately -- so an entity Added first would be
        // committed as a targetless row whose Completed was never incremented, and the next
        // scan would skip it as unchanged. Resolving first means a cancelled record leaves
        // the context exactly as it found it.
        var objectName = record.Metadata!.ObjectName;
        TargetResolver.ResolutionResult? resolution = null;
        if (!string.IsNullOrWhiteSpace(objectName))
        {
            resolution = _resolver.Resolve(objectName, createIfMissing: true, dryRun: false,
                skipOnline: _onlineDisabled, ct: ct);
        }

        // Local-first so two records for the same path inside one unsaved batch update the
        // same entity instead of racing the unique index. The tracker is cleared every
        // batch, so Local never holds more than SaveChangesBatchSize entries.
        var image = _context.Images.Local.FirstOrDefault(i => i.FilePath == record.FilePath)
            ?? _context.Images.SingleOrDefault(i => i.FilePath == record.FilePath);

        if (image is null)
        {
            image = new Entities.Image { Id = Guid.NewGuid(), FilePath = record.FilePath };
            _context.Images.Add(image);
        }

        MapMetadata(image, record);

        // Phase 18 (rulings R6 and R10): the token rule over OBJECT for a LIGHT frame, null for
        // any other. Mosaic detection step 0 recomputes it with the keywords then in force.
        image.PanelLabel = record.Metadata.ImageType == "LIGHT"
            ? MosaicDetectionPass.LabelFor(objectName, _mosaicKeywords)
            : null;

        // Spec 5.2: an empty OBJECT is a legitimate state, not a resolution failure. Nothing
        // was looked up, so no event and no target.
        image.ResolvedTargetId = resolution?.TargetId;
        if (resolution is null)
        {
            return;
        }

        if (resolution.TransientNetworkFailure && _circuitBreakerGate.TryFire())
        {
            _onlineDisabled = true;
            _onWarning?.Invoke(
                "Online target resolution (SIMBAD/SESAME) failed transiently; disabling it for " +
                "the rest of this scan run. Offline catalog lookup and the local cache still apply.");
        }

        // Spec 10.9 says this event fires when a name "resolved to nothing and was
        // negative-cached" -- so it is gated on the negative-cache WRITE, not on the
        // Unresolved stage. Without that gate, one bad OBJECT string shared by 50k frames
        // writes 50k identical rows, and a transient network failure or a circuit-broken
        // record (neither of which negative-caches, precisely because the name was never
        // checked) would be reported as a resolution failure it is not.
        if (resolution.NegativeCached)
        {
            EmitActivity("enrichment", "warning", "resolution_failed",
                $"Could not resolve target for '{objectName}'",
                new { object_name = objectName });
        }
        else if (resolution.Created)
        {
            EmitActivity("enrichment", "info", "target_created",
                $"Created target '{resolution.Identity!.PrimaryName}'",
                new
                {
                    primary_name = resolution.Identity.PrimaryName,
                    catalog_id = resolution.Identity.CatalogId,
                    // One spelling, shared with the CLI resolve verb: ToString().ToLowerInvariant()
                    // wrote "solarsystem" here while the CLI printed "solar_system".
                    source = TargetResolver.StageName(resolution.Stage),
                });
        }
    }

    // The same local-first two-step WriteImage uses, for the same reason.
    private Entities.SkippedFile? FindSkipped(string filePath)
        => _context.SkippedFiles.Local.FirstOrDefault(s => s.FilePath == filePath)
            ?? _context.SkippedFiles.SingleOrDefault(s => s.FilePath == filePath);

    private void RecordSkipped(ParsedRecord record)
    {
        var skipped = FindSkipped(record.FilePath);
        if (skipped is null)
        {
            skipped = new Entities.SkippedFile { FilePath = record.FilePath };
            _context.SkippedFiles.Add(skipped);
        }

        skipped.FileSize = record.FileSize;
        skipped.FileMtime = record.FileMtimeUnixSeconds;
        skipped.Reason = "calibration";
        skipped.RecordedAt = DateTime.UtcNow;
    }

    // A frame ingested after the user turns calibration on leaves skipped_files (spec 5.21).
    private void ForgetSkipped(string filePath)
    {
        var skipped = FindSkipped(filePath);
        if (skipped is not null)
        {
            _context.SkippedFiles.Remove(skipped);
        }
    }

    // Routed through the one activity-event spine (Task 6): the row is added to THIS
    // writer's context, so it rides the same SaveChanges batch as the record that produced
    // it (spec 10.9's "in sequence with the record"). Every sub-event of a scan hangs off the
    // scan_started row so the activity feed can collapse a whole scan into one expandable
    // entry.
    private void EmitActivity(string category, string severity, string eventType, string message, object details)
        => ActivityRepository.Emit(
            _context, category, severity, eventType, message, details, parentId: _scanStartedActivityId);

    private static void MapMetadata(Entities.Image image, ParsedRecord record)
    {
        var m = record.Metadata!;

        image.FileName = record.FileName;
        image.FileSize = record.FileSize;
        image.FileMtime = record.FileMtimeUnixSeconds;
        image.CaptureDate = record.CaptureDateUtc;
        image.SessionDate = record.SessionDate;
        image.RawHeaders = m.RawHeaders.ToJsonString();
        image.Provenance = JsonSerializer.Serialize(m.Provenance);
        // ThumbnailPath is left as-is: Phase 8 owns it, and a re-ingest of a changed file
        // must not blank a thumbnail that is still on disk.

        image.ExposureTime = m.ExposureTime;
        image.FilterUsed = m.FilterUsed;
        image.SensorTemp = m.SensorTemp;
        image.CameraGain = m.CameraGain;
        image.ImageType = m.ImageType;
        image.Telescope = m.Telescope;
        image.Camera = m.Camera;
        image.MedianHfr = m.MedianHfr;
        image.MedianFwhm = m.MedianFwhm;
        image.Eccentricity = m.Eccentricity;
        image.EccentricitySource = m.EccentricitySource;
        image.AltitudeDeg = m.AltitudeDeg;
        image.ArcsecPerPixel = m.ArcsecPerPixel;
        image.RaDeg = m.RaDeg;
        image.DecDeg = m.DecDeg;
        image.WidthPx = m.WidthPx;
        image.HfrStdev = m.HfrStdev;
        image.Fwhm = m.Fwhm;
        image.DetectedStars = m.DetectedStars;
        image.GuidingRmsArcsec = m.GuidingRmsArcsec;
        image.GuidingRmsRaArcsec = m.GuidingRmsRaArcsec;
        image.GuidingRmsDecArcsec = m.GuidingRmsDecArcsec;
        image.GuidingRmsSource = m.GuidingRmsSource;
        image.AduStdev = m.AduStdev;
        image.AduMean = m.AduMean;
        image.AduMedian = m.AduMedian;
        image.AduMin = m.AduMin;
        image.AduMax = m.AduMax;
        image.FocuserPosition = m.FocuserPosition;
        image.FocuserTemp = m.FocuserTemp;
        image.RotatorPosition = m.RotatorPosition;
        image.PierSide = m.PierSide;
        image.Airmass = m.Airmass;
        image.AmbientTemp = m.AmbientTemp;
        image.DewPoint = m.DewPoint;
        image.Humidity = m.Humidity;
        image.Pressure = m.Pressure;
        image.WindSpeed = m.WindSpeed;
        image.WindDirection = m.WindDirection;
        image.WindGust = m.WindGust;
        image.CloudCover = m.CloudCover;
        image.SkyQuality = m.SkyQuality;
    }
}
