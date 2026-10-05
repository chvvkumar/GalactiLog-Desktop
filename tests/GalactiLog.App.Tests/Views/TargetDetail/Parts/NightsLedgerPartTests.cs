using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Layouts;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content cases for the Nights ledger, hosted on the part alone.
public class NightsLedgerPartTests
{
    private const double WideLedgerWidth = 640d;
    private const double NarrowLedgerWidth = 520d;

    [AvaloniaFact]
    public void TargetDetailView_OpensOnTheNewestNight_Lit()
    {
        // The comp opens on the newest night, and the selected row is lit rather than coloured:
        // no accent hue carries state anywhere on this page.
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        Show(view);

        var ledger = view.Named<ListBox>("NightsLedger");
        Assert.Same(harness.ViewModel.Sessions[0], ledger.SelectedItem);
        Assert.Same(harness.ViewModel.SelectedSession, ledger.SelectedItem);

        var selected = Assert.IsType<ListBoxItem>(ledger.ContainerFromIndex(0));
        Assert.True(selected.IsSelected);

        // The lit ink is ColorAccent, which is the comp's var(--lit) and a near-white in this
        // theme: light carries state, colour does not (coordinator ruling 3).
        var night = CellAt(LedgerRowAt(view, 0), 1);
        Assert.Equal(TokenColor(view, "ColorAccent"), ((ISolidColorBrush)night.Foreground!).Color);
        Assert.NotEqual(
            TokenColor(view, "ColorAccent"),
            ((ISolidColorBrush)CellAt(LedgerRowAt(view, 1), 1).Foreground!).Color);
    }

    [AvaloniaFact]
    public void TargetDetailView_LedgerTargetRow_CarriesTheTargetMeans()
    {
        // The comp's .ref row: the target's own averages in the same nine columns as every night,
        // so the comparison is made by alignment.
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        Show(view);

        var totals = view.Named<Border>("TotalsRow");
        Assert.True(totals.IsVisible);

        var row = Assert.IsType<Grid>(totals.Child);
        Assert.Equal("All 2 nights", CellAt(row, 1).Text);
        Assert.Equal("12.4", CellAt(row, 2).Text);
        Assert.Equal("148", CellAt(row, 3).Text);
        Assert.Equal("2.34", CellAt(row, 4).Text);
        Assert.Equal("1,500", CellAt(row, 8).Text);
    }

    [AvaloniaFact]
    public void TargetDetailView_AWorseCell_RendersInTheWorseInk()
    {
        // The fixture's nights detect 1,490 stars against the target's mean of 1,500, which is
        // more than one whole star worse, so the stars cell speaks.
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        Show(view);

        Assert.True(harness.ViewModel.Sessions[0].IsWorseStars);
        var stars = CellAt(LedgerRowAt(view, 0), 8);
        Assert.Equal("1,490", stars.Text);
        Assert.Equal(TokenColor(view, "ColorMetricWorst"), ((ISolidColorBrush)stars.Foreground!).Color);
    }

    [AvaloniaFact]
    public void TargetDetailView_ABetterCell_RendersInTheOrdinaryInk()
    {
        // Better stays silent. The fixture's HFR median is 2.30 against a 2.34 mean.
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        Show(view);

        Assert.False(harness.ViewModel.Sessions[0].IsWorseHfr);
        var hfr = CellAt(LedgerRowAt(view, 0), 4);
        Assert.Equal("2.30", hfr.Text);
        Assert.NotEqual(TokenColor(view, "ColorMetricWorst"), ((ISolidColorBrush)hfr.Foreground!).Color);
    }

    [AvaloniaFact]
    public void TargetDetailView_QualityHeaders_CarryTheComparisonTooltip()
    {
        // P12 R4: a night median against a target mean is not a like-for-like comparison, and
        // every one of the five quality headers says so.
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        Show(view);

        var header = view.Named<Grid>("LedgerHeaderRow");
        var labelled = header.Children
            .OfType<TextBlock>()
            .Where(cell => Equals(ToolTip.GetTip(cell), "Night medians against target means"))
            .Select(cell => cell.Text)
            .ToList();

        Assert.Equal(["HFR px", "Ecc", "FWHM \"", "RMS \"", "Stars"], labelled);
    }

