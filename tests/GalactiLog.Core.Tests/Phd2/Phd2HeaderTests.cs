using GalactiLog.Core.Phd2;
using Xunit;
using static GalactiLog.Core.Tests.Phd2.Phd2Assert;

namespace GalactiLog.Core.Tests.Phd2;

// One case per header grammar of spec 7.6, driven through Phd2LogParser.ApplyHeaderLine against
// the corpus line the grammar was written for. Port of test_phd2_parser.py's header cases.
public class Phd2HeaderTests
{
    private static Phd2Header Fold(params string[] lines)
    {
        var header = new Phd2Header();
        foreach (var line in lines)
        {
            Phd2LogParser.ApplyHeaderLine(header, line);
        }
        return header;
    }

    private static Phd2Header One(string line)
    {
        var header = new Phd2Header();
        Assert.True(Phd2LogParser.ApplyHeaderLine(header, line));
        return header;
    }

    [Fact]
    public void TheWholeDesktopHeaderBlock_FoldsIntoOneHeader()
    {
        var h = Fold(Phd2LogFixtures.GuidingHeaderLines);

        Assert.Equal("AM5n_OAG_ASI174M", h.EquipmentProfile);
        Near(1.54, h.PixelScaleArcsec);
        Assert.Equal(1, h.Binning);
        Near(784.0, h.FocalLengthMm);
        Assert.Equal("ZWO ASI174MM Mini", h.GuideCamera);
        Near(500.0, h.ExposureMs);
        Assert.Equal("ASI Mount (ASCOM)", h.MountName);
        Near(87.9, h.XAngleDeg);
        Near(3.579, h.XRatePxS);
        Near(-170.3, h.YAngleDeg);
        Near(4.437, h.YRatePxS);
        Assert.Equal("?/?", h.Parity);
        Near(7.0, h.NormRateRa);
        Near(6.8, h.NormRateDec);
        Near(11.8, h.OrthoErrorDeg);
        Assert.Equal("Hysteresis", h.AlgoRa);
        Near(0.100, h.HysteresisRa);
        Near(0.700, h.AggressionRa);
        Near(0.250, h.MinMoveRa);
        Assert.Equal("Resist Switch", h.AlgoDec);
        Near(0.250, h.MinMoveDec);
        Near(100.0, h.AggressionDec);
        Assert.False(h.BacklashCompEnabled);
        Near(254.0, h.BacklashPulseMs);
        Near(2500.0, h.MaxRaDurationMs);
        Near(2500.0, h.MaxDecDurationMs);
        Assert.Equal("Auto", h.DecGuideMode);
        Near(7.5, h.RaGuideSpeed);
        Near(7.5, h.DecGuideSpeed);
        Near(38.5, h.CalDecDeg);
        Assert.Equal("None", h.LastCalIssue);
        Assert.Equal(new DateTime(2026, 7, 14, 21, 42, 27), h.CalTimestamp);
        Near(20.22, h.RaHr);
        Near(38.5, h.DecDeg);
        Near(-4.02, h.HourAngleHr);
        Assert.Equal("West", h.PierSide);
        Assert.Equal("N/A", h.RotatorPos);
        Near(43.7, h.AltDeg);
        Near(70.4, h.AzDeg);
        Near(77.407, h.LockX);
        Near(204.420, h.LockY);
        Near(77.407, h.StarX);
        Near(204.420, h.StarY);
        Near(1.91, h.HfdPx);
    }

    // The parser returns the wall clock exactly as written, with no zone applied anywhere
    // (ruling F1). A Kind of Local or Utc here means something converted it.
    [Fact]
    public void TheCalTimestamp_KeepsTheUnspecifiedKind()
    {
        var h = Fold(Phd2LogFixtures.GuidingHeaderLines);
        Assert.Equal(DateTimeKind.Unspecified, h.CalTimestamp!.Value.Kind);
    }

    [Fact]
    public void TheEquipmentProfileLine_FillsTheProfileName()
    {
        Assert.Equal("AM5n_OAG_ASI174M", One("Equipment Profile = AM5n_OAG_ASI174M").EquipmentProfile);
    }

