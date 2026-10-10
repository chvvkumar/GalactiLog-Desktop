using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels.Wbpp;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.App.Views.TargetDetail.Wbpp;
using GalactiLog.Core.Wbpp;
using Xunit;
using static GalactiLog.App.Tests.ViewModels.Wbpp.QualityPanelTestFactory;

namespace GalactiLog.App.Tests.Views.Wbpp;

/// <summary>
/// design-spec 18.3's view cases for spec 12.13's quality panel, Task 3b brief section 8.9: the ten
/// columns in order, the five chips in the fixed metric order, the master check box's two-way
/// binding, the baseline segment's shipped idiom, the metric cell's fixed trailing mark slot, and
/// the two literal sentences.
/// </summary>
/// <remarks>
/// <para>
/// Every layout case hosts the panel at its <b>real allotment inside the export window</b>
/// (HANDOFF 5.2 item 48, TRACKING section 5), read from the shipped
/// <c>WbppExportWindow.axaml</c> rather than from a stated figure: the window declares
/// <c>Width="1000"</c> and <c>MinWidth="860"</c>, and the body scroller's own StackPanel carries
/// <c>Margin="20"</c>, so the panel's slot is <see cref="PanelAllotment"/> at the opening width and
/// <see cref="MinimumAllotment"/> at the narrowest the user can drag it to.
/// </para>
/// <para>
/// The behaviour is asserted on the view-model, in <c>QualityPanelViewModelTests</c> and
/// <c>ConstraintChipTests</c>.
/// </para>
/// </remarks>
public class QualityPanelViewTests
{
    /// <summary>The export page's body content width at the window's opening size:
    /// <c>WbppExportWindow.axaml</c>'s <c>Width="1000"</c> less the body StackPanel's 20 margin on
    /// each side.</summary>
    private const double PanelAllotment = 960d;

    /// <summary>The narrowest slot the page can give this panel: the same window's
    /// <c>MinWidth="860"</c> less the same two margins.</summary>
    private const double MinimumAllotment = 820d;

    private static QualityPanelViewModel Populated()
    {
        var start = new DateTime(2025, 3, 20, 21, 0, 0, DateTimeKind.Utc);
        var panel = QualityPanelViewModelTests.Build(
            [
                Entry(Frame("pass.fits", hfr: 1d, eccentricity: 0.4d, fwhm: 1.8d, stars: 900, rms: 0.4d,
                    captureDate: start), NeutralGrading()),
                Entry(Frame("fail.fits", hfr: 5d, eccentricity: 0.4d, fwhm: 1.8d, stars: 900, rms: 0.4d,
                    captureDate: start.AddMinutes(5)), NeutralGrading()),
            ],
            out _,
            enabled: true);

        var hfr = panel.Chips.Single(chip => chip.Metric == WbppMetric.Hfr);
        hfr.EnableCommand.Execute(null);
        hfr.ThresholdText = "2";
        return panel;
    }

    private static (Window Window, QualityPanelView View) Show(
        QualityPanelViewModel model,
        double allotment = PanelAllotment)
    {
        var view = new QualityPanelView { DataContext = model };

        // The panel's own allotment, not the window's: an Auto or star column measured against a
        // bare 1280 window is measured against a width the export page never gives it. The 40 is
        // the body StackPanel's two 20 margins, so the window around the host is the export
        // window's own width at this allotment.
        var host = new Border { Width = allotment, Child = view };
        var window = new Window { Width = allotment + 40d, Height = 800, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static T Named<T>(Visual root, string name)
        where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static IReadOnlyList<Grid> Rows(Visual root)
        => [.. root.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "VerdictRow")];

    [AvaloniaFact]
    public void QualityPanelView_Constructs_AndRendersAPopulatedViewModel()
    {
        var (window, view) = Show(Populated());

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        Assert.Equal(2, Rows(view).Count);

        window.Close();
    }

    /// <summary>The ten columns of spec 12.13, in the stated order: Copy, Verdict, Filter, HFR,
    /// Ecc, FWHM, Stars, RMS, File, Night.</summary>
    [AvaloniaFact]
    public void QualityPanelView_DrawsTheTenColumns_InTheStatedOrder()
    {
        var (window, view) = Show(Populated());
        var header = Named<Grid>(view, "HeaderRow");

        Assert.Equal(10, header.ColumnDefinitions.Count);

        var titles = header.Children
            .OrderBy(Grid.GetColumn)
            .Select(HeaderTitle)
            .ToList();

        Assert.Equal(
            ["Copy", "Verdict", "Filter", "HFR px", "Ecc", "FWHM \"", "Stars", "RMS \"", "File", "Night"],
            titles);

        window.Close();
    }

    /// <summary>
    /// The table follows the spine's conventions (spine-spec 6.2): one gutter per cell, one right
    /// edge per figure column across the header and the rows, figures never trimmed, a faint dash,
    /// headers on t-label and a scroller that does not auto-hide. Again at the x-large text size.
    /// </summary>
    [AvaloniaFact]
    public void QualityPanelView_FollowsTheTableConventions()
    {
        var (window, view) = Show(Populated());

        TableAssert.Conventions(view);
        window.FontSize = 20;
        Dispatcher.UIThread.RunJobs();
        TableAssert.Conventions(view);

        window.Close();
    }

    /// <summary>The five chips render in spec 12.13's fixed metric order, which is also the table's
    /// metric column order.</summary>
    [AvaloniaFact]
    public void QualityPanelView_DrawsTheFiveChips_InTheFixedMetricOrder()
    {
        var (window, view) = Show(Populated());
        var chips = Named<ItemsControl>(view, "Chips");

        var labels = chips.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.Name is "GhostChip" or "ActiveChip")
            .Where(control => control.IsVisible)
            .Select(ChipLabel)
            .ToList();

        Assert.Equal(["HFR", "+ Ecc", "+ FWHM", "+ Stars", "+ RMS"], labels);

        window.Close();
    }

