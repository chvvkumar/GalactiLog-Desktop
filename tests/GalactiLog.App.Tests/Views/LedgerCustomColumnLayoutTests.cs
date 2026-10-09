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
using GalactiLog.App.Controls.Table;
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
/// Spec 12.15's custom columns on the Nights list, at the page's own allotment rather than at a
/// bare 1280 window (TRACKING section 5). The list is drawn at its own width, every switched-on
/// column shown; the divider decides how much of it the pane covers.
/// </summary>
public class LedgerCustomColumnLayoutTests
{
    /// <summary>The page's real allotment: the shipped 1280x800 window less the 200 px navigation
    /// rail and the 64 px of title bar and status bar. The same two figures
    /// <c>TargetDetailViewTests</c> uses, and for the reason recorded there.</summary>
    private const double PageAllotmentWidth = 1080d;

    private const double PageAllotmentHeight = 736d;

    // The part at its own width, as the sidebar column draws it fully open.
    private static NightsLedgerPart LedgerHost(TargetDetailViewModel page)
        => new() { DataContext = page, HorizontalAlignment = HorizontalAlignment.Left };

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

    private static TableRow HeaderRow(Control view) => view.Named<TableRow>("LedgerHeaderRow");

    private static TableRow NightRow(Control view, int index) => (TableRow)TargetPartHost.LedgerRowAt(view, index);

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
        return (harness, LedgerHost(harness.Page));
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
        return (harness, LedgerHost(harness.Page));
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
        return (harness, LedgerHost(harness.Page));
    }

    [AvaloniaFact]
    public void TheHeaderAndTheNightRows_AgreeOnEveryColumnAndEveryCustomCellEdge()
    {
        // One column set, one shared size scope: the header strip's headings end where every
        // night's cells end. Red against a heading measured at a width its cells do not take.
        var (harness, view) = WithColumns(CustomColumnType.Boolean, CustomColumnType.Text, CustomColumnType.Dropdown);
        using var _ = harness;
        ShowAtThePagesAllotment(view);

        var header = HeaderRow(view);
        var headings = Edges(view.Named<ItemsControl>("LedgerCustomHead"), view);
        Assert.Equal(3, headings.Count);
        for (var index = 0; index < harness.Page.Sessions.Count; index++)
        {
            var row = NightRow(view, index);
            Assert.Equal(
                header.ColumnDefinitions.Select(column => column.ActualWidth),
                row.ColumnDefinitions.Select(column => column.ActualWidth));
            var cells = Edges(row.Children.OfType<ItemsControl>().Single(), view);
            Assert.Equal(headings.Count, cells.Count);
            for (var cell = 0; cell < cells.Count; cell++)
            {
                Assert.Equal(headings[cell], cells[cell], 0.5);
            }
        }
    }

    // The right edge of every item in a custom strip, in the view's coordinates.
    private static List<double> Edges(ItemsControl strip, Visual view)
        => [.. strip.GetRealizedContainers().Select(item => item.TranslatePoint(new Point(item.Bounds.Width, 0), view)!.Value.X)];

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
        ShowAtThePagesAllotment(view);

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
        ShowAtThePagesAllotment(view);

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
        ShowAtThePagesAllotment(view);

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
        // ControlStyleScanTest's seven needles, asserted over this view's own source: none of its
        // styles is a Button, a ToggleButton, a Border.tag, a Border.callout, a ScrollBar or an
        // lvc| style.
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