    // The ASIAIR names no profile. Both forms the corpus carries, with and without the trailing
    // space, leave the field null: ApplyHeaderLine trims the line before matching, so the space
    // is gone by the time "^Equipment Profile = (.+)$" is tried and the line matches nothing.
    // The empty-string reading is unreachable through this method, in the web application too.
    // Failure shape: "" or a true return, which would mean the trim was dropped or the pattern
    // gained a "(.*)"; either would change which branch Task 3's profile map lookup takes.
    [Fact]
    public void AnEmptyEquipmentProfile_ReadsAsNullBecauseTheLineIsTrimmedFirst()
    {
        var withSpace = new Phd2Header();
        Assert.False(Phd2LogParser.ApplyHeaderLine(withSpace, Phd2LogFixtures.AsiairEmptyProfileLine));
        Assert.Null(withSpace.EquipmentProfile);

        var withoutSpace = new Phd2Header();
        Assert.False(Phd2LogParser.ApplyHeaderLine(withoutSpace, "Equipment Profile ="));
        Assert.Null(withoutSpace.EquipmentProfile);
    }

    // A profile name padded with spaces still trims down to its name, which is what makes the
    // line above a no-name line rather than a whitespace name.
    [Fact]
    public void APaddedEquipmentProfileName_IsTrimmed()
    {
        Assert.Equal("Rig A", One("Equipment Profile =   Rig A  ").EquipmentProfile);
    }

    [Fact]
    public void ThePixelScaleLine_FillsScaleBinningAndFocalLength()
    {
        var h = One("Pixel scale = 1.54 arc-sec/px, Binning = 1, Focal length = 784 mm");
        Near(1.54, h.PixelScaleArcsec);
        Assert.Equal(1, h.Binning);
        Near(784.0, h.FocalLengthMm);
    }

    // Both trailing groups are optional, so the shortest legal form still fills the scale.
    [Fact]
    public void ThePixelScaleLine_WithoutBinningOrFocalLength_StillFillsTheScale()
    {
        var h = One("Pixel scale = 6.45 arc-sec/px");
        Near(6.45, h.PixelScaleArcsec);
        Assert.Null(h.Binning);
        Assert.Null(h.FocalLengthMm);
    }

    // Failure shape: GuideCamera null, because the line was matched before it was trimmed.
    // This is a real PHD2 build's output, not a hypothetical.
    [Fact]
    public void CameraLine_WithItsLeadingSpace_IsStillRecognised()
    {
        var h = One(Phd2LogFixtures.GuidingHeaderLines[4]);
        Assert.Equal("ZWO ASI174MM Mini", h.GuideCamera);
    }

    [Fact]
    public void TheExposureLine_FillsTheExposureInMilliseconds()
    {
        Near(500.0, One("Exposure = 500 ms").ExposureMs);
    }

    [Fact]
    public void TheMountLine_FillsTheNameAndItsFiveSubSearches()
    {
        var h = One(Phd2LogFixtures.GuidingHeaderLines[6]);
        Assert.Equal("ASI Mount (ASCOM)", h.MountName);
        Near(87.9, h.XAngleDeg);
        Near(3.579, h.XRatePxS);
        Near(-170.3, h.YAngleDeg);
        Near(4.437, h.YRatePxS);
        Assert.Equal("?/?", h.Parity);
    }

    [Fact]
    public void TheNormRatesLine_FillsBothRatesAndTheOrthogonalityError()
    {
        var h = One(Phd2LogFixtures.GuidingHeaderLines[7]);
        Near(7.0, h.NormRateRa);
        Near(6.8, h.NormRateDec);
        Near(11.8, h.OrthoErrorDeg);
    }

    [Fact]
    public void TheXAlgorithmLine_FillsItsNameHysteresisAggressionAndMinimumMove()
    {
        var h = One(Phd2LogFixtures.GuidingHeaderLines[8]);
        Assert.Equal("Hysteresis", h.AlgoRa);
        Near(0.100, h.HysteresisRa);
        Near(0.700, h.AggressionRa);
        Near(0.250, h.MinMoveRa);
    }

    [Fact]
    public void TheYAlgorithmLine_FillsItsNameMinimumMoveAndAggression()
    {
        var h = One(Phd2LogFixtures.GuidingHeaderLines[9]);
        Assert.Equal("Resist Switch", h.AlgoDec);
        Near(0.250, h.MinMoveDec);
        Near(100.0, h.AggressionDec);
        // The Y line carries no Hysteresis, and nothing may invent one.
        Assert.Null(h.HysteresisRa);
    }

    // Failure shape: MinMoveRa reading the Y line's value, which happens when one shared field
    // is used for both axes.
    [Fact]
    public void TheYAlgorithmLine_DoesNotOverwriteTheXMinimumMove()
    {
        var h = Fold(
            "X guide algorithm = Hysteresis, Hysteresis = 0.100, Aggression = 0.700, Minimum move = 0.250",
            "Y guide algorithm = Resist Switch, Minimum move = 0.900 Aggression = 100% FastSwitch = enabled");
        Near(0.250, h.MinMoveRa);
        Near(0.900, h.MinMoveDec);
    }

