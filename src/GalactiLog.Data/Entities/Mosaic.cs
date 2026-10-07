using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>Spec 5.22: one mosaic project, a name, notes and the arranger's global rotation. It
/// owns its panels (spec 5.23) and through them their nights (spec 5.24); it owns no frame.</summary>
[Table("mosaics")]
public sealed class Mosaic
{
    [Column("id")] public Guid Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("notes")] public string? Notes { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
    [Column("rotation_angle")] public double RotationAngle { get; set; }
}
