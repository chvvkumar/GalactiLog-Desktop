using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.Views.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for the target list: it parses, lays out, and binds against
// a populated view-model, and a hidden column takes its header cell with it. Compiled bindings
// already turn a binding-path typo into a build error; these catch the rest (a missing resource,
// a template that cannot realize, a row cell that cannot find its column).
//
// Phase 14C Task 3 (spec 12.2, user ruling U3) adds the layout cases: one line per row, a header
// label over its own column's cells, one rule between rows, the sessions control at the trailing
// end, and one line of Border.tag badges in filter order. Every case that measures lays out inside
// the shell at 1280 by 800, which is what ShowList does.
public class TargetListViewTests
{
    // Spec 12.2's row height and header height (DESIGN.md section 5): 34 for a row, 30 for the
    // header, which is the base control height Button.header-cell already carries.
    private const double RowHeight = 34d;

    // How far a column may differ between the header and a row. The star column's cell bounds its
    // own text by the width it was arranged at, which settles about two pixels above the column's
    // floor rather than exactly on it; anything larger is the class of defect this file exists to
    // catch, which measured 24 and 26 pixels. Below the 8 pixel gutter, so it cannot read.
    private const double ColumnSettle = 2.5d;
    private const double HeaderHeight = 30d;

    // The palette's three filters are chosen so that filter order, frame-count descending and the
    // order the query hands over are three different orders: filter order (polish ruling 5) is
    // L, B, Ha, frame count descending is L, Ha, B, and the declared order below is Ha, L, B. A
    // fixture whose orders coincide would prove nothing about where the sort happens.
    private static readonly TargetRow SampleRow = new(
        GroupKey: "10000000-0000-0000-0000-000000000000",
        TargetId: Guid.Parse("10000000-0000-0000-0000-000000000000"),
        Name: "M 31",
        CommonName: "Andromeda Galaxy",
        CatalogId: "M 31",
        ObjectType: "G",
        ObjectCategory: "Galaxy",
        IntegrationSeconds: 44_640d,
        FrameCount: 148,
        SessionCount: 2,
        FirstSession: new DateOnly(2024, 1, 5),
        LastSession: new DateOnly(2025, 12, 7),
        Palette:
        [
            new FilterBadge("Ha", "#FF0000", 40, 1_500d),
            new FilterBadge("L", "#FFFFFF", 900, 27_000d),
            new FilterBadge("B", "not-a-colour", 5, 12_000d),
        ],
        Equipment: ["RC8 / ASI2600MM"],
        Aliases: [],
        Sessions:
        [
            // The newest night carries filters, drawn as Border.tag badges; the older one carries
            // none, which must render as an empty cell rather than a placeholder (spec 12.2).
            new SessionSummary(new DateOnly(2025, 12, 7), 88, 26_400d, [new FilterBadge("Ha", "#FF0000", 88, 26_400d)]),
            new SessionSummary(new DateOnly(2024, 1, 5), 60, 18_240d),
        ]);

    // The second row: every cell wider than the first row's, a long common name, and seven badges.
    // It is the row that would actually have caught the shipped bug, and it is what gives the
    // alignment case two rows whose content differs in width per column. The designation seeds the
    // aligned-table pattern's own wide figure (DESIGN.md section 6), so the case can prove the
    // column is not sitting at the width of its header label before it compares anything.
    private static readonly TargetRow WideRow = new(
        GroupKey: "20000000-0000-0000-0000-000000000000",
        TargetId: Guid.Parse("20000000-0000-0000-0000-000000000000"),
        Name: "NGC 7000 - the North America complex",
        CommonName: "North America Nebula and the Pelican",
        CatalogId: "PGC 123456.789",
        ObjectType: "HII",
        ObjectCategory: "Nebula",
        IntegrationSeconds: 123_456d,
        FrameCount: 1_204,
        SessionCount: 1,
        FirstSession: new DateOnly(2025, 2, 2),
        LastSession: new DateOnly(2025, 11, 30),
        Palette:
        [
            new FilterBadge("SII", "#00A0FF", 30, 9_000d),
            new FilterBadge("OIII", "#00FF80", 40, 12_000d),
            new FilterBadge("Ha", "#FF0000", 50, 15_000d),
            new FilterBadge("R", "#FF4040", 20, 6_000d),
            new FilterBadge("G", "#40FF40", 20, 6_000d),
            new FilterBadge("B", "#4040FF", 20, 6_000d),
            new FilterBadge("L", "#FFFFFF", 100, 30_000d),
        ],
        Equipment: ["Esprit 100 / ASI6200MM Pro"],
        Aliases: [],
        Sessions: [new SessionSummary(new DateOnly(2025, 11, 30), 40, 12_000d)]);

    private static TargetListViewModel CreatePopulatedList(bool bothRows = false)
    {
        var display = new DisplaySettings();
        var list = new TargetListViewModel(display, () => display, value => display = value, 50);
        list.Load(new TargetListingPage(
            bothRows ? [SampleRow, WideRow] : [SampleRow], 120, 44_640d, 148, 1, 50));
        list.Rows[0].IsExpanded = true;
        return list;
    }

    // The page's own allotment, not the whole window. On the shipped 1280 by 800 dashboard the list
    // runs from x 538 to x 1258, about 720 pixels: the navigation rail, the 300 pixel filter panel
    // and the page and card gutters take the rest. A bare 1280 window hands the view about 1256,
    // which is how fifteen green layout cases missed a row that overflowed its own viewport by
    // roughly 270 pixels (fix pass, review P2 on the harness).
    private const double PageAllotment = 720d;

    // The same page at about 1900, where the list's allotment is roughly 1330.
    private const double WidePageAllotment = 1330d;

    private static Window ShowList(TargetListView view, double allotment = PageAllotment, double windowWidth = 1280d)
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

        // A second pass. The Name cell bounds its own text by the width it was arranged at, so the
        // first pass settles the column and the second settles the text inside it; a live window
        // lays out continuously and never shows the intermediate state.
        view.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void TargetListView_Constructs_AndRendersAPopulatedViewModel()
    {
        var list = CreatePopulatedList();
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("M 31", texts);
        Assert.Contains("Andromeda Galaxy", texts);
        Assert.Contains("12.4 h", texts);
        Assert.Contains("2025-12-07", texts);
        Assert.Contains("RC8 / ASI2600MM", texts);
        Assert.Contains("Ha", texts);
        Assert.Contains("1 to 50 of 120", texts);

        // Phase 14C: the session count is the trailing button's tooltip, not a label on the row
        // (spec 12.2, "The sessions control"). The fixture opens the row, so the button reads
        // Collapse. View_TheSessionsControl_CarriesTheCountOnItsTooltip is the dedicated case.
        Assert.DoesNotContain("2 sessions", texts);
        Assert.Contains("Collapse", texts);
    }

