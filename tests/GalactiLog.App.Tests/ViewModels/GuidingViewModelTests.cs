using Avalonia.Headless.XUnit;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.StatisticsViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.5's Guiding section, view-model side: the nine scorecard columns and their sub-row, the
/// grading that stays neutral below eight rigs, both forms of the empty notice, and the altitude
/// card's arithmetic. No window, no database: every case builds its own <see cref="GuidingStats"/>.
/// </summary>
public class GuidingViewModelTests
{
    private static GuidingViewModel Build(GuidingStats stats)
    {
        ChartTheme.Apply();
        return new GuidingViewModel(stats, new BandBrushes(), new ArcBrushes());
    }

    [AvaloniaFact]
    public void Scorecard_RendersEveryColumn_AndTheGuideExposureSubRow()
    {
        var page = Build(Factory.Guiding(rigs:
        [
            Factory.Rig(telescope: "Alpha", sessions: 12, gated: 3),
            Factory.Rig(
                telescope: "Zeta",
                sessions: 4,
                gated: 0,
                hours: 9.05d,
                rmsTotal: 1.4d,
                rmsRa: 0.9d,
                rmsDec: 1.1d,
                filtered: 1.2d,
                ratio: 1.22d,
                settle: 7.4d,
                exposures: [500]),
        ]));

        var alpha = page.Rows[0];
        Assert.Equal("Alpha", alpha.Rig);
        Assert.Equal("12", alpha.Sessions);
        Assert.Equal("3 too short to score", alpha.GatedNote);
        Assert.True(alpha.HasGatedNote);
        Assert.Equal("31.4", alpha.Hours);
        Assert.Equal("0.72", alpha.RmsTotal.Text);
        Assert.Equal("0.48", alpha.RmsRa.Text);
        Assert.Equal("0.54", alpha.RmsDec.Text);
        Assert.Equal("0.61", alpha.Filtered);
        Assert.Equal("1.10", alpha.DecRaRatio);
        Assert.Equal("3.2", alpha.SettleSeconds);
        Assert.Equal("Guide exposure: 1000, 2000 ms", alpha.GuideExposure);

        var zeta = page.Rows[1];
        Assert.Equal("Zeta", zeta.Rig);
        Assert.Equal("4", zeta.Sessions);
        Assert.Equal("9.1", zeta.Hours);
        Assert.Equal("1.40", zeta.RmsTotal.Text);
        Assert.Equal("0.90", zeta.RmsRa.Text);
        Assert.Equal("1.10", zeta.RmsDec.Text);
        Assert.Equal("1.20", zeta.Filtered);
        Assert.Equal("1.22", zeta.DecRaRatio);
        Assert.Equal("7.4", zeta.SettleSeconds);
        Assert.Equal("Guide exposure: 500 ms", zeta.GuideExposure);
    }

    [AvaloniaFact]
    public void NoGatedSession_RendersNoTooShortClause()
    {
        var page = Build(Factory.Guiding(rigs: [Factory.Rig(gated: 0)]));

        Assert.Equal("", page.Rows[0].GatedNote);
        Assert.False(page.Rows[0].HasGatedNote);
    }

    [AvaloniaFact]
    public void NoGuideExposure_RendersNoSubRowAtAll()
    {
        var page = Build(Factory.Guiding(rigs: [Factory.Rig(exposures: [])]));

        Assert.Equal("", page.Rows[0].GuideExposure);
        Assert.False(page.Rows[0].HasGuideExposure);
    }

    [AvaloniaFact]
    public void ANullRatio_RendersTheMissingPlaceholder_AndNotAZero()
    {
        var page = Build(Factory.Guiding(rigs:
            [Factory.Rig(ratio: null, settle: null, filtered: null)]));

        Assert.Equal(MetricText.Missing, page.Rows[0].DecRaRatio);
        Assert.Equal(MetricText.Missing, page.Rows[0].SettleSeconds);
        Assert.Equal(MetricText.Missing, page.Rows[0].Filtered);
    }

