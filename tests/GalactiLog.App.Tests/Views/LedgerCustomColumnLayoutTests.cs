using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using Avalonia.Layout;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

/// <summary>
/// Spec 12.15's tenth ledger column, at the page's own allotment rather than at a bare 1280 window
/// (TRACKING section 5, after the Phase 14C shared-size incident). The narrow ledger's measured
/// slack is 2 px and an Avalonia shared size group only ever grows, so every case here is about
/// what the column reserves rather than about what it draws.
/// </summary>
public class LedgerCustomColumnLayoutTests
{
    private const double WideLedgerWidth = 640d;
    private const double NarrowLedgerWidth = 520d;

    /// <summary>The page's real allotment: the shipped 1280x800 window less the 200 px navigation
    /// rail and the 64 px of title bar and status bar. The same two figures
    /// <c>TargetDetailViewTests</c> uses, and for the reason recorded there.</summary>
    private const double PageAllotmentWidth = 1080d;

    private const double PageAllotmentHeight = 736d;

    /// <summary>A window wide enough for the page to report <c>IsWide</c>.</summary>
    private const double WideWindowWidth = 1700d;

    /// <summary>
    /// The nine ledger column widths at the narrow ledger, measured against the tree before this
    /// task's edit and recorded in <c>task6a-report.md</c> section 0. The tenth column is this
    /// task's own and is asserted separately.
    /// </summary>
    private static readonly double[] NarrowBaseline = LedgerBaseline.Narrow;

    /// <summary>The same nine at the wide ledger, from the same reading.</summary>
    private static readonly double[] WideBaseline = LedgerBaseline.Wide;

    /// <summary>The page no longer sizes the ledger, so these cases host the part at the width the
    /// retired page gave it: 520 narrow, 640 wide plus the custom strip.</summary>
    private sealed class LedgerHost : Decorator
    {
        private readonly TargetDetailViewModel _page;

