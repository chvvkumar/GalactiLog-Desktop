using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

/// <summary>
/// Spec 5.16: one row per guiding section, that is one <c>Guiding Begins</c> to
/// <c>Guiding Ends</c> block. Every column of that table, in its order.
/// </summary>
/// <remarks>
/// <para>
/// Sessions are stored raw and never merged at ingest: two PHD2 instances can run at once on two
/// rigs with overlapping wall clock and different pixel scales, so merging would mix incomparable
/// data. Night rollups are frame-count weighted at read time instead (spec 7.6).
/// </para>
/// <para>
/// <b>The 100-frame MinFrames gate is never applied to a stored row</b> (spec 5.16). Nothing in
/// this shape encodes it: every session stores its own RMS and peak columns whatever its
/// <see cref="FrameCount"/>, and those columns are null only for the two reasons spec 5.16 gives
/// them. The gate belongs to the rollups of spec 7.6.
/// </para>
/// <para>
/// <see cref="StartedAtUtc"/> is nullable and the web application's is not. That is the deliberate
/// departure ruling F1 records: with no zone resolved the session is stored with its local start
/// and a null UTC start, and the correlation skips it.
/// </para>
/// <para>
/// No navigation property in either direction; see <see cref="Phd2Log"/>.
/// </para>
/// </remarks>
[Table("phd2_sessions")]
public sealed class Phd2Session
{
    [Column("id")] public Guid Id { get; set; }
    [Column("log_id")] public Guid LogId { get; set; }

    /// <summary>Zero-based position of the owning run within the file.</summary>
    [Column("run_index")] public int RunIndex { get; set; }

    /// <summary>Zero-based position of this section within its run.</summary>
    [Column("section_index")] public int SectionIndex { get; set; }

    /// <summary>The naive wall clock PHD2 wrote, stored with no zone suffix, so a later timezone
    /// correction needs no re-read of the file. <b>Never converted</b>: it carries no zone, no
    /// converter is declared on it, and a reader must not apply this project's usual
    /// <c>SpecifyKind(value, Utc)</c> read-edge habit to it. Round trips
    /// <see cref="DateTimeKind.Unspecified"/>.</summary>
    [Column("started_at_local")] public DateTime StartedAtLocal { get; set; }

    /// <summary><see cref="StartedAtLocal"/> interpreted in the resolved zone. Null when no zone
    /// resolved (spec 7.6, ruling F1). Holds a UTC wall clock and is never shifted either; the
    /// kind is pinned at the read edge, as <c>ActivityQuery</c> pins it, not here.</summary>
    [Column("started_at_utc")] public DateTime? StartedAtUtc { get; set; }

    /// <summary><see cref="EndedAtLocal"/> interpreted in the resolved zone. Null for a truncated
    /// section, which carries no <see cref="EndedAtLocal"/>, and null whenever
    /// <see cref="StartedAtUtc"/> is null.</summary>
    [Column("ended_at_utc")] public DateTime? EndedAtUtc { get; set; }

    /// <summary>The naive wall clock of the section's <c>Guiding Ends</c> line, stored with no zone
    /// suffix, as <see cref="StartedAtLocal"/> is. <b>Never converted</b>: it carries no zone, no
    /// converter is declared on it, and a reader must not apply this project's usual
    /// <c>SpecifyKind(value, Utc)</c> read-edge habit to it. Round trips
    /// <see cref="DateTimeKind.Unspecified"/>. Null for a truncated section, which has no end line.
    /// It is the one column of this table the web model has no counterpart for, and the last of the
    /// table because the fifth migration appends it (spec 5.16).</summary>
    [Column("ended_at_local")] public DateTime? EndedAtLocal { get; set; }

    /// <summary>End minus start when both are known, else the last frame's time offset, else 0.
    /// </summary>
    [Column("duration_s")] public double DurationS { get; set; }

    /// <summary>The imaging night (spec 8.2), derived from <see cref="StartedAtUtc"/>. Null when
    /// that is null.</summary>
    [Column("session_date")] public DateOnly? SessionDate { get; set; }

    /// <summary>The header's <c>Equipment Profile</c> line. Null when the header carries none.
    /// </summary>
    [Column("equipment_profile")] public string? EquipmentProfile { get; set; }

    /// <summary>Resolved from <c>general.phd2_profile_map</c> at ingest. Null means the profile is
    /// unmapped.</summary>
    [Column("telescope")] public string? Telescope { get; set; }

    /// <summary>Per section, never per file. Null on an ASIAIR log.</summary>
    [Column("pixel_scale_arcsec")] public double? PixelScaleArcsec { get; set; }

    [Column("focal_length_mm")] public double? FocalLengthMm { get; set; }
    [Column("guide_camera")] public string? GuideCamera { get; set; }

    /// <summary>Guide exposure.</summary>
    [Column("exposure_ms")] public double? ExposureMs { get; set; }

    [Column("mount_name")] public string? MountName { get; set; }

    /// <summary>For example <c>Auto</c>, <c>North</c>, <c>South</c>, <c>None</c>.</summary>
    [Column("dec_guide_mode")] public string? DecGuideMode { get; set; }

    /// <summary>X guide algorithm.</summary>
    [Column("algo_ra")] public string? AlgoRa { get; set; }

