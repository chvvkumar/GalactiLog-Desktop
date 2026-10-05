using GalactiLog.App.ViewModels.Preview;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.PreviewModalViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 11.5's metadata strip (PAR-011): one wrapping row of Border.tag badges under the
// path, in a fixed order, carrying the frame table's own cell text and the frame table's own band
// and deviation sentence. Plain xunit facts: no window, no dispatcher, no database.
//
// The strip is built in PreviewFrameViewModel.From, which is the one place a frame table row and
// the modal's navigation entry meet, so every case here goes through it rather than constructing a
// badge by hand.
public class PreviewMetadataStripTests
{
    // Spec 11.5's fixed order. These are FrameColumns' own titles, not retyped strings: a badge
    // and the table header it names cannot disagree.
    private static readonly string[] ExpectedOrder =
        ["Filter", "Exp", "HFR", "Ecc", "FWHM", "Stars", "RMS"];

    [Fact]
    public void TheStrip_HasTheSevenBadgesInTheSpecifiedOrder()
    {
        var strip = StripFor(Complete());

        Assert.Equal(ExpectedOrder, strip.Select(badge => badge.Label).ToList());
    }

    [Fact]
    public void ABadgeWithANullValue_IsNotDrawn()
    {
        // "A badge whose value is null is not drawn" means not drawn, not drawn empty and not
        // drawn with a placeholder. The web renders an em dash for a null; the port draws nothing.
        var strip = StripFor(Complete() with { Fwhm = null });

        Assert.DoesNotContain(strip, badge => badge.Label == "FWHM");
        Assert.Equal(6, strip.Count);
        Assert.All(strip, badge => Assert.False(string.IsNullOrWhiteSpace(badge.Value)));
    }

    [Fact]
    public void AFrameWithNoGuiding_LeavesNoEmptyBadge()
    {
        var strip = StripFor(Complete() with { GuidingRmsArcsec = null });

        Assert.DoesNotContain(strip, badge => badge.Label == "RMS");
        Assert.All(strip, badge => Assert.NotEqual("", badge.Value));
    }

    [Theory]
    [InlineData("Filter", "Ha")]
    [InlineData("Exp", "300")]
    [InlineData("HFR", "2.41")]
    [InlineData("Ecc", "0.42")]
    [InlineData("FWHM", "1.90")]
    [InlineData("Stars", "812")]
    [InlineData("RMS", "0.55")]
    public void TheBadgeText_MatchesTheFrameTablesOwnCells(string label, string expected)
    {
        // Every value is the text the frame table's cell already carries, so a number cannot read
        // differently in two places.
        var row = Row(Complete());
        var badge = StripFor(Complete()).Single(entry => entry.Label == label);

        Assert.Equal(expected, badge.Value);
        Assert.Equal(CellTextFor(row, label), badge.Value);
    }

    [Fact]
    public void TheGradedBadges_CarryTheBandFromTheFrameRow()
    {
        var row = Row(Complete(), Grading(hfrZ: -2.0, eccZ: 2.0, fwhmZ: 4.0, starsZ: 0.5, rmsZ: 2.5));
        var strip = PreviewFrameViewModel.From(row).Badges;

        Assert.Equal(QualityBand.Better, Badge(strip, "HFR").Band);
        Assert.Equal(QualityBand.Watch, Badge(strip, "Ecc").Band);
        Assert.Equal(QualityBand.Reject, Badge(strip, "FWHM").Band);
        Assert.Equal(QualityBand.Neutral, Badge(strip, "Stars").Band);
        Assert.Equal(QualityBand.Watch, Badge(strip, "RMS").Band);

        // The modal takes the row's own answer rather than reaching a second one.
        Assert.Equal(row.HfrCell.Band, Badge(strip, "HFR").Band);
        Assert.Equal(row.GuidingRmsCell.Band, Badge(strip, "RMS").Band);
    }

