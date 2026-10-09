using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Controls.Table;

/// <summary>
/// The table spine's figures, and the only place a table figure is written (spec.md item 1).
/// Markup reaches them through <c>x:Static</c>; arithmetic reads the constants.
/// </summary>
public static class TableMetrics
{
    /// <summary>The cell gutter, each side of every cell.</summary>
    public const double Gutter = 8;

    public static readonly Thickness CellPadding = new(Gutter, 0);

    /// <summary>A check box column: a 20 pixel box plus the two gutters.</summary>
    public const double CheckColumnWidth = 20 + 2 * Gutter;

    /// <summary>Header, total and data rows alike.</summary>
    public const double RowMinHeight = 28;

    /// <summary>The space between two sections of one table page.</summary>
    public const double SectionGap = 16;

    /// <summary>Table scroll bars only (spec.md item 8, ruling R2); the app default stays 8.</summary>
    public const double ScrollBarSize = 12;

    /// <summary>A header or total row outside a scroller whose vertical bar is reserved, when the
    /// row's flex column is not its last column.</summary>
    public static readonly Thickness ScrollInset = new(0, 0, ScrollBarSize, 0);

    /// <summary>The indent of a sub-row's label under its parent's label, reserved by a spacer
    /// inside the label cell so the cell's own gutter stays <see cref="Gutter"/>.</summary>
    public const double SubRowIndent = 20;
}

/// <summary>What a column holds. The kind owns alignment, trimming and gutter (Theme/Table.axaml);
/// a cell never sets them itself.</summary>
public enum ColumnKind
{
    /// <summary>Text, dates and identifiers: left-aligned, may trim with a tooltip.</summary>
    Text,

    /// <summary>A quantity: right-aligned, never trimmed.</summary>
    Number,

    /// <summary>A check box or flag: centered, <see cref="TableMetrics.CheckColumnWidth"/> wide.</summary>
    Check,

    /// <summary>Runtime columns: an <c>ItemsControl</c> of <see cref="TableStripCell"/>s.</summary>
    Strip,
}

/// <summary>Which rules a row draws and which classes its cells take.</summary>
public enum RowKind
{
    Data,

    /// <summary>Header text: <c>t-label</c>, a rule under the row.</summary>
    Header,

    /// <summary>A total under the data rows: semibold, a rule above the row.</summary>
    Total,

    /// <summary>A total directly under the header: semibold, a rule under the row, which is the
    /// edge facing the data rows.</summary>
    LeadTotal,
}

/// <summary>One column of a <see cref="TableColumns"/> set. Styled properties so code behind can
/// change a column at run time and every row follows.</summary>
public sealed class TableColumn : AvaloniaObject
{
    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<TableColumn, string>(nameof(Key), "");

    public static readonly StyledProperty<ColumnKind> KindProperty =
        AvaloniaProperty.Register<TableColumn, ColumnKind>(nameof(Kind));

    public static readonly StyledProperty<GridLength> WidthProperty =
        AvaloniaProperty.Register<TableColumn, GridLength>(nameof(Width), GridLength.Auto);

    public static readonly StyledProperty<double> MinWidthProperty =
        AvaloniaProperty.Register<TableColumn, double>(nameof(MinWidth));

    public static readonly StyledProperty<double> MaxWidthProperty =
        AvaloniaProperty.Register<TableColumn, double>(nameof(MaxWidth), double.PositiveInfinity);

    public static readonly StyledProperty<string?> GroupProperty =
        AvaloniaProperty.Register<TableColumn, string?>(nameof(Group));

    public static readonly StyledProperty<bool> RuleBeforeProperty =
        AvaloniaProperty.Register<TableColumn, bool>(nameof(RuleBefore));

    public static readonly StyledProperty<bool> IsDroppedProperty =
        AvaloniaProperty.Register<TableColumn, bool>(nameof(IsDropped));

    /// <summary><c>[a-z][a-z0-9]*</c>. Cells name it through <c>t:TableRow.Col</c>.</summary>
    public string Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public ColumnKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Auto by default; <c>*</c> marks the one flex column. A Check column ignores it.</summary>
    public GridLength Width
    {
        get => GetValue(WidthProperty);
        set => SetValue(WidthProperty, value);
    }

