using CommunityToolkit.Mvvm.Input;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>Spec 12.14's Time Series smoothing segment, whose three buttons choose which of the
/// query's two moving averages is drawn over the points, or none. Not persisted.</summary>
public enum TimeSeriesSmoothing
{
    /// <summary>The nightly points alone.</summary>
    Raw,

    /// <summary>The 7 night moving average, <c>TimeSeriesResult.Ma7</c>.</summary>
    Ma7,

    /// <summary>The 30 night moving average, <c>TimeSeriesResult.Ma30</c>.</summary>
    Ma30,
}

/// <summary>
/// Spec 12.14's Time Series tab: one metric picker over all twenty metrics, a three-button
/// smoothing segment, and the nightly trend chart.
/// </summary>
/// <remarks>
/// <para>The granularity segment does not reach this tab: it is always nightly medians, and the
/// help topic says so because the web's popover says the opposite.</para>
/// <para>
/// Neither control persists. The metric re-queries, because the night rows are per metric; the
/// segment does not, because both averages arrive with every result and the segment only chooses
/// which of them is drawn. The last result is therefore held here, so switching the segment
/// redraws from it.
/// </para>
/// <para>
/// This tab carries the mixed plate scale warning exactly as the other three pixel tabs do (spec
/// 12.14's "When it is shown", which names the Time Series as the one an implementer drops): it
/// answers <see cref="IsPixelMetric"/>, copies the result's count onto the base's and calls
/// <c>NotifyPixelMetricChanged()</c> when the picker moves. The callout, its sentence and its
/// placement are the base's and are drawn once by <c>AnalysisView.axaml</c>.
/// </para>
/// </remarks>
public sealed class TimeSeriesTabViewModel : AnalysisTabViewModel<TimeSeriesResult>
{
    // The picker's twenty metrics, in AnalysisMetrics.X then .Y order, which is spec 12.14's own
    // metric table order (TimeSeriesTab.tsx lines 35 to 40). Built from the two seam lists rather
    // than from Enum.GetValues, which would offer twenty-five: the five PHD2 metrics are
    // Correlation only and the query refuses them.
    private static readonly IReadOnlyList<AnalysisMetricChoice> PickerChoices =
        AnalysisMetricLabels.Choices([.. AnalysisMetrics.X, .. AnalysisMetrics.Y]);

    private readonly Func<AnalysisMetric, AnalysisFilter, TimeSeriesResult> _query;

    private TimeSeriesResult? _result;
    private AnalysisMetric _metric = AnalysisMetric.Hfr;
    private TimeSeriesSmoothing _smoothing = TimeSeriesSmoothing.Raw;

