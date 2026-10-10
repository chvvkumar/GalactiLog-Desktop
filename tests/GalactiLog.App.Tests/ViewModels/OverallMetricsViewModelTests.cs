using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// The Integration tab's Overall metrics: target-wide means, All frames then one row per filter.
public class OverallMetricsViewModelTests
{
    [Fact]
    public void AllFrames_IsTheTargetsMeans()
    {
        var overall = new OverallMetricsViewModel(Factory.PopulatedTotals(), NightFilterMatrixViewModelTests.Swatches("Ha"));

        Assert.Equal(new MetricRowViewModel("All frames", "2.34", "0.42", "1.90", "0.45", "1,500"), overall.AllFrames);
    }

    [Fact]
    public void Filters_FollowTheColumnOrder_AndReadMeansByFilter()
    {
        // A failure is the rows in dictionary order instead of the bar order, or one filter's
        // figures on another's row.
        var totals = Factory.PopulatedTotals() with
        {
            MeansByFilter = new Dictionary<string, MetricMeans>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ha"] = new(2.1, 0.41, 1.8, 0.44, 1_400),
                ["OIII"] = new(2.6, 0.43, 2.0, 0.46, 1_600),
            },
        };

        var overall = new OverallMetricsViewModel(totals, NightFilterMatrixViewModelTests.Swatches("OIII", "Ha"));

        Assert.Equal(
            [
                new MetricRowViewModel("OIII", "2.60", "0.43", "2.00", "0.46", "1,600"),
                new MetricRowViewModel("Ha", "2.10", "0.41", "1.80", "0.44", "1,400"),
            ],
            overall.Filters);
    }

    [Fact]
    public void AMissingFigure_IsADash_AndAFilterWithNoEntryIsAllDashes()
    {
        var totals = Factory.PopulatedTotals() with
        {
            AvgFwhm = null,
            AvgDetectedStars = 0,
            MeansByFilter = new Dictionary<string, MetricMeans>(StringComparer.OrdinalIgnoreCase),
        };

        var overall = new OverallMetricsViewModel(totals, NightFilterMatrixViewModelTests.Swatches("SII"));

        Assert.Equal("-", overall.AllFrames.FwhmText);
        Assert.Equal("0", overall.AllFrames.DetectedStarsText);
        Assert.Equal(new MetricRowViewModel("SII", "-", "-", "-", "-", "-"), Assert.Single(overall.Filters));
    }
}
