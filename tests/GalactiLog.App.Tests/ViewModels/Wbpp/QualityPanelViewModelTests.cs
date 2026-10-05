using System.Reflection;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Wbpp;
using Microsoft.Extensions.Logging;
using Xunit;
using static GalactiLog.App.Tests.ViewModels.Wbpp.QualityPanelTestFactory;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// design-spec 12.13's quality filter, the panel half: the tally sentence, the user's own chip
/// order, the cell colours and their baseline, the override's four lifetime rules, the per-rig
/// load and the coalesced write. Task 3b brief sections 8.1 and 8.3 to 8.8.
/// </summary>
public class QualityPanelViewModelTests
{
    // ---- 8.1 the verdict set and the tally sentence -------------------------------------------

    /// <summary>
    /// A mix of passing, failing and unmeasured frames under one enabled chip prints spec 12.13's
    /// literal sentence, and the three parts always sum to the total.
    /// </summary>
    /// <remarks>Fails when the tally counts the host footer's set, the chosen levels' frames,
    /// rather than every LIGHT frame of the checked nights: that prints two figures the user cannot
    /// reconcile and which spec 12.13 explicitly says will differ.</remarks>
    [Fact]
    public void Tally_ReadsSpec1213sSentence_OverEveryLightFrameOfTheCheckedNights()
    {
        var panel = MixedPanel(out _);

        Assert.Equal("2 copy, 1 excluded, 1 unmeasured of 4 frames", panel.TallyText);
        Assert.Equal(4, panel.Totals.Total);
        Assert.Equal(
            panel.Totals.Total,
            panel.Totals.Copy + panel.Totals.Exclude + panel.Totals.Unmeasured);
        Assert.Equal(0, panel.Totals.Overridden);
    }

    /// <summary>
    /// Spec 12.13 appends ", n overridden" when any row carries an override, and the count stays
    /// outside the sum "because an override is a property of a row rather than a fourth verdict".
    /// </summary>
    /// <remarks>Fails when the overridden count is added into the sum, which prints a tally that
    /// does not add up in front of the user.</remarks>
    [Fact]
    public void Tally_AppendsTheOverriddenCount_WithoutTakingItIntoTheSum()
    {
        var panel = MixedPanel(out _);

        Row(panel, "pass-a").IsCopied = false;

        Assert.Equal("1 copy, 2 excluded, 1 unmeasured of 4 frames, 1 overridden", panel.TallyText);
        Assert.Equal(1, panel.Totals.Overridden);
        Assert.Equal(
            panel.Totals.Total,
            panel.Totals.Copy + panel.Totals.Exclude + panel.Totals.Unmeasured);
    }

    /// <summary>
    /// Spec 12.13: a frame whose verdict is Copy and whose override excludes it "is counted as
    /// excluded", and one frame overridden each way gives two overrides while the three parts still
    /// sum to the total.
    /// </summary>
    [Fact]
    public void Tally_CountsAnOverrideEachWay_AsACrossCutRatherThanAFourthBucket()
    {
        var panel = MixedPanel(out _);

        Row(panel, "pass-a").IsCopied = false;
        Row(panel, "fail").IsCopied = true;

        Assert.Equal(2, panel.Totals.Overridden);
        Assert.Equal(
            panel.Totals.Total,
            panel.Totals.Copy + panel.Totals.Exclude + panel.Totals.Unmeasured);
        Assert.Equal("2 copy, 1 excluded, 1 unmeasured of 4 frames, 2 overridden", panel.TallyText);
    }

    /// <summary>
    /// The coordinator's Task 3a review rule: the phrase prints <c>QualityTotals.Overridden</c>,
    /// which counts only an override that differs from the computed answer, and never the override
    /// dictionary's own count.
    /// </summary>
    /// <remarks>Fails when the panel counts the map, which leaves ", 1 overridden" on the line
    /// after the user has ticked a row back to exactly what the filter already said.</remarks>
    [Fact]
    public void Tally_LeavesThePhraseOut_WhenAnOverrideAgreesWithTheComputedVerdict()
    {
        var panel = MixedPanel(out _);
        var row = Row(panel, "pass-a");

        row.IsCopied = false;
        row.IsCopied = true;

        Assert.Equal(0, panel.Totals.Overridden);
        Assert.Equal("2 copy, 1 excluded, 1 unmeasured of 4 frames", panel.TallyText);
    }

    /// <summary>With the master switch off every frame is copy and the sentence says so.</summary>
    [Fact]
    public void Tally_WithTheFilterOff_CountsEveryFrameAsCopy()
    {
        var panel = MixedPanel(out _);

        panel.IsFilterEnabled = false;

        Assert.Equal("4 copy, 0 excluded, 0 unmeasured of 4 frames", panel.TallyText);
    }

