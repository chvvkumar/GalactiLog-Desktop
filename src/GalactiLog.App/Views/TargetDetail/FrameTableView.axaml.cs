using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;

namespace GalactiLog.App.Views.TargetDetail;

/// <summary>
/// design-spec 12.4's frame table. Markup and the three things a view-model cannot do: hand the
/// <c>ListBox</c> its selection collection, measure text, and scroll a container into view.
/// </summary>
public partial class FrameTableView : UserControl
{
    public static readonly StyledProperty<Control?> ToolbarContentProperty =
        AvaloniaProperty.Register<FrameTableView, Control?>(nameof(ToolbarContent));

    /// <summary>Drawn in the toolbar between the count and the actions, wrapping inside that space.</summary>
    public Control? ToolbarContent
    {
        get => GetValue(ToolbarContentProperty);
        set => SetValue(ToolbarContentProperty, value);
    }

    // Held only while attached: a rebuilt table view adopts the same control, so a detached one lets it go.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ToolbarSlot.Content = ToolbarContent;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ToolbarSlot.Content = null;
        base.OnDetachedFromVisualTree(e);
    }

    // The table this view is currently subscribed to. One table view is reused across session
    // loads, so without it every load would leave another handler on the previous view-model.
    private FrameTableViewModel? _watched;

    // Review P3-4. The row under the press, so a release over a different row opens nothing.
    private FrameRowViewModel? _pressedRow;

    public FrameTableView()
    {
        InitializeComponent();

        // ListBox.SelectedItems is a plain CLR property, not a styled one, so it cannot be bound
        // in XAML in Avalonia 11. Handing the control the view-model's own ObservableCollection
        // makes that collection the selection, in both directions, with no synchronisation code
        // and no SelectionChanged mirror: what the user clicks lands in
        // FrameTableViewModel.SelectedRows, and what SelectFrameAt puts there is what the control
        // shows as selected.
        //
        // Handed over in OnDataContextEndUpdate rather than in the constructor, because the
        // DataContext arrives after construction whether the view is built by a template or by an
        // object initializer, and one table view is reused across session loads.
        DataContextChanged += (_, _) =>
        {
            if (_watched is not null)
            {
                _watched.PropertyChanged -= OnTablePropertyChanged;
                _watched = null;
            }

            if (DataContext is FrameTableViewModel table)
            {
                // R9's scroll seam (ruling Q7).
                table.PropertyChanged += OnTablePropertyChanged;
                _watched = table;

                MeasureColumns();
            }
        };

        // A measurement taken before the control has a typeface produces a wrong width silently,
        // so it runs again once the tree is up and a row's cells have been realized.
        Loaded += (_, _) => MeasureColumns();

        // The header band and the rows pan as one. Setting an equal offset raises no change, so
        // the two handlers cannot loop.
        FrameRows.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            (_, e) =>
            {
                if (e.Source is ScrollViewer rows && rows.Offset.X != FrameHeaderScroller.Offset.X)
                {
                    FrameHeaderScroller.Offset = new Vector(rows.Offset.X, FrameHeaderScroller.Offset.Y);
                }
            });
        FrameHeaderScroller.ScrollChanged += (_, _) =>
        {
            var rows = FrameRows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (rows is not null && rows.Offset.X != FrameHeaderScroller.Offset.X)
            {
                rows.Offset = new Vector(FrameHeaderScroller.Offset.X, rows.Offset.Y);
            }
        };
    }

    // The ListBox shares SelectedRows only while it holds that table's rows: it lets go before the
    // context changes, inherited changes included, so a released view never clears the selection,
    // and takes it back once the new rows are in, so the selected rows are found and shown.
    protected override void OnDataContextBeginUpdate()
    {
        base.OnDataContextBeginUpdate();
        FrameRows.SelectedItems = new AvaloniaList<object>();
    }

    protected override void OnDataContextEndUpdate()
    {
        base.OnDataContextEndUpdate();
        if (DataContext is FrameTableViewModel table)
        {
            FrameRows.SelectedItems = table.SelectedRows;
        }
    }

    // Review P3-2. MainWindow binds its FontSize to the stored root text size, and that size is
    // re-resolved into the live tree without a new DataContext, so the column has to be measured
    // again: with trimming off, a name that no longer fits is clipped at the cell edge rather than
    // elided. FontSize inherits down the tree, so a change on the window arrives here.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToolbarContentProperty && this.IsAttachedToVisualTree())
        {
            ToolbarSlot.Content = ToolbarContent;
        }

        if (change.Property == FontSizeProperty)
        {
            MeasureColumns();
        }
    }

    /// <summary>The air after a column's widest text, so the end of one cell never touches the
    /// start of the next, which follows with no margin.</summary>
    public const double ColumnGap = 12d;

    /// <summary>The room a header title keeps beside it for the sort glyph and the divider.</summary>
    public const double HeaderSlack = 20d;

    /// <summary>R5's auto-fit of one column from its widest text and its header, the one formula
    /// the view applies and a test can pin. Rounded up rather than handed over at sub-pixel
    /// precision: the file name cell has no trimming, so a column a fraction of a pixel short
    /// clips the last glyph instead of eliding.</summary>
    public static double AutoFitWidth(double header, double widestCell)
        => Math.Ceiling(Math.Max(header + HeaderSlack, widestCell) + ColumnGap);

    // R5: every column auto-fits the widest text of the loaded night plus its header. Measured
    // here rather than in the view-model because a FormattedText needs a Typeface and a font
    // size, and both are the rendered view's, not the model's: the root size is a setting and the
    // mono family comes from the shared vocabulary (ruling Q10). Every column is measured, hidden
    // ones included, so a column toggled on later already carries its fit.
    //
    // The source is the whole night, not the filtered rows: measuring the filtered subset would
    // narrow the column while an outlier filter is on and widen it again on clear, moving every
    // column to its right under a pointer about to click one (ruling Q9).
    private void MeasureColumns()
    {
        if (DataContext is not FrameTableViewModel table || FontSize <= 0d)
        {
            return;
        }

        // The control's own size, not a realized cell's: no style in this application sets
        // FontSize (FontSizeTokenTest is the enforcement), so the cells inherit exactly this, and
        // on a root text-size change this control is notified before the size has propagated down
        // to them. Reading a cell here would measure the old size (review P3-2).
        var size = FontSize;
        var inherited = new Typeface(
            TextElement.GetFontFamily(this),
            TextElement.GetFontStyle(this),
            TextElement.GetFontWeight(this));
        var headerTypeface = new Typeface(inherited.FontFamily, inherited.Style, FontWeight.SemiBold);

        // The first realized row's cells, in column order, because they carry the family and the
        // padding the styles applied (the file name's mono family above all); the control's own
        // inherited values before the first row exists.
        var cells = RealizedCells();

        for (var index = 0; index < table.Columns.Count; index++)
        {
            var column = table.Columns[index];
            var cell = cells is not null && index < cells.Count ? cells[index] as TextBlock : null;
            var typeface = cell is null ? inherited : new Typeface(cell.FontFamily, cell.FontStyle, cell.FontWeight);
            var chrome = cell is null
                ? 0d
                : cell.Padding.Left + cell.Padding.Right + cell.Margin.Left + cell.Margin.Right;

            var widest = 0d;
            foreach (var text in Candidates(table.CellTextsToMeasure(column.Key)))
            {
                widest = Math.Max(widest, Measure(text, typeface, size));
            }

            table.SetAutoFitWidth(
                column.Key,
                AutoFitWidth(Measure(column.Title, headerTypeface, size), widest + chrome));
        }
    }

    // ponytail: the longest few distinct texts by character count stand in for the whole night,
    // since 400 rows by 32 columns is too many shapings for one UI-thread pass; the file name is
    // mono and the figures are tabular, so a longer string is a wider one for every column but
    // the three free-text ones. Measure every distinct text if a proportional column is ever
    // seen to clip.
    private static IEnumerable<string> Candidates(IReadOnlyList<string> texts)
        => texts.Where(text => text.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderByDescending(text => text.Length).Take(8);

    private static double Measure(string text, Typeface typeface, double size)
        => text.Length == 0
            ? 0d
            : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, null).Width;

    // The row's cells in column order: the children of the first realized row's panel that are
    // neither the row-end button nor the Rig cell, which is not a keyed column.
    private IReadOnlyList<Control>? RealizedCells()
        => FrameRows.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("frame-row"))
            ?.Child is StackPanel panel
                ? [.. panel.Children.Where(child => child is not Button && child.Name != "RigCell")]
                : null;

    // ---- R5's column dividers -------------------------------------------------------------

    // The column under the drag, the pointer's x and the width when it started, and the width the
    // drag has reached, null until the pointer moves so a plain click stores nothing.
    private ColumnViewModel? _dragColumn;
    private double _dragStartX;
    private double _dragStartWidth;
    private double? _dragWidth;

    private void OnDividerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ColumnViewModel column } divider
            || DataContext is not FrameTableViewModel table
            || !e.GetCurrentPoint(divider).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _dragColumn = null;
            table.ResetColumnWidth(column.Key);
            return;
        }

        _dragColumn = column;
        _dragStartX = e.GetPosition(this).X;
        _dragStartWidth = column.Width;
        _dragWidth = null;
        e.Pointer.Capture(divider);
    }

    private void OnDividerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragColumn is { } column && DataContext is FrameTableViewModel table)
        {
            _dragWidth = _dragStartWidth + e.GetPosition(this).X - _dragStartX;
            table.SetColumnWidth(column.Key, _dragWidth.Value);
        }
    }

    private void OnDividerReleased(object? sender, PointerReleasedEventArgs e) => EndDividerDrag();

    private void OnDividerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDividerDrag();

    private void EndDividerDrag()
    {
        var column = _dragColumn;
        var moved = _dragWidth is not null;
        _dragColumn = null;
        _dragWidth = null;
        if (column is not null && moved && DataContext is FrameTableViewModel table)
        {
            table.StoreColumnWidth(column.Key);
        }
    }

    // R9's "scrolls the list so the row is visible". ScrollIntoView is a control call, so it lives
    // here and the view-model publishes HighlightedRow as the seam. Leaving the strip sets it to
    // null, which scrolls nothing: R9 says the scroll stays where it is.
    private void OnTablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FrameTableViewModel.HighlightedRow)
            && sender is FrameTableViewModel { HighlightedRow: { } row })
        {
            FrameRows.ScrollIntoView(row);
        }
    }

    // R8: a plain click on a row opens the preview at that row; Ctrl and Shift extend the
    // selection and open nothing. On PointerReleased, not PointerPressed: the ListBox moves the
    // selection on press, so opening on press would preview the row that was selected a moment
    // ago (ruling Q11).
    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A right-click opens nothing: the row-end actions are the menu's job.
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        // A whole-modifier-set comparison rather than a flag test, so Ctrl+Shift-click is covered
        // by the same arm.
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (DataContext is not FrameTableViewModel table
            || sender is not Control { DataContext: FrameRowViewModel row } handler)
        {
            return;
        }

        // The row-end flyout button sits inside the row, and without this a click on it would open
        // the preview as well as the menu. Walked only as far as the row itself.
        if (e.Source is Visual source
            && source.GetSelfAndVisualAncestors()
                .TakeWhile(visual => !ReferenceEquals(visual, handler))
                .OfType<Button>()
                .Any())
        {
            return;
        }

        // Review P3-4. R8 names a click, not a drag: a press on one row released over another
        // must not preview the row the pointer happened to be over at the end, and the selection
        // the ListBox set on press is the pressed row's.
        if (!ReferenceEquals(_pressedRow, row))
        {
            _pressedRow = null;
            return;
        }

        _pressedRow = null;

        // Resolved from the template's own DataContext rather than from SelectedRows, which a
        // Ctrl-click would have left holding several rows.
        table.OpenPreviewCommand.Execute(row);
    }

    // The row the press landed on, read back by the release. Null between gestures and after any
    // release, so a release with no matching press opens nothing.
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
        => _pressedRow = (sender as Control)?.DataContext as FrameRowViewModel;
}
