using GalactiLog.Core.Metrics;
using Xunit;

namespace GalactiLog.Core.Tests.Metrics;

/// <summary>
/// Spec 12.4's two band ladders and the row score between them, ported from
/// <c>frontend/src/utils/frameQuality.ts</c> with its literals kept exactly. A separate file from
/// <see cref="FrameQualityTests"/>: those 18 cases pin <c>MadZ</c>, <c>GroupKey</c>,
/// <c>GroupBaselines</c> and <c>CountOutliers</c>, none of which this phase touches.
/// </summary>
public class FrameQualityBandTests
{
    [Theory]
    [InlineData(null, QualityBand.Neutral)]
    [InlineData(-9.0d, QualityBand.Better)]
    [InlineData(-1.5d, QualityBand.Better)]
    [InlineData(-0.999d, QualityBand.Neutral)]
    [InlineData(0d, QualityBand.Neutral)]
    [InlineData(1.49d, QualityBand.Neutral)]
    [InlineData(1.5d, QualityBand.Watch)]
    [InlineData(2.99d, QualityBand.Watch)]
    [InlineData(3.0d, QualityBand.Reject)]
    [InlineData(12.0d, QualityBand.Reject)]
    public void BandForZ_MatchesTheWebsLadder(double? z, QualityBand expected)
        => Assert.Equal(expected, FrameQuality.BandForZ(z));

    /// <summary>Spec 12.4's band table gives neutral to "every ungraded cell", so a null deviation
    /// is neutral and draws no mark rather than being absent.</summary>
    [Fact]
    public void BandForZ_Null_IsNeutral()
        => Assert.Equal(QualityBand.Neutral, FrameQuality.BandForZ(null));

    /// <summary>"At or below -1.0" means the boundary itself is better.</summary>
    [Fact]
    public void BandForZ_ExactlyMinusOne_IsBetter()
        => Assert.Equal(QualityBand.Better, FrameQuality.BandForZ(-1.0d));

    /// <summary>"At or above 1.5" means the boundary itself is watch, not neutral.</summary>
    [Fact]
    public void BandForZ_ExactlyOnePointFive_IsWatch()
        => Assert.Equal(QualityBand.Watch, FrameQuality.BandForZ(1.5d));

    /// <summary>"At or above 3.0" means the boundary itself is reject, which is also
    /// <see cref="FrameQuality.ZReject"/>.</summary>
    [Fact]
    public void BandForZ_ExactlyThree_IsReject()
    {
        Assert.Equal(QualityBand.Reject, FrameQuality.BandForZ(3.0d));
        Assert.Equal(QualityBand.Reject, FrameQuality.BandForZ(FrameQuality.ZReject));
    }

    // 0.5 signal, 0.25 sharpness, 0.25 roundness. Every expectation below is computed from the
    // literal 16.7 the web uses, by hand, so a change to the constant fails here rather than
    // moving with it.
    [Theory]
    [InlineData(0d, 0d, 0d, 50d)]
    [InlineData(1d, 1d, 1d, 50d - 16.7d)]
    [InlineData(-1d, -1d, -1d, 50d + 16.7d)]
    [InlineData(2d, 0d, 0d, 50d - (16.7d * 1.0d))]
    [InlineData(0d, 2d, 2d, 50d - (16.7d * 1.0d))]
    [InlineData(1d, -1d, -1d, 50d)]
    public void CombinedScore_WeightsTheThreeAxes(double s, double sh, double r, double expected)
        => Assert.Equal(expected, FrameQuality.CombinedScore(s, sh, r)!.Value, 9);

