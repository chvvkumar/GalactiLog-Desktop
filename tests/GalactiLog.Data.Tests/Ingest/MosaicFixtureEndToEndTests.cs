using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using GalactiLog.Data.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Phase 18 Task 6b. The whole mosaic pipeline from FITS files on disk: a real ScanCoordinator run
// over MosaicFixtureLibrary into a fresh catalogue with the offline catalogue loaded, then the
// header pass's frame fields (spec 10.3 step 6), detection at scan end (spec 7.7), a second scan,
// and accept through the repository. The fixture files are hashed before and after: the scan and
// everything after it leave every byte of the library as it was (spec 2.1).
//
// The catalogue starts empty of targets: the scan creates every target from the frames. NGC 7000
// and IC 1396 resolve offline, the panel token stripped before every lookup (spec 9.3); IC 5070
// resolves offline with no token. No Sharpless table ships, so Sh2-155 resolves online, and the
// SIMBAD stub answers that one query.
//
// FILE SAFETY: tests/** only. The library is written and removed by this class, in its own temp
// folder. The network is stubbed: SIMBAD answers Sh2-155 only, SESAME misses cleanly.
public sealed class MosaicFixtureEndToEndTests : IDisposable
{
    private static readonly string[] AllLabels = ["Panel 1", "Panel 2", "Panel 3", "Panel 4"];

    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateSeededDatabase();
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-mosaic-e2e-").FullName;
    private readonly SettingsStore _settings;
    private readonly TargetResolver _resolver;
    private readonly MosaicRepository _repository;

