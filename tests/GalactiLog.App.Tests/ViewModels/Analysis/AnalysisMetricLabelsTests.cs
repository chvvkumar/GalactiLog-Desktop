using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

// Task 4 section 9: the one display table for spec 12.14's twenty-five metrics. The expected rows
// below are literals: the full label and the unit suffix from spec 12.14's two metric tables, the
// Correlation short name from CorrelationChart.tsx's METRIC_SHORT and the Matrix short name from
// MatrixTab.tsx's X_LABELS and Y_LABELS, both at the pinned web commit 591234b.
//
// Short labels exist for the TWENTY web metrics only. The five PHD2 night metrics have no row in
// either web table, so both short names are the full label, which is spec 12.14's second table.
//
// The walk is over Enum.GetValues<AnalysisMetric>() and not over the expected rows, so a
// twenty-sixth member fails BY NAME rather than being skipped, which is the shape
// HelpTopicsTests.TheLongestParagraphs_MatchTheSpecTextExactly uses (collision-map.md section 6).
public class AnalysisMetricLabelsTests
{
    // Ruling B15: a glyph ships only where the application's own embedded faces draw it. The
    // cmaps of all six files under src/GalactiLog.App/Assets/Fonts were read with
    // fontTools.ttLib: U+00B0 and U+03BC are in all six, U+03C3 and U+2033 are in NONE. So the
    // degree sign and the mu are asserted as code points below, and the sigma and the arcsecond
    // double prime are absent from every expected string, replaced by spec 12.14's own ASCII
    // words. A regression that reintroduces either glyph fails the last case in this file.
    private const string DegreesC = "\u00b0C";
    private const string Mu = "\u03bc";
    private const string Sigma = "\u03c3";
    private const string DoublePrime = "\u2033";
    private const string Arcsec = "arcsec";

    private static readonly Dictionary<AnalysisMetric, (string Label, string Short, string Matrix, string Unit)> Expected =
        new()
        {
            [AnalysisMetric.Humidity] = ("Humidity (%)", "humidity", "Humid.", "%"),
            [AnalysisMetric.WindSpeed] = ("Wind Speed", "wind", "Wind", ""),
            [AnalysisMetric.AmbientTemp] = ("Ambient Temp (" + DegreesC + ")", "temperature", "Temp", DegreesC),
            [AnalysisMetric.DewPoint] = ("Dew Point (" + DegreesC + ")", "dew point", "Dew Pt", DegreesC),
            [AnalysisMetric.Pressure] = ("Pressure (hPa)", "pressure", "Press.", " hPa"),
            [AnalysisMetric.CloudCover] = ("Cloud Cover (%)", "cloud cover", "Cloud", "%"),
            [AnalysisMetric.SkyQuality] = ("Sky Quality (SQM)", "sky quality", "SQM", ""),
            [AnalysisMetric.FocuserTemp] = ("Focuser Temp (" + DegreesC + ")", "focuser temp", "Focus T", DegreesC),
            [AnalysisMetric.Airmass] = ("Airmass", "airmass", "Airm.", ""),
            [AnalysisMetric.SensorTemp] = ("Sensor Temp (" + DegreesC + ")", "sensor temp", "Sensor T", DegreesC),

            [AnalysisMetric.Hfr] = ("HFR (px)", "HFR", "HFR", " px"),

            // The web writes the double prime in all three of this row's names; no embedded face
            // draws it, so spec 12.14's own word ships and the Matrix cell takes the spec's bare
            // "FWHM" (ruling B15).
            [AnalysisMetric.Fwhm] = (
                "FWHM (" + Arcsec + ")", "FWHM (" + Arcsec + ")", "FWHM", " " + Arcsec),

            [AnalysisMetric.Eccentricity] = ("Eccentricity", "eccentricity", "Ecc.", ""),
            [AnalysisMetric.GuidingRms] = ("Guiding RMS (" + Arcsec + ")", "guiding RMS", "Guide", " " + Arcsec),
            [AnalysisMetric.GuidingRmsRa] = ("Guiding RA RMS (" + Arcsec + ")", "RA guiding", "Guide RA", " " + Arcsec),
            [AnalysisMetric.GuidingRmsDec] = ("Guiding DEC RMS (" + Arcsec + ")", "DEC guiding", "Guide DEC", " " + Arcsec),
            [AnalysisMetric.DetectedStars] = ("Detected Stars", "star count", "Stars", ""),

            // The mu IS in all six faces, so MatrixTab.tsx line 19 ships as the web writes it.
            [AnalysisMetric.AduMean] = ("ADU Mean", "ADU mean", "ADU " + Mu, ""),
            [AnalysisMetric.AduMedian] = ("ADU Median", "ADU median", "ADU med", ""),

            // The sigma is in NONE of them, so spec 12.14's Matrix transcription word ships.
            [AnalysisMetric.AduStdev] = ("ADU StDev", "ADU noise", "ADU sigma", ""),

            // Spec 12.14's second table. Neither web short table carries a row for these five, so
            // both short names are the label.
            [AnalysisMetric.Phd2RmsTotal] = (
                "PHD2 RMS Total (" + Arcsec + ")", "PHD2 RMS Total (" + Arcsec + ")",
                "PHD2 RMS Total (" + Arcsec + ")", " " + Arcsec),
            [AnalysisMetric.Phd2RmsRa] = (
                "PHD2 RMS RA (" + Arcsec + ")", "PHD2 RMS RA (" + Arcsec + ")",
                "PHD2 RMS RA (" + Arcsec + ")", " " + Arcsec),
            [AnalysisMetric.Phd2RmsDec] = (
                "PHD2 RMS Dec (" + Arcsec + ")", "PHD2 RMS Dec (" + Arcsec + ")",
                "PHD2 RMS Dec (" + Arcsec + ")", " " + Arcsec),
            [AnalysisMetric.Phd2StarLostPct] = (
                "PHD2 Star Lost (%)", "PHD2 Star Lost (%)", "PHD2 Star Lost (%)", "%"),
            [AnalysisMetric.Phd2SnrMean] = ("PHD2 Guide SNR", "PHD2 Guide SNR", "PHD2 Guide SNR", ""),
        };