    // Phase 18 Task 6, spec 12.2: the mosaic link sits in the Name cell of a row whose target has
    // an included night in some mosaic and nowhere else; it opens the first mosaic by name and
    // never the row's Target detail.
    [AvaloniaFact]
    public void TheMosaicLink_IsDrawnOnlyForATargetInAMosaic_AndOpensTheFirstByName()
    {
        var alpha = new MosaicLink(Guid.NewGuid(), "Alpha");
        var beta = new MosaicLink(Guid.NewGuid(), "beta");
        var display = new DisplaySettings();
        var list = new TargetListViewModel(display, () => display, value => display = value, 50);
        list.Load(new TargetListingPage([SampleRow with { Mosaics = [alpha, beta] }, WideRow], 120, 44_640d, 148, 1, 50));
        var opened = new List<Guid>();
        var targets = new List<TargetOpenRequest>();
        list.MosaicOpened += (_, id) => opened.Add(id);
        list.TargetOpened += (_, request) => targets.Add(request);
        var view = new TargetListView { DataContext = list };
        var window = ShowList(view);

        var links = view.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "MosaicLink").ToList();
        Assert.Equal(2, links.Count);
        var shown = Assert.Single(links, link => link.IsEffectivelyVisible);
        Assert.Same(list.Rows[0], shown.DataContext);
        Assert.Equal("Mosaics: Alpha, beta", ToolTip.GetTip(shown));
        Assert.Null(list.Rows[1].MosaicId);
        Assert.Equal("Alpha", list.Rows[0].MosaicName);

        // A real pointer press, so the row's own click target behind the cells gets its chance to
        // take it: the link must, and the row must not.
        Click(window, shown);

        Assert.Equal([alpha.MosaicId], opened);
        Assert.Empty(targets);

        // The falsifying half: a press on the name itself reaches the row and opens the target.
        Click(window, NamedCells(view, "NameCell")[0].GetVisualDescendants().OfType<TextBlock>().First());
        Assert.Single(targets);
        Assert.Equal([alpha.MosaicId], opened);