    public MosaicFixtureEndToEndTests()
    {
        _settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        _resolver = new TargetResolver(
            _db.ConnectionString, StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(new SimbadStub()), new SesameClient(new SimbadStub()));
        _repository = new MosaicRepository(new DatabaseConnectionString(_db.ConnectionString));
        _settings.SaveGeneral(new GeneralSettings
        {
            ScanRoots = [_root],
            ScanFilters = ScanFilterConfig.Empty,
            IncludeCalibration = false,
            UseImagingNight = true,
            ObserverLongitude = 0.0,
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<ScanRunOutcome> RunScan()
    {
        var outcome = await new ScanCoordinator(_db.ConnectionString, _settings, _resolver,
                new ScanRunRepository(_db.ConnectionString), NullLogger<ScanCoordinator>.Instance)
            .RunAsync(ScanTrigger.Cli, null, CancellationToken.None);
        Assert.Equal("complete", outcome.State);
        return outcome;
    }

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private Dictionary<string, string> HashLibrary()
        => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    // What a suggestion is, without its row id and timestamp, for the re-scan comparison.
    private static string Shape(MosaicSuggestionRow row)
        => JsonSerializer.Serialize(new
        {
            row.SuggestedName, row.BaseName, row.Confidence, row.DiscoverySource, row.Flags, row.DedupSignature,
            Panels = row.Panels.Select(panel => new { panel.TargetId, panel.Label, panel.Dates }),
        });

    [Fact]
    public async Task Scan_OverTheMosaicFixture_WritesFrameFields_Suggests_Accepts_AndLeavesTheFilesByteIdentical()
    {
        var written = MosaicFixtureLibrary.Write(_root);
        var before = HashLibrary();
        Assert.Equal(written.Order(StringComparer.Ordinal), before.Keys.Order(StringComparer.Ordinal));

        // ---- the first scan: frame fields ---------------------------------------------
        var first = await RunScan();
        Assert.Equal(MosaicFixtureLibrary.FrameCount, first.Completed);
        using (var context = OpenRead())
        {
            var images = context.Images.ToList();
            Assert.Equal(MosaicFixtureLibrary.FrameCount, images.Count);
            foreach (var image in images)
            {
                var set = MosaicFixtureLibrary.Sets.Single(s => s.ObjectName == Path.GetFileName(Path.GetDirectoryName(image.FilePath)));
                Assert.Equal(set.ExpectedLabel, image.PanelLabel);
                Assert.Equal(set.Ra, image.RaDeg!.Value, 6);
                Assert.Equal(set.Dec, image.DecDeg!.Value, 6);
                Assert.Equal(MosaicFixtureLibrary.WidthPx, image.WidthPx);
            }

            // Every frame resolved, and the four sets of NGC 7000, IC 1396, Sh2-155 and IC 5070
            // landed on four distinct targets.
            Guid? TargetOf(string folder) => images
                .Where(image => Path.GetFileName(Path.GetDirectoryName(image.FilePath))!.StartsWith(folder, StringComparison.Ordinal))
                .Select(image => image.ResolvedTargetId).Distinct().Single();
            Assert.DoesNotContain(images, image => image.ResolvedTargetId is null);
            Assert.Equal(4, new[] { "NGC 7000", "IC 1396", "Sh2-155", "IC 5070" }.Select(TargetOf).Distinct().Count());
        }

        // ---- the first scan: suggestions ----------------------------------------------
        var pending = _repository.ListPending();

        var ngc = Assert.Single(pending, row => row.SuggestedName == "NGC 7000");
        Assert.Equal("high", ngc.Confidence);
        Assert.Equal("both", ngc.DiscoverySource);
        Assert.Empty(ngc.Flags);
        Assert.Equal(AllLabels, ngc.Panels.Select(panel => panel.Label));

        var sh2 = Assert.Single(pending, row => row.SuggestedName == "Sh2-155");
        Assert.Equal("low", sh2.Confidence);
        Assert.Contains(sh2.Flags, flag => flag.StartsWith("Positions not distinct", StringComparison.Ordinal));

        var ic = Assert.Single(pending, row => row.SuggestedName == "IC 1396");
        Assert.Equal("low", ic.Confidence);
        Assert.Equal("name", ic.DiscoverySource);
        Assert.Equal(["Only one panel found."], ic.Flags);

        // IC 5070 carries no token, so it is no candidate.
        Assert.DoesNotContain(pending, row => row.BaseName.Contains("5070", StringComparison.Ordinal));
        Assert.Equal(3, pending.Count);

        // ---- a second scan: the same suggestions, no relabel ----------------------------
        await RunScan();
        Assert.Equal(pending.Select(Shape), _repository.ListPending().Select(Shape));
        using (var context = OpenRead())
        {
            var runs = context.ActivityEvents
                .Where(e => e.EventType == "mosaic_detection_complete")
                .OrderBy(e => e.Id).ToList();
            Assert.Equal(2, runs.Count);
            using var details = JsonDocument.Parse(runs[1].Details!);
            Assert.Equal(0, details.RootElement.GetProperty("relabelled").GetInt32());
            Assert.Equal(pending.Count, details.RootElement.GetProperty("suggestions").GetInt32());
        }

        // ---- accept NGC 7000 --------------------------------------------------------
        var mosaicId = _repository.Accept(_repository.ListPending().Single(row => row.SuggestedName == "NGC 7000").Id, AllLabels);
        var detail = new MosaicQueries(new DatabaseConnectionString(_db.ConnectionString), new AliasMapCache(_settings))
            .Detail(mosaicId)!;

        Assert.Equal(AllLabels, detail.Panels.Select(panel => panel.Label).Order(StringComparer.Ordinal));
        var leading = detail.Panels.Max(panel => panel.IntegrationSeconds);
        Assert.Equal(MosaicFixtureLibrary.FullPanelSeconds, leading);
        var panel4 = detail.Panels.Single(panel => panel.Label == "Panel 4");
        Assert.Equal(MosaicFixtureLibrary.ShortPanelSeconds, panel4.IntegrationSeconds);
        Assert.Equal(leading / 2, panel4.DeficitSeconds);

        var shared1 = Assert.Single(detail.Panels.Single(p => p.Label == "Panel 1").Included, n => n.Date == MosaicFixtureLibrary.SharedNight);
        var shared2 = Assert.Single(detail.Panels.Single(p => p.Label == "Panel 2").Included, n => n.Date == MosaicFixtureLibrary.SharedNight);
        Assert.Equal("Panel 1", shared1.FrameLabel);
        Assert.Equal("Panel 2", shared2.FrameLabel);
        Assert.Equal(shared1.TargetId, shared2.TargetId);

        // ---- the fixture files are byte-identical -------------------------------------
        Assert.Equal(before, HashLibrary());
    }

    private sealed class SimbadStub : HttpMessageHandler
    {
        // SIMBAD finds Sh2-155 (the script query carries "SH 2-155" after spec 9.3's rewrite) and
        // reports a clean miss for anything else; SESAME's Resolver-less body is a clean miss.
        private static HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            string text;
            if (path.Contains("sim-script", StringComparison.Ordinal))
            {
                text = body.Contains("2-155", StringComparison.Ordinal) ? "::data::\nSH 2-155|HII|343.99|62.62\n" : "::error::\nnot found\n";
            }
            else if (path.Contains("sim-tap", StringComparison.Ordinal))
            {
                text = "id\n\"SH 2-155\"\n";
            }
            else
            {
                text = "<?xml version=\"1.0\"?><Sesame></Sesame>";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => Respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Respond(request));
    }
}
