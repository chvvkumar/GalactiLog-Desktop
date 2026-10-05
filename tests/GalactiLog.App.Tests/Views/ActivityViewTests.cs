using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.Views;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.ActivityViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Spec 18.3's view smoke scope: the view constructs, binds to a populated view-model and lays out
// non-zero without throwing; every resource key it names resolves (ThemeResourceTest covers that
// repository-wide); nothing sets FontSize (FontSizeTokenTest covers that repository-wide). Plus
// the virtualization assertions in the shape Phase 6's FrameTableViewTests uses.
//
// Every case builds its page through Factory.CreateAsync, which awaits rather than blocks: an
// AvaloniaFact runs on the headless UI thread and that thread is a pool thread, so a blocking wait
// at construction can inline the load onto it (review finding 12, TRACKING section 2 item 8).
public class ActivityViewTests
{
    private static (Window Window, ActivityView View) Show(ActivityViewModel page)
    {
        var view = new ActivityView { DataContext = page };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static List<string?> Texts(ActivityView view)
        => [.. view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)];

    [AvaloniaFact]
    public async Task Constructs_AndLaysOutNonZero()
    {
        using var page = await Factory.CreateAsync();
        var (_, view) = Show(page);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public async Task PopulatedPage_RendersTheRowFields()
    {
        var instant = new DateTime(2025, 3, 4, 21, 5, 0, DateTimeKind.Utc);
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.Page(
        [
            Factory.Row(1, "Scan complete (12 new)", timestamp: instant, eventType: "scan_complete", durationMs: 91_000),
        ]));
        var (_, view) = Show(page);

        var texts = Texts(view);

        Assert.Contains("2025-03-04", texts);
        Assert.Contains("21:05", texts);
        Assert.Contains("scan", texts);
        Assert.Contains("Scan complete (12 new)", texts);
        Assert.Contains("1:31", texts);
    }

