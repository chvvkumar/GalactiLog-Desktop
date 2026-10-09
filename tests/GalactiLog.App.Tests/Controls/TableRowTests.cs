using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Dashboard;
using Xunit;

namespace GalactiLog.App.Tests.Controls;

// The table spine (spine-spec section 6.1): shared columns, cell tagging, rules, zebra, scroll bars
// and strip cells, each proven on rows built in code rather than through a view.
public class TableRowTests
{
    private const double Gutter = TableMetrics.Gutter;

    private static TableColumns Set(string id, params TableColumn[] columns)
    {
        var set = new TableColumns { Id = id };
        set.AddRange(columns);
        return set;
    }

    private static TableColumn Col(string key, ColumnKind kind = ColumnKind.Text) => new() { Key = key, Kind = kind };

    private static T Cell<T>(string key, T cell) where T : Control
    {
        TableRow.SetCol(cell, key);
        return cell;
    }

    private static TextBlock Text(string key, string text) => Cell(key, new TextBlock { Text = text });

    private static TableRow Row(TableColumns columns, RowKind kind, params Control[] cells)
    {
        var row = new TableRow { Columns = columns, Kind = kind };
        row.Children.AddRange(cells);
        return row;
    }

    private static Window Show(Control content, double width = 800, double height = 600)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static StackPanel Scope(params Control[] rows)
    {
        var panel = new StackPanel();
        Grid.SetIsSharedSizeScope(panel, true);
        panel.Children.AddRange(rows);
        return panel;
    }

    private static double Left(Visual cell, Visual root) => cell.TranslatePoint(default, root)!.Value.X;

    private static double Right(Visual cell, Visual root) => Left(cell, root) + cell.Bounds.Width;