    public double MinWidth
    {
        get => GetValue(MinWidthProperty);
        set => SetValue(MinWidthProperty, value);
    }

    /// <summary>Text columns only, and written to each cell, never to the column: a shared size
    /// group's minimum is not clamped by a column maximum (spine-spec section 0 item 3).</summary>
    public double MaxWidth
    {
        get => GetValue(MaxWidthProperty);
        set => SetValue(MaxWidthProperty, value);
    }

    /// <summary>An explicit group name, for one column aligned across two tables in one scope.
    /// Used verbatim and not reset by <see cref="TableColumns.Reset"/>.</summary>
    public string? Group
    {
        get => GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    /// <summary>A vertical rule at this column's left edge, on every row kind.</summary>
    public bool RuleBefore
    {
        get => GetValue(RuleBeforeProperty);
        set => SetValue(RuleBeforeProperty, value);
    }

    /// <summary>The column leaves its group, collapses to zero width and its cells hide.</summary>
    public bool IsDropped
    {
        get => GetValue(IsDroppedProperty);
        set => SetValue(IsDroppedProperty, value);
    }
}

/// <summary>
/// One table's column set, declared once in the view's resources and reached with
/// <c>StaticResource</c> by its header, total and data rows (spec.md item 9).
/// </summary>
public sealed class TableColumns : AvaloniaList<TableColumn>
{
    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9]*$", RegexOptions.Compiled);
    private static readonly Regex IdPattern = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    public TableColumns()
    {
        // Clear reports the removed columns, so their change handlers are released.
        ResetBehavior = ResetBehavior.Remove;
        CollectionChanged += OnCollectionChanged;
    }

    /// <summary><c>[A-Za-z][A-Za-z0-9]*</c>: the prefix of every generated group name.</summary>
    public string Id { get; set; } = "";

    /// <summary>Bumped by <see cref="Reset"/>; part of every generated group name.</summary>
    public int Generation { get; private set; }

