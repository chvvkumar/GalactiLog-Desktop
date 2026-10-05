using GalactiLog.App.ViewModels.Tray;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11 behaviour 10 as this version ships it: the notification-area tooltip carries the
/// last run's outcome and counts until the next scan starts, and the durable record is the
/// scan_finished or scan_failed row the Activity page already shows (spec 12.6, 10.9).
/// </summary>
/// <remarks>
/// The icon is the application's own Shell_NotifyIcon since ruling R30 (NativeTrayIcon), so a
/// NIF_INFO balloon is one NIM_MODIFY away; a Windows toast needs a package and a
/// Windows-flavoured TargetFramework. Both are recorded in questions.md Q6 with their costs; a
/// later phase replaces this one file and nothing else (coordinator ruling Q6).
/// </remarks>
public sealed class TrayTooltipScanNotifier : IScanCompletionNotifier
{
    private readonly TrayIconViewModel _tray;
    private readonly ILogger _logger;

    /// <param name="tray">Spec 12.11 behaviour 4's view-model. It keeps one <c>ToolTipText</c>
    /// with one composer; this type hands that composer its second input and never writes the
    /// property itself.</param>
    /// <param name="logger">Optional. A failed assignment is logged, never rethrown.</param>
    public TrayTooltipScanNotifier(TrayIconViewModel tray, ILogger? logger = null)
    {
        _tray = tray;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Always true: this notifier needs nothing the process does not already have. There
    /// is no package, no P/Invoke and no Application User Model ID, so it works identically on an
    /// installed build and on a <c>dotnet run</c>.</summary>
    public bool IsAvailable => true;

    /// <inheritdoc />
    public void Show(ScanCompletionNotice notice)
    {
        try
        {
            _tray.LastCompletion = notice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The scan completion notice could not be put on the tray tooltip");
        }
    }
}
