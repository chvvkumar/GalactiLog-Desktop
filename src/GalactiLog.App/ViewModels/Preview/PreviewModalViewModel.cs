using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Preview;

/// <summary>
/// Spec 11.5's preview modal, a port of <c>frontend/src/components/FilePreviewModal.tsx</c> and
/// <c>backend/app/api/preview.py</c>. Always autostretched: there are no manual stretch controls in
/// v1, and none may be added without a spec change.
/// </summary>
/// <remarks>
/// <para>
/// Every keyboard shortcut in spec 11.5's table is a command on this type, because spec 18.3
/// asserts the shortcuts at the view-model level and never synthesizes a key press. The window
/// binds the same commands.
/// </para>
/// <para>
/// The resolution the preview renders at is <b>not</b> a member here. <c>ThumbnailCache</c> reads
/// <c>general.preview_resolution</c> (0 meaning native) at call time for every
/// <c>EnsurePreview</c>, which is one answer to "how big is a preview" rather than two; this
/// view-model only asks for a preview.
/// </para>
/// </remarks>
public sealed partial class PreviewModalViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 11.5's zoom range, as a multiple of fit. The web application clamps to
    /// [1, 20]; the spec's range is the port's, so zooming out below fit is allowed.</summary>
    public const double MinZoom = 0.1;

    /// <summary>Spec 11.5's upper bound.</summary>
    public const double MaxZoom = 8.0;

    /// <summary>The web's <c>Math.exp(-deltaY * 0.0015)</c> wheel sensitivity, ported unchanged so
    /// a wheel notch feels the same.</summary>
    public const double WheelZoomRate = 0.0015;

    /// <summary>How close to fit counts as fit. The web compares the scale to 1 exactly, which it
    /// can because its clamp floor <b>is</b> 1; with the spec's 0.1 floor the scale passes through
    /// 1 rather than resting on it, and a chain of exponentials lands a wheel notch from it a few
    /// ulps out. Without this, zooming out to fit would leave the image a pixel off centre
    /// forever.</summary>
    private const double FitTolerance = 1e-9;

    private readonly Func<string, ThumbnailKind, ThumbnailSlotViewModel> _createSlot;
    private readonly ShellIntegration _shell;
    private readonly Func<Guid, FrameHeaders?> _getHeaders;
    private readonly Func<bool> _isWindows;
    private readonly Action<Action>? _post;
    private readonly ILogger _logger;
    // The settings seam, all or nothing (Task 7 review P3). The two were independently optional,
    // so a caller supplying only the getter got a checkbox that raised, rendered and then snapped
    // back to the document's unchanged value on the next read, and a caller supplying only the
    // setter wrote a value the box never showed. They are stored as one pair and read as one
    // condition, so neither half can be the only one there.
    private readonly Func<bool>? _getRenderOnNavigate;
    private readonly Action<bool>? _setRenderOnNavigate;
    private readonly Action<EventHandler<GeneralSettings>>? _unsubscribeGeneralChanged;

    // Reached when the pair is not wired, which is a caller that has no general document to read
    // (the modal still has to answer the checkbox). Production and the test factory both pass the
    // pair, so this is never the answer the application gives.
    private bool _renderOnNavigateWithoutASettingsSeam = true;

    // Cancelled by Dispose and handed to every header panel this modal builds, so a read still in
    // flight when the modal closes drops its result instead of posting it to a dispatcher that may
    // no longer be running. The same guard FrameTableViewModel gives its rows' panels.
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    private bool _disposed;

    /// <param name="frames">Spec 11.5's originating list: the frame table's <c>Rows</c> in their
    /// current sort order, so stepping follows what the user sees rather than capture order. A
    /// snapshot; it does not change while the modal is open.</param>
    /// <param name="index">The clicked row's position in <paramref name="frames"/>. Clamped into
    /// range rather than refused.</param>
    /// <param name="createSlot">Task 5's slot factory. Two slots per frame: the
    /// <c>general.thumbnail_width</c> thumbnail (default 800) that usually hits the cache and
    /// appears immediately, and the full preview. Both go through the one bounded worker, so they
    /// share spec 10.6's budget of two.</param>
    /// <param name="shell">The only process launcher and clipboard writer in the application
    /// (spec 2.1). This modal opens no stream and writes no file.</param>
    /// <param name="getHeaders">Phase 6's <c>FrameHeadersQuery.Get</c>, for the <c>H</c> panel.
    /// Issued on the panel's first show for a frame, never on open.</param>
    /// <param name="isWindows"><c>OperatingSystem.IsWindows()</c> cannot be faked, so the platform
    /// guard spec 19.2 names is a delegate, exactly as <c>ShellIntegration</c> takes its
    /// <c>start</c> delegate for the same reason. Production passes none and the default reads
    /// <see cref="ShellIntegration.IsWindowsShellAvailable"/>, which is the one guard.</param>
    /// <param name="post">How to reach the UI thread, for the header panel. Defaults to the
    /// application dispatcher.</param>
    /// <param name="logger">Optional; tests pass none.</param>
    /// <param name="getRenderOnNavigate">Reads <c>general.preview_render_on_navigate</c> (spec
    /// 5.8.1) from the live document, not from a snapshot taken at startup: the checkbox has to
    /// survive a second opening of the modal. Spec 11.5 reads the flag on each step rather than
    /// capturing it at open, which is why this is a delegate and not a constructor bool.</param>
    /// <param name="setRenderOnNavigate">Writes it back through <c>SettingsStore.MutateGeneral</c>
    /// (spec 12.7: a view-model that writes the general document takes the mutation as a delegate
    /// rather than the store itself). Taken together with
    /// <paramref name="getRenderOnNavigate"/>: one without the other is refused, because a modal
    /// that can read but not write, or write but not read, reports a setting it does not
    /// have.</param>
    /// <param name="subscribeGeneralChanged">Normally <c>SettingsStore.GeneralChanged</c>. The
    /// getter reads the live document, so a write from anywhere else is already in effect on the
    /// next step; this is what makes the checkbox on screen show it as well (phase review
    /// P3-6).</param>
    /// <param name="unsubscribeGeneralChanged">Its pair, called from <see cref="Dispose"/>, so a
    /// closed modal is not kept alive by the process-wide store.</param>
    public PreviewModalViewModel(
        IReadOnlyList<PreviewFrameViewModel> frames,
        int index,
        Func<string, ThumbnailKind, ThumbnailSlotViewModel> createSlot,
        ShellIntegration shell,
        Func<Guid, FrameHeaders?> getHeaders,
        Func<bool>? isWindows = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<bool>? getRenderOnNavigate = null,
        Action<bool>? setRenderOnNavigate = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(createSlot);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(getHeaders);
        if (frames.Count == 0)
        {
            throw new ArgumentException("A preview modal needs at least one frame to show.", nameof(frames));
        }

        Frames = frames;
        _createSlot = createSlot;
        _shell = shell;
        _getHeaders = getHeaders;
        _isWindows = isWindows ?? (() => ShellIntegration.IsWindowsShellAvailable);
        _post = post;
        _logger = logger ?? NullLogger.Instance;
        if ((getRenderOnNavigate is null) != (setRenderOnNavigate is null))
        {
            throw new ArgumentException(
                "getRenderOnNavigate and setRenderOnNavigate are one seam: pass both or neither. "
                + "A modal with only one of them reports a setting it cannot keep.",
                getRenderOnNavigate is null ? nameof(getRenderOnNavigate) : nameof(setRenderOnNavigate));
        }

        _getRenderOnNavigate = getRenderOnNavigate;
        _setRenderOnNavigate = setRenderOnNavigate;

        if (subscribeGeneralChanged is not null)
        {
            subscribeGeneralChanged(OnGeneralChanged);
            _unsubscribeGeneralChanged = unsubscribeGeneralChanged;
        }

        // Read once: CancellationTokenSource.Token throws after the source is disposed, and the
        // token itself keeps answering IsCancellationRequested.
        _lifetimeToken = _lifetime.Token;

        Index = Math.Clamp(index, 0, frames.Count - 1);
        Current = frames[Index];
        Scale = 1d;

        (Thumbnail, Preview) = CreateSlots();
        AttachSlots();
        LoadSlots();
    }

    /// <summary>Raised when the modal should close. The window answers it, the same split
    /// <c>MergeDialogViewModel.CloseRequested</c> and <c>TargetDetailViewModel.BackRequested</c>
    /// use.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Spec 11.5's originating list, in the order it was handed over.</summary>
    public IReadOnlyList<PreviewFrameViewModel> Frames { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    public partial int Index { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial PreviewFrameViewModel Current { get; private set; }

    /// <summary>The full-resolution preview (spec 11.5). One slot per frame: stepping builds a new
    /// one and disposes the old, which is what disposes the previous bitmap.</summary>
    [ObservableProperty]
    public partial ThumbnailSlotViewModel Preview { get; private set; }

    /// <summary>The <c>general.thumbnail_width</c> frame thumbnail (default 800), shown until the
    /// preview arrives (ruling Q18) and the requester spec 11.4's on-demand path needs.</summary>
    [ObservableProperty]
    public partial ThumbnailSlotViewModel Thumbnail { get; private set; }

    /// <summary>What the viewport renders: the preview once it is there, the thumbnail until then,
    /// and null when neither can be rendered, which leaves the placeholder showing (spec 6.2.6).
    /// </summary>
    public Bitmap? DisplayImage => Preview.Image ?? Thumbnail.Image;

    /// <summary>Spec 11.5's "shows a spinner while generating".</summary>
    public bool IsRendering => Preview.IsLoading;

    /// <summary>Spec 11.4's placeholder state, composed over both slots rather than read off
    /// either one.</summary>
    public bool ShowsPlaceholder => DisplayImage is null;

    [ObservableProperty]
    public partial double Scale { get; private set; }

    [ObservableProperty]
    public partial double OffsetX { get; private set; }

    [ObservableProperty]
    public partial double OffsetY { get; private set; }

    [ObservableProperty]
    public partial bool IsHeaderPanelVisible { get; private set; }

    /// <summary>Phase 6's raw header panel, reused unchanged (spec 12.4): sorted keys with a filter
    /// box, <c>COMMENT</c> and <c>HISTORY</c> as multi-line blocks, and the Derived metrics section
    /// with its provenance strings. Built on the panel's first show for a frame, so opening the
    /// modal issues no header query at all.</summary>
    [ObservableProperty]
    public partial RawHeaderPanelViewModel? Headers { get; private set; }

    /// <summary>Set when a preview render comes back with nothing. The modal then offers
    /// <see cref="RetryCommand"/> rather than an empty viewport, which is what
    /// <c>preview.py</c>'s 500 surfaces as in the web application.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;

    public bool CanGoPrevious => Index > 0;

    public bool CanGoNext => Index < Frames.Count - 1;

    /// <summary>"3 of 12", so the bound is visible rather than only felt when a button greys.
    /// </summary>
    public string PositionText => $"{Index + 1} of {Frames.Count}";

    public string Title => Current.FileName;

    /// <summary>Spec 19.2's platform guard, read once per query through the injected delegate so
    /// the two guarded commands and their tests agree on one answer.</summary>
    public bool IsWindows => _isWindows();

    /// <summary>
    /// Spec 11.5's "Render full preview on navigation", bound to
    /// <c>general.preview_render_on_navigate</c> (spec 5.8.1). On, every navigation step renders a
    /// fresh preview at <c>general.preview_resolution</c>; off, the step shows the new frame's
    /// cached thumbnail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The getter reads the document on every access rather than caching a bool, which is what
    /// makes spec 11.5's "the setting is read on each step rather than captured at open" true:
    /// <see cref="GoTo"/> reads this property, so a change made anywhere takes effect on the next
    /// step with no reopening.
    /// </para>
    /// <para>
    /// Ticking it while the modal is open renders the frame on screen at once (coordinator ruling
    /// Q13, matching the web's <c>if (!zoomed() and !loading()) requestZoom()</c>): a reader who
    /// ticks the box is asking to see detail now, and waiting for the next step would read as a
    /// control that did nothing.
    /// </para>
    /// <para>
    /// Ships on by coordinator override of 2026-09-17. The port has rendered the full preview on
    /// every step since ruling Q18, and the checkbox is what turns that off.
    /// </para>
    /// </remarks>
    public bool RenderOnNavigate
    {
        get => _getRenderOnNavigate is { } read ? read() : _renderOnNavigateWithoutASettingsSeam;
        set
        {
            if (value == RenderOnNavigate)
            {
                return;
            }

            _renderOnNavigateWithoutASettingsSeam = value;
            _setRenderOnNavigate?.Invoke(value);
            OnPropertyChanged();

            if (value)
            {
                RenderCurrentFrameNow();
            }
        }
    }

    // Phase review P3-6. The getter reads the live document, so a write made elsewhere is already
    // in effect on the next navigation step; without this the checkbox on screen would keep
    // showing the old state until it was clicked, because the only raise was its own setter's.
    // Nothing else writes general.preview_render_on_navigate today, and a Settings row for it
    // would be the second writer.
    private void OnGeneralChanged(object? sender, GeneralSettings general)
        => OnPropertyChanged(nameof(RenderOnNavigate));

    /// <summary>The offsets after scaling by <paramref name="ratio"/> about the pointer, so the
    /// point under the pointer stays under it. <see cref="Zoom"/> and the mosaic arranger's zoom
    /// share it; both measure the offsets and the pointer in the same viewport frame.</summary>
    public static (double X, double Y) ScaleAbout(double offsetX, double offsetY, double pointerX, double pointerY, double ratio)
        => (pointerX - (pointerX - offsetX) * ratio, pointerY - (pointerY - offsetY) * ratio);

    /// <summary>Pointer-centred wheel zoom, the web's transform ported exactly: it is what makes
    /// the point under the cursor stay under the cursor.</summary>
    /// <param name="delta">The raw wheel delta in the web's <c>e.deltaY</c> sign convention:
    /// positive means scroll down means zoom out. Avalonia's
    /// <c>PointerWheelEventArgs.Delta.Y</c> is the opposite sign, so the view negates it before
    /// calling here and this contract stays the web's.</param>
    /// <param name="pointerX">The pointer's offset from the viewport centre, in pixels.</param>
    /// <param name="pointerY">The pointer's offset from the viewport centre, in pixels.</param>
    public void Zoom(double delta, double pointerX, double pointerY)
        => (Scale, OffsetX, OffsetY) = ZoomAbout(Scale, OffsetX, OffsetY, delta, pointerX, pointerY);

    /// <summary>Spec 11.5's wheel zoom as a pure step: the scale after one wheel
    /// <paramref name="delta"/>, clamped to <see cref="MinZoom"/> and <see cref="MaxZoom"/> of fit,
    /// and the offsets after <see cref="ScaleAbout"/>. At or below fit the offsets are 0. The
    /// preview and the composite lightbox (spec 12.17) share it.</summary>
    public static (double Scale, double OffsetX, double OffsetY) ZoomAbout(
        double scale, double offsetX, double offsetY, double delta, double pointerX, double pointerY)
    {
        var next = Math.Clamp(scale * Math.Exp(-delta * WheelZoomRate), MinZoom, MaxZoom);

        // The web's `if (newScale === oldScale) return`: at either clamp a further notch must not
        // move the offsets either, or the image drifts while the scale stands still.
        if (next == scale)
        {
            return (scale, offsetX, offsetY);
        }

        if (Math.Abs(next - 1d) < FitTolerance)
        {
            next = 1d;
        }

        // At or below fit the image is centred and cannot be panned (coordinator ruling on the
        // zoom-out range): the web snaps to centre on return to fit, and below fit there is
        // nothing off screen to pan to. Double-click or 0 restores fit.
        if (next <= 1d)
        {
            return (next, 0d, 0d);
        }

        var (x, y) = ScaleAbout(offsetX, offsetY, pointerX, pointerY, next / scale);
        return (next, x, y);
    }

    /// <summary>Drag pan. A no-op unless <see cref="Scale"/> is above 1: at fit and below it, the
    /// whole image is on screen and there is nothing to pan to. The view calls this from its
    /// pointer handler; spec 18.3 asserts it here and never synthesizes a gesture.</summary>
    public void Pan(double deltaX, double deltaY)
    {
        if (Scale <= 1d)
        {
            return;
        }

        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    /// <summary>Disposes both slots, which disposes their bitmaps and withdraws anything still in
    /// flight, and cancels the lifetime the header panel's read is bound to. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _unsubscribeGeneralChanged?.Invoke(OnGeneralChanged);

        DetachSlots();
        Thumbnail.Dispose();
        Preview.Dispose();

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---- spec 11.5's interaction table, one command per row ---------------------------------

    /// <summary>Right arrow. Bounded by the originating list: it clamps, it does not wrap
    /// (ruling Q17).</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        // RelayCommand.Execute does not consult CanExecute (TRACKING section 6 item 13), and the
        // bound is a correctness rule rather than button greying: without this, a key binding or a
        // direct caller walks past it and wraps, which is exactly the web behaviour the spec
        // overrides.
        if (!CanGoNext)
        {
            return;
        }

        GoTo(Index + 1);
    }

    /// <summary>Left arrow. Clamps at the first frame.</summary>
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous()
    {
        if (!CanGoPrevious)
        {
            return;
        }

        GoTo(Index - 1);
    }

    /// <summary>Spec 11.5's fit, on double-click and on <c>0</c>. The web's
    /// <c>resetTransform()</c>: scale 1, both offsets 0.</summary>
    [RelayCommand]
    private void Fit()
    {
        Scale = 1d;
        OffsetX = 0d;
        OffsetY = 0d;
    }

    /// <summary><c>H</c>. The panel is built on its first show for a frame and kept while the
    /// frame is on screen, so hiding and showing it again issues no second query.</summary>
    [RelayCommand]
    private void ToggleHeaderPanel()
    {
        IsHeaderPanelVisible = !IsHeaderPanelVisible;
        if (IsHeaderPanelVisible)
        {
            ShowHeaders();
        }
    }

    /// <summary><c>Ctrl+E</c>. Windows only (spec 19.2, ruling Q20).</summary>
    [RelayCommand(CanExecute = nameof(IsWindows))]
    private void RevealInExplorer()
    {
        // The platform guard is a correctness rule, so it is repeated here: RelayCommand.Execute
        // ignores CanExecute. ShellIntegration guards the shell verb as well, which is the choke
        // point; this is the command the roadmap's Verify line names.
        if (!IsWindows)
        {
            return;
        }

        _shell.RevealInExplorer(Current.FilePath);
    }

    /// <summary><c>Ctrl+O</c>. Guarded at the command per the roadmap's Verify line, though the
    /// shell verb itself is cross-platform and is not guarded inside
    /// <c>ShellIntegration</c> (ruling Q20).</summary>
    [RelayCommand(CanExecute = nameof(IsWindows))]
    private void OpenWithDefaultApplication()
    {
        if (!IsWindows)
        {
            return;
        }

        _shell.OpenWithDefaultApplication(Current.FilePath);
    }

    /// <summary><c>Ctrl+Shift+C</c>. The clipboard is cross-platform and is deliberately not
    /// platform-guarded: spec 19.2 names the reveal and open verbs and not this one.</summary>
    [RelayCommand]
    private Task CopyPathAsync() => _shell.CopyTextAsync(Current.FilePath);

    /// <summary><c>Escape</c>, and the window's close button.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Re-requests the preview after a failed render, the port of the web modal's Retry
    /// (<c>preview.py</c> answers a render failure with a 500).</summary>
    [RelayCommand]
    private void Retry()
    {
        if (_disposed)
        {
            return;
        }

        ErrorText = null;
        Preview.Load();
        RaiseImageState();
    }

    // ---- rendering and navigation ------------------------------------------------------------

    private void GoTo(int index)
    {
        Index = index;
        Current = Frames[Index];

        // The web's resetForNewFile: the previous bitmaps are released (disposing the slots is the
        // C# equivalent of revokeObjectURL), the transform resets, and the error clears.
        Fit();
        ErrorText = null;

        // The panel is per frame, so its content is dropped; whether it is showing is the user's
        // choice and survives, which is what makes it useful for comparing frames.
        Headers = null;

        var previousThumbnail = Thumbnail;
        var previousPreview = Preview;
        DetachSlots();
        (Thumbnail, Preview) = CreateSlots();
        AttachSlots();

        // Disposing the previous pair withdraws whatever they still had in flight and releases
        // their bitmaps: the C# equivalent of the web's revokeObjectURL.
        previousThumbnail.Dispose();
        previousPreview.Dispose();

        // Spec 11.5's one branch. Off, the step shows the new frame's cached thumbnail, which is
        // what makes stepping through a few hundred frames usable; on, it renders as the modal's
        // own open does. Read here rather than captured in the constructor, which is the spec's
        // "read on each step".
        LoadSlots(renderPreview: RenderOnNavigate);
        if (IsHeaderPanelVisible)
        {
            ShowHeaders();
        }
    }

    private (ThumbnailSlotViewModel Thumbnail, ThumbnailSlotViewModel Preview) CreateSlots()
        => (_createSlot(Current.FilePath, ThumbnailKind.Frame),
            _createSlot(Current.FilePath, ThumbnailKind.Preview));

    // Ruling Q18: both render automatically on open, and on every navigation while spec 11.5's
    // general.preview_render_on_navigate is on. They share the one bounded worker, whose LIFO
    // order is what makes fast stepping serve the frame the user is on.
    //
    // No cancellation is added here and none is needed: GoTo disposes the previous pair before
    // this runs, and disposing a slot withdraws whatever it still had in flight. That is the port
    // already behaving better than the web, whose requestZoom has no AbortController and can paint
    // the previous frame's blob into the new index.
    private void LoadSlots(bool renderPreview = true)
    {
        Thumbnail.Load();
        if (renderPreview)
        {
            Preview.Load();
        }

        RaiseImageState();
    }

    // Coordinator ruling Q13. The frame on screen has no preview only when the step that brought
    // it here ran with the flag off, so this is the one case the guard admits; a preview already
    // rendered or already in flight is left alone rather than requested twice.
    private void RenderCurrentFrameNow()
    {
        if (_disposed || Preview.Image is not null || Preview.IsLoading)
        {
            return;
        }

        ErrorText = null;
        Preview.Load();
        RaiseImageState();
    }

    private void AttachSlots()
    {
        Thumbnail.PropertyChanged += OnSlotPropertyChanged;
        Preview.PropertyChanged += OnSlotPropertyChanged;
    }

    private void DetachSlots()
    {
        Thumbnail.PropertyChanged -= OnSlotPropertyChanged;
        Preview.PropertyChanged -= OnSlotPropertyChanged;
    }

    private void OnSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName == nameof(ThumbnailSlotViewModel.IsLoading)
            && ReferenceEquals(sender, Preview)
            && !Preview.IsLoading
            && Preview.Image is null)
        {
            // Spec 6.2.6: the frame's pixels could not be read. The modal stays open with the
            // header panel available and offers a retry rather than an empty window.
            ErrorText = "This frame could not be rendered.";
            _logger.LogDebug("No preview was produced for {Frame}", Current.FilePath);
        }

        RaiseImageState();
    }

    private void RaiseImageState()
    {
        OnPropertyChanged(nameof(DisplayImage));
        OnPropertyChanged(nameof(ShowsPlaceholder));
        OnPropertyChanged(nameof(IsRendering));
    }

    private void ShowHeaders()
    {
        if (Headers is not null)
        {
            return;
        }

        // Current.Row is what makes the Derived metrics section real: FrameHeadersQuery returns
        // raw_headers, provenance and the two FWHM values only, and every other metric comes from
        // the read model the caller already held (spec 12.4, spec 7.3).
        Headers = new RawHeaderPanelViewModel(
            Current.ImageId, _getHeaders, Current.Row, post: _post, logger: _logger,
            lifetime: _lifetimeToken);
        Headers.Load();
    }
}
