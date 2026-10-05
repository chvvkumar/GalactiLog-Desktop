using GalactiLog.Core.Aliases;

namespace GalactiLog.Core.Metrics;

/// <summary>A filter's integration goal and how far short of it the filter is, in seconds. Both
/// null when the filter has no goal.</summary>
public readonly record struct FilterGoal(double? GoalSeconds, double? ShortBySeconds);

/// <summary>A filter's goal is the largest integration among its <see cref="FilterCategory"/>
/// group, SII, Ha and OIII or R, G and B. L, an uncategorised filter, the largest of its group
/// (every one of a tie) and a group of one have no goal.</summary>
public static class IntegrationGoal
{
    public static IReadOnlyDictionary<string, FilterGoal> For(IReadOnlyDictionary<string, double> secondsByFilter)
    {
        var largest = new Dictionary<string, double>();
        foreach (var (filter, seconds) in secondsByFilter)
        {
            if (GroupOf(filter, seconds) is { } group)
            {
                largest[group] = largest.TryGetValue(group, out var max) ? Math.Max(max, seconds) : seconds;
            }
        }

        var goals = new Dictionary<string, FilterGoal>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filter, seconds) in secondsByFilter)
        {
            goals[filter] = GroupOf(filter, seconds) is { } group && largest[group] > seconds
                ? new FilterGoal(largest[group], largest[group] - seconds)
                : new FilterGoal(null, null);
        }

        return goals;
    }

    /// <summary>Null for a filter with no group, and for non-finite or non-positive seconds, which
    /// neither set a goal nor get one.</summary>
    private static string? GroupOf(string filter, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return null;
        }

        return FilterCategory.Of(filter) switch
        {
            "SII" or "Ha" or "OIII" => "narrowband",
            "R" or "G" or "B" => "broadband",
            _ => null,
        };
    }
}