    [Fact]
    public void AWatchBadge_AlsoRendersInMetricWorst()
    {
        var strip = StripFor(Complete(), Grading(hfrZ: 2.0));

        Assert.Equal(QualityBand.Watch, Badge(strip, "HFR").Band);
        Assert.True(Badge(strip, "HFR").IsWorst);
        Assert.True(Badge(strip, "HFR").IsWatch);
    }

    [Fact]
    public void ARejectBadge_AlsoRendersInMetricWorst()
    {
        var strip = StripFor(Complete(), Grading(hfrZ: 3.5));

        Assert.Equal(QualityBand.Reject, Badge(strip, "HFR").Band);
        Assert.True(Badge(strip, "HFR").IsWorst);
        Assert.True(Badge(strip, "HFR").IsReject);
    }

    [Fact]
    public void ABetterBadge_DoesNotRenderInMetricWorst()
    {
        var strip = StripFor(Complete(), Grading(hfrZ: -2.0));

        Assert.True(Badge(strip, "HFR").IsBetter);
        Assert.False(Badge(strip, "HFR").IsWorst);
    }

    [Fact]
    public void AGradedBadge_CarriesTheFrameTablesOwnDeviationSentence()
    {
        var row = Row(Complete(), Grading(hfrZ: 2.0));
        var badge = Badge(PreviewFrameViewModel.From(row).Badges, "HFR");

        Assert.NotNull(badge.Tooltip);
        Assert.Equal(row.HfrCell.Tooltip, badge.Tooltip);
        Assert.Contains("MAD units", badge.Tooltip!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUngradedBadge_CarriesNoBandAndNoTooltip()
    {
        // Spec 11.5: a frame the grading left ungraded shows the value with no band and no
        // tooltip, rather than a neutral claim.
        var strip = StripFor(Complete(), Grading(hfrZ: null));

        Assert.Equal(QualityBand.Neutral, Badge(strip, "HFR").Band);
        Assert.Null(Badge(strip, "HFR").Tooltip);
        Assert.False(Badge(strip, "HFR").IsWorst);
    }

    [Fact]
    public void TheTwoIdentityBadges_AreNeverGraded()
    {
        var strip = StripFor(Complete(), Grading(hfrZ: 3.5, eccZ: 3.5, fwhmZ: 3.5, starsZ: 3.5, rmsZ: 3.5));

        foreach (var label in (string[])["Filter", "Exp"])
        {
            Assert.Equal(QualityBand.Neutral, Badge(strip, label).Band);
            Assert.Null(Badge(strip, label).Tooltip);
            Assert.False(Badge(strip, label).IsWorst);
        }
    }

    [Fact]
    public void AFrameOpenedWithNoRow_CarriesNoStripAtAll()
    {
        // Renamed to what it asserts (Task 7 review P3). A caller holding only an image id gets no
        // strip rather than a second formatting of the same numbers, so there is nothing here that
        // could carry a band; that is a different fact from spec 11.5's sentence about a surface
        // with no grading, which the case below covers.
        var frame = Factory.Frame(1, withRow: false);

        Assert.Empty(frame.Badges);
    }

    [Fact]
    public void ARowWithNoGrading_CarriesEverySevenBadgeWithNoBandAndNoTooltip()
    {
        // Spec 11.5's own sentence: "a frame opened from a surface that has no grading shows the
        // value with no band and no tooltip". In this port that surface is a frame table row whose
        // Grading is null, and the strip is complete: seven badges, seven values, no marks.
        var strip = StripFor(Complete(), grading: null);

        Assert.Equal(7, strip.Count);
        Assert.All(strip, badge => Assert.False(string.IsNullOrWhiteSpace(badge.Value)));
        Assert.All(strip, badge => Assert.Equal(QualityBand.Neutral, badge.Band));
        Assert.All(strip, badge => Assert.Null(badge.Tooltip));
        Assert.All(strip, badge => Assert.False(badge.IsWorst));
    }

    [Fact]
    public void TheStrip_CarriesNoVerdictBadge()
    {
        // task1-report.md departure 5: the web's strip leads with a verdict pill from its
        // stacking-export gates, which this port does not ship. There is no session badge either.
        var strip = StripFor(Complete(), Grading(hfrZ: 3.5));

        Assert.DoesNotContain(strip, badge => badge.Label is "Verdict" or "Session" or "Date");
        Assert.Equal(7, strip.Count);
    }

    [Fact]
    public void TheStrip_IsRebuiltOnEveryStep()
    {
        var rows = Factory.Rows(
            Factory.Row(Guid.NewGuid(), @"C:\a\one.fits", "one.fits"),
            Factory.Row(Guid.NewGuid(), @"C:\a\two.fits", "two.fits") with { FilterUsed = "OIII" });
        using var harness = Factory.Create([.. rows.Select(PreviewFrameViewModel.From)]);

        var before = Badge(harness.ViewModel.Current.Badges, "Filter").Value;
        harness.ViewModel.NextCommand.Execute(null);
        var after = Badge(harness.ViewModel.Current.Badges, "Filter").Value;

        Assert.Equal("Ha", before);
        Assert.Equal("OIII", after);

        // It describes the frame on screen, so it follows Current and is not mirrored onto the
        // modal: there is no Badges member on the view-model to go stale.
        Assert.Null(typeof(PreviewModalViewModel).GetProperty("Badges"));
    }

    [Fact]
    public void TheModal_GradesNothingItself()
    {
        // Spec 11.5's own sentence, asserted the way NightStripTests asserts its colour rule: over
        // the source of the one folder. The bands and the deviation sentences arrive from the
        // frame row; a second grading path here would be two answers to one question.
        foreach (var file in Directory.EnumerateFiles(PreviewViewModelFolder(), "*.cs"))
        {
            var source = File.ReadAllText(file);

            Assert.DoesNotContain("FrameQuality", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MadZ", source, StringComparison.Ordinal);
            Assert.DoesNotContain("BandForZ", source, StringComparison.Ordinal);
            Assert.DoesNotContain("BandForScore", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CombinedScore", source, StringComparison.Ordinal);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static PreviewBadge Badge(IReadOnlyList<PreviewBadge> strip, string label)
        => strip.Single(entry => entry.Label == label);

    private static IReadOnlyList<PreviewBadge> StripFor(FrameRow frame, FrameGrading? grading = null)
        => PreviewFrameViewModel.From(Row(frame, grading)).Badges;

    private static FrameRowViewModel Row(FrameRow frame, FrameGrading? grading = null)
        => Factory.Rows(frame with { Grading = grading })[0];

    /// <summary>A frame carrying every one of the seven badge values, so a case about one absent
    /// value is not silently also a case about six others.</summary>
    private static FrameRow Complete()
        => Factory.Row(Guid.NewGuid(), @"C:\Astro\M 31\2025-12-07\frame_0001.fits", "frame_0001.fits");

    private static FrameGrading Grading(
        double? hfrZ = null,
        double? eccZ = null,
        double? fwhmZ = null,
        double? starsZ = null,
        double? rmsZ = null)
        => new(
            SessionHfr: new(hfrZ, 2.0), RigHfr: new(hfrZ, 2.0),
            SessionEccentricity: new(eccZ, 0.4), RigEccentricity: new(eccZ, 0.4),
            SessionFwhm: new(fwhmZ, 1.8), RigFwhm: new(fwhmZ, 1.8),
            DetectedStars: new(starsZ, 800),
            AduMedian: new(null, null),
            GuidingRms: new(rmsZ, 0.5));

    private static string CellTextFor(FrameRowViewModel row, string label) => label switch
    {
        "Filter" => row.FilterText,
        "Exp" => row.ExposureText,
        "HFR" => row.MedianHfrText,
        "Ecc" => row.EccentricityText,
        "FWHM" => row.FwhmText,
        "Stars" => row.DetectedStarsText,
        "RMS" => row.GuidingRmsText,
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Not a badge label."),
    };

    private static string PreviewViewModelFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var folder = Path.Combine(
            directory!.FullName, "src", "GalactiLog.App", "ViewModels", "Preview");
        Assert.True(Directory.Exists(folder), $"{folder} was not found.");
        return folder;
    }
}
