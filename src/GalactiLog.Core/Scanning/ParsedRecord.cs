using GalactiLog.Core.Metadata;

namespace GalactiLog.Core.Scanning;

public enum ParsedRecordKind
{
    // A usable frame: write/refresh its `images` row and resolve its target.
    Ingest,

    // FrameReader.TryRead refused the file (spec 6.1.5 / 6.2.6), or the parse of it threw for
    // any other reason and ScanRecordParser.Parse caught it (spec 10.3 step 3). No row;
    // counted in scan_runs.failed and emitted as a `file_rejected` activity event (spec 10.9).
    Rejected,

    // A calibration frame while general.include_calibration is false (spec 7.5). No row,
    // no thumbnail, no target resolution; counted in both `completed` and
    // `skipped_calibration`, matching tasks_scan._do_ingest.
    SkippedCalibration,
}

// Everything ScanWriter needs to write one `images` row, minus target resolution (which is
// DB-facing and therefore happens on the writer, spec 10.6).
//
// FilePath/FileSize/FileMtimeUnixSeconds come straight from the DiscoveredFile the walker
// produced. Metadata/CaptureDateUtc/SessionDate are populated only for Ingest;
// RejectionReason only for Rejected.
//
// Immutable and DB-free on purpose: this is the only thing that crosses the bounded channel
// between the parallel reader tasks and the single writer task, so it must be safe to hand
// between threads with no synchronization.
public sealed record ParsedRecord(
    ParsedRecordKind Kind,
    string FilePath,
    string FileName,
    long FileSize,
    double FileMtimeUnixSeconds,
    ExtractedMetadata? Metadata,
    DateTime? CaptureDateUtc,
    DateOnly? SessionDate,
    string? RejectionReason);
