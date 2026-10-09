using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.Views.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Spec 12.15's dashboard cells, view side: the one custom grid column, where it sits, what it
// measures, what it costs the width rule and that a press on a cell is not a press on the row.
// The pins this file must not move are TargetListViewTests' own; they are listed in the Phase 20
// collision map and are left untouched here.
public class TargetListCustomColumnTests
{
    // The page's own allotment, not the whole window, for the reason TargetListViewTests records:
    // on the shipped 1280 by 800 dashboard the list runs about 720 wide.
    private const double PageAllotment = 720d;

    // What the launched application actually hands the list at 1280 by 800 with the filter panel
    // open, measured on the running window: the rail, the panel and the page gutters take the rest.
    private const double ShippedAllotment = 732d;

    // The same window with the filter panel folded to its 48 pixel strip, measured on the running
    // application: the list is handed the panel's width back.
    private const double FoldedAllotment = 1032d;

    // What the list is handed at the 1024 by 700 floor, where the panel already renders as its
    // strip, measured on the running application.
    private const double NarrowAllotment = 736d;

    // The same page at about 1900, where the list's allotment is roughly 1330 and nothing gives
    // way at all.
    private const double WidePageAllotment = 1330d;

    // The one allotment band where the custom strip changes the width rule's answer: at or above
    // the default fit width, and below it plus what two custom cells take. Below the band Equipment
    // is suppressed whatever the strip costs; above it, nothing is.
    private const double BandAllotment = 1200d;

    // A list with room for every built-in column and both custom columns at once, about a 2000 wide
    // window. Nothing gives way here at all, which is where the cases that are about what the strip
    // draws rather than about the width rule lay their page out.
    private const double SpaciousAllotment = 1430d;

    private const double SpaciousWindow = 2000d;

    private const double ColumnSettle = 2.5d;

    private static readonly Guid TargetId = Guid.Parse("10000000-0000-0000-0000-000000000000");
    private static readonly DateOnly Night = new(2026, 3, 14);

    [AvaloniaFact]
    public void TheHeaderAndTheRows_AgreeOnTheCustomStripWidthAfterLayout()
    {
        // A failure is a strip declared in one grid and not the other, or two strips whose cells
        // are sized by two different rules: the header would then label one column over another
        // column's cells, which is the defect the ledger's own equal-width case exists for.
        var list = Populated(TwoColumns());
        var view = new TargetListView { DataContext = list };
        Show(view, SpaciousAllotment, SpaciousWindow);

        var header = Strip(view, "CustomHeaderStrip");
        var row = Strip(view, "CustomCellStrip");

        Assert.Equal(2, header.GetVisualDescendants().OfType<TextBlock>().Count());
        Assert.Equal(2, row.GetVisualDescendants().OfType<CustomCellEditor>().Count());
        Assert.True(header.Bounds.Width > 0d, "The header strip measured nothing.");
        Assert.True(
            Math.Abs(header.Bounds.Width - row.Bounds.Width) < ColumnSettle,
            $"Header strip {header.Bounds.Width}, row strip {row.Bounds.Width}.");
        Assert.True(
            Math.Abs(LeftEdge(header, view) - LeftEdge(row, view)) < ColumnSettle,
            $"Header strip at {LeftEdge(header, view)}, row strip at {LeftEdge(row, view)}.");
    }

    [AvaloniaFact]
    public void TheCustomStripSitsBetweenLastSessionAndTheSessionsControl()
    {
        // A failure is an insert at the wrong index, which moves the two pins that read column 4
        // (Equipment) and puts the labels over the wrong cells. Eight columns now: six built-in,
        // the one custom strip, the sessions control.
        var list = Populated(TwoColumns());
        var view = new TargetListView { DataContext = list };
        Show(view, SpaciousAllotment, SpaciousWindow);

        var headerGrid = GridNamed(view, "TargetListHeaderRow");
        var rowGrid = GridNamed(view, "TargetRowGrid");

        Assert.Equal(8, headerGrid.ColumnDefinitions.Count);
        Assert.Equal(8, rowGrid.ColumnDefinitions.Count);

        Assert.Equal(6, Grid.GetColumn(Strip(view, "CustomHeaderStrip")));
        Assert.Equal(6, Grid.GetColumn(Strip(view, "CustomCellStrip")));
        Assert.Equal(5, Grid.GetColumn(Named<TextBlock>(view, "LastSessionCell")));
        Assert.Equal(7, Grid.GetColumn(Named<Button>(view, "SessionsToggle")));

        // And on screen, not only in the declaration.
        Assert.True(LeftEdge(Strip(view, "CustomCellStrip"), view) > LeftEdge(Named<TextBlock>(view, "LastSessionCell"), view));
        Assert.True(LeftEdge(Named<Button>(view, "SessionsToggle"), view) > LeftEdge(Strip(view, "CustomCellStrip"), view));
    }

