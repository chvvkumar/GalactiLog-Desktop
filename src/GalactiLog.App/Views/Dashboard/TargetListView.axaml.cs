using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Views.Dashboard;

/// <summary>
/// design-spec 12.2's target list. The markup carries the whole table; this file carries one rule
/// the markup cannot express: which column leaves the row when the list is too narrow to draw them
/// all (Phase 14C Task 3, second fix pass, provisional pending the user).
/// </summary>
/// <remarks>
/// <para>
/// At the shipped 1280 by 800 window with the filter panel open at 300 the list is handed about
/// 720 pixels, and six columns plus the Expand button need more than that at the application's own
/// type size whatever their caps. The ruling is that Equipment gives way: the web application's
/// table has no equipment column at all, so it is the one whose absence costs least.
/// </para>
/// <para>
/// It is a layout suppression and nothing more. Nothing is written to <c>display.columns.dashboard</c>,
/// <see cref="ColumnViewModel.IsShown"/> stays true, the column gear still shows Equipment ticked,
/// and a user who has hidden Equipment themselves is unaffected because the two gates are combined
/// rather than one overwriting the other. The suppressed column is dropped through
/// <see cref="TableColumn.IsDropped"/>, which takes it out of its shared size group as well as
/// hiding its cells, because a group in Avalonia only ever grows and would otherwise keep reserving
/// the width it last measured.
/// </para>
/// <para>
/// Fixer pass, phase-review P2: the width rule and the user's own column hiding are two causes and
/// now carry two flags. Unticking Equipment in the column gear used to drop Designation,
/// Integration and Last session to the tight cap at any width, and the width at which Equipment
/// leaves was a constant over all six columns rather than a sum over the shown ones, so hiding two
/// columns left Equipment suppressed in a row that would have fitted in half the width.
/// </para>
/// <para>
/// Design lesson 1 (phase-review P3) asks whether this second "derive a breakpoint from declared
/// widths" arithmetic and the retired night pane's breakpoint should share a spine. Ruled left as two
/// copies, because a helper is longer than both: the pane's is one compile-time
/// <c>const</c> summing three fixed terms, consumed by a single <c>width &gt;= WideBreakpoint</c>
/// comparison, while this one sums a set that changes at runtime with the user's column
/// visibility, is recomputed on every <c>Bounds</c> and column change, and answers a second
/// question the pane never asks, what the star column's remainder is. A shared helper would take
/// the keyed budgets and the arranged width and return both answers, and the pane's call site
/// would need an adapter longer than the one line it replaces. The third table that needs this
/// extracts the helper; that is the phase review's own carried ruling.
/// </para>
/// </remarks>
public partial class TargetListView : UserControl
{
    /// <summary>The cell gutter, <see cref="TableMetrics.Gutter"/> left and right, so adjacent
    /// columns sit 16 apart. Every cap below is a column width and includes it; the spine writes a
    /// cap to the cell, whose gutter is a margin outside it, so the gutter is taken off first.</summary>
    internal const double CellGutter = 2 * TableMetrics.Gutter;

    /// <summary>The Name column's floor. It is the one star column: it takes the remainder at a
    /// wide window and trims with an ellipsis down to this, which holds a catalogue designation
    /// and its two gutters.</summary>
    internal const double NameFloor = 120d;

    /// <summary>The cap Designation, Integration and Last session carry while the list is wide
    /// enough to draw every shown column. One figure for the three, not three, because the three
    /// were only ever round numbers and the arithmetic below is the only thing that read them
    /// apart. It is what a long catalogue designation needs: the fixture's "PGC 123456.789" cell
    /// measures 156 with its two gutters. It caps Designation alone, written to the column through
    /// <see cref="DataCellMaxWidth"/>; Integration is a figure and Last session a date, and neither
    /// ever trims (spec.md item 3), so for those two it is the budget the arithmetic sums and
    /// nothing in the table.</summary>
    internal const double DataCap = 160d;

