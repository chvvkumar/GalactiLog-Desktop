using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace GalactiLog.Data.Entities;

[Table("images")]
public class Image
{
    [Column("id")] public Guid Id { get; set; }
    [Column("file_path")] public string FilePath { get; set; } = "";
    [Column("file_name")] public string FileName { get; set; } = "";
    [Column("capture_date")] public DateTime? CaptureDate { get; set; }
    [Column("session_date")] public DateOnly? SessionDate { get; set; }
    [Column("thumbnail_path")] public string? ThumbnailPath { get; set; }
    [Column("resolved_target_id")] public Guid? ResolvedTargetId { get; set; }
    [Column("exposure_time")] public double? ExposureTime { get; set; }
    [Column("filter_used")] public string? FilterUsed { get; set; }
    [Column("sensor_temp")] public double? SensorTemp { get; set; }
    [Column("camera_gain")] public int? CameraGain { get; set; }
    [Column("image_type")] public string? ImageType { get; set; }
    [Column("telescope")] public string? Telescope { get; set; }
    [Column("camera")] public string? Camera { get; set; }
    [Column("median_hfr")] public double? MedianHfr { get; set; }
    [Column("median_fwhm")] public double? MedianFwhm { get; set; }
    [Column("eccentricity")] public double? Eccentricity { get; set; }
    [Column("eccentricity_source")] public string? EccentricitySource { get; set; }
    [Column("altitude_deg")] public double? AltitudeDeg { get; set; }
    [Column("arcsec_per_pixel")] public double? ArcsecPerPixel { get; set; }

    // Phase 18 (spec 5.2): written at ingest, backfilled by mosaic detection step 0 (spec 7.7).
    [Column("ra_deg")] public double? RaDeg { get; set; }
    [Column("dec_deg")] public double? DecDeg { get; set; }
    [Column("width_px")] public int? WidthPx { get; set; }
    [Column("panel_label")] public string? PanelLabel { get; set; }

    [Column("hfr_stdev")] public double? HfrStdev { get; set; }
    [Column("fwhm")] public double? Fwhm { get; set; }
    [Column("detected_stars")] public int? DetectedStars { get; set; }
    [Column("guiding_rms_arcsec")] public double? GuidingRmsArcsec { get; set; }
    [Column("guiding_rms_ra_arcsec")] public double? GuidingRmsRaArcsec { get; set; }
    [Column("guiding_rms_dec_arcsec")] public double? GuidingRmsDecArcsec { get; set; }
    [Column("guiding_rms_source")] public string? GuidingRmsSource { get; set; }
    [Column("adu_stdev")] public double? AduStdev { get; set; }
    [Column("adu_mean")] public double? AduMean { get; set; }
    [Column("adu_median")] public double? AduMedian { get; set; }
    [Column("adu_min")] public int? AduMin { get; set; }
    [Column("adu_max")] public int? AduMax { get; set; }
    [Column("focuser_position")] public int? FocuserPosition { get; set; }
    [Column("focuser_temp")] public double? FocuserTemp { get; set; }
    [Column("rotator_position")] public double? RotatorPosition { get; set; }
    [Column("pier_side")] public string? PierSide { get; set; }
    [Column("airmass")] public double? Airmass { get; set; }
    [Column("ambient_temp")] public double? AmbientTemp { get; set; }
    [Column("dew_point")] public double? DewPoint { get; set; }
    [Column("humidity")] public double? Humidity { get; set; }
    [Column("pressure")] public double? Pressure { get; set; }
    [Column("wind_speed")] public double? WindSpeed { get; set; }
    [Column("wind_direction")] public double? WindDirection { get; set; }
    [Column("wind_gust")] public double? WindGust { get; set; }
    [Column("cloud_cover")] public double? CloudCover { get; set; }
    [Column("sky_quality")] public double? SkyQuality { get; set; }
    [Column("file_size")] public long? FileSize { get; set; }
    [Column("file_mtime")] public double? FileMtime { get; set; }
    [Column("raw_headers")] public string? RawHeaders { get; set; }
    [Column("provenance")] public string? Provenance { get; set; }
}
