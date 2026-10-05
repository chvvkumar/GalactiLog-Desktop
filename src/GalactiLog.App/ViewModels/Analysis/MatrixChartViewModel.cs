using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Theme;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// One position of spec 12.14's Matrix grid, in the order the grid draws it, reading top row
/// first. The heat series is a picture and carries no focusable element of its own, so this is
/// also the keyboard's cell: the view lays one chrome-free button per entry over the chart and a
/// reader who never touches the pointer reaches every pair through
/// <see cref="ActivateCommand"/>.
/// </summary>
/// <remarks>
/// A position with no <c>r</c> is present here and absent from the series, and it is FOCUSABLE and
/// INERT rather than disabled: the arrow keys visit it so a screen
/// reader announces its "no data" name, and Enter or Space on it opens nothing, which is spec
/// 12.14's "a cell with no r is not clickable". Disabling it would have taken the name out of the
/// walk, which is the one thing a reader needs there.
/// </remarks>
public sealed class MatrixCellViewModel
{
    private readonly Action<AnalysisMetric, AnalysisMetric> _open;

    internal MatrixCellViewModel(
        int column,
        int row,
        AnalysisMetric x,
        AnalysisMetric y,
        double? pearsonR,
        int points,
        Action<AnalysisMetric, AnalysisMetric> open)
    {
        Column = column;
        Row = row;
        X = x;
        Y = y;
        PearsonR = pearsonR;
        NPoints = points;
        _open = open;

        // NO CanExecute predicate, deliberately. A Button binds its IsEnabled from the command's
        // CanExecute, and a disabled button is out of the tab and arrow walk, which would silence
        // a blank cell's "no data" name. The gate is the body's, which
        // is also the only gate RelayCommand.Execute consults: it ignores CanExecute entirely
        // (HANDOFF rule 8).
        ActivateCommand = new RelayCommand(Activate);
    }

    /// <summary>The drawn column, which is the index into <c>AnalysisMetrics.X</c>.</summary>
    public int Column { get; }

    /// <summary>The drawn row. Index 0 is the BOTTOM of the grid, because a LiveCharts category
    /// axis starts there; <see cref="MatrixChartViewModel.Rows"/> is the reversed Y
    /// list that makes row 9 the HFR row at the top.</summary>
    public int Row { get; }

    /// <summary>The pair's X metric.</summary>
    public AnalysisMetric X { get; }

    /// <summary>The pair's Y metric.</summary>
    public AnalysisMetric Y { get; }

    /// <summary>The cell's Pearson r, or null when the query gave the pair no value: fewer than
    /// ten paired frames, a constant X axis or a constant Y axis. This type
    /// applies no gate of its own and never reads <see cref="NPoints"/> to decide.</summary>
    public double? PearsonR { get; }

    /// <summary>The pair's paired-frame count, for the hover line and for nothing else.</summary>
    public int NPoints { get; }

    /// <summary>Whether the pair has an r, which is what makes the cell drawn and clickable.
    /// </summary>
    public bool HasValue => PearsonR is not null;

    /// <summary>What a screen reader announces for this position: both metrics by their full
    /// labels and the figure, or the words for a position that has none. Every name comes from
    /// <c>AnalysisMetricLabels</c> and none is spelled here.</summary>
    public string AccessibleName => PearsonR is { } r
        ? string.Format(
            CultureInfo.InvariantCulture,
            "{0} against {1}, r {2}, {3} frames",
            AnalysisMetricLabels.Label(X),
            AnalysisMetricLabels.Label(Y),
            MatrixChartViewModel.CellLabel(r),
            NPoints)
        : $"{AnalysisMetricLabels.Label(X)} against {AnalysisMetricLabels.Label(Y)}, no data";

    /// <summary>The keyboard's route to the Correlation tab. Always executable, so the button
    /// stays focusable, and inert on a position with no r.</summary>
    public IRelayCommand ActivateCommand { get; }

    private void Activate()
    {
        // The one gate, in the body, because that is the only place a direct Execute passes
        // through: a pair the grid never drew opens nothing.
        if (HasValue)
        {
            _open(X, Y);
        }
    }
}

