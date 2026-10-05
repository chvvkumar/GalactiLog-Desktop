using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("activity_events")]
public class ActivityEvent
{
    [Column("id")] public int Id { get; set; }
    [Column("timestamp")] public DateTime Timestamp { get; set; }
    [Column("severity")] public string Severity { get; set; } = "info";
    [Column("category")] public string Category { get; set; } = "";
    [Column("event_type")] public string EventType { get; set; } = "";
    [Column("message")] public string Message { get; set; } = "";
    [Column("details")] public string? Details { get; set; }
    [Column("target_id")] public Guid? TargetId { get; set; }
    [Column("duration_ms")] public int? DurationMs { get; set; }
    [Column("parent_id")] public int? ParentId { get; set; }
}
