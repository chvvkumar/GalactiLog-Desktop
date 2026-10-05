using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 13's "Per-session frame metrics". Assertions are against the Series, XAxes, YAxes and
// Sections objects the view-model built, never against rendered output (spec 18.3).
public class SessionChartViewModelTests
{
    private sealed class Fixture : IDisposable
    {
        public GraphSettings Document;

        public int Saves;

        public readonly ChartSelectionViewModel Selection;

        public Fixture(
            string[]? metrics = null,
            string[]? filters = null,
            Dictionary<string, FilterSetting>? configuredFilters = null)
        {
            Document = new GraphSettings
            {
                EnabledMetrics = metrics ?? ["hfr"],
                EnabledFilters = filters ?? ["overall"],
            };

            var writer = new GraphSettingsWriter(
                () => Document,
                value =>
                {
                    Document = value;
                    Saves++;
                });

            Selection = new ChartSelectionViewModel(
                Document,
                writer,
                () => new AliasMap(configuredFilters ?? [], new EquipmentSettings()));
        }

        public SessionChartViewModel? Chart { get; private set; }

        public SessionChartViewModel Build(SessionDetail detail)
            => Chart = new SessionChartViewModel(detail, Selection);

        public void Dispose() => Chart?.Dispose();
    }

    // One frame per point, in capture order. Only the fields the five chartable metrics read are
    // set; every other column is null, which is what most real frames look like.
    private static FrameRow Frame(
        int index,
        double? hfr = null,
        double? eccentricity = null,
        double? fwhm = null,
        double? guidingRms = null,
        int? stars = null,
        string? filter = "Ha") => new(
        ImageId: Guid.NewGuid(),
        FilePath: $@"C:\Astro\M 31\frame_{index:0000}.fits",
        FileName: $"frame_{index:0000}.fits",
        CaptureDate: new DateTime(2025, 12, 7, 21, 0, 0, DateTimeKind.Utc).AddMinutes(index * 5),
        FilterUsed: filter,
        ExposureTime: 300d,
        MedianHfr: hfr,
        Eccentricity: eccentricity,
        Fwhm: fwhm,
        DetectedStars: stars,
        GuidingRmsArcsec: guidingRms,
        GuidingRmsRaArcsec: null,
        GuidingRmsDecArcsec: null,
        GuidingRmsSource: null,
        AduMean: null,
        AduMedian: null,
        AduStdev: null,
        AduMin: null,
        AduMax: null,
        FocuserPosition: null,
        FocuserTemp: null,
        AmbientTemp: null,
        DewPoint: null,
        Humidity: null,
        Pressure: null,
        WindSpeed: null,
        WindDirection: null,
        WindGust: null,
        CloudCover: null,
        SkyQuality: null,
        Airmass: null,
        PierSide: null,
        RotatorPosition: null,
        SensorTemp: null,
        CameraGain: 100,
        Rig: "RC8 / ASI2600MM",
        IsHfrOutlier: false,
        IsEccentricityOutlier: false);

    private static SessionDetail Detail(
        IReadOnlyList<FrameRow> frames,
        MetricRangeSummary? hfr = null,
        MetricRangeSummary? eccentricity = null,
        MetricRangeSummary? fwhm = null,
        MetricRangeSummary? guidingRms = null)
        => Cards.PopulatedDetail() with
        {
            Frames = frames,
            Hfr = hfr ?? new MetricRangeSummary(null, null, null),
            Eccentricity = eccentricity ?? new MetricRangeSummary(null, null, null),
            Fwhm = fwhm ?? new MetricRangeSummary(null, null, null),
            GuidingRmsArcsec = guidingRms ?? new MetricRangeSummary(null, null, null),
        };

    // ---- Phase 25 R6: the chart on a merged card. ----------------------------------------------
    //
    // Three frames on 2025-12-07 from 21:00 and two on 2025-12-09 from 22:00, each night one hour
    // of domain, so the stitched boundary sits at one half and carries "12-09".

    private static FrameRow On(int day, int minute, double hfr)
        => Frame(0, hfr: hfr) with { CaptureDate = new DateTime(2025, 12, day, day == 7 ? 21 : 22, minute, 0, DateTimeKind.Utc) };

