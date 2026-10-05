using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>
/// Spec 5.18: one row per calibration block. Twenty-six columns.
/// </summary>
/// <remarks>
/// <para>
/// The per-step series is a bounded array of tens of rows that is only ever read whole, so it is
/// one JSON column (<see cref="Steps"/>) rather than a fifth table.
/// </para>
/// <para>
/// Only the West and North legs measure a rate and an angle. East and South re-centre the star and
/// emit no completion line, which is why there are six leg columns and not twelve.
/// </para>
/// <para>
/// No navigation property in either direction; see <see cref="Phd2Log"/>. This is the Data-layer
/// row, not the parser's <c>GalactiLog.Core.Phd2.Phd2Calibration</c> record of the same simple
/// name: a file that needs both qualifies them.
/// </para>
/// </remarks>
[Table("phd2_calibrations")]
public sealed class Phd2Calibration
{
    [Column("id")] public Guid Id { get; set; }
    [Column("log_id")] public Guid LogId { get; set; }

    /// <summary>The naive wall clock, as in spec 5.16. <b>Never converted</b>: no converter is
    /// declared on it and a reader must not apply the usual <c>SpecifyKind(value, Utc)</c>
    /// read-edge habit to it.</summary>
    [Column("started_at_local")] public DateTime StartedAtLocal { get; set; }

    /// <summary>Null when no zone resolved, as in spec 5.16. A UTC wall clock, never shifted; the
    /// kind is pinned at the read edge, not here.</summary>
    [Column("started_at_utc")] public DateTime? StartedAtUtc { get; set; }

    /// <summary>Derived from <see cref="StartedAtUtc"/>.</summary>
    [Column("session_date")] public DateOnly? SessionDate { get; set; }

    [Column("equipment_profile")] public string? EquipmentProfile { get; set; }

    /// <summary>Resolved from the profile map, as in spec 5.16.</summary>
    [Column("telescope")] public string? Telescope { get; set; }

    [Column("pixel_scale_arcsec")] public double? PixelScaleArcsec { get; set; }
    [Column("focal_length_mm")] public double? FocalLengthMm { get; set; }
    [Column("guide_camera")] public string? GuideCamera { get; set; }

    /// <summary>The <c>Calibration complete, mount = ...</c> line when there is one, else the
    /// header's <c>Mount</c> line.</summary>
    [Column("mount_name")] public string? MountName { get; set; }

    /// <summary>Arcseconds per second.</summary>
    [Column("ra_guide_speed")] public double? RaGuideSpeed { get; set; }

    /// <summary>Arcseconds per second.</summary>
    [Column("dec_guide_speed")] public double? DecGuideSpeed { get; set; }

    [Column("dec_deg")] public double? DecDeg { get; set; }
    [Column("hour_angle_hr")] public double? HourAngleHr { get; set; }
    [Column("pier_side")] public string? PierSide { get; set; }
    [Column("alt_deg")] public double? AltDeg { get; set; }
    [Column("az_deg")] public double? AzDeg { get; set; }

    /// <summary>From the West leg's completion line.</summary>
    [Column("west_angle_deg")] public double? WestAngleDeg { get; set; }

    [Column("west_rate_px_s")] public double? WestRatePxS { get; set; }
    [Column("west_parity")] public string? WestParity { get; set; }

    /// <summary>From the North leg's completion line.</summary>
    [Column("north_angle_deg")] public double? NorthAngleDeg { get; set; }

    [Column("north_rate_px_s")] public double? NorthRatePxS { get; set; }
    [Column("north_parity")] public string? NorthParity { get; set; }

    /// <summary>True when a <c>Calibration complete</c> line closed the block.</summary>
    [Column("completed")] public bool Completed { get; set; }

    /// <summary>A JSON array of
    /// <c>{"direction", "step", "dx", "dy", "x", "y", "dist"}</c>, in file order. <c>[]</c> when
    /// the block carried none.</summary>
    [Column("steps")] public string Steps { get; set; } = "[]";
}
