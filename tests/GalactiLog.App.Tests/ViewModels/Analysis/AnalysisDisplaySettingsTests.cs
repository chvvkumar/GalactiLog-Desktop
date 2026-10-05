using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

// Task 4 section 5: spec 5.8.2's display.analysis object, four keys and no others, no migration,
// and a stored value outside a key's set reading as that key's default. The record's
// own round trip is beside the other display settings cases in GalactiLog.Core.Tests; these are
// the four tolerant reads and the four writers.
//
// The two metric reads take an already parsed AnalysisMetric because the string to enum step is
// AnalysisMetrics.Parse, and the two metric writers take the stored key because the enum to string
// step is AnalysisMetrics.Key. Both belong to GalactiLog.Data.Queries and a second copy of either
// table in the App layer would be the duplication design lesson 1 names.
public class AnalysisDisplaySettingsTests
{
    [Theory]
    [InlineData(null, "correlation")]
    [InlineData("", "correlation")]
    [InlineData("nope", "correlation")]
    [InlineData("Correlation", "correlation")]     // the match is ordinal, so the case matters
    [InlineData("correlation", "correlation")]
    [InlineData("distributions", "distributions")]
    [InlineData("timeseries", "timeseries")]
    [InlineData("matrix", "matrix")]
    [InlineData("compare", "compare")]
    public void ParseTab_ReadsTheFiveKeysOrdinallyAndEverythingElseAsCorrelation(string? stored, string expected)
    {
        // Spec 5.8.2. Red against a parse that throws or answers the stored string
        // through: the junk rows select a tab that does not exist and the page has nothing to
        // show.
        Assert.Equal(expected, AnalysisDisplay.ParseTab(stored));
    }

    [Theory]
    [InlineData("distribution")]
    [InlineData("boxplot")]
    [InlineData("filters")]
    public void ParseTab_RejectsTheCacheKeysQueryVocabulary(string queryWord)
    {
        // The persisted tab vocabulary and AnalysisCacheKey.Tab's query vocabulary are two closed
        // sets that overlap but are not the same: the cache splits the Distributions tab into
        // "distribution" and "boxplot" and carries a "filters" entry that is no tab at all
        // (core-shapes.md section 5.5). They stay two vocabularies, so one cannot be parsed as the
        // other. Red against a parse built from the cache's list: "boxplot" selects a tab the
        // strip does not have.
        Assert.Equal("correlation", AnalysisDisplay.ParseTab(queryWord));
        Assert.DoesNotContain(queryWord, AnalysisDisplay.TabKeys);
    }

    [Theory]
    [InlineData(null, AnalysisMetric.Humidity)]                              // AnalysisMetrics.Parse said no
    [InlineData(AnalysisMetric.Humidity, AnalysisMetric.Humidity)]
    [InlineData(AnalysisMetric.SensorTemp, AnalysisMetric.SensorTemp)]
    [InlineData(AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Phd2RmsTotal)]   // PHD2 is a legal X
    [InlineData(AnalysisMetric.Hfr, AnalysisMetric.Humidity)]                // a Y metric is not
    [InlineData(AnalysisMetric.AduStdev, AnalysisMetric.Humidity)]
    public void XMetric_AcceptsXAndPhd2XAndReadsEverythingElseAsHumidity(
        AnalysisMetric? stored, AnalysisMetric expected)
    {
        // Spec 5.8.2: "Null, or a key that is not in X and not in Phd2X, reads as Humidity". Red
        // against a read that accepts any parsed metric: a hand-edited hfr in x_metric opens the
        // page on a picker entry that does not exist.
        Assert.Equal(expected, AnalysisDisplay.XMetric(stored));
    }

    [Theory]
    [InlineData(null, AnalysisMetric.Hfr)]
    [InlineData(AnalysisMetric.Hfr, AnalysisMetric.Hfr)]
    [InlineData(AnalysisMetric.Fwhm, AnalysisMetric.Fwhm)]
    [InlineData(AnalysisMetric.AduStdev, AnalysisMetric.AduStdev)]
    [InlineData(AnalysisMetric.Humidity, AnalysisMetric.Hfr)]                // an X metric is not a Y
    [InlineData(AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Hfr)]            // spec 5.8.2 names this one
    public void YMetric_AcceptsYAndReadsEverythingElseAsHfr(AnalysisMetric? stored, AnalysisMetric expected)
    {
        // Spec 5.8.2 calls the PHD2 row out by name: "a PHD2 key stored in y_metric is a value the
        // Correlation tab would reject at query time, and reading it as hfr is what keeps a
        // hand-edited document from opening a page that cannot draw." Red against a read that
        // passes any parsed metric through: the page opens on a Y the query refuses.
        Assert.Equal(expected, AnalysisDisplay.YMetric(stored));
    }