    // ---- 8.3 the chip order is the user's -----------------------------------------------------

    /// <summary>
    /// Spec 12.13: "Every enabled constraint the frame violated is recorded, in the user's own chip
    /// order". Two chips added Stars then HFR put the Stars failure first.
    /// </summary>
    /// <remarks>Fails when the panel rebuilds the constraint list in the five metrics' display
    /// order, which points the verdict tooltip at a chip the user did not set first and which no
    /// case over a single chip can show.</remarks>
    [Fact]
    public void Failures_AreInTheUsersOwnChipOrder_NotTheMetricsDisplayOrder()
    {
        var frame = Frame("both.fits", hfr: 5d, stars: 100);
        var panel = Build([Entry(frame, NeutralGrading())], out _, enabled: true);

        SetChip(panel, WbppMetric.Stars, "600");
        SetChip(panel, WbppMetric.Hfr, "1");

        var row = panel.Rows.Single();
        Assert.Equal("Exclude: stars 100 < 600, HFR 5.00 > 1.00", row.VerdictTooltip);
    }

    // ---- 8.4 the cell colours read the grades and select on the baseline -----------------------

    /// <summary>
    /// The three sharpness and roundness cells select between the grading pair the session query
    /// already computed; detected stars reads one grade under both baselines; guiding RMS is never
    /// coloured.
    /// </summary>
    /// <remarks>Fails when the panel computes its own deviation from the frames it was handed,
    /// which grades against a set that is neither the night nor the rig and which silently
    /// disagrees with the frame table on the same frame (W10).</remarks>
    [Fact]
    public void Cells_ReadTheStoredGrades_AndTheBaselineSelectsBetweenThePairs()
    {
        var frame = Frame("graded.fits", hfr: 2d, eccentricity: 0.4d, fwhm: 1.8d, stars: 900, rms: 0.4d);
        var panel = Build([Entry(frame, SplitGrading(sessionZ: -2d, rigZ: 2d, starsZ: 2d))], out _);

        var row = panel.Rows.Single();
        Assert.True(row.HfrCell.IsBetter);
        Assert.True(row.EccentricityCell.IsBetter);
        Assert.True(row.FwhmCell.IsBetter);
        Assert.True(row.DetectedStarsCell.IsWatch);
        Assert.False(row.GuidingRmsCell.IsReject);

        panel.IsSessionBaseline = false;

        Assert.True(row.HfrCell.IsWatch);
        Assert.True(row.EccentricityCell.IsWatch);
        Assert.True(row.FwhmCell.IsWatch);

        // Spec 12.13: detected stars "is always graded against the frame's own night whichever
        // baseline is selected", and guiding RMS "has no baseline and is never coloured", although
        // its stored grade in this fixture is in the reject band.
        Assert.True(row.DetectedStarsCell.IsWatch);
        Assert.False(row.GuidingRmsCell.IsReject);
    }

    /// <summary>A frame the query graded nothing for is uncoloured in every cell and throws
    /// nothing.</summary>
    [Fact]
    public void Cells_OfAnUngradedFrame_AreUncolouredUnderBothBaselines()
    {
        var panel = Build([Entry(Frame("ungraded.fits", hfr: 2d), grading: null)], out _);

        var row = panel.Rows.Single();
        panel.IsSessionBaseline = false;

        Assert.All(
            new[] { row.HfrCell, row.EccentricityCell, row.FwhmCell, row.DetectedStarsCell, row.GuidingRmsCell },
            cell =>
            {
                Assert.False(cell.IsBetter);
                Assert.False(cell.IsWatch);
                Assert.False(cell.IsReject);
            });
    }

    /// <summary>Spec 12.13: "A metric cell that failed a gate is drawn in the error ink" with a
    /// mark, whatever band it is in.</summary>
    [Fact]
    public void AFailedCell_CarriesTheFailureAndItsMark_WhateverTheBandSays()
    {
        var frame = Frame("failed.fits", hfr: 5d);
        var panel = Build([Entry(frame, SplitGrading(sessionZ: -2d, rigZ: -2d))], out _, enabled: true);

        SetChip(panel, WbppMetric.Hfr, "1");

        var row = panel.Rows.Single();
        Assert.True(row.HfrCell.IsFailed);
        Assert.Equal(VerdictCellViewModel.FailureMark, row.HfrCell.Mark);
        Assert.Equal("HFR 5.00 > 1.00", row.HfrCell.Tooltip);

        // Spec 12.13: a failure mark is shown only while the filter is enabled.
        panel.IsFilterEnabled = false;
        Assert.False(row.HfrCell.IsFailed);
        Assert.Equal("", row.HfrCell.Mark);
    }

    // ---- 8.5 the override's four lifetime rules -----------------------------------------------

