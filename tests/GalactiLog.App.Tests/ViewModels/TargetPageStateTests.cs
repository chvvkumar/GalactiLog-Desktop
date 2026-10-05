using System.ComponentModel;
using System.Text.Json;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// P13 phase review P2-1. The document holds one value per target_page key, and the process holds
// one holder over it.
public class TargetPageStateTests
{
    [Fact]
    public void Construction_QueuesNoWrite()
    {
        // The backing-field rule, now the holder's: seeding the properties would queue a write of
        // the values just read back out of the document.
        var writes = new List<Func<DisplaySettings, DisplaySettings>>();
        var state = new TargetPageState(
            new TargetPageSettings { GradingBaseline = "rig", FrameListMode = "bad" },
            writes.Add);

        using var card = Cards.Create(targetPage: state);
        using var page = Page.Create(pageState: state).Settle();

        Assert.Empty(writes);
        Assert.Equal(GradingBaseline.Rig, state.GradingBaseline);
        Assert.Equal("bad", state.FrameListMode);
    }

    [Fact]
    public void ADisposedCard_StopsFollowingTheHolder()
    {
        // The holder is a process-wide singleton and outlives every card, so a card that did not
        // drop its handler would be kept alive by it until the process ends.
        var state = new TargetPageState();
        var card = Cards.Create(targetPage: state);
        var raised = 0;
        ((INotifyPropertyChanged)card.Card).PropertyChanged += (_, _) => raised++;

        card.Dispose();
        state.GradingBaseline = GradingBaseline.Rig;

        Assert.Equal(0, raised);
    }

    [Fact]
    public void LanesHeight_AFreshProfile_IsNull_AndConstructionWritesNothing()
    {
        // A failure looks like a fresh profile starting with a stored height, or a load rewriting one.
        var writes = new List<Func<DisplaySettings, DisplaySettings>>();
        var fresh = new TargetPageState();
        var seeded = new TargetPageState(
            new TargetPageSettings { Layouts = new() { ["modes"] = new TargetLayoutState { LanesHeight = 300 } } },
            writes.Add);

        Assert.Null(fresh.LanesHeight("modes"));
        Assert.Equal(300, seeded.LanesHeight("modes"));
        Assert.Null(seeded.LanesHeight("bench"));
        Assert.Empty(writes);
    }

    [Fact]
    public void SetLanesHeight_WritesOneKeysValue_AndLeavesTheOtherLayoutAndTheSiblingsIntact()
    {
        // A failure looks like one layout's value moving the other's, or a sibling key, the other
        // layout's extension data or a later phase's key clobbered by the nested write.
        var writes = new List<Func<DisplaySettings, DisplaySettings>>();
        var state = new TargetPageState(writeDisplay: writes.Add);
        var document = JsonSerializer.Deserialize<DisplaySettings>(
            "{\"target_page\":{\"grading_baseline\":\"rig\",\"a_later_phases_key\":42,"
            + "\"layouts\":{\"bench\":{\"lanes_height\":410,\"later\":true},\"modes\":{\"later\":1}}}}")!;

        state.SetLanesHeight("modes", 320);

        var written = Assert.Single(writes)(document).TargetPage;
        Assert.Equal(320, state.LanesHeight("modes"));
        Assert.Null(state.LanesHeight("bench"));
        Assert.Equal(320, written.Layouts["modes"].LanesHeight);
        Assert.Equal(1, written.Layouts["modes"].ExtensionData!["later"].GetInt32());
        Assert.Equal(410, written.Layouts["bench"].LanesHeight);
        Assert.Equal("rig", written.GradingBaseline);
        Assert.Contains("\"a_later_phases_key\":42", JsonSerializer.Serialize(written), StringComparison.Ordinal);
    }

