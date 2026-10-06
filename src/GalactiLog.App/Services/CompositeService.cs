using System.Security.Cryptography;
using System.Text;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>One composite to build (spec 11.6): the mosaic, its canonical filter, every panel in
/// <c>sort_order</c>, the arranger's frame set and the geometry read of its best frames
/// (<c>PanelFrameQuery.Geometry</c>, ids not found absent).</summary>
public sealed record CompositeRequest(
    Guid MosaicId,
    string MosaicName,
    string Filter,
    IReadOnlyList<(Guid PanelId, string Label)> Panels,
    PanelFrameSet Frames,
    IReadOnlyDictionary<Guid, PanelGeometry> Geometry);

/// <summary>
/// Spec 11.6's composite build behind the lightbox, the port of the web's
/// <c>build_mosaic_composite</c> and its cache (<c>_compute_cache_key</c>, <c>_get_cached</c>,
/// <c>_set_cached</c>): the inclusion rules, a 20-entry in-memory cache (ruling R16), and on a miss
/// one cancellable <see cref="BuildJobKind"/> job off the UI thread with its Activity row
/// (ruling R13). Holds no database access: everything derives from the request.
/// </summary>
/// <param name="jobs">The job registry every build registers with.</param>
/// <param name="emit">(severity, event type, message, details): one <c>user_action</c> Activity
/// row, normally <c>ActivityRepository.EmitStandalone</c>.</param>
/// <param name="build">The drawing, normally <see cref="Compositor.Build"/>.</param>
public sealed class CompositeService(
    JobRegistry jobs,
    Action<string, string, string, object> emit,
    Func<CompositeSelection, IReadOnlyList<string>, Action<string>, CancellationToken, CompositeResult>? build = null,
    ILogger? logger = null)
{
    /// <summary>Spec 11.6: the registered job's kind, census member twenty.</summary>
    public const string BuildJobKind = "mosaic_composite";

    /// <summary>Spec 11.6, ruling R16: the most composites the cache holds.</summary>
    public const int CacheCap = 20;

    private static readonly PanelGeometry NoGeometry = new(null, null, null, null, null, null);

    private readonly Func<CompositeSelection, IReadOnlyList<string>, Action<string>, CancellationToken, CompositeResult> _build
        = build ?? Compositor.Build;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Dictionary<string, CompositeResult> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _inserted = new();

    /// <summary>Spec 11.6's inclusion rules for the request's filter: a panel with no best frame
    /// in it has a null <c>Frame</c>; otherwise the geometry of its best frame, all null when the
    /// read did not find it.</summary>
    public CompositeSelection Select(CompositeRequest request)
        => CompositeLayout.Select([.. request.Panels.Select(panel => new CompositePanel(
            panel.PanelId, panel.Label,
            Best(request, panel.PanelId) is { } best ? request.Geometry.GetValueOrDefault(best.ImageId) ?? NoGeometry : null))]);

    /// <summary>Spec 11.6's cache key, the port of <c>_compute_cache_key</c>: the SHA-256, as
    /// lowercase hexadecimal, of the UTF-8 <c>&lt;mosaic id&gt;:&lt;filter&gt;:&lt;ids&gt;</c>, the
    /// ids lowercase hyphenated, sorted ordinally and joined by commas.</summary>
    public static string CacheKey(Guid mosaicId, string filter, IEnumerable<Guid> includedBestFrameIds)
    {
        var ids = string.Join(",", includedBestFrameIds.Select(id => id.ToString("D")).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{mosaicId:D}:{filter}:{ids}")));
    }

    /// <summary>The cached composite for the request, if any (spec 11.6: a hit shows at once).</summary>
    public bool TryGetCached(CompositeRequest request, out CompositeResult result)
        => TryGet(KeyOf(request, Select(request)), out result);

    /// <summary>Spec 11.6's build: a cache hit returns at once with no job and no row. A miss runs
    /// one cancellable job off the UI thread reporting "Decoding &lt;label&gt;" per tile; success
    /// caches the result, finishes the job "&lt;w&gt; by &lt;h&gt; pixels" and writes
    /// <c>mosaic_composite_built</c>; a cancel caches nothing, writes no row and rethrows; any other
    /// failure caches nothing, finishes the job Failed, writes <c>mosaic_composite_failed</c> and
    /// rethrows.</summary>
    public async Task<CompositeResult> BuildAsync(CompositeRequest request, CancellationToken ct)
    {
        var selection = Select(request);
        var key = KeyOf(request, selection);
        if (TryGet(key, out var hit))
        {
            return hit;
        }

        List<string> framePaths = [.. selection.Included.Select(panel => Best(request, panel.PanelId)!.FilePath)];
        var count = framePaths.Count;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var job = jobs.Begin(BuildJobKind, $"Composite: {request.MosaicName}, {request.Filter}", () =>
        {
            // The monitor's cancel may arrive after the build ended and the source was disposed.
            try { cancel.Cancel(); } catch (ObjectDisposedException) { }
        });

        try
        {
            var done = 0;
            var result = await Task.Run(
                () => _build(selection, framePaths, label => job.Report($"Decoding {label}", 100.0 * done++ / count), cancel.Token),
                cancel.Token).ConfigureAwait(false);

            Store(key, result);
            job.Finish(JobResult.Succeeded, $"{result.Width} by {result.Height} pixels");
            Emit("info", "mosaic_composite_built",
                $"{request.MosaicName}, {request.Filter}: {result.Width} by {result.Height} pixels from {count} panels",
                new { mosaic_id = request.MosaicId, filter = request.Filter });
            return result;
        }
        catch (OperationCanceledException)
        {
            job.Finish(JobResult.Cancelled, "");
            throw;
        }
        catch (Exception ex)
        {
            job.Finish(JobResult.Failed, ex.Message);
            Emit("error", "mosaic_composite_failed", ex.Message,
                new { mosaic_id = request.MosaicId, filter = request.Filter, reason = ex.Message });
            throw;
        }
    }

    private static BestFrame? Best(CompositeRequest request, Guid panelId)
        => request.Frames.BestByPanel.TryGetValue(panelId, out var byFilter) && byFilter.TryGetValue(request.Filter, out var best)
            ? best : null;

    private static string KeyOf(CompositeRequest request, CompositeSelection selection)
        => CacheKey(request.MosaicId, request.Filter, selection.Included.Select(panel => Best(request, panel.PanelId)!.ImageId));

    private bool TryGet(string key, out CompositeResult result)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(key, out result!);
        }
    }

    private void Store(string key, CompositeResult result)
    {
        lock (_cache)
        {
            if (_cache.TryAdd(key, result))
            {
                _inserted.Enqueue(key);
                while (_inserted.Count > CacheCap)
                {
                    _cache.Remove(_inserted.Dequeue());
                }
            }
        }
    }

    // Guarded: a row the activity log could not take must not turn a build into a failure.
    private void Emit(string severity, string eventType, string message, object details)
    {
        try
        {
            emit(severity, eventType, message, details);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {EventType} event could not be written", eventType);
        }
    }
}
