using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 7 Task 6. Spec 12.7's rename history, read from the target_renamed activity events
// (questions.md Q11). Every collaborator is a lambda, so nothing here touches a database.
public class RenameHistoryViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly Guid TargetId = Guid.Parse("20000000-0000-0000-0000-000000000000");

    private static readonly DateTime RenamedAt = new(2025, 3, 4, 21, 6, 7, DateTimeKind.Utc);

    private static RenameHistoryRow Row(
        string previousName = "M 31",
        string newName = "Andromeda",
        string? currentName = "Andromeda",
        DateTime? timestamp = null)
        => new(timestamp ?? RenamedAt, TargetId, previousName, newName, currentName);

    private sealed class Harness : IDisposable
    {
        public RecordingLogger Logger { get; } = new();

        public IReadOnlyList<RenameHistoryRow> History { get; set; } = [];

        public int Loads;

        /// <summary>The thread every load ran on, when <see cref="Create"/> was given an
        /// <c>onUiThread</c> reader.</summary>
        public List<bool> LoadOnUiThread { get; } = [];

        public RenameHistoryViewModel ViewModel { get; internal set; } = null!;

        /// <summary>Joins the in-flight load. The blocking wait lives here rather than in a test
        /// method, which is what xunit's own analyzer asks for.</summary>
        public Harness Settle()
        {
            ViewModel.PendingLoad?.Wait(Budget);
            return this;
        }

        public void Dispose() => ViewModel.Dispose();
    }

    private static Harness Create(
        IReadOnlyList<RenameHistoryRow>? history = null,
        Func<bool>? onUiThread = null)
    {
        var harness = new Harness { History = history ?? [] };

        harness.ViewModel = new RenameHistoryViewModel(
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

                return harness.History;
            },
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            post: action => action(),
            logger: harness.Logger);

        return harness;
    }

    [AvaloniaFact]
    public async Task Construction_LoadsTheHistoryOffTheUiThread()
    {
        // RenameHistoryQuery is a synchronous SQLite read; it must never run on the dispatcher.
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
    public void Rows_RenderThePreviousAndNewNamesAndTheTime()
    {
        using var harness = Create([Row(currentName: "Andromeda Galaxy")]).Settle();

        var row = Assert.Single(harness.ViewModel.Rows);
        Assert.Equal("M 31", row.PreviousName);
        Assert.Equal("Andromeda", row.NewName);

        // The instant renders through the one general.timezone / general.use_24h_time path the
        // session cards and the merge history already use.
        Assert.Contains("21:06", row.RenamedAtText);

        // The target has been renamed again since, so the row says where that name went.
        Assert.True(row.HasCurrentName);
        Assert.Equal("now Andromeda Galaxy", row.CurrentNameText);
    }

    [Fact]
    public void Rows_CurrentNameIsHiddenWhenItHasNotChanged()
    {
        using var harness = Create([Row()]).Settle();

        Assert.False(Assert.Single(harness.ViewModel.Rows).HasCurrentName);
    }

    [Fact]
    public async Task Reload_RepublishesTheList()
    {
        using var harness = Create([Row()]).Settle();
        harness.History = [Row(newName: "first"), Row(newName: "second")];

        await harness.ViewModel.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(2, harness.Loads);
        Assert.Equal(["first", "second"], harness.ViewModel.Rows.Select(row => row.NewName));
    }
}
