using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("merge_candidates")]
public class MergeCandidate
{
    [Column("id")] public Guid Id { get; set; }
    [Column("source_name")] public string SourceName { get; set; } = "";
    [Column("source_image_count")] public int SourceImageCount { get; set; }
    [Column("suggested_target_id")] public Guid? SuggestedTargetId { get; set; }
    [Column("similarity_score")] public double SimilarityScore { get; set; }
    [Column("method")] public string Method { get; set; } = "";
    [Column("status")] public string Status { get; set; } = "pending";
    [Column("reason_text")] public string? ReasonText { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("resolved_at")] public DateTime? ResolvedAt { get; set; }
}
