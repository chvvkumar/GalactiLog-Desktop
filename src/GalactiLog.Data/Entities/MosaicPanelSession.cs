using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>Spec 5.24: one (target, night, frame label) triple a panel holds, either counted
/// (<see cref="Included"/>) or offered (<see cref="Available"/>). A null
/// <see cref="FrameLabel"/> admits the frames of that target and night that carry no label
/// (ruling R19a); a stored label is never the empty string.</summary>
[Table("mosaic_panel_sessions")]
public sealed class MosaicPanelSession
{
    public const string Included = "included";
    public const string Available = "available";

    [Column("id")] public Guid Id { get; set; }
    [Column("panel_id")] public Guid PanelId { get; set; }
    [Column("target_id")] public Guid TargetId { get; set; }
    [Column("session_date")] public DateOnly SessionDate { get; set; }
    [Column("frame_label")] public string? FrameLabel { get; set; }
    [Column("status")] public string Status { get; set; } = Available;
}
