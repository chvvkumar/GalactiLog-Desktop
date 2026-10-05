using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.App.Views.TargetDetail.Wbpp;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.13's Export for stacking wizard, opened on <see cref="ModalHost"/>. The shape
/// <see cref="FrameListDialogService"/> already has, and for the same reason: everything about
/// being a modal belongs to the host, and what is left here is this wizard and nothing else. No
/// second modal host (TRACKING item 22).
/// </summary>
public sealed class WbppExportDialogService
{
    private readonly Func<string, string, IReadOnlyList<DateOnly>, WbppExportWizardViewModel> _create;
    private readonly ModalHost _host;

    /// <param name="create">Builds the wizard for one group key, one target name and one checked
    /// night set.</param>
    /// <param name="host">The application's one modal host.</param>
    public WbppExportDialogService(
        Func<string, string, IReadOnlyList<DateOnly>, WbppExportWizardViewModel> create,
        ModalHost host)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(host);

        _create = create;
        _host = host;
    }

    /// <summary>Opens the modal over the checked nights and completes with whether anything was
    /// written. Must be called on the UI thread: it builds a window.</summary>
    /// <param name="groupKey">The page's group key, which keys the path read.</param>
    /// <param name="targetName">The target's primary name, which the header names.</param>
    /// <param name="nights">The checked nights in the ledger's own order.</param>
    public async Task<bool> ShowAsync(
        string groupKey,
        string targetName,
        IReadOnlyList<DateOnly> nights)
    {
        ArgumentNullException.ThrowIfNull(nights);

        if (nights.Count == 0)
        {
            // Spec 12.13: the entry is disabled while nothing is checked. A wizard over no night
            // would have no folder to copy and nothing to generate.
            return false;
        }

        WbppExportWizardViewModel? wizard = null;
        return await _host.ShowAsync<bool>(
            () =>
            {
                wizard = _create(groupKey, targetName, nights);
                return new WbppExportWindow { DataContext = wizard };
            },
            () => wizard?.Dispose()).ConfigureAwait(true);
    }
}