    [Fact]
    public void EveryMetric_HasItsSpec1214LabelBothShortNamesAndItsUnitSuffix()
    {
        // Red against a missing or transposed row: the member is named in the failure message
        // rather than the table silently answering a neighbour's label.
        foreach (var metric in Enum.GetValues<AnalysisMetric>())
        {
            Assert.True(Expected.ContainsKey(metric), $"spec 12.14 has no transcribed row for {metric}");

            var (label, shortName, matrix, unit) = Expected[metric];
            var actual = AnalysisMetricLabels.For(metric);

            Assert.Equal(label, actual.Label);
            Assert.Equal(shortName, actual.Short);
            Assert.Equal(matrix, actual.Matrix);
            Assert.Equal(unit, actual.Unit);

            // The two conveniences answer the same table.
            Assert.Equal(label, AnalysisMetricLabels.Label(metric));
            Assert.Equal(unit, AnalysisMetricLabels.Unit(metric));
        }
    }

    [Fact]
    public void TheTranscribedRows_CoverTheEnumAndNothingElse()
    {
        // The other direction, so a row for a metric that no longer exists is a failure too.
        Assert.Equal(
            Enum.GetValues<AnalysisMetric>().OrderBy(m => (int)m),
            Expected.Keys.OrderBy(m => (int)m));
    }

    [Fact]
    public void NoLabel_CarriesAGlyphTheEmbeddedFacesDoNotDraw()
    {
        // Ruling B15, asserted by code point over every string the table can put on screen. The
        // mu ships because the six faces carry U+03BC; the sigma and the double prime do not ship
        // because none of them carries U+03C3 or U+2033, and a reader would see the notdef box.
        // Red against a table transcribed from the web verbatim: "ADU sigma" reads U+03C3 and all
        // eight arcsecond rows read U+2033.
        var everyString = Enum.GetValues<AnalysisMetric>()
            .Select(AnalysisMetricLabels.For)
            .SelectMany(row => new[] { row.Label, row.Short, row.Matrix, row.Unit })
            .ToArray();

        Assert.DoesNotContain(everyString, text => text.Contains(Sigma, StringComparison.Ordinal));
        Assert.DoesNotContain(everyString, text => text.Contains(DoublePrime, StringComparison.Ordinal));

        // And the two that DO ship are still there, so the sweep above cannot pass by emptying the
        // table.
        Assert.Equal("ADU " + Mu, AnalysisMetricLabels.For(AnalysisMetric.AduMean).Matrix);
        Assert.Equal(0x03bc, AnalysisMetricLabels.For(AnalysisMetric.AduMean).Matrix[^1]);
        Assert.Equal(0x00b0, AnalysisMetricLabels.For(AnalysisMetric.AmbientTemp).Unit[0]);
    }

    [Fact]
    public void ThisFile_AddsNoMetricList()
    {
        // Section 9: the three picker lists are the seam's, and the Distributions histogram and
        // the Time Series picker offer all twenty in the table's own order, which is X then Y.
        var all = AnalysisMetrics.X.Concat(AnalysisMetrics.Y).ToArray();

        Assert.Equal(20, all.Length);
        Assert.Equal(AnalysisMetric.Humidity, all[0]);
        Assert.Equal(AnalysisMetric.SensorTemp, all[9]);
        Assert.Equal(AnalysisMetric.Hfr, all[10]);
        Assert.Equal(AnalysisMetric.AduStdev, all[19]);
        Assert.All(all, metric => Assert.DoesNotContain(metric, AnalysisMetrics.Phd2X));
    }

    // ---- the one picker choice shape ------------------------------------------------------------

    // The five metric pickers on the page bind one shape, built here and labelled from the table
    // above. These four cases are the shape's own; each picker's list and order stay pinned in its
    // own tab's cases.

