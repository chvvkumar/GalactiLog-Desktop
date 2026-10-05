using GalactiLog.Core.Imaging;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>Spec 11.3's three kinds, with their widths, formats, qualities and path shapes.</summary>
public enum ThumbnailKind
{
    /// <summary><c>general.thumbnail_width</c>, JPEG 85, <c>frames/&lt;key&gt;.jpg</c>.</summary>
    Frame,

    /// <summary><c>general.preview_resolution</c> (0 means native), JPEG 90,
    /// <c>previews/&lt;key&gt;.jpg</c>. The only kind the LRU sweep touches.</summary>
    Preview,

    /// <summary>800 px fixed, JPEG 85, <c>reference/&lt;target id&gt;.jpg</c>.</summary>
    Reference,
}

/// <summary>
/// Spec 11.3's on-disk thumbnail cache. Owns the path shapes, the key, the render-on-miss and the
/// <c>previews</c>-only LRU sweep. The only type in the application that writes a thumbnail, and
/// every one of its writes and deletes goes through <see cref="AppWriter"/>, whose path check makes
/// anything outside the app data directory and the configured cache root unreachable (spec 2.1.1).
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe, and the one place spec 10.6's concurrency budget is enforced. Task 5's worker runs
/// two pumps and the reference pass runs on the scan thread, which is a third caller that never
/// passes through the worker, so the budget cannot live in the pump count: every render in the
/// application goes through <see cref="Ensure"/>, and a <see cref="SemaphoreSlim"/> of
/// <see cref="ThumbnailWorker.MaxConcurrency"/> permits here is the single definition
/// (design-lessons rule 2 applied to a resource budget).
/// </para>
/// <para>
/// Two threads asking for the <b>same</b> key are serialized on a per-key gate, and the hit check
/// runs inside it, so the second caller waits for the first render and then takes the hit. They do
/// not race benignly: <c>File.WriteAllBytes</c> opens with <c>FileMode.Create</c> and
/// <c>FileShare.Read</c>, so a second concurrent write to one path throws <c>IOException</c>, and
/// a <c>File.Exists</c> between the first writer's create and its last byte would hand the second
/// caller a truncated JPEG. Different keys stay fully parallel: the gate is per relative path, not
/// global.
/// </para>
/// <para>
/// Lock order, one way only: render permit, then key gate, then sweep gate. The permit is taken
/// <b>outside</b> the key gate, never inside it, because two callers for one key would otherwise
/// deadlock against the budget (the first holds the gate while waiting for a permit the second
/// holds while waiting for the gate). The eviction sweep is serialized under its own lock so two
/// sweeps cannot double-count, and a thread holding a key gate may take the sweep gate but never
/// the reverse: <see cref="EvictPreviews()"/> takes the sweep gate holding nothing else.
/// </para>
/// <para>
/// Relocation (spec 11.3): the cache root is read from <see cref="AppWriter.ThumbnailCacheRoot"/>
/// on every call, never captured at construction. Changing it moves nothing and deletes nothing;
/// the new location is populated on demand and the old one is left alone.
/// </para>
/// </remarks>
/// <param name="writer">The one writer and deleter. Every byte this class puts on disk and every
/// file it removes goes through it.</param>
/// <param name="general">Reads <c>thumbnail_width</c>, <c>preview_resolution</c> and
/// <c>preview_cache_mb</c> at call time, not at construction: they are read on a background worker
/// after the user changes them in Settings, and a stale bound would keep evicting to the old cap
/// forever. <c>AppHost</c> passes a memo refreshed on <c>SettingsStore.GeneralChanged</c>, because
/// <c>SettingsStore.GetGeneral</c> is a SQLite read on every call.</param>
/// <param name="render">Normally <c>ThumbnailRenderer.Render</c>. A delegate so these paths can be
/// tested without decoding a frame.</param>
/// <param name="logger">Optional; tests pass none.</param>
public sealed class ThumbnailCache(
    AppWriter writer,
    Func<GeneralSettings> general,
    Func<string, int, int, RenderMode, CancellationToken, RenderResult> render,
    ILogger? logger = null)
{
    /// <summary>Spec 11.3: frame thumbnails and reference thumbnails are JPEG quality 85.</summary>
    public const int ThumbnailJpegQuality = 85;

    /// <summary>Spec 11.3: previews are JPEG quality 90. The web application writes 85 here
    /// (<c>preview.generate_preview</c>); the spec's 90 is the port's figure (questions.md
    /// Q5).</summary>
    public const int PreviewJpegQuality = 90;

    /// <summary>Spec 11.3: reference thumbnails are 800 px wide, a fixed figure and not
    /// <c>general.thumbnail_width</c>.</summary>
    public const int ReferenceWidth = 800;

    /// <summary>The web application's eviction floor, ported (questions.md Q8):
    /// <c>preview_cache_mb</c> has no validation, and a value of 0 or 1 would otherwise evict every
    /// preview the instant it is written and turn the modal into a render loop.</summary>
    public const int MinimumCacheMb = 100;

    private const string FramesDirectory = "frames";
    private const string PreviewsDirectory = "previews";
    private const string ReferenceDirectory = "reference";

    /// <summary>
    /// Every flat subdirectory under the cache root this type writes into, derived from the one
    /// kind-to-directory mapping (<c>DirectoryFor</c>) rather than listed a second time, so a
    /// fourth <see cref="ThumbnailKind"/> is included here the moment it has a directory.
    /// </summary>
    /// <remarks>
    /// Exposed for spec 12.5's "thumbnail cache bytes" figure, which <c>AppHost</c> sums through
    /// <c>AppWriter.EnumerateThumbnailFiles</c>. That helper used to repeat the three literals with
    /// a note saying a fourth kind would be silently omitted from the total, and a quietly low
    /// number on a storage readout is not something anyone notices. Widening this type's surface by
    /// one derived list is the smaller cost (fixer list code item 7, design-lessons rule 2).
    /// </remarks>
    public static IReadOnlyList<string> Directories { get; } =
        [.. Enum.GetValues<ThumbnailKind>().Select(DirectoryFor)];

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Lock _sweepGate = new();

    // Spec 10.6's budget, enforced here rather than in ThumbnailWorker's pump count, because the
    // reference pass renders on the scan thread and never enters the worker: with the budget in
    // the pumps, a scan running while the preview modal is open performs three concurrent
    // full-frame decodes. Taken outside the per-key gate (see the lock order in the class
    // remarks). Never disposed: this cache lives as long as the process, and SemaphoreSlim only
    // allocates a wait handle for the AvailableWaitHandle property, which nothing here uses.
    private readonly SemaphoreSlim _renderPermits = new(ThumbnailWorker.MaxConcurrency);

    // ponytail: last use is tracked in this process only, and a relative path with no entry here
    // sorts oldest (questions.md Q7). Ceiling: on a fresh start the sweep evicts the previous
    // session's entries before anything this session has touched, so LRU spans one process, not
    // the cache's lifetime. Upgrade path: persist the stamps (the web application uses a Redis
    // sorted set), which is a store this port declines to add. A monotonic counter rather than
    // Environment.TickCount64: the tick clock has a ~15 ms resolution, so a worker rendering a
    // burst of previews would stamp several of them identically and the sweep would fall back to
    // the LastWriteTimeUtc tie-break for entries whose use order it actually knows.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastUse =
        new(StringComparer.OrdinalIgnoreCase);
    private long _useStamp;

    // One gate per relative path, held for the whole hit-check-render-write sequence, so two
    // callers that computed the same key do not both render and do not both open the same file for
    // writing. Refcounted and removed when the last waiter leaves, so the dictionary holds only
    // the keys currently in flight rather than one entry per thumbnail this process ever produced.
    private readonly Lock _keyGatesLock = new();
    private readonly Dictionary<string, KeyGate> _keyGates = new(StringComparer.OrdinalIgnoreCase);

    private sealed class KeyGate
    {
        public Lock Gate { get; } = new();
        public int Waiters { get; set; }
    }

    /// <summary>Returns the path of an existing or newly rendered frame thumbnail, relative to the
    /// cache root for storage. Null when the frame cannot be rendered (spec 6.1.5, 6.2.6): the
    /// caller shows a placeholder.</summary>
    public string? EnsureFrame(string framePath, CancellationToken ct)
        => Ensure(ThumbnailKind.Frame, framePath, targetId: null, force: false, ct);

    /// <summary>As <see cref="EnsureFrame"/>, at <c>general.preview_resolution</c> (0 means
    /// native), quality 90, under <c>previews/</c>. Runs <see cref="EvictPreviews"/> after a
    /// successful generation (spec 11.3).</summary>
    public string? EnsurePreview(string framePath, CancellationToken ct)
        => Ensure(ThumbnailKind.Preview, framePath, targetId: null, force: false, ct);

    /// <summary>Spec 11.3's <c>reference/&lt;target id&gt;.jpg</c>. Keyed on the target, not on the
    /// frame, so a target's reference thumbnail has one stable path across regenerations.</summary>
    /// <param name="force">True deletes the existing file and renders again, which is what spec
    /// 12.7's regenerate action needs: the path is keyed on the target, so without this the hit
    /// check serves last run's file and a forced pass produces no new pixels. False takes the hit,
    /// like every other kind.</param>
    public string? EnsureReference(Guid targetId, string framePath, bool force, CancellationToken ct)
        => Ensure(ThumbnailKind.Reference, framePath, targetId, force, ct);

    /// <summary>Absolute path for a stored relative path, for loading. Null when the file is not on
    /// disk (the user relocated the cache, or emptied it by hand), so the caller shows a
    /// placeholder and the next request regenerates it. The one way a stored relative path becomes
    /// something to load: nothing outside this class combines the cache root with a stored
    /// path.</summary>
    public string? ResolveExisting(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        try
        {
            var absolute = writer.ResolveThumbnailPath(relativePath);
            if (!File.Exists(absolute))
            {
                return null;
            }
            // Previews only: nothing else is ever swept, so a frame or reference key recorded here
            // would sit in the dictionary for the life of the process and never be read.
            var normalized = relativePath.Replace('\\', '/');
            if (normalized.StartsWith($"{PreviewsDirectory}/", StringComparison.OrdinalIgnoreCase))
            {
                Touch(normalized);
            }
            return absolute;
        }
        catch (UnauthorizedPathException ex)
        {
            _logger.LogWarning(ex, "Stored thumbnail path is outside the cache root: {Path}", relativePath);
            return null;
        }
    }

    /// <summary>Reads a stored thumbnail's bytes, or null when the file is not on disk or the
    /// stored path is not under the cache root. The read lives next to
    /// <see cref="ResolveExisting"/> deliberately: it is the only raw filesystem read of a cache
    /// file in the application, and keeping it here means no caller can read a path that did not
    /// come through <c>AppWriter.ResolveThumbnailPath</c>. Callers are handed bytes, never a
    /// filename, for the reason SkiaSharp is (spec 2.1.2).</summary>
    public byte[]? ReadBytes(string relativePath)
    {
        var absolute = ResolveExisting(relativePath);
        return absolute is null ? null : File.ReadAllBytes(absolute);
    }

    /// <summary>
    /// Spec 11.3's LRU sweep, bounded by <c>max(general.preview_cache_mb, 100)</c> MB, over
    /// <c>previews/</c> only. Called after each preview generation, and at application start off
    /// the dispatcher (Task 5 adds that call in <c>Program.Main</c>, questions.md Q9). Returns the
    /// number of files deleted.
    /// </summary>
    /// <remarks><c>frames</c> and <c>reference</c> are never enumerated and never deleted here:
    /// spec 11.3 says they are small, and regenerating them during scrolling would be
    /// visible.</remarks>
    public int EvictPreviews() => EvictPreviews(justWritten: null);

    /// <param name="justWritten">The relative path this sweep's caller has just produced, exempt
    /// from eviction. A preview larger than the whole bound would otherwise be deleted by the
    /// sweep its own generation triggers, and the caller would be handed a path to a file that no
    /// longer exists. It is the newest entry by definition, so exempting it changes nothing for
    /// any other caller.</param>
    private int EvictPreviews(string? justWritten)
    {
        var cap = (long)Math.Max(general().PreviewCacheMb, MinimumCacheMb) * 1024 * 1024;

        // One sweep at a time. Two previews finishing at once must not both decide to delete the
        // same file: AppWriter.Delete is idempotent on a missing file, but the byte accounting is
        // not, and a double count would evict twice as much as the bound asks for.
        lock (_sweepGate)
        {
            var entries = writer.EnumerateThumbnailFiles(PreviewsDirectory, "*.jpg")
                // A belt, not a fix for a live defect: Directory.EnumerateFiles defaults to
                // MatchType.Simple, which does not implement the Win32 three-character rule, so
                // "*.jpg" does not match x.jpga on .NET 10 (verified with a scratch program during
                // Task 4's fix pass). This filter is what keeps that true if the enumeration ever
                // moves to MatchType.Win32. This cache writes nothing but .jpg, and a file it did
                // not write is not its to delete.
                .Where(file => Path.GetExtension(file.Path).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                .Select(file => new
                {
                    file.Path,
                    file.Length,
                    Relative = RelativeOf(PreviewsDirectory, file.Path),
                    LastUse = _lastUse.TryGetValue(RelativeOf(PreviewsDirectory, file.Path), out var stamp)
                        ? stamp
                        : long.MinValue,
                    file.LastWriteUtc,
                })
                .OrderBy(entry => entry.LastUse)
                .ThenBy(entry => entry.LastWriteUtc)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ToList();

            var total = entries.Sum(entry => entry.Length);
            var deleted = 0;

            // preview_cache.py loops "while over the cap, take the oldest" and needs an explicit
            // no-progress break, because a stale size record makes it re-pick the same entry
            // forever. This walks a snapshot oldest-first instead, so each candidate is considered
            // once and the sweep terminates by construction; a file that refuses deletion is
            // skipped rather than retried.
            foreach (var entry in entries)
            {
                if (total <= cap)
                {
                    break;
                }

                // Counted in the total above, because it does occupy the cache, but never deleted:
                // the caller is holding this path.
                if (justWritten is not null &&
                    entry.Relative.Equals(justWritten, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    writer.Delete(entry.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Open in a viewer, or held by an antivirus scan. Not a reason to abandon the
                    // sweep: the next oldest is still a candidate.
                    _logger.LogDebug(ex, "Preview could not be evicted: {Path}", entry.Path);
                    continue;
                }

                _lastUse.TryRemove(entry.Relative, out _);
                total -= entry.Length;
                deleted++;
            }

            return deleted;
        }
    }

    /// <summary>Deletes every cached file of one kind. The only bulk delete outside
    /// <see cref="EvictPreviews()"/>, and the only path composition for a purge, so no caller ever
    /// builds a cache path of its own (design-lessons rule 2, and <c>TRACKING.md</c> section 6
    /// item 21).</summary>
    /// <remarks>
    /// <para>
    /// Spec 12.7's "purge and regenerate" frame-thumbnail action, and nothing else calls it. It
    /// deletes files and writes no database row: <c>images.thumbnail_path</c> is never written
    /// (spec 5.2), so there is nothing to clear, and on-demand generation refills the directory.
    /// </para>
    /// <para>
    /// Every delete goes through <see cref="AppWriter.Delete"/>, whose path check throws
    /// <c>UnauthorizedPathException</c> for anything outside the app data directory and the
    /// thumbnail cache root. The roadmap's "deletes nothing outside the cache root" guarantee is
    /// therefore structural, not a property of this enumeration being correct: even a caller that
    /// somehow handed this method a path elsewhere could not delete it.
    /// </para>
    /// <para>
    /// A render permit and then the per-key gate are taken per file, in that order and never the
    /// reverse (see the lock order in the class remarks), so a purge cannot remove a file another
    /// thread is in the middle of writing, and a render that starts after the purge has passed
    /// that key simply repopulates it. The eviction sweep's own gate is deliberately not taken:
    /// holding it across the key gates would invert the documented order, and
    /// <see cref="AppWriter.Delete"/> is idempotent, so a sweep and a purge racing over one
    /// preview cost nothing but the file's existence check below.
    /// </para>
    /// <para>
    /// <see cref="ThumbnailKind.Preview"/> is not offered on the Maintenance tab, because eviction
    /// already bounds that directory, but this member takes the kind anyway: a kind-specific
    /// member with one legal value is a member with a hidden assumption.
    /// </para>
    /// </remarks>
    /// <param name="ct">Checked before every file, so closing the Settings page really does stop a
    /// purge already walking a large <c>frames</c> directory: <c>Task.Run</c> checks a token only
    /// before the delegate is scheduled (review minor 5). Cancelling keeps every file already
    /// deleted, which is the same contract the eviction sweep and the reference pass have.</param>
    /// <returns>How many files were deleted. A file that is already gone is not counted and is not
    /// an error.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not one of spec
    /// 11.3's three kinds. A bulk delete refuses what it does not recognize.</exception>
    public int Purge(ThumbnailKind kind, CancellationToken ct = default)
    {
        var directory = DirectoryFor(kind);

        // A snapshot, oldest-first is irrelevant here: every file of this kind is a candidate.
        // Enumerated through AppWriter so the root is resolved by the same authorization every
        // write goes through, exactly as EvictPreviews does, and filtered on the extension for the
        // reason EvictPreviews states: this cache writes nothing but .jpg, and a file it did not
        // write is not its to delete.
        var files = writer.EnumerateThumbnailFiles(directory, "*.jpg")
            .Where(file => Path.GetExtension(file.Path).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var deleted = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = RelativeOf(directory, file.Path);

            _renderPermits.Wait(ct);
            try
            {
                var gate = AcquireGate(relative);
                try
                {
                    lock (gate.Gate)
                    {
                        if (!File.Exists(file.Path))
                        {
                            // Evicted, or removed by hand, between the enumeration and here.
                            continue;
                        }

                        try
                        {
                            writer.Delete(file.Path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Open in a viewer, or held by an antivirus scan. Not a reason to
                            // abandon the purge: every other file is still a candidate.
                            _logger.LogDebug(ex, "Cached thumbnail could not be purged: {Path}", file.Path);
                            continue;
                        }

                        _lastUse.TryRemove(relative, out _);
                        deleted++;
                    }
                }
                finally
                {
                    ReleaseGate(relative, gate);
                }
            }
            finally
            {
                _renderPermits.Release();
            }
        }

        return deleted;
    }

    // The one mapping from a kind to its directory, shared by Purge and Ensure. Spec 11.3's table.
    //
    // Every value has its own arm and an undefined one throws (review minor 1). A default arm
    // falling through to reference is harmless in Ensure, which renders one file, and is not
    // harmless in Purge, which would then delete every reference thumbnail for a kind nobody
    // defined. A bulk delete refuses what it does not recognize.
    private static string DirectoryFor(ThumbnailKind kind) => kind switch
    {
        ThumbnailKind.Frame => FramesDirectory,
        ThumbnailKind.Preview => PreviewsDirectory,
        ThumbnailKind.Reference => ReferenceDirectory,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "There is no thumbnail directory for this kind."),
    };

    private string? Ensure(
        ThumbnailKind kind, string framePath, Guid? targetId, bool force, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(framePath);

        var full = Path.GetFullPath(framePath);
        var frame = UserFiles.GetFileInfo(full);
        if (!frame.Exists)
        {
            // Nothing to render, and no exception: a frame that vanished between the scan and the
            // request is a placeholder, not a failure.
            return null;
        }

        var settings = general();
        // The directory comes from DirectoryFor, the one kind-to-directory mapping, so a purge and
        // a render can never disagree about where a kind lives (design-lessons rule 2).
        var directory = DirectoryFor(kind);
        var (width, quality, mode) = kind switch
        {
            ThumbnailKind.Frame => (settings.ThumbnailWidth, ThumbnailJpegQuality, RenderMode.Thumbnail),
            ThumbnailKind.Preview => (settings.PreviewResolution, PreviewJpegQuality, RenderMode.Preview),
            _ => (ReferenceWidth, ThumbnailJpegQuality, RenderMode.Thumbnail),
        };

        // A GUID's canonical form is hex and hyphens, so it needs no escaping; the key is 32 hex
        // characters, so neither does it.
        var name = targetId is Guid id
            ? id.ToString("D")
            : ThumbnailKey.For(
                full,
                frame.Length,
                new DateTimeOffset(frame.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
                width);
        var relative = $"{directory}/{name}.jpg";

        // Same key, one worker. Held across the hit check, the render and the write, so a second
        // caller for this key waits here and then takes the hit rather than rendering the same
        // frame again and colliding on the same output file. Keys other than this one are
        // unaffected.
        // Spec 10.6's budget, and the reason it is taken here: this is the one line every render
        // in the application passes through, including the reference pass on the scan thread.
        // Outside the key gate, never inside it: two callers for one key would deadlock against
        // the budget otherwise. A hit holds a permit for the length of a File.Exists.
        _renderPermits.Wait(ct);
        try
        {
            var gate = AcquireGate(relative);
            try
            {
                lock (gate.Gate)
                {
                    return EnsureUnderGate(kind, relative, full, width, quality, mode, force, ct);
                }
            }
            finally
            {
                ReleaseGate(relative, gate);
            }
        }
        finally
        {
            _renderPermits.Release();
        }
    }

    private string? EnsureUnderGate(
        ThumbnailKind kind, string relative, string full, int width, int quality, RenderMode mode,
        bool force, CancellationToken ct)
    {
        try
        {
            // Resolved on every call, so a relocation takes effect with no restart and with no
            // copy, move or delete of the old root (spec 11.3).
            var absolute = writer.ResolveThumbnailPath(relative);
            if (File.Exists(absolute))
            {
                if (!force)
                {
                    // The hit path: no hash of the file's contents and no read of the frame
                    // itself. Reached inside the gate, so a file that exists here is complete,
                    // never a half-written one another thread is still filling.
                    TouchPreview(kind, relative);
                    return relative;
                }

                // Forced: the key is the target id, so the path is stable across regenerations and
                // the hit check above would serve last run's file forever. Deleted inside the key
                // gate, so no other caller can observe the gap, and through AppWriter like every
                // other removal under the cache root (spec 2.1.1).
                try
                {
                    writer.Delete(absolute);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held by a viewer or an antivirus scan. The existing file is still a valid
                    // thumbnail for this target, so serve it rather than fail the pass.
                    _logger.LogDebug(ex, "Thumbnail could not be replaced: {Path}", relative);
                    TouchPreview(kind, relative);
                    return relative;
                }
            }

            // Stamped BEFORE the render and therefore before the write, never after it, so the
            // file cannot exist on disk without a last-use stamp for one instant. A concurrent
            // sweep (Task 5 runs one at start-up on a Task.Run) that enumerated in that window
            // would see an unstamped file, sort it oldest, evict it, and leave this method
            // returning a path to a deleted file. The key gate does not close that window: the
            // sweep deliberately takes no key gate, because it must not block behind a render.
            // Stamping first closes it without widening any lock.
            //
            // The stamp is removed again on every path that produces no file, so the dictionary
            // only ever holds keys with a file behind them.
            TouchPreview(kind, relative);
            var produced = false;
            try
            {
                var result = render(full, width, quality, mode, ct);
                if (!result.Rendered)
                {
                    // Debug, not warning: a frame that cannot be rendered is a normal outcome
                    // (spec 6.2.6), and a library full of them would otherwise log a warning per
                    // frame on every scan.
                    _logger.LogDebug("Thumbnail skipped for {Frame}: {Reason}", full, result.SkipReason);
                    return null;
                }

                // AppWriter.WriteAllBytes creates the parent directory itself, so frames/,
                // previews/ and reference/ need no CreateDirectory call here (and FileSafetyTest
                // forbids one outside AppWriter.cs).
                try
                {
                    writer.WriteAllBytes(absolute, result.Jpeg!);
                }
                catch (IOException ex)
                {
                    // The gate above makes a same-key collision inside this process impossible, so
                    // this is the belt: a second GalactiLog instance over one cache root, or an
                    // antivirus holding the file open. Someone else's complete file is as good as
                    // this one (the key is a pure function of the inputs, so the bytes are the same
                    // render).
                    _logger.LogDebug(ex, "Thumbnail could not be written: {Path}", relative);
                    produced = File.Exists(absolute);
                    return produced ? relative : null;
                }

                produced = true;
                if (kind == ThumbnailKind.Preview)
                {
                    // Exempt the file just written: a preview larger than the whole bound would
                    // otherwise be deleted by its own sweep and this method would return a path to
                    // a file that no longer exists.
                    EvictPreviews(justWritten: relative);
                }
                return relative;
            }
            finally
            {
                // A skip, a cancellation, or a write that produced nothing. Covers the cancelled
                // render too, which leaves through this frame without touching either return.
                if (!produced)
                {
                    UntouchPreview(kind, relative);
                }
            }
        }
        catch (UnauthorizedPathException ex)
        {
            // The configured cache root and the path built here disagree. A configuration problem,
            // not a reason to take a scan or a window down.
            _logger.LogWarning(ex, "Thumbnail path refused by the writer: {Path}", relative);
            return null;
        }
    }

    private KeyGate AcquireGate(string relative)
    {
        lock (_keyGatesLock)
        {
            if (!_keyGates.TryGetValue(relative, out var gate))
            {
                gate = new KeyGate();
                _keyGates[relative] = gate;
            }
            gate.Waiters++;
            return gate;
        }
    }

    private void ReleaseGate(string relative, KeyGate gate)
    {
        lock (_keyGatesLock)
        {
            if (--gate.Waiters == 0)
            {
                _keyGates.Remove(relative);
            }
        }
    }

    // Only previews are ever swept, so a frame or reference key recorded here would sit in the
    // dictionary for the life of the process and never be read.
    private void TouchPreview(ThumbnailKind kind, string relativePath)
    {
        if (kind == ThumbnailKind.Preview)
        {
            Touch(relativePath);
        }
    }

    private void UntouchPreview(ThumbnailKind kind, string relativePath)
    {
        if (kind == ThumbnailKind.Preview)
        {
            _lastUse.TryRemove(relativePath, out _);
        }
    }

    private void Touch(string relativePath)
        => _lastUse[relativePath] = Interlocked.Increment(ref _useStamp);

    // EnumerateThumbnailFiles does not recurse, so the file name is the whole tail of the relative
    // path and this needs no cache-root arithmetic.
    private static string RelativeOf(string directory, string absolutePath)
        => $"{directory}/{Path.GetFileName(absolutePath)}";
}
