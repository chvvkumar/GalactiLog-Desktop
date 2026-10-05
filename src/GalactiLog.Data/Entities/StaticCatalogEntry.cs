using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("static_catalog_entries")]
public class StaticCatalogEntry
{
    [Column("catalog_name")] public string CatalogName { get; set; } = "";
    [Column("catalog_number")] public string CatalogNumber { get; set; } = "";
    [Column("ngc_name")] public string? NgcName { get; set; }
    [Column("payload")] public string Payload { get; set; } = "{}";
}