    [Theory]
    [InlineData(null, AnalysisGranularity.Frame)]
    [InlineData("", AnalysisGranularity.Frame)]
    [InlineData("nope", AnalysisGranularity.Frame)]
    [InlineData("frame", AnalysisGranularity.Frame)]
    [InlineData("session", AnalysisGranularity.Session)]
    [InlineData("SESSION", AnalysisGranularity.Session)]     // the parse is case insensitive
    [InlineData("Session", AnalysisGranularity.Session)]
    public void ParseGranularity_ReadsSessionCaseInsensitivelyAndEverythingElseAsFrame(
        string? stored, AnalysisGranularity expected)
    {
        // Spec 5.8.2, and task4.md section 5.4 case 2 names "SESSION" in particular. Red against
        // an ordinal compare: the shouted row reads as Frame.
        Assert.Equal(expected, AnalysisDisplay.ParseGranularity(stored));
    }

    [Theory]
    [InlineData(AnalysisGranularity.Frame, "frame")]
    [InlineData(AnalysisGranularity.Session, "session")]
    public void GranularityToStored_IsTheLowerCaseLiteralSpec582Lists(
        AnalysisGranularity granularity, string expected)
        => Assert.Equal(expected, AnalysisDisplay.GranularityToStored(granularity));

    [Fact]
    public void EachWriter_WritesItsOwnKeyAndClobbersNoOther()
    {
        // Task 4 section 5.4 case 4. Every write is queued and
        // applied to the document AS LOADED inside the write, never to a snapshot taken when the
        // writer was built, so four writes plus an unrelated target_page write all survive.
        //
        // Red against a writer that applies its with-expression to a snapshot: the last write in
        // the queue wins and the other four revert.
        var document = new DisplaySettings
        {
            Analysis = new AnalysisDisplaySettings
            {
                Tab = "correlation",
                XMetric = "humidity",
                YMetric = "hfr",
                Granularity = "frame",
            },
            TargetPage = new TargetPageSettings { GradingBaseline = "rig", FrameListFormat = "names" },
            Columns = new Dictionary<string, string[]> { ["frames"] = ["time", "file_name"] },
        };

        var queued = new List<Func<DisplaySettings, DisplaySettings>>();
        Action<Func<DisplaySettings, DisplaySettings>> write = queued.Add;

        AnalysisDisplay.WriteTab(write, AnalysisDisplay.MatrixTab);
        AnalysisDisplay.WriteXMetric(write, "airmass");
        AnalysisDisplay.WriteYMetric(write, "fwhm");
        AnalysisDisplay.WriteGranularity(write, AnalysisGranularity.Session);

        // An unrelated writer over the same document, between the four: this is the interleaving
        // the one serialized chain exists for.
        queued.Insert(2, d => d with { TargetPage = d.TargetPage with { FrameListMode = "bad" } });

        foreach (var mutate in queued)
        {
            document = mutate(document);
        }

        Assert.Equal("matrix", document.Analysis.Tab);
        Assert.Equal("airmass", document.Analysis.XMetric);
        Assert.Equal("fwhm", document.Analysis.YMetric);
        Assert.Equal("session", document.Analysis.Granularity);

        // Neither target_page nor columns moved, and the unrelated write survived.
        Assert.Equal("rig", document.TargetPage.GradingBaseline);
        Assert.Equal("names", document.TargetPage.FrameListFormat);
        Assert.Equal("bad", document.TargetPage.FrameListMode);
        Assert.Equal(new[] { "time", "file_name" }, document.Columns["frames"]);
    }

