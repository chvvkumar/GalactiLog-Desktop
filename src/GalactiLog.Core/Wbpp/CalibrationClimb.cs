namespace GalactiLog.Core.Wbpp;

/// <summary>
/// Which folder level a calibration scan starts at and how far it may climb from there.
/// <see cref="SessionLevels.Levels"/> excludes the scan root, so index 0 is the shallowest level
/// and the last one a scan can reach.
/// </summary>
public static class CalibrationClimb
{
    /// <summary>The shallowest level holding no other night's lights; when none does, the chosen
    /// level's parent (or level 0 when the chosen level is the shallowest).</summary>
    public static int StartLevel(SessionLevels levels, int chosenIndex)
    {
        for (var i = 0; i < levels.Levels.Count; i++)
        {
            if (levels.Levels[i].OtherNights.Count == 0)
            {
                return i;
            }
        }

        return Math.Max(chosenIndex - 1, 0);
    }

    /// <summary>True when no level is free of other nights, so the scan began at the fallback.</summary>
    public static bool StartedAtFallback(SessionLevels levels)
        => levels.Levels.All(level => level.OtherNights.Count > 0);

    /// <summary>The next level up the automatic climb takes, or null when it stops: at the
    /// shallowest level, or before a folder holding another target's lights.</summary>
    public static int? AutomaticNext(SessionLevels levels, int current)
        => current - 1 >= 0 && levels.Levels[current - 1].OtherTargets.Count == 0 ? current - 1 : null;

    /// <summary>The next level up a manual climb takes, or null at the shallowest level.</summary>
    public static int? ManualNext(SessionLevels levels, int current)
        => current - 1 >= 0 ? current - 1 : null;
}
