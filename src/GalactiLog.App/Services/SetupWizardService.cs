using GalactiLog.App.ViewModels.Setup;
using GalactiLog.App.Views.Setup;

namespace GalactiLog.App.Services;

/// <summary>
/// Design-spec 12.1's setup wizard, opened on <see cref="ModalHost"/>. Two entry points, one
/// service: the first-run branch in <c>App.OnFrameworkInitializationCompleted</c> and the Settings
/// Library tab's "Run setup again" link.
/// </summary>
/// <remarks>
/// Shaped exactly like <see cref="MergeDialogService"/> and <c>PreviewModalService</c>: build the
/// window and its page in the create delegate, dispose the page in the cleanup, and let the host
/// own everything about being a modal (finding the owner, the no-owner no-op, the failure log).
/// This is the third user of the one modal host and writes no second one (TRACKING.md section 6
/// item 22).
/// </remarks>
public sealed class SetupWizardService
{
    private readonly Func<SetupWizardViewModel> _create;
    private readonly ModalHost _host;

    /// <param name="create">Builds a wizard page. Called once per opening, so a second run reads
    /// the stored document again rather than showing the first run's fields.</param>
    /// <param name="host">The application's one modal host.</param>
    public SetupWizardService(Func<SetupWizardViewModel> create, ModalHost host)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(host);

        _create = create;
        _host = host;
    }

    /// <summary>
    /// Opens the wizard and completes with whether the user finished it (Finish or Skip setup).
    /// Must be called on the UI thread: it builds a window.
    /// </summary>
    /// <remarks>A wizard with no owner window to open in has decided nothing, which is
    /// <see cref="ModalHost"/>'s <c>default</c> for <see cref="bool"/>. The first-run branch is
    /// therefore reached only after <c>desktop.MainWindow</c> exists and has been shown.</remarks>
    public async Task<bool> ShowAsync()
    {
        SetupWizardViewModel? page = null;

        return await _host.ShowAsync<bool>(
            () =>
            {
                page = _create();
                return new SetupWizardWindow { DataContext = page };
            },
            () => page?.Dispose()).ConfigureAwait(true);
    }
}
