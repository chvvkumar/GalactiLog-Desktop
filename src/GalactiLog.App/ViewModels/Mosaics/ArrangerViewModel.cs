using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>One tile of the read-only preview (spec 12.17): a checked label, its integration over
/// the in-campaign session rows, and its best frame, read off the UI thread.</summary>
public sealed record PreviewTile(string Label, double IntegrationSeconds, Func<BestFrame?> BestFrame);

/// <summary>
/// Spec 12.17's arranger (Phase 19A): the mosaic detail page's tile canvas and its toolbar state,
/// and, read-only, a suggestion row's tile preview. Display state only (ruling R9): the composite
/// and every figure ignore it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Feeding.</b> The detail page hands every read to <see cref="Apply"/>, which keeps tiles by
/// panel id; a suggestion row hands its checked labels to <see cref="ApplyPreview"/>.
/// </para>
/// <para>
/// <b>Stacking order.</b> <see cref="Tiles"/> stays in <c>sort_order</c>, which the auto layout
/// reads; <see cref="TileViewModel.ZIndex"/> is the stacking order, raised by
/// <see cref="BeginDrag"/>. Moving the item in the collection would rebuild its container and
/// drop the pointer capture the press just took.
/// </para>
/// <para>
/// <b>The save rule.</b> Every layout change schedules one write through one
/// <see cref="Debouncer"/>. The layout is snapshotted on the UI thread when it is scheduled (and
/// again when a re-read changes the tiles under a pending write), so the pool thread never reads a
/// tile. Writes run one at a time on the pool with no continuation posted back to wait on, so
/// <see cref="Dispose"/> can block the UI thread on the flush without a deadlock.
/// </para>
/// </remarks>
public sealed partial class ArrangerViewModel : ObservableObject, IDisposable
{
    /// <summary>A tile's width at zoom 1, in canvas pixels.</summary>
    public const double TileWidth = 250;

    /// <summary>A tile's height at zoom 1, in canvas pixels.</summary>
    public const double TileHeight = 160;

    /// <summary>The gap between auto layout cells: the pitch is 254 by 164.</summary>
    public const double AutoLayoutGap = 4;

    /// <summary>The zoom range (the web's MIN_ZOOM and MAX_ZOOM).</summary>
    public const double MinZoom = 0.1;

    /// <inheritdoc cref="MinZoom"/>
    public const double MaxZoom = 3.0;

    /// <summary>One zoom step, added (the web's ZOOM_STEP).</summary>
    public const double ZoomStep = 0.1;

    /// <summary>Fit's padding a side, in viewport pixels (the web's FIT_PADDING).</summary>
    public const double FitPadding = 40;

    /// <summary>The Rotation slider's range, in degrees, step 1.</summary>
    public const double MinRotation = -180;

    /// <inheritdoc cref="MinRotation"/>
    public const double MaxRotation = 180;

    /// <summary>The Tile opacity slider's range, in percent, step 5.</summary>
    public const double MinOpacity = 20;

    /// <inheritdoc cref="MinOpacity"/>
    public const double MaxOpacity = 100;

    /// <inheritdoc cref="MinOpacity"/>
    public const double OpacityStep = 5;

    /// <summary>The failed save sentence, in the error ink in place of "Saving...".</summary>
    public const string SaveFailedText = "The layout could not be saved.";

    /// <summary>What the preview shows with no label checked.</summary>
    public const string NoPanelsText = "No panels selected.";

    /// <summary>The save debounce (the web's SAVE_DEBOUNCE_MS).</summary>
    public static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    private sealed record Layout(double Rotation, IReadOnlyList<(Guid PanelId, double? X, double? Y, int Rotation, bool FlipH)> Panels);

    private readonly Guid _mosaicId;
    private readonly MosaicsBackend _backend;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Debouncer _debouncer;
    private readonly Dictionary<Guid, TileViewModel> _byPanel = [];
    private readonly Lock _gate = new();

    // The newest layout scheduled and not yet claimed by a write. The window that outlives the
    // debounce claims it; Dispose claims it if no window got there first.
    private Layout? _pending;

    // The write chain: one write at a time, in order (the AutosaveField shape).
    private Task _chain = Task.CompletedTask;
    private int _inFlight;

