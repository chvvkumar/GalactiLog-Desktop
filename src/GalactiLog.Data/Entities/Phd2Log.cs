using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>
/// Spec 5.15: one row per guide-log file on disk. Twelve columns, transcribed from that table.
/// </summary>
/// <remarks>
/// No navigation property in either direction (spec 5.16, 5.17, 5.18): the foreign keys are
/// declared in <c>GalactiLogContext.OnModelCreating</c> with a cascade, and the ingest reads and
/// writes by key. A navigation onto the sessions would invite a lazy load that reaches
/// <c>phd2_frames</c>, which is the volume table.
/// <para>
/// A file with no usable content still gets a row, with <see cref="ParseStatus"/> <c>empty</c> and
/// zero counts, so it is not re-read on every scan forever. The delta skip of spec 10.3 tests
/// <see cref="FileSize"/> and <see cref="FileMtime"/> whatever the status.
/// </para>
/// </remarks>
[Table("phd2_logs")]
public sealed class Phd2Log
{
    [Column("id")] public Guid Id { get; set; }

    /// <summary>Absolute path as walked. Unique, and NOCASE, for the reason the <c>Image</c>
    /// block's comment gives.</summary>
    [Column("file_path")] public string FilePath { get; set; } = "";

    [Column("file_size")] public long? FileSize { get; set; }

    /// <summary>Unix seconds. The delta skip compares with a 1.0 second tolerance, the same
    /// tolerance <c>images</c> uses.</summary>
    [Column("file_mtime")] public double? FileMtime { get; set; }

    /// <summary>One of <c>ok</c>, <c>empty</c>, <c>unreadable</c>, <c>failed</c> (spec 5.15).
    /// </summary>
    [Column("parse_status")] public string ParseStatus { get; set; } = "";

    /// <summary>The reason, truncated to 2000 characters. Null when the status is <c>ok</c> or
    /// <c>empty</c>.</summary>
    [Column("parse_error")] public string? ParseError { get; set; }

    /// <summary>From the banner of the file's first run. Null for an ASIAIR build, which writes
    /// no version.</summary>
    [Column("phd2_version")] public string? Phd2Version { get; set; }

    [Column("log_version")] public string? LogVersion { get; set; }
    [Column("run_count")] public int RunCount { get; set; }
    [Column("session_count")] public int SessionCount { get; set; }
    [Column("calibration_count")] public int CalibrationCount { get; set; }
    [Column("parsed_at")] public DateTime ParsedAt { get; set; }
}