/// <summary>
/// Spec 13's "Correlation matrix" row: one <c>HeatSeries</c> over the hundred metric pairs, two
/// category axes, and the mapping from a drawn cell back to its pair.
/// </summary>
/// <remarks>
/// <para>
/// Every token is read through <c>ChartTheme.Read</c> rather than <c>ChartTheme.Palette</c>:
/// <c>MetricTokenOrder</c> holds ten keys and carries neither <c>ColorInfo</c> nor
/// <c>ColorMetricWorst</c>, so the palette cannot answer for two of the ramp's three stops.
/// <c>ChartPalette</c> is not extended. The read happens on the UI thread at construction and
/// again on every <c>ChartTheme.Changed</c> through <c>ChartTheme.Subscribe</c>, whose token this
/// type holds and disposes: the event is static and a handler left attached pins the view-model
/// for the life of the process (<c>ChartTheme.cs:117</c>).
/// </para>
/// <para>
/// No axis sets a paint. The global axis rule supplies the label and separator ink, and the four
/// tooltip and legend paints are the shared chart style's. What makes an axis follow a theme swap
/// is a FRESH instance rather than the rule running again: see <see cref="PublishAxes"/>.
/// </para>
/// </remarks>
public sealed partial class MatrixChartViewModel : ObservableObject, IDisposable
{
    /// <summary>How a cell's r is printed, in the data label and in the accessible name. Two
    /// decimals, and the sign is printed rather than dropped: on red-light every ink on this page
    /// is a shade of one red and the sign has nowhere else to go.</summary>
    internal const string CellFormat = "0.00";

    // Spec 12.14: hovering a cell reads r to three decimals with the pair's frame count. A blank
    // cell has no drawn point, so it cannot be hovered at all and the web's "Insufficient data"
    // hover has no counterpart.
    private const string HoverFormat = "0.000";

    // The three ramp tokens, in stop order. ColorInfo rather than ColorMetricGuiding at the
    // negative end, because in deep-sky metric-guiding and metric-worst are deltaE 10.5
    // apart at contrast 1.03, so an r of -1 and an r of +1 rendered as the same colour and the
    // ramp was not diverging at all in that theme.
    private const string NegativeToken = "ColorInfo";
    private const string NeutralToken = "ColorBgElevated";
    private const string PositiveToken = "ColorMetricWorst";

    // The two inks a cell's label may take, and the ramp's pinned ends.
    //
    // Spec 13's Matrix row names text-primary alone. It is now "the higher-contrast of
    // text-primary and the elevated background, per cell", which is InkContrast.Choose and is
    // shared with the box plot's median mark: one ink cannot read over a ramp that
    // spans a theme's whole lightness range, and on red-light the printed r is the only thing
    // carrying the sign. The dark ink is ColorBgElevated forced opaque, which is the
    // same value the Correlation tab's mask paints with and the one surface colour in the
    // vocabulary that is darker than every ramp stop in all three themes.
    private const string LabelToken = "ColorTextPrimary";
    private const string DarkLabelToken = "ColorBgElevated";

    private const double RampMinimum = -1d;
    private const double RampMaximum = 1d;

    /// <summary>
    /// The focused cell's box, drawn in the application's own focus and selection ink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ColorAccent</c> rather than any ramp-adjacent token, and the reason is <c>red-light</c>,
    /// where every ink on this page is already a shade of one red. There
    /// the ramp runs continuously from <c>ColorInfo</c> through <c>ColorBgElevated</c> to
    /// <c>ColorMetricWorst</c>, which spans the theme's whole lightness range, so NO opaque token
    /// separates from every cell the box could land on. What makes the box read anyway is where it
    /// is drawn rather than what it is: <see cref="PointPadding"/> insets every cell by two pixels
    /// on each side, so the four-pixel gutter between neighbours is chart background, and a two
    /// pixel stroke centred on the cell boundary sits inside that gutter rather than over a cell's
    /// ramp ink. Against <c>red-light</c>'s own background the accent is the light end of the
    /// theme, and the same holds in the other two.
    /// </para>
    /// <para>
    /// It is also the token this application already uses for focus and selection, so the grid
    /// does not invent an affordance of its own.
    /// </para>
    /// </remarks>
    private const string FocusToken = "ColorAccent";

    // Spec 13's stroke for the focus box, in pixels. Two, so that a stroke centred on the cell
    // boundary stays inside the four-pixel gutter PointPadding leaves between neighbours.
    private const int FocusStroke = 2;

