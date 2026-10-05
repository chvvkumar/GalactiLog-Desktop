using GalactiLog.Core.Metrics;
using Xunit;

namespace GalactiLog.Core.Tests.Metrics;

public class StatisticsTests
{
    [Fact]
    public void Median_OddCount_ReturnsTheMiddleValue()
    {
        Assert.Equal(3d, Statistics.Median([5d, 1d, 3d, 9d, 2d]));
    }

    [Fact]
    public void Median_EvenCount_AveragesTheTwoMiddleValues()
    {
        // Python's statistics.median, which the web application uses, averages; taking either
        // middle value would put this at 2 or 5 and disagree with every figure the web
        // application shows.
        Assert.Equal(3.5d, Statistics.Median([1d, 2d, 5d, 9d]));
    }

    [Fact]
    public void Median_IgnoresNulls()
    {
        Assert.Equal(3d, Statistics.Median([null, 5d, null, 1d, 3d, null, 9d, 2d]));
    }

    [Fact]
    public void Median_AllNullOrEmpty_ReturnsNull()
    {
        // An empty collection expression converts to either overload with equal preference, which
        // is the one ambiguity the new non-nullable overload introduces (records-report.md).
        Assert.Null(Statistics.Median(Array.Empty<double?>()));
        Assert.Null(Statistics.Median([null, null, null]));
    }

    [Fact]
    public void Median_DoesNotMutateTheInput()
    {
        var values = new List<double?> { 5d, 1d, 3d };

        Assert.Equal(3d, Statistics.Median(values));

        Assert.Equal([5d, 1d, 3d], values);
    }

    // ---- The non-nullable Median(IEnumerable<double>) overload, phase 17 core-shapes.md section
    // 9.2: one algorithm behind both overloads, so the non-nullable one is proved to agree with the
    // nullable one on the shapes that matter rather than assumed from sharing code. Every case here
    // was a compile error before the overload existed, which is how it was seen red.

    [Fact]
    public void Median_NonNullable_OddCount_AgreesWithTheNullableOverload()
    {
        double[] values = [5d, 1d, 3d, 9d, 2d];

        Assert.Equal(Statistics.Median(values.Select(v => (double?)v)), Statistics.Median(values));
        Assert.Equal(3d, Statistics.Median(values));
    }

    [Fact]
    public void Median_NonNullable_EvenCount_AgreesWithTheNullableOverload()
    {
        double[] values = [1d, 2d, 5d, 9d];

        Assert.Equal(Statistics.Median(values.Select(v => (double?)v)), Statistics.Median(values));
        Assert.Equal(3.5d, Statistics.Median(values));
    }

    [Fact]
    public void Median_NonNullable_SingleValue_AgreesWithTheNullableOverload()
    {
        double[] values = [7d];

        Assert.Equal(Statistics.Median(values.Select(v => (double?)v)), Statistics.Median(values));
        Assert.Equal(7d, Statistics.Median(values));
    }

    [Fact]
    public void Median_NonNullable_Empty_AgreesWithTheNullableOverload()
    {
        Assert.Equal(Statistics.Median(Array.Empty<double?>()), Statistics.Median(Array.Empty<double>()));
        Assert.Null(Statistics.Median(Array.Empty<double>()));
    }

    [Fact]
    public void Median_NonNullable_Duplicates_AgreesWithTheNullableOverload()
    {
        double[] values = [2d, 2d, 2d, 5d];

        Assert.Equal(Statistics.Median(values.Select(v => (double?)v)), Statistics.Median(values));
        Assert.Equal(2d, Statistics.Median(values));
    }

    [Fact]
    public void Mean_IgnoresNulls()
    {
        Assert.Equal(4d, Statistics.Mean([null, 2d, null, 6d]));
    }

    [Fact]
    public void Mean_Empty_ReturnsNull()
    {
        Assert.Null(Statistics.Mean([]));
        Assert.Null(Statistics.Mean([null, null]));
    }
}
