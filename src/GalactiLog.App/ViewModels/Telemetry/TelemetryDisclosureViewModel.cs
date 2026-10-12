using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services.Telemetry;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Telemetry;

/// <summary>
/// The shared part of the three telemetry surfaces (the first-run prompt, the setup wizard's scan
/// options step and the Settings General tab): the copy, the report preview, the public statistics
/// link and the one rule for what a decision writes.
/// </summary>
/// <remarks>
/// The preview text comes from <see cref="TelemetryReportBuilder.Build"/>, the same function the
/// sender posts, so what the user reads is what leaves the machine.
/// </remarks>
public sealed partial class TelemetryDisclosureViewModel : ObservableObject
{
    /// <summary>The checkbox label, the same on all three surfaces.</summary>
    public const string CheckBoxLabel = "Send anonymous usage statistics";

    /// <summary>What is sent, and where it can be read. "Settings you entered" rather than "any
    /// setting": the report carries the theme, text size and font keys.</summary>
    public const string Description =
        "Each report carries a random install id, the application version, Windows and .NET "
        + "versions, anonymous counters, warning templates and, after a crash, the exception type "
        + "and the methods on the stack. It never carries file paths, folder or target names, or "
        + "settings you entered. Everything collected is public.";

    /// <summary>The expander header on the prompt and the wizard.</summary>
    public const string PreviewHeader = "Show exactly what will be sent";

    /// <summary>The link button label.</summary>
    public const string PublicStatsLabel = "View public statistics";

    /// <summary>Shown in the preview box when the report could not be built.</summary>
    public const string PreviewFailedText = "The report could not be built.";

    private readonly Func<string> _build;
    private readonly Action<string> _open;
    private readonly ILogger _logger;

    /// <param name="build">Normally <c>TelemetryReportBuilder.Build(includeCrash: true, indented:
    /// true)</c>. Run off the UI thread: it reads settings synchronously.</param>
    /// <param name="open">Normally <c>ShellIntegration.OpenWithDefaultApplication</c>.</param>
    public TelemetryDisclosureViewModel(Func<string> build, Action<string> open, ILogger? logger = null)
    {
        _build = build;
        _open = open;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The report as it would be sent, or empty until loaded.</summary>
    [ObservableProperty]
    public partial string PreviewText { get; private set; } = "";

    /// <summary>Bound to the expander. The first expand loads the preview.</summary>
    [ObservableProperty]
    public partial bool IsPreviewExpanded { get; set; }

    /// <summary>Whether the preview box is on screen (Settings shows it after the button).</summary>
    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>The last load, for tests.</summary>
    public Task? PendingPreview { get; private set; }

    /// <summary>
    /// The one rule for what a telemetry decision writes. Turning on: enabled, prompted, a new
    /// lowercase GUID. Staying on (a wizard re-run): the id already stored. Off: disabled,
    /// prompted, no id. Fast and side-effect free, so it can run inside
    /// <c>SettingsStore.MutateGeneral</c>.
    /// </summary>
    public static GeneralSettings WithDecision(GeneralSettings general, bool enabled) => general with
    {
        TelemetryEnabled = enabled,
        TelemetryPrompted = true,
        TelemetryId = !enabled
            ? null
            : general is { TelemetryEnabled: true, TelemetryId: { } id }
                ? id
                : Guid.NewGuid().ToString("D"),
    };

    partial void OnPreviewTextChanged(string value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnIsPreviewExpandedChanged(bool value)
    {
        if (value && PendingPreview is null)
        {
            LoadPreview();
        }
    }

    /// <summary>Builds the report off the UI thread and shows it. Never throws.</summary>
    [RelayCommand]
    private void LoadPreview() => PendingPreview = LoadPreviewAsync();

    private async Task LoadPreviewAsync()
    {
        string text;
        try
        {
            text = await Task.Run(_build).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The telemetry preview could not be built");
            text = PreviewFailedText;
        }

        PreviewText = text;
    }

    [RelayCommand]
    private void OpenPublicStats()
    {
        try
        {
            _open(TelemetryEndpoints.PublicStatsUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The public statistics page could not be opened");
        }
    }
}
