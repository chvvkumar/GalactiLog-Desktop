using GalactiLog.Core.Mosaics;
using Xunit;

namespace GalactiLog.Core.Tests.Mosaics;

// Phase 19A Task 2. Spec 11.4's best-frame score, the port of the web's score_frames.
public class FrameScoreTests
{
    private const double Tolerance = 1e-9;

    private static FrameMetrics M(int? stars = null, double? hfr = null, double? ecc = null, double? rms = null, double? fwhm = null)
        => new(stars, hfr, ecc, rms, fwhm);

    [Fact]
    public void BestOnEveryMetric_ScoresHighest_WorstScoresZero()
    {
        var scores = FrameScore.Score([
            M(200, 2.0, 0.4, 0.8, 3.0),
            M(300, 1.5, 0.3, 0.5, 2.0),
            M(100, 3.0, 0.6, 1.2, 4.0),
        ]);

        Assert.Equal(1.0, scores[1], Tolerance);
        Assert.Equal(0.0, scores[2], Tolerance);
        Assert.True(scores[1] > scores[0] && scores[0] > scores[2]);
    }

    // Stars present on two frames; the third has none (null, then zero), so it takes 0.5 on stars.
    // Every other metric has no value anywhere and gives 0.5 to all.
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void MissingOrZeroValue_TakesHalfOnThatMetric(int? third)
    {
        var scores = FrameScore.Score([M(stars: 100), M(stars: 200), M(stars: third)]);

        Assert.Equal(0.325, scores[0], Tolerance);
        Assert.Equal(0.675, scores[1], Tolerance);
        Assert.Equal(0.5, scores[2], Tolerance);
    }

    [Fact]
    public void EqualValues_ScoreZeroOnStars_AndOneElsewhere()
    {
        var scores = FrameScore.Score([M(5, 5, 5, 5, 5), M(5, 5, 5, 5, 5)]);

        Assert.All(scores, score => Assert.Equal(0.65, score, Tolerance));
    }

    [Fact]
    public void FewerThanTwoPresentValues_GiveHalfToEveryFrame()
    {
        var scores = FrameScore.Score([M(300, 1.5, 0.3, 0.5, 2.0), M(), M()]);

        Assert.All(scores, score => Assert.Equal(0.5, score, Tolerance));
    }

    [Fact]
    public void PoolOfOne_ScoresOne_AndEmptyPoolIsEmpty()
    {
        Assert.Equal([1.0], FrameScore.Score([M()]));
        Assert.Empty(FrameScore.Score([]));
    }

    // Hand computed: stars 0, 1/3, 1; HFR 1, 0, 0.5; eccentricity 0, 1, 0.5; RMS 0, 0.5 (null),
    // 1; FWHM 1, 0, 0.5 (zero). Weighted 0.35, 0.30, 0.15, 0.12, 0.08.
    [Fact]
    public void WeightedSum_MatchesTheHandComputedCase()
    {
        var scores = FrameScore.Score([
            M(100, 2.0, 0.5, 1.0, 3.0),
            M(200, 3.0, 0.3, null, 4.0),
            M(400, 2.5, 0.4, 0.5, 0),
        ]);

        Assert.Equal(0.38, scores[0], Tolerance);
        Assert.Equal(0.35 / 3 + 0.15 + 0.06, scores[1], Tolerance);
        Assert.Equal(0.735, scores[2], Tolerance);
    }
}