    [Theory]
    [InlineData("tab")]
    [InlineData("x_metric")]
    [InlineData("y_metric")]
    [InlineData("granularity")]
    public void OneWriter_LeavesTheOtherThreeAnalysisKeysAlone(string key)
    {
        // The same rule read one writer at a time, so a failure names the key that clobbered.
        var seed = new DisplaySettings
        {
            Analysis = new AnalysisDisplaySettings
            {
                Tab = "compare",
                XMetric = "airmass",
                YMetric = "fwhm",
                Granularity = "session",
            },
        };

        Func<DisplaySettings, DisplaySettings>? mutate = null;
        Action<Func<DisplaySettings, DisplaySettings>> write = f => mutate = f;

        switch (key)
        {
            case "tab":
                AnalysisDisplay.WriteTab(write, AnalysisDisplay.TimeSeriesTab);
                break;
            case "x_metric":
                AnalysisDisplay.WriteXMetric(write, "humidity");
                break;
            case "y_metric":
                AnalysisDisplay.WriteYMetric(write, "hfr");
                break;
            default:
                AnalysisDisplay.WriteGranularity(write, AnalysisGranularity.Frame);
                break;
        }

        var written = mutate!(seed);

        Assert.Equal(key == "tab" ? "timeseries" : "compare", written.Analysis.Tab);
        Assert.Equal(key == "x_metric" ? "humidity" : "airmass", written.Analysis.XMetric);
        Assert.Equal(key == "y_metric" ? "hfr" : "fwhm", written.Analysis.YMetric);
        Assert.Equal(key == "granularity" ? "frame" : "session", written.Analysis.Granularity);
    }

    [Theory]
    [InlineData(null, AnalysisMetric.Humidity, AnalysisMetric.Hfr)]
    [InlineData("", AnalysisMetric.Humidity, AnalysisMetric.Hfr)]
    [InlineData("nope", AnalysisMetric.Humidity, AnalysisMetric.Hfr)]
    [InlineData("HUMIDITY", AnalysisMetric.Humidity, AnalysisMetric.Hfr)]   // Parse is ordinal
    [InlineData("humidity", AnalysisMetric.Humidity, AnalysisMetric.Hfr)]   // legal X, not a Y
    [InlineData("hfr", AnalysisMetric.Humidity, AnalysisMetric.Hfr)]        // legal Y, not an X
    [InlineData("airmass", AnalysisMetric.Airmass, AnalysisMetric.Hfr)]
    [InlineData("fwhm", AnalysisMetric.Humidity, AnalysisMetric.Fwhm)]
    [InlineData("phd2_rms_total", AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Hfr)]
    public void TheStoredKeySeam_ParsesThenFallsBackToTheKeysOwnDefault(
        string? stored, AnalysisMetric expectedX, AnalysisMetric expectedY)
    {
        // Spec 5.8.2's seam as it is written: a stored string in, a usable metric out, with
        // AnalysisMetrics.Parse answering null outside the twenty-five keys and the set membership
        // rule above turning that into the key's documented default. Nothing throws on any row.
        Assert.Equal(expectedX, AnalysisDisplay.XMetric(stored));
        Assert.Equal(expectedY, AnalysisDisplay.YMetric(stored));
    }

    [Fact]
    public void TheMetricWriters_TakeTheEnumAndStoreItsOwnKey()
    {
        // The other half of the same seam: AnalysisMetrics.Key is the enum to string step and the
        // writer composes it, so no caller spells a metric key as a literal.
        Func<DisplaySettings, DisplaySettings>? mutate = null;
        Action<Func<DisplaySettings, DisplaySettings>> write = f => mutate = f;

        AnalysisDisplay.WriteXMetric(write, AnalysisMetric.Phd2StarLostPct);
        Assert.Equal("phd2_star_lost_pct", mutate!(new DisplaySettings()).Analysis.XMetric);

        AnalysisDisplay.WriteYMetric(write, AnalysisMetric.AduStdev);
        Assert.Equal("adu_stdev", mutate!(new DisplaySettings()).Analysis.YMetric);
    }

    [Fact]
    public void TheFiveTabKeys_AreSpec1214sStripOrder()
    {
        // Spec 12.14's tab strip: Correlation, Distributions, Time Series, Matrix, Compare. The
        // exact set in order, never a Contains: the strip's order is the contract and the stored
        // spelling of each key is the suffix of its help topic id.
        Assert.Equal(
            new[] { "correlation", "distributions", "timeseries", "matrix", "compare" },
            AnalysisDisplay.TabKeys);
    }
}
