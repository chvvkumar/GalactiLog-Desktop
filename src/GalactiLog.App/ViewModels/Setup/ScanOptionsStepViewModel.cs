using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Setup;

/// <summary>
/// Design-spec 12.1's step 4: the auto-scan interval over the seven presets, the watcher on/off
/// checkbox and the include-calibration checkbox. Persists <c>auto_scan_enabled</c>,
/// <c>auto_scan_interval_minutes</c>, <c>watcher_enabled</c> and <c>include_calibration</c>.
/// </summary>
/// <remarks>
/// <para>
/// The presets, and the rule for publishing them into the bound list, are
/// <c>IntervalChoices</c>, the same body the Settings Library tab uses, so the wizard and the tab
/// cannot disagree about what "4 hours" is or about what happens to a selection. The default is
/// <strong>240</strong>, which is spec 12.1's own "default 4 hours" and the settings schema
/// default; the web wizard's local default of 360 is not ported, because it disagrees with both.
/// </para>
/// <para>
/// The watcher checkbox is port-only: the web has no watcher. Spec 12.1's step 4 lists it with a
/// default of on, which is also <c>GeneralSettings.WatcherEnabled</c>'s default.
/// </para>
/// </remarks>
public sealed partial class ScanOptionsStepViewModel : SetupStepViewModel
{
    /// <summary>Spec 12.1's interval default, in minutes.</summary>
    public const int DefaultIntervalMinutes = 240;

    /// <summary>The five folder names the first run's default rules exclude, named in the step's
    /// own copy exactly as the web names them (questions.md Q32).</summary>
    /// <remarks>Phase 14B Task 5 (questions.md Q6): the list itself moved to
    /// <see cref="ScanFilterConfig.SeededExcludeNames"/> in Core, beside spec 12.2's notice
    /// predicate, so the wizard and the predicate cannot disagree about what the seeded five are.
    /// </remarks>
    public static IReadOnlyList<string> ExcludeDefaults => ScanFilterConfig.SeededExcludeNames;

    private readonly Func<IReadOnlyList<string>> _chosenFolders;

    /// <param name="chosenFolders">The folders chosen on step 1, read when the wizard navigates
    /// onto this step, so the copy names them the way the web's does.</param>
    public ScanOptionsStepViewModel(
        Func<IReadOnlyList<string>> chosenFolders,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(post, logger)
    {
        _chosenFolders = chosenFolders;

        AutoScanEnabled = true;
        WatcherEnabled = true;
        IncludeCalibration = false;

        // Offered and selected in one call, in that order: the selection is always an option the
        // list already holds, never one assigned beside an empty list.
        SelectedInterval = IntervalChoices.Publish(IntervalOptions, DefaultIntervalMinutes);
        Intro = BuildIntro();
    }

    /// <inheritdoc />
    public override string Title => "Scan options";

    /// <summary>The web wizard's step 3 copy, with the chosen folders named.</summary>
    [ObservableProperty]
    public partial string Intro { get; private set; }

    /// <summary>The seven presets, plus a stored-but-unlisted value on a re-run.</summary>
    public ObservableCollection<IntervalOption> IntervalOptions { get; } = [];

    /// <summary><c>general.auto_scan_interval_minutes</c>, default 240.</summary>
    [ObservableProperty]
    public partial IntervalOption? SelectedInterval { get; set; }

    /// <summary><c>general.auto_scan_enabled</c>.</summary>
    [ObservableProperty]
    public partial bool AutoScanEnabled { get; set; }

    /// <summary><c>general.watcher_enabled</c>, default on (spec 12.1).</summary>
    [ObservableProperty]
    public partial bool WatcherEnabled { get; set; }

    /// <summary><c>general.include_calibration</c>, default off (spec 12.1).</summary>
    [ObservableProperty]
    public partial bool IncludeCalibration { get; set; }

    /// <inheritdoc />
    public override void OnEntered() => Intro = BuildIntro();

    /// <inheritdoc />
    public override void Load(GeneralSettings general)
    {
        AutoScanEnabled = general.AutoScanEnabled;
        WatcherEnabled = general.WatcherEnabled;
        IncludeCalibration = general.IncludeCalibration;

        // The same body the Library tab publishes its interval list through, so a re-run of the
        // wizard over a hand-edited document offers the extra entry on the same rule, and neither
        // surface can clear a collection the window has a ComboBox bound to.
        SelectedInterval = IntervalChoices.Publish(IntervalOptions, general.AutoScanIntervalMinutes);
    }

    /// <inheritdoc />
    public override GeneralSettings Apply(GeneralSettings general)
    {
        var minutes = SelectedInterval?.Minutes ?? DefaultIntervalMinutes;
        var autoScan = AutoScanEnabled;
        var watcher = WatcherEnabled;
        var calibration = IncludeCalibration;

        return general with
        {
            AutoScanEnabled = autoScan,
            AutoScanIntervalMinutes = minutes,
            WatcherEnabled = watcher,
            IncludeCalibration = calibration,
        };
    }

    private string BuildIntro()
    {
        IReadOnlyList<string> folders;
        try
        {
            folders = _chosenFolders();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The chosen scan folders could not be read for the scan options copy");
            folders = [];
        }

        var label = folders.Count switch
        {
            0 => "the folders you chose",
            1 => folders[0],
            _ => string.Join(", ", folders),
        };

        return $"Everything under {label} is scanned. Folders named {string.Join(", ", ExcludeDefaults)} "
            + "are skipped anywhere in the tree. Change this later under Settings, Library, Scan filters.";
    }
}
