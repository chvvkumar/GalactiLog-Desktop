using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.Views;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.StatisticsViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Spec 18.3's view smoke scope: the view constructs, binds to a populated view-model and lays out
// non-zero without throwing; every resource key it names resolves (ThemeResourceTest covers that
// repository-wide); nothing sets FontSize (FontSizeTokenTest covers that repository-wide).
public class StatisticsViewTests
{
    private static (Window Window, StatisticsView View, StatisticsViewModel Page) Show(
        StatisticsViewModel? page = null)
    {
        ChartTheme.Apply();
        var model = page ?? Factory.Create();
        var view = new StatisticsView { DataContext = model };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, model);
    }

    [AvaloniaFact]
    public void Constructs_AndLaysOutNonZero()
    {
        var (_, view, page) = Show();

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        page.Dispose();
    }

    [AvaloniaFact]
    public void PopulatedPage_RendersTheOverviewTiles()
    {
        var (_, view, page) = Show();

        var labels = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("Total Integration", labels);
        Assert.Contains("Catalogued Size", labels);
        Assert.Contains("Avg per Target", labels);
        page.Dispose();
    }

    [AvaloniaFact]
    public void EveryChart_IsTransparent()
    {
        var (_, view, page) = Show();

        // Spec 13's first global-configuration bullet, "chart background transparent so the page
        // gradient shows through", which is an Avalonia control property and so lives in XAML
        // rather than in ChartTheme.
        var cartesian = view.GetVisualDescendants().OfType<CartesianChart>().ToList();
        Assert.NotEmpty(cartesian);
        Assert.All(cartesian, chart => Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(chart.Background).Color.A));

        var pie = view.GetVisualDescendants().OfType<PieChart>().ToList();
        Assert.All(pie, chart => Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(chart.Background).Color.A));
        page.Dispose();
    }

    [AvaloniaFact]
    public void TimelineIsShownFirst_AndTheCalendarToggleSwapsThem()
    {
        var (window, view, page) = Show();

        Assert.True(page.ShowTimeline);
        Assert.False(page.ShowCalendar);
        Assert.True(view.GetControl<StackPanel>("TimelinePane").IsVisible);
        Assert.False(view.GetControl<StackPanel>("CalendarPane").IsVisible);

        view.GetControl<ToggleButton>("CalendarToggle").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(page.ShowTimeline);
        Assert.True(view.GetControl<StackPanel>("CalendarPane").IsVisible);
        Assert.Single(view.GetVisualDescendants().OfType<CalendarHeatmap>());
        window.Close();
        page.Dispose();
    }

    [AvaloniaFact]
    public void EfficiencyLegend_IsHidden_WhenCoordinatesAreUnset()
    {
        var (_, view, page) = Show(Factory.Create(general: Factory.WithoutCoordinates));

        // Spec 8.4's last sentence, at the view level.
        Assert.False(view.GetControl<TextBlock>("EfficiencyLegend").IsVisible);
        page.Dispose();
    }

    [AvaloniaFact]
    public void EfficiencyLegend_CarriesTheWebWording_WhenCoordinatesAreSet()
    {
        var (_, view, page) = Show();

        var legend = view.GetControl<TextBlock>("EfficiencyLegend");
        Assert.True(legend.IsVisible);
        Assert.Equal(ImagingTimelineViewModel.LegendText, legend.Text);
        page.Dispose();
    }

    [AvaloniaFact]
    public void EmptyLibrary_RendersTheSpecEmptyState_AndNoSections()
    {
        var (_, view, page) = Show(Factory.Create(loadStats: () => Factory.Empty()));

        var empty = view.GetControl<TextBlock>("EmptyState");
        Assert.True(empty.IsVisible);
        Assert.Equal(StatisticsViewModel.EmptyStateText, empty.Text);
        Assert.False(view.GetControl<TextBlock>("FailureLine").IsVisible);
        page.Dispose();
    }

    [AvaloniaFact]
    public void FailedLoad_RendersTheFailureLine_AndNoEmptyState()
    {
        var (_, view, page) = Show(Factory.Create(
            loadStats: () => throw new InvalidOperationException("no database")));

        Assert.True(view.GetControl<TextBlock>("FailureLine").IsVisible);
        Assert.False(view.GetControl<TextBlock>("EmptyState").IsVisible);
        page.Dispose();
    }

    [AvaloniaFact]
    public void FwhmFrameCount_IsRenderedBesideBothMedians()
    {
        // Review finding M2: spec 12.5's "the count of frames carrying a fwhm value" and the web's
        // FwhmValue annotation. Both properties were built and neither was bound.
        var (_, view, page) = Show();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Equal("n=380", page.Performance.Rows[0].FwhmFrameCount);
        Assert.Equal("n=380", page.Inventory.Cameras[0].FwhmFrameCount);
        Assert.Contains("n=380", texts);
        page.Dispose();
    }

    [AvaloniaFact]
    public void ColumnHeaders_CarryTheirUnits()
    {
        // Review finding M5, and spec.md item 4: the unit once in the header, in the shared style.
        var (_, view, page) = Show();

        var headers = HeaderRows(view)
            .SelectMany(row => row.GetVisualDescendants().OfType<TextBlock>())
            .Select(text => text.Text ?? "")
            .ToList();
        Assert.Contains(TableHeads.Fwhm, headers);
        Assert.Contains(TableHeads.Hfr, headers);
        Assert.DoesNotContain(headers, header => header.Contains("arcsec", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, header => header.Contains("(px)", StringComparison.Ordinal));
        page.Dispose();
    }

    [AvaloniaFact]
    public void DataQuality_FiguresCarryTheirUnits_AndTheSectionHasAnEmptyState()
    {
        // Review findings M3 and M4.
        var (_, view, page) = Show();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("Avg HFR (px)", texts);
        Assert.Contains("Avg HFR (arcsec)", texts);
        Assert.Contains("Best HFR (px)", texts);
        Assert.Contains("Best HFR (arcsec)", texts);

        Assert.False(view.GetControl<TextBlock>("DataQualityEmptyState").IsVisible);
        var source = view.GetControl<TextBlock>("EccentricitySource");
        Assert.True(source.IsVisible);
        Assert.Contains("HFRStDev", source.Text!, StringComparison.Ordinal);
        page.Dispose();
    }

    [AvaloniaFact]
    public void PresetButtons_AreTheFiveSpecPresets()
    {
        var (_, view, page) = Show();

        var buttons = view.GetControl<StackPanel>("PresetButtons")
            .GetVisualDescendants()
            .OfType<Button>()
            .Select(button => button.Content?.ToString())
            .ToList();

        Assert.Equal(new[] { "All", "1Y", "Q", "M", "W" }, buttons);
        page.Dispose();
    }

    [AvaloniaFact]
    public void PresetButton_SetsTheGranularity()
    {
        var (_, view, page) = Show();
        var quarter = view.GetControl<StackPanel>("PresetButtons")
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button => (button.Content?.ToString()) == "Q");

        quarter.Command!.Execute(quarter.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TimelineRangePreset.Quarter, page.Timeline.RangePreset);
        Assert.Equal(TimelineGranularity.Weekly, page.Timeline.Granularity);
        page.Dispose();
    }

    [AvaloniaFact]
    public void GuidingSection_RendersBothCards_WhenARigHasARow()
    {
        var (_, view, page) = Show();

        Assert.True(view.GetControl<StackPanel>("GuidingSection").IsVisible);
        Assert.False(view.GetControl<StackPanel>("GuidingEmptyNotice").IsVisible);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("Guiding", texts);
        Assert.Contains("Dec:RA", texts);
        Assert.Contains("Guide exposure: 1000, 2000 ms", texts);
        Assert.Single(view.GetVisualDescendants().OfType<AltitudeArc>());
        page.Dispose();
    }

    [AvaloniaFact]
    public void GuidingSection_RendersTheEmptyNotice_WhenNoRigHasARow()
    {
        // Review finding P3-13: the theme is applied before the model is built, because building it
        // resolves BandBrushes and ArcBrushes from the merged dictionary. Both carry a documented
        // fallback so nothing here depended on the order, but the order should not read as an
        // accident.
        ChartTheme.Apply();
        var (_, view, page) = Show(Factory.Create(
            loadStats: () => Factory.Sample(guiding: Factory.NoGuiding(unmapped: 4))));

        var notice = view.GetControl<StackPanel>("GuidingEmptyNotice");
        Assert.True(notice.IsVisible);
        Assert.Equal(
            "Map profiles",
            view.GetControl<Button>("GuidingEmptyLink").Content?.ToString());
        Assert.Empty(view.GetVisualDescendants().OfType<AltitudeArc>());
        page.Dispose();
    }

    [AvaloniaFact]
    public void Scorecard_AtTheWindowMinimum_TrimsTheRigAndKeepsEveryFigureWhole()
    {
        // Spec.md item 3 at the page's real allotment: a figure never trims, so the rig name is the
        // one cell that gives. A rig name far wider than its column, because the rule only bites
        // when the content wants more width than the page has. Gated sessions too, so the gate
        // clause's column is drawn.
        ChartTheme.Apply();
        var model = Factory.Create(loadStats: () => Factory.Sample(guiding: Factory.Guiding(
            rigs: [Factory.Rig(telescope: new string('W', 120), gated: 2)])));
        var view = new StatisticsView { DataContext = model };
        var window = new Window { Width = 1024, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var row = view.GetControl<ItemsControl>("GuidingScorecardRows").GetVisualDescendants().OfType<TableRow>().First();
        var rig = (TextBlock)row.Children.First(cell => TableRow.GetCol(cell) == "rig");

        Assert.Contains(rig.TextLayout.TextLines, line => line.HasCollapsed);
        Assert.NotNull(ToolTip.GetTip(rig));
        Assert.All(row.ColumnDefinitions, column => Assert.True(
            column.ActualWidth > 0, "every scorecard column is arranged at a non-zero width"));
        TableAssert.Conventions(view);

        window.Close();
        model.Dispose();
    }

    /// <summary>
    /// Phase 15B fixer item 26, at the 1296 px width the Equipment performance header overlap was
    /// reported at.
    /// </summary>
    /// <remarks>
    /// The figures are Auto columns that never trim (spec.md item 3), so a header that wants more
    /// width widens its column and the table scrolls sideways rather than trimming or running into
    /// the cell beside it.
    /// </remarks>
    [AvaloniaFact]
    public void EquipmentPerformanceHeaders_NeitherTrimNorOverlap_At1296()
    {
        ChartTheme.Apply();
        var model = Factory.Create();
        var view = new StatisticsView { DataContext = model };
        var window = new Window { Width = 1296, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var headerRow = HeaderRows(view)
            .First(row => row.Children.OfType<TextBlock>().Any(text => text.Text == "Equipment"));

        var headers = headerRow.Children.OfType<TextBlock>().ToList();

        Assert.Equal(9, headers.Count);
        Assert.All(
            headers.Where(text => text.Classes.Contains("tc-num")),
            text => Assert.DoesNotContain(text.TextLayout.TextLines, line => line.HasCollapsed));
        Assert.All(headers, text => Assert.True(
            text.Bounds.Width > 0,
            "every header cell is arranged at a non-zero width"));

        // No header reaches into the next column, which is what "overlap" was.
        var ordered = headers.OrderBy(text => text.Bounds.X).ToList();
        for (var index = 1; index < ordered.Count; index++)
        {
            Assert.True(
                ordered[index - 1].Bounds.Right <= ordered[index].Bounds.X + 0.5d,
                $"the header at column {index - 1} stays out of column {index}");
        }

        window.Close();
        model.Dispose();
    }

    [AvaloniaFact]
    public void TheView_IsInTheLedgerVocabulary()
    {
        // The two-worlds rule (DESIGN.md section 4): a page taken into the Ledger vocabulary
        // declares no card style and leaves no non-zero CornerRadius. Written as a text scan over
        // the file, in ControlStyleScanTest's shape, so it reads as a rule rather than as a
        // snapshot of one render.
        var markup = Markup();

        Assert.DoesNotContain("Border.card", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"card\"", markup, StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(markup, @"CornerRadius\s*=").Select(match => match.Value));
    }

    [AvaloniaFact]
    public void TimelineChart_CarriesAllFourPointerHandlers()
    {
        // The wheel is wired in code-behind with handledEventsToo (WheelPassthrough), not here.
        // The sharpest hazard of the vocabulary rewrite: StatisticsTimelineInputTests drives the
        // handler methods directly rather than the events, so dropping an attribute here
        // compiles, silently drops a gesture and fails no other case. Without PointerCaptureLost a
        // drag interrupted by a window deactivation leaves a stale drag and an unresolved pending
        // click, which is the defect review P3 fixed.
        var markup = Markup();
        var chart = markup[markup.IndexOf("x:Name=\"TimelineChart\"", StringComparison.Ordinal)..];
        chart = chart[..chart.IndexOf("/>", StringComparison.Ordinal)];

        string[] handlers =
        [
            "PointerPressed=\"OnTimelinePointerPressed\"",
            "PointerMoved=\"OnTimelinePointerMoved\"",
            "PointerReleased=\"OnTimelinePointerReleased\"",
            "PointerCaptureLost=\"OnTimelinePointerCaptureLost\"",
        ];

        Assert.All(handlers, handler => Assert.Contains(handler, chart, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void GuidingSection_DrawsNoAltitudeCard_WhenNoSessionCarriesAnAltitude()
    {
        // Review finding P2-2, at the view level: a library whose sessions all lack an alt_deg has
        // scorecard rows and no band row, and the card below the scorecard must be one line rather
        // than a heading over an empty grid, two legends and an empty table.
        var (_, view, page) = Show(Factory.Create(loadStats: () => Factory.Sample(
            guiding: Factory.Guiding(rigs: [Factory.Rig()], bands: []))));

        Assert.True(view.GetControl<ItemsControl>("GuidingScorecardRows").IsVisible);
        Assert.True(view.GetControl<TextBlock>("AltitudeEmptyState").IsVisible);
        Assert.Equal(
            GuidingViewModel.NoRowsText,
            view.GetControl<TextBlock>("AltitudeEmptyState").Text);
        Assert.False(view.GetControl<ItemsControl>("AltitudeArcs").IsVisible);
        Assert.False(view.GetControl<TextBlock>("AltitudeLegend").IsVisible);
        Assert.False(view.GetControl<Button>("AltitudeTableBand").IsVisible);
        Assert.False(view.GetControl<StackPanel>("AltitudeTable").IsVisible);
        Assert.Empty(view.GetVisualDescendants().OfType<AltitudeArc>());
        page.Dispose();
    }

    [AvaloniaFact]
    public void GuidingSection_DrawsTheAltitudeCard_WhenABandHasARow()
    {
        // The other side of the same flag: the empty line is absent and the arcs are drawn.
        var (_, view, page) = Show();

        Assert.False(view.GetControl<TextBlock>("AltitudeEmptyState").IsVisible);
        Assert.True(view.GetControl<ItemsControl>("AltitudeArcs").IsVisible);
        Assert.True(view.GetControl<TextBlock>("AltitudeLegend").IsVisible);
        Assert.True(view.GetControl<Button>("AltitudeTableBand").IsVisible);
        page.Dispose();
    }

    [AvaloniaFact]
    public void Scorecard_TheHeaderColumns_AgreeWithTheRowColumns()
    {
        // Review finding P3-3: the header and the rows read one column set, and line up on it.
        var (_, view, page) = Show();

        var header = HeaderRows(view).First(row => row.Children.OfType<TextBlock>().Any(text => text.Text == "Rig")
            && row.Columns!.Id == "Score");
        var row = view.GetControl<ItemsControl>("GuidingScorecardRows")
            .GetVisualDescendants()
            .OfType<TableRow>()
            .First();

        Assert.Same(header.Columns, row.Columns);
        Assert.Equal(
            Lefts(header).Select(left => Math.Round(left, 1)),
            Lefts(row).Select(left => Math.Round(left, 1)));
        page.Dispose();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatisticsView_MeetsTableConventions(bool extraLarge)
    {
        ChartTheme.Apply();
        var model = Factory.Create();
        var view = new StatisticsView { DataContext = model };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        if (extraLarge)
        {
            window.FontSize = 20d;
        }

        window.Show();
        model.Performance.Rows[0].IsExpanded = true;
        model.Guiding.ToggleTableCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.GetControl<StackPanel>("AltitudeTable").IsVisible);
        TableAssert.Conventions(view);

        // The tables only: the page's wrapping captions are not table text.
        var tables = view.GetVisualDescendants().OfType<ScrollViewer>().Where(scroller => scroller.Classes.Contains("table")).ToList();
        Assert.Equal(4, tables.Count);
        Assert.All(tables, TextFit.AssertTextFitsItsBox);

        window.Close();
        model.Dispose();
    }

    private static IEnumerable<TableRow> HeaderRows(Control view)
        => view.GetVisualDescendants().OfType<TableRow>().Where(row => row.Kind == RowKind.Header);

    // Each column's left edge, in the row's own coordinates.
    private static IEnumerable<double> Lefts(TableRow row)
        => row.ColumnDefinitions.Select((_, index) => row.ColumnDefinitions.Take(index).Sum(column => column.ActualWidth));

    // The page's markup with its XML comments removed, so a rule named in a comment is not a
    // match. SourceScan.StripComments strips the C# forms and this file is XAML.
    private static string Markup() => Regex.Replace(
        File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "StatisticsView.axaml")),
        "<!--.*?-->",
        "",
        RegexOptions.Singleline);
}
