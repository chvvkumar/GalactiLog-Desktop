using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.Views.TargetDetail.Parts;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Layouts;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Xunit.Abstractions;
using static GalactiLog.App.Tests.TestSupport.TargetLayoutProbes;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;
using Rows = GalactiLog.App.Tests.ViewModels.NightFilterMatrixViewModelTests;

namespace GalactiLog.App.Tests.Views.TargetDetail.Layouts;

// Layout C, Question Modes (spec 6.1).
public sealed class ModesLayoutViewTests(ITestOutputHelper output)
{
    private const double NarrowWidth = 1232;

    // The compact sidebar, which the window-width breakpoint once picked at NarrowWidth (R1).
    private static TargetPageSettings Compact => StoredModes("{\"sidebar_width\":472}");

    // Every case mounts through this, so every settle and every teardown waits for the pending loads.
    private sealed class Mounted : IDisposable
    {
        public required TargetDetailViewModel Page { get; init; }
        public required IDisposable Owner { get; init; }
        public required ModesLayoutView View { get; init; }
        public required Window Window { get; init; }
        public IDisposable? Night { get; set; }

        // Nights a case swaps in after the first, disposed with it.
        public List<Cards.Harness> MoreNights { get; } = [];

        public Factory.Harness Harness => (Factory.Harness)Owner;

        private SessionCardViewModel?[] Nights => [(Night as Cards.Harness)?.Card, .. MoreNights.Select(night => night.Card)];

        public void Settle() => SettleLoads(Page, Nights);

        public void Dispose()
        {
            SettleLoads(Page, Nights);
            Window.Close();
            SettleLoads(Page, Nights);
            Night?.Dispose();
            MoreNights.ForEach(night => night.Dispose());
            Owner.Dispose();
        }
    }

    // inShell: the shell hosts the layout, so the page's IsWide follows the window as in the app.
    private static Mounted MountOn(TargetDetailViewModel page, IDisposable owner, double width, double height, bool inShell = false)
    {
        var view = new ModesLayoutView();
        Control content = view;
        if (inShell)
        {
            var shell = new TargetDetailView { DataContext = page };
            shell.Named<ContentControl>("LayoutHost").Content = view;
            content = shell;
        }
        else
        {
            view.DataContext = page;
        }

        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        var mounted = new Mounted { Page = page, Owner = owner, View = view, Window = window };
        mounted.Settle();
        return mounted;
    }

    private static void SettleLoads(TargetDetailViewModel page, IReadOnlyList<SessionCardViewModel?> nights)
        => TargetPartHost.SettleLoads(page, nights);

    // realNight: Night review shows a night with 40 frames, a frame table, a metric chart and a
    // guide graph, which the page factory's own nights do not carry. A null width mounts wide at
    // 1900; NarrowWidth mounts the compact sidebar unless a stored record is given.
    private static Mounted Mount(
        double? width = null,
        double height = 900,
        bool fullNight = true,
        bool realNight = false,
        Func<string, GalactiLog.Data.Queries.TargetDetail?>? get = null,
        int findings = 0,
        bool twoRigs = false,
        TargetPageSettings? stored = null)
    {
        // Publishes reach the UI thread through the dispatcher, as the app's post does, so no card
        // rewrites its rig cells on a pool thread while the layout reads them.
        // 1232 and 1280 mount the compact sidebar the window-width breakpoint once picked there, so
        // the common-window pins keep the geometry they describe.
        stored ??= width is NarrowWidth or 1280d ? Compact : null;
        var harness = Factory.Create(get: get, fullNight: fullNight, targetPage: stored, post: action => Dispatcher.UIThread.Post(action)).Settle();
        var mounted = MountOn(harness.ViewModel, harness, width ?? 1900, height);
        if (realNight)
        {
            var night = RealNight(harness, findings, twoRigs: twoRigs);
            mounted.Night = night;
            mounted.View.Named<Control>("NightReviewRegion").DataContext = night.Card;
            mounted.Settle();
        }

        return mounted;
    }

    // Night filter rows on both nights, so the Compare and Integration tables have content: Ha,
    // OIII and filters - 2 more, each row split over the given number of exposure lengths.
    private static GalactiLog.Data.Queries.TargetDetail WithNightFilters(int filters = 2, int lengths = 1)
    {
        var names = new[] { "Ha", "OIII" }.Concat(Enumerable.Range(1, filters - 2).Select(n => $"X{n}")).ToList();
        (double, int)[] exposures = [.. Enumerable.Range(1, lengths).Select(n => (60d * n, 2))];
        var seconds = exposures.Sum(e => e.Item1 * e.Item2);
        var rows = names.SelectMany(name => new[] { Factory.LastSession, Factory.FirstSession }
            .Select(night => Rows.Row(night, name, seconds, 2 * lengths, exposures, hfr: 2.2d, stars: 1_400d))).ToList();
        var totals = Factory.PopulatedTotals() with
        {
            FiltersUsed = names,
            IntegrationSecondsByFilter = names.ToDictionary(name => name, _ => 2 * seconds),
        };
        return Factory.PopulatedDetail(totals: totals) with { NightFilters = rows };
    }

    private static LedgerPage WithCustomColumn(CustomColumnType type)
    {
        var column = CustomColumnTestFactory.Define("Tag", type, CustomColumnScope.Session, [], order: 0);
        var harness = LedgerPage.Create(
            columns: [column],
            ledgerKeys: [column.Slug],
            post: action => Dispatcher.UIThread.Post(action)).Settle();
        Dispatcher.UIThread.RunJobs();
        return harness;
    }

    private static void AssertTheLedgerRowFits(NightsLedgerPart ledger, Control column)
    {
        var row = TargetPartHost.LedgerRowAt(ledger, 0);
        var used = row.ColumnDefinitions.Sum(c => c.ActualWidth);
        Assert.True(used <= row.Bounds.Width + 0.5, $"a ledger row needs {used} in {row.Bounds.Width}");
        Assert.True(ledger.Bounds.Width <= column.Bounds.Width + 0.5, $"the ledger is {ledger.Bounds.Width} in {column.Bounds.Width}");
    }

    private static Color InkOf(Grid row) => ((ISolidColorBrush)TargetPartHost.CellAt(row, 1).Foreground!).Color;

    [AvaloniaFact]
    public void LeftColumn_TakesTheLedgersWidthInTheWideForm_And472InTheCompactForm_AndTheLedgerFitsIt()
    {
        // Red if a form's width moves or a ledger row's columns run past the column.
        using (var wide = Mount())
        {
            var column = wide.View.Named<Control>("LeftColumn");
            Assert.True(column.Bounds.Width >= LedgerColumn.WideMinWidth - 0.5, $"the left column is {column.Bounds.Width}");
            AssertTheLedgerRowFits(wide.View.Named<NightsLedgerPart>("NightsLedgerPart"), column);
        }

        using var narrow = Mount(NarrowWidth, 720);
        var narrowColumn = narrow.View.Named<Control>("LeftColumn");
        Assert.Equal(LedgerColumn.CompactWidth, narrowColumn.Bounds.Width, 0.5);
        AssertTheLedgerRowFits(narrow.View.Named<NightsLedgerPart>("NightsLedgerPart"), narrowColumn);
    }

    [AvaloniaFact]
    public void TheWideFormShowsTheCustomColumns_AndTheCompactFormHidesThem()
    {
        // Red if a custom column is hidden in the wide form, runs past the column, or shows beside
        // the compact ledger once the sidebar is dragged under 520.
        var harness = WithCustomColumn(CustomColumnType.Boolean);
        using var mounted = MountOn(harness.Page, harness, 1760, 900, inShell: true);
        var view = mounted.View;
        var ledger = view.Named<NightsLedgerPart>("NightsLedgerPart");
        var head = ledger.Named<ItemsControl>("LedgerCustomHead");
        Assert.True(harness.Page.IsWide);
        Assert.False(ledger.IsCompact);
        Assert.True(head.IsEffectivelyVisible && head.Bounds.Width > 0, "the custom column is hidden in the wide form");
        AssertTheLedgerRowFits(ledger, view.Named<Control>("LeftColumn"));

        DragSidebar(mounted, 500d - view.Named<Control>("LeftColumn").Bounds.Width);
        Assert.True(harness.Page.IsWide);
        Assert.True(ledger.IsCompact);
        Assert.False(head.IsEffectivelyVisible && head.Bounds.Width > 0, "the custom column shows in the compact form");
    }

    [AvaloniaFact]
    public void InTheShellAt1600_TheWideNightFloorStillFitsTheLedgerRow()
    {
        // Red if the page's wide Night floor of 112 pushes a ledger row past its column.
        var harness = Factory.Create(fullNight: true, post: action => Dispatcher.UIThread.Post(action)).Settle();
        using var mounted = MountOn(harness.ViewModel, harness, 1600, 900, inShell: true);
        Assert.True(harness.ViewModel.IsWide);
        AssertTheLedgerRowFits(mounted.View.Named<NightsLedgerPart>("NightsLedgerPart"), mounted.View.Named<Control>("LeftColumn"));
    }

