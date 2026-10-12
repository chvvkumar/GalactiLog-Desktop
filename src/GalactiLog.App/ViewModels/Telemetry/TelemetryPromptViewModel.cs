using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Telemetry;

/// <summary>
/// The one-time telemetry question, shown after setup on an installed build. Continue writes the
/// checkbox; closing by the title bar or Escape writes "no". Either way the user is not asked again.
/// </summary>
public sealed partial class TelemetryPromptViewModel : ObservableObject, IModalPageViewModel
{
    /// <summary>The dialog's title.</summary>
    public const string Title = "Help improve GalactiLog";

    /// <summary>The one button.</summary>
    public const string ContinueLabel = "Continue";

    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> _mutate;
    private readonly ILogger _logger;
    private bool _decided;

    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>.</param>
    /// <param name="disclosure">The shared copy, preview and link.</param>
    public TelemetryPromptViewModel(
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        TelemetryDisclosureViewModel disclosure,
        ILogger? logger = null)
    {
        _mutate = mutateGeneral;
        Disclosure = disclosure;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The shared copy, preview and link.</summary>
    public TelemetryDisclosureViewModel Disclosure { get; }

    /// <summary>The checkbox. Checked by default; nothing is stored until the user answers.</summary>
    [ObservableProperty]
    public partial bool SendStatistics { get; set; } = true;

    /// <summary>The write of the decision, for tests. Null until decided.</summary>
    public Task? PendingDecision { get; private set; }

    /// <summary>
    /// Records the answer once. Later calls do nothing, so the close that follows Continue does not
    /// overwrite it with "no". The write runs off the UI thread and never throws: a failure logs at
    /// Debug and the prompt shows again on the next start.
    /// </summary>
    public Task Decide(bool enabled)
    {
        if (_decided)
        {
            return PendingDecision ?? Task.CompletedTask;
        }

        _decided = true;
        return PendingDecision = Task.Run(() =>
        {
            try
            {
                _mutate(general => TelemetryDisclosureViewModel.WithDecision(general, enabled));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The telemetry decision could not be saved");
            }
        });
    }

    /// <summary>The title bar or Escape: "no", and prompted.</summary>
    public Task Dismiss() => Decide(false);

    /// <inheritdoc />
    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Continue()
    {
        _ = Decide(SendStatistics);
        CloseRequested?.Invoke(this, true);
    }
}
