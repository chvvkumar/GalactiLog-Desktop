namespace GalactiLog.Core.Integrations;

/// <summary>What a NINA send did about the rotation call (design-spec 12.16, steps 4 to 6).</summary>
public enum NinaRotation
{
    /// <summary>The target carried no position angle, so no rotation call was made and the result
    /// says nothing about it.</summary>
    None,

    /// <summary>The rotation call was made and succeeded.</summary>
    Sent,

    /// <summary>The rotation call was made and failed, which does not fail the action: the framing
    /// coordinates did land. The caller logs the failure at <c>Warning</c>.</summary>
    Failed,
}

/// <summary>Which of the three routes of design-spec 12.16 pointed Stellarium at the
/// target.</summary>
public enum StellariumFocus
{
    /// <summary>Focused by the catalogue prefix of the target name.</summary>
    CatalogName,

    /// <summary>Focused by the whole trimmed target name.</summary>
    FullName,

    /// <summary>Focus by name did not run or did not succeed, and the coordinate script was
    /// used.</summary>
    Coordinates,
}

/// <summary>The outcome of <see cref="NinaClient.SendCoordinatesAsync"/> (design-spec
/// 12.16).</summary>
/// <param name="Ok">True when the framing coordinates landed, whatever the rotation call
/// did.</param>
/// <param name="Rotation">What the action did about the rotation call.</param>
/// <param name="FailureMessage">One of <see cref="IntegrationMessages"/>' sentences, or null when
/// the action succeeded. It stays null on a success whose rotation failed, where
/// <paramref name="Rotation"/> carries that fact instead. It is never an exception's own
/// text.</param>
/// <param name="Failure">The exception the action last hit, or null when the failure was a
/// non-success status with no exception. It exists so the caller can log it and is never
/// rendered.</param>
public sealed record NinaSendResult(
    bool Ok, NinaRotation Rotation, string? FailureMessage, Exception? Failure);

/// <summary>The outcome of <see cref="StellariumClient.SlewAsync"/> (design-spec 12.16).</summary>
/// <param name="Ok">True when the slew and the field of view call both landed.</param>
/// <param name="Focus">Which route pointed Stellarium at the target; meaningless when
/// <paramref name="Ok"/> is false, because no route completed.</param>
/// <param name="FailureMessage">One of <see cref="IntegrationMessages"/>' sentences, or null when
/// the action succeeded. Never an exception's own text.</param>
/// <param name="Failure">The exception the action last hit, or null when the failure was a
/// non-success status with no exception. For the caller's log only.</param>
public sealed record StellariumSlewResult(
    bool Ok, StellariumFocus Focus, string? FailureMessage, Exception? Failure);
