using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

// Reference values captured from a real PostgreSQL 16 + pg_trgm 1.6 instance
// (see scratchpad/phase3/pg-trgm-captured.md). Do not hand-derive these numbers from the
// port's own algorithm -- that would make the test circular.
public class TrigramTests
{
    // xunit 2.x's double overload takes a decimal-digit precision, not an absolute tolerance;
    // 6 digits is the "tight tolerance (e.g. 1e-6)" the brief asks for.
    private const int Precision = 6;

    [Fact]
    public void Similarity_IdenticalStrings_ReturnsOne()
    {
        Assert.Equal(1.0, Trigram.Similarity("NGC 7000", "NGC 7000"), Precision);
    }

    [Fact]
    public void Similarity_TotallyDifferentStrings_ReturnsZero()
    {
        Assert.Equal(0.0, Trigram.Similarity("abc", "xyz"), Precision);
    }

    [Theory]
    [InlineData("ngc 7000", "ngc 7000 north america nebula", 0.32142857)]
    // "m31" as one word shares no cross-space trigram with "m 31" as two words -- this pair
    // is the captured proof of the word-level padding rule (pg_trgm pads each word
    // separately, so no 3-gram ever spans the space).
    [InlineData("m31", "m 31", 0.2857143)]
    [InlineData("horsehead nebula", "horse head nebula", 0.7894737)]
    [InlineData("andromeda galaxy", "andromeda", 0.5882353)]
    [InlineData("ngc 891", "ngc 892", 0.6)]
    [InlineData("m 42", "m 43", 0.42857143)]
    [InlineData("orion nebula", "orion", 0.46153846)]
    [InlineData("ic 1396", "ic1396", 0.5)]
    [InlineData("a", "a", 1.0)]
    public void Similarity_MatchesCapturedPgTrgmValues(string a, string b, double expected)
    {
        Assert.Equal(expected, Trigram.Similarity(a, b), Precision);
    }

    [Theory]
    // pg_trgm splits words on ANY run of non-alphanumeric characters, not only whitespace --
    // hyphen, period, plus, and underscore all delimit. Each pair below produces identical
    // word sequences under punctuation-splitting, so pg_trgm reports 1.0.
    [InlineData("sh2-155", "sh2 155", 1.0)]
    [InlineData("vdb-142", "vdb 142", 1.0)]
    [InlineData("snr g357.7+00.3", "snr g357.7 00.3", 1.0)]
    [InlineData("ngc_7000", "ngc 7000", 1.0)]
    [InlineData("ic 1396a", "ic 1396", 0.7)]
    public void Similarity_MatchesCapturedPunctuationSplitValues(string a, string b, double expected)
    {
        Assert.Equal(expected, Trigram.Similarity(a, b), Precision);
    }

    [Fact]
    public void Similarity_EmptyStrings_ReturnsZero()
    {
        // Captured pg_trgm value for (empty, empty) is also 0; the union.Count == 0 branch
        // matches it, though no real target name ever exercises this case.
        Assert.Equal(0.0, Trigram.Similarity("", ""), Precision);
    }

    [Fact]
    public void Similarity_ThresholdBoundary_JustAboveIsGreaterThanPointFour()
    {
        // m 42 vs m 43 = 0.42857143, just above the 0.4 duplicate-detection threshold.
        Assert.True(Trigram.Similarity("m 42", "m 43") > 0.4);
    }

    [Fact]
    public void Similarity_ThresholdBoundary_JustBelowIsNotGreaterThanPointFour()
    {
        // ngc 7000 vs ngc 7000 north america nebula = 0.32142857, just below the threshold.
        Assert.False(Trigram.Similarity("ngc 7000", "ngc 7000 north america nebula") > 0.4);
    }
}
