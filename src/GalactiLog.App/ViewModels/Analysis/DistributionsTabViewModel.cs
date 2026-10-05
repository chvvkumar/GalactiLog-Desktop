using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// Spec 12.14's Distributions tab: a two-button segment over a histogram half and a box plot half,
/// each with its own metric picker, its own last result and, for the box plot, its own grouping
/// picker.
/// </summary>
/// <remarks>
/// <para>
/// It is the one tab whose segment issues two different queries, so the base's type argument is
/// <see cref="object"/> and <see cref="Map"/> reads whichever of the two results the current half
/// answered. Both delegates are held from construction, so this file adds no constructor argument.
/// </para>
/// <para>
/// <b>The two halves keep their own controls and their own last result</b> (spec 12.14). Switching
/// the segment redraws from the held result and queries only when that half has never loaded or
/// when its held result was read under a different filter, metric or grouping, which is why each
/// half records the filter its result came from rather than sharing the base's one
/// <c>IsStale</c> flag: a filter change refreshes the VISIBLE half and leaves the other half's
/// result behind, and redrawing that stale result on a segment switch would show the reader
/// figures the filter bar no longer describes.
/// </para>
/// <para>
/// This tab computes no statistic and orders nothing. The bins, the summary, the skewness, the
/// quartiles, the four-value group gate and the ascending ordinal group order are all
/// the query's, which <see cref="BoxPlotResult.Groups"/>'s own summary now promises; a second sort
/// here would silently overrule the query's the day that order changed deliberately.
/// </para>
/// </remarks>
public sealed partial class DistributionsTabViewModel : AnalysisTabViewModel<object>
{
    /// <summary>Spec 12.14's pixel-domain note, verbatim. The first clause is spec 12.14's own and
    /// is in no web file; the second and third are <c>PIXEL_METRIC_NOTE</c> at pinned
    /// <c>metricLabels.ts</c> lines 77 to 79. It is a caption in secondary ink and
    /// is NOT replaced by the base's mixed plate scale warning: the note says HFR is a per-train
    /// unit, the warning says this particular selection is currently mixing two of them, and when
    /// both apply both are shown with the warning above.</summary>
    public const string PixelNoteText =
        "Values are per-train pixel-domain units, not comparable across optical trains. HFR is "
        + "measured in pixels and is only comparable within a single optical train. Use FWHM in "
        + "arcseconds for cross-telescope comparison.";

    /// <summary>Spec 12.14's States table, "Distributions with fewer than 2 values". The web
    /// answers HTTP 400 here; a page state is the port's equivalent.</summary>
    public const string TooFewToBinText =
        "The current filters leave too few frames to bin. Widen them in the filter bar above.";

    /// <summary>
    /// Spec 12.14's States table, "Box plot with no group left" (<c>docs/design-spec.md</c> line
    /// 7977: "The tab states that no group has enough frames"). Rows matched and every group was
    /// dropped at the query's four-value gate, which is a DIFFERENT state from
    /// <see cref="EmptyMessage"/>'s: no rows calls for widening the filters, and no group left
    /// calls for grouping more coarsely, so shipping the no-rows sentence here told the reader to
    /// do the one thing that makes the state worse. The pinned web has no counterpart at all.
    /// </summary>
    public const string BoxNoGroupText =
        "No group has enough frames. Group by something coarser, or narrow the dates to a period "
        + "with more frames per group.";

    /// <summary>The histogram picker's metrics, all twenty in <c>AnalysisMetrics.X</c> then
    /// <c>.Y</c> order (<c>DistributionsTab.tsx</c> lines 12 to 17). The five PHD2 metrics are not
    /// offered: they are Correlation only and the query would refuse them.</summary>
    public static readonly IReadOnlyList<AnalysisMetric> HistogramMetrics =
        [.. AnalysisMetrics.X, .. AnalysisMetrics.Y];

    /// <summary>The box plot picker's metrics, the ten <c>AnalysisMetrics.Y</c>.</summary>
    public static readonly IReadOnlyList<AnalysisMetric> BoxMetrics = AnalysisMetrics.Y;

