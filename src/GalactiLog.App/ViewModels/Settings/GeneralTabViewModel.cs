using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Spec 12.7's General tab and spec 12.11's five controls: close to tray, minimize to tray, start
/// with Windows, start minimized, and notify on scan complete. Every control saves immediately,
/// with the roll-back the base class provides.
/// </summary>
/// <remarks>
/// <para>
/// The sixth surface on <see cref="GeneralSettingsTabViewModel"/>, which owns the read, the
/// publish, the serialized write chain and the follow of another writer's <c>GeneralChanged</c>.
/// This tab adds five properties and five immediate saves and no write chain of its own.
/// </para>
/// <para>
/// Spec 12.11 behaviour 12: this tab carries all five controls and nothing else carries any of
/// them. Five residency controls written by four agents into one settings pipeline is how a
/// project grows thirteen touch points per key (design-lessons rule 1), so the whole set lands
/// here in one place.
/// </para>
/// </remarks>
public sealed partial class GeneralTabViewModel : GeneralSettingsTabViewModel
{
    /// <summary>
    /// Ruling Q2, verbatim. Close to tray ships on with no one-time dialog and no separate key to
    /// remember one was shown, so this sentence is the whole of how a user learns what the default
    /// does.
    /// </summary>
    public const string CloseToTrayDescription =
        "Closing the window hides GalactiLog in the notification area. It keeps scanning.";

    /// <summary>Spec 12.11 behaviour 6, stated where the control is.</summary>
    public const string MinimizeToTrayDescription =
        "Minimizing the window hides it too. Reopening restores it at the size it had.";

    /// <summary>Spec 12.11 behaviour 8's reason, shown beside the disabled control rather than as
    /// a silent grey (spec 12.10).</summary>
    public const string StartWithWindowsUnavailableMessage =
        "Starting with Windows is only available on an installed build.";

    /// <summary>Spec 12.11 behaviour 9, stated where the control is.</summary>
    public const string StartMinimizedDescription =
        "GalactiLog starts in the notification area with no window. Scanning runs on its normal schedule.";

    /// <summary>
    /// Spec 12.11 behaviour 10 and ruling Q6, stated exactly as the notice actually behaves. The
    /// mechanism this phase ships is the tray tooltip and not a Windows toast, and a control
    /// promising a toast that never appears is worse than no control.
    /// </summary>
    public const string NotifyOnScanCompleteDescription =
        "While no window is on screen, the tray tooltip carries the finished scan's outcome and its "
        + "non-zero counts until the next scan starts. It is not a Windows notification.";

    /// <summary>Spec 12.7's first Survey downloads sentence, verbatim.</summary>
    public const string SurveyDownloadsDescription =
        "Sky view on a target's page shows survey images of the sky around that target, fetched "
        + "from the internet at alasky.cds.unistra.fr.";

    /// <summary>Spec 12.7's second Survey downloads sentence, verbatim.</summary>
    public const string SurveyDownloadsHostDescription =
        "While this is off, GalactiLog makes no request to that host, and nothing else in the "
        + "application changes.";

    /// <param name="load">Normally <c>SettingsStore.GetGeneral</c>.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>, which is the one
    /// door into the general document (TRACKING section 6 item 14). Not a <c>GetGeneral</c> plus
    /// <c>SaveGeneral</c> pair: see the base class.</param>
    /// <param name="startupShortcut">Spec 12.11 behaviour 8's seam, whose one implementation is
    /// <c>VelopackStartupShortcut</c> (Phase 11 Task 3). Read here to answer whether the Start
    /// with Windows control may be used, and held so <see cref="OnStartWithWindowsChanged"/> can
    /// apply it. Null, which is what a <c>dotnet run</c> and the whole test suite get, disables
    /// the control, puts <see cref="StartWithWindowsUnavailableMessage"/> beside it, and is never
    /// called: the key still saves in that case, because it carries the user's intent even where
    /// nothing can act on it yet.</param>
    public GeneralTabViewModel(
        Func<GeneralSettings> load,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        IStartupShortcut? startupShortcut = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null)
        : base(load, mutateGeneral, post, logger, subscribeGeneralChanged, unsubscribeGeneralChanged)
    {
        _startupShortcut = startupShortcut;

        // Read once, here: whether this build can carry a Startup shortcut is a property of the
        // build and cannot change while the application runs (spec 12.11 behaviour 8). The
        // shortcut's own presence is not read at all, because spec 12.11 behaviour 8 says the
        // setting and the shortcut are not reconciled at start.
        CanSetStartWithWindows = startupShortcut?.IsSupported ?? false;
        StartWithWindowsUnavailableReason =
            CanSetStartWithWindows ? null : StartWithWindowsUnavailableMessage;

        Load();
    }

