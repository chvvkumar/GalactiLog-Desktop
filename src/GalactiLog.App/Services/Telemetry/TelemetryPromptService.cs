using GalactiLog.App.ViewModels.Telemetry;
using GalactiLog.App.Views.Telemetry;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>
/// Opens the one-time telemetry question on <see cref="ModalHost"/>, the one modal host in this
/// application, the way <see cref="UpdatePromptService"/> opens the update confirmation. With no
/// owner window the host logs and opens nothing, and nothing is recorded, so the next start asks.
/// </summary>
/// <param name="create">Builds the page.</param>
/// <param name="host">The application's one modal host.</param>
public sealed class TelemetryPromptService(Func<TelemetryPromptViewModel> create, ModalHost host)
{
    /// <summary>Opens the prompt over the current owner window. Must be called on the UI thread.
    /// </summary>
    public Task ShowAsync()
        => host.ShowAsync<bool>(() => new TelemetryPromptView { DataContext = create() });
}
