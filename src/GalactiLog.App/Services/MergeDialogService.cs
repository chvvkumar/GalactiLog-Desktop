using GalactiLog.App.ViewModels.Merge;
using GalactiLog.App.Views.Merge;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.9's merge preview, opened on <see cref="ModalHost"/>. Everything about being a modal
/// (finding the owner, the no-owner no-op, the failure log, disposing the page) moved to the host
/// at the second occurrence of the pattern (Phase 8 Task 7, design-lessons rule 1); what is left
/// here is the merge dialog and nothing else.
/// </summary>
public sealed class MergeDialogService
{
    private readonly Func<MergeRequest, MergeDialogViewModel> _create;
    private readonly ModalHost _host;

    /// <param name="create">Builds the page for one merge request.</param>
    /// <param name="host">The application's one modal host.</param>
    public MergeDialogService(Func<MergeRequest, MergeDialogViewModel> create, ModalHost host)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(host);

        _create = create;
        _host = host;
    }

    /// <summary>Opens the modal and completes with spec 12.9's count sentence when a merge was
    /// performed, or null when none was. Must be called on the UI thread: it builds a window. A
    /// merge with no window to confirm it in has not been confirmed, which is
    /// <see cref="ModalHost"/>'s <c>default</c> for <see cref="bool"/>.</summary>
    /// <remarks>Phase 7 FIXER item 17: the page sets <c>ConfirmSummary</c> and closes in the next
    /// statement, so the counts were never on screen. They leave with the result instead, and the
    /// surface that opened the dialog shows them. Read in the cleanup, before the page is
    /// disposed, because <c>Dispose</c> clears the summary.</remarks>
    public async Task<string?> ShowAsync(MergeRequest request)
    {
        MergeDialogViewModel? page = null;
        string? summary = null;
        var merged = await _host.ShowAsync<bool>(
            () =>
            {
                page = _create(request);
                return new MergeDialogWindow { DataContext = page };
            },
            () =>
            {
                summary = page?.ConfirmSummary;
                page?.Dispose();
            }).ConfigureAwait(true);

        return merged ? summary : null;
    }
}
