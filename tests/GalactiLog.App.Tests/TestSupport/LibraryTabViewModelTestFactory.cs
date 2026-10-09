using System.Text.Json;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// Phase 9 Task 5. The one place App.Tests builds a <see cref="LibraryTabViewModel"/>, so Task 9
/// changes one file rather than thirty. Same shape as
/// <see cref="TargetsTabViewModelTestFactory"/>: no window and no dispatcher, the post seam
/// running its closure inline.
/// </summary>
/// <remarks>
/// It builds a <strong>real</strong> <see cref="SettingsStore"/> over a
/// <see cref="TempDatabase"/> rather than a lambda pair, because the roadmap's Verify line asks
/// for a round trip "through <c>SettingsStore</c>" and because the store is where the validation
/// this tab depends on actually lives (design-lessons rule 2). The store is sealed with
/// non-virtual members by design, so the cheapest honest substitute is the real thing over a
/// temp file, which is the same call <c>SettingsFixture</c> makes for the watcher tests.
/// <para>
/// No path used here has to exist on disk. <c>SettingsStore.ValidateGeneral</c> and
/// <c>ScanFilterConfig</c> are pure string work, and nothing in the tab touches the filesystem.
/// </para>
/// </remarks>
internal static class LibraryTabViewModelTestFactory
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>A scan root that does not exist and does not need to.</summary>
    public const string Root = @"C:\Astro\Captures";

    /// <summary>A second, disjoint scan root.</summary>
    public const string SecondRoot = @"D:\Nas\Astro";

    internal sealed class Harness : IDisposable
    {
        private readonly TempDatabase _database = new("galactilog-library-tab");

        public Harness()
        {
            Store = new SettingsStore(new SettingsRepository(_database.ConnectionString));
        }

        /// <summary>The real store the tab reads and writes.</summary>
        public SettingsStore Store { get; }

        public RecordingLogger Logger { get; } = new();

        /// <summary>Thrown by the load delegate while set, so a test can drive the failed-read
        /// state.</summary>
        public Exception? LoadThrows { get; set; }

        /// <summary>Thrown by the save delegate while set, instead of reaching the store. The
        /// only way to exercise the tab's <c>SettingsValidationException</c> backstop, because
        /// the inline pre-check refuses the save before the store can.</summary>
        public Exception? SaveThrows { get; set; }

        /// <summary>Whether the tab was wired to the store's <c>GeneralChanged</c> event. Set by
        /// <see cref="Create"/>'s <c>followGeneralChanged</c> argument.</summary>
        public bool Following { get; init; }

        /// <summary>Writes the general document as another writer would: straight to the store,
        /// outside the tab's own write chain, so the tab sees it only through
        /// <c>GeneralChanged</c>.</summary>
        public GeneralSettings SaveElsewhere(Func<GeneralSettings, GeneralSettings> mutate)
            => Store.MutateGeneral(mutate);

        public int Loads;

        /// <summary>Every document handed to the save delegate, in order.</summary>
        public List<GeneralSettings> Saves { get; } = [];

        public int Cancels;

        public int ScanRuns;

        /// <summary>The <see cref="ScanRunOptions"/> each run was started with, in order (Phase
        /// 14B Task 5, spec 10.3's PAR-013).</summary>
        public List<ScanRunOptions> ScanOptions { get; } = [];

        /// <summary>The thread each scan start ran on, so a test can prove the call left the UI
        /// thread. Empty unless <see cref="Create"/> was given an <c>onUiThread</c> reader.
        /// </summary>
        public List<bool> ScanOnUiThread { get; } = [];

        /// <summary>Parks the save delegate until a test releases it, so a case can act while a
        /// save is genuinely in flight.</summary>
        public ManualResetEventSlim? SaveRelease { get; set; }

        /// <summary>Set each time the save delegate is entered.</summary>
        public ManualResetEventSlim SaveEntered { get; } = new(false);

        /// <summary>Parks the scan delegate until a test releases it.</summary>
        public ManualResetEventSlim? ScanRelease { get; set; }

        /// <summary>Set the first time the scan delegate is entered, so a test can press the
        /// button a second time while the first run is genuinely in flight (FIXER LIST F19).
        /// </summary>
        public ManualResetEventSlim ScanEntered { get; } = new(false);

        public ScanStatusService? ScanStatus { get; init; }

        public LibraryTabViewModel ViewModel { get; internal set; } = null!;

        /// <summary>What the store holds right now.</summary>
        public GeneralSettings Stored => Store.GetGeneral();

        /// <summary>Seeds the stored document before the tab is built.</summary>
        public Harness Seed(Func<GeneralSettings, GeneralSettings> mutate)
        {
            Store.SaveGeneral(mutate(Store.GetGeneral()));
            return this;
        }

        /// <summary>
        /// Seeds a document the store would refuse, by writing the row straight through the
        /// repository.
        /// </summary>
        /// <remarks>
        /// FIXER LIST F10 raised the duplicate-or-nested scan-root rule into
        /// <c>ScanFilterConfig.Validate</c>, so <see cref="Seed"/> can no longer produce the state
        /// the "a stored nested pair is flagged" cases are about. This is the hand-edited settings
        /// file those cases always described: past the store, straight into the column.
        /// </remarks>
        public Harness SeedRaw(Func<GeneralSettings, GeneralSettings> mutate)
        {
            var repository = new SettingsRepository(_database.ConnectionString);
            var row = repository.Load();
            row.General = JsonSerializer.Serialize(mutate(Store.GetGeneral()));
            repository.Save(row);
            return this;
        }

        /// <summary>Joins the in-flight load and every queued write, so a test asserts against a
        /// settled tab instead of sleeping. The blocking wait lives here rather than in a test
        /// method, which is what xunit's own analyzer asks for.</summary>
        public Harness Settle()
        {
            ViewModel.PendingLoad?.Wait(Budget);
            SettleWrites();
            return this;
        }

        /// <summary>
        /// The awaiting form of <see cref="Settle"/>, for tests that run on the headless UI thread.
        /// </summary>
        /// <remarks>
        /// FIXER LIST F26 and TRACKING section 2 item 8: the headless UI thread is itself a
        /// thread-pool thread, so a blocking wait from an <c>AvaloniaFact</c> on a <c>Task.Run</c>
        /// queued from it can let the pool inline that work onto the very thread the test is
        /// asserting about. The Activity, Statistics and preference-tab factories each grew this
        /// during their fix passes; this one did not until the fixer pass.
        /// </remarks>
        public async Task<Harness> SettleAsync()
        {
            if (ViewModel.PendingLoad is { } load)
            {
                await load.ConfigureAwait(true);
            }

            var deadline = DateTime.UtcNow + Budget;
            while (DateTime.UtcNow < deadline)
            {
                var tail = ViewModel.PendingWrite;
                await tail.ConfigureAwait(true);
                if (ReferenceEquals(tail, ViewModel.PendingWrite))
                {
                    break;
                }
            }

            return this;
        }

        /// <summary>Joins the write chain, repeatedly: a save's success callback can queue
        /// nothing further today, but the tail is replaced by every new write.</summary>
        public Harness SettleWrites()
        {
            var deadline = DateTime.UtcNow + Budget;
            while (DateTime.UtcNow < deadline)
            {
                var tail = ViewModel.PendingWrite;
                tail.Wait(Budget);
                if (ReferenceEquals(tail, ViewModel.PendingWrite))
                {
                    break;
                }
            }

            return this;
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            ScanEntered.Dispose();
            SaveEntered.Dispose();
            _database.Dispose();
        }
    }

    /// <param name="seed">Applied to the stored document before the tab is constructed.</param>
    /// <param name="scanStatus">Passed straight through; null leaves the progress readout empty
    /// and the scan button enabled.</param>
    /// <param name="onUiThread">Reads whether the calling thread is the UI thread, so the
    /// off-thread assertion does not need a dispatcher reference inside this factory.</param>
    /// <param name="post">How the tab reaches the UI thread. Defaults to running the closure
    /// inline, which is what every test that is not about marshalling wants.</param>
    /// <param name="loadThrows">Set before the tab is constructed, which is the only way to make
    /// its <em>first</em> load fail.</param>
    /// <param name="followGeneralChanged">Wires the tab to the store's <c>GeneralChanged</c>
    /// event, as <c>AppHost</c> does. Off by default, so a test that is not about a concurrent
    /// writer sees exactly one load.</param>
    public static Harness Create(
        Func<GeneralSettings, GeneralSettings>? seed = null,
        ScanStatusService? scanStatus = null,
        Func<bool>? onUiThread = null,
        Action<Action>? post = null,
        Exception? loadThrows = null,
        bool followGeneralChanged = false,
        Func<GeneralSettings, GeneralSettings>? seedRaw = null)
    {
        var harness = new Harness
        {
            ScanStatus = scanStatus,
            LoadThrows = loadThrows,
            Following = followGeneralChanged,
        };
        if (seed is not null)
        {
            harness.Seed(seed);
        }

        // A document the store would refuse (FIXER LIST F10). Applied after `seed` so a case can
        // use both.
        if (seedRaw is not null)
        {
            harness.SeedRaw(seedRaw);
        }

        harness.ViewModel = new LibraryTabViewModel(
            () =>
            {
                Interlocked.Increment(ref harness.Loads);
                return harness.LoadThrows is null ? harness.Store.GetGeneral() : throw harness.LoadThrows;
            },
            mutate =>
            {
                harness.SaveEntered.Set();
                harness.SaveRelease?.Wait(Budget);
                return harness.Store.MutateGeneral(current =>
            {
                var next = mutate(current);
                lock (harness.Saves)
                {
                    harness.Saves.Add(next);
                }

                if (harness.SaveThrows is { } failure)
                {
                    // Thrown from inside the store's critical section, which is where a refusal
                    // actually happens: nothing is written and no event is raised.
                    throw failure;
                }

                return next;
            });
            },
            (options, token) =>
            {
                Interlocked.Increment(ref harness.ScanRuns);
                lock (harness.ScanOptions)
                {
                    harness.ScanOptions.Add(options);
                }

                lock (harness.ScanOnUiThread)
                {
                    if (onUiThread is not null)
                    {
                        harness.ScanOnUiThread.Add(onUiThread());
                    }
                }

                harness.ScanEntered.Set();
                harness.ScanRelease?.Wait(Budget, token);
                return Task.FromResult(Outcome());
            },
            () => Interlocked.Increment(ref harness.Cancels),
            scanStatus: scanStatus,
            post: post ?? (action => action()),
            logger: harness.Logger,
            subscribeGeneralChanged: followGeneralChanged
                ? handler => harness.Store.GeneralChanged += handler
                : null,
            unsubscribeGeneralChanged: followGeneralChanged
                ? handler => harness.Store.GeneralChanged -= handler
                : null);

        return harness;
    }

    /// <summary>A completed manual run, as <c>ScanCoordinator.RunAsync</c> reports one.</summary>
    public static ScanRunOutcome Outcome() => new(1, "complete", 0, 0, 0, 0, 0, 0, 0);

    /// <summary>One name rule, defaulting to the shape the editor's "Add rule" produces.
    /// </summary>
    public static NameRule Rule(
        string id = "rule-1",
        string action = "exclude",
        string type = "glob",
        string pattern = "*_bad.fits",
        string target = "file",
        bool enabled = true) => new()
        {
            Id = id,
            Action = action,
            Type = type,
            Pattern = pattern,
            Target = target,
            Enabled = enabled,
        };
}
