using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("merge_manifests")]
public class MergeManifest
{
    [Column("id")] public Guid Id { get; set; }
    [Column("winner_id")] public Guid WinnerId { get; set; }
    [Column("loser_id")] public Guid? LoserId { get; set; }
    [Column("payload")] public string Payload { get; set; } = "{}";
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}
