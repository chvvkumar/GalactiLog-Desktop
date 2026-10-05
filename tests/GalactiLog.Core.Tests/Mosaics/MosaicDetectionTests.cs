using GalactiLog.Core.Mosaics;
using Xunit;

namespace GalactiLog.Core.Tests.Mosaics;

public class MosaicDetectionTests
{
    // 1000 px at 1 arcsec per pixel: a 16.67 arcmin field, so the derived tolerance is 4.17
    // arcmin and the position reach is 50 arcmin. 0.25 degrees is 15 arcmin, one field apart.
    private static readonly DetectionSettings Settings = new(["Panel", "P"], 0, 0);
    private static readonly HashSet<string> NoMosaics = [];
    private static readonly DateOnly Night1 = new(2026, 3, 1);
    private static readonly DateOnly Night2 = new(2026, 3, 2);

    private const string NotDistinct =
        "Positions not distinct: two panels share a sky centre within tolerance (possible duplicate or mislabel).";
    private const string Unrelated = "Position-grouped targets have unrelated base names.";
    private const string NoPosition = "Some panels have no usable sky coordinates.";
    private const string MixedKeywords = "Panels name their number with different keywords.";
    private const string OnePanel = "Only one panel found.";

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static DetectionFrame F(string obj, double? ra, double? dec, DateOnly? night = null) =>
        new(night ?? Night1, ra, dec, 1.0, 1000, 300, "L", obj);

    private static DetectionTarget T(int n, params DetectionFrame[] frames) => new(Id(n), $"Target {n}", frames);

    private static IReadOnlyList<SuggestionCandidate> Detect(
        IReadOnlyList<DetectionTarget> targets, DetectionSettings? settings = null,
        IReadOnlySet<string>? existing = null, IReadOnlyList<DismissedSignature>? dismissed = null) =>
        MosaicDetection.Detect(targets, settings ?? Settings, existing ?? NoMosaics, dismissed ?? []);

    [Fact]
    public void ByNameOnly_NoPositions_IsLowWithNoPositionFlag()
    {
        var result = Detect([T(1, F("M31 Panel 1", null, null)), T(2, F("M31 Panel 2", null, null))]);

        var s = Assert.Single(result);
        Assert.Equal("M31", s.SuggestedName);
        Assert.Equal("name", s.DiscoverySource);
        Assert.Equal("low", s.Confidence);
        Assert.Equal([NoPosition], s.Flags);
        Assert.Equal(["Panel 1", "Panel 2"], s.PanelLabels);
        Assert.Equal([Id(1), Id(2)], s.TargetIds);
        Assert.Equal(["%M31%Panel%1%", "%M31%Panel%2%"], s.Panels.Select(p => p.Pattern));
        Assert.Equal([new GeometryPanel(Id(1), "Panel 1", null, null), new GeometryPanel(Id(2), "Panel 2", null, null)], s.Geometry.Panels);
        Assert.Empty(s.Geometry.Pitches);
        Assert.Equal(16.67, s.Geometry.FovArcmin);
    }

    [Fact]
    public void ByNameAndPosition_IsHighWithGeometry()
    {
        var result = Detect([T(2, F("M31 Panel 2", 10.25, 0)), T(1, F("M31 Panel 1", 10, 0))]);

        var s = Assert.Single(result);
        Assert.Equal("both", s.DiscoverySource);
        Assert.Equal("high", s.Confidence);
        Assert.Empty(s.Flags);
        Assert.Equal(["Panel 1", "Panel 2"], s.PanelLabels);
        Assert.Equal([15.0], s.Geometry.Pitches);
        Assert.Equal(16.67, s.Geometry.FovArcmin);
        Assert.Equal(new GeometryPanel(Id(1), "Panel 1", 10, 0), s.Geometry.Panels[0]);
    }

    [Fact]
    public void ByPositionOnly_UnrelatedBases_ClusterOneFieldApart()
    {
        var result = Detect([
            T(1, F("Alpha P1", 10, 0)), T(2, F("Beta P2", 10.25, 0)),
            T(3, F("Gamma P3", 10, 0.25)), T(4, F("Delta P4", 10.25, 0.25)),
        ]);

        var s = Assert.Single(result);
        Assert.Equal("position", s.DiscoverySource);
        Assert.Equal("Alpha", s.BaseName);
        Assert.Equal("low", s.Confidence);
        Assert.Equal([Unrelated], s.Flags);
        Assert.Equal([Id(1), Id(2), Id(3), Id(4)], s.TargetIds);
    }