    // Review finding 11, ruled: the only writer of duration_ms is the terminal scan event, which
    // carries parent_id, so the run's duration reaches the collapsed parent through the same
    // mechanism as the alert glyph.
    [AvaloniaFact]
    public async Task CollapsedScanRow_ShowsTheTerminalChildsDuration()
    {
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying", parentId: 1),
            Factory.Row(3, "Scan complete (12 new)", eventType: "scan_complete", parentId: 1, durationMs: 91_000)));
        var (_, view) = Show(page);

        Assert.False(page.Rows.Single().AreChildrenExpanded);
        Assert.Contains("1:31", Texts(view));
    }

    [AvaloniaFact]
    public async Task FilterPills_RenderBothRows()
    {
        using var page = await Factory.CreateAsync();
        var (window, view) = Show(page);

        var severity = view.GetControl<ItemsControl>("SeverityPills");
        var category = view.GetControl<ItemsControl>("CategoryPills");

        Assert.Equal(4, severity.GetVisualDescendants().OfType<Button>().Count());
        Assert.Equal(ActivityQuery.ValidCategories.Count + 1, category.GetVisualDescendants().OfType<Button>().Count());

        var texts = Texts(view);
        Assert.Contains("warn", texts);
        Assert.Contains("enrich", texts);
        Assert.True(window.Bounds.Width > 0);
    }

    [AvaloniaFact]
    public async Task EmptyLog_RendersTheSpecEmptyState()
    {
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.Empty());
        var (_, view) = Show(page);

        Assert.True(view.GetControl<TextBlock>("EmptyState").IsVisible);
        Assert.False(view.GetControl<ScrollViewer>("ActivityRowsScroller").IsVisible);
        Assert.Equal("No activity recorded.", view.GetControl<TextBlock>("EmptyState").Text);
    }

    [AvaloniaFact]
    public async Task FailedLoad_RendersTheFailureLine_AndNotTheEmptyState()
    {
        using var page = await Factory.CreateAsync(
            page: (_, _, _) => throw new InvalidOperationException("no database"));
        var (_, view) = Show(page);

        Assert.True(view.GetControl<TextBlock>("FailureLine").IsVisible);
        Assert.False(view.GetControl<TextBlock>("EmptyState").IsVisible);
    }

    // Review finding 1: a reload must not collapse the feed to the loading line. The second read
    // is gated open, so the assertions run while the reload is genuinely in flight, and the page is
    // disposed before the gate is released: its publish then no-ops rather than mutating a bound
    // collection from the reading thread (the factory's post seam runs inline).
    [AvaloniaFact]
    public async Task ReloadingAPopulatedList_KeepsTheRowsOnScreen()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var loads = 0;
        var page = await Factory.CreateAsync(page: (_, _, _) =>
        {
            if (Interlocked.Increment(ref loads) > 1)
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            return Factory.Page([Factory.Row(1, "first")]);
        });
        var (_, view) = Show(page);

        page.RefreshCommand.Execute(null);
        started.Wait(TimeSpan.FromSeconds(30));
        Dispatcher.UIThread.RunJobs();

        Assert.True(page.IsLoading);
        Assert.True(view.GetControl<ScrollViewer>("ActivityRowsScroller").IsVisible);
        Assert.False(view.GetControl<TextBlock>("LoadingLine").IsVisible);
        Assert.Contains("first", Texts(view));

        page.Dispose();
        gate.Set();
        await Factory.SettleAsync(page);
    }

    [AvaloniaFact]
    public async Task LoadOlderButton_IsHiddenWithoutACursor_AndShownWithOne()
    {
        using var withoutCursor = await Factory.CreateAsync();
        var (_, view) = Show(withoutCursor);
        Assert.False(view.GetControl<Button>("LoadOlderButton").IsVisible);
        Assert.False(view.GetControl<TextBlock>("AppendFailureLine").IsVisible);

        using var withCursor = await Factory.CreateAsync(
            page: (_, _, _) => Factory.Page(next: new ActivityCursor(Factory.Noon, 1)));
        var (_, withCursorView) = Show(withCursor);

        Assert.True(withCursorView.GetControl<Button>("LoadOlderButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task ExpandingAScanEvent_RendersItsChildren()
    {
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.WithChildren(
            Factory.Row(2, "Classifying 3/10 files", parentId: 1)));
        var (_, view) = Show(page);

        Assert.DoesNotContain("Classifying 3/10 files", Texts(view));

        page.Rows.Single().ToggleChildrenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Classifying 3/10 files", Texts(view));
    }

    [AvaloniaFact]
    public async Task ExpandingDetails_RendersTheKeyValueTable()
    {
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.Page(
            [Factory.Row(1, "Activity log pruned", details: """{"deleted_count":12,"retention_days":90}""")]));
        var (_, view) = Show(page);

        page.Rows.Single().ToggleDetailsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var texts = Texts(view);
        Assert.Contains("Deleted count", texts);
        Assert.Contains("Retention days", texts);
        Assert.Contains("90", texts);
    }

    [AvaloniaFact]
    public async Task ExpandingDetails_FallsBackToFormattedJson()
    {
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.Page(
            [Factory.Row(1, "Scan started (manual)", details: """{"trigger":"manual","roots":["D:\\Lights"]}""")]));
        var (_, view) = Show(page);

        page.Rows.Single().ToggleDetailsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Texts(view), text => text is { } value && value.Contains("\"roots\"", StringComparison.Ordinal));
    }

    // The same shape FrameTableViewTests uses for the frame table (TRACKING section 6 item 12). A
    // retention window on an active library is tens of thousands of events and "Load older" appends
    // without bound, so the list has to realise the viewport rather than the collection.
    [AvaloniaFact]
    public async Task ALongList_RealisesFarFewerContainersThanRows()
    {
        var rows = Enumerable.Range(0, 400)
            .Select(i => Factory.Row(i + 1, $"event {i:000}", Factory.Noon.AddSeconds(-i)))
            .ToList();
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.Page(rows));
        var (_, view) = Show(page);

        Assert.Equal(400, page.Rows.Count);

        var realised = view.GetControl<ItemsControl>("ActivityRows")
            .GetRealizedContainers()
            .Count();
        Assert.InRange(realised, 1, 80);

        // The rows that do exist are real rows, bound in order from the top of the list.
        var texts = Texts(view);
        Assert.Contains("event 000", texts);
        Assert.DoesNotContain("event 399", texts);
    }

    // Review finding 2. A scan's child count is unbounded by construction, so the sub-event rail
    // virtualizes against its own bounded scroller exactly as the top-level list does.
    [AvaloniaFact]
    public async Task AnExpandedScanWithManyChildren_RealisesFarFewerContainersThanChildren()
    {
        var children = Enumerable.Range(0, 300)
            .Select(i => Factory.Row(i + 2, $"rejected {i:000}", Factory.Noon.AddSeconds(i),
                severity: "warning", eventType: "file_rejected", parentId: 1))
            .ToArray();
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.WithChildren(children));
        var (_, view) = Show(page);

        var row = page.Rows.Single();
        Assert.Equal(300, row.ChildCount);

        // Nothing is built until the reader expands the scan.
        Assert.Empty(row.Children);

        row.ToggleChildrenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(300, row.Children.Count);
        var rail = view.GetVisualDescendants()
            .OfType<ItemsControl>()
            .Single(control => control.Name == "ChildRows");
        Assert.InRange(rail.GetRealizedContainers().Count(), 1, 60);
    }

    // Phase 14B Task 7 (PAR-017, spec 12.6). The seen marker's "new" badge, inside column 0's
    // cell (section 7.3), and the vocabulary move's four cardinality assertions restated here so
    // they fail in the task-filtered run rather than only in the full suite.

    [AvaloniaFact]
    public async Task View_AnUnseenRow_CarriesTheNewBadge()
    {
        using var page = await Factory.CreateAsync(
            page: (_, _, _) => Factory.Page([Factory.Row(1, timestamp: Factory.Noon)]),
            general: Factory.Settings());
        var (_, view) = Show(page);

        Assert.True(page.Rows.Single().IsUnseen);
        var badge = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tag"));
        Assert.True(badge.IsVisible);
    }

    [AvaloniaFact]
    public async Task View_ASeenRow_CarriesNoBadge()
    {
        var seenAt = Factory.Noon.AddDays(1);
        using var page = await Factory.CreateAsync(
            page: (_, _, _) => Factory.Page([Factory.Row(1, timestamp: Factory.Noon)]),
            general: Factory.Settings() with { ActivitySeenAt = seenAt });
        var (_, view) = Show(page);

        Assert.False(page.Rows.Single().IsUnseen);
        var badges = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tag")).ToList();
        Assert.All(badges, b => Assert.False(b.IsVisible));
    }

    [AvaloniaFact]
    public async Task View_TheBadge_IsBorderTag()
    {
        using var page = await Factory.CreateAsync(
            page: (_, _, _) => Factory.Page([Factory.Row(1, timestamp: Factory.Noon)]));
        var (_, view) = Show(page);

        var badge = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tag"));
        Assert.Equal("new", (badge.Child as TextBlock)?.Text);
    }

    [AvaloniaFact]
    public async Task View_TheBadge_IsInTheLeadingCell()
    {
        using var page = await Factory.CreateAsync(
            page: (_, _, _) => Factory.Page([Factory.Row(1, timestamp: Factory.Noon)]));
        var (_, view) = Show(page);

        var badge = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tag"));
        var cell = badge.GetVisualParent();
        Assert.IsType<Panel>(cell);
        Assert.Equal(0, Grid.GetColumn((Control)cell!));
    }

    [AvaloniaFact]
    public async Task View_TheRowTemplateColumnIndices_AreUnchanged()
    {
        // Section 7.3: the badge sits inside column 0's cell rather than adding a column, so no
        // Grid.Column index shifts. Message, duration and the chevron are still at columns 4, 5
        // and 6.
        //
        // Phase 14B fixer, fixer list item 53 (task7-review P3). This used to assert five rendered
        // text fragments and never read a Grid.Column at all, although the rule it exists for is
        // directly assertable and the sibling above already reads Grid.GetColumn. The indices are
        // read off the controls now.
        var instant = new DateTime(2025, 3, 4, 21, 5, 0, DateTimeKind.Utc);
        using var page = await Factory.CreateAsync(page: (_, _, _) => Factory.Page(
        [
            Factory.Row(1, "Scan complete (12 new)", timestamp: instant, eventType: "scan_complete", durationMs: 91_000),
        ]));
        var (_, view) = Show(page);

        var blocks = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        var message = Assert.Single(blocks, block => block.Text == "Scan complete (12 new)");
        var duration = Assert.Single(blocks, block => block.Text == "1:31");

        // Scoped to the row grid the message sits in: the page header carries its own help button
        // and the details chevron class is shared with it.
        var rowGrid = Assert.IsType<Grid>(message.GetVisualParent());
        var chevron = Assert.Single(
            rowGrid.Children.OfType<Button>(),
            button => button.Classes.Contains("chevron"));

        Assert.Equal(4, Grid.GetColumn(message));
        Assert.Equal(5, Grid.GetColumn(duration));
        Assert.Equal(6, Grid.GetColumn(chevron));
        Assert.Same(rowGrid, duration.GetVisualParent());
    }

    [AvaloniaFact]
    public void View_DeclaresNoBadgeStyleOfItsOwn()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Views", "ActivityView.axaml"));

        Assert.DoesNotContain("Border.tag", markup, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task View_StillHasItsPillCounts()
    {
        using var page = await Factory.CreateAsync();
        var (window, view) = Show(page);

        var severity = view.GetControl<ItemsControl>("SeverityPills");
        var category = view.GetControl<ItemsControl>("CategoryPills");

        Assert.Equal(4, severity.GetVisualDescendants().OfType<Button>().Count());
        Assert.Equal(ActivityQuery.ValidCategories.Count + 1, category.GetVisualDescendants().OfType<Button>().Count());
        Assert.True(window.Bounds.Width > 0);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }
}
