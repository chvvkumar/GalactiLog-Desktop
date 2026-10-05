using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("targets")]
public class Target
{
    [Column("id")] public Guid Id { get; set; }
    [Column("primary_name")] public string PrimaryName { get; set; } = "";
    [Column("catalog_id")] public string? CatalogId { get; set; }
    [Column("catalog_id_normalized")] public string? CatalogIdNormalized { get; set; }
    [Column("common_name")] public string? CommonName { get; set; }
    [Column("aliases")] public string Aliases { get; set; } = "[]";
    [Column("ra")] public double? Ra { get; set; }
    [Column("dec")] public double? Dec { get; set; }
    [Column("object_type")] public string? ObjectType { get; set; }
    [Column("constellation")] public string? Constellation { get; set; }
    [Column("size_major")] public double? SizeMajor { get; set; }
    [Column("size_minor")] public double? SizeMinor { get; set; }
    [Column("position_angle")] public double? PositionAngle { get; set; }
    [Column("v_mag")] public double? VMag { get; set; }
    [Column("surface_brightness")] public double? SurfaceBrightness { get; set; }
    [Column("sac_description")] public string? SacDescription { get; set; }
    [Column("sac_notes")] public string? SacNotes { get; set; }
    [Column("notes")] public string? Notes { get; set; }
    [Column("reference_thumbnail_path")] public string? ReferenceThumbnailPath { get; set; }
    [Column("merged_into_id")] public Guid? MergedIntoId { get; set; }
    [Column("merged_at")] public DateTime? MergedAt { get; set; }
    [Column("name_locked")] public bool NameLocked { get; set; }
    [Column("user_defined")] public bool UserDefined { get; set; }
}
