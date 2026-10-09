using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views.Settings;

// Phase 20 Task 6c: the Nights ledger column picker's own block on the Display tab, the third of
// three (dashboard, frames, ledger). Views/SettingsPreferenceTabViewTests.cs already covers the
// first two blocks' own construction and click behaviour; this file adds only the third and the
// two things unique to it: the help glyph and the empty state (amendment 2.9).
public class DisplayTabLedgerPickerTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 1100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    // Built directly, not through PreferenceTabViewModelTestFactory.NewDisplayTab (that factory
    // has no seam for a custom column list and this dispatch does not touch it): the same shape
    // that factory and SettingsPreferenceTabViewTests.NewDisplayAsync already take.
    private static async Task<DisplayTabViewModel> NewDisplayAsync(
        IReadOnlyList<CustomColumnDefinition>? customColumns = null)
    {
        var general = new GeneralSettings();
        var display = new DisplaySettings();
        var graph = new GraphSettings();
        var columns = new DisplayColumnWriter(() => display, value => display = value);
        var dashboard = new TargetListViewModel(display, columns, general.DefaultPageSize);

        var tab = new DisplayTabViewModel(
            () => general,
            mutate =>
            {
                general = mutate(general);
                return general;
            },
            () => display,
            value => display = value,
            () => graph,
            new GraphSettingsWriter(() => graph, value => graph = value),
            columns,
            () => ThemeManager.Available,
            _ => { },
            dashboardColumns: dashboard.Columns,
            toggleDashboardColumn: column => dashboard.ToggleColumnCommand.Execute(column),
            post: action => action(),
            loadCustomColumns: customColumns is null ? null : () => customColumns);

        return await tab.SettleAsync().ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task TheTab_DrawsThreePickersInOrder()
    {
        using var tab = await NewDisplayAsync();
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        var texts = VisibleTexts(view).ToList();
        var dashboard = texts.IndexOf("Dashboard columns");
        var frames = texts.IndexOf("Frame table columns");
        var ledger = texts.IndexOf("Nights list columns");

        Assert.True(dashboard >= 0 && frames >= 0 && ledger >= 0);
        Assert.True(dashboard < frames);
        Assert.True(frames < ledger);
    }

    [AvaloniaFact]
    public async Task TheLedgerBlock_CarriesItsHelpGlyph()
    {
        using var tab = await NewDisplayAsync();
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        var section = view.GetControl<Border>("LedgerColumnsSection");
        var glyph = section.GetVisualDescendants().OfType<HelpButton>().Single();

        Assert.Equal("settings.display.ledger-columns", glyph.Topic);
    }

    [AvaloniaFact]
    public async Task TheTab_HasNoTargetPageLayoutBox()
    {
        // A failure looks like the "Target page layout" combo still drawn under Content width.
        using var tab = await NewDisplayAsync();
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        Assert.NotNull(view.NamedOrNull<ComboBox>("ContentWidthBox"));
        Assert.Null(view.NamedOrNull<ComboBox>("TargetLayoutBox"));
        Assert.DoesNotContain("Target page layout", VisibleTexts(view));
    }

    [AvaloniaFact]
    public void TheTab_DeclaresNoStyleOfItsOwn()
    {
        var path = Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Settings", "DisplayTabView.axaml");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("<Style ", source);
    }

    [AvaloniaFact]
    public async Task TheTab_ShowsTheEmptyStateRatherThanAnEmptyList()
    {
        using var tab = await NewDisplayAsync(customColumns: []);
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        var emptyText = view.GetControl<TextBlock>("LedgerColumnsEmptyText");
        var list = view.GetControl<ItemsControl>("LedgerColumnList");

        Assert.True(emptyText.IsEffectivelyVisible);
        Assert.Equal("No custom columns yet.", emptyText.Text);
        Assert.False(list.IsEffectivelyVisible);
    }

    // Not in the brief's numbered list, but proves the wiring the brief describes in section 4:
    // a column read through loadCustomColumns on this tab's own background pass reaches
    // LedgerColumns before the tab is shown, with no restart.
    [AvaloniaFact]
    public async Task ALoadedCustomColumn_AppearsInTheLedgerPickerOn()
    {
        var column = new CustomColumnDefinition(
            Guid.NewGuid(), "Notes tag", "custom_notes_tag", CustomColumnType.Text,
            CustomColumnScope.Session, [], 0, DateTime.UtcNow, 0);

        using var tab = await NewDisplayAsync(customColumns: [column]);
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        var emptyText = view.GetControl<TextBlock>("LedgerColumnsEmptyText");
        var list = view.GetControl<ItemsControl>("LedgerColumnList");

        Assert.False(emptyText.IsEffectivelyVisible);
        Assert.True(list.IsEffectivelyVisible);

        var box = list.GetVisualDescendants().OfType<CheckBox>().Single();
        Assert.Equal("Notes tag", box.Content as string);
        Assert.True(box.IsChecked);
    }

    // ---- ruling C22 with C32: the tab was frozen for the life of the process -------------------

    private sealed record EventHost(
        DisplayTabViewModel Tab,
        Action<IReadOnlyList<CustomColumnDefinition>> SetColumns,
        Action Raise,
        Func<int> Subscribers);

    // The same shape as NewDisplayAsync above, plus the definition-change delegate pair the host
    // wires and a definition list a case can change under the tab, which is what a create on the
    // Custom Columns tab does to the live application.
    private static async Task<EventHost> NewDisplayWithEventAsync(
        IReadOnlyList<CustomColumnDefinition> columns,
        Func<IReadOnlyList<CustomColumnDefinition>>? read = null)
    {
        var general = new GeneralSettings();
        var display = new DisplaySettings();
        var graph = new GraphSettings();
        var writer = new DisplayColumnWriter(() => display, value => display = value);
        var dashboard = new TargetListViewModel(display, writer, general.DefaultPageSize);
        var definitions = columns;
        var handlers = new List<EventHandler>();

        var tab = new DisplayTabViewModel(
            () => general,
            mutate =>
            {
                general = mutate(general);
                return general;
            },
            () => display,
            value => display = value,
            () => graph,
            new GraphSettingsWriter(() => graph, value => graph = value),
            writer,
            () => ThemeManager.Available,
            _ => { },
            dashboardColumns: dashboard.Columns,
            toggleDashboardColumn: column => dashboard.ToggleColumnCommand.Execute(column),
            post: action => Dispatcher.UIThread.Post(action),
            loadCustomColumns: read ?? (() => definitions),
            subscribeCustomColumnsChanged: handler => handlers.Add(handler),
            unsubscribeCustomColumnsChanged: handler => handlers.Remove(handler));

        await tab.SettleAsync().ConfigureAwait(true);

        // The post seam is the dispatcher's, not an inline call, because the handler's own publish
        // arrives from a pool thread and an inline seam would touch bound collections there. So the
        // first publish is queued and has to be drained before anything is asserted.
        Dispatcher.UIThread.RunJobs();
        return new EventHost(
            tab,
            next => definitions = next,
            () =>
            {
                foreach (var handler in handlers.ToList())
                {
                    handler(null, EventArgs.Empty);
                }
            },
            () => handlers.Count);
    }

    // Awaits the handler's own background read through the tab's own seam and then drains the
    // publish it posted. No delay and nothing blocked: PendingCustomColumnsRead is the read itself.
    private static async Task SettleTheHandlerAsync(DisplayTabViewModel tab)
    {
        await tab.PendingCustomColumnsRead!.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
    }

    // Journeys surface 3: this tab is a lazily built singleton behind a memoized navigation item,
    // so ApplyDisplayDocument ran once ever and the picker's rows were the definitions as they were
    // at that moment. A reader who opened Display, then created a Night column, then came back read
    // "No custom columns yet." for the rest of the process.
    //
    // Red against the tab with no OnCustomColumnsChanged: the picker stays empty and the empty
    // sentence stays visible however many times the event is raised.
    [AvaloniaFact]
    public async Task AColumnCreatedAfterThisTabWasBuilt_ReachesTheLedgerPicker()
    {
        var host = await NewDisplayWithEventAsync([]);
        using var tab = host.Tab;
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        Assert.True(tab.LedgerColumns.IsEmpty);
        Assert.True(view.GetControl<TextBlock>("LedgerColumnsEmptyText").IsEffectivelyVisible);

        host.SetColumns(
        [
            CustomColumnTestFactory.Text("Night note", scope: CustomColumnScope.Session),
        ]);
        host.Raise();
        await SettleTheHandlerAsync(tab);

        Assert.False(tab.LedgerColumns.IsEmpty);
        Assert.Equal("Night note", tab.LedgerColumns.Columns.Single().Title);
        Assert.True(tab.LedgerColumns.Columns.Single().IsVisible);

        var list = view.GetControl<ItemsControl>("LedgerColumnList");
        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal("Night note", list.GetVisualDescendants().OfType<CheckBox>().Single().Content as string);
        Assert.False(view.GetControl<TextBlock>("LedgerColumnsEmptyText").IsEffectivelyVisible);
    }

    // The one thing the handler must NOT do. A definition change never touched the display document,
    // and an unsaved metric group edit survives navigating away from this tab, so rebuilding the
    // whole document here would discard it silently. Only the ledger picker is rebuilt.
    //
    // Red against a handler that calls ApplyDisplayDocument: GroupsDirty reads false afterwards and
    // the edited field is back to its stored value.
    [AvaloniaFact]
    public async Task TheEvent_LeavesAnUnsavedMetricGroupEditAlone()
    {
        var host = await NewDisplayWithEventAsync([]);
        using var tab = host.Tab;

        var field = tab.Groups.SelectMany(group => group.Fields).First();
        field.IsChecked = !field.IsChecked;
        var edited = field.IsChecked;
        Assert.True(tab.GroupsDirty);

        host.SetColumns(
        [
            CustomColumnTestFactory.Text("Night note", scope: CustomColumnScope.Session),
        ]);
        host.Raise();
        await SettleTheHandlerAsync(tab);

        Assert.Equal("Night note", tab.LedgerColumns.Columns.Single().Title);
        Assert.True(tab.GroupsDirty);
        Assert.Equal(edited, tab.Groups.SelectMany(group => group.Fields).First().IsChecked);
    }

    // Ruling C32: the subscription is the constructor's, so a tab nobody visited subscribes nothing
    // and the composition root never has to resolve it to deliver the event. The detach is the
    // tab's own dispose, so a disposed tab is not left holding a handler on a process-wide
    // repository.
    //
    // Red against a host-attached handler (nothing subscribes from the constructor, so the count
    // reads 0) and red against a DisposeCore that forgets the detach (the count stays 1).
    [AvaloniaFact]
    public async Task TheSubscription_IsTheConstructorsAndTheDetachIsTheDisposes()
    {
        var host = await NewDisplayWithEventAsync([]);

        Assert.Equal(1, host.Subscribers());

        host.Tab.Dispose();

        Assert.Equal(0, host.Subscribers());
    }

    // The gate the case below closes, held open on the pool thread inside the handler's own
    // Task.Run: loadCustomColumns is a synchronous delegate, so a read that has to finish late has
    // to be held there and nowhere else. No test thread blocks on it, which is what xUnit1031 is
    // about, and the wait is bounded so a regression fails rather than wedging the run.
    private static void HoldTheReadOpen(Task gate)
        => gate.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    // Review P2 4: every definition change starts its own catalogue read, and until the generation
    // guard existed nothing decided which of two in-flight reads was allowed to publish. Two
    // creates in quick succession, or the reorder arrows twice, could therefore finish out of order
    // and leave the Nights ledger picker showing the older list for the rest of the process, with
    // no further event to correct it.
    //
    // Red against the unguarded handler, which publishes whatever each read returned: the picker
    // holds "Older" at the end, because the slow first read's publish is drained after the fast
    // second one's. Green with the guard: the first publish is dropped and "Newer" stands.
    [AvaloniaFact]
    public async Task TwoDefinitionChangesThatFinishOutOfOrder_LeaveThePickerOnTheNewerList()
    {
        IReadOnlyList<CustomColumnDefinition> definitions = [];
        var gate = new TaskCompletionSource();
        var firstReadStarted = new TaskCompletionSource();
        var holdTheNextRead = 0;

        // A read that takes the list as it was when it started and returns it late, which is the
        // real shape: the repository read runs on the pool and two of them can overlap.
        var host = await NewDisplayWithEventAsync(
            [],
            read: () =>
            {
                var snapshot = Volatile.Read(ref definitions);
                if (Interlocked.Exchange(ref holdTheNextRead, 0) == 1)
                {
                    firstReadStarted.SetResult();
                    HoldTheReadOpen(gate.Task);
                }

                return snapshot;
            });

        using var tab = host.Tab;

        Volatile.Write(ref definitions, [CustomColumnTestFactory.Text("Older", scope: CustomColumnScope.Session)]);
        Interlocked.Exchange(ref holdTheNextRead, 1);
        host.Raise();
        var first = tab.PendingCustomColumnsRead!;
        await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        // The second change lands while the first read is still open, and finishes first.
        Volatile.Write(ref definitions, [CustomColumnTestFactory.Text("Newer", scope: CustomColumnScope.Session)]);
        host.Raise();
        var second = tab.PendingCustomColumnsRead!;
        await second.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        gate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Newer", tab.LedgerColumns.Columns.Single().Title);
    }

    // Journeys P2-5 on this tab. Spec 12.15: a picker lists custom columns under a "Custom" heading
    // below the built-in ones, and the heading is drawn only when there is a second group to name.
    //
    // Red against the view binding DashboardColumns.Columns, the flat list: the two headings are
    // absent from the block and the custom entry reads as one more unlabelled built-in column.
    [AvaloniaFact]
    public async Task TheDashboardPicker_DrawsTheBuiltInAndCustomHeadings_OnlyWhenACustomColumnExists()
    {
        using var plain = await NewDisplayAsync();
        var plainView = new DisplayTabView { DataContext = plain };
        Show(plainView);

        var plainSection = plainView.GetControl<Border>("DashboardColumnsSection");
        Assert.False(plain.DashboardColumns!.ShowGroupHeadings);
        Assert.DoesNotContain("Custom", HeadingTexts(plainSection));
        Assert.DoesNotContain("Built-in", HeadingTexts(plainSection));

        // The same tab, with a target-scope column on the live dashboard list the picker is a view
        // over, which is the state ruling C22's route produces.
        var (tab, dashboard) = await NewDisplayOverTheDashboardAsync();
        using var _ = tab;
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        dashboard.Columns.Add(new ColumnViewModel("custom_processed", "Processed", false, canHide: true));
        Dispatcher.UIThread.RunJobs();

        var section = view.GetControl<Border>("DashboardColumnsSection");
        Assert.True(tab.DashboardColumns!.ShowGroupHeadings);
        Assert.Contains("Built-in", HeadingTexts(section));
        Assert.Contains("Custom", HeadingTexts(section));

        // The heading really is above the entry it names, not beside it.
        var texts = VisibleTexts(section).ToList();
        Assert.True(texts.IndexOf("Custom") < texts.IndexOf("Processed"));
        Assert.True(texts.IndexOf("Built-in") < texts.IndexOf("Custom"));
    }

    private static IReadOnlyList<string> HeadingTexts(Control section)
        => [.. section.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.Name == "DashboardColumnGroupHeading" && block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    // The same tab as NewDisplayAsync, with the live target list handed back so a case can append a
    // column to it the way a query does.
    private static async Task<(DisplayTabViewModel Tab, TargetListViewModel Dashboard)> NewDisplayOverTheDashboardAsync()
    {
        var general = new GeneralSettings();
        var display = new DisplaySettings();
        var graph = new GraphSettings();
        var writer = new DisplayColumnWriter(() => display, value => display = value);
        var dashboard = new TargetListViewModel(display, writer, general.DefaultPageSize);

        var tab = new DisplayTabViewModel(
            () => general,
            mutate =>
            {
                general = mutate(general);
                return general;
            },
            () => display,
            value => display = value,
            () => graph,
            new GraphSettingsWriter(() => graph, value => graph = value),
            writer,
            () => ThemeManager.Available,
            _ => { },
            dashboardColumns: dashboard.Columns,
            toggleDashboardColumn: column => dashboard.ToggleColumnCommand.Execute(column),
            post: action => action());

        return (await tab.SettleAsync().ConfigureAwait(true), dashboard);
    }
}
