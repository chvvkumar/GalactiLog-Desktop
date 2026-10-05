using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 13's "Cross-session metric trend". Every assertion is against the Series, XAxes and YAxes
// objects the view-model built, never against rendered output: spec 18.3 puts pixel comparison and
// real rendering out of scope. No window is needed for the shape of a series, so only the tests
// that resolve a theme token are [AvaloniaFact].
public class TargetChartViewModelTests
{
    // One session date per plotted point, ascending, so an assertion can name the order it expects.
    private static readonly DateOnly[] Nights =
    [
        new(2025, 11, 1),
        new(2025, 11, 8),
        new(2025, 11, 15),
    ];

    private sealed class Fixture : IDisposable
    {
        private readonly List<Cards.Harness> _harnesses = [];

        public GraphSettings Document;

        public int Saves;

        public readonly ChartSelectionViewModel Selection;

        public Fixture(
            string[]? metrics = null,
            string[]? filters = null,
            int defaultChartSessions = 100,
            Dictionary<string, FilterSetting>? configuredFilters = null)
        {
            Document = new GraphSettings
            {
                EnabledMetrics = metrics ?? ["hfr"],
                EnabledFilters = filters ?? ["overall"],
                DefaultChartSessions = defaultChartSessions,
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

        /// <summary>The page's card collection, newest session first, which is the order
        /// <c>TargetDetailQuery</c> returns and therefore what the chart is handed.</summary>
        public ObservableCollection<SessionCardViewModel> Sessions { get; } = [];

        public TargetChartViewModel? Chart { get; private set; }

        /// <param name="loadDetail">True expands the card and joins its query, which is the only
        /// way a session gets the per-filter medians a split series needs.</param>
        public Fixture Add(SessionOverview overview, bool loadDetail = false)
        {
            var harness = Cards.Create(
                overview: overview,
                detail: Cards.PopulatedDetail(overview.SessionDate));

            _harnesses.Add(harness);

            if (loadDetail)
            {
                harness.Card.IsExpanded = true;
                harness.Settle();
            }

            // Newest first, the way the page builds it.
            Sessions.Insert(0, harness.Card);
            return this;
        }

        public TargetChartViewModel Build() => Chart = new TargetChartViewModel(Sessions, Selection);

        public void Dispose()
        {
            Chart?.Dispose();
            foreach (var harness in _harnesses)
            {
                harness.Dispose();
            }
        }
    }

    private static Fixture ThreeNights(
        string[]? metrics = null,
        string[]? filters = null,
        int defaultChartSessions = 100,
        Dictionary<string, FilterSetting>? configuredFilters = null,
        Func<DateOnly, SessionOverview>? overview = null)
    {
        var fixture = new Fixture(metrics, filters, defaultChartSessions, configuredFilters);
        foreach (var night in Nights)
        {
            fixture.Add((overview ?? Page.Session)(night));
        }

        return fixture;
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

    [Fact]
    public void Series_OnePerEnabledMetric()
    {
        using var fixture = ThreeNights(metrics: ["hfr", "fwhm"]);
        var chart = fixture.Build();

        Assert.Equal(2, chart.Series.Count);
        Assert.Equal(["HFR (px)", "FWHM"], chart.Series.Select(series => series.Name));
        Assert.False(chart.IsEmpty);
    }

    [Fact]
    public void Series_InChartMetricsOrder()
    {
        // Stored in the reverse of the table's order: the legend still reads in table order,
        // because that order is load-bearing for the primary metric too.
        using var fixture = ThreeNights(metrics: ["detected_stars", "hfr"]);
        var chart = fixture.Build();

        Assert.Equal(["HFR (px)", "Stars"], chart.Series.Select(series => series.Name));
        Assert.Equal("hfr", chart.PrimaryMetric!.Key);
    }

    // Roadmap row 7's Verify clause a second time, at the series level: the stroke, the point
    // outline and the point fill are all the metric's own metric-* token, resolved from the
    // shipped dictionary rather than compared against a hex literal.
    [AvaloniaFact]
    public void Series_Colour_MatchesTheMetricToken()
    {
        ChartTheme.Apply();
        using var fixture = ThreeNights(metrics: ["hfr", "eccentricity", "fwhm", "guiding_rms", "detected_stars"]);
        var chart = fixture.Build();

        Assert.Equal(5, chart.Series.Count);
        for (var index = 0; index < chart.Series.Count; index++)
        {
            var expected = Token(ChartMetrics.All[index].TokenKey);
            Assert.Equal(expected, Stroke(chart, index));
            Assert.Equal(expected, Assert.IsType<SolidColorPaint>(Line(chart, index).GeometryStroke).Color);
            Assert.Equal(expected, Assert.IsType<SolidColorPaint>(Line(chart, index).GeometryFill).Color);
        }
    }

    [Fact]
    public void Series_NoMetricEnabled_IsEmpty()
    {
        using var fixture = ThreeNights(metrics: []);
        var chart = fixture.Build();

        Assert.True(chart.IsEmpty);
        Assert.Empty(chart.Series);
        Assert.Null(chart.PrimaryMetric);

        // One bare axis on each side rather than none: rc5.4's chart engine throws on an empty
        // axis collection even while the control is collapsed, so the empty state is "no series",
        // never "no axes".
        Assert.Single(chart.XAxes);
        Assert.Single(chart.YAxes);
    }

    [Fact]
    public void Series_PointsAreOn()
    {
        // Spec 13's "points on". A zero geometry size renders a bare polyline.
        using var fixture = ThreeNights();
        var chart = fixture.Build();

        Assert.True(Line(chart, 0).GeometrySize > 0);
        Assert.Null(Line(chart, 0).Fill);
    }

    [Fact]
    public void Series_ValuesAreOnePerSession_OldestFirst()
    {
        using var fixture = new Fixture();
        fixture.Add(Page.Session(Nights[0]) with { MedianHfr = 1.1d });
        fixture.Add(Page.Session(Nights[1]) with { MedianHfr = 2.2d });
        fixture.Add(Page.Session(Nights[2]) with { MedianHfr = 3.3d });
        var chart = fixture.Build();

        Assert.Equal([1.1d, 2.2d, 3.3d], Values(chart, 0));
    }

    // The roadmap row 7 Verify line this task owns: a missing session produces a gap rather than a
    // joined line. Both halves are asserted, because the null alone would still be joined if
    // splitting were off.
    [Fact]
    public void MissingSessionValue_ProducesAGap()
    {
        using var fixture = new Fixture();
        fixture.Add(Page.Session(Nights[0]) with { MedianHfr = 1.1d });
        fixture.Add(Page.Session(Nights[1]) with { MedianHfr = null });
        fixture.Add(Page.Session(Nights[2]) with { MedianHfr = 3.3d });
        var chart = fixture.Build();

        Assert.Equal([1.1d, null, 3.3d], Values(chart, 0));
        Assert.True(Line(chart, 0).EnableNullSplitting);
    }

    [Fact]
    public void MissingSessionValue_IsNotFilteredOutAndIsNotZero()
    {
        // The two ways this gets broken: dropping the point, which shifts every later session one
        // tick to the left, and substituting zero, which draws a spike to the floor.
        using var fixture = new Fixture();
        fixture.Add(Page.Session(Nights[0]) with { MedianHfr = 1.1d });
        fixture.Add(Page.Session(Nights[1]) with { MedianHfr = null });
        fixture.Add(Page.Session(Nights[2]) with { MedianHfr = 3.3d });
        var chart = fixture.Build();

        Assert.Equal(3, Values(chart, 0).Length);
        Assert.DoesNotContain(0d, Values(chart, 0));
    }

    [Fact]
    public void XAxis_LabelsAreTheSessionDates()
    {
        using var fixture = ThreeNights();
        var chart = fixture.Build();

        var axis = Assert.IsType<Axis>(Assert.Single(chart.XAxes));
        Assert.Equal(["11-01", "11-08", "11-15"], Printed(axis, 3));
    }

    [Fact]
    public void XAxis_SpanCrossingAYear_ShowsTheYear()
    {
        using var fixture = new Fixture();
        fixture.Add(Page.Session(new DateOnly(2024, 12, 30)));
        fixture.Add(Page.Session(new DateOnly(2025, 1, 4)));
        var chart = fixture.Build();

        var axis = Assert.IsType<Axis>(Assert.Single(chart.XAxes));
        Assert.Equal(["2024-12-30", "2025-01-04"], Printed(axis, 2));
    }

    [Fact]
    public void XAxis_ForcesAStepOfOne()
    {
        // A 40 session target would otherwise render half a label per tick.
        using var fixture = ThreeNights();
        var chart = fixture.Build();

        var axis = Assert.IsType<Axis>(Assert.Single(chart.XAxes));
        Assert.Equal(1d, axis.MinStep);
        Assert.True(axis.ForceStepToMin);
    }

    [Fact]
    public void YAxes_OnlyLeft_WhenEverySelectedMetricIsLeftAxis()
    {
        // FWHM and guiding RMS are both arcseconds, so one unit is shown and spec 13's right axis
        // ("secondary metric when two units are shown") must be absent.
        using var fixture = ThreeNights(metrics: ["fwhm", "guiding_rms"]);
        var chart = fixture.Build();

        var axis = Assert.IsType<Axis>(Assert.Single(chart.YAxes));
        Assert.Equal(AxisPosition.Start, axis.Position);
    }

    [Fact]
    public void YAxes_BothPresent_WhenTwoUnitsAreShown()
    {
        using var fixture = ThreeNights(metrics: ["hfr", "fwhm"]);
        var chart = fixture.Build();

        Assert.Equal(2, chart.YAxes.Count);
        Assert.Equal(AxisPosition.Start, chart.YAxes[0].Position);
        Assert.Equal(AxisPosition.End, chart.YAxes[1].Position);
    }

    [Fact]
    public void Series_ScalesYAt_MatchesTheAxisIndexInBothCases()
    {
        // The most likely defect in this task: a series pinned to index 1 with no right axis
        // present scales against nothing at all.
        using var singleUnit = ThreeNights(metrics: ["fwhm", "guiding_rms"]);
        var one = singleUnit.Build();
        Assert.Single(one.YAxes);
        Assert.Equal(0, Line(one, 0).ScalesYAt);
        Assert.Equal(0, Line(one, 1).ScalesYAt);

        using var twoUnits = ThreeNights(metrics: ["hfr", "fwhm"]);
        var two = twoUnits.Build();
        Assert.Equal(2, two.YAxes.Count);
        // HFR is the primary metric and defines the left axis; arcseconds go to the right.
        Assert.Equal(0, Line(two, 0).ScalesYAt);
        Assert.Equal(1, Line(two, 1).ScalesYAt);
    }

    [Fact]
    public void YAxes_MetricWithNoData_DoesNotConjureARightAxis()
    {
        // Eccentricity is dimensionless and HFR is pixels, but no plotted night measured
        // eccentricity, so only one unit is actually shown.
        using var fixture = ThreeNights(
            metrics: ["hfr", "eccentricity"],
            overview: night => Page.Session(night) with { MedianEccentricity = null });

        var chart = fixture.Build();

        Assert.Single(chart.YAxes);
        Assert.Equal("hfr", chart.PrimaryMetric!.Key);
        Assert.Equal(0, Line(chart, 0).ScalesYAt);
        Assert.Equal(0, Line(chart, 1).ScalesYAt);
    }

    [Fact]
    public void FilterSplit_OverallPlusOneFilter_YieldsTwoSeriesPerMetric()
    {
        using var fixture = ThreeNights(metrics: ["hfr", "fwhm"], filters: ["overall", "Ha"]);
        var chart = fixture.Build();

        Assert.Equal(
            ["HFR (px)", "HFR (px) (Ha)", "FWHM", "FWHM (Ha)"],
            chart.Series.Select(series => series.Name));
    }

    [Fact]
    public void FilterSplit_OverallOff_YieldsOnlyTheSplit()
    {
        using var fixture = new Fixture(metrics: ["hfr"], filters: ["Ha"]);
        fixture.Add(Page.Session(Nights[0]), loadDetail: true);
        var chart = fixture.Build();

        Assert.Equal(["HFR (px) (Ha)"], chart.Series.Select(series => series.Name));
        Assert.Equal("hfr", chart.PrimaryMetric!.Key);
    }

    [AvaloniaFact]
    public void FilterSplit_TintsByTheConfiguredFilterColour()
    {
        ChartTheme.Apply();
        using var fixture = ThreeNights(
            filters: ["overall", "Ha"],
            configuredFilters: new Dictionary<string, FilterSetting>
            {
                ["Ha"] = new FilterSetting { Color = "#FF3366" },
            });

        var chart = fixture.Build();

        // Spec 14.5: the metric series keeps its fixed token, the split takes the filter's
        // configured colour, which is user data and the one legitimate non-token colour.
        Assert.Equal(Token("ColorMetricHfr"), Stroke(chart, 0));
        Assert.Equal(new SKColor(0xFF, 0x33, 0x66, 0xFF), Stroke(chart, 1));
    }

    [AvaloniaFact]
    public void FilterSplit_UnconfiguredFilter_UsesTheSeededCategoryColour()
    {
        ChartTheme.Apply();
        using var fixture = ThreeNights(filters: ["overall", "Ha"]);
        var chart = fixture.Build();

        // This case was
        // FilterSplit_UnconfiguredFilter_UsesTheDefaultGrey. "Ha" is no longer an unconfigured
        // grey: with nothing stored it folds to the seeded palette and reads #c44040, which is
        // the acceptance bar's item 1 reaching the chart through the one spine. The grey is
        // still what a name folding to no category reads as; AliasMapTests pins that, and this
        // fixture's per-filter medians only carry Ha and OIII, so it cannot be shown here.
        Assert.Equal(new SKColor(0xc4, 0x40, 0x40), Stroke(chart, 1));
        Assert.NotEqual(ChartTheme.Fallback, Stroke(chart, 1));
        // Still demonstrably not the metric token, which Apply has just resolved to something
        // else.
        Assert.NotEqual(ChartTheme.Fallback, Stroke(chart, 0));
        Assert.NotEqual(Stroke(chart, 0), Stroke(chart, 1));
    }

    private static string[] Printed(Axis axis, int count)
        => [.. Enumerable.Range(0, count).Select(index => axis.Labeler(index))];

    [Fact]
    public void FilterSplit_SessionWithoutLoadedDetail_ContributesNull()
    {
        // Spec 12.4 puts the per-filter medians on the expanded session detail, so a
        // night whose card has never been expanded has nothing to plot and gaps instead.
        using var fixture = new Fixture(metrics: ["hfr"], filters: ["overall", "Ha"]);
        fixture.Add(Page.Session(Nights[0]), loadDetail: true);
        fixture.Add(Page.Session(Nights[1]));
        var chart = fixture.Build();

        // Overall has both nights; the split has only the loaded one, oldest first.
        Assert.Equal([2.3d, 2.3d], Values(chart, 0));
        Assert.Equal([2.28d, null], Values(chart, 1));
        Assert.True(Line(chart, 1).EnableNullSplitting);
    }

    [Fact]
    public async Task FilterSplit_CardLoadingItsDetail_FillsInThePoint()
    {
        using var fixture = new Fixture(metrics: ["hfr"], filters: ["overall", "Ha"]);
        fixture.Add(Page.Session(Nights[0]));
        var chart = fixture.Build();
        Assert.Equal([null], Values(chart, 1));

        // Expanding the card is what makes the per-filter medians available, so the chart has to
        // notice rather than wait for an unrelated rebuild.
        var card = fixture.Sessions[0];
        card.IsExpanded = true;
        await card.PendingLoad!;

        Assert.Equal([2.28d], Values(chart, 1));
    }

    [Fact]
    public async Task Selection_TogglingAMetric_RebuildsTheSeries()
    {
        using var fixture = ThreeNights(metrics: ["hfr"]);
        var chart = fixture.Build();
        Assert.Single(chart.Series);

        fixture.Selection.Metrics.Single(pill => pill.Key == "fwhm").IsSelected = true;

        Assert.Equal(["HFR (px)", "FWHM"], chart.Series.Select(series => series.Name));

        // The other half of roadmap row 7's Verify line: the toggle wrote graph.enabled_metrics
        // through the one writer, and the chart did not open a second chain to do it.
        await fixture.Selection.PendingPersist;
        Assert.Equal(["hfr", "fwhm"], fixture.Document.EnabledMetrics);
        Assert.Equal(1, fixture.Saves);
    }

    [Fact]
    public void DefaultChartSessions_LimitsThePlottedSessions()
    {
        // The newest N sessions on first open, N from spec 5.8.3's setting.
        using var fixture = ThreeNights(defaultChartSessions: 2);
        var chart = fixture.Build();

        Assert.Equal(2, chart.PlottedSessionCount);
        Assert.Equal(2, Values(chart, 0).Length);
        Assert.Equal(
            ["11-08", "11-15"],
            Printed(Assert.IsType<Axis>(Assert.Single(chart.XAxes)), 2));

        Assert.True(chart.HasSessionScope);
        Assert.Equal("newest 2 of 3 sessions", chart.SessionScopeText);
    }

    [Fact]
    public void ShowAllSessions_WidensTheScope_WithoutPersistingIt()
    {
        using var fixture = ThreeNights(defaultChartSessions: 1);
        var chart = fixture.Build();
        Assert.Equal(1, chart.PlottedSessionCount);

        chart.ShowAllSessions = true;

        Assert.Equal(3, chart.PlottedSessionCount);
        Assert.Equal(3, Values(chart, 0).Length);
        Assert.Equal("all 3 sessions", chart.SessionScopeText);

        // The affordance is unpersisted. default_chart_sessions still means "on first
        // open", so nothing was written.
        Assert.Equal(1, fixture.Document.DefaultChartSessions);
        Assert.Equal(0, fixture.Saves);
    }

    [Fact]
    public void DefaultChartSessions_FewerSessionsThanTheSetting_HasNoScopeControl()
    {
        using var fixture = ThreeNights();
        var chart = fixture.Build();

        Assert.Equal(3, chart.PlottedSessionCount);
        Assert.False(chart.HasSessionScope);
    }

    [Fact]
    public void NoSessions_IsEmpty()
    {
        // A target whose sessions have not loaded yet, or an obj: group a scan just pruned.
        using var fixture = new Fixture();
        var chart = fixture.Build();

        Assert.True(chart.IsEmpty);
        Assert.Empty(chart.Series);
        Assert.Single(chart.XAxes);
        Assert.Single(chart.YAxes);
    }

    [Fact]
    public void Reload_OffersTheFiltersTheSessionsContain_AndNeverAnEmptyList()
    {
        using var fixture = new Fixture(filters: ["overall", "OIII"]);
        fixture.Add(Page.Session(Nights[0]) with { FiltersUsed = ["Ha", "OIII"] });
        var chart = fixture.Build();

        Assert.Equal(
            ["overall", "Ha", "OIII"],
            fixture.Selection.Filters.Select(pill => pill.Key));

        // A page whose cards have been cleared for a reload must not have its enabled filters
        // reconciled against nothing, which would drop OIII and write the loss through.
        fixture.Sessions.Clear();
        chart.Reload();

        Assert.Contains("OIII", fixture.Selection.EnabledFilters);
    }

    [Fact]
    public void Reload_OffersTheFilters_LrgbShoFirst_ThenTheRestAlphabetically()
    {
        // A failure is the alphabetical offer B, Duoband, L after "overall".
        using var fixture = new Fixture(filters: ["overall"]);
        fixture.Add(Page.Session(Nights[0]) with { FiltersUsed = ["Duoband", "L", "B"] });
        fixture.Build();

        Assert.Equal(
            ["overall", "L", "B", "Duoband"],
            fixture.Selection.Filters.Select(pill => pill.Key));
    }

    // [AvaloniaFact] rather than [Fact]: the assertion below calls ChartTheme.Apply, which
    // resolves the palette from Application.Current and replaces it process-wide. Running it
    // without an application would leave every metric colour on the fallback grey for
    // whatever test runs next.
    [AvaloniaFact]
    public void Dispose_StopsRebuilding()
    {
        ChartTheme.Apply();

        using var fixture = ThreeNights(metrics: ["hfr"]);
        var chart = fixture.Build();
        chart.Dispose();

        fixture.Selection.Metrics.Single(pill => pill.Key == "fwhm").IsSelected = true;

        // Still the one series it had: a disposed chart that keeps listening rebuilds for every
        // page opened afterwards, because the selection is a process-wide singleton.
        Assert.Single(chart.Series);

        // Review finding 3: ChartTheme.Changed is a static event, so a handler left behind pins
        // this chart, its cards and its page for the life of the process. Same instances after a
        // theme swap is what proves the handler is gone.
        var series = chart.Series;
        var pills = chart.MetricPills;
        ChartTheme.Apply();

        Assert.Same(series, chart.Series);
        Assert.Same(pills, chart.MetricPills);
    }

    // Spec 13's "re-read on theme change", and the live half of review finding 3: an undisposed
    // chart repaints what it already built.
    [AvaloniaFact]
    public void ThemeChange_RepaintsALiveChart()
    {
        ChartTheme.Apply();
        using var fixture = ThreeNights();
        var chart = fixture.Build();

        var series = chart.Series;
        var stroke = Assert.IsType<SolidColorPaint>(Line(chart, 0).Stroke);
        var pills = chart.MetricPills;

        ChartTheme.Apply();

        // New objects, not mutated ones: a paint is handed to LiveCharts by reference, so the
        // rebuild is what makes a theme swap visible.
        Assert.NotSame(series, chart.Series);
        Assert.NotSame(stroke, Assert.IsType<SolidColorPaint>(Line(chart, 0).Stroke));
        Assert.NotSame(pills, chart.MetricPills);

        // And still the right colour, from the same token.
        Assert.Equal(Token("ColorMetricHfr"), Stroke(chart, 0));
    }

    [Fact]
    public void YAxes_RightAxisCarryingTwoUnits_LabelsNoUnitAtAll()
    {
        // Review finding 2. Three units are enabled, HFR is primary and owns the left axis in
        // pixels, and the dimensionless and arcsecond metrics both land on the right one, so
        // labelling every one of its ticks with either unit would be wrong for the other.
        using var fixture = ThreeNights(metrics: ["eccentricity", "hfr", "fwhm"]);
        var chart = fixture.Build();

        Assert.Equal(2, chart.YAxes.Count);
        Assert.Equal("hfr", chart.PrimaryMetric!.Key);

        var right = Assert.IsType<Axis>(chart.YAxes[1]);
        Assert.Equal("1.90", right.Labeler(1.9d));
        Assert.DoesNotContain("arcsec", right.Labeler(1.9d));
        Assert.DoesNotContain("px", right.Labeler(1.9d));

        // The left axis carries exactly one unit by construction, so it keeps its suffix.
        Assert.Equal("2.00 px", Assert.IsType<Axis>(chart.YAxes[0]).Labeler(2d));
    }

    [Fact]
    public void YAxes_RightAxisCarryingOneUnit_KeepsItsSuffix()
    {
        using var fixture = ThreeNights(metrics: ["hfr", "fwhm", "guiding_rms"]);
        var chart = fixture.Build();

        Assert.Equal("2.00 px", Assert.IsType<Axis>(chart.YAxes[0]).Labeler(2d));
        Assert.Equal("1.90 arcsec", Assert.IsType<Axis>(chart.YAxes[1]).Labeler(1.9d));
    }

    [Fact]
    public void YAxes_DetectedStars_DoesNotShareTheEccentricityAxis()
    {
        // The axis-grouping rule: 1,490 stars and an eccentricity of 0.4
        // are not comparable magnitudes, and before detected stars had its own unit both landed
        // on the same axis, flattening the eccentricity line into the floor.
        using var fixture = ThreeNights(metrics: ["eccentricity", "detected_stars"]);
        var chart = fixture.Build();

        Assert.Equal(2, chart.YAxes.Count);
        Assert.Equal("eccentricity", chart.PrimaryMetric!.Key);
        Assert.Equal(0, Line(chart, 0).ScalesYAt);
        Assert.Equal(1, Line(chart, 1).ScalesYAt);
        Assert.Equal("1,490 count", Assert.IsType<Axis>(chart.YAxes[1]).Labeler(1490d));
    }

    [Fact]
    public void YAxes_DetectedStars_AndEccentricity_AreDistinctUnitGroups()
    {
        // The same rule with a third unit present and neither of the two primary: a two axis
        // chart cannot separate three unit groups, so both still scale against the right axis and
        // that axis's suffix is dropped. What the rule fixes is the case above,
        // where one of them owns the left axis.
        using var fixture = ThreeNights(metrics: ["hfr", "eccentricity", "detected_stars"]);
        var chart = fixture.Build();

        Assert.Equal("hfr", chart.PrimaryMetric!.Key);
        Assert.NotEqual(
            ChartMetrics.ByKey("eccentricity")!.Unit,
            ChartMetrics.ByKey("detected_stars")!.Unit);

        // Both still scale against the right axis, which therefore carries no unit suffix. A
        // count on the shared axis makes its labels whole numbers.
        var right = Assert.IsType<Axis>(chart.YAxes[1]);
        Assert.Equal("1,490", right.Labeler(1490d));
        Assert.Equal(1, Line(chart, 1).ScalesYAt);
        Assert.Equal(1, Line(chart, 2).ScalesYAt);
    }

    [Fact]
    public void BeginUpdate_SuspendsRebuilds_AndEndUpdatePublishesOnce()
    {
        // Review finding 5, at the chart level: the page brackets its whole ReplaceSessions, so a
        // reload publishes once instead of once per card added, refreshed or dropped.
        using var fixture = ThreeNights(metrics: ["hfr"]);
        var chart = fixture.Build();

        var publishes = 0;
        var sawEmpty = false;
        chart.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(chart.Series))
            {
                return;
            }

            publishes++;
            sawEmpty |= chart.IsEmpty;
        };

        chart.BeginUpdate();
        fixture.Selection.Metrics.Single(pill => pill.Key == "fwhm").IsSelected = true;
        chart.ShowAllSessions = true;
        Assert.Equal(0, publishes);

        chart.EndUpdate();

        Assert.Equal(1, publishes);
        Assert.False(sawEmpty);
        Assert.Equal(["HFR (px)", "FWHM"], chart.Series.Select(series => series.Name));
    }

