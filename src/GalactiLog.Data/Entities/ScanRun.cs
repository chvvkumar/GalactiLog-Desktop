using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("scan_runs")]
public class ScanRun
{
    [Column("id")] public int Id { get; set; }
    [Column("started_at")] public DateTime StartedAt { get; set; }
    [Column("finished_at")] public DateTime? FinishedAt { get; set; }
    [Column("trigger")] public string Trigger { get; set; } = "";
    [Column("state")] public string State { get; set; } = "running";
    [Column("discovered")] public int Discovered { get; set; }
    [Column("new_files")] public int NewFiles { get; set; }
    [Column("changed_files")] public int ChangedFiles { get; set; }
    [Column("completed")] public int Completed { get; set; }
    [Column("failed")] public int Failed { get; set; }
    [Column("skipped_calibration")] public int SkippedCalibration { get; set; }
    [Column("removed")] public int Removed { get; set; }

    // Spec 5.13's three guide-log counters, added by the Phase 15A migration with a database
    // default of 0 so rows written by an earlier version read as a run that found no guide logs.
    // They sit between `removed` and `error_text`, which is the column order spec 5.13 gives and
    // the key order the bundle serializes.

    /// <summary>Guide-log files the walk discovered this run (spec 10.3). Zero when
    /// <c>general.phd2_scan_enabled</c> is off.</summary>
    [Column("phd2_found")] public int Phd2Found { get; set; }

    /// <summary>Guide logs parsed and stored this run.</summary>
    [Column("phd2_ingested")] public int Phd2Ingested { get; set; }

    /// <summary>Guide logs that could not be read or parsed this run.</summary>
    [Column("phd2_failed")] public int Phd2Failed { get; set; }

    [Column("error_text")] public string? ErrorText { get; set; }
}