    private static (SessionDetail Detail, NightStripViewModel Strip) MergedNight()
    {
        FrameRow[] first = [On(7, 0, 2.0d), On(7, 5, 2.1d), On(7, 10, 2.2d)];
        FrameRow[] second = [On(9, 0, 2.3d), On(9, 5, 2.4d)];
        var detail = Detail([.. first, .. second]) with
        {
            Nights = [new NightSpan(new DateOnly(2025, 12, 7), 0, 3), new NightSpan(new DateOnly(2025, 12, 9), 3, 2)],
        };
        var inks = new Dictionary<string, Avalonia.Media.Immutable.ImmutableSolidColorBrush>();
        var fallback = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.Gray);
        var strip = NightStripViewModel.Stitched(
        [
            (new DateOnly(2025, 12, 7), new NightStripViewModel(first, inks, fallback, null, TimeZoneInfo.Utc, true), 0),
            (new DateOnly(2025, 12, 9), new NightStripViewModel(second, inks, fallback, null, TimeZoneInfo.Utc, true), 3),
        ]);
        return (detail, strip);
    }

    // A failure is a line drawn from the first night's last frame to the second night's first,
    // or a boundary with no date on it.
    [Fact]
    public void OnAMergedNight_TheSeriesBreaksAtTheBoundary_AndASectionCarriesTheDate()
    {
        using var fixture = new Fixture();
        var (detail, strip) = MergedNight();
        var chart = fixture.Build(detail);

        chart.UseLaneAxis(strip.LaneAxis, TimeZoneInfo.Utc, strip);

        Assert.Equal([2.0d, 2.1d, 2.2d, null, 2.3d, 2.4d], Values(chart, 0));

        var boundary = Assert.Single(chart.Sections, section => section.Label == "12-09");
        Assert.Equal(0.5d, boundary.Xi!.Value, 9);
        Assert.Equal(0.5d, boundary.Xj!.Value, 9);
        Assert.IsType<LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect>(Assert.IsType<SolidColorPaint>(boundary.Stroke).PathEffect);
        Assert.NotNull(boundary.LabelPaint);

        var first = Assert.Single(chart.Sections, section => section.Label == "12-07");
        Assert.Equal(0d, first.Xi!.Value, 9);
        Assert.Null(first.Stroke);
    }

    // A failure is a null slot or a boundary section appearing on an ordinary night.
    [Fact]
    public void OnASingleNight_TheSeriesShapeIsUnchanged_AndNoBoundarySectionIsDrawn()
    {
        using var fixture = new Fixture();
        FrameRow[] frames = [On(7, 0, 2.0d), On(7, 5, 2.1d), On(7, 10, 2.2d)];
        var strip = new NightStripViewModel(
            frames,
            new Dictionary<string, Avalonia.Media.Immutable.ImmutableSolidColorBrush>(),
            new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.Gray),
            null,
            TimeZoneInfo.Utc,
            true);
        var chart = fixture.Build(Detail(frames));

        chart.UseLaneAxis(strip.LaneAxis, TimeZoneInfo.Utc, strip);

        Assert.Equal([2.0d, 2.1d, 2.2d], Values(chart, 0));
        Assert.DoesNotContain(chart.Sections, section => section.Label is { } label && label.StartsWith("12-", StringComparison.Ordinal));
    }

    private static LineSeries<double?> Line(MetricChartViewModel chart, int index)
        => Assert.IsType<LineSeries<double?>>(chart.Series[index]);

    private static double?[] Values(MetricChartViewModel chart, int index)
        => [.. Line(chart, index).Values!];

    private static SKColor Stroke(MetricChartViewModel chart, int index)
        => Assert.IsType<SolidColorPaint>(Line(chart, index).Stroke).Color;

    private static SKColor Token(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
    }

    [AvaloniaFact]
    public async Task Construction_OffTheUiThread_DoesNotThrow()
    {
        // Fix pass 1. Avalonia's SolidColorBrush is an AvaloniaObject and its constructor calls
        // Dispatcher.VerifyAccess, so the metric pill tints threw "Call from invalid thread"
        // whenever a chart was constructed anywhere but the UI thread with a dispatcher present.
        // The card does publish through a post seam in production, but the tints are immutable
        // brushes now, the way Phase 5's filter tints already were.
        ChartTheme.Apply();
        using var fixture = new Fixture();
        var detail = Detail(
            [Frame(1, hfr: 2.1d)],
            hfr: new MetricRangeSummary(2.1d, 2.1d, 2.1d));

        using var chart = await Task.Run(() => new SessionChartViewModel(detail, fixture.Selection));

        Assert.Equal(5, chart.MetricPills.Count);
        Assert.All(chart.MetricPills, pill => Assert.NotNull(pill.Tint));
        Assert.Single(chart.Series);
    }

    [Fact]
    public void Series_OnePerEnabledMetric_OverFrameIndex()
    {
        using var fixture = new Fixture(metrics: ["hfr", "fwhm"]);
        var chart = fixture.Build(Detail(
            [
                Frame(1, hfr: 2.1d, fwhm: 1.8d),
                Frame(2, hfr: 2.3d, fwhm: 1.9d),
            ],
            hfr: new MetricRangeSummary(2.1d, 2.3d, 2.2d),
            fwhm: new MetricRangeSummary(1.8d, 1.9d, 1.85d)));

        Assert.Equal(["HFR (px)", "FWHM"], chart.Series.Select(series => series.Name));
        Assert.Equal(2, Values(chart, 0).Length);
    }

    [Fact]
    public void Series_ValuesAreOnePerFrame_InCaptureOrder()
    {
        using var fixture = new Fixture();
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d), Frame(2, hfr: 2.5d), Frame(3, hfr: 2.2d)],
            hfr: new MetricRangeSummary(2.1d, 2.5d, 2.2d)));

        // The query already returns Detail.Frames in capture order (Task 2 handoff), so the chart
        // reorders nothing: the assertion is that it does not.
        Assert.Equal([2.1d, 2.5d, 2.2d], Values(chart, 0));
    }

    [AvaloniaFact]
    public void Series_Colour_MatchesTheMetricToken()
    {
        ChartTheme.Apply();
        using var fixture = new Fixture(metrics: ["hfr", "detected_stars"]);
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d, stars: 1400), Frame(2, hfr: 2.3d, stars: 1500)],
            hfr: new MetricRangeSummary(2.1d, 2.3d, 2.2d)));

        Assert.Equal(Token("ColorMetricHfr"), Stroke(chart, 0));
        Assert.Equal(Token("ColorMetricStars"), Stroke(chart, 1));
    }

    [Fact]
    public void MissingFrameValue_ProducesAGap()
    {
        using var fixture = new Fixture();
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d), Frame(2), Frame(3, hfr: 2.2d)],
            hfr: new MetricRangeSummary(2.1d, 2.2d, 2.15d)));

        Assert.Equal([2.1d, null, 2.2d], Values(chart, 0));
        Assert.True(Line(chart, 0).EnableNullSplitting);
    }

    [Fact]
    public void XAxis_IsLinearFrameIndex_OneBased()
    {
        using var fixture = new Fixture();
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d), Frame(2, hfr: 2.2d)],
            hfr: new MetricRangeSummary(2.1d, 2.2d, 2.15d)));

        var axis = Assert.IsType<Axis>(Assert.Single(chart.XAxes));

        // Linear, not ordinal: no Labels list, and the labeler is what makes the first frame
        // read as 1 rather than 0 (spec 13's "frame index, linear").
        Assert.Null(axis.Labels);
        Assert.Equal("1", axis.Labeler(0d));
        Assert.Equal("2", axis.Labeler(1d));
        Assert.Equal(1d, axis.MinStep);
    }

    [Fact]
    public void YAxes_LeftAndRight_MatchChartMetricsAxisSides()
    {
        using var fixture = new Fixture(metrics: ["hfr", "fwhm", "guiding_rms"]);
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d, fwhm: 1.8d, guidingRms: 0.4d)],
            hfr: new MetricRangeSummary(2.1d, 2.1d, 2.1d),
            fwhm: new MetricRangeSummary(1.8d, 1.8d, 1.8d),
            guidingRms: new MetricRangeSummary(0.4d, 0.4d, 0.4d)));

        // HFR is pixels and the primary metric, so it takes the left axis; the two arcsecond
        // metrics share the right one, which is the whole of the Unit grouping rule.
        Assert.Equal(2, chart.YAxes.Count);
        Assert.Equal(AxisPosition.Start, chart.YAxes[0].Position);
        Assert.Equal(AxisPosition.End, chart.YAxes[1].Position);
        Assert.Equal(0, Line(chart, 0).ScalesYAt);
        Assert.Equal(1, Line(chart, 1).ScalesYAt);
        Assert.Equal(1, Line(chart, 2).ScalesYAt);
    }

    [Fact]
    public void MedianLine_IsAtTheSessionMedianOfThePrimaryMetric()
    {
        // The figure is the SessionDetail's own MetricRangeSummary.Median, which is what proves it
        // is Task 2's median rather than a second one computed here: the frames deliberately do
        // not average to it.
        using var fixture = new Fixture();
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d), Frame(2, hfr: 2.9d)],
            hfr: new MetricRangeSummary(2.1d, 2.9d, 2.44d)));

        var section = Assert.Single(chart.Sections);
        Assert.Equal(2.44d, section.Yi);
        Assert.Equal(2.44d, section.Yj);
        Assert.Equal(0, section.ScalesYAt);
        Assert.Null(section.Fill);
        Assert.Equal("median 2.44 px", section.Label);
    }

    [Fact]
    public void MedianLine_PrimaryMetric_IsTheFirstEnabledMetricWithData()
    {
        // HFR is enabled and first in the table, but no frame measured it, so the primary
        // metric is eccentricity and the line is drawn at its median.
        using var fixture = new Fixture(metrics: ["hfr", "eccentricity"]);
        var chart = fixture.Build(Detail(
            [Frame(1, eccentricity: 0.38d), Frame(2, eccentricity: 0.44d)],
            eccentricity: new MetricRangeSummary(0.38d, 0.44d, 0.41d)));

        Assert.Equal("eccentricity", chart.PrimaryMetric!.Key);
        Assert.Equal(0.41d, Assert.Single(chart.Sections).Yi);
    }

    [Fact]
    public void MedianLine_NoEnabledMetricHasData_YieldsNoSection()
    {
        using var fixture = new Fixture();
        var chart = fixture.Build(Detail([Frame(1), Frame(2)]));

        Assert.True(chart.IsEmpty);
        Assert.Empty(chart.Sections);
        Assert.Empty(chart.Series);
    }

    [AvaloniaFact]
    public void MedianLine_UsesThePrimaryMetricColour()
    {
        ChartTheme.Apply();
        using var fixture = new Fixture();
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d)],
            hfr: new MetricRangeSummary(2.1d, 2.1d, 2.1d)));

        var section = Assert.Single(chart.Sections);
        var stroke = Assert.IsType<SolidColorPaint>(section.Stroke).Color;
        var token = Token("ColorMetricHfr");

        // The metric's own hue (spec 14.5), at reduced opacity so it reads as an annotation
        // rather than as a sixth series.
        Assert.Equal(token.Red, stroke.Red);
        Assert.Equal(token.Green, stroke.Green);
        Assert.Equal(token.Blue, stroke.Blue);
        Assert.True(stroke.Alpha < token.Alpha);
    }

    [Fact]
    public void MedianLine_DetectedStars_FallsBackToTheSharedMedian()
    {
        // SessionDetail carries no MetricRangeSummary for detected stars, so the one metric
        // without a precomputed median uses Core's Statistics.Median over the frames the chart is
        // already plotting: the same function Task 2 built the other four summaries with.
        using var fixture = new Fixture(metrics: ["detected_stars"]);
        var frames = new[] { Frame(1, stars: 1400), Frame(2, stars: 1500), Frame(3, stars: 1900) };
        var chart = fixture.Build(Detail(frames));

        Assert.Equal("detected_stars", chart.PrimaryMetric!.Key);
        Assert.Equal(
            Statistics.Median(frames.Select(frame => (double?)frame.DetectedStars)),
            Assert.Single(chart.Sections).Yi);
    }

    [Fact]
    public void FilterSplit_RestrictsPointsToThatFilter_AndNullsElsewhere()
    {
        using var fixture = new Fixture(filters: ["overall", "Ha"]);
        var chart = fixture.Build(Detail(
            [
                Frame(1, hfr: 2.1d, filter: "Ha"),
                Frame(2, hfr: 2.5d, filter: "OIII"),
                Frame(3, hfr: 2.2d, filter: "Ha"),
            ],
            hfr: new MetricRangeSummary(2.1d, 2.5d, 2.2d)));

        Assert.Equal(["HFR (px)", "HFR (px) (Ha)"], chart.Series.Select(series => series.Name));
        Assert.Equal([2.1d, 2.5d, 2.2d], Values(chart, 0));
        Assert.Equal([2.1d, null, 2.2d], Values(chart, 1));
    }

    [Fact]
    public void Series_NeverReadFwhmFromTheHeaderValue()
    {
        // Spec 7.1.1: the header FWHM is never charted. A frame's own fwhm column is plotted, and
        // there is no accessor for a header FWHM anywhere in the metric table to plot instead.
        using var fixture = new Fixture(metrics: ["fwhm"]);
        var chart = fixture.Build(Detail(
            [Frame(1, fwhm: 1.7d)],
            fwhm: new MetricRangeSummary(1.7d, 1.7d, 1.7d)));

        Assert.Equal([1.7d], Values(chart, 0));
        Assert.Equal("fwhm", chart.PrimaryMetric!.Column);
        Assert.DoesNotContain("median_fwhm", ChartMetrics.All.Select(metric => metric.Column));
    }

    [Fact]
    public void EmptySession_IsEmpty()
    {
        // A night whose frames all failed to ingest a metric, or the empty Frames list Task 4's
        // fixture ships.
        using var fixture = new Fixture();
        var chart = fixture.Build(Detail([]));

        Assert.True(chart.IsEmpty);
        Assert.Empty(chart.Series);
        Assert.Empty(chart.Sections);

        // One bare axis on each side rather than none: rc5.4's chart engine throws on an empty
        // axis collection even while the control is collapsed.
        Assert.Single(chart.XAxes);
        Assert.Single(chart.YAxes);
    }

    [Fact]
    public void Selection_TogglingAMetric_RebuildsTheSeries()
    {
        using var fixture = new Fixture(metrics: ["hfr"]);
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d, fwhm: 1.8d)],
            hfr: new MetricRangeSummary(2.1d, 2.1d, 2.1d),
            fwhm: new MetricRangeSummary(1.8d, 1.8d, 1.8d)));

        Assert.Single(chart.Series);
        fixture.Selection.Metrics.Single(pill => pill.Key == "fwhm").IsSelected = true;

        // The same selection singleton both charts share, so one toggle rebuilds both.
        Assert.Equal(["HFR (px)", "FWHM"], chart.Series.Select(series => series.Name));
    }

    // [AvaloniaFact] rather than [Fact]: the assertion below calls ChartTheme.Apply, which
    // resolves the palette from Application.Current and replaces it process-wide. Running it
    // without an application would leave every metric colour on the fallback grey for
    // whatever test runs next.
    [AvaloniaFact]
    public void Dispose_StopsRebuilding()
    {
        ChartTheme.Apply();

        using var fixture = new Fixture(metrics: ["hfr"]);
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d, fwhm: 1.8d)],
            hfr: new MetricRangeSummary(2.1d, 2.1d, 2.1d),
            fwhm: new MetricRangeSummary(1.8d, 1.8d, 1.8d)));

        // The card disposes its chart on every Invalidate (Task 4 handoff), and the selection is a
        // process-wide singleton, so a chart that keeps listening rebuilds forever.
        chart.Dispose();
        fixture.Selection.Metrics.Single(pill => pill.Key == "fwhm").IsSelected = true;

        Assert.Single(chart.Series);

        // Review finding 3: ChartTheme.Changed is a static event, so a handler left behind pins
        // this chart and the card that owns it for the life of the process. Same instances after a
        // theme swap is what proves the handler is gone.
        var series = chart.Series;
        var pills = chart.MetricPills;
        ChartTheme.Apply();

        Assert.Same(series, chart.Series);
        Assert.Same(pills, chart.MetricPills);
    }

    [AvaloniaFact]
    public void ThemeChange_RepaintsALiveChart()
    {
        // Spec 13's "re-read on theme change", for the chart the card owns.
        ChartTheme.Apply();
        using var fixture = new Fixture();
        var chart = fixture.Build(Detail(
            [Frame(1, hfr: 2.1d)],
            hfr: new MetricRangeSummary(2.1d, 2.1d, 2.1d)));

        var stroke = Assert.IsType<SolidColorPaint>(Line(chart, 0).Stroke);
        var section = Assert.Single(chart.Sections);

        ChartTheme.Apply();

        Assert.NotSame(stroke, Assert.IsType<SolidColorPaint>(Line(chart, 0).Stroke));
        Assert.NotSame(section, Assert.Single(chart.Sections));
        Assert.Equal(Token("ColorMetricHfr"), Stroke(chart, 0));
    }

    [Fact]
    public void YAxes_DetectedStars_DoesNotShareTheEccentricityAxis()
    {
        // The axis-grouping rule, on the per-frame chart: a star count and
        // an eccentricity are not comparable magnitudes and no longer share an axis group.
        using var fixture = new Fixture(metrics: ["eccentricity", "detected_stars"]);
        var chart = fixture.Build(Detail(
            [Frame(1, eccentricity: 0.38d, stars: 1400), Frame(2, eccentricity: 0.44d, stars: 1500)],
            eccentricity: new MetricRangeSummary(0.38d, 0.44d, 0.41d)));

        Assert.Equal(2, chart.YAxes.Count);
        Assert.Equal(0, Line(chart, 0).ScalesYAt);
        Assert.Equal(1, Line(chart, 1).ScalesYAt);
        Assert.Equal("1,490 count", Assert.IsType<Axis>(chart.YAxes[1]).Labeler(1490d));
    }

    [Fact]
    public void YAxes_RightAxisCarryingTwoUnits_LabelsNoUnitAtAll()
    {
        // Review finding 2, on the per-frame chart: the shipped default enabled set plus stars is
        // already three units, and one suffix cannot describe two of them.
        using var fixture = new Fixture(metrics: ["eccentricity", "hfr", "fwhm"]);
        var chart = fixture.Build(Detail(
            [Frame(1, eccentricity: 0.4d, hfr: 2.1d, fwhm: 1.8d)],
            hfr: new MetricRangeSummary(2.1d, 2.1d, 2.1d),
            eccentricity: new MetricRangeSummary(0.4d, 0.4d, 0.4d),
            fwhm: new MetricRangeSummary(1.8d, 1.8d, 1.8d)));

        var right = Assert.IsType<Axis>(chart.YAxes[1]);
        Assert.Equal("2.00", right.Labeler(2d));
        Assert.DoesNotContain("px", right.Labeler(2d));
        Assert.DoesNotContain("arcsec", right.Labeler(2d));
    }

    // D212: red if IsLaned builds session lanes, so the night's chart leaves the overlaid form.
    [Fact]
    public void IsLaned_StillShowsOverlaid_AndBuildsNoLanes()
    {
        using var fixture = new Fixture(metrics: ["hfr", "fwhm"]);
        var chart = fixture.Build(Detail([Frame(1, hfr: 2.1d, fwhm: 1.8d), Frame(2, hfr: 2.3d, fwhm: 1.9d)]));

        chart.IsLaned = true;

        Assert.True(chart.ShowsOverlaid, "a laned session chart left the overlaid form");
        Assert.False(chart.ShowsLanes);
        Assert.Empty(chart.Lanes);
        Assert.NotEmpty(chart.Series);
    }
}
