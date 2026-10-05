using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// One of spec 12.14's six Correlation presets: a label, the pair it sets and whether that pair is
/// the current one.
/// </summary>
public sealed partial class CorrelationPreset : ObservableObject
{
    internal CorrelationPreset(string label, AnalysisMetric x, AnalysisMetric y, Action<CorrelationPreset> apply)
    {
        Label = label;
        X = x;
        Y = y;
        Command = new RelayCommand(() => apply(this));
    }

    /// <summary>The button's text, <c>CorrelationTab.tsx</c> lines 34 to 41.</summary>
    public string Label { get; }

    /// <summary>The X metric this preset sets.</summary>
    public AnalysisMetric X { get; }

    /// <summary>The Y metric this preset sets.</summary>
    public AnalysisMetric Y { get; }

    /// <summary>Sets both pickers at once and fires one refresh.</summary>
    public IRelayCommand Command { get; }

    /// <summary>Whether this preset's pair is the current pair, which draws it selected.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Spec 12.14's Correlation tab: six presets, the X and Y pickers, the Hide Outliers toggle, the
/// scatter with its confidence band, the verdict sentence and the two stats cards.
/// </summary>
/// <remarks>
/// <para>
/// The five members the base requires are at the bottom of this file and the rest is the tab's own
/// controls. Nothing here re-implements the load loop, the stale marking, the per tab failure, the
/// reversed range or the mixed plate scale callout: all six are the base's.
/// </para>
/// <para>
/// <b>The controls are built with the tab, not with the first result.</b> Spec 12.14 keeps every
/// picker, preset and toggle live in every state, including the empty one, because a reader who
/// filtered the chart into emptiness has to be able to filter back out of it. Building them in the
/// field initializers rather than inside <see cref="CreateBody"/> makes that structural: no binding
/// can observe a null chart or an empty option list at any point in the tab's life.
/// <see cref="CreateBody"/> still answers a non-null body, so the base's lazy rule is untouched and
/// nothing is queried or painted for a tab that is never visited.
/// </para>
/// <para>
/// This tab never touches <c>DisplaySettings</c>. Both stored metric keys are written in one call
/// through the page's own writer, reached as <see cref="PersistMetrics"/>.
/// </para>
/// </remarks>
public sealed class CorrelationTabViewModel : AnalysisTabViewModel<CorrelationResult>
{
    /// <summary>Spec 12.14's PHD2 join note, verbatim, shown under the pickers whenever the chosen
    /// X metric is one of the five night metrics. Secondary ink and a caption, not a callout: it is
    /// a statement about the join and not a warning, and it can be on screen at the same time as
    /// the base's mixed plate scale callout.</summary>
    public const string Phd2NoteText =
        "Night-level PHD2 figures joined by rig and imaging night; nights with no mapped PHD2 "
        + "profile are omitted.";

    /// <summary>The "Guiding (PHD2)" group header of the X picker, the port of the web's
    /// <c>optgroup</c> label.</summary>
    public const string Phd2GroupHeader = "Guiding (PHD2)";

    /// <summary>The toggle's label while the outliers are drawn: it states the action it will
    /// perform, which is the web's own wording.</summary>
    public const string HideOutliersLabel = "Hide Outliers";

    /// <inheritdoc cref="HideOutliersLabel"/>
    public const string ShowOutliersLabel = "Show Outliers";

    /// <summary>
    /// Everything in the page's scroll viewport that is NOT this tab's chart, at the reading order
    /// spec 12.14 lays out. The chart's height is the viewport less this, floored by the converter,
    /// so the scatter grows with the window instead of sitting in a fixed letterbox.
    /// </summary>
    /// <remarks>
    /// Measured at 1280 by 800 through the shell, not estimated. It sums what the page spends above
    /// the chart (its heading, the shared filter card, the tab strip, this tab's heading rule, the
    /// preset row and the picker row) and what this tab spends below it (the verdict with its point
    /// count, the disclaimer, the two stats cards and the four gaps between them), plus the margin
    /// that keeps the cards clear of the fold rather than exactly on it. The layout cases measure
    /// the result; this figure is one edit to change and never a reason to edit them.
    /// </remarks>
    public const double ChartFixedSpend = 528d;

    /// <summary>The floor the converter clamps to, which is what the chart is at the two smaller
    /// standard window sizes. It equals <c>ChartHeightConverter.MinimumHeight</c>, the smallest of
    /// the page's other charts.</summary>
    public const double ChartMinimumHeight = 280d;

    // Spec 12.14's sampling sentence, with both counts group separated. The condition is
    // SampledCount < TotalCount and nothing else: the 5,000 cap is the query's and a
    // second copy of it here would be wrong on a 40-of-41 answer.
    private const string SamplingFormat =
        "Showing {0} of {1} frames (sampled for display; trend and statistics use all frames)";

