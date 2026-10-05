using System.Net;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests;

// The roadmap's own Phase 4 end-to-end case (spec 18.1): one generated fixture folder, one
// real ScanCoordinator run against a fresh migrated database, and exact row counts for every
// table the scan writes -- images, targets, target_catalog_memberships, activity_events and
// scan_runs -- plus the field values that prove the walk, the classifier, the CSV backfill,
// session-date derivation, offline target resolution and orphan pruning all ran.
//
// FILE SAFETY: this is tests/**, outside FileSafetyTest's src/** scan. The only file this
// class ever removes is one it created itself, inside its own temp folder, to drive the
// orphan-pruning case. Nothing under src/** deletes, moves or modifies any file, ever.
//
// The network is stubbed with a clean "queried, found nothing" from both SIMBAD and SESAME,
// so the one deliberately unresolvable OBJECT negative-caches (producing exactly one
// resolution_failed event) instead of tripping the transient-failure circuit breaker, and no
// test here touches the real network.
public sealed class EndToEndScanTests : IDisposable
{
    private const string CaptureUtc = "2025-03-15T02:00:00";

    // Imaging-night grouping at longitude 0: 02:00 UTC on the 15th belongs to the night
    // that started on the 14th (spec 8.2, offset = 12 - longitude/15 hours).
    private static readonly DateOnly ExpectedSessionDate = new(2025, 3, 14);

    private readonly TestDatabaseHandle _db;
    private readonly SettingsStore _settings;
    private readonly TargetResolver _resolver;
    private readonly ScanRunRepository _scanRuns;
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-e2e-").FullName;

    public EndToEndScanTests()
    {
        _db = TestDatabaseFactory.CreateSeededDatabase();
        _settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        var catalogsDirectory = StaticCatalogLoader.ResolveCatalogsDirectory();
        _resolver = new TargetResolver(
            _db.ConnectionString, catalogsDirectory,
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(NoMatchHandler()), new SesameClient(NoMatchHandler()));
        _scanRuns = new ScanRunRepository(_db.ConnectionString);

        _settings.SaveGeneral(new GeneralSettings
        {
            ScanRoots = [_root],
            ScanFilters = ScanFilterConfig.Empty,
            IncludeCalibration = false,
            UseImagingNight = true,
            ObserverLongitude = 0.0,
        });

        BuildFixtureFolder();
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    // ---- fixture folder -------------------------------------------------------------

    // Five light frames (four with catalog-resolvable OBJECT values, one deliberately
    // unresolvable), three calibration frames, one malformed FITS, and an ImageMetaData.csv
    // covering exactly one of the light frames.
    private const string LightM31A = "light_m31_a.fits";   // CSV-covered
    private const string LightM31B = "light_m31_b.fits";   // not CSV-covered
    private const string LightM42 = "light_m42.fits";
    private const string LightXisf = "light_m31_c.xisf";
    private const string LightUnresolvable = "light_unknown.fits";
    private const string BiasFrame = "bias_001.fits";
    private const string DarkFrame = "dark_001.fits";
    private const string FlatFrame = "flat_001.fits";
    private const string BrokenFrame = "broken.fits";

    private const int DiscoveredFileCount = 9;
    private const int LightFrameCount = 5;
    private const int CalibrationFrameCount = 3;

    private void BuildFixtureFolder()
    {
        WriteFits(LightM31A, "LIGHT", "M 31");
        WriteFits(LightM31B, "LIGHT", "M 31");
        WriteFits(LightM42, "LIGHT", "M 42");
        WriteFits(LightUnresolvable, "LIGHT", "Zzyzx Blob 42");
        WriteXisf(LightXisf, "LIGHT", "M 31");

        WriteFits(BiasFrame, "BIAS", null);
        WriteFits(DarkFrame, "DARK", null);
        WriteFits(FlatFrame, "FLAT", null);

        // First card is not SIMPLE, so FitsHeaderReader rejects it ("not a simple FITS
        // file") and the writer records one file_rejected event and one failure.
        WriteBytes(BrokenFrame, new FitsBuilder()
            .RawCard("NOTSIMPL=                    T".PadRight(80))
            .Card("BITPIX", (long)16)
            .Card("NAXIS", (long)0)
            .EndCard()
            .Build().ToArray());

        WriteBytes("ImageMetaData.csv", CsvBuilder.BuildImageMetaDataCsv(
        [
            new Dictionary<string, string?>
            {
                ["FilePath"] = @"D:\NINA\captures\" + LightM31A,
                ["HFR"] = "2.5",
                ["FWHM"] = "3.21",
                ["Eccentricity"] = "0.42",
                ["DetectedStars"] = "1234",
                ["GuidingRMSArcSec"] = "0.55",
                ["FocuserPosition"] = "18500",
            },
        ]).ToArray());
    }

    private void WriteFits(string name, string imageType, string? objectName)
    {
        var builder = new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", (long)16)
            .Card("NAXIS", (long)0)
            .Card("IMAGETYP", imageType)
            .Card("DATE-OBS", CaptureUtc)
            .Card("EXPTIME", 300.0);
        if (objectName is not null)
        {
            builder.Card("OBJECT", objectName);
        }
        WriteBytes(name, builder.EndCard().Build().ToArray());
    }

    private void WriteXisf(string name, string imageType, string objectName)
        => WriteBytes(name, new XisfBuilder()
            .Geometry(4, 4, 1)
            .ImageTypeAttribute(imageType)
            .FitsKeyword("OBJECT", $"'{objectName}'")
            .FitsKeyword("DATE-OBS", $"'{CaptureUtc}'")
            .FitsKeyword("EXPTIME", "300.0")
            .Pixels(new float[4, 4])
            .Build().ToArray());

    private void WriteBytes(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_root, name), bytes);

