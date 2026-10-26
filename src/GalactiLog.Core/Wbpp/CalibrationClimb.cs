using GalactiLog.Core.Io;

namespace GalactiLog.Core.Wbpp;

/// <summary>
/// Which folder level a calibration scan starts at and how far it may climb from there.
/// <see cref="SessionLevels.Levels"/> excludes the scan root, so index 0 is the shallowest level
/// and the last one a scan can reach.
/// </summary>
public static class CalibrationClimb
{
    /// <summary>The shallowest level holding no other night's lights; when none does, the chosen
    /// level's containing level (or the chosen level itself when nothing contains it).</summary>
    public static int StartLevel(SessionLevels levels, int chosenIndex)
    {
        for (var i = 0; i < levels.Levels.Count; i++)
        {
            if (levels.Levels[i].OtherNights.Count == 0)
            {
                return i;
            }
        }

        return ParentOf(levels, chosenIndex) ?? chosenIndex;
    }

    /// <summary>True when no level is free of other nights, so the scan began at the fallback.</summary>
    public static bool StartedAtFallback(SessionLevels levels)
        => levels.Levels.All(level => level.OtherNights.Count > 0);

    /// <summary>The level whose path contains the given level's path and is not that path, the
    /// deepest such one; null when none contains it. The level list is ordered depth first then
    /// path, so the entry before an index can be a sibling at a fork, never use index - 1.</summary>
    public static int? ParentOf(SessionLevels levels, int index)
    {
        var path = levels.Levels[index].Path;
        int? best = null;
        for (var i = 0; i < levels.Levels.Count; i++)
        {
            var candidate = levels.Levels[i].Path;
            if (i != index && candidate.Length < path.Length && PathConfinement.IsUnderOrEqual(candidate, path)
                && (best is null || candidate.Length > levels.Levels[best.Value].Path.Length))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>The next level up the automatic climb takes, or null when it stops: at the
    /// shallowest level, or before a folder holding another target's lights.</summary>
    public static int? AutomaticNext(SessionLevels levels, int current)
        => ParentOf(levels, current) is { } parent && levels.Levels[parent].OtherTargets.Count == 0 ? parent : null;

    /// <summary>The next level up a manual climb takes (the containing level), or null at the
    /// shallowest level.</summary>
    public static int? ManualNext(SessionLevels levels, int current)
        => ParentOf(levels, current);
}
