namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11 behaviour 8's Startup shortcut, as a seam.
/// </summary>
/// <remarks>
/// <para>
/// Declared here by Phase 11 Task 2, which writes <c>general.start_with_windows</c> through the
/// Settings General tab and calls nothing on this interface. Phase 11 Task 3 writes the one
/// implementation, and it is the only type in the application permitted to name the updater's
/// shortcut API or to touch the Startup folder (spec 2.1.1). Nothing else implements this.
/// </para>
/// <para>
/// Synchronous by ruling Q5. Creating or deleting one shortcut is a single shell call on a path
/// the platform resolves for us, so an asynchronous shape would buy a state machine and no
/// responsiveness, and the General tab's control has to report the outcome inline anyway.
/// </para>
/// </remarks>
public interface IStartupShortcut
{
    /// <summary>
    /// Whether this build can carry a Startup shortcut at all. False on a build the updater did
    /// not install, because the shortcut targets an install-root stub that no other build has
    /// (spec 12.11 behaviour 8). The General tab disables its control and shows the reason when
    /// this is false, rather than offering a switch that would do nothing.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Whether the shortcut is on disk: true, false, or null when it could not be determined.
    /// Null is a real answer and not an error, and the Diagnostics page reports the three states
    /// separately (spec 12.11 behaviour 11).
    /// </summary>
    bool? Exists();

    /// <summary>
    /// Creates the shortcut when <paramref name="enabled"/> is true and removes it otherwise.
    /// Returns false only when the change was attempted and failed.
    /// </summary>
    /// <remarks>
    /// A build where <see cref="IsSupported"/> is false has nothing to do and returns true:
    /// "there was nothing to do" is not a failure (phase review finding P5). Returning false for
    /// it made callers re-test <see cref="IsSupported"/> at their own call site to tell "this
    /// build cannot carry a shortcut" apart from "the write failed", which is the duplicated
    /// per-call-site check design-lessons rule 2 exists to prevent. An implementation guards its
    /// own members on <see cref="IsSupported"/>; a caller does not.
    /// </remarks>
    bool Apply(bool enabled);
}