    /// <summary>What those same three are capped at while the list is too narrow to draw every
    /// shown column. It is bounded above, not chosen: at the 720 pixels the shipped 1280 window
    /// gives the list, Name's floor, the palette, the sessions cell and the trailing inset leave
    /// 386 for the three, so a cap above 128 puts Last session and the Expand button off the
    /// viewport. A header label may therefore still trim here; see the fixer report, which carries
    /// the arithmetic and the product options.</summary>
    internal const double TightCap = 124d;

    /// <summary>The palette column's share of the arithmetic. A budget and not a cap: ruling Q10
    /// keeps the badges uncapped and unclipped, so nothing in the markup applies this and the
    /// column is as wide as the widest palette on the page.</summary>
    internal const double PaletteBudget = 120d;

    /// <summary>The Equipment column's cap, written to its <see cref="TableColumn"/> once. Raised
    /// from 180 by the fixer pass: at a 1900 window the column trimmed a telescope and camera pair
    /// ("Esprit 100 / ASI6200MM Pro" measures 272 with its gutters) while Name held about 500
    /// pixels of spare width. Equipment is drawn at all only above the fit width, and the fit width
    /// is built from this, so a generous cap buys its own room rather than taking Name's.</summary>
    internal const double EquipmentCap = 300d;

    /// <summary>The sessions cell: the Expand button plus its gutter. Not a cap, because the
    /// button sizes itself; the figure is what it measures at the shipped type size.</summary>
    internal const double SessionsBudget = 96d;

    /// <summary>The reserved vertical bar: the rows end at it, and the header and the pager carry
    /// the same figure as <see cref="TableMetrics.ScrollInset"/>.</summary>
    internal const double TrailingInset = TableMetrics.ScrollBarSize;

    /// <summary>The list width at or above which the default six columns fit with Name at its
    /// floor. The shipped figure for the default set; the rule itself runs over the shown set
    /// (<see cref="FitWidthFor"/>), which is what spec 12.2's "every shown column" says.</summary>
    internal const double DefaultFitWidth =
        NameFloor + DataCap + PaletteBudget + DataCap + EquipmentCap + DataCap
        + SessionsBudget + TrailingInset;

    /// <summary>The narrowest list that still draws a row whole, with Equipment already
    /// suppressed and Name at its floor. Measured rather than summed: the caps above are maxima,
    /// so a sum over them (720) overstates what the row actually takes, and on the shipped fixture
    /// the Expand button's trailing edge settles at 664, its gutter ends the row at 672 and the
    /// reserved vertical bar carries the list to 684. The pager's own minimum, the page buttons
    /// plus "Rows per page" and its select, is 566, so the row is the binding constraint and this
    /// is the figure the page is laid out against.
    /// <para>
    /// <c>DashboardView.axaml.cs</c> is what reads it: the filter panel's rendered width is
    /// bounded so the list keeps at least this much, and below the panel's own 220 floor the panel
    /// renders as its 48 pixel strip instead (fixer-list item 6, phase-review P2).
    /// <c>DashboardViewTests</c> lays the page out at the 1024 by 700 floor and at the shipped
    /// 1280 by 800 and asserts the Expand button and the page-size select are inside the list,
    /// which is what fails if this figure drifts from what the row measures.
    /// </para></summary>
    internal const double ListMinWidth = 684d;

    /// <summary>A check box cell (spec 12.15): <see cref="CustomCellWidths.Check"/>. An empty
    /// <c>CheckBox</c> measures 18, which the ledger's own selection column comment records
    /// (<c>Views/TargetDetail/TargetDetailView.axaml</c>), plus the cell's two 8 pixel
    /// gutters.</summary>
    internal const double BooleanCellWidth = CustomCellWidths.Check;

    /// <summary>A dropdown cell: <see cref="CustomCellWidths.Choice"/>, the widest option plus the
    /// combo's chevron, bounded so one long option cannot take the row.</summary>
    internal const double DropdownCellWidth = CustomCellWidths.Choice;

    /// <summary>A text cell: <see cref="CustomCellWidths.Text"/>. 120 rather than
    /// <see cref="DataCap"/>, which is what every other text-bearing dashboard cell carries: the two
    /// surfaces that draw a custom cell size its editor from one table.</summary>
    internal const double TextCellWidth = CustomCellWidths.Text;

