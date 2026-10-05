using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;

namespace GalactiLog.App.Services;

/// <summary>
/// Opens spec 12.7's typed confirmation for reset database on <see cref="ModalHost"/>. It exists
/// for the reason <see cref="MergeDialogService"/> and <see cref="PreviewModalService"/> do:
/// something has to own "build this window with this page and read what it finished with", and
/// neither the host nor the page may know about the other.
/// </summary>
/// <remarks>
/// <para>
/// The third of these wrappers, and the review asked whether the three share a shape worth
/// extracting. They do not share more than is already extracted. <see cref="ModalHost.ShowAsync"/>
/// is that shape, pulled out at the second occurrence exactly as design-lessons rule 1 prescribes:
/// find the owner window, no-op with no owner, log a failure, run the cleanup on every path. What
/// is left in each wrapper is the part that genuinely differs, and it differs three ways.
/// <see cref="PreviewModalService"/> discards the dialog's result and disposes its page;
/// <see cref="MergeDialogService"/> reads a string off its page <b>before</b> disposing it, because
/// that page's <c>Dispose</c> clears the value; and this one reads two values off a page that is
/// not <see cref="IDisposable"/> at all. A shared helper would have to take a result type, a read
/// callback and a disposal policy, which is the cleanup delegate <see cref="ModalHost"/> already
/// takes, spelled longer.
/// </para>
/// <para>
/// The reset runs inside the dialog, not here and not on the tab: that is what lets
/// <c>ResetConfirmWindow</c> refuse to close while the delete is in flight.
/// </para>
/// </remarks>
/// <param name="create">Builds the page, which carries the bound <c>DatabaseReset.Run</c>.</param>
/// <param name="host">The application's one modal host. It does the logging, so this type takes no
/// logger of its own.</param>
public sealed class ResetConfirmDialogService(Func<ResetConfirmViewModel> create, ModalHost host)
{
    /// <summary>
    /// Opens the modal over the current owner window and completes with what the dialog produced.
    /// Must be called on the UI thread: it builds a window.
    /// </summary>
    /// <returns>
    /// <c>Summary</c> is whatever line the dialog finished with, and <c>Failed</c> says whether
    /// that line is spec 12.10's failure line. <c>Summary</c> is null only for a genuine cancel:
    /// the user closed the dialog without confirming, or there was no owner window to show it
    /// over. A reset the resolution lease refused and a reset that threw both come back with their
    /// own line, so the caller never reports them as a cancel (review finding I4).
    /// </returns>
    public async Task<(string? Summary, bool Failed)> ShowAsync()
    {
        ResetConfirmViewModel? page = null;
        string? summary = null;
        var failed = false;

        await host.ShowAsync<bool>(
            () =>
            {
                page = create();
                return new ResetConfirmWindow { DataContext = page };
            },
            () =>
            {
                // Read in the cleanup, which ModalHost runs whether the dialog closed, threw, or
                // was never opened. The page holds nothing to dispose.
                summary = page?.Summary;
                failed = page?.Failed ?? false;
            }).ConfigureAwait(true);

        return (summary, failed);
    }
}
