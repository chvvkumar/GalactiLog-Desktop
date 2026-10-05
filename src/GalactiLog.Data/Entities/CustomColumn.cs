using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>
/// Spec 5.19: one row per user-defined column.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ColumnType"/> and <see cref="AppliesTo"/> are the stored lower-case words, not the
/// enums: the column is TEXT and a hand-edited row must read back without throwing, which an enum
/// conversion would not allow. <see cref="GalactiLog.Core.Settings.CustomColumnSlug.ParseType"/>
/// and <c>ParseScope</c> are how a reader gets the enum, and a null answer is a row the repository
/// skips.
/// </para>
/// <para>
/// <see cref="DropdownOptions"/> is a JSON array of strings, or null for the other two types. One
/// TEXT column rather than the web's Postgres <c>ARRAY(String)</c>, the same argument spec 5.18
/// makes for <c>phd2_calibrations.steps</c>.
/// </para>
/// <para>No <c>created_by</c> (spec 2.3, there are no users).</para>
/// </remarks>
[Table("custom_columns")]
public sealed class CustomColumn
{
    [Column("id")] public Guid Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("slug")] public string Slug { get; set; } = "";
    [Column("column_type")] public string ColumnType { get; set; } = "";
    [Column("applies_to")] public string AppliesTo { get; set; } = "";
    [Column("dropdown_options")] public string? DropdownOptions { get; set; }
    [Column("display_order")] public int DisplayOrder { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}