    /// <summary>Y guide algorithm.</summary>
    [Column("algo_dec")] public string? AlgoDec { get; set; }

    [Column("min_move_ra")] public double? MinMoveRa { get; set; }
    [Column("min_move_dec")] public double? MinMoveDec { get; set; }

    /// <summary>The X-axis raw fraction. The Y-axis percentage is parsed but not stored, matching
    /// the web model.</summary>
    [Column("aggression_ra")] public double? AggressionRa { get; set; }

    /// <summary>From the <c>Norm rates</c> line.</summary>
    [Column("ortho_error_deg")] public double? OrthoErrorDeg { get; set; }

    [Column("last_cal_issue")] public string? LastCalIssue { get; set; }
    [Column("pier_side")] public string? PierSide { get; set; }
    [Column("alt_deg")] public double? AltDeg { get; set; }
    [Column("az_deg")] public double? AzDeg { get; set; }
    [Column("dec_deg")] public double? DecDeg { get; set; }
    [Column("hour_angle_hr")] public double? HourAngleHr { get; set; }

    /// <summary>Every CSV row in the section, DROP rows included.</summary>
    [Column("frame_count")] public int FrameCount { get; set; }

    /// <summary>Rows whose mount field is <c>DROP</c>.</summary>
    [Column("drop_count")] public int DropCount { get; set; }

    /// <summary>Longest consecutive run of DROP rows.</summary>
    [Column("max_drop_run")] public int MaxDropRun { get; set; }

    /// <summary>Elapsed seconds spent inside drop runs.</summary>
    [Column("unguided_seconds")] public double UnguidedSeconds { get; set; }

    /// <summary>Null only when the section carries no pixel scale or no usable frame.</summary>
    [Column("rms_ra_arcsec")] public double? RmsRaArcsec { get; set; }

    [Column("rms_dec_arcsec")] public double? RmsDecArcsec { get; set; }

    /// <summary>Hypotenuse of the two above. Null when either of them is null.</summary>
    [Column("rms_total_arcsec")] public double? RmsTotalArcsec { get; set; }

    /// <summary>The one-pass 5 sigma excursion filter (spec 7.6).</summary>
    [Column("rms_ra_filtered_arcsec")] public double? RmsRaFilteredArcsec { get; set; }

    [Column("rms_dec_filtered_arcsec")] public double? RmsDecFilteredArcsec { get; set; }
    [Column("rms_total_filtered_arcsec")] public double? RmsTotalFilteredArcsec { get; set; }

    /// <summary>Largest absolute RA raw distance among the counted frames.</summary>
    [Column("peak_ra_arcsec")] public double? PeakRaArcsec { get; set; }

    /// <summary>Largest absolute Dec raw distance among the counted frames.</summary>
    [Column("peak_dec_arcsec")] public double? PeakDecArcsec { get; set; }

    /// <summary>Over every row carrying an SNR, DROP rows included.</summary>
    [Column("snr_mean")] public double? SnrMean { get; set; }

    [Column("snr_min")] public double? SnrMin { get; set; }

    /// <summary>Over every row carrying a star mass, DROP rows included.</summary>
    [Column("star_mass_mean")] public double? StarMassMean { get; set; }

    [Column("pulse_count_ra_west")] public int PulseCountRaWest { get; set; }
    [Column("pulse_count_ra_east")] public int PulseCountRaEast { get; set; }
    [Column("pulse_count_dec_north")] public int PulseCountDecNorth { get; set; }
    [Column("pulse_count_dec_south")] public int PulseCountDecSouth { get; set; }
    [Column("pulse_total_ms_ra")] public long PulseTotalMsRa { get; set; }
    [Column("pulse_total_ms_dec")] public long PulseTotalMsDec { get; set; }
    [Column("dither_count")] public int DitherCount { get; set; }

    /// <summary>Settles that completed or failed.</summary>
    [Column("settle_count")] public int SettleCount { get; set; }

    [Column("settle_failed_count")] public int SettleFailedCount { get; set; }

    /// <summary>Median of the settle durations, one per settle counted in <c>settle_count</c>, a
    /// failed settle timed like a completed one. Null when none started and finished.</summary>
    [Column("settle_median_s")] public double? SettleMedianS { get; set; }

    /// <summary>A JSON object, reason string to count, over the DROP rows. <c>{}</c> when there
    /// were none.</summary>
    [Column("star_lost_reasons")] public string StarLostReasons { get; set; } = "{}";

    /// <summary>A JSON array of <c>{"type", "t", "detail"}</c>, <c>t</c> being the time offset of
    /// the preceding CSV row. <c>[]</c> when the section had none. The dither and settle window
    /// rule reads this array, so a re-read derives the same windows as the ingest did.</summary>
    [Column("events")] public string Events { get; set; } = "[]";

    /// <summary>True when the section had no <c>Guiding Ends</c> line or when a CSV row failed to
    /// parse.</summary>
    [Column("truncated")] public bool Truncated { get; set; }

    /// <summary>CSV rows thrown away after the first unparsable one. Distinguishes a clipped tail
    /// from a lost session.</summary>
    [Column("discarded_rows")] public int DiscardedRows { get; set; }
}
