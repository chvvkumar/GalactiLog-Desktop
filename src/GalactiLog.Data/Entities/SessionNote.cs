using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("session_notes")]
public class SessionNote
{
    [Column("id")] public Guid Id { get; set; }
    [Column("target_id")] public Guid TargetId { get; set; }
    [Column("session_date")] public DateOnly SessionDate { get; set; }
    [Column("notes")] public string Notes { get; set; } = "";
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}
