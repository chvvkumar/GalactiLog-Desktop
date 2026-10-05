using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.Core.Diagnostics;
using GalactiLog.Core.Io;
using GalactiLog.Data;
using GalactiLog.Data.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Events;
using Xunit;
using DiagnosticsFactory = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory;
// Fixer list code item 5 and design-lessons rule 1: one RecordingPost, in TestSupport, shared by
// this file and UpdateServiceTests. The private copy this file carried was folded onto it at the
// phase close.
using RecordingPost = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory.RecordingPost;

namespace GalactiLog.App.Tests.ViewModels;

// Plain xunit facts, no window: design-spec 18.3's rule that every view-model is unit-testable on
// its own is what these assert. LogReader, SettingsStore and ShellIntegration are sealed with
// non-virtual members by design, so the fixture hands the page the real thing over a temp log
// directory, a temp database and recording delegates.
public class LogViewerViewModelTests
{
    private const string Stack = "   at GalactiLog.App.Program.Main()";

    private static string[] SixtyEntries() =>
    [
        .. Enumerable.Range(0, 60).Select(index => LogViewerFixture.Entry(
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture, "10:00:{0:00}.000", index),
            message: "m" + index)),
    ];

    [Fact]
    public async Task FirstLoad_RunsOffTheUiThread()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", LogViewerFixture.Entry("10:00:00.000"));

        var callerThread = Environment.CurrentManagedThreadId;
        var publishThread = 0;
        var kicked = false;
        using var page = fixture.Build(post: action =>
        {
            // The constructor's kick runs on the caller's thread; the publish runs on whatever
            // thread the read finished on, which is the one this case is about.
            if (kicked)
            {
                publishThread = Environment.CurrentManagedThreadId;
            }

            kicked = true;
            action();
        });

        // Awaited, never blocked on (TRACKING section 2 item 8).
        await LogViewerFixture.SettleAsync(page);

        Assert.NotEqual(0, publishThread);
        Assert.NotEqual(callerThread, publishThread);
        Assert.NotEmpty(page.Lines);
    }

    [Fact]
    public async Task Constructor_ReadsNoFile()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", LogViewerFixture.Entry("10:00:00.000"));

        // A post seam that holds every closure until the test drains it, so "the constructor read
        // nothing" is a fact about ordering rather than a race against the pool.
        var deferred = new DiagnosticsFactory.DeferredPost();
        using var page = fixture.Build(post: deferred.Post);

        Assert.Equal(0, page.LoadCount);
        Assert.Null(page.PendingLoad);
        Assert.Empty(page.Lines);
        Assert.Equal(1, deferred.Queued);

        deferred.Drain();
        await LogViewerFixture.SettleAsync(page);
        deferred.Drain();

        Assert.Equal(1, page.LoadCount);
        Assert.NotEmpty(page.Lines);
    }

    [Fact]
    public async Task MinimumLevel_Changed_ReloadsFromPageOne()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            LogViewerFixture.Entry("10:00:00.000", "DBG", message: "debug"),
            LogViewerFixture.Entry("10:00:01.000", "INF", message: "information"),
            LogViewerFixture.Entry("10:00:02.000", "ERR", message: "error"));

        using var page = await fixture.CreateAsync();
        Assert.Equal(3, page.Lines.Count);

        page.MinimumLevel = LogLineLevel.Error;
        await LogViewerFixture.SettleAsync(page);

        var only = Assert.Single(page.Lines);
        Assert.Equal("error", only.Message);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Search_IsDebounced_AndReloadsOnce()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            LogViewerFixture.Entry("10:00:00.000", message: "alpha one"),
            LogViewerFixture.Entry("10:00:01.000", message: "beta two"));

        using var page = await fixture.CreateAsync();
        var before = page.LoadCount;

        page.Search = "a";
        page.Search = "al";
        page.Search = "alpha";

        await fixture.Delay.WaitForRequestCountAsync(3);
        fixture.Delay.Release();
        await page.PendingSearch!;
        await LogViewerFixture.SettleAsync(page);

        // Three keystrokes, three windows, one reload: the first two windows were cancelled by the
        // keystroke after them.
        Assert.Equal(before + 1, page.LoadCount);
        var only = Assert.Single(page.Lines);
        Assert.Equal("alpha one", only.Message);
    }

    [Fact]
    public async Task LoadMore_AppendsTheNextPage_AndStopsWhenTheCursorIsNull()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", SixtyEntries());

        using var page = await fixture.CreateAsync();

        Assert.Equal(LogViewerViewModel.PageSize, page.Lines.Count);
        Assert.NotNull(page.NextCursor);
        Assert.True(page.CanLoadMore);

        await page.LoadMoreCommand.ExecuteAsync(null);
        await LogViewerFixture.SettleAsync(page);

        Assert.Equal(60, page.Lines.Count);
        Assert.Null(page.NextCursor);
        Assert.False(page.CanLoadMore);

        // Newest first, and the second page continued the first rather than restarting it.
        Assert.Equal("m59", page.Lines[0].Message);
        Assert.Equal("m0", page.Lines[^1].Message);
        Assert.Equal(60, page.Lines.Select(line => line.Message).Distinct().Count());
    }

    [Fact]
    public async Task LoadMore_PressedTwice_LoadsOncePage()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", SixtyEntries());

        var deferred = new DiagnosticsFactory.DeferredPost();
        using var page = fixture.Build(post: deferred.Post);

        deferred.Drain();
        await LogViewerFixture.SettleAsync(page);
        deferred.Drain();
        Assert.NotNull(page.NextCursor);

        var before = page.LoadCount;

        // The publish stays queued while the deferred post holds it, so the first execution is
        // genuinely still in flight when the second Execute arrives.
        page.LoadMoreCommand.Execute(null);
        var first = page.LoadMoreCommand.ExecutionTask;
        page.LoadMoreCommand.Execute(null);

        Assert.NotNull(first);
        await first;
        await LogViewerFixture.SettleAsync(page);
        deferred.Drain();

        Assert.Equal(before + 1, page.LoadCount);
        Assert.Equal(60, page.Lines.Count);
    }

    [Fact]
    public async Task LoadMore_ExecutedDirectly_IsGuardedInTheCommandBody()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", LogViewerFixture.Entry("10:00:00.000"));

        using var page = await fixture.CreateAsync();
        Assert.Null(page.NextCursor);
        var before = page.LoadCount;

        // Execute ignores CanExecute (TRACKING section 6 item 13). A run with no cursor would
        // re-fetch page one and append it to itself.
        page.LoadMoreCommand.Execute(null);
        await LogViewerFixture.SettleAsync(page);

        Assert.Equal(before, page.LoadCount);
        Assert.Single(page.Lines);
    }

    [Fact]
    public async Task CaptureLevel_Changed_WritesGeneralLogLevelThroughMutateGeneral()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync(initialCaptureLevel: LogLineLevel.Information);

        // Changed out of band after the page took its initial value. A SaveGeneral of a document
        // the page had read earlier would clobber this; MutateGeneral reads inside the write gate
        // and preserves it, which is what makes it the one door into the general document
        // (TRACKING section 6 item 14).
        fixture.Settings.MutateGeneral(general => general with { DefaultPageSize = 123 });

        page.CaptureLevel = LogLineLevel.Warning;

        var stored = fixture.Settings.GetGeneral();
        Assert.Equal("Warning", stored.LogLevel);
        Assert.Equal(123, stored.DefaultPageSize);
    }

    [Fact]
    public async Task CaptureLevel_Changed_ChangesWhatIsSubsequentlyCaptured()
    {
        using var host = new LogHostFixture();

        using (var page = await host.CreatePageAsync())
        {
            page.CaptureLevel = LogLineLevel.Warning;
        }

        Assert.Equal("Warning", host.Settings.GetGeneral().LogLevel);
        Assert.Equal(LogEventLevel.Warning, host.LevelSwitch.MinimumLevel);

        Serilog.Log.Information("information-after-the-change");
        Serilog.Log.Warning("warning-after-the-change");

        // The explicit flush, rather than waiting out the sink's one second flushToDiskInterval.
        Serilog.Log.CloseAndFlush();

        var lines = host.Reader.All(new LogFilters(), cap: 1000);

        Assert.Contains(lines, line => line.Message.Contains("warning-after-the-change", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Message.Contains("information-after-the-change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Viewer_ReadsAFileTheLiveSinkHoldsOpen()
    {
        using var host = new LogHostFixture();

        Serilog.Log.Warning("a line the live sink still holds open {Marker}", "marker-9317");

        // The host is deliberately NOT disposed and the logger deliberately NOT closed: this case
        // is the roadmap's "reads a file the sink currently holds open" clause, and it only means
        // anything while that handle is open.
        using var page = await host.CreatePageAsync();

        for (var attempt = 0; attempt < 20 && !Holds(page); attempt++)
        {
            await Task.Delay(100);
            await page.RefreshCommand.ExecuteAsync(null);
            await LogViewerFixture.SettleAsync(page);
        }

        Assert.True(Holds(page), "The viewer never read the line the live sink had written.");

        static bool Holds(LogViewerViewModel page)
            => page.Lines.Any(line => line.Message.Contains("marker-9317", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FollowTail_ReloadsOnEachTick_AndStopsWhenTurnedOff()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", LogViewerFixture.Entry("10:00:00.000"));

        using var page = await fixture.CreateAsync();
        var before = page.LoadCount;

        page.FollowTail = true;

        await fixture.Delay.WaitForRequestCountAsync(1);
        Assert.Equal(LogViewerViewModel.FollowInterval, fixture.Delay.Requested[0]);

        fixture.Delay.Release();
        await fixture.Delay.WaitForRequestCountAsync(2);

        Assert.True(page.LoadCount > before, "The follow-tail tick did not reload.");

        page.FollowTail = false;
        var requested = fixture.Delay.Requested.Count;
        fixture.Delay.Release();
        await Task.Delay(50);

        Assert.Equal(requested, fixture.Delay.Requested.Count);
    }

    [Fact]
    public async Task FollowTail_StoppedByDispose()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", LogViewerFixture.Entry("10:00:00.000"));

        var page = await fixture.CreateAsync();
        page.FollowTail = true;
        await fixture.Delay.WaitForRequestCountAsync(1);

        page.Dispose();
        var requested = fixture.Delay.Requested.Count;
        fixture.Delay.Release();
        await Task.Delay(50);

        Assert.Equal(requested, fixture.Delay.Requested.Count);
    }

    [Fact]
    public async Task CopySelection_CopiesTheRawEntry_IncludingItsException()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            LogViewerFixture.Entry("10:00:00.000", "ERR", message: "Ingest failed"),
            "System.IO.IOException: access denied",
            Stack);

        using var page = await fixture.CreateAsync();

        Assert.False(page.CopySelectionCommand.CanExecute(null));

        page.Selected = page.Lines[0];
        Assert.True(page.CopySelectionCommand.CanExecute(null));

        await page.CopySelectionCommand.ExecuteAsync(null);

        var copied = Assert.Single(fixture.Copied);
        Assert.Contains("Ingest failed", copied, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException: access denied", copied, StringComparison.Ordinal);
        Assert.Contains(Stack.Trim(), copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyAll_RespectsTheCap_AndRunsOffTheUiThread()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            [.. Enumerable.Range(0, 5).Select(index => LogViewerFixture.Entry(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, "10:00:{0:00}.000", index),
                message: "m" + index))]);

        // A post seam that records how each closure was handed over and runs them only when this
        // test drains it, so "the read ran off the caller" and "the clipboard write ran on the
        // posting thread" are both facts about ordering rather than races.
        //
        // The drain runs on a dedicated thread of this test's own, standing in for the dispatcher
        // thread. That is what makes the thread assertions below deterministic: a thread created
        // here has an id distinct from every thread already alive, and it is never a thread-pool
        // thread, so it can never coincide with the pool thread the read finished on. The earlier
        // shape drained on whatever pool thread resumed after an await, which is load dependent
        // and failed once under a full-suite run (TRACKING section 2 item 8 family).
        var post = new RecordingPost();
        using var page = fixture.Build(post: post.Post, copyAllCap: 2);
        post.Drain();
        await LogViewerFixture.SettleAsync(page);
        post.Drain();

        // Ruling Q26's production value, which the Core suite's All_RespectsTheCap proves the
        // reader honours. The page is built with a small cap here so the cap and its notice are
        // asserted without writing fifty thousand fixture entries.
        Assert.Equal(50_000, LogViewerViewModel.DefaultCopyAllCap);
        Assert.Equal(2, page.CopyAllCap);
        Assert.Contains("up to 2", page.CopyAllTooltip, StringComparison.Ordinal);

        var callerThread = Environment.CurrentManagedThreadId;
        var copy = page.CopyAllCommand.ExecuteAsync(null);

        // The read is inside Task.Run and the clipboard write is posted, so the command cannot
        // complete and nothing can reach the clipboard until the seam is drained. Checked before
        // any await, so it is the caller's own thread that is observed to have run neither.
        Assert.False(copy.IsCompleted);
        Assert.Empty(fixture.Copied);

        await post.WaitForQueuedAsync(1);
        Assert.False(copy.IsCompleted);
        Assert.Empty(fixture.Copied);

        // The seam was handed the clipboard write from a thread-pool thread, which is where
        // Task.Run put the read. Recorded as a property of the thread rather than as an id
        // comparison, because an id comparison against the caller is load dependent: a caller
        // parked on an await is back in the pool and can be reused by the read itself.
        Assert.True(post.PostedFromThreadPool[^1]);

        // Drained on a dedicated non-pool thread, so the clipboard write's thread is knowable
        // exactly. Not joined: awaiting the command is what waits for it, and a test never blocks.
        var drain = new Thread(post.Drain) { IsBackground = true, Name = "LogViewerCopyAllDrain" };
        drain.Start();
        await copy;

        // The write ran on the thread the seam dispatched to, and on no other: in production that
        // seam is UiPost.Default and that thread is the UI thread, which is where Avalonia's
        // clipboard has to be reached (review finding I1).
        var copyThread = Assert.Single(fixture.CopyThreads);
        Assert.Equal(drain.ManagedThreadId, post.DrainThread);
        Assert.Equal(post.DrainThread, copyThread);
        Assert.NotEqual(callerThread, copyThread);
        Assert.NotEqual(post.Threads[^1], copyThread);

        var copied = Assert.Single(fixture.Copied);
        var copiedLines = copied.Split(Environment.NewLine);

        Assert.Equal(3, copiedLines.Length);
        Assert.Contains("Truncated to the newest 2 entries.", copiedLines[0], StringComparison.Ordinal);
        Assert.Contains("m4", copiedLines[1], StringComparison.Ordinal);
        Assert.Contains("m3", copiedLines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("m2", copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyAll_WithNoCapReached_CarriesNoTruncationNotice()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            LogViewerFixture.Entry("10:00:00.000", message: "only"));

        using var page = await fixture.CreateAsync();
        await page.CopyAllCommand.ExecuteAsync(null);

        var copied = Assert.Single(fixture.Copied);
        Assert.DoesNotContain("Truncated", copied, StringComparison.Ordinal);
        Assert.Contains("only", copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenLogFolder_IsDisabled_WhenTheWindowsShellIsUnavailable()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();

        // The one guard, read from ShellIntegration rather than re-derived from
        // OperatingSystem.IsWindows() at this call site (design-lessons rule 2).
        Assert.Equal(ShellIntegration.IsWindowsShellAvailable, page.OpenLogFolderCommand.CanExecute(null));

        page.Dispose();
        Assert.False(page.OpenLogFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenLogFolder_RevealsTheNewestLogFile()
    {
        if (!ShellIntegration.IsWindowsShellAvailable)
        {
            return;
        }

        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260914.log", LogViewerFixture.Entry("09:00:00.000"));
        var newest = fixture.WriteLogFile(
            "galactilog-20260915.log", LogViewerFixture.Entry("10:00:00.000"));

        using var page = await fixture.CreateAsync();
        page.OpenLogFolderCommand.Execute(null);

        // Ruling Q28: the folder opens with the newest file selected, which is explorer.exe's
        // /select, verb. Nothing is launched: the fixture's start delegate records and returns.
        var launched = Assert.Single(fixture.Launched);
        Assert.Equal("explorer.exe", launched.FileName);
        Assert.Contains(newest, launched.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyState_NoFiles_SaysNoLogFilesYet()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();

        Assert.True(page.IsEmpty);
        Assert.Equal(LogViewerViewModel.NoFilesMessage, page.EmptyMessage);
    }

    [Fact]
    public async Task EmptyState_LevelFiltersEverythingOut_SaysNoEntriesAtThisLevel()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            LogViewerFixture.Entry("10:00:00.000", "DBG", message: "debug"));

        using var page = await fixture.CreateAsync();
        page.MinimumLevel = LogLineLevel.Error;
        await LogViewerFixture.SettleAsync(page);

        Assert.True(page.IsEmpty);
        Assert.Equal(LogViewerViewModel.NoEntriesAtLevelMessage, page.EmptyMessage);

        // Spec 12.10's sentence for the log viewer, verbatim (review finding I2).
        Assert.Equal("No log entries at this level.", page.EmptyMessage);
    }

    [Fact]
    public async Task EmptyState_SearchMatchesNothing_SaysNoEntriesMatchThisSearch()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            LogViewerFixture.Entry("10:00:00.000", message: "alpha"));

        using var page = await fixture.CreateAsync();

        page.Search = "no-such-text";
        await fixture.Delay.WaitForRequestCountAsync(1);
        fixture.Delay.Release();
        await page.PendingSearch!;
        await LogViewerFixture.SettleAsync(page);

        Assert.True(page.IsEmpty);
        Assert.Equal(LogViewerViewModel.NoSearchMatchMessage, page.EmptyMessage);
    }

    [Fact]
    public async Task Dispose_StopsTheFollowLoop_AndDetachesEverything()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", SixtyEntries());

        var page = await fixture.CreateAsync();
        page.FollowTail = true;
        await fixture.Delay.WaitForRequestCountAsync(1);

        var loads = page.LoadCount;
        page.Dispose();
        page.Dispose();

        // Every command is inert afterwards, and nothing it does not own was disposed: the
        // reader, the settings store and the shell are all still usable.
        page.RefreshCommand.Execute(null);
        page.LoadMoreCommand.Execute(null);
        page.MinimumLevel = LogLineLevel.Fatal;
        page.Search = "anything";
        page.FollowTail = true;

        Assert.Equal(loads, page.LoadCount);
        Assert.False(page.RefreshCommand.CanExecute(null));
        Assert.False(page.LoadMoreCommand.CanExecute(null));
        Assert.NotEmpty(fixture.Reader.All(new LogFilters(), cap: 5));
        Assert.Equal("Information", fixture.Settings.GetGeneral().LogLevel);
    }

    [Fact]
    public void LogLineLevel_NamesMatchSerilogLogEventLevelNames()
    {
        // GalactiLog.Core has no Serilog reference and must not gain one (ruling Q5), so this is
        // the one place the two vocabularies are pinned equal. AppHost.ParseLevel is
        // Enum.TryParse<LogEventLevel> over general.log_level, and LogViewerViewModel writes that
        // setting as LogLineLevel.ToString(): if these names ever diverge, the capture picker
        // would write a value AppHost silently falls back on.
        Assert.Equal(
            Enum.GetNames<LogEventLevel>().Order(StringComparer.Ordinal),
            Enum.GetNames<LogLineLevel>().Order(StringComparer.Ordinal));

        // And the ascending order, which is what makes the minimum-level filter one comparison.
        foreach (var name in Enum.GetNames<LogLineLevel>())
        {
            Assert.Equal(
                (int)Enum.Parse<LogEventLevel>(name),
                (int)Enum.Parse<LogLineLevel>(name));
        }
    }

    [Fact]
    public void LogReaderLimits_MatchActivityQuerysLimits()
    {
        // Phase review minor P5. Ruling Q4 made LogCursor a parallel record with the identical
        // rule set and a cross-referencing XML doc, and LogReader's three limit constants carry
        // the same cross-reference. A comment is not a constraint: GalactiLog.Core cannot
        // reference GalactiLog.Data, so this project, which references both, is the one place the
        // two sets can be pinned equal. The phase's own standard is the assertion (Task 3 wrote
        // the analogous one for DiagnosticsBundle.RecentEventCount against ActivityQuery.MaxLimit),
        // and this is the case that fails when one set is changed without the other.
        Assert.Equal(ActivityQuery.DefaultLimit, LogReader.DefaultLimit);
        Assert.Equal(ActivityQuery.MinLimit, LogReader.MinLimit);
        Assert.Equal(ActivityQuery.MaxLimit, LogReader.MaxLimit);
    }

    [Fact]
    public void SearchPattern_MatchesTheSinkFileNamePrefix()
    {
        // One definition of the stem, in Core, read by the App layer: DiagnosticsService is what
        // AppHost composes the Serilog sink path from, and it now reads LogFileSet's constant
        // rather than repeating the literal (review round 1, ruled escalation). The guard stays,
        // retargeted, so a future edit that reintroduces a second spelling fails here.
        Assert.Equal(LogFileSet.FileNamePrefix, DiagnosticsService.LogFileNamePrefix);
        Assert.StartsWith(
            LogFileSet.FileNamePrefix, LogFileSet.SearchPattern, StringComparison.Ordinal);
    }

    // A real host over a temp app data root, which is what makes the two roadmap Verify clauses
    // end-to-end rather than a unit test of a path this task did not write: general.log_level
    // reaches Serilog through AppHost's existing GeneralChanged handler, and the log file is the
    // one the live sink holds open.
    //
    // AppHost.Build sets the process-wide Serilog.Log.Logger and this fixture clears the
    // process-wide SQLite pool, exactly as AppHostTests' fixture does. AssemblyInfo.cs disables
    // cross-collection parallelization for the whole assembly, so nothing runs alongside it.
    private sealed class LogHostFixture : IDisposable
    {
        private readonly string _root;
        private readonly IHost _host;

        public LogHostFixture()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "GalactiLogLogViewerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            // A previous fixture's file sink would otherwise keep its galactilog-<date>.log open
            // with no reference left that Dispose can reach.
            Serilog.Log.CloseAndFlush();
            _host = AppHost.Build(_root, cliMode: true);

            Settings = _host.Services.GetRequiredService<SettingsStore>();
            LevelSwitch = _host.Services.GetRequiredService<Serilog.Core.LoggingLevelSwitch>();
            LogDirectory = _host.Services.GetRequiredService<AppWriter>()
                .ResolveAppDataPath(DiagnosticsService.LogDirectoryName);
            Reader = new LogReader(LogDirectory);
        }

        public SettingsStore Settings { get; }

        public Serilog.Core.LoggingLevelSwitch LevelSwitch { get; }

        public string LogDirectory { get; }

        public LogReader Reader { get; }

        public async Task<LogViewerViewModel> CreatePageAsync()
        {
            var page = new LogViewerViewModel(
                Reader,
                Settings.MutateGeneral,
                new ShellIntegration(),
                LogDirectory,
                Enum.Parse<LogLineLevel>(Settings.GetGeneral().LogLevel),
                post: action => action());

            await LogViewerFixture.SettleAsync(page).ConfigureAwait(false);
            return page;
        }

        public void Dispose()
        {
            _host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();

            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only; a leftover temp file does not fail the test.
            }
        }
    }
}
