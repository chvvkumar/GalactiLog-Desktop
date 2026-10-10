using Avalonia.Media;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Xunit;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.2 and 18.3: the filter panel maps its state onto TargetListingCriteria and
// nothing else. Plain xunit facts -- no window, no database, no dispatcher. The debounce is
// driven by the injected FakeDelay already shared with WatcherServiceTests and
// ScanSchedulerTests, so nothing here sleeps.
public class FilterPanelViewModelTests
{
    private static readonly TargetListingCriteria Seed = new();

    // ---- construction helpers -------------------------------------------------------------

    private static AliasMap Map(
        (string Name, string Color)[]? filters = null,
        string[]? cameras = null,
        string[]? telescopes = null)
    {
        var filterSettings = new Dictionary<string, FilterSetting>();
        foreach (var (name, color) in filters ?? [])
        {
            filterSettings[name] = new FilterSetting { Color = color };
        }

        var equipment = new EquipmentSettings();
        foreach (var camera in cameras ?? [])
        {
            equipment.Cameras[camera] = new EquipmentItemSettings();
        }

        foreach (var telescope in telescopes ?? [])
        {
            equipment.Telescopes[telescope] = new EquipmentItemSettings();
        }

        return new AliasMap(filterSettings, equipment);
    }

    // The panel fills its three library-derived lists on a background task and publishes through
    // the post seam, so every test that reads them waits for that one reload first. `post` runs
    // the closure inline: there is no dispatcher in these tests.
    private static FilterPanelViewModel CreatePanel(
        AliasMap? aliases = null,
        DashboardFacets? facets = null,
        IReadOnlyList<string>? headerKeys = null,
        DateOnly? today = null)
    {
        var panel = new FilterPanelViewModel(
            () => aliases ?? Map(),
            () => facets ?? new DashboardFacets([], [], []),
            () => headerKeys ?? [],
            today is null ? null : () => today.Value,
            action => action());

        // Phase review item 8: Reload is no longer started from the panel's constructor, so the
        // owner starts it. Here that owner is the test.
        panel.Reload();
        panel.PendingReload!.GetAwaiter().GetResult();
        return panel;
    }

    // Phase review item 1. Two Reloads overlap (startup plus a scan that finishes during it) and
    // their background queries can complete in either order. The older publish is replayed last
    // here; the newer lists must survive it, and the pair must cost exactly one Changed.
    [Fact]
    public async Task Reload_OverlappingReloads_OlderPublishReplayedLast_DoesNotOverwriteTheNewerLists()
    {
        var queued = new List<Action>();
        var facets = new DashboardFacets([new FilterFacet("Ha", "Ha", 3)], ["ASI2600"], ["RC8"]);
        var panel = new FilterPanelViewModel(
            () => Map(),
            () => facets,
            () => ["EXPTIME"],
            null,
            queued.Add);

        // The owner's first reload; drain it so the lists start populated.
        panel.Reload();
        await panel.PendingReload!;
        queued.Single()();
        queued.Clear();

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        // Two more reloads. Neither has published yet: both closures are parked in `queued`.
        facets = new DashboardFacets([new FilterFacet("Ha", "Ha", 3)], ["ASI2600"], ["RC8"]);
        panel.Reload();
        await panel.PendingReload!;

        facets = new DashboardFacets([new FilterFacet("OIII", "OIII", 9)], ["ASI6200"], ["RASA"]);
        panel.Reload();
        await panel.PendingReload!;

        Assert.Equal(2, queued.Count);

        // The newer one lands first, then the older one is replayed. Completion order, not
        // request order, is what the guard has to survive.
        queued[1]();
        queued[0]();

        Assert.Equal(["OIII"], panel.Filters.Select(pill => pill.Key));
        Assert.Equal([FilterPanelViewModel.AnyOption, "ASI6200"], panel.Cameras);
        Assert.Equal([FilterPanelViewModel.AnyOption, "RASA"], panel.Telescopes);
        Assert.Equal(0, changed);
    }

    // Records every criteria the dashboard hands the listing query. List runs on a thread pool
    // thread (the dashboard never queries on the UI thread), hence the lock.
    private sealed class RecordingListQuery
    {
        private readonly Lock _gate = new();
        private readonly List<TargetListingCriteria> _calls = [];

        public TargetListingPage Page { get; set; } = new([], 0, 0d, 0, 1, 50);

        public IReadOnlyList<TargetListingCriteria> Calls
        {
            get { lock (_gate) { return [.. _calls]; } }
        }

        public void Clear()
        {
            lock (_gate) { _calls.Clear(); }
        }

        public TargetListingPage List(TargetListingCriteria criteria)
        {
            lock (_gate) { _calls.Add(criteria); }
            return Page;
        }
    }

    private static DashboardViewModel CreateDashboard(
        RecordingListQuery query,
        FakeDelay delay,
        AliasMap? aliases = null,
        DashboardFacets? facets = null,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null)
    {
        var dashboard = new DashboardViewModel(
            query.List,
            () => facets ?? new DashboardFacets([], [], []),
            () => [],
            () => aliases ?? Map(),
            new GeneralSettings(),
            delay.Delay,
            scanStatus: scanStatus,
            post: post ?? (action => action()));
        dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        return dashboard;
    }

    // Releases parked waits until the dashboard's in-flight window has resolved, exactly as
    // FakeDelay.DrainAsync does for the watcher.
    private static async Task DrainAsync(DashboardViewModel dashboard, FakeDelay delay)
    {
        var work = dashboard.PendingQuery;
        if (work is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!work.IsCompleted && DateTime.UtcNow < deadline)
        {
            delay.Release();
            await Task.Delay(2);
        }

        Assert.True(work.IsCompleted, "The dashboard's pending query never completed.");
        await work;
        Assert.Null(dashboard.LastQueryFailure);
    }

