using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>
/// Spec 5.20: one stored value slot, keyed by column plus the four parts of
/// <see cref="GalactiLog.Data.Queries.CustomValueKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// The scope determines which of <see cref="TargetId"/>, <see cref="MosaicId"/>,
/// <see cref="SessionDate"/> and <see cref="RigLabel"/> are set; the rest are SQL null, never an
/// empty string and never a sentinel date (core-shapes.md section 5.3's key table).
/// </para>
/// <para>
/// <see cref="MosaicId"/> carries no foreign key and no reader (U1). It exists so the phase that
/// builds mosaics adds the key, its cascade and the fourth scope and nothing else. It is null on
/// every row this phase can write, and a case proves it.
/// </para>
/// <para>No <c>updated_by</c> (spec 2.3).</para>
/// </remarks>
[Table("custom_column_values")]
public sealed class CustomColumnValue
{
    [Column("id")] public Guid Id { get; set; }
    [Column("column_id")] public Guid ColumnId { get; set; }
    [Column("target_id")] public Guid? TargetId { get; set; }
    [Column("mosaic_id")] public Guid? MosaicId { get; set; }
    [Column("session_date")] public DateOnly? SessionDate { get; set; }
    [Column("rig_label")] public string? RigLabel { get; set; }
    [Column("value")] public string Value { get; set; } = "";
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}
