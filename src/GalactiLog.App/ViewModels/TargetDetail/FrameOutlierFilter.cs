namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// Which per-frame outlier flag the frame table is filtered to, one at a time. The frames strip's
/// outlier pills set it (P24 R22), and a pill's count and the rows on screen are the same flagged
/// set by construction (P12 R3).
/// </summary>
public enum FrameOutlierFilter
{
    /// <summary>Every row. The default, and what a fresh table and a cleared filter both hold.
    /// </summary>
    None,

    /// <summary>Only rows whose <c>IsHfrOutlier</c> is set.</summary>
    Hfr,

    /// <summary>Only rows whose <c>IsEccentricityOutlier</c> is set.</summary>
    Eccentricity,

    /// <summary>Only rows whose <c>IsFwhmOutlier</c> is set.</summary>
    Fwhm,

    /// <summary>Only rows whose <c>IsStarsOutlier</c> is set.</summary>
    Stars,

    /// <summary>Only rows whose <c>IsGuidingRmsOutlier</c> is set.</summary>
    GuidingRms,
}
