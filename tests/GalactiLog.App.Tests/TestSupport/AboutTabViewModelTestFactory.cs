using System.Diagnostics;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// Builds spec 12.7's About tab over a recording update checker, a recording shell and a temp log
/// directory, so a case asserts what the tab shows and what it launched without an installed
/// build, a window or a network call.
/// </summary>
internal static class AboutTabViewModelTestFactory
{
    /// <summary>The tab with everything it needs to be disposed and torn down again.</summary>
    internal sealed class Harness : IDisposable
    {
        public required AboutTabViewModel ViewModel { get; init; }

        public required RecordingUpdateChecker Checker { get; init; }

        public required RecordingLogger Logger { get; init; }

        /// <summary>Every process the tab asked the shell to launch, in order.</summary>
        public required List<ProcessStartInfo> Launches { get; init; }

        /// <summary>The rolling log directory the tab was given.</summary>
        public required string LogDirectory { get; init; }

        public UpdateService? Updates { get; init; }

        public ScanStatusService? Status { get; init; }

        public ScanCoordinator? Coordinator { get; init; }

        public TempDatabase? Database { get; init; }

        public void Dispose()
        {
            ViewModel.Dispose();
            Updates?.Dispose();
            Status?.Dispose();
            Database?.Dispose();

            if (Directory.Exists(LogDirectory))
            {
                // Test-only plain file I/O, which FileSafetyTest does not scan.
                Directory.Delete(LogDirectory, recursive: true);
            }
        }
    }

    /// <param name="buildInfo">The build the tab reports. Defaults to an installed alpha build,
    /// which is the case every field has something to show for.</param>
    /// <param name="withUpdateService">False builds the tab with no update service at all, which
    /// is the surface that leaves the check button present and disabled.</param>
    /// <param name="logFiles">Names of files to place in the temp log directory, oldest first.
    /// </param>
    /// <param name="post">How the tab reaches the UI thread. Synchronous by default.</param>
    public static Harness Create(
        BuildInfo? buildInfo = null,
        bool withUpdateService = true,
        IReadOnlyList<string>? logFiles = null,
        Action<Action>? post = null)
    {
        var info = buildInfo ?? new BuildInfo("1.4.2.0", "0b1d3f5", "alpha", isInstalled: true);
        var checker = new RecordingUpdateChecker();
        var logger = new RecordingLogger();
        var launches = new List<ProcessStartInfo>();

        var logDirectory = Directory
            .CreateTempSubdirectory("galactilog-about-logs-")
            .FullName;
        foreach (var name in logFiles ?? [])
        {
            File.WriteAllText(Path.Combine(logDirectory, name), "log line");
        }

        var shell = new ShellIntegration(start: startInfo =>
        {
            launches.Add(startInfo);
            return null;
        });

        TempDatabase? database = null;
        ScanCoordinator? coordinator = null;
        ScanStatusService? status = null;
        UpdateService? updates = null;

        if (withUpdateService)
        {
            database = new TempDatabase("galactilog-abouttab");
            coordinator = ScanCoordinatorTestFactory.CreateBare();
            status = new ScanStatusService(coordinator, action => action());
            updates = new UpdateService(
                info,
                () => checker,
                status,
                new ActivityRepository(database.ConnectionString),
                showPrompt: null,
                logger: logger,
                post: action => action());
        }

        return new Harness
        {
            ViewModel = new AboutTabViewModel(
                info, updates, shell, logDirectory, post ?? (action => action()), logger),
            Checker = checker,
            Logger = logger,
            Launches = launches,
            LogDirectory = logDirectory,
            Updates = updates,
            Status = status,
            Coordinator = coordinator,
            Database = database,
        };
    }
}
