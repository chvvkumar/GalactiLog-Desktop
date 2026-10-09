using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.CustomColumnTestFactory;

namespace GalactiLog.App.Tests.ViewModels.CustomColumns;

// Phase 20 Task 3, brief section 7 cases 15 to 18. Spec 12.15's "Where the cells appear" table, as
// the one place a surface's column set is built. Pure static members over a definition list: no
// database, no view and no settings document except the one this phase's default sits in.
public class CustomColumnSetTests
{
    [Fact]
    public void DashboardRow_TakesTargetScopeColumnsNotInTheHiddenList_InDisplayOrder()
    {
        var second = Factory.Boolean("Second", order: 1);
        var first = Factory.Text("First", order: 0);
        var hidden = Factory.Boolean("Hidden", order: 2);

        // The trap this case exists for: a session-scope column whose slug is NOT in the hidden
        // list. A filter on the hidden list alone would put it on the target row, where its value
        // has no night to belong to.
        var night = Factory.Boolean("Night", order: 3, scope: CustomColumnScope.Session);

        var all = new List<CustomColumnDefinition> { second, night, hidden, first };

        var chosen = CustomColumnSet.DashboardRow(all, [hidden.Slug]);

        Assert.Equal<string[]>([first.Slug, second.Slug], [.. chosen.Select(column => column.Slug)]);
    }

    [Fact]
    public void NightExpanderAndRigRow_AreUngated()
    {
        var session = Factory.Boolean("Session", order: 0, scope: CustomColumnScope.Session);
        var rig = Factory.Boolean("Rig", order: 0, scope: CustomColumnScope.Rig);
        var all = new List<CustomColumnDefinition> { session, rig };

        // Neither surface has a picker (user choice 4). A gate copied from the dashboard would
        // answer empty here, because nothing put either slug in a visible list.
        Assert.Equal(session, Assert.Single(CustomColumnSet.NightExpander(all)));
        Assert.Equal(rig, Assert.Single(CustomColumnSet.RigRow(all)));
    }

    [Fact]
    public void LedgerRow_ShowsEveryColumnOnAFreshProfile()
    {
        var session = Factory.Boolean("Session", scope: CustomColumnScope.Session);
        var all = new List<CustomColumnDefinition> { session };

        // Custom columns start shown: a fresh profile hides nothing.
        var hidden = new DisplaySettings().ColumnsFor(DisplaySettings.LedgerHiddenTableId);

        Assert.Empty(hidden);
        Assert.Equal(session, Assert.Single(CustomColumnSet.LedgerRow(all, hidden)));

        // And the gate is a gate, not an always-full answer.
        Assert.Empty(CustomColumnSet.LedgerRow(all, [session.Slug]));
    }

    [Fact]
    public void AnyRigScope_IsFalseWithNoRigColumn_AndTrueWithOne()
    {
        // This is the predicate that makes the per filter table draw a rig label row on a
        // single-rig night (user choice 7).
        var withoutRig = new List<CustomColumnDefinition>
        {
            Factory.Boolean("Target"),
            Factory.Boolean("Session", scope: CustomColumnScope.Session),
        };

        Assert.False(CustomColumnSet.AnyRigScope([]));
        Assert.False(CustomColumnSet.AnyRigScope(withoutRig));
        Assert.True(CustomColumnSet.AnyRigScope(
            [.. withoutRig, Factory.Boolean("Rig", scope: CustomColumnScope.Rig)]));
    }
}