    [AvaloniaFact]
    public void TheCustomHeaderCell_CarriesNoSortButton()
    {
        // A custom column is not sortable in this phase, and a button here is what would take the
        // header's own button count past six. The label is still drawn.
        var list = Populated(TwoColumns());
        var view = new TargetListView { DataContext = list };
        Show(view, SpaciousAllotment, SpaciousWindow);

        var header = Strip(view, "CustomHeaderStrip");

        Assert.Empty(header.GetVisualDescendants().OfType<Button>());
        Assert.Equal(
            ["Done", "Priority"],
            header.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
    }

    [AvaloniaFact]
    public void ACheckBoxColumnsHeading_IsDrawnWholeAndNamesItselfOnHover()
    {
        // The defect: the header entry took the boolean kind's cell width, 36, and the cell gutter
        // left 20 pixels for the label, so a check box column's heading was drawn as an ellipsis at
        // every width. Two boolean columns beside each other then read as two identical dots and
        // nothing on screen said which box was which.
        var list = Populated(TwoColumns());
        var view = new TargetListView { DataContext = list };
        Show(view, WidePageAllotment, 1900d);

        var heading = Heading(view, "Done");

        Assert.False(IsTrimmed(heading), "The heading is drawn as an ellipsis.");
        Assert.Equal("Done", ToolTip.GetTip(heading));
        Assert.Equal("Done", AutomationProperties.GetName(heading));

        // And one figure still drives both strips: a heading that grew and a cell that did not
        // would put every later label over the wrong column.
        AssertEntriesLineUp(view);
    }

    [AvaloniaFact]
    public void ALongColumnName_IsTrimmedAtTheCap_AndItsFullNameIsOnHover()
    {
        // The other end of the same rule: a heading may grow only as far as the dropdown cell's
        // width, so one long column name cannot take the row, and what the trim hides is readable
        // on hover and by a screen reader.
        const string longName = "Processed and stacked and archived twice";
        var list = Populated([CustomColumnTestFactory.Boolean(longName)]);
        var view = new TargetListView { DataContext = list };
        Show(view, WidePageAllotment, 1900d);

        var heading = Heading(view, longName);

        Assert.Equal(TargetListView.DropdownCellWidth, heading.Bounds.Width);
        Assert.True(IsTrimmed(heading), "The heading grew past the cap.");
        Assert.Equal(longName, ToolTip.GetTip(heading));
        Assert.Equal(longName, AutomationProperties.GetName(heading));
        AssertEntriesLineUp(view);
    }

    [AvaloniaFact]
    public void WithCustomColumnsOn_TheBuiltInColumnsMeasureWhatTheyDoWithNoneOn()
    {
        // Ruling C24's principle: a custom column gives way first and no built-in column pays for
        // one. The defect: the three capped columns gave up a third of the strip's width whenever a
        // strip was drawn, so the built-in headings trimmed harder at a wider window than at a
        // narrower one and the Last Session value, which carries no trimming at all, hard-clipped
        // mid-figure and read as a wrong date.
        foreach (var allotment in new[] { ShippedAllotment, NarrowAllotment })
        {
            var noneList = Populated(CheckBoxAndText(), visible: false);
            var none = new TargetListView { DataContext = noneList };
            Show(none, allotment);

            var both = new TargetListView { DataContext = Populated(CheckBoxAndText()) };
            Show(both, allotment);

            Assert.Equal(none.DataCellMaxWidth, both.DataCellMaxWidth);
            Assert.Equal(none.IsEquipmentShown, both.IsEquipmentShown);

            // Every built-in column but the star, which is what the strip is allowed to take its
            // room from and which never goes below its own floor.
            for (var index = 1; index <= 5; index++)
            {
                Assert.True(
                    Math.Abs(ColumnWidth(none, "TargetListHeaderRow", index)
                        - ColumnWidth(both, "TargetListHeaderRow", index)) < ColumnSettle,
                    $"At {allotment}, built-in column {index} measures "
                        + $"{ColumnWidth(both, "TargetListHeaderRow", index)} with custom columns on and "
                        + $"{ColumnWidth(none, "TargetListHeaderRow", index)} with none on.");
            }

            var value = Named<TextBlock>(both, "LastSessionCell").Bounds.Width;
            var baseline = Named<TextBlock>(none, "LastSessionCell").Bounds.Width;
            Assert.True(
                Math.Abs(value - baseline) < ColumnSettle,
                $"At {allotment}, the Last Session value measures {value} with custom columns on "
                    + $"and {baseline} with none on.");
            Assert.True(both.NameCellMaxWidth >= TargetListView.NameFloor, "The star column went below its floor.");
        }
    }

    [AvaloniaFact]
    public void ARowWithTwoCustomColumns_StillDrawsWholeAtTheShippedWindow()
    {
        // The defect: BudgetFor's default arm answers zero for a custom slug, so a row carrying two
        // visible custom columns reports the same fit width as a row carrying none, and a strip
        // that costs the width rule nothing is a strip drawn past the list's right edge.
        //
        // The band is where that shows: at 1200 the six default columns fit (1128) and the same six
        // plus the strip do not. What gives way there is the strip, not Equipment, because a custom
        // column is the first thing to give way and no built-in column pays for one.
        var list = Populated(TwoColumns());
        var view = new TargetListView { DataContext = list };
        Show(view, BandAllotment, 1760d);

        var lookup = CustomWidth(list, view);
        var custom = list.CustomColumns.Sum(column => lookup(column.Slug));
        Assert.Equal(
            TargetListView.DefaultFitWidth + custom,
            TargetListView.FitWidthFor(list.Columns, lookup));
        Assert.True(BandAllotment >= TargetListView.DefaultFitWidth, "The band is below the default fit width.");
        Assert.True(BandAllotment < TargetListView.DefaultFitWidth + custom, "The band is above the widened fit width.");

        Assert.True(
            list.DrawnCustomColumns.Count < list.CustomColumns.Count,
            "Nothing gave way in the band.");
        Assert.True(view.IsEquipmentShown, "Equipment paid for the strip.");
        AssertDrawsWhole(view);
        AssertStripsAgree(view, list);

        // And at the window the application ships in, where Equipment has stepped aside on its own
        // account long before any custom column existed.
        var shippedList = Populated(TwoColumns());
        var shipped = new TargetListView { DataContext = shippedList };
        Show(shipped, PageAllotment);
        Assert.False(shipped.IsEquipmentShown);
        Assert.Empty(shippedList.DrawnCustomColumns);
        AssertDrawsWhole(shipped);
    }

    [AvaloniaFact]
    public void ARowWhoseColumnsAreSwitchedOnAfterLayout_StillDrawsWhole()
    {
        // Collision map trap 2: a seam the view assigns is exercised in the order the shipped view
        // assigns it. The reader switches a custom column on through the gear on a page that has
        // already laid out, and an Avalonia shared size group only ever grows, so a strip that
        // appears after the first layout can leave the row wider than the list even though a fresh
        // layout of the same state fits.
        // At the allotment the folded panel leaves, where both columns are drawn: the shipped
        // window with the panel open has room for neither, so nothing there would be switched on
        // for the size groups to re-measure.
        var definitions = CheckBoxAndText();
        var list = Populated(definitions, visible: false);
        var view = new TargetListView { DataContext = list };
        Show(view, FoldedAllotment);

        Assert.Empty(view.GetVisualDescendants().OfType<CustomCellEditor>());

        foreach (var definition in definitions)
        {
            list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == definition.Slug));
        }

