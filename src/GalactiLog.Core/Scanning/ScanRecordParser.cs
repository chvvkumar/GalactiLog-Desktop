using System.Globalization;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Sessions;

namespace GalactiLog.Core.Scanning;

// The CPU-bound half of spec 10.3 step 3: header read, metadata extraction, CSV backfill,
// calibration classification (7.5) and session-date derivation (8). Runs on the pipeline's
// N parallel reader tasks and performs no database access at all, not even a read (spec
// 10.6) -- everything it needs is in the file and in the settings snapshot the caller took
// at scan start.
//
// Warnings come back through onWarning as plain data, not through ILogger: same convention
// as FrameReader.TryRead's rejectionReason and FileWalker's onWarning, so nothing in Core
// decides how a message is surfaced.
public static class ScanRecordParser
{
    /// <param name="csv">
    /// The scan run's single NinaCsvReader. Its per-CSV cache is a ConcurrentDictionary, so
    /// one instance shared across every reader task parses each Session Metadata CSV exactly
    /// once no matter which reader reaches a folder first (spec 10.6).
    /// </param>
    /// <param name="imagingNightFallbackGate">
    /// One OnceGate per scan run, shared across every reader task, so spec 8.3's fallback
    /// warning fires once per run rather than once per frame.
    /// </param>
    public static ParsedRecord Parse(
        DiscoveredFile file,
        NinaCsvReader csv,
        bool includeCalibration,
        bool useImagingNight,
        double? observerLongitude,
        string? observerTimezone,
        OnceGate imagingNightFallbackGate,
        Action<string>? onWarning = null)
    {
        var fileName = Path.GetFileName(file.Path);

        // Spec 10.3 step 3's frame-side counterpart of step 5 item 3, "One bad file never
        // stops the pass". The guard wraps the whole body rather than any one step, because
        // this is the single method every frame's parse passes through: nothing here now,
        // and nothing a later phase adds inside it, can abort a scan over one file the user
        // did not write and cannot repair. A check each parse step had to remember would be
        // forgotten at step N+1.
        try
        {
            if (!FrameReader.TryRead(file.Path, csv, out var result, out var rejectionReason))
            {
                return new ParsedRecord(ParsedRecordKind.Rejected, file.Path, fileName,
                    file.FileSize, file.FileMtimeUnixSeconds, null, null, null, rejectionReason);
            }

            var metadata = result.Metadata;

            // Spec 7.5: decided here, with the header already open, rather than during the walk.
            if (!includeCalibration && CalibrationFrames.IsCalibrationFrame(metadata.ImageType))
            {
                return new ParsedRecord(ParsedRecordKind.SkippedCalibration, file.Path, fileName,
                    file.FileSize, file.FileMtimeUnixSeconds, null, null, null, null);
            }

            // ExtractedMetadata.CaptureDate is already the normalized UTC ISO-8601 string spec
            // 7.1.2 defines; this is a mechanical round trip back to a DateTime, not a second
            // implementation of the offset rule. Parsed once here so ScanWriter does not parse
            // the same string again on the writer task.
            DateTime? captureUtc = null;
            if (metadata.CaptureDate is { } captureText
                && DateTime.TryParse(captureText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                captureUtc = parsed;
            }

            DateOnly? sessionDate = null;
            if (captureUtc is { } capture)
            {
                var (longitude, usedFallback) = SessionDate.ResolveLongitude(
                    metadata.RawHeaders, observerLongitude, observerTimezone, capture);

                // Only a run that actually asked for imaging-night grouping is degraded by the
                // fallback, so only that run warns about it (spec 8.3 step 4).
                if (useImagingNight && usedFallback && imagingNightFallbackGate.TryFire())
                {
                    onWarning?.Invoke(SessionDate.ImagingNightFallbackWarning);
                }

                sessionDate = SessionDate.Compute(capture, useImagingNight, longitude);
            }

            return new ParsedRecord(ParsedRecordKind.Ingest, file.Path, fileName,
                file.FileSize, file.FileMtimeUnixSeconds, metadata, captureUtc, sessionDate, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cancellation is the one thing that still propagates: Parse takes no token, so the
            // only OperationCanceledException that can arise here comes from the caller's own
            // onWarning delegate, and a cancelled scan must stay cancelled.
            //
            // ex.Message, never ex.ToString(): this reason reaches the Activity feed and the
            // activity_events.details JSON, which are no place for a stack trace. No ILogger
            // line either -- the `file_rejected` event is the per-file record, the same as it
            // is for every other rejection class (spec 10.9).
            return new ParsedRecord(ParsedRecordKind.Rejected, file.Path, fileName,
                file.FileSize, file.FileMtimeUnixSeconds, null, null, null,
                $"unexpected error while reading the file: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
