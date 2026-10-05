using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>
/// Spec 5.17: one row per guiding CSV row. <b>Pixels only.</b> Eighteen columns.
/// </summary>
/// <remarks>
/// <para>
/// Arcseconds are derived at read time from the owning session's <c>pixel_scale_arcsec</c>, so a
/// corrected pixel scale never requires rewriting frame rows. This is the volume table, roughly
/// 390,000 rows for a six-month corpus, and it is kept narrow on purpose (ruling F2).
/// </para>
/// <para>
/// The row's <c>XStep</c> and <c>YStep</c> columns are parsed past and not stored, as in the web
/// model. A DROP row's quoted nineteenth field, the star-loss reason, is not stored per frame
/// either: it is counted into <c>phd2_sessions.star_lost_reasons</c>.
/// </para>
/// <para>
/// No navigation property in either direction; see <see cref="Phd2Log"/>. On this table above all,
/// because a navigation is what turns a per-session read into a lazy load over the whole corpus.
/// </para>
/// </remarks>
[Table("phd2_frames")]
public sealed class Phd2Frame
{
    /// <summary>Autoincrement, not a composite natural key: frame numbers skip across DROP gaps
    /// and are not unique inside a truncated section (spec 5.17).</summary>
    [Column("id")] public long Id { get; set; }

    [Column("session_id")] public Guid SessionId { get; set; }

    /// <summary>The row's <c>Frame</c> column.</summary>
    [Column("frame_index")] public int FrameIndex { get; set; }

    /// <summary>Seconds since the section began, the row's <c>Time</c> column.</summary>
    [Column("time_offset")] public double TimeOffset { get; set; }

    /// <summary>Pixels.</summary>
    [Column("dx")] public double? Dx { get; set; }

    /// <summary>Pixels.</summary>
    [Column("dy")] public double? Dy { get; set; }

    /// <summary><c>RARawDistance</c>, pixels. The RMS input.</summary>
    [Column("ra_raw")] public double? RaRaw { get; set; }

    /// <summary><c>DECRawDistance</c>, pixels. The RMS input.</summary>
    [Column("dec_raw")] public double? DecRaw { get; set; }

    /// <summary><c>RAGuideDistance</c>, pixels.</summary>
    [Column("ra_guide")] public double? RaGuide { get; set; }

    /// <summary><c>DECGuideDistance</c>, pixels.</summary>
    [Column("dec_guide")] public double? DecGuide { get; set; }

    [Column("ra_duration_ms")] public int RaDurationMs { get; set; }

    /// <summary><c>W</c>, <c>E</c> or empty.</summary>
    [Column("ra_direction")] public string RaDirection { get; set; } = "";

    [Column("dec_duration_ms")] public int DecDurationMs { get; set; }

    /// <summary><c>N</c>, <c>S</c> or empty.</summary>
    [Column("dec_direction")] public string DecDirection { get; set; } = "";

    [Column("star_mass")] public double? StarMass { get; set; }
    [Column("snr")] public double? Snr { get; set; }
    [Column("error_code")] public int? ErrorCode { get; set; }

    /// <summary>True when the row's mount field is <c>DROP</c>.</summary>
    [Column("dropped")] public bool Dropped { get; set; }
}
