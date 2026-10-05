using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.4's Copy Frame List dialog, opened on <see cref="ModalHost"/>. The shape
/// <see cref="MergeDialogService"/> already has, and for the same reason: everything about being a
/// modal belongs to the host, and what is left here is this dialog and nothing else. No second
/// modal host (TRACKING item 22).
/// </summary>
public sealed class FrameListDialogService
{
    private readonly Func<string, IReadOnlyList<FrameListNight>, FrameListDialogViewModel> _create;
    private readonly ModalHost _host;

    /// <param name="create">Builds the page for one group key and one checked-night set.</param>
    /// <param name="host">The application's one modal host.</param>
    public FrameListDialogService(
        Func<string, IReadOnlyList<FrameListNight>, FrameListDialogViewModel> create,
        ModalHost host)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(host);

        _create = create;
        _host = host;
    }

    /// <summary>Opens the modal over the checked nights and completes with whether a list was
    /// copied. Must be called on the UI thread: it builds a window.</summary>
    /// <param name="groupKey">The page's group key, which keys the per-night query.</param>
    /// <param name="nights">The checked nights in the ledger's own order, each carrying the page's
    /// already-loaded detail or null.</param>
    public async Task<bool> ShowAsync(string groupKey, IReadOnlyList<FrameListNight> nights)
    {
        ArgumentNullException.ThrowIfNull(nights);

        if (nights.Count == 0)
        {
            // Spec 12.4: the command is disabled while nothing is checked. A dialog over no night
            // would have nothing to list and nothing to copy.
            return false;
        }

        FrameListDialogViewModel? page = null;
        return await _host.ShowAsync<bool>(
            () =>
            {
                page = _create(groupKey, nights);
                return new FrameListDialogWindow { DataContext = page };
            },
            () => page?.Dispose()).ConfigureAwait(true);
    }
}