    /// <summary>The master check box is bound two way: the view-model reaches the control and the
    /// control reaches the view-model.</summary>
    [AvaloniaFact]
    public void QualityPanelView_TheMasterCheckBox_IsBoundTwoWay()
    {
        var model = Populated();
        var (window, view) = Show(model);
        var box = Named<CheckBox>(view, "EnableFilters");

        Assert.True(box.IsChecked);

        model.IsFilterEnabled = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(box.IsChecked);

        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.IsFilterEnabled);

        window.Close();
    }

    /// <summary>
    /// The baseline segment is the shipped two-button idiom (HANDOFF 5.2 item 23): two
    /// <see cref="ToggleButton"/>s with no <c>Command</c>, the second bound to the negation of the
    /// first.
    /// </summary>
    [AvaloniaFact]
    public void QualityPanelView_TheBaselineSegment_IsTwoToggleButtonsOverOneBoolAndItsNegation()
    {
        var model = Populated();
        var (window, view) = Show(model);
        var session = Named<ToggleButton>(view, "BaselineSession");
        var rig = Named<ToggleButton>(view, "BaselineRig");

        Assert.Null(session.Command);
        Assert.Null(rig.Command);
        Assert.True(session.IsChecked);
        Assert.False(rig.IsChecked);

        model.IsSessionBaseline = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsChecked);
        Assert.True(rig.IsChecked);

        session.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.IsSessionBaseline);
        Assert.False(rig.IsChecked);

        window.Close();
    }

    /// <summary>
    /// Spec 12.13: "a mark in a fixed trailing slot present on every cell, so the digits keep one
    /// right edge whether a row failed or not". The slot is rendered on a passing cell and on a
    /// failed one alike, and the two rows' digits share one right edge.
    /// </summary>
    /// <remarks>Fails when the trailing slot is added only to a failed cell, which shifts the
    /// digits of every failed row by the mark's width and is the defect the spec sentence
    /// describes.</remarks>
    [AvaloniaFact]
    public void QualityPanelView_EveryMetricCell_RendersItsTrailingSlotMarkedOrNot()
    {
        var (window, view) = Show(Populated());
        var rows = Rows(view);

        var passValue = Named<TextBlock>(rows[0], "HfrValue");
        var passMark = Named<TextBlock>(rows[0], "HfrMark");
        var failValue = Named<TextBlock>(rows[1], "HfrValue");
        var failMark = Named<TextBlock>(rows[1], "HfrMark");

        // The slot exists on both rows, and only the failed one carries a mark in it.
        Assert.True(passMark.IsVisible);
        Assert.True(failMark.IsVisible);
        Assert.Equal("", passMark.Text);
        Assert.Equal(VerdictCellViewModel.FailureMark, failMark.Text);

        // And the digits keep one right edge: the slot takes the same width on both rows, and a
        // slot that were only laid out on the failed cell would measure zero on the passing one.
        Assert.True(passMark.Bounds.Width > 0d);
        Assert.Equal(passMark.Bounds.Width, failMark.Bounds.Width, precision: 3);
        Assert.Equal(
            passValue.Bounds.Right,
            failValue.Bounds.Right,
            precision: 3);

        window.Close();
    }

    /// <summary>Spec 12.13's sentence under the table, verbatim.</summary>
    [AvaloniaFact]
    public void QualityPanelView_CarriesTheLiteralSentenceUnderTheTable()
    {
        var (window, view) = Show(Populated());

        Assert.Equal(
            "Filters apply to light frames only. All other files are copied unchanged.",
            Named<TextBlock>(view, "FooterNote").Text);

        window.Close();
    }

    /// <summary>Spec 12.13's empty state, verbatim, with no table beside it.</summary>
    [AvaloniaFact]
    public void QualityPanelView_WithNoFrames_RendersTheLiteralEmptyState()
    {
        var model = QualityPanelViewModelTests.Build([], out _);
        var (window, view) = Show(model);

        var empty = Named<TextBlock>(view, "EmptyState");
        Assert.True(empty.IsVisible);
        Assert.Equal("No light frames in the selected nights", empty.Text);
        Assert.Empty(Rows(view));
        Assert.False(Named<Grid>(view, "HeaderRow").IsVisible);

        window.Close();
    }

    /// <summary>
    /// The panel lays out inside its real allotment with no horizontal overflow, at the export
    /// window's opening width and again at its <c>MinWidth</c>: the File column is the star column
    /// and absorbs whatever the nine fitted columns leave over.
    /// </summary>
    /// <remarks>Fails when a fitted column grows past what the narrow allotment can carry, which
    /// puts the table into a horizontal scroll the export page's own scroller has disabled.
    /// </remarks>
    /// <param name="allotment">The panel's slot, read from the window's own declared widths.</param>
    /// <param name="fileColumnFloor">What the star File column must still carry at that allotment.
    /// One floor per row rather than one constant for both, because the two rows do not have the
    /// same slack and a single figure hides which of them is the binding one.</param>
    [AvaloniaTheory]
    [InlineData(PanelAllotment, 170d)]
    [InlineData(MinimumAllotment, 64d)]
    public void QualityPanelView_LaysOutInsideItsAllotment_WithNoHorizontalOverflow(
        double allotment,
        double fileColumnFloor)
    {
        var (window, view) = Show(Populated(), allotment);
        var row = Rows(view)[0];

        Assert.True(view.Bounds.Width <= allotment + 0.5d);
        Assert.True(row.Bounds.Width <= allotment + 0.5d);
        var scroller = Named<ScrollViewer>(view, "RowsScroller");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5d);

        // The File column is the one star column, so it carries whatever the fitted columns leave.
        // Cell origin to cell origin, so the figure includes the File cell's own gutter. The fitted
        // columns are as wide as their headings (units, sort glyph slot, mark inset) and a figure
        // never trims (spec.md item 3), so at the window's MinWidth the file name is what gives:
        // it trims with the whole path on its tooltip.
        Assert.True(
            LeftEdge(row, 9, view) - LeftEdge(row, 8, view) >= fileColumnFloor,
            $"the File column is {LeftEdge(row, 9, view) - LeftEdge(row, 8, view):F1} px at an "
            + $"allotment of {allotment:F0}, under its floor of {fileColumnFloor:F0}");

        window.Close();
    }

    /// <summary>
    /// The Night heading sits over its dates although only the rows are inside the scroller: the
    /// header row carries the reserved bar's width, so the two Night columns start at one x.
    /// </summary>
    /// <remarks>Fails when the header's ScrollInset and the rows' reserved bar disagree, which puts
    /// the label and the dates under it out of line.</remarks>
    [AvaloniaFact]
    public void QualityPanelView_TheNightHeading_StartsWhereItsDatesStart()
    {
        var (window, view) = Show(Populated());
        var header = Named<Grid>(view, "HeaderRow");
        var row = Rows(view)[0];

        Assert.Equal(LeftEdge(header, 9, view), LeftEdge(row, 9, view), precision: 0);

        window.Close();
    }

    /// <summary>
    /// The Night cell draws a whole ISO date at the export window's opening width.
    /// </summary>
    /// <remarks>Launched-app look D1: a fixed Night column once trimmed every date to
    /// "2025-03-...". The column is now Auto and a text column without a cap never trims.</remarks>
    [AvaloniaFact]
    public void QualityPanelView_AtTheWindowsOpeningWidth_TheNightCellDrawsAWholeIsoDate()
    {
        var (window, view) = Show(Populated());
        var cell = Named<TextBlock>(Rows(view)[0], "NightCell");

        Assert.Equal("2025-03-20", cell.Text);
        Assert.DoesNotContain(cell.TextLayout.TextLines, line => line.HasCollapsed);

        window.Close();
    }

    // Where a grid's cell starts, in the panel's own coordinates, so a header cell and a row cell
    // are compared against one origin rather than against their own parents.
    private static double LeftEdge(Grid grid, int column, Visual origin)
    {
        var cell = grid.Children.First(child => Grid.GetColumn(child) == column);
        return cell.TranslatePoint(default, origin)!.Value.X;
    }

    // A sort header's title, not its direction glyph, which leads in a numeric heading.
    private static string HeaderTitle(Control cell) => cell switch
    {
        TextBlock label => label.Text ?? "",
        _ => cell.GetVisualDescendants().OfType<TextBlock>().First(block => !block.Classes.Contains("tc-sortglyph")).Text ?? "",
    };

    private static string ChipLabel(Control chip) => chip switch
    {
        Button ghost => ghost.Content as string ?? "",
        _ => chip.GetVisualDescendants().OfType<TextBlock>().First().Text ?? "",
    };
}