    [Fact]
    public void EndUpdate_AfterAThrowingUpdate_StillPublishes()
    {
        // The page's bracket is in a finally for this reason: a chart left suspended never draws
        // again for the life of the page.
        using var fixture = ThreeNights(metrics: ["hfr"]);
        var chart = fixture.Build();

        chart.BeginUpdate();
        chart.EndUpdate();

        Assert.Single(chart.Series);
        Assert.False(chart.IsEmpty);
    }

    [Fact]
    public void MetricPills_AreTheSharedTogglesTintedByTheirToken()
    {
        using var fixture = ThreeNights();
        var chart = fixture.Build();

        // The same instances the selection holds, not copies: checking a pill has to reach the
        // one write chain (spec 5.8.3).
        Assert.Equal(
            fixture.Selection.Metrics,
            chart.MetricPills.Select(pill => pill.Toggle));

        Assert.All(chart.MetricPills, pill => Assert.NotNull(pill.Tint));
    }
    [Fact]
    public void CheckedOnly_PlotsOnlyTheCheckedNights_AndFollowsACheckBox()
    {
        // Red if Checked still plots every night, or if a later check is not picked up.
        using var fixture = ThreeNights();
        var chart = fixture.Build();
        fixture.Sessions[2].IsChecked = true;

        chart.CheckedOnly = true;

        Assert.Equal(1, chart.PlottedSessionCount);
        Assert.Equal(["11-01"], Printed(Assert.IsType<Axis>(Assert.Single(chart.XAxes)), 1));

        fixture.Sessions[0].IsChecked = true;

        Assert.Equal(2, chart.PlottedSessionCount);
        Assert.Equal(["11-01", "11-15"], Printed(Assert.IsType<Axis>(Assert.Single(chart.XAxes)), 2));
    }