    private string PathOf(string name) => Path.Combine(_root, name);

    private static StubHandler NoMatchHandler() => new(request =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            // SIMBAD's "queried, no match" marker, and a Resolver-less SESAME body: a clean
            // miss from both, never an HTTP error (which would be a transient failure).
            Content = new StringContent(
                request.RequestUri!.Host.Contains("simbad", StringComparison.OrdinalIgnoreCase)
                    ? "::error::\nnot found\n"
                    : "<?xml version=\"1.0\"?><Sesame></Sesame>"),
        });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private ScanCoordinator MakeCoordinator()
        => new(_db.ConnectionString, _settings, _resolver, _scanRuns, NullLogger<ScanCoordinator>.Instance);

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private Task<ScanRunOutcome> RunScan()
        => MakeCoordinator().RunAsync(ScanTrigger.Cli, null, CancellationToken.None);

    private static Dictionary<string, int> EventCounts(GalactiLogContext context)
        => context.ActivityEvents.GroupBy(e => e.EventType)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionary(x => x.Key, x => x.Count);

    // ---- the whole pipeline, once ----------------------------------------------------

    [Fact]
    public async Task FullScan_OverGeneratedFixtureFolder_ProducesExactExpectedRowCountsAndValues()
    {
        var outcome = await RunScan();

        Assert.Equal("complete", outcome.State);
        Assert.Equal(DiscoveredFileCount, outcome.Discovered);
        Assert.Equal(DiscoveredFileCount, outcome.NewFiles);
        Assert.Equal(0, outcome.ChangedFiles);
        Assert.Equal(LightFrameCount + CalibrationFrameCount, outcome.Completed);
        Assert.Equal(1, outcome.Failed);
        Assert.Equal(CalibrationFrameCount, outcome.SkippedCalibration);
        Assert.Equal(0, outcome.Removed);

        using var context = OpenRead();

        // images: one row per light frame. A skipped calibration frame writes none, and the
        // malformed file writes none.
        Assert.Equal(LightFrameCount, context.Images.Count());
        Assert.All(context.Images.ToList(), image => Assert.Equal(ExpectedSessionDate, image.SessionDate));
        Assert.Equal(
            new[] { LightM31A, LightM31B, LightXisf, LightM42, LightUnresolvable }.Order().ToArray(),
            context.Images.Select(i => i.FileName).ToList().Order().ToArray());

        // targets: M 31 and M 42, resolved offline, one row each however many frames name
        // them. The unresolvable OBJECT creates nothing.
        var targets = context.Targets.ToList();
        Assert.Equal(2, targets.Count);
        var m31 = Assert.Single(targets, t => t.CatalogId == "M 31");
        var m42 = Assert.Single(targets, t => t.CatalogId == "M 42");
        Assert.Contains("M 31", m31.PrimaryName);

        // Every M 31 frame -- FITS and XISF alike -- points at that one target row.
        var images = context.Images.ToList();
        Assert.All(
            images.Where(i => i.FileName is LightM31A or LightM31B or LightXisf),
            image => Assert.Equal(m31.Id, image.ResolvedTargetId));
        Assert.Equal(m42.Id, images.Single(i => i.FileName == LightM42).ResolvedTargetId);
        Assert.Null(images.Single(i => i.FileName == LightUnresolvable).ResolvedTargetId);

        // target_catalog_memberships: one Messier row and one SAC row per target -- the SAC
        // catalog bundled in Catalogs/sac.csv keys its rows on "M 31"/"M 42" directly, so
        // both targets match it as well as the Messier rule.
        var memberships = context.TargetCatalogMemberships.ToList();
        Assert.Single(memberships, m => m.TargetId == m31.Id && m.CatalogName == "messier" && m.CatalogNumber == "31");
        Assert.Single(memberships, m => m.TargetId == m42.Id && m.CatalogName == "messier" && m.CatalogNumber == "42");
        Assert.Single(memberships, m => m.TargetId == m31.Id && m.CatalogName == "sac");
        Assert.Single(memberships, m => m.TargetId == m42.Id && m.CatalogName == "sac");
        Assert.All(memberships, m => Assert.Contains(m.TargetId, new[] { m31.Id, m42.Id }));
        Assert.Equal(ExpectedMembershipCount, memberships.Count);

        // activity_events, by event type, all parented to the run's scan_started row.
        var counts = EventCounts(context);
        Assert.Equal(1, counts["scan_started"]);
        Assert.Equal(1, counts["scan_complete"]);
        Assert.Equal(1, counts["file_rejected"]);
        Assert.Equal(2, counts["target_created"]);
        Assert.Equal(1, counts["resolution_failed"]);
        // The run ingested new files, so spec 9.7's duplicate detection pass runs and records
        // its one event. "Zzyzx Blob 42" resolves to nothing and neither existing target scores
        // above the trigram threshold, so it becomes a single orphan candidate.
        Assert.Equal(1, counts["duplicates_detected"]);
        Assert.False(counts.ContainsKey("orphans_pruned"));
        Assert.False(counts.ContainsKey("orphan_prune_skipped"));
        Assert.Equal(7, context.ActivityEvents.Count());

        var orphan = Assert.Single(context.MergeCandidates.ToList());
        Assert.Equal("Zzyzx Blob 42", orphan.SourceName);
        Assert.Equal("orphan", orphan.Method);

        var started = context.ActivityEvents.Single(e => e.EventType == "scan_started");
        Assert.All(
            context.ActivityEvents.Where(e => e.Id != started.Id).ToList(),
            e => Assert.Equal(started.Id, e.ParentId));

        // scan_runs: exactly one row, matching the outcome the coordinator returned.
        var run = Assert.Single(context.ScanRuns.ToList());
        Assert.Equal(outcome.RunId, run.Id);
        Assert.Equal("cli", run.Trigger);
        Assert.Equal("complete", run.State);
        Assert.Equal(DiscoveredFileCount, run.Discovered);
        Assert.Equal(DiscoveredFileCount, run.NewFiles);
        Assert.Equal(LightFrameCount + CalibrationFrameCount, run.Completed);
        Assert.Equal(1, run.Failed);
        Assert.Equal(CalibrationFrameCount, run.SkippedCalibration);
        Assert.Equal(0, run.Removed);
        Assert.Null(run.ErrorText);
        Assert.NotNull(run.FinishedAt);
    }

    // Two Messier rows plus two SAC rows. Pinned as one number so a catalog change or a
    // membership-matching regression is visible here, not just the two rows named above.
    private const int ExpectedMembershipCount = 4;

    [Fact]
    public async Task CsvBackfill_AppliesOnlyToCoveredFrames_WithCsvProvenance()
    {
        await RunScan();

        using var context = OpenRead();
        var covered = context.Images.Single(i => i.FileName == LightM31A);
        var uncovered = context.Images.Single(i => i.FileName == LightM31B);

        Assert.Equal(3.21, covered.Fwhm);
        Assert.Equal(1234, covered.DetectedStars);
        Assert.Equal(2.5, covered.MedianHfr);
        Assert.Equal(0.55, covered.GuidingRmsArcsec);
        Assert.Equal("csv", covered.GuidingRmsSource);
        Assert.Equal(0.42, covered.Eccentricity);
        Assert.Equal("csv", covered.EccentricitySource);
        Assert.Equal(18500, covered.FocuserPosition);

        var provenance = JsonSerializer.Deserialize<Dictionary<string, string>>(covered.Provenance!)!;
        Assert.Equal("csv:FWHM", provenance["fwhm"]);
        Assert.Equal("csv:DetectedStars", provenance["detected_stars"]);
        Assert.Equal("csv:HFR", provenance["median_hfr"]);

        Assert.Null(uncovered.Fwhm);
        Assert.Null(uncovered.DetectedStars);
        Assert.Null(uncovered.MedianHfr);
        Assert.DoesNotContain(
            "csv:",
            JsonSerializer.Deserialize<Dictionary<string, string>>(uncovered.Provenance!)!.Values);
    }

    // ---- second run over the same folder ---------------------------------------------

    [Fact]
    public async Task SecondRun_NothingChangedOnDisk_FindsNothingNew_AndDuplicatesNoRow()
    {
        await RunScan();

        int imagesAfterFirst, targetsAfterFirst, membershipsAfterFirst;
        using (var context = OpenRead())
        {
            imagesAfterFirst = context.Images.Count();
            targetsAfterFirst = context.Targets.Count();
            membershipsAfterFirst = context.TargetCatalogMemberships.Count();
        }

        var second = await RunScan();

        Assert.Equal("complete", second.State);
        Assert.Equal(DiscoveredFileCount, second.Discovered);

        // Every ingestable frame is now known and is re-classified as Unchanged: nothing is
        // re-parsed, nothing is re-resolved, no row is rewritten. The three calibration frames
        // are known through `skipped_files` (spec 5.21), so they are not read again either. The
        // one file still reported New is the malformed one: a rejection writes no row, so it
        // costs a header read and produces the same rejection as before.
        Assert.Equal(1, second.NewFiles);
        Assert.Equal(0, second.ChangedFiles);
        Assert.Equal(0, second.Completed);
        Assert.Equal(1, second.Failed);
        Assert.Equal(0, second.SkippedCalibration);
        Assert.Equal(0, second.Removed);

        using var after = OpenRead();
        Assert.Equal(imagesAfterFirst, after.Images.Count());
        Assert.Equal(targetsAfterFirst, after.Targets.Count());
        Assert.Equal(membershipsAfterFirst, after.TargetCatalogMemberships.Count());
        Assert.Equal(2, after.ScanRuns.Count());

        // The second run adds its own envelope plus the malformed file's rejection again
        // (it has no images row, so it is re-read every scan). No target is created twice,
        // no name is re-resolved, and nothing is pruned.
        var counts = EventCounts(after);
        Assert.Equal(2, counts["scan_started"]);
        Assert.Equal(2, counts["scan_complete"]);
        Assert.Equal(2, counts["file_rejected"]);
        Assert.Equal(2, counts["target_created"]);
        Assert.Equal(1, counts["resolution_failed"]);
        Assert.False(counts.ContainsKey("orphans_pruned"));
    }

    // The literal "found nothing new" case spec 15 requires the CLI to report as exit 0: a
    // root holding nothing but ingestable light frames, so no file lacks an images row and
    // the second run's new_files is exactly zero.
    [Fact]
    public async Task SecondRun_OverIngestableFramesOnly_FindsNothingNew()
    {
        var lightsOnly = Directory.CreateTempSubdirectory("galactilog-e2e-lights-").FullName;
        try
        {
            File.Copy(PathOf(LightM31A), Path.Combine(lightsOnly, LightM31A));
            File.Copy(PathOf(LightM42), Path.Combine(lightsOnly, LightM42));

            var first = await MakeCoordinator().RunAsync(ScanTrigger.Cli, [lightsOnly], CancellationToken.None);
            Assert.Equal(2, first.NewFiles);
            Assert.Equal(2, first.Completed);

            var second = await MakeCoordinator().RunAsync(ScanTrigger.Cli, [lightsOnly], CancellationToken.None);

            Assert.Equal("complete", second.State);
            Assert.Equal(2, second.Discovered);
            Assert.Equal(0, second.NewFiles);
            Assert.Equal(0, second.ChangedFiles);
            Assert.Equal(0, second.Completed);
            Assert.Equal(0, second.Failed);
            Assert.Equal(0, second.Removed);

            using var context = OpenRead();
            Assert.Equal(2, context.Images.Count());
        }
        finally
        {
            Directory.Delete(lightsOnly, recursive: true);
        }
    }

    // ---- a file removed from the test's own fixture folder ----------------------------

    [Fact]
    public async Task ThirdRun_AfterOneFixtureFileIsRemoved_PrunesExactlyThatRow_AndSaysSo()
    {
        await RunScan();
        await RunScan();

        // Test code, test-owned temp folder, test-created file. Nothing in src/** ever does
        // this: orphan pruning removes database rows only.
        File.Delete(PathOf(LightM42));

        var third = await RunScan();

        Assert.Equal("complete", third.State);
        Assert.Equal(DiscoveredFileCount - 1, third.Discovered);
        Assert.Equal(1, third.NewFiles);   // only the rejected file; calibration frames are known through skipped_files
        Assert.Equal(0, third.ChangedFiles);
        Assert.Equal(1, third.Removed);

        using var context = OpenRead();
        Assert.Equal(LightFrameCount - 1, context.Images.Count());
        Assert.DoesNotContain(LightM42, context.Images.Select(i => i.FileName).ToList());

        // The row went; the target it pointed at is untouched (orphan pruning is about
        // images, not targets).
        Assert.Equal(2, context.Targets.Count());

        var pruned = Assert.Single(context.ActivityEvents.ToList(), e => e.EventType == "orphans_pruned");
        Assert.Contains("1", pruned.Message);
        Assert.Equal(3, context.ScanRuns.Count());
        Assert.Equal(1, context.ScanRuns.Single(r => r.Id == third.RunId).Removed);
    }

    // Phase 4 review item 1. LoadKnownFiles and OrphanPruner key file_path
    // OrdinalIgnoreCase, but the writer's upsert lookup is a SQL comparison; with a BINARY
    // column a row catalogued in a different case would be classified Changed and then
    // inserted a second time. Migration 0003 gives images.file_path COLLATE NOCASE, so the
    // three agree. This test fails before that migration (two rows, one stale) and passes
    // after (one row, updated).
    [Fact]
    public async Task Scan_WhenARowIsCataloguedInADifferentCase_UpdatesThatRow_AndInsertsNoDuplicate()
    {
        var upperCasePath = PathOf(LightM31A).ToUpperInvariant();
        using (var seed = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            seed.Images.Add(new Entities.Image
            {
                Id = Guid.NewGuid(),
                FilePath = upperCasePath,
                FileName = LightM31A,
                FileSize = 1,           // stale: forces the Changed branch and a re-ingest
                FileMtime = 1,
            });
            seed.SaveChanges();
        }

        var outcome = await RunScan();

        Assert.Equal("complete", outcome.State);
        Assert.Equal(1, outcome.ChangedFiles);

        using var context = OpenRead();
        Assert.Equal(LightFrameCount, context.Images.Count());

        // One row for that file, and it is the seeded one, updated in place rather than
        // shadowed by a second insert.
        var row = Assert.Single(context.Images.Where(i => i.FilePath == upperCasePath).ToList());
        Assert.Equal("LIGHT", row.ImageType);
        Assert.NotEqual(1, row.FileSize);
    }

    // ---- FIXER LIST item 4, end to end ------------------------------------------------

    // A frame whose OBJECT is "Jupiter" produces a target whose display category is Planet, so
    // the dashboard's Planet pill selects it. The fixture folder and the database are per test
    // instance, so the extra frame here is invisible to every other case in this class and none
    // of the exact counts above move.
    [Fact]
    public async Task FullScan_SolarSystemObjectName_ProducesATargetUnderItsDashboardPill()
    {
        WriteFits("light_jupiter.fits", "LIGHT", "Jupiter");

        var outcome = await RunScan();

        Assert.Equal("complete", outcome.State);
        using var context = OpenRead();

        var jupiter = Assert.Single(context.Targets.Where(t => t.PrimaryName == "Jupiter").ToList());
        Assert.True(jupiter.UserDefined);
        Assert.Equal("Planet", jupiter.ObjectType);
        Assert.Equal("Planet", ObjectTypeCategories.Categorize(jupiter.ObjectType));

        // The frame is assigned, so it leaves the obj: group the dashboard used to strand it in.
        var frame = Assert.Single(context.Images.Where(i => i.FileName == "light_jupiter.fits").ToList());
        Assert.Equal(jupiter.Id, frame.ResolvedTargetId);

        // A solar-system name is not an unresolved name: nothing negative-cached it, and the
        // duplicate-detection pass wrote no candidate for it.
        Assert.DoesNotContain(
            context.MergeCandidates.ToList(),
            candidate => candidate.SourceName == "Jupiter");
    }

    // The other half of item 1: the unique index inherits the column collation, so a
    // case-variant duplicate is rejected by the database itself, not only by the writer.
    [Fact]
    public void UniqueIndexOnFilePath_RejectsACaseVariantDuplicate()
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        context.Images.Add(new Entities.Image
        {
            Id = Guid.NewGuid(), FilePath = PathOf(LightM31A), FileName = LightM31A,
        });
        context.SaveChanges();

        context.Images.Add(new Entities.Image
        {
            Id = Guid.NewGuid(), FilePath = PathOf(LightM31A).ToUpperInvariant(), FileName = LightM31A,
        });
        Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateException>(() => context.SaveChanges());
    }
}
