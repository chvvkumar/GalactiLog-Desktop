using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// design-spec 12.4's frame table column list and design-spec 5.8.2's metric-group gate. The table
// is the join between two vocabularies (the images column names and the display.groups field
// keys), so most of what is worth asserting here is that the join is complete and that the keys on
// both sides of it exist.
public class FrameColumnsTests
{
    // Spec 12.4's table, verbatim, in its documented order. Written out again rather than read
    // from FrameColumns.All: a test that derives its expectation from the code under test asserts
    // nothing.
    private static readonly (string Key, string Title)[] Documented =
    [
        ("time", "Time"),
        ("file_name", "File name"),
        ("filter_used", "Filter"),
        ("exposure_time", "Exp"),
        ("median_hfr", "HFR"),
        ("eccentricity", "Ecc"),
        ("fwhm", "FWHM"),
        ("detected_stars", "Stars"),
        ("guiding_rms_arcsec", "RMS"),
        ("guiding_rms_ra_arcsec", "RMS RA"),
        ("guiding_rms_dec_arcsec", "RMS Dec"),
        ("adu_mean", "ADU Mean"),
        ("adu_median", "ADU Med"),
        ("adu_stdev", "ADU sigma"),
        ("adu_min", "ADU Min"),
        ("adu_max", "ADU Max"),
        ("focuser_position", "Focus"),
        ("focuser_temp", "Focus Temp"),
        ("ambient_temp", "Amb Temp"),
        ("dew_point", "Dew Pt"),
        ("humidity", "Humidity"),
        ("pressure", "Pressure"),
        ("wind_speed", "Wind"),
        ("wind_direction", "Wind Dir"),
        ("wind_gust", "Gust"),
        ("cloud_cover", "Clouds"),
        ("sky_quality", "SQM"),
        ("airmass", "Airmass"),
        ("pier_side", "Pier"),
        ("rotator_position", "Rotator"),
        ("sensor_temp", "Temp"),
        ("camera_gain", "Gain"),
    ];

    public static TheoryData<int, string, string> DocumentedColumns()
    {
        var data = new TheoryData<int, string, string>();
        for (var index = 0; index < Documented.Length; index++)
        {
            data.Add(index, Documented[index].Key, Documented[index].Title);
        }

        return data;
    }

    [Fact]
    public void All_HasThirtyTwoColumns_InTheSpecifiedOrder()
    {
        Assert.Equal(32, FrameColumns.All.Length);
        Assert.Equal(Documented.Select(column => column.Key), FrameColumns.All.Select(column => column.Key));
    }

