using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels;

/// <summary>
/// Spec 11.4's "missing thumbnails render a placeholder that is replaced when generation
/// completes", as one view-model so the Target detail header (Task 6) and the preview modal
/// (Task 7) show the same three states through one type rather than two.
/// </summary>
/// <remarks>
/// <para>
/// Holds an Avalonia <c>Bitmap</c>, which owns unmanaged memory: this type is
/// <see cref="IDisposable"/> and every owner disposes it. It holds no brush; if it ever needs one
/// it holds an <c>ImmutableSolidColorBrush</c> (spec 14).
/// </para>
/// <para>
/// Single-threaded by contract: <see cref="Load"/> and <see cref="Dispose"/> are called from the
/// UI thread, and every callback returns to it through the post delegate. The one thing that runs
/// off it is the decode.
/// </para>
/// </remarks>
public sealed partial class ThumbnailSlotViewModel : ObservableObject, IDisposable
{
    private readonly string _framePath;
    private readonly ThumbnailWorker _worker;
    private readonly Func<string, byte[]?> _readBytes;
    private readonly Func<byte[], Bitmap?> _decode;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly bool _preview;

    private IDisposable? _request;
    private TaskCompletionSource? _pending;

    // Every completion carries the generation it was requested under, so a result for a
    // superseded request is dropped rather than assigned over a newer one. The withdraw handle
    // covers the common case; this covers the race the handle cannot, where the pump has already
    // snapshotted the callback and posted it.
    private int _generation;
    private bool _disposed;

    /// <param name="framePath">The frame this slot shows. One slot, one frame: stepping to
    /// another frame builds another slot, or the owner re-points its own.</param>
    /// <param name="worker">Spec 11.4's bounded queue. The slot never renders anything
    /// itself.</param>
    /// <param name="readBytes"><c>ThumbnailCache.ReadBytes</c>: turns the cache-relative path a
    /// render returns into the file's bytes, or null when the file is no longer there. The resolve
    /// and the read live together in the cache, so nothing here composes or opens a cache
    /// path.</param>
    /// <param name="kind"><see cref="ThumbnailKind.Frame"/> for spec 11.4's thumbnail,
    /// <see cref="ThumbnailKind.Preview"/> for spec 11.5's preview. A reference thumbnail is not
    /// requestable here: it is keyed on a target, and Task 6's pass produces it.</param>
    /// <param name="decode">How those bytes become a bitmap. A delegate so a test needs no JPEG;
    /// the default hands Avalonia a <c>MemoryStream</c>, never a filename (spec 2.1.2).</param>
    /// <param name="post">How to reach the UI thread; defaults to
    /// <see cref="UiPost.Default"/>.</param>
    /// <param name="logger">Optional; tests pass none.</param>
    public ThumbnailSlotViewModel(
        string framePath,
        ThumbnailWorker worker,
        Func<string, byte[]?> readBytes,
        ThumbnailKind kind = ThumbnailKind.Frame,
        Func<byte[], Bitmap?>? decode = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(framePath);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(readBytes);
        if (kind is not (ThumbnailKind.Frame or ThumbnailKind.Preview))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A slot shows a frame thumbnail or a preview.");
        }

        _framePath = framePath;
        _worker = worker;
        _readBytes = readBytes;
        _preview = kind == ThumbnailKind.Preview;
        _decode = decode ?? DecodeBytes;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Null until a render completes. The view binds its Image to this and its
    /// placeholder to <see cref="ShowsPlaceholder"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlaceholder))]
    public partial Bitmap? Image { get; private set; }

    /// <summary>True while a render is in flight. The view shows a spinner (spec 11.5's "Shows a
    /// spinner while generating").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlaceholder))]
    public partial bool IsLoading { get; private set; }

    /// <summary>True when there is no image and none is being loaded: the frame has no thumbnail
    /// and could not get one (spec 6.2.6), or none has been requested yet.</summary>
    // Spec 12.10's three states are exclusive: while a load is in flight the spinner owns the
    // panel, so the placeholder must not render underneath it.
    public bool ShowsPlaceholder => Image is null && !IsLoading;

    /// <summary>The in-flight load, so a test can await it instead of sleeping. Completes when
    /// the image has been assigned, when the render came back empty, and when the load is
    /// superseded or disposed.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>Requests a render through the worker and replaces <see cref="Image"/> when it
    /// completes. A second call withdraws the first request.</summary>
    public void Load()
    {
        if (_disposed)
        {
            return;
        }

        var (generation, completion) = BeginLoad();
        _request = _preview
            ? _worker.RequestPreview(_framePath, result => OnRendered(generation, completion, result))
            : _worker.RequestFrame(_framePath, result => OnRendered(generation, completion, result));
    }

