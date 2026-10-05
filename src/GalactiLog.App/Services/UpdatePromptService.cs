using GalactiLog.App.ViewModels.Update;
using GalactiLog.App.Views.Update;

namespace GalactiLog.App.Services;

/// <summary>
/// Opens spec 17.1's update confirmation on <see cref="ModalHost"/>. The fifth user of the one
/// modal host in this application, joining <see cref="MergeDialogService"/>,
/// <see cref="PreviewModalService"/>, <see cref="ResetConfirmDialogService"/> and
/// <see cref="SetupWizardService"/>; a second host would be a second answer to "what is modal in
/// this app" (TRACKING section 6 item 22).
/// </summary>
/// <remarks>
/// <para>
/// The no-owner path is the host's: with no window to show over, <see cref="ModalHost.ShowAsync"/>
/// logs and returns a default, and nothing is applied. A prompt that could not be shown has
/// decided nothing, which is the right answer here as well: the update stays downloaded and the
/// next scan that finishes, or the status bar indicator, offers it again.
/// </para>
/// <para>
/// What differs from the other four wrappers is the null case below. This page is built from the
/// update service's current state, and that state can have moved on between the service asking
/// for a prompt and the dispatcher running it, so <paramref name="create"/> may legitimately have
/// nothing to show.
/// </para>
/// </remarks>
/// <param name="create">Builds the page from the update service's current state, or returns null
/// when there is no downloaded update to confirm.</param>
/// <param name="host">The application's one modal host. It does the logging, so this type takes
/// no logger of its own.</param>
public sealed class UpdatePromptService(Func<UpdatePromptViewModel?> create, ModalHost host)
{
    /// <summary>
    /// Opens the confirmation over the current owner window. Must be called on the UI thread: it
    /// builds a window.
    /// </summary>
    /// <returns>
    /// Whether a window was actually shown. False when there was nothing to confirm and when
    /// there was no owner window to show the dialog over; true when the dialog opened, whichever
    /// answer the user gave it.
    /// </returns>
    /// <remarks>
    /// "Shown", not "confirmed", because that is the question <see cref="UpdateService"/> has to
    /// answer: a user who answered Later has decided and must not be asked again for that
    /// version, while a dialog that never opened has decided nothing (review round 1, M5). The
    /// flag is set inside the factory below, which <see cref="ModalHost.ShowAsync"/> invokes only
    /// after it has found an owner window, so it distinguishes the no-owner path from Later
    /// without either type learning anything about the other.
    /// </remarks>
    public async Task<bool> ShowAsync()
    {
        // Checked before the host is asked, because ModalHost.ShowAsync takes a factory that must
        // return a window. Nothing to confirm is not a dialog that failed to open.
        if (create() is not { } page)
        {
            return false;
        }

        var shown = false;
        await host
            .ShowAsync<bool>(() =>
            {
                shown = true;
                return new UpdatePromptView { DataContext = page };
            })
            .ConfigureAwait(true);

        return shown;
    }
}