    /// <summary>An axis whose deviation is null is dropped and the surviving weights are
    /// renormalised by their own sum, so two equal deviations give the same score whichever of the
    /// three is missing.</summary>
    [Fact]
    public void CombinedScore_ANullAxis_RenormalisesTheRest()
    {
        // Sharpness and roundness alone: 0.25 and 0.25 renormalise to 0.5 and 0.5, so two 2s are
        // a mean of 2 and not of 1.
        Assert.Equal(50d - (16.7d * 2d), FrameQuality.CombinedScore(null, 2d, 2d)!.Value, 9);

        // Signal alone: its own weight renormalises to 1.
        Assert.Equal(50d - (16.7d * 2d), FrameQuality.CombinedScore(2d, null, null)!.Value, 9);

        // Signal and sharpness: 0.5 and 0.25 renormalise to two thirds and one third.
        Assert.Equal(
            50d - (16.7d * (((0.5d * 3d) + (0.25d * 0d)) / 0.75d)),
            FrameQuality.CombinedScore(3d, 0d, null)!.Value,
            9);
    }

    [Fact]
    public void CombinedScore_AllNull_IsNull()
        => Assert.Null(FrameQuality.CombinedScore(null, null, null));

    [Fact]
    public void CombinedScore_ClampsAtPlusAndMinusThree()
    {
        // A weighted mean of 40 clamps to 3; one of -40 clamps to -3.
        Assert.Equal(
            FrameQuality.CombinedScore(3d, 3d, 3d)!.Value,
            FrameQuality.CombinedScore(40d, 40d, 40d)!.Value,
            9);
        Assert.Equal(
            FrameQuality.CombinedScore(-3d, -3d, -3d)!.Value,
            FrameQuality.CombinedScore(-40d, -40d, -40d)!.Value,
            9);
    }

    /// <summary>The constant is the literal 16.7 and not <c>100.0 / 6.0</c>. A clamped -3 maps to
    /// 100.1 and a clamped 3 to -0.1, which the exact fraction does not produce, and at the edges
    /// the difference is enough to move a score across a band boundary. This case is what catches
    /// a well-meaning tidy-up.</summary>
    [Fact]
    public void CombinedScore_UsesTheLiteralSixteenPointSeven()
    {
        Assert.Equal(100.1d, FrameQuality.CombinedScore(-3d, -3d, -3d)!.Value, 9);
        Assert.Equal(-0.1d, FrameQuality.CombinedScore(3d, 3d, 3d)!.Value, 9);

        // And the fraction would not: 100.0 / 6.0 is 16.666..., which gives exactly 100 and 0.
        Assert.NotEqual(100d, FrameQuality.CombinedScore(-3d, -3d, -3d)!.Value, 9);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(100d, QualityBand.Better)]
    [InlineData(60d, QualityBand.Better)]
    [InlineData(59.9d, QualityBand.Neutral)]
    [InlineData(45d, QualityBand.Neutral)]
    [InlineData(44.9d, QualityBand.Watch)]
    [InlineData(30d, QualityBand.Watch)]
    [InlineData(29.9d, QualityBand.Reject)]
    [InlineData(0d, QualityBand.Reject)]
    public void BandForScore_MatchesTheScoreLadder(double? score, QualityBand? expected)
        => Assert.Equal(expected, FrameQuality.BandForScore(score));

    /// <summary>The score ladder and the deviation ladder share four band names and nothing else.
    /// A score of 1.4 is a reject frame; a deviation of 1.4 is a neutral cell. An implementer who
    /// applies the wrong ladder fails here.</summary>
    [Fact]
    public void BandForScore_IsNotTheZLadder()
    {
        Assert.Equal(QualityBand.Reject, FrameQuality.BandForScore(1.4d));
        Assert.Equal(QualityBand.Neutral, FrameQuality.BandForZ(1.4d));

        Assert.Equal(QualityBand.Better, FrameQuality.BandForScore(61d));
        Assert.Equal(QualityBand.Reject, FrameQuality.BandForZ(61d));

        // And a score of exactly 50, the score of a frame that sits on its baseline, is neutral on
        // one ladder while a deviation of 50 is a reject on the other.
        Assert.Equal(QualityBand.Neutral, FrameQuality.BandForScore(50d));
        Assert.Equal(QualityBand.Reject, FrameQuality.BandForZ(50d));
    }
}
