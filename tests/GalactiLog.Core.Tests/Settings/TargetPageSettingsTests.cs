using System.Text.Json;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// Spec 12.4's Target detail page choices, one display.target_page object. A retired key, such as
// guiding_expanded or the five page arrangement keys, rides through the extension data untouched.
public class TargetPageSettingsTests
{
    [Fact]
    public void TargetPage_RoundTripsThroughJson()
    {
        var display = new DisplaySettings
        {
            TargetPage = new TargetPageSettings
            {
                GradingBaseline = "rig",
                FrameListFormat = "names",
                FrameListMode = "bad",
                FrameListIncludeUnmeasured = false,
            },
        };

        var json = JsonSerializer.Serialize(display);

        Assert.Contains("\"target_page\":", json, StringComparison.Ordinal);
        Assert.Contains("\"grading_baseline\":\"rig\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frame_list_format\":\"names\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frame_list_mode\":\"bad\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frame_list_include_unmeasured\":false", json, StringComparison.Ordinal);
        Assert.DoesNotContain("guiding_expanded", json, StringComparison.Ordinal);
        Assert.DoesNotContain("guide_graph_height", json, StringComparison.Ordinal);

        var reloaded = JsonSerializer.Deserialize<DisplaySettings>(json)!;

        Assert.Equal("rig", reloaded.TargetPage.GradingBaseline);
        Assert.Equal("names", reloaded.TargetPage.FrameListFormat);
        Assert.Equal("bad", reloaded.TargetPage.FrameListMode);
        Assert.False(reloaded.TargetPage.FrameListIncludeUnmeasured);
    }

    [Fact]
    public void TargetPage_AnUnknownKey_SurvivesAWrite()
    {
        // Spec 5.8's rule: an unrecognized key is preserved on write and never dropped. The nested
        // record carries its own extension data for the same reason the document does.
        const string stored =
            "{\"target_page\":{\"grading_baseline\":\"rig\",\"a_later_phases_key\":42}}";

        var display = JsonSerializer.Deserialize<DisplaySettings>(stored)!;
        var written = JsonSerializer.Serialize(display);

        Assert.Contains("\"a_later_phases_key\":42", written, StringComparison.Ordinal);
        Assert.Equal("rig", display.TargetPage.GradingBaseline);

        // A key the stored document did not carry still reads as its default.
        Assert.Equal("good", display.TargetPage.FrameListMode);
    }

