using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Survey;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Preview;

/// <summary>
/// Spec 12.4's Sky view window: one survey still image of the target, refetched for each settled
/// pan, zoom, survey, Reset or Refresh. Every field and pan comes from <see cref="SurveyView"/>;
/// this type only sequences the fetches and keeps the drawn image's transform.
/// </summary>
public sealed partial class SurveyViewViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.4: the zoom fetch runs this long after the last wheel notch or key.</summary>
    public static readonly TimeSpan ZoomSettle = TimeSpan.FromMilliseconds(400);

    private readonly SurveyTarget _target;
    private readonly Func<SurveyView, bool, CancellationToken, Task<SurveyImageResult>> _fetch;
    private readonly Action<string> _saveSurvey;
    private readonly Func<byte[], Bitmap?> _decode;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly bool _ready;

    private CancellationTokenSource? _inFlight;
    private CancellationTokenSource? _debounce;
    private SurveyView? _pending;
    private double _drawnFov;
    private bool _disposed;

    /// <param name="target">The target the window opens on, every time (spec 12.4).</param>
    /// <param name="fetch"><c>SurveyImageService.GetAsync</c> bound to the target id.</param>
    /// <param name="readSurvey">Reads <c>general.sky_view_survey</c>, raw.</param>
    /// <param name="saveSurvey">Writes it through <c>SettingsStore.MutateGeneral</c>.</param>
    /// <param name="decode">JPEG bytes to a bitmap; a test passes a fake.</param>
    /// <param name="delay">The zoom settle wait; a test releases it by hand.</param>
    /// <param name="post">How to reach the UI thread; defaults to the dispatcher.</param>
    /// <param name="logger">Optional.</param>
    public SurveyViewViewModel(
        SurveyTarget target,
        Func<SurveyView, bool, CancellationToken, Task<SurveyImageResult>> fetch,
        Func<string?> readSurvey,
        Action<string> saveSurvey,
        Func<byte[], Bitmap?>? decode = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(readSurvey);
        ArgumentNullException.ThrowIfNull(saveSurvey);

        _target = target;
        _fetch = fetch;
        _saveSurvey = saveSurvey;
        _decode = decode ?? (bytes => new Bitmap(new MemoryStream(bytes)));
        _delay = delay ?? Task.Delay;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        SelectedSurvey = Surveys.Resolve(readSurvey());
        Scale = 1d;
        _ready = true;
        View = SurveyView.Initial(target, SelectedSurvey.Id);
        _drawnFov = View.Fov;
        Load(View, refresh: false);
    }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Spec 12.4's window title.</summary>
    public string Title => "Sky view: " + _target.PrimaryName;

    /// <summary>The survey combo's five entries in spec order.</summary>
    public IReadOnlyList<SurveyOption> SurveyOptions => Surveys.All;

    /// <summary>Spec 12.4's credit caption.</summary>
    public string Caption => "Image: CDS hips2fits, " + SelectedSurvey.Label;

    /// <summary>The latest view asked for, drawn or still loading.</summary>
    public SurveyView View { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    public partial SurveyOption SelectedSurvey { get; set; }

    [ObservableProperty]
    public partial Bitmap? Image { get; private set; }

    [ObservableProperty]
    public partial double Scale { get; private set; }

    [ObservableProperty]
    public partial double OffsetX { get; private set; }

    [ObservableProperty]
    public partial double OffsetY { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSentence))]
    public partial string? Sentence { get; private set; }

    /// <summary>True when one of spec 12.4's two sentences shows.</summary>
    public bool HasSentence => Sentence is not null;

    /// <summary>The newest debounce or fetch, for tests to await.</summary>
    internal Task Settled { get; private set; } = Task.CompletedTask;

    /// <summary>Spec 12.4's wheel notch or key: scales the drawn image about the viewport centre
    /// by 1.5 per step and fetches the new field once the input settles.</summary>
    public void Zoom(int steps)
    {
        var basis = _pending ?? View;
        var next = basis.Zoomed(steps);
        if (_disposed || next.Fov == basis.Fov)
        {
            return;
        }

        var factor = basis.Fov / next.Fov;
        Scale *= factor;
        OffsetX *= factor;
        OffsetY *= factor;
        _pending = next;

        // The view in flight is already superseded, so it is dropped now rather than drawn and
        // then rescaled when the settled fetch starts.
        _inFlight?.Cancel();
        _inFlight = null;
        _debounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _debounce = debounce;
        IsLoading = true;
        Settled = SettleZoomAsync(debounce.Token);
    }

    /// <summary>Moves the drawn image with the pointer while a drag is held.</summary>
    public void DragBy(double dx, double dy)
    {
        OffsetX += dx;
        OffsetY += dy;
    }

    /// <summary>Spec 12.4's drag release: the offset as a fraction of the drawn image's side,
    /// positive right and down, fetched at once.</summary>
    public void CommitPan(double dxFraction, double dyFraction)
    {
        if (_disposed || (dxFraction == 0 && dyFraction == 0))
        {
            return;
        }

        // The fraction is of the drawn image, whose field may differ from a pending zoom's.
        var basis = _pending ?? View;
        Load((basis with { Fov = _drawnFov }).Panned(dxFraction, dyFraction) with { Fov = basis.Fov }, refresh: false);
    }

    [RelayCommand]
    private void Reset() => Load(SurveyView.Initial(_target, SelectedSurvey.Id), refresh: false);

    [RelayCommand]
    private void Refresh() => Load(_pending ?? View, refresh: true);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedSurveyChanged(SurveyOption oldValue, SurveyOption newValue)
    {
        // A null pushed through the TwoWay combo binding puts the previous survey back, and that
        // restore (the one change whose old value is null once ready) neither saves nor fetches.
        if (newValue is null)
        {
            SelectedSurvey = oldValue;
            return;
        }

        if (!_ready || _disposed || oldValue is null)
        {
            return;
        }

        try
        {
            _saveSurvey(newValue.Id);
        }
        catch (Exception failure)
        {
            _logger.LogWarning(failure, "Could not save the Sky view survey {SurveyId}", newValue.Id);
        }

        Load((_pending ?? View) with { SurveyId = newValue.Id }, refresh: false);
    }

    private async Task SettleZoomAsync(CancellationToken token)
    {
        try
        {
            await _delay(ZoomSettle, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested && _pending is { } view)
        {
            await Load(view, refresh: false);
        }
    }

    private Task Load(SurveyView view, bool refresh)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _debounce?.Cancel();
        _debounce = null;
        _pending = null;
        _inFlight?.Cancel();
        var fetch = new CancellationTokenSource();
        _inFlight = fetch;
        View = view;
        IsLoading = true;
        Sentence = null;
        return Settled = Task.Run(() => FetchAndPostAsync(fetch, view, refresh), CancellationToken.None);
    }

    private async Task FetchAndPostAsync(CancellationTokenSource fetch, SurveyView view, bool refresh)
    {
        var token = fetch.Token;
        SurveyImageResult result;
        Bitmap? bitmap = null;
        try
        {
            result = await _fetch(view, refresh, token);
            if (result.Outcome == SurveyImageOutcome.Image)
            {
                bitmap = result.Jpeg is { } jpeg ? _decode(jpeg) : null;
                if (bitmap is null)
                {
                    result = new SurveyImageResult(SurveyImageOutcome.Failed, null);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new SurveyImageResult(SurveyImageOutcome.Cancelled, null);
        }
        catch (Exception failure)
        {
            // A refused cache path or an undecodable image reaches here as the error sentence.
            _logger.LogWarning(failure, "The {SurveyId} Sky view image did not load", view.SurveyId);
            result = new SurveyImageResult(SurveyImageOutcome.Failed, null);
        }

        _post(() => Land(fetch, view, result, bitmap));
    }

    private void Land(CancellationTokenSource fetch, SurveyView view, SurveyImageResult result, Bitmap? bitmap)
    {
        if (_disposed || !ReferenceEquals(fetch, _inFlight))
        {
            bitmap?.Dispose();
            return;
        }

        _inFlight = null;
        IsLoading = false;
        switch (result.Outcome)
        {
            case SurveyImageOutcome.Image:
                SwapImage(bitmap);
                Scale = 1d;
                OffsetX = 0d;
                OffsetY = 0d;
                _drawnFov = view.Fov;
                Sentence = null;
                break;

            case SurveyImageOutcome.SwitchOff:
                SwapImage(null);
                Sentence = SurveyMessages.SwitchOff;
                break;

            case SurveyImageOutcome.Failed:
                SwapImage(null);
                Sentence = SurveyMessages.LoadFailed;
                break;

            default:
                Sentence = null;
                break;
        }
    }

    // The binding lets go of the old bitmap before it is disposed.
    private void SwapImage(Bitmap? next)
    {
        var old = Image;
        Image = next;
        old?.Dispose();
    }

    /// <summary>Cancels the fetch in flight and releases the drawn bitmap.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounce?.Cancel();
        _inFlight?.Cancel();
        SwapImage(null);
    }
}