    /// <summary>An override "survives a re-sort", because it is keyed by image id rather than by a
    /// row position.</summary>
    [Fact]
    public void AnOverride_SurvivesAReSort()
    {
        var panel = MixedPanel(out _);
        var row = Row(panel, "pass-a");

        row.IsCopied = false;
        panel.SortByCommand.Execute("file");
        panel.SortByCommand.Execute("file");

        Assert.False(Row(panel, "pass-a").IsCopied);
        Assert.Equal(1, panel.Totals.Overridden);
    }

    /// <summary>
    /// Spec 12.13: an override "is cleared when the constraint set changes, because a verdict the
    /// user reversed against one filter says nothing about another". The constraint set changing
    /// means a chip added, disabled, re-enabled, its comparator moved or its value moved.
    /// </summary>
    /// <remarks>Fails when the override survives a constraint change, so a row the user rescued
    /// against an eccentricity gate stays rescued against a star-count gate it was never judged
    /// under.</remarks>
    [Theory]
    [InlineData("added")]
    [InlineData("disabled")]
    [InlineData("reenabled")]
    [InlineData("comparator")]
    [InlineData("value")]
    [InlineData("cleared")]
    public void AnOverride_IsCleared_ByEveryKindOfConstraintChange(string change)
    {
        var panel = MixedPanel(out _);
        var hfr = panel.Chips.Single(chip => chip.Metric == WbppMetric.Hfr);
        Row(panel, "pass-a").IsCopied = false;
        Assert.Equal(1, panel.Totals.Overridden);

        switch (change)
        {
            case "added":
                SetChip(panel, WbppMetric.Stars, "600");
                break;
            case "disabled":
                hfr.DisableCommand.Execute(null);
                break;
            case "reenabled":
                hfr.DisableCommand.Execute(null);
                Row(panel, "pass-a").IsCopied = false;
                hfr.EnableCommand.Execute(null);
                break;
            case "comparator":
                hfr.Comparator = ConstraintChipViewModel.Comparators.Single(
                    option => option.Op == ConstraintOp.AtLeast);
                break;
            case "value":
                hfr.ThresholdText = "3";
                break;
            default:
                hfr.ThresholdText = "";
                break;
        }

        Assert.Equal(0, panel.Totals.Overridden);
        Assert.DoesNotContain("overridden", panel.TallyText, StringComparison.Ordinal);
    }

    /// <summary>Spec 12.13: "Turning the master Enable filters switch off and on again preserves
    /// overrides, which are not a constraint."</summary>
    [Fact]
    public void AnOverride_SurvivesTheMasterSwitchGoingOffAndOnAgain()
    {
        var panel = MixedPanel(out _);
        Row(panel, "pass-a").IsCopied = false;

        panel.IsFilterEnabled = false;
        panel.IsFilterEnabled = true;

        Assert.False(Row(panel, "pass-a").IsCopied);
        Assert.Equal(1, panel.Totals.Overridden);
    }

