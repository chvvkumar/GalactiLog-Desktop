using System.Globalization;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// design-spec 12.13's chip lifecycle, Task 3b brief section 8.2: absent to active to held and
/// back, the threshold field's own rules, and the three eccentricity presets.
/// </summary>
public class ConstraintChipTests
{
    private static ConstraintChipViewModel Chip(WbppMetric metric, out List<WbppMetric> edits)
    {
        var recorded = new List<WbppMetric>();
        edits = recorded;
        return new ConstraintChipViewModel(metric, chip => recorded.Add(chip.Metric));
    }

    /// <summary>A fresh chip is absent: a ghost carrying a plus and the metric name, nothing
    /// else.</summary>
    [Fact]
    public void AFreshChip_IsAGhostCarryingAPlusAndTheMetricName()
    {
        var chip = Chip(WbppMetric.Hfr, out var edits);

        Assert.False(chip.IsActive);
        Assert.Equal("+ HFR", chip.GhostText);
        Assert.Null(chip.Constraint);
        Assert.Empty(edits);
    }

    /// <summary>
    /// Clicking an absent chip adds <c>QualityFilter.EmptyConstraintFor</c>: enabled, valueless,
    /// with the comparator the metric's polarity implies.
    /// </summary>
    [Theory]
    [InlineData(WbppMetric.Hfr, ConstraintOp.AtMost)]
    [InlineData(WbppMetric.Ecc, ConstraintOp.AtMost)]
    [InlineData(WbppMetric.Fwhm, ConstraintOp.AtMost)]
    [InlineData(WbppMetric.Stars, ConstraintOp.AtLeast)]
    [InlineData(WbppMetric.Rms, ConstraintOp.AtMost)]
    public void ClickingAnAbsentChip_AddsAnEnabledValuelessConstraintWithThePolaritysComparator(
        WbppMetric metric,
        ConstraintOp expected)
    {
        var chip = Chip(metric, out var edits);

        chip.EnableCommand.Execute(null);

        Assert.True(chip.IsActive);
        Assert.Null(chip.Value);
        Assert.Equal(expected, chip.Comparator.Op);
        Assert.NotNull(chip.Constraint);
        Assert.Equal(new RawConstraint(metric, expected, null, true), chip.Constraint);
        Assert.Single(edits);
    }