    /// <summary>What one custom cell's editor needs, by the column's type and by nothing else
    /// (spec 12.15). The dashboard's name for <see cref="CustomCellWidths.For"/>, which is the one
    /// per-kind table; the Nights ledger reads the same one through
    /// <c>TargetDetailViewModel.CustomCellWidth</c>. It is a floor under
    /// <see cref="CustomCellWidthFor"/>, never the whole answer: a column whose name needs more than
    /// its editor does is as wide as its name.</summary>
    internal static double WidthFor(CustomColumnType type) => CustomCellWidths.For(type);

    /// <summary>
    /// The width one custom column takes, its header entry and its cell on every row alike: what
    /// its editor needs, or what its own heading needs when that is more, and never more than
    /// <see cref="DropdownCellWidth"/> so one long column name cannot take the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One figure for the two strips and for the width rule, so the label of a column is drawn over
    /// that column's own cells by construction. A check box cell needs 36 and the word "Processed"
    /// needs more than twice that: with the editor's figure alone the heading was drawn as an
    /// ellipsis at every width, and two boolean columns beside each other read as two identical
    /// dots with nothing on screen saying which box was which. Above the cap the heading trims and
    /// the whole name is on the entry's tooltip and its automation name.
    /// </para>
    /// <para>
    /// The heading is measured rather than estimated, and it is measured at the rendered view's own
    /// type size and family, which is what <c>FrameTableView</c> already does for the file name
    /// column and for the same reason: the root size is a setting and the family comes from the
    /// shared vocabulary, so neither is a constant this file could carry.
    /// </para>
    /// </remarks>
    internal static double CustomCellWidthFor(
        string? name, CustomColumnType type, double fontSize, FontFamily? family)
        => CustomCellWidths.ForHeading(name, type, fontSize, family);

    /// <summary>What one column contributes to the fit width. Keyed by the column key rather than
    /// by position, so the shown set can be summed in any order.</summary>
    /// <remarks>Built-in keys only. A custom slug falls into the default arm and contributes
    /// nothing, which is why the overload below exists and why nothing sums a custom column
    /// through this one.</remarks>
    internal static double BudgetFor(string key) => key switch
    {
        "name" => NameFloor,
        "palette" => PaletteBudget,
        "equipment" => EquipmentCap,
        "designation" or "integration" or "last_session" => DataCap,
        _ => 0d,
    };

    /// <summary>The same question over a column that may be a custom one (spec 12.15). A custom
    /// slug is told apart by <see cref="CustomColumnSlug.IsCustom"/> and its width comes from the
    /// lookup rather than from the key table, because its figure depends on the column's type and
    /// not on its name.
    /// <para>
    /// Without this a row carrying two visible custom columns reported the same fit width as a row
    /// carrying none, so Equipment was not suppressed when it should have been and the row was
    /// laid out past the list's right edge, which is the <see cref="ListMinWidth"/> failure this
    /// file's own remarks describe.
    /// </para></summary>
    internal static double BudgetFor(ColumnViewModel column, Func<string, double> customWidth)
        => CustomColumnSlug.IsCustom(column.Key) ? customWidth(column.Key) : BudgetFor(column.Key);

    /// <summary>The width at or above which every column the user has chosen to show fits with
    /// Name at its floor (spec 12.2). It is built from the shown set and not from a constant over
    /// all six: hiding Designation and Integration in the column gear used to leave Equipment
    /// suppressed below 936 although the row would have fitted in half of it (phase-review P2).
    /// The set read here is the user's own <see cref="ColumnViewModel.IsShown"/>, never the
    /// width rule's own suppression, so the two cannot feed each other.</summary>
    /// <param name="columns">The column set, built-in and custom together.</param>
    /// <param name="customWidth">What one custom slug's cell takes. Null answers zero for every
    /// custom column, which is the right answer only for a list that has none.</param>
    internal static double FitWidthFor(
        IEnumerable<ColumnViewModel> columns, Func<string, double>? customWidth = null)
    {
        var width = customWidth ?? (_ => 0d);
        return columns.Where(column => column.IsShown).Sum(column => BudgetFor(column, width))
            + SessionsBudget + TrailingInset;
    }