        list.Load(new TargetListingPage([SampleRow with { Mosaics = [beta] }], 1, 44_640d, 148, 1, 50));
        Assert.Equal("Mosaic: beta", list.Rows[0].MosaicTooltip);
    }

    // Fix round 1: the Name cell clips at NameCellMaxWidth, so a name as wide as the cell used to
    // arrange the link past the clip edge, invisible and unclickable. The link is measured first
    // and the name trims into what is left.
    [AvaloniaFact]
    public void TheMosaicLink_StaysInsideTheNameCell_OnALongName()
    {
        var display = new DisplaySettings();
        var list = new TargetListViewModel(display, () => display, value => display = value, 50);
        var longName = WideRow with
        {
            Name = "NGC 6960 - Veil Nebula, Filamentary Nebula, Western Veil, Witch's Broom, Caldwell 34",
            Mosaics = [new MosaicLink(Guid.NewGuid(), "Veil")],
        };
        list.Load(new TargetListingPage([longName], 1, 44_640d, 148, 1, 50));
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var cell = Assert.Single(NamedCells(view, "NameCell"));
        var link = cell.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MosaicLink");
        Assert.True(link.IsEffectivelyVisible);
        Assert.True(link.Bounds.Width > 0);

        var topLeft = link.TranslatePoint(new Point(0, 0), cell);
        var bottomRight = link.TranslatePoint(new Point(link.Bounds.Width, link.Bounds.Height), cell);
        Assert.NotNull(topLeft);
        Assert.NotNull(bottomRight);
        Assert.True(topLeft.Value.X >= 0 && bottomRight.Value.X <= cell.Bounds.Width + 0.5d,
            $"link spans {topLeft.Value.X:F1} to {bottomRight.Value.X:F1} in a cell {cell.Bounds.Width:F1} wide");
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TargetListView_EveryTextBlock_RendersAtAReadableSize()
    {
        // Review item 1. Scales.axaml's FontSize* keys are ratios (0.5 to 0.714), not point sizes:
        // binding one to FontSize used to render the sort glyph, the common name and the palette
        // badges at well under a pixel. Nothing in this view sets FontSize any more, and this is
        // what fails if someone binds one of those keys again.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var blocks = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }

    [AvaloniaFact]
    public void TargetListView_TemplatedCommandBindings_Resolve()
    {
        // Review item 4. The header cell and the row are DataTemplates whose commands come from
        // the view's own DataContext through #Root, which compiled bindings cannot check: a typo
        // there is a silently dead button, not a build error.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view, WidePageAllotment, 1900d);

        var headerButtons = HeaderCells(view)
            .SelectMany(cell => cell.GetVisualDescendants().OfType<Button>())
            .ToList();

        // Still six after the sessions column arrives: its header cell is empty and holds no
        // button. A button placed there is what fails this, and the fix is to remove it.
        Assert.Equal(6, headerButtons.Count);
        Assert.All(headerButtons, button =>
        {
            Assert.NotNull(button.Command);
            Assert.IsType<string>(button.CommandParameter);
        });

        // The expander's own toggle inherits the row's DataContext too; the row button is the one
        // that passes the row as its command parameter.
        // Phase 18: the mosaic link carries the row too, so it is excluded by name.
        var rowButton = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            button => button.CommandParameter is TargetRowViewModel && button.Name != "MosaicLink");

        Assert.NotNull(rowButton.Command);
        Assert.IsType<TargetRowViewModel>(rowButton.CommandParameter);
    }

    [AvaloniaFact]
    public void TargetListView_HidingAColumn_RemovesItsHeaderCell()
    {
        var list = CreatePopulatedList();
        var view = new TargetListView { DataContext = list };
        ShowList(view, WidePageAllotment, 1900d);

        Assert.Contains("Equipment", VisibleHeaderTitles(view));
        Assert.Contains("RC8 / ASI2600MM", VisibleCellTexts(view));

        var equipmentColumn = list.Columns.Single(column => column.Key == "equipment");
        var equipmentCell = HeaderCells(view).Single(cell => ReferenceEquals(cell.Content, equipmentColumn));
        Assert.True(ColumnWidth(view, "TargetListHeaderRow", 4) > 0);

        list.ToggleColumnCommand.Execute(equipmentColumn);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("Equipment", VisibleHeaderTitles(view));
        Assert.DoesNotContain("RC8 / ASI2600MM", VisibleCellTexts(view));

        // Review item 2: the whole header cell collapses, not just the button inside it. On the
        // button, the container kept its fixed width and every column to its right sat one cell
        // out of line with the rows. Since Phase 14C the gutter rides on the cell rather than on
        // the grid, so a hidden column takes its 24 pixels with it and leaves no gap.
        //
        // Re-pointed in the fix pass. The old assertion was that the next cell's x moved left by
        // exactly the hidden cell's width, which a star column makes false by design: Equipment is
        // one of the two stars now, so the width it gives up is absorbed by Name rather than
        // pulling Last Session leftwards. The rule the case exists for is that the hidden column
        // contributes nothing and the table stays aligned, which is what it asserts instead.
        Assert.False(equipmentCell.IsVisible);
        Assert.Equal(0d, ColumnWidth(view, "TargetListHeaderRow", 4));
        Assert.All(RowGrids(view), grid => Assert.Equal(0d, InnerColumnWidth(grid, 4)));
        AssertHeaderAlignsWithRows(view, list, skip: "equipment");

        // The name column's checkbox is the one that cannot be cleared (ruling Q5).
        Assert.Contains("Name", VisibleHeaderTitles(view));
    }

    // The six header cells: ContentControls whose Content is a column. Buttons are ContentControls
    // too, so the Content test is what separates them.
    private static IReadOnlyList<ContentControl> HeaderCells(TargetListView view)
        => [.. view.GetVisualDescendants().OfType<ContentControl>().Where(cell => cell.Content is ColumnViewModel)];

    private static IReadOnlyList<string> VisibleHeaderTitles(TargetListView view)
        => [.. view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .SelectMany(button => button.GetVisualDescendants().OfType<TextBlock>())
            .Select(block => block.Text ?? "")];

    private static IReadOnlyList<string> VisibleCellTexts(TargetListView view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    [AvaloniaFact]
    public void TargetListView_SessionsToggle_IsARowButton_ThatShowsAndHidesTheSessionLines()
    {
        var list = CreatePopulatedList();
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        // Collapsed: the per-session lines are not rendered. The older session's date is the
        // discriminator; the newer one is also the row's Last Session cell.
        Assert.DoesNotContain("2024-01-05", VisibleCellTexts(view));

        var toggle = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            button => button.Name == "SessionsToggle");
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.NotNull(toggle.Command);
        // No Expander any more: the row button is the one control that opens the sessions.
        Assert.Empty(view.GetVisualDescendants().OfType<Expander>());

        toggle.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(list.Rows[0].IsExpanded);
        Assert.Contains("2024-01-05", VisibleCellTexts(view));

        toggle.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(list.Rows[0].IsExpanded);
        Assert.DoesNotContain("2024-01-05", VisibleCellTexts(view));
    }

    // ---- Phase 14B Task 6: the Filters column and Deep dive (spec 12.2, PAR-010) -------------

    [AvaloniaFact]
    public void View_AnExpandedRow_HasFourSessionColumns()
    {
        var list = CreatePopulatedList();
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        // Date, Frames, Integration: the three that already existed. 26,400 seconds is 7.3 hours.
        var texts = VisibleCellTexts(view);
        Assert.Contains("2025-12-07", texts);
        Assert.Contains("88", texts);
        Assert.Contains("7.3 h", texts);

        // Phase 14B fixer, fixer list item 38 (task6-review P3). The fourth column used to be
        // proved by Assert.Contains("Ha", texts) over every visible TextBlock, and SampleRow's
        // Palette already carries a Ha badge, so the case stayed green with the Filters cell
        // rendering nothing at all. The cell is read by ItemsSource reference instead, the way the
        // two neighbouring cases already do, and its own badge text is the assertion.
        var filters = list.Rows[0].Sessions[0].Filters;
        var cell = view.GetVisualDescendants().OfType<ItemsControl>()
            .Single(c => c.Name == "SessionFiltersCell" && ReferenceEquals(c.ItemsSource, filters));

        Assert.Contains(
            "Ha",
            cell.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
    }

    [AvaloniaFact]
    public void View_TheFiltersCell_DrawsTheSameBadgesAsThePalette()
    {
        var list = CreatePopulatedList();
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        // The newest session (index 0) carries the one filter badge; selected by ItemsSource
        // reference rather than by name alone, since both sessions render a cell of that name.
        var filters = list.Rows[0].Sessions[0].Filters;
        var cell = view.GetVisualDescendants().OfType<ItemsControl>()
            .Single(c => c.Name == "SessionFiltersCell" && ReferenceEquals(c.ItemsSource, filters));

        // Re-pointed for Phase 14C. The predicate used to be "the one Border carrying a brush",
        // which no longer separates a badge from anything: under Border.tag the outline comes from
        // the style, so every badge Border has a non-null brush and so may a template root. The
        // class is the selector now.
        var badge = Assert.Single(Badges(cell));

        // Spec 12.2: both cells draw the same Border.tag badge. Both share the one
        // TargetFilterBadge template, so the shape is identical by construction rather than by two
        // copies of the same markup, and the 2 pixel tinted border is retired.
        Assert.Equal(1d, badge.BorderThickness.Left);
        Assert.NotNull(badge.BorderBrush);
    }

    [AvaloniaFact]
    public void View_ANightWithNoFilter_DrawsAnEmptyCell()
    {
        var list = CreatePopulatedList();
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var cells = view.GetVisualDescendants().OfType<ItemsControl>().Where(c => c.Name == "SessionFiltersCell").ToList();
        Assert.Equal(2, cells.Count);

        // The older session (index 1, 2024-01-05) has no filters: its cell realizes no badge at
        // all, rather than a placeholder. Selected by class for the reason the case above gives.
        var emptyFilters = list.Rows[0].Sessions[1].Filters;
        var empty = cells.Single(c => ReferenceEquals(c.ItemsSource, emptyFilters));

        Assert.Empty(Badges(empty));
    }

    [AvaloniaFact]
    public void View_EachSessionLine_HasADeepDiveButton()
    {
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "DeepDiveButton").ToList();
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button => Assert.NotNull(button.Command));
    }

    [AvaloniaFact]
    public void View_TheDeepDiveButton_CarriesTheSessionRowAsItsParameter()
    {
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "DeepDiveButton").ToList();
        Assert.All(buttons, button => Assert.IsType<SessionRowViewModel>(button.CommandParameter));
    }

    [AvaloniaFact]
    public void View_StillHasExactlyOneButtonCarryingATargetRow()
    {
        // Restates TargetListView_TemplatedCommandBindings_Resolve's uniqueness assertion here so
        // a Deep dive button, or the trailing sessions button, that carries the parent row by
        // mistake fails in this task's own filtered run rather than only in the full suite.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            button => button.CommandParameter is TargetRowViewModel && button.Name != "MosaicLink");
    }

    [AvaloniaFact]
    public void View_StillHasNoExpander()
    {
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        Assert.Empty(view.GetVisualDescendants().OfType<Expander>());
    }

    [AvaloniaFact]
    public void View_TheColumnButton_IsAGear()
    {
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var gear = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => button.Name == "ColumnGearButton");

        // A drawn Path over the geometry, not the text label the button used to show
        // (DESIGN.md section 8, questions.md Q10).
        Assert.Contains(gear.GetVisualDescendants().OfType<ShapePath>(), path => path.Data is not null);
        Assert.DoesNotContain(gear.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Columns");
    }

    [AvaloniaFact]
    public void View_TheColumnButton_KeepsItsTooltip()
    {
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var gear = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => button.Name == "ColumnGearButton");
        Assert.Equal("Columns", ToolTip.GetTip(gear));
    }

    [AvaloniaFact]
    public void View_DeclaresNoStyleOfItsOwn()
    {
        // Task6.md section 8, questions.md Q2: every control this task adds is already shared
        // vocabulary. A source check rather than a runtime one, the same shape
        // ControlStyleScanTest itself uses over every view under src/.
        var source = File.ReadAllText(FindTargetListViewAxaml());
        Assert.DoesNotContain("<Style ", source);
    }

    // ---- Phase 14C Task 3: the flat row (spec 12.2, user ruling U3) --------------------------

    [AvaloniaFact]
    public void View_ARow_IsOneLineHigh()
    {
        // A failure is a row that grows past one line because a cell wrapped, the common name
        // stacked, or the trailing button forced a taller line. Measured on the laid-out row grid
        // inside the shell at 1280 by 800, never against a constant the view declares.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        Assert.Equal(RowHeight, Assert.Single(RowGrids(view)).Bounds.Height);

        // The header row is the base control height, under its own rule.
        var header = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TargetListHeaderRow");
        Assert.Equal(HeaderHeight, header.Bounds.Height);
    }

    [AvaloniaFact]
    public void View_ARowWithALongCommonNameAndManyBadges_IsStillOneLineHigh()
    {
        // The case that would have caught the shipped bug: a 35 character common name and a seven
        // filter palette. Before Phase 14C the name stacked and the palette wrapped, and this row
        // drew at twice the height of a one badge row.
        var list = CreatePopulatedList(bothRows: true);
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var wide = RowGrids(view)[1];
        Assert.Equal(7, list.Rows[1].PaletteBadges.Count);
        Assert.Equal(RowHeight, wide.Bounds.Height);
        Assert.All(RowGrids(view), grid => Assert.Equal(RowHeight, grid.Bounds.Height));
    }

    [AvaloniaFact]
    public void View_EveryColumn_AlignsItsHeaderWithItsCells()
    {
        // A failure is a header label sitting at a different x from its column's cells, which is
        // what the user saw. Two rows whose content differs in width in every column, so a case
        // cannot pass on two identical rows, and the wide row's designation seeds the
        // aligned-table pattern's guard figure (DESIGN.md section 6): the Designation column has to
        // measure wider than its own header label before any comparison means anything.
        var list = CreatePopulatedList(bothRows: true);
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view, WidePageAllotment, 1900d);

        var designationHeader = HeaderCell(view, list, "designation");
        Assert.Contains("123456.789", list.Rows[1].Designation);
        Assert.True(
            designationHeader.Bounds.Width > HeaderTitle(designationHeader).Bounds.Width + 16d,
            "The Designation column is sitting at the width of its own header label, so alignment proves nothing.");

        AssertHeaderAlignsWithRows(view, list);
    }

    [AvaloniaFact]
    public void View_AtThePageAllotment_TheHeaderAndTheRows_ResolveOneColumnSet()
    {
        // The fix pass's own case, at the allotment the page actually gives the list rather than a
        // bare window's full width (review P2 on the harness).
        //
        // It fails on the shape this task first shipped and on the first two attempts at the fix.
        // With the header grid in a star column beside the help glyph and the gear it was drawn
        // about 60 pixels narrower than a row; with Name and Equipment as star columns the row's
        // stars were floored at their cells' desired widths while the header's were not, and the
        // Name column came out 24 pixels wider in a row than in the header. The two grids resolve
        // one column set now, asserted column by column rather than only at the cells' edges, so
        // either regression fails here and not only in a screenshot.
        AssertOneColumnSet(PageAllotment, 1280d);
        AssertOneColumnSet(WidePageAllotment, 1900d);
    }

    private static void AssertOneColumnSet(double allotment, double windowWidth)
    {
        var list = CreatePopulatedList(bothRows: true);
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view, allotment, windowWidth);

        var header = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TargetListHeaderRow");
        foreach (var row in RowGrids(view))
        {
            Assert.Equal(header.ColumnDefinitions.Count, row.ColumnDefinitions.Count);
            var shape = $"at {allotment}: header [{string.Join(",", header.ColumnDefinitions.Select(d => d.ActualWidth))}]"
                + $" row [{string.Join(",", row.ColumnDefinitions.Select(d => d.ActualWidth))}]";
            for (var i = 0; i < header.ColumnDefinitions.Count; i++)
            {
                Assert.True(
                    Math.Abs(header.ColumnDefinitions[i].ActualWidth - row.ColumnDefinitions[i].ActualWidth) < ColumnSettle,
                    $"Column {i} diverges, {shape}.");
            }
        }

        // Nothing is carried sideways by a scroller: it is disabled, not merely hidden, which is
        // what lets a row be measured at the viewport at all and what closes the header's old
        // drift out of line with the rows under horizontal scroll.
        Assert.DoesNotContain(
            view.GetVisualDescendants().OfType<ScrollBar>(),
            bar => bar.Orientation == Orientation.Horizontal && bar.IsEffectivelyVisible);

        // The free-text columns give way with an ellipsis rather than a cut.
        Assert.All(
            NamedCells(view, "EquipmentCell").Cast<TextBlock>(),
            cell => Assert.Equal(TextTrimming.CharacterEllipsis, cell.TextTrimming));
        Assert.All(
            NamedCells(view, "NameCell").SelectMany(cell => cell.GetVisualDescendants().OfType<TextBlock>()),
            block => Assert.Equal(TextTrimming.CharacterEllipsis, block.TextTrimming));

        AssertHeaderAlignsWithRows(view, list, skip: view.IsEquipmentShown ? null : "equipment");
    }

    [AvaloniaFact]
    public void View_AtAWideWindow_TheSessionsControl_SitsAtTheRowsTrailingEdge()
    {
        // The mirror of the case above, and the one that fails on review C2: with every column Auto
        // the seven hugged the left at a wide window and the sessions control sat mid-row with
        // about 220 pixels of dead space after it. The star columns take the remainder now.
        var list = CreatePopulatedList(bothRows: true);
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view, WidePageAllotment, 1900d);

        var toggle = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SessionsToggle");
        var row = RowGrids(view)[0];

        Assert.True(view.Bounds.Width >= WidePageAllotment - 0.5d);
        Assert.Equal(view.Bounds.Width - TargetListView.TrailingInset, RightEdge(row, view), 1);
        Assert.True(
            RightEdge(row, view) - RightEdge(toggle, view) <= 12.5d,
            $"The sessions control ends {RightEdge(row, view) - RightEdge(toggle, view)} short of the row's trailing edge.");

        // The row's click target reaches the sessions control rather than stopping after the six
        // data cells (review P3 on the dead area).
        var rowButton = view.GetVisualDescendants().OfType<Button>()
            .First(b => b.CommandParameter is TargetRowViewModel);
        Assert.True(
            RightEdge(rowButton, view) >= LeftEdge(toggle, view) - 12.5d,
            "The row button stops short of the sessions cell, leaving dead space that does not open the target.");

        AssertHeaderAlignsWithRows(view, list);
    }

    [AvaloniaFact]
    public void View_NoCellDeclaresAFixedWidth()
    {
        // A failure is a Width= surviving on a header cell or a row cell, or one of the seven
        // retired TargetColumn*Width resources. MaxWidth is permitted on the Name and Equipment
        // cell content and must not be rejected, so the pattern requires a digit after the quote
        // and excludes the Max and Min spellings. The gear icon's own 16 square is not a cell.
        var source = File.ReadAllText(FindTargetListViewAxaml());

        foreach (var key in new[]
                 {
                     "TargetSessionsCellWidth", "TargetColumnNameWidth", "TargetColumnDesignationWidth",
                     "TargetColumnPaletteWidth", "TargetColumnIntegrationWidth", "TargetColumnEquipmentWidth",
                     "TargetColumnLastSessionWidth",
                 })
        {
            Assert.DoesNotContain(key, source);
        }

        var offenders = source
            .Split('\n')
            .Where(line => Regex.IsMatch(line, @"(?<!Max|Min)Width=""\d") && !line.Contains("<Path"))
            .ToList();

        Assert.Empty(offenders);
        Assert.Contains("MaxWidth=\"", source);
    }

    [AvaloniaFact]
    public void View_TheRows_AreSeparatedByOneRuleAndCarryNoCard()
    {
        // A failure is the per-row card coming back (a fill, a radius, a four sided border) or a
        // row drawing a top edge as well, which puts a 2 pixel line between two rows.
        var view = new TargetListView { DataContext = CreatePopulatedList(bothRows: true) };
        ShowList(view);

        var rules = view.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("rule"))
            .ToList();

        // One under the header, one under each of the two rows.
        Assert.Equal(3, rules.Count);
        Assert.All(rules, border =>
        {
            Assert.Equal(new Thickness(0, 0, 0, 1), border.BorderThickness);
            Assert.Equal(default, border.CornerRadius);
            Assert.Null(border.Background);
        });

        // No rounded or filled container anywhere in a row but the badges themselves, which since
        // the frosted-glass port carry their tint wash on an inner Border of their own.
        Assert.All(
            RowGrids(view).SelectMany(grid => grid.GetVisualDescendants().OfType<Border>())
                .Where(border => !border.Classes.Contains("tag")
                    && !border.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("tag"))),
            border => Assert.Equal(default, border.CornerRadius));
    }

    [AvaloniaFact]
    public void View_TheSessionsControl_IsTheTrailingCell()
    {
        // A failure is it staying in a leading cell, which is where the chevron toggle sat.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var toggle = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "SessionsToggle");
        var lastSession = Assert.Single(NamedCells(view, "LastSessionCell"));

        Assert.True(
            LeftEdge(toggle, view) > LeftEdge(lastSession, view),
            "The sessions control is not past the Last Session cell.");
    }

    [AvaloniaFact]
    public void View_TheSessionsControl_ReadsExpandThenCollapse()
    {
        var list = CreatePopulatedList();
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var toggle = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "SessionsToggle");
        Assert.Equal("Expand", toggle.Content);

        toggle.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Collapse", toggle.Content);
    }

    [AvaloniaFact]
    public void View_TheSessionsControl_CarriesTheCountOnItsTooltip()
    {
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var toggle = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "SessionsToggle");
        Assert.Equal("2 sessions", ToolTip.GetTip(toggle));
    }

    [AvaloniaFact]
    public void View_ThePaletteBadges_AreInFilterOrder()
    {
        // A failure is the badges rendering in the query's own order, Ha, L, B, or in the
        // alphabetical order B, Ha, L that polish ruling 5 retired.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var cell = Assert.Single(NamedCells(view, "PaletteCell"));
        Assert.Equal(
            ["L", "B", "Ha"],
            Badges(cell).Select(badge => badge.GetVisualDescendants().OfType<TextBlock>().Single().Text));
    }

    [AvaloniaFact]
    public void View_ThePaletteCell_DoesNotWrap()
    {
        // A failure is the cell laying out a second badge row, which is what made the row two lines
        // high. Both WrapPanels are retired, and every badge in a cell shares one y.
        var view = new TargetListView { DataContext = CreatePopulatedList(bothRows: true) };
        ShowList(view);

        Assert.Empty(view.GetVisualDescendants().OfType<WrapPanel>());

        foreach (var cell in NamedCells(view, "PaletteCell"))
        {
            var badges = Badges(cell);
            Assert.NotEmpty(badges);
            var tops = badges.Select(badge => Math.Round(badge.TranslatePoint(default, cell)!.Value.Y, 2)).Distinct();
            Assert.Single(tops);
        }
    }

    [AvaloniaFact]
    public void View_ABadge_IsBorderTag_WithTheFilterColourAsItsOutline()
    {
        // A failure is the 2 pixel tinted border of the old build surviving, a solid filled pill,
        // or the filter colour being thrown away so every badge reads the same.
        //
        // Re-pointed 2026-10-05 (user request): the badge is the web app's frosted-glass style,
        // ported figure for figure from the frontend's utils/filterStyles.ts. The 1 pixel edge is
        // the tint at 25%, the wash inside it is the tint at 18%, and the label is the tint itself,
        // which reverses the earlier ColorBadgeText ruling in favour of matching the web app.
        var list = CreatePopulatedList();
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var cell = Assert.Single(NamedCells(view, "PaletteCell"));
        var badge = Badges(cell)[2];

        Assert.Contains("tag", badge.Classes);
        Assert.Equal(1d, badge.BorderThickness.Left);

        // Filter order L, B, Ha (polish ruling 5), so index 2 is Ha, whose stored colour is #FF0000.
        var tint = list.Rows[0].PaletteBadges[2].Tint.Color;
        Assert.Equal(Color.Parse("#FF0000"), tint);

        var edge = Assert.IsAssignableFrom<ISolidColorBrush>(badge.BorderBrush);
        Assert.Equal(Color.FromArgb(0x40, tint.R, tint.G, tint.B), edge.Color);

        var wash = Assert.IsAssignableFrom<ISolidColorBrush>(
            badge.GetVisualDescendants().OfType<Border>().Single().Background);
        Assert.Equal(Color.FromArgb(0x2E, tint.R, tint.G, tint.B), wash.Color);

        var ink = Assert.IsAssignableFrom<ISolidColorBrush>(
            badge.GetVisualDescendants().OfType<TextBlock>().Single().Foreground);
        Assert.Equal(tint, ink.Color);

        // The soft tinted drop shadow and the inset top highlight, in that order.
        Assert.Equal(2, badge.BoxShadow.Count);
        Assert.True(badge.BoxShadow[0].IsInset);
        Assert.Equal(Color.FromArgb(0x26, tint.R, tint.G, tint.B), badge.BoxShadow[1].Color);
    }

    [AvaloniaFact]
    public void View_ABadge_CarriesItsFrameCountOnItsTooltipNotInItsText()
    {
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var cell = Assert.Single(NamedCells(view, "PaletteCell"));
        // Filter order L, B, Ha (polish ruling 5), so index 2 is Ha.
        var text = Badges(cell)[2].GetVisualDescendants().OfType<TextBlock>().Single();

        Assert.Equal("Ha", text.Text);
        Assert.Equal("Ha, 40 frames", ToolTip.GetTip(text));
    }

    [AvaloniaFact]
    public void View_TheNameAndTheDesignation_ShareOneBaseline()
    {
        // A failure is the stacked subtitle coming back: the common name under the name puts the
        // name's own line above the designation's.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var nameCell = Assert.Single(NamedCells(view, "NameCell"));
        var blocks = nameCell.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.Equal(2, blocks.Count);

        var nameTop = blocks[0].TranslatePoint(default, view)!.Value.Y;
        var commonTop = blocks[1].TranslatePoint(default, view)!.Value.Y;
        var designation = Assert.Single(NamedCells(view, "DesignationCell"));
        var designationTop = designation.TranslatePoint(default, view)!.Value.Y;

        Assert.Equal(nameTop, commonTop, 1);
        Assert.Equal(nameTop, designationTop, 1);
    }

    [AvaloniaFact]
    public void View_TheExpander_CarriesNoBorderOfItsOwn()
    {
        // A failure is the 2 pixel left edge and the padded container coming back: DESIGN.md
        // section 8 refuses nested bordered containers, so the indent and the row's own rule pair
        // carry the containment. Checked above the expander as well as inside it, because the
        // retired edge sat on a Border wrapping it.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var expander = Assert.Single(
            view.GetVisualDescendants().OfType<ItemsControl>(), c => c.Name == "SessionsExpander");

        Assert.All(
            expander.GetVisualDescendants().OfType<Border>().Where(border => !border.Classes.Contains("tag")),
            border => Assert.Equal(new Thickness(0), border.BorderThickness));

        foreach (var ancestor in expander.GetVisualAncestors().OfType<Border>())
        {
            if (ancestor.Classes.Contains("rule"))
            {
                break;
            }

            Assert.Equal(new Thickness(0), ancestor.BorderThickness);
        }
    }

    [AvaloniaFact]
    public void View_ThePagerHasMovedAboveTheHeader_AndTheBottomPairIsGone()
    {
        // Re-points this file's Task 3 starting-point case (task4.md section 8): the numbered
        // pager now sits above the header row, Previous, Next and PageStatusText moved with it,
        // and the row it used to sit in at the bottom is gone. The pager's own thorough suite,
        // the page button set and the page-size select included, is
        // Views/TargetListPagerViewTests.cs; this case only proves this file's own claim still
        // holds after the move.
        var view = new TargetListView { DataContext = CreatePopulatedList() };
        ShowList(view);

        var buttons = view.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Content is "Previous" or "Next")
            .ToList();

        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button => Assert.True(button.IsEffectivelyVisible));
        Assert.Contains("1 to 50 of 120", VisibleCellTexts(view));

        var pager = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TargetListPager");
        var header = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TargetListHeaderRow");
        Assert.True(
            Grid.GetRow(pager) < Grid.GetRow((Control)header.Parent!),
            "The pager no longer sits above the header row.");
    }


    // ---- Phase 14C Task 3, second fix pass: the fit rule (spec 12.2, provisional) --------------

    [AvaloniaFact]
    public void View_AtTheShippedWindow_EveryColumnButEquipment_IsOnScreen()
    {
        // The user's directive 3 at the window the application ships in: the Expand button has to
        // be visible. Six columns and a worded button do not fit the 720 the page gives the list at
        // this type size, so Equipment gives way (the web's table has no equipment column at all).
        // This case fails on the previous pass's markup, where Equipment was cut at the viewport
        // edge and Last Session and the sessions control were off screen. It runs on the
        // representative row rather than the seven filter stress row, whose palette alone measures
        // 286 and which no column set fits into 720.
        var list = CreatePopulatedList();
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var toggle = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SessionsToggle");
        var lastSession = NamedCells(view, "LastSessionCell")[0];

        Assert.True(
            RightEdge(toggle, view) <= view.Bounds.Width + 0.5d,
            $"The sessions control ends at {RightEdge(toggle, view)} in a {view.Bounds.Width} viewport.");
        Assert.True(
            RightEdge(lastSession, view) <= view.Bounds.Width + 0.5d,
            $"Last Session ends at {RightEdge(lastSession, view)} in a {view.Bounds.Width} viewport.");

        // Equipment is not drawn, and it has taken its width with it. The tight cap is pinned here
        // so the pair with View_HidingEquipmentInTheGear_DoesNotTightenTheOtherColumns, which pins
        // DataCap at a wide window, cannot both pass with the tight cap never applied at all.
        Assert.Equal(TargetListView.TightCap, view.DataCellMaxWidth);
        Assert.False(view.IsEquipmentShown);
        Assert.All(NamedCells(view, "EquipmentCell"), cell => Assert.False(cell.IsEffectivelyVisible));
        Assert.Equal(0d, ColumnWidth(view, "TargetListHeaderRow", 4));

        // Name is the column that gives way, down to its floor and no further, with an ellipsis.
        var nameColumn = ColumnWidth(view, "TargetListHeaderRow", 0);
        Assert.True(nameColumn >= TargetListView.NameFloor - 0.5d, $"The Name column is {nameColumn}.");
        var wideName = NamedCells(view, "NameCell")[0].GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal(TextTrimming.CharacterEllipsis, wideName.TextTrimming);
        Assert.True(
            wideName.Bounds.Width < nameColumn,
            "The long name is not trimmed, so the star column is not giving way.");

        Assert.DoesNotContain(
            view.GetVisualDescendants().OfType<ScrollBar>(),
            bar => bar.Orientation == Orientation.Horizontal && bar.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void View_AtAWideWindow_Equipment_IsBack()
    {
        var list = CreatePopulatedList(bothRows: true);
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view, WidePageAllotment, 1900d);

        Assert.True(view.IsEquipmentShown);
        Assert.All(NamedCells(view, "EquipmentCell"), cell => Assert.True(cell.IsEffectivelyVisible));
        Assert.True(ColumnWidth(view, "TargetListHeaderRow", 4) > 0);
        Assert.Contains("Equipment", VisibleHeaderTitles(view));
    }

    [AvaloniaFact]
    public void View_TheEquipmentSuppression_IsLayoutOnly()
    {
        // It never writes display.columns.dashboard, the column stays shown, and the gear's own
        // checkbox stays ticked. A user who has hidden Equipment is unaffected: the two gates are
        // combined, not one overwriting the other.
        var display = new DisplaySettings();
        var saved = 0;
        var list = new TargetListViewModel(display, () => display, value => { display = value; saved++; }, 50);
        list.Load(new TargetListingPage([SampleRow, WideRow], 120, 44_640d, 148, 1, 50));
        var view = new TargetListView { DataContext = list };
        ShowList(view);

        var equipment = list.Columns.Single(column => column.Key == "equipment");
        Assert.False(view.IsEquipmentShown);
        Assert.True(equipment.IsShown);
        Assert.True(equipment.IsVisible);
        Assert.Equal(0, saved);
        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            display.ColumnsFor(DisplaySettings.DashboardTableId));
    }

    [AvaloniaFact]
    public void View_EverySortableHeader_ShowsItsFullLabelWithItsSortMark()
    {
        // The wide screenshot showed "Last Sess..." with its sort mark beside it while the column
        // had room: the cap on that header cell was smaller than label plus mark. A header label is
        // never trimmed when its column has room, whichever column carries the mark.
        var list = CreatePopulatedList(bothRows: true);
        var view = new TargetListView { DataContext = list };
        ShowList(view, WidePageAllotment, 1900d);

        foreach (var key in new[] { "name", "integration", "equipment", "last_session" })
        {
            list.SortByCommand.Execute(key);
            Dispatcher.UIThread.RunJobs();

            foreach (var cell in HeaderCells(view).Where(cell => cell.IsEffectivelyVisible))
            {
                var column = (ColumnViewModel)cell.Content!;
                var title = HeaderTitle(cell);
                Assert.False(
                    title.TextLayout.TextLines[0].HasCollapsed,
                    $"Header '{column.Title}' is trimmed while sorting by '{key}', mark '{column.SortGlyph}'.");
            }
        }
    }

    [AvaloniaFact]
    public void View_TheFitWidth_IsAHandWrittenFigure_AndEveryCapItIsBuiltFromReachesACell()
    {
        // Re-pointed by the fixer pass (fixer-list item 4, phase-review P2). The case this
        // replaces asserted NameFloor + DesignationCap + ... == EquipmentFitWidth, which restates
        // that constant's own initializer and cannot fail, and it greped the markup for one of the
        // five caps while its comment claimed it read them all back out. The figure below is
        // written out by hand, and the two bound caps are checked where the markup applies them,
        // on the header cell and the row cell of every column that carries one.
        var source = File.ReadAllText(FindTargetListViewAxaml());

        // 120 Name floor + 160 Designation + 120 Palette + 160 Integration + 300 Equipment
        // + 160 Last Session + 96 sessions + 12 trailing inset.
        Assert.Equal(1128d, TargetListView.DefaultFitWidth);
        Assert.Equal(
            TargetListView.DefaultFitWidth,
            TargetListView.FitWidthFor(CreatePopulatedList().Columns));

        // Applied, not merely declared: three data columns and Equipment, header cell and row cell
        // each, and no literal cap left on a cell.
        Assert.Equal(6, Regex.Matches(source, @"MaxWidth=""\{Binding #Root\.DataCellMaxWidth\}""").Count);
        Assert.Equal(2, Regex.Matches(source, @"MaxWidth=""\{Binding #Root\.EquipmentCellMaxWidth\}""").Count);
        Assert.Equal(3, Regex.Matches(source, @"MaxWidth=""\{Binding #Root\.NameCellMaxWidth\}""").Count);
        Assert.DoesNotMatch(new Regex(@"MaxWidth=""\d"), source);
        Assert.Contains($"MinWidth=\"{TargetListView.NameFloor:0}\"", source);
        Assert.Contains("TableTrailingInset", source);

        // Palette is a budget and not a cap (ruling Q10): it is in the sum and in no cell.
        Assert.Equal(TargetListView.PaletteBudget, TargetListView.BudgetFor("palette"));

        // The rule is only worth having if the shipped window is below it and a wide one above it.
        Assert.True(PageAllotment < TargetListView.DefaultFitWidth);
        Assert.True(WidePageAllotment > TargetListView.DefaultFitWidth);
    }

    [AvaloniaFact]
    public void View_TheFitWidth_IsBuiltFromTheShownColumns_NotAConstantOverAllOfThem()
    {
        // Spec 12.2 says Equipment leaves "below the width at which every shown column fits".
        // Hiding Designation and Integration used to leave it suppressed below the full set's own
        // constant although the row would have fitted in far less (phase-review P2).
        var list = CreatePopulatedList();
        var view = new TargetListView { DataContext = list };
        ShowList(view, 820d);

        Assert.False(view.IsEquipmentShown);

        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "designation"));
        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "integration"));
        Dispatcher.UIThread.RunJobs();
        view.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            TargetListView.DefaultFitWidth - (2 * TargetListView.DataCap),
            TargetListView.FitWidthFor(list.Columns));
        Assert.True(
            view.IsEquipmentShown,
            "Equipment is still suppressed in a 820 pixel list that now shows four columns.");
    }

    [AvaloniaFact]
    public void View_HidingEquipmentInTheGear_DoesNotTightenTheOtherColumns()
    {
        // One flag served two causes (phase-review P2): unticking Equipment at any width also
        // dropped Designation, Integration and Last Session to the tight cap and trimmed their
        // headers. The tight cap follows the width rule and nothing else.
        var list = CreatePopulatedList(bothRows: true);
        var view = new TargetListView { DataContext = list };
        ShowList(view, WidePageAllotment, 1900d);

        Assert.Equal(TargetListView.DataCap, view.DataCellMaxWidth);

        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "equipment"));
        Dispatcher.UIThread.RunJobs();
        view.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsEquipmentShown);
        Assert.Equal(TargetListView.DataCap, view.DataCellMaxWidth);

        foreach (var cell in HeaderCells(view).Where(cell => cell.IsEffectivelyVisible))
        {
            var title = HeaderTitle(cell);
            Assert.False(
                title.TextLayout.TextLines[0].HasCollapsed,
                $"Header '{((ColumnViewModel)cell.Content!).Title}' trimmed because Equipment was unticked.");
        }
    }

    [AvaloniaFact]
    public void View_AtAWideWindow_TheEquipmentCell_HoldsATelescopeAndCameraPair()
    {
        // fixer-list item 5: at 1900 the column trimmed "Esprit 100 / ASI6200MM Pro" against its
        // 180 cap while Name held about 500 pixels of spare width. The cap is raised, and it buys
        // its own room because the fit width is built from it.
        var list = CreatePopulatedList(bothRows: true);
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view, WidePageAllotment, 1900d);

        Assert.True(view.IsEquipmentShown);
        foreach (var cell in NamedCells(view, "EquipmentCell").Cast<TextBlock>())
        {
            Assert.False(
                cell.TextLayout.TextLines[0].HasCollapsed,
                $"Equipment '{cell.Text}' is trimmed at {cell.Bounds.Width} against a {view.EquipmentCellMaxWidth} cap.");
        }

        // Not vacuous: the longer of the two rigs would trim against the cap this replaces.
        var longest = NamedCells(view, "EquipmentCell").Cast<TextBlock>().Max(cell => cell.Bounds.Width);
        Assert.True(longest > 180d, $"The widest equipment cell measures {longest}, so a 180 cap would not have trimmed it.");
    }

    [AvaloniaFact]
    public void View_AtTheListMinimum_TheExpandButtonAndThePageSizeSelect_AreInside()
    {
        // TargetListView.ListMinWidth is the figure DashboardView.axaml.cs bounds the filter panel
        // by (fixer-list item 6), so it has to be the narrowest list that still draws a row whole
        // with Equipment suppressed and Name at its floor, and still draws the pager's own select.
        // It is measured rather than summed, because the caps are maxima and a sum over them
        // overstates what the row takes; this case is what fails if the figure drifts from what the
        // row and the pager actually measure. It fails below the figure: at 40 pixels less the
        // Expand button's trailing edge leaves the viewport.
        var list = CreatePopulatedList();
        list.Rows[0].IsExpanded = false;
        var view = new TargetListView { DataContext = list };
        ShowList(view, TargetListView.ListMinWidth, 1024d);

        Assert.False(view.IsEquipmentShown);
        Assert.True(
            ColumnWidth(view, "TargetListHeaderRow", 0) >= TargetListView.NameFloor - 0.5d,
            $"The Name column is {ColumnWidth(view, "TargetListHeaderRow", 0)} at the list minimum.");

        var toggle = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SessionsToggle");
        Assert.True(
            RightEdge(toggle, view) <= view.Bounds.Width + 0.5d,
            $"The Expand button ends at {RightEdge(toggle, view)} in a {view.Bounds.Width} list.");

        var select = view.GetVisualDescendants().OfType<ComboBox>().First();
        Assert.True(
            RightEdge(select, view) <= view.Bounds.Width + 0.5d,
            $"The page-size select ends at {RightEdge(select, view)} in a {view.Bounds.Width} list.");
    }

    // ---- helpers ------------------------------------------------------------------------------


    private static IReadOnlyList<Grid> RowGrids(TargetListView view)
        => [.. view.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "TargetRowGrid")];

    private static double ColumnWidth(TargetListView view, string gridName, int index)
        => view.GetVisualDescendants().OfType<Grid>()
            .Single(grid => grid.Name == gridName)
            .ColumnDefinitions[index].ActualWidth;

    // Since the fix pass the row grid declares the header grid's own seven columns directly, so a
    // row column is read the same way a header column is.
    private static double InnerColumnWidth(Grid rowGrid, int index)
        => rowGrid.ColumnDefinitions[index].ActualWidth;

    // The alignment rule, in one place so the hidden-column case and the two viewport cases hold
    // the table to it as well. The two right-aligned columns are compared on their trailing edge,
    // which is where a right-aligned label and a right-aligned figure both sit; the rest on their
    // leading edge. Both readings are taken on the header cell's own title TextBlock, which is why
    // the numeric template puts the sort marker before the label rather than after it.
    private static void AssertHeaderAlignsWithRows(TargetListView view, TargetListViewModel list, string? skip = null)
    {
        (string Key, string CellName, bool Numeric)[] columns =
        [
            ("name", "NameCell", false),
            ("designation", "DesignationCell", false),
            ("palette", "PaletteCell", false),
            ("integration", "IntegrationCell", true),
            ("equipment", "EquipmentCell", false),
            ("last_session", "LastSessionCell", true),
        ];

        var rows = RowGrids(view).Count;
        foreach (var (key, cellName, numeric) in columns)
        {
            if (key == skip)
            {
                continue;
            }

            var header = HeaderCell(view, list, key);
            var title = HeaderTitle(header);
            var headerEdge = numeric ? RightEdge(title, view) : LeftEdge(title, view);

            var cells = NamedCells(view, cellName);
            Assert.Equal(rows, cells.Count);
            foreach (var cell in cells)
            {
                // The Name cell is a grid of two runs and carries its gutter on the runs rather
                // than on itself, so its own leading edge is the column's; the run is what lines up
                // with the header label.
                var measured = cell is Grid ? cell.GetVisualDescendants().OfType<TextBlock>().First() : cell;

                // A row cell carries its gutter as Padding, inside its own bounds, exactly as the
                // header cell carries it as the base Button padding; the ink starts after it.
                var gutter = measured switch
                {
                    TextBlock text => text.Padding,
                    ItemsControl items => items.Padding,
                    _ => default,
                };
                var cellEdge = numeric
                    ? RightEdge(measured, view) - gutter.Right
                    : LeftEdge(measured, view) + gutter.Left;
                Assert.True(
                    Math.Abs(headerEdge - cellEdge) < ColumnSettle,
                    $"Column '{key}': header at {headerEdge}, cell at {cellEdge}.");
            }
        }
    }

    private static IReadOnlyList<Control> NamedCells(TargetListView view, string name)
        => [.. view.GetVisualDescendants().OfType<Control>().Where(control => control.Name == name)];

    // A badge is a Border wearing the tag class. Selected by class rather than by "has a border
    // brush": under Border.tag the outline comes from the style, so a template root can carry one
    // too (Phase 14C re-point of the two predicates that used the brush).
    private static IReadOnlyList<Border> Badges(Visual root)
        => [.. root.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("tag"))];

    private static ContentControl HeaderCell(TargetListView view, TargetListViewModel list, string key)
    {
        var column = list.Columns.Single(c => c.Key == key);
        return HeaderCells(view).Single(cell => ReferenceEquals(cell.Content, column));
    }

    // The header cell holds two runs, the label and the sort direction marker; the label is the
    // one that carries the column's own title, whichever side of the marker it sits on.
    private static TextBlock HeaderTitle(ContentControl header)
        => header.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Text == ((ColumnViewModel)header.Content!).Title);

    private static double LeftEdge(Visual control, Visual root)
        => control.TranslatePoint(default, root)!.Value.X;

    private static double RightEdge(Visual control, Visual root)
        => control.TranslatePoint(new Point(control.Bounds.Width, 0), root)!.Value.X;

    // Phase 14B fixer, fixer list item 39 (task6-review P3): the duplicate of
    // TargetListView_EveryTextBlock_RendersAtAReadableSize that used to sit here is gone. It was
    // byte for byte the same case over the same view and the same fixture, added to restate the
    // rule once the Filters cell and the Deep dive button existed; both render inside the same
    // ShowList the original walks, so the original already covers them.

    private static string FindTargetListViewAxaml()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "src/GalactiLog.App/Views/Dashboard/TargetListView.axaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new FileNotFoundException("TargetListView.axaml");
    }
}