    private static Color Resource(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value));
        return ((ISolidColorBrush)value!).Color;
    }

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static TableColumns Metrics() => Set(
        "Metrics",
        Col("label"),
        Col("hfr", ColumnKind.Number),
        Col("stars", ColumnKind.Number),
        new TableColumn { Key = "spare", Width = GridLength.Star });

    [AvaloniaFact]
    public void T1_RowsOfOneSet_ShareTheirColumns()
    {
        var columns = Metrics();
        var header = Row(columns, RowKind.Header, Text("label", "Filter"), Text("hfr", TableHeads.Hfr), Text("stars", TableHeads.Stars));
        var total = Row(columns, RowKind.Total, Text("label", "All frames"), Text("hfr", "2.10"), Text("stars", "812"));
        var narrow = Row(columns, RowKind.Data, Text("label", "L"), Text("hfr", "2.0"), Text("stars", "9"));
        var wide = Row(columns, RowKind.Data, Text("label", "Hydrogen alpha"), Text("hfr", "2.31"), Text("stars", "12,345"));
        var window = Show(Scope(header, total, narrow, wide));

        TableRow[] rows = [header, total, narrow, wide];
        for (var i = 0; i < columns.Count - 1; i++)
        {
            Assert.All(rows, row => Assert.Equal(header.ColumnDefinitions[i].ActualWidth, row.ColumnDefinitions[i].ActualWidth, 0.5));
        }

        // The widest figure sets the header's column too.
        var figure = wide.Children[2];
        Assert.Equal(figure.DesiredSize.Width, header.ColumnDefinitions[2].ActualWidth, 0.5);

        // Every figure ends where its heading ends.
        foreach (var index in new[] { 1, 2 })
        {
            Assert.All(rows, row => Assert.Equal(Right(header.Children[index], window), Right(row.Children[index], window), 0.5));
        }

        // Content to content, two gutters: the widest label against the next column's figure.
        Assert.Equal(2 * Gutter, Left(wide.Children[1], window) - Right(wide.Children[0], window), 0.5);
        Assert.Equal(Gutter, Left(wide.Children[0], window), 0.5);

        window.Close();
    }

    [AvaloniaFact]
    public void T2_ACellWithoutAKnownColumn_ThrowsAtLayout()
    {
        // Not at Add: compiled XAML adds a cell before it sets the cell's key.
        var missing = Row(Metrics(), RowKind.Data, new TextBlock { Text = "no key" });
        var unknown = Row(Metrics(), RowKind.Data, Text("nope", "unknown key"));

        Assert.Throws<InvalidOperationException>(() => missing.Measure(Size.Infinity));
        Assert.Throws<InvalidOperationException>(() => unknown.Measure(Size.Infinity));
    }

    [AvaloniaFact]
    public void T2_ACellKeyedAfterItJoined_IsTaggedFromItsLatestKey()
    {
        var cell = new TextBlock { Text = "2.10" };
        var row = Row(Metrics(), RowKind.Data, cell);
        TableRow.SetCol(cell, "hfr");
        var window = Show(Scope(row));
        Assert.Equal(1, Grid.GetColumn(cell));

        TableRow.SetCol(cell, "stars");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, Grid.GetColumn(cell));
        Assert.Contains("tc-num", cell.Classes);

        window.Close();
    }

    [AvaloniaFact]
    public void T2_TheSpineSpecMarkup_LoadsFromCompiledXaml()
    {
        var probe = new TableProbe();
        var window = Show(probe);

        var rows = probe.GetVisualDescendants().OfType<TableRow>().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, Grid.GetColumn(row.Children[1])));
        Assert.Contains("tc-head", rows[0].Children[1].Classes);
        Assert.Contains("tc-num", rows[2].Children[1].Classes);
        TableAssert.Conventions(probe);

        window.Close();
    }

    [AvaloniaFact]
    public void T3_AFigureCannotBeCapped_AndATextCapLandsOnTheCell()
    {
        var capped = Metrics();
        capped["hfr"].MaxWidth = 40;
        Assert.Throws<InvalidOperationException>(() => new TableRow { Columns = capped });

        var columns = Metrics();
        columns["label"].MaxWidth = 60;
        var cell = Text("label", "A long filter name that has to trim");
        var local = Text("label", "Local cap");
        local.MaxWidth = 30;
        var row = Row(columns, RowKind.Data, cell);
        var other = Row(columns, RowKind.Data, local);
        var window = Show(Scope(row, other));

        Assert.Equal(60, cell.MaxWidth);
        Assert.Equal(30, local.MaxWidth);
        Assert.True(double.IsPositiveInfinity(row.ColumnDefinitions[0].MaxWidth));
        Assert.Equal(60 + 2 * Gutter, row.ColumnDefinitions[0].ActualWidth, 0.5);

        window.Close();
    }

    [AvaloniaFact]
    public void T4_ADroppedColumn_HidesItsCellsAndCollapses_AndComesBack()
    {
        var columns = Metrics();
        var cell = Text("stars", "12,345");
        var row = Row(columns, RowKind.Data, Text("label", "L"), Text("hfr", "2.0"), cell);
        var window = Show(Scope(row));
        Assert.NotNull(row.ColumnDefinitions[2].SharedSizeGroup);

        columns["stars"].IsDropped = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(cell.IsVisible);
        Assert.Equal(0, row.ColumnDefinitions[2].ActualWidth);
        Assert.Null(row.ColumnDefinitions[2].SharedSizeGroup);

        columns["stars"].IsDropped = false;
        Dispatcher.UIThread.RunJobs();

        Assert.True(cell.IsVisible);
        Assert.True(row.ColumnDefinitions[2].ActualWidth > 2 * Gutter);
        Assert.Equal("Metrics_stars_g0", row.ColumnDefinitions[2].SharedSizeGroup);

        window.Close();
    }

    [AvaloniaFact]
    public void T5_Reset_ShrinksAColumnToItsWidestRemainingCell()
    {
        var columns = Metrics();
        var keep = Row(columns, RowKind.Data, Text("label", "L"), Text("hfr", "2.0"), Text("stars", "9"));
        var wide = Row(columns, RowKind.Data, Text("label", "L"), Text("hfr", "2.0"), Text("stars", "123,456,789"));
        var scope = Scope(keep, wide);
        var window = Show(scope);
        var wideWidth = keep.ColumnDefinitions[2].ActualWidth;

        scope.Children.Remove(wide);
        columns.Reset();
        Dispatcher.UIThread.RunJobs();

        var nine = keep.Children[2];
        Assert.Equal(1, columns.Generation);
        Assert.Equal(nine.DesiredSize.Width, keep.ColumnDefinitions[2].ActualWidth, 0.5);
        Assert.True(keep.ColumnDefinitions[2].ActualWidth < wideWidth);

        window.Close();
    }

    [AvaloniaFact]
    public void T6_RuleLines_FollowTheRowKind()
    {
        var columns = Set(
            "Ruled",
            Col("label"),
            Col("a", ColumnKind.Number),
            new TableColumn { Key = "total", Kind = ColumnKind.Number, RuleBefore = true });
        TableRow Make(RowKind kind) => Row(columns, kind, Text("label", "Label"), Text("a", "1"), Text("total", "1"));
        var header = Make(RowKind.Header);
        var lead = Make(RowKind.LeadTotal);
        var data = Make(RowKind.Data);
        var total = Make(RowKind.Total);
        var window = Show(Scope(header, lead, data, total));

        bool Horizontal((Point A, Point B) line, double y) => Math.Abs(line.A.Y - line.B.Y) < 0.01 && Math.Abs(line.A.Y - y) <= 1;
        Assert.Contains(header.RuleLines, line => Horizontal(line, header.Bounds.Height));
        Assert.Contains(lead.RuleLines, line => Horizontal(line, lead.Bounds.Height));
        Assert.Contains(total.RuleLines, line => Horizontal(line, 0));
        Assert.DoesNotContain(total.RuleLines, line => Horizontal(line, total.Bounds.Height));
        Assert.DoesNotContain(data.RuleLines, line => Math.Abs(line.A.Y - line.B.Y) < 0.01);

        var edge = header.ColumnDefinitions[0].ActualWidth + header.ColumnDefinitions[1].ActualWidth;
        foreach (var row in new[] { header, lead, data, total })
        {
            Assert.Contains(row.RuleLines, line => Math.Abs(line.A.X - line.B.X) < 0.01 && Math.Abs(line.A.X - edge) <= 1);
        }

        window.Close();
    }

    // Gate: :nth-child reads the item index under virtualisation, so the zebra follows the data and
    // not the realized slot. If this fails, spine-spec 6.1 T7 names the fallback.
    [AvaloniaFact]
    public void T7_Zebra_FollowsTheItemIndex_AtTheTopAndAfterScrolling()
    {
        var list = new ListBox { ItemsSource = Enumerable.Range(0, 200).Select(i => $"Night {i}").ToList() };
        Table.SetScroller(list, true);
        var window = Show(list, 400, 300);
        var zebra = Resource("ColorBorderDefault");

        void AssertZebra()
        {
            var realized = list.GetRealizedContainers().OfType<ListBoxItem>().ToList();
            Assert.True(realized.Count > 3, "Nothing to compare: too few containers realized.");
            Assert.All(realized, item =>
            {
                var odd = list.IndexFromContainer(item) % 2 == 1;
                Assert.Equal(odd, ColorOf(item.Background) == zebra);
            });
        }

        AssertZebra();
        list.ScrollIntoView(199);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(list.GetRealizedContainers(), item => list.IndexFromContainer(item) == 199);
        AssertZebra();

        window.Close();
    }

    [AvaloniaFact]
    public void T7_Zebra_OnAnUnvirtualisedItemsControl()
    {
        var items = new ItemsControl { ItemsSource = Enumerable.Range(0, 6).Select(i => $"Row {i}").ToList() };
        items.Classes.Add("table");
        var window = Show(items);
        var zebra = Resource("ColorBorderDefault");

        var containers = items.GetRealizedContainers().OfType<ContentPresenter>().ToList();
        Assert.Equal(6, containers.Count);
        Assert.All(containers, presenter =>
            Assert.Equal(items.IndexFromContainer(presenter) % 2 == 1, ColorOf(presenter.Background) == zebra));

        window.Close();
    }

    [AvaloniaFact]
    public void T8_AMissingValue_DrawsInTheFaintInk_AndAZeroDoesNot()
    {
        var columns = Metrics();
        var dash = Text("hfr", "-");
        var zero = Text("stars", "0");
        var window = Show(Scope(Row(columns, RowKind.Data, Text("label", "L"), dash, zero)));
        var faint = Resource("ColorTextTertiary");

        Assert.Equal(faint, ColorOf(dash.Foreground));
        Assert.NotEqual(faint, ColorOf(zero.Foreground));

        window.Close();
    }

    // Gate: the per-control ScrollBarSize resource reaches the Fluent bar inside a template, and
    // the bar is expanded at rest. A plain scroller beside them keeps the app's 8 pixel overlay.
    [AvaloniaFact]
    public void T9_ATableScroller_KeepsAFullTwelvePixelBar_AndAPlainOneStaysEight()
    {
        static Border Tall() => new() { Height = 2000, Width = 100 };

        var list = new ListBox { Height = 200, ItemsSource = Enumerable.Range(0, 100).Select(i => $"Item {i}").ToList() };
        ScrollViewer.SetVerticalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Visible);
        Table.SetScroller(list, true);
        var scroller = new ScrollViewer { Height = 200, Content = Tall() };
        Table.SetScroller(scroller, true);
        var plain = new ScrollViewer { Height = 200, Content = Tall() };
        var window = Show(new StackPanel { Children = { list, scroller, plain } }, 400, 700);

        static Avalonia.Controls.Primitives.ScrollBar Vertical(Visual host)
            => host.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .First(bar => bar.Orientation == Orientation.Vertical);

        var inner = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.False(inner.AllowAutoHide);
        Assert.False(scroller.AllowAutoHide);
        Assert.Contains("table", list.Classes);

        foreach (var host in new Visual[] { list, scroller })
        {
            var bar = Vertical(host);
            Assert.True(bar.IsExpanded);
            Assert.Equal(TableMetrics.ScrollBarSize, bar.Bounds.Width, 0.5);
        }

        Assert.Equal(8d, Vertical(plain).Bounds.Width, 0.5);

        window.Close();
    }

    [AvaloniaFact]
    public void T10_StripCells_ShareAWidthPerGroup_AndResetShrinksIt()
    {
        var columns = Set("Matrix", Col("label"), new TableColumn { Key = "filters", Kind = ColumnKind.Strip });
        TableStripCell Strip(string text) => new() { Group = "f0", Children = { new TextBlock { Text = text } } };
        var small = Strip("1");
        var big = Strip("12,345");
        var keep = Row(columns, RowKind.Data, Text("label", "L"), Cell("filters", new StackPanel { Orientation = Orientation.Horizontal, Children = { small } }));
        var wide = Row(columns, RowKind.Data, Text("label", "L"), Cell("filters", new StackPanel { Orientation = Orientation.Horizontal, Children = { big } }));
        var scope = Scope(keep, wide);
        var window = Show(scope);

        Assert.Equal(big.Bounds.Width, small.Bounds.Width, 0.5);
        Assert.Contains("tc-num", small.Children[0].Classes);
        Assert.Equal(Gutter, small.Bounds.Width - small.Children[0].Bounds.Right, 0.5);

        scope.Children.Remove(wide);
        columns.Reset();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(small.Children[0].DesiredSize.Width, small.Bounds.Width, 0.5);

        window.Close();
    }

    // Ruling R5's sort header: a numeric title ends where its figures end, sorted or not.
    [AvaloniaTheory]
    [InlineData("▲")]
    [InlineData("")]
    public void ANumericSortTitle_EndsWhereItsFiguresEnd(string glyph)
    {
        var columns = Set("Sorted", Col("name"), Col("hours", ColumnKind.Number));
        Assert.True(Application.Current!.TryFindResource("TableSortTitleNumeric", out var template));
        var button = Cell("hours", new Button
        {
            Content = new ColumnViewModel("integration", TableHeads.Hours, true) { SortGlyph = glyph },
            ContentTemplate = (IDataTemplate)template!,
        });
        var figure = Text("hours", "12,345");
        var window = Show(Scope(
            Row(columns, RowKind.Header, Text("name", "Name"), button),
            Row(columns, RowKind.Data, Text("name", "M31"), figure)));

        var title = button.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == TableHeads.Hours);
        Assert.Contains("tc-head", button.Classes);
        Assert.Equal(Right(figure, window), Right(title, window), 0.5);

        window.Close();
    }

    [AvaloniaFact]
    public void ACappedTextCell_SitsOneGutterInsideItsColumn()
    {
        var columns = Set("Capped", new TableColumn { Key = "name", Width = GridLength.Star, MaxWidth = 60 });
        var cell = Text("name", "Andromeda galaxy and its companions");
        var row = Row(columns, RowKind.Data, cell);
        row.Width = 200;
        row.HorizontalAlignment = HorizontalAlignment.Left;
        var window = Show(Scope(row));

        Assert.Equal(Gutter, cell.Bounds.X, 0.5);
        Assert.Equal(60, cell.Bounds.Width, 0.5);

        window.Close();
    }

    [AvaloniaFact]
    public void TheCensus_PassesASpineTable_AndCatchesALocalInkOnADash()
    {
        var columns = Metrics();
        var list = new ListBox
        {
            ItemsSource = new[] { "2.31", "-" },
            ItemTemplate = new FuncDataTemplate<string>((value, _) =>
                Row(columns, RowKind.Data, Text("label", "Ha"), Text("hfr", value), Text("stars", "12"))),
        };
        Table.SetScroller(list, true);
        var header = Row(columns, RowKind.Header, Text("label", "Filter"), Text("hfr", TableHeads.Hfr), Text("stars", TableHeads.Stars));
        var lead = Row(columns, RowKind.LeadTotal, Text("label", "All frames"), Text("hfr", "2.40"), Text("stars", "1,204"));
        var window = Show(Scope(header, lead, list));

        TableAssert.Conventions(window);

        var dash = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "-");
        dash.Foreground = Brushes.White;
        Dispatcher.UIThread.RunJobs();
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => TableAssert.Conventions(window));

        window.Close();
    }
}
