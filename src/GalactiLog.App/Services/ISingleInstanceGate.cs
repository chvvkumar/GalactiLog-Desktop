namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11 behaviours 1 to 3: whether this process is the application, and how a later launch
/// asks the running one to come to the front. The seam exists so <c>GalactiLog.App.Tests</c> can
/// exercise the gate's decisions without creating a process-wide named kernel object that would
/// collide with a real GalactiLog running on the same machine.
/// </summary>
public interface ISingleInstanceGate : IDisposable
{
    /// <summary>
    /// Claims ownership of this application for this process. True when this process is the
    /// first: the caller continues to build the host and show a window. False when another
    /// process already owns it: the caller has already been asked to activate that process
    /// (or deliberately not to), and must exit 0 immediately without building anything.
    /// </summary>
    /// <param name="requestActivation">Whether a losing process should ask the owner to show its
    /// window. False for a launch that carried <c>--minimized</c>.</param>
    /// <returns>True when this process owns the application.</returns>
    bool TryAcquire(bool requestActivation);

    /// <summary>
    /// Starts watching for another launch's activation request. Called once, after the window
    /// exists. <paramref name="onActivationRequested"/> is raised on a background thread; the
    /// caller marshals.
    /// </summary>
    /// <param name="onActivationRequested">Raised once per activation request.</param>
    void StartListening(Action onActivationRequested);

    /// <summary>Stops the watch. Idempotent, and safe when listening never started.</summary>
    void StopListening();
}