    // For tests that capture the post seam instead of running it inline: runs whatever is queued,
    // draining any query a posted closure itself opens, until nothing is left.
    private static async Task SettleAsync(DashboardViewModel dashboard, FakeDelay delay, List<Action> posted)
    {
        for (var round = 0; round < 10; round++)
        {
            await DrainAsync(dashboard, delay);
            if (posted.Count == 0)
            {
                return;
            }

            var pending = posted.ToArray();
            posted.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }

        Assert.Empty(posted);
    }

    // ---- Search ----------------------------------------------------------------------------

    [Fact]
    public void Search_PinnedTarget_SetsTargetIdCriterion()
    {
        var panel = CreatePanel();
        var id = Guid.NewGuid();
        panel.PinnedTargetId = id;

        var criteria = panel.BuildCriteria(Seed);

        Assert.Equal(id, criteria.TargetId);
        Assert.Null(criteria.UnresolvedObject);
    }

    [Fact]
    public void Search_PinnedUnresolvedObject_SetsUnresolvedObjectCriterion()
    {
        var panel = CreatePanel();
        panel.PinnedUnresolvedObject = "Barnard 33";

        var criteria = panel.BuildCriteria(Seed);

        Assert.Equal("Barnard 33", criteria.UnresolvedObject);
        Assert.Null(criteria.TargetId);
    }

    [Fact]
    public void Search_FreeTextAlone_SetsNoCriterion()
    {
        var panel = CreatePanel();
        panel.SearchText = "andromeda";

        var criteria = panel.BuildCriteria(Seed);

        Assert.Null(criteria.TargetId);
        Assert.Null(criteria.UnresolvedObject);
        Assert.False(criteria.AnyFilterActive);
    }

    // ---- Object Type -------------------------------------------------------------------------

    [Fact]
    public void ObjectType_SelectedPills_MapToObjectCategories()
    {
        var panel = CreatePanel();
        panel.ObjectTypes.Single(pill => pill.Key == "Galaxy").IsSelected = true;
        panel.ObjectTypes.Single(pill => pill.Key == "Unresolved").IsSelected = true;

        var criteria = panel.BuildCriteria(Seed);

        Assert.Equal(["Galaxy", "Unresolved"], criteria.ObjectCategories);
    }

    [Fact]
    public void ObjectType_PillsAreExactlySixteen_InSpecOrder()
    {
        var panel = CreatePanel();

        Assert.Equal(16, panel.ObjectTypes.Count);
        Assert.Equal(
            [
                "Emission Nebula", "Reflection Nebula", "Dark Nebula", "Planetary Nebula",
                "Supernova Remnant", "Galaxy", "Open Cluster", "Globular Cluster", "Star",
                "Planet", "Moon", "Sun", "Comet", "Asteroid",
                "Other", "Unresolved",
            ],
            panel.ObjectTypes.Select(pill => pill.Key));
    }

    // ---- Date Range --------------------------------------------------------------------------