    // Failure shape: AggressionDec 1.0, which means someone "fixed" the percent into a fraction
    // and silently changed a stored value. PHD2 writes the two axes in different units.
    [Fact]
    public void TheTwoAggressionFields_KeepTheirDifferentUnits()
    {
        var h = Fold(Phd2LogFixtures.GuidingHeaderLines);
        Near(0.700, h.AggressionRa);
        Near(100.0, h.AggressionDec);
    }

    // Failure shape: BacklashCompEnabled null, which means the word was compared
    // case-sensitively against "Enabled".
    [Fact]
    public void BacklashDisabled_ReadsFalseAndStillKeepsThePulse()
    {
        var h = One("Backlash comp = disabled, pulse = 254 ms");
        Assert.False(h.BacklashCompEnabled);
        Near(254.0, h.BacklashPulseMs);
    }

    // Not in the brief's case list: it pins the other side of the same case-insensitive test,
    // which no case above exercises. Failure shape: false on a line that says Enabled.
    [Fact]
    public void BacklashEnabled_ReadsTrueWhateverItsCase()
    {
        Assert.True(One("Backlash comp = Enabled, pulse = 100 ms").BacklashCompEnabled);
        Assert.True(One("Backlash comp = enabled").BacklashCompEnabled);
    }

    [Fact]
    public void TheDesktopMaxDurationLine_FillsBothDurationsAndTheGuideMode()
    {
        var h = One("Max RA duration = 2500, Max DEC duration = 2500, DEC guide mode = Auto");
        Near(2500.0, h.MaxRaDurationMs);
        Near(2500.0, h.MaxDecDurationMs);
        Assert.Equal("Auto", h.DecGuideMode);
    }

    // Failure shape: all three of MaxRaDurationMs, MaxDecDurationMs and DecGuideMode null, the
    // exact silent loss the optional prefix exists to prevent. It cost all 74 ASIAIR sections.
    [Fact]
    public void TheAsiairMaxDurationLine_SurvivesItsCalibrationStepPrefix()
    {
        var h = One(
            "Calibration step = phdlab_placeholder, Max RA duration = 2000, " +
            "Max DEC duration = 2000, DEC guide mode = Auto");
        Near(2000.0, h.MaxRaDurationMs);
        Near(2000.0, h.MaxDecDurationMs);
        Assert.Equal("Auto", h.DecGuideMode);
    }

    [Fact]
    public void TheGuideSpeedLine_FillsBothSpeedsAndItsThreeSubSearches()
    {
        var h = One(Phd2LogFixtures.GuidingHeaderLines[12]);
        Near(7.5, h.RaGuideSpeed);
        Near(7.5, h.DecGuideSpeed);
        Near(38.5, h.CalDecDeg);
        Assert.Equal("None", h.LastCalIssue);
        Assert.Equal(new DateTime(2026, 7, 14, 21, 42, 27), h.CalTimestamp);
    }

    // The United States twelve-hour timestamp is unpadded in the corpus, so the format string
    // is "M/d/yyyy h:mm:ss tt" and not "MM/dd/yyyy hh:mm:ss tt", which would reject it.
    [Fact]
    public void TheCalTimestamp_ParsesBothPaddedAndUnpaddedForms()
    {
        var unpadded = One("RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Timestamp = 7/14/2026 9:42:27 PM");
        Assert.Equal(new DateTime(2026, 7, 14, 21, 42, 27), unpadded.CalTimestamp);

        var padded = One("RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Timestamp = 07/14/2026 09:42:27 PM");
        Assert.Equal(new DateTime(2026, 7, 14, 21, 42, 27), padded.CalTimestamp);
    }

    // Failure shape: a throw from ParseExact, or a DateTime.MinValue. The pattern does not match
    // "Unknown" at all, which every ASIAIR log writes, so the field simply stays null.
    [Fact]
    public void TheAsiairCalTimestampOfUnknown_LeavesTheFieldNull()
    {
        var h = One(
            "RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Cal Dec = 31.7, " +
            "Last Cal Issue = None, Timestamp = Unknown");
        Near(7.5, h.RaGuideSpeed);
        Near(31.7, h.CalDecDeg);
        Assert.Null(h.CalTimestamp);
    }