    [AvaloniaFact]
    public void Ledger_IsCompactOnlyInTheCompactForm_AndNeverDrawsFilters()
    {
        using (var wide = Mount())
        {
            var ledger = wide.View.Named<NightsLedgerPart>("NightsLedgerPart");
            Assert.False(ledger.IsCompact);
            Assert.False(ledger.Named<TextBlock>("LedgerFiltersHead").IsEffectivelyVisible);
        }

        using var narrow = Mount(NarrowWidth, 720);
        Assert.True(narrow.View.Named<NightsLedgerPart>("NightsLedgerPart").IsCompact);
    }

    [AvaloniaFact]
    public void TheLitRowAndACheckedRow_CarryDistinctMarks()
    {
        // Spec 5.6: red if checking a night lights it, or the lit night draws as checked.
        using var mounted = Mount();
        var page = mounted.Harness.ViewModel;
        page.Sessions[1].IsChecked = true;
        mounted.Settle();
        var ledger = mounted.View.Named<NightsLedgerPart>("NightsLedgerPart");
        var lit = TargetPartHost.LedgerRowAt(ledger, 0);
        var checkedRow = TargetPartHost.LedgerRowAt(ledger, 1);

        Assert.Same(page.Sessions[0], page.SelectedSession);
        Assert.False(lit.Children.OfType<CheckBox>().Single().IsChecked);
        Assert.True(checkedRow.Children.OfType<CheckBox>().Single().IsChecked);
        var accent = TargetPartHost.TokenColor(ledger, "ColorAccent");
        Assert.Equal(accent, InkOf(lit));
        Assert.NotEqual(accent, InkOf(checkedRow));
    }

