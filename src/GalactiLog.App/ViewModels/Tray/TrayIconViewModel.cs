using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Tray;

/// <summary>
/// Spec 12.11 behaviour 4: what the notification-area icon shows and what its menu does. It owns
/// no behaviour of its own. Scan now is the status bar's command object; Check for updates calls
/// the one update service; Open and Exit call the one residency service.
/// </summary>
/// <remarks>
/// Design-lessons rule 1. A tray menu is the second place in this application that offers Run scan
/// and Check for updates, so it reuses the command and the service the first place already uses
/// rather than growing a second guard for each. A second run-scan guard here would be a tray menu
/// that can start a scan the status bar's guard would have refused.
/// </remarks>
public sealed partial class TrayIconViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Windows truncates a notification-area tooltip at 127 characters. Truncated here rather than
    /// left to the platform, so a long scan message can never produce a silently empty tooltip.
    /// </summary>
    public const int ToolTipMaxLength = 127;

    /// <summary>The tooltip while nothing is running. The application name first, because that is
    /// the half the user is looking for in a crowded notification area.</summary>
    public const string IdleToolTipText = "GalactiLog";

    private const string RunningToolTipPrefix = "GalactiLog - ";

    // Spec 12.11 behaviour 10's separator between the outcome and the counts.
    private const string CompletionSeparator = " - ";

    private ScanCompletionNotice? _lastCompletion;

    private readonly StatusBarViewModel _statusBar;
    private readonly WindowResidencyService _residency;
    private readonly UpdateService? _updates;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private bool _disposed;

    /// <param name="statusBar">Spec 12's status bar view-model. The tooltip is composed from its
    /// <see cref="StatusBarViewModel.StateText"/>, and Scan now is its own command object, so the
    /// tray and the status bar cannot disagree about what the scan is doing or about whether one
    /// may be started.</param>
    /// <param name="residency">Spec 12.11's one owner of whether the window is on screen and of
    /// the one path out of the process.</param>
    /// <param name="updates">Spec 17.1's update service. Null leaves Check for updates present and
    /// disabled, which is the surface a build the updater did not install gets.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed check is logged, never rethrown.</param>
    public TrayIconViewModel(
        StatusBarViewModel statusBar,
        WindowResidencyService residency,
        UpdateService? updates = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _statusBar = statusBar;
        _residency = residency;
        _updates = updates;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        // Two sources, because the tooltip reads both: StateText changes as the scan moves from
        // discovery to ingest without IsRunning ever toggling, and IsRunning is what decides
        // whether the tooltip carries a state at all. The shape is StatusBarViewModel's own
        // OnStatusPropertyChanged.
        _statusBar.PropertyChanged += OnStatusBarPropertyChanged;
        _statusBar.Status.PropertyChanged += OnScanStatusPropertyChanged;
    }

    /// <summary>
    /// The tooltip (spec 12.11 behaviour 4: "its tooltip reads the application name when idle and
    /// the scan state while a scan runs").
    /// </summary>
    public string ToolTipText => ComposeToolTip();

    /// <summary>
    /// Spec 12.11 behaviour 10's notice, or null when there is none to report. The second input
    /// to the one tooltip composer, never a second writer of <see cref="ToolTipText"/>: one
    /// property, one composer, two inputs.
    /// </summary>
    /// <remarks>
    /// Written by <c>TrayTooltipScanNotifier</c> on the UI thread and cleared the moment a scan
    /// starts, so the tooltip never reports a finished run over a running one. The composer
    /// prefers the scan state while a scan runs, which is spec 12.11 behaviour 4's rule and is
    /// unchanged by this input.
    /// </remarks>
    public ScanCompletionNotice? LastCompletion
    {
        get => _lastCompletion;
        set
        {
            if (ReferenceEquals(_lastCompletion, value))
            {
                return;
            }

            _lastCompletion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    /// <summary>
    /// Spec 12.11 behaviour 4's menu item 2. The very same command instance the status bar's Run
    /// scan button is bound to, never a second one.
    /// </summary>
    public ICommand ScanNowCommand => _statusBar.RunScanCommand;

    /// <summary>
    /// Detaches both status subscriptions. It does not dispose the status bar or the update
    /// service: the host owns both.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusBar.PropertyChanged -= OnStatusBarPropertyChanged;
        _statusBar.Status.PropertyChanged -= OnScanStatusPropertyChanged;
    }

    /// <summary>Spec 12.11 behaviour 4's menu item 1.</summary>
    [RelayCommand]
    private void Open()
    {
        // TRACKING item 13: the body carries the guard, because RelayCommand.Execute ignores
        // CanExecute and a native menu item is not a button this application controls. The
        // exiting half of that guard is the service's now (Task 1 review, important finding):
        // ShowAndActivate is a no-op while IsExiting, so the three callers of it cannot disagree
        // about whether an activation after the drain is allowed.
        if (_disposed)
        {
            return;
        }

        _residency.ShowAndActivate();
    }

    /// <summary>Spec 12.11 behaviour 4's menu item 4, after the separator.</summary>
    [RelayCommand]
    private void Exit()
    {
        if (_disposed || _residency.IsExiting)
        {
            return;
        }

        _residency.RequestExit();
    }

    /// <summary>Spec 12.11 behaviour 4's menu item 3.</summary>
    /// <remarks>
    /// No <see cref="CancellationToken"/> parameter (TRACKING section 6 item 13, Phase 9 D10): a
    /// command built from a token-taking delegate cancels the in-flight token on a second press,
    /// which would abort the check rather than refuse the press.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (!CanCheckForUpdates())
        {
            return;
        }

        try
        {
            await _updates!.CheckNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The service reports its own failures through its state; this catch exists so a
            // faulted command task cannot be rethrown onto whichever context the menu click
            // arrived on.
            _logger.LogWarning(ex, "The tray menu could not start an update check");
        }
        finally
        {
            // The await above resumes off the dispatcher, and this touches command state a native
            // menu item is bound to.
            _post(() =>
            {
                if (!_disposed)
                {
                    CheckForUpdatesCommand.NotifyCanExecuteChanged();
                }
            });
        }
    }

    // Ruling Q4: the same predicate spec 12.7's About tab button reads, owned by the update
    // service. No second copy of "not disposed, installed, not already checking" lives here.
    private bool CanCheckForUpdates() => !_disposed && _updates is { CanCheckNow: true };

    // The one composer, with two inputs (spec 12.11 behaviours 4 and 10). A running scan wins:
    // what the application is doing now is what a user hovering the icon is asking about, and the
    // last run's outcome is what they are asking about when nothing is running.
    private string ComposeToolTip()
    {
        if (!_statusBar.Status.IsRunning)
        {
            return _lastCompletion is { } completion
                ? Cap(completion.Title + CompletionSeparator + completion.Body)
                : IdleToolTipText;
        }

        var state = _statusBar.StateText;
        if (state.Length == 0)
        {
            return IdleToolTipText;
        }

        return Cap(RunningToolTipPrefix + state);
    }

    private static string Cap(string text)
        => text.Length <= ToolTipMaxLength ? text : text[..ToolTipMaxLength];

    private void OnStatusBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName is nameof(StatusBarViewModel.StateText) or null)
        {
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    private void OnScanStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName is nameof(ScanStatusService.IsRunning) or null)
        {
            // Spec 12.11 behaviour 10: the notice stands "until the next scan starts". Cleared
            // here rather than by whoever starts a scan, so every trigger (the tray menu, the
            // status bar, the watcher, the scheduler, a CLI run beside the window) clears it
            // through one expression.
            if (_statusBar.Status.IsRunning)
            {
                LastCompletion = null;
            }

            OnPropertyChanged(nameof(ToolTipText));
        }
    }
}
