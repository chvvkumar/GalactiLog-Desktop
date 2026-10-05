using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Design-spec 12.7's About tab: version, git SHA, release channel, an update check button,
/// release notes and a log folder link. Six fields, no more.
/// </summary>
/// <remarks>
/// <para>
/// Every value comes from <see cref="BuildInfo"/> or <see cref="UpdateService"/>; this tab reads
/// no assembly attribute and asks the updater nothing of its own, so it cannot disagree with the
/// Diagnostics Versions group or the support bundle about which build is running.
/// </para>
/// <para>
/// The status line under the button is the button's own feedback, not a seventh field: a check
/// with no visible result is unusable. The view labels it as such.
/// </para>
/// <para>
/// Nothing here reaches the network in its constructor, and nothing here composes a path or
/// starts a process: <see cref="ShellIntegration"/> is the only launcher in this application.
/// </para>
/// </remarks>
public sealed partial class AboutTabViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.7's field labels, named so the view and its test cannot drift apart.
    /// </summary>
    public const string VersionLabel = "Version";

    /// <inheritdoc cref="VersionLabel" />
    public const string GitShaLabel = "Git SHA";

    /// <inheritdoc cref="VersionLabel" />
    public const string ChannelLabel = "Release channel";

    /// <inheritdoc cref="VersionLabel" />
    public const string ReleaseNotesLabel = "Release notes";

    /// <inheritdoc cref="VersionLabel" />
    public const string CheckForUpdatesLabel = "Check for updates";

    /// <inheritdoc cref="VersionLabel" />
    public const string LogFolderLabel = "Open log folder";

    /// <summary>Ruling Q19's releases link, which opens in the default browser. Not an embedded
    /// browser (spec 19.2): the same mechanism the frame actions already use.</summary>
    public const string ReleasesLabel = "View releases on GitHub";

    /// <summary>Ruling Q19 step 3, when a check has run and there are no notes and the tag name
    /// is unknown.</summary>
    public const string NoReleaseNotesMessage = "No release notes available.";

    /// <summary>Why the update check button is disabled on a build the updater did not install.
    /// The button is present and disabled with the reason on screen, because an absent button
    /// reads as a missing feature.</summary>
    public const string NotInstalledMessage = "This build was not installed by the updater.";

    /// <summary>The status line before any check has run.</summary>
    public const string NotCheckedMessage = "No update check has run yet.";

    private readonly BuildInfo _buildInfo;
    private readonly UpdateService? _updates;
    private readonly ShellIntegration _shell;
    private readonly string _logDirectory;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <param name="buildInfo">The running build. The one reader of build identity.</param>
    /// <param name="updates">Spec 17.1's update service, or null on a surface that has none,
    /// which leaves the check button present and disabled.</param>
    /// <param name="shell">The only process launcher in this application.</param>
    /// <param name="logDirectory">The rolling log directory, composed by <c>AppHost</c> through
    /// the authorized app data root. This tab never composes a path of its own.</param>
    /// <param name="post">How to reach the UI thread. Used for the command's own completion,
    /// which resumes off the dispatcher; <see cref="UpdateService.StateChanged"/> arrives already
    /// marshalled and is not posted again.</param>
    /// <param name="logger">Optional. A failed check is logged, never rethrown.</param>
    public AboutTabViewModel(
        BuildInfo buildInfo,
        UpdateService? updates,
        ShellIntegration shell,
        string logDirectory,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _buildInfo = buildInfo;
        _updates = updates;
        _shell = shell;
        _logDirectory = logDirectory;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        // Assigned before anything can observe them. No query, no assembly scan and no network
        // call: the four values are already known.
        ReleaseNotes = ComposeReleaseNotes(updates?.State);
        UpdateStatus = ComposeStatus(updates?.State);

        if (_updates is not null)
        {
            _updates.StateChanged += OnUpdateStateChanged;
        }
    }

    /// <summary>Spec 12.7's "Version".</summary>
    public string Version => _buildInfo.Version;

    /// <summary>Spec 12.7's "git SHA".</summary>
    public string GitSha => _buildInfo.GitSha;

    /// <summary>Spec 12.7's "release channel".</summary>
    public string Channel => _buildInfo.Channel;

    /// <summary>Ruling Q19's releases page, opened in the default browser. The feed URL has one
    /// definition, in the production update checker.</summary>
    public static string ReleasesUrl => VelopackUpdateChecker.RepositoryUrl + "/releases";

    /// <summary>
    /// Spec 12.7's "release notes", through ruling Q19's three steps: the notes the last check
    /// carried, then the installed version's tag name, then
    /// <see cref="NoReleaseNotesMessage"/>.
    /// </summary>
    [ObservableProperty]
    public partial string ReleaseNotes { get; private set; }

    /// <summary>The update check button's own feedback. Not a spec 12.7 field.</summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; private set; }

    /// <summary>
    /// Spec 12.7's update check button. Runs one check now, off the UI thread, outside the six
    /// hour loop's schedule.
    /// </summary>
    /// <remarks>
    /// No <see cref="CancellationToken"/> parameter (TRACKING section 6 item 13): a command built
    /// from a token-taking delegate cancels the in-flight token on a second press, which would
    /// abort the check rather than refuse the press.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        // CanExecute is the affordance; this is the guard. A direct Execute arrives here too.
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
            // The service reports its own failures through the state; this catch exists so a
            // faulted command task cannot be rethrown onto whichever synchronization context the
            // press happened to arrive on.
            _logger.LogWarning(ex, "The update check could not be started");
        }
        finally
        {
            // The await above resumes off the dispatcher, and this touches command state the
            // view is bound to.
            _post(() =>
            {
                if (!_disposed)
                {
                    CheckForUpdatesCommand.NotifyCanExecuteChanged();
                }
            });
        }
    }

    // Ruling Q4: the three clauses that used to be spelled here are one property on the update
    // service now, so this button and spec 12.11's tray menu item cannot drift apart about when a
    // check may be started. The build-installed half moved with them, because the service holds
    // the same BuildInfo this tab does.
    private bool CanCheckForUpdates() => !_disposed && _updates is { CanCheckNow: true };

    /// <summary>
    /// Spec 12.7's log folder link. Ruling Q28: the newest log file is revealed, so the folder
    /// opens with that file selected; with no file yet, the directory is opened.
    /// </summary>
    /// <remarks>
    /// The same decision spec 12.8's log viewer makes, and both surfaces reach the shell through
    /// <see cref="ShellIntegration"/> and enumerate through <see cref="LogFileSet"/>. Neither
    /// composes a path. The enumeration is a directory listing of at most fifteen names, which is
    /// why this action reads on the calling thread rather than through a <c>Task.Run</c>.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUseTheShell))]
    private void OpenLogFolder()
    {
        if (_disposed || !ShellIntegration.IsWindowsShellAvailable)
        {
            return;
        }

        var files = LogFileSet.Newest(_logDirectory);
        if (files.Count > 0)
        {
            _shell.RevealInExplorer(files[0]);
        }
        else
        {
            _shell.OpenWithDefaultApplication(_logDirectory);
        }
    }

    /// <summary>Ruling Q19's releases link. The default browser, never an embedded one.</summary>
    [RelayCommand]
    private void OpenReleases()
    {
        if (_disposed)
        {
            return;
        }

        _shell.OpenWithDefaultApplication(ReleasesUrl);
    }

    private bool CanUseTheShell() => !_disposed && ShellIntegration.IsWindowsShellAvailable;

    /// <summary>Detaches the update subscription. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_updates is not null)
        {
            _updates.StateChanged -= OnUpdateStateChanged;
        }
    }

    // Already on the UI thread by the time this runs: UpdateService publishes through its own
    // dispatcher seam. Do not post again.
    private void OnUpdateStateChanged(object? sender, UpdateState state)
    {
        if (_disposed)
        {
            return;
        }

        ReleaseNotes = ComposeReleaseNotes(state);
        UpdateStatus = ComposeStatus(state);
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }

    private string ComposeReleaseNotes(UpdateState? state)
    {
        // Ruling Q19, in order of preference at render time.
        if (state?.ReleaseNotes is { Length: > 0 } notes)
        {
            return notes;
        }

        return TagName(_buildInfo.Version) ?? NoReleaseNotesMessage;
    }

    private string ComposeStatus(UpdateState? state)
    {
        if (state is null || !_buildInfo.IsInstalled)
        {
            return NotInstalledMessage;
        }

        return state.Phase switch
        {
            UpdatePhase.Checking => "Checking for updates...",
            UpdatePhase.Available => $"Update {state.AvailableVersion} is available.",
            UpdatePhase.Downloading =>
                $"Downloading update {state.AvailableVersion}: {state.DownloadPercent:0}%",
            UpdatePhase.ReadyToApply => $"Update {state.AvailableVersion} is ready to install.",
            UpdatePhase.Failed => $"The update check failed: {state.LastError}",
            _ => state.LastCheckedUtc is null
                ? NotCheckedMessage
                : "No update available. This build is up to date.",
        };
    }

    /// <summary>
    /// Ruling Q19 step 2: the installed version's tag name, which is the release name
    /// (spec 17.5). Null when the version is not known, which sends the caller to step 3.
    /// </summary>
    /// <remarks>
    /// A four part assembly version whose revision is zero is written as its three part tag, so
    /// <c>1.2.3.0</c> reads as <c>v1.2.3</c>, which is what the release is actually called.
    /// </remarks>
    internal static string? TagName(string version)
    {
        if (version.Length == 0 || version == DiagnosticsService.Unknown)
        {
            return null;
        }

        var parts = version.Split('.');
        var trimmed = parts.Length == 4 && parts[3] == "0"
            ? string.Join('.', parts[..3])
            : version;

        return "v" + trimmed;
    }
}
