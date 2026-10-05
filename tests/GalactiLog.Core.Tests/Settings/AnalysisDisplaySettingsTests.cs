using System.Text.Json;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.Core.Tests.Settings;

// Phase 17 Task 4: spec 5.8.2's display.analysis object, four keys and no others, no migration
// (ruling A9). The four tolerant reads themselves live at the App seam, where the metric tables
// are, and have their own cases there; these are the record's own defaults, round trip and
// extension data, in the shape TargetPageSettingsTests already holds for target_page.
public class AnalysisDisplaySettingsTests
{
    [Fact]
    public void Analysis_Defaults_AreSpec582sFourValues()
    {
        var analysis = new AnalysisDisplaySettings();

        Assert.Equal("correlation", analysis.Tab);
        Assert.Equal("humidity", analysis.XMetric);
        Assert.Equal("hfr", analysis.YMetric);
        Assert.Equal("frame", analysis.Granularity);
    }

    [Fact]
    public void DisplaySettings_WithNoAnalysisKey_TakesAllFourDefaults()
    {
        // Task 4 section 5.4 case 1: ALL four, not only the missing ones. Every profile written
        // before this phase. Red against a nullable property with no initializer: the read throws
        // a null reference before it reaches the first assertion.
        var display = JsonSerializer.Deserialize<DisplaySettings>("{}")!;

        Assert.Equal("correlation", display.Analysis.Tab);
        Assert.Equal("humidity", display.Analysis.XMetric);
        Assert.Equal("hfr", display.Analysis.YMetric);
        Assert.Equal("frame", display.Analysis.Granularity);
    }

    [Fact]
    public void DisplaySettings_WithAPartialAnalysisObject_TakesTheDefaultsForTheRest()
    {
        // The rule spec 5.8's opening states for the whole document, applied inside the object: a
        // missing key reads as its default and is written on the first write.
        var display = JsonSerializer.Deserialize<DisplaySettings>(
            "{\"analysis\":{\"tab\":\"matrix\"}}")!;

        Assert.Equal("matrix", display.Analysis.Tab);
        Assert.Equal("humidity", display.Analysis.XMetric);
        Assert.Equal("hfr", display.Analysis.YMetric);
        Assert.Equal("frame", display.Analysis.Granularity);
    }

    [Fact]
    public void Analysis_RoundTripsThroughJson()
    {
        var display = new DisplaySettings
        {
            Analysis = new AnalysisDisplaySettings
            {
                Tab = "compare",
                XMetric = "phd2_rms_total",
                YMetric = "fwhm",
                Granularity = "session",
            },
        };

        var json = JsonSerializer.Serialize(display);

        Assert.Contains("\"analysis\":", json, StringComparison.Ordinal);
        Assert.Contains("\"tab\":\"compare\"", json, StringComparison.Ordinal);
        Assert.Contains("\"x_metric\":\"phd2_rms_total\"", json, StringComparison.Ordinal);
        Assert.Contains("\"y_metric\":\"fwhm\"", json, StringComparison.Ordinal);
        Assert.Contains("\"granularity\":\"session\"", json, StringComparison.Ordinal);

        var reloaded = JsonSerializer.Deserialize<DisplaySettings>(json)!;

        Assert.Equal("compare", reloaded.Analysis.Tab);
        Assert.Equal("phd2_rms_total", reloaded.Analysis.XMetric);
        Assert.Equal("fwhm", reloaded.Analysis.YMetric);
        Assert.Equal("session", reloaded.Analysis.Granularity);
    }

    [Fact]
    public void Analysis_IsWrittenAfterTargetPage_AsSpec582sSampleOrders()
    {
        // Spec 5.8.2's sample document has the analysis object last. The property order on the
        // record is what System.Text.Json writes, so the written document matches the spec a
        // reader has in front of them.
        var json = JsonSerializer.Serialize(new DisplaySettings());

        Assert.True(
            json.IndexOf("\"target_page\":", StringComparison.Ordinal)
                < json.IndexOf("\"analysis\":", StringComparison.Ordinal),
            "display.analysis must be written after display.target_page");
    }

