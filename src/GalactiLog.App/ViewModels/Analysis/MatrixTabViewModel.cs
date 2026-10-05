using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// Spec 12.14's Matrix tab: a hundred-cell heat grid over every metric pair, with no control of
/// its own.
/// </summary>
/// <remarks>
/// It is the one tab whose constructor carries an argument beyond the base's own:
/// <see cref="OpenCorrelationOn"/> is the page's <c>AnalysisViewModel.OpenCorrelationOn</c>,
/// supplied as a delegate so the tab holds no page reference and this file never opens
/// <c>AnalysisViewModel.cs</c>.
/// </remarks>
public sealed class MatrixTabViewModel : AnalysisTabViewModel<MatrixResult>
{
    /// <summary>Spec 12.14's caption, verbatim, drawn above the grid and outside the result
    /// region so it stands in every state. The first and last sentences are the web's own
    /// (<c>MatrixTab.tsx</c> line 69); the middle one is the port's, because the port removed the
    /// hover that carried it.</summary>
    public const string CaptionText =
        "Pearson r for all metric pairs. A pair needs at least 10 frames carrying both metrics; "
        + "below that the cell is blank. Click a cell to explore in the Correlation tab.";

    /// <summary>The legend's negative end, beside a swatch in the <c>ColorInfo</c> token.</summary>
    public const string NegativeLegendText = "r = -1: the two move in opposite directions";

    /// <summary>The legend's positive end, beside a swatch in the <c>ColorMetricWorst</c> token.
    /// </summary>
    public const string PositiveLegendText = "r = +1: the two move together";

    /// <summary>The legend's third entry, which has no swatch because a blank cell draws nothing.
    /// </summary>
    public const string BlankLegendText = "Blank: no value for that pair";

    private readonly Func<AnalysisFilter, MatrixResult> _query;

    // The same logger the base takes, kept so the grid can record a malformed result. The base
    // holds its own copy privately, so this is a second reference to one object rather than a
    // second seam.
    private readonly ILogger? _logger;

    private MatrixChartViewModel? _chart;

    /// <param name="filter">The shared bar's current filter, null while the range is reversed.</param>
    /// <param name="query">Normally <c>AnalysisCache.Matrix</c>.</param>
    /// <param name="openCorrelationOn">The page's own navigation, invoked with the clicked cell's
    /// <c>(x, y)</c> pair. It writes both stored metric keys and selects the Correlation tab.</param>
    /// <param name="post">The post seam.</param>
    /// <param name="logger">Where a failed query is recorded.</param>
    public MatrixTabViewModel(
        Func<AnalysisFilter?> filter,
        Func<AnalysisFilter, MatrixResult> query,
        Action<AnalysisMetric, AnalysisMetric> openCorrelationOn,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(AnalysisDisplay.MatrixTab, "Matrix", "analysis.matrix", filter, post, logger)
    {
        _query = query;
        _logger = logger;
        OpenCorrelationOn = openCorrelationOn;
    }

    /// <summary>The page's Correlation route, which the grid's cell click ends at.</summary>
    public Action<AnalysisMetric, AnalysisMetric> OpenCorrelationOn { get; }

    /// <summary>The grid, which is this tab's whole body. Null until the tab is first selected,
    /// because the base builds a body only then and a grid that was never drawn must not hold a
    /// <c>ChartTheme.Changed</c> subscription.</summary>
    public MatrixChartViewModel? Chart => _chart;

    /// <summary>Spec 12.14's "When it is shown": always on the Matrix, because the grid's
    /// <c>hfr</c> row is present whenever the grid is.</summary>
    protected override bool IsPixelMetric => true;

    /// <summary>Spec 12.14's States table: the Matrix's loading line is its own.</summary>
    protected override string LoadingMessage => "Computing correlations...";

    /// <inheritdoc/>
    protected override object CreateBody()
    {
        _chart = new MatrixChartViewModel(OpenCorrelationOn, _logger);
        OnPropertyChanged(nameof(Chart));
        return _chart;
    }

    /// <inheritdoc/>
    protected override MatrixResult? Query(AnalysisFilter filter) => _query(filter);

    /// <inheritdoc/>
    /// <remarks>
    /// A result whose cells are all blank is <see cref="AnalysisTabState.Ready"/>, not empty: spec
    /// 12.14's "Matrix with every cell below 10 points" row draws the two axes and the labels with
    /// no cell in them. <see cref="AnalysisTabState.Empty"/> is the row above it, where the filters
    /// matched no frame at all and the query answered no cell.
    /// </remarks>
    protected override AnalysisTabState Map(MatrixResult? result)
    {
        if (result is null || result.Cells.Count == 0)
        {
            _chart?.Clear();
            return AnalysisTabState.Empty;
        }

        // Assigned only on the arm that read rows: the base clears the count before every mapping
        // and again on a failure, so an early arm that writes 0 is redundant.
        DistinctPlateScales = result.DistinctPlateScales;
        _chart?.Update(result.Cells);
        return AnalysisTabState.Ready;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _chart?.Dispose();
        base.Dispose();
    }
}
