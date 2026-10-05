using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("catalog_cache")]
public class CatalogCacheEntry
{
    [Column("source")] public string Source { get; set; } = "";
    [Column("key")] public string Key { get; set; } = "";
    [Column("payload")] public string? Payload { get; set; }
    [Column("negative")] public bool Negative { get; set; }
    [Column("fetched_at")] public DateTime FetchedAt { get; set; }
}
