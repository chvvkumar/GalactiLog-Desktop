using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Data.Tests.Ingest;

// The single-writer spine (spec 5.1, 10.6). Every test drives ScanWriter through a real
// channel against a real migrated SQLite database and a real TargetResolver; only the
// network is stubbed, and the default stub throws so an accidental online call fails loudly.
public class ScanWriterTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TestDatabaseHandle _db;
    private readonly string _catalogsDirectory;
    private readonly CatalogCacheRepository _cache;
    private readonly List<string> _warnings = new();

    public ScanWriterTests(ITestOutputHelper output)
    {
        _output = output;
        _db = TestDatabaseFactory.CreateSeededDatabase();
        _catalogsDirectory = StaticCatalogLoader.ResolveCatalogsDirectory();
        _cache = new CatalogCacheRepository(_db.ConnectionString);
    }

    public void Dispose() => _db.Dispose();

    // ---- harness -----------------------------------------------------------------

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return respond(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private static StubHttpMessageHandler ThrowingHandler()
        => new(request => throw new InvalidOperationException($"network access attempted: {request.RequestUri}"));

    private static HttpResponseMessage TextResponse(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    private TargetResolver MakeResolver(HttpMessageHandler? handler = null)
    {
        handler ??= ThrowingHandler();
        return new TargetResolver(_db.ConnectionString, _catalogsDirectory, _cache,
            new SimbadClient(handler), new SesameClient(handler));
    }

    private GalactiLogContext OpenWriterContext()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    // A second, independent connection: what it can see is what has actually been committed.
    private GalactiLogContext OpenReadContext()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private static ExtractedMetadata Meta(string? objectName) => new()
    {
        ObjectName = objectName,
        ImageType = "LIGHT",
        ExposureTime = 300.0,
        FilterUsed = "Ha",
        RawHeaders = new JsonObject { ["OBJECT"] = objectName, ["EXPTIME"] = 300.0 },
        Provenance = new Dictionary<string, string> { ["exposure_time"] = "header" },
    };

    private static ParsedRecord Ingest(string path, string? objectName = "M 31", ExtractedMetadata? metadata = null)
        => new(ParsedRecordKind.Ingest, path, Path.GetFileName(path), 4096, 1_700_000_000d,
            metadata ?? Meta(objectName), new DateTime(2025, 3, 15, 2, 0, 0, DateTimeKind.Utc),
            new DateOnly(2025, 3, 14), null);

    private static ParsedRecord Rejected(string path, string reason)
        => new(ParsedRecordKind.Rejected, path, Path.GetFileName(path), 10, 1d, null, null, null, reason);

    private static ParsedRecord Calibration(string path)
        => new(ParsedRecordKind.SkippedCalibration, path, Path.GetFileName(path), 10, 1d, null, null, null, null);

    private static ChannelReader<ParsedRecord> Filled(IEnumerable<ParsedRecord> records)
    {
        var channel = Channel.CreateUnbounded<ParsedRecord>();
        foreach (var record in records)
        {
            channel.Writer.TryWrite(record);
        }
        channel.Writer.Complete();
        return channel.Reader;
    }

    private ScanWriter MakeWriter(GalactiLogContext context, TargetResolver? resolver = null, int? parentId = null)
        => new(context, resolver ?? MakeResolver(), parentId, _warnings.Add);

    // ---- Phase 18 frame fields (spec 5.2, 10.3 step 3) -------------------------------

    [Fact]
    public async Task RunAsync_WritesTheGeometryColumns_AndThePanelLabelFromObject()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);
        var metadata = Meta("M 31 Panel 2") with { RaDeg = 10.684, DecDeg = 41.269, WidthPx = 6248 };

        // "M 31" first, so the panel name resolves offline through the target it creates.
        await writer.RunAsync(Filled([Ingest(@"C:\frames\m31.fits"), Ingest(@"C:\frames\m31-p2.fits", metadata: metadata)]),
            null, CancellationToken.None);

        using var read = OpenReadContext();
        var image = read.Images.Single(row => row.FileName == "m31-p2.fits");
        Assert.Equal((10.684, 41.269, 6248, "Panel 2"), (image.RaDeg!.Value, image.DecDeg!.Value, image.WidthPx!.Value, image.PanelLabel));
    }

    // The keywords are the run's own, and a frame that is not LIGHT carries no label.
    [Fact]
    public async Task RunAsync_PanelLabel_IsNullOffLight_AndFollowsTheRunsKeywords()
    {
        using (var context = OpenWriterContext())
        {
            await MakeWriter(context).RunAsync(Filled(
            [
                Ingest(@"C:\frames\0.fits"),
                Ingest(@"C:\frames\a.fits", metadata: Meta("M 31 Panel 3")),
                Ingest(@"C:\frames\b.fits", metadata: Meta("M 31 Panel 3") with { ImageType = "FLAT" }),
            ]), null, CancellationToken.None);
        }

        using (var context = OpenWriterContext())
        {
            await new ScanWriter(context, MakeResolver(), null, _warnings.Add, mosaicKeywords: [])
                .RunAsync(Filled([Ingest(@"C:\frames\c.fits", metadata: Meta("M 31 Panel 3"))]), null, CancellationToken.None);
        }

        using var read = OpenReadContext();
        Assert.Equal(
            new[] { null, (string?)"Panel 3", null, null },
            read.Images.OrderBy(image => image.FileName).Select(image => image.PanelLabel));
    }

    // ---- volume and identity ------------------------------------------------------

    [Fact]
    public async Task RunAsync_500SyntheticIngestRecords_Produces500ImageRows()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);
        var records = Enumerable.Range(0, 500).Select(i => Ingest($@"C:\frames\light-{i:D4}.fits")).ToList();

        var stopwatch = Stopwatch.StartNew();
        await writer.RunAsync(Filled(records), null, CancellationToken.None);
        stopwatch.Stop();

        _output.WriteLine($"500 records in {stopwatch.Elapsed.TotalSeconds:F2}s " +
            $"({500 / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001):F0} records/s)");

        Assert.Equal(500, writer.Completed);
        Assert.Equal(0, writer.Failed);
        Assert.Equal(0, writer.SkippedCalibration);
        using var read = OpenReadContext();
        Assert.Equal(500, read.Images.Count());
        Assert.Equal(500, read.Images.Count(i => i.ResolvedTargetId != null));
    }

    [Fact]
    public async Task RunAsync_SameObjectNameAcrossManyRecords_CreatesExactlyOneTarget()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);
        var records = Enumerable.Range(0, 50).Select(i => Ingest($@"C:\frames\m31-{i}.fits", "M 31")).ToList();

        await writer.RunAsync(Filled(records), null, CancellationToken.None);

        using var read = OpenReadContext();
        var target = Assert.Single(read.Targets);
        Assert.Equal(50, read.Images.Count(i => i.ResolvedTargetId == target.Id));
        // Exactly one target_created event too: the writer must not announce a create per frame.
        Assert.Single(read.ActivityEvents.Where(a => a.EventType == "target_created"));
    }

    [Fact]
    public async Task RunAsync_MixedNamesAndPanelVariants_ShareOneTarget()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);

        await writer.RunAsync(Filled(new[]
        {
            Ingest(@"C:\frames\a.fits", "M 31"),
            Ingest(@"C:\frames\b.fits", "M31"),
            Ingest(@"C:\frames\c.fits", "M 31 Panel 1"),
        }), null, CancellationToken.None);

        using var read = OpenReadContext();
        Assert.Single(read.Targets);
        Assert.Equal(3, read.Images.Count(i => i.ResolvedTargetId != null));
    }

    // ---- per-kind behaviour --------------------------------------------------------

    [Fact]
    public async Task RunAsync_RejectedRecord_IncrementsFailed_EmitsFileRejectedActivity_NoImageRow()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context, parentId: null);

        await writer.RunAsync(Filled(new[] { Rejected(@"C:\frames\bad.fits", "unsupported BITPIX: 12") }),
            null, CancellationToken.None);

        Assert.Equal(1, writer.Failed);
        Assert.Equal(0, writer.Completed);
        using var read = OpenReadContext();
        Assert.Empty(read.Images);
        var activity = Assert.Single(read.ActivityEvents);
        Assert.Equal("scan", activity.Category);
        Assert.Equal("file_rejected", activity.EventType);
        Assert.Equal("warning", activity.Severity);
        var details = JsonNode.Parse(activity.Details!)!;
        Assert.Equal(@"C:\frames\bad.fits", (string?)details["path"]);
        Assert.Equal("unsupported BITPIX: 12", (string?)details["reason"]);
    }

    [Fact]
    public async Task RunAsync_SkippedCalibrationRecord_IncrementsCompletedAndSkippedCalibration_NoImageRow()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);

        await writer.RunAsync(Filled(new[] { Calibration(@"C:\frames\dark.fits") }), null, CancellationToken.None);

        // Spec 7.5 / tasks_scan._do_ingest: counted as completed AND skipped_calibration.
        Assert.Equal(1, writer.Completed);
        Assert.Equal(1, writer.SkippedCalibration);
        Assert.Equal(0, writer.Failed);
        using var read = OpenReadContext();
        Assert.Empty(read.Images);
        Assert.Empty(read.ActivityEvents);
    }

    // Spec 5.21. Two records for one path in one batch: a failure here is either no row at all
    // (the frame is read again on every scan) or a unique violation from a second Add.
    [Fact]
    public async Task RunAsync_SkippedCalibrationRecord_WritesOneSkippedFilesRow()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);

        await writer.RunAsync(Filled(new[]
        {
            Calibration(@"C:\frames\dark.fits"),
            Calibration(@"C:\frames\dark.fits"),
        }), null, CancellationToken.None);

        using var read = OpenReadContext();
        var row = Assert.Single(read.SkippedFiles);
        Assert.Equal(@"C:\frames\dark.fits", row.FilePath);
        Assert.Equal(10, row.FileSize);
        Assert.Equal(1d, row.FileMtime);
        Assert.Equal("calibration", row.Reason);
        Assert.Empty(read.Images);
    }

    // Spec 5.21: a frame ingested after the user turns calibration on leaves the table. A failure
    // here leaves a row that the next calibration-excluded run would treat as a skipped frame.
    [Fact]
    public async Task RunAsync_IngestRecordForAPathWithASkippedRow_RemovesTheRow()
    {
        const string path = @"C:\frames\dark.fits";

        using (var first = OpenWriterContext())
        {
            await MakeWriter(first).RunAsync(Filled(new[] { Calibration(path) }), null, CancellationToken.None);
        }
        using (var second = OpenWriterContext())
        {
            await MakeWriter(second).RunAsync(Filled(new[] { Ingest(path, null) }), null, CancellationToken.None);
        }

        using var read = OpenReadContext();
        Assert.Single(read.Images);
        Assert.Empty(read.SkippedFiles);
    }

    [Fact]
    public async Task RunAsync_ExistingFilePath_UpdatesInPlace_DoesNotDuplicate()
    {
        const string path = @"C:\frames\same.fits";

        using (var first = OpenWriterContext())
        {
            await MakeWriter(first).RunAsync(Filled(new[] { Ingest(path, "M 31") }), null, CancellationToken.None);
        }

        var changed = Meta("M 42") with { ExposureTime = 600.0, FilterUsed = "OIII" };
        using (var second = OpenWriterContext())
        {
            await MakeWriter(second).RunAsync(Filled(new[] { Ingest(path, metadata: changed) }), null, CancellationToken.None);
        }

        using var read = OpenReadContext();
        var image = Assert.Single(read.Images);
        Assert.Equal(600.0, image.ExposureTime);
        Assert.Equal("OIII", image.FilterUsed);
        Assert.Equal(2, read.Targets.Count());
        Assert.Equal(read.Targets.Single(t => t.CatalogId == "M 42").Id, image.ResolvedTargetId);
    }

    [Fact]
    public async Task RunAsync_DuplicatePathWithinOneUnsavedBatch_UpsertsOnceNotTwice()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);

        await writer.RunAsync(Filled(new[]
        {
            Ingest(@"C:\frames\dup.fits", "M 31"),
            Ingest(@"C:\frames\dup.fits", "M 31"),
        }), null, CancellationToken.None);

        using var read = OpenReadContext();
        Assert.Single(read.Images);
    }

    [Fact]
    public async Task RunAsync_MapsMetadataProvenanceRawHeadersAndSessionDate()
    {
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);

        await writer.RunAsync(Filled(new[] { Ingest(@"C:\frames\full.fits", "M 31") }), null, CancellationToken.None);

        using var read = OpenReadContext();
        var image = Assert.Single(read.Images);
        Assert.Equal("full.fits", image.FileName);
        Assert.Equal(4096, image.FileSize);
        Assert.Equal(1_700_000_000d, image.FileMtime);
        Assert.Equal(new DateTime(2025, 3, 15, 2, 0, 0, DateTimeKind.Utc), image.CaptureDate!.Value);
        Assert.Equal(new DateOnly(2025, 3, 14), image.SessionDate);
        Assert.Equal("LIGHT", image.ImageType);
        Assert.Equal(300.0, image.ExposureTime);
        Assert.Equal("Ha", image.FilterUsed);
        Assert.Null(image.ThumbnailPath);
        Assert.Equal("M 31", (string?)JsonNode.Parse(image.RawHeaders!)!["OBJECT"]);
        Assert.Equal("header",
            JsonSerializer.Deserialize<Dictionary<string, string>>(image.Provenance!)!["exposure_time"]);
    }

    // ---- resolution outcomes -------------------------------------------------------

    [Fact]
    public async Task RunAsync_TargetCreated_EmitsTargetCreatedActivity_WithParentId()
    {
        using var seedContext = OpenWriterContext();
        seedContext.ActivityEvents.Add(new Entities.ActivityEvent
        {
            Timestamp = DateTime.UtcNow, Category = "scan", EventType = "scan_started", Message = "scan started",
        });
        seedContext.SaveChanges();
        var parentId = seedContext.ActivityEvents.Single().Id;

        using var context = OpenWriterContext();
        await MakeWriter(context, parentId: parentId)
            .RunAsync(Filled(new[] { Ingest(@"C:\frames\m31.fits", "M 31") }), null, CancellationToken.None);

        using var read = OpenReadContext();
        var created = Assert.Single(read.ActivityEvents.Where(a => a.EventType == "target_created"));
        Assert.Equal("enrichment", created.Category);
        Assert.Equal("info", created.Severity);
        Assert.Equal(parentId, created.ParentId);
        var details = JsonNode.Parse(created.Details!)!;
        Assert.Equal("M 31", (string?)details["catalog_id"]);
        Assert.Equal("offline", (string?)details["source"]);
        Assert.False(string.IsNullOrEmpty((string?)details["primary_name"]));
    }

    // Task 7 review ruling: one spelling of a stage across everything that stores or prints one.
    // This event used to derive its own with ToString().ToLowerInvariant(), which wrote
    // "solarsystem" here while the CLI resolve verb printed "solar_system" for the same stage.
    [Fact]
    public async Task RunAsync_SolarSystemTargetCreated_WritesTheSharedStageName()
    {
        var noMatch = new StubHttpMessageHandler(request => TextResponse(HttpStatusCode.OK,
            request.RequestUri!.Host == "simbad.cds.unistra.fr"
                ? "::error::\nnot found\n"
                : "<?xml version=\"1.0\"?><Sesame></Sesame>"));

        using var context = OpenWriterContext();
        await MakeWriter(context, MakeResolver(noMatch))
            .RunAsync(Filled(new[] { Ingest(@"C:\frames\jupiter.fits", "Jupiter") }), null, CancellationToken.None);

        using var read = OpenReadContext();
        var created = Assert.Single(read.ActivityEvents.Where(a => a.EventType == "target_created"));
        var details = JsonNode.Parse(created.Details!)!;
        Assert.Equal("solar_system", (string?)details["source"]);
        Assert.Equal(
            TargetResolver.StageName(TargetResolver.ResolutionStage.SolarSystem),
            (string?)details["source"]);
        Assert.Equal("Jupiter", (string?)details["primary_name"]);
    }

    [Fact]
    public async Task RunAsync_UnresolvedObjectName_EmitsResolutionFailedActivity()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.Host == "simbad.cds.unistra.fr"
            ? TextResponse(HttpStatusCode.OK, "::error::\nNo object found\n")
            : TextResponse(HttpStatusCode.OK, "<?xml version=\"1.0\"?><Sesame></Sesame>"));

        using var context = OpenWriterContext();
        var writer = MakeWriter(context, MakeResolver(handler));

        await writer.RunAsync(Filled(new[] { Ingest(@"C:\frames\x.fits", "zzzz not real") }), null, CancellationToken.None);

        using var read = OpenReadContext();
        var image = Assert.Single(read.Images);
        Assert.Null(image.ResolvedTargetId);
        Assert.Equal(1, writer.Completed); // an unresolvable name still produces a row.
        var failed = Assert.Single(read.ActivityEvents.Where(a => a.EventType == "resolution_failed"));
        Assert.Equal("enrichment", failed.Category);
        Assert.Equal("warning", failed.Severity);
        Assert.Equal("zzzz not real", (string?)JsonNode.Parse(failed.Details!)!["object_name"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_BlankObjectName_LeavesResolvedTargetIdNull_NoResolveCall(string? objectName)
    {
        var handler = ThrowingHandler();
        using var context = OpenWriterContext();
        var writer = MakeWriter(context, MakeResolver(handler));

        await writer.RunAsync(Filled(new[] { Ingest(@"C:\frames\blank.fits", objectName) }), null, CancellationToken.None);

        using var read = OpenReadContext();
        var image = Assert.Single(read.Images);
        Assert.Null(image.ResolvedTargetId);
        Assert.Equal(0, handler.CallCount);
        // No name was looked up, so this is not a resolution failure and emits nothing.
        Assert.Empty(read.ActivityEvents);
        Assert.Empty(read.CatalogCacheEntries.Where(c => c.Source == "resolver"));
    }

    // ---- batching, cancellation ----------------------------------------------------

    [Fact]
    public async Task RunAsync_SaveChangesBatching_CommitsEvery200AndOnceAtEnd()
    {
        var channel = Channel.CreateUnbounded<ParsedRecord>();
        var processed = 0;
        var reached201 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var context = OpenWriterContext();
        var writer = MakeWriter(context);

        // Blank OBJECT: no resolver work, so this test measures the batching rule alone.
        for (var i = 0; i < 250; i++)
        {
            channel.Writer.TryWrite(Ingest($@"C:\frames\batch-{i:D4}.fits", null));
        }

        var run = writer.RunAsync(channel.Reader, () =>
        {
            if (Interlocked.Increment(ref processed) == 201)
            {
                reached201.TrySetResult();
            }
        }, CancellationToken.None);

        // By the time record 201 has been processed, the batch save after record 200 has
        // already run; the channel is still open, so no end-of-run flush has happened yet.
        await reached201.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using (var read = OpenReadContext())
        {
            Assert.Equal(200, read.Images.Count());
        }

        channel.Writer.Complete();
        await run;

        using var final = OpenReadContext();
        Assert.Equal(250, final.Images.Count());
        Assert.Equal(250, writer.Completed);
    }

    [Fact]
    public async Task RunAsync_CancellationMidRun_FlushesPartialBatchAndStops()
    {
        var channel = Channel.CreateUnbounded<ParsedRecord>();
        for (var i = 0; i < 100; i++)
        {
            channel.Writer.TryWrite(Ingest($@"C:\frames\cancel-{i:D3}.fits", null));
        }

        using var cts = new CancellationTokenSource();
        var processed = 0;
        using var context = OpenWriterContext();
        var writer = MakeWriter(context);

        // Cancel well before the 200-record batch boundary, so the only thing that can have
        // committed rows is the flush on the cancellation path.
        await writer.RunAsync(channel.Reader, () =>
        {
            if (Interlocked.Increment(ref processed) == 10)
            {
                cts.Cancel();
            }
        }, cts.Token);

        using var read = OpenReadContext();
        var rows = read.Images.Count();
        Assert.InRange(rows, 10, 100);
        Assert.Equal(writer.Completed, rows);
        Assert.True(rows < 100, "cancellation should stop the drain before the whole channel is consumed");
        // Every row that exists is complete: no half-written record survived the stop.
        Assert.All(read.Images.ToList(), i => Assert.Equal("LIGHT", i.ImageType));
    }

    // ---- circuit breaker -----------------------------------------------------------

    // SIMBAD's sim-script endpoint carries the object name in the POST body, not the URL,
    // so a per-name stub is not possible here: this handler fails EVERY online call
    // transiently and counts them. That makes the assertion the sharp one -- after the
    // breaker trips, the call count must stop moving even though three more unresolvable
    // names follow.
    private static StubHttpMessageHandler AlwaysTransientlyFailingHandler()
        => new(_ => throw new HttpRequestException("simulated network failure"));

    // Spec 9.6: three attempts per source, SIMBAD then SESAME, for the one name that is
    // actually checked online before the breaker opens.
    private const int CallsForOneFullyRetriedName = 6;

    [Fact]
    public async Task RunAsync_TransientNetworkFailure_TripsCircuitBreaker_SkipsOnlineForRestOfRun()
    {
        var handler = AlwaysTransientlyFailingHandler();

        using var context = OpenWriterContext();
        var writer = MakeWriter(context, MakeResolver(handler));

        await writer.RunAsync(Filled(new[]
        {
            Ingest(@"C:\frames\a.fits", "first unresolvable"),
            Ingest(@"C:\frames\b.fits", "second unresolvable"),
            Ingest(@"C:\frames\c.fits", "third unresolvable"),
            Ingest(@"C:\frames\d.fits", "M 31"), // offline still works while the breaker is open.
        }), null, CancellationToken.None);

        Assert.True(writer.OnlineResolutionDisabled);
        // The second and third names never reached the network at all.
        Assert.Equal(CallsForOneFullyRetriedName, handler.CallCount);

        // Exactly one warning even though three records would each have re-tripped it.
        var breakerWarnings = _warnings.Where(w => w.Contains("SIMBAD/SESAME")).ToList();
        Assert.Single(breakerWarnings);
        Assert.Contains("rest of this scan run", breakerWarnings[0]);

        using var read = OpenReadContext();
        Assert.Equal(4, read.Images.Count());
        Assert.Equal(4, writer.Completed);
        // The offline-resolvable frame still got its target: the breaker disables the online
        // sources only, never the local catalog or cache.
        Assert.Single(read.Targets);
        Assert.NotNull(read.Images.Single(i => i.FileName == "d.fits").ResolvedTargetId);
        Assert.Null(read.Images.Single(i => i.FileName == "b.fits").ResolvedTargetId);
        Assert.Empty(read.ActivityEvents.Where(a => a.EventType == "resolution_failed"));
    }

    [Fact]
    public async Task RunAsync_CircuitBreakerTripped_WritesNoResolverNegativeCache()
    {
        var handler = AlwaysTransientlyFailingHandler();

        using var context = OpenWriterContext();
        var writer = MakeWriter(context, MakeResolver(handler));

        await writer.RunAsync(Filled(new[]
        {
            Ingest(@"C:\frames\a.fits", "trip the breaker"),
            Ingest(@"C:\frames\b.fits", "never checked at all"),
        }), null, CancellationToken.None);

        Assert.Equal(CallsForOneFullyRetriedName, handler.CallCount);
        // "not checked" must stay distinguishable from "checked and genuinely absent": a
        // negative row here would suppress a real query for 7 days once the network is back.
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "TRIP THE BREAKER").Kind);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, _cache.Get("resolver", "NEVER CHECKED AT ALL").Kind);
        // Neither frame emits resolution_failed: spec 10.9 fires that event when a name
        // "resolved to nothing and was negative-cached", and neither of these was ever
        // checked -- one failed transiently, the other was skipped by the breaker.
        using var read = OpenReadContext();
        Assert.Empty(read.ActivityEvents.Where(a => a.EventType == "resolution_failed"));
    }

    // ---- review fix items 1 and 2 ---------------------------------------------------

    [Fact]
    public async Task RunAsync_CancellationInsideResolve_WritesNoRowForThatFile()
    {
        using var cts = new CancellationTokenSource();

        // Cancels the scan from inside the online fetch, then throws the way a cancelled
        // HttpClient does. TargetResolver deliberately does not swallow that (it is not a
        // transient failure), so Resolve ends the record with an OperationCanceledException.
        var handler = new StubHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        using var context = OpenWriterContext();
        var writer = MakeWriter(context, MakeResolver(handler));

        await writer.RunAsync(Filled(new[]
        {
            Ingest(@"C:\frames\done.fits", "M 31"),              // resolves offline, no network
            Ingest(@"C:\frames\cancelled.fits", "zzq cancel me"), // cancelled inside Resolve
        }), null, cts.Token);

        using var read = OpenReadContext();
        // The cancelled record leaves NOTHING behind. A row written before resolution would
        // have been committed by the cancellation flush as a targetless image whose
        // Completed was never incremented -- and the next scan would skip it as unchanged.
        Assert.Equal("done.fits", Assert.Single(read.Images).FileName);
        Assert.Equal(1, writer.Completed);
        Assert.Equal(0, writer.Failed);
    }

    [Fact]
    public async Task RunAsync_SameUnresolvableObjectNameTwice_EmitsExactlyOneResolutionFailed()
    {
        // A clean "queried, found nothing" from both sources: the first frame negative-caches
        // the name, the second frame hits that cache at step 1.
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.Host == "simbad.cds.unistra.fr"
            ? TextResponse(HttpStatusCode.OK, "::error::\nNo object found\n")
            : TextResponse(HttpStatusCode.OK, "<?xml version=\"1.0\"?><Sesame></Sesame>"));

        using var context = OpenWriterContext();
        var writer = MakeWriter(context, MakeResolver(handler));

        await writer.RunAsync(Filled(new[]
        {
            Ingest(@"C:\frames\one.fits", "zzzz not real"),
            Ingest(@"C:\frames\two.fits", "zzzz not real"),
            Ingest(@"C:\frames\three.fits", "zzzz not real"),
        }), null, CancellationToken.None);

        using var read = OpenReadContext();
        Assert.Equal(3, read.Images.Count());
        Assert.Equal(3, read.Images.Count(i => i.ResolvedTargetId == null));
        // One event per NAME, not per frame: 50k frames sharing one bad OBJECT string must
        // not write 50k identical activity rows.
        Assert.Single(read.ActivityEvents.Where(a => a.EventType == "resolution_failed"));
    }
}
