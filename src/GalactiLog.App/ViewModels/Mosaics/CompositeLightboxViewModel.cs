using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.Core.Io;
using GalactiLog.Core.Mosaics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// Spec 12.17's composite lightbox (Phase 19B), the port of the web's
/// <c>MosaicCompositeModal.tsx</c>: Building, then Ready or Failed with Retry, over spec 11.6's
/// <see cref="CompositeService"/>. Zoom and pan are spec 11.5's preview rules through
/// <see cref="PreviewModalViewModel.ZoomAbout"/>.
/// </summary>
/// <remarks>
/// <para>
/// Opening on a cache hit goes straight to Ready with no job. Closing (<see cref="Dispose"/>)
/// cancels a build in flight and shows nothing; a build cancelled from the status bar flyout while
/// the window is open shows Failed with <see cref="CancelledText"/>.
/// </para>
/// <para>
/// This file is a Phase 19B <c>AppWriter.BeginExport</c> caller (spec 2.1.1), allowlisted by full
/// path in <c>FileSafetyTest</c>: Download writes the composite's bytes as one new file at the path
/// the save dialog returned and nothing else.
/// </para>
/// </remarks>
public sealed partial class CompositeLightboxViewModel : ObservableObject, IZoomPanSurface, IDisposable
{
    /// <summary>The Building caption (spec 12.17).</summary>
    public const string BuildingText = "Building the composite...";

    /// <summary>Failed's reason for a build cancelled from the status bar flyout (spec 12.17).</summary>
    public const string CancelledText = "The build was cancelled.";

    /// <summary>What a failed Download write shows under the image, as Export panels does.</summary>
    public const string DownloadFailedText = MosaicDetailViewModel.ExportFailedText;

    private readonly CompositeService _service;
    private readonly AppWriter _appWriter;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly Func<byte[], Bitmap> _decode;
    private readonly string? _leftOut;
    private CancellationTokenSource? _build;
    private CompositeResult? _result;
    private bool _disposed;

    /// <param name="request">The composite to show, built by the detail page for the arranger's
    /// selected filter; <see cref="CompositeService.Select"/> already found it possible.</param>
    /// <param name="service">Spec 11.6's cache and build.</param>
    /// <param name="appWriter">The application's one <c>AppWriter</c>, for Download.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">A failed build or write is logged, never thrown on the UI thread.</param>
    /// <param name="decode">JPEG bytes to a bitmap, from memory and never a path; a test passes a
    /// fake.</param>
    public CompositeLightboxViewModel(
        CompositeRequest request,
        CompositeService service,
        AppWriter appWriter,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<byte[], Bitmap>? decode = null)
    {
        Request = request;
        _service = service;
        _appWriter = appWriter;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _decode = decode ?? (bytes => new Bitmap(new MemoryStream(bytes)));
        _leftOut = LeftOutSentence(service.Select(request).LeftOut, request.Filter);
        Title = $"{request.MosaicName}, {request.Filter} composite";
        ImageName = $"Composite of {request.MosaicName}, {request.Filter}";
        Scale = 1d;

        if (service.TryGetCached(request, out var hit))
        {
            ShowReady(hit);
        }
        else
        {
            Start();
        }
    }

    /// <summary>Raised by Close and Escape; the window answers it.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>What this lightbox composites.</summary>
    public CompositeRequest Request { get; }

    /// <summary>"&lt;mosaic name&gt;, &lt;filter&gt; composite".</summary>
    public string Title { get; }

    /// <summary>The image's automation name, "Composite of &lt;mosaic name&gt;, &lt;filter&gt;".</summary>
    public string ImageName { get; }

    /// <summary>The newest build, so a test awaits it.</summary>
    internal Task PendingBuild { get; private set; } = Task.CompletedTask;

    /// <summary>Building: the spinner and <see cref="BuildingText"/>.</summary>
    [ObservableProperty]
    public partial bool IsBuilding { get; private set; }

    /// <summary>The job's current message under the caption, "Decoding &lt;panel label&gt;".</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    /// <summary>Ready: the composite, decoded from its JPEG bytes in memory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial Bitmap? Image { get; private set; }

    /// <summary>Ready's left-out sentence, null when every panel is in or before Ready.</summary>
    [ObservableProperty]
    public partial string? LeftOutText { get; private set; }

    /// <summary>Failed: the reason, shown in the error ink with Retry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    /// <summary>Shows the error block.</summary>
    public bool HasError => ErrorText is not null;

    /// <summary>A failed Download write, under the image.</summary>
    [ObservableProperty]
    public partial string? DownloadError { get; private set; }

    /// <summary>Download is enabled in Ready alone.</summary>
    public bool CanDownload => Image is not null;

    [ObservableProperty]
    public partial double Scale { get; private set; }

    [ObservableProperty]
    public partial double OffsetX { get; private set; }

    [ObservableProperty]
    public partial double OffsetY { get; private set; }

