using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Theme;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>Which axis of a <see cref="StatsBarChartViewModel"/> carries the categories.</summary>
public enum StatsChartOrientation
{
    /// <summary>Vertical bars: categories on X, values on Y. Spec 13's "Column" rows.</summary>
    Column,

    /// <summary>Horizontal bars: categories on Y, values on X. Spec 13's "Horizontal bar" rows.
    /// </summary>
    Bar,
}

/// <summary>
/// One series of a <see cref="StatsBarChartViewModel"/>: a name, one value per category, and where
/// its colour comes from.
/// </summary>
/// <param name="Values">One entry per category label, in label order. A null entry draws no bar,
/// which is how a per-category tint is expressed (see <see cref="StatsBarChartViewModel"/>'s
/// remarks).</param>
/// <param name="TokenKey">A spec 14.1 <c>metric-*</c> resource key, resolved through
/// <c>ChartTheme.Palette</c> on every build so a theme swap re-colours the series. Null only when
/// <paramref name="Colour"/> is given.</param>
/// <param name="Colour">An already-resolved colour, for the one legitimate non-token case: a
/// filter's configured tint, which is user data rather than a theme token (spec 14.5).</param>
public sealed record StatsChartSeries(
    string Name,
    IReadOnlyList<double?> Values,
    string? TokenKey = null,
    SKColor? Colour = null);

/// <summary>
/// The shared spine behind spec 13's six bar and column charts on the Statistics page: Filter
/// usage, Top targets, the two HFR histograms, Equipment performance comparison and Ingest
/// history. One categorical axis, one value axis, one or more column or row series, a colour per
/// series and an empty state; the six differ only in their data, their orientation and their value
/// labeller.
/// </summary>
/// <remarks>
/// <para>
/// Built once rather than six times, per design-lessons rule 1 (extract the spine at the second
/// occurrence, and this screen has six). The two Statistics charts that are not built from it are
/// the imaging timeline, which carries a second Y axis for the efficiency line, and the storage
/// pie, which has no axes at all.
/// </para>
/// <para>
/// Nothing here spells a colour. Axis label and separator paints come from
/// <c>ChartTheme</c>'s global axis rule and are deliberately never set on an axis built below
/// (spec 13: the configuration is global and re-read on theme change). The series paints are
/// resolved from <c>ChartTheme.Palette</c> on every <see cref="Rebuild"/>, and
/// <c>ChartTheme.Changed</c> triggers one, exactly as <c>MetricChartViewModel</c> does.
/// </para>
/// <para>
/// A per-category tint (Filter usage, where each bar is the filter's own colour) is expressed as
/// one series per category whose <see cref="StatsChartSeries.Values"/> array is null everywhere
/// except at that category's index. LiveCharts draws nothing for a null, so the result is N bars
/// in N colours on one categorical axis. The alternative, a per-point paint callback, does not
/// survive a series replacement in rc5.4.
/// </para>
/// <para>
/// FIXER LIST item 10: <see cref="PublishEmpty"/> publishes one bare axis per side rather than
/// none. rc5.4's <c>CartesianChartEngine.Measure</c> throws "XAxes and YAxes must contain at least
/// one element", and the control measures while it is collapsed, so an empty state with no axes
/// takes the page down instead of rendering the empty message.
/// </para>
/// </remarks>
public sealed partial class StatsBarChartViewModel : ObservableObject, IDisposable
{
    // FIXER LIST F8: the theme subscription and its unsubscribe as one token. The unsubscribe is
    // the half that matters: ChartTheme.Changed is static, so a handler left behind pins this
    // view-model for the life of the process.
    private readonly IDisposable _themeSubscription;

    private readonly StatsChartOrientation _orientation;
    private readonly Func<double, string> _valueLabeler;

    private IReadOnlyList<string> _labels = [];
    private IReadOnlyList<StatsChartSeries> _definitions = [];
    private bool _disposed;