    /// <summary>
    /// Spec 12.13: "changing a night's chosen level preserves overrides, because an override is a
    /// property of a frame rather than of a folder". The panel never learns about a level at all,
    /// which is how that rule holds here by construction.
    /// </summary>
    /// <remarks>Fails the moment a member carrying a level reaches this type, which is the one way
    /// a level change could touch an override.</remarks>
    [Fact]
    public void ThePanel_ExposesNoMember_ByWhichALevelChangeCouldReachAnOverride()
    {
        var members = typeof(QualityPanelViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)
            .Where(name => name.Contains("Level", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Folder", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(members);
    }

    // ---- 8.6 the baseline moves no frame ------------------------------------------------------

    /// <summary>
    /// Spec 12.13: the baseline toggle "colours cells and nothing else: the constraints alone
    /// decide a verdict, and the toggle never moves a frame between Copy and Exclude."
    /// </summary>
    /// <remarks>Fails when the baseline is folded into the verdict path, which turns a colouring
    /// control into one that silently changes what a user is about to copy.</remarks>
    [Fact]
    public void TheBaseline_RaisesNoExcludedChange_AndLeavesTheExcludedSetByteForByteEqual()
    {
        var raised = new List<IReadOnlyCollection<Guid>>();
        var panel = Build(
            [
                Entry(Frame("pass.fits", hfr: 1d), SplitGrading(-2d, 2d)),
                Entry(Frame("fail.fits", hfr: 5d), SplitGrading(2d, -2d)),
            ],
            out _,
            enabled: true,
            excludedChanged: raised.Add);

        SetChip(panel, WbppMetric.Hfr, "2");
        var before = panel.ExcludedImageIds.ToList();
        var raisedBefore = raised.Count;
        var bandBefore = panel.Rows.First(row => row.FileName == "pass.fits").HfrCell.IsBetter;

        panel.IsSessionBaseline = false;

        Assert.Equal(raisedBefore, raised.Count);
        Assert.Equal(before, panel.ExcludedImageIds);
        Assert.True(bandBefore);
        Assert.True(panel.Rows.First(row => row.FileName == "pass.fits").HfrCell.IsWatch);
    }

    /// <summary>
    /// Brief section 4.3's positive arm: a chip edit moves the excluded set, so the host is told,
    /// and is told the set the copy will leave out.
    /// </summary>
    /// <remarks>Fails when <c>RaiseExcluded</c> is missing from the chip edit path, which leaves
    /// the host's footer and its copy set describing the filter the user has just replaced.</remarks>
    [Fact]
    public void ExcludedChanged_IsRaised_WhenAChipEditMovesTheSet()
    {
        var panel = WatchedPair(out var raised, out _, out var fail);
        var before = raised.Count;

        SetChip(panel, WbppMetric.Hfr, "2");

        Assert.True(raised.Count > before);
        Assert.Equal([fail.ImageId], raised[^1]);
    }

    /// <summary>Brief section 4.3's positive arm for a row override, which works in both
    /// directions and therefore moves the set both ways.</summary>
    /// <remarks>Fails when <c>RaiseExcluded</c> is missing from the row toggle path, so a row the
    /// user ticked off is still in the host's copy set.</remarks>
    [Fact]
    public void ExcludedChanged_IsRaised_WhenARowOverrideMovesTheSet()
    {
        var panel = WatchedPair(out var raised, out var pass, out var fail);
        SetChip(panel, WbppMetric.Hfr, "2");
        var before = raised.Count;

        Row(panel, "pass").IsCopied = false;

        Assert.Equal(before + 1, raised.Count);
        Assert.Equal([pass.ImageId, fail.ImageId], raised[^1]);

        Row(panel, "fail").IsCopied = true;

        Assert.Equal(before + 2, raised.Count);
        Assert.Equal([pass.ImageId], raised[^1]);
    }

    /// <summary>Brief section 4.3's positive arm for the master switch: everything is copied while
    /// it is off, which is a set the host has to be told about.</summary>
    /// <remarks>Fails when <c>RaiseExcluded</c> is missing from the master switch path, so the
    /// host keeps excluding frames the user has just un-filtered.</remarks>
    [Fact]
    public void ExcludedChanged_IsRaised_WhenTheMasterSwitchMovesTheSet()
    {
        var panel = WatchedPair(out var raised, out _, out var fail);
        SetChip(panel, WbppMetric.Hfr, "2");
        Assert.Equal([fail.ImageId], raised[^1]);
        var before = raised.Count;

        panel.IsFilterEnabled = false;

        Assert.Equal(before + 1, raised.Count);
        Assert.Empty(raised[^1]);

        panel.IsFilterEnabled = true;

        Assert.Equal(before + 2, raised.Count);
        Assert.Equal([fail.ImageId], raised[^1]);
    }

    /// <summary>The host's footer is correct before the user touches anything: one raise on
    /// construction, after the first verdict pass.</summary>
    [Fact]
    public void ExcludedChanged_IsRaisedOnce_OnConstruction()
    {
        var raised = new List<IReadOnlyCollection<Guid>>();
        var stored = new WbppQualityState(
            true, QualityBaseline.Session, [new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 2d, true)]);

        var frames = new[]
        {
            Entry(Frame("pass.fits", hfr: 1d), NeutralGrading()),
            Entry(Frame("fail.fits", hfr: 5d), NeutralGrading()),
        };

        var spy = new SettingsSpy(General((Rig, stored)));
        using var panel = new QualityPanelViewModel(
            frames, Rig, () => spy.Current, spy.Mutate, excludedChanged: raised.Add);

        var single = Assert.Single(raised);
        Assert.Equal(frames[1].Frame.ImageId, Assert.Single(single));
        Assert.Equal(0, spy.Calls);
    }

    // ---- 8.7 the per-rig load -----------------------------------------------------------------

    /// <summary>A document holding two rigs loads this rig's entry and leaves the other rig's
    /// untouched after a write.</summary>
    /// <remarks>Fails when the panel writes the whole map from its own state, which costs the other
    /// rig everything the user tuned on it in a previous visit.</remarks>
    [Fact]
    public async Task TheLoad_TakesThisRigsEntry_AndAWriteLeavesEveryOtherRigsUntouched()
    {
        const string other = "RedCat 51 / ASI533MC";
        var mine = new WbppQualityState(
            true, QualityBaseline.Rig, [new RawConstraint(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55d, true)]);
        var theirs = new WbppQualityState(
            true, QualityBaseline.Session, [new RawConstraint(WbppMetric.Stars, ConstraintOp.AtLeast, 800d, true)]);

        var seam = new DelaySeam();
        var spy = new SettingsSpy(General((Rig, mine), (other, theirs)));
        using var panel = new QualityPanelViewModel(
            [Entry(Frame("a.fits", eccentricity: 0.4d), NeutralGrading())],
            Rig,
            () => spy.Current,
            spy.Mutate,
            delay: seam.Seam);

        Assert.True(panel.IsFilterEnabled);
        Assert.False(panel.IsSessionBaseline);
        var ecc = panel.Chips.Single(chip => chip.Metric == WbppMetric.Ecc);
        Assert.True(ecc.IsActive);
        Assert.Equal(0.55d, ecc.Value);

        ecc.ThresholdText = "0.65";
        seam.Elapse();
        await panel.PendingSave;

        // A record's list member compares by reference, so the round trip is checked member by
        // member rather than with one Assert.Equal on the record.
        var untouched = spy.StateFor(other);
        Assert.Equal(theirs.Enabled, untouched.Enabled);
        Assert.Equal(theirs.Baseline, untouched.Baseline);
        Assert.Equal(theirs.Constraints.Single(), untouched.Constraints.Single());
        Assert.Equal(0.65d, spy.StateFor(Rig).Constraints.Single().Value);
    }

    /// <summary>A rig with no entry loads <c>WbppQualityState.Default</c>: off, session, no
    /// constraints.</summary>
    [Fact]
    public void TheLoad_OfARigWithNoEntry_IsTheDefaultState()
    {
        var spy = new SettingsSpy(General(("someone else / a camera", new WbppQualityState(
            true, QualityBaseline.Rig, [new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 1d, true)]))));

        using var panel = new QualityPanelViewModel(
            [Entry(Frame("a.fits", hfr: 5d), NeutralGrading())], Rig, () => spy.Current, spy.Mutate);

        Assert.False(panel.IsFilterEnabled);
        Assert.True(panel.IsSessionBaseline);
        Assert.All(panel.Chips, chip => Assert.False(chip.IsActive));
    }