        public LedgerHost(TargetDetailViewModel page)
        {
            _page = page;
            DataContext = page;
            Child = new NightsLedgerPart { DataContext = page, HorizontalAlignment = HorizontalAlignment.Left };
            SizeChanged += (_, e) =>
            {
                page.ApplyWidth(e.NewSize.Width);
                Fit();
            };
            page.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TargetDetailViewModel.LedgerCustomExtraWidth))
                {
                    Fit();
                }
            };
        }

        private void Fit() => Child!.Width = _page.IsWide
            ? WideLedgerWidth + _page.LedgerCustomExtraWidth
            : NarrowLedgerWidth;
    }

    private static Window ShowAtThePagesAllotment(Control view)
    {
        var window = new Window
        {
            Width = PageAllotmentWidth,
            Height = PageAllotmentHeight,
            Content = view,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Grid HeaderRow(Control view) => view.Named<Grid>("LedgerHeaderRow");

    private static Grid TotalsRow(Control view)
        => Assert.IsType<Grid>(view.Named<Border>("TotalsRow").Child);

    private static Grid NightRow(Control view, int index)
    {
        var ledger = view.Named<ListBox>("NightsLedger");
        var container = ledger.ContainerFromIndex(index);
        Assert.NotNull(container);
        return container!
            .GetVisualDescendants()
            .OfType<Grid>()
            .First(grid => grid.Classes.Contains("ledger-row"));
    }

    private static double[] Widths(Grid row, int count)
        => [.. row.ColumnDefinitions.Take(count).Select(column => column.ActualWidth)];

    private static void AssertWidths(double[] expected, double[] actual, string where)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(
                Math.Abs(expected[index] - actual[index]) < 0.01d,
                $"{where}: column {index} measured {actual[index]}, not the {expected[index]} it measured before the custom column existed");
        }
    }

    // A page whose ledger draws one text custom column, hosted in the view.
    private static (LedgerPage Harness, Control View) WithOneColumn(
        CustomColumnType type = CustomColumnType.Text)
    {
        var column = CustomColumnTestFactory.Define(
            "Notes tag", type, CustomColumnScope.Session, type == CustomColumnType.Dropdown ? ["High"] : [], order: 0);
        var harness = LedgerPage.Create(
            columns: [column],
            ledgerKeys: [column.Slug],
            post: action => Dispatcher.UIThread.Post(action)).Settle();
        Dispatcher.UIThread.RunJobs();
        return (harness, new LedgerHost(harness.Page));
    }

    // A page whose ledger draws one custom column under the given name, hosted in the view.
    private static (LedgerPage Harness, Control View) WithOneNamedColumn(
        string name, CustomColumnType type = CustomColumnType.Text)
    {
        var column = CustomColumnTestFactory.Define(
            name, type, CustomColumnScope.Session, type == CustomColumnType.Dropdown ? ["High"] : [], order: 0);
        var harness = LedgerPage.Create(
            columns: [column],
            ledgerKeys: [column.Slug],
            post: action => Dispatcher.UIThread.Post(action)).Settle();
        Dispatcher.UIThread.RunJobs();
        return (harness, new LedgerHost(harness.Page));
    }

    // A page whose ledger draws the given custom columns, hosted in the view.
    private static (LedgerPage Harness, Control View) WithColumns(
        params CustomColumnType[] types)
    {
        var columns = types
            .Select((type, index) => CustomColumnTestFactory.Define(
                $"Col {index + 1}",
                type,
                CustomColumnScope.Session,
                type == CustomColumnType.Dropdown ? ["High"] : [],
                order: index))
            .ToList();

        var harness = LedgerPage.Create(
            columns: columns,
            ledgerKeys: [.. columns.Select(column => column.Slug)],
            post: action => Dispatcher.UIThread.Post(action)).Settle();
        Dispatcher.UIThread.RunJobs();
        return (harness, new LedgerHost(harness.Page));
    }

    // ---- Fix pass 1, ruling C21: the wide ledger grows rather than squeezing a built-in column --

    [AvaloniaFact]
    public void WithNoCustomColumn_TheWideLedgerIsStillExactlySixHundredAndForty()
    {
        // Ruling C21's first half, and the one that protects every reader who never switched a
        // column on. Red against a ledger that grew unconditionally, which would move the session
        // pane on a library with no custom column at all.
        using var harness = LedgerPage.Create(post: action => Dispatcher.UIThread.Post(action)).Settle();
        Dispatcher.UIThread.RunJobs();
        var view = new LedgerHost(harness.Page);
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.Page.IsWide);
        Assert.Equal(0d, harness.Page.LedgerCustomStripWidth);
        Assert.Equal(
            WideLedgerWidth,
            view.Named<Grid>("Ledger").Bounds.Width,
            3);
    }

    [AvaloniaFact]
    public void WithOneCheckBoxColumn_TheWideLedgerGrowsByExactlyThatColumn_AndTheCellIsInsideIt()
    {
        // Ruling C21's second half. Before the fix the ledger stayed at 640, the strip was laid out
        // from 645 to 681 and the cells were clipped away with the heading drawn over the session
        // pane (look/impl-p20-t6a-2-target-wide-column-on.png). Red against that: the ledger's width
        // is 640 and the cell's right edge is outside it.
        var (harness, view) = WithColumns(CustomColumnType.Boolean);
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.Page.IsWide);
        var grown = WideLedgerWidth
            + TargetDetailViewModel.CustomCellCheckWidth
            + TargetDetailViewModel.WideLedgerCustomGutter;

        Assert.Equal(TargetDetailViewModel.CustomCellCheckWidth, harness.Page.LedgerCustomStripWidth);

        var ledger = view.Named<Grid>("Ledger");
        Assert.Equal(grown, ledger.Bounds.Width, 3);

        // Every night row draws its cell, and every one of them is inside the ledger's own bounds,
        // which is what "not over the session pane" means in a measurement.
        for (var index = 0; index < harness.Page.Sessions.Count; index++)
        {
            var editor = Assert.Single(CellEditors(NightRow(view, index)));
            Assert.True(editor.IsEffectivelyVisible, $"night row {index} draws no cell");

            var right = editor.TranslatePoint(new Point(editor.Bounds.Width, 0), ledger);
            Assert.NotNull(right);
            Assert.True(
                right!.Value.X <= ledger.Bounds.Width,
                $"night row {index} draws its cell to {right.Value.X}, outside the {ledger.Bounds.Width} px ledger");
        }

        // And the heading, which is the element the blocker capture showed over the pane.
        var heading = view.Named<ItemsControl>("LedgerCustomHead");
        var headingRight = heading.TranslatePoint(new Point(heading.Bounds.Width, 0), ledger);
        Assert.NotNull(headingRight);
        Assert.True(
            headingRight!.Value.X <= ledger.Bounds.Width,
            $"the heading strip ends at {headingRight.Value.X}, outside the {ledger.Bounds.Width} px ledger");
    }

    [AvaloniaFact]
    public void ColumnsBeyondTheCap_AreDroppedLastFirst_AndTheLedgerStopsAtTheCap()
    {
        // Four text columns want 120 each, 492 with their gutters, against a 208 px cap that the
        // ledger's own 32 px gutter eats into first. One fits and the rest are dropped from the END,
        // so the set the reader sees is always a prefix of the display order. Red against a ledger
        // that grows without a cap (the cap exceeded, and the session pane squeezed out), and red
        // against a drop that takes the first column instead of the last.
        var (harness, view) = WithColumns(
            CustomColumnType.Text, CustomColumnType.Text, CustomColumnType.Text, CustomColumnType.Text);
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.Page.IsWide);
        Assert.Equal(["Col 1"], harness.Page.LedgerCustomHeadings.Select(heading => heading.Name));
        Assert.Equal(TargetDetailViewModel.CustomCellTextWidth, harness.Page.LedgerCustomStripWidth);
        Assert.All(harness.Page.Sessions, card => Assert.Single(card.LedgerCells));

        // Four check boxes reach the cap: 4 * 36 plus 3 gaps of 4 is 156, and 156 plus the ledger's
        // own 32 px gutter is 188, where a fifth would make it 228 against the 208 cap.
        var (capped, cappedView) = WithColumns(
            [.. Enumerable.Repeat(CustomColumnType.Boolean, 12)]);
        using var __ = capped;
        var cappedWindow = ShowAtThePagesAllotment(cappedView);
        cappedWindow.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            capped.Page.LedgerCustomExtraWidth <= TargetDetailViewModel.WideLedgerCustomWidthCap,
            $"the ledger took {capped.Page.LedgerCustomExtraWidth} extra, past the {TargetDetailViewModel.WideLedgerCustomWidthCap} cap");
        Assert.Equal(
            ["Col 1", "Col 2", "Col 3", "Col 4"],
            capped.Page.LedgerCustomHeadings.Select(heading => heading.Name));
    }

    [AvaloniaFact]
    public void WithNoCustomColumn_TheNarrowLedgerMeasuresExactlyAsItDoesToday()
    {
        // The single most important case in this task. A tenth column that reserves width when it
        // holds nothing takes that width off the starred Night column, whose narrow floor of 104 px
        // leaves exactly 2 px of slack with three numeric columns already at their caps. The nine
        // figures are a reading taken against the unedited tree, not a restatement of the markup.
        using var harness = LedgerPage.Create(post: action => Dispatcher.UIThread.Post(action)).Settle();
        Dispatcher.UIThread.RunJobs();
        var view = new LedgerHost(harness.Page);
        ShowAtThePagesAllotment(view);

        Assert.False(harness.Page.IsWide);
        Assert.Empty(harness.Page.LedgerCustomHeadings);

        foreach (var (row, name) in Rows(view))
        {
            AssertWidths(NarrowBaseline, Widths(row, NarrowBaseline.Length), name);
            Assert.Equal(0d, row.ColumnDefinitions[10].ActualWidth, 3);
        }
    }

    [AvaloniaFact]
    public void WithNoCustomColumn_TheWideLedgerMeasuresExactlyAsItDoesToday()
    {
        // The same reading at the wide ledger, where the custom column is in the shared size group
        // and the Filters column is drawn. Red against a tenth column that reserves a minimum, a
        // margin or a padding of its own when it holds nothing.
        using var harness = LedgerPage.Create(post: action => Dispatcher.UIThread.Post(action)).Settle();
        Dispatcher.UIThread.RunJobs();
        var view = new LedgerHost(harness.Page);
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.Page.IsWide, "the ledger was never drawn wide, so this case cannot fail");

        foreach (var (row, name) in Rows(view))
        {
            AssertWidths(WideBaseline, Widths(row, WideBaseline.Length), name);
            Assert.Equal(0d, row.ColumnDefinitions[10].ActualWidth, 3);
        }
    }

    [AvaloniaFact]
    public void BelowTheBreakpoint_TheCustomColumnLeavesTheSharedSizeGroupEntirely()
    {
        // Phase 12 blocker 1's shape, applied to the tenth column: a shared size group in Avalonia
        // only ever grows, so a wide ledger that measured a strip of cells hands that width back to
        // the narrow one and the Night column pays for it. Red against a design that only binds
        // MaxWidth, which is what the Filters column's own remark records as measured.
        var (harness, view) = WithOneColumn();
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.Page.IsWide);
        Assert.Equal(TargetDetailViewModel.LedgerCustomSharedSizeGroup, harness.Page.LedgerCustomSizeGroup);
        Assert.True(
            HeaderRow(view).ColumnDefinitions[10].ActualWidth > 0d,
            "the wide ledger reserved no custom width, so this case cannot fail");

        window.Width = PageAllotmentWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.Page.IsWide);
        Assert.Null(harness.Page.LedgerCustomSizeGroup);

        var ledger = view.Named<Grid>("Ledger");
        foreach (var (row, name) in Rows(view))
        {
            Assert.Equal(0d, row.ColumnDefinitions[10].ActualWidth, 3);

            var last = row.Children
                .Where(cell => cell.IsEffectivelyVisible && Grid.GetColumnSpan(cell) == 1)
                .OrderByDescending(Grid.GetColumn)
                .First();
            var right = last.TranslatePoint(new Point(last.Bounds.Width, 0), ledger);
            Assert.NotNull(right);
            Assert.True(
                right!.Value.X <= ledger.Bounds.Width,
                $"{name} draws its last cell to {right.Value.X}, outside the {ledger.Bounds.Width} px ledger");
        }
    }

    [AvaloniaFact]
    public void BelowTheBreakpoint_TheCustomColumnIsDroppedBeforeTheFiltersColumn()
    {
        // User choice 3, in the order it states: the custom column goes first. Red against a drop
        // order that takes Filters first, which is the same ruling inverted.
        var (harness, view) = WithOneColumn();
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();
        Assert.True(harness.Page.IsWide);
        Assert.True(view.Named<ItemsControl>("LedgerCustomHead").IsEffectivelyVisible);

        window.Width = PageAllotmentWidth;
        Dispatcher.UIThread.RunJobs();
        Assert.False(harness.Page.IsWide);

        // The narrow ledger below the breakpoint drops BOTH strips (ruling Q12 already drops the
        // filters), so what the order has to show is which column still reserves width. The custom
        // column reserves nothing and the Filters column is what the Q12 rule left, which is the
        // state a reversed drop order cannot produce.
        Assert.Equal(0d, HeaderRow(view).ColumnDefinitions[10].ActualWidth, 3);
        Assert.All(
            CellEditors(NightRow(view, 0)),
            editor => Assert.False(editor.IsEffectivelyVisible));

        // And at the wide ledger, where only one of the two can be cut if the arithmetic runs out,
        // the filter swatches are still drawn beside the custom cells rather than instead of them.
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Named<ItemsControl>("TotalsRowFilters").IsEffectivelyVisible);
        Assert.Single(CellEditors(NightRow(view, 0)));
    }

    [AvaloniaFact]
    public void TheNightDateIsStillDrawnWhole_AtTheNarrowLedgerWithACustomColumnSwitchedOn()
    {
        // The consequence the narrow ledger's 2 px of slack makes possible: a custom strip that
        // took any of the star column's share would trim the ISO date the Night column exists to
        // show, which is Phase 12 blocker 1 returning. Red against a custom column that keeps a
        // reservation while narrow.
        var (harness, view) = WithOneColumn();
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);

        // Wide first, so the group has measured a strip and has something to hand back.
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();
        window.Width = PageAllotmentWidth;
        Dispatcher.UIThread.RunJobs();
        Assert.False(harness.Page.IsWide);

        var night = NightRow(view, 0).Children.OfType<TextBlock>().Single(cell => Grid.GetColumn(cell) == 1);
        var iso = new FormattedText(
            "2025-03-20",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(night.FontFamily, night.FontStyle, night.FontWeight),
            night.FontSize,
            Brushes.White).Width;

        Assert.True(iso > 0d, "the harness measured the ISO date as nothing, so this case cannot fail");
        Assert.True(
            night.Bounds.Width >= iso,
            $"the night's date cell is {night.Bounds.Width} px wide, less than the {iso} px the date needs");
    }

    [AvaloniaFact]
    public void TheHeaderTheTotalsRowAndTheNightRows_AgreeOnEveryColumnWidth()
    {
        // TargetDetailViewTests:666's rule applied to the tenth column: three grids, one shared
        // column set. Red against a column definition added to one grid and not the other two, and
        // against a tenth column left out of the shared size group above the breakpoint.
        var (harness, view) = WithOneColumn();
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        var header = Widths(HeaderRow(view), 11);
        Assert.True(header[10] > 0d, "the wide ledger drew no custom column, so this case cannot fail");
        AssertWidths(header, Widths(TotalsRow(view), 11), "the totals row");
        AssertWidths(header, Widths(NightRow(view, 0), 11), "the first night row");
    }

    [AvaloniaFact]
    public void TheTotalsRow_CarriesNoCell()
    {
        // Amendment 2.5: nothing on the target row, which is not a night. The column definition is
        // there so the three grids stay one shape; the cell is not. Red against a template that
        // draws the strip on all three rows.
        var (harness, view) = WithOneColumn();
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.Single(CellEditors(NightRow(view, 0)));
        Assert.Empty(CellEditors(TotalsRow(view)));
        Assert.DoesNotContain(TotalsRow(view).Children, child => Grid.GetColumn(child) == 10);
    }

    [AvaloniaFact]
    public void AClickOnALedgerCell_DoesNotSelectTheNight()
    {
        // Spec 12.15: clicking an editor edits the value, it does not open the night. Counting the
        // row's own selection is not enough and counting a Click is vacuous (Task 3 review, facts
        // section 7): a pointer pressed inside the cell is captured there. What the row acts on is
        // an UNHANDLED press, so that is what this asserts, in the shipped order and over the real
        // ledger row. Red against the editor without Task 3's swallow.
        var (harness, view) = WithOneColumn(CustomColumnType.Boolean);
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        var row = NightRow(view, 1);
        var editor = Assert.Single(CellEditors(row));
        editor.Background = Brushes.Transparent;
        Dispatcher.UIThread.RunJobs();

        var unhandled = 0;
        row.AddHandler(
            InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) =>
            {
                if (!e.Handled)
                {
                    unhandled++;
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        var box = editor.GetVisualDescendants().OfType<CheckBox>().First(control => control.IsEffectivelyVisible);
        box.RaiseEvent(new PointerPressedEventArgs(
            box,
            new Pointer(0, PointerType.Mouse, isPrimary: true),
            box,
            default,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, unhandled);

        // The positive control, without which the case also passes when the watcher is attached
        // where the press never arrives, which is the failure mode that made counting a Click
        // vacuous. A press on the cell's own gutter, outside the editor, still reaches the row.
        editor.RaiseEvent(new PointerPressedEventArgs(
            editor,
            new Pointer(1, PointerType.Mouse, isPrimary: true),
            editor,
            default,
            0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, unhandled);
    }

    // ---- Phase review, data P2-3: the heading names itself for a reader who cannot see it --------

    [AvaloniaFact]
    public void TheLedgerHeading_CarriesTheColumnNameAsToolTipAndAutomationName()
    {
        // Data P2-3: the dashboard's own custom header cell already carries its name as both
        // (TargetListView.axaml's TargetCustomHeaderCell), and the ledger heading carried neither.
        // Red before the fix: ToolTip.GetTip and AutomationProperties.GetName both read null while
        // the heading's Text already read the column's name.
        var (harness, view) = WithOneColumn();
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        var heading = Assert.Single(HeadingLabels(view));
        Assert.Equal("Notes tag", heading.Text);
        Assert.Equal("Notes tag", ToolTip.GetTip(heading));
        Assert.Equal("Notes tag", AutomationProperties.GetName(heading));
    }

    [AvaloniaFact]
    public void ALongLedgerHeading_IsTrimmedOnScreen_WithItsWholeNameInTheToolTipAndAutomationName()
    {
        // The per-kind cell width is fixed (rulings C21 and C31), so a name past it has to trim
        // rather than widen the strip, and the reader who cannot read the ellipsis still needs the
        // whole name. Red before the fix, the same way as the case above: the tooltip and the
        // automation name were both null, so the trimmed heading's full name was unrecoverable.
        const string name = "Weather and seeing conditions this night"; // exactly 40 characters
        var (harness, view) = WithOneNamedColumn(name);
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        var heading = Assert.Single(HeadingLabels(view));
        Assert.Equal(name, heading.Text);

        var natural = new FormattedText(
            name,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(heading.FontFamily, heading.FontStyle, heading.FontWeight),
            heading.FontSize,
            Brushes.White).Width;

        Assert.True(
            heading.Bounds.Width < natural,
            $"the heading measured {heading.Bounds.Width} px, at least as wide as the {natural} px the whole name needs, so this case cannot prove a trim");
        Assert.Equal(name, ToolTip.GetTip(heading));
        Assert.Equal(name, AutomationProperties.GetName(heading));
    }

    [AvaloniaFact]
    public void TheView_StillDeclaresNoLocalButtonTagOrCalloutStyle()
    {
        // ControlStyleScanTest's seven needles, asserted over this view's own source: the three
        // per-kind width styles this task added are CustomCellEditor selectors and none of them is
        // a Button, a ToggleButton, a Border.tag, a Border.callout, a ScrollBar or an lvc| style.
        var source = File.ReadAllText(ViewSourcePath());

        foreach (var needle in new[]
                 {
                     "<Style Selector=\"Button",
                     "<Style Selector=\"ToggleButton",
                     "<Style Selector=\"Border.tag",
                     "<Style Selector=\"Border.callout",
                     "<Style Selector=\"ScrollBar",
                     "<ControlTheme TargetType=\"ScrollBar",
                     "<Style Selector=\"lvc|",
                 })
        {
            Assert.DoesNotContain(needle, source, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<(Grid Row, string Name)> Rows(Control view)
    {
        yield return (HeaderRow(view), "the header row");
        yield return (TotalsRow(view), "the target row");
        yield return (NightRow(view, 0), "the first night row");
    }

    // ---- phase review, target P3-6: the Tab order spec 12.15 states ------------------------------

    // The real Tab walk, which is the only thing that answers "is this reachable by keyboard":
    // a programmatic Focus() succeeds on elements Tab can never reach. The idiom is
    // MatrixTabViewTests' own, walked until it returns to where it started or runs out.
    private static List<IInputElement> TabOrder(Window window)
    {
        var stops = new List<IInputElement>();
        IInputElement? current = window;
        for (var step = 0; step < 500; step++)
        {
            current = KeyboardNavigationHandler.GetNext(current!, NavigationDirection.Next);
            if (current is null || stops.Contains(current))
            {
                break;
            }

            stops.Add(current);
        }

        return stops;
    }

    [AvaloniaFact]
    public void TheLedgerIsOneTabStop_AndItsCellsAreReachedByPointerRatherThanByTab()
    {
        // Spec 12.15 states "Tab reaches every editor in the row's own column order, after the row's
        // own content and before the row's trailing control". Nothing in this slice sets
        // TabNavigation, IsTabStop or TabIndex anywhere, so that claim rested on the default keyboard
        // model of a virtualised, always-selected ListBox, and this is the walk that measures it.
        //
        // MEASURED, and the sentence does not hold on this surface. The whole ledger contributes
        // exactly ONE stop to the page's Tab walk, the selected ListBoxItem, and a step from a cell
        // leaves the ledger rather than reaching the next cell. That is the ListBox's own model and
        // it is pre-existing rather than this phase's: the night's own check box has been in that row
        // since Phase 12 and is no more Tab reachable than a cell is. What holds is that a cell IS
        // focusable, so the pointer reaches it (AClickOnALedgerCell_DoesNotSelectTheNight) and a
        // screen reader walking the automation tree finds it under its own name. This case pins the
        // measured behaviour so the spec sentence can be corrected against something rather than
        // restated.
        //
        // Red against a ledger row that really did carry a tab chain: the single-stop assertion
        // fails. Red against an editor that is not focusable at all: the focus assertions fail, which
        // is what would make the cells unreachable by any route but a click on a check box.
        var (harness, view) = WithColumns(CustomColumnType.Boolean, CustomColumnType.Text);
        using var _ = harness;
        var window = ShowAtThePagesAllotment(view);
        window.Width = WideWindowWidth;
        Dispatcher.UIThread.RunJobs();

        var row = NightRow(view, 0);
        var editors = CellEditors(row);
        Assert.Equal(2, editors.Count);

        // Each CustomCellEditor realizes all three of its own kinds in one Panel and shows one by
        // IsVisible, so the realized control is the visible one; the hidden siblings are not
        // focusable and are in no walk either. The strip's order is the columns' order, not the
        // reverse: Col 1 is the check box.
        var firstCell = RealizedEditor(editors[0]);
        var secondCell = RealizedEditor(editors[1]);
        Assert.IsType<CheckBox>(firstCell);
        Assert.IsType<TextBox>(secondCell);

        // One stop for the whole ledger, and it is the item rather than anything inside the row.
        var ledger = view.Named<ListBox>("NightsLedger");
        var stops = TabOrder(window);
        var ledgerStops = stops
            .OfType<Visual>()
            .Where(stop => stop.GetVisualAncestors().Contains(ledger))
            .ToList();

        Assert.Single(ledgerStops);
        Assert.IsType<ListBoxItem>(ledgerStops[0]);
        Assert.DoesNotContain(firstCell, stops);
        Assert.DoesNotContain(secondCell, stops);

        // And a step out of a cell leaves the ledger rather than reaching the next cell, which is
        // the half of the spec sentence that is not true here.
        var afterTheFirst = KeyboardNavigationHandler.GetNext(firstCell, NavigationDirection.Next);
        Assert.NotSame(secondCell, afterTheFirst);

        // What the reader does have: both cells take focus, so a click edits the cell it was made on
        // and the automation tree names each of them.
        Assert.True(firstCell.Focusable);
        Assert.True(secondCell.Focusable);
        secondCell.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(secondCell.IsFocused);
    }

    private static InputElement RealizedEditor(CustomCellEditor editor)
        => editor.GetVisualDescendants()
            .OfType<InputElement>()
            .First(control => control is (CheckBox or TextBox or ComboBox) && control.IsEffectivelyVisible);

    private static IReadOnlyList<CustomCellEditor> CellEditors(Grid row)
        => [.. row.GetVisualDescendants().OfType<CustomCellEditor>()];

    private static IReadOnlyList<TextBlock> HeadingLabels(Control view)
        => [.. view.Named<ItemsControl>("LedgerCustomHead").GetVisualDescendants().OfType<TextBlock>()];

    private static string ViewSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            directory!.FullName,
            "src",
            "GalactiLog.App",
            "Views",
            "TargetDetail",
            "Parts",
            "NightsLedgerPart.axaml");
    }
}

/// <summary>
/// The ledger's nine column widths as they measured before spec 12.15's tenth column existed, at
/// the narrow ledger and at the wide one. Taken by running the page at the allotment against the
/// tree at <c>HEAD</c> and reading the header row's own column definitions; the reading and how it
/// was taken are recorded in <c>docs/superpowers/work/phase20/task6a-report.md</c> section 0.
/// </summary>
/// <remarks>A literal and not a recomputation: a case that measured today's ledger and compared it
/// with itself would pass against every defect this task can introduce.</remarks>
internal static class LedgerBaseline
{
    /// <summary>The ten pre-Phase-20 column definitions at the narrow ledger: the 20 px selection
    /// gutter, the starred Night column at its 104 px floor, the seven numeric columns, and the
    /// Filters column, which ruling Q12 collapses to nothing below the breakpoint.</summary>
    public static readonly double[] Narrow = [20d, 104d, 46d, 62d, 64d, 52d, 62d, 57d, 54d, 0d];

    /// <summary>The same ten at the wide ledger, where the Night floor is 112 and the Filters
    /// column carries the fixture's two swatches.</summary>
    public static readonly double[] Wide = [20d, 112d, 46d, 62d, 64d, 52d, 62d, 57d, 54d, 116d];
}
