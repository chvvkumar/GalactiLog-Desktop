using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.2's two panel states and spec 5.8.2's two display.dashboard keys (Phase 14C, UI layout
// ruling 4). The panel's state and its committed width persist per profile; every filter value
// stays session state, and the last case here is what guards that line.
public class DashboardFilterPanelStateTests
{
    private sealed class Profile
    {
        public DisplaySettings Document { get; set; } = new();
    }

    /// <summary>A stored panel that is open, for the cases that collapse it; a fresh profile is
    /// collapsed (polish 1 ruling 4).</summary>
    private static readonly DisplaySettings Expanded =
        new() { Dashboard = new DashboardDisplaySettings { FilterPanelExpanded = true } };

    private static DashboardViewModel Dashboard(Profile profile)
    {
        var dashboard = new DashboardViewModel(
            _ => DashboardViewModelTestFactory.EmptyPage,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            (_, _) => Task.CompletedTask,
            initialDisplay: profile.Document,
            getDisplay: () => profile.Document,
            saveDisplay: saved => profile.Document = saved,
            post: action => action());

        dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        DashboardViewModelTestFactory.Settle(dashboard);
        return dashboard;
    }

    private static void Settle(DashboardViewModel dashboard)
        => dashboard.PendingPanelPersist.GetAwaiter().GetResult();

    [Fact]
    public void AFreshProfile_OpensCollapsedAtThreeHundred()
    {
        // Polish 1 ruling 4. A regression is the full panel on a profile that has never opened it.
        using var dashboard = Dashboard(new Profile());

        Assert.True(dashboard.IsFilterPanelCollapsed);
        Assert.Equal(300d, dashboard.FilterPanelWidth, 3);
    }

    [Fact]
    public void TheWidth_ClampsToTwoTwentyOnWrite()
    {
        using var dashboard = Dashboard(new Profile());

        dashboard.FilterPanelWidth = 40d;

        Assert.Equal(220d, dashboard.FilterPanelWidth, 3);
    }

    [Fact]
    public void TheWidth_ClampsToFourEightyOnWrite()
    {
        using var dashboard = Dashboard(new Profile());

        dashboard.FilterPanelWidth = 4000d;

        Assert.Equal(480d, dashboard.FilterPanelWidth, 3);
    }

    [Fact]
    public void AStoredWidthBelowTheFloor_ReadsAsTwoTwenty()
    {
        var profile = new Profile
        {
            Document = new DisplaySettings
            {
                Dashboard = new DashboardDisplaySettings { FilterPanelWidth = 12 },
            },
        };

        using var dashboard = Dashboard(profile);

        Assert.Equal(220d, dashboard.FilterPanelWidth, 3);
    }

    [Fact]
    public void AStoredWidthAboveTheCeiling_ReadsAsFourEighty()
    {
        var profile = new Profile
        {
            Document = new DisplaySettings
            {
                Dashboard = new DashboardDisplaySettings { FilterPanelWidth = 9000 },
            },
        };

        using var dashboard = Dashboard(profile);

        Assert.Equal(480d, dashboard.FilterPanelWidth, 3);
    }

    [Fact]
    public void TheStateAndTheWidth_RoundTripThroughTheDocument()
    {
        var profile = new Profile { Document = Expanded };

        using (var first = Dashboard(profile))
        {
            first.FilterPanelWidth = 415d;
            first.ToggleFilterPanelCommand.Execute(null);
            Settle(first);
        }

        Assert.False(profile.Document.Dashboard.FilterPanelExpanded);
        Assert.Equal(415, profile.Document.Dashboard.FilterPanelWidth);

        using var second = Dashboard(profile);

        Assert.True(second.IsFilterPanelCollapsed);
        Assert.Equal(415d, second.FilterPanelWidth, 3);
    }

    [Fact]
    public void AProfileWithNoDashboardKey_TakesBothDefaults()
    {
        // Spec 5.8.2: no migration. A document written by an earlier version carries no dashboard
        // object and takes both defaults on its first read.
        var read = JsonSerializer.Deserialize<DisplaySettings>("""{"columns":{}}""");
        Assert.NotNull(read);

        using var dashboard = Dashboard(new Profile { Document = read! });

        Assert.True(dashboard.IsFilterPanelCollapsed);
        Assert.Equal(300d, dashboard.FilterPanelWidth, 3);
    }

    [Fact]
    public void AnUnrecognizedKeyInsideDashboard_SurvivesAWrite()
    {
        const string stored = """
        {"dashboard":{"filter_panel_expanded":true,"filter_panel_width":300,"future_key":7}}
        """;

        var profile = new Profile { Document = JsonSerializer.Deserialize<DisplaySettings>(stored)! };

        using var dashboard = Dashboard(profile);
        dashboard.ToggleFilterPanelCommand.Execute(null);
        Settle(dashboard);

        Assert.False(profile.Document.Dashboard.FilterPanelExpanded);
        Assert.Contains(
            "\"future_key\":7",
            JsonSerializer.Serialize(profile.Document),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandOnSection_OpensThatSection_AndChangesNoOther()
    {
        using var dashboard = Dashboard(new Profile());
        dashboard.Filters.DateRangeSection.IsExpanded = true;
        dashboard.IsFilterPanelCollapsed = true;

        var before = dashboard.Filters.Sections.Select(section => section.IsExpanded).ToList();

        dashboard.ExpandFilterPanelOnCommand.Execute("equipment");

        Assert.False(dashboard.IsFilterPanelCollapsed);
        Assert.True(dashboard.Filters.EquipmentSection.IsExpanded);

        for (var index = 0; index < dashboard.Filters.Sections.Count; index++)
        {
            if (dashboard.Filters.Sections[index].Key == "equipment")
            {
                continue;
            }

            Assert.Equal(before[index], dashboard.Filters.Sections[index].IsExpanded);
        }
    }

    [Fact]
    public void ExpandOnSection_WithAnUnknownKey_DoesNothing_EvenWhenExecutedDirectly()
    {
        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the gate has to
        // be repeated in the body. This calls Execute past it.
        using var dashboard = Dashboard(new Profile());
        dashboard.IsFilterPanelCollapsed = true;

        var before = dashboard.Filters.Sections.Select(section => section.IsExpanded).ToList();

        dashboard.ExpandFilterPanelOnCommand.Execute("no_such_section");
        dashboard.ExpandFilterPanelOnCommand.Execute("");
        dashboard.ExpandFilterPanelOnCommand.Execute(null);

        Assert.True(dashboard.IsFilterPanelCollapsed);
        Assert.Equal(before, dashboard.Filters.Sections.Select(section => section.IsExpanded).ToList());
    }

    [Fact]
    public void CollapsingAndReopening_KeepsTheCommittedWidth()
    {
        using var dashboard = Dashboard(new Profile { Document = Expanded });
        dashboard.FilterPanelWidth = 455d;

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Assert.True(dashboard.IsFilterPanelCollapsed);
        Assert.Equal(48d, dashboard.FilterPanelColumnWidth.Value, 3);

        dashboard.ToggleFilterPanelCommand.Execute(null);

        Assert.False(dashboard.IsFilterPanelCollapsed);
        Assert.Equal(455d, dashboard.FilterPanelWidth, 3);
        Assert.Equal(455d, dashboard.FilterPanelColumnWidth.Value, 3);
    }

    [AvaloniaFact]
    public void PersistPanelState_WritesOnTheCapturedContext()
    {
        // A FORWARD GUARD, not proof of the current shape, and review P3 is right that the name
        // alone reads as the second. As the code stands this case cannot fail: PersistPanelState
        // awaits nothing and returns synchronously, DisplayColumnWriter.Write(Func<>) queues the
        // load, modify and save onto TaskScheduler.Default under its own gate and raises no event,
        // and DashboardViewModel subscribes to nothing on the writer, so no PropertyChanged can
        // reach the page from the write path by any route. Assert.Empty(offDispatcher) is
        // therefore vacuous today and only the document assertion below currently proves anything.
        //
        // What would make it able to fail: PersistPanelState becoming async and awaiting the
        // writer, or the view-model subscribing to a writer event, or any continuation on this
        // path taking ConfigureAwait(false) above the service line and then writing an
        // [ObservableProperty] or raising a CanExecute notification. That is the shape Phase 14B
        // lost two clean-copy passes to in Clear log, which is why the guard is kept rather than
        // deleted.
        //
        // The document assertion is the part that is load bearing today.
        var profile = new Profile();
        using var dashboard = Dashboard(profile);

        var offDispatcher = new List<string>();
        dashboard.PropertyChanged += (_, args) =>
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                lock (offDispatcher)
                {
                    offDispatcher.Add(args.PropertyName ?? "(null)");
                }
            }
        };

        dashboard.ToggleFilterPanelCommand.Execute(null);
        dashboard.FilterPanelWidth = 401d;
        Settle(dashboard);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(offDispatcher);
        Assert.Equal(401, profile.Document.Dashboard.FilterPanelWidth);
    }

    [Fact]
    public void TheFilterValues_StillDoNotPersist()
    {
        // Spec 12.2's Layout paragraph draws this line explicitly: the panel's own width and state
        // persist across restarts, every filter value lives for the running session only. The
        // obvious over-reach is a PersistPanelState that writes the whole panel.
        var profile = new Profile { Document = Expanded };
        using var dashboard = Dashboard(profile);

        dashboard.Filters.SearchText = "andromeda";
        dashboard.Filters.ObjectTypes[0].IsSelected = true;
        dashboard.Filters.MetricsSection.IsExpanded = true;

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Settle(dashboard);

        var written = JsonSerializer.Serialize(profile.Document);

        Assert.DoesNotContain("andromeda", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object_type", written, StringComparison.Ordinal);
        // The dashboard object alone: display.target_page carries its own keys, which are not this panel's sections.
        Assert.DoesNotContain("metrics", JsonSerializer.Serialize(profile.Document.Dashboard), StringComparison.Ordinal);
        Assert.False(profile.Document.Dashboard.FilterPanelExpanded);
    }
}
