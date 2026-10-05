using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GalactiLog.Core.Metadata;

// One frame's extracted metadata (spec 7.1's field table). Every field from HfrStdev
// through SkyQuality is the CSV-only portion of the table: this task always leaves them
// null; Task 7's NinaCsvReader fills them in via a `with` expression over this record.
public sealed record ExtractedMetadata
{
    [JsonPropertyName("object_name")] public string? ObjectName { get; init; }
    [JsonPropertyName("exposure_time")] public double? ExposureTime { get; init; }
    [JsonPropertyName("filter_used")] public string? FilterUsed { get; init; }
    [JsonPropertyName("sensor_temp")] public double? SensorTemp { get; init; }
    [JsonPropertyName("camera_gain")] public int? CameraGain { get; init; }
    [JsonPropertyName("image_type")] public string? ImageType { get; init; }
    [JsonPropertyName("telescope")] public string? Telescope { get; init; }
    [JsonPropertyName("camera")] public string? Camera { get; init; }
    [JsonPropertyName("rotator_position")] public double? RotatorPosition { get; init; }
    [JsonPropertyName("median_hfr")] public double? MedianHfr { get; init; }
    [JsonPropertyName("median_fwhm")] public double? MedianFwhm { get; init; }
    [JsonPropertyName("eccentricity")] public double? Eccentricity { get; init; }
    [JsonPropertyName("eccentricity_source")] public string? EccentricitySource { get; init; }
    [JsonPropertyName("altitude_deg")] public double? AltitudeDeg { get; init; }
    [JsonPropertyName("arcsec_per_pixel")] public double? ArcsecPerPixel { get; init; }
    [JsonPropertyName("capture_date")] public string? CaptureDate { get; init; }
    [JsonPropertyName("hfr_stdev")] public double? HfrStdev { get; init; }
    [JsonPropertyName("fwhm")] public double? Fwhm { get; init; }
    [JsonPropertyName("detected_stars")] public int? DetectedStars { get; init; }
    [JsonPropertyName("guiding_rms_arcsec")] public double? GuidingRmsArcsec { get; init; }
    [JsonPropertyName("guiding_rms_ra_arcsec")] public double? GuidingRmsRaArcsec { get; init; }
    [JsonPropertyName("guiding_rms_dec_arcsec")] public double? GuidingRmsDecArcsec { get; init; }
    [JsonPropertyName("guiding_rms_source")] public string? GuidingRmsSource { get; init; }
    [JsonPropertyName("adu_stdev")] public double? AduStdev { get; init; }
    [JsonPropertyName("adu_mean")] public double? AduMean { get; init; }
    [JsonPropertyName("adu_median")] public double? AduMedian { get; init; }
    [JsonPropertyName("adu_min")] public int? AduMin { get; init; }
    [JsonPropertyName("adu_max")] public int? AduMax { get; init; }
    [JsonPropertyName("focuser_position")] public int? FocuserPosition { get; init; }
    [JsonPropertyName("focuser_temp")] public double? FocuserTemp { get; init; }
    [JsonPropertyName("pier_side")] public string? PierSide { get; init; }
    [JsonPropertyName("airmass")] public double? Airmass { get; init; }
    [JsonPropertyName("ambient_temp")] public double? AmbientTemp { get; init; }
    [JsonPropertyName("dew_point")] public double? DewPoint { get; init; }
    [JsonPropertyName("humidity")] public double? Humidity { get; init; }
    [JsonPropertyName("pressure")] public double? Pressure { get; init; }
    [JsonPropertyName("wind_speed")] public double? WindSpeed { get; init; }
    [JsonPropertyName("wind_direction")] public double? WindDirection { get; init; }
    [JsonPropertyName("wind_gust")] public double? WindGust { get; init; }
    [JsonPropertyName("cloud_cover")] public double? CloudCover { get; init; }
    [JsonPropertyName("sky_quality")] public double? SkyQuality { get; init; }
    [JsonPropertyName("raw_headers")] public JsonObject RawHeaders { get; init; } = new();
    [JsonPropertyName("provenance")] public IReadOnlyDictionary<string, string> Provenance { get; init; }
        = new Dictionary<string, string>();
}