    [Fact]
    public void Analysis_AnUnknownKey_SurvivesAWrite()
    {
        // Task 4 section 5.4 case 3, spec 5.8's rule: an unrecognized key is preserved on write
        // and never dropped, inside a nested object too. Red against a record with no
        // [JsonExtensionData]: the key is gone from the written document.
        const string stored = "{\"analysis\":{\"tab\":\"matrix\",\"a_later_phases_key\":42}}";

        var display = JsonSerializer.Deserialize<DisplaySettings>(stored)!;
        var written = JsonSerializer.Serialize(display);

        Assert.Contains("\"a_later_phases_key\":42", written, StringComparison.Ordinal);
        Assert.Equal("matrix", display.Analysis.Tab);

        // The three keys the stored document did not carry still read as their defaults.
        Assert.Equal("humidity", display.Analysis.XMetric);
        Assert.Equal("hfr", display.Analysis.YMetric);
        Assert.Equal("frame", display.Analysis.Granularity);
    }

    [Theory]
    [InlineData("tab", "\"nope\"", "nope", "humidity", "hfr", "frame")]
    [InlineData("tab", "42", "correlation", "humidity", "hfr", "frame")]
    [InlineData("tab", "true", "correlation", "humidity", "hfr", "frame")]
    [InlineData("tab", "null", "correlation", "humidity", "hfr", "frame")]
    [InlineData("tab", "{}", "correlation", "humidity", "hfr", "frame")]
    [InlineData("tab", "{\"a\":[1,{\"b\":2}]}", "correlation", "humidity", "hfr", "frame")]
    [InlineData("tab", "[1,2,3]", "correlation", "humidity", "hfr", "frame")]
    [InlineData("x_metric", "\"nope\"", "correlation", "nope", "hfr", "frame")]
    [InlineData("x_metric", "42", "correlation", "humidity", "hfr", "frame")]
    [InlineData("x_metric", "true", "correlation", "humidity", "hfr", "frame")]
    [InlineData("x_metric", "null", "correlation", "humidity", "hfr", "frame")]
    [InlineData("x_metric", "{}", "correlation", "humidity", "hfr", "frame")]
    [InlineData("x_metric", "[]", "correlation", "humidity", "hfr", "frame")]
    [InlineData("y_metric", "\"phd2_rms_total\"", "correlation", "humidity", "phd2_rms_total", "frame")]
    [InlineData("y_metric", "3.5", "correlation", "humidity", "hfr", "frame")]
    [InlineData("y_metric", "false", "correlation", "humidity", "hfr", "frame")]
    [InlineData("y_metric", "null", "correlation", "humidity", "hfr", "frame")]
    [InlineData("y_metric", "{\"nested\":true}", "correlation", "humidity", "hfr", "frame")]
    [InlineData("y_metric", "[\"hfr\"]", "correlation", "humidity", "hfr", "frame")]
    [InlineData("granularity", "\"SESSION\"", "correlation", "humidity", "hfr", "SESSION")]
    [InlineData("granularity", "0", "correlation", "humidity", "hfr", "frame")]
    [InlineData("granularity", "true", "correlation", "humidity", "hfr", "frame")]
    [InlineData("granularity", "null", "correlation", "humidity", "hfr", "frame")]
    [InlineData("granularity", "{}", "correlation", "humidity", "hfr", "frame")]
    [InlineData("granularity", "[[]]", "correlation", "humidity", "hfr", "frame")]
    public void AKeyOfTheWrongKind_ReadsAsItsDefaultAndDoesNotThrow(
        string key, string stored, string tab, string x, string y, string granularity)
    {
        // Ruling A9 and spec 5.8.2: nothing throws on any input. A JsonException raised inside
        // this object is answered here, at the seam that reads it, and the profile keeps its
        // columns, its dashboard panel and its target_page disclosures. SettingsStore.ReadDisplay
        // stands behind this for the members that have no converter of their own.
        //
        // Red against the plain record with no converter: every non-string row throws
        // JsonException against a string property, and the null rows assign null to the property
        // so the first read of the object throws a null reference instead.
        var display = JsonSerializer.Deserialize<DisplaySettings>(
            $"{{\"analysis\":{{\"{key}\":{stored}}},\"target_page\":{{\"grading_baseline\":\"rig\"}}}}")!;

        Assert.Equal(tab, display.Analysis.Tab);
        Assert.Equal(x, display.Analysis.XMetric);
        Assert.Equal(y, display.Analysis.YMetric);
        Assert.Equal(granularity, display.Analysis.Granularity);

        // The keys around it survived, which is the half of the rule a throw would break.
        Assert.Equal("rig", display.TargetPage.GradingBaseline);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"correlation\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("[{\"tab\":\"matrix\"}]")]
    public void AnalysisItself_AsANonObject_ReadsAsTheFourDefaultsAndDoesNotThrow(string stored)
    {
        // The other half of the same rule: the object's own value being of the wrong kind. The
        // null row is the one System.Text.Json handles silently and worst, by assigning null to a
        // property the whole page then dereferences, which is why the converter handles null.
        var display = JsonSerializer.Deserialize<DisplaySettings>(
            $"{{\"analysis\":{stored},\"target_page\":{{\"grading_baseline\":\"rig\"}}}}")!;

        Assert.NotNull(display.Analysis);
        Assert.Equal("correlation", display.Analysis.Tab);
        Assert.Equal("humidity", display.Analysis.XMetric);
        Assert.Equal("hfr", display.Analysis.YMetric);
        Assert.Equal("frame", display.Analysis.Granularity);
        Assert.Equal("rig", display.TargetPage.GradingBaseline);
    }

    [Fact]
    public void AKeyOfTheWrongKind_LeavesTheOtherThreeReadable()
    {
        // One bad key does not cost the other three: the converter skips the value whole and goes
        // on reading the object. Red against a converter that returns early on the first token it
        // does not like.
        var display = JsonSerializer.Deserialize<DisplaySettings>(
            "{\"analysis\":{\"tab\":{\"junk\":[1,2]},\"x_metric\":\"airmass\","
            + "\"y_metric\":99,\"granularity\":\"session\",\"a_later_phases_key\":7}}")!;

        Assert.Equal("correlation", display.Analysis.Tab);
        Assert.Equal("airmass", display.Analysis.XMetric);
        Assert.Equal("hfr", display.Analysis.YMetric);
        Assert.Equal("session", display.Analysis.Granularity);
        Assert.Contains("\"a_later_phases_key\":7", JsonSerializer.Serialize(display), StringComparison.Ordinal);
    }

    [Fact]
    public void Analysis_JunkStrings_DeserializeWithoutThrowing()
    {
        // Spec 5.8.2 and ruling A9: nothing throws on a hand-edited document. The record keeps
        // whatever string it was given and the four parses at the App seam turn each into its
        // documented default; this case pins only that the read itself survives.
        const string stored =
            "{\"analysis\":{\"tab\":\"\",\"x_metric\":\"nope\",\"y_metric\":\"phd2_rms_total\","
            + "\"granularity\":\"SESSION\"},\"target_page\":{\"grading_baseline\":\"rig\"}}";

        var display = JsonSerializer.Deserialize<DisplaySettings>(stored)!;

        Assert.Equal(string.Empty, display.Analysis.Tab);
        Assert.Equal("nope", display.Analysis.XMetric);
        Assert.Equal("phd2_rms_total", display.Analysis.YMetric);
        Assert.Equal("SESSION", display.Analysis.Granularity);

        // And the keys around it survived, which is the half of the rule a throw would break.
        Assert.Equal("rig", display.TargetPage.GradingBaseline);
    }
}
