namespace GalactiLog.Core.Metrics;

/// <summary>
/// Spec 7.2's pooling rule: the three eccentricity sources produce numbers that are not
/// comparable, so an aggregate pools only the frames whose <c>eccentricity_source</c> is the
/// modal one and discloses which source that was and how many frames it excluded.
/// </summary>
/// <remarks>
/// One implementation because the rule has to give the same answer in four places: the totals
/// row of spec 12.4, the collapsed session card, the expanded card's ranges and per-filter
/// medians, and Phase 9's library-wide average. Two copies would eventually disagree on the tie
/// break and the same library would report two different averages on two screens.
/// </remarks>
public static class EccentricitySources
{
    /// <summary>
    /// The modal <c>eccentricity_source</c> among the frames that actually carry an
    /// eccentricity. False when no frame carries one, in which case there is nothing to pool and
    /// <paramref name="modalSource"/> is null.
    /// </summary>
    /// <remarks>
    /// Ties break on a null source sorting last and then on the lexicographically smallest
    /// source string, so the answer is deterministic and every screen agrees. A null modal source
    /// is a real outcome, not an absence: frames can carry an eccentricity with no source
    /// recorded and win the vote, which is why this returns a bool rather than using null as the
    /// "nothing to pool" signal.
    /// </remarks>
    public static bool TryGetModalSource(
        IEnumerable<(string? Source, double? Value)> frames,
        out string? modalSource)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nullSourceCount = 0;
        foreach (var (source, value) in frames)
        {
            if (value is null)
            {
                continue;
            }

            if (source is null)
            {
                nullSourceCount++;
            }
            else
            {
                counts[source] = counts.GetValueOrDefault(source) + 1;
            }
        }

        modalSource = null;
        if (counts.Count == 0 && nullSourceCount == 0)
        {
            return false;
        }

        var bestCount = nullSourceCount;
        var bestIsNull = nullSourceCount > 0;
        foreach (var (source, count) in counts)
        {
            // A higher count always wins. On an equal count the tie breaks exactly as the summary
            // remark states: a named source beats the null source, because the null source sorts
            // last, and among named sources the ordinal comparison takes the smaller string,
            // matching TargetDetailQuery's OrderBy chain.
            var wins = count > bestCount
                || (count == bestCount
                    && (bestIsNull || string.CompareOrdinal(source, modalSource) < 0));
            if (!wins)
            {
                continue;
            }

            bestCount = count;
            bestIsNull = false;
            modalSource = source;
        }

        return true;
    }

    /// <summary>True when a frame's eccentricity belongs in an aggregate pooled on
    /// <paramref name="modalSource"/>: it has a value and its source is the modal one.</summary>
    public static bool IsPooled(string? source, double? value, string? modalSource)
        => value is not null && string.Equals(source, modalSource, StringComparison.Ordinal);
}