    [Fact]
    public void OneTargetFourTokenObjects_OnA2By2Grid_IsOneFourPanelSuggestion()
    {
        var result = Detect([T(1,
            F("Veil Panel 1", 10, 0), F("Veil Panel 2", 10.25, 0),
            F("Veil Panel 3", 10, 0.25), F("Veil Panel 4", 10.25, 0.25),
            F("Veil Panel 4", 10.25, 0.25))]);

        var s = Assert.Single(result);
        Assert.Equal("both", s.DiscoverySource);
        Assert.Equal("high", s.Confidence);
        Assert.Equal(["Panel 1", "Panel 2", "Panel 3", "Panel 4"], s.PanelLabels);
        Assert.All(s.TargetIds, id => Assert.Equal(Id(1), id));
        Assert.Equal(6, s.Geometry.Pitches.Count);
        Assert.Equal($"Veil|{Id(1)},{Id(1)},{Id(1)},{Id(1)}|Panel 1,Panel 2,Panel 3,Panel 4", s.DedupSignature);
    }

    [Fact]
    public void TwoPanelsShareANight_DatesFollowTheLabel()
    {
        var night3 = new DateOnly(2026, 3, 3);
        var result = Detect([T(1,
            F("Veil Panel 1", 10, 0, Night1), F("Veil Panel 1", 10, 0, Night2),
            F("Veil Panel 2", 10.25, 0, Night2), F("Veil Panel 2", 10.25, 0, night3))]);

        var s = Assert.Single(result);
        Assert.Equal([Night1, Night2], s.Panels[0].Dates);
        Assert.Equal([Night2, night3], s.Panels[1].Dates);
    }

    [Fact]
    public void OnePanelAlone_IsLowWithItsFlag()
    {
        var s = Assert.Single(Detect([T(1, F("IC 1396 P1", 10, 0))]));

        Assert.Equal("IC 1396", s.SuggestedName);
        Assert.Equal("name", s.DiscoverySource);
        Assert.Equal("low", s.Confidence);
        Assert.Equal([OnePanel], s.Flags);
    }

    [Fact]
    public void LeftoverCandidateOfAGroupedBase_IsDropped_WhileALoneBaseStillSurfaces()
    {
        var result = Detect([
            T(1, F("Foo P1", 10, 0)), T(2, F("Foo P2", 10.25, 0)), T(3, F("Foo P1", 10, 0)),
            T(4, F("IC 1396 P1", 50, 10)),
        ]);

        Assert.Equal(["Foo", "IC 1396"], result.Select(s => s.SuggestedName));
        Assert.Equal([Id(1), Id(2)], result[0].TargetIds);
        Assert.Equal("low", result[1].Confidence);
        Assert.Equal([OnePanel], result[1].Flags);
    }

    [Fact]
    public void SecondSpellingOfATokenInOneTarget_YieldsNoSecondCandidate()
    {
        // "M31 panel 1" parses to the same base and number as "M31 Panel 1": one entry, whose
        // centre and nights come from the first OBJECT's frames only.
        var s = Assert.Single(Detect([T(1,
            F("M31 Panel 1", 10, 0, Night1), F("M31 panel 1", 30, 0, Night2), F("M31 Panel 2", 10.25, 0, Night1))]));

        Assert.Equal(["Panel 1", "Panel 2"], s.PanelLabels);
        Assert.Equal([Night1], s.Panels[0].Dates);
        Assert.Equal(10, s.Geometry.Panels[0].Ra);
    }

    [Fact]
    public void NoTokenTarget_YieldsNothing() =>
        Assert.Empty(Detect([T(1, F("North America Nebula", 10, 0)), T(2, F("Pelican Nebula", 10.25, 0))]));

    [Fact]
    public void IdenticalPositions_FlagPositionsNotDistinct()
    {
        var s = Assert.Single(Detect([T(1, F("M31 Panel 1", 10, 0)), T(2, F("M31 Panel 2", 10, 0))]));

        Assert.Equal("name", s.DiscoverySource);
        Assert.Equal("low", s.Confidence);
        Assert.Equal([NotDistinct], s.Flags);
    }