    [AvaloniaFact]
    public async Task TargetDetailView_AReload_KeepsTheSelectedNight_WithTheLedgerBound()
    {
        // Review P2-1. The view-model case cannot see this: Sessions.Clear() raises a Reset, the
        // bound ListBox writes its own selection back before the refill finishes, and a page that
        // resolved the kept night from SelectedSession after the diff always landed on the newest
        // one. Only a shown page with the ledger attached can fail.
        // The post seam goes to the dispatcher rather than running inline: a publish reaches bound
        // controls through NotifyActionState, and Button.Command calls VerifyAccess, so a shown
        // page cannot be published to from the thread pool.
        using var harness = Factory.Create(post: action => Dispatcher.UIThread.Post(action)).Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        Show(view);

        var ledger = view.Named<ListBox>("NightsLedger");
        ledger.SelectedItem = harness.ViewModel.Sessions[1];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Factory.FirstSession, harness.ViewModel.SelectedSession!.SessionDate);

        // A page reload through the production path: a changed re-resolve calls Load, which
        // republishes and runs the whole session diff.
        await harness.ViewModel.ReResolveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        harness.Settle();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Factory.FirstSession, harness.ViewModel.SelectedSession!.SessionDate);
        Assert.Same(harness.ViewModel.SelectedSession, ledger.SelectedItem);
        Assert.Same(harness.ViewModel.Sessions[1], ledger.SelectedItem);
    }

    [AvaloniaFact]
    public void TargetDetailView_EachNightRow_CarriesACheckBox()
    {
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        ShowAtThePagesAllotment(view);

        for (var index = 0; index < harness.ViewModel.Sessions.Count; index++)
        {
            var box = LedgerRowAt(view, index).Children.OfType<CheckBox>().Single();
            Assert.Equal(0, Grid.GetColumn(box));
            Assert.True(box.IsEffectivelyVisible);
            Assert.False(box.IsChecked);
        }

        harness.ViewModel.Sessions[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(LedgerRowAt(view, 0).Children.OfType<CheckBox>().Single().IsChecked);
    }

    [AvaloniaFact]
    public void TargetDetailView_TheTargetRow_CarriesNone()
    {
        // Spec 12.4: the target row is not a night, so it carries no check box.
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        ShowAtThePagesAllotment(view);

        var target = Assert.IsType<Grid>(view.Named<Border>("TotalsRow").Child);

        Assert.Empty(target.Children.OfType<CheckBox>());
    }

    [AvaloniaFact]
    public void TargetDetailView_TheHeaderBox_IsTriState()
    {
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        ShowAtThePagesAllotment(view);

        var box = view.Named<CheckBox>("LedgerSelectAllBox");
        Assert.True(box.IsThreeState);
        Assert.Equal(0, Grid.GetColumn(box));
        Assert.False(box.IsChecked);

        harness.ViewModel.Sessions[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(box.IsChecked);

        harness.ViewModel.Sessions[1].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsChecked);

        // Spec 12.4: it clears when any are checked, rather than inverting.
        box.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(box.IsChecked);
        Assert.Empty(harness.ViewModel.SelectedNights);
    }

    [AvaloniaFact]
    public void TargetDetailView_CheckingARow_DoesNotLightIt()
    {
        // Spec 12.4: checking a night does not select it. The box swallows its own pointer press
        // so the ListBoxItem beneath it never reads the click as a selection.
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel };
        ShowAtThePagesAllotment(view);

        var window = Assert.IsType<Window>(view.GetVisualRoot());
        var lit = harness.ViewModel.SelectedSession;
        Assert.Same(harness.ViewModel.Sessions[0], lit);

        var box = LedgerRowAt(view, 1).Children.OfType<CheckBox>().Single();
        var point = box.TranslatePoint(new Point(box.Bounds.Width / 2d, box.Bounds.Height / 2d), window);
        Assert.NotNull(point);

        window.MouseDown(point!.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // The box took the click, the row did not.
        Assert.True(box.IsChecked);
        Assert.True(harness.ViewModel.Sessions[1].IsChecked);
        Assert.Same(lit, harness.ViewModel.SelectedSession);
    }

    [AvaloniaFact]
    public void AstroBinCsvMenuItem_LabelAndCommandFollowTheSelection()
    {
        using var page = BuildPage();
        var view = new NightsLedgerPart { DataContext = page };
        Show(view);

        var owner = view.Named<Button>("ExportButton");
        owner.Flyout!.ShowAt(owner);
        Dispatcher.UIThread.RunJobs();
        var item = Assert.IsType<MenuFlyout>(owner.Flyout)
            .Items.OfType<MenuItem>().First(m => m.Name == "AstroBinCsvMenuItem");

        Assert.Equal("AstroBin CSV", item.Header);
        Assert.False(item.Command!.CanExecute(null));

        page.Sessions[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("AstroBin CSV (1)", item.Header);
        Assert.True(item.Command.CanExecute(null));
    }

    [AvaloniaFact]
    public void ExportButton_CarriesTheCheckedNightCount()
    {
        // Red if the button reads "Export" with nights checked, or keeps a count at zero.
        using var page = BuildPage();
        var view = new NightsLedgerPart { DataContext = page };
        Show(view);
        var button = view.Named<Button>("ExportButton");

        Assert.Equal("Export", button.Content);

        page.Sessions[0].IsChecked = true;
        page.Sessions[1].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Export (2)", button.Content);

        page.Sessions[0].IsChecked = false;
        page.Sessions[1].IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Export", button.Content);
    }

    [AvaloniaFact]
    public void TargetDetailView_AboveTheBreakpoint_TheLedgerShowsItsFiltersColumn()
    {
        // The other half of ruling Q12, so the collapse above is a rule rather than a deletion.
        using var harness = Factory.Create().Settle();
        harness.ViewModel.ApplyWidth(1700);
        var view = AtLedgerWidth(harness.ViewModel, WideLedgerWidth);
        var window = new Window { Width = 1700, Height = PageAllotmentHeight, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.ViewModel.IsWide);
        Assert.True(view.Named<TextBlock>("LedgerFiltersHead").IsEffectivelyVisible);
        Assert.True(view.Named<ItemsControl>("TotalsRowFilters").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void LitRowContent_IsPresentedUnderTheLitRow_AndFollowsTheSelection()
    {
        // Red if the slot is missing, sits on another row, or stays behind on the old night.
        using var harness = Factory.Create().Settle();
        var slot = new Border { Height = 12 };
        var view = new NightsLedgerPart { DataContext = harness.ViewModel, LitRowContent = slot };
        Show(view);

        var item = slot.FindAncestorOfType<ListBoxItem>();
        Assert.NotNull(item);
        Assert.Same(harness.ViewModel.Sessions[0], item!.DataContext);
        Assert.True(slot.IsEffectivelyVisible);

        harness.ViewModel.SelectedSession = harness.ViewModel.Sessions[1];
        Dispatcher.UIThread.RunJobs();

        Assert.Same(harness.ViewModel.Sessions[1], slot.FindAncestorOfType<ListBoxItem>()?.DataContext);
    }

    [AvaloniaFact]
    public void IsCompact_DropsTheFramesAndFiltersColumns()
    {
        // Red if the Frames column still holds its width or draws its cells when compact.
        using var harness = Factory.Create().Settle();
        var view = new NightsLedgerPart { DataContext = harness.ViewModel, Width = 504 };
        Show(view);
        Assert.True(CellAt(LedgerRowAt(view, 0), 3).Bounds.Width > 0);

        view.IsCompact = true;
        Dispatcher.UIThread.RunJobs();

        var frames = CellAt(LedgerRowAt(view, 0), 3);
        Assert.False(frames.IsEffectivelyVisible && frames.Bounds.Width > 0);
        Assert.Equal(0d, LedgerRowAt(view, 0).ColumnDefinitions[3].ActualWidth, 0.5);
        Assert.False(view.Named<TextBlock>("LedgerFiltersHead").IsEffectivelyVisible);
    }

    // The width the retired page ledger gave the cases below: 520 narrow, 640 wide.
    private static NightsLedgerPart AtLedgerWidth(TargetDetailViewModel page, double width, bool compact = false)
        => new() { DataContext = page, Width = width, IsCompact = compact };

    /// <summary>The RMS column's drawn digit cell (Grid.Column 7 of a ledger row), whichever of
    /// the two shapes that row uses: a bare <c>TextBlock</c> or a value-plus-mark
    /// <c>StackPanel</c>, where the value is always the first child.</summary>
    private static TextBlock RmsValueCellOf(Grid row)
    {
        var cell = row.Children.Single(c => Grid.GetColumn(c) == 7);
        return cell as TextBlock ?? ((StackPanel)cell).Children.OfType<TextBlock>().First();
    }

    private static TextBlock MarkCellOf(Grid row, string name)
        => row.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == name);
    /// <summary>
    /// The ISO date the ledger's Night column exists to show, measured in the cell's own typeface
    /// and size rather than guessed at. Under the headless harness that is the stub face, which is
    /// the only face any of these cases can be red or green against; the markup's own floor is the
    /// same string measured in Atkinson Hyperlegible Next at the shipped 18 px root.
    /// </summary>
    private static double IsoDateWidth(TextBlock cell)
        => new FormattedText(
            "2025-03-20",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(cell.FontFamily, cell.FontStyle, cell.FontWeight),
            cell.FontSize,
            Brushes.White).Width;

    [AvaloniaFact]
    public void NightsLedgerPart_LedgerColumns_ReportEqualWidthsAfterLayout()
    {
        // The SharedSizeGroup proof: the header cell, the target row's cell and the first night
        // row's cell in the same column are one width, which is what stops the collision
        // panel-density.md diagnosed on the retired card.
        //
        // The target row's HFR cell is seeded with a figure far wider than anything the column's
        // own content or its declared MinWidth would produce, because without one every cell in
        // the column sits at the minimum and the case passes with every SharedSizeGroup removed
        // (phase review P2-2). With the seed the header and the night row follow the target row's
        // width only while the group is there.
        using var harness = Factory
            .Create(get: _ => Factory.PopulatedDetail(
                totals: Factory.PopulatedTotals() with { AvgHfr = 123456.789d }))
            .Settle();
        var view = AtLedgerWidth(harness.ViewModel, NarrowLedgerWidth);
        Show(view);
        Dispatcher.UIThread.RunJobs();

        var header = CellAt(view.Named<Grid>("LedgerHeaderRow"), 4);
        var target = CellAt(Assert.IsType<Grid>(view.Named<Border>("TotalsRow").Child), 4);
        var night = CellAt(LedgerRowAt(view, 0), 4);

        Assert.Equal("123456.79", target.Text);
        Assert.True(header.Bounds.Width > 0, "the ledger did not lay out");

        // Wider than the column's declared minimum and wider than the header label, so only the
        // shared group can be carrying it.
        Assert.True(
            header.Bounds.Width > 80d,
            $"the seeded figure did not widen the column; it measured {header.Bounds.Width}");

        Assert.Equal(header.Bounds.Width, target.Bounds.Width, 3);
        Assert.Equal(header.Bounds.Width, night.Bounds.Width, 3);
    }

    [AvaloniaFact]
    public void NightsLedgerPart_AtThePagesAllotment_TheNarrowLedgerFitsInsideItsWidth()
    {
        // Phase review P1-1, measured at Question Modes' compact width: the nine declared column
        // minimums summed to 600, so at the shipped window the Stars and Filters cells were laid
        // out past the ledger's right edge and clipped against the workbench gutter. Ruling Q12's filters
        // column now collapses below the breakpoint and the Night column is the star that absorbs
        // the rest, so the last visible cell ends inside the ledger whatever the figures measure.
        using var harness = Factory.Create().Settle();
        var view = AtLedgerWidth(harness.ViewModel, LedgerColumn.CompactWidth, compact: true);
        ShowAtThePagesAllotment(view);

        var ledger = view.Named<Grid>("Ledger");
        Assert.False(harness.ViewModel.IsWide);
        Assert.Equal(LedgerColumn.CompactWidth, ledger.Bounds.Width, 3);

        // Ruling Q12: no filters column below the breakpoint, in all three grids.
        Assert.False(view.Named<TextBlock>("LedgerFiltersHead").IsEffectivelyVisible);
        Assert.False(view.Named<ItemsControl>("TotalsRowFilters").IsEffectivelyVisible);

        // The last visible cell of the header, the target row and the first night row all end
        // inside the ledger.
        foreach (var (row, name) in new (Grid Row, string Name)[]
                 {
                     (view.Named<Grid>("LedgerHeaderRow"), "the header row"),
                     (Assert.IsType<Grid>(view.Named<Border>("TotalsRow").Child), "the target row"),
                     (LedgerRowAt(view, 0), "the first night row"),
                 })
        {
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
    public void NightsLedgerPart_AtThePagesAllotment_TheNightColumnDrawsAWholeIsoDate()
    {
        // Phase 12 verification, blocker 1. At the shipped 1280x800 window with the rail expanded
        // the ledger's Night column drew nothing at all: seven Auto columns sized by their headers
        // took the row, and Avalonia resolves every Auto column before it resolves a star, so the
        // one star column was left with about 16 px. No night in the ledger could be told from
        // another and the target row lost its "All 3 nights" label. The Night column now carries a
        // MinWidth measured off the date, the seven numeric columns carry a cap each, and the cell
        // gutters came down, so the date is drawn whole.
        using var harness = Factory.Create().Settle();
        var view = AtLedgerWidth(harness.ViewModel, NarrowLedgerWidth);
        ShowAtThePagesAllotment(view);

        Assert.False(harness.ViewModel.IsWide);

        var headerNight = CellAt(view.Named<Grid>("LedgerHeaderRow"), 1);
        var rowNight = CellAt(LedgerRowAt(view, 0), 1);
        var iso = IsoDateWidth(rowNight);
        Assert.True(iso > 0d, "the harness measured the ISO date as nothing, so this case cannot fail");

        Assert.True(
            rowNight.Bounds.Width >= iso,
            $"the first night's date cell is {rowNight.Bounds.Width} px wide, less than the {iso} px the date needs");
        Assert.True(
            headerNight.Bounds.Width >= iso,
            $"the Night header cell is {headerNight.Bounds.Width} px wide, less than the {iso} px a date needs");

        // The target row's label is the other half of the same collapse: it is laid out in the
        // same column and it vanished with it.
        var label = view.Named<TextBlock>("TotalsRowLabel");
        Assert.StartsWith("All ", label.Text);
        Assert.True(
            label.Bounds.Width > 0d,
            "the target row's label was laid out with no width at all");
    }

    /// <summary>Review P2-2: the guiding RMS column's SharedSizeGroup carries a pinned MinWidth 56
    /// and MaxWidth 58 (HANDOFF 5.1 items 7b and 9h; the "Auto column does not shrink" rule is
    /// this file's own header comment above the ledger style block). This case is both the "does
    /// it fit" measurement the fix required before landing, and the standing regression proof: it
    /// runs at the narrow ledger and the wide one, and at the default and the Extra Large
    /// (general.text_size "x-large", 20 px root) sizes, so a later change to either the value or
    /// the mark's fixed width cannot silently start clipping again.</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NightsLedgerPart_TheGuidingRmsMark_FitsTheLedgerColumnAndKeepsOneRightEdge(
        bool wide, bool extraLarge)
    {
        var marked = Factory.Session(Factory.LastSession) with
        {
            GuidingProvenance = GuidingRmsProvenance.Phd2,
            MedianGuidingRmsArcsec = 0.52d,
        };
        var unmarked = Factory.Session(Factory.FirstSession) with
        {
            GuidingProvenance = GuidingRmsProvenance.Csv,
            MedianGuidingRmsArcsec = 0.41d,
        };
        // The totals row is unmarked and reads its own independent average (never derived from
        // the two sessions above), matching the shape the coordinator's second capture showed:
        // 0.49 on the All-nights row against 0.55 and 0.43 on the two seeded nights.
        var totals = Factory.PopulatedTotals() with { AvgGuidingRmsArcsec = 0.49d };

        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(totals: totals, sessions: [marked, unmarked])).Settle();
        if (wide)
        {
            harness.ViewModel.ApplyWidth(1700);
        }

        var view = AtLedgerWidth(
            harness.ViewModel,
            wide ? WideLedgerWidth : NarrowLedgerWidth);
        var window = wide
            ? new Window { Width = 1700, Height = PageAllotmentHeight, Content = view }
            : new Window { Width = PageAllotmentWidth, Height = PageAllotmentHeight, Content = view };
        if (extraLarge)
        {
            window.FontSize = 20d;
        }

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(wide, harness.ViewModel.IsWide);

        var ledger = view.Named<Grid>("Ledger");

        // Narrow only: the filters column leaves the shared size group entirely below the
        // breakpoint (ruling Q12), so "the last visible cell" is a numeric column here and this
        // sweep is the established pattern's own overflow check. At the wide ledger the last
        // visible cell is the Filters column, whose own fit is not this fix's to prove or to
        // re-litigate; the RMS column's own MinWidth/MaxWidth check below covers this fix instead.
        if (!wide)
        {
            foreach (var (row, name) in new (Grid Row, string Name)[]
                     {
                         (view.Named<Grid>("LedgerHeaderRow"), "the header row"),
                         (Assert.IsType<Grid>(view.Named<Border>("TotalsRow").Child), "the target row"),
                         (LedgerRowAt(view, 0), "the marked night row"),
                         (LedgerRowAt(view, 1), "the unmarked night row"),
                     })
            {
                var last = row.Children
                    .Where(cell => cell.IsEffectivelyVisible && Grid.GetColumnSpan(cell) == 1)
                    .OrderByDescending(Grid.GetColumn)
                    .First();
                var right = last.TranslatePoint(new Point(last.Bounds.Width, 0), ledger);
                Assert.NotNull(right);
                Assert.True(
                    right!.Value.X <= ledger.Bounds.Width + 0.5d,
                    $"{name} draws its last cell to {right.Value.X:F1} at xl={extraLarge}, "
                    + $"outside the {ledger.Bounds.Width:F1} px ledger");
            }
        }

        // The RMS column's own pinned MinWidth 56 and MaxWidth 58 (TargetDetailView.axaml) are
        // unmoved by this fix: the value-plus-mark StackPanel must measure inside that budget at
        // both ledger widths and both text sizes, which is the "does it fit" question the fix
        // required answering before it could land.
        var markedPanel = (StackPanel)LedgerRowAt(view, 0).Children.Single(c => Grid.GetColumn(c) == 7);
        Assert.True(
            markedPanel.Bounds.Width <= 58.5d,
            $"the RMS column measured {markedPanel.Bounds.Width:F1} px, over its 58 px cap, "
            + $"at wide={wide} xl={extraLarge}");

        // The marked row's value cell (0.52, dagger shown), the unmarked row's (0.41, no dagger)
        // AND the All-nights totals row's (0.49, never marked this phase) share one right edge:
        // the coordinator's follow-up finding, read off the second capture, is that the totals
        // row's RMS cell was still the old single, un-boxed TextBlock, so it right-aligned into
        // the column's full (value + mark) width instead of the value box alone and sat about
        // LedgerRmsMarkWidth to the right of the two night rows.
        var markedValue = RmsValueCellOf(LedgerRowAt(view, 0));
        var unmarkedValue = RmsValueCellOf(LedgerRowAt(view, 1));
        var totalsValue = RmsValueCellOf(Assert.IsType<Grid>(view.Named<Border>("TotalsRow").Child));
        var markedMark = LedgerRowAt(view, 0).GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "LedgerRmsMarkCell");

        Assert.Equal("0.52", markedValue.Text);
        Assert.Equal("0.41", unmarkedValue.Text);
        Assert.Equal("0.49", totalsValue.Text);
        Assert.Equal("†", markedMark.Text);

        var markedRight = markedValue.TranslatePoint(new Point(markedValue.Bounds.Width, 0), ledger)!.Value.X;
        var unmarkedRight = unmarkedValue.TranslatePoint(new Point(unmarkedValue.Bounds.Width, 0), ledger)!.Value.X;
        var totalsRight = totalsValue.TranslatePoint(new Point(totalsValue.Bounds.Width, 0), ledger)!.Value.X;
        Assert.Equal(unmarkedRight, markedRight, 1);
        Assert.True(
            Math.Abs(totalsRight - markedRight) <= 0.5d,
            $"the totals row's value cell ends at {totalsRight:F1}, the night rows' at {markedRight:F1}, "
            + $"at wide={wide} xl={extraLarge}");

        // And the value's own text is not trimmed: its rendered width covers what "0.52" measures
        // in the cell's own face and size.
        var measured = new FormattedText(
            "0.52",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(markedValue.FontFamily, markedValue.FontStyle, markedValue.FontWeight),
            markedValue.FontSize,
            Brushes.White).Width;
        Assert.True(
            markedValue.Bounds.Width >= measured - 0.5d,
            $"the value cell is {markedValue.Bounds.Width:F1} px for text measuring {measured:F1} px "
            + $"at wide={wide} xl={extraLarge}");
    }

    /// <summary>Phase review P3-10: the mark cell keeps its declared width on an unmarked night,
    /// so a literal <c>ToolTip.Tip</c> on that cell offered "from a PHD2 guide log" over an empty
    /// box on every unmarked night row and on the totals row, whose mark box is empty by
    /// construction. A null tip is a control with no tooltip at all.</summary>
    [AvaloniaFact]
    public void NightsLedgerPart_TheGuidingRmsMarksTooltip_ExistsOnlyWhereTheMarkDoes()
    {
        var marked = Factory.Session(Factory.LastSession) with
        {
            GuidingProvenance = GuidingRmsProvenance.Phd2,
            MedianGuidingRmsArcsec = 0.52d,
        };
        var unmarked = Factory.Session(Factory.FirstSession) with
        {
            GuidingProvenance = GuidingRmsProvenance.Csv,
            MedianGuidingRmsArcsec = 0.41d,
        };

        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(sessions: [marked, unmarked])).Settle();
        var view = AtLedgerWidth(harness.ViewModel, NarrowLedgerWidth);
        ShowAtThePagesAllotment(view);

        var markedMark = MarkCellOf(LedgerRowAt(view, 0), "LedgerRmsMarkCell");
        var unmarkedMark = MarkCellOf(LedgerRowAt(view, 1), "LedgerRmsMarkCell");
        var totalsMark = MarkCellOf(
            Assert.IsType<Grid>(view.Named<Border>("TotalsRow").Child), "TotalsRmsMarkCell");

        Assert.Equal("†", markedMark.Text);
        Assert.Equal("from a PHD2 guide log", ToolTip.GetTip(markedMark));

        Assert.True(string.IsNullOrEmpty(unmarkedMark.Text));
        Assert.Null(ToolTip.GetTip(unmarkedMark));
        Assert.True(string.IsNullOrEmpty(totalsMark.Text));
        Assert.Null(ToolTip.GetTip(totalsMark));

        // The mark cell still occupies its own width on the unmarked row: the tooltip fix must
        // not have reached for IsVisible, which would move every value box's right edge.
        Assert.True(unmarkedMark.Bounds.Width > 0);
    }

    [AvaloniaFact]
    public void NightsLedgerPart_AfterTheWideLedgerHasBeenDrawn_TheNarrowFiltersColumnReservesNothing()
    {
        // The mechanism behind blocker 1, and the reason ruling Q12's IsVisible rule was not
        // enough on its own: an Avalonia shared size group only ever grows. Once the wide ledger
        // has been laid out the LedgerFilters group holds the widest filters cell it measured, and
        // hiding the cells does not give it back, so the narrow ledger went on reserving it and
        // the star Night column paid for it. Below the breakpoint the three filters columns now
        // leave the group altogether.
        using var harness = Factory.Create().Settle();
        harness.ViewModel.ApplyWidth(1700);
        var view = AtLedgerWidth(harness.ViewModel, WideLedgerWidth);
        var window = new Window { Width = 1700, Height = PageAllotmentHeight, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(harness.ViewModel.IsWide, "the ledger was never drawn wide, so nothing seeded the group");
        Assert.True(
            view.Named<Grid>("LedgerHeaderRow").ColumnDefinitions[9].ActualWidth > 0d,
            "the wide ledger reserved no filters width, so this case cannot fail");

        harness.ViewModel.ApplyWidth(PageAllotmentWidth);
        view.Width = NarrowLedgerWidth;
        Dispatcher.UIThread.RunJobs();
        Assert.False(harness.ViewModel.IsWide);

        var ledger = view.Named<Grid>("Ledger");
        foreach (var (row, name) in new (Grid Row, string Name)[]
                 {
                     (view.Named<Grid>("LedgerHeaderRow"), "the header row"),
                     (Assert.IsType<Grid>(view.Named<Border>("TotalsRow").Child), "the target row"),
                     (LedgerRowAt(view, 0), "the first night row"),
                 })
        {
            Assert.Equal(0d, row.ColumnDefinitions[9].ActualWidth, 3);

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

        var rowNight = CellAt(LedgerRowAt(view, 0), 1);
        Assert.True(
            rowNight.Bounds.Width >= IsoDateWidth(rowNight),
            $"the date cell is {rowNight.Bounds.Width} px wide after the ledger had been wide");
    }

    [AvaloniaFact]
    public void NightsLedgerPart_AtThePagesAllotment_TheLedgerStillFitsWithTheCheckColumn()
    {
        // Ruling C5: the check column takes the leading gutter and the 520 px narrow ledger does
        // not widen. The seven numeric columns keep their declared minimums and maximums.
        using var harness = Factory.Create().Settle();
        var view = AtLedgerWidth(harness.ViewModel, NarrowLedgerWidth);
        ShowAtThePagesAllotment(view);

        var ledger = view.Named<Grid>("Ledger");
        var header = view.Named<Grid>("LedgerHeaderRow");

        Assert.Equal(NarrowLedgerWidth, ledger.Bounds.Width, 3);
        Assert.True(
            header.ColumnDefinitions[0].ActualWidth
                >= view.Named<CheckBox>("LedgerSelectAllBox").DesiredSize.Width,
            $"the check column measured {header.ColumnDefinitions[0].ActualWidth}, too narrow for a box");

        // The last drawn cell still ends inside the ledger, which is ruling C5's whole constraint.
        var last = header.Children
            .Where(cell => cell.IsEffectivelyVisible && Grid.GetColumnSpan(cell) == 1)
            .OrderByDescending(Grid.GetColumn)
            .First();
        var right = last.TranslatePoint(new Point(last.Bounds.Width, 0), ledger);
        Assert.NotNull(right);
        Assert.True(
            right!.Value.X <= ledger.Bounds.Width,
            $"the header row draws its last cell to {right.Value.X}, outside the {ledger.Bounds.Width} px ledger");

        var minimums = new[] { 46d, 52d, 60d, 52d, 60d, 56d, 52d };
        var maximums = new[] { 52d, 62d, 97d, 54d, 62d, 58d, 64d };
        for (var column = 2; column <= 8; column++)
        {
            Assert.Equal(minimums[column - 2], header.ColumnDefinitions[column].MinWidth, 3);
            Assert.Equal(maximums[column - 2], header.ColumnDefinitions[column].MaxWidth, 3);
        }
    }

    [AvaloniaFact]
    public void NightsLedgerPart_TheNightColumnFloor_IsHigherWideThanNarrow()
    {
        // Phase review's carried observation (phase14c/phase-review.md section 7): one Nights
        // ledger row trimmed its ISO date to "2025-03-..." at a wide 640 px capture, because the
        // Night column's star floor sat at 104 with only a pixel or two of headroom for ten
        // tabular characters plus the cell's own 2 px left and right margin. Raised to 112 for
        // the wide ledger, where WideLedgerWidth (640) less NarrowLedgerWidth (520) leaves well
        // over 100 px of room to give it. The narrow ledger keeps 104: HANDOFF 5.1 item 7b
        // measured its own slack at exactly 2 px, with three numeric columns already at their
        // caps, so an 8 px raise there has nowhere to come from (fixer-list item 2).
        using var harness = Factory.Create().Settle();
        var view = AtLedgerWidth(harness.ViewModel, NarrowLedgerWidth);
        ShowAtThePagesAllotment(view);

        Assert.False(harness.ViewModel.IsWide);
        Assert.Equal(
            TargetDetailViewModel.NarrowNightColumnMinWidth,
            view.Named<Grid>("LedgerHeaderRow").ColumnDefinitions[1].MinWidth,
            3);

        harness.ViewModel.ApplyWidth(1700);
        view.Width = WideLedgerWidth;
        Dispatcher.UIThread.RunJobs();
        Assert.True(harness.ViewModel.IsWide, "the ledger was never drawn wide, so this case cannot prove the wide figure");

        Assert.Equal(
            TargetDetailViewModel.WideNightColumnMinWidth,
            view.Named<Grid>("LedgerHeaderRow").ColumnDefinitions[1].MinWidth,
            3);

        // The narrow ledger's own fit is unchanged (it keeps the original 104), and the existing
        // TargetDetailView_AtThePagesAllotment_TheLedgerStillFitsWithTheCheckColumn case is what
        // pins that it still fits; this case only pins the two figures the floor now takes.
    }

    [AvaloniaFact]
    public void NightsLedgerPart_TheNightRowsCheckBox_MeasuresInsideItsColumn()
    {
        // Polish wave 2 ruling 3: the box was clipped on its right because the leading column was
        // narrower than an empty Fluent CheckBox, and the 2 px lit edge beside it is gone. The box
        // is measured unconstrained, or it would report whatever width the column gave it.
        using var harness = Factory.Create().Settle();
        var view = AtLedgerWidth(harness.ViewModel, NarrowLedgerWidth);
        ShowAtThePagesAllotment(view);

        var row = LedgerRowAt(view, 0);
        var box = row.Children.OfType<CheckBox>().Single();
        var column = row.ColumnDefinitions[0].ActualWidth;
        box.Measure(Size.Infinity);

        Assert.True(
            box.DesiredSize.Width <= column,
            $"the night row's check box needs {box.DesiredSize.Width} px and its column is {column} px, so the box is clipped");
        Assert.Empty(row.Children.OfType<Rectangle>());
    }
}