    /// <summary>Spec 12.14's four Group by entries, in <see cref="BoxPlotGrouping"/>'s own ordinal
    /// order so the picker's index IS the enum member.</summary>
    public static readonly IReadOnlyList<string> GroupByLabels =
        ["By Filter", "By Equipment", "By Month", "By Target"];

    private readonly Func<AnalysisFilter?> _filter;
    private readonly Func<AnalysisMetric, AnalysisFilter, DistributionResult?> _distribution;
    private readonly Func<AnalysisMetric, BoxPlotGrouping, AnalysisFilter, BoxPlotResult> _boxPlot;

    private AnalysisMetric _histogramMetric = AnalysisMetric.Hfr;
    private AnalysisMetric _boxMetric = AnalysisMetric.Hfr;
    private BoxPlotGrouping _groupBy = BoxPlotGrouping.Filter;
    private bool _isBoxPlot;

    // One query's answer with the filter it was computed under, so the two travel together.
    private sealed record Answer(object? Result, AnalysisFilter? Filter);

    private string _tooFew = TooFewToBinText;

    private DistributionResult? _histogram;
    private AnalysisFilter? _histogramFilter;
    private bool _histogramLoaded;

    private BoxPlotResult? _box;
    private AnalysisFilter? _boxFilter;
    private bool _boxLoaded;

    private StatsCardViewModel _card = new(null);