    // Failure shape: AltDeg and AzDeg null while RotatorPos reads
    // "N/A, Alt = 43.7 deg, Az = 70.4 deg", which is what a non-greedy Rotator pos group gives.
    [Fact]
    public void TheFullDesktopPointingLine_FillsAllSevenFields()
    {
        var h = One(Phd2LogFixtures.DesktopPointingLine);
        Near(20.22, h.RaHr);
        Near(38.5, h.DecDeg);
        Near(-4.02, h.HourAngleHr);
        Assert.Equal("West", h.PierSide);
        Assert.Equal("N/A", h.RotatorPos);
        Near(43.7, h.AltDeg);
        Near(70.4, h.AzDeg);
    }

    // Failure shape: the whole line unrecognised, so DecDeg, HourAngleHr and PierSide are all
    // null. Nothing absent from the file is fabricated here.
    [Fact]
    public void TheAsiairPointingLine_WithoutRaAltAndAz_IsParsed()
    {
        var h = One(Phd2LogFixtures.AsiairPointingLine);
        Near(57.6, h.DecDeg);
        Near(-2.77, h.HourAngleHr);
        Assert.Equal("West", h.PierSide);
        Assert.Equal("N/A", h.RotatorPos);
        Assert.Null(h.RaHr);
        Assert.Null(h.AltDeg);
        Assert.Null(h.AzDeg);
    }

    // Failure shape: null, which means a truthiness test stands where a null test belongs.
    [Fact]
    public void ADecOfNegativeZero_IsParsedAsZeroAndNotAsNull()
    {
        var h = Fold(Phd2LogFixtures.CalHeaderLines);
        Assert.NotNull(h.DecDeg);
        Near(0.0, h.DecDeg);
    }

    [Fact]
    public void TheLockPositionLine_FillsBothPositionsAndTheHfd()
    {
        var h = One("Lock position = 77.407, 204.420, Star position = 77.407, 204.420, HFD = 1.91 px");
        Near(77.407, h.LockX);
        Near(204.420, h.LockY);
        Near(77.407, h.StarX);
        Near(204.420, h.StarY);
        Near(1.91, h.HfdPx);
    }

    // Failure shape: XAngleDeg filled from a line that does not carry it. The calibration Mount
    // line is shorter than the guiding one and its five sub-searches simply find nothing.
    [Fact]
    public void TheCalibrationHeaderVariant_LeavesTheGuidingOnlyFieldsNull()
    {
        var h = Fold(Phd2LogFixtures.CalHeaderLines);
        Assert.Equal("ASI Mount (ASCOM)", h.MountName);
        Assert.Null(h.XAngleDeg);
        Assert.Null(h.XRatePxS);
        Assert.Null(h.Parity);
        Near(7.5, h.RaGuideSpeed);
        Near(7.5, h.DecGuideSpeed);
        Assert.Null(h.LastCalIssue);
        Assert.Null(h.CalTimestamp);
        Near(2.37, h.HfdPx);
    }

    // Failure shape: true, which would make "Dither = both axes, ..." look like a recognised
    // line. PHD2 writes several header lines the parser deliberately ignores.
    [Fact]
    public void AnUnrecognisedHeaderLine_ReturnsFalseAndFillsNothing()
    {
        var header = new Phd2Header();
        Assert.False(Phd2LogParser.ApplyHeaderLine(header, "Search region = 15 px, Star mass tolerance = 50.0%"));
        Assert.False(Phd2LogParser.ApplyHeaderLine(header, Phd2LogFixtures.GuidingHeaderLines[1]));
        Assert.Equal(new Phd2Header(), header);

        Assert.True(Phd2LogParser.ApplyHeaderLine(header, "Exposure = 500 ms"));
    }

    // "RA Guide Speed = " and "RA = " both start with RA, and the first match wins, so the order
    // of the pattern list is load bearing. Failure shape: the guide-speed line read as a pointing
    // line, or the reverse.
    [Fact]
    public void TheGuideSpeedLine_IsNotConfusedWithThePointingLine()
    {
        var speeds = One("RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s");
        Assert.Null(speeds.RaHr);
        Near(7.5, speeds.RaGuideSpeed);

        var pointing = One(Phd2LogFixtures.DesktopPointingLine);
        Assert.Null(pointing.RaGuideSpeed);
        Near(20.22, pointing.RaHr);
    }

    // Spec 7.6: a numeric header field that does not parse is null rather than zero. "..."
    // satisfies the pattern's [0-9.]+ and then fails the number parser, which is the only way a
    // recognised header line can reach it. Failure shape: 0.0, which reads as a real measurement.
    [Fact]
    public void ANumericFieldThatDoesNotParse_ReadsAsNullRatherThanZero()
    {
        var h = One("Pixel scale = ... arc-sec/px, Binning = 1");
        Assert.Null(h.PixelScaleArcsec);
        Assert.Equal(1, h.Binning);
    }
}