    /// <summary>The literal <c>default</c> slot, which spec 12.13 says is "reached only by a
    /// selection that holds no frame at all".</summary>
    [Fact]
    public void TheLoad_OfTheDefaultRigKey_ReadsTheDefaultSlot()
    {
        var stored = new WbppQualityState(true, QualityBaseline.Rig, []);
        var spy = new SettingsSpy(General((WbppQualityByRig.DefaultRigKey, stored)));

        using var panel = new QualityPanelViewModel(
            [], WbppQualityByRig.DefaultRigKey, () => spy.Current, spy.Mutate);

        Assert.True(panel.IsFilterEnabled);
        Assert.False(panel.IsSessionBaseline);
        Assert.False(panel.HasFrames);
        Assert.Equal("0 copy, 0 excluded, 0 unmeasured of 0 frames", panel.TallyText);
    }

    /// <summary>
    /// Seam-review ruling 1: <c>Unknown / Unknown</c> is a legitimate rig key with its own entry
    /// and is never folded into <c>default</c>.
    /// </summary>
    /// <remarks>Fails when an "is this rig unknown" test folds the all-unknown label into the
    /// default slot, which is the second rig-label spelling ruling 1 forbids and which mixes two
    /// libraries' filters into one slot.</remarks>
    [Fact]
    public async Task TheLoad_OfUnknownOverUnknown_ReadsAndWritesThatKeyAndNotTheDefaultSlot()
    {
        const string unknown = "Unknown / Unknown";
        var stored = new WbppQualityState(
            true, QualityBaseline.Session, [new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 3d, true)]);
        var defaults = new WbppQualityState(false, QualityBaseline.Rig, []);

        var seam = new DelaySeam();
        var spy = new SettingsSpy(General((unknown, stored), (WbppQualityByRig.DefaultRigKey, defaults)));
        using var panel = new QualityPanelViewModel(
            [Entry(Frame("a.fits", hfr: 1d, rig: unknown), NeutralGrading())],
            unknown,
            () => spy.Current,
            spy.Mutate,
            delay: seam.Seam);

        Assert.True(panel.IsFilterEnabled);
        Assert.Equal(3d, panel.Chips.Single(chip => chip.Metric == WbppMetric.Hfr).Value);

        panel.IsSessionBaseline = false;
        seam.Elapse();
        await panel.PendingSave;