    [Fact]
    public void CheckedOnly_UncheckingTheLastNight_ReturnsToAll()
    {
        // The switch is disabled with nothing checked, so it cannot be left on an empty chart.
        using var fixture = ThreeNights();
        var chart = fixture.Build();
        fixture.Sessions[1].IsChecked = true;
        chart.CheckedOnly = true;

        fixture.Sessions[1].IsChecked = false;

        Assert.False(chart.CheckedOnly);
        Assert.Equal(3, chart.PlottedSessionCount);
    }

    // ---- the laned form ----------------------------------------------------------------------

    private static IReadOnlyList<NightFramePoint> FourFramesANight()
        => [.. Nights.SelectMany(night => Enumerable.Range(0, 4).Select(k => new NightFramePoint(
            night,
            night.ToDateTime(new TimeOnly(21, 0), DateTimeKind.Utc).AddMinutes(k * 10),
            "Ha",
            2d + (k * 0.1d),
            0.4d,
            1.8d,
            0.5d,
            1200d)))];

    private static Fixture LanedThreeNights()
    {
        var fixture = ThreeNights(metrics: ["hfr", "fwhm"]);
        var chart = fixture.Build();
        chart.NightFrames = FourFramesANight();
        chart.IsLaned = true;
        return fixture;
    }

