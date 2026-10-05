using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

// Named UserSettingsRow, not UserSettings, to keep it visually distinct from the
// GalactiLog.Core.Settings record types (Task 4), which are the deserialized C# shape of
// these same JSON columns. This entity only ever holds raw JSON text.
[Table("user_settings")]
public class UserSettingsRow
{
    [Column("id")] public int Id { get; set; }
    [Column("general")] public string General { get; set; } = "{}";
    [Column("filters")] public string Filters { get; set; } = "{}";
    [Column("equipment")] public string Equipment { get; set; } = "{}";
    [Column("dismissed_suggestions")] public string DismissedSuggestions { get; set; } = "[]";
    [Column("display")] public string Display { get; set; } = "{}";
    [Column("graph")] public string Graph { get; set; } = "{}";
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}
