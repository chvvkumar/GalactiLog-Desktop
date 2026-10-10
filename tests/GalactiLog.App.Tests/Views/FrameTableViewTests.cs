using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for the frame table: it parses, lays out and binds against a
// populated view-model, a hidden column takes its header cell with it and leaves the rows aligned,
// and a disabled metric group renders none of its columns. Compiled bindings already turn a
// binding-path typo into a build error; these catch the rest (a missing resource, a template that
// cannot realize, a cell that cannot find its column).
public class FrameTableViewTests
{
    private static readonly FrameRow SampleFrame = FrameTableViewModelTests.Frame(
        filePath: @"D:\Astro\M31\M31_Ha_001.fits",
        fileName: "M31_Ha_001.fits",
        captureDate: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc),
        filterUsed: "Ha",
        exposureTime: 300d,
        medianHfr: 2.35d,
        eccentricity: 0.41d,
        fwhm: 1.88d,
        detectedStars: 1490,
        guidingRmsArcsec: 0.45d,
        guidingRmsSource: "phd2",
        aduMean: 1234d,
        airmass: 1.23d,
        pierSide: "East",
        sensorTemp: -10.4d,
        cameraGain: 100);

    /// <summary>Every group on and every one of the 32 columns in the persisted list, so a test
    /// about layout is not silently also a test about the gate.</summary>
    private static DisplaySettings EveryColumn()
    {
        var display = new DisplaySettings();
        foreach (var key in display.Groups.Keys.ToList())
        {
            display.Groups[key] = display.Groups[key] with { Enabled = true };
        }

        display.Columns[DisplaySettings.FramesTableId] =
            [.. FrameColumns.All.Select(column => column.Key)];

        return display;
    }

    private static FrameTableViewModel CreateTable(
        DisplaySettings? display = null,
        IReadOnlyList<FrameRow>? frames = null,
        List<string>? copies = null,
        List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>? previews = null,
        GeneralSettings? general = null,
        List<DisplaySettings>? saves = null,
        Action<DisplayColumnWriter>? writerSink = null,
        Func<Guid, FrameHeaders?>? getHeaders = null)
    {
        var current = display ?? EveryColumn();
        var writer = new DisplayColumnWriter(() => current, value =>
        {
            current = value;
            saves?.Add(value);
        });
        writerSink?.Invoke(writer);
        return new FrameTableViewModel(
            frames ?? [SampleFrame],
            current,
            writer,
            new ShellIntegration(
                copyText: copies is null
                    ? null
                    : text =>
                    {
                        copies.Add(text);
                        return Task.CompletedTask;
                    },
                start: _ => null),
            openPreview: previews is null
                ? null
                : (rows, index) => previews.Add((rows, index)),
            general ?? new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            // Phase 6 Task 6: raw header rendering is covered by RawHeaderPanelTests; a view test
            // passes its own only to expand a panel inside a row.
            getHeaders: getHeaders ?? (_ => null));
    }

    /// <summary>The frame table no longer bounds its own rows: P12 Task 6 deleted
    /// <c>FrameRowsViewportHeight</c> and the row scroller, because Task 5's session pane gives
    /// the table a starred row and that is what the viewport is now. The host here does the same
    /// thing, which is what the virtualisation case needs to have a viewport at all.</summary>
    private static Window ShowTable(FrameTableView view)
    {
        var host = new Grid { RowDefinitions = new RowDefinitions("*") };
        host.Children.Add(view);

        var window = new Window { Width = 1280, Height = 800, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void FrameTableView_Constructs_AndRendersAPopulatedViewModel()
    {
        var view = new FrameTableView { DataContext = CreateTable() };
        var window = ShowTable(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("21:05", texts);
        Assert.Contains("M31_Ha_001.fits", texts);
        Assert.Contains("Ha", texts);
        Assert.Contains("2.35", texts);
        Assert.Contains("1,490", texts);
        Assert.Contains("100", texts);

        // The header carries the configured zone's GMT offset (fixer-list item 20, re-pointed
        // from the bare "UTC" id), and the guiding RMS source marker is the dagger, U+2020 (spec
        // 12.4, Phase 15A), never an emoji or a private-use glyph. SampleFrame's source is phd2,
        // so the dagger renders; this is the render-time proof that the font tables' claim of
        // ruling F4 holds in the actual control tree and not only on paper.
        Assert.Contains("Time (GMT+00:00)", texts);
        Assert.Contains("†", texts);

        // The spine's census at 1280x800 and at the x-large root size. The frame table has no
        // TableRow (ruling R1), so its row clauses apply by class: no figure trimmed or blank,
        // every dash faint, the rows' scroller never auto-hiding.
        TableAssert.Conventions(view);
        window.FontSize = 20d;
        Dispatcher.UIThread.RunJobs();
        TableAssert.Conventions(view);
    }

    [AvaloniaFact]
    public void FrameTableView_GuidingRmsDagger_SitsWithNoSpaceAndDigitsShareOneRightEdge()
    {
        // Review P2-1: spec 12.4's dagger sits "with no space before it", and a marked row's
        // digits stay in line with an unmarked row's. The value fills the cell with the gutter
        // padding every number has, so its digits end at Width - Gutter, and the mark sits in the
        // right gutter, starting exactly where the digits end.
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(
                fileName: "csv.fits",
                guidingRmsArcsec: 0.41d,
                guidingRmsSource: "csv"),
            FrameTableViewModelTests.Frame(
                fileName: "phd2.fits",
                guidingRmsArcsec: 0.52d,
                guidingRmsSource: "phd2"),
        };
        var view = new FrameTableView { DataContext = CreateTable(frames: frames) };
        var window = ShowTable(view);

        var rows = RowBorders(view);
        Assert.Equal(2, rows.Count);

        var csvValue = rows[0].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuidingRmsValueCell");
        var csvMark = rows[0].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuidingRmsMarkCell");
        var phd2Value = rows[1].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuidingRmsValueCell");
        var phd2Mark = rows[1].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuidingRmsMarkCell");

        Assert.False(csvMark.IsVisible);
        Assert.True(phd2Mark.IsVisible);

        // Both values' content right edges land at the same x translated to the view, whether
        // or not the row is marked: the mark overlays the gutter and takes no width from the value.
        static double ContentRight(TextBlock value, Visual view)
            => value.TranslatePoint(new Point(value.Bounds.Width - value.Padding.Right, 0), view)!.Value.X;
        var csvValueRight = ContentRight(csvValue, view);
        var phd2ValueRight = ContentRight(phd2Value, view);
        Assert.Equal(csvValueRight, phd2ValueRight, 1);

        // No space before the dagger: the mark's left edge is exactly the digits' right edge.
        var markLeft = phd2Mark.TranslatePoint(new Point(0, 0), view)!.Value.X;
        Assert.Equal(phd2ValueRight, markLeft, 1);

        // The dagger starts in the column's right gutter and, at the default and the x-large root
        // size alike, ends before the next column's text starts: it may draw on into that
        // column's left gutter (two gutters of air between the digits and the next figure), never
        // onto its figure.
        foreach (var rootSize in new[] { 14d, 20d })
        {
            window.FontSize = rootSize;
            Dispatcher.UIThread.RunJobs();
            var next = (TextBlock)RowCellsAt(view, 1)[9];
            var daggerRight = phd2Mark.TranslatePoint(new Point(MeasuredWidth(phd2Mark, phd2Mark.Text!), 0), view)!.Value.X;
            var nextTextLeft = next.TranslatePoint(new Point(next.Padding.Left, 0), view)!.Value.X;
            Assert.True(daggerRight <= nextTextLeft,
                $"at {rootSize}: the dagger ends at {daggerRight:F1}, the next column's text starts at {nextTextLeft:F1}");
        }
    }

    [AvaloniaFact]
    public void FrameTableView_GuidingRmsValueCell_AccessibleNameCarriesTheFigure()
    {
        // Review P2-3: AutomationProperties.Name used to be bound straight to the sentence, so a
        // phd2 row's accessible name replaced the figure instead of carrying it beside the
        // sentence. A csv row's name must stay the figure alone (no override, so the cell falls
        // back to its own Text), and the decorative dagger must stay out of the accessibility
        // tree entirely.
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(
                fileName: "csv.fits",
                guidingRmsArcsec: 0.41d,
                guidingRmsSource: "csv"),
            FrameTableViewModelTests.Frame(
                fileName: "phd2.fits",
                guidingRmsArcsec: 0.52d,
                guidingRmsSource: "phd2"),
        };
        var view = new FrameTableView { DataContext = CreateTable(frames: frames) };
        ShowTable(view);

        var rows = RowBorders(view);
        var csvValue = rows[0].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuidingRmsValueCell");
        var csvMark = rows[0].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuidingRmsMarkCell");
        var phd2Value = rows[1].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuidingRmsValueCell");
        var phd2Mark = rows[1].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "GuidingRmsMarkCell");

        Assert.Null(AutomationProperties.GetName(csvValue));
        Assert.Equal("0.41", csvValue.Text);

        Assert.Equal("0.52 arcseconds, from a PHD2 guide log", AutomationProperties.GetName(phd2Value));
        Assert.Equal("0.52", phd2Value.Text);

        // The dagger stays decorative: Raw takes it out of the accessibility tree, so a screen
        // reader reaches only the value cell's name above.
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(phd2Mark));
        Assert.Null(AutomationProperties.GetName(csvMark));
    }

    // FIXER LIST F12. The verification agent measured a 605 ms median UI-thread stall expanding a
    // 200 frame session, because every row of 32 cells was realised at once. The rows now sit in a
    // VirtualizingStackPanel inside a bounded ScrollViewer, so only the viewport's rows exist.
    [AvaloniaFact]
    public void FrameTableView_A200RowTable_RealisesFarFewerThan200Rows()
    {
        var frames = Enumerable.Range(0, 200)
            .Select(i => FrameTableViewModelTests.Frame(
                fileName: $"frame_{i:000}.fits",
                captureDate: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc).AddSeconds(i * 300),
                medianHfr: 2d + (i % 10) * 0.01d))
            .ToList();
        var table = CreateTable(frames: frames);
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        Assert.Equal(200, table.Rows.Count);

        var realised = RowBorders(view).Count;
        Assert.InRange(realised, 1, 60);

        // The rows that do exist are real rows, bound in order from the top of the list.
        var texts = VisibleCellTexts(view);
        Assert.Contains("frame_000.fits", texts);
        Assert.DoesNotContain("frame_199.fits", texts);
    }

    [AvaloniaFact]
    public void FrameTableView_HidingAColumn_RemovesItsHeaderCellAndKeepsTheRowsAligned()
    {
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        // P12 Task 6: the unit lives in the header now, in the inch-mark style.
        Assert.Contains(TableHeads.Fwhm, VisibleHeaderTitles(view));
        Assert.Contains("1.88", VisibleCellTexts(view));

        var fwhm = table.Columns.Single(column => column.Key == "fwhm");
        var fwhmCell = HeaderCell(view, fwhm);
        var starsCell = HeaderCell(view, table.Columns.Single(column => column.Key == "detected_stars"));
        var cellWidth = fwhmCell.Bounds.Width;
        var starsXBefore = starsCell.Bounds.X;
        Assert.True(cellWidth > 0);

        table.ToggleColumnCommand.Execute(fwhm);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(TableHeads.Fwhm, VisibleHeaderTitles(view));
        Assert.DoesNotContain("1.88", VisibleCellTexts(view));

        // Phase 5 review item 2, ported: the whole header cell collapses, not just the button
        // inside it, so every column to its right moves left by exactly the hidden width and the
        // header stays in step with the rows.
        Assert.False(fwhmCell.IsVisible);
        Assert.Equal(starsXBefore - cellWidth, starsCell.Bounds.X);
    }

    [AvaloniaFact]
    public void FrameTableView_OnAMultiRigNight_TheHeaderStaysInStepWithTheRows()
    {
        // Review P2-2. The single-rig fixture above collapses the Rig header, so nothing there can
        // see a header cell that occupies more than its own column. This one draws it.
        var table = CreateTable(frames:
        [
            SampleFrame with { Rig = "Alpha / Cam" },
            SampleFrame with { Rig = "Bravo / Cam" },
        ]);
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        Assert.True(table.IsMultiRig);

        var header = view.GetControl<TextBlock>("RigColumnHeader");
        Assert.True(header.IsEffectivelyVisible);

        // The header occupies exactly what the row cell does. A horizontal Margin here would make
        // it 102 against the cell's 90 and push every header after it out of its own column.
        var cell = ((StackPanel)RowBorders(view)[0].Child!).Children
            .OfType<Control>()
            .Single(child => child.Name == "RigCell");

        Assert.Equal(cell.Bounds.Width, header.Bounds.Width);
        Assert.Equal(cell.Bounds.X, header.Bounds.X, 1);

        // And the first column after Rig still starts where its own row cell does, which is the
        // property the alignment case above protects for the other 31.
        var fileNameHeader = HeaderCell(view, table.Columns.Single(column => column.Key == "file_name"));
        var fileNameCell = RowCells(view)[1];
        Assert.Equal(fileNameCell.Bounds.X, fileNameHeader.Bounds.X, 1);
    }

    [AvaloniaFact]
    public void FrameTableView_TheRigCell_SitsAfterTime()
    {
        // Spec 12.4 item 3 places the Rig column after Time. RowCellsAt excludes it by name, so
        // this is the one case that pins where it actually sits in the row.
        var view = new FrameTableView
        {
            DataContext = CreateTable(frames:
            [
                SampleFrame with { Rig = "Alpha / Cam" },
                SampleFrame with { Rig = "Bravo / Cam" },
            ]),
        };
        ShowTable(view);

        var visible = ((StackPanel)RowBorders(view)[0].Child!).Children
            .OfType<Control>()
            .Where(child => child is not Button && child.IsEffectivelyVisible)
            .ToList();

        Assert.Equal("RigCell", visible[1].Name);
        Assert.Equal("Alpha / Cam", ((TextBlock)visible[1]).Text);
    }

    [AvaloniaFact]
    public void FrameTableView_DisabledGroup_RendersNoneOfItsColumns()
    {
        // The roadmap Verify line, at the view: the weather columns are all in the persisted list
        // and the group is off, so not one of them renders.
        var display = EveryColumn();
        display.Groups["weather"] = display.Groups["weather"] with { Enabled = false };

        var view = new FrameTableView { DataContext = CreateTable(display) };
        ShowTable(view);

        var titles = VisibleHeaderTitles(view);
        foreach (var column in FrameColumns.All.Where(column => column.Group == "weather"))
        {
            Assert.DoesNotContain(column.Title, titles);
        }

        // The rest of the table is untouched, including the other gated-by-default groups that
        // this document turned on.
        Assert.Contains("Airmass", titles);
        Assert.Contains("ADU mean", titles);
    }

    [AvaloniaFact]
    public void FrameTableView_EveryTextBlock_RendersAtAReadableSize()
    {
        // Scales.axaml's FontSize* keys are ratios, not point sizes: binding one to FontSize
        // renders sub-pixel text. Nothing in this view sets FontSize, and this is what fails if
        // someone binds one of those keys.
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        var blocks = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }

    [AvaloniaFact]
    public void FrameTableView_NumericCells_CarryTheTabularFigureClass()
    {
        // Review finding 3: FrameColumn.IsNumeric had no consumer, and the spine's kind class is
        // assigned by hand in the markup. This is the join, so the two cannot drift: every
        // numeric column's cell carries tc-num (tabular figures, right alignment, never trimmed:
        // spec.md item 3) and every textual column's carries tc-text.
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        var cells = RowCells(view);
        Assert.Equal(32, cells.Count);

        for (var index = 0; index < cells.Count; index++)
        {
            var column = FrameColumns.All[index];

            // The guiding RMS cell is a panel holding the value and the source marker; every
            // other cell is the TextBlock itself.
            var text = cells[index] as TextBlock
                ?? cells[index].GetVisualDescendants().OfType<TextBlock>().First();

            Assert.Contains("cell", text.Classes);
            Assert.Equal(column.IsNumeric, text.Classes.Contains("tc-num"));
            Assert.Equal(!column.IsNumeric, text.Classes.Contains("tc-text"));

            if (column.IsNumeric)
            {
                Assert.Equal(TextAlignment.Right, text.TextAlignment);
                Assert.Equal(TextTrimming.None, text.TextTrimming);
            }
        }
    }

    [AvaloniaFact]
    public void FrameTableView_RowHover_UsesTheHoverToken()
    {
        // Review finding 2: the row Border used to set Background="Transparent" locally, and a
        // local value outranks a style trigger, so the spec 14.5 hover fill never rendered. Both
        // fills are style setters now, and this is what fails if either goes back to an attribute.
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        var row = RowBorder(view);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)row.Background!).Color);

        ((IPseudoClasses)row.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.TryFindResource("ColorBgHover", out var token));
        Assert.Equal(
            ((ISolidColorBrush)token!).Color,
            ((ISolidColorBrush)row.Background!).Color);
    }

    [AvaloniaFact]
    public void FrameTableView_TemplatedCommandBindings_Resolve()
    {
        // The header cell, the row and both flyouts are DataTemplates whose commands come from the
        // view's own DataContext through #Root, which compiled bindings cannot check: a typo there
        // is a silently dead button, not a build error.
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var headerButtons = HeaderCells(view)
            .SelectMany(cell => cell.GetVisualDescendants().OfType<Button>())
            .ToList();

        Assert.Equal(32, headerButtons.Count);
        Assert.All(headerButtons, button =>
        {
            Assert.NotNull(button.Command);
            Assert.IsType<string>(button.CommandParameter);
        });

        // The row-end action menu. Opened here on purpose: its five items live in a popup, so a
        // dead command binding inside it is invisible until something shows it.
        var actionButton = Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            button => button.Flyout is not null && button.Content as string == "...");
        actionButton.Flyout!.ShowAt(actionButton);
        Dispatcher.UIThread.RunJobs();

        var actionItems = OpenFlyoutButtons(actionButton);
        Assert.Equal(5, actionItems.Count);
        Assert.All(actionItems, button =>
        {
            Assert.NotNull(button.Command);
            Assert.IsType<FrameRowViewModel>(button.CommandParameter);
        });

        // Open preview is spec 11.5's modal and has no delegate in Phase 6, so its item is the
        // one that renders disabled.
        Assert.False(actionItems.Single(button => button.Content as string == "Open preview").IsEffectivelyEnabled);
        actionButton.Flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        // The column picker, the same way.
        var picker = view.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "FrameColumnPicker");
        picker.Flyout!.ShowAt(picker);
        Dispatcher.UIThread.RunJobs();

        var checkBoxes = FlyoutContent(picker).GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(32, checkBoxes.Count);
        Assert.All(checkBoxes, box =>
        {
            Assert.NotNull(box.Command);
            Assert.IsType<ColumnViewModel>(box.CommandParameter);
        });

        picker.Flyout.Hide();
        Dispatcher.UIThread.RunJobs();
    }

    // ---- P12 Task 6: alignment, ink, hairlines, the viewport and the selection ---------------

    [AvaloniaFact]
    public void FrameTableView_ANumericHeaderCell_ReportsRightAlignment()
    {
        // design-spec 14.4: a numeric column's header aligns over its figures. Before the
        // IsNumeric flag reached the view the header template hardcoded Left for all 32 columns
        // while the rows right-aligned 26 of them (panel-density.md section 2, item 5).
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        foreach (var key in new[] { "median_hfr", "exposure_time", "camera_gain" })
        {
            var column = table.Columns.Single(entry => entry.Key == key);
            Assert.True(column.IsNumeric);
            Assert.Equal(HorizontalAlignment.Right, HeaderButton(view, column).HorizontalContentAlignment);
        }
    }

    [AvaloniaFact]
    public void FrameTableView_ATextHeaderCell_ReportsLeftAlignment()
    {
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        foreach (var key in new[] { "time", "file_name", "filter_used", "pier_side" })
        {
            var column = table.Columns.Single(entry => entry.Key == key);
            Assert.False(column.IsNumeric);
            Assert.Equal(HorizontalAlignment.Left, HeaderButton(view, column).HorizontalContentAlignment);
        }
    }

    [AvaloniaFact]
    public void FrameTableView_InARegionNarrowerThanItsColumns_KeepsTheRowScrollBarInsideTheRegion()
    {
        var frames = Enumerable.Range(0, 200)
            .Select(i => FrameTableViewModelTests.Frame(fileName: $"frame_{i:000}.fits"))
            .ToList();
        var view = new FrameTableView { DataContext = CreateTable(frames: frames) };
        var host = new Grid { Width = 600, Height = 400, RowDefinitions = new RowDefinitions("*") };
        host.Children.Add(view);
        var window = new Window { Width = 1280, Height = 800, Content = new Grid { Children = { host } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bar = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .First(scroll => scroll.Orientation == Avalonia.Layout.Orientation.Vertical && scroll.IsEffectivelyVisible);
        var right = bar.TranslatePoint(new Point(bar.Bounds.Width, 0), host)!.Value.X;
        Assert.True(
            right <= host.Bounds.Width + 0.5d,
            $"the vertical scroll bar's right edge ({right:F1}) is outside its {host.Bounds.Width:F1} px region");
    }

    private static (FrameTableView View, ScrollViewer Rows, ScrollViewer Header, FrameTableViewModel Table) ShowNarrow()
    {
        var frames = Enumerable.Range(0, 200)
            .Select(i => FrameTableViewModelTests.Frame(fileName: $"frame_{i:000}.fits"))
            .ToList();
        var table = CreateTable(frames: frames);
        var view = new FrameTableView { DataContext = table };
        var host = new Grid { Width = 600, Height = 400, RowDefinitions = new RowDefinitions("*") };
        host.Children.Add(view);
        new Window { Width = 1280, Height = 800, Content = new Grid { Children = { host } } }.Show();
        Dispatcher.UIThread.RunJobs();
        var rows = view.GetControl<ListBox>("FrameRows").GetVisualDescendants().OfType<ScrollViewer>().First();
        return (view, rows, view.GetControl<ScrollViewer>("FrameHeaderScroller"), table);
    }

    [AvaloniaFact]
    public void FrameTableView_ScrollingARowIntoView_KeepsTheSidewaysPan()
    {
        var (_, rows, _, table) = ShowNarrow();
        rows.Offset = new Vector(200, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(200d, rows.Offset.X);

        table.HighlightFrameAt(150);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200d, rows.Offset.X);
    }

    [AvaloniaFact]
    public void FrameTableView_FocusingARow_KeepsTheSidewaysPan()
    {
        var (view, rows, _, _) = ShowNarrow();
        rows.Offset = new Vector(200, 0);
        Dispatcher.UIThread.RunJobs();

        var list = view.GetControl<ListBox>("FrameRows");
        ((ListBoxItem)list.ContainerFromIndex(1)!).Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200d, rows.Offset.X);
    }

    [AvaloniaFact]
    public void FrameTableView_TheHeaderBandAndTheRows_KeepOneSidewaysOffsetBothWays()
    {
        var (_, rows, header, _) = ShowNarrow();

        rows.Offset = new Vector(150, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(150d, header.Offset.X);

        header.Offset = new Vector(80, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(80d, rows.Offset.X);
    }

    [AvaloniaFact]
    public void FrameTableView_ANumericHeaderTitle_EndsWhereItsColumnsValuesEnd()
    {
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var timeTitle = HeaderTitle(HeaderCell(view, table.Columns.Single(entry => entry.Key == "time")));
        Assert.NotEqual(TextAlignment.Right, timeTitle.TextAlignment);

        // Twice: the second pass with HFR as the sort key, whose glyph leads the title rather
        // than sitting between it and the column's right edge.
        foreach (var sorted in new[] { false, true })
        {
            if (sorted)
            {
                table.SortByCommand.Execute("median_hfr");
                Dispatcher.UIThread.RunJobs();
            }

            foreach (var (key, index) in new[] { ("exposure_time", 3), ("median_hfr", 4) })
            {
                var column = table.Columns.Single(entry => entry.Key == key);
                var title = HeaderTitle(HeaderCell(view, column));
                var value = RowCells(view)[index];
                var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), view)!.Value.X;
                Assert.Equal(TextAlignment.Right, title.TextAlignment);
                var valueRight = value.TranslatePoint(new Point(value.Bounds.Width, 0), view)!.Value.X - ((TextBlock)value).Padding.Right;
                Assert.True(
                    Math.Abs(titleRight - valueRight) < 1d,
                    $"{key} (sorted {sorted}): header right edge {titleRight:F1} against its values' {valueRight:F1}");
            }
        }

        Assert.NotEqual("", table.Columns.Single(entry => entry.Key == "median_hfr").SortGlyph);
    }

    // Red if a header title's left inset differs from its row text's, so the two sit apart in a column.
    [AvaloniaFact]
    public void FrameTableView_ATextHeaderTitle_StartsWhereItsRowTextStarts()
    {
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        foreach (var (key, index) in new[] { ("time", 0), ("file_name", 1), ("filter_used", 2) })
        {
            var title = HeaderTitle(HeaderCell(view, table.Columns.Single(entry => entry.Key == key)));
            var cell = (TextBlock)RowCells(view)[index];
            var titleLeft = title.TranslatePoint(new Point(0, 0), view)!.Value.X;
            var textLeft = cell.TranslatePoint(new Point(cell.Padding.Left, 0), view)!.Value.X;
            Assert.True(
                Math.Abs(titleLeft - textLeft) < 1d,
                $"{key}: header text starts at {titleLeft:F1}, row text at {textLeft:F1}");
        }
    }

    [AvaloniaFact]
    public void FrameTableView_TheTimeHeadersTitle_EndsInsideItsSeventyEightPixelCell()
    {
        // Phase 14C fixer-list item 20 / phase-review's carried observation: the title TextBlock
        // used to sit in a horizontal StackPanel, whose MeasureOverride offers each child
        // infinite width in the stacking direction regardless of the panel's own available
        // width, so TextTrimming never engaged and the label painted over the File name header
        // beside it (pre-existing since Phase 6, confirmed by the Task 5 review). Constraining
        // the title in a Grid star column, the shape TargetHeaderCell already uses in
        // Views/Dashboard/TargetListView.axaml, is what makes the rendered title end inside its
        // own 78 px cell instead of overrunning it.
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var timeColumn = table.Columns.Single(column => column.Key == "time");
        var cell = HeaderCell(view, timeColumn);
        var title = HeaderTitle(cell);

        Assert.True(cell.Bounds.Width > 0);
        var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), cell)!.Value.X;
        Assert.True(
            titleRight <= cell.Bounds.Width + 0.5d,
            $"the title's right edge ({titleRight:F1}) overran its own {cell.Bounds.Width:F1} px cell");
    }

    [AvaloniaFact]
    public void FrameTableView_TheFileNameCell_UsesTheMonoFamily()
    {
        // A file name is what the user copies and reads, so it takes the mono family and the
        // primary ink rather than the secondary ink every other text cell carries.
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        var fileName = (TextBlock)RowCells(view)[1];
        Assert.True(view.TryFindResource("FontMono", out var mono));
        Assert.Equal(mono, fileName.FontFamily);

        Assert.True(view.TryFindResource("ColorTextPrimary", out var primary));
        Assert.Equal(
            ((ISolidColorBrush)primary!).Color,
            ((ISolidColorBrush)fileName.Foreground!).Color);
    }

    [AvaloniaFact]
    public void FrameTableView_AnOutlierRow_RendersItsHfrCellInTheWorseInk()
    {
        // The offending cell takes the worse ink, not the row: colour is spent only on data, so
        // the eye is sent to the number rather than to the whole line (panel-density.md 5).
        var frame = FrameTableViewModelTests.Frame(
            fileName: "outlier.fits",
            medianHfr: 3.4d,
            eccentricity: 0.41d,
            fwhm: 1.9d,
            isHfrOutlier: true);

        var view = new FrameTableView { DataContext = CreateTable(frames: [frame]) };
        ShowTable(view);

        Assert.True(view.TryFindResource("ColorMetricWorst", out var worse));
        var worseColor = ((ISolidColorBrush)worse!).Color;

        var cells = RowCells(view);
        Assert.Equal(worseColor, ((ISolidColorBrush)((TextBlock)cells[4]).Foreground!).Color);

        // The row's other quality cells stay in the ordinary ink.
        Assert.NotEqual(worseColor, ((ISolidColorBrush)((TextBlock)cells[5]).Foreground!).Color);
        Assert.NotEqual(worseColor, ((ISolidColorBrush)((TextBlock)cells[6]).Foreground!).Color);
    }

    [AvaloniaFact]
    public void FrameTableView_ANonOutlierRow_RendersEveryCellInTheOrdinaryInk()
    {
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        Assert.True(view.TryFindResource("ColorMetricWorst", out var worse));
        var worseColor = ((ISolidColorBrush)worse!).Color;

        Assert.True(view.TryFindResource("ColorTextPrimary", out var primary));
        var primaryColor = ((ISolidColorBrush)primary!).Color;

        var cells = RowCells(view);
        Assert.All(
            cells.OfType<TextBlock>(),
            cell => Assert.NotEqual(worseColor, ((ISolidColorBrush)cell.Foreground!).Color));

        // The four quality-group cells are the ones the inputs' type table puts in the primary
        // ink; every other numeric cell stays secondary.
        foreach (var index in new[] { 4, 5, 6, 7 })
        {
            Assert.Equal(primaryColor, ((ISolidColorBrush)((TextBlock)cells[index]).Foreground!).Color);
        }

        Assert.NotEqual(primaryColor, ((ISolidColorBrush)((TextBlock)cells[27]).Foreground!).Color);
    }

    [AvaloniaFact]
    public void FrameTableView_RowsCarryNoHairline()
    {
        // The comp's frame table separates rows by rhythm, not by a rule per row: 158 hairlines
        // is 158 lines of ink the reader has to look past.
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        var row = RowBorder(view);
        Assert.Equal(default, row.BorderThickness);
        Assert.Equal(28d, row.MinHeight);
    }

    [AvaloniaFact]
    public void FrameTableView_HasNoFixedViewportHeight()
    {
        // FrameRowsViewportHeight and the row scroller are gone: the host's starred row bounds
        // the viewport now. Anything that re-adds a MaxHeight here fails this.
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        Assert.False(view.TryFindResource("FrameRowsViewportHeight", out _));

        var rows = view.GetControl<ItemsControl>("FrameRows");
        Assert.True(double.IsInfinity(rows.MaxHeight));
        Assert.True(double.IsNaN(rows.Height));

        Assert.All(
            view.GetVisualDescendants().OfType<ScrollViewer>(),
            scroller => Assert.True(double.IsInfinity(scroller.MaxHeight)));
    }

    [AvaloniaFact]
    public void FrameTableView_SelectingTwoRows_EnablesCopyAndDisablesReveal()
    {
        // The ListBox's selection and the view-model's SelectedRows are one collection, driven
        // from either end: the control fills it, and the night strip's SelectFrameAt sets it.
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(fileName: "one.fits", filePath: @"D:\Astro\one.fits"),
            FrameTableViewModelTests.Frame(fileName: "two.fits", filePath: @"D:\Astro\two.fits"),
        };
        var table = CreateTable(frames: frames);
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var list = view.GetControl<ListBox>("FrameRows");
        var copy = view.GetControl<Button>("CopySelectedPathsButton");
        var reveal = view.GetControl<Button>("RevealSelectedButton");

        Assert.False(copy.IsEffectivelyEnabled);
        Assert.False(reveal.IsEffectivelyEnabled);

        Assert.DoesNotContain(LitEdges(view), edge => edge.IsEffectivelyVisible);

        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Single(table.SelectedRows);
        Assert.True(copy.IsEffectivelyEnabled);
        Assert.True(reveal.IsEffectivelyEnabled);

        // Selection is light, not colour (P12 direction): the lit field and the 2 px lit edge,
        // and no accent hue anywhere else on the row.
        Assert.Single(LitEdges(view), edge => edge.IsEffectivelyVisible);

        var container = (ListBoxItem)list.ContainerFromIndex(0)!;
        Assert.True(container.IsSelected);
        Assert.True(view.TryFindResource("ColorBgElevated", out var lit));
        Assert.Equal(
            ((ISolidColorBrush)lit!).Color,
            ((ISolidColorBrush)container.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .First()
                .Background!).Color);

        list.SelectAll();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, table.SelectedRows.Count);
        Assert.Equal("Copy paths (2)", copy.Content);
        Assert.True(copy.IsEffectivelyEnabled);
        Assert.False(reveal.IsEffectivelyEnabled);
        Assert.Equal(2, LitEdges(view).Count(edge => edge.IsEffectivelyVisible));

        // The other direction: the strip names a capture index and the control follows.
        table.SelectFrameAt(1);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, list.SelectedIndex);
        Assert.Single(table.SelectedRows);
    }

    [AvaloniaFact]
    public void FrameTableView_TheColumnPickerIsInTheToolbar()
    {
        // The picker used to sit at the far right of a 2600 px header row, inside the horizontal
        // scroller, where nobody found it. The toolbar does not scroll horizontally.
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var toolbar = view.GetControl<Grid>("FrameTableToolbar");
        var picker = view.GetControl<Button>("FrameColumnPicker");
        var header = view.GetControl<StackPanel>("FrameTableHeader");

        Assert.Contains(picker, toolbar.GetVisualDescendants().OfType<Button>());
        Assert.DoesNotContain(picker, header.GetVisualDescendants().OfType<Button>());

        Assert.Contains(
            view.GetControl<Button>("CopySelectedPathsButton"),
            toolbar.GetVisualDescendants().OfType<Button>());
        Assert.Contains(
            view.GetControl<Button>("RevealSelectedButton"),
            toolbar.GetVisualDescendants().OfType<Button>());
        Assert.Equal(table.ShownCountText, view.GetControl<TextBlock>("ShownCount").Text);
    }

    // ---- fix pass: Escape's enablement, the selection through a re-projection, the outlier ink --

    [AvaloniaFact]
    public void FrameTableView_EscapeWithNothingToClear_IsNotHandledByTheTable()
    {
        // Review P2-1 and ruling (b). Ruling Q14 orders the table's two Escape actions; it does
        // not give the table the key when it has neither to give. A KeyBinding whose command
        // reports it cannot execute leaves the event unhandled, so the press bubbles out to
        // TargetDetailView's drawer-then-back handler. Without the enablement the frame table
        // swallows the page's Escape from the moment the user clicks a row.
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(fileName: "clean.fits"),
            FrameTableViewModelTests.Frame(fileName: "flagged.fits", medianHfr: 3.4d, isHfrOutlier: true),
        };
        var table = CreateTable(frames: frames);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        // Every Escape that reaches the page still unhandled, which is exactly the set the page's
        // own drawer-then-back handler would act on. Counting unhandled presses rather than all
        // presses on purpose: a press the table acts on may also re-project the rows and detach
        // the focused container mid-route, so whether the window sees that press at all is an
        // artifact, while whether it sees it unhandled is the contract.
        var unhandledAtThePage = 0;
        window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Escape && !e.Handled)
                {
                    unhandledAtThePage++;
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        var list = view.GetControl<ListBox>("FrameRows");
        FocusARow(view, list);

        Assert.False(table.ClearFilterOrSelectionCommand.CanExecute(null));

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, unhandledAtThePage);

        // With a selection the table does take it, and the page sees the press already handled.
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.True(table.ClearFilterOrSelectionCommand.CanExecute(null));

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(table.SelectedRows);
        Assert.Equal(1, unhandledAtThePage);

        // And with a filter but no selection, the same: the filter is what it clears first.
        // Re-focused deliberately, because applying a filter rebuilds Rows in place and with it
        // the row containers, so the container that had focus no longer exists and focus falls
        // back to the window.
        table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Dispatcher.UIThread.RunJobs();
        FocusARow(view, list);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(FrameOutlierFilter.None, table.OutlierFilter);
        Assert.Equal(1, unhandledAtThePage);
    }

    [AvaloniaFact]
    public void FrameTableView_CtrlC_CopiesTheSelectionAndIsNotHandledWithoutOne()
    {
        // Ruling Q11's other key, and it has the same shape as Escape: the command reports it
        // cannot execute at the moment the view is attached, so it is handled in OnKeyDown rather
        // than by a KeyBinding, which would have cached that answer and never fired.
        var copies = new List<string>();
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(fileName: "one.fits", filePath: @"D:\Astro\one.fits"),
            FrameTableViewModelTests.Frame(fileName: "two.fits", filePath: @"D:\Astro\two.fits"),
        };
        var table = CreateTable(frames: frames, copies: copies);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var list = view.GetControl<ListBox>("FrameRows");
        FocusARow(view, list);

        // Nothing selected: the table does not own the key and copies nothing.
        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(copies);

        list.SelectAll();
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        var copied = Assert.Single(copies);
        Assert.Equal(
            @"D:\Astro\one.fits" + Environment.NewLine + @"D:\Astro\two.fits" + Environment.NewLine,
            copied);
    }

    [AvaloniaFact]
    public void FrameTableView_SortingAndFilteringALiveSelection_KeepsTheSurvivorsSelected()
    {
        // Review P2-2. Project rebuilds Rows in place, and with a ListBox attached that Clear
        // empties SelectedRows behind the code's back, because SelectedItems and SelectedRows are
        // one object. The snapshot-and-restore in Project exists for exactly this configuration,
        // and a view-model test with no ListBox attached can never take that branch.
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(fileName: "clean.fits", medianHfr: 2.0d),
            FrameTableViewModelTests.Frame(fileName: "hfr_low.fits", medianHfr: 2.8d, isHfrOutlier: true),
            FrameTableViewModelTests.Frame(fileName: "hfr_high.fits", medianHfr: 3.4d, isHfrOutlier: true),
        };
        var table = CreateTable(frames: frames);
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var list = view.GetControl<ListBox>("FrameRows");
        list.SelectAll();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, table.SelectedRows.Count);

        // A sort re-projects with every row still a survivor: nothing may be lost.
        table.SortByCommand.Execute("median_hfr");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["clean.fits", "hfr_low.fits", "hfr_high.fits"],
            table.SelectedRows.Select(row => row.FileName));

        // The filter re-projects and drops one: the two survivors stay selected, in table order,
        // and the containers agree with the collection.
        table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["hfr_high.fits", "hfr_low.fits"],
            table.Rows.Select(row => row.FileName));
        Assert.Equal(
            ["hfr_high.fits", "hfr_low.fits"],
            table.SelectedRows.Select(row => row.FileName));

        Assert.True(((ListBoxItem)list.ContainerFromIndex(0)!).IsSelected);
        Assert.True(((ListBoxItem)list.ContainerFromIndex(1)!).IsSelected);
        Assert.Equal(2, LitEdges(view).Count(edge => edge.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public void FrameTableView_AnOutlierRow_LiftsItsWholeLineToThePrimaryInk()
    {
        // Review ruling (a): the comp's .tbl.fr .row.out .c lifts every cell of a flagged row to
        // the primary ink and reserves the worse ink for the offending cell. The row comes
        // forward by weight of ink, not by a fill and not by a colour.
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(
                fileName: "flagged.fits", filterUsed: "L", medianHfr: 3.4d, eccentricity: 0.41d,
                airmass: 1.23d, isHfrOutlier: true, captureDate: SampleFrame.CaptureDate),
            FrameTableViewModelTests.Frame(
                fileName: "clean.fits", filterUsed: "L", medianHfr: 2.0d, eccentricity: 0.30d,
                airmass: 1.24d, captureDate: SampleFrame.CaptureDate),
        };
        var view = new FrameTableView { DataContext = CreateTable(frames: frames) };
        ShowTable(view);

        Assert.True(view.TryFindResource("ColorTextPrimary", out var primary));
        Assert.True(view.TryFindResource("ColorTextSecondary", out var secondary));
        Assert.True(view.TryFindResource("ColorMetricWorst", out var worse));
        var primaryColor = ((ISolidColorBrush)primary!).Color;
        var secondaryColor = ((ISolidColorBrush)secondary!).Color;
        var worseColor = ((ISolidColorBrush)worse!).Color;

        var flagged = RowCellsAt(view, 0);
        var clean = RowCellsAt(view, 1);

        // The flagged row: a cell that is neither a quality cell nor the offending one still
        // reads primary, where the clean row's same cell reads secondary.
        foreach (var index in new[] { 0, 2, 27 })
        {
            Assert.Equal(primaryColor, ((ISolidColorBrush)((TextBlock)flagged[index]).Foreground!).Color);
            Assert.Equal(secondaryColor, ((ISolidColorBrush)((TextBlock)clean[index]).Foreground!).Color);
        }

        // The offending cell alone reads in the worse ink, on the flagged row only.
        Assert.Equal(worseColor, ((ISolidColorBrush)((TextBlock)flagged[4]).Foreground!).Color);
        Assert.Equal(primaryColor, ((ISolidColorBrush)((TextBlock)flagged[5]).Foreground!).Color);
        Assert.Equal(primaryColor, ((ISolidColorBrush)((TextBlock)clean[4]).Foreground!).Color);
    }


    // ---- Phase 13 Task 6: R6's measured column, R8's click matrix, R9's tint and scroll --------

    [AvaloniaFact]
    public void FrameTableView_APlainClick_SelectsOneRow_AndOpensThePreviewAtIt()
    {
        // R8. The handler is on PointerReleased, not PointerPressed: the ListBox moves the
        // selection on press, so opening on press would preview the row selected a moment ago.
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        Click(window, RowBorders(view)[1]);

        var selected = Assert.Single(table.SelectedRows);
        Assert.Equal("two.fits", selected.FileName);

        var opened = Assert.Single(previews);
        Assert.Equal(1, opened.Index);
        Assert.Same(selected, opened.Rows[opened.Index]);
    }

    [AvaloniaFact]
    public void FrameTableView_ACtrlClick_ExtendsTheSelection_AndOpensNothing()
    {
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var list = view.GetControl<ListBox>("FrameRows");
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Click(window, RowBorders(view)[1], RawInputModifiers.Control);

        Assert.Equal(2, table.SelectedRows.Count);
        Assert.Empty(previews);
    }

    [AvaloniaFact]
    public void FrameTableView_AShiftClick_ExtendsTheSelection_AndOpensNothing()
    {
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var list = view.GetControl<ListBox>("FrameRows");
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Click(window, RowBorders(view)[1], RawInputModifiers.Shift);

        Assert.Equal(2, table.SelectedRows.Count);
        Assert.Empty(previews);
    }

    [AvaloniaFact]
    public void FrameTableView_ARightClick_OpensNothing()
    {
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        Click(window, RowBorders(view)[1], button: MouseButton.Right);

        Assert.Empty(previews);
    }

    [AvaloniaFact]
    public void FrameTableView_KeyboardNavigation_MovesTheSelection_AndOpensNothing()
    {
        // Ruling Q12. Arrowing through two hundred frames must not open two hundred preview
        // windows, so the preview is a pointer affordance and the handler has no keyboard arm.
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var list = view.GetControl<ListBox>("FrameRows");
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        FocusARow(view, list);

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, list.SelectedIndex);
        Assert.Equal("two.fits", Assert.Single(table.SelectedRows).FileName);
        Assert.Empty(previews);
    }

    [AvaloniaFact]
    public void FrameTableView_CtrlA_SelectsEveryRow_AndOpensNothing()
    {
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var list = view.GetControl<ListBox>("FrameRows");
        FocusARow(view, list);

        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, table.SelectedRows.Count);
        Assert.Empty(previews);
    }

    [AvaloniaFact]
    public void FrameTableView_AClickOnTheRowEndButton_OpensNoPreview()
    {
        // Without the Button-ancestor guard a click on the row-end flyout button, and on the
        // flyout's own "Open preview" item, would open the preview twice.
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var actionButton = RowBorders(view)[0]
            .GetVisualDescendants()
            .OfType<Button>()
            .First(button => button.Flyout is not null);

        Click(window, actionButton);

        Assert.Empty(previews);

        actionButton.Flyout?.Hide();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void FrameTableView_APreviewOpenedAndClosed_LeavesTheSelectionAndTheHighlight()
    {
        // Phase review P3-8. Opening a preview disposes a page and returns to the owner window
        // (PreviewModalService), and the selection collection is the table's own, so by
        // construction nothing in the table is torn down with the modal. That was reasoning, not a
        // case: this one pins it, including that the table opens again on a second click of the
        // same row rather than swallowing it as a repeat.
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        // A tick hover on the night strip leaves a highlight that a preview must not clear.
        table.HighlightFrameAt(0);
        Dispatcher.UIThread.RunJobs();

        Click(window, RowBorders(view)[1]);

        var selected = Assert.Single(table.SelectedRows);
        var highlighted = table.HighlightedRow;
        Assert.Single(previews);
        Assert.NotNull(highlighted);

        // The recorder stands in for the modal's whole lifetime: it returns once the open is
        // handed over, which is the moment the window would be up, and closing it runs nothing on
        // the table.
        Dispatcher.UIThread.RunJobs();

        Assert.Same(selected, Assert.Single(table.SelectedRows));
        Assert.Same(highlighted, table.HighlightedRow);
        Assert.True(highlighted!.IsHighlighted);

        Click(window, RowBorders(view)[1]);

        Assert.Equal(2, previews.Count);
        Assert.Equal(1, previews[1].Index);
        Assert.Same(selected, Assert.Single(table.SelectedRows));
    }

    [AvaloniaFact]
    public void FrameTableView_AColumn_AutoFitsItsWidestCellPlusItsHeader()
    {
        // R5. The auto-fit on load is the widest cell of the loaded night plus the header, the
        // one formula FrameTableView.AutoFitWidth states, measured here with the same
        // FormattedText the implementation uses in the cell's own typeface and the header's
        // label-tier one. Red if the column is wider or narrower than that formula, for the file
        // name (mono) and for a numeric column, both padded by the gutter on each side.
        var table = CreateTable(frames: LongNamedFrames());
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var fileName = table.Columns.Single(column => column.Key == "file_name");
        var fileNameCell = (TextBlock)RowCells(view)[1];
        var measured = MeasuredWidth(fileNameCell, LongestName);
        Assert.True(measured > FrameTableViewModel.ColumnFloor,
            $"the fixture name measures {measured}, which is not wider than the floor");

        Assert.Equal(ExpectedFit(view, fileName, fileNameCell, LongestName, "short.fits"), fileName.Width);
        Assert.Equal(fileName.Width, fileNameCell.Bounds.Width);

        var hfr = table.Columns.Single(column => column.Key == "median_hfr");
        var hfrCell = (TextBlock)RowCells(view)[4];
        Assert.Equal(TableMetrics.Gutter, hfrCell.Padding.Right);
        Assert.Equal(ExpectedFit(view, hfr, hfrCell, "2.00", "3.40"), hfr.Width);
        Assert.Equal(hfr.Width, hfrCell.Bounds.Width);
    }

    [AvaloniaFact]
    public void FrameTableView_ANightOfShortNames_FitsTheHeader_AndNeverFallsBelowTheFloor()
    {
        // The header is in the fit: a night of short names takes the "File name" title's width,
        // and every column, whatever its content, sits at or above the floor.
        var table = CreateTable(frames: TwoFrames());
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var fileName = table.Columns.Single(column => column.Key == "file_name");
        var cell = (TextBlock)RowCells(view)[1];

        Assert.Equal(ExpectedFit(view, fileName, cell, "one.fits", "two.fits"), fileName.Width);
        Assert.Equal(fileName.Width, cell.Bounds.Width);
        Assert.All(table.Columns, column => Assert.True(column.Width >= FrameTableViewModel.ColumnFloor,
            $"{column.Key} is {column.Width}, under the floor"));
    }

    [AvaloniaFact]
    public void FrameTableView_EveryHeaderCell_CarriesADivider()
    {
        // R5: a drag divider at the right edge of every keyed column's header cell.
        var table = CreateTable();
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var headers = HeaderCells(view);
        Assert.Equal(32, headers.Count);
        Assert.All(headers, header =>
        {
            var divider = Divider(header);
            Assert.Equal(HorizontalAlignment.Right, divider.HorizontalAlignment);
            Assert.NotNull(divider.Cursor);
            Assert.True(divider.Bounds.Width > 0d && divider.Bounds.Height > 0d, "the divider has no size");
        });
    }

    [AvaloniaFact]
    public async Task FrameTableView_DraggingADivider_ResizesTheColumnLive_AndStoresItOnRelease()
    {
        // The pointer drag changes the width live, the rows follow the header, nothing is written
        // until the release, and the release stores the width under the column's key beside the
        // visibility list. Red if the width lags the pointer, if the rows keep the old width, or
        // if the release writes nothing.
        var saves = new List<DisplaySettings>();
        DisplayColumnWriter? writer = null;
        var table = CreateTable(frames: TwoFrames(), saves: saves, writerSink: w => writer = w);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var fileName = table.Columns.Single(column => column.Key == "file_name");
        var before = fileName.Width;
        var from = DividerPoint(window, view, fileName);

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(40d, 0d));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + 40d, fileName.Width);
        Assert.Equal(before + 40d, RowCells(view)[1].Bounds.Width);
        Assert.False(table.HasStoredWidth("file_name"));

        window.MouseMove(from + new Point(80d, 0d));
        window.MouseUp(from + new Point(80d, 0d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await writer!.Pending;

        Assert.Equal(before + 80d, fileName.Width);
        Assert.Equal(before + 80d, HeaderCell(view, fileName).Bounds.Width);
        Assert.Equal(before + 80d, RowCells(view)[1].Bounds.Width);
        Assert.True(table.HasStoredWidth("file_name"));
        var saved = Assert.Single(saves);
        Assert.Equal(before + 80d, saved.ColumnWidthsFor(DisplaySettings.FramesTableId)["file_name"]);
        Assert.Equal(EveryColumn().ColumnsFor(DisplaySettings.FramesTableId), saved.ColumnsFor(DisplaySettings.FramesTableId));

        // A drag under the floor stops at the floor.
        window.MouseDown(DividerPoint(window, view, fileName), MouseButton.Left);
        window.MouseMove(DividerPoint(window, view, fileName) - new Point(2000d, 0d));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FrameTableViewModel.ColumnFloor, fileName.Width);
        window.MouseUp(DividerPoint(window, view, fileName) - new Point(2000d, 0d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await writer.Pending;
        Assert.Equal(FrameTableViewModel.ColumnFloor, saves[^1].ColumnWidthsFor(DisplaySettings.FramesTableId)["file_name"]);
    }

    [AvaloniaFact]
    public async Task FrameTableView_DoubleClickingADivider_ClearsTheStoredWidth_AndRefits()
    {
        // The stored width wins over the auto-fit until the divider is double-clicked, which
        // clears the entry from the document and returns the column to its auto-fit. Red if the
        // width stays dragged, if the document keeps the key, or if a plain click stores one.
        var saves = new List<DisplaySettings>();
        DisplayColumnWriter? writer = null;
        var stored = EveryColumn().WithColumnWidth(DisplaySettings.FramesTableId, "file_name", 400d);
        var table = CreateTable(display: stored, frames: TwoFrames(), saves: saves, writerSink: w => writer = w);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var fileName = table.Columns.Single(column => column.Key == "file_name");
        var cell = (TextBlock)RowCells(view)[1];
        var fit = ExpectedFit(view, fileName, cell, "one.fits", "two.fits");
        Assert.Equal(400d, fileName.Width);
        Assert.True(table.HasStoredWidth("file_name"));

        // A plain click is not a drag and stores nothing.
        var at = DividerPoint(window, view, fileName);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await writer!.Pending;
        Assert.Empty(saves);
        Assert.Equal(400d, fileName.Width);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await writer.Pending;

        Assert.Equal(fit, fileName.Width);
        Assert.Equal(fit, cell.Bounds.Width);
        Assert.False(table.HasStoredWidth("file_name"));
        Assert.Empty(saves[^1].ColumnWidthsFor(DisplaySettings.FramesTableId));
    }

    [AvaloniaFact]
    public void FrameTableView_TheFileNameCell_DoesNotTrim()
    {
        // R6: the file name is never cut. TextTrimming stays on TextBlock.cell.tc-text for the
        // other text columns, and the file name cell overrides it with a local value.
        var view = new FrameTableView { DataContext = CreateTable(frames: LongNamedFrames()) };
        ShowTable(view);

        var cells = RowCells(view);
        Assert.Equal(TextTrimming.None, ((TextBlock)cells[1]).TextTrimming);

        // And the rule is the file name cell's alone.
        Assert.Equal(TextTrimming.CharacterEllipsis, ((TextBlock)cells[0]).TextTrimming);
        Assert.Equal(TextTrimming.CharacterEllipsis, ((TextBlock)cells[2]).TextTrimming);
    }

    [AvaloniaFact]
    public void FrameTableView_TheFileNameColumn_LeavesAGapBeforeTheFilterCell()
    {
        // Red if the longest name's drawn right edge is under two gutters from the Filter cell's
        // text (the content-to-content gap the spine's T1 pins), the name flush against "Ha", or
        // the header and the cell disagree on the column's width.
        const string name = "M99_2031-01-02_Ha_300s_f_00012.fits";
        Assert.Equal(35, name.Length);
        var table = CreateTable(frames:
        [
            FrameTableViewModelTests.Frame(fileName: name, filePath: @"D:\Astro\M99\" + name),
            FrameTableViewModelTests.Frame(fileName: "short.fits", filePath: @"D:\Astro\M99\short.fits"),
        ]);
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var cells = RowCells(view);
        var fileName = (TextBlock)cells[1];
        var drawnRight = fileName.TranslatePoint(
            new Point(fileName.Padding.Left + MeasuredWidth(fileName, name), 0), view)!.Value.X;
        var filterLeft = cells[2].TranslatePoint(new Point(((TextBlock)cells[2]).Padding.Left, 0), view)!.Value.X;
        Assert.True(filterLeft - drawnRight >= TableMetrics.ColumnGutters, $"the name ends at {drawnRight}, the Filter text starts at {filterLeft}");
        Assert.Equal(HeaderCell(view, table.Columns.Single(column => column.Key == "file_name")).Bounds.Width, fileName.Bounds.Width);
    }

    [AvaloniaFact]
    public void FrameTableView_TheFileNameCell_CarriesTheFullPathOnItsTooltip()
    {
        // Ruling Q8: R6's "the full path stays on the tooltip" is read as "is there after this
        // task". The cell shows the name; the tip shows where it is.
        var view = new FrameTableView { DataContext = CreateTable(frames: TwoFrames()) };
        ShowTable(view);

        var cell = (TextBlock)RowCells(view)[1];

        Assert.Equal("one.fits", cell.Text);
        Assert.Equal(@"D:\Astro\one.fits", ToolTip.GetTip(cell));
    }

    [AvaloniaFact]
    public void FrameTableView_TheHeaderAndTheCell_ReportTheSameWidth_ForEveryColumn()
    {
        // Header and row cells bind one width per column, or every column to the right sits out
        // of line with its header. Every column, not the file name alone (R5), and after a drag
        // on one of them as well as on load.
        var table = CreateTable(frames: LongNamedFrames());
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        table.SetColumnWidth("fwhm", 150d);
        Dispatcher.UIThread.RunJobs();

        var cells = RowCells(view);
        for (var index = 0; index < table.Columns.Count; index++)
        {
            var column = table.Columns[index];
            var header = HeaderCell(view, column);
            Assert.Equal(column.Width, header.Bounds.Width);
            Assert.Equal(header.Bounds.Width, cells[index].Bounds.Width);
        }

        Assert.Equal(150d, table.Columns.Single(column => column.Key == "fwhm").Width);
    }

    [AvaloniaFact]
    public void FrameTableView_AHighlightedRow_RendersTheHoverFill()
    {
        // Spec 14.5: the strip's hover is the row's own hover fill, not a new token and not an
        // accent. The strip's tick is already the data.
        var table = CreateTable(frames: TwoFrames());
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        Assert.True(view.TryFindResource("ColorBgHover", out var hover));
        var hoverColor = ((ISolidColorBrush)hover!).Color;

        table.HighlightFrameAt(1);
        Dispatcher.UIThread.RunJobs();

        var rows = RowBorders(view);
        Assert.Equal(hoverColor, ((ISolidColorBrush)rows[1].Background!).Color);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)rows[0].Background!).Color);

        // A hover is not a selection: nothing is selected and the lit edge stays dark.
        Assert.Empty(table.SelectedRows);
        Assert.DoesNotContain(LitEdges(view), edge => edge.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void FrameTableView_TheHighlightClears_WhenHighlightedRowGoesNull()
    {
        var table = CreateTable(frames: TwoFrames());
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        table.HighlightFrameAt(1);
        Dispatcher.UIThread.RunJobs();

        table.HighlightFrameAt(null);
        Dispatcher.UIThread.RunJobs();

        Assert.All(
            RowBorders(view),
            row => Assert.Equal(Colors.Transparent, ((ISolidColorBrush)row.Background!).Color));
    }

    [AvaloniaFact]
    public void FrameTableView_HighlightingARowBelowTheFold_ScrollsItIntoView()
    {
        // Ruling Q7's proof that the seam is wired, and it is a rendering assertion rather than a
        // mock: row 60 does not exist until the highlight brings it into the viewport.
        var frames = Enumerable.Range(0, 200)
            .Select(i => FrameTableViewModelTests.Frame(
                fileName: $"frame_{i:000}.fits",
                captureDate: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc).AddSeconds(i * 300)))
            .ToList();
        var table = CreateTable(frames: frames);
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var list = view.GetControl<ListBox>("FrameRows");
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();

        Assert.Null(list.ContainerFromIndex(60));

        table.HighlightFrameAt(60);
        Dispatcher.UIThread.RunJobs();

        var container = list.ContainerFromIndex(60);
        Assert.NotNull(container);

        var top = ((Visual)container!).TranslatePoint(new Point(0, 0), scroller)!.Value;
        Assert.InRange(top.Y, -0.5d, scroller.Viewport.Height - ((Visual)container).Bounds.Height);
        Assert.Contains("frame_060.fits", VisibleCellTexts(view));
    }

    [AvaloniaFact]
    public void FrameTableView_AnOutlierFilter_DoesNotResizeTheFileNameColumn()
    {
        // Ruling Q9. The column is measured from the whole loaded night, so turning a filter on
        // and off does not move every column to its right under a pointer about to click one.
        var table = CreateTable(frames: LongNamedFrames());
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);

        var fileName = table.Columns.Single(column => column.Key == "file_name");
        var before = fileName.Width;
        Assert.True(before > FrameTableViewModel.ColumnFloor);

        table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(table.Rows);
        Assert.DoesNotContain(LongestName, table.Rows.Select(row => row.FileName));
        Assert.Equal(before, fileName.Width);
        Assert.Equal(before, RowCells(view)[1].Bounds.Width);

        table.SetOutlierFilter(FrameOutlierFilter.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, fileName.Width);

        // Review P3-3, Q9's other half: a sort re-projects the rows and must not resize a column
        // either. Safe by construction, because the measurement reads _captureOrder and is not
        // wired to Project at all, and pinned here so it stays that way.
        table.SortByCommand.Execute("median_hfr");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before, fileName.Width);
        Assert.Equal(before, RowCells(view)[1].Bounds.Width);
    }

    [AvaloniaFact]
    public void FrameTableView_APressOnOneRowReleasedOverAnother_OpensNoPreviewAtTheSecond()
    {
        // Review P3-4. R8 names a click, not a drag. Whether the release routes to the row the
        // pointer ended over or, under a capture, back to the row it started on, the one thing
        // that must never happen is a preview at the row the pointer merely finished over.
        var previews = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = CreateTable(frames: TwoFrames(), previews: previews);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var rows = RowBorders(view);
        window.MouseDown(RowPoint(window, rows[0]), MouseButton.Left);
        window.MouseUp(RowPoint(window, rows[1]), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(previews, opened => opened.Index == 1);
    }

    [AvaloniaFact]
    public void FrameTableView_ARootTextSizeChange_RemeasuresTheFileNameColumn()
    {
        // Review P3-2. The root text size is a setting re-resolved into the live tree with no new
        // DataContext, and with trimming off a name that no longer fits would be clipped at the
        // cell edge rather than elided, so the column has to be measured again.
        //
        // The assertion is that the change re-runs the measurement, not that the width grew: the
        // headless text shaper returns a size-independent advance per glyph, so a rendered width
        // here is the same at 14 and at 30. A width nobody measured is planted first, and the size
        // change is what replaces it with the measured one.
        var table = CreateTable(frames: LongNamedFrames());
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);

        var fileName = table.Columns.Single(column => column.Key == "file_name");
        var measured = fileName.Width;
        Assert.True(measured > FrameTableViewModel.ColumnFloor);

        table.SetColumnWidth("file_name", measured + 500d);
        Assert.Equal(measured + 500d, fileName.Width);

        window.FontSize *= 1.5d;
        Dispatcher.UIThread.RunJobs();

        // The window's size really did reach this control, which is the inheritance half.
        Assert.Equal(window.FontSize, view.FontSize);
        Assert.Equal(measured, fileName.Width);
    }

    [AvaloniaFact]
    public void FrameTableView_ANightWithNoFrames_FitsTheHeadersAlone()
    {
        // Review P3-5, declined as a code change and pinned instead: the widths live on the
        // table, and every table seeds its own in its constructor, so a night with no frames fits
        // its headers rather than inheriting the previous night's measurement. The view is the
        // one that is reused across session loads, not the view-model.
        var wide = CreateTable(frames: LongNamedFrames());
        var view = new FrameTableView { DataContext = wide };
        ShowTable(view);

        var wideFileName = wide.Columns.Single(column => column.Key == "file_name");
        Assert.True(wideFileName.Width > FrameTableViewModel.ColumnFloor);

        var empty = CreateTable(frames: []);
        view.DataContext = empty;
        Dispatcher.UIThread.RunJobs();

        var fileName = empty.Columns.Single(column => column.Key == "file_name");
        Assert.Equal(FrameTableView.AutoFitWidth(HeaderWidth(view, fileName), 0d), fileName.Width);
        Assert.True(fileName.Width < wideFileName.Width);
    }
    // ---- Task 6's own fixtures and gestures ---------------------------------------------------

    /// <summary>A name far wider than the 240 px floor, so the measured column has to grow.</summary>
    private const string LongestName =
        "M31_2025-12-07_Ha_300s_gain100_offset50_-10C_frame_000147_calibrated.fits";

    private static IReadOnlyList<FrameRow> TwoFrames() =>
    [
        FrameTableViewModelTests.Frame(fileName: "one.fits", filePath: @"D:\Astro\one.fits"),
        FrameTableViewModelTests.Frame(fileName: "two.fits", filePath: @"D:\Astro\two.fits"),
    ];

    /// <summary>One long name, and the flagged row is the short one: an HFR filter then leaves the
    /// long name off the table without the column following it.</summary>
    private static IReadOnlyList<FrameRow> LongNamedFrames() =>
    [
        FrameTableViewModelTests.Frame(
            fileName: LongestName,
            filePath: @"D:\Astro\M31\" + LongestName,
            medianHfr: 2.0d),
        FrameTableViewModelTests.Frame(
            fileName: "short.fits",
            filePath: @"D:\Astro\M31\short.fits",
            medianHfr: 3.4d,
            isHfrOutlier: true),
    ];

    /// <summary>The width the implementation's FormattedText produces for a string in a cell's own
    /// typeface, so the case pins R6's rule rather than a number.</summary>
    private static double MeasuredWidth(TextBlock cell, string text)
        => new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(cell.FontFamily, cell.FontStyle, cell.FontWeight),
            cell.FontSize,
            null).Width;

    /// <summary>The header title's width in the view's family at the label tier (t-label: Medium
    /// at FontSizeLabel x root), which is what the header cell renders and the auto-fit measures.
    /// </summary>
    private static double HeaderWidth(FrameTableView view, ColumnViewModel column)
        => new FormattedText(
            column.Title,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(view.FontFamily, view.FontStyle, FontWeight.Medium),
            view.FontSize * (double)view.FindResource("FontSizeLabel")!,
            null).Width;

    /// <summary>R5's auto-fit a column should report: the formula over the header and the widest
    /// of the given cell texts in the cell's own typeface; the formula adds the two gutters.</summary>
    private static double ExpectedFit(FrameTableView view, ColumnViewModel column, TextBlock cell, params string[] texts)
        => FrameTableView.AutoFitWidth(HeaderWidth(view, column), texts.Max(text => MeasuredWidth(cell, text)));

    private static Border Divider(ContentControl header)
        => header.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("column-divider"));

    /// <summary>The centre of a column's divider, in window coordinates.</summary>
    private static Point DividerPoint(Window window, FrameTableView view, ColumnViewModel column)
    {
        var divider = Divider(HeaderCell(view, column));
        return divider.TranslatePoint(new Point(divider.Bounds.Width / 2d, divider.Bounds.Height / 2d), window)!.Value;
    }

    /// <summary>A press and a release over a control, at the point RowPoint picks.</summary>
    private static void Click(
        Window window,
        Visual target,
        RawInputModifiers modifiers = RawInputModifiers.None,
        MouseButton button = MouseButton.Left)
    {
        var point = RowPoint(window, target);

        window.MouseDown(point, button, modifiers);
        window.MouseUp(point, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A point near a control's left edge: a row is wider than the window with every
    /// column on, so its centre is off screen and would hit nothing.</summary>
    private static Point RowPoint(Window window, Visual target)
    {
        var local = new Point(Math.Min(20d, target.Bounds.Width / 2d), target.Bounds.Height / 2d);
        return target.TranslatePoint(local, window) ?? local;
    }

    // Focus a row container, which is what clicking a row does, and prove it landed inside the
    // table. A key event routes from the focused element upward, so with focus left on the window
    // the press never passes through FrameTableView at all and a key test would be asserting
    // nothing. Focusing the ListBox itself is not enough: it delegates focus to a container.
    private static void FocusARow(FrameTableView view, ListBox list)
    {
        var container = (ListBoxItem)list.ContainerFromIndex(0)!;
        container.Focus();
        Dispatcher.UIThread.RunJobs();

        var focused = TopLevel.GetTopLevel(view)!.FocusManager!.GetFocusedElement();
        Assert.NotNull(focused);
        Assert.Contains(view, ((Visual)focused!).GetVisualAncestors());
    }

    // The 2 px lit edge each row draws over itself when its container is selected. Drawn over the
    // row rather than inside its layout, so selecting a row does not push its cells out of line
    // with the header.
    private static IReadOnlyList<Rectangle> LitEdges(FrameTableView view)
        => [.. view.GetVisualDescendants().OfType<Rectangle>().Where(edge => edge.Width == 2d)];

    // The header cell's sort button, which is what carries the alignment.
    private static Button HeaderButton(FrameTableView view, ColumnViewModel column)
        => HeaderCell(view, column).GetVisualDescendants().OfType<Button>().First();

    // The flyout's own content control. FlyoutBase does not expose its popup, and the popup host
    // is not in the anchor's visual tree, so the content is reached through the Flyout that
    // declared it; its bindings are live once ShowAt has attached it.
    private static Control FlyoutContent(Button anchor)
        => (Control)((Flyout)anchor.Flyout!).Content!;

    private static Border RowBorder(FrameTableView view) => RowBorders(view)[0];

    // Every realised row. With the rows virtualised (F12) this is the viewport's rows, not the
    // whole list.
    private static IReadOnlyList<Border> RowBorders(FrameTableView view)
        => [.. view.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("frame-row"))];

    // The row's cells, in FrameColumns.All's order. The row-end action button is the one child of
    // the cell panel that is not a cell.
    private static IReadOnlyList<Control> RowCells(FrameTableView view) => RowCellsAt(view, 0);

    // The same, for one of several realised rows, in the order the table shows them.
    //
    // P14A PAR-004: the Rig cell is excluded by name for the reason the Rig column is excluded
    // from HeaderCells by its Content type. It is not a FrameColumns entry and it is not a
    // display.columns.frames key (task1-report departure 1), so a list that claims to be "the
    // row's cells, in FrameColumns.All's order" must not carry it, and every index into this list
    // stays the column index it was before that column existed.
    private static IReadOnlyList<Control> RowCellsAt(FrameTableView view, int rowIndex)
        => [.. ((StackPanel)RowBorders(view)[rowIndex].Child!).Children
            .OfType<Control>()
            .Where(child => child is not Button && child.Name != "RigCell")];

    private static IReadOnlyList<Button> OpenFlyoutButtons(Button anchor)
        => [.. FlyoutContent(anchor).GetVisualDescendants().OfType<Button>()];

    // The 32 header cells: ContentControls whose Content is a column. Buttons are ContentControls
    // too, so the Content test is what separates them.
    private static IReadOnlyList<ContentControl> HeaderCells(FrameTableView view)
        => [.. view.GetVisualDescendants().OfType<ContentControl>().Where(cell => cell.Content is ColumnViewModel)];

    private static ContentControl HeaderCell(FrameTableView view, ColumnViewModel column)
        => HeaderCells(view).Single(cell => ReferenceEquals(cell.Content, column));

    // The shown title TextBlock of a header cell. The cell carries both spine title templates, one
    // hidden, and the sort glyph beside the title is a TextBlock too.
    private static TextBlock HeaderTitle(ContentControl cell)
        => cell.GetVisualDescendants().OfType<TextBlock>().Single(block =>
            block.IsEffectivelyVisible && block.Text == ((ColumnViewModel)cell.Content!).Title);

    private static IReadOnlyList<string> VisibleHeaderTitles(FrameTableView view)
        => [.. HeaderCells(view)
            .Where(cell => cell.IsEffectivelyVisible)
            .SelectMany(cell => cell.GetVisualDescendants().OfType<TextBlock>())
            .Select(block => block.Text ?? "")];

    private static IReadOnlyList<string> VisibleCellTexts(FrameTableView view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    // ---- spec 12.4's per-frame quality grading (Phase 14A Task 3) ---------------------------

    private static readonly MetricGrade NoGrade = new(null, null);

    private static FrameGrading BandedGrading(double? hfrZ, double? eccentricityZ = null, double? starsZ = null)
        => new(
            new MetricGrade(hfrZ, 2.10d), NoGrade,
            new MetricGrade(eccentricityZ, 0.40d), NoGrade,
            NoGrade, NoGrade,
            new MetricGrade(starsZ, 1400d),
            NoGrade,
            NoGrade);

    private static FrameRow Banded(
        double? hfrZ,
        double? eccentricityZ = null,
        double? starsZ = null,
        bool isHfrOutlier = false,
        string fileName = "banded.fits")
        => FrameTableViewModelTests.Frame(
            fileName: fileName,
            captureDate: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc),
            filterUsed: "Ha",
            exposureTime: 300d,
            medianHfr: 2.35d,
            eccentricity: 0.41d,
            detectedStars: 1490,
            isHfrOutlier: isHfrOutlier) with
        { Grading = BandedGrading(hfrZ, eccentricityZ, starsZ) };

    private static Color ThemeColor(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"the theme has no {key} key");
        return value is Color color ? color : ((ISolidColorBrush)value!).Color;
    }

    /// <summary>The HFR cell of the first row, which is the column at index 4 of spec 12.4's
    /// order and the one every band case below reads.</summary>
    private static TextBlock HfrCell(FrameTableView view)
        => view.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(block => block.Classes.Contains("tc-num") && block.Text == "2.35");

    [AvaloniaFact]
    public void FrameTableView_AGradedCell_RendersItsBandInk()
    {
        // Spec 12.4's band table: at or above 3.0 is reject, ColorErrorValue, whose brush is
        // ColorError (questions.md Q14).
        var view = new FrameTableView { DataContext = CreateTable(frames: [Banded(3.2d)]) };
        ShowTable(view);

        var cell = HfrCell(view);

        Assert.Contains("band-reject", cell.Classes);
        Assert.DoesNotContain("band-better", cell.Classes);
        Assert.DoesNotContain("band-watch", cell.Classes);
        Assert.Equal(
            ThemeColor("ColorErrorValue"),
            Assert.IsAssignableFrom<ISolidColorBrush>(cell.Foreground).Color);

        // And the cell carries the grading tooltip, in MAD units and never in sigma.
        var tooltip = ToolTip.GetTip(cell) as string;
        Assert.NotNull(tooltip);
        Assert.Contains("MAD units", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("sigma", tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void FrameTableView_ANeutralCell_RendersThePrimaryInk()
    {
        // Spec 12.4 gives neutral ColorTextPrimary, "which is to say no mark", so a neutral cell
        // renders exactly as an ungraded one and carries none of the three classes.
        var view = new FrameTableView { DataContext = CreateTable(frames: [Banded(0.4d)]) };
        ShowTable(view);

        var cell = HfrCell(view);

        Assert.DoesNotContain("band-better", cell.Classes);
        Assert.DoesNotContain("band-watch", cell.Classes);
        Assert.DoesNotContain("band-reject", cell.Classes);
        Assert.Equal(
            ThemeColor("ColorTextPrimary"),
            Assert.IsAssignableFrom<ISolidColorBrush>(cell.Foreground).Color);
    }

    [AvaloniaFact]
    public void FrameTableView_AFlaggedCell_KeepsTheWorseInk()
    {
        // A cell that is both flagged and banded takes the flag's ink. The flag is the night's own
        // verdict about this frame and it is what the outlier filter, the findings' counts and the
        // night strip's tall ticks all agree on; the band is a different question about the same
        // number. TextBlock.cell.worse is declared in this view's own style host, and a control
        // level style beats an application level one whatever the selectors say, which is the
        // mechanism that makes the flag win.
        var view = new FrameTableView
        {
            DataContext = CreateTable(frames: [Banded(-2.0d, isHfrOutlier: true)]),
        };
        ShowTable(view);

        var cell = HfrCell(view);

        Assert.Contains("worse", cell.Classes);
        Assert.Contains("band-better", cell.Classes);
        Assert.Equal(
            ThemeColor("ColorMetricWorst"),
            Assert.IsAssignableFrom<ISolidColorBrush>(cell.Foreground).Color);
    }

    [AvaloniaFact]
    public void FrameTableView_NoCell_TakesATintedFill()
    {
        // The band is carried by the cell's ink alone: no cell paints anything, whatever the
        // row's own tint (R23) does behind it.
        var view = new FrameTableView
        {
            DataContext = CreateTable(frames: [
                Banded(3.2d, eccentricityZ: 3.2d, starsZ: 3.2d, fileName: "reject.fits"),
                Banded(-3.2d, eccentricityZ: -3.2d, starsZ: -3.2d, fileName: "better.fits"),
            ]),
        };
        ShowTable(view);

        foreach (var block in view.GetVisualDescendants().OfType<TextBlock>())
        {
            Assert.True(
                block.Background is null || block.Background is ISolidColorBrush { Color.A: 0 },
                $"the cell '{block.Text}' paints a background");
        }
    }

    // R23: the row's grade is carried by the row's own fill, for watch and reject only, and the
    // 2 px score rule at the leading edge is gone. Red if the rule comes back, if a better or
    // neutral row takes a fill, if a watch or reject row's fill is not its band colour at its
    // alpha, or if hover and selection stop reading over the tint.
    [AvaloniaTheory]
    [InlineData(1.0d, "ColorWarningValue", 0.12d)]
    [InlineData(3.2d, "ColorErrorValue", 0.15d)]
    [InlineData(-3.2d, null, 0d)]
    [InlineData(0d, null, 0d)]
    public void FrameTableView_ARowsGrade_TintsItsFill_ForWatchAndRejectOnly(double z, string? tintKey, double alpha)
    {
        var view = new FrameTableView
        {
            DataContext = CreateTable(frames: [Banded(z, eccentricityZ: z, starsZ: z)]),
        };
        ShowTable(view);

        Assert.DoesNotContain(
            view.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("score-rule"));

        var row = RowBorder(view);
        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
        if (tintKey is null)
        {
            Assert.Equal(0, fill.Color.A);
            return;
        }

        Assert.Equal(ThemeColor(tintKey), fill.Color);
        Assert.Equal(alpha, fill.Opacity, 3);

        // Hover over a tint is a stronger tint in the same colour, not the neutral hover token.
        ((IPseudoClasses)row.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();
        var hovered = Assert.IsAssignableFrom<ISolidColorBrush>(row.Background);
        Assert.Equal(ThemeColor(tintKey), hovered.Color);
        Assert.True(hovered.Opacity > alpha, $"hovered opacity {hovered.Opacity} is not above {alpha}");
        ((IPseudoClasses)row.Classes).Set(":pointerover", false);

        // Selection wins: the tint steps aside so the container's lit field shows through.
        view.GetControl<ListBox>("FrameRows").SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color.A);
    }

    // D211 and ruling R16: red if a row template still carries the brush bar, or the rows and the
    // header reserve layout room for the lit edge, which now lands inside the Time cell's gutter.
    [AvaloniaFact]
    public void FrameTableView_ARow_HasNoBrushBar_AndTheRailSitsInTheGutter()
    {
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Rectangle>(), r => r.Name is "BrushBar" or "BrushBarBase");
        Assert.All(RowBorders(view), row => Assert.Equal(0d, row.Padding.Left));
        Assert.Equal(0d, view.GetControl<StackPanel>("FrameTableHeader").Margin.Left);
        Assert.All(LitEdges(view), edge => Assert.True(edge.Width <= TableMetrics.Gutter));

        // A selection moves no text: Time's text starts where it did.
        var time = (TextBlock)RowCells(view)[0];
        double TextLeft() => time.TranslatePoint(new Point(time.Padding.Left, 0), view)!.Value.X;
        var before = TextLeft();
        view.GetControl<ListBox>("FrameRows").SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.True(LitEdges(view).Single().IsEffectivelyVisible);
        Assert.Equal(before, TextLeft());
    }

    [AvaloniaFact]
    public void FrameTableView_AlternateRows_TakeTheZebraFill()
    {
        // Spec.md item 7: a light fill on alternate rows, by index, so a re-sort keeps the bands
        // where they are while the frames move through them.
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(fileName: "a.fits", medianHfr: 1.0d),
            FrameTableViewModelTests.Frame(fileName: "b.fits", medianHfr: 2.0d),
            FrameTableViewModelTests.Frame(fileName: "c.fits", medianHfr: 3.0d),
        };
        var table = CreateTable(frames: frames);
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);
        var list = view.GetControl<ListBox>("FrameRows");
        var zebra = ThemeColor("ColorBorderDefault");

        void AssertZebra()
        {
            for (var index = 0; index < 3; index++)
            {
                var fill = Assert.IsAssignableFrom<ISolidColorBrush>(((ListBoxItem)list.ContainerFromIndex(index)!).Background);
                if (index % 2 == 1)
                {
                    Assert.Equal(zebra, fill.Color);
                }
                else
                {
                    Assert.Equal(0, fill.Color.A);
                }
            }
        }

        AssertZebra();

        table.SortByCommand.Execute("median_hfr");
        table.SortByCommand.Execute("median_hfr");
        Dispatcher.UIThread.RunJobs();
        Assert.True(table.Descending);
        Assert.Equal("c.fits", table.Rows[0].FileName);

        AssertZebra();
    }

    [AvaloniaFact]
    public void FrameTableView_AMissingValue_RendersTheFaintDash()
    {
        // Spec.md item 6: an absent value is "-" in the faintest ink, and a real zero is a figure
        // in the ordinary ink.
        var frames = new[]
        {
            FrameTableViewModelTests.Frame(fileName: "bare.fits", cameraGain: 0),
        };
        var view = new FrameTableView { DataContext = CreateTable(frames: frames) };
        ShowTable(view);

        var cells = RowCells(view);
        var hfr = (TextBlock)cells[4];
        var gain = (TextBlock)cells[31];

        Assert.Equal(MetricText.Missing, hfr.Text);
        Assert.Equal(ThemeColor("ColorTextTertiary"), Assert.IsAssignableFrom<ISolidColorBrush>(hfr.Foreground).Color);
        Assert.Equal("0", gain.Text);
        Assert.Equal(ThemeColor("ColorTextSecondary"), Assert.IsAssignableFrom<ISolidColorBrush>(gain.Foreground).Color);
    }

    [AvaloniaFact]
    public async Task FrameTableView_ANarrowedNumericColumn_StopsAtItsWidestFigure()
    {
        // Spec.md items 3 and 5: a drag 2000 px left stops a numeric column at its widest figure,
        // below the header-driven auto-fit since the title may trim, and no realised figure is
        // cut: every figure's text fits inside its cell's padding.
        DisplayColumnWriter? writer = null;
        var table = CreateTable(writerSink: w => writer = w);
        var view = new FrameTableView { DataContext = table };
        var window = ShowTable(view);
        var hfr = table.Columns.Single(column => column.Key == "median_hfr");
        var fit = hfr.Width;
        var hfrCells = RowBorders(view).Select((_, row) => (TextBlock)RowCellsAt(view, row)[4]).ToList();
        var figureFit = Math.Max(
            FrameTableViewModel.ColumnFloor,
            FrameTableView.FigureFitWidth(hfrCells.Max(cell => MeasuredWidth(cell, cell.Text!))));

        var from = DividerPoint(window, view, hfr);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from - new Point(2000d, 0d));
        window.MouseUp(from - new Point(2000d, 0d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await writer!.Pending;

        Assert.Equal(figureFit, hfr.Width);
        Assert.True(hfr.Width < fit);
        Assert.All(
            view.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.Classes.Contains("tc-num") && block.IsEffectivelyVisible),
            block => Assert.True(
                block.TextLayout.WidthIncludingTrailingWhitespace
                    <= block.Bounds.Width - block.Padding.Left - block.Padding.Right + 0.01d,
                $"'{block.Text}' is cut"));
    }

    [AvaloniaFact]
    public async Task FrameTableView_ASelectedRow_KeepsItsRawHeaderTextInThePageInk()
    {
        // P12: no accent hue carries state on this page. The spine's selected rule inks the
        // container accent, and the raw header panel's failure line has no ink of its own.
        var table = CreateTable(getHeaders: _ => throw new InvalidOperationException("unreadable"));
        var view = new FrameTableView { DataContext = table };
        ShowTable(view);
        var row = table.Rows[0];

        table.ToggleRawHeadersCommand.Execute(row);
        await row.RawHeaders!.PendingLoad!;
        Dispatcher.UIThread.RunJobs();
        table.SelectFrameAt(0);
        Dispatcher.UIThread.RunJobs();

        var failure = view.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "FailureLine");
        Assert.True(failure.IsEffectivelyVisible);
        Assert.Equal(ThemeColor("ColorTextPrimary"), Assert.IsAssignableFrom<ISolidColorBrush>(failure.Foreground).Color);
    }

    [AvaloniaFact]
    public void FrameTableView_HeaderTitles_UseTheLabelTier()
    {
        // Spec.md item 5: one header style, the shared t-label tier, and every title has a tip.
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        Assert.All(HeaderCells(view), cell =>
        {
            var title = HeaderTitle(cell);
            Assert.Contains("t-label", title.Classes);
            Assert.NotEqual(BindingPriority.LocalValue, title.GetDiagnostic(TextBlock.FontWeightProperty).Priority);
            Assert.NotEqual(BindingPriority.LocalValue, title.GetDiagnostic(TextBlock.ForegroundProperty).Priority);
            Assert.NotNull(ToolTip.GetTip(title));
        });
        Assert.Contains("t-label", view.GetControl<TextBlock>("RigColumnHeader").Classes);
    }

    [AvaloniaFact]
    public void FrameTableView_TheHeaderRow_HasARuleUnderIt()
    {
        // Spec.md item 7: one rule under the header row, in TableRow's header-edge brush.
        var view = new FrameTableView { DataContext = CreateTable() };
        ShowTable(view);

        var rule = Assert.IsType<Border>(view.GetControl<ScrollViewer>("FrameHeaderScroller").Parent);
        Assert.Equal(new Thickness(0, 0, 0, 1), rule.BorderThickness);
        Assert.Equal(
            ThemeColor("ColorBorderEmphasis"),
            Assert.IsAssignableFrom<ISolidColorBrush>(rule.BorderBrush).Color);
    }

    [AvaloniaFact]
    public void FrameTableView_ScrollBars_AreAlwaysVisibleAndFullWidth()
    {
        // Spec.md item 8: the rows' bars never auto-hide, and the vertical bar is drawn at the
        // table width even on a table too short to scroll.
        var view = new FrameTableView { DataContext = CreateTable(frames: TwoFrames()) };
        ShowTable(view);

        var rows = view.GetControl<ListBox>("FrameRows").GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.False(rows.AllowAutoHide);
        var bar = rows.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(scroll => scroll.Orientation == Orientation.Vertical);
        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(TableMetrics.ScrollBarSize, bar.Bounds.Width);
    }

    [AvaloniaFact]
    public void FrameTableView_PannedToTheFarRight_TheHeaderStaysInStep()
    {
        // The rows' viewport is one bar width narrower than the window onto the header would be;
        // the header's ScrollInset keeps the two equal, and its trailing spacer over the row-end
        // button keeps the extents equal, so at the far right neither offset clamps the other
        // short. The two-way sync makes the header's offset always equal the rows', so what
        // proves it is that the rows still reach their own far right.
        var (view, rows, header, table) = ShowNarrow();
        var farRight = rows.Extent.Width - rows.Viewport.Width;
        rows.Offset = new Vector(farRight, 0);
        Dispatcher.UIThread.RunJobs();

        Assert.True(rows.Offset.X > 0d);
        Assert.Equal(farRight, rows.Offset.X, 0.5);
        Assert.Equal(rows.Offset.X, header.Offset.X);

        var last = table.Columns.Last(column => column.IsShown);
        var headerCell = HeaderCell(view, last);
        var rowCell = RowCells(view)[table.Columns.ToList().IndexOf(last)];
        Assert.Equal(
            headerCell.TranslatePoint(new Point(headerCell.Bounds.Width, 0), view)!.Value.X,
            rowCell.TranslatePoint(new Point(rowCell.Bounds.Width, 0), view)!.Value.X,
            1);
    }
}
