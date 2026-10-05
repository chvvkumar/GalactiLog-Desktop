using System.Globalization;
using System.Text.RegularExpressions;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

/// <summary>
/// The WBPP export's verdict path, task3a.md sections 7.1 to 7.9. The case list is the web's
/// frontend/src/lib/wbppQualityFilter.test.ts, and every failure sentence asserted here is a string
/// equality against the literal that file expects.
/// </summary>
public class QualityFilterTests
{
    private static readonly DateOnly Night = new(2026, 3, 15);

    private static readonly WbppMetric[] AllMetrics =
    [
        WbppMetric.Hfr,
        WbppMetric.Ecc,
        WbppMetric.Fwhm,
        WbppMetric.Stars,
        WbppMetric.Rms,
    ];

    // Minimal frame factory mirroring the web's: every metric null unless the caller sets it, so a
    // case reads as "this frame carries exactly these figures".
    private static WbppFrame Frame(
        double? hfr = null,
        double? ecc = null,
        double? fwhm = null,
        int? stars = null,
        double? rms = null,
        Guid? imageId = null,
        string fileName = "light_001.fits",
        string filterUsed = "Ha")
        => new(
            imageId ?? Guid.NewGuid(),
            Night,
            @"C:\Astro\2026-03-15\Target\" + filterUsed + @"\" + fileName,
            fileName,
            null,
            filterUsed,
            "T / C",
            "T",
            "C",
            hfr,
            ecc,
            fwhm,
            stars,
            rms);

    // Constraint factory, enabled by default so a case reads as "an active gate".
    private static RawConstraint Con(WbppMetric metric, ConstraintOp op, double? value, bool enabled = true)
        => new(metric, op, value, enabled);

    private static WbppFrame FrameCarrying(WbppMetric metric, double value) => metric switch
    {
        WbppMetric.Hfr => Frame(hfr: value),
        WbppMetric.Ecc => Frame(ecc: value),
        WbppMetric.Fwhm => Frame(fwhm: value),
        WbppMetric.Stars => Frame(stars: (int)value),
        _ => Frame(rms: value),
    };

    // A threshold each metric can sit above, on and below with values the metric's own type allows
    // (detected stars is a count, so its ladder is whole numbers).
    private static double LimitFor(WbppMetric metric) => metric == WbppMetric.Stars ? 100d : 2d;

    private static Verdict VerdictOf(WbppFrame frame, params RawConstraint[] constraints)
        => QualityFilter.Evaluate(frame, constraints).Verdict;

    // ---- 7.1 The verdict truth table per metric and comparator ----

    [Fact]
    public void Evaluate_AtMost_PassesOnAndBelowTheThresholdAndFailsAbove()
    {
        foreach (var metric in AllMetrics)
        {
            var limit = LimitFor(metric);
            var gate = Con(metric, ConstraintOp.AtMost, limit);

            Assert.Equal(Verdict.Copy, VerdictOf(FrameCarrying(metric, limit - 1), gate));
            Assert.Equal(Verdict.Copy, VerdictOf(FrameCarrying(metric, limit), gate));
            Assert.Equal(Verdict.Exclude, VerdictOf(FrameCarrying(metric, limit + 1), gate));
        }
    }

    [Fact]
    public void Evaluate_AtLeast_PassesOnAndAboveTheThresholdAndFailsBelow()
    {
        foreach (var metric in AllMetrics)
        {
            var limit = LimitFor(metric);
            var gate = Con(metric, ConstraintOp.AtLeast, limit);

            Assert.Equal(Verdict.Exclude, VerdictOf(FrameCarrying(metric, limit - 1), gate));
            Assert.Equal(Verdict.Copy, VerdictOf(FrameCarrying(metric, limit), gate));
            Assert.Equal(Verdict.Copy, VerdictOf(FrameCarrying(metric, limit + 1), gate));
        }
    }