    [Fact]
    public void DateRange_MapsToSessionDateFromAndTo()
    {
        var panel = CreatePanel();
        panel.DateFrom = new DateTimeOffset(new DateTime(2024, 1, 5), TimeSpan.Zero);
        panel.DateTo = new DateTimeOffset(new DateTime(2025, 12, 7), TimeSpan.Zero);

        var criteria = panel.BuildCriteria(Seed);

        Assert.Equal(new DateOnly(2024, 1, 5), criteria.SessionDateFrom);
        Assert.Equal(new DateOnly(2025, 12, 7), criteria.SessionDateTo);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 30)]
    [InlineData(2, 90)]
    [InlineData(3, 365)]
    public void DateRange_Preset_SetsBothBounds(int index, int days)
    {
        var today = new DateOnly(2025, 6, 15);
        var panel = CreatePanel(today: today);
        var preset = panel.DatePresets[index];

        Assert.Equal(days, preset.Days);
        preset.ApplyCommand.Execute(null);

        var criteria = panel.BuildCriteria(Seed);
        Assert.Equal(today.AddDays(-(days - 1)), criteria.SessionDateFrom);
        Assert.Equal(today, criteria.SessionDateTo);
    }

    // ---- Filters -----------------------------------------------------------------------------

    [Fact]
    public void Filters_SelectedPills_MapToCanonicalFilterNames()
    {
        var panel = CreatePanel(facets: new DashboardFacets(
            [new FilterFacet("Ha", "#808080", 10), new FilterFacet("OIII", "#808080", 4)], [], []));
        panel.Filters.Single(pill => pill.Key == "OIII").IsSelected = true;

        var criteria = panel.BuildCriteria(Seed);

        Assert.Equal(["OIII"], criteria.Filters);
    }

    [Fact]
    public void Filters_PillColour_ComesFromTheAliasMap_AndFallsBackToGreyWhenMalformed()
    {
        var panel = CreatePanel(
            aliases: Map(filters: [("Ha", "#FF0000"), ("Broken", "not-a-colour")]),
            facets: new DashboardFacets(
                [new FilterFacet("Lum", "#808080", 3), new FilterFacet("Duoband", "#808080", 2)], [], []));

        Assert.Equal(Color.Parse("#FF0000"), Tint(panel, "Ha"));
        // A malformed configured colour falls back to grey rather than throwing.
        Assert.Equal(Color.Parse("#808080"), Tint(panel, "Broken"));
        // P13 R2a re-pointed the third assertion. A discovered but unconfigured filter takes the
        // alias map's default grey only when its name folds to no category; "Lum" folds to L and
        // now takes the seeded white, through the same one spine.
        Assert.Equal(Color.Parse("#808080"), Tint(panel, "Duoband"));
        Assert.Equal(Color.Parse("#e0e0e0"), Tint(panel, "Lum"));

        static Color Tint(FilterPanelViewModel panel, string key)
        {
            var pill = panel.Filters.Single(option => option.Key == key);
            Assert.True(pill.HasTint);
            return pill.Tint!.Color;
        }
    }

    [Fact]
    public void Filters_PillList_IsTheUnionOfConfiguredAndDiscoveredNames()
    {
        var panel = CreatePanel(
            aliases: Map(filters: [("OIII", "#00AAAA"), ("Ha", "#FF0000")]),
            facets: new DashboardFacets(
                [new FilterFacet("Ha", "#FF0000", 5), new FilterFacet("L", "#CCCCCC", 10)], [], []));

        // Every discovered name and every configured name, once each, in filter order (polish
        // ruling 5). A configured filter present on no frame is still offered, at count zero.
        Assert.Equal(["L", "Ha", "OIII"], panel.Filters.Select(pill => pill.Key));
        Assert.Equal([10, 5, 0], panel.Filters.Select(pill => pill.FrameCount));
    }

    [Fact]
    public void Filters_PillList_IsInFilterOrder_NotFrameCountOrder()
    {
        // Polish ruling 5: L, R, G, B, SII, Ha, OIII then the rest alphabetically, whatever the
        // counts. A failure is frame count descending Duoband, B, L or alphabetical B, Duoband, L.
        var panel = CreatePanel(facets: new DashboardFacets(
            [new FilterFacet("B", "#0000FF", 4), new FilterFacet("Duoband", "#808080", 9), new FilterFacet("L", "#CCCCCC", 1)],
            [], []));

        Assert.Equal(["L", "B", "Duoband"], panel.Filters.Select(pill => pill.Key));
    }

    [Fact]
    public void Filters_PillList_RanksANonstandardCanonicalByItsFirstFoldingAlias()
    {
        // Polish wave 2 ruling 1: a canonical name that folds to no category takes the category
        // of its first configured alias, as FilterColor.Resolve already does for its tint. A
        // failure is "Chroma Ha 3nm" ranking as an unknown filter, after OIII.
        var filters = new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new(),
            ["Chroma Ha 3nm"] = new() { Aliases = ["Ha"] },
        };
        var panel = CreatePanel(aliases: new AliasMap(filters, new EquipmentSettings()));

        Assert.Equal(["Chroma Ha 3nm", "OIII"], panel.Filters.Select(pill => pill.Key));
    }

    // ---- Equipment ---------------------------------------------------------------------------

    [Fact]
    public void Equipment_CameraAndTelescope_MapToCriteria()
    {
        var panel = CreatePanel(
            aliases: Map(cameras: ["ASI2600MM"], telescopes: ["RC8"]),
            facets: new DashboardFacets([], ["ASI294MC"], ["FRA600"]));

        // Both combo boxes lead with Any, then the union of what is configured and what the
        // library holds.
        Assert.Equal(FilterPanelViewModel.AnyOption, panel.Cameras[0]);
        Assert.Equal(FilterPanelViewModel.AnyOption, panel.Telescopes[0]);
        Assert.Equal(3, panel.Cameras.Count);
        Assert.Contains("ASI294MC", panel.Cameras);
        Assert.Contains("ASI2600MM", panel.Cameras);
        Assert.Equal(3, panel.Telescopes.Count);
        Assert.Contains("RC8", panel.Telescopes);
        Assert.Contains("FRA600", panel.Telescopes);

        panel.SelectedCamera = "ASI2600MM";
        panel.SelectedTelescope = "RC8";

        var criteria = panel.BuildCriteria(Seed);
        Assert.Equal("ASI2600MM", criteria.Camera);
        Assert.Equal("RC8", criteria.Telescope);
    }

    // ---- Metrics Quality ---------------------------------------------------------------------

    [Fact]
    public void Metrics_EachBound_MapsToTheMatchingMetricRange()
    {
        var panel = CreatePanel();
        Metric(panel, "hfr").Max = 3.0;
        Metric(panel, "stars").Min = 500;

        var criteria = panel.BuildCriteria(Seed);

        Assert.Equal(new MetricRange(null, 3.0), criteria.MetricRanges["hfr"]);
        Assert.Equal(new MetricRange(500, null), criteria.MetricRanges["stars"]);
        Assert.Equal(2, criteria.MetricRanges.Count);
    }

    [Fact]
    public void Metrics_MetricKeys_MatchTheQueryColumnMapExactly()
    {
        // The panel's table and Task 2's column map are the two halves of one contract. This is
        // the test the handoff note names: change one and this fails.
        Assert.Equal(
            MetricColumns.ByKey.Keys.OrderBy(key => key, StringComparer.Ordinal),
            MetricRangeViewModel.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void Metrics_UnsetBound_ContributesNoRange()
    {
        var panel = CreatePanel();

        Assert.Empty(panel.BuildCriteria(Seed).MetricRanges);

        Metric(panel, "airmass").Min = 1.0;
        Metric(panel, "airmass").Min = null;

        Assert.Empty(panel.BuildCriteria(Seed).MetricRanges);
    }

    [Fact]
    public void Metrics_GroupsAreTheSixOfTheSpec()
    {
        var panel = CreatePanel();

        Assert.Equal(
            ["Quality", "Guiding", "ADU", "Focuser", "Weather", "Mount"],
            panel.MetricGroups.Select(group => group.Title));
        Assert.Equal(10, panel.MetricGroups.Sum(group => group.Metrics.Count));
        Assert.Equal(["HFR", "FWHM", "Eccentricity", "Stars"], panel.MetricGroups[0].Metrics.Select(m => m.Label));
    }

    // ---- FITS Header Query -------------------------------------------------------------------

    [Fact]
    public void HeaderQuery_CommittedRows_MapToHeaderConditions()
    {
        var panel = CreatePanel(headerKeys: ["OBJECT", "GAIN"]);
        Add(panel, "OBJECT", "contains", "NGC");
        Add(panel, "GAIN", ">=", "100");

        var criteria = panel.BuildCriteria(Seed);

        Assert.Equal(
            [new HeaderCondition("OBJECT", "contains", "NGC"), new HeaderCondition("GAIN", ">=", "100")],
            criteria.HeaderConditions);
        Assert.Equal("OBJECT contains NGC", panel.HeaderConditions[0].Display);
    }

    [Fact]
    public void HeaderQuery_DraftRow_IsNotIncludedUntilAdded()
    {
        var panel = CreatePanel(headerKeys: ["OBJECT"]);
        panel.DraftHeaderKey = "OBJECT";
        panel.DraftHeaderOperator = "=";
        panel.DraftHeaderValue = "M 31";

        Assert.Empty(panel.BuildCriteria(Seed).HeaderConditions);

        panel.AddHeaderConditionCommand.Execute(null);

        Assert.Single(panel.BuildCriteria(Seed).HeaderConditions);
        // The draft row clears once committed, so Enter twice cannot duplicate it.
        Assert.Null(panel.DraftHeaderKey);
        Assert.Equal("", panel.DraftHeaderValue);
    }

    [Fact]
    public void HeaderQuery_RemoveRow_DropsThatConditionOnly()
    {
        var panel = CreatePanel();
        Add(panel, "OBJECT", "=", "M 31");
        Add(panel, "GAIN", ">", "50");
        Add(panel, "FILTER", "=", "Ha");

        panel.RemoveHeaderConditionCommand.Execute(panel.HeaderConditions[1]);

        Assert.Equal(
            [new HeaderCondition("OBJECT", "=", "M 31"), new HeaderCondition("FILTER", "=", "Ha")],
            panel.BuildCriteria(Seed).HeaderConditions);
    }

    [Fact]
    public void HeaderQuery_RowsOwnRemoveCommand_UsesTheSamePath()
    {
        var panel = CreatePanel();
        Add(panel, "OBJECT", "=", "M 31");

        panel.HeaderConditions[0].RemoveCommand.Execute(null);

        Assert.Empty(panel.HeaderConditions);
    }

    [Fact]
    public void HeaderQuery_OperatorsAreTheSevenFromTheBuilder()
    {
        var panel = CreatePanel();

        // Bound to the gate's own list, so the combo box and HeaderQueryBuilder cannot disagree.
        Assert.Equal(HeaderQueryBuilder.SupportedOperators, panel.HeaderOperators);
        Assert.Equal(7, panel.HeaderOperators.Count);
    }

    [Fact]
    public void HeaderQuery_KeysComeFromTheDistinctKeysDelegate()
    {
        var panel = CreatePanel(headerKeys: ["GAIN", "OBJECT"]);

        Assert.Equal(["GAIN", "OBJECT"], panel.HeaderKeys);
    }

    // ---- Debounce ------------------------------------------------------------------------------

    [Fact]
    public async Task RapidInput_CoalescesToASingleQuery()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);
        query.Clear();

        // A metric bound, which spec 12.2 names explicitly as a debounced input. Search text is
        // deliberately not one of them: it pins nothing, so it re-issues no listing query at all.
        var hfr = Metric(dashboard.Filters, "hfr");
        hfr.Max = 5.0;
        hfr.Max = 4.0;
        hfr.Max = 3.5;
        hfr.Max = 3.2;
        hfr.Max = 3.0;

        await DrainAsync(dashboard, delay);

        Assert.Single(query.Calls);
        Assert.Equal(new MetricRange(null, 3.0), query.Calls[0].MetricRanges["hfr"]);
    }

    [Fact]
    public async Task Debounce_FiresOnceAfterTheWindowElapses()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);
        query.Clear();

        dashboard.Filters.ObjectTypes[0].IsSelected = true;

        // Nothing runs while the window is still open.
        Assert.Empty(query.Calls);
        Assert.Contains(DashboardViewModel.DebounceWindow, delay.Requested);

        await DrainAsync(dashboard, delay);

        Assert.Single(query.Calls);
        Assert.Equal(["Emission Nebula"], query.Calls[0].ObjectCategories);
    }

    [Fact]
    public async Task PillTogglesAndComboSelections_TakeTheSameDebouncedPath()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(
            query, delay, facets: new DashboardFacets([], ["ASI2600MM"], []));
        await DrainAsync(dashboard, delay);
        query.Clear();

        dashboard.Filters.ObjectTypes[0].IsSelected = true;
        dashboard.Filters.SelectedCamera = "ASI2600MM";
        await DrainAsync(dashboard, delay);

        Assert.Single(query.Calls);
        Assert.Equal("ASI2600MM", query.Calls[0].Camera);
    }

    // ---- Active markers -------------------------------------------------------------------------

    [Theory]
    [InlineData("search")]
    [InlineData("object_type")]
    [InlineData("date_range")]
    [InlineData("filters")]
    [InlineData("equipment")]
    [InlineData("metrics")]
    [InlineData("header_query")]
    public void EachSection_ShowsActive_OnlyWhenItHoldsAValue(string key)
    {
        var panel = CreatePanel(facets: new DashboardFacets(
            [new FilterFacet("Ha", "#FF0000", 1)], ["ASI2600MM"], ["RC8"]));

        Assert.All(panel.Sections, section => Assert.False(section.IsActive));

        Activate(panel, key);

        foreach (var section in panel.Sections)
        {
            Assert.Equal(section.Key == key, section.IsActive);
        }
    }

    // ---- Reset --------------------------------------------------------------------------------

    [Fact]
    public void Reset_ClearsEverySection()
    {
        var panel = CreatePanel(facets: new DashboardFacets(
            [new FilterFacet("Ha", "#FF0000", 1)], ["ASI2600MM"], ["RC8"]));

        foreach (var key in panel.Sections.Select(section => section.Key))
        {
            Activate(panel, key);
        }

        panel.Sections[3].IsExpanded = true;
        Assert.All(panel.Sections, section => Assert.True(section.IsActive));

        panel.ResetCommand.Execute(null);

        Assert.All(panel.Sections, section => Assert.False(section.IsActive));
        Assert.False(panel.BuildCriteria(Seed).AnyFilterActive);
        Assert.Equal("", panel.SearchText);
        Assert.Null(panel.PinnedLabel);
        Assert.Empty(panel.HeaderConditions);
        Assert.Null(panel.DraftHeaderKey);
        Assert.Equal("=", panel.DraftHeaderOperator);
        // Section expansion is presentation state, not a filter value.
        Assert.True(panel.Sections[3].IsExpanded);
    }

    [Fact]
    public async Task Reset_DoesNotChangeSortOrPaging()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);

        // Sort and paging belong to the target list (Task 7), never to the filter panel.
        dashboard.Targets.Sort = TargetListingSort.Name;
        dashboard.Targets.Descending = false;
        dashboard.Targets.PageSize = 25;
        dashboard.Targets.Page = 3;
        dashboard.Filters.ObjectTypes[0].IsSelected = true;
        await DrainAsync(dashboard, delay);
        query.Clear();

        dashboard.Filters.ResetCommand.Execute(null);
        await DrainAsync(dashboard, delay);

        var criteria = Assert.Single(query.Calls);
        Assert.Equal(TargetListingSort.Name, criteria.Sort);
        Assert.False(criteria.Descending);
        Assert.Equal(25, criteria.PageSize);
        Assert.False(criteria.AnyFilterActive);

        // Page is the deliberate exception, and it is Task 7's rule rather than this panel's: a
        // narrower filtered set can have fewer pages than the one the user is standing on, so any
        // filter change, Reset included, returns to page one.
        Assert.Equal(1, criteria.Page);
    }

    [Fact]
    public async Task Reset_IssuesExactlyOneQuery()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        dashboard.Filters.ObjectTypes[0].IsSelected = true;
        dashboard.Filters.SearchText = "m31";
        dashboard.Filters.DateFrom = new DateTimeOffset(new DateTime(2024, 1, 1), TimeSpan.Zero);
        await DrainAsync(dashboard, delay);
        query.Clear();

        // A dozen values are cleared, but the panel raises Changed once for the whole reset and
        // the window coalesces it regardless.
        dashboard.Filters.ResetCommand.Execute(null);
        await DrainAsync(dashboard, delay);

        Assert.Single(query.Calls);
    }

    // ---- Summary strip -------------------------------------------------------------------------

    [Fact]
    public async Task SummaryStrip_ReadsTotalsFromThePage_NotThePageRows()
    {
        var query = new RecordingListQuery
        {
            // No rows at all: every figure must come from the totals, which describe the whole
            // filtered set rather than the slice.
            Page = new TargetListingPage([], 7, 270_000d, 900, 1, 50),
        };
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);

        await DrainAsync(dashboard, delay);

        Assert.Equal(75d, dashboard.TotalIntegrationHours, 3);
        Assert.Equal(7, dashboard.TotalGroups);
        Assert.Equal(900, dashboard.TotalFrames);
    }

    [Fact]
    public void SummaryStrip_GroupsLabel_IsLiterallyGroups()
    {
        // Spec 12.2 keeps this deliberately distinct from the Statistics page's "resolved
        // targets": it counts unresolved obj: groups too.
        Assert.Equal("Groups", DashboardViewModelTestFactory.Create().GroupsLabel);
    }

    [Fact]
    public async Task SummaryStrip_FilteredMarker_AppearsOnlyWhenAFilterIsActive()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var dashboard = CreateDashboard(query, delay);
        await DrainAsync(dashboard, delay);

        Assert.False(dashboard.IsFiltered);

        dashboard.Filters.ObjectTypes[0].IsSelected = true;
        await DrainAsync(dashboard, delay);
        Assert.True(dashboard.IsFiltered);

        dashboard.Filters.ResetCommand.Execute(null);
        await DrainAsync(dashboard, delay);
        Assert.False(dashboard.IsFiltered);
    }

    // Spec 12.2's ceiling is 200 ms; a slow query must never run where the bindings live. F18
    // follow-up: proven by parking the query and observing that the thread which released the
    // debounce carried on, rather than by comparing thread ids after an await that frees the
    // calling thread for the pool to reuse (TRACKING section 2 item 8).
    [Fact]
    public async Task Query_RunsOffTheCallingThread()
    {
        using var release = new ManualResetEventSlim(false);
        var queries = 0;
        var delay = new FakeDelay();
        var dashboard = new DashboardViewModel(
            _ =>
            {
                Interlocked.Increment(ref queries);
                release.Wait(TimeSpan.FromSeconds(30));
                return new TargetListingPage([], 0, 0d, 0, 1, 50);
            },
            () => new DashboardFacets([], [], []),
            () => [],
            () => Map(),
            new GeneralSettings(),
            delay.Delay);

        // The constructor requested the first query behind the debounce. Releasing that window is
        // what starts the read, and this line returns while the read is parked: a read on this
        // thread would have parked it here.
        delay.Release();
        Assert.False(dashboard.PendingQuery!.IsCompleted);

        release.Set();
        await DrainAsync(dashboard, delay);

        Assert.True(Volatile.Read(ref queries) >= 1);
    }

    // ---- Session persistence (coordinator ruling Q4) ---------------------------------------------

    [Fact]
    public void State_SurvivesNavigatingAwayAndBack()
    {
        // The DI singleton is the whole implementation of spec 12.2's "persist for the session".
        var dashboard = DashboardViewModelTestFactory.Create();
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        // Review finding M12: disposed, so its nine ChartTheme.Changed subscriptions do not outlive
        // the test.
        using var statistics = StatisticsViewModelTestFactory.Create();
        var shell = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard,
            new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel),
            TabFactory.CreateSettingsPage(),
            // Phase 9 Task 3 made the statistics destination a real page, and FIXER LIST F21 made
            // both it and the Activity page factories the shell calls on the first visit.
            () => statistics,
            // Never invoked either: this case navigates to Statistics and back and to nothing else.
            () => AnalysisViewModelTestFactory.Create(),
            // Never invoked: this case navigates to Statistics and back, never to Activity.
            () => ActivityViewModelTestFactory.Create());
        dashboard.Filters.SearchText = "andromeda";
        dashboard.Filters.ObjectTypes[5].IsSelected = true;
        dashboard.IsFilterPanelCollapsed = true;

        shell.Selected = shell.Items[2];
        shell.Selected = shell.Items[0];

        var restored = Assert.IsType<DashboardViewModel>(shell.CurrentPage);
        Assert.Same(dashboard, restored);
        Assert.Equal("andromeda", restored.Filters.SearchText);
        Assert.True(restored.Filters.ObjectTypes[5].IsSelected);
        Assert.True(restored.IsFilterPanelCollapsed);
    }

    [Fact]
    public void ToggleFilterPanel_FlipsTheCollapsedFlag()
    {
        var dashboard = DashboardViewModelTestFactory.Create();

        // Collapsed on a fresh profile (polish 1 ruling 4), so the first toggle opens it.
        Assert.True(dashboard.IsFilterPanelCollapsed);
        dashboard.ToggleFilterPanelCommand.Execute(null);
        Assert.False(dashboard.IsFilterPanelCollapsed);
        dashboard.ToggleFilterPanelCommand.Execute(null);
        Assert.True(dashboard.IsFilterPanelCollapsed);
    }

    // ---- fix pass: option lists load off the UI thread (review item 1) ---------------------------

    [Fact]
    public async Task Construction_DoesNotWaitForTheFacetQueries()
    {
        // Both queries are full scans of the LIGHT frames. The panel is constructed on the UI
        // thread during host startup, so construction must return before they do.
        using var released = new ManualResetEventSlim(false);
        var panel = new FilterPanelViewModel(
            () => Map(),
            () =>
            {
                released.Wait();
                return new DashboardFacets([new FilterFacet("Ha", "#FF0000", 4)], ["ASI2600MM"], ["RC8"]);
            },
            () => ["OBJECT"],
            today: null,
            post: action => action());
        panel.Reload();

        // Construction has already returned while the facet query is still blocked.
        Assert.Empty(panel.Filters);
        Assert.Empty(panel.HeaderKeys);
        Assert.False(panel.PendingReload!.IsCompleted);

        released.Set();
        await panel.PendingReload!;

        Assert.Equal(["Ha"], panel.Filters.Select(pill => pill.Key));
        Assert.Equal(["OBJECT"], panel.HeaderKeys);
        Assert.Contains("ASI2600MM", panel.Cameras);
        Assert.Contains("RC8", panel.Telescopes);
    }

    // FIXER LIST F7: the reload guard reports through the injected logger, so a failed option-list
    // load is observable instead of being a silently swallowed exception.
    [Fact]
    public async Task Reload_WhenAQueryThrows_LogsThroughTheInjectedLogger_AndKeepsTheLists()
    {
        var logger = new RecordingLogger();
        var panel = new FilterPanelViewModel(
            () => Map(),
            () => throw new InvalidOperationException("database is locked"),
            () => [],
            today: null,
            post: action => action(),
            logger: logger);

        panel.Reload();
        await panel.PendingReload!;

        Assert.Empty(panel.Filters);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("could not load its option lists", warning.Message, StringComparison.Ordinal);
    }

    // Phase review item 8. Reload ran from the panel's own constructor, so its publish could raise
    // Changed before DashboardViewModel had attached a handler, and that event went nowhere. The
    // ordering is now structural: the panel starts nothing, and the owner reloads after wiring.
    [Fact]
    public void Construction_StartsNoReload_TheOwnerDoesItAfterWiringItsHandlers()
    {
        var panel = new FilterPanelViewModel(
            () => Map(),
            () => new DashboardFacets([], [], []),
            () => [],
            today: null,
            post: action => action());

        Assert.Null(panel.PendingReload);

        var dashboard = DashboardViewModelTestFactory.Create();
        Assert.NotNull(dashboard.Filters.PendingReload);
        dashboard.Dispose();
    }

    [Fact]
    public async Task Reload_PreservesSelectionsThatStillExist()
    {
        var filters = new List<FilterFacet> { new("Ha", "#FF0000", 4) };
        var panel = new FilterPanelViewModel(
            () => Map(),
            () => new DashboardFacets([.. filters], ["ASI2600MM"], []),
            () => [],
            today: null,
            post: action => action());
        panel.Reload();
        await panel.PendingReload!;

        panel.Filters.Single(pill => pill.Key == "Ha").IsSelected = true;
        panel.SelectedCamera = "ASI2600MM";

        filters.Add(new FilterFacet("OIII", "#00AAAA", 9));
        panel.Reload();
        await panel.PendingReload!;

        Assert.Equal(["Ha", "OIII"], panel.Filters.Select(pill => pill.Key));
        Assert.True(panel.Filters.Single(pill => pill.Key == "Ha").IsSelected);
        Assert.Equal("ASI2600MM", panel.BuildCriteria(Seed).Camera);
    }

    // ---- fix pass: post seam and stale responses (review item 2) ---------------------------------

    [Fact]
    public async Task Results_AreAppliedThroughTheInjectedPost()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var posted = new List<Action>();
        var dashboard = CreateDashboard(query, delay, post: posted.Add);
        await SettleAsync(dashboard, delay, posted);
        query.Clear();

        query.Page = new TargetListingPage([], 4, 3600d, 40, 1, 50);
        dashboard.Filters.ObjectTypes[0].IsSelected = true;
        await DrainAsync(dashboard, delay);

        // The query ran, but nothing reached the bindings: the write is inside a posted closure.
        Assert.Single(query.Calls);
        Assert.Equal(0, dashboard.TotalGroups);
        Assert.NotEmpty(posted);

        foreach (var action in posted.ToArray())
        {
            action();
        }

        Assert.Equal(4, dashboard.TotalGroups);
        Assert.Equal(1d, dashboard.TotalIntegrationHours, 3);
    }

    [Fact]
    public async Task StaleResponse_IsDiscarded_WhenANewerQueryHasAlreadyBeenApplied()
    {
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var posted = new List<Action>();
        var dashboard = CreateDashboard(query, delay, post: posted.Add);
        await SettleAsync(dashboard, delay, posted);

        // A completed query can post the refetch dim's SetRefetching(true) ahead of Apply itself
        // (spec 12.2, "The refetch dim"), or not, when it beats the dim's grace wait. Only Apply
        // matters to what this case proves, and it is always the last closure a query posts.
        //
        // The first query completes but its result is still sitting in the queue...
        query.Page = new TargetListingPage([], 1, 0d, 1, 1, 50);
        dashboard.Filters.ObjectTypes[0].IsSelected = true;
        await DrainAsync(dashboard, delay);
        Assert.NotEmpty(posted);
        var firstApply = posted[^1];
        posted.Clear();

        // ...when a second one is requested and completes.
        query.Page = new TargetListingPage([], 99, 0d, 99, 1, 50);
        dashboard.Filters.ObjectTypes[1].IsSelected = true;
        await DrainAsync(dashboard, delay);
        Assert.NotEmpty(posted);
        var secondApply = posted[^1];

        // Applying the newer one first, then the stale one, must not roll the figures back.
        secondApply();
        firstApply();

        Assert.Equal(99, dashboard.TotalGroups);
    }

    // ---- fix pass: refresh after a scan (review items 3 and 4) -----------------------------------

    [Fact]
    public async Task ScanFinished_ReloadsTheOptionListsAndRequeries()
    {
        // The only test here that touches a database: ScanCoordinator raises ScanFinished itself
        // and offers no seam to fake it, so this runs one real scan over an empty temp root, the
        // pattern ScanStatusServiceTests already uses.
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var status = new ScanStatusService(coordinator, action => action());

        var facetLoads = 0;
        var query = new RecordingListQuery();
        var delay = new FakeDelay();
        var dashboard = new DashboardViewModel(
            query.List,
            () =>
            {
                Interlocked.Increment(ref facetLoads);
                return new DashboardFacets([], [], []);
            },
            () => [],
            () => Map(),
            new GeneralSettings(),
            delay.Delay,
            scanStatus: status,
            post: action => action());
        await dashboard.Filters.PendingReload!;
        await DrainAsync(dashboard, delay);
        Assert.Equal(1, facetLoads);
        query.Clear();

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        await dashboard.Filters.PendingReload!;
        await DrainAsync(dashboard, delay);

        Assert.Equal(2, facetLoads);
        Assert.Single(query.Calls);
    }

    // ---- fix pass: query failure is logged and observable (review item 5) ------------------------

    [Fact]
    public async Task FailingQuery_SetsLastQueryFailure_AndRecoversOnTheNextSuccess()
    {
        // The Log.Warning beside the assignment is deliberately not asserted here: Serilog's
        // Log.Logger is process-global and AppHost's AddSerilog(dispose: true) disposes whatever
        // is installed at host-dispose time, so swapping it from a test that runs in parallel
        // with AppHostTests leaves that suite's own log file locked. The observable half is what
        // Task 8's banner binds to, and it is asserted.
        var boom = new InvalidOperationException("boom");
        var fail = true;
        var delay = new FakeDelay();
        var page = new TargetListingPage([], 6, 0d, 6, 1, 50);
        var dashboard = new DashboardViewModel(
            _ => fail ? throw boom : page,
            () => new DashboardFacets([], [], []),
            () => [],
            () => Map(),
            new GeneralSettings(),
            delay.Delay,
            post: action => action());
        await dashboard.Filters.PendingReload!;

        await RunToCompletionAsync(dashboard, delay);
        Assert.Same(boom, dashboard.LastQueryFailure);
        Assert.Equal(0, dashboard.TotalGroups);

        fail = false;
        dashboard.Filters.ObjectTypes[0].IsSelected = true;
        await RunToCompletionAsync(dashboard, delay);

        Assert.Null(dashboard.LastQueryFailure);
        Assert.Equal(6, dashboard.TotalGroups);

        // DrainAsync asserts LastQueryFailure is null, which is the point of this test.
        static async Task RunToCompletionAsync(DashboardViewModel dashboard, FakeDelay delay)
        {
            var work = dashboard.PendingQuery!;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!work.IsCompleted && DateTime.UtcNow < deadline)
            {
                delay.Release();
                await Task.Delay(2);
            }

            await work;
        }
    }

    // ---- fix pass: blank header value (review item 6) --------------------------------------------

    [Fact]
    public void AddHeaderCondition_RequiresBothAKeyAndAValue()
    {
        var panel = CreatePanel();

        Assert.False(panel.AddHeaderConditionCommand.CanExecute(null));

        panel.DraftHeaderKey = "OBJECT";
        Assert.False(panel.AddHeaderConditionCommand.CanExecute(null));

        panel.DraftHeaderValue = "   ";
        Assert.False(panel.AddHeaderConditionCommand.CanExecute(null));

        panel.DraftHeaderValue = "M 31";
        Assert.True(panel.AddHeaderConditionCommand.CanExecute(null));
    }

    // ---- fix pass: clearing a combo box and a date (review item 7) -------------------------------

    [Fact]
    public void Equipment_SelectingAny_YieldsNoCriterion()
    {
        var panel = CreatePanel(facets: new DashboardFacets([], ["ASI2600MM"], ["RC8"]));
        panel.SelectedCamera = "ASI2600MM";
        panel.SelectedTelescope = "RC8";
        Assert.True(panel.EquipmentSection.IsActive);

        panel.SelectedCamera = FilterPanelViewModel.AnyOption;
        panel.SelectedTelescope = FilterPanelViewModel.AnyOption;

        var criteria = panel.BuildCriteria(Seed);
        Assert.Null(criteria.Camera);
        Assert.Null(criteria.Telescope);
        Assert.False(panel.EquipmentSection.IsActive);
    }

    [Fact]
    public void DateRange_ClearCommands_UnsetEachBoundIndependently()
    {
        var panel = CreatePanel();
        panel.DateFrom = new DateTimeOffset(new DateTime(2024, 1, 1), TimeSpan.Zero);
        panel.DateTo = new DateTimeOffset(new DateTime(2024, 12, 31), TimeSpan.Zero);

        panel.ClearDateFromCommand.Execute(null);
        Assert.Null(panel.BuildCriteria(Seed).SessionDateFrom);
        Assert.Equal(new DateOnly(2024, 12, 31), panel.BuildCriteria(Seed).SessionDateTo);

        panel.ClearDateToCommand.Execute(null);
        Assert.Null(panel.BuildCriteria(Seed).SessionDateTo);
        Assert.False(panel.DateRangeSection.IsActive);
    }

    // ---- fix pass: search text is not a criterion (review item 9) --------------------------------

    [Fact]
    public void SearchText_DoesNotFireChanged_ButMovesItsMarkerAndSignalsTheDropdown()
    {
        var panel = CreatePanel();
        var changed = 0;
        var searchSignals = 0;
        panel.Changed += (_, _) => changed++;
        panel.SearchTextChanged += (_, _) => searchSignals++;

        panel.SearchText = "andromeda";

        // BuildCriteria never reads SearchText, so a keystroke must re-issue no listing query.
        Assert.Equal(0, changed);
        Assert.Equal(1, searchSignals);
        Assert.True(panel.SearchSection.IsActive);

        panel.ObjectTypes[0].IsSelected = true;
        Assert.Equal(1, changed);
    }

    // ---- shared helpers ------------------------------------------------------------------------

    private static MetricRangeViewModel Metric(FilterPanelViewModel panel, string key)
        => panel.MetricGroups.SelectMany(group => group.Metrics).Single(metric => metric.Key == key);

    private static void Add(FilterPanelViewModel panel, string key, string @operator, string value)
    {
        panel.DraftHeaderKey = key;
        panel.DraftHeaderOperator = @operator;
        panel.DraftHeaderValue = value;
        panel.AddHeaderConditionCommand.Execute(null);
    }

    // Gives exactly one section a value, by its spec key.
    private static void Activate(FilterPanelViewModel panel, string key)
    {
        switch (key)
        {
            case "search":
                panel.PinnedTargetId = Guid.NewGuid();
                break;
            case "object_type":
                panel.ObjectTypes[0].IsSelected = true;
                break;
            case "date_range":
                panel.DateFrom = new DateTimeOffset(new DateTime(2024, 1, 1), TimeSpan.Zero);
                break;
            case "filters":
                panel.Filters[0].IsSelected = true;
                break;
            case "equipment":
                panel.SelectedCamera = "ASI2600MM";
                break;
            case "metrics":
                Metric(panel, "hfr").Max = 3.0;
                break;
            case "header_query":
                Add(panel, "OBJECT", "=", "M 31");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown filter section.");
        }
    }

    // ---- Phase 14C, spec 12.2's collapsed strip labels (questions.md Q1) ---------------------

    [Fact]
    public void EverySection_CarriesAShortLabel_OfAtMostFourCharacters()
    {
        // Four characters is a measurement rather than a taste: carried fixer-list item 18 records
        // that the 48 pixel strip already clips the fifth character of a date at the Extra Large
        // text size.
        var panel = CreatePanel();

        Assert.All(panel.Sections, section =>
        {
            Assert.False(string.IsNullOrWhiteSpace(section.ShortLabel));
            Assert.True(
                section.ShortLabel.Length <= 4,
                $"{section.Key} carries the short label '{section.ShortLabel}', which is {section.ShortLabel.Length} characters.");
        });
    }

    [Fact]
    public void TheSevenShortLabels_AreDistinct()
    {
        var panel = CreatePanel();

        var labels = panel.Sections.Select(section => section.ShortLabel).ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            new[] { "Srch", "Type", "Date", "Filt", "Eqp", "Metr", "FITS" },
            labels);
    }

    // Phase 20, spec 12.15. The array above is deliberately NOT extended: CreatePanel builds a
    // panel with no custom column, so it still holds exactly seven. This is the eight-section
    // state, over a panel that has published one definition.
    [Fact]
    public async Task TheEighthShortLabel_IsCustAndIsDistinct()
    {
        var panel = new FilterPanelViewModel(
            () => Map(),
            () => new DashboardFacets([], [], []),
            () => [],
            today: null,
            post: action => action(),
            loadCustomColumns: () => [CustomColumnTestFactory.Boolean("Processed")]);
        panel.Reload();
        await panel.PendingReload!;

        var labels = panel.Sections.Select(section => section.ShortLabel).ToList();

        Assert.Equal(
            new[] { "Srch", "Type", "Date", "Filt", "Eqp", "Metr", "FITS", "Cust" },
            labels);
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());

        // Four characters is the strip's measured ceiling, not a taste: the 48 pixel strip clips
        // a fifth character at the Extra Large text size.
        Assert.True(panel.Sections[^1].ShortLabel.Length <= 4);
    }

    [Fact]
    public void EverySection_FullLabel_IsItsTitle()
    {
        // The shared strip template puts FullLabel on the item's tooltip, and a section's full
        // label is the heading the expanded panel already shows.
        var panel = CreatePanel();

        Assert.All(panel.Sections, section =>
        {
            Assert.Equal(section.Title, section.FullLabel);
            Assert.False(section.IsMonospace);
        });
    }
}
