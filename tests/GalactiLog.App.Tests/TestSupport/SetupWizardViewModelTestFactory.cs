using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Setup;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// Phase 9 Task 9. The one place App.Tests builds a <see cref="SetupWizardViewModel"/>, so a
/// constructor change is one file rather than fifty. The same shape as
/// <see cref="LibraryTabViewModelTestFactory"/>: no window and no dispatcher, the post seam
/// running its closure inline.
/// </summary>
/// <remarks>
/// It builds a <strong>real</strong> <see cref="SettingsStore"/> over a <see cref="TempDatabase"/>
/// rather than a lambda pair, because the roadmap's Verify line asks that each step's settings be
/// persisted and that finishing set <c>setup_complete</c>, and because the store is where the
/// validation the wizard leans on actually lives (design-lessons rule 2). No path used here has to
/// exist on disk: <c>SettingsStore.ValidateGeneral</c> is pure string work and the probe is a
/// delegate.
/// </remarks>
internal static class SetupWizardViewModelTestFactory
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>A scan folder that does not exist and does not need to.</summary>
    public const string Folder = @"C:\Astro\Captures";

    /// <summary>A second, disjoint scan folder.</summary>
    public const string SecondFolder = @"D:\Nas\Astro";

    /// <summary>The resolved default thumbnail cache root the wizard seeds step 2 with.</summary>
    public const string DefaultCacheRoot = @"C:\AppData\GalactiLog\thumbnails";

    /// <summary>The resolved data location step 2 shows. A path that does not exist and does not
    /// need to: nothing in the wizard touches disk.</summary>
    public const string DataRoot = @"C:\AppData\GalactiLogData";

    internal sealed class Harness : IDisposable
    {
        private readonly TempDatabase _database = new("galactilog-setup-wizard");

        public Harness()
        {
            Store = new SettingsStore(new SettingsRepository(_database.ConnectionString));
        }

        /// <summary>The real store the wizard reads and writes.</summary>
        public SettingsStore Store { get; }

        public RecordingLogger Logger { get; } = new();

        /// <summary>Thrown by the load delegate while set, so a test can drive the failed-read
        /// path.</summary>
        public Exception? LoadThrows { get; set; }

        /// <summary>Thrown from inside the store's critical section while set, which is where a
        /// refusal actually happens: nothing is written and no event is raised.</summary>
        public Exception? SaveThrows { get; set; }

        /// <summary>Every document handed to the mutator, in order.</summary>
        public List<GeneralSettings> Saves { get; } = [];

        /// <summary>The wizard's <see cref="SetupWizardViewModel.StepIndex"/> as each save
        /// reached the store, so a test can prove the save ran before the advance.</summary>
        public List<int> StepIndexAtSave { get; } = [];

        /// <summary>Every roots override the first scan was started with.</summary>
        public List<IReadOnlyList<string>> ScanRoots { get; } = [];

        /// <summary>Every cancellation token the first-scan delegate was handed, so a case can
        /// prove that finishing the wizard does not abort a scan already running.</summary>
        public List<CancellationToken> ScanTokens { get; } = [];

        public int Cancels;

        /// <summary>Parks the scan delegate until a test releases it.</summary>
        public ManualResetEventSlim? ScanRelease { get; set; }

        /// <summary>The probe's answer per folder. Anything not listed counts as 0.</summary>
        public Dictionary<string, int> ProbeCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every folder the probe was asked about.</summary>
        public List<string> Probed { get; } = [];

        /// <summary>Whether the shell was navigated to the Dashboard.</summary>
        public int Navigations;

        /// <summary>Every data location handed to the move recorder, in order.</summary>
        public List<string> MoveRequests { get; } = [];

        /// <summary>Returned by the move recorder while set, which is how the host refuses a
        /// destination that already holds a database.</summary>
        public string? MoveRefusal { get; set; }

        /// <summary>How many times a pending move was cancelled.</summary>
        public int MoveCancels;

        public ScanStatusService? ScanStatus { get; init; }

        public SetupWizardViewModel ViewModel { get; internal set; } = null!;

        /// <summary>What the store holds right now.</summary>
        public GeneralSettings Stored => Store.GetGeneral();

        /// <summary>Seeds the stored document before the wizard is built.</summary>
        public Harness Seed(Func<GeneralSettings, GeneralSettings> mutate)
        {
            Store.SaveGeneral(mutate(Store.GetGeneral()));
            return this;
        }

        /// <summary>The five steps, by their spec 12.1 position.</summary>
        public ScanFoldersStepViewModel Folders => (ScanFoldersStepViewModel)ViewModel.Steps[0];

        public ThumbnailCacheStepViewModel Cache => (ThumbnailCacheStepViewModel)ViewModel.Steps[1];

        public ObserverLocationStepViewModel Observer => (ObserverLocationStepViewModel)ViewModel.Steps[2];

        public ScanOptionsStepViewModel Options => (ScanOptionsStepViewModel)ViewModel.Steps[3];

        public FirstScanStepViewModel FirstScan => (FirstScanStepViewModel)ViewModel.Steps[4];

        public void Dispose()
        {
            ViewModel.Dispose();
            _database.Dispose();
        }
    }

    /// <param name="seed">Applied to the stored document before the wizard is constructed.</param>
    /// <param name="scanStatus">Passed straight through; null leaves the progress readout empty
    /// and the scan button enabled.</param>
    /// <param name="navigate">Whether the wizard is given a navigate-to-dashboard delegate.
    /// </param>
    /// <param name="timezones">The observer timezone list. Defaults to two fixed ids plus the
    /// local one, so the test does not depend on the machine's zone database.</param>
    /// <param name="localTimezoneId">The system zone the combo defaults to.</param>
    /// <param name="post">How the wizard reaches the UI thread. Defaults to running the closure
    /// inline, which is what every test that is not about marshalling wants.</param>
    /// <param name="loadThrows">Set before the wizard is constructed, which is the only way to
    /// make its one read fail.</param>
    /// <param name="volumeSpace">The free-space reader. Defaults to a fixed pair.</param>
    /// <param name="dataRoot">The resolved data location step 2 shows. Defaults to a
    /// default-source resolution over <see cref="DataRoot"/>.</param>
    public static Harness Create(
        Func<GeneralSettings, GeneralSettings>? seed = null,
        ScanStatusService? scanStatus = null,
        bool navigate = true,
        Func<IReadOnlyList<string>>? timezones = null,
        Func<string>? localTimezoneId = null,
        Action<Action>? post = null,
        Exception? loadThrows = null,
        Func<string, (long Total, long Free)?>? volumeSpace = null,
        AppDataRootResolution? dataRoot = null)
    {
        var harness = new Harness
        {
            ScanStatus = scanStatus,
            LoadThrows = loadThrows,
        };
        if (seed is not null)
        {
            harness.Seed(seed);
        }

        harness.ViewModel = new SetupWizardViewModel(
            () => harness.LoadThrows is null ? harness.Store.GetGeneral() : throw harness.LoadThrows,
            mutate => harness.Store.MutateGeneral(current =>
            {
                var next = mutate(current);
                lock (harness.Saves)
                {
                    harness.Saves.Add(next);
                    harness.StepIndexAtSave.Add(harness.ViewModel.StepIndex);
                }

                if (harness.SaveThrows is { } failure)
                {
                    throw failure;
                }

                return next;
            }),
            (folder, _) =>
            {
                lock (harness.Probed)
                {
                    harness.Probed.Add(folder);
                }

                return Task.FromResult(harness.ProbeCounts.TryGetValue(folder, out var count) ? count : 0);
            },
            volumeSpace ?? (_ => (100_000_000_000L, 40_000_000_000L)),
            () => DefaultCacheRoot,
            (roots, token) =>
            {
                lock (harness.ScanRoots)
                {
                    harness.ScanRoots.Add(roots);
                    harness.ScanTokens.Add(token);
                }

                harness.ScanRelease?.Wait(Budget, token);
                return Task.FromResult(Outcome());
            },
            () => Interlocked.Increment(ref harness.Cancels),
            navigateToDashboard: navigate ? () => Interlocked.Increment(ref harness.Navigations) : null,
            scanStatus: scanStatus,
            systemTimezones: timezones ?? (() => ["Etc/UTC", "Europe/London", TimeZoneInfo.Local.Id]),
            localTimezoneId: localTimezoneId,
            post: post ?? (action => action()),
            logger: harness.Logger,
            // Phase 10 Task 9's three seams. Values and lambdas only: nothing here reads or writes
            // a pointer file, and no path used has to exist.
            dataRoot: () => dataRoot
                ?? new AppDataRootResolution(DataRoot, AppDataRootSource.Default, null, null, null),
            requestDataRootMove: destination =>
            {
                lock (harness.MoveRequests)
                {
                    harness.MoveRequests.Add(destination);
                }

                return harness.MoveRefusal;
            },
            cancelDataRootMove: () => Interlocked.Increment(ref harness.MoveCancels));

        return harness;
    }

    /// <summary>A completed first run, as <c>ScanCoordinator.RunAsync</c> reports one.</summary>
    public static ScanRunOutcome Outcome() => new(1, "complete", 12, 12, 0, 12, 0, 0, 0);
}
