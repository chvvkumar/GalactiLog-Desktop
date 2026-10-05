using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Activity;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 7 (PAR-017, spec 12.6). The seen marker's five-row truth table and the rules
// around it: read before write, one write per open, display state only.
public class ActivitySeenMarkerTests
{
    private static async Task<ActivityViewModel> OpenAsync(ActivityViewModel page)
    {
        page.MarkOpened();
        if (page.PendingOpen is { } pending)
        {
            await pending;
        }

        return page;
    }

    [Fact]
    public async Task AFreshProfile_MarksEveryRow()
    {
        var rows = new[]
        {
            ActivityViewModelTestFactory.Row(1, timestamp: ActivityViewModelTestFactory.Noon),
            ActivityViewModelTestFactory.Row(2, timestamp: ActivityViewModelTestFactory.Noon.AddMinutes(-5)),
        };

        // Never opened: general.activity_seen_at is null, the compile-time default.
        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, _, _) => ActivityViewModelTestFactory.Page(rows));

        Assert.All(page.Rows, row => Assert.True(row.IsUnseen));
    }

    [Fact]
    public async Task TheFirstOpen_RendersAgainstNull_ThenWritesTheCapturedTime()
    {
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        var rows = new[] { ActivityViewModelTestFactory.Row(1, timestamp: ActivityViewModelTestFactory.Noon) };

        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, _, _) => ActivityViewModelTestFactory.Page(rows),
            general: store.Current,
            mutateGeneral: store.Mutate);

        // Renders against null: the row built at construction, before any open, is unseen.
        Assert.True(page.Rows.Single().IsUnseen);

        Assert.Null(store.Current.ActivitySeenAt);
        await OpenAsync(page);
        Assert.NotNull(store.Current.ActivitySeenAt);
    }

    [Fact]
    public async Task TheValueIsCapturedBeforeTheRender()
    {
        // The obvious wrong implementation writes _seenAt before Publish builds the rows, which
        // would show no markers at all on the first open. MarkOpened is called synchronously
        // right after construction, exactly as MainWindowViewModel.Selected's setter does it, so
        // the constructor's own initial Load is still in flight when the call lands.
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        var rows = new[] { ActivityViewModelTestFactory.Row(1, timestamp: ActivityViewModelTestFactory.Noon) };

        var page = new ActivityViewModel(
            (_, _, _) => ActivityViewModelTestFactory.Page(rows),
            () => 90,
            _ => 0,
            () => store.Current,
            post: action => action(),
            delay: (_, _) => Task.CompletedTask,
            mutateGeneral: store.Mutate);

        page.MarkOpened();

        await ActivityViewModelTestFactory.SettleAsync(page);
        if (page.PendingOpen is { } pending)
        {
            await pending;
        }

        Assert.True(page.Rows.Single().IsUnseen);
        Assert.NotNull(store.Current.ActivitySeenAt);

        page.Dispose();
    }

    [Fact]
    public async Task ARowArrivingWhileOpen_KeepsItsMarkerUntilTheNextOpen()
    {
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        var rows = new List<GalactiLog.Data.Queries.ActivityRow>
        {
            ActivityViewModelTestFactory.Row(1, timestamp: ActivityViewModelTestFactory.Noon.AddMinutes(-10)),
        };

        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, _, _) => ActivityViewModelTestFactory.Page(rows),
            general: store.Current,
            mutateGeneral: store.Mutate);

        await OpenAsync(page); // T2, a real DateTimeOffset.UtcNow capture

        // A row arrives while the feed is already open (a scan finishes, a refresh runs): built
        // with the cutoff still in force (T2), so it is correctly marked unseen. Its timestamp is
        // real UtcNow, not the fixed Noon (2025) fixture constant every other row here uses, so it
        // is genuinely newer than T2 regardless of when this suite runs.
        rows.Add(ActivityViewModelTestFactory.Row(2, timestamp: DateTime.UtcNow.AddMinutes(5)));
        page.RefreshCommand.Execute(null);
        ActivityViewModelTestFactory.Settle(page);

        var arrivedRow = page.Rows.Single(r => r.Id == 2);
        Assert.True(arrivedRow.IsUnseen);

        // Second open (T4): the row already on screen keeps its marker, per spec 12.6's "a row
        // that arrives while the feed is already open keeps its marker until the next open" -
        // the marker was baked in at build time and this open does not rebuild it.
        await OpenAsync(page);
        Assert.True(page.Rows.Single(r => r.Id == 2).IsUnseen);
    }

    [Fact]
    public async Task AThirdOpenWithNothingNew_MarksNothing()
    {
        // Spec 12.6's row 5, literally: three opens, nothing written since the second. The row's
        // fixed Noon (2025) timestamp is older than any real DateTimeOffset.UtcNow this suite
        // captures, so it becomes seen on the SECOND open (re-rendered in place against the
        // first open's marker, P2-2 review) and the third open, with nothing new, marks nothing:
        // it is already seen and stays seen.
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        var rows = new[] { ActivityViewModelTestFactory.Row(1, timestamp: ActivityViewModelTestFactory.Noon) };

        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, _, _) => ActivityViewModelTestFactory.Page(rows),
            general: store.Current,
            mutateGeneral: store.Mutate);

        await OpenAsync(page); // first open: renders against null, commits T2
        await OpenAsync(page); // second open: renders against T2; the row is older than T2, so
                                // it becomes seen here, not on the third open
        Assert.False(page.Rows.Single().IsUnseen);

        var afterSecondOpen = store.Current.ActivitySeenAt;
        await OpenAsync(page); // third open, nothing new since
        var afterThirdOpen = store.Current.ActivitySeenAt;

        Assert.True(afterThirdOpen > afterSecondOpen);
        // Nothing was newly marked by the third open: the row was already seen, and stays seen.
        Assert.False(page.Rows.Single().IsUnseen);
    }

    [Fact]
    public async Task AReopenWithOneNewRow_MarksExactlyThatRow_WhileTheLoadedRowSetIsUnchanged()
    {
        // P2-2 review: no reload on open, so an existing row's own view-model instance and the
        // loaded count survive a reopen untouched; only IsUnseen on each row is corrected.
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        var oldRow = ActivityViewModelTestFactory.Row(1, timestamp: ActivityViewModelTestFactory.Noon);
        var firstPageCursor = new GalactiLog.Data.Queries.ActivityCursor(ActivityViewModelTestFactory.Noon, 1);

        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, before, _) => before is null
                ? new GalactiLog.Data.Queries.ActivityPage(
                    [oldRow],
                    new Dictionary<int, IReadOnlyList<GalactiLog.Data.Queries.ActivityRow>>(),
                    firstPageCursor,
                    2)
                : ActivityViewModelTestFactory.Page(
                    [ActivityViewModelTestFactory.Row(2, timestamp: DateTime.UtcNow.AddMinutes(5))]),
            general: store.Current,
            mutateGeneral: store.Mutate);

        await OpenAsync(page); // first open: renders the old row against null, commits Ta
        var oldRowInstance = page.Rows.Single(r => r.Id == 1);

        // "Load older" appends without clearing (Publish's fromStart: false path): the loaded set
        // grows by exactly one row rather than being rebuilt, unlike a Refresh.
        page.LoadOlderCommand.Execute(null);
        ActivityViewModelTestFactory.Settle(page);

        Assert.Equal(2, page.Rows.Count);
        Assert.Same(oldRowInstance, page.Rows.Single(r => r.Id == 1));

        var newRowInstance = page.Rows.Single(r => r.Id == 2);
        Assert.True(newRowInstance.IsUnseen); // built live against Ta; genuinely newer

        // Second open: re-renders in place against Ta. The old row is older than Ta and becomes
        // seen; the row that arrived while open is still newer than Ta and keeps its marker. The
        // loaded set itself (both instances, the count) is untouched: no reload happened.
        await OpenAsync(page);

        Assert.Equal(2, page.Rows.Count);
        Assert.Same(oldRowInstance, page.Rows.Single(r => r.Id == 1));
        Assert.Same(newRowInstance, page.Rows.Single(r => r.Id == 2));
        Assert.False(page.Rows.Single(r => r.Id == 1).IsUnseen);
        Assert.True(page.Rows.Single(r => r.Id == 2).IsUnseen);
    }

    [Fact]
    public void StrictlyNewer_IsStrict()
    {
        var seenAt = ActivityViewModelTestFactory.Noon;
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(
            ActivityViewModelTestFactory.Settings() with { ActivitySeenAt = seenAt });
        var rows = new[] { ActivityViewModelTestFactory.Row(1, timestamp: seenAt) };

        using var page = ActivityViewModelTestFactory.Create(
            page: (_, _, _) => ActivityViewModelTestFactory.Page(rows),
            general: store.Current,
            mutateGeneral: store.Mutate);

        // Equal to the marker, not strictly newer: seen, not unseen.
        Assert.False(page.Rows.Single().IsUnseen);
    }

    [Fact]
    public async Task TheMarker_FiltersNothing()
    {
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        var rows = new[]
        {
            ActivityViewModelTestFactory.Row(1, timestamp: ActivityViewModelTestFactory.Noon),
            ActivityViewModelTestFactory.Row(2, timestamp: ActivityViewModelTestFactory.Noon.AddMinutes(-5)),
        };

        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, _, _) => ActivityViewModelTestFactory.Page(rows),
            general: store.Current,
            mutateGeneral: store.Mutate);

        await OpenAsync(page);

        // Both rows are still loaded; the marker changed IsUnseen, never Rows.Count.
        Assert.Equal(2, page.Rows.Count);
    }

    [Fact]
    public async Task TheMarker_ChangesNoQuery()
    {
        GalactiLog.Data.Queries.ActivityFilters? captured = null;
        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (filters, cursor, limit) =>
            {
                captured = filters;
                return ActivityViewModelTestFactory.Page();
            });

        await OpenAsync(page);
        page.RefreshCommand.Execute(null);
        ActivityViewModelTestFactory.Settle(page);

        // ActivityFilters carries only severity, category and search: the marker has no field
        // here to change, by construction of the type itself.
        Assert.NotNull(captured);
        Assert.Null(captured!.Severity);
        Assert.Null(captured.Category);
    }

    [Fact]
    public async Task RetentionPruning_IgnoresTheMarker()
    {
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        var prunedRetention = -1;
        using var page = await ActivityViewModelTestFactory.CreateAsync(
            pruneNow: retention =>
            {
                prunedRetention = retention;
                return 0;
            },
            general: store.Current,
            mutateGeneral: store.Mutate);

        await OpenAsync(page);
        var seenAfterOpen = store.Current.ActivitySeenAt;

        await page.PruneNowCommand.ExecuteAsync(null);

        // ActivityRepository.PruneRetention (the pruneNow delegate) takes only the retention
        // window, no marker; and pruning does not itself move the marker.
        Assert.True(prunedRetention >= 0);
        Assert.Equal(seenAfterOpen, store.Current.ActivitySeenAt);
    }

    [Fact]
    public async Task TheWrite_HappensOnOpen_NotOnScroll()
    {
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, before, _) => before is null
                ? new GalactiLog.Data.Queries.ActivityPage(
                    [ActivityViewModelTestFactory.Row(1, timestamp: ActivityViewModelTestFactory.Noon)],
                    new Dictionary<int, IReadOnlyList<GalactiLog.Data.Queries.ActivityRow>>(),
                    new GalactiLog.Data.Queries.ActivityCursor(ActivityViewModelTestFactory.Noon, 1),
                    2)
                : ActivityViewModelTestFactory.Page(
                    [ActivityViewModelTestFactory.Row(2, timestamp: ActivityViewModelTestFactory.Noon.AddMinutes(-10))]),
            general: store.Current,
            mutateGeneral: store.Mutate);

        Assert.Null(store.Current.ActivitySeenAt);

        // "Load older" is the closest thing to a scroll action this page has; it must not move
        // the marker on its own.
        page.LoadOlderCommand.Execute(null);
        ActivityViewModelTestFactory.Settle(page);

        Assert.Null(store.Current.ActivitySeenAt);
    }

    [Fact]
    public async Task TheWrite_HappensOnce_NotPerRow()
    {
        var mutateCalls = 0;
        var store = new ActivityViewModelTestFactory.InMemoryGeneralStore(ActivityViewModelTestFactory.Settings());
        var rows = Enumerable.Range(1, 5)
            .Select(i => ActivityViewModelTestFactory.Row(i, timestamp: ActivityViewModelTestFactory.Noon.AddMinutes(-i)))
            .ToArray();

        using var page = await ActivityViewModelTestFactory.CreateAsync(
            page: (_, _, _) => ActivityViewModelTestFactory.Page(rows),
            mutateGeneral: mutate =>
            {
                mutateCalls++;
                return store.Mutate(mutate);
            });

        await OpenAsync(page);

        Assert.Equal(1, mutateCalls);
    }

    [Fact]
    public async Task AViewModelBuiltWithNoMutateDelegate_StillRenders()
    {
        // mutateGeneral is trailing and optional; null is the shape a unit test with no store has
        // (collision-map: no existing construction site moves).
        using var page = await ActivityViewModelTestFactory.CreateAsync(mutateGeneral: null);

        var exception = Record.Exception(page.MarkOpened);

        Assert.Null(exception);
        Assert.NotEmpty(page.Rows);
    }
}
