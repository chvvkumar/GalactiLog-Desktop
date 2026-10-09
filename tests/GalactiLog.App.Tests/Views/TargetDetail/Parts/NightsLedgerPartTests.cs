using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content cases for the Nights list, hosted on the part alone.
public class NightsLedgerPartTests
{
    // The part at its own width, as the sidebar column draws it fully open.
    private static NightsLedgerPart AtItsOwnWidth(TargetDetailViewModel page)
        => new() { DataContext = page, HorizontalAlignment = HorizontalAlignment.Left };

    private static LedgerPage WithColumns(params CustomColumnType[] types)
    {
        var columns = types
            .Select((type, index) => CustomColumnTestFactory.Define(
                "Col " + (index + 1), type, CustomColumnScope.Session, type == CustomColumnType.Dropdown ? ["High"] : [], order: index))
            .ToList();
        var harness = LedgerPage.Create(
            columns: columns,
            ledgerKeys: [.. columns.Select(column => column.Slug)],
            post: action => Dispatcher.UIThread.Post(action)).Settle();
        Dispatcher.UIThread.RunJobs();
        return harness;
    }

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
    public void TheList_HasTheCheckTheDateAndTheCustomColumnsOnly()
    {
        // Spec.md, Nights list: the metric and filter columns, the totals row and the outlier marks
        // are gone. Red against any of them left in the markup.
        using var harness = Factory.Create().Settle();
        var view = AtItsOwnWidth(harness.ViewModel);
        Show(view);

        var header = view.Named<TableRow>("LedgerHeaderRow");
        Assert.Equal(["check", "date", "custom"], header.Children.Select(TableRow.GetCol));
        Assert.Equal(["check", "date", "custom"], LedgerRowAt(view, 0).Children.Select(TableRow.GetCol));

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToList();
        foreach (var gone in new[] { "Hours", "Frames", "HFR px", "Ecc", "FWHM \"", "RMS \"", "Stars", "Filters" })
        {
            Assert.DoesNotContain(gone, texts);
        }

        Assert.DoesNotContain(texts, text => text.Length > 4 && text.StartsWith("All ", StringComparison.Ordinal) && char.IsDigit(text[4]));
        Assert.Null(view.NamedOrNull<Control>("TotalsRow"));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheList_FollowsTheTableConventions(bool extraLarge)
    {
        // The spine's census at the shipped window and at general.text_size "x-large" (20 px root).
        using var harness = WithColumns(CustomColumnType.Text, CustomColumnType.Boolean);
        var view = AtItsOwnWidth(harness.Page);
        var window = new Window { Width = 1280, Height = 800, Content = view };
        if (extraLarge)
        {
            window.FontSize = 20d;
        }

        window.Show();
        Dispatcher.UIThread.RunJobs();

        TableAssert.Conventions(view);
    }

    [AvaloniaFact]
    public void AnEmptyTextCell_ShowsAFaintDash_AndACheckCellIsCentred()
    {
        // Spec.md, Nights list: "-" for an empty custom text cell, and centred check box cells.
        using var harness = WithColumns(CustomColumnType.Text, CustomColumnType.Boolean);
        var view = AtItsOwnWidth(harness.Page);
        Show(view);

        // Row 1: the lit row's ink is the accent, which the watermark inherits.
        var row = LedgerRowAt(view, 1);
        var box = row.GetVisualDescendants().OfType<TextBox>().First(control => control.IsEffectivelyVisible);
        Assert.Equal(MetricText.Missing, box.Watermark);
        var dash = box.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == MetricText.Missing && block.IsEffectivelyVisible);
        Assert.Equal(TokenColor(view, "ColorTextTertiary"), ((ISolidColorBrush)dash.Foreground!).Color);

        var strip = row.Children.OfType<ItemsControl>().Single();
        var check = strip.GetRealizedContainers().Last();
        var checkBox = check.GetVisualDescendants().OfType<CheckBox>().First(control => control.IsEffectivelyVisible);
        var cellCentre = check.TranslatePoint(new Point(check.Bounds.Width / 2d, 0), view)!.Value.X;
        var boxCentre = checkBox.TranslatePoint(new Point(checkBox.Bounds.Width / 2d, 0), view)!.Value.X;
        Assert.Equal(cellCentre, boxCentre, 0.5);
    }

