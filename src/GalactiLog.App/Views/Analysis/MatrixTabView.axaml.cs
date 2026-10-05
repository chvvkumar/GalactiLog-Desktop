using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Analysis;
using LiveChartsCore;

namespace GalactiLog.App.Views.Analysis;

/// <summary>
/// Spec 12.14's Matrix tab body, bound to
/// <see cref="ViewModels.Analysis.MatrixTabViewModel"/>: the caption, the ten by ten heat grid and
/// the legend.
/// </summary>
/// <remarks>
/// <para>
/// The code behind exists for the keyboard's cell layer and nothing else. The grid is one Tab stop
/// (<c>KeyboardNavigation.TabNavigation="Once"</c>) and the four arrow keys move within it, which
/// is the shape <c>Controls/AltitudeArc.cs</c> already ships for a drawn control with many
/// addressable parts: before this, a hundred cells were a hundred tab stops, which is most of the
/// page's whole tab order and a hundred announcements to cross one chart.
/// </para>
/// <para>
/// The two focus handlers tell the grid which cell holds focus, and the grid answers with the
/// chart's own <c>Sections</c>, so the focus box is drawn in series space and lands on the drawn
/// cell whatever the axis gutters do.
/// </para>
/// <para>
/// It also keeps the layer square with the painting: see <see cref="AlignCellLayer"/>.
/// </para>
/// </remarks>
public partial class MatrixTabView : UserControl
{
    // The engine this view is subscribed to, so the detach removes the handler it added rather
    // than reading the property a second time and missing a core that has been replaced.
    private CartesianChartEngine? _engine;

    public MatrixTabView()
    {
        InitializeComponent();

        // Loaded rather than the constructor: rc5.4 builds the chart's engine when the control
        // reaches the visual tree, so there is nothing to subscribe to before then. The alignment
        // is also run once here, because the first measure can already have happened by the time
        // this arrives and its signal is not replayed.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_engine is null && MatrixChart.CoreChart is CartesianChartEngine engine)
        {
            // DrawMarginDefined and not UpdateFinished: measured on this harness, UpdateFinished is
            // never raised at all without a render loop to validate the canvas, while this one is
            // raised inside every measure at the moment the margin becomes known, which covers the
            // first measure, a new result and a resize alike.
            _engine = engine;
            _engine.DrawMarginDefined += OnDrawMarginDefined;
        }

        AlignCellLayer();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_engine is not null)
        {
            _engine.DrawMarginDefined -= OnDrawMarginDefined;
            _engine = null;
        }
    }

    // rc5.4 raises this from inside its own measure. Posting keeps the layout write out of that
    // pass rather than re-entering it, and the post is what the UI thread drains next.
    private void OnDrawMarginDefined(CartesianChartEngine engine)
        => Dispatcher.UIThread.Post(AlignCellLayer);

    /// <summary>
    /// Insets the keyboard's cell layer to the chart's draw margin, which is the rectangle the
    /// hundred cells are actually painted in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chart reserves a gutter on the left for the row labels and one at the bottom for the
    /// column labels, and paints the grid in what is left. A layer spread over the whole control
    /// lays its hundred buttons out at a different origin and a different pitch, so every cell's
    /// reported screen rectangle is wrong by about a gutter width: measured on a hosted grid whose
    /// control was 1048 by 460, the draw margin was 903.70 by 390.65 at (101.78, 23.60), and a
    /// magnifier or a screen reader was pointed a column to the left of the cell it had just
    /// named.
    /// </para>
    /// <para>
    /// The inset is written as <c>Padding</c> rather than as a margin and a size, so the element
    /// itself still covers the chart and a grid the library has not measured yet falls back to the
    /// whole control rather than collapsing to nothing.
    /// </para>
    /// </remarks>
    private void AlignCellLayer()
    {
        if (MatrixChart.CoreChart is not Chart core)
        {
            return;
        }

        var size = core.DrawMarginSize;
        var control = core.ControlSize;
        if (size.Width <= 0f || size.Height <= 0f || control.Width <= 0f || control.Height <= 0f)
        {
            return;
        }

        // Clamped, because a Border rejects a negative padding and a library that ever reported a
        // margin wider than its own control would otherwise take the page down.
        var at = core.DrawMarginLocation;
        MatrixCellKeys.Padding = new Thickness(
            Math.Max(0d, at.X),
            Math.Max(0d, at.Y),
            Math.Max(0d, control.Width - at.X - size.Width),
            Math.Max(0d, control.Height - at.Y - size.Height));
    }

    // The grid's cells hold no state of their own, so both handlers read the button's own
    // DataContext rather than an index.
    private static MatrixCellViewModel? CellOf(object? source)
        => (source as Control)?.DataContext as MatrixCellViewModel;

    private MatrixChartViewModel? Chart => (DataContext as MatrixTabViewModel)?.Chart;

    private void OnCellGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (CellOf(e.Source) is { } cell)
        {
            Chart?.SetFocus(cell);
        }
    }

    private void OnCellLostFocus(object? sender, RoutedEventArgs e)
    {
        // Only the cell that actually holds the box clears it. Avalonia raises LostFocus on the
        // old element before GotFocus on the new one, so an unguarded clear would be harmless
        // today and would blank the box the moment that order changed.
        if (Chart is { } chart && ReferenceEquals(chart.FocusedCell, CellOf(e.Source)))
        {
            chart.SetFocus(null);
        }
    }

    private void OnCellKeyDown(object? sender, KeyEventArgs e)
    {
        var columns = MatrixChartViewModel.Columns.Count;
        var buttons = MatrixCellKeys.GetVisualDescendants().OfType<Button>().ToList();
        var current = buttons.FindIndex(button => button.IsFocused);
        if (current < 0 || buttons.Count == 0)
        {
            return;
        }

        // One cell per press, and the edges STOP rather than wrap: a left arrow in column 0 must
        // not land on the previous row's last cell, which would read as a jump across the grid.
        var column = current % columns;
        var next = e.Key switch
        {
            Key.Left when column > 0 => current - 1,
            Key.Right when column < columns - 1 => current + 1,
            Key.Up when current >= columns => current - columns,
            Key.Down when current + columns < buttons.Count => current + columns,
            _ => -1,
        };

        // An arrow at an edge is still handled, so it neither scrolls the page nor moves focus out
        // of the grid: the reader stays where they are, which is what AltitudeArc's clamp does.
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            e.Handled = true;
        }

        if (next >= 0)
        {
            buttons[next].Focus(NavigationMethod.Directional);
        }
    }
}