    // A value sitting exactly on a round threshold the user typed must pass. A strict comparison
    // would drop it, and no looser case would show that.
    [Fact]
    public void Passes_TreatsTheThresholdItselfAsSatisfiedInBothDirections()
    {
        Assert.True(QualityFilter.Passes(Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55), 0.55));
        Assert.True(QualityFilter.Passes(Con(WbppMetric.Stars, ConstraintOp.AtLeast, 100), 100));
    }

    [Fact]
    public void Passes_IsTrueForAConstraintWithNoValue()
        => Assert.True(QualityFilter.Passes(Con(WbppMetric.Ecc, ConstraintOp.AtMost, null), 0.99));

    [Fact]
    public void MetricTable_MapsEachMetricToItsField_DecimalsAndPolarity()
    {
        var frame = Frame(hfr: 2.1, ecc: 0.4, fwhm: 3.2, stars: 110, rms: 0.62);

        Assert.Equal((double?)2.1, QualityFilter.ValueOf(frame, WbppMetric.Hfr));
        Assert.Equal((double?)0.4, QualityFilter.ValueOf(frame, WbppMetric.Ecc));
        Assert.Equal((double?)3.2, QualityFilter.ValueOf(frame, WbppMetric.Fwhm));
        Assert.Equal((double?)110, QualityFilter.ValueOf(frame, WbppMetric.Stars));
        Assert.Equal((double?)0.62, QualityFilter.ValueOf(frame, WbppMetric.Rms));
        Assert.Null(QualityFilter.ValueOf(Frame(), WbppMetric.Hfr));

        Assert.Equal(2, QualityFilter.Decimals(WbppMetric.Hfr));
        Assert.Equal(2, QualityFilter.Decimals(WbppMetric.Ecc));
        Assert.Equal(2, QualityFilter.Decimals(WbppMetric.Fwhm));
        Assert.Equal(0, QualityFilter.Decimals(WbppMetric.Stars));
        Assert.Equal(2, QualityFilter.Decimals(WbppMetric.Rms));

        Assert.True(QualityFilter.BetterWhenHigh(WbppMetric.Stars));
        Assert.False(QualityFilter.BetterWhenHigh(WbppMetric.Hfr));
        Assert.False(QualityFilter.BetterWhenHigh(WbppMetric.Ecc));
        Assert.False(QualityFilter.BetterWhenHigh(WbppMetric.Fwhm));
        Assert.False(QualityFilter.BetterWhenHigh(WbppMetric.Rms));

        Assert.Equal(new[] { "HFR", "ecc", "FWHM", "stars", "RMS" }, AllMetrics.Select(QualityFilter.ShortName));
    }

    // ---- 7.2 The partial-metric rule ----

    [Fact]
    public void Evaluate_PassesWhenEveryPresentMetricSatisfiesItsConstraint()
    {
        var frame = Frame(hfr: 2.0, stars: 100);

        Assert.Equal(
            Verdict.Copy,
            VerdictOf(frame, Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0), Con(WbppMetric.Stars, ConstraintOp.AtLeast, 50)));
    }

    [Fact]
    public void Evaluate_FailsWhenOnePresentMetricViolatesItsConstraint()
    {
        var frame = Frame(hfr: 2.0, stars: 100);

        Assert.Equal(
            Verdict.Exclude,
            VerdictOf(frame, Con(WbppMetric.Hfr, ConstraintOp.AtMost, 1.5), Con(WbppMetric.Stars, ConstraintOp.AtLeast, 50)));
    }

    [Fact]
    public void Evaluate_SkipsAConstraintOnAMetricTheFrameDoesNotCarry()
    {
        var frame = Frame(hfr: 2.0);

        Assert.Equal(
            Verdict.Copy,
            VerdictOf(frame, Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0), Con(WbppMetric.Rms, ConstraintOp.AtMost, 1.0)));
    }

    [Fact]
    public void Evaluate_FailsOnAPresentMetricEvenWhenAnotherIsMissing()
    {
        var frame = Frame(hfr: 5.0);

        Assert.Equal(
            Verdict.Exclude,
            VerdictOf(frame, Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0), Con(WbppMetric.Rms, ConstraintOp.AtMost, 1.0)));
    }

    [Fact]
    public void Evaluate_IsUnmeasuredWhenTheFrameCarriesNoneOfTheConstrainedMetrics()
        => Assert.Equal(Verdict.Unmeasured, VerdictOf(Frame(hfr: 2.0), Con(WbppMetric.Rms, ConstraintOp.AtMost, 1.0)));

    // The web's deliberate case. An impossible guiding limit must leave an unguided frame
    // unmeasured rather than deleting it, because a limit added for guided nights must not silently
    // drop every frame from before guiding was recorded.
    [Fact]
    public void Evaluate_SkipsAMissingMetricDeliberatelyRatherThanFailingTheFrame()
    {
        var unguided = Frame(hfr: 2.0, ecc: 0.4);
        var impossible = Con(WbppMetric.Rms, ConstraintOp.AtMost, 0.0001);

        Assert.Equal(Verdict.Unmeasured, VerdictOf(unguided, impossible));
        Assert.Equal(Verdict.Copy, VerdictOf(unguided, impossible, Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0)));
        Assert.Equal(Verdict.Exclude, VerdictOf(Frame(rms: 0.8), impossible));
    }

    // ---- 7.3 The failure sentences ----

    [Fact]
    public void FailedBy_NamesTheGateTheFrameViolatedWithItsValueAndTheLimit()
    {
        Assert.Equal(
            "ecc 0.62 > 0.55",
            QualityFilter.Evaluate(Frame(ecc: 0.62), [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55)]).FailedBy);
    }

    [Fact]
    public void FailedBy_FlipsTheComparisonForAnAtLeastConstraint()
    {
        Assert.Equal(
            "stars 80 < 100",
            QualityFilter.Evaluate(Frame(stars: 80), [Con(WbppMetric.Stars, ConstraintOp.AtLeast, 100)]).FailedBy);
    }

    [Fact]
    public void FailedBy_IsTheFirstFailingConstraintInTheUsersOwnOrder()
    {
        var frame = Frame(hfr: 5.0, ecc: 0.9);

        var verdict = QualityFilter.Evaluate(
            frame,
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.5), Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0)]);

        Assert.Equal("ecc 0.90 > 0.50", verdict.FailedBy);
    }

    [Fact]
    public void Failures_CollectsEveryViolatedGateInOrderWithFailedByStayingTheFirst()
    {
        var frame = Frame(hfr: 5.0, ecc: 0.9);

        var verdict = QualityFilter.Evaluate(
            frame,
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.5), Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0)]);

        Assert.Equal(
            new[]
            {
                new MetricFailure(WbppMetric.Ecc, "ecc 0.90 > 0.50"),
                new MetricFailure(WbppMetric.Hfr, "HFR 5.00 > 3.00"),
            },
            verdict.Failures);
        Assert.Equal("ecc 0.90 > 0.50", verdict.FailedBy);
    }

    [Fact]
    public void FailedBy_IsNullAndFailuresEmptyOnACopyOrUnmeasuredVerdict()
    {
        var gate = Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.6);

        var passing = QualityFilter.Evaluate(Frame(ecc: 0.35), [gate]);
        var blank = QualityFilter.Evaluate(Frame(), [gate]);

        Assert.Equal(Verdict.Copy, passing.Verdict);
        Assert.Null(passing.FailedBy);
        Assert.Empty(passing.Failures);
        Assert.Equal(Verdict.Unmeasured, blank.Verdict);
        Assert.Null(blank.FailedBy);
        Assert.Empty(blank.Failures);
    }

    [Fact]
    public void FailedBy_NeverBlamesADisabledConstraint()
    {
        var frame = Frame(hfr: 5.0, ecc: 0.9);

        var verdict = QualityFilter.Evaluate(
            frame,
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.5, enabled: false), Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0)]);

        Assert.Equal("HFR 5.00 > 3.00", verdict.FailedBy);
    }

    // A machine whose culture writes a comma decimal mark must not put one into a sentence the user
    // reads or into a figure another surface compares as text.
    [Fact]
    public void FailureSentences_AreInvariantOfTheCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(
                "ecc 0.62 > 0.55",
                QualityFilter.Evaluate(Frame(ecc: 0.62), [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55)]).FailedBy);
            Assert.Equal("5.00 > 3.00", QualityFilter.Format(WbppMetric.Hfr, 5.0) + " > " + QualityFilter.Format(WbppMetric.Hfr, 3.0));
            Assert.Equal("80", QualityFilter.Format(WbppMetric.Stars, 80));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // The printed number is the web's Number.prototype.toFixed to the last digit, which the
    // framework's "F" format is not: toFixed rounds the double's exact binary value half away from
    // zero and "F" rounds it half to even. Every expectation below was produced by running node
    // v24.21.0 over the same value; the generator line is in task3a-filter-report.md. Both decimals
    // settings in use are covered by every row, two for the four measured metrics and zero for the
    // detected star count.
    [Theory]
    [InlineData(0.125, "0.13", "0")]
    [InlineData(0.375, "0.38", "0")]
    [InlineData(0.625, "0.63", "1")]
    [InlineData(0.875, "0.88", "1")]
    [InlineData(1.005, "1.00", "1")]
    [InlineData(8.345, "8.35", "8")]
    [InlineData(2.5, "2.50", "3")]
    [InlineData(3.5, "3.50", "4")]
    [InlineData(-0.125, "-0.13", "-0")]
    [InlineData(-2.5, "-2.50", "-3")]
    [InlineData(-0.001, "-0.00", "-0")]
    [InlineData(0.995, "0.99", "1")]
    [InlineData(9.995, "9.99", "10")]
    [InlineData(0.999, "1.00", "1")]
    [InlineData(9.999, "10.00", "10")]
    [InlineData(99.995, "100.00", "100")]
    [InlineData(0.045, "0.04", "0")]
    [InlineData(2.675, "2.67", "3")]
    [InlineData(0.5, "0.50", "1")]
    [InlineData(1.5, "1.50", "2")]
    [InlineData(0.0, "0.00", "0")]
    [InlineData(123456789.0, "123456789.00", "123456789")]
    public void Format_MatchesTheWebsToFixedToTheLastPrintedDigit(
        double value,
        string atTwoDecimals,
        string atNoDecimals)
    {
        Assert.Equal(atTwoDecimals, QualityFilter.Format(WbppMetric.Hfr, value));
        Assert.Equal(atTwoDecimals, QualityFilter.Format(WbppMetric.Ecc, value));
        Assert.Equal(atTwoDecimals, QualityFilter.Format(WbppMetric.Fwhm, value));
        Assert.Equal(atTwoDecimals, QualityFilter.Format(WbppMetric.Rms, value));
        Assert.Equal(atNoDecimals, QualityFilter.Format(WbppMetric.Stars, value));
    }

    [Fact]
    public void ShortNameAndValueOf_RefuseAMetricOutsideTheFive()
    {
        var outside = (WbppMetric)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => QualityFilter.ShortName(outside));
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityFilter.ValueOf(Frame(), outside));
    }

    // ---- 7.4 The enabled flag and the valueless constraint ----

    [Fact]
    public void Evaluate_PassesEveryFrameWhenNoConstraintIsActive()
    {
        Assert.Equal(Verdict.Copy, VerdictOf(Frame(hfr: 2.0)));
        Assert.Equal(Verdict.Copy, VerdictOf(Frame()));
        Assert.Equal(Verdict.Copy, VerdictOf(Frame(hfr: 99), Con(WbppMetric.Hfr, ConstraintOp.AtMost, 1.0, enabled: false)));
    }

    [Fact]
    public void Evaluate_TreatsADisabledConstraintExactlyLikeADeletedOne()
    {
        var frame = Frame(hfr: 5.0, ecc: 0.4);

        Assert.Equal(
            Verdict.Exclude,
            VerdictOf(frame, Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0), Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.6)));
        Assert.Equal(
            Verdict.Copy,
            VerdictOf(frame, Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0, enabled: false), Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.6)));
    }

    [Fact]
    public void Evaluate_DoesNotCountADisabledConstraintsMetricTowardPresent()
    {
        var frame = Frame(hfr: 2.0);

        Assert.Equal(
            Verdict.Unmeasured,
            VerdictOf(frame, Con(WbppMetric.Hfr, ConstraintOp.AtMost, 3.0, enabled: false), Con(WbppMetric.Rms, ConstraintOp.AtMost, 1.0)));
    }

    [Fact]
    public void Evaluate_GatesNothingOnAValuelessConstraint()
    {
        var empty = Con(WbppMetric.Ecc, ConstraintOp.AtMost, null);

        Assert.Equal(Verdict.Copy, VerdictOf(Frame(ecc: 0.99), empty));
        Assert.Equal(Verdict.Copy, VerdictOf(Frame(), empty));
        Assert.Equal(
            Verdict.Unmeasured,
            VerdictOf(Frame(ecc: 0.4), empty, Con(WbppMetric.Rms, ConstraintOp.AtMost, 1.0)));
    }

    [Fact]
    public void IsActive_RequiresBothEnabledAndAValue()
    {
        Assert.True(QualityFilter.IsActive(Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55)));
        Assert.False(QualityFilter.IsActive(Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55, enabled: false)));
        Assert.False(QualityFilter.IsActive(Con(WbppMetric.Ecc, ConstraintOp.AtMost, null)));
    }

    [Fact]
    public void EmptyConstraintFor_StartsEveryChipValuelessAndEnabledWithPolarityCorrectOp()
    {
        foreach (var metric in AllMetrics)
        {
            var constraint = QualityFilter.EmptyConstraintFor(metric);

            Assert.Null(constraint.Value);
            Assert.True(constraint.Enabled);
            Assert.Equal(metric == WbppMetric.Stars ? ConstraintOp.AtLeast : ConstraintOp.AtMost, constraint.Op);
            Assert.False(QualityFilter.IsActive(constraint));
        }
    }

    [Fact]
    public void EccentricityPresets_ShipTheStrictBalancedRelaxedLadder()
        => Assert.Equal(new[] { 0.55, 0.65, 0.75 }, QualityFilter.EccentricityPresets);

    // ---- 7.5 The override ----

    [Fact]
    public void IsIncluded_LetsAnOverrideReplaceTheComputedAnswerInBothDirections()
    {
        var excludedId = Guid.NewGuid();
        var keptId = Guid.NewGuid();
        var blankId = Guid.NewGuid();
        var gate = Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.45);

        var excluded = QualityFilter.Evaluate(Frame(ecc: 0.5, imageId: excludedId), [gate]);
        var kept = QualityFilter.Evaluate(Frame(ecc: 0.35, imageId: keptId), [gate]);
        var blank = QualityFilter.Evaluate(Frame(imageId: blankId), [gate]);

        var none = new Dictionary<Guid, bool>();
        Assert.False(QualityFilter.IsIncluded(excluded, true, none));
        Assert.True(QualityFilter.IsIncluded(kept, true, none));
        Assert.False(QualityFilter.IsIncluded(blank, true, none));

        var forcedIn = new Dictionary<Guid, bool> { [excludedId] = true, [blankId] = true };
        Assert.True(QualityFilter.IsIncluded(excluded, true, forcedIn));
        Assert.True(QualityFilter.IsIncluded(blank, true, forcedIn));
        // An override on one frame leaves another alone.
        Assert.True(QualityFilter.IsIncluded(kept, true, forcedIn));

        var forcedOut = new Dictionary<Guid, bool> { [keptId] = false };
        Assert.False(QualityFilter.IsIncluded(kept, true, forcedOut));
        // A replacement, never an "or": the override still excludes with the filter switched off.
        Assert.False(QualityFilter.IsIncluded(kept, false, forcedOut));
        Assert.True(QualityFilter.IsIncluded(excluded, false, forcedOut));
    }

    // ---- 7.6 Totals ----

    [Fact]
    public void Totals_SplitTheSelectionAndAlwaysAddUp()
    {
        var goodId = Guid.NewGuid();
        var badId = Guid.NewGuid();
        var verdicts = QualityFilter.EvaluateAll(
            [Frame(ecc: 0.35, imageId: goodId), Frame(ecc: 0.5, imageId: badId), Frame()],
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.45)]);
        var none = new Dictionary<Guid, bool>();

        var on = QualityFilter.Totals(verdicts, true, none);
        Assert.Equal(new QualityTotals(3, 1, 1, 1, 0), on);
        AssertSums(on);

        var off = QualityFilter.Totals(verdicts, false, none);
        Assert.Equal(new QualityTotals(3, 3, 0, 0, 0), off);
        AssertSums(off);

        // The excluded frame forced in and the passing frame forced out. The forced-out Copy
        // verdict counts under Exclude, so no frame falls into no bucket at all.
        var both = QualityFilter.Totals(
            verdicts,
            true,
            new Dictionary<Guid, bool> { [badId] = true, [goodId] = false });
        Assert.Equal(new QualityTotals(3, 1, 1, 1, 2), both);
        AssertSums(both);
    }

    [Fact]
    public void Totals_IgnoreAnOverrideKeyedOnAFrameTheSelectionDoesNotHold()
    {
        var verdicts = QualityFilter.EvaluateAll(
            [Frame(ecc: 0.35), Frame(ecc: 0.5)],
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.45)]);

        var stray = QualityFilter.Totals(
            verdicts,
            true,
            new Dictionary<Guid, bool> { [Guid.NewGuid()] = false });

        Assert.Equal(new QualityTotals(2, 1, 1, 0, 0), stray);
        AssertSums(stray);
    }

    [Fact]
    public void Totals_CountAnUnmeasuredFrameForcedInUnderCopy()
    {
        var blankId = Guid.NewGuid();
        var verdicts = QualityFilter.EvaluateAll(
            [Frame(ecc: 0.35), Frame(ecc: 0.5), Frame(imageId: blankId)],
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.45)]);

        var rescued = QualityFilter.Totals(verdicts, true, new Dictionary<Guid, bool> { [blankId] = true });

        Assert.Equal(new QualityTotals(3, 2, 1, 0, 1), rescued);
        AssertSums(rescued);
    }

    [Fact]
    public void Totals_CountOverriddenOnlyWhereTheOverrideDiffersFromTheComputedAnswer()
    {
        var goodId = Guid.NewGuid();
        var badId = Guid.NewGuid();
        var verdicts = QualityFilter.EvaluateAll(
            [Frame(ecc: 0.35, imageId: goodId), Frame(ecc: 0.5, imageId: badId)],
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.45)]);

        // Both overrides agree with the computed answer, so neither is an override of anything.
        var agreeing = QualityFilter.Totals(
            verdicts,
            true,
            new Dictionary<Guid, bool> { [goodId] = true, [badId] = false });

        Assert.Equal(new QualityTotals(2, 1, 1, 0, 0), agreeing);
        AssertSums(agreeing);
    }

    private static void AssertSums(QualityTotals totals)
        => Assert.Equal(totals.Total, totals.Copy + totals.Exclude + totals.Unmeasured);

    // ---- 7.7 The copy set ----

    [Fact]
    public void CopySet_HoldsCopyVerdictsOnlyUnlessAnOverrideRescuesARow()
    {
        var blankId = Guid.NewGuid();
        var verdicts = QualityFilter.EvaluateAll(
            [Frame(ecc: 0.35, fileName: "good.fits"), Frame(ecc: 0.5, fileName: "bad.fits"), Frame(imageId: blankId, fileName: "blank.fits")],
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.45)]);
        var none = new Dictionary<Guid, bool>();

        Assert.Equal(
            new[] { "good.fits" },
            verdicts.Where(v => QualityFilter.IsIncluded(v, true, none)).Select(v => v.Frame.FileName));

        var rescued = new Dictionary<Guid, bool> { [blankId] = true };
        Assert.Equal(
            new[] { "good.fits", "blank.fits" },
            verdicts.Where(v => QualityFilter.IsIncluded(v, true, rescued)).Select(v => v.Frame.FileName));
    }

    [Fact]
    public void EvaluateAll_KeepsTheInputOrderAndGroupsNothing()
    {
        var names = new[] { "c.fits", "a.fits", "b.fits" };

        var verdicts = QualityFilter.EvaluateAll(
            names.Select(n => Frame(ecc: 0.5, fileName: n)),
            [Con(WbppMetric.Ecc, ConstraintOp.AtMost, 0.45)]);

        Assert.Equal(names, verdicts.Select(v => v.Frame.FileName));
    }

    // ---- 7.8 Provenance blindness ----

    [Fact]
    public void GuidingFilter_IsBlindToWhereTheGuidingFigureCameFrom()
    {
        // The filter reads GuidingRmsArcsec, which the PHD2 correlation and the sidecar importer
        // fill in the same column and the same units. The two frames differ in every non-metric
        // member a provenance field would sit beside, so the first assertion is the live rule, that
        // nothing but the constrained metric reaches the verdict, rather than one value equalling
        // itself. The reflection sweep is the structural half: it goes red the moment a provenance
        // member lands on the record.
        var fromGuideLog = Frame(rms: 0.62, fileName: "guided_007.fits", filterUsed: "Ha");
        var fromSidecar = Frame(rms: 0.62, fileName: "sidecar_412.fits", filterUsed: "OIII");
        var gate = Con(WbppMetric.Rms, ConstraintOp.AtMost, 0.8);

        Assert.NotEqual(fromGuideLog.FileName, fromSidecar.FileName);
        Assert.NotEqual(fromGuideLog.FilePath, fromSidecar.FilePath);
        Assert.NotEqual(fromGuideLog.FilterUsed, fromSidecar.FilterUsed);
        Assert.Equal(VerdictOf(fromGuideLog, gate), VerdictOf(fromSidecar, gate));
        Assert.Equal(
            QualityFilter.Evaluate(fromGuideLog, [gate]).FailedBy,
            QualityFilter.Evaluate(fromSidecar, [gate]).FailedBy);
        Assert.DoesNotContain(
            typeof(WbppFrame).GetProperties(),
            p => p.Name.Contains("Source", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 7.9 No second grading ladder ----

    // The verdict is absolute thresholds over the frame's own values. FrameQuality's deviation and
    // score ladders belong to the panel's cell colours, and a band threshold copied into this file
    // because the panel would need one is a second ladder in a file that has no business holding
    // one.
    [Fact]
    public void QualityFilterSource_HoldsNoBandThresholdAndNoGradingMember()
    {
        var source = StripComments(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "GalactiLog.Core", "Wbpp", "QualityFilter.cs")));

        foreach (var number in new[] { "1.5", "3.0", "16.7", "60", "45", "30" })
        {
            Assert.False(
                Regex.IsMatch(source, $@"(?<![\d.]){Regex.Escape(number)}(?![\d.])"),
                $"QualityFilter.cs names the band figure {number}.");
        }

        Assert.DoesNotContain("MadZ", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MinGroup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BandFor", source, StringComparison.Ordinal);
    }

    // Block comments, whole comment lines and a trailing comment after code on the same line.
    // Prose that names what the verdict path declines to do is the honest documentation of the
    // rule; a band figure in code is what this scan is for. The trailing run is cut to the end of
    // the line, which would also cut a string literal holding two slashes; the file under scan
    // holds no such literal, and a future one is a question for the coordinator rather than a
    // reason to loosen the case.
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return string.Join(
            Environment.NewLine,
            source.Split('\n')
                .Select(line => Regex.Replace(line, @"//.*$", ""))
                .Where(line => line.Trim().Length > 0));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above the test output directory.");
    }
}
