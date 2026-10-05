using System.Text;
using System.Text.RegularExpressions;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 10.3's per-run arguments (PAR-013), Phase 14B Task 5: the run's scope and the orphan
// cleanup override. Neither is a settings key and neither survives the run.
//
// This is tests/**, outside FileSafetyTest's src/** scan, so writing fixture files here is fine.
public class ScanRunOptionsTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly SettingsStore _settings;
    private readonly TargetResolver _resolver;
    private readonly ScanRunRepository _scanRuns;
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-runoptions-").FullName;

    public ScanRunOptionsTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        _resolver = new TargetResolver(
            _db.ConnectionString, StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(new ThrowingHandler()), new SesameClient(new ThrowingHandler()));
        _scanRuns = new ScanRunRepository(_db.ConnectionString);
        SaveGeneral(includeCalibration: true);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private const int FitsBlockSize = 2880;

    private static byte[] Frame(string imageType)
    {
        var cards = new[]
        {
            "SIMPLE  =                    T",
            "BITPIX  =                   16",
            "NAXIS   =                    0",
            $"IMAGETYP= '{imageType,-8}'",
            "DATE-OBS= '2025-03-15T02:00:00'",
            "EXPTIME =                300.0",
            "END",
        };
        var text = new StringBuilder();
        foreach (var card in cards) text.Append(card.PadRight(80));
        var blocks = (text.Length + FitsBlockSize - 1) / FitsBlockSize;
        return Encoding.ASCII.GetBytes(text.ToString().PadRight(blocks * FitsBlockSize));
    }

    private string WriteFrame(string name, string imageType = "LIGHT")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Frame(imageType));
        return path;
    }

    private void SaveGeneral(bool includeCalibration)
        => _settings.SaveGeneral(new GeneralSettings
        {
            ScanRoots = [_root],
            ScanFilters = ScanFilterConfig.Empty,
            IncludeCalibration = includeCalibration,
        });

    private ScanCoordinator MakeCoordinator()
        => new(_db.ConnectionString, _settings, _resolver, _scanRuns, NullLogger<ScanCoordinator>.Instance);

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private GalactiLogContext OpenWrite() => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private void SeedImage(string path)
    {
        using var context = OpenWrite();
        context.Images.Add(new Image { Id = Guid.NewGuid(), FilePath = path, FileName = Path.GetFileName(path) });
        context.SaveChanges();
    }

    private int RowCount()
    {
        using var context = OpenRead();
        return context.Images.Count();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException($"network access attempted: {request.RequestUri}");
    }

    // ---- the run's scope ------------------------------------------------------------

    [Fact]
    public async Task LightFramesOnly_ReachesTheCalibrationDecision()
    {
        SaveGeneral(includeCalibration: true);
        WriteFrame("light.fits");
        WriteFrame("dark.fits", "DARK");

        var outcome = await MakeCoordinator().RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: false, ForceOrphanCleanup: false));

        // The stored key says "include", the run's argument says "light only", and the argument
        // is what step 3 read. A skipped calibration frame counts as completed as well as
        // skipped and writes no row (spec 7.5), so the row count is the discriminating figure.
        Assert.Equal(1, outcome.SkippedCalibration);
        Assert.Equal(2, outcome.Completed);
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task AllFrames_ReachesTheCalibrationDecision()
    {
        SaveGeneral(includeCalibration: false);
        WriteFrame("light.fits");
        WriteFrame("dark.fits", "DARK");

        var outcome = await MakeCoordinator().RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: false));

        Assert.Equal(0, outcome.SkippedCalibration);
        Assert.Equal(2, outcome.Completed);
        Assert.Equal(2, RowCount());
    }

    [Fact]
    public async Task NoOptions_FallsBackToTheStoredIncludeCalibration()
    {
        SaveGeneral(includeCalibration: false);
        WriteFrame("light.fits");
        WriteFrame("dark.fits", "DARK");

        var outcome = await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, outcome.SkippedCalibration);
        Assert.Equal(2, outcome.Completed);
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task AScopedRun_DoesNotRewriteTheStoredKey()
    {
        SaveGeneral(includeCalibration: true);
        WriteFrame("light.fits");

        await MakeCoordinator().RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: false, ForceOrphanCleanup: true));

        // Spec 10.3: "choosing light frames only for one run never rewrites the stored default."
        Assert.True(_settings.GetGeneral().IncludeCalibration);
    }

    [Fact]
    public async Task AScopedRunToLightOnly_SkipsCalibrationExactlyAsAStoredFalseDoes()
    {
        WriteFrame("light.fits");
        WriteFrame("dark.fits", "DARK");

        SaveGeneral(includeCalibration: false);
        var stored = await MakeCoordinator().RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        var storedRows = RowCount();

        // Same library, same coordinator shape, the stored key now true and the scope argument
        // false. The two runs must classify identically.
        using (var context = OpenWrite())
        {
            context.Images.RemoveRange(context.Images);
            context.SkippedFiles.RemoveRange(context.SkippedFiles);
            context.SaveChanges();
        }

        SaveGeneral(includeCalibration: true);
        var scoped = await MakeCoordinator().RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: false, ForceOrphanCleanup: false));

        Assert.Equal(stored.SkippedCalibration, scoped.SkippedCalibration);
        Assert.Equal(stored.Completed, scoped.Completed);
        Assert.Equal(storedRows, RowCount());
    }

    // ---- the orphan cleanup override -------------------------------------------------

    [Fact]
    public async Task TheOverride_ReachesTheOrphanPrune()
    {
        var present = WriteFrame("here.fits");
        SeedImage(present);
        foreach (var name in new[] { "gone1.fits", "gone2.fits", "gone3.fits" })
        {
            SeedImage(Path.Combine(_root, name));
        }

        // 3 of 4 missing is past the safety limit, so only a forced run removes them.
        var outcome = await MakeCoordinator().RunAsync(
            ScanTrigger.Manual, null, CancellationToken.None,
            new ScanRunOptions(IncludeCalibration: true, ForceOrphanCleanup: true));

        Assert.Equal(3, outcome.Removed);
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task TheOverride_DefaultsOffOnEveryTrigger()
    {
        var present = WriteFrame("here.fits");
        SeedImage(present);
        foreach (var name in new[] { "gone1.fits", "gone2.fits", "gone3.fits" })
        {
            SeedImage(Path.Combine(_root, name));
        }

        // Every trigger but a manual press reaches RunAsync with no options at all, and the
        // limit therefore refuses the deletion for all of them.
        foreach (var trigger in new[]
                 {
                     ScanTrigger.Watcher, ScanTrigger.Scheduler, ScanTrigger.Cli, ScanTrigger.FirstRun,
                     ScanTrigger.Manual,
                 })
        {
            var outcome = await MakeCoordinator().RunAsync(trigger, null, CancellationToken.None);
            Assert.Equal(0, outcome.Removed);
        }

        Assert.Equal(4, RowCount());
    }

    // Spec 10.3's "only a manual run can set it" applied to RunGuardedAsync's own queued
    // follow-up: a follow-up is not the run the user configured, so it must not carry the
    // user's one-run override. The follow-up call site passes no options at all.
    [Fact]
    public void AQueuedFollowUpScan_CarriesNoOverride()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.Data", "Ingest", "ScanCoordinator.cs"));

        Assert.Contains(
            "await RunAsync(nextTrigger, null, CancellationToken.None).ConfigureAwait(false);",
            source,
            StringComparison.Ordinal);

        // And there is exactly one RunAsync call inside the coordinator itself, which is that one.
        Assert.Equal(1, CountOccurrences(source, "await RunAsync("));
    }

    [Fact]
    public async Task ATargetedRun_PrunesNothingAndIgnoresTheOverride()
    {
        var present = WriteFrame("here.fits");
        SeedImage(present);
        foreach (var name in new[] { "gone1.fits", "gone2.fits", "gone3.fits" })
        {
            SeedImage(Path.Combine(_root, name));
        }

        // A partial file list is never "everything on disk", so the targeted entry point has no
        // options parameter to ignore in the first place and prunes nothing.
        var outcome = await MakeCoordinator().RunTargetedAsync(
            ScanTrigger.Watcher, [present], CancellationToken.None);

        Assert.Equal(0, outcome.Removed);
        Assert.Equal(4, RowCount());
    }

    // Spec 10.3: the scope argument "reaches step 2's known set and step 3's calibration decision
    // (section 7.5) and nothing else". Read off the source, because the alternative is asserting
    // the absence of an effect everywhere in a pipeline.
    //
    // The consumers are named rather than counted (Phase 15A): a bare count of 2 would let a
    // third consumer in as soon as one of these two moved out, and a count alone never said
    // WHICH place read the argument.
    [Fact]
    public void TheOptions_ReachNothingElseInThePipeline()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.Data", "Ingest", "ScanCoordinator.cs"));

        // IncludeCalibration is read twice: the known set of step 2 (spec 5.21's skipped_files rows
        // are known only to a run that excludes calibration) and the ScanReadOptions construction.
        Assert.Equal(["LoadKnownFiles", "ScanReadOptions"], CalleesReading(source, "options?.IncludeCalibration"));

        // ForceOrphanCleanup is read exactly twice, and both reads are orphan prunes:
        // RunOrphanPruning is step 4's frame prune, and Phd2Ingest.Run carries the same flag to
        // the guide-log orphan prune of step 5. Spec 10.3 step 5 guard 2: "When the override is
        // on, those rows are deleted and `phd2_orphan_prune_forced` is written at warning. The
        // override is the same per-run argument the Library tab's checkbox sets (PAR-013 above);
        // it is not a second control." Guard 1, zero discovery, is absolute on both sides and
        // reads the flag nowhere, which is why neither guard appears here a third time.
        Assert.Equal(
            ["RunOrphanPruning", "Phd2Ingest.Run"],
            CalleesReading(source, "options?.ForceOrphanCleanup"));

        // And the GeneralSettings snapshot is never copied or mutated to carry the scope.
        Assert.DoesNotContain("general with", source, StringComparison.Ordinal);
        Assert.DoesNotContain("general.IncludeCalibration =", source, StringComparison.Ordinal);
    }

    // The name of the call each read of <paramref name="needle"/> is an argument to: the last
    // "name(" or "Type.Method(" opened before it. A new consumer therefore adds its own name to
    // this list rather than nudging a number.
    private static string[] CalleesReading(string source, string needle)
    {
        var callees = new List<string>();
        var index = source.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            var opened = Regex.Matches(source[..index], @"([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(");
            Assert.NotEmpty(opened);
            callees.Add(opened[^1].Groups[1].Value);
            index = source.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return [.. callees];
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