    /// <summary>Whether the Equipment column is drawn: the user's own column visibility and the
    /// width rule above, combined, and written to the column's <see cref="TableColumn.IsDropped"/>,
    /// so the persisted list and the suppression cannot be confused for one another.</summary>
    public static readonly StyledProperty<bool> IsEquipmentShownProperty =
        AvaloniaProperty.Register<TargetListView, bool>(nameof(IsEquipmentShown), defaultValue: true);

    /// <summary>The width the Name cell is bounded by, header cell and row cell alike: the share
    /// the star column has once every other column has taken its cap, never below
    /// <see cref="NameFloor"/>, less the cell's two gutters.
    /// <para>
    /// It exists because a star column in Avalonia does not shrink below its own cell's desired
    /// width. The header's Name cell holds the word "Name" and desires about 60; a row's holds a
    /// target name and desires far more, so the row's star floored where the header's did not and
    /// the two grids resolved different column sets, by 16 to 24 pixels, which moved every column
    /// after Name. Bounding both cells by one computed figure removes the flooring instead of
    /// compensating for it, and the figure is built from the same budgets
    /// <see cref="FitWidthFor"/> is built from, over the same shown set.
    /// </para></summary>
    public static readonly StyledProperty<double> NameCellMaxWidthProperty =
        AvaloniaProperty.Register<TargetListView, double>(
            nameof(NameCellMaxWidth), defaultValue: NameFloor - CellGutter);

    /// <summary>The width rule's budget for Designation, Integration and Last session:
    /// <see cref="DataCap"/> while the list is wide enough to draw every shown column,
    /// <see cref="TightCap"/> while it is not, and Designation's cap (less its gutters). It is
    /// driven by the width rule alone: the user's own hide is the column's
    /// <see cref="ColumnViewModel.IsShown"/> and nothing else, so unticking Equipment in the
    /// column gear does not drop the others to the tight cap (phase-review P2).
    /// <para>
    /// A shared size group only ever grows, so a cap that tightens resets the column set
    /// (<see cref="TableColumns.Reset"/>): switching a custom column on through the gear once
    /// narrowed these caps and the groups went on reserving what they had measured, and the Expand
    /// button was drawn off the viewport (launched look, 1280 by 800, spec 12.15).
    /// </para></summary>
    public static readonly StyledProperty<double> DataCellMaxWidthProperty =
        AvaloniaProperty.Register<TargetListView, double>(
            nameof(DataCellMaxWidth), defaultValue: DataCap);

    private IReadOnlyList<ColumnViewModel> _columns = [];

    // The list the columns came from, so the view can follow it. Since Phase 20 the collection
    // itself grows after construction: the custom column entries arrive with the first query, and
    // a view that only re-read Columns on a DataContext change would keep summing the six
    // built-ins for the life of the page.
    private TargetListViewModel? _list;

    // The two column sets the markup declares; this file writes the dashboard's drops and caps to
    // them and resets them where a column has to shrink.
    private readonly TableColumns _cols;
    private readonly TableColumns _sessCols;

    public TargetListView()
    {
        InitializeComponent();
        _cols = (TableColumns)Resources["TargetCols"]!;
        _sessCols = (TableColumns)Resources["SessCols"]!;
        _cols["designation"].MaxWidth = DataCap - CellGutter;
        _cols["equipment"].MaxWidth = EquipmentCap - CellGutter;
    }

    public bool IsEquipmentShown
    {
        get => GetValue(IsEquipmentShownProperty);
        private set => SetValue(IsEquipmentShownProperty, value);
    }

    public double NameCellMaxWidth
    {
        get => GetValue(NameCellMaxWidthProperty);
        private set => SetValue(NameCellMaxWidthProperty, value);
    }

    public double DataCellMaxWidth
    {
        get => GetValue(DataCellMaxWidthProperty);
        private set => SetValue(DataCellMaxWidthProperty, value);
    }