    [Fact]
    public void MixedKeywords_AreFlagged()
    {
        var s = Assert.Single(Detect([T(1, F("NGC 7000 Panel 1", 10, 0)), T(2, F("NGC 7000 P2", 10.25, 0))]));

        Assert.Equal("both", s.DiscoverySource);
        Assert.Equal("low", s.Confidence);
        Assert.Equal([MixedKeywords], s.Flags);
    }

    [Fact]
    public void CampaignSplit_AtTheGap_NamesEachCampaignByItsDates()
    {
        DateOnly mar30 = new(2026, 3, 30), apr2 = new(2026, 4, 2), sep10 = new(2026, 9, 10);
        var result = Detect(
            [
                T(1, F("M31 Panel 1", 10, 0, mar30), F("M31 Panel 1", 10, 0, sep10)),
                T(2, F("M31 Panel 2", 10.25, 0, apr2), F("M31 Panel 2", 10.25, 0, sep10)),
            ],
            Settings with { CampaignGapDays = 30 });

        Assert.Equal(["M31 (Mar 2026 - Apr 2026)", "M31 (Sep 2026)"], result.Select(s => s.SuggestedName));
        Assert.Equal([[mar30], [apr2]], result[0].Panels.Select(p => p.Dates));
        Assert.Equal([[sep10], [sep10]], result[1].Panels.Select(p => p.Dates));
    }

    [Fact]
    public void CampaignWithOnePanelNumber_IsSkipped()
    {
        DateOnly sep10 = new(2026, 9, 10);
        var result = Detect(
            [
                T(1, F("M31 Panel 1", 10, 0, Night1), F("M31 Panel 1", 10, 0, sep10)),
                T(2, F("M31 Panel 2", 10.25, 0, Night1)),
            ],
            Settings with { CampaignGapDays = 30 });

        Assert.Equal(["M31 (Mar 2026)"], result.Select(s => s.SuggestedName));
    }

    [Fact]
    public void UniqueName_AvoidsExistingMosaicAndEarlierSuggestion()
    {
        // The name group "Foo" is emitted first and takes the name. A leftover "foo P1" clusters
        // with "Bar P2" far away; that position group's base is "foo", and it takes the first
        // " (#n)" that neither the run nor an existing mosaic holds.
        var result = Detect(
            [
                T(1, F("Foo P1", 10, 0)), T(2, F("Foo P2", 10.25, 0)),
                T(3, F("foo P1", 100, 40)), T(4, F("Bar P2", 100.25, 40)),
            ],
            existing: new HashSet<string> { "FOO (#2)" });

        Assert.Equal(["Foo", "foo (#3)"], result.Select(s => s.SuggestedName));
        Assert.Equal("position", result[1].DiscoverySource);
    }

    [Fact]
    public void GroupNamedLikeAnExistingMosaic_IsSkipped() =>
        Assert.Empty(Detect([T(1, F("M31 Panel 1", 10, 0)), T(2, F("M31 Panel 2", 10.25, 0))],
            existing: new HashSet<string> { "m31" }));

    [Fact]
    public void CoveredTriples_SkipAnAcceptedCampaign()
    {
        var targets = new[] { T(1, F("M31 Panel 1", 10, 0)), T(2, F("M31 Panel 2", 10.25, 0)) };
        var covered = new HashSet<(Guid, DateOnly, string)> { (Id(1), Night1, "Panel 1"), (Id(2), Night1, "Panel 2") };

        Assert.Empty(MosaicDetection.Detect(targets, Settings, NoMosaics, [], covered));
        covered.Remove((Id(2), Night1, "Panel 2"));
        covered.Add((Id(2), Night1, "Panel 1"));
        Assert.Single(MosaicDetection.Detect(targets, Settings, NoMosaics, [], covered));
    }