    // Each cell is inset by this on every side, which is also what makes the focus box legible.
    private const int PointPadding = 2;

    private readonly IDisposable _themeSubscription;
    private readonly Action<AnalysisMetric, AnalysisMetric> _openCorrelationOn;
    private readonly ILogger _logger;

    // The drawn positions that carry an r, keyed by the coordinate the series build wrote. Filled
    // by the same loop that fills Values, so a transposed or unreversed build transposes this map
    // with it and the round-trip case sees the swap rather than agreeing with the defect.
    private readonly Dictionary<(int Column, int Row), MatrixCellViewModel> _drawn = [];

    // One shape across the phase. The five inks are resolved HERE, on the UI
    // thread, at construction and again on every ChartTheme.Changed, and Rebuild uses the held
    // values. ChartTheme.Read answers its documented neutral rather than throwing when it is
    // called off the UI thread, so a read inside Rebuild was correct only by the convention that
    // every caller publishes through the post seam, which is a rule each future case would have to
    // remember rather than a choke point (design lesson 2).
    private SKColor _negativeInk;
    private SKColor _neutralInk;
    private SKColor _positiveInk;
    private SKColor _labelInk;
    private SKColor _darkInk;
    private SKColor _focusInk;

    private IReadOnlyList<MatrixCell> _cells = [];
    private bool _disposed;

