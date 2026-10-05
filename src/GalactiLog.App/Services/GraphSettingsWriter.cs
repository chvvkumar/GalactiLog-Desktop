using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.Services;

/// <summary>
/// The one writer of <c>user_settings.graph</c> (spec 5.8.3). Same contract as the dashboard's
/// column chain in <c>TargetListViewModel</c>: the mutation is captured on the calling thread, the
/// write runs off it, writes are chained so two toggles cannot interleave into a lost update, and
/// a failure is logged and dropped because the toggle has already changed what is on screen.
/// <para>
/// Ruling Q18: a small concrete type, not a generic settings-document writer.
/// <c>SettingsStore</c>'s getters and setters are per-document methods rather than a keyed API, so
/// a generic version would need a getter/setter pair passed in anyway, which is what this
/// constructor already takes.
/// </para>
/// </summary>
/// <param name="getGraph">Normally <c>SettingsStore.GetGraph</c>. Called inside the queued write,
/// never on the calling thread, so the load-modify-save reads the document as it is at write
/// time.</param>
/// <param name="saveGraph">Normally <c>SettingsStore.SaveGraph</c>, which already round-trips
/// <c>GraphSettings.ExtensionData</c>, so an unrecognized key is preserved on write and never
/// dropped (spec 5.8).</param>
public sealed class GraphSettingsWriter(
    Func<GraphSettings> getGraph,
    Action<GraphSettings> saveGraph,
    ILogger? logger = null)
{
    private readonly Lock _gate = new();
    private Task _persist = Task.CompletedTask;

    /// <summary>The tail of the write chain. Tests await it; nothing in the application does,
    /// because a toggle must not wait on SQLite.</summary>
    public Task Pending
    {
        get { lock (_gate) { return _persist; } }
    }

    /// <summary>
    /// Queues a load-modify-save. The mutation is applied to the document as loaded inside the
    /// queued write rather than to a snapshot taken now, which is what keeps a metric toggle from
    /// clobbering a concurrent expanded-state flag.
    /// </summary>
    /// <param name="mutate">Applied to the document as loaded inside the queued write.</param>
    /// <param name="onFailure">Optional. Invoked on the write chain's thread when the write threw,
    /// so a caller that owes the user a roll-back can perform one. Phase 9 Task 6 review minor 1:
    /// the chart toggles are genuinely fire and forget, because the toggle has already changed
    /// what is on screen and the next one tries again, but the Settings Display tab's default
    /// chart scope is a persisted preference whose control must not stay changed after a refused
    /// save. Null keeps the original behaviour, which is what every chart toggle passes.</param>
    public void Write(Func<GraphSettings, GraphSettings> mutate, Action<Exception>? onFailure = null)
    {
        lock (_gate)
        {
            _persist = _persist.ContinueWith(_ => Run(mutate, onFailure), TaskScheduler.Default);
        }
    }

    private void Run(Func<GraphSettings, GraphSettings> mutate, Action<Exception>? onFailure)
    {
        try
        {
            saveGraph(mutate(getGraph()));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "Persisting user_settings.graph failed; the chart selection on screen is unchanged");

            // Reported after the log, never instead of it, and never allowed to take the chain
            // down with it: the next write must still run.
            try
            {
                onFailure?.Invoke(ex);
            }
            catch (Exception callbackFailure)
            {
                logger?.LogWarning(callbackFailure, "A user_settings.graph failure callback threw");
            }
        }
    }
}