    [Fact]
    public void DismissedSignature_SkipsWhileNightsAreASubset_AndResurfacesOnANewNight()
    {
        var targets = new List<DetectionTarget>
        {
            T(1, F("M31 Panel 1", 10, 0, Night1)),
            T(2, F("M31 Panel 2", 10.25, 0, Night2)),
        };
        var signature = Assert.Single(Detect(targets)).DedupSignature;
        var dismissed = new[] { new DismissedSignature(signature, new HashSet<DateOnly> { Night1, Night2, new(2026, 3, 3) }) };

        Assert.Empty(Detect(targets, dismissed: dismissed));

        targets[1] = T(2, F("M31 Panel 2", 10.25, 0, Night2), F("M31 Panel 2", 10.25, 0, new(2026, 3, 20)));
        Assert.Equal(signature, Assert.Single(Detect(targets, dismissed: dismissed)).DedupSignature);
    }

    [Fact]
    public void DedupSignature_IsOrderIndependentAndKeepsDuplicates()
    {
        var a = MosaicDetection.DedupSignature("M31", [Id(2), Id(1), Id(1)], ["Panel 2", "Panel 1", "Panel 1"]);
        var b = MosaicDetection.DedupSignature("M31", [Id(1), Id(1), Id(2)], ["Panel 1", "Panel 2", "Panel 1"]);

        Assert.Equal(a, b);
        Assert.Equal($"M31|{Id(1)},{Id(1)},{Id(2)}|Panel 1,Panel 1,Panel 2", a);
    }

    [Fact]
    public void Detect_SameInputsInAnotherOrder_GiveTheSameResult()
    {
        var targets = new[] { T(1, F("M31 Panel 1", 10, 0)), T(2, F("M31 Panel 2", 10.25, 0)), T(3, F("IC 1396 P1", 50, 10)) };

        var forward = Detect(targets);
        var backward = Detect(targets.Reverse().ToArray());

        Assert.Equal(forward.Select(s => (s.SuggestedName, s.DedupSignature)), backward.Select(s => (s.SuggestedName, s.DedupSignature)));
        Assert.Equal(["IC 1396", "M31"], forward.Select(s => s.SuggestedName));
    }

    [Fact]
    public void RobustCentre_UnwrapsTheSeamAndIgnoresAnOutlier()
    {
        // The reference is the median raw RA, 359.9; 0.1 unwraps to +0.2 rather than pulling
        // the centre towards 180.
        var seam = MosaicDetection.RobustCentre([F("x", 359.9, 0), F("x", 359.95, 0), F("x", 0.1, 0), F("x", null, 5)])!.Value;
        Assert.Equal(359.95, seam.RaDeg, 9);

        var outlier = MosaicDetection.RobustCentre([F("x", 10, 20), F("x", 10.1, 20.1), F("x", 200, -60)])!.Value;
        Assert.Equal(10.0, outlier.RaDeg, 9);
        Assert.Equal(20, outlier.DecDeg, 9);

        Assert.Null(MosaicDetection.RobustCentre([F("x", null, null)]));
    }

    [Fact]
    public void Geometry_Helpers()
    {
        Assert.Equal(1.0, MosaicDetection.AngularSeparationDeg(0, 0, 1, 0), 9);
        Assert.Equal(0.5, MosaicDetection.AngularSeparationDeg(359.75, 0, 0.25, 0), 9);
        Assert.Equal(1000 / 60.0, MosaicDetection.FieldOfViewArcmin(1.0, 1000));
        Assert.Null(MosaicDetection.FieldOfViewArcmin(null, 1000));
        Assert.Null(MosaicDetection.FieldOfViewArcmin(1.0, 0));
    }

    [Fact]
    public void EffectiveTolerance_ConfiguredThenQuarterFieldThenDefault()
    {
        Assert.Equal(7, MosaicDetection.EffectiveToleranceArcmin(7, [100.0]));
        Assert.Equal(25, MosaicDetection.EffectiveToleranceArcmin(0, [null, 200.0, 100.0]));
        Assert.Equal(1, MosaicDetection.EffectiveToleranceArcmin(0, [2.0]));
        Assert.Equal(12, MosaicDetection.EffectiveToleranceArcmin(0, [null]));
    }

    [Fact]
    public void ClusterByGap_SplitsWhereTheGapIsExceeded()
    {
        static DateOnly D(int day) => new(2026, 1, day);
        var clusters = MosaicDetection.ClusterByGap([D(10), D(1), D(3), D(3), D(20)], 7);

        Assert.Equal([[D(1), D(3), D(10)], [D(20)]], clusters);
        Assert.Empty(MosaicDetection.ClusterByGap([], 7));
    }
}
