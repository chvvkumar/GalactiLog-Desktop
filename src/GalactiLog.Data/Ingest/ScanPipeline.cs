using System.Threading.Channels;
using GalactiLog.Core;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Scanning;

namespace GalactiLog.Data.Ingest;

// The settings snapshot a scan run's reader tasks need, taken once at scan start (spec
// 10.6: "everything it needs is in the file and in the immutable settings snapshot").
public sealed record ScanReadOptions(
    bool IncludeCalibration = false,
    bool UseImagingNight = false,
    double? ObserverLongitude = null,
    string? ObserverTimezone = null);

// Live counts for the stages that run in parallel. Interlocked rather than a lock: the walk
// task and every reader task touch these concurrently, and a scan can read them mid-run to
// drive progress. The writer's own counters (Completed/Failed/SkippedCalibration) stay on
// ScanWriter, which is single-threaded by contract.
public sealed class ScanCounters
{
    private int _discovered;
    private int _parsed;

    public int Discovered => Volatile.Read(ref _discovered);
    public int Parsed => Volatile.Read(ref _parsed);

    internal void IncrementDiscovered() => Interlocked.Increment(ref _discovered);
    internal void IncrementParsed() => Interlocked.Increment(ref _parsed);
}

// Spec 10.6's structure, and nothing more: one walk task feeding an unbounded
// Channel<DiscoveredFile>, N reader tasks parsing into a bounded Channel<ParsedRecord>, and
// one ScanWriter draining it. No mediator, no pipeline framework, no per-stage abstraction:
// the whole concurrency model is the two channels and the task counts below.
//
// The coordinator (Phase 4 Task 5) owns the walk, the delta classification, the scan_runs
// row and the progress envelope, and calls this for the ingest stage only.
public static class ScanPipeline
{
    // Spec 10.6: Environment.ProcessorCount clamped to [2, 8]. CPU-bound parsing over
    // I/O-bound reads, so more readers than cores buys nothing and a single-core machine
    // still needs two so one blocked file read cannot stall the stage.
    public static int ReaderCount => Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>
    /// Runs the ingest stage to completion. Returns when every file in
    /// <paramref name="files"/> has been parsed and written, or throws
    /// <see cref="OperationCanceledException"/> once <paramref name="ct"/> fires -- in which
    /// case the writer has already flushed its partial batch, so the database is consistent
    /// and every frame already ingested stays ingested (spec 10.5).
    /// </summary>
    /// <param name="files">
    /// The new-or-changed files to ingest. Enumerated on the walk task, so a lazy
    /// <c>FileWalker.Walk</c> iterator overlaps discovery with parsing; a materialized list
    /// works equally well.
    /// </param>
    /// <param name="writer">
    /// The run's single writer, holding the one long-lived tracking context. Its counters
    /// are final once this method returns.
    /// </param>
    public static async Task RunAsync(
        IEnumerable<DiscoveredFile> files,
        ScanWriter writer,
        ScanReadOptions options,
        ScanCounters counters,
        Action? onRecordProcessed = null,
        Action<string>? onWarning = null,
        CancellationToken ct = default)
    {
        // Unbounded by design (coordinator ruling Q5): the discovered set is finite and
        // already known by the time the ingest stage starts, so bounding it would add
        // blocking for no backpressure benefit. The bound that matters is the record
        // channel below, between the readers and the writer.
        var discovery = Channel.CreateUnbounded<DiscoveredFile>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

        var records = Channel.CreateBounded<ParsedRecord>(
            new BoundedChannelOptions(ScanWriter.ChannelCapacity)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        // One CSV reader for the whole run: its per-file cache is a ConcurrentDictionary, so
        // frames from one folder landing on several readers still parse that folder's
        // Session Metadata CSV exactly once (spec 10.6).
        var csv = new NinaCsvReader();
        var imagingNightGate = new OnceGate();

        // Cancelled either by the caller or by a writer failure: without the second, a
        // faulted writer would stop draining the bounded channel and every reader would
        // block forever on a full WriteAsync.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        // Not Task.Run(..., token): if the token is already cancelled the task would never
        // start, and the finally that completes the discovery channel would never run.
        var walk = Task.Run(() =>
        {
            try
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    counters.IncrementDiscovered();
                    discovery.Writer.TryWrite(file); // unbounded: never fails.
                }
            }
            finally
            {
                discovery.Writer.TryComplete();
            }
        });

        var readers = new Task[ReaderCount];
        for (var i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(async () =>
            {
                await foreach (var file in discovery.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    var record = ScanRecordParser.Parse(
                        file, csv, options.IncludeCalibration, options.UseImagingNight,
                        options.ObserverLongitude, options.ObserverTimezone, imagingNightGate, onWarning);

                    counters.IncrementParsed();
                    await records.Writer.WriteAsync(record, token).ConfigureAwait(false);
                }
            });
        }

        var produce = ProduceAsync(walk, readers, records.Writer);

        var write = Task.Run(async () =>
        {
            try
            {
                await writer.RunAsync(records.Reader, onRecordProcessed, ct).ConfigureAwait(false);
            }
            finally
            {
                // Releases any reader blocked on a full channel, whether the writer finished
                // normally (producers are already done, so this is a no-op) or failed.
                linked.Cancel();
            }
        });

        var all = Task.WhenAll(produce, write);
        try
        {
            await all.ConfigureAwait(false);
        }
        catch when (write.IsFaulted)
        {
            // Surface the writer's real failure rather than the cancellation it induced in
            // the readers, which WhenAll may otherwise pick first.
            await write.ConfigureAwait(false);
            throw;
        }

        // A cancelled run must always surface as a cancellation, even when every producer
        // happened to finish before the token fired and only the writer stopped short.
        // Without this the outcome would depend on how full the channel was at the moment
        // of cancellation, and the coordinator could record a partial run as complete.
        ct.ThrowIfCancellationRequested();
    }

    private static async Task ProduceAsync(Task walk, Task[] readers, ChannelWriter<ParsedRecord> records)
    {
        try
        {
            await Task.WhenAll([walk, .. readers]).ConfigureAwait(false);
        }
        finally
        {
            // Completed without the exception on purpose: the writer drains what was already
            // queued and flushes it before this method's failure propagates, so a mid-run
            // fault still leaves the rows it produced committed.
            records.TryComplete();
        }
    }
}
