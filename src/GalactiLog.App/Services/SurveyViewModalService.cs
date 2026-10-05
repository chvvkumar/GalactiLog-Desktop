using GalactiLog.App.ViewModels.Preview;
using GalactiLog.App.Views.Preview;
using GalactiLog.Core.Survey;

namespace GalactiLog.App.Services;

/// <summary>
/// Opens spec 12.4's Sky view window on <see cref="ModalHost"/> and disposes its page on close,
/// the <see cref="PreviewModalService"/> shape.
/// </summary>
public sealed class SurveyViewModalService(Func<SurveyTarget, SurveyViewViewModel> create, ModalHost host)
{
    /// <summary>Opens the window over the current owner and completes when it closes. Must be
    /// called on the UI thread.</summary>
    public Task ShowAsync(SurveyTarget target)
    {
        SurveyViewViewModel? page = null;
        return host.ShowAsync<bool>(
            () =>
            {
                page = create(target);
                return new SurveyViewWindow { DataContext = page };
            },
            () => page?.Dispose());
    }
}