    private readonly Func<AnalysisMetric, AnalysisMetric, AnalysisFilter, CorrelationResult> _query;
    private readonly Action<AnalysisMetric, AnalysisMetric>? _persistMetrics;

    private AnalysisMetric _xMetric;
    private AnalysisMetric _yMetric;

    // Which geometry size the points take. Read off the filter inside Query, which is the only
    // member the base hands one to, and consumed by Map on the UI thread after the query's task
    // has completed.
    private AnalysisGranularity _granularity = AnalysisGranularity.Frame;

    private bool _outliersHidden;
    private bool _phd2NoteVisible;
    private bool _statsVisible;
    private string _verdict = string.Empty;
    private string _pointCount = string.Empty;
    private string _samplingSentence = string.Empty;
    private StatsCardViewModel _xCard = new(null);
    private StatsCardViewModel _yCard = new(null);

    /// <param name="filter">The shared bar's current filter, null while the range is reversed.</param>
    /// <param name="query">Normally <c>AnalysisCache.Correlation</c>, bound in <c>AppHost</c>. A
    /// delegate and never the cache, which is sealed with no interface.</param>
    /// <param name="x">The stored <c>display.analysis.x_metric</c>, already read through
    /// <see cref="AnalysisDisplay.XMetric(string)"/>. The X picker starts here.</param>
    /// <param name="y">The stored <c>display.analysis.y_metric</c>, read through
    /// <see cref="AnalysisDisplay.YMetric(string)"/>.</param>
    /// <param name="persistMetrics">Writes both <c>display.analysis</c> metric keys when the
    /// pickers move. Null writes nothing, which is what a unit test wants.</param>
    /// <param name="post">The post seam, normally <c>UiPost.Default</c>.</param>
    /// <param name="logger">Where a failed query is recorded.</param>
    public CorrelationTabViewModel(
        Func<AnalysisFilter?> filter,
        Func<AnalysisMetric, AnalysisMetric, AnalysisFilter, CorrelationResult> query,
        AnalysisMetric x = AnalysisMetric.Humidity,
        AnalysisMetric y = AnalysisMetric.Hfr,
        Action<AnalysisMetric, AnalysisMetric>? persistMetrics = null,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(AnalysisDisplay.CorrelationTab, "Correlation", "analysis.correlation", filter, post, logger)
    {
        _query = query;
        _persistMetrics = persistMetrics;

        // Spec 12.14's preset table, which is CorrelationTab.tsx lines 34 to 41.
        Presets =
        [
            new CorrelationPreset("Humidity vs HFR", AnalysisMetric.Humidity, AnalysisMetric.Hfr, ApplyPreset),
            new CorrelationPreset("Airmass vs FWHM", AnalysisMetric.Airmass, AnalysisMetric.Fwhm, ApplyPreset),
            new CorrelationPreset("Wind vs Guiding", AnalysisMetric.WindSpeed, AnalysisMetric.GuidingRms, ApplyPreset),
            new CorrelationPreset("Temp vs Eccentricity", AnalysisMetric.AmbientTemp, AnalysisMetric.Eccentricity, ApplyPreset),
            new CorrelationPreset("Sky Quality vs Stars", AnalysisMetric.SkyQuality, AnalysisMetric.DetectedStars, ApplyPreset),
            new CorrelationPreset("Guiding vs FWHM", AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Fwhm, ApplyPreset),
        ];

        ToggleOutliersCommand = new RelayCommand(ToggleOutliers);

        // Seeding writes nothing: the stored pair is where the pickers START, and a picker that
        // fired its writer while seeding itself would save a value nobody chose.
        _xMetric = x;
        _yMetric = y;
        UpdateDerivedControls();
    }

    /// <summary>The X axis metric. Moving the picker sets this, writes both stored keys and
    /// re-evaluates the base's pixel-metric answer; the Matrix route sets it directly and drives
    /// the refresh itself (<c>AnalysisViewModel.OpenCorrelationOn</c>).</summary>
    public AnalysisMetric XMetric
    {
        get => _xMetric;
        set
        {
            if (!SetProperty(ref _xMetric, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedXOption));
            UpdateDerivedControls();
        }
    }

    /// <summary>The Y axis metric, which is also what decides the mixed plate scale warning.
    /// </summary>
    public AnalysisMetric YMetric
    {
        get => _yMetric;
        set
        {
            if (!SetProperty(ref _yMetric, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedYOption));
            UpdateDerivedControls();
        }
    }

    /// <summary>The scatter, its confidence band and its two axes.</summary>
    public CorrelationChartViewModel Chart { get; } = new();

    /// <summary>Spec 12.14's six presets, in the web's order. Built once, in the constructor,
    /// because each one holds a command that calls back into this tab.</summary>
    public IReadOnlyList<CorrelationPreset> Presets { get; }

    /// <summary>The X picker's entries: the ten X metrics, the "Guiding (PHD2)" header, then the
    /// five PHD2 night metrics. An <c>ObservableCollection</c> built once and never replaced.
    /// </summary>
    public ObservableCollection<AnalysisMetricChoice> XOptions { get; } =
    [
        .. AnalysisMetricLabels.Choices(AnalysisMetrics.X),
        AnalysisMetricLabels.Header(Phd2GroupHeader),
        .. AnalysisMetricLabels.Choices(AnalysisMetrics.Phd2X),
    ];

    /// <summary>The Y picker's entries, the ten Y metrics. Built once and never replaced.</summary>
    public ObservableCollection<AnalysisMetricChoice> YOptions { get; } =
        [.. AnalysisMetricLabels.Choices(AnalysisMetrics.Y)];

    /// <summary>The X picker's two-way selection. Assigning the group header moves nothing and
    /// queries nothing: the setter restores the previous entry instead.</summary>
    public AnalysisMetricChoice? SelectedXOption
    {
        get => XOptions.FirstOrDefault(option => option.Metric == XMetric);
        set => Select(value, XMetric, metric => XMetric = metric, nameof(SelectedXOption));
    }

    /// <inheritdoc cref="SelectedXOption"/>
    public AnalysisMetricChoice? SelectedYOption
    {
        get => YOptions.FirstOrDefault(option => option.Metric == YMetric);
        set => Select(value, YMetric, metric => YMetric = metric, nameof(SelectedYOption));
    }

    /// <summary>Whether the flagged points are currently hidden.</summary>
    public bool OutliersHidden
    {
        get => _outliersHidden;
        private set
        {
            if (SetProperty(ref _outliersHidden, value))
            {
                OnPropertyChanged(nameof(OutliersToggleLabel));
            }
        }
    }

    /// <summary>The toggle's label, which states the action the button will perform.</summary>
    public string OutliersToggleLabel => OutliersHidden ? ShowOutliersLabel : HideOutliersLabel;

    /// <summary>Removes the flagged points from the drawn series and from nothing else. Issues no
    /// query: it re-maps the result the chart already holds.</summary>
    public IRelayCommand ToggleOutliersCommand { get; }

    /// <summary>Whether the chosen X metric is one of the five PHD2 night metrics, which is when
    /// <see cref="Phd2NoteText"/> is shown.</summary>
    public bool Phd2NoteVisible
    {
        get => _phd2NoteVisible;
        private set => SetProperty(ref _phd2NoteVisible, value);
    }

    /// <summary>Spec 12.14's verdict sentence for the last result, or empty before there is one.
    /// </summary>
    public string Verdict
    {
        get => _verdict;
        private set => SetProperty(ref _verdict, value);
    }

    /// <summary>
    /// The result's point count, drawn in the tertiary ink after the verdict sentence on every
    /// band, which is pinned <c>CorrelationChart.tsx</c>'s own
    /// <c>&lt;span class="opacity-60"&gt; ({points.length} points)&lt;/span&gt;</c> and spec 12.14
    /// line 7679's "The point count follows in the tertiary ink". Empty before there is a result.
    /// </summary>
    /// <remarks>It is the FULL set's count and never the drawn one, so Hide Outliers does not move
    /// it: spec 12.14 line 7660 promises that the counts are unchanged, and the web's own count is
    /// the one figure that clause corrects.</remarks>
    public string PointCount
    {
        get => _pointCount;
        private set => SetProperty(ref _pointCount, value);
    }

    /// <summary>Spec 12.14's fixed disclaimer, drawn under the verdict.</summary>
    public static string Disclaimer => CorrelationVerdict.Disclaimer;

    /// <summary>The sampling sentence, or empty when the set was not sampled.</summary>
    public string SamplingSentence
    {
        get => _samplingSentence;
        private set => SetProperty(ref _samplingSentence, value);
    }

    /// <summary>The X axis summary card.</summary>
    public StatsCardViewModel XCard
    {
        get => _xCard;
        private set => SetProperty(ref _xCard, value);
    }

    /// <summary>The Y axis summary card.</summary>
    public StatsCardViewModel YCard
    {
        get => _yCard;
        private set => SetProperty(ref _yCard, value);
    }

    /// <summary>Whether the pair of cards is drawn. Both or neither: pinned
    /// <c>CorrelationTab.tsx</c> line 192 gates on both stat sets being present, so one flag and
    /// not two.</summary>
    public bool StatsVisible
    {
        get => _statsVisible;
        private set => SetProperty(ref _statsVisible, value);
    }

    /// <summary>Writes both stored metric keys, in one call, through the page's own display
    /// writer.</summary>
    public void PersistMetrics() => _persistMetrics?.Invoke(XMetric, YMetric);

    /// <inheritdoc/>
    public override void Dispose()
    {
        base.Dispose();
        Chart.Dispose();
    }

    /// <summary>Spec 12.14's "When it is shown": on Correlation the pixel metric is the <b>Y</b>
    /// one.</summary>
    protected override bool IsPixelMetric => YMetric == AnalysisMetric.Hfr;

    /// <inheritdoc/>
    protected override object CreateBody() => Chart;

    /// <inheritdoc/>
    protected override CorrelationResult? Query(AnalysisFilter filter)
    {
        _granularity = filter.Granularity;
        return _query(XMetric, YMetric, filter);
    }

    /// <inheritdoc/>
    protected override AnalysisTabState Map(CorrelationResult? result)
    {
        // The sentence keys on the two counts the result carries and never on the cap.
        SamplingSentence = result is { } answer && answer.SampledCount < answer.TotalCount
            ? string.Format(
                CultureInfo.InvariantCulture,
                SamplingFormat,
                answer.SampledCount.ToString("N0", CultureInfo.InvariantCulture),
                answer.TotalCount.ToString("N0", CultureInfo.InvariantCulture))
            : string.Empty;

        // The base clears DistinctPlateScales before every mapping, so this arm, which read no
        // rows, assigns nothing.
        if (result is not { Points.Count: > 0 })
        {
            Chart.Clear();
            Verdict = string.Empty;
            PointCount = string.Empty;
            XCard = new StatsCardViewModel(null);
            YCard = new StatsCardViewModel(null);
            StatsVisible = false;
            return AnalysisTabState.Empty;
        }

        DistinctPlateScales = result.DistinctPlateScales;

        Chart.Update(result, XMetric, YMetric, _granularity, OutliersHidden);
        Verdict = CorrelationVerdict.Describe(result.Trend, result.Points.Count, XMetric, YMetric);
        PointCount = string.Format(
            CultureInfo.InvariantCulture, "({0} points)", result.Points.Count);

        var xLabels = AnalysisMetricLabels.For(XMetric);
        var yLabels = AnalysisMetricLabels.For(YMetric);
        XCard = new StatsCardViewModel(result.XStats, $"X: {xLabels.Label}", xLabels.Unit);
        YCard = new StatsCardViewModel(result.YStats, $"Y: {yLabels.Label}", yLabels.Unit);
        StatsVisible = result.XStats is not null && result.YStats is not null;

        return AnalysisTabState.Ready;
    }

    private void ToggleOutliers()
    {
        OutliersHidden = !OutliersHidden;
        Chart.SetOutliersHidden(OutliersHidden);
    }

    // One picker move: set the metric, write both stored keys once and refresh. The header
    // refusal, the cleared selection and the unchanged metric are the one shared rule's
    // (AnalysisMetricChoice.Apply); the setters the assignment reaches re-evaluate the plate scale
    // callout on their way through UpdateDerivedControls.
    private void Select(
        AnalysisMetricChoice? option,
        AnalysisMetric current,
        Action<AnalysisMetric> assign,
        string propertyName)
        => AnalysisMetricChoice.Apply(
            option,
            current,
            metric =>
            {
                assign(metric);
                PersistMetrics();
                Refresh();
            },
            () => OnPropertyChanged(propertyName));

    // A preset sets BOTH pickers and fires ONE refresh: the two backing fields are written first
    // and everything that follows is raised once (spec 12.14).
    private void ApplyPreset(CorrelationPreset preset)
    {
        // The preset that already draws selected changes nothing, writes nothing and queries
        // nothing, which is the guard Select already has and what the web's two unchanged signal
        // writes do.
        if (preset.X == _xMetric && preset.Y == _yMetric)
        {
            return;
        }

        _xMetric = preset.X;
        _yMetric = preset.Y;

        OnPropertyChanged(nameof(XMetric));
        OnPropertyChanged(nameof(YMetric));
        OnPropertyChanged(nameof(SelectedXOption));
        OnPropertyChanged(nameof(SelectedYOption));
        UpdateDerivedControls();

        PersistMetrics();
        Refresh();
    }

    // Everything that follows the current pair is answered here rather than at each of the sites
    // that move it: the note, the preset marks and the base's mixed plate scale callout, whose
    // other half is the Y metric and whose count does not move with it. The page's Matrix route
    // assigns the two metrics directly (AnalysisViewModel.OpenCorrelationOn) and both setters end
    // here, so no route can forget the callout.
    private void UpdateDerivedControls()
    {
        // Membership of the seam's own list, never a prefix test on the enum's name: the two agree
        // today and only one of them stays true when a metric is added.
        Phd2NoteVisible = AnalysisMetrics.Phd2X.Contains(XMetric);

        foreach (var preset in Presets)
        {
            preset.IsSelected = preset.X == XMetric && preset.Y == YMetric;
        }

        NotifyPixelMetricChanged();
    }
}
