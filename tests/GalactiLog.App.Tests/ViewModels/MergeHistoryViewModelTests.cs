using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 7 Task 5. Spec 12.9's merge history and its undo, plus the roadmap Verify line: undo
// removes the history row and restores the loser as an active target. The last of those runs
// against a real migrated database through the real MergeRepository, because "restores the loser
// as an active target" is a claim about merged_into_id and nothing in-memory can hold it.
public class MergeHistoryViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly Guid WinnerId = Guid.Parse("20000000-0000-0000-0000-000000000000");

    private static readonly Guid LoserId = Guid.Parse("30000000-0000-0000-0000-000000000000");

    private static readonly DateTime MergedAt = new(2025, 3, 4, 21, 6, 7, DateTimeKind.Utc);

    private static MergeHistoryRow Row(
        Guid? manifestId = null,
        Guid? loserId = null,
        string? loserName = "NGC 224",
        int movedFrameCount = 148,
        DateTime? mergedAt = null) => new(
        manifestId ?? Guid.NewGuid(),
        WinnerId,
        "M 31",
        loserId ?? LoserId,
        loserName,
        mergedAt ?? MergedAt,
        movedFrameCount);

    /// <summary>A row of the unresolved-name shape: no loser target, so the undo names the
    /// manifest instead.</summary>
    private static MergeHistoryRow UnresolvedRow(Guid? manifestId = null, string? sourceName = "Andromeda field")
        => Row(manifestId, loserId: null, loserName: sourceName) with { LoserId = null };

    private sealed class Harness : IDisposable
    {
        public RecordingLogger Logger { get; } = new();

        public IReadOnlyList<MergeHistoryRow> History { get; set; } = [];

        public Exception? LoadThrows { get; set; }

        /// <summary>Records the thread every load ran on, when <see cref="Create"/> was given an
        /// <c>onUiThread</c> reader.</summary>
        public List<bool> LoadOnUiThread { get; } = [];

        public int Loads;

        public List<Guid> Unmerged { get; } = [];

        public List<Guid> UndidUnresolved { get; } = [];

        public UnmergeResult Outcome { get; set; } = new(UnmergeStatus.Unmerged, 148, 2, ["NGC 224"]);

        /// <summary>Parks the unmerge delegates until a test releases them, so a second command
        /// execution can arrive while the first write is still in flight.</summary>
        public ManualResetEventSlim? UndoRelease { get; set; }

        /// <summary>Set when an unmerge delegate is entered, so a test knows the first undo is
        /// parked rather than merely queued.</summary>
        public ManualResetEventSlim UndoEntered { get; } = new();

        public int UndoneRaised;

        public MergeHistoryViewModel ViewModel { get; internal set; } = null!;

        /// <summary>Joins the in-flight load. The blocking wait lives here rather than in a test
        /// method, which is what xunit's own analyzer asks for.</summary>
        public Harness Settle()
        {
            try
            {
                ViewModel.PendingLoad?.Wait(Budget);
            }
            catch (AggregateException)
            {
                // A load cancelled by Dispose counts as settled.
            }

            return this;
        }

        public void Dispose() => ViewModel.Dispose();
    }

    private static Harness Create(
        IReadOnlyList<MergeHistoryRow>? history = null,
        Func<bool>? onUiThread = null,
        ManualResetEventSlim? loadRelease = null,
        Action<Action>? post = null)
    {
        var harness = new Harness { History = history ?? [] };

        harness.ViewModel = new MergeHistoryViewModel(
            () =>
            {
                Interlocked.Increment(ref harness.Loads);
                lock (harness.LoadOnUiThread)
                {
                    if (onUiThread is not null)
                    {
                        harness.LoadOnUiThread.Add(onUiThread());
                    }
                }

                loadRelease?.Wait(Budget);
                return harness.LoadThrows is null ? harness.History : throw harness.LoadThrows;
            },
            loserId =>
            {
                lock (harness.Unmerged)
                {
                    harness.Unmerged.Add(loserId);
                }

                harness.UndoEntered.Set();
                harness.UndoRelease?.Wait(Budget);
                return harness.Outcome;
            },
            manifestId =>
            {
                lock (harness.UndidUnresolved)
                {
                    harness.UndidUnresolved.Add(manifestId);
                }

                harness.UndoEntered.Set();
                harness.UndoRelease?.Wait(Budget);
                return harness.Outcome;
            },
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: post ?? (action => action()),
            logger: harness.Logger);

        harness.ViewModel.Undone += (_, _) => Interlocked.Increment(ref harness.UndoneRaised);
        return harness;
    }

    /// <summary>Joins a hand-built list's in-flight load. A helper, not a test method, which is
    /// what xunit's own analyzer asks of a blocking wait (TRACKING section 2 item 8: nothing here
    /// asserts what thread the work ran on, so the wait cannot hide the answer).</summary>
    private static MergeHistoryViewModel Settle(MergeHistoryViewModel history)
    {
        history.PendingLoad?.Wait(Budget);
        return history;
    }

    // ---- loading -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Construction_LoadsTheHistoryOffTheUiThread()
    {
        // MergeHistoryQuery is a synchronous SQLite read; it must never run on the dispatcher.
        // The load is awaited rather than blocked on (TRACKING section 2 item 8).
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        using var harness = Create([Row()], onUiThread: () => Dispatcher.UIThread.CheckAccess());
        await harness.ViewModel.PendingLoad!;

        Assert.Equal(1, harness.Loads);
        Assert.NotEmpty(harness.LoadOnUiThread);
        Assert.All(harness.LoadOnUiThread, Assert.False);
        Assert.Single(harness.ViewModel.Rows);
    }

    [Fact]
    public void EmptyHistory_ShowsTheEmptyState()
    {
        using var harness = Create().Settle();

        Assert.Empty(harness.ViewModel.Rows);
        Assert.True(harness.ViewModel.ShowEmptyState);
    }

    [Fact]
    public void NonEmptyHistory_HidesTheEmptyState()
    {
        using var harness = Create([Row()]).Settle();

        Assert.False(harness.ViewModel.ShowEmptyState);
    }

    [Fact]
    public void EmptyStateIsHiddenWhileTheFirstLoadIsInFlight()
    {
        using var release = new ManualResetEventSlim();
        using var harness = Create(loadRelease: release);

        // The first load has not published, so the empty state must not flash before the rows.
        Assert.False(harness.ViewModel.ShowEmptyState);

        release.Set();
        harness.Settle();
        Assert.True(harness.ViewModel.ShowEmptyState);
    }

    [Fact]
    public void LoadThatThrows_ShowsTheFailedState_AndClearsItOnTheNextLoad()
    {
        using var harness = Create([Row()]);
        harness.LoadThrows = new InvalidOperationException("the database is locked");
        harness.ViewModel.Reload();
        harness.Settle();

        Assert.True(harness.ViewModel.LoadFailed);
        Assert.False(harness.ViewModel.ShowEmptyState);

        harness.LoadThrows = null;
        harness.ViewModel.Reload();
        harness.Settle();

        Assert.False(harness.ViewModel.LoadFailed);
        Assert.Single(harness.ViewModel.Rows);
    }

    // ---- undo --------------------------------------------------------------------------

    [Fact]
    public async Task Undo_CallsUnmergeForATargetLoser()
    {
        using var harness = Create([Row()]).Settle();

        await harness.ViewModel.UndoCommand.ExecuteAsync(harness.ViewModel.Rows[0]);

        Assert.Equal(LoserId, Assert.Single(harness.Unmerged));
        Assert.Empty(harness.UndidUnresolved);
    }

    [Fact]
    public async Task Undo_CallsUndoUnresolvedForANullLoserRow()
    {
        var manifestId = Guid.NewGuid();
        using var harness = Create([UnresolvedRow(manifestId)]).Settle();

        await harness.ViewModel.UndoCommand.ExecuteAsync(harness.ViewModel.Rows[0]);

        Assert.Equal(manifestId, Assert.Single(harness.UndidUnresolved));
        Assert.Empty(harness.Unmerged);
    }

    /// <summary>Roadmap Verify, first half.</summary>
    [Fact]
    public async Task Undo_RemovesTheHistoryRow()
    {
        using var harness = Create([Row(), Row(loserId: Guid.NewGuid(), loserName: "Andromeda")]).Settle();
        var row = harness.ViewModel.Rows[0];

        await harness.ViewModel.UndoCommand.ExecuteAsync(row);

        Assert.DoesNotContain(row, harness.ViewModel.Rows);
        Assert.Single(harness.ViewModel.Rows);
        Assert.Contains("restored", harness.ViewModel.LastOutcome);
        Assert.Contains("148 frames", harness.ViewModel.LastOutcome);
        Assert.Contains("2 notes", harness.ViewModel.LastOutcome);
    }

    /// <summary>
    /// Roadmap Verify, second half, over a real migrated database through the real
    /// <see cref="MergeRepository"/> and the real <see cref="MergeHistoryQuery"/>: the loser's
    /// <c>merged_into_id</c> is null afterwards, its frames are back, and the manifest is gone.
    /// </summary>
    [Fact]
    public async Task Undo_RestoresTheLoserAsAnActiveTarget()
    {
        using var database = new HistoryDatabase();
        var connectionString = new DatabaseConnectionString(database.ConnectionString);
        var repository = new MergeRepository(connectionString);
        var query = new MergeHistoryQuery(connectionString);

        Assert.Equal(MergeStatus.Merged, repository.Merge(database.WinnerId, database.LoserId).Status);
        Assert.NotNull(database.MergedIntoIdOf(database.LoserId));

        var undone = 0;
        using var history = new MergeHistoryViewModel(
            query.All,
            repository.Unmerge,
            repository.UndoUnresolvedNameMerge,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: action => action());
        history.Undone += (_, _) => undone++;
        Settle(history);

        var row = Assert.Single(history.Rows);
        Assert.Equal(database.LoserId, row.LoserId);
        Assert.Equal("2 frames", row.FrameCountText);

        // Awaited, not fired and forgotten: the unmerge runs on the pool, and the assertions
        // below are about what it wrote.
        await history.UndoCommand.ExecuteAsync(row);

        // The row is gone, the manifest is consumed, and the loser is an active target again.
        Assert.Empty(history.Rows);
        Assert.True(history.ShowEmptyState);
        Assert.Equal(1, undone);
        Assert.Empty(query.All());
        Assert.Null(database.MergedIntoIdOf(database.LoserId));
        Assert.Equal(2, database.FrameCountOf(database.LoserId));
        Assert.Equal(0, database.FrameCountOf(database.WinnerId));
    }

    [Fact]
    public async Task Undo_RaisesUndone()
    {
        using var harness = Create([Row()]).Settle();

        await harness.ViewModel.UndoCommand.ExecuteAsync(harness.ViewModel.Rows[0]);

        Assert.Equal(1, harness.UndoneRaised);
    }

    [Fact]
    public async Task Undo_ThatFails_KeepsTheRowAndReportsTheOutcome()
    {
        using var harness = Create([Row()]).Settle();
        harness.Outcome = new UnmergeResult(UnmergeStatus.NoManifest, 0, 0, []);
        var row = harness.ViewModel.Rows[0];

        await harness.ViewModel.UndoCommand.ExecuteAsync(row);

        // The manifest is still there, so the row that names it is still true.
        Assert.Contains(row, harness.ViewModel.Rows);
        Assert.Equal("No merge record remains for that merge, so nothing was restored.", harness.ViewModel.LastOutcome);
        Assert.Equal(0, harness.UndoneRaised);
    }

    [Fact]
    public async Task Undo_WhenTheWinnerHasItselfBeenMergedAway_NamesTheMergeToUndoFirst()
    {
        // F17: chained merges undo in reverse order only, and the row stays, because this undo
        // becomes available again once the later merge is undone.
        using var harness = Create([Row()]).Settle();
        harness.Outcome = new UnmergeResult(UnmergeStatus.WinnerMergedAway, 0, 0, [], "NGC 7331");
        var row = harness.ViewModel.Rows[0];

        await harness.ViewModel.UndoCommand.ExecuteAsync(row);

        Assert.Contains(row, harness.ViewModel.Rows);
        Assert.Equal("Undo the merge of \"M 31\" into \"NGC 7331\" first.", harness.ViewModel.LastOutcome);
        Assert.Equal(0, harness.UndoneRaised);
    }

    [Fact]
    public async Task Undo_AlreadyUndoneElsewhere_RemovesTheRowAndSaysSo()
    {
        using var harness = Create([Row()]).Settle();
        harness.Outcome = new UnmergeResult(UnmergeStatus.NotMerged, 0, 0, []);
        var row = harness.ViewModel.Rows[0];

        await harness.ViewModel.UndoCommand.ExecuteAsync(row);

        Assert.DoesNotContain(row, harness.ViewModel.Rows);
        Assert.Equal("Already undone.", harness.ViewModel.LastOutcome);

        // Not an undo this list performed, so nothing beside it needs reloading.
        Assert.Equal(0, harness.UndoneRaised);
    }

    /// <summary>
    /// Review finding 1. <c>RelayCommand.Execute</c> runs regardless of <c>CanExecute</c>, so
    /// neither the toolkit's concurrency flag nor the button's own disabling is the guard: a
    /// second execution arriving before the first write completes would unmerge twice, and the
    /// second result (<c>NotMerged</c>) would overwrite the first one's success sentence.
    /// </summary>
    [Fact]
    public async Task Undo_ExecutedTwice_UnmergesOnce()
    {
        using var release = new ManualResetEventSlim();
        using var harness = Create([Row()]).Settle();
        harness.UndoRelease = release;
        var row = harness.ViewModel.Rows[0];

        // Execute, not ExecuteAsync: this is the double-click path, which ignores CanExecute.
        // The first Execute's task is captured immediately, before the second Execute call: the
        // second invocation trips the view model's _undoing guard and returns synchronously
        // (already completed), which overwrites ExecutionTask on the command. Awaiting the
        // command's ExecutionTask after both calls would therefore await that already-completed
        // no-op instead of the real write, racing the assertions below against ApplyUndo's
        // continuation on its own thread-pool thread.
        harness.ViewModel.UndoCommand.Execute(row);
        var firstExecution = harness.ViewModel.UndoCommand.ExecutionTask!;
        Assert.True(harness.UndoEntered.Wait(TimeSpan.FromSeconds(30)));
        harness.ViewModel.UndoCommand.Execute(row);

        release.Set();
        await firstExecution;

        Assert.Equal(LoserId, Assert.Single(harness.Unmerged));
        Assert.Empty(harness.ViewModel.Rows);
        Assert.Equal(1, harness.UndoneRaised);
        Assert.Contains("restored", harness.ViewModel.LastOutcome);
    }

    [Fact]
    public async Task Undo_ThatThrows_ReportsTheOutcomeAndKeepsTheRow()
    {
        using var harness = Create([Row()]).Settle();
        var row = harness.ViewModel.Rows[0];

        using var thrower = new MergeHistoryViewModel(
            () => [Row()],
            _ => throw new InvalidOperationException("the database is locked"),
            _ => throw new InvalidOperationException("the database is locked"),
            new GeneralSettings(),
            post: action => action(),
            logger: harness.Logger);
        Settle(thrower);

        await thrower.UndoCommand.ExecuteAsync(thrower.Rows[0]);

        Assert.Single(thrower.Rows);
        Assert.Equal("The undo could not be completed. See the log for details.", thrower.LastOutcome);
    }

    // ---- row formatting ----------------------------------------------------------------

    [Fact]
    public void Rows_RenderTheLoserNameMergeTimeAndFrameCount()
    {
        using var harness = Create([Row()]).Settle();

        var row = Assert.Single(harness.ViewModel.Rows);
        Assert.Equal("NGC 224", row.LoserName);
        Assert.Equal("M 31", row.WinnerName);

        // UTC, 24 hour, from the same general.timezone / general.use_24h_time path the session
        // cards use for an instant.
        Assert.Equal("2025-03-04 21:06", row.MergedAtText);
        Assert.Equal("148 frames", row.FrameCountText);
    }

    [Fact]
    public void Rows_RenderTheMergeTimeInTheConfiguredZoneAndClock()
    {
        using var history = new MergeHistoryViewModel(
            () => [Row()],
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 0, 0, []),
            _ => new UnmergeResult(UnmergeStatus.Unmerged, 0, 0, []),
            new GeneralSettings { Timezone = "UTC", Use24HTime = false },
            post: action => action());
        Settle(history);

        Assert.Equal("2025-03-04 9:06 PM", Assert.Single(history.Rows).MergedAtText);
    }

    [Fact]
    public void Rows_NullLoserName_ReadsAsAnUnresolvedName()
    {
        using var harness = Create([UnresolvedRow(sourceName: null)]).Settle();

        Assert.Equal("an unresolved name", Assert.Single(harness.ViewModel.Rows).LoserName);
    }

    [Fact]
    public void Rows_FrameCount_SingularPluralAndUnknown()
    {
        using var harness = Create(
        [
            Row(movedFrameCount: 1),
            Row(loserId: Guid.NewGuid(), movedFrameCount: 0),
            Row(loserId: Guid.NewGuid(), movedFrameCount: -1),
        ]).Settle();

        Assert.Equal("1 frame", harness.ViewModel.Rows[0].FrameCountText);
        Assert.Equal("0 frames", harness.ViewModel.Rows[1].FrameCountText);
        Assert.Equal("unknown frames", harness.ViewModel.Rows[2].FrameCountText);
    }

    // ---- lifetime ----------------------------------------------------------------------

    [Fact]
    public void Dispose_CancelsAnInFlightLoad()
    {
        using var release = new ManualResetEventSlim();
        var harness = Create([Row()], loadRelease: release);

        harness.ViewModel.Dispose();
        release.Set();
        harness.Settle();

        // The load that was parked when Dispose ran must not have published into a disposed list.
        Assert.Empty(harness.ViewModel.Rows);
        Assert.False(harness.ViewModel.ShowEmptyState);
    }

    /// <summary>
    /// A migrated temp database holding a winner and a loser with two frames. Owned by the test
    /// that built it. Test-only plain file I/O, which <c>FileSafetyTest</c> does not scan.
    /// </summary>
    private sealed class HistoryDatabase : IDisposable
    {
        // F8: one App.Tests temp-database type (path, Pooling=False, Migrate, sidecar delete);
        // only the rows below are this fixture's own.
        private readonly TempDatabase _database = new("galactilog-merge-history");

        public HistoryDatabase() => _database.Seed(context =>
        {
            context.Targets.Add(new Target
            {
                Id = WinnerId,
                PrimaryName = "M 31",
                Aliases = JsonSerializer.Serialize(Array.Empty<string>()),
            });
            context.Targets.Add(new Target
            {
                Id = LoserId,
                PrimaryName = "NGC 224",
                Aliases = JsonSerializer.Serialize(Array.Empty<string>()),
            });

            foreach (var day in new[] { 3, 5 })
            {
                var id = Guid.NewGuid();
                context.Images.Add(new Image
                {
                    Id = id,
                    FilePath = $@"C:\GalactiLogFixture\history\{id:N}.fits",
                    FileName = $"{id:N}.fits",
                    SessionDate = new DateOnly(2025, 1, day),
                    CaptureDate = new DateTime(2025, 1, day, 22, 0, 0, DateTimeKind.Utc),
                    ResolvedTargetId = LoserId,
                    ImageType = "LIGHT",
                    ExposureTime = 300d,
                });
            }
        });

        public string ConnectionString => _database.ConnectionString;

        public Guid WinnerId => MergeHistoryViewModelTests.WinnerId;

        public Guid LoserId => MergeHistoryViewModelTests.LoserId;

        public Guid? MergedIntoIdOf(Guid targetId)
            => _database.Read(context => context.Targets.Single(target => target.Id == targetId).MergedIntoId);

        public int FrameCountOf(Guid targetId)
            => _database.Read(context => context.Images.Count(image => image.ResolvedTargetId == targetId));

        public void Dispose() => _database.Dispose();
    }
}
