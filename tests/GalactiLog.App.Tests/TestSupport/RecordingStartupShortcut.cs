using GalactiLog.App.Services;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The Startup shortcut as a recording stub. The one <see cref="IStartupShortcut"/>
/// implementation any test binds, in the shape <see cref="RecordingUpdateChecker"/> established:
/// no test run can create, modify or remove a real Startup shortcut (HANDOFF section 5.2 note 9).
/// </summary>
internal sealed class RecordingStartupShortcut : IStartupShortcut
{
    /// <summary>Whether the control may be used. True by default, which is the installed-build
    /// case every case that cares about the seam wants.</summary>
    public bool IsSupported { get; set; } = true;

    /// <summary>What <see cref="Exists"/> reports. Null, the default, is what an unsupported
    /// build reports; the seam does not enforce that pairing on its own, so a case that wants an
    /// inconsistent double sets both explicitly.</summary>
    public bool? Present { get; set; }

    /// <summary>What <see cref="Apply"/> returns on a supported build, until a case changes it to
    /// exercise a refusal. An unsupported build returns true whatever this says, which is the
    /// interface's own contract: nothing was attempted, so nothing failed (phase review finding
    /// P5).</summary>
    public bool Result { get; set; } = true;

    /// <summary>Every value <see cref="Apply"/> was called with, in order.</summary>
    public List<bool> Applied { get; } = [];

    /// <summary>How many times <see cref="Exists"/> was called.</summary>
    public int ExistsCalls { get; private set; }

    /// <summary>Runs inside <see cref="Apply"/>, before the call is recorded, in the shape
    /// <c>RecordingUpdateChecker.OnApply</c> already uses: a case can observe what else was true
    /// at the moment <see cref="Apply"/> ran, such as whether a settings write was in flight
    /// (Phase 11 Task 3 review, Important 1).</summary>
    public Action? OnApply { get; set; }

    public bool? Exists()
    {
        ExistsCalls++;
        return Present;
    }

    public bool Apply(bool enabled)
    {
        OnApply?.Invoke();
        Applied.Add(enabled);

        // The call is still recorded on an unsupported build, because callers no longer filter it
        // out: the seam does. What an unsupported build may not do is report a failure.
        return !IsSupported || Result;
    }
}
