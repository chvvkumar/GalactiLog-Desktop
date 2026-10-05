using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Diagnostics;
using Serilog;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 7 (PAR-016, spec 12.8, section 6.4). Clear log deletes the rolled Serilog files
// under the app data log directory through AppWriter.Delete and nothing else; the live file (the
// one the sink currently holds open) is identified by name and excluded, never relied on the
// operating system's share lock alone.
//
// Phase 14B fixer, fixer list item 44: the live file is the FIRST of LogFileSet.Newest, not the
// first match on today's date prefix. Every case below therefore writes a live file, which is what
// a running application always has: AppHost logs one unconditional startup line, so the sink has
// opened a file by the time anything can press this button.
public class ClearLogTests
{
    private static string TodayName(string suffix = "")
        => $"galactilog-{DateTime.Now:yyyyMMdd}{suffix}.log";

    /// <summary>The file the sink holds open, which sorts newest.</summary>
    private static string Live(LogViewerFixture fixture)
        => fixture.WriteLogFile(TodayName(), LogViewerFixture.Entry("09:00:00.000", message: "live"));

    [Fact]
    public async Task ClearLog_DeletesEveryRolledFile()
    {
        using var fixture = new LogViewerFixture();
        Live(fixture);
        var rolledA = fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));
        var rolledB = fixture.WriteLogFile("galactilog-20260911.log", LogViewerFixture.Entry("09:00:00.000"));

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.False(File.Exists(rolledA));
        Assert.False(File.Exists(rolledB));
    }

    [Fact]
    public async Task ClearLog_LeavesTheLiveFile()
    {
        using var fixture = new LogViewerFixture();
        var live = fixture.WriteLogFile(TodayName(), LogViewerFixture.Entry("09:00:00.000"));
        fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.True(File.Exists(live));
    }

    [Fact]
    public async Task ClearLog_DoesNotTruncateTheLiveFile()
    {
        using var fixture = new LogViewerFixture();
        var body = LogViewerFixture.Entry("09:00:00.000", message: "kept");
        var live = fixture.WriteLogFile(TodayName(), body);
        fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.Contains("kept", File.ReadAllText(live), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearLog_WithASecondFileForToday_LeavesOnlyTheLiveOne()
    {
        using var fixture = new LogViewerFixture();
        // A size roll produced a second file for today: galactilog-yyyyMMdd.log (closed, the
        // first part of the day) and galactilog-yyyyMMdd_001.log (the one the sink now holds
        // open). Ordinal descending sorts _001 first (LogFileSet.Newest's own rule: '.' < '_'),
        // so it is the one section 6.4 identifies as live.
        var closedEarlier = fixture.WriteLogFile(TodayName(), LogViewerFixture.Entry("08:00:00.000"));
        var liveNow = fixture.WriteLogFile(TodayName("_001"), LogViewerFixture.Entry("09:00:00.000"));

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.False(File.Exists(closedEarlier));
        Assert.True(File.Exists(liveNow));
    }

    // Phase 14B fixer, fixer list item 54 (task7-review P3). Both cases below used to assert that
    // a single digit appears somewhere in the sentence and never looked at the byte figure their
    // names promise. The whole sentence is asserted now, against MetricText.Bytes over the
    // fixture's own file lengths, which is the formatter the production line uses.
    [Fact]
    public async Task ClearLog_ReportsTheFileCountAndTheBytes()
    {
        using var fixture = new LogViewerFixture();
        Live(fixture);
        var rolledA = fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));
        var rolledB = fixture.WriteLogFile("galactilog-20260911.log", LogViewerFixture.Entry("09:00:00.000"));
        var bytes = new FileInfo(rolledA).Length + new FileInfo(rolledB).Length;

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.Equal($"Cleared 2 rolled log files, {MetricText.Bytes(bytes)}.", page.ClearLogNotice);
    }

    [Fact]
    public async Task ClearLog_NamesWhatItWillDeleteBeforeItRuns()
    {
        using var fixture = new LogViewerFixture();
        Live(fixture);
        var rolled = fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));
        var bytes = new FileInfo(rolled).Length;

        using var page = await fixture.CreateAsync();

        // Named before the click: ClearLogNotice is populated by the load that already happened
        // at construction, not by the click itself.
        Assert.Equal($"Clear log deletes 1 rolled log file, {MetricText.Bytes(bytes)}.", page.ClearLogNotice);
    }

    // Phase 14B fixer, fixer list item 44 (task7-review P3). Between midnight and the first log
    // event of the new day the sink has not rolled, so the file it holds open still carries
    // YESTERDAY's date. Identifying the live file by today's date prefix left it null in that
    // window, treated the open file as rolled, and AppWriter.Delete threw IOException on it: the
    // outer catch swallowed the throw, the loop abandoned the remaining deletes and the report
    // presented the partial count as a success. The newest file is the live one whatever date it
    // carries, which is the ordering argument IdentifyLogFiles already made in prose.
    [Fact]
    public async Task ClearLog_AfterMidnightBeforeTheFirstEvent_SparesTheOpenFile()
    {
        using var fixture = new LogViewerFixture();
        var stillOpen = fixture.WriteLogFile("galactilog-20260911.log", LogViewerFixture.Entry("23:59:00.000"));
        var rolledA = fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));
        var rolledB = fixture.WriteLogFile("galactilog-20260909.log", LogViewerFixture.Entry("09:00:00.000"));
        var bytes = new FileInfo(rolledA).Length + new FileInfo(rolledB).Length;

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.True(File.Exists(stillOpen));
        Assert.False(File.Exists(rolledA));
        Assert.False(File.Exists(rolledB));
        Assert.Equal($"Cleared 2 rolled log files, {MetricText.Bytes(bytes)}.", page.ClearLogNotice);
    }

    [Fact]
    public async Task ClearLog_NeedsNoTypedConfirmation()
    {
        // Spec-writer question 5, ruled as written: a named statement is not a confirmation
        // dialog. There is no seam on LogViewerViewModel for a confirm/cancel prompt at all, so
        // Execute alone is the whole action.
        using var fixture = new LogViewerFixture();
        Live(fixture);
        var rolled = fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.False(File.Exists(rolled));
    }

    // Verification E1. Clear log deletes everything LogFileSet.Live does not name, and the size
    // roll is where a positional rule breaks first: ten rolls in one day is an ordinary heavy
    // scan against a 32 MB size limit. The open file here is _010, not the one a careless rule
    // picks.
    [Fact]
    public async Task ClearLog_AfterTenSizeRolls_SparesTheHighestSequence()
    {
        using var fixture = new LogViewerFixture();
        var rolled = new List<string>();
        rolled.Add(fixture.WriteLogFile(TodayName(), LogViewerFixture.Entry("08:00:00.000")));
        for (var sequence = 1; sequence <= 9; sequence++)
        {
            rolled.Add(fixture.WriteLogFile(
                TodayName($"_{sequence:000}"), LogViewerFixture.Entry("09:00:00.000")));
        }

        var stillOpen = fixture.WriteLogFile(TodayName("_010"), LogViewerFixture.Entry("10:00:00.000"));

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.True(File.Exists(stillOpen));
        Assert.All(rolled, path => Assert.False(File.Exists(path)));
    }

    // Verification E1, the platform half, measured rather than assumed. The suspicion put to this
    // fixer was that Clear log had deleted the file the Serilog sink holds open and that this was
    // why the application exited with nothing in the log. This case builds a real Serilog rolling
    // file sink over a temp directory, exactly as AppHost does (shared: false), writes an event so
    // the file is open, and records what a delete of that file does. It documents the safety net
    // the whole Clear log design rests on: the sink's own share mode, not this application's care.
    [Fact]
    public void AnOpenSerilogSinkFile_CannotBeDeletedWhileTheSinkHoldsIt()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "galactilog-sinklock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var logger = new LoggerConfiguration()
                .WriteTo.File(
                    Path.Combine(directory, LogFileSet.FileNamePrefix + ".log"),
                    rollingInterval: RollingInterval.Day,
                    shared: false,
                    flushToDiskInterval: TimeSpan.FromMilliseconds(50))
                .CreateLogger();

            try
            {
                logger.Information("An event, so the sink has opened its file");
                var open = Assert.Single(Directory.GetFiles(directory));

                var failure = Record.Exception(() => File.Delete(open));

                Assert.IsAssignableFrom<IOException>(failure);
                Assert.True(File.Exists(open));
            }
            finally
            {
                logger.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    // ---- Verification B4: the Clear log path's writes stay on the dispatcher -----------------
    //
    // Clear log crashed the process on every press with an unhandled "Call from invalid thread"
    // out of Button.get_Command, because ClearLogAsync resumed on a thread-pool thread after its
    // deletes and then called RefreshAsync straight from there, and Load writes IsLoading, which
    // carries [NotifyCanExecuteChangedFor].
    //
    // The two cases below build the page with a REAL dispatcher post, not the fixture's inline
    // default, which is what makes the thread the writes land on a question at all. A headless
    // page has no bound Button, so the production symptom itself cannot be reproduced; the thread
    // the write happens on is the cause and is what these pin.
    /// <summary>
    /// Every <c>PropertyChanged</c> that arrived somewhere other than the dispatcher, with the
    /// thread it arrived on, so a failure names the write rather than only its own assertion.
    /// </summary>
    private sealed class OffThreadWrites
    {
        private readonly List<(string Name, int Thread)> _seen = [];

        public void Watch(LogViewerViewModel page) => page.PropertyChanged += (_, e) =>
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                lock (_seen)
                {
                    _seen.Add((e.PropertyName ?? "(no name)", Environment.CurrentManagedThreadId));
                }
            }
        };

        public int Count
        {
            get
            {
                lock (_seen)
                {
                    return _seen.Count;
                }
            }
        }

        public override string ToString()
        {
            lock (_seen)
            {
                return _seen.Count == 0
                    ? "none"
                    : string.Join(", ", _seen.Select(one => $"{one.Name} (thread {one.Thread})"));
            }
        }
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="reached"/> holds, then fails naming what it was
    /// waiting for and every off-dispatcher write seen so far.
    /// </summary>
    /// <remarks>
    /// The fixture's own <c>SettleAsync</c> joins <c>PendingLoad</c>, which is the right join while
    /// the page's post seam runs inline. These two cases give the page a REAL dispatcher post, and
    /// then <c>PendingLoad</c> is still null immediately after construction, because the first load
    /// is QUEUED rather than started: the constructor ends in
    /// <c>_post(() =&gt; RefreshCommand.Execute(null))</c>. Joining a null pending load returns at
    /// once and leaves that load to start later, overlapping whatever the case does next, and an
    /// overlapping load's older <c>Publish</c> returns early on its generation check without
    /// clearing <c>IsLoading</c>. That is a property of this harness under a real dispatcher rather
    /// than of the page, which is why the wait is here and not in the fixture.
    /// </remarks>
    private static async Task PumpUntil(Func<bool> reached, string what, OffThreadWrites writes)
    {
        for (var round = 0; round < 400; round++)
        {
            Dispatcher.UIThread.RunJobs();
            if (reached())
            {
                Dispatcher.UIThread.RunJobs();
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"Timed out waiting for {what}. Off-dispatcher writes seen: {writes}.");
    }

    // Every PropertyChanged this command produces arrives on the dispatcher thread, and the page
    // is idle when it ends.
    [AvaloniaFact]
    public async Task ClearLog_WritesEveryObservablePropertyOnTheDispatcherThread()
    {
        using var fixture = new LogViewerFixture();
        Live(fixture);
        fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));

        var writes = new OffThreadWrites();
        var page = fixture.Build(post: action => Dispatcher.UIThread.Post(action));
        await PumpUntil(
            () => page.PendingLoad is { IsCompleted: true } && !page.IsLoading,
            "the constructor's own first load to publish",
            writes);

        // Watched after construction has settled, so nothing the page did while being built is
        // attributed to the command under test.
        writes.Watch(page);

        await page.ClearLogCommand.ExecuteAsync(null);
        await PumpUntil(() => !page.IsLoading, "the reload after Clear log to publish", writes);

        Assert.True(writes.Count == 0, $"PropertyChanged arrived off the dispatcher: {writes}.");
        Assert.False(page.IsLoading);
    }

    [AvaloniaFact]
    public async Task SaveLogAs_WritesEveryObservablePropertyOnTheDispatcherThread()
    {
        using var fixture = new LogViewerFixture();
        Live(fixture);
        var destination = Path.Combine(
            Path.GetTempPath(), "galactilog-savelog-" + Guid.NewGuid().ToString("N") + ".txt");
        fixture.SaveLogAsDestinationPicker = () => Task.FromResult<string?>(destination);

        var writes = new OffThreadWrites();
        var page = fixture.Build(post: action => Dispatcher.UIThread.Post(action));
        await PumpUntil(
            () => page.PendingLoad is { IsCompleted: true } && !page.IsLoading,
            "the constructor's own first load to publish",
            writes);

        writes.Watch(page);

        try
        {
            await page.SaveLogAsCommand.ExecuteAsync(null);
            await PumpUntil(() => !page.IsLoading, "the page to be idle after Save log as", writes);

            Assert.True(writes.Count == 0, $"PropertyChanged arrived off the dispatcher: {writes}.");
            Assert.False(page.IsLoading);
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    [Fact]
    public async Task ClearLog_ReloadsTheViewer()
    {
        using var fixture = new LogViewerFixture();
        Live(fixture);
        fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000", message: "gone soon"));

        using var page = await fixture.CreateAsync();
        Assert.Equal(1, page.LoadCount);

        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.True(page.LoadCount > 1);
    }

    [Fact]
    public async Task ClearLog_TouchesNothingOutsideTheLogDirectory()
    {
        using var fixture = new LogViewerFixture();
        Live(fixture);
        fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));
        var outsideMarker = Path.Combine(fixture.LogDirectory, "..", "marker.txt");
        File.WriteAllText(outsideMarker, "keep");

        using var page = await fixture.CreateAsync();
        await page.ClearLogCommand.ExecuteAsync(null);

        Assert.True(File.Exists(outsideMarker));
    }

    [Fact]
    public async Task ClearLog_OnAnEmptyDirectory_ReportsZeroAndDoesNotThrow()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();

        var exception = await Record.ExceptionAsync(() => page.ClearLogCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Contains("No rolled log files", page.ClearLogNotice, StringComparison.Ordinal);
    }
}