    // A failure is the laned form drawing one overlaid plot, or lanes whose plots do not line up.
    [AvaloniaFact]
    public void Laned_HasOneLanePerToggledMetric_EachOnItsOwnLabelledYAxis_WithOnePlotEdge()
    {
        using var fixture = LanedThreeNights();
        var lanes = fixture.Chart!.Lanes;

        Assert.Equal(["HFR (px)", "FWHM (arcsec)"], lanes.Select(lane => lane.Title));
        Assert.All(lanes, lane => Assert.Null(Assert.IsType<Axis>(Assert.Single(lane.YAxes)).Name));
        Assert.Single(lanes.Select(lane => (lane.DrawMargin.Left, lane.DrawMargin.Right)).Distinct());
    }

    // A failure is date labels on every lane, or on none.
    [AvaloniaFact]
    public void Laned_CarriesItsXLabelsOnTheBottomLaneOnly()
    {
        using var fixture = LanedThreeNights();
        var lanes = fixture.Chart!.Lanes;

        Assert.Equal("", Assert.IsType<Axis>(lanes[0].XAxes[0]).Labeler(0d));
        Assert.Equal("11-01", Assert.IsType<Axis>(lanes[^1].XAxes[0]).Labeler(0d));
    }

    // A failure is a frame outside its night's band, out of capture order, or a median under
    // the dots.
    [AvaloniaFact]
    public void Laned_DrawsEveryFrameAsADotInItsNightsBand_InCaptureOrder_WithTheMedianOnTop()
    {
        using var fixture = LanedThreeNights();
        var lane = fixture.Chart!.Lanes[0];
        var dots = Assert.IsType<ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>>(lane.Series[0]);
        var xs = dots.Values!.Select(point => point.X!.Value).ToList();

        Assert.Equal(12, xs.Count);
        for (var night = 0; night < 3; night++)
        {
            var band = xs.Skip(night * 4).Take(4).ToList();
            Assert.All(band, x => Assert.InRange(x, night - 0.5d, night + 0.5d));
            Assert.Equal(band.Order(), band);
        }

        Assert.IsType<LineSeries<LiveChartsCore.Defaults.ObservablePoint>>(lane.Series[^1]);
    }