    /// <summary>Spec 12.17's one sentence: "Not in this composite: &lt;label&gt; (no &lt;filter&gt;
    /// frames), &lt;label&gt; (no position).", every left-out panel in <c>sort_order</c>; null when
    /// none is.</summary>
    public static string? LeftOutSentence(IReadOnlyList<LeftOutPanel> leftOut, string filter)
        => leftOut.Count == 0
            ? null
            : "Not in this composite: " + string.Join(", ", leftOut.Select(panel =>
                $"{panel.Label} ({(panel.Reason == LeftOutReason.NoFrames ? $"no {filter} frames" : "no position")})")) + ".";

    /// <summary>Download's suggested name, <c>&lt;name&gt;-&lt;filter&gt;.jpg</c>, each through the
    /// Export panels character rule.</summary>
    public static string DownloadFileName(string mosaicName, string filter)
        => $"{MosaicDetailViewModel.SafeFileName(mosaicName)}-{MosaicDetailViewModel.SafeFileName(filter)}.jpg";

    // ---- the build ----------------------------------------------------------------------------

    private void Start()
    {
        _build?.Dispose();
        var build = _build = new CancellationTokenSource();
        ErrorText = null;
        ProgressText = null;
        IsBuilding = true;
        PendingBuild = RunAsync(build);
    }

    private async Task RunAsync(CancellationTokenSource build)
    {
        var token = build.Token;
        try
        {
            var result = await _service.BuildAsync(Request, token, message => Show(build, () => ProgressText = message))
                .ConfigureAwait(false);
            Show(build, () => ShowReady(result));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closing the window cancelled it: nothing is shown and nothing was cached.
            _logger.LogDebug("The composite build was cancelled by closing the lightbox");
        }
        catch (OperationCanceledException)
        {
            Show(build, () => Fail(CancelledText));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Building the mosaic composite failed");
            Show(build, () => Fail(ex.Message));
        }
    }

    // A late message from a build this lightbox no longer shows is dropped.
    private void Show(CancellationTokenSource build, Action apply)
        => _post(() =>
        {
            if (!_disposed && ReferenceEquals(build, _build))
            {
                apply();
            }
        });

    private void ShowReady(CompositeResult result)
    {
        try
        {
            Image = _decode(result.Jpeg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decoding the mosaic composite failed");
            Fail(ex.Message);
            return;
        }

        _result = result;
        IsBuilding = false;
        ProgressText = null;
        LeftOutText = _leftOut;
    }

    private void Fail(string reason)
    {
        IsBuilding = false;
        ProgressText = null;
        ErrorText = reason;
    }

    /// <summary>Failed's Retry: a new build, a new job.</summary>
    [RelayCommand]
    private void Retry()
    {
        if (_disposed || IsBuilding || Image is not null)
        {
            return;
        }

        Start();
    }

    // ---- download -----------------------------------------------------------------------------

    /// <summary>Opens the platform save dialog for the suggested file name and returns the chosen
    /// absolute path, or null when cancelled. Set by the window; null in a test that does not
    /// exercise it, which makes Download a no-op there.</summary>
    public Func<string, Task<string?>>? ExportDestinationPicker { get; set; }

    /// <summary>Spec 12.17's Download: the composite's bytes, byte for byte the image shown, as one
    /// new file at the path the save dialog returned.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (_result is not { } result || ExportDestinationPicker is not { } picker || _disposed)
        {
            return;
        }

        DownloadError = null;
        try
        {
            var path = await picker(DownloadFileName(Request.MosaicName, Request.Filter)).ConfigureAwait(true);
            if (path is null || _disposed)
            {
                return;
            }

            using var writer = _appWriter.BeginExport(path);
            writer.WriteAllBytes(path, result.Jpeg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Writing the mosaic composite failed");
            DownloadError = DownloadFailedText;
        }
    }

    // ---- zoom and pan, spec 11.5's rules --------------------------------------------------------

    /// <summary>Pointer-centred wheel zoom from 0.1x to 8x of fit; <paramref name="delta"/> in the
    /// web's <c>deltaY</c> sign, the pointer measured from the viewport centre.</summary>
    public void Zoom(double delta, double pointerX, double pointerY)
        => (Scale, OffsetX, OffsetY) = PreviewModalViewModel.ZoomAbout(Scale, OffsetX, OffsetY, delta, pointerX, pointerY);

    /// <summary>Drag pan, refused at fit and below.</summary>
    public void Pan(double deltaX, double deltaY)
    {
        if (Scale <= 1d)
        {
            return;
        }

        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    /// <summary>Double-click and the <c>0</c> key: back to fit.</summary>
    [RelayCommand]
    public void ResetFit()
    {
        Scale = 1d;
        OffsetX = 0d;
        OffsetY = 0d;
    }

    /// <summary>Escape and the Close button.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Cancels a build in flight without waiting for it and disposes the bitmap.
    /// Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _build?.Cancel();
        _build?.Dispose();
        var image = Image;
        Image = null;
        image?.Dispose();
    }
}