    [Fact]
    public void All_KeysAreUnique()
    {
        // The keys are persisted in display.columns.frames and are the sort keys, so a duplicate
        // would make one of the two columns unhideable and unsortable.
        Assert.Equal(FrameColumns.All.Length, FrameColumns.All.Select(column => column.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(DocumentedColumns))]
    public void All_TitlesMatchSpecTwelveFour(int index, string key, string title)
    {
        Assert.Equal(key, FrameColumns.All[index].Key);

        // "ADU sigma" is spelled out, as spec 12.4 itself writes it: the house style forbids
        // decorative characters, so the Greek letter is not an option.
        Assert.Equal(title, FrameColumns.All[index].Title);
    }

    [Fact]
    public void All_GroupAndFieldKeys_ExistInTheDefaultDisplayDocument()
    {
        // The join that would otherwise rot silently: a typo like rms_ra_total gates nothing,
        // because IsGroupEnabled reads a missing key as its documented default of true.
        var display = new DisplaySettings();

        foreach (var column in FrameColumns.All.Where(column => column.Group is not null))
        {
            Assert.True(
                display.Groups.ContainsKey(column.Group!),
                $"{column.Key} names group {column.Group}, which display.groups does not have.");
            Assert.True(
                display.Groups[column.Group!].Fields.ContainsKey(column.Field!),
                $"{column.Key} names field {column.Group}.{column.Field}, which display.groups does not have.");
        }
    }

    [Fact]
    public void All_AlwaysOnColumns_AreTheSixSpecified()
    {
        Assert.Equal(
            ["time", "file_name", "filter_used", "exposure_time", "sensor_temp", "camera_gain"],
            FrameColumns.All.Where(column => column.Group is null).Select(column => column.Key));

        // Group and field move together: a column with one and not the other would be half gated.
        Assert.All(FrameColumns.All, column => Assert.Equal(column.Group is null, column.Field is null));
    }

    [Fact]
    public void IsGroupEnabled_AlwaysOnColumn_IsAlwaysTrue()
    {
        // Every group off, every field off: the six always-on columns still render.
        var display = new DisplaySettings
        {
            Groups = new DisplaySettings().Groups.ToDictionary(
                entry => entry.Key,
                entry => new MetricGroupSettings(false, entry.Value.Fields.ToDictionary(field => field.Key, _ => false))),
        };

        foreach (var column in FrameColumns.All.Where(column => column.Group is null))
        {
            Assert.True(FrameColumns.IsGroupEnabled(column, display));
        }
    }

    [Fact]
    public void IsGroupEnabled_DisabledGroup_IsFalse()
    {
        var display = new DisplaySettings();
        display.Groups["quality"] = display.Groups["quality"] with { Enabled = false };

        Assert.All(
            FrameColumns.All.Where(column => column.Group == "quality"),
            column => Assert.False(FrameColumns.IsGroupEnabled(column, display)));

        // Only that group. Guiding is enabled by default and stays so.
        Assert.All(
            FrameColumns.All.Where(column => column.Group == "guiding"),
            column => Assert.True(FrameColumns.IsGroupEnabled(column, display)));
    }

    [Fact]
    public void IsGroupEnabled_EnabledGroupWithDisabledField_IsFalse()
    {
        var display = new DisplaySettings();
        display.Groups["guiding"] = new MetricGroupSettings(
            true, new Dictionary<string, bool> { ["rms_total"] = false, ["rms_ra"] = true, ["rms_dec"] = true });

        Assert.False(FrameColumns.IsGroupEnabled(Column("guiding_rms_arcsec"), display));
        Assert.True(FrameColumns.IsGroupEnabled(Column("guiding_rms_ra_arcsec"), display));
        Assert.True(FrameColumns.IsGroupEnabled(Column("guiding_rms_dec_arcsec"), display));
    }

    [Fact]
    public void IsGroupEnabled_DefaultDocument_EnablesQualityAndGuidingOnly()
    {
        // Spec 5.8.2's shipped defaults: quality and guiding on, adu, focuser, weather and mount
        // off. So on a fresh install 13 of the 32 columns are ungated (the six always-on ones,
        // four quality, three guiding) and 19 are gated off whatever the persisted list says.
        var display = new DisplaySettings();
        var enabled = FrameColumns.All.Where(column => FrameColumns.IsGroupEnabled(column, display)).ToList();

        Assert.Equal(13, enabled.Count);
        Assert.Equal(
            ["time", "file_name", "filter_used", "exposure_time", "median_hfr", "eccentricity",
             "fwhm", "detected_stars", "guiding_rms_arcsec", "guiding_rms_ra_arcsec",
             "guiding_rms_dec_arcsec", "sensor_temp", "camera_gain"],
            enabled.Select(column => column.Key));

        Assert.All(
            FrameColumns.All.Where(column => column.Group is "adu" or "focuser" or "weather" or "mount"),
            column => Assert.False(FrameColumns.IsGroupEnabled(column, display)));
    }

    [Fact]
    public void IsGroupEnabled_MissingGroupKey_ReadsAsEnabled()
    {
        // A hand-edited or truncated document must not silently blank the table: every documented
        // default is enabled/true, so an absent key reads as that.
        var display = new DisplaySettings { Groups = [] };

        Assert.All(FrameColumns.All, column => Assert.True(FrameColumns.IsGroupEnabled(column, display)));
    }

    [Fact]
    public void IsGroupEnabled_MissingFieldKey_ReadsAsEnabled()
    {
        var display = new DisplaySettings();
        display.Groups["quality"] = new MetricGroupSettings(true, []);

        Assert.All(
            FrameColumns.All.Where(column => column.Group == "quality"),
            column => Assert.True(FrameColumns.IsGroupEnabled(column, display)));
    }

    [Fact]
    public void DefaultColumns_Frames_IsTheDocumentedEightKeys_AndEveryOneIsInAll()
    {
        // Guards the two lists drifting apart: spec 5.8.2 names the default visible list and spec
        // 12.4 names the column table, and a key in the first that is not in the second would be
        // a persisted column that can never render.
        // Materialized to an array on purpose: ImmutableArray<T> implements IEquatable<T> over
        // its underlying array reference, so xunit compares two of them by reference rather than
        // element by element and a correct list fails.
        string[] defaults = [.. DisplaySettings.DefaultColumns[DisplaySettings.FramesTableId]];

        Assert.Equal(
            ["time", "file_name", "filter_used", "exposure_time", "median_hfr", "eccentricity", "fwhm", "detected_stars"],
            defaults);
        Assert.All(defaults, key => Assert.Contains(key, FrameColumns.All.Select(column => column.Key)));
    }

    private static FrameColumn Column(string key)
        => FrameColumns.All.Single(column => column.Key == key);
}