    /// <param name="title">Spec 13's row name for this chart.</param>
    /// <param name="orientation">Which axis carries the categories.</param>
    /// <param name="emptyMessage">What the view renders in place of the chart when there is
    /// nothing to plot.</param>
    /// <param name="valueLabeler">How a value-axis tick renders. Defaults to a thousands-separated
    /// integer, which is what the two histograms and the ingest history want; the two integration
    /// charts pass an hours labeller.</param>
    public StatsBarChartViewModel(
        string title,
        StatsChartOrientation orientation,
        string emptyMessage,
        Func<double, string>? valueLabeler = null)
    {
        Title = title;
        _orientation = orientation;
        EmptyMessage = emptyMessage;
        _valueLabeler = valueLabeler ?? (value => value.ToString("N0", CultureInfo.InvariantCulture));

        // Seeded before anything can bind, for the reason PublishEmpty documents.
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        IsEmpty = true;

        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>Spec 13's row name for this chart.</summary>
    public string Title { get; }

    /// <summary>What the view renders in place of the chart when <see cref="IsEmpty"/>.</summary>
    public string EmptyMessage { get; }

    /// <summary>Which axis carries the categories. Exposed so a test can assert the orientation
    /// spec 13's table names for each chart.</summary>
    public StatsChartOrientation Orientation => _orientation;

    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> XAxes { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> YAxes { get; private set; }

    /// <summary>True when there is nothing to plot. The view renders
    /// <see cref="EmptyMessage"/> instead of the chart.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>
    /// Replaces the chart's data. The definitions are kept so a later theme change re-resolves
    /// every token without the page reloading.
    /// </summary>
    /// <param name="labels">The category labels, in axis order.</param>
    /// <param name="series">One or more series, each carrying one value per label.</param>
    public void Update(IReadOnlyList<string> labels, IReadOnlyList<StatsChartSeries> series)
    {
        _labels = labels;
        _definitions = series;
        Rebuild();
    }

    /// <summary>Drops the data and publishes the empty state.</summary>
    public void Clear() => Update([], []);

    private void Rebuild()
    {
        if (_labels.Count == 0
            || _definitions.Count == 0
            || !_definitions.Any(definition => definition.Values.Any(value => value is not null)))
        {
            PublishEmpty();
            return;
        }

        // A row series draws index 0 at the bottom, so a list the caller ordered "largest first"
        // would render upside down. Reversed here, once, rather than in each of the two horizontal
        // bar charts, which is the whole point of the spine.
        var reverse = _orientation == StatsChartOrientation.Bar;
        var labels = reverse ? _labels.Reverse().ToArray() : [.. _labels];

        var categorical = new Axis
        {
            Labels = labels,
            MinStep = 1,
            ForceStepToMin = true,
        };

        var values = new Axis
        {
            Labeler = _valueLabeler,

            // Bars are read against a zero baseline; without this a set of similar values renders
            // as wildly different bar lengths against an auto-scaled floor.
            MinLimit = 0,
        };

        var built = new List<ISeries>(_definitions.Count);
        foreach (var definition in _definitions)
        {
            var ordered = reverse ? definition.Values.Reverse().ToArray() : [.. definition.Values];
            built.Add(Build(definition, ordered));
        }

        Series = built;
        IsEmpty = false;
        if (_orientation == StatsChartOrientation.Column)
        {
            XAxes = [categorical];
            YAxes = [values];
        }
        else
        {
            XAxes = [values];
            YAxes = [categorical];
        }
    }

    private ISeries Build(StatsChartSeries definition, IReadOnlyList<double?> values)
    {
        var colour = definition.Colour
            ?? (definition.TokenKey is { } key && ChartTheme.Palette.Metrics.TryGetValue(key, out var token)
                ? token
                : ChartTheme.Fallback);

        // A fresh paint per series: LiveCharts paints carry per-zone animation state, so two
        // series sharing one instance is a rendering defect waiting to happen (the reason
        // MetricChartViewModel.Line gives).
        var fill = new SolidColorPaint(colour);

        return _orientation == StatsChartOrientation.Column
            ? new ColumnSeries<double?>
            {
                Name = definition.Name,
                Values = values,
                Fill = fill,
                Stroke = null,
            }
            : new RowSeries<double?>
            {
                Name = definition.Name,
                Values = values,
                Fill = fill,
                Stroke = null,
            };
    }

    /// <summary>
    /// The empty state: no series, and one bare axis on each side rather than none (FIXER LIST
    /// item 10). Fresh axis instances per call, because an axis carries measure state and two
    /// charts must not share one.
    /// </summary>
    private void PublishEmpty()
    {
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        IsEmpty = true;
    }

    /// <summary>Spec 13's "re-read on theme change": the paints already handed to the control are
    /// replaced by rebuilding from the definitions this view-model kept.</summary>
    private void OnThemeChanged() => Rebuild();

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
}
