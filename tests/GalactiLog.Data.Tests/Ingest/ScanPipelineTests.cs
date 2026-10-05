using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// The whole spec 10.6 structure end to end: walk task -> unbounded Channel<DiscoveredFile>
// -> N reader tasks -> bounded Channel<ParsedRecord> -> one ScanWriter. Real files on disk,
// a real database, a stubbed network. This is tests/**, outside FileSafetyTest's src/**
// scan, so writing fixture files here is fine.
public class ScanPipelineTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly string _catalogsDirectory;
    private readonly CatalogCacheRepository _cache;
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-pipeline-").FullName;
    // ConcurrentBag, not List: onWarning is invoked from the walk task and from every
    // reader task, and List<T>.Add is not thread-safe.
    private readonly ConcurrentBag<string> _warnings = new();

    public ScanPipelineTests()
    {
        _db = TestDatabaseFactory.CreateSeededDatabase();
        _catalogsDirectory = StaticCatalogLoader.ResolveCatalogsDirectory();
        _cache = new CatalogCacheRepository(_db.ConnectionString);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    // ---- fixtures ------------------------------------------------------------------

    private const int FitsBlockSize = 2880;

    // A minimal, valid primary header: NAXIS = 0 means no pixel data, so one 2880-byte block
    // is a complete file as far as FitsHeaderReader is concerned (spec 6.1.5).
    private static byte[] MinimalFits(
        string? objectName, string imageType,
        string dateObs = "2025-03-15T02:00:00", double? siteLong = null)
    {
        var cards = new List<string>
        {
            "SIMPLE  =                    T",
            "BITPIX  =                   16",
            "NAXIS   =                    0",
            $"IMAGETYP= '{imageType,-8}'",
            $"DATE-OBS= '{dateObs}'",
            "EXPTIME =                300.0",
        };
        if (objectName is not null)
        {
            cards.Insert(3, $"OBJECT  = '{objectName,-8}'");
        }
        if (siteLong is { } longitude)
        {
            cards.Add($"SITELONG= {longitude.ToString("G", CultureInfo.InvariantCulture),20}");
        }
        cards.Add("END");

        var text = new StringBuilder();
        foreach (var card in cards)
        {
            text.Append(card.PadRight(80));
        }
        var blocks = (text.Length + FitsBlockSize - 1) / FitsBlockSize;
        return Encoding.ASCII.GetBytes(text.ToString().PadRight(blocks * FitsBlockSize));
    }

    private DiscoveredFile WriteFrame(
        string name, string? objectName = "M 31", string imageType = "LIGHT",
        string dateObs = "2025-03-15T02:00:00", double? siteLong = null)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, MinimalFits(objectName, imageType, dateObs, siteLong));
        var info = new FileInfo(path);
        return new DiscoveredFile(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds());
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A clean "queried, found nothing" from both sources, slowly. Not a failure, so
            // it never trips the circuit breaker -- it just stalls the writer, which is the
            // only thing this fixture is for.
            cancellationToken.WaitHandle.WaitOne(delay);
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.RequestUri!.Host == "simbad.cds.unistra.fr"
                ? "::error::\nNo object found\n"
                : "<?xml version=\"1.0\"?><Sesame></Sesame>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"network access attempted: {request.RequestUri}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private TargetResolver MakeResolver(HttpMessageHandler? handler = null)
        => new(_db.ConnectionString, _catalogsDirectory, _cache,
            new SimbadClient(handler ?? new ThrowingHandler()), new SesameClient(handler ?? new ThrowingHandler()));

    private GalactiLogContext OpenWriterContext()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private GalactiLogContext OpenReadContext()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    // ---- tests ---------------------------------------------------------------------

    [Fact]
    public void ReaderCount_IsProcessorCountClampedToTwoThroughEight()
    {
        // Spec 10.6's Environment.ProcessorCount clamped to [2, 8].
        Assert.InRange(ScanPipeline.ReaderCount, 2, 8);
        Assert.Equal(Math.Clamp(Environment.ProcessorCount, 2, 8), ScanPipeline.ReaderCount);
    }

    [Fact]
    public async Task RunAsync_DrivenByTheRealWalk_WritesOneRowPerDiscoveredFrame()
    {
        for (var i = 0; i < 40; i++)
        {
            WriteFrame($"light-{i:D3}.fits");
        }

        using var context = OpenWriterContext();
        var writer = new ScanWriter(context, MakeResolver(), null, _warnings.Add);
        var counters = new ScanCounters();
        var processed = 0;

        // A lazy FileWalker iterator, enumerated on the pipeline's walk task: discovery and
        // parsing genuinely overlap rather than being two sequential passes.
        var files = FileWalker.Walk([_root], [_root], ScanFilterConfig.Empty, null, _warnings.Add, CancellationToken.None);

        await ScanPipeline.RunAsync(files, writer, new ScanReadOptions(), counters,
            () => Interlocked.Increment(ref processed), _warnings.Add, CancellationToken.None);

        Assert.Equal(40, counters.Discovered);
        Assert.Equal(40, counters.Parsed);
        Assert.Equal(40, processed);
        Assert.Equal(40, writer.Completed);
        Assert.Equal(0, writer.Failed);

        using var read = OpenReadContext();
        Assert.Equal(40, read.Images.Count());
        // One target for all 40 frames: resolution runs only on the writer task, so the
        // check-then-insert sequence cannot interleave with itself (spec 10.6).
        Assert.Single(read.Targets);
        Assert.Equal(40, read.Images.Count(i => i.ResolvedTargetId != null));
    }

    [Fact]
    public async Task RunAsync_CalibrationFrames_AreCountedAndNotWritten()
    {
        var files = new List<DiscoveredFile>
        {
            WriteFrame("light.fits"),
            WriteFrame("bias.fits", imageType: "BIAS"),
            WriteFrame("dark.fits", imageType: "DARK"),
            WriteFrame("flat.fits", imageType: "FLAT"),
        };

        using var context = OpenWriterContext();
        var writer = new ScanWriter(context, MakeResolver(), null, _warnings.Add);

        await ScanPipeline.RunAsync(files, writer, new ScanReadOptions(IncludeCalibration: false),
            new ScanCounters(), null, _warnings.Add, CancellationToken.None);

        Assert.Equal(3, writer.SkippedCalibration);
        Assert.Equal(4, writer.Completed); // spec 7.5: skipped calibration still counts completed.
        Assert.Equal(0, writer.Failed);
        using var read = OpenReadContext();
        Assert.Equal("light.fits", Assert.Single(read.Images).FileName);
    }

    [Fact]
    public async Task RunAsync_IncludeCalibrationTrue_WritesCalibrationRows()
    {
        var files = new List<DiscoveredFile> { WriteFrame("bias.fits", objectName: null, imageType: "BIAS") };

        using var context = OpenWriterContext();
        var writer = new ScanWriter(context, MakeResolver(), null, _warnings.Add);

        await ScanPipeline.RunAsync(files, writer, new ScanReadOptions(IncludeCalibration: true),
            new ScanCounters(), null, _warnings.Add, CancellationToken.None);

        Assert.Equal(0, writer.SkippedCalibration);
        using var read = OpenReadContext();
        Assert.Equal("BIAS", Assert.Single(read.Images).ImageType);
    }

    [Fact]
    public async Task RunAsync_UnreadableFile_IsRejectedNotFatal()
    {
        var good = WriteFrame("good.fits");
        var missing = new DiscoveredFile(Path.Combine(_root, "gone.fits"), 0, 0);

        var bad = Path.Combine(_root, "bad.fits");
        File.WriteAllBytes(bad, Encoding.ASCII.GetBytes("not a fits file at all".PadRight(FitsBlockSize)));

        using var context = OpenWriterContext();
        var writer = new ScanWriter(context, MakeResolver(), null, _warnings.Add);

        await ScanPipeline.RunAsync(
            [good, missing, new DiscoveredFile(bad, new FileInfo(bad).Length, 0)],
            writer, new ScanReadOptions(), new ScanCounters(), null, _warnings.Add, CancellationToken.None);

        Assert.Equal(2, writer.Failed);
        Assert.Equal(1, writer.Completed);
        using var read = OpenReadContext();
        Assert.Single(read.Images);
        Assert.Equal(2, read.ActivityEvents.Count(a => a.EventType == "file_rejected"));
    }

    // Spec 10.3 step 3's frame-side counterpart of step 5 item 3, "One bad file never stops the
    // pass". DATE-OBS at DateTime.MinValue with SITELONG supplying a longitude makes spec 8.2's
    // imaging-night shift underflow inside ScanRecordParser.Parse. Before the choke point in
    // Parse, the throw faulted the reader task, Task.WhenAll rethrew out of RunAsync, and every
    // file the readers had not yet reached was never opened.
    //
    // The shape is RunAsync_UnreadableFile_IsRejectedNotFatal's, with its own file names: a
    // throw is routed into exactly the path a refused header already takes, and adds no
    // counter, column, event type or message format.
    [Fact]
    public async Task RunAsync_FrameWhoseParseThrows_IsOneFailedFile_AndTheRestIsStillCatalogued()
    {
        var files = new List<DiscoveredFile>();
        for (var i = 0; i < 8; i++)
        {
            files.Add(WriteFrame($"parse-throw-healthy-{i:D2}.fits"));
        }
        // Inserted in the middle so a reader has work both before and after it.
        files.Insert(4, WriteFrame(
            "parse-throw-bad.fits", objectName: null,
            dateObs: "0001-01-01T00:00:00", siteLong: 0.0));

        using var context = OpenWriterContext();
        var writer = new ScanWriter(context, MakeResolver(), null, _warnings.Add);
        var counters = new ScanCounters();

        await ScanPipeline.RunAsync(files, writer, new ScanReadOptions(UseImagingNight: true),
            counters, null, _warnings.Add, CancellationToken.None);

        Assert.Equal(1, writer.Failed);
        Assert.Equal(8, writer.Completed);
        // Parsed is incremented after Parse returns, so the bad file counts only because the
        // call came back with a record instead of throwing.
        Assert.Equal(9, counters.Parsed);

        using var read = OpenReadContext();
        Assert.Equal(8, read.Images.Count());
        Assert.DoesNotContain("parse-throw-bad.fits", read.Images.Select(i => i.FileName).ToList());

        var rejected = Assert.Single(read.ActivityEvents.Where(a => a.EventType == "file_rejected").ToList());
        Assert.Equal("warning", rejected.Severity);
        Assert.Contains("parse-throw-bad.fits", rejected.Message);
        Assert.Contains(
            "unexpected error while reading the file: ArgumentOutOfRangeException: ", rejected.Message);
        Assert.DoesNotContain("   at ", rejected.Message);
    }

    [Fact]
    public async Task RunAsync_SlowResolver_AppliesBackpressure_ParsedStaysBounded()
    {
        const int fileCount = 400;
        var files = new List<DiscoveredFile>(fileCount);
        for (var i = 0; i < fileCount; i++)
        {
            // A distinct unresolvable name per frame, so every single record makes the
            // writer pay the (stubbed, slow) online round trip: nothing is cache-shortcut.
            files.Add(WriteFrame($"slow-{i:D4}.fits", $"zzq unresolvable {i:D4}"));
        }

        using var context = OpenWriterContext();
        var writer = new ScanWriter(context, MakeResolver(new DelayingHandler(TimeSpan.FromMilliseconds(20))),
            null, _warnings.Add);
        var counters = new ScanCounters();
        var processed = 0;

        using var cts = new CancellationTokenSource();
        var run = ScanPipeline.RunAsync(files, writer, new ScanReadOptions(), counters,
            () => Interlocked.Increment(ref processed), _warnings.Add, cts.Token);

        // Wait until the readers have clearly outrun the writer and filled the channel.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (counters.Parsed < ScanWriter.ChannelCapacity && DateTime.UtcNow < deadline && !run.IsCompleted)
        {
            await Task.Delay(20);
        }

        // Read Parsed first, then the writer's count: any drift between the two reads can
        // only make the observed slack smaller, never larger, so the assertion cannot pass
        // by luck.
        var parsed = counters.Parsed;
        var written = Volatile.Read(ref processed);
        // In flight at any instant: whatever the channel holds (at most ChannelCapacity),
        // the one record the writer has taken but not yet reported, and at most one per
        // reader parsed but not yet handed over. Nothing else can be in memory -- that is
        // exactly what the bounded channel buys.
        var ceiling = written + 1 + ScanWriter.ChannelCapacity + ScanPipeline.ReaderCount;

        Assert.True(parsed >= ScanWriter.ChannelCapacity,
            $"expected the readers to fill the bounded channel; parsed={parsed}");
        Assert.True(parsed <= ceiling,
            $"readers outran the writer past the channel bound: parsed={parsed}, written={written}, ceiling={ceiling}");
        Assert.True(parsed < fileCount,
            $"the readers should still be blocked on a full channel, not finished; parsed={parsed}");

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_CancellationMidRun_LeavesRowsConsistent()
    {
        const int fileCount = 200;
        var files = new List<DiscoveredFile>(fileCount);
        for (var i = 0; i < fileCount; i++)
        {
            files.Add(WriteFrame($"cancel-{i:D4}.fits", $"zzq cancel {i:D4}"));
        }

        using var context = OpenWriterContext();
        var writer = new ScanWriter(context, MakeResolver(new DelayingHandler(TimeSpan.FromMilliseconds(20))),
            null, _warnings.Add);
        var counters = new ScanCounters();
        var processed = 0;

        using var cts = new CancellationTokenSource();
        var run = ScanPipeline.RunAsync(files, writer, new ScanReadOptions(), counters,
            () =>
            {
                if (Interlocked.Increment(ref processed) == 5)
                {
                    cts.Cancel();
                }
            }, _warnings.Add, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        using var read = OpenReadContext();
        var rows = read.Images.Count();

        // Spec 10.5: every frame already ingested stays ingested, nothing partial survives,
        // and the run stopped well short of the whole set.
        Assert.Equal(writer.Completed, rows);
        Assert.InRange(rows, 5, fileCount - 1);
        Assert.All(read.Images.ToList(), i =>
        {
            Assert.Equal("LIGHT", i.ImageType);
            Assert.Equal(new DateOnly(2025, 3, 15), i.SessionDate);
            Assert.False(string.IsNullOrEmpty(i.RawHeaders));
        });
    }

    [Fact]
    public async Task RunAsync_ImagingNightWithNoLongitude_WarnsOnceAcrossEveryReader()
    {
        for (var i = 0; i < 60; i++)
        {
            WriteFrame($"night-{i:D3}.fits");
        }

        using var context = OpenWriterContext();
        var writer = new ScanWriter(context, MakeResolver(), null, _warnings.Add);
        var warnings = new ConcurrentBag<string>();

        await ScanPipeline.RunAsync(
            FileWalker.Walk([_root], [_root], ScanFilterConfig.Empty, null, warnings.Add, CancellationToken.None),
            writer, new ScanReadOptions(UseImagingNight: true), new ScanCounters(), null,
            warnings.Add, CancellationToken.None);

        // One OnceGate for the run, shared by every reader task (spec 8.3, 16.1).
        Assert.Single(warnings, w => w.Contains("use_imaging_night"));
        Assert.Equal(60, writer.Completed);
    }
}
