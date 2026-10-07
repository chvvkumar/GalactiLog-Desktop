using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 18.1 "Coordinator": the pending flag, cancellation, the progress envelope and its
// throttle, ScanFinished, and the targeted (watcher) entry point. Real files on disk, a real
// database, and a network stub that throws if anything reaches for it -- every fixture frame
// here carries no OBJECT, so no scan in this class has any reason to resolve a target.
//
// This is tests/**, outside FileSafetyTest's src/** scan, so writing fixture files here is
// fine.
public class ScanCoordinatorTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly SettingsStore _settings;
    private readonly TargetResolver _resolver;
    private readonly ScanRunRepository _scanRuns;
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-coordinator-").FullName;

    public ScanCoordinatorTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        _resolver = new TargetResolver(
            _db.ConnectionString, StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(new ThrowingHandler()), new SesameClient(new ThrowingHandler()));
        _scanRuns = new ScanRunRepository(_db.ConnectionString);
        SaveRoots();
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    // ---- fixtures ------------------------------------------------------------------

    private const int FitsBlockSize = 2880;

    // `commentCards` pads the header: 40 of them push it into a second 2880-byte block, which is
    // how a case changes a file's size without changing what the reader makes of it.
    private static byte[] MinimalFits(string imageType = "LIGHT", int commentCards = 0)
    {
        var cards = new[]
        {
            "SIMPLE  =                    T",
            "BITPIX  =                   16",
            "NAXIS   =                    0",
            $"IMAGETYP= '{imageType,-8}'",
            "DATE-OBS= '2025-03-15T02:00:00'",
            "EXPTIME =                300.0",
        };
        return Blocks([.. cards, .. Enumerable.Repeat("COMMENT padding", commentCards), "END"]);
    }

    // A frame whose parse throws, with nothing test-only about it: DATE-OBS at DateTime.MinValue
    // and SITELONG supplying a longitude, so spec 8.2's imaging-night shift underflows and
    // DateTime.op_Subtraction raises ArgumentOutOfRangeException inside ScanRecordParser.Parse.
    // The longitude rides in the header rather than in settings, so nothing outside this file is
    // configured and no real site is named.
    private static byte[] ThrowingFits()
    {
        var cards = new[]
        {
            "SIMPLE  =                    T",
            "BITPIX  =                   16",
            "NAXIS   =                    0",
            "IMAGETYP= 'LIGHT   '",
            "DATE-OBS= '0001-01-01T00:00:00'",
            "SITELONG=                    0",
            "EXPTIME =                300.0",
            "END",
        };
        return Blocks(cards);
    }

    private static byte[] Blocks(string[] cards)
    {
        var text = new StringBuilder();
        foreach (var card in cards) text.Append(card.PadRight(80));
        var blocks = (text.Length + FitsBlockSize - 1) / FitsBlockSize;
        return Encoding.ASCII.GetBytes(text.ToString().PadRight(blocks * FitsBlockSize));
    }

    private string WriteFrame(string name, string imageType = "LIGHT")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, MinimalFits(imageType));
        return path;
    }

    private string WriteThrowingFrame(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, ThrowingFits());
        return path;
    }

    private void SaveRoots(ScanFilterConfig? filters = null)
        => _settings.SaveGeneral(new GeneralSettings
        {
            ScanRoots = [_root],
            ScanFilters = filters ?? ScanFilterConfig.Empty,
        });

    private ScanCoordinator MakeCoordinator()
        => new(_db.ConnectionString, _settings, _resolver, _scanRuns, NullLogger<ScanCoordinator>.Instance);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"network access attempted: {request.RequestUri}");
    }

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    // The fault injector for every "a scan blew up" test: drop the table EmitScanStarted
    // writes to. It fails inside the run, after the scan_runs row exists, through a real code
    // path, and needs no seam, subclass or test-only hook in production code -- ScanRunRepository
    // and ScanCoordinator are both sealed, and the throwing-ProgressChanged injector this class
    // used before is no longer available now that RaiseProgress guards its subscribers.
    private void BreakActivityEventWrites()
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        context.Database.ExecuteSqlRaw("DROP TABLE activity_events");
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(60), $"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    // ---- task vocabulary (spec 10.4) -----------------------------------------------

    [Fact]
    public void TaskVocabulary_IsExactlyTheTenNames_InOrder()
    {
        // Spec 10.4's closed set, in the spec table's own order. Phase 15A inserted
        // `phd2_ingest` and `phd2_correlate` between `prune_orphans` and `dedup`, which is
        // where spec 10.3's numbered list puts the guide-log pass: after the header pass and
        // its orphan prune, before duplicate detection. Phase 18 inserted `mosaic_detection`
        // after `dedup` (spec 10.3 step 6).
        Assert.Equal(
            new[]
            {
                "discovery", "classify", "ingest", "prune_orphans", "phd2_ingest", "phd2_correlate",
                "dedup", "mosaic_detection", "ref_thumbnails", "prune_activity",
            },
            ScanTaskNames.All);
    }

    [Fact]
    public void PruneOrphansAndPruneActivity_AreDistinctNames()
    {
        Assert.NotEqual(ScanTaskNames.PruneOrphans, ScanTaskNames.PruneActivity);
        Assert.Equal(ScanTaskNames.All.Count, ScanTaskNames.All.Distinct().Count());
    }

    [Fact]
    public void ScanProgress_PercentIsDerived_AndZeroWhenNoFixedTotal()
    {
        Assert.Equal(25.0, new ScanProgress("ingest", 1, 4, "").Percent);
        Assert.Equal(0.0, new ScanProgress("discovery", 17, 0, "").Percent);
    }

    // ---- full scan -----------------------------------------------------------------

    [Fact]
    public async Task RunAsync_EmptyRoot_CompletesWithZeroCounts_ExitStateComplete()
    {
        var coordinator = MakeCoordinator();

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.NotNull(outcome.RunId);
        Assert.Equal(0, outcome.Discovered);
        Assert.Equal(0, outcome.NewFiles);
        Assert.Equal(0, outcome.ChangedFiles);
        Assert.Equal(0, outcome.Completed);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(0, outcome.SkippedCalibration);
        Assert.Equal(0, outcome.Removed);

        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal("complete", run.State);
        Assert.Equal("manual", run.Trigger);
        Assert.NotNull(run.FinishedAt);
    }

    // The reader count is ScanPipeline's ([2, 8]); what the coordinator must guarantee is
    // that the row count is exactly the discovered frame count regardless of how many
    // readers this machine spawns. Asserting the literal thread count would only re-test
    // ScanPipeline on a machine-dependent number.
    [Fact]
    public async Task RunAsync_ReaderCountClampedBetween2And8_ProducesExactlyOneRowPerFile()
    {
        Assert.InRange(ScanPipeline.ReaderCount, 2, 8);
        for (var i = 0; i < 12; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();

        var outcome = await coordinator.RunAsync(ScanTrigger.Cli, null, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.Equal(12, outcome.Discovered);
        Assert.Equal(12, outcome.NewFiles);
        Assert.Equal(0, outcome.ChangedFiles);
        Assert.Equal(12, outcome.Completed);

        using var context = OpenRead();
        Assert.Equal(12, context.Images.Count());
        Assert.Equal("cli", context.ScanRuns.Single().Trigger);
    }

    [Fact]
    public async Task RunAsync_SecondScan_ReportsUnchangedFilesAsNeitherNewNorChanged()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        var second = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, second.Discovered);
        Assert.Equal(0, second.NewFiles);
        Assert.Equal(0, second.ChangedFiles);
        Assert.Equal(0, second.Completed);
        using var context = OpenRead();
        Assert.Equal(1, context.Images.Count());
    }

    [Fact]
    public async Task RunAsync_RootsOverride_ScansOnlyTheGivenPath()
    {
        WriteFrame("in-root.fits");
        var other = Directory.CreateTempSubdirectory("galactilog-coordinator-alt-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(other, "elsewhere.fits"), MinimalFits());
            var coordinator = MakeCoordinator();

            var outcome = await coordinator.RunAsync(ScanTrigger.Cli, new[] { other }, CancellationToken.None);

            Assert.Equal(1, outcome.Discovered);
            using var context = OpenRead();
            Assert.Equal("elsewhere.fits", Path.GetFileName(context.Images.Single().FilePath));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    // Regression: filters are validated against the CONFIGURED roots. Validating them against
    // an override rejected every configuration carrying an exclude path, because an exclude
    // path is required to sit under a configured scan root.
    [Fact]
    public async Task RunAsync_RootsOverride_WithExcludePathsConfigured_DoesNotThrow()
    {
        var excluded = Directory.CreateDirectory(Path.Combine(_root, "excluded")).FullName;
        SaveRoots(new ScanFilterConfig { ExcludePaths = [excluded] });
        var other = Directory.CreateTempSubdirectory("galactilog-coordinator-alt-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(other, "elsewhere.fits"), MinimalFits());
            var coordinator = MakeCoordinator();

            var outcome = await coordinator.RunAsync(ScanTrigger.Cli, new[] { other }, CancellationToken.None);

            Assert.Equal("complete", outcome.State);
            Assert.Equal(1, outcome.Discovered);
            using var context = OpenRead();
            Assert.Equal("elsewhere.fits", Path.GetFileName(context.Images.Single().FilePath));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    // Regression: EffectiveRoots(override) silently returns include_paths whenever they are
    // set, so a CLI override used to scan the include paths instead of the path it was given.
    [Fact]
    public async Task RunAsync_RootsOverride_WithIncludePathsConfigured_WalksTheOverrideNotTheIncludePaths()
    {
        var included = Directory.CreateDirectory(Path.Combine(_root, "included")).FullName;
        File.WriteAllBytes(Path.Combine(included, "in-include-path.fits"), MinimalFits());
        SaveRoots(new ScanFilterConfig { IncludePaths = [included] });
        var other = Directory.CreateTempSubdirectory("galactilog-coordinator-alt-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(other, "elsewhere.fits"), MinimalFits());
            var coordinator = MakeCoordinator();

            var outcome = await coordinator.RunAsync(ScanTrigger.Cli, new[] { other }, CancellationToken.None);

            // The override is what gets walked. Nothing is ingested here because the file
            // found there is then excluded by the configured include_paths narrowing -- but
            // the include-path frame is emphatically NOT scanned, which is the bug.
            Assert.Equal("complete", outcome.State);
            Assert.Equal(0, outcome.Discovered);
            using var context = OpenRead();
            Assert.Empty(context.Images);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    // ---- skipped_files (spec 5.21, spec 10.3 step 2) ---------------------------------------
    // Every fixture here is a DARK with the default include_calibration of false, so the header
    // pass skips it. The discriminating figures are new_files and skipped_calibration on the
    // SECOND run: before the table existed both were 1 on every scan, forever.

    [Fact]
    public async Task RunAsync_SkippedCalibrationFrame_IsRecordedAndNotReadAgainOnTheNextRun()
    {
        var dark = WriteFrame("dark.fits", "DARK");
        var coordinator = MakeCoordinator();

        var first = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        var second = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, first.NewFiles);
        Assert.Equal(1, first.SkippedCalibration);
        Assert.Equal(0, second.NewFiles);
        Assert.Equal(0, second.SkippedCalibration);
        Assert.Equal(0, second.Completed);
        using var context = OpenRead();
        var row = Assert.Single(context.SkippedFiles);
        var info = new FileInfo(dark);
        Assert.Equal(info.Length, row.FileSize);
        Assert.Equal((info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds, row.FileMtime, 3);
        Assert.Equal("calibration", row.Reason);
        Assert.Empty(context.Images);
    }

    [Fact]
    public async Task RunAsync_IncludeCalibrationAfterASkip_IngestsTheFrameAndDropsItsSkippedRow()
    {
        WriteFrame("dark.fits", "DARK");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: false));

        // A failure on new_files means the skipped row reached a known set that should have left
        // it out, so turning calibration on ingests nothing.
        Assert.Equal(1, outcome.NewFiles);
        Assert.Equal(1, outcome.Completed);
        using var context = OpenRead();
        Assert.Single(context.Images);
        Assert.Empty(context.SkippedFiles);
    }

    [Fact]
    public async Task RunAsync_SkippedFrameRewrittenToANewSize_IsReadAgainAndTheRowCarriesTheNewSize()
    {
        var dark = WriteFrame("dark.fits", "DARK");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        File.WriteAllBytes(dark, MinimalFits("DARK", commentCards: 40));

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, outcome.ChangedFiles);
        Assert.Equal(1, outcome.SkippedCalibration);
        using var context = OpenRead();
        Assert.Equal(new FileInfo(dark).Length, Assert.Single(context.SkippedFiles).FileSize);
    }

    // Spec 10.3 step 4 for skipped_files: pruned under the discovered-set rule, held back under
    // the zero-discovery guard. The LIGHT frame is what makes the first half reachable: with the
    // DARK alone, deleting it would empty the root and the guard would hold the row back. The
    // second half is a calibration-only root: a skipped row, no images row, nothing discovered.
    [Fact]
    public async Task RunAsync_SkippedRowForADeletedFile_IsPruned_UnlessTheRootDiscoveredNothing()
    {
        var dark = WriteFrame("dark.fits", "DARK");
        var light = WriteFrame("light.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        File.Delete(dark);
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using (var context = OpenRead())
        {
            Assert.Empty(context.SkippedFiles);
        }

        using (var seed = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            seed.Images.RemoveRange(seed.Images);
            seed.SkippedFiles.Add(new SkippedFile
            {
                FilePath = dark, FileSize = 1, FileMtime = 1, Reason = "calibration", RecordedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }
        File.Delete(light);
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // A failure here means an unmounted share holding only calibration frames would wipe its
        // rows, and the next scan with it back would read every one of them again.
        using var after = OpenRead();
        Assert.Single(after.SkippedFiles);
    }

    [Fact]
    public async Task RunTargetedAsync_SkippedCalibrationFrame_IsNotReadAgainOnTheSecondRun()
    {
        var dark = WriteFrame("dark.fits", "DARK");
        var coordinator = MakeCoordinator();

        var first = await coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { dark }, CancellationToken.None);
        var second = await coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { dark }, CancellationToken.None);

        Assert.Equal(1, first.SkippedCalibration);
        Assert.Equal(0, second.NewFiles);
        Assert.Equal(0, second.Completed);
    }

    // Regression: before migration 0003 images.file_path was uniquely indexed BINARY, so two
    // rows differing only by case were legal, and an OrdinalIgnoreCase ToDictionary threw on
    // the second one. 0003's NOCASE collation stops NEW pairs being created, but a database
    // written before it can still hold one, so the known-file map keeps its indexer-assignment
    // guard. The unique index is dropped here to reproduce exactly that legacy shape.
    [Fact]
    public async Task RunAsync_KnownFilesDifferingOnlyByCase_DoesNotThrow()
    {
        var path = WriteFrame("a.fits");
        using (var seed = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            seed.Database.ExecuteSqlRaw("DROP INDEX IX_images_file_path");
            seed.Images.Add(new Image { Id = Guid.NewGuid(), FilePath = path.ToLowerInvariant(), FileName = "a.fits" });
            seed.Images.Add(new Image { Id = Guid.NewGuid(), FilePath = path.ToUpperInvariant(), FileName = "A.FITS" });
            seed.SaveChanges();
        }
        var coordinator = MakeCoordinator();

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.Equal(1, outcome.Discovered);
    }

    // ---- the resolution lease (Phase 7 fixer item 1) ---------------------------------

    [Fact]
    public async Task RunAsync_WhileTheResolutionLeaseIsHeld_RefusesAndStartsNoScan()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();

        var lease = coordinator.TryBeginResolution();
        Assert.NotNull(lease);
        Assert.True(coordinator.ResolutionInProgress);

        var refused = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(ScanRunOutcome.AlreadyRunning, refused);
        using (var context = OpenRead()) Assert.Empty(context.ScanRuns);

        // Releasing it opens the gate again, and no pending flag queued a second scan behind it.
        lease!.Dispose();
        Assert.False(coordinator.ResolutionInProgress);
        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        Assert.Equal(1, outcome.NewFiles);
        using (var context = OpenRead()) Assert.Single(context.ScanRuns);
    }

    [Fact]
    public async Task TryBeginResolution_WhileAScanIsRunning_ReturnsNull()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();

        // Asked from inside the run's own first progress event, so it is provably concurrent.
        // Captured, never asserted in the handler: RaiseProgress swallows subscriber exceptions.
        IDisposable? duringScan = null;
        var asked = false;
        coordinator.ProgressChanged += (_, _) =>
        {
            if (asked) return;
            asked = true;
            duringScan = coordinator.TryBeginResolution();
        };

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.True(asked);
        Assert.Null(duringScan);
        Assert.False(coordinator.ResolutionInProgress);

        // And once the scan is over the lease issues again.
        Assert.NotNull(coordinator.TryBeginResolution());
    }

    [Fact]
    public void ResolutionStateChanged_FiresOnBothEdges()
    {
        var coordinator = MakeCoordinator();
        var states = new List<bool>();
        coordinator.ResolutionStateChanged += (_, _) => states.Add(coordinator.ResolutionInProgress);

        var lease = coordinator.TryBeginResolution();
        lease!.Dispose();
        lease.Dispose();

        // Twice, not three times: disposing a released lease is a no-op.
        Assert.Equal([true, false], states);
    }

    // ---- pending flag (spec 10, SCAN_PENDING_KEY port) -------------------------------

    [Fact]
    public async Task RunAsync_SecondCallWhileRunning_SetsPendingFlag_StartsExactlyOneMoreScanAfterFirstFinishes()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();

        var finished = 0;
        coordinator.ScanFinished += (_, _) => Interlocked.Increment(ref finished);

        // Two overlapping triggers, fired from inside the first run's own first progress
        // event so they are provably concurrent with it. They must collapse into ONE
        // follow-up scan, not two.
        // Captured, never asserted inside the handler: RaiseProgress swallows subscriber
        // exceptions, so an assertion here would be logged instead of failing the test.
        var overlapped = new List<ScanRunOutcome>();
        var runningDuringOverlap = false;
        var reentered = false;
        coordinator.ProgressChanged += (_, _) =>
        {
            if (reentered) return;
            reentered = true;
            runningDuringOverlap = coordinator.IsRunning;
            overlapped.Add(coordinator.RunAsync(ScanTrigger.Watcher, null, CancellationToken.None).Result);
            overlapped.Add(coordinator.RunAsync(ScanTrigger.Scheduler, null, CancellationToken.None).Result);
        };

        var first = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("complete", first.State);
        Assert.True(runningDuringOverlap);
        Assert.Equal(2, overlapped.Count);
        Assert.All(overlapped, o => Assert.Same(ScanRunOutcome.AlreadyRunning, o));

        await WaitFor(() => Volatile.Read(ref finished) >= 2, "the pending follow-up scan to finish");
        await Task.Delay(200); // any stacked second follow-up would have started by now

        Assert.Equal(2, Volatile.Read(ref finished));
        using var context = OpenRead();
        var runs = context.ScanRuns.OrderBy(r => r.Id).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal("manual", runs[0].Trigger);
        // The pending flag records the LAST trigger that arrived while the scan ran.
        Assert.Equal("scheduler", runs[1].Trigger);
        Assert.All(runs, r => Assert.Equal("complete", r.State));
        Assert.False(coordinator.IsRunning);
    }

    // ---- cancellation (spec 10.5) ---------------------------------------------------

    [Fact]
    public async Task RunAsync_Cancelled_RecordsStateCancelled_SkipsOrphanPruning()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();

        var tasks = new List<string>();
        coordinator.ProgressChanged += (_, p) =>
        {
            lock (tasks) tasks.Add(p.Task);
            // Cancel at the ingest phase boundary: deterministic, and after classification
            // has produced its counts.
            if (p.Task == ScanTaskNames.Ingest) coordinator.Cancel();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("cancelled", outcome.State);
        Assert.Equal(6, outcome.Discovered);
        Assert.Equal(6, outcome.NewFiles);
        Assert.Equal(0, outcome.Removed);

        // Orphan pruning and every post-pass are skipped entirely on a cancelled run.
        lock (tasks)
        {
            Assert.DoesNotContain(ScanTaskNames.PruneOrphans, tasks);
            Assert.DoesNotContain(ScanTaskNames.Dedup, tasks);
            Assert.DoesNotContain(ScanTaskNames.MosaicDetection, tasks);
            Assert.DoesNotContain(ScanTaskNames.RefThumbnails, tasks);
            Assert.DoesNotContain(ScanTaskNames.PruneActivity, tasks);
        }

        using var context = OpenRead();
        Assert.Equal("cancelled", context.ScanRuns.Single().State);
    }

    [Fact]
    public async Task RunAsync_CallerToken_CancelsTheRunToo()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();
        using var cts = new CancellationTokenSource();
        coordinator.ProgressChanged += (_, p) =>
        {
            if (p.Task == ScanTaskNames.Ingest) cts.Cancel();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, cts.Token);

        Assert.Equal("cancelled", outcome.State);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task Cancel_WhenIdle_TwiceOver_AndAfterAFinishedScan_DoesNothing()
    {
        var coordinator = MakeCoordinator();

        coordinator.Cancel();
        coordinator.Cancel();
        Assert.False(coordinator.IsRunning);

        WriteFrame("a.fits");
        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        Assert.Equal("complete", outcome.State);

        // After the run, the per-run CancellationTokenSource has been disposed: cancelling
        // again must not reach it (ObjectDisposedException) or resurrect any state.
        coordinator.Cancel();
        coordinator.Cancel();
        Assert.False(coordinator.IsRunning);

        var second = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        Assert.Equal("complete", second.State);
    }

    [Fact]
    public async Task Cancel_TwiceDuringOneRun_CancelsOnceAndDoesNotThrow()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();
        coordinator.ProgressChanged += (_, p) =>
        {
            if (p.Task != ScanTaskNames.Ingest) return;
            coordinator.Cancel();
            coordinator.Cancel();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("cancelled", outcome.State);
        Assert.False(coordinator.IsRunning);
    }

    // ---- shutdown drain (spec 10.5) --------------------------------------------------

    [Fact]
    public async Task WaitForIdleAsync_WhenAlreadyIdle_ReturnsTrueImmediately()
    {
        var coordinator = MakeCoordinator();
        Assert.True(await coordinator.WaitForIdleAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task WaitForIdleAsync_AfterCancel_ReturnsTrueWithinTheBudget()
    {
        for (var i = 0; i < 20; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();

        var busy = false;
        bool? midRunIdle = null;
        coordinator.ProgressChanged += (_, p) =>
        {
            if (p.Task != ScanTaskNames.Ingest || busy) return;
            busy = true;
            // Mid-run, WaitForIdleAsync must NOT claim idle. TimeSpan.Zero returns
            // synchronously, so the scan is not blocked by it. Captured, not asserted here:
            // RaiseProgress now swallows subscriber exceptions, so an assertion inside this
            // handler would be logged rather than failing the test.
            midRunIdle = coordinator.WaitForIdleAsync(TimeSpan.Zero).Result;
            coordinator.Cancel();
        };

        var run = coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.True(await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(5)));
        Assert.False(midRunIdle);
        Assert.False(coordinator.IsRunning);
        Assert.Equal("cancelled", (await run).State);
    }

    [Fact]
    public async Task WaitForIdleAsync_CoversThePendingFollowUpScan()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();
        var reentered = false;
        coordinator.ProgressChanged += (_, _) =>
        {
            if (reentered) return;
            reentered = true;
            coordinator.RunAsync(ScanTrigger.Scheduler, null, CancellationToken.None).Wait();
        };

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // The follow-up may not even have reached _running yet when the first run returns;
        // idle must mean "the follow-up is done too", not "no scan happens to be running".
        Assert.True(await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(60)));
        using var context = OpenRead();
        Assert.Equal(2, context.ScanRuns.Count());
        Assert.All(context.ScanRuns.ToList(), r => Assert.NotNull(r.FinishedAt));
    }

    // ---- failure (ruling Q6) --------------------------------------------------------

    [Fact]
    public async Task RunAsync_UnhandledFailure_RecordsStateFailedWithErrorText_ThenRethrows()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        BreakActivityEventWrites();

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal("failed", run.State);
        Assert.NotNull(run.ErrorText);
        Assert.NotEmpty(run.ErrorText);
        Assert.NotNull(run.FinishedAt);
        Assert.NotNull(ex);
    }

    // Regression: EmitScanStarted writes a row, so it can fail, and it runs after the
    // scan_runs row exists. Outside the try it would leave state = "running" forever.
    [Fact]
    public async Task RunAsync_EmitScanStartedFails_RecordsStateFailed_NotLeftRunning()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        BreakActivityEventWrites();

        await Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal("failed", run.State);
        Assert.NotNull(run.FinishedAt);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public async Task RunTargetedAsync_EmitScanStartedFails_RecordsStateFailed_NotLeftRunning()
    {
        var a = WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        BreakActivityEventWrites();

        await Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { a }, CancellationToken.None));

        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal("failed", run.State);
        Assert.NotNull(run.FinishedAt);
        Assert.False(coordinator.IsRunning);
    }

    // ---- ScanFinished ---------------------------------------------------------------

    [Fact]
    public async Task RunAsync_ScanFinished_FiresExactlyOnce_RegardlessOfOutcome()
    {
        WriteFrame("a.fits");

        var completed = MakeCoordinator();
        var completedCount = 0;
        completed.ScanFinished += (_, _) => Interlocked.Increment(ref completedCount);
        await completed.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        Assert.Equal(1, completedCount);

        var cancelled = MakeCoordinator();
        var cancelledCount = 0;
        cancelled.ScanFinished += (_, _) => Interlocked.Increment(ref cancelledCount);
        cancelled.ProgressChanged += (_, p) =>
        {
            if (p.Task == ScanTaskNames.Ingest) cancelled.Cancel();
        };
        var cancelledOutcome = await cancelled.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        Assert.Equal("cancelled", cancelledOutcome.State);
        Assert.Equal(1, cancelledCount);

        var failing = MakeCoordinator();
        var failedCount = 0;
        failing.ScanFinished += (_, _) => Interlocked.Increment(ref failedCount);
        BreakActivityEventWrites();
        await Assert.ThrowsAnyAsync<Exception>(
            () => failing.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));
        Assert.Equal(1, failedCount);
    }

    [Fact]
    public async Task ScanFinished_ThrowingSubscriber_DoesNotMaskTheFailure_OrDropThePendingScan()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();
        coordinator.ScanFinished += (_, _) => throw new InvalidOperationException("subscriber boom");

        var reentered = false;
        coordinator.ProgressChanged += (_, _) =>
        {
            if (reentered) return;
            reentered = true;
            coordinator.RunAsync(ScanTrigger.Watcher, null, CancellationToken.None).Wait();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // The subscriber's exception never reaches the caller...
        Assert.Equal("complete", outcome.State);

        // ...and the pending follow-up still runs, exactly once.
        Assert.True(await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(60)));
        using var context = OpenRead();
        Assert.Equal(2, context.ScanRuns.Count());
    }

    [Fact]
    public async Task ScanFinished_ThrowingSubscriber_LetsTheOriginalFailureSurface()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        coordinator.ScanFinished += (_, _) => throw new InvalidOperationException("subscriber boom");
        BreakActivityEventWrites();

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        Assert.IsNotType<InvalidOperationException>(ex);
        using var context = OpenRead();
        Assert.Equal("failed", context.ScanRuns.Single().State);
    }

    [Fact]
    public async Task ProgressChanged_ThrowingSubscriber_DoesNotFailTheScan()
    {
        for (var i = 0; i < 4; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();
        coordinator.ProgressChanged += (_, _) => throw new InvalidOperationException("subscriber boom");

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.Equal(4, outcome.Completed);
        using var context = OpenRead();
        Assert.Equal("complete", context.ScanRuns.Single().State);
        Assert.Equal(4, context.Images.Count());
    }

    // ---- progress throttle (spec 10.4) ----------------------------------------------

    [Fact]
    public void RunAsync_ProgressChanged_ThrottledToAtMostTenPerSecond_TerminalEventAlwaysDelivered()
    {
        var coordinator = MakeCoordinator();
        var seen = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, p) => seen.Add(p);

        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            coordinator.RaiseProgress(ScanTaskNames.Ingest, i, 1000, "tick");
        }
        coordinator.RaiseProgress(ScanTaskNames.Ingest, 1000, 1000, "Ingest complete", force: true);
        elapsed.Stop();

        var nonTerminal = seen.Count(p => p.Message == "tick");

        // The bound, not a stopwatch assertion: one event is always allowed through
        // immediately, then at most one more per elapsed 100 ms window.
        var windows = (int)(elapsed.ElapsedMilliseconds / 100);
        Assert.InRange(nonTerminal, 1, 2 + windows);

        // The terminal event is never throttled away, even though it lands microseconds
        // after the last suppressed tick.
        Assert.Equal("Ingest complete", seen[^1].Message);
        Assert.Equal(1000, seen[^1].Step);
    }

    [Fact]
    public void RaiseProgress_ConsecutiveForcedEvents_AreNeverDropped()
    {
        var coordinator = MakeCoordinator();
        var seen = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, p) => seen.Add(p);

        coordinator.RaiseProgress(ScanTaskNames.Classify, 1, 1, "a", force: true);
        coordinator.RaiseProgress(ScanTaskNames.Ingest, 0, 1, "b", force: true);
        coordinator.RaiseProgress(ScanTaskNames.Ingest, 1, 1, "c", force: true);

        Assert.Equal(new[] { "a", "b", "c" }, seen.Select(p => p.Message));
    }

    [Fact]
    public async Task RunAsync_EmitsEveryPhaseOfTheVocabulary_InOrder()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        var order = new List<string>();
        coordinator.ProgressChanged += (_, p) =>
        {
            lock (order)
            {
                if (order.Count == 0 || order[^1] != p.Task) order.Add(p.Task);
            }
        };

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        lock (order) Assert.Equal(ScanTaskNames.All, order);
    }

    // ---- targeted ingest (spec 10.7) -------------------------------------------------

    [Fact]
    public async Task RunTargetedAsync_IngestsOnlyTheGivenFiles()
    {
        var a = WriteFrame("a.fits");
        WriteFrame("b.fits");
        var coordinator = MakeCoordinator();

        var outcome = await coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { a }, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.Equal(1, outcome.Discovered);
        Assert.Equal(1, outcome.NewFiles);
        Assert.Equal(1, outcome.Completed);
        using var context = OpenRead();
        Assert.Equal("a.fits", Path.GetFileName(context.Images.Single().FilePath));
        Assert.Equal("watcher", context.ScanRuns.Single().Trigger);
    }

    // Regression: an unchanged file used to be counted as changed and re-ingested just
    // because the watcher reported a touch.
    [Fact]
    public async Task RunTargetedAsync_UnchangedFile_IsNeitherCountedNorReIngested()
    {
        var a = WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        var outcome = await coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { a }, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.Equal(1, outcome.Discovered);   // the watcher did report one file
        Assert.Equal(0, outcome.NewFiles);
        Assert.Equal(0, outcome.ChangedFiles);
        Assert.Equal(0, outcome.Completed);    // and none of it was re-ingested
        using var context = OpenRead();
        Assert.Equal(1, context.Images.Count());
    }

    [Fact]
    public async Task RunTargetedAsync_ScanStartedDetails_CarryRootsAndFileCount_NotTheFileList()
    {
        var a = WriteFrame("a.fits");
        var coordinator = MakeCoordinator();

        await coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { a }, CancellationToken.None);

        using var context = OpenRead();
        var details = context.ActivityEvents.Single(e => e.EventType == "scan_started").Details!;
        using var payload = JsonDocument.Parse(details);
        Assert.Equal("watcher", payload.RootElement.GetProperty("trigger").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("file_count").GetInt32());
        var roots = payload.RootElement.GetProperty("roots").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(new[] { _root }, roots);
        Assert.DoesNotContain("a.fits", details);
    }

    [Fact]
    public async Task RunTargetedAsync_SkipsOrphanPruning_EvenWhenFilesAreASubsetOfKnownRoot()
    {
        var a = WriteFrame("a.fits");
        WriteFrame("b.fits");
        WriteFrame("c.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        var tasks = new List<string>();
        coordinator.ProgressChanged += (_, p) => { lock (tasks) tasks.Add(p.Task); };

        // One path out of three known files: this must never be read as "the other two are
        // gone from disk".
        var outcome = await coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { a }, CancellationToken.None);

        Assert.Equal(0, outcome.Removed);
        lock (tasks) Assert.DoesNotContain(ScanTaskNames.PruneOrphans, tasks);
        using var context = OpenRead();
        Assert.Equal(3, context.Images.Count());
    }

    [Fact]
    public async Task RunTargetedAsync_FiltersThroughScanFilterConfig()
    {
        var good = WriteFrame("keep.fits");
        var excluded = WriteFrame("drafts_skipme.fits");
        SaveRoots(new ScanFilterConfig
        {
            NameRules =
            [
                new NameRule { Id = "r1", Action = "exclude", Type = "glob", Pattern = "drafts_*", Target = "file" },
            ],
        });
        var coordinator = MakeCoordinator();

        var outcome = await coordinator.RunTargetedAsync(
            ScanTrigger.Watcher, new[] { good, excluded }, CancellationToken.None);

        Assert.Equal(1, outcome.Discovered);
        using var context = OpenRead();
        Assert.Equal("keep.fits", Path.GetFileName(context.Images.Single().FilePath));
    }

    [Fact]
    public async Task RunTargetedAsync_PathOutsideEveryRoot_IsNeverIngested()
    {
        var outside = Directory.CreateTempSubdirectory("galactilog-coordinator-outside-").FullName;
        try
        {
            var path = Path.Combine(outside, "sneaky.fits");
            File.WriteAllBytes(path, MinimalFits());
            var coordinator = MakeCoordinator();

            var outcome = await coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { path }, CancellationToken.None);

            Assert.Equal(0, outcome.Discovered);
            using var context = OpenRead();
            Assert.Empty(context.Images);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task RunTargetedAsync_VanishedOrUnsupportedPath_IsSkippedNotFatal()
    {
        var good = WriteFrame("a.fits");
        var vanished = Path.Combine(_root, "gone.fits");
        var unsupported = Path.Combine(_root, "notes.txt");
        File.WriteAllText(unsupported, "not a frame");
        var coordinator = MakeCoordinator();

        var outcome = await coordinator.RunTargetedAsync(
            ScanTrigger.Watcher, new[] { good, vanished, unsupported }, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.Equal(1, outcome.Discovered);
        using var context = OpenRead();
        Assert.Equal(1, context.Images.Count());
    }

    [Fact]
    public async Task RunTargetedAsync_WhileRunning_SetsPendingFlag()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();
        ScanRunOutcome? overlapped = null;
        var reentered = false;
        coordinator.ProgressChanged += (_, _) =>
        {
            if (reentered) return;
            reentered = true;
            overlapped = coordinator.RunTargetedAsync(
                ScanTrigger.Watcher, Array.Empty<string>(), CancellationToken.None).Result;
        };

        var finished = 0;
        coordinator.ScanFinished += (_, _) => Interlocked.Increment(ref finished);
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Same(ScanRunOutcome.AlreadyRunning, overlapped);
        Assert.Null(overlapped!.RunId);
        Assert.Equal("pending", overlapped.State);

        // The follow-up is always a FULL scan, whatever kind of call set the flag: a
        // targeted list is stale by the time the current scan ends.
        await WaitFor(() => Volatile.Read(ref finished) >= 2, "the pending follow-up scan to finish");
        using var context = OpenRead();
        Assert.Equal(2, context.ScanRuns.Count());
    }

    // ---- activity parenting ----------------------------------------------------------

    [Fact]
    public async Task RunAsync_WritesScanStartedActivity_AndParentsScanEventsToIt()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();

        await coordinator.RunAsync(ScanTrigger.FirstRun, null, CancellationToken.None);

        using var context = OpenRead();
        var started = context.ActivityEvents.Single(e => e.EventType == "scan_started");
        Assert.Equal("scan", started.Category);
        Assert.Equal("info", started.Severity);
        Assert.Contains("first_run", started.Details);
        Assert.Contains("run_id", started.Details);
        Assert.Equal("first_run", context.ScanRuns.Single().Trigger);
    }

    // ---- orphan pruning and scan-lifecycle activity (spec 10.3 steps 4-5, 10.9) ------

    // Every file under the scan root with its size and last-write time. Equality of two
    // snapshots across a prune is the assertion that pruning is database-only.
    private List<string> SnapshotRoot()
        => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p):O}")
            .ToList();

    // THE RULE, IN CAPITALS: ORPHAN PRUNING DELETES DATABASE ROWS ONLY. IT NEVER DELETES,
    // MOVES OR MODIFIES ANY FILE ON DISK. The one file that disappears here is deleted by
    // the TEST HARNESS, never by product code, and every other entry under the root -- the
    // remaining frames and a non-frame sidecar -- has the same length and timestamp after
    // the rescan that drops its row.
    [Fact]
    public async Task RunAsync_MissingFilesOnRescan_DeletesRowsOnly_NeverTouchesDisk()
    {
        var keep = WriteFrame("keep.fits");
        var alsoKeep = WriteFrame("also-keep.fits");
        var doomed = WriteFrame("doomed.fits");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a frame, and not ours to touch");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        File.Delete(doomed);            // the harness removes the file, the scan removes the row
        var before = SnapshotRoot();

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, outcome.Removed);
        Assert.Equal(before, SnapshotRoot());
        using var context = OpenRead();
        Assert.Equal(
            new[] { alsoKeep, keep }.OrderBy(p => p, StringComparer.Ordinal),
            context.Images.Select(i => i.FilePath).ToList().OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(1, context.ScanRuns.OrderByDescending(r => r.Id).First().Removed);
    }

    [Fact]
    public async Task RunAsync_OrphansPruned_EmitsTheEventWithTheCount()
    {
        // Phase 14B Task 5: three kept frames to one deleted, so the run stays below spec 10.3's
        // 50 percent safety limit and this case keeps its own subject.
        WriteFrame("keep.fits");
        WriteFrame("keep2.fits");
        WriteFrame("keep3.fits");
        var doomed = WriteFrame("doomed.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        File.Delete(doomed);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        var pruned = context.ActivityEvents.Single(e => e.EventType == "orphans_pruned");
        Assert.Equal("scan", pruned.Category);
        Assert.Equal("info", pruned.Severity);
        using var payload = JsonDocument.Parse(pruned.Details!);
        Assert.Equal(1, payload.RootElement.GetProperty("count").GetInt32());
    }

    // Spec 10.3's one guard: a root that previously had rows and discovers nothing this run
    // is treated as an unreachable share, not as an emptied library.
    [Fact]
    public async Task RunAsync_ZeroDiscoveryUnderAKnownRoot_SkipsPruning_AndSaysSoInTheActivityLog()
    {
        var frames = new[] { WriteFrame("a.fits"), WriteFrame("b.fits") };
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        foreach (var frame in frames) File.Delete(frame);   // the whole share "went away"

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(0, outcome.Discovered);
        Assert.Equal(0, outcome.Removed);
        using var context = OpenRead();
        Assert.Equal(2, context.Images.Count());          // the catalogue is NOT emptied
        var skipped = context.ActivityEvents.Single(e => e.EventType == "orphan_prune_skipped");
        Assert.Equal("warning", skipped.Severity);
        using var payload = JsonDocument.Parse(skipped.Details!);
        Assert.Equal(2, payload.RootElement.GetProperty("known_rows").GetInt32());
        Assert.Equal(_root, payload.RootElement.GetProperty("root").GetString());
    }

    [Fact]
    public async Task RunAsync_ScanComplete_EmitsTerminalEvent_WithCountersAndDuration()
    {
        // Phase 14B Task 5: three kept frames to one deleted, so the run stays below spec 10.3's
        // 50 percent safety limit and this case keeps its own subject.
        WriteFrame("keep.fits");
        WriteFrame("keep2.fits");
        WriteFrame("keep3.fits");
        var doomed = WriteFrame("doomed.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        File.Delete(doomed);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        var run = context.ScanRuns.OrderByDescending(r => r.Id).First();
        var complete = context.ActivityEvents
            .Where(e => e.EventType == "scan_complete").OrderByDescending(e => e.Id).First();
        Assert.Equal("info", complete.Severity);
        Assert.Equal("scan", complete.Category);
        Assert.NotNull(complete.DurationMs);
        using var payload = JsonDocument.Parse(complete.Details!);
        Assert.Equal(3, payload.RootElement.GetProperty("discovered").GetInt32());
        Assert.Equal(0, payload.RootElement.GetProperty("new").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("removed").GetInt32());
        Assert.Equal(run.Removed, payload.RootElement.GetProperty("removed").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("error", out _));   // only failures carry it
    }

    // Spec 10.9: "Every sub-event emitted during a scan carries parent_id set to the
    // scan_started event's id, so the activity feed can collapse a scan into one entry."
    [Fact]
    public async Task RunAsync_EveryScanSubEvent_ParentsToTheScanStartedRow()
    {
        // Phase 14B Task 5: three kept frames to one deleted, so the run stays below spec 10.3's
        // 50 percent safety limit and this case keeps its own subject.
        WriteFrame("keep.fits");
        WriteFrame("keep2.fits");
        WriteFrame("keep3.fits");
        var doomed = WriteFrame("doomed.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        File.Delete(doomed);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        var events = context.ActivityEvents.OrderBy(e => e.Id).ToList();
        var started = events.Last(e => e.EventType == "scan_started");

        // Scoped to the scan category on purpose: `activity_pruned` is a `system`-category
        // housekeeping event that is deliberately parentless (it outlives the run that
        // triggered it, and cascading it away with the run would be wrong), so an unscoped
        // assertion here would only pass while nothing happens to age out.
        var afterStart = events.Where(e => e.Id > started.Id && e.Category == "scan").ToList();
        Assert.Contains(afterStart, e => e.EventType == "orphans_pruned");
        Assert.Contains(afterStart, e => e.EventType == "scan_complete");
        Assert.All(afterStart, e => Assert.Equal(started.Id, e.ParentId));
        Assert.Null(started.ParentId);
    }

    [Fact]
    public async Task RunAsync_Cancelled_EmitsScanCancelledAtWarning()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();
        coordinator.ProgressChanged += (_, p) =>
        {
            if (p.Task == ScanTaskNames.Ingest) coordinator.Cancel();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("cancelled", outcome.State);
        using var context = OpenRead();
        var cancelled = context.ActivityEvents.Single(e => e.EventType == "scan_cancelled");
        Assert.Equal("warning", cancelled.Severity);
        Assert.DoesNotContain(context.ActivityEvents.ToList(), e => e.EventType == "orphans_pruned");
    }

    // Ruling Q6: the failure is recorded and reported before the rethrow. This injector drops
    // `images` rather than `activity_events`, so the failure path can still write its event.
    [Fact]
    public async Task RunAsync_UnhandledFailure_EmitsScanFailedWithTheErrorText()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        using (var breaker = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            breaker.Database.ExecuteSqlRaw("DROP TABLE images");
        }

        await Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        using var context = OpenRead();
        var failed = context.ActivityEvents.Single(e => e.EventType == "scan_failed");
        Assert.Equal("error", failed.Severity);
        Assert.Equal(context.ActivityEvents.Single(e => e.EventType == "scan_started").Id, failed.ParentId);
        using var payload = JsonDocument.Parse(failed.Details!);
        Assert.NotEmpty(payload.RootElement.GetProperty("error").GetString()!);
        Assert.Equal("failed", context.ScanRuns.Single().State);
    }

    // Spec 5.12: pruned after each scan (AppHost does the same on application start).
    [Fact]
    public async Task RunAsync_ActivityRetentionAppliedAfterEveryScan()
    {
        WriteFrame("a.fits");
        SeedAncientActivityEvent();
        var coordinator = MakeCoordinator();

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        var events = context.ActivityEvents.ToList();
        Assert.DoesNotContain(events, e => e.EventType == "ancient");
        Assert.Contains(events, e => e.EventType == "activity_pruned");
        Assert.Contains(events, e => e.EventType == "scan_started");
    }

    [Fact]
    public async Task RunTargetedAsync_AlsoPrunesActivityRetention_ButNeverOrphans()
    {
        var a = WriteFrame("a.fits");
        WriteFrame("b.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        SeedAncientActivityEvent();

        var outcome = await coordinator.RunTargetedAsync(ScanTrigger.Watcher, new[] { a }, CancellationToken.None);

        Assert.Equal(0, outcome.Removed);
        using var context = OpenRead();
        Assert.Equal(2, context.Images.Count());
        Assert.DoesNotContain(context.ActivityEvents.ToList(), e => e.EventType == "ancient");
    }

    // Older than general.activity_retention_days (default 90), and parentless, so nothing
    // else rides on its deletion.
    private void SeedAncientActivityEvent()
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        context.ActivityEvents.Add(new Entities.ActivityEvent
        {
            Timestamp = DateTime.UtcNow.AddDays(-120),
            Severity = "info",
            Category = "scan",
            EventType = "ancient",
            Message = "older than general.activity_retention_days",
        });
        context.SaveChanges();
    }

    // ---- fix pass: what the run actually walked is what may be pruned ----------------

    // Review item 1 (critical). include_paths narrows the walk to a subfolder; the rest of
    // the configured root is never visited, so every row under it would look undiscovered.
    // Pruning must follow the effective roots, not the configured ones.
    [Fact]
    public async Task RunAsync_IncludePathsNarrowing_PrunesOnlyInsideTheNarrowedSubtree()
    {
        var current = Path.Combine(_root, "2024");
        Directory.CreateDirectory(Path.Combine(_root, "2023"));
        Directory.CreateDirectory(current);
        var archived = WriteFrame(Path.Combine("2023", "archived.fits"));

        // Phase 14B Task 5: three kept frames under the narrowed root to one deleted, so the run
        // stays below spec 10.3's 50 percent safety limit.
        var keep = WriteFrame(Path.Combine("2024", "keep.fits"));
        var keep2 = WriteFrame(Path.Combine("2024", "keep2.fits"));
        var keep3 = WriteFrame(Path.Combine("2024", "keep3.fits"));
        var doomed = WriteFrame(Path.Combine("2024", "doomed.fits"));
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        // The user narrows the scan to this season's folder, and one of its frames goes away.
        SaveRoots(new ScanFilterConfig { IncludePaths = [current] });
        File.Delete(doomed);

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, outcome.Removed);
        using var context = OpenRead();
        var catalogued = context.Images.Select(i => i.FilePath).ToList();
        Assert.Contains(archived, catalogued);   // never walked this run, never a candidate
        Assert.Contains(keep, catalogued);
        Assert.Contains(keep2, catalogued);
        Assert.Contains(keep3, catalogued);
        Assert.DoesNotContain(doomed, catalogued);
    }

    // The exclude_paths ruling, made explicit in spec 10.3 step 4: an excluded subtree's rows
    // DO leave the catalogue on the next scan (clearing the exclusion and rescanning puts
    // them back), and its FILES ARE NOT TOUCHED -- the whole directory listing, lengths and
    // timestamps included, is identical across the prune.
    [Fact]
    public async Task RunAsync_ExcludedSubfolder_RowsArePruned_ButEveryFileStaysOnDisk()
    {
        var excluded = Path.Combine(_root, "drafts");
        Directory.CreateDirectory(excluded);
        var draft = WriteFrame(Path.Combine("drafts", "draft.fits"));

        // Phase 14B Task 5: three kept frames to one excluded, so the run stays below spec 10.3's
        // 50 percent safety limit.
        var keep = WriteFrame("keep.fits");
        var keep2 = WriteFrame("keep2.fits");
        var keep3 = WriteFrame("keep3.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        SaveRoots(new ScanFilterConfig { ExcludePaths = [excluded] });
        var before = SnapshotRoot();

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, outcome.Removed);
        using var context = OpenRead();
        Assert.Equal(
            new[] { keep, keep2, keep3 }.Order().ToArray(),
            context.Images.Select(i => i.FilePath).ToList().Order().ToArray());
        Assert.Equal(before, SnapshotRoot());
        Assert.True(File.Exists(draft), "the excluded frame must still be on disk");
    }

    // Review item 3: the phase reports rows removed out of rows found missing, not out of
    // itself. OrphanPrunerTests owns the discriminating assertions on the denominator.
    [Fact]
    public async Task RunAsync_PruneOrphansProgress_ReportsRemovedOutOfCandidates()
    {
        // Phase 14B Task 5: two of five missing stays below spec 10.3's 50 percent safety limit,
        // so the denominator is what this case is still about.
        WriteFrame("keep.fits");
        WriteFrame("keep2.fits");
        WriteFrame("keep3.fits");
        var doomed = new[] { WriteFrame("gone1.fits"), WriteFrame("gone2.fits") };
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        foreach (var frame in doomed) File.Delete(frame);

        ScanProgress? prune = null;
        coordinator.ProgressChanged += (_, p) => { if (p.Task == ScanTaskNames.PruneOrphans) prune = p; };
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.NotNull(prune);
        Assert.Equal(2, prune!.Step);
        Assert.Equal(2, prune.TotalSteps);
    }

    // A scan with nothing to prune reports a zero denominator rather than a fake 100%.
    [Fact]
    public async Task RunAsync_NothingToPrune_PruneOrphansProgressHasZeroTotal()
    {
        WriteFrame("keep.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        ScanProgress? prune = null;
        coordinator.ProgressChanged += (_, p) => { if (p.Task == ScanTaskNames.PruneOrphans) prune = p; };
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.NotNull(prune);
        Assert.Equal(0, prune!.Step);
        Assert.Equal(0, prune.TotalSteps);
        Assert.Equal(0.0, prune.Percent);
    }

    // ---- review items 2, 5 and 11 ---------------------------------------------------

    // Review item 2. A trigger that arrived mid-scan sets the pending flag; Cancel() must
    // clear it under the same lock, or the finally schedules a follow-up scan the Cancel has
    // no token for and the shutdown drain waits out its whole budget on it.
    [Fact]
    public async Task Cancel_ClearsThePendingFlag_SoNoFollowUpScanRuns()
    {
        for (var i = 0; i < 5; i++) WriteFrame($"frame{i}.fits");
        var coordinator = MakeCoordinator();

        var pendingSet = false;
        coordinator.ProgressChanged += (_, p) =>
        {
            if (pendingSet || p.Task != ScanTaskNames.Classify) return;
            pendingSet = true;
            // Runs while _running is true, so this returns AlreadyRunning and sets _pending.
            _ = coordinator.RunAsync(ScanTrigger.Watcher, null, CancellationToken.None);
            coordinator.Cancel();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.True(pendingSet, "the pending flag was never set, so this test proved nothing");
        Assert.Equal("cancelled", outcome.State);
        Assert.True(await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(5)));

        using var context = OpenRead();
        Assert.Equal(1, context.ScanRuns.Count());
    }

    // Review item 5. NameRuleMatcher.OnTimeout is a static hook with no production subscriber
    // until a coordinator exists; without it a regex match timeout degrades to "never
    // matches" in complete silence.
    [Fact]
    public void Constructor_InstallsTheNameRuleTimeoutHook()
    {
        NameRuleMatcher.OnTimeout = null;

        _ = MakeCoordinator();
        Assert.NotNull(NameRuleMatcher.OnTimeout);

        // Idempotent: a second coordinator leaves a live hook, not a null one.
        _ = MakeCoordinator();
        Assert.NotNull(NameRuleMatcher.OnTimeout);
    }

    // Review item 11. When the failure path's own bookkeeping write fails too, the exception
    // that surfaces must still be the one that actually broke the scan.
    [Fact]
    public async Task RunAsync_WhenRecordingTheFailureAlsoFails_TheOriginalCauseStillSurfaces()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();

        // Both tables go after the run has started and after LoadKnownFiles has read
        // `images`: the ingest write then fails on `images`, and stamping the terminal row
        // fails on `scan_runs`.
        coordinator.ProgressChanged += (_, p) =>
        {
            if (p.Task != ScanTaskNames.Classify) return;
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
            context.Database.ExecuteSqlRaw("DROP TABLE images");
            context.Database.ExecuteSqlRaw("DROP TABLE scan_runs");
        };

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        Assert.Contains("images", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scan_runs", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(coordinator.IsRunning);
    }

    // ---- duplicate detection (spec 9.7, Phase 7 Task 1) -----------------------------
    //
    // Every fixture frame in this class carries no OBJECT card, so the pass finds no
    // unresolved names and no active targets: what is asserted here is the gate, the envelope
    // and the ordering, not the detection itself (DuplicateDetectorTests owns that).

    private static async Task<List<ScanProgress>> RecordProgress(ScanCoordinator coordinator)
    {
        var seen = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, p) => { lock (seen) seen.Add(p); };
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        lock (seen) return [.. seen];
    }

    [Fact]
    public async Task Scan_WithNewFiles_RunsDuplicateDetection()
    {
        WriteFrame("a.fits");

        var seen = await RecordProgress(MakeCoordinator());

        // The LAST dedup envelope, not the only one (Phase 14B Task 5, questions.md Q1). A run
        // can raise a non-forced intermediate envelope before the forced terminal one, and whether
        // it survives depends on RaiseProgress's 100 ms wall-clock throttle rather than on any
        // property of the pass. Assert.Single here was therefore a machine-load assertion. The
        // message pin below is unchanged and is what the case is actually about.
        var dedup = seen.Last(p => p.Task == ScanTaskNames.Dedup);
        Assert.StartsWith("Duplicate detection complete", dedup.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_DuplicatesDetectedDetails_AreSnakeCase()
    {
        // Phase 7 fixer item 4: the outcome record was serialised straight into details, making
        // these the only PascalCase keys in activity_events.
        WriteFrame("a.fits");

        await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        var dedup = context.ActivityEvents.Single(e => e.EventType == "duplicates_detected");
        using var details = JsonDocument.Parse(dedup.Details!);
        Assert.Equal(
            ["candidates_written", "frames_assigned", "names_examined", "orphan_count",
             "stopped_on_network_failure", "targets_created"],
            details.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList());
    }

    [Fact]
    public async Task Scan_WithNoNewFiles_SkipsDuplicateDetection()
    {
        WriteFrame("a.fits");
        var coordinator = MakeCoordinator();
        var first = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        Assert.Equal(1, first.NewFiles);

        // Nothing changed on disk, so the second run classifies zero new files.
        var seen = await RecordProgress(coordinator);

        var dedup = Assert.Single(seen, p => p.Task == ScanTaskNames.Dedup);
        Assert.Equal("No new files; skipping duplicate detection", dedup.Message);
    }

    [Fact]
    public async Task Scan_DedupEnvelope_IsRaisedBetweenPruneOrphansAndRefThumbnails()
    {
        WriteFrame("a.fits");

        var tasks = (await RecordProgress(MakeCoordinator())).Select(p => p.Task).ToList();

        Assert.InRange(
            tasks.IndexOf(ScanTaskNames.Dedup),
            tasks.IndexOf(ScanTaskNames.PruneOrphans) + 1,
            tasks.IndexOf(ScanTaskNames.RefThumbnails) - 1);
    }

    // ---- mosaic detection (spec 7.7, Phase 18) ---------------------------------------

    // A lone first panel catalogued before Phase 18 (no panel_label), outside the scan root so
    // orphan pruning leaves it alone: the pass relabels it and writes its one-panel suggestion.
    private void SeedLonePanel()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "IC 1396");
        LibrarySeeder.AddFrame(_db.ConnectionString, target.Id, new DateOnly(2025, 3, 15),
            image => image.RawHeaders = LibrarySeeder.RawHeadersWithObject("IC 1396 P1"));
    }

    [Fact]
    public async Task Scan_RunsMosaicDetection_BetweenDedupAndRefThumbnails_AndRecordsIt()
    {
        WriteFrame("a.fits");
        SeedLonePanel();

        var seen = await RecordProgress(MakeCoordinator());

        var tasks = seen.Select(p => p.Task).ToList();
        Assert.InRange(
            tasks.IndexOf(ScanTaskNames.MosaicDetection),
            tasks.LastIndexOf(ScanTaskNames.Dedup) + 1,
            tasks.IndexOf(ScanTaskNames.RefThumbnails) - 1);
        Assert.Equal("1 suggestion", seen.Last(p => p.Task == ScanTaskNames.MosaicDetection).Message);

        using var context = OpenRead();
        Assert.Equal("IC 1396", context.MosaicSuggestions.Single().SuggestedName);
        var started = context.ActivityEvents.Single(a => a.EventType == "scan_started");
        var detected = context.ActivityEvents.Single(a => a.EventType == "mosaic_detection_complete");
        Assert.Equal(started.Id, detected.ParentId);
        using var details = JsonDocument.Parse(detected.Details!);
        Assert.Equal("scan", details.RootElement.GetProperty("trigger").GetString());
        Assert.Equal(1, details.RootElement.GetProperty("suggestions").GetInt32());
        Assert.Equal(1, details.RootElement.GetProperty("relabelled").GetInt32());
    }

    [Fact]
    public async Task Scan_Cancelled_DoesNotRunMosaicDetection()
    {
        WriteFrame("a.fits");
        SeedLonePanel();
        var coordinator = MakeCoordinator();
        coordinator.ProgressChanged += (_, p) =>
        {
            if (p.Task == ScanTaskNames.Dedup) coordinator.Cancel();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("cancelled", outcome.State);
        using var context = OpenRead();
        Assert.Empty(context.MosaicSuggestions);
        Assert.DoesNotContain(context.ActivityEvents, a => a.EventType.StartsWith("mosaic_detection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunMosaicDetectionAsync_RunsThePassWithTheManualTrigger()
    {
        SeedLonePanel();
        var steps = new List<string>();

        var result = await MakeCoordinator().RunMosaicDetectionAsync((_, _, message) => steps.Add(message), CancellationToken.None);

        Assert.Equal(1, result!.SuggestionsWritten);
        Assert.Equal(["Relabelling frames", "Filling positions", "Grouping candidates", "Writing suggestions", "1 suggestion"], steps);
        using var context = OpenRead();
        var detected = context.ActivityEvents.Single(a => a.EventType == "mosaic_detection_complete");
        Assert.Null(detected.ParentId);
        Assert.Contains("\"trigger\":\"manual\"", detected.Details!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunMosaicDetectionAsync_WhileTheLeaseIsHeld_ReturnsNull()
    {
        var coordinator = MakeCoordinator();
        using var lease = coordinator.TryBeginResolution();

        Assert.Null(await coordinator.RunMosaicDetectionAsync((_, _, _) => { }, CancellationToken.None));
    }

    // A pass that throws does not fail the scan: it reports a negative-total terminal envelope
    // and writes mosaic_detection_failed.
    [Fact]
    public async Task Scan_AMosaicDetectionFailure_DoesNotFailTheScan()
    {
        WriteFrame("a.fits");
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            context.Database.ExecuteSqlRaw("DROP TABLE mosaic_suggestions");
        }

        var seen = await RecordProgress(MakeCoordinator());

        Assert.Equal("complete", OpenRead().ScanRuns.Single().State);
        Assert.Equal(-1, seen.Last(p => p.Task == ScanTaskNames.MosaicDetection).TotalSteps);
        using var read = OpenRead();
        Assert.Single(read.ActivityEvents, a => a.EventType == "mosaic_detection_failed");
    }

    // ---- the reference thumbnail pass (spec 11.4, Phase 8 Task 6) -------------------

    // A coordinator whose render delegate is a recording lambda. Null (the default) is the
    // no-renderer shape every other test in this class already gets.
    private ScanCoordinator MakeCoordinator(Func<Guid, string, string?> ensureReference)
        => new(_db.ConnectionString, _settings, _resolver, _scanRuns, NullLogger<ScanCoordinator>.Instance,
            (targetId, framePath, _, _) => ensureReference(targetId, framePath));

    // One target with one eligible LIGHT frame, outside the scan root so orphan pruning leaves it
    // alone: what is under test is the post-pass, not ingestion.
    private Guid SeedTargetNeedingAThumbnail(string primaryName = "M 31")
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, primaryName);
        LibrarySeeder.AddFrame(_db.ConnectionString, target.Id, new DateOnly(2025, 3, 15));
        return target.Id;
    }

    [Fact]
    public async Task Scan_RunsTheReferenceThumbnailPass()
    {
        WriteFrame("a.fits");
        var targetId = SeedTargetNeedingAThumbnail();
        var rendered = new List<Guid>();

        var seen = await RecordProgress(MakeCoordinator((id, _) =>
        {
            lock (rendered) rendered.Add(id);
            return "reference/x.jpg";
        }));

        Assert.Equal([targetId], rendered);
        // The LAST ref_thumbnails envelope, not the only one (Phase 14B Task 5, questions.md Q1).
        // A one-target run raises a non-forced intermediate from ReferenceThumbnailPass's
        // "index == targets.Count - 1" arm and then the forced terminal; the intermediate survives
        // only when 100 ms has elapsed since the previous envelope, so Assert.Single passed on a
        // fast run and threw SingleException on a loaded one. The pass keeps its intermediate
        // report, which is the progress a large run needs; the assertion is what was wrong.
        var envelope = seen.Last(p => p.Task == ScanTaskNames.RefThumbnails);
        Assert.Equal("Reference thumbnails: 1 generated, 0 failed", envelope.Message);

        using var context = OpenRead();
        Assert.Equal("reference/x.jpg", context.Targets.Single(t => t.Id == targetId).ReferenceThumbnailPath);
    }

    [Fact]
    public async Task Scan_RefThumbnailsEnvelope_IsRaisedBetweenDedupAndPruneActivity()
    {
        WriteFrame("a.fits");

        var tasks = (await RecordProgress(MakeCoordinator())).Select(p => p.Task).ToList();

        Assert.InRange(
            tasks.IndexOf(ScanTaskNames.RefThumbnails),
            tasks.IndexOf(ScanTaskNames.Dedup) + 1,
            tasks.IndexOf(ScanTaskNames.PruneActivity) - 1);
    }

    [Fact]
    public async Task Scan_NoTargetsNeedingAThumbnail_StillRaisesOneRefThumbnailsEnvelope()
    {
        // Ruling Q15: the query is the gate, and a run with nothing to do keeps spec 10.4's
        // seven-name vocabulary intact rather than dropping a phase out of the status bar.
        WriteFrame("a.fits");

        var seen = await RecordProgress(MakeCoordinator((_, _) => { Assert.Fail("nothing to render"); return null; }));

        var envelope = Assert.Single(seen, p => p.Task == ScanTaskNames.RefThumbnails);
        Assert.Equal("No targets need a reference thumbnail", envelope.Message);
    }

    [Fact]
    public async Task Scan_WithNoRenderDelegate_StillRaisesTheEnvelope()
    {
        // A coordinator built by hand with no renderer, which is a test-only shape: AppHost binds
        // ThumbnailCache.EnsureReference for the GUI and the CLI alike, so a `galactilog scan`
        // does run the pass. The no-op path still reports the phase and writes nothing.
        WriteFrame("a.fits");
        var targetId = SeedTargetNeedingAThumbnail();

        var seen = await RecordProgress(MakeCoordinator());

        var envelope = Assert.Single(seen, p => p.Task == ScanTaskNames.RefThumbnails);
        Assert.Equal("Reference thumbnails: no renderer configured", envelope.Message);

        using var context = OpenRead();
        Assert.Null(context.Targets.Single(t => t.Id == targetId).ReferenceThumbnailPath);
    }

    [Fact]
    public async Task Scan_ReferenceThumbnailPass_EmitsTheActivityEventWithSnakeCaseDetails()
    {
        WriteFrame("a.fits");
        SeedTargetNeedingAThumbnail();

        await MakeCoordinator((_, _) => "reference/x.jpg")
            .RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        var thumbnails = context.ActivityEvents.Single(e => e.EventType == "reference_thumbnails");
        Assert.Equal("thumbnail", thumbnails.Category);
        Assert.Equal("info", thumbnails.Severity);
        using var details = JsonDocument.Parse(thumbnails.Details!);
        // Spec 10.9 names generated and failed; total and cancelled are additive.
        Assert.Equal(
            ["cancelled", "failed", "generated", "total"],
            details.RootElement.EnumerateObject().Select(p => p.Name).Order().ToList());
        Assert.Equal(1, details.RootElement.GetProperty("generated").GetInt32());
        Assert.Equal(0, details.RootElement.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Scan_ReferenceThumbnailEvent_IsParentedToScanStarted()
    {
        WriteFrame("a.fits");
        SeedTargetNeedingAThumbnail();

        await MakeCoordinator((_, _) => "reference/x.jpg")
            .RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        using var context = OpenRead();
        Assert.Equal(
            context.ActivityEvents.Single(e => e.EventType == "scan_started").Id,
            context.ActivityEvents.Single(e => e.EventType == "reference_thumbnails").ParentId);
    }

    [Fact]
    public async Task Scan_Cancelled_SkipsTheReferenceThumbnailPass()
    {
        // Spec 10.5: a cancelled run skips orphan pruning and every post-pass entirely.
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var targetId = SeedTargetNeedingAThumbnail();
        ScanCoordinator? coordinator = null;
        coordinator = MakeCoordinator((_, _) => { Assert.Fail("the pass must not run"); return null; });

        var tasks = new List<string>();
        coordinator.ProgressChanged += (_, p) =>
        {
            lock (tasks) tasks.Add(p.Task);
            if (p.Task == ScanTaskNames.Ingest) coordinator!.Cancel();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("cancelled", outcome.State);
        lock (tasks) Assert.DoesNotContain(ScanTaskNames.RefThumbnails, tasks);

        using var context = OpenRead();
        Assert.Null(context.Targets.Single(t => t.Id == targetId).ReferenceThumbnailPath);
    }

    [Fact]
    public async Task Scan_CancelledInsideTheReferenceThumbnailPass_IsRecordedCancelled()
    {
        // The pass breaks rather than throwing, so that it keeps what it produced. Without the
        // checkpoint after it, a run stopped here would be recorded complete, emit scan_complete
        // and go on to prune (spec 10.5: a cancelled run skips every post-pass).
        WriteFrame("a.fits");
        var first = SeedTargetNeedingAThumbnail("M 31");
        var second = SeedTargetNeedingAThumbnail("M 42");
        var third = SeedTargetNeedingAThumbnail("M 45");
        ScanCoordinator? coordinator = null;
        coordinator = MakeCoordinator((targetId, _) =>
        {
            if (targetId == second)
            {
                coordinator!.Cancel();
            }

            return "reference/" + targetId.ToString("D") + ".jpg";
        });

        var tasks = new List<string>();
        coordinator.ProgressChanged += (_, p) => { lock (tasks) tasks.Add(p.Task); };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("cancelled", outcome.State);
        lock (tasks) Assert.DoesNotContain(ScanTaskNames.PruneActivity, tasks);

        using var context = OpenRead();
        Assert.Equal("cancelled", context.ScanRuns.Single(run => run.Id == outcome.RunId).State);
        Assert.DoesNotContain(context.ActivityEvents, e => e.EventType == "scan_complete");
        Assert.Single(context.ActivityEvents, e => e.EventType == "scan_cancelled");

        // What the pass produced before the break is kept, and the target it never reached is not
        // counted as a failure.
        Assert.NotNull(context.Targets.Single(t => t.Id == first).ReferenceThumbnailPath);
        Assert.NotNull(context.Targets.Single(t => t.Id == second).ReferenceThumbnailPath);
        Assert.Null(context.Targets.Single(t => t.Id == third).ReferenceThumbnailPath);

        var thumbnails = context.ActivityEvents.Single(e => e.EventType == "reference_thumbnails");
        using var details = JsonDocument.Parse(thumbnails.Details!);
        Assert.True(details.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.Equal(0, details.RootElement.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task TargetedScan_DoesNotRunTheReferenceThumbnailPass()
    {
        // Spec 10.7: a watcher batch during a scan is followed by a full scan, so the targeted
        // path skips the post-passes for the same reason it skips dedup.
        var path = WriteFrame("a.fits");
        SeedTargetNeedingAThumbnail();
        var coordinator = MakeCoordinator((_, _) => { Assert.Fail("the pass must not run"); return null; });

        var tasks = new List<string>();
        coordinator.ProgressChanged += (_, p) => { lock (tasks) tasks.Add(p.Task); };
        await coordinator.RunTargetedAsync(ScanTrigger.Watcher, [path], CancellationToken.None);

        lock (tasks) Assert.DoesNotContain(ScanTaskNames.RefThumbnails, tasks);
    }

    [Fact]
    public async Task Scan_Cancelled_SkipsDuplicateDetection()
    {
        for (var i = 0; i < 6; i++) WriteFrame($"frame{i:00}.fits");
        var coordinator = MakeCoordinator();

        var tasks = new List<string>();
        coordinator.ProgressChanged += (_, p) =>
        {
            lock (tasks) tasks.Add(p.Task);
            if (p.Task == ScanTaskNames.Ingest) coordinator.Cancel();
        };

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("cancelled", outcome.State);
        lock (tasks) Assert.DoesNotContain(ScanTaskNames.Dedup, tasks);

        using var context = OpenRead();
        Assert.Empty(context.MergeCandidates);
    }

    // ---- Phase 9 Task 8: the standalone reference thumbnail run (spec 12.7) -----------------
    //
    // The maintenance entry point runs the same pass the post-scan phase runs, over the same
    // private body, under the coordinator's own resolution lease. The eight scan-path cases above
    // are unchanged and are what proves the scan behaviour did not move.

    private ScanCoordinator MakeCoordinator(Func<Guid, string, bool, string?> ensureReference)
        => new(_db.ConnectionString, _settings, _resolver, _scanRuns, NullLogger<ScanCoordinator>.Instance,
            (targetId, framePath, force, _) => ensureReference(targetId, framePath, force));

    [Fact]
    public async Task RunReferenceThumbnailsAsync_RunsThePass()
    {
        var targetId = SeedTargetNeedingAThumbnail();
        var rendered = new List<Guid>();
        var coordinator = MakeCoordinator((id, _) =>
        {
            lock (rendered) rendered.Add(id);
            return "reference/x.jpg";
        });

        var progress = new List<string>();
        var outcome = await coordinator.RunReferenceThumbnailsAsync(
            force: false,
            (_, _, message) => { lock (progress) progress.Add(message); },
            CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(1, outcome!.Total);
        Assert.Equal(1, outcome.Generated);
        Assert.Equal([targetId], rendered);
        lock (progress) Assert.Contains("Reference thumbnails: 1 generated, 0 failed", progress);

        using var context = OpenRead();
        Assert.Equal("reference/x.jpg", context.Targets.Single(t => t.Id == targetId).ReferenceThumbnailPath);
    }

    [Fact]
    public async Task RunReferenceThumbnailsAsync_Forced_PassesForceIntoThePass()
    {
        // FIXER item 17: without force the query re-offers every target, the cache serves the
        // existing file as a hit, and the action reports a generated count while producing no new
        // pixels. The flag has to reach the render.
        var targetId = SeedTargetNeedingAThumbnail();
        var forces = new List<bool>();
        var coordinator = MakeCoordinator((_, _, force) =>
        {
            lock (forces) forces.Add(force);
            return "reference/x.jpg";
        });

        await coordinator.RunReferenceThumbnailsAsync(force: false, (_, _, _) => { }, CancellationToken.None);
        var forced = await coordinator.RunReferenceThumbnailsAsync(force: true, (_, _, _) => { }, CancellationToken.None);

        // The unforced run filled the gap, so only the forced run re-offers this target.
        lock (forces) Assert.Equal(new[] { false, true }, forces);
        Assert.NotNull(forced);
        Assert.Equal(1, forced!.Total);
        using var context = OpenRead();
        Assert.Equal("reference/x.jpg", context.Targets.Single(t => t.Id == targetId).ReferenceThumbnailPath);
    }

    [Fact]
    public async Task RunReferenceThumbnailsAsync_WhileAScanIsRunning_ReturnsNull()
    {
        SeedTargetNeedingAThumbnail();
        var coordinator = MakeCoordinator((_, _) =>
        {
            Assert.Fail("the pass must not run while the lease is held");
            return null;
        });

        using var lease = coordinator.TryBeginResolution();
        Assert.NotNull(lease);

        var outcome = await coordinator.RunReferenceThumbnailsAsync(
            force: true, (_, _, _) => { }, CancellationToken.None);

        Assert.Null(outcome);
    }

    [Fact]
    public async Task RunReferenceThumbnailsAsync_ReleasesTheLease()
    {
        SeedTargetNeedingAThumbnail();
        var coordinator = MakeCoordinator((_, _) => "reference/x.jpg");

        await coordinator.RunReferenceThumbnailsAsync(force: false, (_, _, _) => { }, CancellationToken.None);

        Assert.False(coordinator.ResolutionInProgress);
        using var lease = coordinator.TryBeginResolution();
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task RunReferenceThumbnailsAsync_DoesNotChangeTheScanPathBehaviour()
    {
        // A maintenance run is not a scan phase, so it raises no ProgressChanged envelope and
        // spec 10.4's seven-name vocabulary is untouched. Its activity event is parented to
        // nothing, because there is no scan_started to parent it to.
        SeedTargetNeedingAThumbnail();
        var coordinator = MakeCoordinator((_, _) => "reference/x.jpg");

        var envelopes = new List<ScanProgress>();
        coordinator.ProgressChanged += (_, p) => { lock (envelopes) envelopes.Add(p); };

        await coordinator.RunReferenceThumbnailsAsync(force: false, (_, _, _) => { }, CancellationToken.None);

        lock (envelopes) Assert.Empty(envelopes);
        Assert.False(coordinator.IsRunning);

        using var context = OpenRead();
        var thumbnails = context.ActivityEvents.Single(e => e.EventType == "reference_thumbnails");
        Assert.Null(thumbnails.ParentId);
        Assert.Equal("thumbnail", thumbnails.Category);
        Assert.DoesNotContain(context.ActivityEvents, e => e.EventType == "scan_started");
    }

    [Fact]
    public async Task RunReferenceThumbnailsAsync_NoRenderer_ReportsAndIsNotConfusedWithARefusedLease()
    {
        // Review minor 3: null used to mean either "a scan holds the lease" or "no renderer was
        // supplied", and the caller's summary line called both a running scan. Null now means the
        // lease and nothing else; the no-renderer shape reports itself and comes back empty.
        SeedTargetNeedingAThumbnail();
        var coordinator = MakeCoordinator();

        var progress = new List<string>();
        var outcome = await coordinator.RunReferenceThumbnailsAsync(
            force: false, (_, _, message) => { lock (progress) progress.Add(message); }, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(0, outcome!.Total);
        Assert.Equal(0, outcome.Generated);
        lock (progress) Assert.Contains("Reference thumbnails: no renderer configured", progress);
    }

    [Fact]
    public async Task RunReferenceThumbnailsAsync_NothingToDo_ReportsAndReturnsAnEmptyOutcome()
    {
        var coordinator = MakeCoordinator((_, _) =>
        {
            Assert.Fail("nothing to render");
            return null;
        });

        var progress = new List<string>();
        var outcome = await coordinator.RunReferenceThumbnailsAsync(
            force: false, (_, _, message) => { lock (progress) progress.Add(message); }, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(0, outcome!.Total);
        lock (progress) Assert.Contains("No targets need a reference thumbnail", progress);
    }

    // ---- a frame whose parse throws (spec 10.3 step 3) -------------------------------

    // The frame-side counterpart of step 5 item 3, "One bad file never stops the pass". Before
    // ScanRecordParser.Parse guarded its own body, the throw faulted a reader task, RunIngestAsync
    // let it past its cancellation-only catch, and the coordinator's broad catch recorded the whole
    // run failed and rethrew. Every file the readers had not yet reached was never opened, and the
    // bad file itself was never marked failed.
    private static string HashOf(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task RunAsync_FrameWhoseParseThrows_EndsComplete_AndCountsItAsOneFailedFile()
    {
        for (var i = 0; i < 8; i++) WriteFrame($"healthy{i:00}.fits");
        var bad = WriteThrowingFrame("bad.fits");
        var beforeFiles = SnapshotRoot();
        var beforeHash = HashOf(bad);
        var coordinator = MakeCoordinator();

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.Equal(9, outcome.Discovered);
        Assert.Equal(9, outcome.NewFiles);
        Assert.Equal(0, outcome.ChangedFiles);
        Assert.Equal(8, outcome.Completed);
        Assert.Equal(1, outcome.Failed);
        Assert.Equal(0, outcome.SkippedCalibration);
        Assert.Equal(0, outcome.Removed);

        // Spec 7.5's reconciliation identity: every record takes exactly one arm of the writer.
        Assert.Equal(outcome.NewFiles + outcome.ChangedFiles, outcome.Completed + outcome.Failed);

        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal("complete", run.State);
        Assert.Equal(1, run.Failed);
        Assert.Null(run.ErrorText);

        // No images row for the bad file, and the eight healthy frames are all catalogued.
        Assert.Equal(8, context.Images.Count());
        Assert.DoesNotContain(bad, context.Images.Select(i => i.FilePath).ToList());

        var events = context.ActivityEvents.ToList();
        var rejected = Assert.Single(events.Where(e => e.EventType == "file_rejected").ToList());
        Assert.Equal("warning", rejected.Severity);
        Assert.Contains("bad.fits", rejected.Message);
        Assert.Contains("unexpected error while reading the file: ArgumentOutOfRangeException: ", rejected.Message);
        Assert.DoesNotContain("   at ", rejected.Message);
        Assert.Equal(events.Single(e => e.EventType == "scan_started").Id, rejected.ParentId);
        Assert.Contains(events, e => e.EventType == "scan_complete");
        Assert.DoesNotContain(events, e => e.EventType == "scan_failed");

        // FILE SAFETY: the run reads the bad file and never writes to it. Every entry under the
        // root has the same path, length and last-write time afterwards, and the bad file's bytes
        // hash to what they hashed to before.
        Assert.Equal(beforeFiles, SnapshotRoot());
        Assert.Equal(beforeHash, HashOf(bad));
    }

    // Spec 10.3 step 2's known limit (the Phase 4 ruling): a failed file writes no images row, so
    // classification sees it as New on every scan and it is header-read again each time. It is
    // never silently treated as catalogued.
    [Fact]
    public async Task RunAsync_FrameWhoseParseThrows_IsAttemptedAgainOnTheNextScan_AndStillWritesNoRow()
    {
        for (var i = 0; i < 8; i++) WriteFrame($"healthy{i:00}.fits");
        var bad = WriteThrowingFrame("bad.fits");
        var coordinator = MakeCoordinator();
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        var second = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("complete", second.State);
        Assert.Equal(9, second.Discovered);
        Assert.Equal(1, second.NewFiles);      // the eight healthy frames are unchanged
        Assert.Equal(0, second.ChangedFiles);
        Assert.Equal(0, second.Completed);
        Assert.Equal(1, second.Failed);
        Assert.Equal(0, second.Removed);       // and nothing is pruned

        using var context = OpenRead();
        Assert.Equal(8, context.Images.Count());
        Assert.DoesNotContain(bad, context.Images.Select(i => i.FilePath).ToList());
        Assert.Equal(2, context.ActivityEvents.Count(e => e.EventType == "file_rejected"));
    }

    // The one risk the fix carries, stated as its own case (brief section 9): a scan of a HEALTHY
    // library produces the same files, the same rows and the same counts. This is the bad-frame
    // case above with the bad frame removed, so the two read side by side.
    [Fact]
    public async Task RunAsync_HealthyLibrary_SameFilesRowsAndCounts()
    {
        var frames = new List<string>();
        for (var i = 0; i < 8; i++) frames.Add(WriteFrame($"healthy{i:00}.fits"));
        var beforeFiles = SnapshotRoot();
        var coordinator = MakeCoordinator();

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        Assert.Equal(8, outcome.Discovered);
        Assert.Equal(8, outcome.NewFiles);
        Assert.Equal(0, outcome.ChangedFiles);
        Assert.Equal(8, outcome.Completed);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(0, outcome.SkippedCalibration);
        Assert.Equal(0, outcome.Removed);
        Assert.Equal(beforeFiles, SnapshotRoot());

        using var context = OpenRead();
        Assert.Equal(
            frames.Order(StringComparer.Ordinal).ToList(),
            context.Images.Select(i => i.FilePath).ToList().Order(StringComparer.Ordinal).ToList());
        // Same session date for every frame: general.observer_longitude is unset and
        // general.observer_timezone is empty, so spec 8.3 step 4's UTC-midnight fallback applies
        // and DATE-OBS 2025-03-15T02:00:00 labels the frame 2025-03-15.
        Assert.All(context.Images.ToList(), i => Assert.Equal(new DateOnly(2025, 3, 15), i.SessionDate));
        Assert.DoesNotContain(context.ActivityEvents.ToList(), e => e.EventType == "file_rejected");
        Assert.DoesNotContain(context.ActivityEvents.ToList(), e => e.EventType == "scan_failed");
    }
}