    [AvaloniaFact]
    public void Rows_ComeBackInTheQuerysOrder_AndAreNotResorted()
    {
        // The query already ordered the rigs and a second sort here would be a second answer to
        // the same question, so a deliberately unsorted list renders unsorted.
        var page = Build(Factory.Guiding(rigs:
            [Factory.Rig(telescope: "Zeta"), Factory.Rig(telescope: "Alpha")]));

        Assert.Equal(["Zeta", "Alpha"], page.Rows.Select(row => row.Rig));
    }

    [AvaloniaFact]
    public void TwoRigsWithWildlyDifferentRms_GradeNothingAtAll()
    {
        // Ruling G4, proved without a view: MetricBaseline.N counts rigs with a figure, and
        // FrameQuality.MinGroup is 8, so a two-rig library is neutral however far apart the two are.
        ChartTheme.Apply();
        var brushes = new BandBrushes();
        var page = new GuidingViewModel(
            Factory.Guiding(rigs:
            [
                Factory.Rig(telescope: "Good", rmsTotal: 0.3d, rmsRa: 0.2d, rmsDec: 0.2d),
                Factory.Rig(telescope: "Bad", rmsTotal: 9.0d, rmsRa: 8.0d, rmsDec: 8.0d),
            ]),
            brushes,
            new ArcBrushes());

        Assert.All(page.Rows, row =>
        {
            Assert.Equal(QualityBand.Neutral, row.RmsTotal.Band);
            Assert.Equal(QualityBand.Neutral, row.RmsRa.Band);
            Assert.Equal(QualityBand.Neutral, row.RmsDec.Band);
            Assert.Same(brushes.Neutral, row.RmsTotal.Brush);
            Assert.Same(brushes.Neutral, row.RmsRa.Brush);
            Assert.Same(brushes.Neutral, row.RmsDec.Brush);
        });
    }

    [AvaloniaFact]
    public void EightRigsWithASpread_GradeTheOutlier()
    {
        double[] totals = [0.5d, 0.6d, 0.7d, 0.8d, 0.9d, 1.0d, 1.1d, 3.0d];
        var page = Build(Factory.Guiding(rigs:
        [
            .. totals.Select((value, index) => Factory.Rig(
                telescope: "Rig" + index,
                rmsTotal: value,
                rmsRa: value,
                rmsDec: value)),
        ]));

        Assert.Equal(QualityBand.Reject, page.Rows[7].RmsTotal.Band);
        Assert.Contains(page.Rows, row => row.RmsTotal.Band != QualityBand.Neutral);
    }

    [AvaloniaFact]
    public void SevenRigsWithASpread_StillGradeNothing()
    {
        // Review finding P3-4, the boundary the other two grading cases leave open: a gate written
        // `N < 7` rather than `N < MinGroup` passes both of them and grades a seven-rig library,
        // which spec 12.5 names by hand as the thing that must not happen ("One rig is therefore
        // neutral, and so are seven"). This is the eight-rig seed with one value dropped.
        double[] totals = [0.5d, 0.6d, 0.7d, 0.8d, 0.9d, 1.0d, 3.0d];
        var page = Build(Factory.Guiding(rigs:
        [
            .. totals.Select((value, index) => Factory.Rig(
                telescope: "Rig" + index,
                rmsTotal: value,
                rmsRa: value,
                rmsDec: value)),
        ]));

        Assert.Equal(7, page.Rows.Count);
        Assert.All(page.Rows, row =>
        {
            Assert.Equal(QualityBand.Neutral, row.RmsTotal.Band);
            Assert.Equal(QualityBand.Neutral, row.RmsRa.Band);
            Assert.Equal(QualityBand.Neutral, row.RmsDec.Band);
            Assert.Equal("", row.RmsTotal.Tooltip);
        });
    }

    [AvaloniaFact]
    public void ALibraryWithNoAltitudeAtAll_HasRowsAndNoArc()
    {
        // Review finding P2-2. A library whose guiding sessions all carry a null alt_deg, which is
        // the ASIAIR shape and the inherited fixture log, has rigs on the scorecard and no band row
        // at all. The altitude card must then draw nothing rather than a heading over an empty grid,
        // two legends explaining absent wedges and a disclosure onto an empty table.
        var page = Build(Factory.Guiding(rigs: [Factory.Rig()], bands: []));

        Assert.False(page.IsEmpty);
        Assert.True(page.ShowCards);
        Assert.False(page.HasArcs);
        Assert.Empty(page.Arcs);
        Assert.Empty(page.TableRows);
    }

