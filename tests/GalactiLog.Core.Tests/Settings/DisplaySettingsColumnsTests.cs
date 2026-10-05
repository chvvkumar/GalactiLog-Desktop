using System.Collections.Immutable;
using System.Text.Json;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// design-spec 5.8.2: display.columns maps a table id to the ordered list of visible column keys,
// and "a table id absent from the map uses that table's default list". The rule lives in the
// settings record because both the dashboard target list (Phase 5) and the Target detail frame
// table (Phase 6) need it.
public class DisplaySettingsColumnsTests
{
    [Fact]
    public void ColumnsFor_Dashboard_DefaultsToTheSixDocumentedKeysInOrder()
    {
        var columns = new DisplaySettings().ColumnsFor(DisplaySettings.DashboardTableId);

        Assert.Equal(
            ["name", "designation", "palette", "integration", "equipment", "last_session"],
            columns);
    }

    [Fact]
    public void ColumnsFor_Frames_DefaultsToTheEightDocumentedKeysInOrder()
    {
        var columns = new DisplaySettings().ColumnsFor(DisplaySettings.FramesTableId);

        Assert.Equal(
            [
                "time", "file_name", "filter_used", "exposure_time",
                "median_hfr", "eccentricity", "fwhm", "detected_stars",
            ],
            columns);
    }