    /// <summary>Raised on a list change, on any column property change and on <see cref="Reset"/>.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The one way a column shrinks: a shared size group only ever grows, so every generated group
    /// is renamed and measured afresh. The owning view calls it when its items source is replaced
    /// or the root text size changes.
    /// </summary>
    public void Reset()
    {
        Generation++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public TableColumn this[string key] => this[IndexOf(key)];

    internal int IndexOf(string key)
    {
        for (var i = 0; i < Count; i++)
        {
            if (this[i].Key == key)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Table '{Id}' has no column '{key}'.");
    }

    internal string? GroupFor(TableColumn column)
        => column.IsDropped || column.Width.IsStar ? null
            : column.Group ?? $"{Id}_{column.Key}_g{Generation}";

    /// <summary>Fails loud on a malformed set, at every build rather than as a misdrawn table.
    /// </summary>
    internal void EnsureValid()
    {
        if (!IdPattern.IsMatch(Id))
        {
            throw new InvalidOperationException($"Table id '{Id}' must match {IdPattern}.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in this)
        {
            if (!KeyPattern.IsMatch(column.Key) || !keys.Add(column.Key))
            {
                throw new InvalidOperationException(
                    $"Table '{Id}': column key '{column.Key}' is malformed or repeated.");
            }

            if (column.Kind is ColumnKind.Number or ColumnKind.Check && double.IsFinite(column.MaxWidth))
            {
                throw new InvalidOperationException(
                    $"Table '{Id}': {column.Kind} column '{column.Key}' cannot have a MaxWidth; a figure never trims.");
            }
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (TableColumn column in e.OldItems ?? Array.Empty<TableColumn>())
        {
            column.PropertyChanged -= OnColumnChanged;
        }

        foreach (TableColumn column in e.NewItems ?? Array.Empty<TableColumn>())
        {
            column.PropertyChanged += OnColumnChanged;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// One table row: a <see cref="Grid"/> whose column definitions are built from a shared
/// <see cref="TableColumns"/> and whose cells are placed and classed by their
/// <c>t:TableRow.Col</c> key. Draws the table's rules itself. Nothing else: no templating, no
/// generated cells, no width arithmetic of its own.
/// </summary>
/// <remarks>Grid has no bindable column set (spine-spec section 0 item 1), so each row builds its
/// own definitions from the shared description, and the shared size groups line the rows up.
/// </remarks>
public class TableRow : Grid
{
    public static readonly StyledProperty<TableColumns?> ColumnsProperty =
        AvaloniaProperty.Register<TableRow, TableColumns?>(nameof(Columns));

    public static readonly StyledProperty<RowKind> KindProperty =
        AvaloniaProperty.Register<TableRow, RowKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> RuleBrushProperty =
        AvaloniaProperty.Register<TableRow, IBrush?>(nameof(RuleBrush));

    public static readonly StyledProperty<IBrush?> EmphasisRuleBrushProperty =
        AvaloniaProperty.Register<TableRow, IBrush?>(nameof(EmphasisRuleBrush));

    /// <summary>The column key a cell sits in. Required on every direct child.</summary>
    public static readonly AttachedProperty<string?> ColProperty =
        AvaloniaProperty.RegisterAttached<TableRow, Control, string?>("Col");

    // The MaxWidth each cell took from its column, at Style priority so a local value wins.
    private static readonly ConditionalWeakTable<Control, IDisposable> CellCaps = new();

    private readonly RuleLayer _layer;
    private List<(Point A, Point B, bool Emphasis)> _lines = [];
    private TableColumns? _subscribed;
    private bool _untagged;

    static TableRow()
    {
        AffectsArrange<TableRow>(KindProperty);
        // A cell whose key changes after it joined the row, by a binding for example, is re-tagged.
        ColProperty.Changed.AddClassHandler<Control>((cell, _) => (cell.Parent as TableRow)?.Retag());
    }

    public TableRow()
    {
        _layer = new RuleLayer(this) { ZIndex = int.MaxValue, IsHitTestVisible = false };
        VisualChildren.Add(_layer);
        UpdatePseudoClasses();
    }

    public TableColumns? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public RowKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Vertical rules (<see cref="TableColumn.RuleBefore"/>).</summary>
    public IBrush? RuleBrush
    {
        get => GetValue(RuleBrushProperty);
        set => SetValue(RuleBrushProperty, value);
    }

    /// <summary>The header's and the total's horizontal rule.</summary>
    public IBrush? EmphasisRuleBrush
    {
        get => GetValue(EmphasisRuleBrushProperty);
        set => SetValue(EmphasisRuleBrushProperty, value);
    }

    public static string? GetCol(Control cell) => cell.GetValue(ColProperty);

    public static void SetCol(Control cell, string? key) => cell.SetValue(ColProperty, key);

    /// <summary>The rule segments of the last arrange, in row coordinates, for tests.</summary>
    internal IReadOnlyList<(Point A, Point B)> RuleLines => _lines.Select(line => (line.A, line.B)).ToList();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ColumnsProperty)
        {
            if (this.IsAttachedToVisualTree())
            {
                Subscribe();
            }

            Rebuild();
        }
        else if (change.Property == KindProperty)
        {
            UpdatePseudoClasses();
            Retag();
        }
        else if (change.Property == RuleBrushProperty || change.Property == EmphasisRuleBrushProperty)
        {
            _layer.InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // A recycled row picks up whatever changed while it was detached.
        Subscribe();
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe();
    }

    // Compiled XAML adds a cell before it sets the cell's t:TableRow.Col, so the cells are tagged
    // (and a missing key fails) at the next measure, not here.
    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        Retag();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_untagged)
        {
            TagCells();
            _untagged = false;
        }

        _layer.Measure(availableSize);
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        _layer.Arrange(new Rect(size));
        var lines = Rules(size);
        if (!lines.SequenceEqual(_lines))
        {
            _lines = lines;
            _layer.InvalidateVisual();
        }

        return size;
    }

    // Panel.Render is sealed, so the rules are drawn by a visual child over the cells, the way
    // Grid draws its own grid lines. It is not a logical child and takes no grid cell.
    private sealed class RuleLayer(TableRow row) : Control
    {
        public override void Render(DrawingContext context)
        {
            foreach (var (a, b, emphasis) in row._lines)
            {
                if ((emphasis ? row.EmphasisRuleBrush : row.RuleBrush) is { } brush)
                {
                    context.DrawLine(new Pen(brush, 1), a, b);
                }
            }
        }
    }

    // 1 pixel lines on the pixel centre: the header's and the lead total's bottom edge, the total's
    // top edge, and the left edge of every drawn RuleBefore column. Data rows draw no horizontal
    // rule; the zebra separates them.
    private List<(Point A, Point B, bool Emphasis)> Rules(Size size)
    {
        var lines = new List<(Point, Point, bool)>();
        var bottom = Math.Max(0.5, size.Height - 0.5);
        switch (Kind)
        {
            case RowKind.Header or RowKind.LeadTotal:
                lines.Add((new Point(0, bottom), new Point(size.Width, bottom), true));
                break;
            case RowKind.Total:
                lines.Add((new Point(0, 0.5), new Point(size.Width, 0.5), true));
                break;
        }

        if (Columns is { } columns && columns.Count == ColumnDefinitions.Count)
        {
            var left = 0d;
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].RuleBefore && !columns[i].IsDropped)
                {
                    var x = Math.Round(left) + 0.5;
                    lines.Add((new Point(x, 0), new Point(x, size.Height), false));
                }

                left += ColumnDefinitions[i].ActualWidth;
            }
        }

        return lines;
    }

    private void Subscribe()
    {
        if (ReferenceEquals(_subscribed, Columns))
        {
            return;
        }

        Unsubscribe();
        _subscribed = Columns;
        if (_subscribed is not null)
        {
            _subscribed.Changed += OnColumnsChanged;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnColumnsChanged;
            _subscribed = null;
        }
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        ColumnDefinitions.Clear();
        if (Columns is not { } columns)
        {
            return;
        }

        columns.EnsureValid();
        foreach (var column in columns)
        {
            ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = column.IsDropped ? GridLength.Auto
                    : column.Kind == ColumnKind.Check ? new GridLength(TableMetrics.CheckColumnWidth)
                    : column.Width,
                MinWidth = column.IsDropped ? 0 : column.MinWidth,
                SharedSizeGroup = columns.GroupFor(column),
            });
        }

        Retag();
    }