    [AvaloniaFact]
    public void ALibraryWithOneBandRow_HasAnArc()
    {
        // The other side of the same flag, so it cannot be satisfied by a constant false.
        var page = Build(Factory.Guiding(rigs: [Factory.Rig()], bands: [Factory.Band()]));

        Assert.True(page.HasArcs);
        Assert.Single(page.Arcs);
    }

    [AvaloniaFact]
    public void EmptyNotice_FirstForm_WhenNoSessionIsUnmapped()
    {
        var page = Build(Factory.NoGuiding());

        Assert.True(page.IsEmpty);
        Assert.False(page.ShowCards);
        Assert.Equal("No PHD2 guide logs catalogued.", page.EmptyNoticeText);
        Assert.Equal("Enable guide log scanning", page.EmptyNoticeLink);
        Assert.Equal(SettingsDestination.LibraryGuideLogSwitch, page.EmptyNoticeDestination);
    }

    [AvaloniaFact]
    public void EmptyNotice_SecondFormWins_WhenSessionsAreUnmapped()
    {
        // A library with logs and no mapping is one click from working and must not be told to
        // enable something it already enabled.
        var page = Build(Factory.NoGuiding(unmapped: 4));

        Assert.Equal(
            "4 guiding sessions found but no PHD2 profile is mapped to a telescope.",
            page.EmptyNoticeText);
        Assert.Equal("Map profiles", page.EmptyNoticeLink);
        Assert.Equal(SettingsDestination.EquipmentPhd2Profiles, page.EmptyNoticeDestination);
    }

    [AvaloniaFact]
    public void EmptyNotice_IsAbsent_WhenARigHasARow_EvenWithUnmappedSessions()
    {
        var page = Build(Factory.Guiding(unmapped: 4, rigs: [Factory.Rig()]));

        Assert.False(page.IsEmpty);
        Assert.True(page.ShowCards);
    }

    [AvaloniaFact]
    public void TheLink_RaisesItsDestination()
    {
        ChartTheme.Apply();
        SettingsDestination? raised = null;
        var page = new GuidingViewModel(
            Factory.NoGuiding(unmapped: 2),
            new BandBrushes(),
            new ArcBrushes(),
            destination => raised = destination);

        page.OpenSettingsCommand.Execute(null);

        Assert.Equal(SettingsDestination.EquipmentPhd2Profiles, raised);
    }