    /// <param name="filter">The shared bar's current filter, null while the range is reversed.</param>
    /// <param name="query">Normally <c>AnalysisCache.TimeSeries</c>.</param>
    /// <param name="post">The post seam.</param>
    /// <param name="logger">Where a failed query is recorded.</param>
    public TimeSeriesTabViewModel(
        Func<AnalysisFilter?> filter,
        Func<AnalysisMetric, AnalysisFilter, TimeSeriesResult> query,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(AnalysisDisplay.TimeSeriesTab, "Time Series", "analysis.timeseries", filter, post, logger)
    {
        _query = query;
        SelectSmoothingCommand = new RelayCommand<TimeSeriesSmoothing>(SelectSmoothing);
    }

    /// <summary>The chart, built with the body on the tab's first refresh and kept with its data
    /// afterwards. Null only for a tab never selected, which is what makes one cost nothing.
    /// </summary>
    public TimeSeriesChartViewModel? Chart { get; private set; }

    /// <summary>The metric picker's twenty entries, the page's one choice shape. One stable list,
    /// never rebuilt: a bound option list replaced under a two-way selection drops the selection
    /// (Phase 15B).</summary>
    public IReadOnlyList<AnalysisMetricChoice> MetricChoices => PickerChoices;

    /// <summary>The chosen metric, all twenty offered, defaulting to <c>hfr</c>. Moving it
    /// re-queries, because the night rows are per metric, and re-evaluates the mixed plate scale
    /// callout without waiting for that query to land.</summary>
    public AnalysisMetric Metric
    {
        get => _metric;
        set
        {
            if (_metric == value)
            {
                return;
            }

            _metric = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMetricChoice));

            // The count has not moved, so the base cannot know its own answer changed.
            NotifyPixelMetricChanged();
            Refresh();
        }
    }

    /// <summary>The picker's two-way selection, matched on the metric the entry carries and never
    /// on its label or its position. A cleared selection moves nothing: raising the property
    /// restores the entry the picker had.</summary>
    public AnalysisMetricChoice? SelectedMetricChoice
    {
        get => PickerChoices.FirstOrDefault(choice => choice.Metric == Metric);
        set => AnalysisMetricChoice.Apply(
            value,
            Metric,
            metric => Metric = metric,
            () => OnPropertyChanged(nameof(SelectedMetricChoice)));
    }

    /// <summary>Which of the two moving averages is drawn over the points, or none. Switching
    /// redraws from the held result and queries nothing: both averages arrive with every
    /// result.</summary>
    public TimeSeriesSmoothing Smoothing
    {
        get => _smoothing;
        set => SelectSmoothing(value);
    }

    /// <summary>The segment's three buttons, each passing its own member.</summary>
    public IRelayCommand<TimeSeriesSmoothing> SelectSmoothingCommand { get; }

    /// <summary>The segment's current marker, one property per button as the filter bar's
    /// granularity segment has.</summary>
    public bool IsRaw => Smoothing == TimeSeriesSmoothing.Raw;

    /// <inheritdoc cref="IsRaw"/>
    public bool IsMa7 => Smoothing == TimeSeriesSmoothing.Ma7;

    /// <inheritdoc cref="IsRaw"/>
    public bool IsMa30 => Smoothing == TimeSeriesSmoothing.Ma30;

    /// <summary>Spec 12.14's "When it is shown": on Time Series the pixel metric is the chosen
    /// one. This tab is in that list and is the one an implementer drops.</summary>
    protected override bool IsPixelMetric => Metric == AnalysisMetric.Hfr;

    /// <inheritdoc/>
    protected override object CreateBody()
    {
        Chart = new TimeSeriesChartViewModel();
        OnPropertyChanged(nameof(Chart));
        return Chart;
    }

    /// <inheritdoc/>
    protected override TimeSeriesResult? Query(AnalysisFilter filter) => _query(Metric, filter);

    /// <inheritdoc/>
    protected override AnalysisTabState Map(TimeSeriesResult? result)
    {
        _result = result;

        if (result is not { Points.Count: > 0 })
        {
            Chart?.Clear();
            return AnalysisTabState.Empty;
        }

        // The count is assigned only on the arm that read rows: the base clears it before every
        // mapping and on a failure, so an early arm that left the previous result's count standing
        // would warn over rows it never read.
        DistinctPlateScales = result.DistinctPlateScales;
        Draw(result);
        return AnalysisTabState.Ready;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        base.Dispose();
        Chart?.Dispose();
    }

    private void SelectSmoothing(TimeSeriesSmoothing smoothing)
    {
        if (_smoothing == smoothing)
        {
            return;
        }

        _smoothing = smoothing;
        OnPropertyChanged(nameof(Smoothing));
        OnPropertyChanged(nameof(IsRaw));
        OnPropertyChanged(nameof(IsMa7));
        OnPropertyChanged(nameof(IsMa30));

        if (_result is { Points.Count: > 0 })
        {
            Draw(_result);
        }
    }

    // Both averages arrive computed and this tab plots the chosen one verbatim: it holds no
    // window, no sum and no window length, because the query sums the nightly medians already
    // rounded to 6 and an average taken again here would differ in the sixth decimal on every
    // point (analysis.py line 721).
    private void Draw(TimeSeriesResult result) => Chart?.Update(
        result.Points,
        _smoothing switch
        {
            TimeSeriesSmoothing.Ma7 => result.Ma7,
            TimeSeriesSmoothing.Ma30 => result.Ma30,
            _ => [],
        },
        Metric);
}
