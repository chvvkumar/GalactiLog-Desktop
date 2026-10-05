using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.Settings;

namespace GalactiLog.App.ViewModels.Update;

/// <summary>
/// Design-spec 17.1's confirmation before an update is applied: "nothing is applied silently".
/// The new version, the running version, the channel, the release notes, and two answers.
/// </summary>
/// <remarks>
/// <para>
/// Shown on <c>ModalHost</c> through <c>UpdatePromptService</c>, which is the fifth user of the
/// one modal host in this application (TRACKING section 6 item 22).
/// </para>
/// <para>
/// Install and restart hands off to <c>UpdateService.ApplyAndRestart</c>, which re-reads the
/// running scan in its own body: the process does not survive a successful apply, so the close
/// below is what happens when the apply was refused or failed.
/// </para>
/// </remarks>
public sealed partial class UpdatePromptViewModel : ObservableObject, IModalPageViewModel
{
    /// <summary>The dialog's title, named so the window and its test cannot drift apart.
    /// </summary>
    public const string Title = "Update available";

    /// <summary>The affirmative answer.</summary>
    public const string InstallLabel = "Install and restart";

    /// <summary>The other answer. A window that opened records the version as prompted, whichever
    /// answer it was given, so a scan finishing does not ask again. The recording is
    /// <c>UpdateService.FinishPrompt(version, shown)</c>, which turns on whether the window was
    /// shown and not on the answer (phase review minor P11).</summary>
    public const string LaterLabel = "Later";

    private readonly Action _installAndRestart;

    /// <param name="newVersion">The version the feed offered.</param>
    /// <param name="currentVersion">The version now running.</param>
    /// <param name="channel">The channel this build was installed from (spec 17.4).</param>
    /// <param name="releaseNotes">What the feed carried, or null for
    /// <see cref="AboutTabViewModel.NoReleaseNotesMessage"/>.</param>
    /// <param name="installAndRestart">Normally <c>UpdateService.ApplyAndRestart</c>. A delegate,
    /// not the service, so this page builds in a unit test with a counting lambda and the two
    /// types do not reference each other.</param>
    public UpdatePromptViewModel(
        string newVersion,
        string currentVersion,
        string channel,
        string? releaseNotes,
        Action installAndRestart)
    {
        NewVersion = newVersion;
        CurrentVersion = currentVersion;
        Channel = channel;
        ReleaseNotes = releaseNotes is { Length: > 0 } notes
            ? notes
            : AboutTabViewModel.NoReleaseNotesMessage;
        _installAndRestart = installAndRestart;
    }

    /// <summary>The version the feed offered.</summary>
    public string NewVersion { get; }

    /// <summary>The version now running.</summary>
    public string CurrentVersion { get; }

    /// <summary>The channel this build was installed from.</summary>
    public string Channel { get; }

    /// <summary>The notes to show, already through the empty case.</summary>
    public string ReleaseNotes { get; }

    /// <summary>What the dialog says it is about to do.</summary>
    public string Summary =>
        $"Version {NewVersion} is ready to install on the {Channel} channel. "
        + $"This build is {CurrentVersion}. Installing restarts GalactiLog.";

    /// <summary>Raised with true when the user confirmed the install, false when they deferred.
    /// The window answers it, the split <c>MergeDialogViewModel.CloseRequested</c> established.
    /// </summary>
    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void InstallAndRestart()
    {
        // The apply does not return on the success path, so the close below runs only when the
        // update was refused (a scan started between the prompt opening and this press) or the
        // apply itself failed. Either way the dialog has nothing left to show.
        _installAndRestart();
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Later() => CloseRequested?.Invoke(this, false);
}
