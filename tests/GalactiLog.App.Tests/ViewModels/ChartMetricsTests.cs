using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 13's fixed metric-to-token mapping, which spec 14.5 forbids reassigning per chart.
public class ChartMetricsTests
{
    [Fact]
    public void All_HasTheFiveSpecifiedSeries_InOrder()
        => Assert.Equal(
            ["hfr", "eccentricity", "fwhm", "guiding_rms", "detected_stars"],
            ChartMetrics.All.Select(metric => metric.Key));

    // Roadmap row 7's first Verify clause: the series colour for each metric key matches its
    // metric-* token. Asserted against the resolved value in the shipped dictionary, not against a
    // hex literal, so a token edit that forgets a chart shows up here.
    [AvaloniaTheory]
    [InlineData("hfr", "ColorMetricHfr")]
    [InlineData("eccentricity", "ColorMetricEccentricity")]
    [InlineData("fwhm", "ColorMetricFwhm")]
    [InlineData("guiding_rms", "ColorMetricGuiding")]
    [InlineData("detected_stars", "ColorMetricStars")]
    public void EachMetricUsesItsSpecifiedToken(string key, string tokenKey)
    {
        var metric = ChartMetrics.ByKey(key);
        Assert.NotNull(metric);
        Assert.Equal(tokenKey, metric.TokenKey);

        ChartTheme.Apply();

        Assert.True(Application.Current!.TryFindResource(tokenKey, out var value));
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        var expected = new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);

        Assert.Equal(expected, ChartTheme.Palette.Metrics[metric.TokenKey]);
    }

    // Ruling Q16: this document's key for detected stars is detected_stars, not the dashboard's
    // stars. A mismatch would silently drop a metric that the settings document says is enabled.
    [Fact]
    public void Keys_MatchTheGraphSettingsDefaults()
    {
        var defaults = new GraphSettings().EnabledMetrics;

        Assert.Equal(["hfr", "eccentricity", "fwhm", "guiding_rms"], defaults);
        Assert.All(defaults, key => Assert.NotNull(ChartMetrics.ByKey(key)));
    }

    // Spec 13's two Target detail chart rows name the column each series reads. Task 8 builds its
    // accessors from these, so the column contract is asserted here rather than in two chart
    // view-models.
    [Fact]
    public void Columns_MatchSpecThirteen()
        => Assert.Equal(
            ["median_hfr", "eccentricity", "fwhm", "guiding_rms_arcsec", "detected_stars"],
            ChartMetrics.All.Select(metric => metric.Column));

    // Spec 13: the FWHM series reads "fwhm (never median_fwhm, see 7.1.1)", and spec 7.1.1 says
    // the header FWHM is "never charted". No entry may name it, under any key.
    [Fact]
    public void NoMetricReadsMedianFwhm()
    {
        Assert.DoesNotContain("median_fwhm", ChartMetrics.All.Select(metric => metric.Column));
        Assert.Equal("fwhm", ChartMetrics.ByKey("fwhm")!.Column);
    }

    // Review finding 1: the axis side is not a per-metric constant. Unit is what decides it, at
    // series-build time, from the current selection. Asserted here so a later edit cannot quietly
    // reintroduce a pinned side: the two arcsec metrics carry the same Unit, which is the whole
    // basis of the ruling's rule.
    [Fact]
    public void Units_AreTheAxisGroupingKey()
    {
        Assert.Equal(
            [" px", "", " arcsec", " arcsec", " count"],
            ChartMetrics.All.Select(metric => metric.Unit));

        // Review ruling on Task 8's axis-grouping escalation: detected stars is its own unit
        // group, so a star count never shares an axis group with a dimensionless eccentricity.
        Assert.NotEqual(
            ChartMetrics.ByKey("eccentricity")!.Unit,
            ChartMetrics.ByKey("detected_stars")!.Unit);
        Assert.DoesNotContain(
            "Axis",
            typeof(ChartMetric).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void ByKey_UnknownKey_ReturnsNull()
    {
        Assert.Null(ChartMetrics.ByKey("median_fwhm"));
        // The dashboard's spelling. Two namespaces, one of which this table does not answer to.
        Assert.Null(ChartMetrics.ByKey("stars"));
        Assert.Null(ChartMetrics.ByKey("HFR"));
    }
}
