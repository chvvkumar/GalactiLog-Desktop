using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>Spec 5.23: a panel is a label plus its arranger layout. It names no target, because
/// its nights may come from any target (ruling R19).</summary>
[Table("mosaic_panels")]
public sealed class MosaicPanel
{
    [Column("id")] public Guid Id { get; set; }
    [Column("mosaic_id")] public Guid MosaicId { get; set; }
    [Column("panel_label")] public string PanelLabel { get; set; } = "";
    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("canvas_x")] public double? CanvasX { get; set; }
    [Column("canvas_y")] public double? CanvasY { get; set; }
    [Column("rotation")] public int Rotation { get; set; }
    [Column("flip_h")] public bool FlipH { get; set; }
}