    private void TagCells()
    {
        if (Columns is not { } columns)
        {
            return;
        }

        foreach (var cell in Children)
        {
            var key = GetCol(cell)
                ?? throw new InvalidOperationException(
                    $"A cell of table '{columns.Id}' ({cell.GetType().Name}) has no t:TableRow.Col.");
            var index = columns.IndexOf(key);
            var column = columns[index];

            SetColumn(cell, index);
            TableCells.Classify(cell, column.Kind, Kind);
            TableCells.Toggle(cell, "tc-dropped", column.IsDropped);

            if (CellCaps.TryGetValue(cell, out var previous))
            {
                previous.Dispose();
                CellCaps.Remove(cell);
            }

            if (column.Kind == ColumnKind.Text && double.IsFinite(column.MaxWidth)
                && cell.SetValue(MaxWidthProperty, column.MaxWidth, BindingPriority.Style) is { } cap)
            {
                CellCaps.Add(cell, cap);
            }
        }
    }

    private void Retag()
    {
        _untagged = true;
        InvalidateMeasure();
    }

    private void UpdatePseudoClasses()
    {
        PseudoClasses.Set(":header", Kind == RowKind.Header);
        PseudoClasses.Set(":total", Kind is RowKind.Total or RowKind.LeadTotal);
        PseudoClasses.Set(":data", Kind == RowKind.Data);
    }
}

/// <summary>
/// One runtime column's cell (spine-spec 3.6): the integration matrices hold one column per
/// filter, known only at run time. Sits in an <c>ItemsControl</c> in a Strip column; its one
/// <c>Auto</c> column joins the group <c>{Id}_{Group}_g{Generation}</c> of the nearest ancestor
/// <see cref="TableRow"/>'s set, so a filter's cells line up across rows and follow
/// <see cref="TableColumns.Reset"/>.
/// </summary>
public class TableStripCell : Grid
{
    public static readonly StyledProperty<string?> GroupProperty =
        AvaloniaProperty.Register<TableStripCell, string?>(nameof(Group));