    // Task 2 left this unread; Task 3 holds it so the shortcut half of the save below can reach
    // it. Not re-read from CanSetStartWithWindows: that flag is derived from IsSupported once at
    // construction, and the field itself is what OnStartWithWindowsChanged calls Apply on.
    private readonly IStartupShortcut? _startupShortcut;

    /// <summary><c>general.close_to_tray</c> (spec 12.11 behaviour 5). Saves immediately.
    /// </summary>
    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    /// <summary><c>general.minimize_to_tray</c> (spec 12.11 behaviour 6). Saves immediately.
    /// </summary>
    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    /// <summary><c>general.start_with_windows</c> (spec 12.11 behaviour 8). Saves immediately.
    /// </summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary><c>general.start_minimized</c> (spec 12.11 behaviour 9). Saves immediately.
    /// </summary>
    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    /// <summary><c>general.notify_on_scan_complete</c> (spec 12.11 behaviour 10). Saves
    /// immediately.</summary>
    [ObservableProperty]
    public partial bool NotifyOnScanComplete { get; set; }

    /// <summary><c>general.survey_downloads_enabled</c> (spec 12.7). On by
    /// default; gates the Sky view window only. Saves immediately.</summary>
    [ObservableProperty]
    public partial bool SurveyDownloadsEnabled { get; set; }

    /// <summary>
    /// Whether the Start with Windows control may be used. False on a build the updater did not
    /// install, exactly as the About tab's update button is (spec 17.1). With no seam bound it is
    /// false, which is the correct shipped behaviour for a <c>dotnet run</c>.
    /// </summary>
    public bool CanSetStartWithWindows { get; }

    /// <summary>Why the Start with Windows control is disabled, or null when it is not. Shown
    /// beside the control, never as a silent grey (spec 12.10).</summary>
    public string? StartWithWindowsUnavailableReason { get; }

    /// <summary>Whether <see cref="StartWithWindowsUnavailableReason"/> is on screen.</summary>
    public bool HasStartWithWindowsUnavailableReason => StartWithWindowsUnavailableReason is not null;

    /// <inheritdoc />
    protected override void ApplyDocument(GeneralSettings general)
    {
        // Inside the base class's Apply, so seeding a checkbox is not mistaken for a user gesture
        // and none of the five handlers below queues a write for it.
        CloseToTray = general.CloseToTray;
        MinimizeToTray = general.MinimizeToTray;
        StartWithWindows = general.StartWithWindows;
        StartMinimized = general.StartMinimized;
        NotifyOnScanComplete = general.NotifyOnScanComplete;
        SurveyDownloadsEnabled = general.SurveyDownloadsEnabled;
    }

    // ---- the immediate saves ------------------------------------------------------------------
    //
    // One ImmediateSave per control, each with its own success message and its own roll-back.
    // Deliberately not batched: the base class's roll-back restores one control, and a batched
    // write would restore a control the user did not touch.

    partial void OnCloseToTrayChanged(bool value)
        => ImmediateSave(
            general => general with { CloseToTray = value },
            value ? "Closing the window hides GalactiLog" : "Closing the window exits GalactiLog",
            () => CloseToTray = !value);

