using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("target_catalog_memberships")]
public class TargetCatalogMembership
{
    [Column("id")] public int Id { get; set; }
    [Column("target_id")] public Guid TargetId { get; set; }
    [Column("catalog_name")] public string CatalogName { get; set; } = "";
    [Column("catalog_number")] public string CatalogNumber { get; set; } = "";
    [Column("metadata")] public string? Metadata { get; set; }
}