    public static readonly StyledProperty<ColumnKind> KindProperty =
        AvaloniaProperty.Register<TableStripCell, ColumnKind>(nameof(Kind), ColumnKind.Number);

    private TableRow? _row;
    private TableColumns? _subscribed;

    /// <summary>The per-item column id, for example <c>f0</c>, supplied by the cell view model.
    /// </summary>
    public string? Group
    {
        get => GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    public ColumnKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GroupProperty || change.Property == KindProperty)
        {
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _row = this.FindAncestorOfType<TableRow>();
        _subscribed = _row?.Columns;
        if (_subscribed is not null)
        {
            _subscribed.Changed += OnColumnsChanged;
        }

        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnColumnsChanged;
            _subscribed = null;
        }

        _row = null;
    }

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        TagCells();
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        ColumnDefinitions.Clear();
        ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            SharedSizeGroup = _row?.Columns is { } columns && !string.IsNullOrEmpty(Group)
                ? $"{columns.Id}_{Group}_g{columns.Generation}"
                : null,
        });
        TagCells();
    }

    private void TagCells()
    {
        foreach (var cell in Children)
        {
            TableCells.Classify(cell, Kind, _row?.Kind ?? RowKind.Data);
        }
    }
}

/// <summary>The cell classes the two row types share; Theme/Table.axaml keys every rule on them.
/// </summary>
internal static class TableCells
{
    private static readonly (ColumnKind Kind, string Class)[] KindClasses =
    [
        (ColumnKind.Text, "tc-text"),
        (ColumnKind.Number, "tc-num"),
        (ColumnKind.Check, "tc-check"),
        (ColumnKind.Strip, "tc-strip"),
    ];

    public static void Classify(Control cell, ColumnKind kind, RowKind row)
    {
        foreach (var (each, name) in KindClasses)
        {
            Toggle(cell, name, each == kind);
        }

        Toggle(cell, "tc-head", row == RowKind.Header);
        Toggle(cell, "tc-total", row is RowKind.Total or RowKind.LeadTotal);
        if (row == RowKind.Header && cell is TextBlock)
        {
            Toggle(cell, "t-label", true);
        }
    }

    public static void Toggle(Control cell, string name, bool on)
    {
        if (on && !cell.Classes.Contains(name))
        {
            cell.Classes.Add(name);
        }
        else if (!on)
        {
            cell.Classes.Remove(name);
        }
    }
}

/// <summary>Attached table behaviour for an items host or a scroller, plus the markup helpers the
/// shared templates in Theme/Table.axaml need.</summary>
public static class Table
{
    /// <summary>
    /// Spec.md item 8, ruling R2: a table's scroll bars never auto-hide and are 12 pixels. The
    /// value is set on the control and on its template's <c>PART_ScrollViewer</c> directly, because
    /// <c>AllowAutoHide</c> does not inherit, and the bar size is a resource on the control itself,
    /// because a style's resources are not scoped by its selector (spine-spec section 0 items 5
    /// and 6). Also adds the <c>table</c> class, which carries the zebra and the row states.
    /// </summary>
    public static readonly AttachedProperty<bool> ScrollerProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Scroller", typeof(Table));

    /// <summary>A custom column's heading alignment: a check box column centres over its boxes,
    /// every other kind is text and sits left.</summary>
    public static readonly FuncValueConverter<CustomColumnType, HorizontalAlignment> CustomAlign =
        new(type => type == CustomColumnType.Boolean ? HorizontalAlignment.Center : HorizontalAlignment.Left);

    static Table() => ScrollerProperty.Changed.AddClassHandler<Control>(OnScrollerChanged);

    public static bool GetScroller(Control control) => control.GetValue(ScrollerProperty);

    public static void SetScroller(Control control, bool value) => control.SetValue(ScrollerProperty, value);

    private static void OnScrollerChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        ScrollViewer.SetAllowAutoHide(control, false);
        control.Resources["ScrollBarSize"] = TableMetrics.ScrollBarSize;
        TableCells.Toggle(control, "table", true);
        if (control is TemplatedControl templated and not ScrollViewer)
        {
            templated.TemplateApplied += (_, applied) =>
            {
                if (applied.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is { } inner)
                {
                    inner.AllowAutoHide = false;
                }
            };
        }
    }
}