    // Ruling C24's figure, decided by Apply and read back by the taken loop.
    private int _drawnCustomColumns = int.MaxValue;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_list is not null)
        {
            _list.Columns.CollectionChanged -= OnColumnsChanged;
            _list.PropertyChanged -= OnListChanged;
            _list.Rows.CollectionChanged -= OnRowsChanged;
        }

        _list = DataContext as TargetListViewModel;

        if (_list is not null)
        {
            _list.Columns.CollectionChanged += OnColumnsChanged;
            _list.PropertyChanged += OnListChanged;
            _list.Rows.CollectionChanged += OnRowsChanged;
        }

        FollowColumns();
        base.OnDataContextChanged(e);
    }

    // Every column, not Equipment alone: the fit width is built from the shown set, so hiding any
    // of them moves it.
    private void FollowColumns()
    {
        foreach (var column in _columns)
        {
            column.PropertyChanged -= OnColumnChanged;
        }

        _columns = _list?.Columns ?? (IReadOnlyList<ColumnViewModel>)[];

        foreach (var column in _columns)
        {
            column.PropertyChanged += OnColumnChanged;
        }

        Apply();
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => FollowColumns();

    // A new page clears the rows before it adds the next set, so the columns are measured afresh
    // rather than keeping the previous page's widest cells.
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResetColumns();
        }
    }

    private void ResetColumns()
    {
        _cols.Reset();
        _sessCols.Reset();
    }

    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The drawn custom set is what the strip's widths are summed from, so the arithmetic runs
        // again when it moves: the definitions arrive with the first query, not at construction.
        if (e.PropertyName is null or nameof(TargetListViewModel.CustomColumns))
        {
            Apply();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Bounds changes arrive on the UI thread from the layout pass itself, so this is a straight
        // read and two property writes with no continuation and no scheduling (TRACKING section 5).
        //
        // The root text size is a setting and it is re-resolved into the live tree with no new
        // DataContext, so a custom column's own width moves with it: the heading it is measured
        // from is drawn at this size (FrameTableView's file name column follows the same property
        // for the same reason).
        if (change.Property == FontSizeProperty)
        {
            // A column measured at the old size would keep that width (a group only grows).
            ResetColumns();
        }

        if (change.Property == BoundsProperty || change.Property == FontSizeProperty)
        {
            Apply();
        }
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e) => Apply();

    private void Apply()
    {
        // One flag per cause (phase-review P2). "fits" is the width rule and nothing else, and it
        // is what the tight caps follow; the user's own hide is each column's IsShown, and only
        // Equipment's drawing combines the two.
        //
        // Ruling C24 is decided here too, in the one place the taken set is computed rather than
        // as a second rule beside it: a custom column is the FIRST thing to give way, last first,
        // until the row fits, and only then does Equipment step aside as it always has. No
        // built-in column and never the Expand button is pushed out by a custom column, and
        // folding the panel or widening the window puts the dropped ones straight back, because
        // this runs on every Bounds change and nothing below it is rebuilt.
        var custom = _columns
            .Where(column => column.IsShown && CustomColumnSlug.IsCustom(column.Key))
            .ToArray();

        var drawn = custom.Length;
        while (drawn > 0 && !RowDrawsWhole(custom, drawn))
        {
            drawn--;
        }

        var customBudget = CustomBudgetFor(custom, drawn);
        _drawnCustomColumns = drawn;
        _list?.SetDrawnCustomColumns(drawn);

        var fits = Bounds.Width >= FitWidthFor(_columns, _ => 0d) + customBudget;
        var equipmentShown = _columns.FirstOrDefault(column => column.Key == "equipment")?.IsShown ?? true;

        IsEquipmentShown = equipmentShown && fits;

        // A dropped column leaves its group and hides its cells. Writing an unchanged value is a
        // no-op, so a Bounds change rebuilds no row unless a column actually flips.
        foreach (var column in _columns.Where(column => !CustomColumnSlug.IsCustom(column.Key)))
        {
            _cols[column.TableKey].IsDropped =
                !(column.Key == "equipment" ? IsEquipmentShown : column.IsShown);
        }

        // The three capped columns are capped by the width rule alone. A custom strip never
        // tightens them: a custom column gives way first and no built-in column pays for one, and
        // these three are the columns that used to pay. The cap is a maximum and not a width, so
        // the tight cap here is exactly what a library with no custom column at all gets at the
        // same allotment.
        var cap = fits ? DataCap : TightCap;
        if (cap != DataCellMaxWidth)
        {
            // The cap moved, so the groups are renamed and measured again rather than keeping the
            // width they last reserved.
            DataCellMaxWidth = cap;
            _cols["designation"].MaxWidth = cap - CellGutter;
            _cols.Reset();
        }

        NameCellMaxWidth = Math.Max(NameFloor, StarRemainderFor(customBudget)) - CellGutter;
    }

    /// <summary>
    /// Whether the row draws whole with the first <paramref name="drawn"/> custom columns on it,
    /// and at no cost to any built-in column.
    /// </summary>
    /// <remarks>
    /// Two costs, because a built-in column can be made to pay in two ways. Equipment steps aside
    /// when the row does not fit, and it must keep the place it would have had with no custom
    /// column at all, so a strip that flips it off is one column too long. The star column takes
    /// what is left after every other shown column, and below its floor it stops shrinking, so
    /// whatever is over is pushed past the list's right edge with the Expand button after it. The
    /// three capped columns cannot pay at all any more: their cap is the same figure whatever is
    /// drawn here.
    /// </remarks>
    private bool RowDrawsWhole(IReadOnlyList<ColumnViewModel> custom, int drawn)
    {
        var shown = new HashSet<string>(
            custom.Take(drawn).Select(column => column.Key), StringComparer.Ordinal);

        var everythingFits = Bounds.Width
            >= FitWidthFor(_columns, key => shown.Contains(key) ? CustomWidthFor(key) : 0d);
        var equipmentPays = !everythingFits && Bounds.Width >= FitWidthFor(_columns, _ => 0d);

        return !equipmentPays && StarRemainderFor(CustomBudgetFor(custom, drawn)) >= NameFloor;
    }

    private double CustomBudgetFor(IReadOnlyList<ColumnViewModel> custom, int drawn)
        => custom.Take(drawn).Sum(column => CustomWidthFor(column.Key));

    /// <summary>What the star column has left once every other shown column and a custom strip of
    /// <paramref name="customBudget"/> have taken their budgets. Read over the shown set, so a
    /// hidden column gives its share to Name rather than reserving it.</summary>
    /// <remarks>A pure function of the arranged width and the shown column set, never of the layout
    /// it produces, so it cannot feed back into the pass that invokes it. The budgets are caps, so
    /// on a page whose cells are narrower than their caps this answers a few pixels less than the
    /// row really leaves; that error is on the safe side of the rule that a built-in column never
    /// pays for a custom one.</remarks>
    private double StarRemainderFor(double customBudget)
    {
        var fits = Bounds.Width >= FitWidthFor(_columns, _ => 0d) + customBudget;
        var equipmentShown = _columns.FirstOrDefault(column => column.Key == "equipment")?.IsShown ?? true;
        var cap = fits ? DataCap : TightCap;

        // Spec 12.15's strip takes its width out of the row like any other column, so the star's
        // remainder is what is left after it too.
        var taken = SessionsBudget + TrailingInset + customBudget;
        foreach (var column in _columns)
        {
            taken += column.Key switch
            {
                "name" => 0d,
                "equipment" => equipmentShown && fits ? EquipmentCap : 0d,
                _ when !column.IsShown => 0d,
                "palette" => PaletteBudget,
                _ when CustomColumnSlug.IsCustom(column.Key) => 0d,
                _ => cap,
            };
        }

        return Bounds.Width - taken;
    }

    // What one drawn custom column's cell takes, the same figure both strips draw it at. A slug the
    // list is not drawing, which is every column the reader has not switched on, contributes
    // nothing: its cells are not built and its strip entry does not exist.
    private double CustomWidthFor(string key)
    {
        var column = _list?.CustomColumns
            .FirstOrDefault(candidate => string.Equals(candidate.Slug, key, StringComparison.Ordinal));

        return column is null ? 0d : CustomCellWidthFor(column.Name, column.Type, FontSize, FontFamily);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
