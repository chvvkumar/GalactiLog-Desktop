using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Mosaics;

// Spec 12.17's save rule: every layout change schedules one write 500 ms after the last, through
// one Debouncer, and closing the page runs a pending write rather than dropping it.
public sealed partial class ArrangerViewModel
{
    private sealed record Layout(double Rotation, IReadOnlyList<(Guid PanelId, double? X, double? Y, int Rotation, bool FlipH)> Panels);

    private readonly Debouncer _debouncer;

    // Guards _pending, _chain and _inFlight together: claiming the pending layout, raising the
    // in-flight count and attaching its write are one step, so a Dispose or a re-read never sees
    // a layout that has left _pending but is not yet on the chain.
    private readonly Lock _gate = new();

    // The newest layout scheduled and not yet claimed by a write. The window that outlives the
    // debounce claims it; a flush claims it if no window got there first.
    private Layout? _pending;

    // The write chain: one write at a time, in order (the AutosaveField shape).
    private Task _chain = Task.CompletedTask;
    private int _inFlight;

    // Set once the owner has flushed or discarded the pending write, so Dispose does not wait a
    // second time.
    private bool _flushedByOwner;

    /// <summary>"Saving..." from a write's start to its end.</summary>
    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    /// <summary><see cref="SaveFailedText"/> after a failed write, until the next success.</summary>
    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    private bool SavePending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null || _inFlight > 0;
            }
        }
    }

    private Layout Snapshot()
        => new(GlobalRotation, [.. Tiles.Select(tile => (tile.PanelId, (double?)tile.X, (double?)tile.Y, tile.Rotation, tile.FlipH))]);

    private void ScheduleSave()
    {
        if (IsReadOnly || _disposed)
        {
            return;
        }

        // The first save writes every tile, so from here on every tile is placed and none
        // reflows. This marks them placed in memory at once: a first save that then fails leaves
        // nulls in the database, which is harmless within the session (the tiles keep their
        // positions and the next change writes them) and reflows on the next open.
        foreach (var tile in Tiles)
        {
            tile.Place();
        }

        var layout = Snapshot();
        lock (_gate)
        {
            _pending = layout;
        }

        PendingSave = _debouncer.Restart(async (_, cancellationToken) =>
        {
            try
            {
                await _debouncer.Wait(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!cancellationToken.IsCancellationRequested && ClaimAndAttach(snapshot: null) is { } write)
            {
                await write.ConfigureAwait(false);
            }
        });
    }

    // A re-read that adds or removes a panel under a pending write re-takes the snapshot, so the
    // write never names a panel that is gone.
    private void RefreshPendingLayout()
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                _pending = Snapshot();
            }
        }
    }

    // Claims the pending layout (or, from a flush on the UI thread, a fresh snapshot in its place)
    // and attaches its write, under the gate. Null when nothing was pending.
    private Task? ClaimAndAttach(Func<Layout>? snapshot)
    {
        lock (_gate)
        {
            if (_pending is not { } layout)
            {
                return null;
            }

            _pending = null;
            layout = snapshot?.Invoke() ?? layout;
            _inFlight++;
            return _chain = _chain.ContinueWith(_ => Write(layout), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Write(Layout layout)
    {
        _post(() => IsSaving = true);
        string? error = null;
        try
        {
            _backend.UpdateLayout(_mosaicId, layout.Rotation, layout.Panels);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the mosaic layout failed; the tiles keep what the reader did");
            error = SaveFailedText;
        }

        bool stillSaving;
        lock (_gate)
        {
            stillSaving = --_inFlight > 0;
        }

        _post(() =>
        {
            IsSaving = stillSaving;
            SaveError = error;
        });
    }

    /// <summary>Runs a pending write at once and returns the write chain, which also covers a
    /// write already in flight. The detail page waits on it beside its notes flush, under one
    /// bound. Writes run on the pool and post nothing the wait depends on, so blocking the UI
    /// thread on the returned task cannot deadlock.</summary>
    internal Task FlushAsync()
    {
        _flushedByOwner = true;
        _debouncer.Dispose();
        if (IsReadOnly)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            ClaimAndAttach(Snapshot);
            return _chain;
        }
    }

    /// <summary>Drops a pending write without running it: the mosaic was deleted.</summary>
    internal void Discard()
    {
        _flushedByOwner = true;
        _debouncer.Dispose();
        lock (_gate)
        {
            _pending = null;
        }
    }

    /// <summary>Runs a pending write at once and waits for it, bounded by 2 seconds (the shape of
    /// <c>MosaicDetailViewModel.Dispose</c>'s notes flush), unless the owner already flushed or
    /// discarded it; then releases every tile.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_flushedByOwner)
        {
            try
            {
                FlushAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flushing the mosaic layout on close failed");
            }
        }

        _debouncer.Dispose();
        _disposed = true;
        foreach (var tile in Tiles)
        {
            tile.Dispose();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
