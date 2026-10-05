using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

// Spec 5.21: a frame the header pass opened and decided not to catalogue, with the size and
// modification time it had, so the known set of spec 10.3 step 2 can skip it until it changes.
[Table("skipped_files")]
public class SkippedFile
{
    [Key, Column("file_path")] public string FilePath { get; set; } = "";
    [Column("file_size")] public long FileSize { get; set; }
    [Column("file_mtime")] public double FileMtime { get; set; }
    [Column("reason")] public string Reason { get; set; } = "";
    [Column("recorded_at")] public DateTime RecordedAt { get; set; }
}
