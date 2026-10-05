using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Theme;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// Spec 13's "Correlation scatter" row: the Correlation tab's four drawn layers, in the one series
/// order that makes them read as a picture rather than as a wedge.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is load bearing.</b> rc5.4 has no series that fills the region between two curves
/// (chart spike section 3), so the confidence band is an upper-bound area filled at a low alpha
/// with an opaque area painted over everything below the lower bound. The mask must come after the
/// band and both must come before the points and the trend, or the band renders as a wedge down to
/// the axis floor.
/// </para>
/// <para>
/// <b>The chart's own background is <c>bg-elevated</c> and so is the mask</b> (ruling A23), which
/// is the one exception to spec 13's transparent chart rule. The token is named twice, here and in
/// <c>CorrelationTabView.axaml</c>, and the two must agree; that is why the mask takes the token's
/// own colour verbatim rather than forcing its alpha to opaque.
/// </para>
/// <para>
/// Every token is read through <c>ChartTheme.Read</c> and never through <c>ChartTheme.Palette</c>
/// (ruling P1-4): <c>ColorMetricWorst</c> is not one of the ten keys in
/// <c>ChartTheme.MetricTokenOrder</c>, and one door for all four keeps a reader from having to know
/// which of them the palette happens to carry. The reads happen inside <see cref="Rebuild"/>, which
/// runs on the UI thread through the tab's post seam and again on every
/// <c>ChartTheme.Changed</c>; the subscription is held as one token and disposed with this
/// view-model, because <c>ChartTheme.Changed</c> is static and a handler left attached pins its
/// view-model for the life of the process (<c>ChartTheme.cs</c>'s own remark).
/// </para>
/// <para>
/// No axis sets a paint: the global axis rule supplies the label and separator ink and re-reads it
/// on a theme change. The four tooltip and legend paints are the shared chart paint style's,
/// assigned by <c>Theme/Controls.axaml</c> on the control, and are never set on a series here.
/// </para>
/// </remarks>
public sealed partial class CorrelationChartViewModel : ObservableObject, IDisposable
{
    /// <summary>The band fill and the trend stroke, spec 13's <c>metric-hfr</c>.</summary>
    public const string BandTokenKey = "ColorMetricHfr";

    /// <summary>The mask fill, spec 13's <c>bg-elevated</c>, which is also the chart's own
    /// background.</summary>
    public const string MaskTokenKey = "ColorBgElevated";

    /// <summary>The ordinary point fill, spec 13's <c>metric-stars</c>.</summary>
    public const string PointTokenKey = "ColorMetricStars";

    /// <summary>The outlier ring stroke, spec 13's <c>metric-worst</c>. Not a member of
    /// <c>ChartTheme.MetricTokenOrder</c>, which is why every token here goes through
    /// <c>ChartTheme.Read</c>.</summary>
    public const string OutlierTokenKey = "ColorMetricWorst";

    /// <summary>Spec 13: "a session-granularity point draws at 6 pixels and a frame one at 4".</summary>
    public const double SessionGeometrySize = 6d;

    /// <inheritdoc cref="SessionGeometrySize"/>
    public const double FrameGeometrySize = 4d;

    /// <summary>The outlier ring, two pixels larger than an ordinary point so a ring reads as a
    /// ring rather than as a hollow dot (chart spike, kind 1).</summary>
    public const double OutlierGeometrySize = 8d;

    /// <summary>The outlier ring's stroke, and the trend line's.</summary>
    public const float StrokeThicknessPixels = 2f;

    /// <summary>The web's own stand-in for a target it cannot name (pinned
    /// <c>CorrelationChart.tsx</c> line 189).</summary>
    public const string UnknownTarget = "Unknown";

    // The band's fill alpha, spec 13's "filled metric-hfr at 0x40 alpha".
    private const byte BandAlpha = 0x40;

    // The web draws no trend below three points (CorrelationChart.tsx line 138) and Analysis.Trend
    // answers null below the same count, so the two agree and this gate is belt beside braces.
    private const int MinimumTrendPoints = 3;

    private readonly IDisposable _themeSubscription;

    // The four resolved tokens, read on the UI thread at construction and again on every
    // ChartTheme.Changed, and HELD (coordinator ruling after the Time Series unit's finding).
    // ChartTheme.Read degrades to ChartTheme.Fallback off the UI thread and throws nothing, so a
    // rebuild that resolved its own tokens would paint every layer the neutral grey whenever the
    // post seam ran it on a thread-pool thread, and a case asserting "paint equals
    // ChartTheme.Read(token)" on that same thread would compare the fallback with itself and read
    // green. Reading once, here, makes the thread the read happens on a property of this type
    // rather than of whichever seam the caller passed.
    private SKColor _bandColour;
    private SKColor _maskColour;
    private SKColor _pointColour;
    private SKColor _outlierColour;

    private CorrelationResult? _result;
    private AnalysisMetric _x = AnalysisMetric.Humidity;
    private AnalysisMetric _y = AnalysisMetric.Hfr;
    private AnalysisGranularity _granularity = AnalysisGranularity.Frame;
    private bool _outliersHidden;
    private bool _disposed;

