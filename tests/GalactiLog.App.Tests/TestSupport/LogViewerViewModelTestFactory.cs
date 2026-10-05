using System.Diagnostics;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.Core.Diagnostics;
using GalactiLog.Core.Io;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one place App.Tests builds a log viewer and the fixture files behind it, so a later
/// constructor change is one edit rather than twenty.
/// </summary>
/// <remarks>
/// <para>
/// It composes with <see cref="DiagnosticsViewModelTestFactory"/> rather than duplicating it: that
/// factory owns the Diagnostics page and its snapshot, this one owns the log viewer, its temp log
/// directory and the real <c>SettingsStore</c> the capture-level picker writes through.
/// </para>
/// <para>
/// <c>LogReader</c>, <c>SettingsStore</c> and <c>ShellIntegration</c> are all sealed with
/// non-virtual members by design, so there is nothing to stub: the cheapest honest substitute is
/// the real thing over a temp directory, a temp database, and recording delegates.
/// </para>
/// <para>
/// Test-only plain file I/O, which <c>FileSafetyTest</c> does not scan (it scans <c>src/**</c>).
/// </para>
/// </remarks>
internal sealed class LogViewerFixture : IDisposable
{
    private readonly TempDatabase _database;
    private readonly string _root;

    public LogViewerFixture()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "galactilog-logviewer-" + Guid.NewGuid().ToString("N"));
        LogDirectory = Path.Combine(_root, DiagnosticsService.LogDirectoryName);
        Directory.CreateDirectory(LogDirectory);

        _database = new TempDatabase("galactilog-logviewer");
        Settings = new SettingsStore(new SettingsRepository(_database.ConnectionString));
        Reader = new LogReader(LogDirectory);
        AppWriter = new AppWriter(_root);
        Shell = new ShellIntegration(
            text =>
            {
                CopyThreads.Add(Environment.CurrentManagedThreadId);
                Copied.Add(text);
                return Task.CompletedTask;
            },
            info =>
            {
                Launched.Add(info);

                // Nothing is actually launched: returning null is what keeps Explorer out of the
                // test run.
                return null;
            });
    }

    public string LogDirectory { get; }

    public SettingsStore Settings { get; }

    public LogReader Reader { get; }

    public ShellIntegration Shell { get; }

    /// <summary>Task 7: authorizes Clear log's deletes under the same temp root <see cref="LogDirectory"/>
    /// sits under.</summary>
    public AppWriter AppWriter { get; }

    /// <summary>Task 7's Save log as picker seam. Null by default (the command stays disabled,
    /// the same "no export surface" shape <c>DiagnosticsViewModelTestFactory</c> uses); a case
    /// sets it before building the page to exercise the export.</summary>
    public Func<Task<string?>>? SaveLogAsDestinationPicker { get; set; }

    /// <summary>Every destination and contents pair <see cref="ExportLog"/> wrote, in order, so a
    /// case can assert on what Save log as produced without a real <c>DiagnosticsService</c>.
    /// </summary>
    public List<(string Destination, string Contents)> Exported { get; } = [];

    /// <summary>The honest substitute for <c>DiagnosticsService.ExportLog</c> (same shape,
    /// beside <c>ExportBundle</c>): <c>DiagnosticsService</c> needs a database and a dozen other
    /// collaborators this fixture has no use for, so the export itself is reproduced inline over
    /// the same <see cref="AppWriter"/>.</summary>
    public void ExportLog(string destination, string contents)
    {
        using var writer = AppWriter.BeginExport(destination);
        writer.WriteAllText(destination, contents);
        Exported.Add((destination, contents));
    }

    /// <summary>Every text handed to the clipboard, in order.</summary>
    public List<string> Copied { get; } = [];

    /// <summary>The thread each copy arrived on, so a case can assert the read behind it did not
    /// run on the caller's thread.</summary>
    public List<int> CopyThreads { get; } = [];

    /// <summary>Every process the shell was asked to start, in order. None of them runs.</summary>
    public List<ProcessStartInfo> Launched { get; } = [];

    /// <summary>The one delay seam, driving both the search debounce and the follow-tail loop.
    /// </summary>
    public FakeDelay Delay { get; } = new();

    /// <summary>One entry exactly as spec 16.1's output template writes it.</summary>
    public static string Entry(
        string time, string level = "INF", string source = "GalactiLog.App.AppHost",
        string message = "Started")
        => $"2026-09-15 {time} +00:00 [{level}] {source} {message}";

    /// <summary>Writes one rolling log file into the fixture's log directory.</summary>
    public string WriteLogFile(string name, params string[] lines)
    {
        var path = Path.Combine(LogDirectory, name);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        return path;
    }

    /// <summary>The raw form, for the cases that supply their own post seam.</summary>
    public LogViewerViewModel Build(
        Action<Action>? post = null,
        LogLineLevel initialCaptureLevel = LogLineLevel.Information,
        int copyAllCap = LogViewerViewModel.DefaultCopyAllCap,
        int initialActivityRetentionDays = 90,
        int initialAppLogRetentionDays = 14,
        int initialAppLogMaxRows = 50_000,
        bool withSaveLogAs = true,
        bool withClearLog = true)
    {
        var page = new LogViewerViewModel(
            Reader,
            Settings.MutateGeneral,
            Shell,
            LogDirectory,
            initialCaptureLevel,
            post ?? (action => action()),
            Delay.Delay,
            copyAllCap,
            logger: null,
            initialActivityRetentionDays: initialActivityRetentionDays,
            initialAppLogRetentionDays: initialAppLogRetentionDays,
            initialAppLogMaxRows: initialAppLogMaxRows,
            saveLogAsDestinationPicker: SaveLogAsDestinationPicker,
            exportLog: withSaveLogAs ? ExportLog : null,
            appWriter: withClearLog ? AppWriter : null);

        _pages.Add(page);
        return page;
    }

    /// <summary>
    /// The awaiting form. The first load is kicked through the post seam in the constructor, so
    /// this awaits it before handing the page back. Awaited, never blocked on (TRACKING section 2
    /// item 8).
    /// </summary>
    public async Task<LogViewerViewModel> CreateAsync(
        LogLineLevel initialCaptureLevel = LogLineLevel.Information,
        int copyAllCap = LogViewerViewModel.DefaultCopyAllCap,
        int initialActivityRetentionDays = 90,
        int initialAppLogRetentionDays = 14,
        int initialAppLogMaxRows = 50_000,
        bool withSaveLogAs = true,
        bool withClearLog = true)
    {
        var page = Build(
            post: null, initialCaptureLevel, copyAllCap,
            initialActivityRetentionDays, initialAppLogRetentionDays, initialAppLogMaxRows,
            withSaveLogAs, withClearLog);
        await SettleAsync(page).ConfigureAwait(false);
        return page;
    }

    /// <summary>
    /// Joins whatever load the page has in flight, bounded and without rethrowing, the shape
    /// <c>ActivityViewModel.Quiesce</c> established. A harness join exists to join work, not to
    /// assert on it: a test that cares about the outcome asserts on the page.
    /// </summary>
    public static async Task SettleAsync(LogViewerViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            if (page.PendingLoad is { } pending)
            {
                await pending.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Every page this fixture built, so <see cref="Dispose"/> joins whatever read is
    /// still walking the temp log directory before deleting it (review finding M4).</summary>
    private readonly List<LogViewerViewModel> _pages = [];

    public void Dispose()
    {
        // Bounded, and it joins rather than asserts: LogReader swallows the IOException a deleted
        // directory would raise, so this is about not racing the delete rather than about a
        // failure anyone would see.
        foreach (var page in _pages)
        {
            page.Quiesce(TimeSpan.FromSeconds(30));
        }

        _database.Dispose();

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
