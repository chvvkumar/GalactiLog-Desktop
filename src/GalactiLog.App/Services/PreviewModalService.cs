using GalactiLog.App.ViewModels.Preview;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.Preview;

namespace GalactiLog.App.Services;

/// <summary>
/// Opens spec 11.5's preview modal on <see cref="ModalHost"/>. It exists for the reason
/// <see cref="MergeDialogService"/> does: something has to own "build this window with this page
/// and dispose the page afterwards", and neither the host nor the page may know about the other.
/// </summary>
/// <param name="create">Builds the page from the originating frame list and the clicked index.
/// </param>
/// <param name="host">The application's one modal host. It does the logging, so this type takes no
/// logger of its own.</param>
public sealed class PreviewModalService(
    Func<IReadOnlyList<FrameRowViewModel>, int, PreviewModalViewModel> create,
    ModalHost host)
{
    /// <summary>Opens the modal over the current owner window and completes when it closes. Must
    /// be called on the UI thread: it builds a window. The modal answers nothing, so the host's
    /// result is discarded; what matters is that the page is disposed, which releases both
    /// bitmaps and withdraws anything still rendering.</summary>
    public Task ShowAsync(IReadOnlyList<FrameRowViewModel> frames, int index)
    {
        PreviewModalViewModel? page = null;
        return host.ShowAsync<bool>(
            () =>
            {
                page = create(frames, index);
                return new PreviewModalWindow { DataContext = page };
            },
            () => page?.Dispose());
    }
}
