using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("openngc_catalog")]
public class OpenNgcCatalogEntry
{
    [Column("name")] public string Name { get; set; } = "";
    [Column("type")] public string? Type { get; set; }
    [Column("ra")] public double? Ra { get; set; }
    [Column("dec")] public double? Dec { get; set; }
    [Column("constellation")] public string? Constellation { get; set; }
    [Column("major_axis")] public double? MajorAxis { get; set; }
    [Column("minor_axis")] public double? MinorAxis { get; set; }
    [Column("position_angle")] public double? PositionAngle { get; set; }
    [Column("b_mag")] public double? BMag { get; set; }
    [Column("v_mag")] public double? VMag { get; set; }
    [Column("surface_brightness")] public double? SurfaceBrightness { get; set; }
    [Column("common_names")] public string? CommonNames { get; set; }
    [Column("messier")] public string? Messier { get; set; }
}