    /// <summary>
    /// Loads a thumbnail the cache already holds, addressed by the stored cache-relative path,
    /// without going through the worker: spec 11.4's reference pass has already produced the file,
    /// and a path that is on disk has no business joining a render queue (Phase 8 Task 6, reused
    /// by Task 7's modal for a cached preview).
    /// </summary>
    /// <param name="cacheRelativePath">What the cache returned when it generated the file, as
    /// stored (<c>targets.reference_thumbnail_path</c>, spec 5.3).</param>
    /// <remarks>
    /// When the file is gone (the user relocated or emptied the cache, spec 11.3) this falls back
    /// to a worker request for the source frame the slot was constructed with, and when it was
    /// constructed with none it leaves the placeholder showing. That fallback is the one place the
    /// stored-path entry point and the on-demand one meet. The fallback issues a fresh
    /// <see cref="Load"/>, so <see cref="PendingLoad"/> then names the render's completion rather
    /// than this call's.
    /// </remarks>
    public void LoadExisting(string cacheRelativePath)
    {
        ArgumentNullException.ThrowIfNull(cacheRelativePath);
        if (_disposed)
        {
            return;
        }

        var (generation, completion) = BeginLoad();

        // No worker request at all: the bytes exist, so this is the decode half of OnRendered and
        // nothing else.
        DecodeAndApply(generation, completion, cacheRelativePath, fallBackToRender: true);
    }

    /// <summary>Withdraws any in-flight request and disposes the bitmap.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _request?.Dispose();
        _request = null;

        var bitmap = Image;
        Image = null;
        bitmap?.Dispose();

        IsLoading = false;
        _pending?.TrySetResult();
    }

    private void OnRendered(int generation, TaskCompletionSource completion, string? relativePath)
    {
        if (_disposed || generation != _generation)
        {
            // A completion that arrives after disposal or after a newer request is dropped, and
            // the bitmap it would have built is not built.
            completion.TrySetResult();
            return;
        }

        if (relativePath is null)
        {
            // Spec 6.2.6: an unrenderable frame keeps its placeholder, and the spinner stops.
            IsLoading = false;
            completion.TrySetResult();
            return;
        }

        DecodeAndApply(generation, completion, relativePath, fallBackToRender: false);
    }

    // The preamble both entry points share: withdraw whatever is in flight, release whoever was
    // awaiting it, take the next generation, and raise the spinner on the frame the call is made
    // rather than whenever a pump picks it up.
    private (int Generation, TaskCompletionSource Completion) BeginLoad()
    {
        _request?.Dispose();
        _request = null;
        _pending?.TrySetResult();

        var generation = ++_generation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;
        PendingLoad = completion.Task;
        IsLoading = true;
        return (generation, completion);
    }

    // Off the dispatcher: constructing a Bitmap from a 2400 px JPEG on the UI thread is a visible
    // stall, and the modal's next/previous stepping does it once per step.
    private void DecodeAndApply(
        int generation, TaskCompletionSource completion, string relativePath, bool fallBackToRender)
    {
        _ = Task.Run(() =>
        {
            Bitmap? bitmap = null;
            var missing = false;
            try
            {
                var bytes = _readBytes(relativePath);
                missing = bytes is null;
                bitmap = bytes is null ? null : _decode(bytes);
            }
            catch (Exception ex)
            {
                // A cache file deleted or truncated between the render and the read. The
                // placeholder stays; the next request regenerates it.
                _logger.LogWarning(ex, "Thumbnail could not be loaded from the cache: {Path}", relativePath);
            }

            _post(() =>
            {
                try
                {
                    // Spec 11.3's relocated or emptied cache: the stored path names nothing, so
                    // fall through to a render of the source frame if this slot has one.
                    if (fallBackToRender && missing && bitmap is null &&
                        !_disposed && generation == _generation && _framePath.Length > 0)
                    {
                        Load();
                        return;
                    }

                    Apply(generation, bitmap);
                }
                finally
                {
                    completion.TrySetResult();
                }
            });
        });
    }

    private void Apply(int generation, Bitmap? bitmap)
    {
        if (_disposed || generation != _generation)
        {
            bitmap?.Dispose();
            return;
        }

        // Replacing the image disposes the previous one: stepping through five hundred frames
        // otherwise holds five hundred decoded bitmaps until a collection that never comes.
        var previous = Image;
        Image = bitmap;
        previous?.Dispose();
        IsLoading = false;
    }

    // Avalonia is handed bytes the cache read, never a filename, for the reason SkiaSharp is not
    // (spec 2.1.2): a decoder that opens its own file handle is invisible to the file safety scan.
    private static Bitmap DecodeBytes(byte[] bytes) => new(new MemoryStream(bytes));
}