    [Fact]
    public void ColumnsFor_TableIdAbsentFromTheMap_FallsBackToTheDefaultList()
    {
        // A document written before this table existed, or one a user trimmed by hand.
        var settings = new DisplaySettings { Columns = new Dictionary<string, string[]>() };

        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            settings.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.FramesTableId],
            settings.ColumnsFor(DisplaySettings.FramesTableId));
    }

    [Fact]
    public void ColumnsFor_UnknownTableId_ReturnsEmpty()
        => Assert.Empty(new DisplaySettings().ColumnsFor("no_such_table"));

    [Fact]
    public void ColumnsFor_StoredListWins_OverTheDefault()
    {
        var settings = new DisplaySettings
        {
            Columns = new Dictionary<string, string[]>
            {
                [DisplaySettings.DashboardTableId] = ["name", "integration"],
            },
        };

        Assert.Equal(["name", "integration"], settings.ColumnsFor(DisplaySettings.DashboardTableId));

        // A stored empty list is a stored list, not an absent one: it does not resurrect the
        // defaults. The view-model, not the record, is what keeps "name" on screen (ruling Q5).
        var emptied = new DisplaySettings
        {
            Columns = new Dictionary<string, string[]> { [DisplaySettings.DashboardTableId] = [] },
        };

        Assert.Empty(emptied.ColumnsFor(DisplaySettings.DashboardTableId));
    }

    [Fact]
    public void ColumnsFor_StoredNull_FallsBackToTheDefaultList()
    {
        // The one hand-edited shape the read cannot repair: "dashboard": null deserializes
        // without raising anything, so SettingsStore.ReadDisplay has no refused path to drop the
        // member on, and the null reaches the record. Red against the previous ColumnsFor, which
        // spread the stored value straight into a new array and threw NullReferenceException.
        var settings = JsonSerializer.Deserialize<DisplaySettings>(
            "{\"columns\":{\"dashboard\":null}}")!;

        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            settings.ColumnsFor(DisplaySettings.DashboardTableId));
    }

    [Fact]
    public void DefaultColumns_MatchTheSerializedDefaultsOfTheRecord()
    {
        // Guards the rewrite of the Columns initializer: the serialized document must stay
        // byte-compatible with what SettingsStore already round-trips.
        var settings = new DisplaySettings();

        Assert.Equal(DisplaySettings.DefaultColumns.Count, settings.Columns.Count);
        foreach (var (tableId, expected) in DisplaySettings.DefaultColumns)
        {
            Assert.Equal(expected, settings.Columns[tableId]);
        }

        var json = JsonSerializer.Serialize(settings);
        Assert.Contains(
            "\"columns\":{\"dashboard\":[\"name\",\"designation\",\"palette\",\"integration\",\"equipment\",\"last_session\"],",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnsFor_ReturnsACopy_OnBothPaths()
    {
        // Review item 6: the stored path used to hand back the record's own array, so a caller
        // that sorted or trimmed the result silently rewrote the settings document.
        var settings = new DisplaySettings();

        var fromDefault = settings.ColumnsFor(DisplaySettings.FramesTableId);
        Assert.NotSame(fromDefault, settings.ColumnsFor(DisplaySettings.FramesTableId));

        var stored = settings.ColumnsFor(DisplaySettings.DashboardTableId);
        Assert.NotSame(stored, settings.Columns[DisplaySettings.DashboardTableId]);

        stored[0] = "mutated";
        Assert.Equal("name", settings.Columns[DisplaySettings.DashboardTableId][0]);
        Assert.Equal("name", settings.ColumnsFor(DisplaySettings.DashboardTableId)[0]);
        Assert.Equal("name", DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId][0]);
    }

    [Fact]
    public void Columns_AreFreshArrays_NotTheSharedDefaults()
    {
        // The property is a mutable dictionary of mutable arrays; a caller editing one instance
        // must not rewrite the defaults for every instance built afterwards.
        var first = new DisplaySettings();
        first.Columns[DisplaySettings.DashboardTableId][0] = "mutated";
        first.Columns.Remove(DisplaySettings.FramesTableId);

        // Review item 5: the defaults themselves are an ImmutableArray, so there is no write to
        // guard against on that side at all.
        Assert.IsType<ImmutableArray<string>>(DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId]);

        var second = new DisplaySettings();

        Assert.Equal("name", second.Columns[DisplaySettings.DashboardTableId][0]);
        Assert.Equal("name", DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId][0]);
        Assert.True(second.Columns.ContainsKey(DisplaySettings.FramesTableId));
    }

    // ---- Phase 24 R5: display.column_widths ---------------------------------------------------

    [Fact]
    public void ColumnWidths_RoundTripThroughJson_BesideTheColumnList()
    {
        // The storage shape: "column_widths" maps a table id to a map of column key to width,
        // written beside "columns". Red if the key, the nesting or the number type changes.
        var settings = new DisplaySettings()
            .WithColumnWidth(DisplaySettings.FramesTableId, "file_name", 312.5d)
            .WithColumnWidth(DisplaySettings.FramesTableId, "median_hfr", 64d);

        var json = JsonSerializer.Serialize(settings);
        Assert.Contains(
            "\"column_widths\":{\"frames\":{\"file_name\":312.5,\"median_hfr\":64}}",
            json,
            StringComparison.Ordinal);
        Assert.True(
            json.IndexOf("\"columns\":", StringComparison.Ordinal)
                < json.IndexOf("\"column_widths\":", StringComparison.Ordinal),
            "column_widths is written after columns");

        var read = JsonSerializer.Deserialize<DisplaySettings>(json)!;
        Assert.Equal(
            new Dictionary<string, double> { ["file_name"] = 312.5d, ["median_hfr"] = 64d },
            read.ColumnWidthsFor(DisplaySettings.FramesTableId));
    }

    [Fact]
    public void ColumnWidthsFor_MissingTableOrKey_ReadsAsAutoFit()
    {
        // A fresh profile stores nothing, a document written before this key existed carries
        // none, and a column absent from its table's map is one the view auto-fits: every one of
        // these reads as an empty map or a missing key, never as a width and never as a throw.
        Assert.Empty(new DisplaySettings().ColumnWidthsFor(DisplaySettings.FramesTableId));

        var older = JsonSerializer.Deserialize<DisplaySettings>("""{"columns":{}}""")!;
        Assert.Empty(older.ColumnWidthsFor(DisplaySettings.FramesTableId));

        var partial = JsonSerializer.Deserialize<DisplaySettings>(
            """{"column_widths":{"frames":{"file_name":300},"dashboard":null}}""")!;
        Assert.False(partial.ColumnWidthsFor(DisplaySettings.FramesTableId).ContainsKey("fwhm"));
        Assert.Equal(300d, partial.ColumnWidthsFor(DisplaySettings.FramesTableId)["file_name"]);
        Assert.Empty(partial.ColumnWidthsFor(DisplaySettings.DashboardTableId));
    }

    [Fact]
    public void WithColumnWidth_SetsAndRemovesOneEntry_AndCopiesRatherThanMutates()
    {
        var settings = new DisplaySettings();

        var stored = settings.WithColumnWidth(DisplaySettings.FramesTableId, "fwhm", 70d);
        Assert.Empty(settings.ColumnWidths);
        Assert.Equal(70d, stored.ColumnWidthsFor(DisplaySettings.FramesTableId)["fwhm"]);

        var cleared = stored.WithColumnWidth(DisplaySettings.FramesTableId, "fwhm", null);
        Assert.Equal(70d, stored.ColumnWidthsFor(DisplaySettings.FramesTableId)["fwhm"]);
        Assert.Empty(cleared.ColumnWidthsFor(DisplaySettings.FramesTableId));

        // The last entry of a table takes the table's map with it, so a profile that has cleared
        // every drag writes the same document it did before the first one.
        Assert.False(cleared.ColumnWidths.ContainsKey(DisplaySettings.FramesTableId));

        // And the copy handed out is a copy.
        stored.ColumnWidthsFor(DisplaySettings.FramesTableId)["fwhm"] = 1d;
        Assert.Equal(70d, stored.ColumnWidthsFor(DisplaySettings.FramesTableId)["fwhm"]);
    }
}
