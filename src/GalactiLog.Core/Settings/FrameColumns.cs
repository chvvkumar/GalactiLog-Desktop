using System.Collections.Immutable;

namespace GalactiLog.Core.Settings;

/// <summary>
/// One frame table column: its persisted key, its header, and which metric group and field flag
/// of <c>display.groups</c> gates it (design-spec 5.8.2, 12.4). A column in the "always" groups of
/// design-spec 12.4's table has a null <see cref="Group"/> and can never be gated off.
/// </summary>
/// <param name="Key">The key that appears in <c>display.columns.frames</c>. Equal to the
/// <c>images</c> column name in every case, so the persisted document, the sort key and the read
/// model field are one vocabulary rather than three (questions.md Q14).</param>
/// <param name="Title">The header text, verbatim from design-spec 12.4's table.</param>
/// <param name="Group">The <c>display.groups</c> group id, or null for an always-on column.</param>
/// <param name="Field">The field key inside that group, or null for an always-on column.</param>
/// <param name="IsNumeric">Right-aligned and rendered with tabular figures (design-spec 14.4).</param>
public sealed record FrameColumn(string Key, string Title, string? Group, string? Field, bool IsNumeric);

/// <summary>
/// The join between design-spec 12.4's column table and design-spec 5.8.2's <c>groups</c>
/// document. The two use different vocabularies: the table names columns by their <c>images</c>
/// column, the groups document names fields by short keys (<c>hfr</c>, <c>rms_total</c>,
/// <c>mean</c>, <c>position</c>). The join is one table in one place so no caller re-derives it.
/// </summary>
public static class FrameColumns
{
    /// <summary>design-spec 12.4's eight-group table, in its documented order. 32 columns.</summary>
    public static readonly ImmutableArray<FrameColumn> All =
    [
        new("time",                  "Time",       null,      null,               false),
        new("file_name",             "File name",  null,      null,               false),
        new("filter_used",           "Filter",     null,      null,               false),
        new("exposure_time",         "Exp",        null,      null,               true),

        new("median_hfr",            "HFR",        "quality", "hfr",              true),
        new("eccentricity",          "Ecc",        "quality", "eccentricity",     true),
        new("fwhm",                  "FWHM",       "quality", "fwhm",             true),
        new("detected_stars",        "Stars",      "quality", "detected_stars",   true),

        new("guiding_rms_arcsec",    "RMS",        "guiding", "rms_total",        true),
        new("guiding_rms_ra_arcsec", "RMS RA",     "guiding", "rms_ra",           true),
        new("guiding_rms_dec_arcsec","RMS Dec",    "guiding", "rms_dec",          true),

        new("adu_mean",              "ADU Mean",   "adu",     "mean",             true),
        new("adu_median",            "ADU Med",    "adu",     "median",           true),
        new("adu_stdev",             "ADU sigma",  "adu",     "stdev",            true),
        new("adu_min",               "ADU Min",    "adu",     "min",              true),
        new("adu_max",               "ADU Max",    "adu",     "max",              true),

        new("focuser_position",      "Focus",      "focuser", "position",         true),
        new("focuser_temp",          "Focus Temp", "focuser", "temp",             true),

        new("ambient_temp",          "Amb Temp",   "weather", "ambient_temp",     true),
        new("dew_point",             "Dew Pt",     "weather", "dew_point",        true),
        new("humidity",              "Humidity",   "weather", "humidity",         true),
        new("pressure",              "Pressure",   "weather", "pressure",         true),
        new("wind_speed",            "Wind",       "weather", "wind_speed",       true),
        new("wind_direction",        "Wind Dir",   "weather", "wind_direction",   true),
        new("wind_gust",             "Gust",       "weather", "wind_gust",        true),
        new("cloud_cover",           "Clouds",     "weather", "cloud_cover",      true),
        new("sky_quality",           "SQM",        "weather", "sky_quality",      true),

        new("airmass",               "Airmass",    "mount",   "airmass",          true),
        new("pier_side",             "Pier",       "mount",   "pier_side",        false),
        new("rotator_position",      "Rotator",    "mount",   "rotator_position", true),

        new("sensor_temp",           "Temp",       null,      null,               true),
        new("camera_gain",           "Gain",       null,      null,               true),
    ];

    /// <summary>design-spec 5.8.2's rule, as one function so no caller reimplements it: a column
    /// is gated off when its group's <c>enabled</c> flag is false, or when its field flag inside
    /// that group is false. The group toggle wins over the persisted column list, which is
    /// enforced by the caller applying this on top of <see cref="DisplaySettings.ColumnsFor"/>,
    /// never instead of it. An always-on column (null group) is never gated. A group or field key
    /// absent from a hand-edited document reads as its documented default, which for every one of
    /// them is enabled/true, so a truncated document does not silently blank the table.</summary>
    public static bool IsGroupEnabled(FrameColumn column, DisplaySettings display)
    {
        if (column.Group is null || column.Field is null)
        {
            return true;
        }

        if (!display.Groups.TryGetValue(column.Group, out var group))
        {
            return true;
        }

        if (!group.Enabled)
        {
            return false;
        }

        // A null fields map is what a hand-edited "fields": null deserializes to. Same rule as a
        // missing key: the documented default for every field of every group is true.
        return group.Fields is null
            || !group.Fields.TryGetValue(column.Field, out var enabled)
            || enabled;
    }
}