    /// <summary>Typing a threshold sets the value, at the metric's own decimals and in invariant
    /// culture.</summary>
    [Fact]
    public void TypingAThreshold_SetsTheValue()
    {
        var chip = Chip(WbppMetric.Hfr, out var edits);
        chip.EnableCommand.Execute(null);

        chip.ThresholdText = "2.5";

        Assert.Equal(2.5d, chip.Value);
        Assert.Equal(new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 2.5d, true), chip.Constraint);
        Assert.Equal(2, edits.Count);
    }

    /// <summary>
    /// Spec 12.13: the button "disables the constraint without deleting it: the chip drops back to
    /// a ghost that still shows its held value, and clicking it again restores that value rather
    /// than starting over."
    /// </summary>
    /// <remarks>Fails when the disable button deletes the constraint, so the user's threshold is
    /// gone the moment they turn a gate off and back on, which reads as the panel losing their
    /// settings.</remarks>
    [Fact]
    public void TheDisableButton_LeavesTheConstraintInPlaceWithItsValue_AndAClickRestoresIt()
    {
        var chip = Chip(WbppMetric.Hfr, out _);
        chip.EnableCommand.Execute(null);
        chip.ThresholdText = "2.5";

        chip.DisableCommand.Execute(null);

        Assert.False(chip.IsActive);
        Assert.Equal(2.5d, chip.Value);
        Assert.Equal(new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, 2.5d, false), chip.Constraint);

        // The ghost still shows its held comparator and value.
        Assert.Equal("HFR at most 2.50", chip.GhostText);

        chip.EnableCommand.Execute(null);

        Assert.True(chip.IsActive);
        Assert.Equal(2.5d, chip.Value);
        Assert.Equal("2.5", chip.ThresholdText);
    }

    /// <summary>A held chip with no value yet shows the plus and the name, not an empty
    /// comparison.</summary>
    [Fact]
    public void AHeldChipWithNoValue_ShowsThePlusAndTheName()
    {
        var chip = Chip(WbppMetric.Hfr, out _);
        chip.EnableCommand.Execute(null);
        chip.DisableCommand.Execute(null);

        Assert.Equal("+ HFR", chip.GhostText);
    }

    /// <summary>
    /// Spec 12.13: "Emptying the threshold field returns the chip to valueless rather than pinning
    /// the last number typed."
    /// </summary>
    [Fact]
    public void EmptyingTheThresholdField_PublishesNullRatherThanPinningTheLastNumber()
    {
        var chip = Chip(WbppMetric.Hfr, out var edits);
        chip.EnableCommand.Execute(null);
        chip.ThresholdText = "2.5";
        var before = edits.Count;

        chip.ThresholdText = "";

        Assert.Null(chip.Value);
        Assert.Equal(new RawConstraint(WbppMetric.Hfr, ConstraintOp.AtMost, null, true), chip.Constraint);
        Assert.Equal(before + 1, edits.Count);
    }

    /// <summary>A non-numeric entry leaves the held value alone and the text as the user typed it,
    /// so the field can be corrected in place, and it publishes nothing.</summary>
    [Fact]
    public void ANonNumericEntry_LeavesTheHeldValueAloneAndPublishesNothing()
    {
        var chip = Chip(WbppMetric.Hfr, out var edits);
        chip.EnableCommand.Execute(null);
        chip.ThresholdText = "2.5";
        var before = edits.Count;

        chip.ThresholdText = "2.5x";

        Assert.Equal(2.5d, chip.Value);
        Assert.Equal("2.5x", chip.ThresholdText);
        Assert.Equal(before, edits.Count);
    }

    /// <summary>Invariant culture on the way in: a comma decimal mark is not a number here, so a
    /// European machine cannot put <c>0,55</c> into a stored threshold.</summary>
    [Fact]
    public void TheThresholdField_ParsesInvariantCulture()
    {
        var chip = Chip(WbppMetric.Ecc, out _);
        chip.EnableCommand.Execute(null);
        chip.ThresholdText = "0.55";

        chip.ThresholdText = "0,65";

        Assert.Equal(0.55d, chip.Value);
        Assert.Equal("0.55", QualityFilter.Format(WbppMetric.Ecc, chip.Value!.Value));
    }

    /// <summary>The comparator select carries spec 12.13's two words, and its option list is one
    /// static list that is never rebuilt under a two-way selection.</summary>
    [Fact]
    public void TheComparatorSelect_CarriesTheTwoWords_OnOneListThatIsNeverRebuilt()
    {
        var first = Chip(WbppMetric.Hfr, out _);
        var second = Chip(WbppMetric.Stars, out _);

        Assert.Equal(["at most", "at least"], ConstraintChipViewModel.Comparators.Select(option => option.Label));
        Assert.Same(ConstraintChipViewModel.Comparators, first.ComparatorOptions);
        Assert.Same(first.ComparatorOptions, second.ComparatorOptions);
        Assert.Contains(first.Comparator, ConstraintChipViewModel.Comparators);
    }

    /// <summary>Moving the comparator publishes one edit.</summary>
    [Fact]
    public void MovingTheComparator_PublishesOneEdit()
    {
        var chip = Chip(WbppMetric.Hfr, out var edits);
        chip.EnableCommand.Execute(null);
        var before = edits.Count;

        chip.Comparator = ConstraintChipViewModel.Comparators.Single(option => option.Op == ConstraintOp.AtLeast);

        Assert.Equal(ConstraintOp.AtLeast, chip.Constraint!.Op);
        Assert.Equal(before + 1, edits.Count);
    }

    /// <summary>
    /// Spec 12.13's three presets are Strict 0.55, Balanced 0.65 and Relaxed 0.75, their figures
    /// read from <c>QualityFilter.EccentricityPresets</c>, and only the eccentricity chip has them.
    /// </summary>
    [Fact]
    public void OnlyTheEccentricityChip_CarriesTheThreePresets()
    {
        var ecc = Chip(WbppMetric.Ecc, out _);

        Assert.True(ecc.HasPresets);
        Assert.Equal(
            QualityFilter.EccentricityPresets,
            ecc.Presets.Select(preset => preset.Value));
        Assert.Equal(["Strict 0.55", "Balanced 0.65", "Relaxed 0.75"], ecc.Presets.Select(preset => preset.Caption));

        foreach (var metric in new[] { WbppMetric.Hfr, WbppMetric.Fwhm, WbppMetric.Stars, WbppMetric.Rms })
        {
            var other = Chip(metric, out _);
            Assert.False(other.HasPresets);
            Assert.Empty(other.Presets);
        }
    }

    /// <summary>Each preset sets the value and the comparator together and is lit only while both
    /// match.</summary>
    [Fact]
    public void APreset_SetsTheValueAndTheComparatorTogether_AndIsLitOnlyWhileBothMatch()
    {
        var chip = Chip(WbppMetric.Ecc, out var edits);
        chip.EnableCommand.Execute(null);
        chip.Comparator = ConstraintChipViewModel.Comparators.Single(option => option.Op == ConstraintOp.AtLeast);
        var balanced = chip.Presets[1];

        chip.ApplyPresetCommand.Execute(balanced);

        Assert.Equal(0.65d, chip.Value);
        Assert.Equal(ConstraintOp.AtMost, chip.Comparator.Op);
        Assert.True(balanced.IsActive);
        Assert.All(chip.Presets.Where(preset => preset != balanced), preset => Assert.False(preset.IsActive));
        Assert.Equal("0.65", chip.ThresholdText);
        Assert.NotEmpty(edits);

        // The same value under the other comparator is not the current pair.
        chip.Comparator = ConstraintChipViewModel.Comparators.Single(option => option.Op == ConstraintOp.AtLeast);
        Assert.All(chip.Presets, preset => Assert.False(preset.IsActive));
    }

    /// <summary>
    /// A click on the preset that is already lit publishes nothing, because neither the comparator
    /// nor the value moved.
    /// </summary>
    /// <remarks>Fails when <c>ApplyPreset</c> publishes unconditionally: the panel answers an edit
    /// by clearing every row override and queueing a settings write, so a click on a button that
    /// changes nothing would silently cost the user every row they had rescued.</remarks>
    [Fact]
    public void ClickingTheLitPreset_PublishesNothing()
    {
        var chip = Chip(WbppMetric.Ecc, out var edits);
        chip.EnableCommand.Execute(null);
        var balanced = chip.Presets[1];
        chip.ApplyPresetCommand.Execute(balanced);
        var before = edits.Count;

        chip.ApplyPresetCommand.Execute(balanced);

        Assert.Equal(before, edits.Count);
        Assert.True(balanced.IsActive);
        Assert.Equal(0.65d, chip.Value);
        Assert.Equal(ConstraintOp.AtMost, chip.Comparator.Op);

        // A preset that is not the current pair still publishes: the guard is the lit one only.
        chip.ApplyPresetCommand.Execute(chip.Presets[0]);
        Assert.Equal(before + 1, edits.Count);
    }

    /// <summary>A preset figure printed by the chip is invariant, whatever the running culture
    /// writes a decimal mark as.</summary>
    [Fact]
    public void APresetFigure_IsPrintedInvariant()
    {
        var chip = Chip(WbppMetric.Ecc, out _);

        Assert.Equal(
            ["0.55", "0.65", "0.75"],
            chip.Presets.Select(preset => preset.ValueText));
        Assert.Equal(
            "0.55",
            0.55d.ToString("F2", CultureInfo.InvariantCulture));
    }

    /// <summary>Adopting a stored entry publishes nothing, which is what stops a page open from
    /// queueing a write of the value it has just read.</summary>
    [Fact]
    public void Adopt_PublishesNothing()
    {
        var chip = Chip(WbppMetric.Ecc, out var edits);

        chip.Adopt(new RawConstraint(WbppMetric.Ecc, ConstraintOp.AtMost, 0.55d, true));

        Assert.True(chip.IsActive);
        Assert.Equal(0.55d, chip.Value);
        Assert.Equal("0.55", chip.ThresholdText);
        Assert.True(chip.Presets[0].IsActive);
        Assert.Empty(edits);
    }

    /// <summary>A second click on an already active chip changes nothing, because
    /// <c>RelayCommand.Execute</c> ignores <c>CanExecute</c> and the body repeats the
    /// guard.</summary>
    [Fact]
    public void ASecondClickOnAnActiveChip_DoesNotResetTheValue()
    {
        var chip = Chip(WbppMetric.Hfr, out var edits);
        chip.EnableCommand.Execute(null);
        chip.ThresholdText = "2.5";
        var before = edits.Count;

        chip.EnableCommand.Execute(null);

        Assert.Equal(2.5d, chip.Value);
        Assert.Equal(before, edits.Count);
    }
}
