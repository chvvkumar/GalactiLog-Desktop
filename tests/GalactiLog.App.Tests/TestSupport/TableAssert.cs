using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Media;
using Avalonia.VisualTree;
using GalactiLog.App.Controls.Table;
using Xunit;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The table convention census (spine-spec 6.2). Every migrated view's test calls
/// <see cref="Conventions"/> on its laid-out view, at 1280x800 and at the x-large text size, so a
/// table that drifts from spec.md's convention fails where it is drawn rather than in a review.
/// </summary>
public static class TableAssert
{
    private const double Tolerance = 0.5;

    public static void Conventions(Control root)
    {
        var failures = new List<string>();
        var cells = root.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToList();
        Assert.True(
            cells.Any(c => c.Classes.Contains("tc-num") || c.Classes.Contains("tc-text")),
            "No table cell under this root, so the census proves nothing.");

        var rows = cells.OfType<TableRow>().ToList();
        var edges = new Dictionary<(TableColumns, string), List<double>>();
        foreach (var row in rows.Where(r => r.Columns is not null))
        {
            var columns = row.Columns!;
            foreach (var cell in row.Children.Where(c => c.IsEffectivelyVisible))
            {
                var index = Grid.GetColumn(cell);
                var left = row.ColumnDefinitions.Take(index).Sum(d => d.ActualWidth);
                var right = left + row.ColumnDefinitions[index].ActualWidth;
                var key = TableRow.GetCol(cell)!;
                var name = $"{columns.Id}.{key} ({row.Kind})";
                GutterOf(cell, left, right, name, failures);

                if (cell.Classes.Contains("tc-num"))
                {
                    var edge = cell.TranslatePoint(new Point(cell.Bounds.Width, 0), root)!.Value.X;
                    (edges.TryGetValue((columns, key), out var list) ? list : edges[(columns, key)] = []).Add(edge);
                }

                var column = columns[key];
                if (cell.Classes.Contains("tc-text")
                    && (double.IsFinite(column.MaxWidth) || column.Width.IsStar)
                    && ToolTip.GetTip(cell) is null)
                {
                    failures.Add($"{name}: a trimmable text cell has no tooltip");
                }
            }

            if (row.Kind == RowKind.Header)
            {
                foreach (var block in row.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible))
                {
                    if (!block.Classes.Contains("t-label"))
                    {
                        failures.Add($"{columns.Id} header '{block.Text}': no t-label");
                    }

                    if (IsLocal(block, TextBlock.FontWeightProperty) || IsLocal(block, TextBlock.ForegroundProperty))
                    {
                        failures.Add($"{columns.Id} header '{block.Text}': a local FontWeight or Foreground");
                    }
                }
            }
        }

        foreach (var strip in cells.OfType<TableStripCell>())
        {
            foreach (var cell in strip.Children.Where(c => c.IsEffectivelyVisible))
            {
                GutterOf(cell, 0, strip.Bounds.Width, $"strip {strip.Group}", failures);
            }
        }

        foreach (var ((columns, key), list) in edges)
        {
            if (list.Max() - list.Min() > Tolerance)
            {
                failures.Add($"{columns.Id}.{key}: figures end at {list.Min()} to {list.Max()}, not one edge");
            }
        }

        var faint = Application.Current!.TryFindResource("ColorTextTertiary", out var value) ? ((ISolidColorBrush)value!).Color : default;
        foreach (var block in cells.OfType<TextBlock>().Where(b => b.Classes.Contains("tc-num") || b.Classes.Contains("tc-text")))
        {
            var isNumber = block.Classes.Contains("tc-num");
            if (isNumber && block.TextLayout.TextLines.Any(line => line.HasCollapsed))
            {
                failures.Add($"figure '{block.Text}' is trimmed");
            }

            if (isNumber && !block.Classes.Contains("tc-head") && string.IsNullOrEmpty(block.Text))
            {
                failures.Add("a figure cell is blank; a missing value is MetricText.Missing");
            }

            if (block.Text == "-" && (block.Foreground as ISolidColorBrush)?.Color != faint)
            {
                failures.Add("a '-' cell is not in ColorTextTertiary (a local Foreground outranks the faint dash)");
            }
        }

        foreach (var host in cells.Prepend(root).Where(c => c.Classes.Contains("table")))
        {
            var scrollers = host is ScrollViewer own
                ? [own]
                : host.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.TemplatedParent == host).ToList();
            if (scrollers.Any(s => s.AllowAutoHide))
            {
                failures.Add($"{host.GetType().Name}.table: its scroller auto-hides; set t:Table.Scroller");
            }
        }

        Assert.True(failures.Count == 0, "Table conventions:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    // A text cell starts one gutter inside its column; a figure ends one gutter inside it.
    private static void GutterOf(Control cell, double left, double right, string name, List<string> failures)
    {
        if (cell.Classes.Contains("tc-text") && Math.Abs(cell.Bounds.X - left - TableMetrics.Gutter) > Tolerance)
        {
            failures.Add($"{name}: text starts {cell.Bounds.X - left} inside its column, not {TableMetrics.Gutter}");
        }
        else if (cell.Classes.Contains("tc-num") && Math.Abs(right - cell.Bounds.Right - TableMetrics.Gutter) > Tolerance)
        {
            failures.Add($"{name}: figure ends {right - cell.Bounds.Right} inside its column, not {TableMetrics.Gutter}");
        }
    }

    private static bool IsLocal(AvaloniaObject target, AvaloniaProperty property)
        => target.GetDiagnostic(property).Priority == BindingPriority.LocalValue;
}
