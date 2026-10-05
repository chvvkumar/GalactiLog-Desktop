using GalactiLog.Core.Metrics;
using Xunit;

namespace GalactiLog.Core.Tests.Metrics;

/// <summary>A filter's goal is the largest filter of its narrowband or RGB group.</summary>
public class IntegrationGoalTests
{
    private const double Hour = 3600;
    private static readonly FilterGoal None = new(null, null);

    // A failure is a goal taken across groups, or a short-by that is not goal minus own seconds.
    [Fact]
    public void Each_filter_is_short_of_the_largest_of_its_own_group()
    {
        var goals = IntegrationGoal.For(new Dictionary<string, double>
        {
            ["Ha"] = 10 * Hour,
            ["OIII"] = 6 * Hour,
            ["SII"] = 4 * Hour,
            ["R"] = 3 * Hour,
            ["G"] = 2 * Hour,
            ["B"] = 1 * Hour,
        });

        Assert.Equal(new FilterGoal(10 * Hour, 4 * Hour), goals["OIII"]);
        Assert.Equal(new FilterGoal(10 * Hour, 6 * Hour), goals["SII"]);
        Assert.Equal(new FilterGoal(3 * Hour, 1 * Hour), goals["G"]);
        Assert.Equal(new FilterGoal(3 * Hour, 2 * Hour), goals["B"]);
    }

    // A failure is the largest filter given a goal of itself and a zero short-by.
    [Fact]
    public void The_largest_of_a_group_has_no_goal()
    {
        var goals = IntegrationGoal.For(new Dictionary<string, double> { ["Ha"] = 5 * Hour, ["OIII"] = 2 * Hour });

        Assert.Equal(None, goals["Ha"]);
    }

    // A failure is L or an uncategorised filter joining a group or setting a goal for one.
    [Fact]
    public void L_and_uncategorised_filters_have_no_goal_and_set_none()
    {
        var goals = IntegrationGoal.For(new Dictionary<string, double>
        {
            ["L"] = 20 * Hour,
            ["Duoband"] = 15 * Hour,
            ["R"] = 2 * Hour,
            ["G"] = 1 * Hour,
        });

        Assert.Equal(None, goals["L"]);
        Assert.Equal(None, goals["Duoband"]);
        Assert.Equal(None, goals["R"]);
        Assert.Equal(new FilterGoal(2 * Hour, 1 * Hour), goals["G"]);
    }

    // A failure is a lone filter measured against another group's largest.
    [Fact]
    public void A_group_of_one_has_no_goal()
    {
        var goals = IntegrationGoal.For(new Dictionary<string, double> { ["Ha"] = 8 * Hour, ["R"] = 1 * Hour });

        Assert.Equal(None, goals["Ha"]);
        Assert.Equal(None, goals["R"]);
    }

    // A failure is a tied largest filter shown as short by zero.
    [Fact]
    public void A_tie_for_largest_gives_both_no_goal()
    {
        var goals = IntegrationGoal.For(new Dictionary<string, double>
        {
            ["Ha"] = 6 * Hour,
            ["SII"] = 6 * Hour,
            ["OIII"] = 2 * Hour,
        });

        Assert.Equal(None, goals["Ha"]);
        Assert.Equal(None, goals["SII"]);
        Assert.Equal(new FilterGoal(6 * Hour, 4 * Hour), goals["OIII"]);
    }

    // A failure is a goal of zero for a negative filter, or a NaN that wipes its group's goals.
    [Fact]
    public void Non_finite_and_non_positive_seconds_are_skipped()
    {
        var goals = IntegrationGoal.For(new Dictionary<string, double>
        {
            ["Ha"] = double.NaN,
            ["OIII"] = 2 * Hour,
            ["SII"] = 1 * Hour,
            ["R"] = -1 * Hour,
        });

        Assert.Equal(None, goals["Ha"]);
        Assert.Equal(None, goals["OIII"]);
        Assert.Equal(new FilterGoal(2 * Hour, 1 * Hour), goals["SII"]);
        Assert.Equal(None, goals["R"]);
    }

    // A failure is two keys of one category pooled into one member of the group.
    [Fact]
    public void Two_keys_of_one_category_are_two_members()
    {
        var goals = IntegrationGoal.For(new Dictionary<string, double>
        {
            ["Ha"] = 6 * Hour,
            ["H-alpha"] = 4 * Hour,
            ["OIII"] = 8 * Hour,
        });

        Assert.Equal(None, goals["OIII"]);
        Assert.Equal(new FilterGoal(8 * Hour, 2 * Hour), goals["Ha"]);
        Assert.Equal(new FilterGoal(8 * Hour, 4 * Hour), goals["H-alpha"]);
    }

    // A failure is a throw or an invented entry on a target with no filtered integration.
    [Fact]
    public void Empty_input_gives_an_empty_result()
        => Assert.Empty(IntegrationGoal.For(new Dictionary<string, double>()));
}