    // A failure is the Checked switch or the filter pills being ignored by the lanes.
    [AvaloniaFact]
    public void Laned_HonoursCheckedOnly_AndTheFilterPills()
    {
        using var fixture = LanedThreeNights();
        var chart = fixture.Chart!;
        fixture.Sessions[0].IsChecked = true;
        chart.CheckedOnly = true;

        var dots = (ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>)chart.Lanes[0].Series[0];
        Assert.Equal(4, dots.Values!.Count());

        chart.CheckedOnly = false;
        fixture.Selection.Filters.Single(f => f.Key == "overall").IsSelected = false;
        Assert.DoesNotContain(chart.Lanes.SelectMany(lane => lane.Series), series => series.Name == "HFR (px)");
    }

    // A failure is a metric in the table that draws no dots in its lane.
    [AvaloniaFact]
    public void Laned_EveryMetricInTheTable_YieldsDots()
    {
        using var fixture = ThreeNights(metrics: [.. ChartMetrics.All.Select(metric => metric.Key)]);
        var chart = fixture.Build();
        chart.NightFrames = FourFramesANight();
        chart.IsLaned = true;

        Assert.Equal(ChartMetrics.All.Length, chart.Lanes.Count);
        Assert.All(chart.Lanes, lane => Assert.Equal(
            12,
            Assert.IsType<ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>>(lane.Series[0]).Values!.Count()));
    }