    private PanelFrameSet? _frameSet;
    private int _framesGeneration;
    private int _topZ;
    private bool _applied;
    private bool _fitted;
    private bool _applyingRotation;
    private double _viewportWidth;
    private double _viewportHeight;
    private TileViewModel? _dragging;
    private double _grabX;
    private double _grabY;
    private bool _dragMoved;
    private bool _disposed;

    /// <param name="mosaicId">The mosaic whose layout this saves; unused by the preview.</param>
    /// <param name="backend">The data collaborators: <c>PanelFrames</c>, <c>UpdateLayout</c>,
    /// <c>ThumbnailFor</c>.</param>
    /// <param name="readOnly">The suggestion row's preview: it lays out, fits and loads thumbnails,
    /// and never saves.</param>
    /// <param name="post">How to reach the UI thread.</param>
    /// <param name="delay">The debounce seam.</param>
    /// <param name="logger">A failed read or write is logged, never thrown on the UI thread.</param>
    public ArrangerViewModel(
        Guid mosaicId,
        MosaicsBackend backend,
        bool readOnly,
        Action<Action> post,
        Func<TimeSpan, CancellationToken, Task> delay,
        ILogger logger)
    {
        _mosaicId = mosaicId;
        _backend = backend;
        _post = post;
        _logger = logger;
        IsReadOnly = readOnly;
        _debouncer = new Debouncer(_lifetime.Token, delay, SaveDebounce);
        Filters = [];
        Tiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsLoading));
        };
    }

    /// <summary>One tile per panel, in <c>sort_order</c>.</summary>
    public ObservableCollection<TileViewModel> Tiles { get; } = [];

    /// <summary>True with no tile; the preview then shows <see cref="NoPanelsText"/>.</summary>
    public bool IsEmpty => Tiles.Count == 0;

    /// <summary>The preview: no toolbar, no save.</summary>
    public bool IsReadOnly { get; }

    /// <summary>The newest scheduled save, so a test awaits it.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>The newest frame set read, so a test awaits it.</summary>
    internal Task PendingFrames { get; private set; } = Task.CompletedTask;

    // ---- selection and the tile opacity ----------------------------------------------------------

    /// <summary>The selected tile, at most one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RotateSelectedCommand), nameof(FlipSelectedCommand))]
    public partial TileViewModel? Selected { get; private set; }

    /// <summary>Enables Rotate CW, Flip H and the Tile opacity slider.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>The Tile opacity slider, 20 to 100 percent; kept across selections, never saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityText))]
    public partial double SelectedOpacity { get; set; } = MaxOpacity;

    /// <summary>"&lt;n&gt;%".</summary>
    public string OpacityText => Percent(SelectedOpacity);

    partial void OnSelectedOpacityChanged(double value) => Selected?.SetSelected(true, value / 100);

    /// <summary>Selects <paramref name="tile"/>, or clears the selection with null.</summary>
    public void Select(TileViewModel? tile)
    {
        if (ReferenceEquals(tile, Selected))
        {
            return;
        }

        Selected?.SetSelected(false, 1);
        Selected = tile;
        tile?.SetSelected(true, SelectedOpacity / 100);
    }

    /// <summary>A click with no movement: deselects a selected tile, selects any other.</summary>
    public void ToggleSelect(TileViewModel tile) => Select(ReferenceEquals(tile, Selected) ? null : tile);

    /// <summary>The Labels toggle: false hides every overlay; never saved.</summary>
    [ObservableProperty]
    public partial bool ShowLabels { get; set; } = true;

    // ---- rotate, flip, reset -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RotateSelected() => Rotate(Selected);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void FlipSelected() => Flip(Selected);

    /// <summary>Rotate CW: adds 90 modulo 360 and schedules a save. The context menu calls it on
    /// its tile after selecting it.</summary>
    public void Rotate(TileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        tile.SetTransform((tile.Rotation + 90) % 360, tile.FlipH);
        ScheduleSave();
    }

    /// <summary>Flip H: toggles the flip and schedules a save.</summary>
    public void Flip(TileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        tile.SetTransform(tile.Rotation, !tile.FlipH);
        ScheduleSave();
    }

    /// <summary>Every tile's rotation and flip, and the global rotation, to 0; positions stay.</summary>
    [RelayCommand]
    private void ResetAll()
    {
        foreach (var tile in Tiles)
        {
            tile.SetTransform(0, false);
        }

        SetRotationQuietly(0);
        ScheduleSave();
    }

    // ---- the global rotation -------------------------------------------------------------------------

    /// <summary>The Rotation slider, -180 to 180 degrees: the whole group turns about
    /// (<see cref="RotationCentreX"/>, <see cref="RotationCentreY"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RotationText))]
    public partial double GlobalRotation { get; set; }

    /// <summary>"&lt;n&gt;°".</summary>
    public string RotationText => Math.Round(GlobalRotation, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "°";

    /// <summary>The tiles' bounding box centre in canvas pixels, the <c>RotateTransform</c>'s
    /// centre. Taken when the rotation changes and when panels come or go, never during a drag.</summary>
    [ObservableProperty]
    public partial double RotationCentreX { get; private set; }

    /// <inheritdoc cref="RotationCentreX"/>
    [ObservableProperty]
    public partial double RotationCentreY { get; private set; }

    partial void OnGlobalRotationChanged(double value)
    {
        TakeRotationCentre();
        if (!_applyingRotation)
        {
            ScheduleSave();
        }
    }

    /// <summary>The "0" button.</summary>
    [RelayCommand]
    private void ResetRotation() => GlobalRotation = 0;

    private void SetRotationQuietly(double value)
    {
        _applyingRotation = true;
        try
        {
            GlobalRotation = value;
        }
        finally
        {
            _applyingRotation = false;
        }
    }

    private void TakeRotationCentre()
    {
        if (Bounds() is { } box)
        {
            RotationCentreX = (box.Left + box.Right) / 2;
            RotationCentreY = (box.Top + box.Bottom) / 2;
        }
    }

    // ---- dragging ------------------------------------------------------------------------------------

    /// <summary>A left press on a tile: raises it to the top of the stacking order and records the
    /// grab offset from the pointer's canvas position. The view selects it first.</summary>
    public void BeginDrag(TileViewModel tile, double canvasX, double canvasY)
    {
        tile.RaiseTo(++_topZ);
        _dragging = tile;
        _dragMoved = false;
        (_grabX, _grabY) = (canvasX - tile.X, canvasY - tile.Y);
    }

    /// <summary>Moves the dragged tile so the grab point stays under the pointer's canvas position.</summary>
    public void Drag(TileViewModel tile, double canvasX, double canvasY)
    {
        if (!ReferenceEquals(tile, _dragging))
        {
            return;
        }

        _dragMoved = true;
        tile.SetPosition(canvasX - _grabX, canvasY - _grabY, placed: true);
    }

    /// <summary>The release: schedules a save when the tile moved.</summary>
    public void EndDrag(TileViewModel tile)
    {
        if (!ReferenceEquals(tile, _dragging))
        {
            return;
        }

        _dragging = null;
        if (_dragMoved)
        {
            ScheduleSave();
        }
    }

    // ---- zoom and pan --------------------------------------------------------------------------------

    /// <summary>0.1 to 3.0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText), nameof(ViewMatrix))]
    public partial double Zoom { get; private set; } = 1;

    /// <summary>The canvas's translation in viewport pixels from the viewport's top left corner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewMatrix))]
    public partial double OffsetX { get; private set; }

    /// <inheritdoc cref="OffsetX"/>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewMatrix))]
    public partial double OffsetY { get; private set; }

    /// <summary>Scale then translate: the view's <c>MatrixTransform</c>, after the rotation.</summary>
    public Matrix ViewMatrix => Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(OffsetX, OffsetY);

    /// <summary>"&lt;n&gt;%", the zoom times 100, rounded.</summary>
    public string ZoomText => Percent(Zoom * 100);

    /// <summary>The view reports the viewport's size; the first size with a tile fits.</summary>
    public void SetViewportSize(double width, double height)
    {
        var first = _viewportWidth <= 0 || _viewportHeight <= 0;
        (_viewportWidth, _viewportHeight) = (width, height);
        if (first)
        {
            FitOnce();
        }
    }

    /// <summary>The wheel: one step per event by the sign of <paramref name="steps"/> (the wheel's
    /// <c>Delta.Y</c>) about the pointer, in viewport pixels.</summary>
    public void ZoomAt(double steps, double pointerX, double pointerY)
    {
        if (steps != 0)
        {
            StepZoom(Math.Sign(steps) * ZoomStep, pointerX, pointerY);
        }
    }

    [RelayCommand]
    private void ZoomIn() => StepZoom(ZoomStep, _viewportWidth / 2, _viewportHeight / 2);

    [RelayCommand]
    private void ZoomOut() => StepZoom(-ZoomStep, _viewportWidth / 2, _viewportHeight / 2);

    /// <summary>A drag on empty canvas, in viewport pixels.</summary>
    public void Pan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    /// <summary>Scales the tiles' bounding box into the viewport less the padding, and centres it.
    /// Does nothing with no tile or no size.</summary>
    [RelayCommand]
    private void Fit()
    {
        if (Bounds() is not { } box || _viewportWidth <= 0 || _viewportHeight <= 0)
        {
            return;
        }

        var scale = Math.Clamp(
            Math.Min((_viewportWidth - 2 * FitPadding) / box.Width, (_viewportHeight - 2 * FitPadding) / box.Height),
            MinZoom, MaxZoom);
        Zoom = scale;
        OffsetX = _viewportWidth / 2 - box.Center.X * scale;
        OffsetY = _viewportHeight / 2 - box.Center.Y * scale;
        _fitted = true;
    }

    private void FitOnce()
    {
        if (!_fitted)
        {
            Fit();
        }
    }

    // PreviewModalViewModel.Zoom's rule in viewport pixels from the top left corner: a step the
    // clamp leaves unchanged moves nothing.
    private void StepZoom(double step, double pointerX, double pointerY)
    {
        var old = Zoom;
        var next = Math.Clamp(old + step, MinZoom, MaxZoom);
        if (next == old)
        {
            return;
        }

        var ratio = next / old;
        OffsetX = pointerX - (pointerX - OffsetX) * ratio;
        OffsetY = pointerY - (pointerY - OffsetY) * ratio;
        Zoom = next;
    }

    // Every tile's 250 by 160 box; the rotated footprint is not considered.
    private Rect? Bounds()
    {
        if (Tiles.Count == 0)
        {
            return null;
        }

        var left = Tiles.Min(tile => tile.X);
        var top = Tiles.Min(tile => tile.Y);
        return new Rect(left, top, Tiles.Max(tile => tile.X) + TileWidth - left, Tiles.Max(tile => tile.Y) + TileHeight - top);
    }

    // ---- feeding -------------------------------------------------------------------------------------

    /// <summary>Takes a read of the detail page: tiles kept by panel id, a new panel unplaced, a
    /// gone panel's tile disposed; the global rotation taken on the first read or while no save is
    /// pending; then the frame set re-read and the thumbnails re-pointed.</summary>
    public void Apply(MosaicDetail detail)
    {
        if (_disposed)
        {
            return;
        }

        var leader = detail.Panels.Count == 0 ? 0 : detail.Panels.Max(panel => panel.IntegrationSeconds);
        var changed = !_applied;
        var wanted = new List<TileViewModel>();
        foreach (var panel in detail.Panels)
        {
            if (!_byPanel.TryGetValue(panel.Id, out var tile))
            {
                tile = new TileViewModel(panel.Id, panel.Label, NotifyLoading);
                if (panel.CanvasX is { } x && panel.CanvasY is { } y)
                {
                    tile.SetPosition(x, y, placed: true);
                }

                tile.SetTransform(panel.Rotation, panel.FlipH);
                _byPanel[panel.Id] = tile;
                changed = true;
            }

            tile.SetFigures(panel.Label, panel.IntegrationSeconds, panel.DeficitSeconds, leader);
            wanted.Add(tile);
        }

        var gone = _byPanel.Keys.Except(detail.Panels.Select(panel => panel.Id)).ToList();
        foreach (var id in gone)
        {
            Remove(_byPanel[id]);
            _byPanel.Remove(id);
            changed = true;
        }

        MosaicDetailViewModel.Reconcile(Tiles, wanted);
        AutoLayout();

        if (!_applied || !SavePending)
        {
            SetRotationQuietly(detail.RotationAngle);
        }

        if (changed)
        {
            TakeRotationCentre();
        }

        _applied = true;
        RefreshPendingLayout();
        FitOnce();
        PendingFrames = ReadFramesAsync();
    }

    /// <summary>Takes the preview's checked labels, in label order: tiles kept by label, every
    /// tile unplaced, no badge; refits on every call.</summary>
    public void ApplyPreview(IReadOnlyList<PreviewTile> tiles)
    {
        if (_disposed)
        {
            return;
        }

        var existing = Tiles.ToDictionary(tile => tile.Label, StringComparer.OrdinalIgnoreCase);
        var wanted = new List<TileViewModel>();
        var added = new List<(TileViewModel Tile, Func<BestFrame?> Best)>();
        foreach (var entry in tiles)
        {
            if (!existing.Remove(entry.Label, out var tile))
            {
                tile = new TileViewModel(Guid.NewGuid(), entry.Label, NotifyLoading);
                added.Add((tile, entry.BestFrame));
            }

            tile.SetFigures(entry.Label, entry.IntegrationSeconds, 0, 0);
            wanted.Add(tile);
        }

        foreach (var tile in existing.Values)
        {
            Remove(tile);
        }

        MosaicDetailViewModel.Reconcile(Tiles, wanted);
        AutoLayout();
        TakeRotationCentre();
        Fit();
        PendingFrames = added.Count == 0 ? Task.CompletedTask : ReadPreviewFramesAsync(added);
    }

    private void Remove(TileViewModel tile)
    {
        if (ReferenceEquals(tile, Selected))
        {
            Select(null);
        }

        if (ReferenceEquals(tile, _dragging))
        {
            _dragging = null;
        }

        tile.Dispose();
    }

    // Spec 12.17's auto layout: the unplaced tiles, in sort_order, fill ceil(sqrt(n)) columns at a
    // pitch of 254 by 164 from the origin.
    private void AutoLayout()
    {
        var unplaced = Tiles.Where(tile => !tile.IsPlaced).ToList();
        var columns = (int)Math.Ceiling(Math.Sqrt(unplaced.Count));
        for (var index = 0; index < unplaced.Count; index++)
        {
            unplaced[index].SetPosition(
                index % columns * (TileWidth + AutoLayoutGap),
                index / columns * (TileHeight + AutoLayoutGap),
                placed: false);
        }
    }

    // ---- the filter selector and the thumbnails ------------------------------------------------------

    /// <summary>The available filters, in the frame set's order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    public partial IReadOnlyList<string> Filters { get; private set; }

    /// <summary>Shows the Filter selector.</summary>
    public bool HasFilters => Filters.Count > 0;

    /// <summary>The chosen filter, always in the <see cref="Filters"/> spelling; never saved.</summary>
    [ObservableProperty]
    public partial string? SelectedFilter { get; set; }

    /// <summary>"Loading..." while any tile's thumbnail is outstanding.</summary>
    public bool IsLoading => Tiles.Any(tile => tile.IsLoading);

    private void NotifyLoading() => OnPropertyChanged(nameof(IsLoading));

    partial void OnSelectedFilterChanged(string? value) => RePoint();

    private async Task ReadFramesAsync()
    {
        if (IsReadOnly)
        {
            return;
        }

        var generation = ++_framesGeneration;
        PanelFrameSet set;
        try
        {
            set = await Task.Run(() => _backend.PanelFrames(_mosaicId), _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the mosaic's best frames failed");
            return;
        }

        _post(() =>
        {
            if (_disposed || generation != _framesGeneration)
            {
                return;
            }

            _frameSet = set;
            if (!Filters.SequenceEqual(set.AvailableFilters, StringComparer.Ordinal))
            {
                Filters = set.AvailableFilters;
            }

            // The choice survives while still available, else the default; always the available
            // list's spelling, since the default and a frame's filter may differ in case.
            var wanted = Spelling(SelectedFilter) ?? Spelling(set.DefaultFilter) ?? set.AvailableFilters.FirstOrDefault();
            if (string.Equals(wanted, SelectedFilter, StringComparison.Ordinal))
            {
                RePoint();
            }
            else
            {
                SelectedFilter = wanted;
            }
        });
    }

    private string? Spelling(string? filter)
        => filter is null ? null : Filters.FirstOrDefault(entry => string.Equals(entry, filter, StringComparison.OrdinalIgnoreCase));

    private void RePoint()
    {
        if (_frameSet is not { } set)
        {
            return;
        }

        var filter = SelectedFilter;
        foreach (var tile in Tiles)
        {
            BestFrame? best = null;
            if (filter is not null && set.BestByPanel.TryGetValue(tile.PanelId, out var byFilter))
            {
                // The inner dictionaries are already ordinal and case insensitive.
                byFilter.TryGetValue(filter, out best);
            }

            tile.SetFrame(best?.FilePath, filter, _backend.ThumbnailFor);
        }
    }

    private async Task ReadPreviewFramesAsync(IReadOnlyList<(TileViewModel Tile, Func<BestFrame?> Best)> added)
    {
        IReadOnlyList<BestFrame?> best;
        try
        {
            best = await Task.Run(() => added.Select(entry => entry.Best()).ToList(), _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading a suggestion's best frames failed");
            return;
        }

        _post(() =>
        {
            for (var index = 0; index < added.Count; index++)
            {
                var tile = added[index].Tile;
                if (!_disposed && Tiles.Contains(tile))
                {
                    tile.SetFrame(best[index]?.FilePath, null, _backend.ThumbnailFor);
                }
            }
        });
    }

    // ---- the save rule -------------------------------------------------------------------------------

    /// <summary>"Saving..." from a write's start to its end.</summary>
    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    /// <summary><see cref="SaveFailedText"/> after a failed write, until the next success.</summary>
    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    private bool SavePending => Volatile.Read(ref _pending) is not null || Volatile.Read(ref _inFlight) > 0;

    private Layout Snapshot()
        => new(GlobalRotation, [.. Tiles.Select(tile => (tile.PanelId, (double?)tile.X, (double?)tile.Y, tile.Rotation, tile.FlipH))]);

    // The first save writes every tile, so from here on every tile is placed and none reflows.
    private void ScheduleSave()
    {
        if (IsReadOnly || _disposed)
        {
            return;
        }

        foreach (var tile in Tiles)
        {
            tile.Place();
        }

        Volatile.Write(ref _pending, Snapshot());
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

            if (!cancellationToken.IsCancellationRequested && Interlocked.Exchange(ref _pending, null) is { } layout)
            {
                await Attach(layout).ConfigureAwait(false);
            }
        });
    }

    // A re-read that adds or removes a panel under a pending write re-takes the snapshot, so the
    // write never names a panel that is gone.
    private void RefreshPendingLayout()
    {
        if (Volatile.Read(ref _pending) is { } observed)
        {
            Interlocked.CompareExchange(ref _pending, Snapshot(), observed);
        }
    }

    private Task Attach(Layout layout)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _inFlight);
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
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        _post(() =>
        {
            IsSaving = Volatile.Read(ref _inFlight) > 0;
            SaveError = error;
        });
    }

    /// <summary>Runs a pending write at once and waits for it, bounded by 2 seconds (the shape of
    /// <c>MosaicDetailViewModel.Dispose</c>'s notes flush), then releases every tile.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _debouncer.Dispose();
        if (!IsReadOnly)
        {
            try
            {
                // Attach runs the write on the pool (TaskScheduler.Default) and posts nothing this
                // wait depends on, so blocking the UI thread here cannot deadlock. With nothing
                // pending, a write already in flight is waited for instead.
                var flush = Interlocked.Exchange(ref _pending, null) is not null ? Attach(Snapshot()) : _chain;
                flush.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flushing the mosaic layout on close failed");
            }
        }

        _disposed = true;
        foreach (var tile in Tiles)
        {
            tile.Dispose();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static string Percent(double value)
        => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
}