    /// <summary>
    /// Phase 15B fixer F4. The wedge and the legend that explains it print the same glyph, which
    /// is the web's own <c>TIMES</c> and the one spec 12.5 names, and neither prints the ASCII
    /// letter. Asserted on the code point, never on a pasted character.
    /// </summary>
    /// <remarks>
    /// A failure looks like a wedge reading <c>x1.61</c> under a legend that explains a symbol the
    /// card does not show, and a spec sentence that is false about shipped code. The repository
    /// forbids the em dash and the en dash; the multiplication sign is neither, which is why this
    /// case names the code point rather than avoiding it.
    /// </remarks>
    [AvaloniaFact]
    public void TheWedgeRatioAndTheLegend_PrintTheTimesSign_AndNoAsciiLetter()
    {
        var page = Build(Factory.Guiding(
            rigs: [Factory.Rig()],
            bands:
            [
                Factory.Band(band: GuidingAltitudeBand.Below30, sessions: 2, rmsTotal: 0.75d),
                Factory.Band(band: GuidingAltitudeBand.Above60, sessions: 5, rmsTotal: 0.50d),
            ]));

        var wedge = Assert.Single(page.Arcs).Wedges.First(entry => entry.HasRatio);

        Assert.StartsWith("×", wedge.Ratio, StringComparison.Ordinal);
        Assert.DoesNotContain("x", wedge.Ratio, StringComparison.Ordinal);

        Assert.Contains("×", GuidingViewModel.RankingLegend, StringComparison.Ordinal);
        Assert.DoesNotContain("times figure", GuidingViewModel.RankingLegend, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AltitudeCard_RatiosAreAgainstTheRigsOwnAboveSixtyBand()
    {
        var page = Build(Factory.Guiding(
            rigs: [Factory.Rig()],
            bands:
            [
                Factory.Band(band: GuidingAltitudeBand.Below30, sessions: 2, rmsTotal: 0.75d),
                Factory.Band(band: GuidingAltitudeBand.From30To60, sessions: 3, rmsTotal: 0.60d),
                Factory.Band(band: GuidingAltitudeBand.Above60, sessions: 5, rmsTotal: 0.50d),
            ]));

        var rig = Assert.Single(page.Arcs);
        Assert.Equal("0.50 to 0.75 arcsec, 10 sessions", rig.Subtitle);
        // Phase 15B fixer F4: the times sign the spec and the web both print, asserted as its code
        // point rather than as a pasted glyph, so a source file saved in another encoding cannot
        // make this read as green. The rows above asserted the ASCII letter "x", which was the
        // defect.
        Assert.Equal(
            ["×1.50", "×1.20", "×1.00"],
            rig.Wedges.Select(wedge => wedge.Ratio));
        Assert.Equal(["0.75", "0.60", "0.50"], rig.Wedges.Select(wedge => wedge.ValueText));
        Assert.Equal(["n 2", "n 3", "n 5"], rig.Wedges.Select(wedge => wedge.CountText));

        // The three shades are ranked inside this rig, so all three differ.
        Assert.Equal(3, rig.Wedges.Select(wedge => wedge.Fill).Distinct().Count());
    }

    [AvaloniaFact]
    public void AltitudeCard_ARigWithOneBand_TakesTheMiddleStep_AndTheOthersReadNoData()
    {
        ChartTheme.Apply();
        var arcs = new ArcBrushes();
        var page = new GuidingViewModel(
            Factory.Guiding(
                rigs: [Factory.Rig()],
                bands: [Factory.Band(band: GuidingAltitudeBand.From30To60, sessions: 6, rmsTotal: 0.9d)]),
            new BandBrushes(),
            arcs);

        var rig = Assert.Single(page.Arcs);
        Assert.Equal(
            [AltitudeWedge.NoDataText, "0.90", AltitudeWedge.NoDataText],
            rig.Wedges.Select(wedge => wedge.ValueText));
        Assert.Equal([false, true, false], rig.Wedges.Select(wedge => wedge.HasData));

        // The middle accent step by name, not a false extreme: index 2 of the ramp is the second of
        // its three accent strengths, and the two empty wedges take index 0, the empty shade.
        Assert.Same(arcs.Steps[2], rig.Wedges[1].Fill);
        Assert.Same(arcs.Steps[0], rig.Wedges[0].Fill);
        Assert.Same(arcs.Steps[0], rig.Wedges[2].Fill);
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(0d)]
    public void AltitudeCard_WithNoUsableAboveSixtyFigure_EveryRatioIsAbsent(double? aboveSixty)
    {
        var bands = new List<GuidingAltitudeBandRow>
        {
            Factory.Band(band: GuidingAltitudeBand.Below30, sessions: 2, rmsTotal: 0.8d),
            Factory.Band(band: GuidingAltitudeBand.From30To60, sessions: 2, rmsTotal: 0.7d),
        };
        if (aboveSixty is { } figure)
        {
            bands.Add(Factory.Band(band: GuidingAltitudeBand.Above60, sessions: 1, rmsTotal: figure));
        }

        var page = Build(Factory.Guiding(rigs: [Factory.Rig()], bands: bands));

        var rig = Assert.Single(page.Arcs);
        Assert.All(rig.Wedges, wedge => Assert.False(wedge.HasRatio));
        Assert.All(rig.Wedges, wedge => Assert.Equal(MetricText.Missing, wedge.RatioText));

        // And the wedges still print their own RMS.
        Assert.Equal("0.80", rig.Wedges[0].ValueText);
        Assert.Equal("0.70", rig.Wedges[1].ValueText);
    }

    [AvaloniaFact]
    public void AltitudeCard_ARigWithNoRmsInAnyBand_ReadsNoRmsRecorded()
    {
        var page = Build(Factory.Guiding(
            rigs: [Factory.Rig()],
            bands:
            [
                Factory.Band(band: GuidingAltitudeBand.Below30, sessions: 2, rmsTotal: null, rmsRa: null, rmsDec: null),
            ]));

        var rig = Assert.Single(page.Arcs);
        Assert.Equal("No RMS recorded, 2 sessions", rig.Subtitle);

        // The band has a row, so its wedge is drawn and its session count printed; only the figure
        // is missing.
        Assert.True(rig.Wedges[0].HasData);
        Assert.Equal(MetricText.Missing, rig.Wedges[0].RmsTotal);
        Assert.Equal("n 2", rig.Wedges[0].CountText);
    }

    [AvaloniaFact]
    public void ASessionWithNoAltitude_CountsOnTheScorecard_AndInNoWedge()
    {
        // Ruling G3's shape: the altitude comes from the log's own pointing line, and an ASIAIR
        // section that carried none puts its session in the rig row and in no band row at all.
        var page = Build(Factory.Guiding(
            rigs: [Factory.Rig(telescope: "RC8", sessions: 9)],
            bands: [Factory.Band(telescope: "RC8", band: GuidingAltitudeBand.Above60, sessions: 4)]));

        // Nine sessions on the row, four of them in a band: the other five carried no altitude.
        Assert.Equal("9", page.Rows[0].Sessions);
        var rig = Assert.Single(page.Arcs);
        Assert.Equal("0.50 arcsec, 4 sessions", rig.Subtitle);
        Assert.Equal(["n 0", "n 0", "n 4"], rig.Wedges.Select(wedge => wedge.CountText));
    }

    [AvaloniaFact]
    public void TableView_ListsTheSameRowsInTheQuerysOrder()
    {
        var page = Build(Factory.Guiding(
            rigs: [Factory.Rig()],
            bands:
            [
                Factory.Band(band: GuidingAltitudeBand.Below30, sessions: 2, rmsTotal: 0.8d, rmsRa: 0.5d, rmsDec: 0.6d),
                Factory.Band(band: GuidingAltitudeBand.Above60, sessions: 5, rmsTotal: 0.5d, rmsRa: 0.3d, rmsDec: 0.4d),
            ]));

        Assert.Equal(
            ["Below 30 degrees", "Above 60 degrees"],
            page.TableRows.Select(row => row.Band));
        Assert.Equal(["0.80", "0.50"], page.TableRows.Select(row => row.RmsTotal));
        Assert.Equal(["0.50", "0.30"], page.TableRows.Select(row => row.RmsRa));
        Assert.Equal(["0.60", "0.40"], page.TableRows.Select(row => row.RmsDec));
        Assert.Equal(["2", "5"], page.TableRows.Select(row => row.Sessions));
    }

    [AvaloniaFact]
    public void AThemeChange_KeepsTheTableViewOpen()
    {
        // Review finding P3-1, which is finding M6 in a second place: the page rebuilds the guiding
        // cards on a theme change because their brushes and their wedge ramp are resolved from
        // theme tokens, and the rebuild starts the disclosure closed. The expanded row set of
        // Equipment performance is already carried across that rebuild; this is the same rule for
        // the same reason.
        ChartTheme.Apply();
        using var page = Factory.Create();
        page.Guiding.ToggleTableCommand.Execute(null);
        Assert.True(page.Guiding.IsTableExpanded);

        ChartTheme.Apply();

        Assert.True(page.Guiding.IsTableExpanded);
        Assert.NotEmpty(page.Guiding.Rows);
    }

    [AvaloniaFact]
    public void TableView_IsClosedAtEveryLoad()
    {
        var page = Build(Factory.Guiding());

        Assert.False(page.IsTableExpanded);
        page.ToggleTableCommand.Execute(null);
        Assert.True(page.IsTableExpanded);
    }
}