    [AvaloniaFact]
    public void FreshMount_IsNightReview()
    {
        using var mounted = Mount();

        Assert.Equal(TargetPageMode.NightReview, mounted.View.Mode);
        Assert.True(mounted.View.Named<ToggleButton>("NightReviewButton").IsChecked);
        Assert.True(mounted.View.Named<Control>("NightReviewRegion").IsEffectivelyVisible);
        Assert.False(mounted.View.Named<Control>("CompareNightsRegion").IsEffectivelyVisible);
        Assert.False(mounted.View.Named<Control>("IntegrationRegion").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ModeButtons_ShowTheirRegionAndHideTheOthers()
    {
        using var mounted = Mount();
        var view = mounted.View;
        (string Button, TargetPageMode Mode, string Region)[] modes =
        [
            ("CompareNightsButton", TargetPageMode.CompareNights, "CompareNightsRegion"),
            ("IntegrationButton", TargetPageMode.Integration, "IntegrationRegion"),
            ("NightReviewButton", TargetPageMode.NightReview, "NightReviewRegion"),
        ];

        foreach (var (buttonName, mode, regionName) in modes)
        {
            var button = view.Named<ToggleButton>(buttonName);
            button.Command!.Execute(button.CommandParameter);
            mounted.Settle();

            Assert.Equal(mode, view.Mode);
            foreach (var (other, _, otherRegion) in modes)
            {
                Assert.Equal(otherRegion == regionName, view.Named<Control>(otherRegion).IsEffectivelyVisible);
                Assert.Equal(other == buttonName, view.Named<ToggleButton>(other).IsChecked);
            }
        }

        view.Mode = TargetPageMode.CompareNights;
        mounted.Settle();
        Assert.True(view.Named<TrendChartPart>("TrendChartPart").Named<ContentControl>("TargetChartRegion").IsEffectivelyVisible);
        view.Mode = TargetPageMode.Integration;
        mounted.Settle();
        Assert.True(view.Named<IntegrationBarsPart>("IntegrationBarsPart").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void CheckedSwitch_IsDisabledWithNothingChecked_AndOpensOnAll()
    {
        using var mounted = Mount();
        mounted.View.Mode = TargetPageMode.CompareNights;
        mounted.Settle();
        var page = mounted.Harness.ViewModel;
        var checkedOnly = mounted.View.Named<ToggleButton>("CheckedNightsToggle");

        Assert.False(page.TargetChart.CheckedOnly);
        Assert.True(mounted.View.Named<ToggleButton>("AllNightsToggle").IsChecked);
        Assert.False(checkedOnly.IsEffectivelyEnabled);

        page.Sessions[0].IsChecked = true;
        mounted.Settle();
        Assert.True(checkedOnly.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void CheckedSwitch_PlotsOnlyTheCheckedNights()
    {
        using var mounted = Mount();
        mounted.View.Mode = TargetPageMode.CompareNights;
        var page = mounted.Harness.ViewModel;
        Assert.True(page.Sessions.Count > 1);
        // The fresh profile plots the newest night only (default_chart_sessions 1).
        page.TargetChart.ShowAllSessions = true;
        page.Sessions[0].IsChecked = true;
        mounted.Settle();

        TargetPartHost.Click(mounted.Window, mounted.View.Named<ToggleButton>("CheckedNightsToggle"));

        Assert.True(page.TargetChart.CheckedOnly);
        Assert.Equal(1, page.TargetChart.PlottedSessionCount);

        page.Sessions[1].IsChecked = true;
        mounted.Settle();
        Assert.Equal(2, page.TargetChart.PlottedSessionCount);
    }

    [AvaloniaTheory]
    [InlineData(null, 900d)]
    [InlineData(1552d, 852d)]
    [InlineData(NarrowWidth, 720d)]
    public void ThePageDoesNotScroll_AndNoRegionHasTwoScrollBars(double? width, double height)
    {
        // Red if the shell puts the layout inside a scroll viewer, any mode's content outside a
        // scroll viewer runs past the layout's height, or a region shows a second vertical bar.
        var harness = Factory.Create(fullNight: true, post: action => Dispatcher.UIThread.Post(action)).Settle();
        using (var shelled = MountOn(harness.ViewModel, harness, width ?? 1900, height, inShell: true))
        {
            Assert.Empty(shelled.View.GetVisualAncestors().OfType<ScrollViewer>());
        }

        using var mounted = Mount(width, height, realNight: true);
        var view = mounted.View;

        foreach (var mode in Enum.GetValues<TargetPageMode>())
        {
            view.Mode = mode;
            mounted.Settle();
            Assert.True(Overflow(view) <= 0.5, $"{mode} runs {Overflow(view)} past the layout's {view.Bounds.Height}");
            // Compare nights holds two scrollers side by side in time, the lanes and the table, whose
            // bar is always shown (spec.md item 8), so each is counted on its own.
            foreach (var region in new[] { "LeftColumn", "LanesRegion", "FramesRegion", "TrendChartPart", "CompareTableHost", "IntegrationRegion" })
            {
                var bars = VerticalBars(view.Named<Control>(region));
                Assert.True(bars <= 1, $"{region} in {mode} shows {bars} vertical scroll bars");
            }
        }
    }

    // R15: the chart is whole at rest and the rows give way before it. The Session metrics header line
    // costs the seventh row at 852 (6.9 rows measure); at 720 one row measures at 1280 (1.67) and 20 px
    // of a row at 1232, whose narrower right region wraps the frames toolbar.
    [AvaloniaTheory]
    [InlineData(1552d, 852d, 6)]
    [InlineData(NarrowWidth, 720d, 0)]
    public void NightReview_TheFramesTableKeepsItsRows_AndTheLanesScrollInTheirOwnRegion(double width, double height, int least)
    {
        // Red if the frames table is left fewer rows than its floor at this size, the lanes do not
        // scroll in their own region, or anything outside a scroll viewer runs past the layout.
        using var mounted = Mount(width, height, realNight: true);
        var view = mounted.View;
        var rows = view.Named<FramesPart>("FramesPart").Named<ListBox>("FrameRows");
        var first = rows.ContainerFromIndex(0);
        Assert.NotNull(first);
        Assert.True(rows.Bounds.Height >= (least * first!.Bounds.Height) - 0.5,
            $"the frames table is {rows.Bounds.Height} tall for rows of {first.Bounds.Height} at {width} x {height}");
        Assert.Equal(1, VerticalBars(view.Named<Control>("LanesRegion")));
        Assert.True(Overflow(view) <= 0.5, $"the layout's content runs {Overflow(view)} past its {view.Bounds.Height}");
    }

    [AvaloniaFact]
    public void NightReview_At720_TheTimelineAndTheMetricsLanesTopAreInTheLanesViewportAtRest()
    {
        // Red if the lanes region is too short to show the whole timeline and the top of the
        // metrics lane without scrolling, or the lane's host gives the chart less than 180 px.
        using var mounted = Mount(NarrowWidth, 720, realNight: true);
        CloseNightMetrics(mounted);
        var view = mounted.View;
        var lanes = view.Named<ScrollViewer>("LanesRegion");
        var timeline = BoundsIn(view.Named<NightTimelinePart>("NightTimelinePart"), lanes);
        var metrics = BoundsIn(view.Named<NightMetricsPart>("NightMetricsPart"), lanes);

        Assert.Equal(0d, lanes.Offset.Y);
        Assert.True(lanes.Bounds.Height >= view.LanesFloor - 0.5, $"the lanes region is {lanes.Bounds.Height} tall");
        Assert.True(timeline.Top >= 0d && timeline.Bottom <= lanes.Bounds.Height + 0.5,
            $"the timeline spans {timeline.Top} to {timeline.Bottom} in a {lanes.Bounds.Height} viewport");
        Assert.True(metrics.Top < lanes.Bounds.Height, $"the metrics lane starts at {metrics.Top} in a {lanes.Bounds.Height} viewport");
        Assert.True(metrics.Height >= 180d - 0.5, $"the metrics lane is {metrics.Height} tall");
    }

    [AvaloniaTheory]
    [InlineData(1280d, 720d, 1)]
    [InlineData(1600d, 900d, 7)]
    public void NightReview_ThePerFrameChartIsWholeAtRest_AndTheRowsGiveWayBeforeIt(double width, double height, int least)
    {
        // Red if the per-frame chart's plot is cut by the lanes viewport at rest, or the frames
        // table shows fewer rows than this size keeps.
        using var mounted = Mount(width, height, realNight: true);
        CloseNightMetrics(mounted);
        var view = mounted.View;
        var lanes = view.Named<ScrollViewer>("LanesRegion");
        var plot = BoundsIn(view.Named<NightMetricsPart>("NightMetricsPart").Named<Control>("PlotHost"), lanes);
        var frames = view.Named<FramesPart>("FramesPart");
        var rows = frames.Named<ListBox>("FrameRows");
        var row = rows.ContainerFromIndex(0)!.Bounds.Height;
        var chrome = BoundsIn(frames.Named<Control>("FrameTableHeader"), frames).Top;
        output.WriteLine($"{width} x {height}: lanes {lanes.Bounds.Height}, plot {plot}, chrome {chrome}, rows {rows.Bounds.Height / row:0.##} of {row}");

        Assert.Equal(0d, lanes.Offset.Y);
        Assert.True(plot.Height >= 100d - 0.5, $"the plot is {plot.Height} tall");
        Assert.True(plot.Top >= -0.5 && plot.Bottom <= lanes.Bounds.Height + 0.5,
            $"the plot spans {plot.Top} to {plot.Bottom} in a {lanes.Bounds.Height} lanes viewport");
        Assert.True(rows.Bounds.Height >= (least * row) - 0.5, $"the frames table is {rows.Bounds.Height} tall for rows of {row}");
        Assert.True(Overflow(view) <= 0.5, $"the layout's content runs {Overflow(view)} past its {view.Bounds.Height}");
    }

    [AvaloniaTheory]
    [InlineData(1280d, 720d)]
    [InlineData(1600d, 900d)]
    public void NightReview_ATwoRigNightWhoseDetailArrivesAfterTheFirstLayout_ShowsItsRigPillsWithoutASwitch(double width, double height)
    {
        // Red if, in the app's order (part laid out and its form applied, the night's detail and table
        // arriving later), the rig pills end in no host or a hidden one until a night switch. The
        // page's own selected night carries the two rigs and its table, so nothing is switched.
        TargetPageState? holder = null;
        var harness = Factory.Create(
            fullNight: false,
            post: action => Dispatcher.UIThread.Post(action),
            nightDetail: date => Cards.PopulatedDetail(sessionDate: date) with
            {
                Frames = [.. NightPartsTestKit.Frames(40, date).Select((frame, index) => frame with { Rig = index % 2 == 0 ? "Alpha / Cam" : "Bravo / Cam" })],
            },
            frameTable: detail => NightPartsTestKit.Table(detail.Frames, holder));
        holder = harness.TargetPage;
        using var mounted = MountOn(harness.ViewModel, harness, width, height, inShell: true);
        var view = mounted.View;
        var night = harness.ViewModel.SelectedSession!;

        var part = view.Named<FramesPart>("FramesPart");
        var pills = part.Named<ItemsControl>("RigPillRow");
        output.WriteLine($"compact {part.IsCompact}, parent {pills.Parent?.Name ?? pills.Parent?.GetType().Name}, visible {pills.IsVisible}, "
            + $"context {pills.DataContext?.GetType().Name}, same {ReferenceEquals(pills.DataContext, night)}, table {night.FrameTable is not null}, multi {night.FrameTable?.IsMultiRig}, items {pills.ItemCount}");
        Assert.True(pills.IsEffectivelyVisible && part.GetVisualDescendants().Contains(pills), "the rig pills are not shown on first load");
        Assert.Equal(2, pills.GetVisualDescendants().OfType<ToggleButton>().Count());
    }

    [AvaloniaTheory]
    [InlineData(1280d, 720d, 0)]
    [InlineData(1600d, 900d, 7)]
    public void NightReview_ATwoRigNightWithFourFindings_KeepsTheChartWholeAndItsRows_UnderTwoChromeLines(double width, double height, int least)
    {
        // Red if a two-rig night with four findings cuts the chart, leaves fewer full frame rows than
        // this size keeps, or takes more than two lines of chrome above the column header.
        using var mounted = Mount(width, height, realNight: true, findings: 4, twoRigs: true);
        CloseNightMetrics(mounted);
        var view = mounted.View;
        var lanes = view.Named<ScrollViewer>("LanesRegion");
        var plot = BoundsIn(view.Named<NightMetricsPart>("NightMetricsPart").Named<Control>("PlotHost"), lanes);
        var frames = view.Named<FramesPart>("FramesPart");
        var rows = frames.Named<ListBox>("FrameRows");
        var row = rows.ContainerFromIndex(0)!.Bounds.Height;
        var chrome = BoundsIn(frames.Named<Control>("FrameTableHeader"), frames).Top;
        output.WriteLine($"{width} x {height}: compact {frames.IsCompact}, lanes {lanes.Bounds.Height}, plot {plot}, chrome {chrome}, rows {rows.Bounds.Height / row:0.##} of {row}");

        Assert.True(plot.Height >= 100d - 0.5 && plot.Top >= -0.5 && plot.Bottom <= lanes.Bounds.Height + 0.5,
            $"the plot spans {plot.Top} to {plot.Bottom} in a {lanes.Bounds.Height} lanes viewport");
        var line = frames.Named<Control>("FramesChrome").Bounds.Height;
        var toolbar = frames.Named<Control>("FrameTableToolbar").Bounds.Height;
        output.WriteLine($"chrome line {line}, toolbar {toolbar}");
        Assert.True(line <= 30d, $"the chrome line is {line} tall, more than one line");
        NightPartsTestKit.AssertTheToolbarClipsNothing(frames);
        Assert.True(rows.Bounds.Height >= (least * row) - 0.5, $"the frames table is {rows.Bounds.Height} tall for rows of {row}");
    }

    [AvaloniaFact]
    public void NightReview_TwoNightSwitchesWithRebuiltTables_KeepTheRigPillsOnTheirLine()
    {
        // Red if a frame table rebuilt after a night switch (a night still loading, then loaded)
        // throws on taking the rig pills, draws without them, or leaves them off the form's line.
        using var mounted = Mount(1600, 900);
        var view = mounted.View;
        var pills = view.Named<FramesPart>("FramesPart").Named<ItemsControl>("RigPillRow");
        for (var pass = 1; pass <= 2; pass++)
        {
            var night = RealNight(mounted.Harness, twoRigs: true);
            mounted.MoreNights.Add(night);
            view.Named<Control>("NightReviewRegion").DataContext = night.Card;
            mounted.Settle();
            var frames = night.Card.Detail!.Frames;
            night.FrameTableResult = NightPartsTestKit.Table(frames, night.TargetPage);
            night.Card.Invalidate();
            night.Settle();
            mounted.Settle();

            var part = view.Named<FramesPart>("FramesPart");
            var table = part.GetVisualDescendants().OfType<FrameTableView>().Single();
            var host = part.IsCompact ? part.Named<Control>("FilterLineHost") : table.Named<Control>("FrameTableToolbar");
            Assert.Contains(host, pills.GetVisualAncestors());
            Assert.True(pills.IsEffectivelyVisible, $"the rig pills are hidden after switch {pass}");
            Assert.Equal(2, pills.GetVisualDescendants().OfType<ToggleButton>().Count());
        }
    }

    [AvaloniaTheory]
    [InlineData(720d)]
    [InlineData(640d)]
    public void CompareNights_At1280InTheShell_TheTableStartsBelowTheLanes(double height)
    {
        // Red if any part of the chart, its lanes viewport or a visible lane reaches below the top
        // of the table's host, so the table's night header row sits on the lanes.
        var harness = Factory.Create(get: _ => WithNightFilters(12), fullNight: true, post: action => Dispatcher.UIThread.Post(action)).Settle();
        using var mounted = MountOn(harness.ViewModel, harness, 1280, height, inShell: true);
        var view = mounted.View;
        view.Mode = TargetPageMode.CompareNights;
        mounted.Settle();
        var region = view.Named<Control>("CompareNightsRegion");
        var host = BoundsIn(view.Named<Control>("CompareTableHost"), region);
        var part = BoundsIn(view.Named<TrendChartPart>("TrendChartPart"), region);
        var lanes = LanesScrollOrNull(view)!;
        var viewport = BoundsIn(lanes, region);
        output.WriteLine($"region {region.Bounds}, chart {part}, lanes {viewport}, table host {host}");
        Assert.True(part.Bottom <= host.Top + 0.5, $"the chart ends at {part.Bottom}, the table starts at {host.Top}");
        Assert.True(viewport.Bottom <= host.Top + 0.5, $"the lanes end at {viewport.Bottom}, the table starts at {host.Top}");
        foreach (var chart in LaneCharts(view))
        {
            var box = BoundsIn(chart, region);
            output.WriteLine($"lane {box}");
            Assert.True(Math.Min(box.Bottom, viewport.Bottom) <= host.Top + 0.5, $"a lane is drawn to {Math.Min(box.Bottom, viewport.Bottom)}, the table starts at {host.Top}");
        }
    }

    [AvaloniaTheory]
    [InlineData(NarrowWidth, 720d)]
    [InlineData(1552d, 852d)]
    public void NightReview_TheFramesTableAndTheNightHeaderStayInsideTheLayout(double width, double height)
    {
        // Red if the right region is wider than the layout, so the frames table's viewport or the
        // night header's help glyph is drawn past the window's edge.
        using var mounted = Mount(width, height, realNight: true);
        var view = mounted.View;
        var frames = view.Named<FramesPart>("FramesPart");
        var viewport = frames.Named<ContentControl>("FrameTableRegion").GetVisualDescendants().OfType<ScrollViewer>()
            .First(viewer => viewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto);
        var table = BoundsIn(viewport, view);
        output.WriteLine($"{width}: layout {view.Bounds.Width}, viewport {table}, rows {BoundsIn(frames.Named<ListBox>("FrameRows"), view)}");
        Assert.True(table.Right <= view.Bounds.Width + 0.5, $"the frames table's viewport ends at {table.Right} in a {view.Bounds.Width} layout");

        var glyphs = view.Named<NightHeaderPart>("NightHeaderPart").GetVisualDescendants()
            .OfType<HelpButton>().Where(glyph => glyph.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(glyphs);
        foreach (var glyph in glyphs)
        {
            var bounds = BoundsIn(glyph, view);
            Assert.True(bounds.Right <= view.Bounds.Width + 0.5, $"a night header help glyph ends at {bounds.Right} in a {view.Bounds.Width} layout");
        }
    }

    [AvaloniaFact]
    public void CompareNights_OnOpening_PlotsEveryNight()
    {
        // Red if Compare nights keeps the fresh profile's newest-one cap.
        var middle = new DateOnly(2025, 6, 1);
        using var mounted = Mount(get: _ => Factory.PopulatedDetail(
            sessions: [Factory.Session(Factory.LastSession), Factory.Session(middle), Factory.Session(Factory.FirstSession)]));
        var page = mounted.Harness.ViewModel;
        Assert.Equal(3, page.Sessions.Count);

        var button = mounted.View.Named<ToggleButton>("CompareNightsButton");
        button.Command!.Execute(button.CommandParameter);
        mounted.Settle();

        Assert.Equal(3, page.TargetChart.PlottedSessionCount);
    }

    [AvaloniaFact]
    public void CompareNights_ANightWithNoMetrics_IsHidden_AndTheLineShowsIt()
    {
        // Red if the empty night is plotted, the line is missing or disabled with nothing checked
        // (the switch beside it is), or its button does not bring the night back.
        var middle = new DateOnly(2025, 6, 1);
        using var mounted = Mount(get: _ => Factory.PopulatedDetail(
            sessions: [Factory.Session(Factory.LastSession), Factory.SessionWithoutMetrics(middle), Factory.Session(Factory.FirstSession)]));
        var page = mounted.Harness.ViewModel;
        var button = mounted.View.Named<ToggleButton>("CompareNightsButton");
        button.Command!.Execute(button.CommandParameter);
        mounted.Settle();

        Assert.Equal(2, page.TargetChart.PlottedSessionCount);
        var line = mounted.View.Named<Control>("EmptyNightsLine");
        Assert.True(line.IsEffectivelyVisible);
        Assert.True(line.IsEffectivelyEnabled);
        Assert.Contains("1 night has no metrics.", line.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));

        var show = mounted.View.Named<Button>("EmptyNightsButton");
        show.Command!.Execute(null);
        mounted.Settle();

        Assert.Equal(3, page.TargetChart.PlottedSessionCount);
        Assert.Equal("Hide them", show.Content);
    }

    [AvaloniaFact]
    public async Task TheSelectedFrame_IsMarkedAtTheTimelinesX_InTheChart_AndAPointPressSelectsIt()
    {
        // Spec 5.2 and 5.3: red if the per-frame chart's mark is more than 1 px off the timeline's
        // active tick, or a press on a chart point leaves the frames table and the strip on
        // another frame.
        using var mounted = Mount(1600, 900);
        var view = mounted.View;
        var night = RealNight(mounted.Harness, guideFromFirstFrame: true);
        mounted.Night = night;
        view.Named<Control>("NightReviewRegion").DataContext = night.Card;
        mounted.Settle();
        var strip = night.Card.NightStrip!;
        var table = (FrameTableViewModel)night.FrameTableResult!;
        table.SelectFrameAt(20);
        mounted.Settle();

        var timeline = view.Named<ContentControl>("NightStripRegion").GetVisualDescendants().OfType<NightStrip>().Single();
        var expected = timeline.TranslatePoint(new Point(timeline.GeometryFor(strip.Ticks.Single(tick => tick.FrameIndex == 20)).Rect.Center.X, 0), view)!.Value.X;

        var plot = view.Named<NightMetricsPart>("NightMetricsPart").GetVisualDescendants()
            .OfType<LiveChartsCore.SkiaSharpView.Avalonia.CartesianChart>().Single(chart => chart.Name == "Chart");
        var core = (LiveChartsCore.Chart)plot.CoreChart;
        for (var step = 0; step < 120 && core.DrawMarginSize.Width <= 0f; step++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        var chart = (SessionChartViewModel)night.Card.Chart!;
        var mark = chart.Sections.Single(section => section.Xi is not null);
        var markX = plot.TranslatePoint(new Point(core.DrawMarginLocation.X + (mark.Xi!.Value * core.DrawMarginSize.Width), 0), view)!.Value.X;
        Assert.Equal(expected, markX, 1d);

        var pressed = strip.Ticks.Single(tick => tick.FrameIndex == 30);
        var entity = new LiveChartsCore.Defaults.MappedChartEntity
        {
            MetaData = new LiveChartsCore.Kernel.ChartEntityMetaData(_ => { }),
            Coordinate = new LiveChartsCore.Kernel.Coordinate(pressed.Fraction, 2d),
        };
        chart.PointPressedCommand.Execute(new[] { new LiveChartsCore.Kernel.ChartPoint(null!, null!, entity) });
        mounted.Settle();
        Assert.Equal(30, table.SelectedCaptureIndex);
        Assert.Equal(30, strip.ActiveFrame);
    }

    [AvaloniaFact]
    public void AnOutlierFrame_IsATallerTick_AndItsRowCarriesNoFill()
    {
        // Spec 5.4: red if an outlier's tick is no taller than an ordinary one, or its frame row
        // takes a background the ordinary row does not. Frame 0 is an outlier, frame 1 is not.
        using var mounted = Mount(realNight: true);
        var card = ((Cards.Harness)mounted.Night!).Card;
        var strip = mounted.View.Named<ContentControl>("NightStripRegion").GetVisualDescendants().OfType<NightStrip>().Single();
        var outlier = card.NightStrip!.Ticks.Single(tick => tick.FrameIndex == 0);
        var ordinary = card.NightStrip.Ticks.Single(tick => tick.FrameIndex == 1);
        Assert.True(outlier.IsOutlier && !ordinary.IsOutlier, "the fixture no longer marks frame 0 alone");
        Assert.True(strip.GeometryFor(outlier).Rect.Height > strip.GeometryFor(ordinary).Rect.Height,
            $"the outlier tick is {strip.GeometryFor(outlier).Rect.Height} tall, an ordinary one {strip.GeometryFor(ordinary).Rect.Height}");

        var rows = mounted.View.Named<FramesPart>("FramesPart").Named<ListBox>("FrameRows");
        var outlierRow = (ListBoxItem)rows.ContainerFromIndex(0)!;
        var ordinaryRow = (ListBoxItem)rows.ContainerFromIndex(1)!;
        Assert.True(((FrameRowViewModel)outlierRow.DataContext!).IsHfrOutlier);
        Assert.False(outlierRow.IsSelected || ordinaryRow.IsSelected);
        Assert.Equal(Fill(ordinaryRow), Fill(outlierRow));

        static string Fill(ListBoxItem row) => string.Join(",", row.GetVisualDescendants().OfType<Border>()
            .Select(border => border.Background?.ToString() ?? "none")
            .Prepend(row.Background?.ToString() ?? "none"));
    }

    [AvaloniaFact]
    public void SelectingAFrameRow_MarksTheStripsActiveTick()
    {
        // The user's own bar, at the layout's wide width: 200 close-set ticks,
        // and the active one's drawn rectangle differs from an inactive one's in both height and
        // ink, asserted on the render geometry rather than on a property that merely holds the
        // index.
        var date = Factory.LastSession;
        var frames = NightPartsTestKit.Frames(200, date);
        var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        var table = NightPartsTestKit.Table(frames);
        harness.FrameTableResult = table;

        harness.Card.IsExpanded = true;
        harness.Settle();

        using var mounted = Mount();
        mounted.Night = harness;
        mounted.View.Named<Control>("NightReviewRegion").DataContext = harness.Card;
        mounted.Settle();

        var strip = mounted.View.Named<ContentControl>("NightStripRegion")
            .GetVisualDescendants()
            .OfType<NightStrip>()
            .Single();

        // 101 and 1: neither is a multiple of 25, so neither is one of the fixture's own outlier
        // ticks (i % 25 == 0) and this stays a plain active-versus-ordinary comparison; the
        // active-and-outlier collision has its own case in NightStripActiveTickTests.
        table.SelectFrameAt(101);
        Dispatcher.UIThread.RunJobs();

        var activeTick = harness.Card.NightStrip!.Ticks.Single(tick => tick.FrameIndex == 101);
        var inactiveTick = harness.Card.NightStrip!.Ticks.Single(tick => tick.FrameIndex == 1);
        var activeGeometry = strip.GeometryFor(activeTick);
        var inactiveGeometry = strip.GeometryFor(inactiveTick);

        // A null Ink is still NotEqual to the filter's own brush, so without this the
        // ink half of the user's bar would pass vacuously if ColorTextPrimary failed to resolve.
        Assert.NotNull(activeGeometry.Ink);
        Assert.True(
            activeGeometry.Rect.Height > inactiveGeometry.Rect.Height,
            $"active height {activeGeometry.Rect.Height} did not exceed inactive height {inactiveGeometry.Rect.Height}");
        Assert.NotEqual(activeGeometry.Ink, inactiveGeometry.Ink);
    }

    [AvaloniaFact]
    public void ModesLayoutView_EveryTextBlock_RendersAtAReadableSize()
    {
        // A regression pin. Scales.axaml's FontSize* keys are ratios (0.500 to 0.786), not
        // point sizes: binding one to FontSize renders text at well under a pixel. Nothing here
        // sets FontSize; the four tier classes reach their size through the Multiply converter.
        using var mounted = Mount();
        var pane = mounted.View;

        var blocks = pane.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }

    [AvaloniaFact]
    public void ModesLayoutView_HoldsNoSessionNotes()
    {
        // The session notes moved to the Details drawer (night pane round): red if the layout still
        // carries the Session notes section or its box.
        using var mounted = Mount();
        var pane = mounted.View;

        Assert.Null(pane.NamedOrNull<Expander>("NotesSection"));
        Assert.Null(pane.NamedOrNull<TextBox>("SessionNotesTextBox"));
        Assert.DoesNotContain(pane.GetLogicalDescendants().OfType<Control>(), control => control is NightNotesPart);
    }

    // How far a visible descendant outside any scroll viewer reaches past the host's right edge.
    private static double RightOverflow(Control host)
        => host.GetVisualDescendants().OfType<Control>()
            .Where(child => child.IsEffectivelyVisible && child.Bounds.Width > 0
                && !child.GetVisualAncestors().TakeWhile(a => !ReferenceEquals(a, host)).OfType<ScrollViewer>().Any())
            .Select(child => (child.TranslatePoint(new Point(child.Bounds.Width, 0), host)?.X ?? 0d) - host.Bounds.Width)
            .DefaultIfEmpty(double.NegativeInfinity)
            .Max();

    private static ScrollViewer? LanesScrollOrNull(ModesLayoutView view)
        => view.Named<TrendChartPart>("TrendChartPart").NamedOrNull<ScrollViewer>("LanesScroll");

    private static List<Control> LaneCharts(ModesLayoutView view)
        => [.. view.Named<TrendChartPart>("TrendChartPart").GetVisualDescendants().OfType<ItemsControl>()
            .First(items => items.Name == "Lanes").GetRealizedContainers()];

    [AvaloniaTheory]
    [InlineData(1280d, 720d)]
    [InlineData(1600d, 900d)]
    [InlineData(null, 900d)]
    public void EveryMode_StaysInsideTheLayout_WithAtMostOneVerticalBarPerRegion(double? width, double height)
    {
        // Red if any mode's content outside a scroll viewer runs past the layout's right or bottom
        // edge, or a region of stage 2 shows a second vertical scroll bar.
        using var mounted = Mount(width, height, realNight: true, get: _ => WithNightFilters(filters: 7, lengths: 20));
        var view = mounted.View;

        foreach (var mode in Enum.GetValues<TargetPageMode>())
        {
            view.Mode = mode;
            mounted.Settle();
            Assert.True(Overflow(view) <= 0.5, $"{mode} runs {Overflow(view)} past the layout's {view.Bounds.Height}");
            Assert.True(RightOverflow(view) <= 0.5, $"{mode} runs {RightOverflow(view)} past the layout's {view.Bounds.Width}");
            foreach (var region in new[] { "LeftColumn", "LanesRegion", "FramesRegion", "CompareTableHost", "IntegrationScroll" })
            {
                var bars = VerticalBars(view.Named<Control>(region));
                Assert.True(bars <= 1, $"{region} in {mode} shows {bars} vertical scroll bars");
            }

            // The chart's template is realized only once Compare nights has been shown.
            if (LanesScrollOrNull(view) is { } lanes)
            {
                Assert.True(VerticalBars(lanes) <= 1, $"LanesScroll in {mode} shows {VerticalBars(lanes)} vertical scroll bars");
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(1280d, 720d, 2)]
    [InlineData(1280d, 720d, 12)]
    [InlineData(1600d, 900d, 12)]
    [InlineData(null, 900d, 12)]
    public void CompareNights_TheLanesScrollInTheirOwnRegion_AndTheTableBelowTakesAtMostFortyPercent(double? width, double height, int filters)
    {
        // Red if the switch is not above both, the lanes are not their own scrolling region,
        // the table is not below them in its host, takes more than 40 percent of the region or does
        // not scroll inside itself past that, a lane is under 96 px, or fewer than two lanes show.
        using var mounted = Mount(width, height, get: _ => WithNightFilters(filters));
        var view = mounted.View;
        view.Mode = TargetPageMode.CompareNights;
        mounted.Settle();
        var region = view.Named<Control>("CompareNightsRegion");
        var lanes = LanesScrollOrNull(view)!;
        var host = view.Named<Control>("CompareTableHost");
        var table = view.Named<CompareTablePart>("CompareTablePart");
        var part = view.Named<TrendChartPart>("TrendChartPart");

        Assert.Same(lanes, LaneCharts(view)[0].FindAncestorOfType<ScrollViewer>());
        Assert.DoesNotContain(lanes, part.Named<Control>("TrendHelp").GetVisualAncestors());
        Assert.DoesNotContain(lanes, part.Named<Control>("PillRow").GetVisualAncestors());
        Assert.Contains(host, table.GetVisualAncestors());
        Assert.True(LayoutParityCensusTests.IsShown(table), "the Compare table is not on screen");
        var switchBox = BoundsIn(view.Named<Control>("CheckedSwitch"), region);
        var lanesBox = BoundsIn(lanes, region);
        var tableBox = BoundsIn(host, region);
        output.WriteLine($"{width} x {height}, {filters} filters: region {region.Bounds}, lanes {lanesBox}, table {tableBox}");
        Assert.True(switchBox.Bottom <= lanesBox.Top + 0.5, $"the switch ends at {switchBox.Bottom}, the lanes start at {lanesBox.Top}");
        Assert.True(lanesBox.Bottom <= tableBox.Top + 0.5, $"the lanes end at {lanesBox.Bottom}, the table starts at {tableBox.Top}");
        Assert.True(tableBox.Height <= (0.4 * region.Bounds.Height) + 0.5, $"the table is {tableBox.Height} of a {region.Bounds.Height} region");
        if (filters > 2)
        {
            Assert.True(tableBox.Height >= (0.4 * region.Bounds.Height) - 1, $"a long table is {tableBox.Height} of a {region.Bounds.Height} region");
            Assert.Equal(1, VerticalBars(host));
        }

        var charts = LaneCharts(view);
        Assert.True(charts.Count >= 2, $"{charts.Count} lanes");
        Assert.All(charts, chart => Assert.True(chart.Bounds.Height >= 96d - 0.5, $"a lane is {chart.Bounds.Height} tall"));
        // A lane counts when nine tenths of it are in the viewport: at 1280 x 720 with the table at
        // its cap the second lane ends 1 px past a 281 px viewport.
        var visible = charts.Count(chart => BoundsIn(chart, lanes) is var box
            && Math.Min(box.Bottom, lanes.Bounds.Height) - Math.Max(box.Top, 0d) >= 0.9 * box.Height);
        Assert.True(visible >= 2, $"{visible} lanes in view in a {lanes.Bounds.Height} lanes region");
    }

    [AvaloniaTheory]
    [InlineData(1280d, 720d)]
    [InlineData(1600d, 900d)]
    public void Integration_IsOneScrollViewer_HoldingTheFullBarsThenTheTables(double width, double height)
    {
        // Red if the bars are not in their full form, the tables are not below them in
        // the same scroll viewer, or the hours table at the end cannot be reached by its one bar.
        using var mounted = Mount(width, height, get: _ => WithNightFilters(lengths: 20));
        var view = mounted.View;
        view.Mode = TargetPageMode.Integration;
        mounted.Settle();
        var scroll = view.Named<ScrollViewer>("IntegrationScroll");
        var bars = view.Named<IntegrationBarsPart>("IntegrationBarsPart");
        var tables = view.Named<IntegrationTablesPart>("IntegrationTablesPart");

        Assert.True(bars.IsFull);
        Assert.Same(scroll, bars.FindAncestorOfType<ScrollViewer>());
        Assert.Same(scroll, tables.FindAncestorOfType<ScrollViewer>());
        Assert.True(BoundsIn(bars, scroll).Bottom <= BoundsIn(tables, scroll).Top + 0.5, "the tables are not below the bars");
        Assert.True(LayoutParityCensusTests.IsShown(tables), "the Integration tables are not on screen");
        Assert.Equal(1, VerticalBars(view.Named<Control>("IntegrationRegion")));

        var overall = tables.Named<Control>("OverallTable");
        Assert.True(BoundsIn(overall, scroll).Top < scroll.Bounds.Height, "the Overall metrics table starts below the viewport at rest");
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        mounted.Settle();
        var hours = BoundsIn(tables.Named<Control>("HoursTable"), scroll);
        Assert.True(hours.Top >= -0.5 && hours.Bottom <= scroll.Bounds.Height + 0.5, $"the hours table spans {hours.Top} to {hours.Bottom} in a {scroll.Bounds.Height} viewport at the end");
    }

    [AvaloniaFact]
    public void NightReview_WithNothingStored_TheLanesCapIsTheFloor_AndFollowsTheTextSize()
    {
        // Red if the lanes' cap with nothing stored is other than the floor (the chart at 180 under
        // the timeline, the frames taking the rest), or keeps the floor it took before a larger text
        // size grew the lanes above the chart.
        using var mounted = Mount(null, 1400, realNight: true);
        var view = mounted.View;

        double AssertTheCap()
        {
            var cap = view.Named<ScrollViewer>("LanesRegion").MaxHeight;
            var chart = view.Named<Control>("NightMetricsPart").Bounds.Height;
            output.WriteLine($"font {view.FontSize}: floor {view.LanesFloor}, cap {cap}, chart {chart}");
            Assert.Equal(view.LanesFloor, cap, 1d);
            Assert.Equal(ModesLayoutView.ChartHeightDefault, chart, 1d);
            return cap;
        }

        var before = AssertTheCap();
        mounted.Window.FontSize = 26;
        mounted.Settle();
        var after = AssertTheCap();
        Assert.True(after > before + 1d, $"the cap is {after} at the larger text size, {before} before, so the case cannot tell a stale floor");
    }

    [AvaloniaTheory]
    [InlineData(1280d, 720d)]
    [InlineData(1600d, 900d)]
    [InlineData(null, 900d)]
    public void TheStageTwoViewers_HoldNothingWiderThanTheirViewport(double? width, double height)
    {
        // Red if content under a viewer with no sideways bar is wider than the viewer and
        // so cut at its right edge, which the edge check above does not see.
        using var mounted = Mount(width, height, realNight: true, get: _ => WithNightFilters(filters: 7, lengths: 20));
        var view = mounted.View;
        foreach (var (mode, viewers) in new[]
        {
            (TargetPageMode.CompareNights, new[] { "LanesScroll", "CompareScroll" }),
            (TargetPageMode.Integration, new[] { "IntegrationScroll" }),
        })
        {
            view.Mode = mode;
            mounted.Settle();
            foreach (var name in viewers)
            {
                var viewer = name == "LanesScroll" ? LanesScrollOrNull(view)! : view.Named<ScrollViewer>(name);
                Assert.True(viewer.Extent.Width <= viewer.Viewport.Width + 0.5,
                    $"{name} holds {viewer.Extent.Width} in a {viewer.Viewport.Width} viewport");
                // The extent is clamped to the viewport when the sideways bar is disabled, so the
                // content's own right edges are read too.
                var widest = ((Control)viewer.Content!).GetVisualDescendants().OfType<Control>().Prepend((Control)viewer.Content!)
                    .Where(child => child.IsEffectivelyVisible && child.Bounds.Width > 0
                        && !child.GetVisualAncestors().TakeWhile(a => !ReferenceEquals(a, viewer)).OfType<ScrollViewer>().Any())
                    .Max(child => child.TranslatePoint(new Point(child.Bounds.Width, 0), viewer)!.Value.X);
                Assert.True(widest <= viewer.Viewport.Width + 0.5, $"{name} draws content to {widest} in a {viewer.Viewport.Width} viewport");
            }
        }
    }

    // ---- Phase 24 R1 and R2: the sidebar and the moved sections ----------------------------

    private static TargetPageSettings StoredModes(string layoutJson)
        => JsonSerializer.Deserialize<DisplaySettings>("{\"target_page\":{\"layouts\":{\"modes\":" + layoutJson + "}}}")!.TargetPage;

    // The document after every queued write, replayed over the seeded one, as JSON.
    private static string Replayed(Mounted mounted, TargetPageSettings? seed = null)
        => JsonSerializer.Serialize(mounted.Harness.DisplayWrites
            .Aggregate(new DisplaySettings { TargetPage = seed ?? new TargetPageSettings() }, (document, write) => write(document)).TargetPage);

    private static bool IsCollapsed(NightsLedgerPart ledger) => ledger.Classes.Contains(":collapsed");

    // The at-rest pins describe the lanes with the Session metrics section closed: open, the section
    // scrolls above the timeline rather than taking frame rows (R2).
    private static void CloseNightMetrics(Mounted mounted)
    {
        mounted.View.Named<Expander>("NightMetricsSection").IsExpanded = false;
        mounted.Settle();
    }

    private static void DragSidebar(Mounted mounted, double dx)
    {
        var handle = mounted.View.Named<Control>("SidebarHandle");
        var from = handle.TranslatePoint(new Point(handle.Bounds.Width / 2d, handle.Bounds.Height / 2d), mounted.Window)!.Value;
        TargetPartHost.Press(mounted.Window, from);
        mounted.Window.MouseMove(from + new Point(dx / 2d, 0));
        mounted.Window.MouseMove(from + new Point(dx, 0));
        Dispatcher.UIThread.RunJobs();
        mounted.Window.MouseUp(from + new Point(dx, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        mounted.Settle();
    }

    [AvaloniaTheory]
    [InlineData(600d, false, false)]
    [InlineData(520d, false, false)]
    [InlineData(519d, true, false)]
    [InlineData(472d, true, false)]
    [InlineData(471d, true, true)]
    public void Sidebar_TheFormFollowsTheStoredWidthAlone(double width, bool compact, bool collapsed)
    {
        // Red if the form is picked from the window's width rather than the sidebar's, or a
        // threshold moves: wide at 520 and up, compact from 472, collapsed under 472.
        using var mounted = Mount(1900, 900, stored: StoredModes($"{{\"sidebar_width\":{width}}}"));
        var ledger = mounted.View.Named<NightsLedgerPart>("NightsLedgerPart");
        var column = mounted.View.Named<Control>("LeftColumn");

        Assert.Equal(compact, ledger.IsCompact);
        Assert.Equal(collapsed, IsCollapsed(ledger));
        if (!collapsed)
        {
            Assert.Equal(width, column.Bounds.Width, 0.5);
        }

        Assert.Empty(mounted.Harness.DisplayWrites);
    }

    [AvaloniaFact]
    public void Sidebar_ADragUnder472SnapsToCollapsed_AndADragOutOfCollapsedSnapsToCompact()
    {
        // Red if a drag under 472 leaves a sliver of ledger, loses the open width, or a drag out of
        // collapsed does not land in the compact form at the dragged width.
        var seed = StoredModes("{\"sidebar_width\":480}");
        using var mounted = Mount(1900, 900, stored: seed);
        var ledger = mounted.View.Named<NightsLedgerPart>("NightsLedgerPart");
        var column = mounted.View.Named<Control>("LeftColumn");

        DragSidebar(mounted, -20);
        Assert.True(IsCollapsed(ledger), "not collapsed after a drag to 460");
        Assert.Contains("\"sidebar_collapsed\":true", Replayed(mounted, seed), StringComparison.Ordinal);
        Assert.Contains("\"sidebar_width\":480", Replayed(mounted, seed), StringComparison.Ordinal);

        DragSidebar(mounted, 10);
        Assert.False(IsCollapsed(ledger), "still collapsed after a drag out");
        Assert.True(ledger.IsCompact, "wide after a drag out of collapsed");
        Assert.Equal(LedgerColumn.CompactWidth + 10, column.Bounds.Width, 1.5);
        Assert.Contains("\"sidebar_collapsed\":false", Replayed(mounted, seed), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Sidebar_Collapsed_FitsTheBoxAndTheDate_HidesEveryMetricColumn_AndCannotBeNarrowed()
    {
        // Red if the collapsed sidebar is wider than the box column and the Night column, a metric
        // cell still draws, the All-nights label, the Export button or the sort note is lost, or a
        // drag narrows it.
        var seed = StoredModes("{\"sidebar_width\":600,\"sidebar_collapsed\":true}");
        using var mounted = Mount(1900, 900, stored: seed);
        var ledger = mounted.View.Named<NightsLedgerPart>("NightsLedgerPart");
        var column = mounted.View.Named<Control>("LeftColumn");
        var row = TargetPartHost.LedgerRowAt(ledger, 0);

        Assert.True(IsCollapsed(ledger));
        Assert.True(row.Children.OfType<CheckBox>().Single().IsEffectivelyVisible);
        Assert.True(TargetPartHost.CellAt(row, 1).IsEffectivelyVisible);
        foreach (var cell in row.Children.Where(child => Grid.GetColumn(child) >= 2))
        {
            Assert.False(cell.IsEffectivelyVisible && cell.Bounds.Width > 0, $"column {Grid.GetColumn(cell)} still draws");
        }

        // The Night column fits the widest of the dates and the All-nights label, never under its floor.
        var night = new[] { TargetPartHost.CellAt(row, 1).DesiredSize.Width, ledger.Named<TextBlock>("TotalsRowLabel").DesiredSize.Width, mounted.Harness.ViewModel.NightColumnMinWidth }.Max();
        var fit = row.ColumnDefinitions[0].ActualWidth + night;
        var actions = ledger.Named<StackPanel>("LedgerActions");
        output.WriteLine($"collapsed sidebar {column.Bounds.Width}, fit {fit}, actions {actions.Bounds.Width}");
        Assert.Equal(Orientation.Vertical, actions.Orientation);

        Assert.Equal(fit, column.Bounds.Width, 1d);
        Assert.True(ledger.Named<TextBlock>("TotalsRowLabel").IsEffectivelyVisible);
        Assert.True(ledger.Named<Button>("ExportButton").IsEffectivelyVisible);
        Assert.True(ledger.Named<TextBlock>("LedgerSortNote").IsEffectivelyVisible);
        Assert.False(ledger.Named<TextBlock>("LedgerTitleText").IsEffectivelyVisible);

        DragSidebar(mounted, -100);
        Assert.True(IsCollapsed(ledger));
        Assert.Equal(fit, column.Bounds.Width, 1d);
    }

    [AvaloniaFact]
    public void Sidebar_TheChevronCollapsesAndRestoresTheLastOpenWidth_AndStoresBoth()
    {
        // Red if the chevron does not toggle, expanding comes back at another width, or either
        // key is not written.
        var seed = StoredModes("{\"sidebar_width\":560}");
        using var mounted = Mount(1900, 900, stored: seed);
        var ledger = mounted.View.Named<NightsLedgerPart>("NightsLedgerPart");
        var column = mounted.View.Named<Control>("LeftColumn");
        var chevron = ledger.Named<Button>("CollapseChevron");

        TargetPartHost.Click(mounted.Window, chevron);
        mounted.Settle();
        Assert.True(IsCollapsed(ledger), "the chevron did not collapse");
        Assert.True(column.Bounds.Width < LedgerColumn.CompactWidth / 2d, $"collapsed at {column.Bounds.Width}");
        Assert.Contains("\"sidebar_collapsed\":true", Replayed(mounted, seed), StringComparison.Ordinal);

        TargetPartHost.Click(mounted.Window, chevron);
        mounted.Settle();
        Assert.False(IsCollapsed(ledger), "the chevron did not expand");
        Assert.False(ledger.IsCompact);
        Assert.Equal(560d, column.Bounds.Width, 0.5);
        Assert.Contains("\"sidebar_collapsed\":false", Replayed(mounted, seed), StringComparison.Ordinal);
        Assert.Contains("\"sidebar_width\":560", Replayed(mounted, seed), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Sidebar_WithNothingStored_IsWideAtTheLedgersWidth_AndLeftAndRightStepItAndWriteOnRelease()
    {
        // Red if a fresh profile is not the wide form at the ledger's own width, a key step is not
        // 24, or the width is written before the key is released.
        using var mounted = Mount(1900, 900);
        var ledger = mounted.View.Named<NightsLedgerPart>("NightsLedgerPart");
        var column = mounted.View.Named<Control>("LeftColumn");
        var handle = mounted.View.Named<Control>("SidebarHandle");
        Assert.False(ledger.IsCompact);
        Assert.False(IsCollapsed(ledger));
        Assert.True(column.Bounds.Width >= LedgerColumn.WideMinWidth - 0.5, $"the sidebar is {column.Bounds.Width}");
        Assert.Equal(ledger.Bounds.Width, column.Bounds.Width, 0.5);
        Assert.Empty(mounted.Harness.DisplayWrites);
        var start = column.Bounds.Width;

        handle.Focus();
        mounted.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(start - 24d, column.Bounds.Width, 1.5);
        Assert.True(ledger.IsCompact, "a step under 520 is not compact");
        Assert.Empty(mounted.Harness.DisplayWrites);
        mounted.Window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(mounted.Harness.DisplayWrites);
        Assert.Contains($"\"sidebar_width\":{start - 24d}", Replayed(mounted), StringComparison.Ordinal);

        mounted.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        mounted.Window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(start, column.Bounds.Width, 1.5);
        Assert.False(ledger.IsCompact);
    }

    [AvaloniaFact]
    public void Frames_AreCompactFromTheRightRegionsWidth_NotTheWindows()
    {
        // Red if the compact frames follow the window rather than the right region: at 1400 a
        // 900 px sidebar leaves under the 800 the wide form needs, and a collapsed one leaves more.
        var seed = StoredModes("{\"sidebar_width\":900}");
        using var mounted = Mount(1400, 900, stored: seed, realNight: true);
        var frames = mounted.View.Named<FramesPart>("FramesPart");
        var right = mounted.View.Named<Control>("RightRegion");
        Assert.True(right.Bounds.Width < FramesColumn.WideWidth, $"the right region is {right.Bounds.Width} beside a 900 px sidebar");
        Assert.True(frames.IsCompact, "the frames keep the wide chrome beside a 900 px sidebar");

        TargetPartHost.Click(mounted.Window, mounted.View.Named<NightsLedgerPart>("NightsLedgerPart").Named<Button>("CollapseChevron"));
        mounted.Settle();
        Assert.False(frames.IsCompact, "the frames stay compact beside a collapsed sidebar");
    }

    [AvaloniaFact]
    public void TheFramesRegion_IsTheFramesPartAtTheRightColumnsFullWidth()
    {
        // P24 R22: red if a findings column is back beside the frames table, or the frames part
        // is narrower than its region.
        using var mounted = Mount(realNight: true);
        var view = mounted.View;
        Assert.Null(view.NamedOrNull<Control>("FindingsPart"));
        var region = view.Named<Control>("FramesRegion");
        var frames = view.Named<FramesPart>("FramesPart");
        Assert.True(region.Bounds.Width > ModesLayoutView.FramesFloor, $"the frames region is {region.Bounds.Width} wide");
        Assert.Equal(region.Bounds.Width, frames.Bounds.Width, 0.5);
        Assert.Equal(region.Bounds.Height, frames.Bounds.Height, 0.5);
    }

    [AvaloniaFact]
    public void NightMetrics_IsOpenByDefaultHoldingTheFourParts_AndStores()
    {
        // Red if the section's default is wrong, a part is left in the sidebar or the lit row,
        // the lanes are out of order, or a toggle is not written.
        using var mounted = Mount(1900, 900);
        var view = mounted.View;
        var lanes = view.Named<ScrollViewer>("LanesRegion");
        var metrics = view.Named<Expander>("NightMetricsSection");
        var ledger = view.Named<NightsLedgerPart>("NightsLedgerPart");

        Assert.True(metrics.IsExpanded);
        Assert.Null(ledger.LitRowContent);
        Assert.Contains(lanes, metrics.GetVisualAncestors());
        foreach (var name in new[] { "PerFilterTablePart", "RangesTablePart", "ComparisonLine", "SharpestFramePart" })
        {
            var part = view.Named<Control>(name);
            Assert.Contains(metrics, part.GetVisualAncestors());
            Assert.Same(mounted.Harness.ViewModel.SelectedSession, part.DataContext);
        }

        var header = BoundsIn(view.Named<NightHeaderPart>("NightHeaderPart"), lanes);
        var section = BoundsIn(metrics, lanes);
        var timeline = BoundsIn(view.Named<NightTimelinePart>("NightTimelinePart"), lanes);
        var chart = BoundsIn(view.Named<NightMetricsPart>("NightMetricsPart"), lanes);
        output.WriteLine($"header {header}, section {section}, header line {((NightMetricsSection)metrics).HeaderHeight}, timeline {timeline}, chart {chart}, lanes {lanes.Bounds.Height}, floor {view.LanesFloor}");
        Assert.True(header.Bottom <= section.Top + 0.5 && section.Bottom <= timeline.Top + 0.5, $"header {header}, metrics {section}, timeline {timeline}");
        Assert.True(timeline.Bottom <= chart.Top + 0.5, $"timeline {timeline}, chart {chart}");
        Assert.Empty(mounted.Harness.DisplayWrites);

        metrics.IsExpanded = false;
        mounted.Settle();
        Assert.Contains("\"night_metrics_open\":false", Replayed(mounted), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void NightMetrics_OpensAsStored_WithoutAWrite()
    {
        // Red if a stored state is not applied, or applying it queues a write.
        var seed = StoredModes("{\"night_metrics_open\":false,\"notes_open\":true}");
        using var mounted = Mount(1900, 900, stored: seed);
        var metrics = mounted.View.Named<Expander>("NightMetricsSection");
        Assert.False(metrics.IsExpanded);
        Assert.Empty(mounted.Harness.DisplayWrites);
    }

    [AvaloniaFact]
    public void NightHeader_HasNoNotesDisclosure()
    {
        // Red if the "Notes" button or the notes glyph is back at the top right of the night
        // header (R17): the Session notes section is the one disclosure.
        using var mounted = Mount(1900, 900);
        var header = mounted.View.Named<NightHeaderPart>("NightHeaderPart");
        Assert.Null(header.NamedOrNull<Control>("NotesToggle"));
        Assert.DoesNotContain(header.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Notes");
        Assert.DoesNotContain(header.GetVisualDescendants().OfType<HelpButton>(), glyph => glyph.Topic == "target.session-notes");
    }

    [AvaloniaFact]
    public void GuidingPart_IsNoLongerInTheLayout()
    {
        // Red if the separate guiding section is still placed under the chart (R3 moves guiding into the chart).
        using var mounted = Mount(1900, 900);
        Assert.Null(mounted.View.NamedOrNull<Control>("GuidingPart"));
        Assert.DoesNotContain(mounted.View.GetLogicalDescendants().OfType<Control>(), control => control.GetType().Name == "GuidingPart");
    }

    // ---- Phase 24 R19 and R20: the section writes off the UI thread, the lanes capped at their content ----

    [AvaloniaTheory]
    [InlineData("NightMetricsSection", "night_metrics_open", false)]
    public async Task ASectionToggle_QueuesAWriteTheWriterCanRunOffTheUiThread(string section, string key, bool toggled)
    {
        // Red if the queued write reads the section inside the writer's thread: "Call from invalid
        // thread" and the toggle never persists (R19).
        using var mounted = Mount(1900, 900);
        mounted.View.Named<Expander>(section).IsExpanded = toggled;
        mounted.Settle();
        var write = Assert.Single(mounted.Harness.DisplayWrites);

        var written = await Task.Run(() => write(new DisplaySettings()));

        var layout = JsonSerializer.Serialize(written.TargetPage.Layouts["modes"]);
        Assert.Contains($"\"{key}\":{(toggled ? "true" : "false")}", layout, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AHandleKeyPress_QueuesAWriteTheWriterCanRunOffTheUiThread_AndGrowsTheChart()
    {
        // Red if the queued write reads a control inside the writer's thread: "Call from invalid
        // thread" and the height never persists (R19's shape); or the chart does not follow the
        // one handle's step (night pane round).
        using var mounted = Mount(1900, 900, stored: StoredModes("{\"night_metrics_open\":false}"));
        var lanes = mounted.View.Named<ScrollViewer>("LanesRegion").Bounds.Height;
        mounted.View.Named<LanesHandle>("LanesHandle").Focus();
        mounted.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        mounted.Window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        mounted.Settle();
        var write = Assert.Single(mounted.Harness.DisplayWrites);

        var written = await Task.Run(() => write(new DisplaySettings()));

        var layout = written.TargetPage.Layouts["modes"];
        Assert.Equal(lanes + LanesHandle.KeyStep, layout.LanesHeight!.Value, 1d);
        Assert.Null(layout.ChartHeight);
        Assert.Equal(ModesLayoutView.ChartHeightDefault + LanesHandle.KeyStep, mounted.View.Named<Control>("NightMetricsPart").Bounds.Height, 1d);
    }

    [AvaloniaFact]
    public void TheMetricsSectionClosed_TheChartEndsAtTheHandle_AndOpeningItScrollsTheLanes()
    {
        // Red if a stored lanes height leaves empty space between the chart and the handle (the
        // chart no longer fills the cap), or the section's open body moves the handle instead of
        // scrolling inside the lanes (R2, R15).
        var seed = StoredModes("{\"lanes_height\":600,\"night_metrics_open\":true}");
        using var mounted = Mount(1900, 1400, stored: seed);
        var view = mounted.View;
        var region = view.Named<Grid>("NightReviewRegion");
        var lanes = view.Named<ScrollViewer>("LanesRegion");
        var metrics = view.Named<Expander>("NightMetricsSection");
        var chart = view.Named<Control>("NightMetricsPart");
        var frames = view.Named<Control>("FramesRegion");

        metrics.IsExpanded = false;
        mounted.Settle();
        output.WriteLine($"chart bottom {BoundsIn(chart, region).Bottom}, lanes {lanes.Bounds.Height}, frames top {BoundsIn(frames, region).Top}");
        Assert.Equal(600d, lanes.Bounds.Height, 1d);
        Assert.Equal(BoundsIn(chart, region).Bottom + LanesHandle.Thickness, BoundsIn(frames, region).Top, 1d);

        metrics.IsExpanded = true;
        mounted.Settle();
        Assert.Equal(600d, lanes.Bounds.Height, 1d);
        Assert.True(lanes.Extent.Height > lanes.Viewport.Height + 20d, $"extent {lanes.Extent.Height}, viewport {lanes.Viewport.Height} with the section open");
    }

    // The shell's history observes the page's Mode (.planning/mouse-navigation.md): a strip press
    // writes it, and Back across a mode switch writes it the other way, which the layout follows.
    [AvaloniaFact]
    public void ModeStrip_ReportsToThePage_AndFollowsThePage()
    {
        using var mounted = Mount();
        var view = mounted.View;
        var page = mounted.Page;

        // Attach never writes the page, or every open would push a second history entry.
        Assert.Null(page.Mode);

        view.SelectMode.Execute(TargetPageMode.Integration);
        mounted.Settle();
        Assert.Equal(nameof(TargetPageMode.Integration), page.Mode);
        Assert.True(view.Named<Control>("IntegrationRegion").IsVisible);

        page.Mode = nameof(TargetPageMode.CompareNights);
        mounted.Settle();
        Assert.Equal(TargetPageMode.CompareNights, view.Mode);
        Assert.True(view.Named<Control>("CompareNightsRegion").IsVisible);
        Assert.False(view.Named<Control>("IntegrationRegion").IsVisible);
    }
}