    /// <param name="openCorrelationOn">The page's Correlation route, held by
    /// <c>MatrixTabViewModel.OpenCorrelationOn</c> and passed straight through. Every route out of
    /// this grid, pointer and keyboard alike, ends here.</param>
    /// <param name="logger">Where a malformed result is recorded. Null logs nothing.</param>
    public MatrixChartViewModel(
        Action<AnalysisMetric, AnalysisMetric> openCorrelationOn, ILogger? logger = null)
    {
        _openCorrelationOn = openCorrelationOn;
        _logger = logger ?? NullLogger.Instance;

        // Seeded before anything can bind, for the reason PublishEmpty documents.
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        Cells = [];
        Sections = [];

        ReadInks();
        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>The grid's columns, which are <c>AnalysisMetrics.X</c> in its own order. Ten, never
    /// fifteen: the five PHD2 metrics join the Correlation X list only.</summary>
    public static IReadOnlyList<AnalysisMetric> Columns => AnalysisMetrics.X;

    /// <summary>
    /// The grid's rows in DRAWN order, which is <c>AnalysisMetrics.Y</c> reversed.
    /// </summary>
    /// <remarks>
    /// A LiveCharts category axis puts index 0 at the bottom, and spec 12.14 wants "Y metrics down
    /// the rows" with HFR first. The list is reversed once, here, and the row coordinate is
    /// reversed with it. Inverting the axis instead would leave the labels and the
    /// series coordinates disagreeing about which end is which.
    /// </remarks>
    public static IReadOnlyList<AnalysisMetric> Rows { get; } = [.. AnalysisMetrics.Y.Reverse()];

    /// <summary>The one heat series, or nothing at all in the empty state.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    /// <summary>The column axis.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> XAxes { get; private set; }

    /// <summary>The row axis.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> YAxes { get; private set; }

    /// <summary>The hundred positions in reading order, top row first, which is what the view's
    /// keyboard layer draws over the chart. Empty in the empty state.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MatrixCellViewModel> Cells { get; private set; }

    /// <summary>
    /// The focused cell's box: one <c>RectangularSection</c> while a cell holds keyboard focus and
    /// nothing at all otherwise.
    /// </summary>
    /// <remarks>
    /// A section is in the SERIES' own coordinate space, so the box lands on the drawn cell by
    /// construction at every window size and whatever the axis gutters do. The overlay the keyboard
    /// layer draws is a different rectangle from the plot area, at a different origin and a
    /// different pitch, so a ring drawn on the overlay itself was out by more than a cell in two
    /// corners and would have contradicted the name the screen reader had just announced.
    /// </remarks>
    [ObservableProperty]
    public partial IReadOnlyList<RectangularSection> Sections { get; private set; }

    /// <summary>The cell that holds keyboard focus, or null while none does.</summary>
    public MatrixCellViewModel? FocusedCell { get; private set; }

    /// <summary>
    /// Records which cell holds keyboard focus and republishes <see cref="Sections"/>. The view's
    /// cell layer calls it with the cell on got-focus and with null on lost-focus.
    /// </summary>
    public void SetFocus(MatrixCellViewModel? cell)
    {
        if (ReferenceEquals(FocusedCell, cell))
        {
            return;
        }

        FocusedCell = cell;
        OnPropertyChanged(nameof(FocusedCell));
        PublishFocus();
    }

    /// <summary>Replaces the grid's data. The cells are kept so a later theme change re-resolves
    /// every token without the tab re-querying.</summary>
    public void Update(IReadOnlyList<MatrixCell> cells)
    {
        _cells = cells;
        Rebuild();
    }

    /// <summary>Drops the data and publishes the empty state.</summary>
    public void Clear() => Update([]);

    /// <summary>
    /// What <c>CartesianChart.DataPointerDownCommand</c> is bound to. It reads the pressed point's
    /// own coordinate and hands it to <see cref="OpenCell"/>; nothing is computed from pointer
    /// pixels, so a press in the gap a blank cell leaves carries no point and opens nothing.
    /// </summary>
    [RelayCommand]
    public void CellPressed(IEnumerable<ChartPoint>? points)
    {
        if (points?.FirstOrDefault() is not { } point)
        {
            return;
        }

        // The spike's own reading: SecondaryValue is the column and PrimaryValue is the row.
        OpenCell(point.Coordinate.SecondaryValue, point.Coordinate.PrimaryValue);
    }

    /// <summary>
    /// Spec 12.14's cell click, by drawn coordinate rather than by a LiveCharts point, so the rule
    /// is testable without constructing one (the shape <c>ImagingTimelineViewModel.PointClicked</c>
    /// already takes).
    /// </summary>
    public void OpenCell(double column, double row)
    {
        if (Resolve(column, row) is { } cell)
        {
            _openCorrelationOn(cell.X, cell.Y);
        }
    }

    /// <summary>The drawn position at a coordinate, or null where the grid drew nothing. Answers
    /// off the set the series build itself wrote, never off index arithmetic: a coordinate a blank
    /// cell left empty resolves to nothing at all.</summary>
    public MatrixCellViewModel? Resolve(double column, double row)
    {
        // (int)Math.Round(double.NaN) is unspecified and keys (0, 0) on this
        // runtime, so a malformed point out of the chart library would open the top-left pair
        // rather than nothing. Only the library can produce one, and it costs one guard.
        if (!double.IsFinite(column) || !double.IsFinite(row))
        {
            return null;
        }

        var key = ((int)Math.Round(column), (int)Math.Round(row));
        return _drawn.TryGetValue(key, out var cell) ? cell : null;
    }

    private void Rebuild()
    {
        _drawn.Clear();

        // The entries are about to be replaced, so the focused one is stale whatever happens next.
        FocusedCell = null;
        OnPropertyChanged(nameof(FocusedCell));

        if (_cells.Count == 0)
        {
            PublishEmpty();
            return;
        }

        // One dictionary read per position rather than a scan of the hundred cells per position.
        // ToDictionary THROWS on a duplicate (X, Y), the base turns that into
        // Failed plus Retry, and Retry re-runs the same query and fails identically, so one
        // malformed result would lock the tab in a loop rather than degrade the grid. The first
        // entry for a pair wins, deliberately rather than silently: last-one-wins would pick a
        // figure by row order, and the warning below names the pair so the query can be fixed.
        var byPair = new Dictionary<(AnalysisMetric X, AnalysisMetric Y), MatrixCell>(_cells.Count);
        var duplicates = 0;
        foreach (var cell in _cells)
        {
            if (!byPair.TryAdd((cell.X, cell.Y), cell) && duplicates++ == 0)
            {
                // Once per result, naming the first offending pair: a hundred warnings would tell
                // a reader of the log nothing the first one does not.
                _logger.LogWarning(
                    "The matrix result carries more than one cell for {XMetric} against {YMetric}; "
                    + "the first is drawn and the rest are dropped",
                    cell.X,
                    cell.Y);
            }
        }

        var entries = new List<MatrixCellViewModel>(Columns.Count * Rows.Count);

        // Rows descending, so the entry list reads top row first and the view's keyboard layer,
        // which fills left to right and top to bottom, sits square with the drawn grid.
        for (var row = Rows.Count - 1; row >= 0; row--)
        {
            for (var column = 0; column < Columns.Count; column++)
            {
                var x = Columns[column];
                var y = Rows[row];
                byPair.TryGetValue((x, y), out var cell);

                var entry = new MatrixCellViewModel(
                    column, row, x, y, cell?.PearsonR, cell?.NPoints ?? 0, _openCorrelationOn);
                entries.Add(entry);

                // Spec 12.14 departure 3: a WeightedPoint with a null weight is
                // DRAWN, takes the ramp's midpoint ink and prints 0.00, which a reader cannot tell
                // from a measured zero. A pair with no r is omitted from Values entirely and the
                // chart's own background shows through.
                //
                // "No r" is MatrixCell.PearsonR being null and nothing else. The three reasons a
                // pair has none live inside the query and Analysis.MatrixPearson,
                // so NPoints is never read here to decide what is drawn.
                if (cell?.PearsonR is null)
                {
                    continue;
                }

                _drawn[(column, row)] = entry;
            }
        }

        Cells = entries;
        PublishAxes();
        PublishSeries();
        PublishFocus();
    }

    /// <summary>
    /// The two category axes, as FRESH instances every time, which is what makes their label and
    /// separator ink follow a theme swap.
    /// </summary>
    /// <remarks>
    /// Neither axis sets a paint: the ink comes from the one axis rule <c>ChartTheme.Apply</c>
    /// appends, whose closure reads the CURRENT palette. That rule runs over an axis once, when a
    /// chart first measures it, and it does not run again on a later <c>ChartTheme.Apply</c>: the
    /// library guards the styling with a theme id, and <c>ChartTheme.Apply</c> installs no new theme
    /// after the first call (<c>ChartTheme.cs</c>, the <c>UseDefaults</c> comment), so the id never
    /// moves. Measured directly under rc5.4: one hosted chart read <c>#ffa8a49c</c> off its X axis
    /// under <c>luminance</c> and the SAME instance still read <c>#ffa8a49c</c> after a swap
    /// to <c>deep-sky</c>, whose axis ink is <c>#ff94a3b8</c>. The ten row labels, the ten column
    /// labels and the grid separators therefore stayed in the ink of the theme the reader left.
    /// <para>
    /// A fresh instance is what the other five chart view-models publish on every swap
    /// (<c>CorrelationChartViewModel</c>, <c>HistogramChartViewModel</c>,
    /// <c>BoxPlotChartViewModel</c>, <c>TimeSeriesChartViewModel</c> and
    /// <c>StatsBarChartViewModel</c>), and the measure state it costs is what every one of them
    /// already pays. <c>Cells</c> and the focus are a different question and survive the swap; see
    /// <see cref="OnThemeChanged"/>.
    /// </para>
    /// </remarks>
    private void PublishAxes()
    {
        XAxes = [Categories([.. Columns])];
        YAxes = [Categories([.. Rows])];
    }

    /// <summary>
    /// The partition and the two label paints, from the held inks and the entries already
    /// published. Separate from <see cref="Rebuild"/> because a theme swap has to redo exactly this
    /// and nothing else: the cells themselves depend on no token.
    /// </summary>
    /// <remarks>
    /// One <c>HeatSeries</c> carries ONE <c>DataLabelsPaint</c>, and the label's ink depends on the
    /// cell it sits in, so the drawn cells are partitioned by which ink wins and each half gets its
    /// own series over the same ramp and the same pair of axes. The partition is the only
    /// difference between the two: every other property of <see cref="HeatHalf"/> is identical, so
    /// the picture is one grid.
    /// </remarks>
    private void PublishSeries()
    {
        var lightLabelled = new List<WeightedPoint>(Cells.Count);
        var darkLabelled = new List<WeightedPoint>(Cells.Count);

        // Cells is in drawn order, rows descending, and a blank one is in no half at all.
        foreach (var entry in Cells)
        {
            if (entry.PearsonR is not { } r)
            {
                continue;
            }

            // The label's ink is chosen against the ink THIS cell is about to be painted in, which
            // is what puts the cell in one half of the partition or the other.
            var point = new WeightedPoint(entry.Column, entry.Row, r);
            if (InkContrast.Choose(RampColourAt(r), _labelInk, _darkInk) == _labelInk)
            {
                lightLabelled.Add(point);
            }
            else
            {
                darkLabelled.Add(point);
            }
        }

        // ALWAYS two series, even where one half is empty, so nothing downstream has to know how
        // many there are and a theme swap that moves every cell from one half to the other changes
        // no shape. An empty Values renders and measures without throwing.
        Series = [HeatHalf(lightLabelled, _labelInk), HeatHalf(darkLabelled, _darkInk)];
    }

    // The focus box, rebuilt from the held ink so a theme swap re-paints it with everything else.
    // Half a cell either way, in series space, which is what puts it on the drawn cell rather than
    // near it.
    private void PublishFocus() => Sections = FocusedCell is { } cell
        ?
        [
            new RectangularSection
            {
                Xi = cell.Column - 0.5d,
                Xj = cell.Column + 0.5d,
                Yi = cell.Row - 0.5d,
                Yj = cell.Row + 0.5d,
                Fill = null,
                Stroke = new SolidColorPaint(_focusInk) { StrokeThickness = FocusStroke },
            },
        ]
        : [];

    // The one place a token is read, on the UI thread, at construction and on
    // every ChartTheme.Changed.
    private void ReadInks()
    {
        _negativeInk = ChartTheme.Read(NegativeToken, ChartTheme.Fallback);
        _neutralInk = ChartTheme.Read(NeutralToken, ChartTheme.Fallback);
        _positiveInk = ChartTheme.Read(PositiveToken, ChartTheme.Fallback);
        _labelInk = ChartTheme.Read(LabelToken, ChartTheme.Fallback);
        _focusInk = ChartTheme.Read(FocusToken, ChartTheme.Fallback);

        // Forced opaque: deep-sky declares ColorBgElevated at alpha 0xE6, and a translucent
        // label would take some of the cell's own ink and lose the contrast it was chosen for.
        // The ramp's own neutral stop keeps the declared alpha, because that one is a fill.
        _darkInk = ChartTheme.Read(DarkLabelToken, ChartTheme.Fallback).WithAlpha(0xFF);
    }

    /// <summary>
    /// A theme swap re-reads the inks and republishes only what is painted from them: the two
    /// halves of the partition, because a cell can change side between themes, both label paints
    /// with them, the focus box, and the two axes, whose ink the library will not re-apply to an
    /// instance it has already styled (<see cref="PublishAxes"/>).
    /// </summary>
    /// <remarks>
    /// <c>Cells</c> is NOT rebuilt, and that is the point. Rebuilding it replaced all
    /// hundred entries, the keyboard layer's <c>ItemsControl</c> regenerated all hundred buttons,
    /// and the focused button went away, so a reader three arrow presses into the grid was put back
    /// at the top of the page with no announcement over a change that moved no data. An entry
    /// depends on no token, so there is nothing to rebuild. A DATA rebuild still replaces them,
    /// where the entries really are stale.
    /// </remarks>
    private void OnThemeChanged()
    {
        ReadInks();

        if (Cells.Count == 0)
        {
            // The empty state's shape stands: PublishEmpty's two BARE axes carry no label and the
            // region is closed, so nothing of it is on the page to take the new ink, and two empty
            // series are not published over it.
            return;
        }

        PublishAxes();
        PublishSeries();
        PublishFocus();
    }

    // One half of the partition. Everything but Values and DataLabelsPaint is identical between
    // the two, so the two halves are one grid drawn in one ramp over one pair of axes.
    //
    // Two series share a category slot rather than dividing it, because the cartesian engine
    // divides a slot only among bar series (IBarSeries) and a heat series is not one. A case pins
    // that, since a version that changed it would offset the two halves against each other and
    // nothing else would notice.
    private HeatSeries<WeightedPoint> HeatHalf(IReadOnlyCollection<WeightedPoint> values, SKColor labelInk) => new()
    {
        Values = values,
        HeatMap = Ramp,
        ColorStops = [0d, 0.5d, 1d],

        // The two most important lines in the block. Without them the ramp rescales
        // to the data and a matrix whose strongest r is 0.3 paints that cell fully saturated. They
        // are also what makes the two halves agree: a ramp pinned to the same range paints the
        // same r the same colour whichever series holds it.
        MinValue = RampMinimum,
        MaxValue = RampMaximum,

        PointPadding = new Padding(PointPadding),
        DataLabelsPaint = new SolidColorPaint(labelInk) { SKTypeface = SKTypeface.Default },
        DataLabelsSize = 12,
        DataLabelsPosition = DataLabelsPosition.Middle,
        DataLabelsFormatter = point => CellLabel(point.Coordinate.TertiaryValue),
        YToolTipLabelFormatter = point => Hover(point.Coordinate),
    };

    // The ramp as the series takes it, from the held tokens and in stop order.
    private LvcColor[] Ramp =>
        [_negativeInk.AsLvcColor(), _neutralInk.AsLvcColor(), _positiveInk.AsLvcColor()];

    /// <summary>
    /// The colour a cell of this <c>r</c> is painted, computed with the LIBRARY'S OWN
    /// interpolation over the same three stops and the same pinned range the series carries, so
    /// the ink the label is chosen against is the ink the cell actually gets.
    /// </summary>
    internal SKColor RampColourAt(double r)
    {
        var ramp = Ramp;
        var stops = HeatFunctions.BuildColorStops(ramp, [0d, 0.5d, 1d]);
        var colour = HeatFunctions.InterpolateColor(
            // Named, because rc5.4 declares this constructor (max, min) and not (min, max).
            (float)r, new Bounds(max: RampMaximum, min: RampMinimum), ramp, stops);
        return new SKColor(colour.R, colour.G, colour.B, colour.A);
    }

    /// <summary>
    /// What a cell prints. Two decimals, and the SIGN is printed: an <c>r</c> of -0.55 reads
    /// "-0.55" and never "0.55", because on red-light the ramp is decoration and the printed
    /// figure is the only thing carrying the direction. The data label formatter is
    /// this member and nothing else, so the rule has one place to be got wrong.
    /// </summary>
    internal static string CellLabel(double r) => r.ToString(CellFormat, CultureInfo.InvariantCulture);

    /// <summary>Spec 12.14's hover line, exposed so a case asserts the string rather than
    /// constructing a LiveCharts point to reach the formatter.</summary>
    internal string Hover(Coordinate coordinate)
    {
        var count = Resolve(coordinate.SecondaryValue, coordinate.PrimaryValue)?.NPoints ?? 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "r={0} (N={1})",
            coordinate.TertiaryValue.ToString(HoverFormat, CultureInfo.InvariantCulture),
            count);
    }