    [Fact]
    public void SetLayout_WritesTheSidebarAndSectionKeysBesideTheLanesHeight_AndAnEqualChangeWritesNothing()
    {
        // A failure looks like a sidebar or section key clobbering lanes_height or a sibling layout,
        // or a change that leaves the record as it was queuing a write.
        var writes = new List<Func<DisplaySettings, DisplaySettings>>();
        var state = new TargetPageState(
            new TargetPageSettings { Layouts = new() { ["modes"] = new TargetLayoutState { LanesHeight = 300, SidebarWidth = 560 } } },
            writes.Add);
        var document = JsonSerializer.Deserialize<DisplaySettings>(
            "{\"target_page\":{\"layouts\":{\"modes\":{\"lanes_height\":300,\"sidebar_width\":560,\"later\":1},\"bench\":{\"lanes_height\":410}}}}")!;

        state.SetLayout("modes", layout => layout with { SidebarWidth = 560 });
        Assert.Empty(writes);

        state.SetLayout("modes", layout => layout with { SidebarCollapsed = true, NightMetricsOpen = false, NotesOpen = true });

        var written = JsonSerializer.Serialize(Assert.Single(writes)(document).TargetPage);
        Assert.True(state.Layout("modes").SidebarCollapsed);
        Assert.Equal(560, state.Layout("modes").SidebarWidth);
        Assert.Contains("\"lanes_height\":300", written, StringComparison.Ordinal);
        Assert.Contains("\"sidebar_width\":560", written, StringComparison.Ordinal);
        Assert.Contains("\"sidebar_collapsed\":true", written, StringComparison.Ordinal);
        Assert.Contains("\"night_metrics_open\":false", written, StringComparison.Ordinal);
        Assert.Contains("\"notes_open\":true", written, StringComparison.Ordinal);
        Assert.Contains("\"later\":1", written, StringComparison.Ordinal);
        Assert.Contains("\"bench\":{\"lanes_height\":410", written, StringComparison.Ordinal);
    }

    [Fact]
    public void SetLanesHeight_Null_ClearsTheValueByWritingNull_AndTheSameValueWritesNothing()
    {
        // A failure looks like a clear that leaves the stored number, or a repeated value queuing writes.
        var writes = new List<Func<DisplaySettings, DisplaySettings>>();
        var state = new TargetPageState(
            new TargetPageSettings { Layouts = new() { ["bench"] = new TargetLayoutState { LanesHeight = 410 } } },
            writes.Add);

        state.SetLanesHeight("bench", 410);
        Assert.Empty(writes);

        state.SetLanesHeight("bench", null);
        var document = JsonSerializer.Deserialize<DisplaySettings>("{\"target_page\":{\"layouts\":{\"bench\":{\"lanes_height\":410}}}}")!;
        var written = Assert.Single(writes)(document).TargetPage;
        Assert.Null(state.LanesHeight("bench"));
        Assert.Null(written.Layouts["bench"].LanesHeight);
    }

    [Fact]
    public void ANullLayoutEntry_BuildsReadsNullAndLaterWritesKeepTheOtherKeys()
    {
        // A failure looks like a hand-edited "modes": null crashing the holder at startup or the next write.
        var writes = new List<Func<DisplaySettings, DisplaySettings>>();
        var document = JsonSerializer.Deserialize<DisplaySettings>(
            "{\"target_page\":{\"layouts\":{\"modes\":null,\"bench\":{\"lanes_height\":410}}}}")!;
        var state = new TargetPageState(document.TargetPage, writes.Add);

        Assert.Null(state.LanesHeight("modes"));
        state.SetLanesHeight("modes", 300);

        var written = Assert.Single(writes)(document).TargetPage;
        Assert.Equal(300, written.Layouts["modes"].LanesHeight);
        Assert.Equal(410, written.Layouts["bench"].LanesHeight);
    }

    [Fact]
    public void ThePage_HandsBackTheHolderItWasGiven()
    {
        // The parts reach the process-wide holder through the page. A failure looks like a
        // choice landing in a private copy no other page sees.
        var state = new TargetPageState();
        using var page = Page.Create(pageState: state).Settle();

        Assert.Same(state, page.ViewModel.TargetPage);
    }
}