    /// <summary>Builds the empty state, for the reason <see cref="PublishEmpty"/> documents, and
    /// subscribes to the theme.</summary>
    public CorrelationChartViewModel()
    {
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        IsEmpty = true;

        ReadTokens();
        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>The band, the mask, the ordinary points, the outlier rings and the trend, in that
    /// order. The two band layers are absent when the result carries no confidence band and the
    /// trend is absent below three points, so the two point series are the only ones always
    /// present.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    /// <summary>One axis, titled with the X metric's full label and setting no paint.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> XAxes { get; private set; }

    /// <inheritdoc cref="XAxes"/>
    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> YAxes { get; private set; }

    /// <summary>True while there is nothing to plot.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Draws one result. Every figure on it was computed over the full frame set, before
    /// any downsampling and before the outlier toggle was read.</summary>
    /// <param name="result">The query's answer, or null for the empty state.</param>
    /// <param name="x">The X metric, for the axis title.</param>
    /// <param name="y">The Y metric, for the axis title.</param>
    /// <param name="granularity">Which of the two geometry sizes the points take.</param>
    /// <param name="outliersHidden">Whether the flagged points are dropped from the drawn series.
    /// </param>
    public void Update(
        CorrelationResult? result,
        AnalysisMetric x,
        AnalysisMetric y,
        AnalysisGranularity granularity,
        bool outliersHidden)
    {
        _result = result;
        _x = x;
        _y = y;
        _granularity = granularity;
        _outliersHidden = outliersHidden;
        Rebuild();
    }

    /// <summary>
    /// Spec 12.14's Hide Outliers toggle: the flagged points leave the drawn series and nothing
    /// else changes, because the trend, the band and both stats cards were computed before the
    /// toggle was read (<c>CorrelationTab.tsx</c> lines 89 to 94). It issues no query and re-reads
    /// the result this view-model already holds.
    /// </summary>
    public void SetOutliersHidden(bool hidden)
    {
        _outliersHidden = hidden;
        Rebuild();
    }

    /// <summary>Drops the result and publishes the empty state.</summary>
    public void Clear() => Update(null, _x, _y, _granularity, _outliersHidden);

    /// <summary>
    /// The hover label, <c>CorrelationChart.tsx</c> line 190 verbatim: the target's name, the
    /// night as an ISO date, then X to one decimal and Y to two. Both formats are the web's and
    /// they differ from each other.
    /// </summary>
    /// <remarks>The name is "Unknown" both for a null target id and for an id the result's map
    /// does not resolve, which is what pinned line 189 does.</remarks>
    public static string Tooltip(CorrelationPoint point, IReadOnlyDictionary<Guid, string> targetNames)
    {
        var name = point.TargetId is { } id && targetNames.TryGetValue(id, out var resolved)
            ? resolved
            : UnknownTarget;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} ({1}): {2:F1}, {3:F2}",
            name,
            point.Night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            point.X,
            point.Y);
    }

    /// <summary>The static theme outlives every chart, so a chart that does not unsubscribe keeps
    /// rebuilding after its page has closed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _themeSubscription.Dispose();
    }

    private void Rebuild()
    {
        if (_result is not { Points.Count: > 0 } result)
        {
            PublishEmpty();
            return;
        }

        // The four colours are the ones ReadTokens resolved on the UI thread, never a read taken
        // here: this method runs wherever the caller's post seam put it.
        var bandColour = _bandColour;
        var maskColour = _maskColour;
        var pointColour = _pointColour;
        var outlierColour = _outlierColour;

        // The web filters the point list before the chart splits it, so hiding the outliers leaves
        // the ordinary series untouched and empties the ring series.
        CorrelationPoint[] ordinary = [.. result.Points.Where(point => !point.Outlier)];
        CorrelationPoint[] flagged = _outliersHidden
            ? []
            : [.. result.Points.Where(point => point.Outlier)];

        var series = new List<ISeries>(5);
        var trend = result.Trend;

        // Spec 12.14's States table: at one or two points the points draw and the band and the
        // trend do not, whatever the result happens to carry. The band's own emptiness gate is the
        // web's (CorrelationChart.tsx line 118) and rides beside it.
        var hasTrend = trend is not null && result.Points.Count >= MinimumTrendPoints;

        if (hasTrend && trend is { ConfidenceUpper.Count: > 0, ConfidenceLower.Count: > 0 })
        {
            // ONE floor for both areas, and the mask at full alpha: the two rules BandAreas exists
            // for, and the two this tab had wrong. The floor is taken over the FULL point set, not
            // the drawn one, so hiding the outliers cannot move it and the band is byte for byte
            // what it was before the toggle.
            var floor = BandAreas.Floor(
                trend.ConfidenceLower.Select(bound => bound.Y),
                result.Points.Select(point => point.Y));

            series.Add(new LineSeries<ObservablePoint>
            {
                Values = [.. trend.ConfidenceUpper.Select(bound => new ObservablePoint(bound.X, bound.Y))],
                Fill = new SolidColorPaint(bandColour.WithAlpha(BandAlpha)),
                Stroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                IsHoverable = false,
                Pivot = floor,
            });

            series.Add(new LineSeries<ObservablePoint>
            {
                Values = [.. trend.ConfidenceLower.Select(bound => new ObservablePoint(bound.X, bound.Y))],
                Fill = new SolidColorPaint(BandAreas.OpaqueMask(maskColour)),
                Stroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                IsHoverable = false,
                Pivot = floor,
            });
        }

        var geometry = _granularity == AnalysisGranularity.Session
            ? SessionGeometrySize
            : FrameGeometrySize;

        series.Add(new ScatterSeries<ObservablePoint>
        {
            Values = [.. ordinary.Select(point => new ObservablePoint(point.X, point.Y))],
            Fill = new SolidColorPaint(pointColour),
            Stroke = null,
            GeometrySize = geometry,
            XToolTipLabelFormatter = _ => LiveCharts.IgnoreToolTipLabel,
            YToolTipLabelFormatter = chartPoint => Label(ordinary, chartPoint.Index, result.TargetNames),
        });

        // CircleGeometry is named explicitly: the one-argument form takes the theme's default
        // geometry, which is a circle today and is not a contract (chart spike, kind 1).
        series.Add(new ScatterSeries<ObservablePoint, CircleGeometry>
        {
            Values = [.. flagged.Select(point => new ObservablePoint(point.X, point.Y))],
            Fill = null,
            Stroke = new SolidColorPaint(outlierColour) { StrokeThickness = StrokeThicknessPixels },
            GeometrySize = OutlierGeometrySize,
            XToolTipLabelFormatter = _ => LiveCharts.IgnoreToolTipLabel,
            YToolTipLabelFormatter = chartPoint => Label(flagged, chartPoint.Index, result.TargetNames),
        });

        if (hasTrend && trend is not null)
        {
            // Drawn from the smallest plotted X to the largest, over the FULL point set, so hiding
            // the outliers never shortens the line.
            var smallest = result.Points.Min(point => point.X);
            var largest = result.Points.Max(point => point.X);

            series.Add(new LineSeries<ObservablePoint>
            {
                Values =
                [
                    new ObservablePoint(smallest, (trend.Slope * smallest) + trend.Intercept),
                    new ObservablePoint(largest, (trend.Slope * largest) + trend.Intercept),
                ],
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                // Not hoverable, for the same reason the two band layers are not: spec 12.14 gives
                // the tooltip to a POINT. A line series' hover area is a full axis unit wide, and
                // this one carries no tooltip formatter, so under the chart's declared
                // CompareOnlyXTakeClosest strategy it would answer a raw default tooltip at both
                // ends of the line.
                IsHoverable = false,
                Stroke = new SolidColorPaint(bandColour) { StrokeThickness = StrokeThicknessPixels },
            });
        }

        Series = series;
        XAxes = [new Axis { Name = AnalysisMetricLabels.Label(_x) }];
        YAxes = [new Axis { Name = AnalysisMetricLabels.Label(_y) }];
        IsEmpty = false;
    }

    // Spec 13's "re-read on theme change". The tokens are resolved first and the series rebuilt
    // from the new values, both on the thread ChartTheme.Changed is raised on, which is the UI
    // thread in the application and in every case that swaps a theme.
    private void OnThemeChanged()
    {
        ReadTokens();
        Rebuild();
    }

    private void ReadTokens()
    {
        _bandColour = ChartTheme.Read(BandTokenKey, ChartTheme.Fallback);
        _maskColour = ChartTheme.Read(MaskTokenKey, ChartTheme.Fallback);
        _pointColour = ChartTheme.Read(PointTokenKey, ChartTheme.Fallback);
        _outlierColour = ChartTheme.Read(OutlierTokenKey, ChartTheme.Fallback);
    }

    // The formatter is handed the position of the point in the collection it was drawn from, which
    // is the array the series was built over. Out of range answers nothing rather than throwing: a
    // tooltip is not worth taking a page down for.
    private static string Label(
        IReadOnlyList<CorrelationPoint> points,
        int index,
        IReadOnlyDictionary<Guid, string> targetNames)
        => index >= 0 && index < points.Count ? Tooltip(points[index], targetNames) : string.Empty;

    /// <summary>
    /// The empty state: no series, and one bare axis on each side rather than none. rc5.4's
    /// <c>CartesianChartEngine.Measure</c> throws "XAxes and YAxes must contain at least one
    /// element" for every series type, scatter included (spec 13, trap 11), and the control
    /// measures while it is collapsed, so an empty state with no axes takes the page down instead
    /// of rendering nothing. Fresh axis instances per call, because an axis carries measure state.
    /// <c>ChartEmptyAxisCensusTest</c> checks this rule on every chart view-model, not only this
    /// one.
    /// </summary>
    private void PublishEmpty()
    {
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        IsEmpty = true;
    }
}