    // The short labels are AnalysisMetricLabels' and none is spelled here: that table is the one
    // place that glyph rule is enforced, by code point, in both directions.
    //
    // The two limits are what keep all ten categories on the grid whatever the data holds, which
    // is spec 12.14's "both axes and all ten labels, with a blank cell where there is no value".
    // Without them an axis fits itself to the points it was given, and a column or a row whose
    // every cell is blank has no point at all, so it falls off the end: measured on a hosted grid
    // whose tenth column carried no r, the X axis answered data bounds -0.5 to 8.5 and the picture
    // drew nine columns under nine labels while the keyboard still walked into the tenth.
    //
    // Half a cell either way is the span a full grid produces of its own accord, and the same one
    // the focus RectangularSection already assumes.
    private static Axis Categories(IReadOnlyList<AnalysisMetric> metrics) => new()
    {
        Labels = [.. metrics.Select(metric => AnalysisMetricLabels.For(metric).Matrix)],
        MinStep = 1,
        ForceStepToMin = true,
        MinLimit = -0.5d,
        MaxLimit = metrics.Count - 0.5d,
    };

    /// <summary>
    /// The empty state: no series, no cells, and one bare axis per side rather than none. rc5.4's
    /// <c>CartesianChartEngine.Measure</c> throws "XAxes and YAxes must contain at least one
    /// element" for a heat series as for every other kind, and the control
    /// measures while it is collapsed. Fresh axis instances per call, because an axis carries
    /// measure state.
    /// </summary>
    private void PublishEmpty()
    {
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        Cells = [];
        Sections = [];
    }

    /// <summary>The static theme outlives every chart, so a grid that does not unsubscribe keeps
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