    /// <param name="filter">The shared bar's current filter, null while the range is reversed.</param>
    /// <param name="distribution">Normally <c>AnalysisCache.Distribution</c>, which answers null
    /// below two values.</param>
    /// <param name="boxPlot">Normally <c>AnalysisCache.BoxPlot</c>.</param>
    /// <param name="post">The post seam.</param>
    /// <param name="logger">Where a failed query is recorded.</param>
    public DistributionsTabViewModel(
        Func<AnalysisFilter?> filter,
        Func<AnalysisMetric, AnalysisFilter, DistributionResult?> distribution,
        Func<AnalysisMetric, BoxPlotGrouping, AnalysisFilter, BoxPlotResult> boxPlot,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(AnalysisDisplay.DistributionsTab, "Distributions", "analysis.distributions", filter, post, logger)
    {
        // The base keeps its own copy for the load loop; this one answers the pixel-domain note's
        // "has the reader selected both a telescope and a camera", which is a question about the
        // bar and not about a result.
        _filter = filter;
        _distribution = distribution;
        _boxPlot = boxPlot;
    }

    /// <summary>The histogram half's chart.</summary>
    public HistogramChartViewModel Histogram { get; } = new();

    /// <summary>The box plot half's chart, the one box plot type this page has.</summary>
    public BoxPlotChartViewModel BoxPlot { get; } = new();

    /// <summary>The histogram's one stats card, labelled
    /// "&lt;metric label&gt; (skewness: x.xx)". Hidden by its own <c>IsVisible</c> while there is
    /// no summary.</summary>
    public StatsCardViewModel Card
    {
        get => _card;
        private set => SetProperty(ref _card, value);
    }

    /// <summary>Whether the segment is showing the box plot rather than the histogram.</summary>
    public bool IsBoxPlot
    {
        get => _isBoxPlot;
        set => SelectHalf(value);
    }

    /// <summary>Whether the segment is showing the histogram, which is what the Histogram button's
    /// <c>current</c> class binds to.</summary>
    public bool IsHistogram => !_isBoxPlot;

    /// <summary>The chosen metric of the CURRENT half, which is what the query is issued for. Each
    /// half keeps its own, so a metric chosen on the box plot does not move the histogram's.
    /// </summary>
    public AnalysisMetric Metric
    {
        get => _isBoxPlot ? _boxMetric : _histogramMetric;
        set
        {
            if (_isBoxPlot)
            {
                BoxMetric = value;
            }
            else
            {
                HistogramMetric = value;
            }
        }
    }

    /// <summary>The histogram half's own metric, one of <see cref="HistogramMetrics"/>.</summary>
    public AnalysisMetric HistogramMetric
    {
        get => _histogramMetric;
        set
        {
            if (_histogramMetric == value)
            {
                return;
            }

            _histogramMetric = value;
            _histogram = null;
            _histogramLoaded = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedHistogramMetricChoice));
            MetricChanged(reload: !_isBoxPlot);
        }
    }

    /// <summary>The box plot half's own metric, one of <see cref="BoxMetrics"/>.</summary>
    public AnalysisMetric BoxMetric
    {
        get => _boxMetric;
        set
        {
            if (_boxMetric == value)
            {
                return;
            }

            _boxMetric = value;
            _box = null;
            _boxLoaded = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedBoxMetricChoice));
            MetricChanged(reload: _isBoxPlot);
        }
    }

    /// <summary>The box plot's grouping.</summary>
    public BoxPlotGrouping GroupBy
    {
        get => _groupBy;
        set
        {
            if (_groupBy == value)
            {
                return;
            }

            _groupBy = value;
            _box = null;
            _boxLoaded = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GroupByIndex));

            // By Equipment widens the scope to every optical train the filters admit, which is one
            // of spec 12.14's two arms for the note.
            OnPropertyChanged(nameof(PixelNoteVisible));

            if (_isBoxPlot)
            {
                Refresh();
            }
        }
    }

    /// <summary>The histogram picker's entries, in <see cref="HistogramMetrics"/> order. Every
    /// label is <see cref="AnalysisMetricLabels"/>'s and none is spelled here. One
    /// stable list, never rebuilt (Phase 15B).</summary>
    public static IReadOnlyList<AnalysisMetricChoice> HistogramMetricChoices { get; } =
        AnalysisMetricLabels.Choices(HistogramMetrics);

    /// <summary>The box plot picker's entries, in <see cref="BoxMetrics"/> order.</summary>
    public static IReadOnlyList<AnalysisMetricChoice> BoxMetricChoices { get; } =
        AnalysisMetricLabels.Choices(BoxMetrics);

    /// <summary>The histogram picker's two-way selection, matched on the metric the entry carries
    /// and never on its label or its position. A cleared selection moves nothing.</summary>
    public AnalysisMetricChoice? SelectedHistogramMetricChoice
    {
        get => HistogramMetricChoices.FirstOrDefault(choice => choice.Metric == _histogramMetric);
        set => AnalysisMetricChoice.Apply(
            value,
            _histogramMetric,
            metric => HistogramMetric = metric,
            () => OnPropertyChanged(nameof(SelectedHistogramMetricChoice)));
    }

    /// <summary>The box plot picker's selection, under the same rule.</summary>
    public AnalysisMetricChoice? SelectedBoxMetricChoice
    {
        get => BoxMetricChoices.FirstOrDefault(choice => choice.Metric == _boxMetric);
        set => AnalysisMetricChoice.Apply(
            value,
            _boxMetric,
            metric => BoxMetric = metric,
            () => OnPropertyChanged(nameof(SelectedBoxMetricChoice)));
    }

    /// <summary>The Group by picker's selection. <see cref="GroupByLabels"/> is in the enum's own
    /// ordinal order, so the index IS the member.</summary>
    public int GroupByIndex
    {
        get => (int)_groupBy;
        set
        {
            if (value >= 0 && value < GroupByLabels.Count)
            {
                GroupBy = (BoxPlotGrouping)value;
            }
        }
    }

    /// <summary>
    /// Spec 12.14's pixel-domain note: the chosen metric is <c>hfr</c> AND the scope can span more
    /// than one optical train, which is when the filter bar has not selected both a telescope and
    /// a camera or the box plot is grouped By Equipment.
    /// </summary>
    /// <remarks>A reversed range answers a null filter, which has selected neither name and
    /// therefore spans, so the note stands while the sentence under the date fields does.</remarks>
    public bool PixelNoteVisible =>
        Metric == AnalysisMetric.Hfr
        && ((_isBoxPlot && _groupBy == BoxPlotGrouping.Equipment)
            || _filter() is not { Telescope: not null, Camera: not null });

    /// <summary>Selects the histogram half.</summary>
    [RelayCommand]
    private void ShowHistogram() => SelectHalf(false);

    /// <summary>Selects the box plot half.</summary>
    [RelayCommand]
    private void ShowBoxPlot() => SelectHalf(true);

    /// <summary>Spec 12.14's "When it is shown": on the histogram and the box plot the pixel metric
    /// is the chosen one.</summary>
    protected override bool IsPixelMetric => Metric == AnalysisMetric.Hfr;

    /// <inheritdoc/>
    /// <remarks>Written by the half that answered, because the two halves have different too-few
    /// sentences: the histogram's is spec 12.14's "fewer than 2 values" row and the box plot's is
    /// its "no group left" row, and those two say opposite things about what the reader should do
    /// next. The one-field shape is <c>CompareTabViewModel</c>'s own.</remarks>
    protected override string TooFewMessage => _tooFew;

    /// <inheritdoc/>
    protected override object CreateBody() => new();

    /// <inheritdoc/>
    /// <remarks>The filter travels WITH the result rather than in a field of its own. A field
    /// written here, on the query's own thread-pool thread, and read in
    /// <see cref="Map"/> on the UI thread is not safe against two queries whose start order is not
    /// the order they were issued in: query 2 may run first and query 1 may then overwrite the
    /// field before publish 2 runs, so the answering half would record a filter its result was
    /// never computed under.</remarks>
    protected override object? Query(AnalysisFilter filter) => new Answer(
        _isBoxPlot
            ? _boxPlot(_boxMetric, _groupBy, filter)
            : _distribution(_histogramMetric, filter),
        filter);

    /// <inheritdoc/>
    protected override AnalysisTabState Map(object? result)
    {
        // The note's second clause reads the filter BAR, not the result, and a bar change reaches
        // this tab only as a refresh, so every published result re-answers it. Without this the
        // note stood over a chart drawn for one optical train after the reader picked a telescope
        // and a camera, and stayed hidden after they widened the bar back to all equipment.
        OnPropertyChanged(nameof(PixelNoteVisible));

        // Query always answers an Answer; the arm exists so a hand-built result cannot throw here
        // and fail the tab through the base's mapper catch.
        var (inner, filter) = result as Answer ?? new Answer(result, null);

        switch (inner)
        {
            case DistributionResult distribution:
                _histogram = distribution;
                _histogramLoaded = true;
                _histogramFilter = filter;
                return DrawHistogram();

            case BoxPlotResult boxPlot:
                _box = boxPlot;
                _boxLoaded = true;
                _boxFilter = filter;
                return DrawBoxPlot();

            default:
                // AnalysisCache.Distribution answers null below two values, which spec 12.14's
                // States table calls "too few frames to bin" and never "no rows".
                if (_isBoxPlot)
                {
                    _box = null;
                    _boxLoaded = true;
                    _boxFilter = filter;
                    return DrawBoxPlot();
                }

                _histogram = null;
                _histogramLoaded = true;
                _histogramFilter = filter;
                return DrawHistogram();
        }
    }

    /// <summary>Disposes the two charts, which is what drops their theme subscriptions: the theme
    /// event is static and a handler left behind pins its chart for the life of the process.
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
        Histogram.Dispose();
        BoxPlot.Dispose();
    }

    private void MetricChanged(bool reload)
    {
        OnPropertyChanged(nameof(Metric));
        OnPropertyChanged(nameof(PixelNoteVisible));

        // The count did not move, so the base's callout has to re-evaluate its other half.
        NotifyPixelMetricChanged();

        if (reload)
        {
            Refresh();
        }
    }

    private void SelectHalf(bool boxPlot)
    {
        if (_isBoxPlot == boxPlot)
        {
            return;
        }

        _isBoxPlot = boxPlot;
        OnPropertyChanged(nameof(IsBoxPlot));
        OnPropertyChanged(nameof(IsHistogram));
        OnPropertyChanged(nameof(Metric));
        OnPropertyChanged(nameof(PixelNoteVisible));
        NotifyPixelMetricChanged();

        var current = _filter();
        var loaded = boxPlot ? _boxLoaded : _histogramLoaded;
        var used = boxPlot ? _boxFilter : _histogramFilter;

        // Spec 12.14: the half's own last result is redrawn and nothing is queried, unless this
        // half has never loaded or its result predates the bar's current filter.
        if (!loaded || current is null || used != current)
        {
            Refresh();

            // A reversed range queries nothing, and the base's reversed-range arm writes no state
            // at all once a result has been drawn, so the OUTGOING half's state would stand over
            // the incoming half's chart: an empty box plot with the result region still open and
            // no sentence under it. The incoming half is drawn from whatever it holds instead.
            //
            // What the reader is left with depends on the state and never on the route they took
            // to it. A half that holds a drawn result is drawn, with no sentence, which is what
            // reversing the range without touching the segment shows here and on the other four
            // tabs (spec 12.14: the last drawn result stays on screen) while the filter bar carries
            // the date error. A half that holds nothing, whether it loaded no rows or never loaded
            // at all, closes the result region and takes the bar's own sentence: the no-rows and
            // too-few sentences both send the reader to widen or regroup filters that are not the
            // problem while the dates are reversed. The flag has to be set here because the load
            // loop above answers it for the tab as a whole, and the tab as a whole has published.
            if (current is null)
            {
                DistinctPlateScales = 0;
                var held = boxPlot ? DrawBoxPlot() : DrawHistogram();
                var drawn = held == AnalysisTabState.Ready;

                State = drawn ? held : AnalysisTabState.NotLoaded;
                SetRangeReversed(!drawn);
            }

            return;
        }

        // The base clears the count before every mapping it drives; a redraw
        // that bypasses the load loop owes the same clear.
        DistinctPlateScales = 0;
        State = boxPlot ? DrawBoxPlot() : DrawHistogram();
    }

    private AnalysisTabState DrawHistogram()
    {
        if (_histogram is null)
        {
            Histogram.Clear();
            Card = new StatsCardViewModel(null);
            _tooFew = TooFewToBinText;
            return AnalysisTabState.TooFew;
        }

        DistinctPlateScales = _histogram.DistinctPlateScales;
        Histogram.Show(_histogram, _histogramMetric);
        Card = new StatsCardViewModel(
            _histogram.Stats,
            CardLabel(_histogramMetric, _histogram.Skewness),
            AnalysisMetricLabels.Unit(_histogramMetric));

        return _histogram.Bins.Count > 0 ? AnalysisTabState.Ready : AnalysisTabState.Empty;
    }

    private AnalysisTabState DrawBoxPlot()
    {
        if (_box is null)
        {
            BoxPlot.Clear();
            return AnalysisTabState.Empty;
        }

        DistinctPlateScales = _box.DistinctPlateScales;
        if (_box.Groups.Count == 0)
        {
            BoxPlot.Clear();

            // Spec 12.14's two distinct States rows, told apart by the one figure the seam now
            // carries: rows matched and every group was dropped at the query's four-value gate,
            // or no row matched at all. They call for opposite actions, which is why the seam
            // gained RowCount rather than the tab guessing.
            if (_box.RowCount <= 0)
            {
                return AnalysisTabState.Empty;
            }

            _tooFew = BoxNoGroupText;
            return AnalysisTabState.TooFew;
        }

        // In the query's own order, which is ascending ordinal by group name and is the seam's
        // documented promise (the tab sorted a second time, and the day the query's
        // order changed deliberately the tab would have silently overruled it).
        BoxPlot.Show(_box.Groups, _boxMetric);

        return AnalysisTabState.Ready;
    }

    private static string CardLabel(AnalysisMetric metric, double skewness) => string.Create(
        CultureInfo.InvariantCulture,
        $"{AnalysisMetricLabels.Label(metric)} (skewness: {skewness:F2})");
}