        Assert.Equal(QualityBaseline.Rig, spy.StateFor(unknown).Baseline);
        var untouched = spy.StateFor(WbppQualityByRig.DefaultRigKey);
        Assert.Equal(defaults.Enabled, untouched.Enabled);
        Assert.Equal(defaults.Baseline, untouched.Baseline);
        Assert.Empty(untouched.Constraints);
    }

    /// <summary>A malformed entry for this rig loads the default state and does not throw.</summary>
    [Fact]
    public void TheLoad_OfAMalformedEntry_IsTheDefaultStateAndThrowsNothing()
    {
        var spy = new SettingsSpy(GeneralWithMalformedEntry(Rig));

        using var panel = new QualityPanelViewModel(
            [Entry(Frame("a.fits", hfr: 5d), NeutralGrading())], Rig, () => spy.Current, spy.Mutate);

        Assert.False(panel.IsFilterEnabled);
        Assert.True(panel.IsSessionBaseline);
        Assert.All(panel.Chips, chip => Assert.False(chip.IsActive));
    }

    /// <summary>
    /// A hand-edited document carrying two entries for one metric loads the first and drops the
    /// rest, because a chip is one per metric.
    /// </summary>
    /// <remarks>Fails when the later duplicate is kept: it gates every frame while the chip shows
    /// the first entry's threshold, and a later edit replaces only the first, so the second is a
    /// gate the user can neither see nor remove.</remarks>
    [Fact]
    public async Task TheLoad_OfADuplicateEntryForOneMetric_KeepsTheFirstAndDropsTheRest()
    {
        var stored = new WbppQualityState(
            true,
            QualityBaseline.Session,
            [
                new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 4d, true),
                new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 1d, true),
            ]);

        var seam = new DelaySeam();
        var spy = new SettingsSpy(General((Rig, stored)));
        using var panel = new QualityPanelViewModel(
            [Entry(Frame("a.fits", hfr: 2d), NeutralGrading())],
            Rig,
            () => spy.Current,
            spy.Mutate,
            delay: seam.Seam);

        // The chip shows 4, and the frame at 2 is judged against 4 alone rather than also against
        // the invisible gate at 1.
        Assert.Equal(4d, panel.Chips.Single(chip => chip.Metric == WbppMetric.Hfr).Value);
        Assert.Equal("1 copy, 0 excluded, 0 unmeasured of 1 frames", panel.TallyText);
        Assert.Empty(panel.ExcludedImageIds);

        // And the next write stores one entry, so the duplicate does not outlive the visit.
        panel.IsSessionBaseline = false;
        seam.Elapse();
        await panel.PendingSave;

        Assert.Equal(4d, spy.StateFor(Rig).Constraints.Single().Value);
    }

    // ---- 8.8 the coalesced write --------------------------------------------------------------

    /// <summary>
    /// Spec 12.13: "the writes go through one queued <c>SettingsStore.MutateGeneral</c> per change,
    /// coalesced, so a threshold typed digit by digit is one save and not four."
    /// </summary>
    /// <remarks>Fails as one write per keystroke, which is what the spec sentence exists to prevent
    /// and which a case asserting only the final stored value cannot see.</remarks>
    [Fact]
    public async Task FourEditsInsideOneWindow_ProduceExactlyOneWrite_CarryingTheLastValue()
    {
        var seam = new DelaySeam();
        var panel = Build([Entry(Frame("a.fits", hfr: 1d), NeutralGrading())], out var spy, delay: seam.Seam);
        var hfr = panel.Chips.Single(chip => chip.Metric == WbppMetric.Hfr);
        hfr.EnableCommand.Execute(null);

        hfr.ThresholdText = "1";
        hfr.ThresholdText = "1.2";
        hfr.ThresholdText = "1.25";
        hfr.ThresholdText = "1.253";

        seam.Elapse();
        await panel.PendingSave;

        Assert.Equal(1, spy.Calls);
        Assert.Equal(1.253d, spy.StateFor(Rig).Constraints.Single().Value);
        panel.Dispose();
    }

    /// <summary>Edits in two separate windows produce two writes.</summary>
    [Fact]
    public async Task EditsInTwoWindows_ProduceTwoWrites()
    {
        var seam = new DelaySeam();
        var panel = Build([Entry(Frame("a.fits", hfr: 1d), NeutralGrading())], out var spy, delay: seam.Seam);
        var hfr = panel.Chips.Single(chip => chip.Metric == WbppMetric.Hfr);

        hfr.EnableCommand.Execute(null);
        hfr.ThresholdText = "2";
        seam.Elapse();
        await panel.PendingSave;

        hfr.ThresholdText = "3";
        seam.Elapse();
        await panel.PendingSave;

        Assert.Equal(2, spy.Calls);
        Assert.Equal(3d, spy.StateFor(Rig).Constraints.Single().Value);
        panel.Dispose();
    }

    /// <summary>A throwing write is logged at warning and dropped; the panel keeps what is on
    /// screen.</summary>
    [Fact]
    public async Task AThrowingWrite_IsLoggedAndDropped_AndThePanelKeepsWhatIsOnScreen()
    {
        var seam = new DelaySeam();
        var logger = new RecordingLogger();
        var spy = new SettingsSpy { Throws = true };
        var panel = new QualityPanelViewModel(
            [Entry(Frame("a.fits", hfr: 1d), NeutralGrading())],
            Rig,
            () => spy.Current,
            spy.Mutate,
            delay: seam.Seam,
            logger: logger);

        var hfr = panel.Chips.Single(chip => chip.Metric == WbppMetric.Hfr);
        hfr.EnableCommand.Execute(null);
        hfr.ThresholdText = "2";
        seam.Elapse();
        await panel.PendingSave;

        Assert.Equal(2d, hfr.Value);
        Assert.True(hfr.IsActive);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
        panel.Dispose();
    }

    /// <summary>Dispose inside an open window still writes the last state once, which is what
    /// <c>AutosaveField.FlushAsync</c> does for the notes field.</summary>
    [Fact]
    public void Dispose_InsideAnOpenWindow_StillWritesTheLastStateOnce()
    {
        var seam = new DelaySeam();
        var panel = Build([Entry(Frame("a.fits", hfr: 1d), NeutralGrading())], out var spy, delay: seam.Seam);
        var hfr = panel.Chips.Single(chip => chip.Metric == WbppMetric.Hfr);

        hfr.EnableCommand.Execute(null);
        hfr.ThresholdText = "2";
        Assert.Equal(0, spy.Calls);

        panel.Dispose();

        Assert.Equal(1, spy.Calls);
        Assert.Equal(2d, spy.StateFor(Rig).Constraints.Single().Value);

        // Idempotent, and the parked window cannot fire behind the flush.
        panel.Dispose();
        seam.Elapse();
        Assert.Equal(1, spy.Calls);
    }

    /// <summary>The save window is spec 12.4's own one second idle debounce, referenced rather than
    /// restated, so a threshold field and a notes field cannot drift to different windows.</summary>
    [Fact]
    public void TheSaveWindow_IsTheApplicationsOneIdleWindow()
        => Assert.Equal(AutosaveField.IdleWindow, QualityPanelViewModel.SaveWindow);

    /// <summary>Constructing the panel writes nothing: the three values it adopts are the three it
    /// has just read.</summary>
    [Fact]
    public void Construction_QueuesNoWrite()
    {
        var stored = new WbppQualityState(
            true, QualityBaseline.Rig, [new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 2d, true)]);
        var seam = new DelaySeam();
        var spy = new SettingsSpy(General((Rig, stored)));

        using var panel = new QualityPanelViewModel(
            [Entry(Frame("a.fits", hfr: 1d), NeutralGrading())],
            Rig,
            () => spy.Current,
            spy.Mutate,
            delay: seam.Seam);

        seam.Elapse();
        Assert.Equal(0, spy.Calls);
    }

    // ---- the sort (spec 12.13's six comparator rules) ------------------------------------------

    /// <summary>The table is chronological by capture time until a column is clicked, nulls
    /// last.</summary>
    [Fact]
    public void TheTable_IsChronologicalUntilAColumnIsClicked_WithNullsLast()
    {
        var panel = Build(
            [
                Entry(Frame("c.fits", captureDate: new DateTime(2025, 3, 20, 23, 0, 0, DateTimeKind.Utc))),
                Entry(Frame("a.fits", captureDate: new DateTime(2025, 3, 20, 21, 0, 0, DateTimeKind.Utc))),
                Entry(Frame("b.fits", withoutCaptureDate: true)),
            ],
            out _);

        Assert.Equal(["a.fits", "c.fits", "b.fits"], panel.Rows.Select(row => row.FileName));
    }

    /// <summary>Clicking the current sort column reverses it; clicking another sets it
    /// ascending.</summary>
    [Fact]
    public void ClickingAColumn_SortsAscendingThenReverses()
    {
        var panel = Build(
            [Entry(Frame("c.fits")), Entry(Frame("a.fits")), Entry(Frame("b.fits"))], out _);

        panel.SortByCommand.Execute("file");
        Assert.Equal(["a.fits", "b.fits", "c.fits"], panel.Rows.Select(row => row.FileName));

        panel.SortByCommand.Execute("file");
        Assert.Equal(["c.fits", "b.fits", "a.fits"], panel.Rows.Select(row => row.FileName));

        panel.SortByCommand.Execute("night");
        Assert.False(panel.Descending);
    }

    /// <summary>Copy is not sortable: "inclusion is a per-row decision, not a metric."</summary>
    [Fact]
    public void TheCopyColumn_IsNotSortable()
    {
        var panel = Build([Entry(Frame("c.fits")), Entry(Frame("a.fits"))], out _);

        panel.SortByCommand.Execute("file");
        panel.SortByCommand.Execute("copy");

        Assert.Equal("file", panel.SortKey);
        Assert.Equal(["a.fits", "c.fits"], panel.Rows.Select(row => row.FileName));
    }

    /// <summary>A metric column sorts on the value, and a missing value sinks to the bottom in
    /// either direction: "a frame with no measurement is not the smallest one."</summary>
    [Fact]
    public void AMetricColumn_SinksAMissingValueInBothDirections()
    {
        var panel = Build(
            [
                Entry(Frame("high.fits", hfr: 5d, captureDate: new DateTime(2025, 3, 20, 21, 0, 0, DateTimeKind.Utc))),
                Entry(Frame("none.fits", captureDate: new DateTime(2025, 3, 20, 22, 0, 0, DateTimeKind.Utc))),
                Entry(Frame("low.fits", hfr: 1d, captureDate: new DateTime(2025, 3, 20, 23, 0, 0, DateTimeKind.Utc))),
            ],
            out _);

        panel.SortByCommand.Execute("hfr");
        Assert.Equal(["low.fits", "high.fits", "none.fits"], panel.Rows.Select(row => row.FileName));

        panel.SortByCommand.Execute("hfr");
        Assert.Equal(["high.fits", "low.fits", "none.fits"], panel.Rows.Select(row => row.FileName));
    }

    /// <summary>Verdict sorts by group, not by value: the excluded rows first ascending, flipped
    /// when descending, and chronological inside each group.</summary>
    [Fact]
    public void TheVerdictColumn_SortsByGroupAndStaysChronologicalInsideOne()
    {
        var panel = MixedPanel(out _);

        panel.SortByCommand.Execute("verdict");
        var ascending = panel.Rows.Select(row => row.FileName).ToList();
        Assert.Equal(["fail.fits", "unmeasured.fits", "pass-a.fits", "pass-b.fits"], ascending);

        panel.SortByCommand.Execute("verdict");
        Assert.Equal(["pass-a.fits", "pass-b.fits", "fail.fits", "unmeasured.fits"], panel.Rows.Select(row => row.FileName));
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Two passing frames, one failing and one unmeasured under one enabled HFR chip.</summary>
    private static QualityPanelViewModel MixedPanel(out SettingsSpy spy)
    {
        var start = new DateTime(2025, 3, 20, 21, 0, 0, DateTimeKind.Utc);
        var stored = new WbppQualityState(
            true, QualityBaseline.Session, [new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 2d, true)]);

        var panelSpy = new SettingsSpy(General((Rig, stored)));
        spy = panelSpy;

        return new QualityPanelViewModel(
            [
                Entry(Frame("pass-a.fits", hfr: 1.0d, captureDate: start), NeutralGrading()),
                Entry(Frame("pass-b.fits", hfr: 1.5d, captureDate: start.AddMinutes(5)), NeutralGrading()),
                Entry(Frame("fail.fits", hfr: 3.0d, captureDate: start.AddMinutes(10)), NeutralGrading()),
                Entry(Frame("unmeasured.fits", captureDate: start.AddMinutes(15)), NeutralGrading()),
            ],
            Rig,
            () => panelSpy.Current,
            panelSpy.Mutate);
    }

    /// <summary>Two frames, one either side of a gate at 2, under a subscriber that records every
    /// raise of <c>excludedChanged</c>.</summary>
    private static QualityPanelViewModel WatchedPair(
        out List<IReadOnlyCollection<Guid>> raised,
        out WbppFrame pass,
        out WbppFrame fail)
    {
        var passFrame = Frame("pass.fits", hfr: 1d);
        var failFrame = Frame("fail.fits", hfr: 5d);
        var recorded = new List<IReadOnlyCollection<Guid>>();

        pass = passFrame;
        fail = failFrame;
        raised = recorded;

        return Build(
            [Entry(passFrame, NeutralGrading()), Entry(failFrame, NeutralGrading())],
            out _,
            enabled: true,
            excludedChanged: recorded.Add);
    }

    internal static QualityPanelViewModel Build(
        IReadOnlyList<QualityPanelFrame> frames,
        out SettingsSpy spy,
        bool enabled = false,
        Action<IReadOnlyCollection<Guid>>? excludedChanged = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var stored = new WbppQualityState(enabled, QualityBaseline.Session, []);
        var panelSpy = new SettingsSpy(General((Rig, stored)));
        spy = panelSpy;

        return new QualityPanelViewModel(
            frames,
            Rig,
            () => panelSpy.Current,
            panelSpy.Mutate,
            excludedChanged: excludedChanged,
            delay: delay);
    }

    private static VerdictRowViewModel Row(QualityPanelViewModel panel, string stem)
        => panel.Rows.Single(row => row.FileName == stem + ".fits");

    private static void SetChip(QualityPanelViewModel panel, WbppMetric metric, string threshold)
    {
        var chip = panel.Chips.Single(entry => entry.Metric == metric);
        chip.EnableCommand.Execute(null);
        chip.ThresholdText = threshold;
    }
}