    [AvaloniaFact]
    public void OpenWidth_IsTheTablesNaturalWidth_AndCollapsedWidth_IsTheDatesRightEdge()
    {
        using var harness = WithColumns(CustomColumnType.Text, CustomColumnType.Boolean, CustomColumnType.Dropdown);
        var view = AtItsOwnWidth(harness.Page);
        Show(view);

        var header = view.Named<TableRow>("LedgerHeaderRow");
        Assert.Equal(header.ColumnDefinitions[0].ActualWidth + header.ColumnDefinitions[1].ActualWidth, view.CollapsedWidth, 0.5);
        Assert.Equal(header.ColumnDefinitions.Sum(column => column.ActualWidth), view.OpenWidth, 0.5);

        var date = CellAt(LedgerRowAt(view, 0), 1);
        Assert.DoesNotContain(date.TextLayout.TextLines, line => line.HasCollapsed);
    }

    [AvaloniaFact]
    public void OpenWidth_IsNeverNarrowerThanTheTitleLine()
    {
        // Ruling R8: with no custom column the table is a box and a date, narrower than the title
        // line; open is the title line's width, so the line is drawn whole on one line.
        using var harness = Factory.Create().Settle();
        var view = AtItsOwnWidth(harness.ViewModel);
        Show(view);

        var title = view.Named<StackPanel>("LedgerTitle");
        var actions = view.Named<StackPanel>("LedgerActions");
        var table = view.Named<SlideOverViewport>("LedgerViewport").NaturalWidth;
        Assert.True(view.OpenWidth > table + 1d, $"the title line is no wider than the {table} px table, so this case cannot fail");

        view.Width = view.OpenWidth;
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            title.Bounds.Right <= actions.Bounds.Left + 0.5,
            $"the title ends at {title.Bounds.Right} and the actions start at {actions.Bounds.Left}");
        Assert.Equal(title.Bounds.Center.Y, actions.Bounds.Center.Y, 1.5);
    }

    [AvaloniaFact]
    public void SwitchingAColumnOff_ShrinksOpenWidth_AndRaisesExtentsChanged()
    {
        // A shared size group only grows, so the strip's group starts a new generation when the
        // drawn set changes. Red against a strip that keeps the switched-off column's width.
        using var harness = WithColumns(CustomColumnType.Text, CustomColumnType.Text, CustomColumnType.Text, CustomColumnType.Text);
        var view = AtItsOwnWidth(harness.Page);
        Show(view);

        var open = view.OpenWidth;
        Assert.Equal(view.Named<SlideOverViewport>("LedgerViewport").NaturalWidth, open);
        var heading = view.Named<ItemsControl>("LedgerCustomHead").GetRealizedContainers().Last().Bounds.Width;
        var raised = 0;
        view.ExtentsChanged += (_, _) => raised++;

        var hidden = harness.Page.LedgerCustomHeadings.Last().Slug;
        harness.Columns.Write(DisplaySettings.LedgerHiddenTableId, [hidden]);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, harness.Page.LedgerCustomHeadings.Count);
        Assert.Equal(open - heading, view.OpenWidth, 0.5);
        Assert.True(raised > 0, "the extents moved and nothing was told");
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
    public void NightsLedgerPart_TheNightRowsCheckBox_IsCentredInItsColumn()
    {
        // The check column is the spine's CheckColumnWidth: the box plus a gutter each side, with
        // the box centred and never clipped.
        using var harness = Factory.Create().Settle();
        var view = AtItsOwnWidth(harness.ViewModel);
        ShowAtThePagesAllotment(view);

        var row = LedgerRowAt(view, 0);
        var box = row.Children.OfType<CheckBox>().Single();
        var column = row.ColumnDefinitions[0].ActualWidth;
        Assert.Equal(TableMetrics.CheckColumnWidth, column, 0.5);
        Assert.Equal(column / 2d, box.Bounds.X + box.Bounds.Width / 2d, 0.5);

        box.Measure(Size.Infinity);
        Assert.True(
            box.DesiredSize.Width <= column,
            $"the night row's check box needs {box.DesiredSize.Width} px and its column is {column} px, so the box is clipped");
    }
}
