using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one place App.Tests builds an Activity page and the query rows behind it, so a later
/// constructor or read-model change is one edit rather than thirty. No database, no window and no
/// dispatcher: every delegate is a lambda, the post seam runs its closure inline and the debounce
/// completes immediately unless a test asks for a gate (design-spec 18.3).
/// </summary>
internal static class ActivityViewModelTestFactory
{
    /// <summary>The instant every seeded row hangs off, so a formatted time is a fixed string.
    /// </summary>
    public static readonly DateTime Noon = new(2025, 3, 4, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A zone with a whole-hour offset and no daylight saving, so the expected clock text
    /// is the same on every machine this suite runs on.</summary>
    public const string Zone = "UTC";

    public static GeneralSettings Settings(string timezone = Zone, bool use24Hour = true)
        => new() { Timezone = timezone, Use24HTime = use24Hour };

    public static ActivityRow Row(
        int id,
        string message = "Scan started (manual)",
        DateTime? timestamp = null,
        string severity = "info",
        string category = "scan",
        string eventType = "scan_started",
        string? details = null,
        Guid? targetId = null,
        int? durationMs = null,
        int? parentId = null)
        => new(
            id,
            timestamp ?? Noon,
            severity,
            category,
            eventType,
            message,
            details,
            targetId,
            durationMs,
            parentId);

    public static ActivityPage Page(
        IReadOnlyList<ActivityRow>? rows = null,
        IReadOnlyDictionary<int, IReadOnlyList<ActivityRow>>? children = null,
        ActivityCursor? next = null,
        int? total = null)
    {
        var list = rows ?? [Row(1)];
        return new ActivityPage(
            list,
            children ?? new Dictionary<int, IReadOnlyList<ActivityRow>>(),
            next,
            total ?? list.Count);
    }

    public static ActivityPage Empty() => Page([], total: 0);

    /// <summary>A scan row with the sub-events the feed collapses under it.</summary>
    public static ActivityPage WithChildren(params ActivityRow[] children)
    {
        var parent = Row(1);
        return Page(
            [parent],
            new Dictionary<int, IReadOnlyList<ActivityRow>> { [parent.Id] = children });
    }

    /// <summary>The blocking form, for plain xunit facts, which run on a pool thread of their own.
    /// </summary>
    public static ActivityViewModel Create(
        Func<ActivityFilters, ActivityCursor?, int, ActivityPage>? page = null,
        Func<int>? retentionDays = null,
        Func<int, int>? pruneNow = null,
        GeneralSettings? general = null,
        ScanStatusService? scanStatus = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings>? mutateGeneral = null,
        Func<DateTime?, int>? countUnseen = null)
    {
        var view = Build(page, retentionDays, pruneNow, general, scanStatus, delay, mutateGeneral, countUnseen);
        Settle(view);
        return view;
    }

    /// <summary>
    /// The awaiting form. Review finding 12: an <c>AvaloniaFact</c> runs on the headless UI thread,
    /// and that thread is a pool thread, so a blocking <c>Task.Wait</c> at construction can inline
    /// the load onto it under pool saturation. Every AvaloniaFact suite builds its page through
    /// this; the blocking form above stays for the plain facts.
    /// </summary>
    public static async Task<ActivityViewModel> CreateAsync(
        Func<ActivityFilters, ActivityCursor?, int, ActivityPage>? page = null,
        Func<int>? retentionDays = null,
        Func<int, int>? pruneNow = null,
        GeneralSettings? general = null,
        ScanStatusService? scanStatus = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings>? mutateGeneral = null,
        Func<DateTime?, int>? countUnseen = null)
    {
        var view = Build(page, retentionDays, pruneNow, general, scanStatus, delay, mutateGeneral, countUnseen);
        await SettleAsync(view).ConfigureAwait(true);
        return view;
    }

    private static ActivityViewModel Build(
        Func<ActivityFilters, ActivityCursor?, int, ActivityPage>? page,
        Func<int>? retentionDays,
        Func<int, int>? pruneNow,
        GeneralSettings? general,
        ScanStatusService? scanStatus,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings>? mutateGeneral = null,
        Func<DateTime?, int>? countUnseen = null)
    {
        var settings = general ?? Settings();
        return new ActivityViewModel(
            page ?? ((_, _, _) => Page()),
            retentionDays ?? (() => 90),
            pruneNow ?? (_ => 0),
            () => settings,
            scanStatus,
            post: action => action(),
            delay: delay ?? ((_, _) => Task.CompletedTask),
            mutateGeneral: mutateGeneral,
            countUnseen: countUnseen);
    }

    /// <summary>
    /// Task 7's marker tests (PAR-017, spec 12.6). An in-memory <c>general</c> document a test
    /// mutates and reads back, rather than a real <c>SettingsStore</c> over a temp database: this
    /// factory's own doc says "no database", and <c>MutateGeneral</c>'s contract (read, apply,
    /// return the written value) is all <c>ActivityViewModel.MarkOpened</c> needs from it.
    /// </summary>
    internal sealed class InMemoryGeneralStore(GeneralSettings initial)
    {
        public GeneralSettings Current { get; private set; } = initial;

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate)
        {
            Current = mutate(Current);
            return Current;
        }
    }

    /// <summary>
    /// Joins whatever the page has in flight. Two rounds, because a prune and a debounce each
    /// start a load from inside their own completion. In a harness helper rather than in a test
    /// method deliberately: a test that asserts the off-UI-thread rule awaits
    /// <see cref="SettleAsync"/> instead (TRACKING section 2 item 8).
    /// </summary>
    public static void Settle(ActivityViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            page.Quiesce(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>The awaiting form, for the tests that assert work never runs on the UI thread.
    /// </summary>
    public static async Task SettleAsync(ActivityViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            foreach (var pending in new[] { page.PendingLoad, page.PendingSearch, page.PendingPrune })
            {
                if (pending is { } task)
                {
                    await task.ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// A debounce window a test opens and closes by hand, so the "debounced before it reloads"
    /// assertion is a fact about ordering rather than a race against a wall clock.
    /// </summary>
    internal sealed class ManualDelay
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        public TimeSpan LastWindow { get; private set; }

        public Task Wait(TimeSpan window, CancellationToken cancellationToken)
        {
            Calls++;
            LastWindow = window;
            return _gate.Task.WaitAsync(cancellationToken);
        }

        public void Elapse()
        {
            var gate = _gate;
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.TrySetResult();
        }
    }
}