    partial void OnMinimizeToTrayChanged(bool value)
        => ImmediateSave(
            general => general with { MinimizeToTray = value },
            value ? "Minimizing the window hides GalactiLog" : "Minimizing the window minimizes normally",
            () => MinimizeToTray = !value);

    // Spec 12.11 behaviour 8's two halves, written together so they cannot disagree: the shortcut
    // is applied in the preWrite step, which runs before SettingsStore.MutateGeneral is even
    // called and therefore never runs while that method's process-wide write gate is held (Phase
    // 11 Task 3 review, Important 1: MutateGeneral's own contract requires its mutate callback to
    // be pure and fast, and WindowResidencyService calls GetGeneral synchronously on the UI
    // thread on every close and every minimize, so a slow or hung shortcut write must never be
    // able to block that gate). A refused Apply throws before MutateGeneral is reached, so
    // nothing is written to the key when the shortcut could not be changed to match it; the base
    // class's existing roll-back (RunWrite's catch) then reports the reason and restores the
    // checkbox. The rule this encodes, for a refused Apply specifically: the shortcut is the
    // truth and the key is the intent, and the checkbox is left agreeing with the filesystem,
    // never with the key. (The reverse direction, Apply succeeding and the subsequent
    // MutateGeneral call failing, is not covered by that sentence: the checkbox still rolls back
    // there, but the shortcut has already changed, so the two can disagree until Diagnostics
    // behaviour 11 makes the disagreement visible, exactly as ruling Q21 accepts.)
    //
    // Apply is called on whatever seam is bound, and IsSupported is not re-tested here (phase
    // review finding P5, design-lessons rule 2). Apply returns true on a build that cannot carry
    // a shortcut, because nothing was attempted and so nothing failed, which is what lets this
    // call site treat false as the one thing it means: the write was tried and refused. The
    // implementation guards its own members on IsSupported, so an unsupported build still reaches
    // neither Velopack's locator nor the Startup folder, even when a direct assignment bypasses
    // the control's own IsEnabled binding (TRACKING item 13's reasoning applied to a property
    // setter). An absent seam has nothing to call at all. In every one of those cases the key
    // still saves, because it carries the user's intent even where nothing can act on it yet (a
    // dotnet run, or a build the updater did not install).
    //
    // SettingsValidationException is reused deliberately for a refused Apply, which is not a
    // document validation failure: it is the one exception type RunWrite's catch already reports
    // through ErrorMessage without a generic message replacing it, which is what lets this
    // handler show which of the two directions failed. That catch itself logs nothing, so the
    // warning below is this handler's own, not a duplicate of one the base class would have made.
    partial void OnStartWithWindowsChanged(bool value)
        => ImmediateSave(
            preWrite: () =>
            {
                if (_startupShortcut is { } shortcut && !shortcut.Apply(value))
                {
                    var reason = value
                        ? "The Startup shortcut could not be created."
                        : "The Startup shortcut could not be removed.";
                    Logger.LogWarning("Start with Windows change refused: {Reason}", reason);
                    throw new SettingsValidationException(reason);
                }
            },
            mutate: general => general with { StartWithWindows = value },
            successMessage: value ? "GalactiLog starts with Windows" : "GalactiLog does not start with Windows",
            rollBack: () => StartWithWindows = !value);

    partial void OnStartMinimizedChanged(bool value)
        => ImmediateSave(
            general => general with { StartMinimized = value },
            value ? "GalactiLog starts in the notification area" : "GalactiLog starts with its window open",
            () => StartMinimized = !value);

    partial void OnNotifyOnScanCompleteChanged(bool value)
        => ImmediateSave(
            general => general with { NotifyOnScanComplete = value },
            value ? "Scan completion notices enabled" : "Scan completion notices disabled",
            () => NotifyOnScanComplete = !value);

    partial void OnSurveyDownloadsEnabledChanged(bool value)
        => ImmediateSave(
            general => general with { SurveyDownloadsEnabled = value },
            value ? "Survey downloads turned on" : "Survey downloads turned off",
            () => SurveyDownloadsEnabled = !value);
}