        Dispatcher.UIThread.RunJobs();
        view.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();

        // The live path must reach the same layout the fresh path does. Before the size groups
        // carried a generation the caps tightened and the groups kept the 120 they had measured,
        // so the row stayed wider than the list although a fresh view of the same state fitted.
        var freshList = Populated(definitions);
        var fresh = new TargetListView { DataContext = freshList };
        Show(fresh, FoldedAllotment);

        Assert.Equal(freshList.DrawnCustomColumns.Count, list.DrawnCustomColumns.Count);
        Assert.NotEmpty(view.GetVisualDescendants().OfType<CustomCellEditor>());
        AssertDrawsWhole(view);
        AssertStripsAgree(view, list);
    }

    [AvaloniaFact]
    public void AtTheShippedWindow_TheExpandButtonIsInsideTheList_AndACustomColumnGivesWayFirst()
    {
        // Ruling C24. At the width the shipped 1280 by 800 page gives the list, a check box column
        // and a text column together need more than the row has, so the trailing custom column
        // steps aside, last first, until the row fits. A failure is the Expand button drawn past
        // the list, which is what the launched look caught before this rule existed.
        var list = Populated(CheckBoxAndText());
        var view = new TargetListView { DataContext = list };
        Show(view, ShippedAllotment);

        Assert.Equal(2, list.CustomColumns.Count);
        Assert.True(list.DrawnCustomColumns.Count < 2, "Nothing gave way at the shipped window.");
        AssertDrawsWhole(view);
        AssertStripsAgree(view, list);
    }

    [AvaloniaFact]
    public void AtTheNarrowWindow_TheSameBoundHolds()
    {
        // The 1024 by 700 floor, where the filter panel renders as its strip. Same rule, same
        // bound: whatever is drawn, the Expand button is inside the list.
        var list = Populated(CheckBoxAndText());
        var view = new TargetListView { DataContext = list };
        Show(view, NarrowAllotment);

        AssertDrawsWhole(view);
        AssertStripsAgree(view, list);
    }

    [AvaloniaFact]
    public void AtAWideWindow_BothCustomColumnsAreDrawn()
    {
        // Nothing gives way where there is room: the rule is a width rule, not a cap on how many
        // custom columns a reader may switch on.
        var list = Populated(CheckBoxAndText());
        var view = new TargetListView { DataContext = list };
        Show(view, SpaciousAllotment, SpaciousWindow);

        Assert.Equal(2, list.DrawnCustomColumns.Count);
        Assert.True(view.IsEquipmentShown);
        AssertDrawsWhole(view);
        AssertStripsAgree(view, list);
    }

    [AvaloniaFact]
    public void FoldingTheFilterPanel_BringsADroppedColumnBack()
    {
        // The width rule runs on every Bounds change, so the column comes back with no reload and
        // no re-read: the cell that returns is the cell it was, still holding what it held. A
        // failure is a drop that is remembered rather than recomputed.
        var list = Populated(CheckBoxAndText());
        var view = new TargetListView { DataContext = list };
        var window = Show(view, ShippedAllotment);

        Assert.True(list.DrawnCustomColumns.Count < 2, "Nothing gave way at the shipped window.");

        // Folding the panel is what the page does to the list's own width; the host is the panel.
        Resize(window, view, FoldedAllotment);

        Assert.Equal(2, list.DrawnCustomColumns.Count);
        var cellWhileFolded = list.Rows[0].CustomCells[0];
        AssertDrawsWhole(view);
        AssertStripsAgree(view, list);

        // Opening the panel again drops it, and folding a second time brings back the cell itself,
        // not a rebuilt one: nothing is discarded by a drop, so a cell that returns still holds
        // whatever was typed into it.
        Resize(window, view, ShippedAllotment);
        Assert.True(list.DrawnCustomColumns.Count < 2, "The column did not give way again.");

        Resize(window, view, FoldedAllotment);
        Assert.Equal(2, list.DrawnCustomColumns.Count);
        Assert.Same(cellWhileFolded, list.Rows[0].CustomCells[0]);
        AssertDrawsWhole(view);
    }

    private static void Resize(Window window, TargetListView view, double allotment)
    {
        ((Border)window.Content!).Width = allotment;
        Dispatcher.UIThread.RunJobs();
        view.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ALibraryWithNoCustomColumn_LaysOutExactlyAsItDoesToday()
    {
        // A failure is a strip that reserves width when it holds nothing: a shared size group in
        // Avalonia only ever grows, so a strip that measured once would keep its gap in the header
        // and in every row.
        //
        // The subject is a library whose columns exist and are all switched off, not one with no
        // definition at all: a list with no definition holds exactly the six built-in entries, so
        // the custom arm of the taken loop never runs in either side of the comparison and the
        // six-column half of this case could not tell the two apart.
        var withColumns = Populated(CheckBoxAndText(), visible: false);
        var view = new TargetListView { DataContext = withColumns };
        Show(view, PageAllotment);

        var plain = new TargetListView { DataContext = Plain() };
        Show(plain, PageAllotment);

        Assert.Equal(
            TargetListView.DefaultFitWidth,
            TargetListView.FitWidthFor(withColumns.Columns, CustomWidth(withColumns, view)));
        Assert.Equal(0d, ColumnWidth(view, "TargetListHeaderRow", 6));
        Assert.Equal(0d, InnerColumnWidth(GridNamed(view, "TargetRowGrid"), 6));
        Assert.Empty(view.GetVisualDescendants().OfType<CustomCellEditor>());

        for (var index = 0; index < 6; index++)
        {
            Assert.True(
                Math.Abs(ColumnWidth(view, "TargetListHeaderRow", index) - ColumnWidth(plain, "TargetListHeaderRow", index))
                    < ColumnSettle,
                $"Column {index}: {ColumnWidth(view, "TargetListHeaderRow", index)} with the strip, "
                    + $"{ColumnWidth(plain, "TargetListHeaderRow", index)} without it.");
        }
    }

    [AvaloniaFact]
    public void AClickOnACustomCell_DoesNotOpenTheTarget()
    {
        // Driven on the real row in the shipped order, which is what proves Task 3's swallow is
        // wired here rather than only tested in isolation. Counting the row button's Click is
        // vacuous: a press inside the cell is captured there and never completes a click on the
        // button whatever happens. What the row acts on is an unhandled press, so that is what is
        // counted, and the outcome the case is named for is asserted beside it in both directions.
        var list = Populated(TwoColumns());
        var view = new TargetListView { DataContext = list };
        var window = Show(view, SpaciousAllotment, SpaciousWindow);

        var opened = 0;
        list.TargetOpened += (_, _) => opened++;

        // The strip itself is the watcher's host: a press that the editor swallowed still bubbles
        // to it, carrying Handled, and one that nothing swallowed arrives with Handled false.
        var unhandled = 0;
        var strip = Strip(view, "CustomCellStrip");
        strip.AddHandler(
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

        var editor = view.GetVisualDescendants().OfType<CustomCellEditor>().First();
        Press(window, At(window, editor, 0.5d, 0.5d));

        Assert.Equal(0, unhandled);
        Assert.Equal(0, opened);

        // The falsifying half, on the same row in the same order: a press on the Name cell, whose
        // runs paint no background of their own, reaches the row's own click target behind the
        // cells and opens the target. Without it this case would pass against a row that never
        // opens at all.
        Press(window, At(window, Named<Grid>(view, "NameCell"), 0.5d, 0.5d));

        Assert.Equal(1, opened);
    }

    [AvaloniaFact]
    public void TheView_DeclaresNoStyleAndNoNonZeroCornerRadius()
    {
        // The Ledger vocabulary cannot erode at site N+1 (DESIGN.md section 4, the Phase 14C
        // amendment). A failure is a <Style> element or a radiused container arriving with the
        // custom strip.
        var source = File.ReadAllText(SourceScan
            .EnumerateMarkupFiles("GalactiLog.App")
            .Single(file => Path.GetFileName(file) == "TargetListView.axaml"));

        Assert.DoesNotContain("<Style ", source);
        Assert.DoesNotContain("<Style>", source);
        Assert.DoesNotContain("Mode=OneTime", source);
        foreach (var line in source.Split('\n').Where(line => line.Contains("CornerRadius=", StringComparison.Ordinal)))
        {
            Assert.Contains("CornerRadius=\"0", line);
        }
    }

    // ---- fixture ----

    // The pair the launched look uses: the widest custom set the dashboard can be asked for, a
    // check box and a text box.
    private static IReadOnlyList<CustomColumnDefinition> CheckBoxAndText() =>
    [
        CustomColumnTestFactory.Boolean("Processed"),
        CustomColumnTestFactory.Text("Notes", order: 1),
    ];

    private static IReadOnlyList<CustomColumnDefinition> TwoColumns() =>
    [
        CustomColumnTestFactory.Boolean("Done"),
        CustomColumnTestFactory.Define(
            "Priority", CustomColumnType.Dropdown, CustomColumnScope.Target, ["High", "Low"], order: 1),
    ];

    // The view's own lookup, read back at the view's own type size and family: a heading is
    // measured, so the figure cannot be spelled as a constant here.
    private static Func<string, double> CustomWidth(TargetListViewModel list, TargetListView view)
        => key => list.CustomColumns
            .Where(column => string.Equals(column.Slug, key, StringComparison.Ordinal))
            .Select(column => TargetListView.CustomCellWidthFor(
                column.Name, column.Type, view.FontSize, view.FontFamily))
            .FirstOrDefault();

    private static TargetListViewModel Plain()
    {
        var display = new DisplaySettings();
        var list = new TargetListViewModel(display, () => display, value => display = value, 50);
        list.Load(Page());
        return list;
    }

    private static TargetListViewModel Populated(
        IReadOnlyList<CustomColumnDefinition> definitions, bool visible = true)
    {
        var display = new DisplaySettings();
        display.Columns[DisplaySettings.DashboardHiddenTableId] = visible
            ? []
            : [.. definitions.Select(column => column.Slug)];

        var stored = new DisplaySettings();
        var list = new TargetListViewModel(
            display,
            new DisplayColumnWriter(() => stored, value => stored = value, NullLogger.Instance),
            50,
            writeDefaultPageSize: null,
            loadCustomColumns: () => definitions,
            loadTargetValues: _ => [],
            loadValuesForTarget: _ => [],
            writeValue: (_, _, _) => CustomColumnTestFactory.Written,
            post: action => action(),
            cellDelay: CustomColumnTestFactory.Instant);

        var page = Page();
        list.Load(page, list.ReadCustomColumns(page));
        return list;
    }

    // The row the width rule is actually asked to draw, not an empty one. The Palette column is
    // uncapped by ruling Q10, so an empty palette measures nothing and every bound below would be
    // asserted against a row narrower than the one the application draws; three badges is what the
    // shipped fixture and the launched look both carry, and it is the term that put the row past
    // the list's edge. The designation is the widest the caps were measured on and the name is a
    // long one, so the star column is at its floor here as it is on a real library.
    private static TargetListingPage Page() => new(
        [
            new TargetRow(
                GroupKey: TargetId.ToString(),
                TargetId: TargetId,
                Name: "NGC 7000 - the North America complex",
                CommonName: "North America Nebula",
                CatalogId: "PGC 123456.789",
                ObjectType: "HII",
                ObjectCategory: "Nebula",
                IntegrationSeconds: 44_640d,
                FrameCount: 148,
                SessionCount: 1,
                FirstSession: Night,
                LastSession: Night,
                Palette:
                [
                    new FilterBadge("Ha", "#FF0000", 5, 1_500d),
                    new FilterBadge("L", "#FFFFFF", 900, 27_000d),
                    new FilterBadge("B", "not-a-colour", 40, 12_000d),
                ],
                Equipment: ["Esprit 100 / ASI6200MM Pro"],
                Aliases: [],
                Sessions: [new SessionSummary(Night, 148, 44_640d)]),
        ],
        1,
        44_640d,
        148,
        1,
        50);

    // ---- journeys P2-5: the "Custom" heading in the column gear ---------------------------------

    // Every text the open flyout draws, in the order it draws it. The flyout lives in its own popup
    // host rather than in the button's own visual tree, which is also why no launched look can show
    // it in PrintWindow's pixels; the same idiom HelpButtonTests uses to read a flyout's content.
    private static List<string> FlyoutTexts(Button gear)
    {
        var host = ((IPopupHostProvider)gear.Flyout!).PopupHost;
        Assert.NotNull(host);
        return
        [
            .. ((Visual)host!).GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible)
                .Select(block => block.Text ?? ""),
        ];
    }

    private static List<string> FlyoutBoxes(Button gear)
    {
        var host = ((IPopupHostProvider)gear.Flyout!).PopupHost;
        Assert.NotNull(host);
        return
        [
            .. ((Visual)host!).GetVisualDescendants()
                .OfType<CheckBox>()
                .Select(box => box.Content as string ?? ""),
        ];
    }

    // Spec 12.15: a picker lists custom columns under a "Custom" heading below the built-in ones.
    // The gear flyout bound the flat Columns list, so a custom column was one more unlabelled entry
    // at the end of the built-in six.
    //
    // Red against that binding: the two headings are absent from the flyout and "Processed" follows
    // "Last Session" with nothing between them.
    [AvaloniaFact]
    public void TheGearFlyout_ListsACustomColumnUnderACustomHeading()
    {
        var list = Populated([CustomColumnTestFactory.Boolean("Processed")], visible: false);
        var view = new TargetListView { DataContext = list };
        Show(view, SpaciousAllotment, SpaciousWindow);

        var gear = Named<Button>(view, "ColumnGearButton");
        gear.Flyout!.ShowAt(gear);
        Dispatcher.UIThread.RunJobs();

        var texts = FlyoutTexts(gear);
        Assert.Contains("Built-in", texts);
        Assert.Contains("Custom", texts);
        Assert.True(texts.IndexOf("Built-in") < texts.IndexOf("Custom"));

        // Both groups list their own columns, and the custom one is still one entry among the six
        // built-in ones rather than a second column state.
        var boxes = FlyoutBoxes(gear);
        Assert.Equal(
            ["Name", "Designation", "Palette", "Integration", "Equipment", "Last Session", "Processed"],
            boxes);

        gear.Flyout.Hide();
        Dispatcher.UIThread.RunJobs();
    }

    // The other half of the rule: the heading is drawn only when a custom column exists, so a
    // library with none sees exactly the flyout it saw before Phase 20.
    //
    // Red against a view that draws the heading unconditionally: "Built-in" appears over a list that
    // has nothing to be distinguished from.
    [AvaloniaFact]
    public void TheGearFlyout_DrawsNoHeadingWithNoCustomColumn()
    {
        var list = Plain();
        var view = new TargetListView { DataContext = list };
        Show(view, SpaciousAllotment, SpaciousWindow);

        var gear = Named<Button>(view, "ColumnGearButton");
        gear.Flyout!.ShowAt(gear);
        Dispatcher.UIThread.RunJobs();

        var texts = FlyoutTexts(gear);
        Assert.DoesNotContain("Built-in", texts);
        Assert.DoesNotContain("Custom", texts);
        Assert.Equal(
            ["Name", "Designation", "Palette", "Integration", "Equipment", "Last Session"],
            FlyoutBoxes(gear));

        gear.Flyout.Hide();
        Dispatcher.UIThread.RunJobs();
    }

    // Ruling C22's own consequence in the gear: the picker's groups are a projection of the live
    // column collection, so a column appended to it after the gear was built reaches the Custom
    // heading. Without this the flyout of an application that never re-queried would list the
    // built-in six for the rest of the process.
    //
    // Red against groups built once in the picker's constructor: the appended column is in Columns,
    // the flyout draws no Custom heading and no box for it.
    [AvaloniaFact]
    public void AColumnAppendedAfterTheGearWasBuilt_ReachesTheCustomHeading()
    {
        var list = Plain();
        var view = new TargetListView { DataContext = list };
        Show(view, SpaciousAllotment, SpaciousWindow);

        var gear = Named<Button>(view, "ColumnGearButton");
        Assert.False(list.ColumnPicker.ShowGroupHeadings);

        list.Columns.Add(new ColumnViewModel("custom_processed", "Processed", false, canHide: true));
        Dispatcher.UIThread.RunJobs();

        gear.Flyout!.ShowAt(gear);
        Dispatcher.UIThread.RunJobs();

        Assert.True(list.ColumnPicker.ShowGroupHeadings);
        Assert.Contains("Custom", FlyoutTexts(gear));
        Assert.Contains("Processed", FlyoutBoxes(gear));

        gear.Flyout.Hide();
        Dispatcher.UIThread.RunJobs();
    }

    private static Window Show(TargetListView view, double allotment, double windowWidth = 1280d)
    {
        var host = new Border
        {
            Width = allotment,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = view,
        };
        var window = new Window { Width = windowWidth, Height = 800, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A second pass, for the reason TargetListViewTests records: the Name cell bounds its own
        // text by the width it was arranged at, so the first pass settles the column and the second
        // settles the text inside it.
        view.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void AssertDrawsWhole(TargetListView view)
    {
        var toggle = Named<Button>(view, "SessionsToggle");
        var widths = string.Join(
            ", ",
            GridNamed(view, "TargetRowGrid").ColumnDefinitions.Select(column => column.ActualWidth));
        Assert.True(
            RightEdge(toggle, view) <= view.Bounds.Width + 0.5d,
            $"The sessions control ends at {RightEdge(toggle, view)} in a {view.Bounds.Width} viewport. "
                + $"Row columns: {widths}. Data cap {view.DataCellMaxWidth}, name cap {view.NameCellMaxWidth}.");

        var strip = Strip(view, "CustomCellStrip");
        Assert.True(
            RightEdge(strip, view) <= view.Bounds.Width + 0.5d,
            $"The custom strip ends at {RightEdge(strip, view)} in a {view.Bounds.Width} viewport.");
    }

    // Ruling C24's second half: the header strip and every row strip always show the same set.
    private static void AssertStripsAgree(TargetListView view, TargetListViewModel list)
    {
        var header = Strip(view, "CustomHeaderStrip");
        Assert.Equal(
            list.DrawnCustomColumns.Count,
            header.GetVisualDescendants().OfType<TextBlock>().Count());

        foreach (var strip in view.GetVisualDescendants().OfType<ItemsControl>()
                     .Where(control => control.Name == "CustomCellStrip"))
        {
            Assert.Equal(
                list.DrawnCustomColumns.Count,
                strip.GetVisualDescendants().OfType<CustomCellEditor>().Count());
        }
    }

    private static void Press(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point At(Window window, Control control, double x, double y)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width * x, control.Bounds.Height * y), window);
        Assert.NotNull(point);
        return point.Value;
    }

    // Ruling C24's first half, per entry: the label of column n is drawn over the cells of column
    // n, at the same width and the same x, whatever either of them measures on its own.
    private static void AssertEntriesLineUp(TargetListView view)
    {
        var headings = Strip(view, "CustomHeaderStrip").GetVisualDescendants().OfType<TextBlock>().ToList();
        var cells = Strip(view, "CustomCellStrip").GetVisualDescendants().OfType<CustomCellEditor>().ToList();

        Assert.Equal(headings.Count, cells.Count);
        for (var index = 0; index < headings.Count; index++)
        {
            var container = (Control)cells[index].GetVisualParent()!;
            Assert.True(
                Math.Abs(headings[index].Bounds.Width - container.Bounds.Width) < ColumnSettle,
                $"Entry {index}: heading {headings[index].Bounds.Width}, cell {container.Bounds.Width}.");
            Assert.True(
                Math.Abs(LeftEdge(headings[index], view) - LeftEdge(container, view)) < ColumnSettle,
                $"Entry {index}: heading at {LeftEdge(headings[index], view)}, "
                    + $"cell at {LeftEdge(container, view)}.");
        }
    }

    private static TextBlock Heading(TargetListView view, string text)
        => Strip(view, "CustomHeaderStrip")
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .First(block => block.Text == text);

    // Whether the run on screen is the whole name or an elided one. The Text property still carries
    // the whole name either way, so it cannot answer this.
    private static bool IsTrimmed(TextBlock block)
        => block.TextLayout.TextLines.Any(line => line.HasCollapsed);

    private static ItemsControl Strip(TargetListView view, string name)
        => view.GetVisualDescendants().OfType<ItemsControl>().First(control => control.Name == name);

    private static Grid GridNamed(TargetListView view, string name)
        => view.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == name);

    private static T Named<T>(TargetListView view, string name) where T : Control
        => view.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private static double ColumnWidth(TargetListView view, string gridName, int index)
        => GridNamed(view, gridName).ColumnDefinitions[index].ActualWidth;

    private static double InnerColumnWidth(Grid grid, int index) => grid.ColumnDefinitions[index].ActualWidth;

    private static double LeftEdge(Control control, Visual root)
        => control.TranslatePoint(default, root)!.Value.X;

    private static double RightEdge(Control control, Visual root)
        => control.TranslatePoint(new Point(control.Bounds.Width, 0), root)!.Value.X;
}
