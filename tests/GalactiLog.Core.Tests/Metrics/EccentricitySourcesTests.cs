using GalactiLog.Core.Metrics;
using Xunit;

namespace GalactiLog.Core.Tests.Metrics;

/// <summary>
/// Spec 7.2's pooling rule, extracted from <c>TargetDetailQuery</c> when
/// <c>SessionDetailQuery</c> became its second caller. These assertions pin the tie-break, which
/// is the half a second implementation would have got wrong.
/// </summary>
public class EccentricitySourcesTests
{
    [Fact]
    public void TryGetModalSource_NoFrameCarriesAnEccentricity_IsFalse()
    {
        Assert.False(EccentricitySources.TryGetModalSource(
            [("header", null), (null, null)],
            out var modal));
        Assert.Null(modal);
    }

    [Fact]
    public void TryGetModalSource_ReturnsTheMostCommonSource()
    {
        Assert.True(EccentricitySources.TryGetModalSource(
            [("header", 0.3), ("header", 0.3), ("csv", 0.4)],
            out var modal));
        Assert.Equal("header", modal);
    }

    [Fact]
    public void TryGetModalSource_IgnoresFramesWithNoValue()
    {
        // A frame with a source but no eccentricity is not a vote: it measured nothing.
        Assert.True(EccentricitySources.TryGetModalSource(
            [("csv", null), ("csv", null), ("csv", null), ("header", 0.3)],
            out var modal));
        Assert.Equal("header", modal);
    }

    [Fact]
    public void TryGetModalSource_NullSourceCanWinTheVote()
    {
        // A null modal source is a real outcome, which is why the method returns a bool rather
        // than using null for "nothing to pool". The card needs a "source not recorded" wording.
        Assert.True(EccentricitySources.TryGetModalSource(
            [(null, 0.3), (null, 0.3), ("header", 0.4)],
            out var modal));
        Assert.Null(modal);
    }

    [Fact]
    public void TryGetModalSource_TieBreaksNullLastThenOrdinal()
    {
        // Same count, named beats null.
        Assert.True(EccentricitySources.TryGetModalSource(
            [(null, 0.3), ("header", 0.4)],
            out var namedWins));
        Assert.Equal("header", namedWins);

        // Same count among named sources: the lexicographically smallest wins, in both insertion
        // orders, so the answer does not depend on dictionary iteration order.
        Assert.True(EccentricitySources.TryGetModalSource(
            [("header", 0.3), ("csv", 0.4)],
            out var forward));
        Assert.Equal("csv", forward);

        Assert.True(EccentricitySources.TryGetModalSource(
            [("csv", 0.4), ("header", 0.3)],
            out var backward));
        Assert.Equal("csv", backward);
    }

    [Fact]
    public void IsPooled_RequiresBothAValueAndTheModalSource()
    {
        Assert.True(EccentricitySources.IsPooled("header", 0.3, "header"));
        Assert.False(EccentricitySources.IsPooled("csv", 0.3, "header"));
        Assert.False(EccentricitySources.IsPooled("header", null, "header"));
        Assert.True(EccentricitySources.IsPooled(null, 0.3, null));
        Assert.False(EccentricitySources.IsPooled(null, 0.3, "header"));
    }
}