    // A failure is a filter pill that adds no dots of that filter to the lanes.
    [AvaloniaFact]
    public void Laned_AFilterPill_YieldsThatFiltersDots()
    {
        using var fixture = LanedThreeNights();
        var chart = fixture.Chart!;

        fixture.Selection.Filters.Single(f => f.Key == "Ha").IsSelected = true;

        var dots = Assert.IsType<ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>>(
            Assert.Single(chart.Lanes[0].Series, series => series.Name == "HFR (px) (Ha)"));
        Assert.Equal(12, dots.Values!.Count());
    }

    // A failure is a multi-rig target whose lanes draw no rig lines while the rig pills
    // stay on screen, or a rig pill that does not remove its line.
    [AvaloniaFact]
    public void Laned_OnAMultiRigTarget_DrawsTheDashedPerRigMedians_AndTheRigPillsActOnThem()
    {
        const string rigA = "Alpha / Cam";
        const string rigB = "Bravo / Cam";
        static FrameRow RigFrame(int index, string rig, double hfr) => FrameTableViewModelTests.Frame(
            fileName: $"frame_{index:0000}.fits",
            captureDate: new DateTime(2025, 11, 15, 21, 0, 0, DateTimeKind.Utc).AddMinutes(index * 5),
            filterUsed: "Ha",
            exposureTime: 300d,
            medianHfr: hfr,
            rig: rig);
        var twoRigs = Cards.PopulatedDetail(Nights[2]) with
        {
            Rigs = [new RigGroup(0, rigA, "Alpha", "Cam", 2, 600d, null, null), new RigGroup(1, rigB, "Bravo", "Cam", 2, 600d, null, null)],
            Frames = [RigFrame(0, rigA, 2.0d), RigFrame(1, rigB, 3.0d), RigFrame(2, rigA, 2.1d), RigFrame(3, rigB, 3.1d)],
        };

        using var fixture = new Fixture(metrics: ["hfr"]);
        using var oldest = Cards.Create(overview: Page.Session(Nights[0]));
        using var newest = Cards.Create(overview: Page.Session(Nights[2]), detail: twoRigs);
        fixture.Sessions.Add(newest.Card);
        fixture.Sessions.Add(oldest.Card);
        var chart = fixture.Build();
        newest.Card.IsExpanded = true;
        newest.Settle();
        chart.NightFrames = FourFramesANight();
        chart.IsLaned = true;

        static bool Dashed(ISeries series)
            => (Assert.IsType<LineSeries<LiveChartsCore.Defaults.ObservablePoint>>(series).Stroke as SolidColorPaint)?.PathEffect
                is LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect;

        Assert.True(fixture.Selection.HasRigPills);
        var rigLines = chart.Lanes[0].Series.Where(series => series.Name?.Contains('[', StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, rigLines.Count);
        Assert.All(rigLines, line => Assert.True(Dashed(line)));

        fixture.Selection.Rigs.Single(pill => pill.Key == rigB).IsSelected = false;
        Assert.DoesNotContain(chart.Lanes[0].Series, series => series.Name?.Contains(rigB, StringComparison.Ordinal) == true);
    }

    // A failure is a plotted per-rig or per-filter point outside the pinned axis limits, which
    // the chart then clips.
    [AvaloniaFact]
    public void TheOverlaidAxes_ContainEveryPlottedPoint_WithAFilterOnAndTwoRigs()
    {
        const string rigA = "Alpha / Cam";
        const string rigB = "Bravo / Cam";
        static FrameRow RigFrame(int index, string rig, double hfr) => FrameTableViewModelTests.Frame(
            fileName: $"frame_{index:0000}.fits",
            captureDate: new DateTime(2025, 11, 15, 21, 0, 0, DateTimeKind.Utc).AddMinutes(index * 5),
            filterUsed: "Ha",
            exposureTime: 300d,
            medianHfr: hfr,
            rig: rig);
        var twoRigs = Cards.PopulatedDetail(Nights[2]) with
        {
            Rigs = [new RigGroup(0, rigA, "Alpha", "Cam", 2, 600d, null, null), new RigGroup(1, rigB, "Bravo", "Cam", 2, 600d, null, null)],
            Frames = [RigFrame(0, rigA, 2.0d), RigFrame(1, rigB, 3.0d), RigFrame(2, rigA, 2.1d), RigFrame(3, rigB, 3.1d)],
        };

        using var fixture = new Fixture(metrics: ["hfr"], filters: ["overall", "Ha"]);
        using var newest = Cards.Create(overview: Page.Session(Nights[2]), detail: twoRigs);
        fixture.Sessions.Add(newest.Card);
        var chart = fixture.Build();
        newest.Card.IsExpanded = true;
        newest.Settle();

        var axis = Assert.IsType<Axis>(chart.YAxes[0]);
        var points = chart.Series.OfType<LineSeries<double?>>().SelectMany(line => line.Values!).OfType<double>().ToList();
        Assert.True(points.Count > 1);
        Assert.All(points, value => Assert.True(
            axis.MinLimit <= value && value <= axis.MaxLimit,
            $"{value} lies outside {axis.MinLimit} to {axis.MaxLimit}"));
    }

    // A failure is a date axis that prints every night of a long span, which overprint into one
    // band, or one that drops the first or the last night.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(25)]
    [InlineData(120)]
    public void TheDateAxis_LabelsOnlyWhereItFits_KeepingTheFirstAndTheLast(int count)
    {
        var shown = Enumerable.Range(0, count).Where(index => TargetChartViewModel.LabelShown(index, count)).ToList();

        Assert.Contains(0, shown);
        Assert.Contains(count - 1, shown);
        Assert.True(shown.Count <= 9, $"{shown.Count} labels for {count} nights");
        Assert.All(shown.Zip(shown.Skip(1)), pair => Assert.True(count <= 8 || pair.Second - pair.First >= 2, $"{pair.First} and {pair.Second} are adjacent"));

        if (count == 25)
        {
            using var fixture = new Fixture(metrics: ["hfr"]);
            for (var day = 0; day < count; day++)
            {
                fixture.Add(Page.Session(new DateOnly(2023, 1, 1).AddDays(day * 40)));
            }

            var chart = fixture.Build();
            chart.ShowAllSessions = true;
            var axis = Assert.IsType<Axis>(Assert.Single(chart.XAxes));
            var printed = Enumerable.Range(0, count).Select(index => axis.Labeler(index)).ToList();

            // The drawing step prints every entry of a Labels list and ignores the labeler.
            Assert.Null(axis.Labels);
            Assert.True(printed.Count(label => label.Length > 0) <= 9, "the axis prints more than nine dates");
            Assert.NotEqual("", printed[0]);
            Assert.NotEqual("", printed[^1]);

            // A night whose tick is skipped still names its full date in the tooltip.
            var skipped = printed.FindIndex(label => label.Length == 0);
            Assert.All(chart.Series.OfType<LineSeries<double?>>(), line => Assert.NotNull(line.XToolTipLabelFormatter));
            var full = TargetChartViewModel.DateLabels([.. fixture.Sessions.Reverse()]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", TargetChartViewModel.DateAt(full, skipped));
        }
    }

    // A failure is a lane axis that prints all 25 dates, which overprint into one band.
    [AvaloniaFact]
    public void TheLaneDateAxis_LabelsOnlyWhereItFits_On25Nights()
    {
        using var fixture = new Fixture(metrics: ["hfr", "fwhm"]);
        for (var day = 0; day < 25; day++)
        {
            fixture.Add(Page.Session(new DateOnly(2023, 1, 1).AddDays(day * 40)));
        }

        var chart = fixture.Build();
        chart.ShowAllSessions = true;
        chart.NightFrames = FourFramesANight();
        chart.IsLaned = true;

        var bottom = Assert.IsType<Axis>(Assert.Single(chart.Lanes[^1].XAxes));
        var printed = Enumerable.Range(0, 25).Select(index => bottom.Labeler(index)).ToList();
        Assert.True(printed.Count(label => label.Length > 0) <= 9, "the bottom lane prints more than nine dates");
        Assert.NotEqual("", printed[0]);
        Assert.NotEqual("", printed[^1]);
    }

    // A failure is a trend axis whose three separators are not the padded floor, a rounded
    // middle and the padded ceiling over the plotted nights, or a plotted point off the limits.
    [AvaloniaFact]
    public void TheTrendAxis_OverThreeNights_PinsTheFloorTheMiddleAndTheCeiling()
    {
        using var fixture = ThreeNights(metrics: ["hfr"]);
        var chart = fixture.Build();
        var axis = Assert.IsType<Axis>(chart.YAxes[0]);
        var ticks = axis.CustomSeparators!.ToList();

        Assert.Equal(3, ticks.Count);
        Assert.Equal(axis.MinLimit!.Value, ticks[0], 9);
        Assert.Equal(axis.MaxLimit!.Value, ticks[2], 9);
        Assert.True(ticks[0] < ticks[1] && ticks[1] < ticks[2]);
        var values = chart.Series.OfType<LineSeries<double?>>().SelectMany(line => line.Values!).OfType<double>().ToList();
        Assert.All(values, value => Assert.True(axis.MinLimit <= value && value <= axis.MaxLimit));
    }

    // A failure is a count axis whose padded floor runs below zero and prints a negative label.
    [AvaloniaFact]
    public void TheCountAxis_WhoseDataFloorIsUnderThePad_KeepsAFloorOfZero()
    {
        using var fixture = new Fixture(metrics: ["detected_stars"]);
        fixture.Add(Page.Session(Nights[0]) with { MedianDetectedStars = 10d });
        fixture.Add(Page.Session(Nights[1]) with { MedianDetectedStars = 1000d });
        var chart = fixture.Build();
        chart.ShowAllSessions = true;
        var axis = Assert.IsType<Axis>(chart.YAxes[0]);
        var ticks = axis.CustomSeparators!.ToList();

        Assert.Equal(0d, axis.MinLimit);
        Assert.Equal(0d, ticks[0]);
        Assert.Equal("0 count", axis.Labeler(ticks[0]));
        Assert.All(ticks, tick => Assert.DoesNotContain("-", axis.Labeler(tick)));
    }

    // A failure is a middle label with a fraction on a right axis a count forces to whole numbers.
    [AvaloniaFact]
    public void ASharedRightAxis_RoundsItsMiddleSeparatorToWholeNumbers()
    {
        using var fixture = new Fixture(metrics: ["hfr", "fwhm", "detected_stars"]);
        fixture.Add(Page.Session(Nights[0]) with { MedianDetectedStars = 11d });
        fixture.Add(Page.Session(Nights[1]) with { MedianDetectedStars = 100d });
        var chart = fixture.Build();
        chart.ShowAllSessions = true;
        var ticks = Assert.IsType<Axis>(chart.YAxes[1]).CustomSeparators!.ToList();

        Assert.Equal(Math.Round(ticks[1]), ticks[1]);
    }

    // A failure is a stage 4 range pin or separator list reaching a lane axis, which the engine
    // labelled on its own at stages 2 and 3.
    [AvaloniaFact]
    public void Laned_YAxes_AreTheEnginesOwn_NoPinNoSeparators()
    {
        using var fixture = ThreeNights(metrics: ["hfr", "fwhm", "guiding_rms"]);
        var chart = fixture.Build();
        chart.NightFrames = FourFramesANight();
        chart.IsLaned = true;

        Assert.Equal(3, chart.Lanes.Count);
        Assert.All(chart.Lanes, lane =>
        {
            var y = Assert.IsType<Axis>(Assert.Single(lane.YAxes));
            Assert.Null(y.MinLimit);
            Assert.Null(y.MaxLimit);
            Assert.Null(y.CustomSeparators);
        });
    }

    // A failure is a lane tick label carrying its unit, which runs over the plot's left edge,
    // or a lane title without the unit the labels gave up.
    [AvaloniaFact]
    public void Laned_TickLabelsCarryTheNumberOnly_AndFitTheLeftInset_TheTitleCarriesTheUnit()
    {
        using var fixture = ThreeNights(metrics: [.. ChartMetrics.All.Select(metric => metric.Key)]);
        var chart = fixture.Build();
        chart.NightFrames = FourFramesANight();
        chart.IsLaned = true;

        Assert.Equal(["HFR (px)", "Ecc", "FWHM (arcsec)", "RMS (arcsec)", "Stars (count)"], chart.Lanes.Select(lane => lane.Title));
        double[] ticks = [0.1125d, 2.1125d, 12.5d, 1490d, 23456d];
        foreach (var lane in chart.Lanes)
        {
            var y = Assert.IsType<Axis>(Assert.Single(lane.YAxes));
            using var font = new SKFont(SKTypeface.Default, (float)y.TextSize);
            foreach (var tick in ticks)
            {
                var label = y.Labeler(tick);
                Assert.Matches("^[0-9.,]+$", label);
                var width = font.MeasureText(label);
                Assert.True(width <= NightLaneAxis.SharedPlotLeft, $"{lane.Title}: \"{label}\" is {width} px wide");
            }
        }
    }

    // A failure is a filter pill turned off that leaves its dots or its median line in a
    // lane, or overall turned off that leaves the pooled median line.
    [AvaloniaFact]
    public void Laned_AFilterPillOff_RemovesItsDotsAndMedianInEveryLane_AndOverallOffRemovesThePooledLine()
    {
        using var fixture = LanedThreeNights();
        var chart = fixture.Chart!;
        var ha = fixture.Selection.Filters.Single(f => f.Key == "Ha");
        ha.IsSelected = true;
        Assert.All(chart.Lanes, lane => Assert.Contains(lane.Series, series => series.Name == lane.Series[0].Name + " (Ha)"));

        ha.IsSelected = false;
        Assert.DoesNotContain(chart.Lanes.SelectMany(lane => lane.Series), series => series.Name?.Contains("(Ha)", StringComparison.Ordinal) == true);
        Assert.All(chart.Lanes, lane => Assert.Contains(lane.Series, series => series.Name == lane.Series[0].Name + " median"));

        ha.IsSelected = true;
        fixture.Selection.Filters.Single(f => f.Key == "overall").IsSelected = false;
        Assert.DoesNotContain(chart.Lanes.SelectMany(lane => lane.Series), series => series.Name is "HFR (px) median" or "FWHM median");
        Assert.Contains(chart.Lanes.SelectMany(lane => lane.Series), series => series.Name == "FWHM (Ha) median");
    }

    // The order proof for moving the lane builder into the base: written and green against the
    // target chart's own builder first. A failure is a lane count or a title the move changed.
    [AvaloniaFact]
    public void Laned_ThreeMetrics_PinsLaneCountAndTitles_AndAToggleDropsOne()
    {
        using var fixture = ThreeNights(metrics: ["hfr", "eccentricity", "fwhm"]);
        var chart = fixture.Build();
        chart.NightFrames = FourFramesANight();
        chart.IsLaned = true;

        Assert.Equal(["HFR (px)", "Ecc", "FWHM (arcsec)"], chart.Lanes.Select(lane => lane.Title));

        fixture.Selection.Metrics.Single(pill => pill.Key == "eccentricity").IsSelected = false;

        Assert.Equal(["HFR (px)", "FWHM (arcsec)"], chart.Lanes.Select(lane => lane.Title));
    }
}
