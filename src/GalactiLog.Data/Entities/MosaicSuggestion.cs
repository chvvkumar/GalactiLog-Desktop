using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>Spec 5.25: one mosaic candidate a detection run produced (spec 7.7). The JSON columns
/// are written by <c>MosaicRepository.ReplacePending</c> and read by <c>ListPending</c> only.
/// </summary>
[Table("mosaic_suggestions")]
public sealed class MosaicSuggestion
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    [Column("id")] public Guid Id { get; set; }
    [Column("suggested_name")] public string SuggestedName { get; set; } = "";
    [Column("base_name")] public string BaseName { get; set; } = "";
    [Column("target_ids")] public string TargetIds { get; set; } = "[]";
    [Column("panel_labels")] public string PanelLabels { get; set; } = "[]";
    [Column("panel_patterns")] public string PanelPatterns { get; set; } = "[]";
    [Column("session_dates")] public string SessionDates { get; set; } = "[]";
    [Column("status")] public string Status { get; set; } = Pending;
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("confidence")] public string Confidence { get; set; } = "";
    [Column("discovery_source")] public string DiscoverySource { get; set; } = "";
    [Column("geometry")] public string? Geometry { get; set; }
    [Column("flags")] public string Flags { get; set; } = "[]";
    [Column("dedup_signature")] public string DedupSignature { get; set; } = "";
}