    [Fact]
    public void TheBuilder_AnswersOneChoicePerMetric_InTheOrderItWasGiven()
    {
        // Red against a builder that sorts, groups or de-duplicates: every picker's order is spec
        // 12.14's and is the caller's list, not this file's.
        IReadOnlyList<AnalysisMetric> given =
            [AnalysisMetric.AduStdev, AnalysisMetric.Humidity, AnalysisMetric.Phd2RmsDec, AnalysisMetric.Hfr];

        var choices = AnalysisMetricLabels.Choices(given);

        Assert.Equal(given.Count, choices.Count);
        Assert.Equal(given.ToList(), choices.Select(choice => choice.Metric!.Value).ToList());
        Assert.Equal(
            given.Select(AnalysisMetricLabels.Label).ToList(),
            choices.Select(choice => choice.Label).ToList());
        Assert.All(choices, choice => Assert.True(choice.IsSelectable));
        Assert.All(choices, choice => Assert.False(choice.IsHeader));
    }

    [Fact]
    public void EveryMetric_RoundTripsThroughItsChoice()
    {
        // A picker matches its selection on the metric the entry carries, so a choice has to answer
        // the metric it was built for, for all twenty-five. Red against a builder that drops the
        // metric and leaves the label to identify the entry.
        var metrics = Enum.GetValues<AnalysisMetric>();
        var choices = AnalysisMetricLabels.Choices(metrics);

        foreach (var metric in metrics)
        {
            var choice = Assert.Single(choices, entry => entry.Metric == metric);
            Assert.Equal(AnalysisMetricLabels.Label(metric), choice.Label);
        }
    }

    [Fact]
    public void AllTwentyFiveLabels_AreDistinct()
    {
        // Four of them differ only by their leading word (Ambient Temp, Dew Point, Focuser Temp and
        // Sensor Temp all carry the same parenthetical), so this is a real guard and not a
        // formality: before the fold, two pickers mapped a selection back through the label text
        // and a duplicate would have silently selected the earlier metric. Red the day two rows of
        // the table above share a label.
        var labels = Enum.GetValues<AnalysisMetric>().Select(AnalysisMetricLabels.Label).ToArray();

        Assert.Equal(25, labels.Length);
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AGroupHeader_CarriesNoMetricAndCannotBeSelected()
    {
        // Avalonia has no optgroup, so a grouped picker carries its header as a non-selectable
        // entry in one flat list. Red against a header built as an ordinary choice: it becomes a
        // selectable metric and an unparseable key reaches a query.
        var header = AnalysisMetricLabels.Header("Guiding (PHD2)");

        Assert.Null(header.Metric);
        Assert.False(header.IsSelectable);
        Assert.True(header.IsHeader);
        Assert.Equal("Guiding (PHD2)", header.Label);
    }

    // ---- the one selection rule ------------------------------------------------------------------

    // The rule five pickers used to carry a copy of each. Each case below is red against the arm it
    // names being dropped from the helper, and the four folded pickers keep their own cases: this
    // file pins the rule and each tab pins that it follows it.

    [Fact]
    public void AChosenMetric_IsAssignedAndNothingIsRestored()
    {
        var assigned = new List<AnalysisMetric>();
        var restores = 0;

        AnalysisMetricChoice.Apply(
            new AnalysisMetricChoice(AnalysisMetric.Fwhm, "FWHM"),
            AnalysisMetric.Hfr,
            assigned.Add,
            () => restores++);

        Assert.Equal([AnalysisMetric.Fwhm], assigned);
        Assert.Equal(0, restores);
    }

    [Fact]
    public void AGroupHeader_RestoresAndAssignsNothing()
    {
        // The header is a real entry in a flat list, so a picker can be set to it. Red against a
        // helper with no header arm: the assignment runs with no metric to assign, or the picker
        // is left showing an entry no query can be issued for.
        var assigned = new List<AnalysisMetric>();
        var restores = 0;

        AnalysisMetricChoice.Apply(
            AnalysisMetricLabels.Header("Guiding (PHD2)"),
            AnalysisMetric.Hfr,
            assigned.Add,
            () => restores++);

        Assert.Empty(assigned);
        Assert.Equal(1, restores);
    }

    [Fact]
    public void AClearedSelection_RestoresAndAssignsNothing()
    {
        var assigned = new List<AnalysisMetric>();
        var restores = 0;

        AnalysisMetricChoice.Apply(null, AnalysisMetric.Hfr, assigned.Add, () => restores++);

        Assert.Empty(assigned);
        Assert.Equal(1, restores);
    }

    [Fact]
    public void TheMetricAlreadyChosen_DoesNothingAtAll()
    {
        // Neither arm runs: an assignment would write the stored key and fire a query for the
        // metric already on screen, and a restore would raise a property nothing moved.
        var assigned = new List<AnalysisMetric>();
        var restores = 0;

        AnalysisMetricChoice.Apply(
            new AnalysisMetricChoice(AnalysisMetric.Hfr, "HFR (px)"),
            AnalysisMetric.Hfr,
            assigned.Add,
            () => restores++);

        Assert.Empty(assigned);
        Assert.Equal(0, restores);
    }
}