    [Fact]
    public void TargetPage_ARetiredKey_SurvivesAWriteUntouched()
    {
        // guiding_expanded and guide_graph_height are no longer read, so a
        // document that stores them rides them through the extension data and writes them back
        // as they were. A failure looks like an old profile losing a key on its first write.
        const string stored =
            "{\"target_page\":{\"guiding_expanded\":true,\"guide_graph_height\":512}}";

        var display = JsonSerializer.Deserialize<DisplaySettings>(stored)!;
        var written = JsonSerializer.Serialize(display);

        Assert.Contains("\"guiding_expanded\":true", written, StringComparison.Ordinal);
        Assert.Contains("\"guide_graph_height\":512", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetPage_AStoredLayoutKey_LoadsIntoTheExtensionData_AndEveryOtherValueSurvives()
    {
        // The layout key is no longer read or written. A failure looks like an old profile failing
        // to load, a sibling value lost, or the old literal changed or dropped on the first write.
        const string stored =
            "{\"target_page\":{\"layout\":\"bench\",\"grading_baseline\":\"rig\",\"frame_list_format\":\"names\","
            + "\"frame_list_mode\":\"bad\",\"frame_list_include_unmeasured\":false,"
            + "\"layouts\":{\"modes\":{\"lanes_height\":300}}}}";

        var page = JsonSerializer.Deserialize<DisplaySettings>(stored)!.TargetPage;
        var written = JsonSerializer.Serialize(new DisplaySettings { TargetPage = page });

        Assert.Equal("rig", page.GradingBaseline);
        Assert.Equal("names", page.FrameListFormat);
        Assert.Equal("bad", page.FrameListMode);
        Assert.False(page.FrameListIncludeUnmeasured);
        Assert.Equal(300, page.Layouts["modes"].LanesHeight);
        Assert.Equal("bench", page.ExtensionData!["layout"].GetString());
        Assert.Contains("\"layout\":\"bench\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetPage_AnOldProfilesFiveRetiredKeys_LoadIntoTheExtensionDataAndSurviveAWrite()
    {
        // The five page arrangement keys are no longer read. A failure looks like an old profile
        // failing to load, or losing one of these keys on its first write.
        const string stored =
            "{\"target_page\":{\"night_detail_expanded\":true,\"frames_expanded\":true,"
            + "\"ledger_expanded\":true,\"metrics_expanded\":false,\"night_detail_height\":512,"
            + "\"grading_baseline\":\"rig\"}}";

        var display = JsonSerializer.Deserialize<DisplaySettings>(stored)!;
        var written = JsonSerializer.Serialize(display);

        Assert.Equal("rig", display.TargetPage.GradingBaseline);
        var extension = display.TargetPage.ExtensionData;
        Assert.NotNull(extension);
        Assert.Equal(
            ["frames_expanded", "ledger_expanded", "metrics_expanded", "night_detail_expanded", "night_detail_height"],
            extension.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("\"night_detail_expanded\":true", written, StringComparison.Ordinal);
        Assert.Contains("\"metrics_expanded\":false", written, StringComparison.Ordinal);
        Assert.Contains("\"night_detail_height\":512", written, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplaySettings_WithNoTargetPageKey_TakesTheDefaults()
    {
        // Every profile written before this phase. The object is absent, so the record's own
        // initializers are what the page opens with.
        var display = JsonSerializer.Deserialize<DisplaySettings>("{}")!;

        // Spec 5.8.2: the four grading and frame-list keys need no migration for the same reason
        // the rest of the document needs none. A missing key reads as its default on the first
        // read and is written on the first write.
        Assert.Equal("session", display.TargetPage.GradingBaseline);
        Assert.Equal("paths", display.TargetPage.FrameListFormat);
        Assert.Equal("good", display.TargetPage.FrameListMode);
        Assert.True(display.TargetPage.FrameListIncludeUnmeasured);
    }

    [Fact]
    public void GraphSettings_AStoredSessionChartExpanded_SurvivesAWriteUntouched()
    {
        // The key is retired. Nothing reads it, and a document that stores it
        // writes it back through the extension data as spec 5.8 requires.
        var graph = JsonSerializer.Deserialize<GraphSettings>("{\"session_chart_expanded\":true}")!;

        Assert.True(graph.ExtensionData!["session_chart_expanded"].GetBoolean());
        Assert.Contains(
            "\"session_chart_expanded\":true",
            JsonSerializer.Serialize(graph),
            StringComparison.Ordinal);
    }

    [Fact]
    public void GraphSettings_TargetChartExpanded_RidesTheExtensionData()
    {
        // The key is retired like session_chart_expanded: nothing reads it and a stored value
        // is written back.
        var graph = JsonSerializer.Deserialize<GraphSettings>("{\"target_chart_expanded\":true}")!;

        Assert.True(graph.ExtensionData!["target_chart_expanded"].GetBoolean());
        Assert.Contains(
            "\"target_chart_expanded\":true",
            JsonSerializer.Serialize(graph),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "target_chart_expanded",
            JsonSerializer.Serialize(new GraphSettings()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TargetPageSettings_EqualityIsByContent_IncludingTheLayouts()
    {
        // A failure looks like two equal stored documents comparing unequal, which fails the logger wiring pin that compares a repaired target_page with a default one.
        TargetPageSettings Make(double? height) => new() { Layouts = new() { ["modes"] = new TargetLayoutState { LanesHeight = height } } };

        Assert.Equal(Make(300), Make(300));
        Assert.Equal(Make(300).GetHashCode(), Make(300).GetHashCode());
        Assert.NotEqual(Make(300), Make(310));
        Assert.NotEqual(Make(300), new TargetPageSettings());
        Assert.Equal(new TargetPageSettings(), new TargetPageSettings { Layouts = new() });
    }

    [Fact]
    public void Layouts_AFreshProfileAndADocumentWithoutTheKey_HoldNoHeight()
    {
        // A failure looks like a fresh profile opening a layout at a stored height.
        Assert.Empty(new DisplaySettings().TargetPage.Layouts);
        Assert.Empty(JsonSerializer.Deserialize<DisplaySettings>("{\"target_page\":{}}")!.TargetPage.Layouts);
        Assert.Null(JsonSerializer.Deserialize<DisplaySettings>("{\"target_page\":{\"layouts\":{\"modes\":{}}}}")!.TargetPage.Layouts["modes"].LanesHeight);
    }

    [Fact]
    public void Layouts_RoundTripPerKeyUnderTheirJsonNames_AndKeepTheExtensionData()
    {
        // A failure looks like a height under another name, one key's value landing on the other,
        // or an unknown key under the layout or beside it dropped.
        const string stored = "{\"target_page\":{\"grading_baseline\":\"rig\",\"zz\":7,"
            + "\"layouts\":{\"modes\":{\"lanes_height\":300,\"later\":\"x\"},\"bench\":{\"lanes_height\":410}}}}";

        var display = JsonSerializer.Deserialize<DisplaySettings>(stored)!;
        var changed = display.TargetPage.Layouts["modes"] with { LanesHeight = null };
        var written = JsonSerializer.Serialize(display with
        {
            TargetPage = display.TargetPage with
            {
                Layouts = new Dictionary<string, TargetLayoutState>(display.TargetPage.Layouts) { ["modes"] = changed },
            },
        });
        var reloaded = JsonSerializer.Deserialize<DisplaySettings>(written)!.TargetPage;

        Assert.Equal(300, display.TargetPage.Layouts["modes"].LanesHeight);
        Assert.Null(reloaded.Layouts["modes"].LanesHeight);
        Assert.Equal("x", reloaded.Layouts["modes"].ExtensionData!["later"].GetString());
        Assert.Equal(410, reloaded.Layouts["bench"].LanesHeight);
        Assert.Equal("rig", reloaded.GradingBaseline);
        Assert.Equal(7, reloaded.ExtensionData!["zz"].GetInt32());
        Assert.Contains("\"lanes_height\":410", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Layouts_TheChartHeight_RoundTripsUnderItsJsonName_BesideTheLanesHeight()
    {
        // A failure looks like the chart height under another name or in the extension data, a
        // clear that does not reload as null, or the lanes height or an unknown key lost beside it.
        const string stored = "{\"target_page\":{\"layouts\":{\"modes\":{\"lanes_height\":300,\"chart_height\":240.5,\"later\":\"x\"}}}}";

        var display = JsonSerializer.Deserialize<DisplaySettings>(stored)!;
        var modes = display.TargetPage.Layouts["modes"];
        var written = JsonSerializer.Serialize(display);
        var cleared = JsonSerializer.Serialize(display with
        {
            TargetPage = display.TargetPage with
            {
                Layouts = new Dictionary<string, TargetLayoutState>(display.TargetPage.Layouts) { ["modes"] = modes with { ChartHeight = null } },
            },
        });
        var reloaded = JsonSerializer.Deserialize<DisplaySettings>(cleared)!.TargetPage.Layouts["modes"];

        Assert.Equal(240.5, modes.ChartHeight);
        Assert.Equal(300, modes.LanesHeight);
        Assert.DoesNotContain("chart_height", modes.ExtensionData!.Keys);
        Assert.Contains("\"chart_height\":240.5", written, StringComparison.Ordinal);
        Assert.Null(reloaded.ChartHeight);
        Assert.Equal(300, reloaded.LanesHeight);
        Assert.Equal("x", reloaded.ExtensionData!["later"].GetString());
        Assert.NotEqual(modes, modes with { ChartHeight = 250 });
    }
}
