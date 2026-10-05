using Avalonia.Headless.XUnit;
using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.7's Display tab and design-spec 18.3: delegates throughout, so every case runs
// with lambdas and no database.
public class DisplayTabViewModelTests
{
    private sealed class FakeStore
    {
        public GeneralSettings General { get; private set; } = new();

        public DisplaySettings Display { get; private set; } = new();

        public GraphSettings Graph { get; private set; } = new();

        public int GeneralSaves { get; private set; }

        public int DisplaySaves { get; private set; }

        public string? RefuseGeneralWith { get; set; }

        public bool DisplaySaveThrows { get; set; }

        public GeneralSettings GetGeneral() => General;

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate)
        {
            var next = mutate(General);
            if (RefuseGeneralWith is { } message)
            {
                throw new SettingsValidationException(message);
            }

            General = next;
            GeneralSaves++;
            return next;
        }

        public DisplaySettings GetDisplay() => Display;

        public void SaveDisplay(DisplaySettings value)
        {
            if (DisplaySaveThrows)
            {
                throw new InvalidOperationException("the display document is refused by this test");
            }

            Display = value;
            DisplaySaves++;
        }

        public GraphSettings GetGraph() => Graph;

        public void SaveGraph(GraphSettings value) => Graph = value;
    }

    private sealed record Harness(
        DisplayTabViewModel Tab,
        FakeStore Store,
        DisplayColumnWriter Columns,
        GraphSettingsWriter Graph,
        TargetListViewModel Dashboard);

    private static async Task<Harness> CreateAsync(
        GeneralSettings? general = null,
        DisplaySettings? display = null,
        GraphSettings? graph = null,
        Action<string>? applyTheme = null)
    {
        var store = new FakeStore();
        if (general is not null)
        {
            store.Mutate(_ => general);
        }

        if (display is not null)
        {
            store.SaveDisplay(display);
        }

        if (graph is not null)
        {
            store.SaveGraph(graph);
        }

        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        var graphWriter = new GraphSettingsWriter(store.GetGraph, store.SaveGraph);
        var dashboard = new TargetListViewModel(store.GetDisplay(), columns, store.GetGeneral().DefaultPageSize);

        var tab = new DisplayTabViewModel(
            store.GetGeneral,
            store.Mutate,
            store.GetDisplay,
            store.SaveDisplay,
            store.GetGraph,
            graphWriter,
            columns,
            () => ThemeManager.Available,
            applyTheme ?? (_ => { }),
            dashboardColumns: dashboard.Columns,
            toggleDashboardColumn: column => dashboard.ToggleColumnCommand.Execute(column),
            post: action => action());

        await Settle(tab, columns).ConfigureAwait(false);
        return new Harness(tab, store, columns, graphWriter, dashboard);
    }

    // Every background path this tab owns is awaited, never blocked on.
    private static async Task Settle(DisplayTabViewModel tab, DisplayColumnWriter columns)
    {
        if (tab.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        await tab.PendingWrite.ConfigureAwait(false);
        await columns.Pending.ConfigureAwait(false);
    }

    private static Task Settle(Harness harness) => Settle(harness.Tab, harness.Columns);

    // ---- theme -------------------------------------------------------------------------------

    [Fact]
    public async Task Theme_RoundTrips()
    {
        var applied = new List<string>();
        var harness = await CreateAsync(applyTheme: applied.Add);

        // Six themes ship, in picker order, civil-dusk first.
        Assert.Equal(
            ["civil-dusk", "luminance", "deep-sky", "red-light", "atlas", "logbook"],
            harness.Tab.Themes.Select(option => option.Id));
        Assert.Equal(
            ["Civil Dusk", "Luminance", "Deep Sky", "Red Light", "Atlas", "Logbook"],
            harness.Tab.Themes.Select(option => option.Label));

        // ThemeOption is a record, so re-selecting the same id is not a change and swaps nothing.
        var selected = harness.Tab.SelectedTheme!;
        harness.Tab.SelectedTheme = new ThemeOption(selected.Id, selected.Label);
        await Settle(harness);
        Assert.Empty(applied);

        // A real swap now has somewhere to go: selecting the second entry applies it and writes
        // it, which is the path the single-theme build could not exercise at all.
        harness.Tab.SelectedTheme = harness.Tab.Themes[1];
        await Settle(harness);

        Assert.Equal("luminance", harness.Store.General.Theme);
        Assert.Equal(["luminance"], applied);

        harness.Tab.SelectedTheme = harness.Tab.Themes[0];
        await Settle(harness);

        Assert.Equal("civil-dusk", harness.Store.General.Theme);
        Assert.Equal(["luminance", "civil-dusk"], applied);
    }

    [AvaloniaFact]
    public void Theme_Apply_CallsChartThemeApply_AndRaisesChanged()
    {
        // FIXER item 8, ruling Q19. The swap replaces the merged dictionary and the theme variant
        // and then re-reads the chart palette, because the paints LiveCharts2 holds are values
        // rather than DynamicResource lookups and nothing else re-reads them. Asserted for the one
        // shipped theme, which is what makes the second theme a dictionary file rather than a
        // debugging session.
        var raised = 0;
        void OnChanged(object? sender, EventArgs e) => raised++;

        ChartTheme.Changed += OnChanged;
        try
        {
            ThemeManager.Apply("deep-sky");
        }
        finally
        {
            ChartTheme.Changed -= OnChanged;
        }

        Assert.Equal(1, raised);
    }

    // Ruling Q13: no migration, a stored id wins, and an id this build does not ship falls back to
    // Table[0], which is civil-dusk.
    [AvaloniaFact]
    public void Theme_Apply_WithAnUnknownId_FallsBackToTheShippedTheme()
    {
        var raised = 0;
        void OnChanged(object? sender, EventArgs e) => raised++;

        ChartTheme.Changed += OnChanged;
        try
        {
            ThemeManager.Apply("no-such-theme");
        }
        finally
        {
            ChartTheme.Changed -= OnChanged;
        }

        Assert.Equal(1, raised);
        Assert.Equal(["civil-dusk", "luminance", "deep-sky", "red-light", "atlas", "logbook"], ThemeManager.Available);
    }

    // ---- text size, content width, page size, chart sessions ---------------------------------

    [Fact]
    public async Task TextSize_RoundTrips()
    {
        var harness = await CreateAsync();

        // The fresh default is small, so the round trip has to move away from it to be a change
        // at all.
        Assert.Equal("small", harness.Tab.SelectedTextSize.Id);

        harness.Tab.SelectedTextSize = DisplayTabViewModel.TextSizes.Single(option => option.Id == "large");
        await Settle(harness);

        Assert.Equal("large", harness.Store.General.TextSize);
    }

    [Theory]
    [InlineData("small", 14d)]
    [InlineData("medium", 16d)]
    [InlineData("large", 18d)]
    [InlineData("x-large", 20d)]
    public void TextSize_MapsToTheFourSpecPixelValues(string id, double pixels)
    {
        // Design-spec 14.4: Small 14 (default), Medium 16, Large 18, Extra large 20.
        Assert.Equal(pixels, DisplayTabViewModel.TextSizes.Single(option => option.Id == id).Pixels);
        Assert.Equal(pixels, MainWindowViewModel.ResolveRootFontSize(id));
    }

    [Fact]
    public void TextSize_UnknownValue_FallsBackToFourteen()
        => Assert.Equal(14d, MainWindowViewModel.ResolveRootFontSize("gargantuan"));

    [Theory]
    [InlineData("normal")]
    [InlineData("wide")]
    [InlineData("extra-wide")]
    public async Task ContentWidth_RoundTrips_OverTheThreeSpecValues(string id)
    {
        var harness = await CreateAsync();

        harness.Tab.SelectedContentWidth = DisplayTabViewModel.ContentWidths.Single(option => option.Id == id);
        await Settle(harness);

        Assert.Equal(id, harness.Store.General.ContentWidth);
    }

    [Fact]
    public async Task DefaultPageSize_RoundTrips()
    {
        var harness = await CreateAsync();

        // Spec 5.8.1's four choices (fix pass: the web's larger PAGE_SIZES list, which included
        // 10, is no longer a second list of its own; see DefaultPageSizeLists_AreTheOneList).
        Assert.Equal([25, 50, 100, 250], DisplayTabViewModel.PageSizes);
        Assert.Equal(4, harness.Tab.PageSizeOptions.Count);

        harness.Tab.SelectedPageSize = harness.Tab.PageSizeOptions.Single(option => option.Size == 100);
        await Settle(harness);

        Assert.Equal(100, harness.Store.General.DefaultPageSize);
    }

    // Fix pass (task4-review.md P2, "RULED, one list"): DisplayTabViewModel.PageSizes used to be
    // its own literal that still offered 10, spec 5.8.1's amended list does not, and this tab
    // wrote a stored 10 back unnormalized. There is now one declaration,
    // TargetListViewModel.PageSizeOptions, which this test pins by reference so a second literal
    // list cannot quietly reappear here.
    [Fact]
    public void DefaultPageSizeLists_AreTheOneList()
    {
        Assert.Same(TargetListViewModel.PageSizeOptions, DisplayTabViewModel.PageSizes);
    }

    [Fact]
    public async Task DefaultPageSize_AStoredValueOutsideTheList_ReadsAsFifty()
    {
        // Fix pass: a stored 37 used to be appended to PageSizeOptions as its own selectable
        // entry ("offered rather than snapped"). Spec 5.8.1 says an unlisted value reads as the
        // default, 50, the same rule the pager's own select already applies through
        // TargetListViewModel.NormalizePageSize; two surfaces reading the same key must agree.
        var harness = await CreateAsync(new GeneralSettings { DefaultPageSize = 37 });

        Assert.Equal(4, harness.Tab.PageSizeOptions.Count);
        var stored = harness.Tab.PageSizeOptions.Single(option => option.Size == 50);
        Assert.Same(stored, harness.Tab.SelectedPageSize);

        // Seeding the tab wrote nothing of its own: the only save is the harness's own seed.
        Assert.Equal(1, harness.Store.GeneralSaves);
    }

    // Fix pass, task4-review.md P2 escalation: a stored value outside the list must read as 50
    // through both writers of general.default_page_size, not only the Display tab. Built the same
    // way AppHost wires the pager, over the same stored document the Display tab reads.
    //
    // The pager's own SelectedPageSize normalizes at construction (escalation 2's ruled
    // behaviour); PageSize does not, by the same ruling, so a freshly built pager still pages by
    // the raw stored figure the instant after construction. What actually pages by 50 is the
    // application's normal reconciliation path, the same GeneralChanged subscription that
    // follows every other write of the key: AppHost.FollowDefaultPageSize, exercised here the way
    // it runs in production rather than by asserting the constructor did work the ruling says it
    // does not.
    [Fact]
    public async Task AStoredValueOutsideTheList_ReadsAsFiftyInBothTheTabAndThePager_AndPagesByFifty()
    {
        var harness = await CreateAsync(new GeneralSettings { DefaultPageSize = 10 });

        var tabSelection = harness.Tab.PageSizeOptions.Single(option => option.Size == 50);
        Assert.Same(tabSelection, harness.Tab.SelectedPageSize);

        var pager = new TargetListViewModel(
            harness.Store.GetDisplay(),
            harness.Columns,
            harness.Store.GetGeneral().DefaultPageSize,
            size => harness.Store.Mutate(general => general with { DefaultPageSize = size }));

        Assert.Equal(50, pager.SelectedPageSize);

        AppHost.FollowDefaultPageSize(pager, harness.Store.General.DefaultPageSize, action => action());

        Assert.Equal(50, pager.SelectedPageSize);
        Assert.Equal(50, pager.PageSize);
    }

    // Phase 14C Task 4: general.default_page_size now has two writers, this tab and the
    // dashboard pager's page-size select. Both must reach the same document through the same
    // MutateGeneral seam rather than two chains that could lose one another's write, which is
    // questions.md Q6's whole point.
    [Fact]
    public async Task TheDashboardSelectAndTheDisplayTab_WriteTheSameKey()
    {
        var harness = await CreateAsync();

        // Built the same way AppHost wires the pager: the one door into the document,
        // MutateGeneral, closed over as an Action<int>.
        var pager = new TargetListViewModel(
            harness.Store.GetDisplay(),
            harness.Columns,
            harness.Store.GetGeneral().DefaultPageSize,
            size => harness.Store.Mutate(general => general with { DefaultPageSize = size }));

        pager.SelectedPageSize = 100;
        Assert.Equal(100, harness.Store.General.DefaultPageSize);

        harness.Tab.SelectedPageSize = harness.Tab.PageSizeOptions.Single(option => option.Size == 25);
        await Settle(harness);
        Assert.Equal(25, harness.Store.General.DefaultPageSize);

        // The pager reads the tab's write back through the same document, exactly what
        // AppHost.FollowDefaultPageSize does in production. Fix pass (task4-review.md P2):
        // FollowDefaultPageSize used to assign PageSize alone, so the select kept showing the
        // figure from before the Display tab's own change; both members are asserted here so
        // that gap cannot reopen silently.
        Assert.Equal(100, pager.PageSize);
        Assert.Equal(100, pager.SelectedPageSize);
        AppHost.FollowDefaultPageSize(pager, harness.Store.General.DefaultPageSize, action => action());
        Assert.Equal(25, pager.PageSize);
        Assert.Equal(25, pager.SelectedPageSize);
    }

    [Fact]
    public async Task DefaultChartSessions_RoundTrips()
    {
        var harness = await CreateAsync();

        // Questions.md Q23: the shipped default stays 1, and 0 is deliberately not offered.
        Assert.Equal(1, harness.Tab.SelectedChartSessions.Sessions);
        Assert.Equal([1, 3, 5, 10, 20], DisplayTabViewModel.ChartSessions.Select(option => option.Sessions));
        Assert.DoesNotContain(DisplayTabViewModel.ChartSessions, option => option.Sessions == 0);

        harness.Tab.SelectedChartSessions = DisplayTabViewModel.ChartSessions.Single(option => option.Sessions == 20);
        await harness.Graph.Pending;

        Assert.Equal(20, harness.Store.Graph.DefaultChartSessions);
    }

    // ---- metric groups -----------------------------------------------------------------------

    [Fact]
    public async Task MetricGroups_DefaultToTheSpecDocument()
    {
        var harness = await CreateAsync();

        Assert.Equal(
            ["quality", "guiding", "adu", "focuser", "weather", "mount"],
            harness.Tab.Groups.Select(group => group.Key));

        // Design-spec 5.8.2: quality and guiding enabled, the other four disabled, every field
        // within each group true.
        Assert.True(harness.Tab.Groups.Single(group => group.Key == "quality").IsEnabled);
        Assert.True(harness.Tab.Groups.Single(group => group.Key == "guiding").IsEnabled);
        foreach (var key in new[] { "adu", "focuser", "weather", "mount" })
        {
            Assert.False(harness.Tab.Groups.Single(group => group.Key == key).IsEnabled);
        }

        Assert.All(harness.Tab.Groups, group => Assert.All(group.Fields, field => Assert.True(field.IsChecked)));
    }

    [Fact]
    public async Task MetricGroups_AreSavedOnlyWhenSaveIsPressed()
    {
        var harness = await CreateAsync();
        var before = harness.Store.DisplaySaves;

        harness.Tab.Groups.Single(group => group.Key == "weather").IsEnabled = true;
        Assert.True(harness.Tab.GroupsDirty);
        Assert.Equal(before, harness.Store.DisplaySaves);

        await harness.Tab.SaveGroupsCommand.ExecuteAsync(null);
        await Settle(harness);

        Assert.False(harness.Tab.GroupsDirty);
        Assert.True(harness.Store.Display.Groups["weather"].Enabled);
    }

    [Fact]
    public async Task MetricGroups_Revert_RestoresTheStoredDocument()
    {
        var harness = await CreateAsync();

        harness.Tab.Groups.Single(group => group.Key == "quality").IsEnabled = false;
        Assert.True(harness.Tab.GroupsDirty);

        harness.Tab.RevertGroupsCommand.Execute(null);

        Assert.False(harness.Tab.GroupsDirty);
        Assert.True(harness.Tab.Groups.Single(group => group.Key == "quality").IsEnabled);
    }

    [Fact]
    public async Task MetricGroups_DisablingAGroup_DimsAndDisablesItsFields()
    {
        var harness = await CreateAsync();
        var quality = harness.Tab.Groups.Single(group => group.Key == "quality");

        quality.IsEnabled = false;

        // The checkbox is disabled (and so dimmed by the control's own disabled state); the stored
        // field flags are untouched, which is design-spec 5.8.2's own separation.
        Assert.All(quality.Fields, field => Assert.False(field.IsGroupEnabled));
        Assert.All(quality.Fields, field => Assert.True(field.IsChecked));
    }

    [Theory]
    [InlineData("quality", "hfr", "HFR")]
    [InlineData("quality", "hfr_stdev", "HFR StDev")]
    [InlineData("quality", "fwhm", "FWHM")]
    [InlineData("quality", "eccentricity", "Eccentricity")]
    [InlineData("quality", "detected_stars", "Detected Stars")]
    [InlineData("guiding", "rms_total", "RMS Total")]
    [InlineData("guiding", "rms_ra", "RMS RA")]
    [InlineData("guiding", "rms_dec", "RMS Dec")]
    [InlineData("adu", "mean", "Mean")]
    [InlineData("adu", "median", "Median")]
    [InlineData("adu", "stdev", "StDev")]
    [InlineData("adu", "min", "Min")]
    [InlineData("adu", "max", "Max")]
    [InlineData("focuser", "position", "Position")]
    [InlineData("focuser", "temp", "Temperature")]
    [InlineData("weather", "ambient_temp", "Temperature")]
    [InlineData("weather", "dew_point", "Dew Point")]
    [InlineData("weather", "humidity", "Humidity")]
    [InlineData("weather", "pressure", "Pressure")]
    [InlineData("weather", "wind_speed", "Wind Speed")]
    [InlineData("weather", "wind_direction", "Wind Direction")]
    [InlineData("weather", "wind_gust", "Wind Gust")]
    [InlineData("weather", "cloud_cover", "Cloud Cover")]
    [InlineData("weather", "sky_quality", "Sky Quality")]
    [InlineData("mount", "airmass", "Airmass")]
    [InlineData("mount", "pier_side", "Pier Side")]
    [InlineData("mount", "rotator_position", "Rotator Position")]
    public void MetricGroups_FieldLabels_MatchTheWebStrings(string group, string field, string label)
    {
        var definition = DisplayMetricGroupViewModel.Definitions.Single(entry => entry.Key == group);
        Assert.Equal(label, definition.Fields.Single(entry => entry.Key == field).Label);
    }

    [Fact]
    public void MetricGroups_TheVocabularyIsTheOneFrameColumnsJoins()
    {
        // Design-spec 5.8.2: FrameColumns is the only join between the column vocabulary and the
        // groups vocabulary, so every gated column's (group, field) pair has to exist here.
        var pairs = DisplayMetricGroupViewModel.Definitions
            .SelectMany(group => group.Fields.Select(field => (group.Key, field.Key)))
            .ToHashSet();

        Assert.Equal(27, pairs.Count);
        foreach (var column in FrameColumns.All.Where(column => column.Group is not null))
        {
            Assert.Contains((column.Group!, column.Field!), pairs);
        }
    }

    [Fact]
    public async Task MetricGroups_HfrStdev_RendersACheckboxThatGatesNothing()
    {
        var harness = await CreateAsync();

        // Design-spec 5.8.2's own note, asserted so the behaviour reads as deliberate rather than
        // as a defect: quality.fields.hfr_stdev has a checkbox and gates no column, because
        // design-spec 12.4's frame table has no HFR sigma column and neither does the web.
        var quality = harness.Tab.Groups.Single(group => group.Key == "quality");
        Assert.Contains(quality.Fields, field => field.Key == "hfr_stdev");
        Assert.DoesNotContain(FrameColumns.All, column => column.Field == "hfr_stdev");
    }

    // ---- the column pickers ------------------------------------------------------------------

    [Fact]
    public async Task ColumnPicker_Dashboard_WritesThroughDisplayColumnWriter()
    {
        var harness = await CreateAsync();
        var picker = harness.Tab.DashboardColumns!;

        picker.ToggleCommand.Execute(picker.Columns.Single(column => column.Key == "palette"));
        await Settle(harness);

        Assert.DoesNotContain("palette", harness.Store.Display.Columns[DisplaySettings.DashboardTableId]);
        Assert.Equal(
            harness.Columns.LastWritten(DisplaySettings.DashboardTableId),
            harness.Store.Display.Columns[DisplaySettings.DashboardTableId]);

        // One dashboard column state in the process: the Settings picker and the in-header picker
        // are the same rows, so there is nothing to keep in step.
        Assert.False(harness.Dashboard.Columns.Single(column => column.Key == "palette").IsVisible);
    }

    [Fact]
    public async Task ColumnPicker_Frames_WritesThroughDisplayColumnWriter()
    {
        var harness = await CreateAsync();
        var picker = harness.Tab.FramesColumns;

        picker.ToggleCommand.Execute(picker.Columns.Single(column => column.Key == "fwhm"));
        await Settle(harness);

        Assert.DoesNotContain("fwhm", harness.Store.Display.Columns[DisplaySettings.FramesTableId]);
    }

    [Fact]
    public async Task ColumnPicker_Dashboard_NameColumnCannotBeHidden()
    {
        var harness = await CreateAsync();
        var picker = harness.Tab.DashboardColumns!;
        var name = picker.Columns.Single(column => column.Key == "name");

        Assert.False(name.CanHide);
        picker.ToggleCommand.Execute(name);
        await Settle(harness);

        Assert.True(name.IsVisible);
    }

    [Fact]
    public async Task ColumnPicker_Frames_EmptyList_IsALegalState_AndDoesNotThrow()
    {
        var harness = await CreateAsync();
        var picker = harness.Tab.FramesColumns;

        foreach (var column in picker.Columns.Where(column => column.IsVisible && column.IsGroupEnabled).ToArray())
        {
            picker.ToggleCommand.Execute(column);
        }

        await Settle(harness);

        // Design-spec 5.8.2: an empty columns.frames array is a legal state that must not throw.
        Assert.Empty(harness.Store.Display.Columns[DisplaySettings.FramesTableId]);
        Assert.DoesNotContain(picker.Columns, column => column.IsVisible);
    }

    [Fact]
    public async Task ColumnPicker_ReflectsTheGroupGate()
    {
        var harness = await CreateAsync();
        var picker = harness.Tab.FramesColumns;

        // adu ships disabled, so every adu column is gated off on a fresh document.
        Assert.False(picker.Columns.Single(column => column.Key == "adu_mean").IsGroupEnabled);
        Assert.True(picker.Columns.Single(column => column.Key == "median_hfr").IsGroupEnabled);

        harness.Tab.Groups.Single(group => group.Key == "quality").IsEnabled = false;
        await harness.Tab.SaveGroupsCommand.ExecuteAsync(null);
        await Settle(harness);

        Assert.False(picker.Columns.Single(column => column.Key == "median_hfr").IsGroupEnabled);
    }

    [Fact]
    public async Task ColumnPicker_Frames_AGatedColumnCannotBeToggled()
    {
        var harness = await CreateAsync();
        var picker = harness.Tab.FramesColumns;
        var gated = picker.Columns.Single(column => column.Key == "adu_mean");

        picker.ToggleCommand.Execute(gated);
        await Settle(harness);

        Assert.False(gated.IsVisible);
    }

    // ---- save shapes ---------------------------------------------------------------------

    [Fact]
    public async Task EverythingExceptMetricGroups_SavesImmediately()
    {
        var harness = await CreateAsync();

        harness.Tab.SelectedTextSize = DisplayTabViewModel.TextSizes.Single(option => option.Id == "medium");
        await Settle(harness);
        harness.Tab.SelectedContentWidth = DisplayTabViewModel.ContentWidths.Single(option => option.Id == "wide");
        await Settle(harness);
        harness.Tab.SelectedPageSize = harness.Tab.PageSizeOptions.Single(option => option.Size == 25);
        await Settle(harness);

        Assert.Equal("medium", harness.Store.General.TextSize);
        Assert.Equal("wide", harness.Store.General.ContentWidth);
        Assert.Equal(25, harness.Store.General.DefaultPageSize);

        // And the metric groups did not go with them.
        harness.Tab.Groups.Single(group => group.Key == "mount").IsEnabled = true;
        await Settle(harness);
        Assert.False(harness.Store.Display.Groups["mount"].Enabled);
    }

    [Fact]
    public async Task AFailedImmediateSave_RollsTheControlBack()
    {
        var harness = await CreateAsync();
        harness.Store.RefuseGeneralWith = "general.text_size is refused by this test";

        harness.Tab.SelectedTextSize = DisplayTabViewModel.TextSizes.Single(option => option.Id == "large");
        await Settle(harness);

        Assert.Equal("small", harness.Tab.SelectedTextSize.Id);
        Assert.Equal("general.text_size is refused by this test", harness.Tab.ErrorMessage);
    }

    [Fact]
    public async Task AFailedGroupsSave_ReportsAndKeepsTheEdit()
    {
        var harness = await CreateAsync();
        harness.Store.DisplaySaveThrows = true;

        harness.Tab.Groups.Single(group => group.Key == "mount").IsEnabled = true;
        await harness.Tab.SaveGroupsCommand.ExecuteAsync(null);
        await Settle(harness);

        Assert.True(harness.Tab.GroupsDirty);
        Assert.True(harness.Tab.Groups.Single(group => group.Key == "mount").IsEnabled);
        Assert.NotNull(harness.Tab.ErrorMessage);
    }

    [Fact]
    public async Task ADisplaySaveElsewhere_ReGatesWithoutReloadingTheColumnsTwice()
    {
        var store = new FakeStore();
        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        EventHandler<DisplaySettings>? displaySubscriber = null;

        var tab = new DisplayTabViewModel(
            store.GetGeneral,
            store.Mutate,
            store.GetDisplay,
            store.SaveDisplay,
            store.GetGraph,
            new GraphSettingsWriter(store.GetGraph, store.SaveGraph),
            columns,
            () => ThemeManager.Available,
            _ => { },
            post: action => action(),
            subscribeDisplayChanged: handler => displaySubscriber += handler,
            unsubscribeDisplayChanged: handler => displaySubscriber -= handler);

        await Settle(tab, columns);

        // Another writer turns the ADU group on.
        var next = store.Display with
        {
            Groups = new Dictionary<string, MetricGroupSettings>(store.Display.Groups)
            {
                ["adu"] = new MetricGroupSettings(true, store.Display.Groups["adu"].Fields),
            },
        };
        displaySubscriber!.Invoke(this, next);

        Assert.True(tab.FramesColumns.Columns.Single(column => column.Key == "adu_mean").IsGroupEnabled);
        Assert.True(tab.Groups.Single(group => group.Key == "adu").IsEnabled);

        tab.Dispose();
        Assert.Null(displaySubscriber);
    }

    [Fact]
    public async Task OneColumnClick_IsNotAppliedTwice()
    {
        // FIXER item 7's reviewer focus: DisplayColumnWriter.Write raises Changed and its queued
        // SaveDisplay raises DisplayChanged, so a subscriber to both sees two notifications for
        // one click. The split is the columns half from Changed and the groups half from
        // DisplayChanged, so the click lands once.
        var store = new FakeStore();
        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        EventHandler<DisplaySettings>? displaySubscriber = null;

        var tab = new DisplayTabViewModel(
            store.GetGeneral,
            store.Mutate,
            store.GetDisplay,
            store.SaveDisplay,
            store.GetGraph,
            new GraphSettingsWriter(store.GetGraph, store.SaveGraph),
            columns,
            () => ThemeManager.Available,
            _ => { },
            post: action => action(),
            subscribeDisplayChanged: handler => displaySubscriber += handler,
            unsubscribeDisplayChanged: handler => displaySubscriber -= handler);

        await Settle(tab, columns);

        var picker = tab.FramesColumns;
        var column = picker.Columns.Single(entry => entry.Key == "fwhm");
        Assert.True(column.IsVisible);

        picker.ToggleCommand.Execute(column);
        await Settle(tab, columns);
        displaySubscriber!.Invoke(this, store.Display);

        // Hidden once, not hidden and then shown again.
        Assert.False(column.IsVisible);
        Assert.DoesNotContain("fwhm", store.Display.Columns[DisplaySettings.FramesTableId]);

        tab.Dispose();
    }

    [Fact]
    public async Task OneColumnClick_DoesNotRebuildTheGroups()
    {
        // Task 6 review finding 3. DisplayChanged carries the whole document, but this tab takes
        // only the groups half from it; the columns half is DisplayColumnWriter.Changed's. Applying
        // both would rebuild the six group rows on a change that never touched them, and would
        // discard whatever per-group state they carry. The expander state is that state today.
        var store = new FakeStore();
        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        EventHandler<DisplaySettings>? displaySubscriber = null;

        var tab = new DisplayTabViewModel(
            store.GetGeneral,
            store.Mutate,
            store.GetDisplay,
            store.SaveDisplay,
            store.GetGraph,
            new GraphSettingsWriter(store.GetGraph, store.SaveGraph),
            columns,
            () => ThemeManager.Available,
            _ => { },
            post: action => action(),
            subscribeDisplayChanged: handler => displaySubscriber += handler,
            unsubscribeDisplayChanged: handler => displaySubscriber -= handler);

        await Settle(tab, columns);

        var groupsBefore = tab.Groups.ToArray();
        tab.Groups.Single(group => group.Key == "weather").IsExpanded = false;

        var picker = tab.FramesColumns;
        picker.ToggleCommand.Execute(picker.Columns.Single(entry => entry.Key == "fwhm"));
        await Settle(tab, columns);
        displaySubscriber!.Invoke(this, store.Display);

        // Same row objects, and the one piece of per-group state on them survived.
        Assert.Equal(groupsBefore, tab.Groups);
        Assert.False(tab.Groups.Single(group => group.Key == "weather").IsExpanded);
        Assert.False(tab.GroupsDirty);

        tab.Dispose();
    }

    [Fact]
    public async Task AGroupsSaveElsewhere_RebuildsTheGroupsButNotTheColumns()
    {
        var store = new FakeStore();
        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        EventHandler<DisplaySettings>? displaySubscriber = null;

        var tab = new DisplayTabViewModel(
            store.GetGeneral,
            store.Mutate,
            store.GetDisplay,
            store.SaveDisplay,
            store.GetGraph,
            new GraphSettingsWriter(store.GetGraph, store.SaveGraph),
            columns,
            () => ThemeManager.Available,
            _ => { },
            post: action => action(),
            subscribeDisplayChanged: handler => displaySubscriber += handler,
            unsubscribeDisplayChanged: handler => displaySubscriber -= handler);

        await Settle(tab, columns);

        // A column hidden in this process, which lives in the writer rather than in the document
        // the event below carries.
        var picker = tab.FramesColumns;
        picker.ToggleCommand.Execute(picker.Columns.Single(entry => entry.Key == "fwhm"));
        await Settle(tab, columns);

        // Another writer turns a group on, carrying a document whose columns half is whatever it
        // read. The groups half lands; the columns half is left to the writer's own event.
        var next = store.Display with
        {
            Groups = new Dictionary<string, MetricGroupSettings>(store.Display.Groups)
            {
                ["mount"] = new MetricGroupSettings(true, store.Display.Groups["mount"].Fields),
            },
            Columns = new Dictionary<string, string[]>(store.Display.Columns)
            {
                [DisplaySettings.FramesTableId] = ["time", "fwhm"],
            },
        };
        displaySubscriber!.Invoke(this, next);

        Assert.True(tab.Groups.Single(group => group.Key == "mount").IsEnabled);
        Assert.False(picker.Columns.Single(entry => entry.Key == "fwhm").IsVisible);

        tab.Dispose();
    }

    [Fact]
    public async Task TheConstructorPublishesOnce_UnderAnInlinePostSeamOnPoolThreads()
    {
        // Task 6 review finding 1, the regression this fix pass exists for. The tab used to start
        // two background reads that both published; under an inline post seam they ran on two pool
        // threads at once, the base class's applying guard was left stuck true by the interleave,
        // and every later save on the tab was silently dropped. The failure was intermittent, so
        // this runs the whole construct-and-round-trip cycle repeatedly rather than once.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var harness = await CreateAsync();

            harness.Tab.SelectedContentWidth =
                DisplayTabViewModel.ContentWidths.Single(option => option.Id == "wide");
            await Settle(harness);

            Assert.True(harness.Tab.IsReady, $"attempt {attempt}: the tab never became ready");
            Assert.Equal("wide", harness.Store.General.ContentWidth);

            // The other half of the symptom: the display document has to have landed too, in the
            // same publish rather than in a second one that raced it.
            Assert.Equal(6, harness.Tab.Groups.Count);
            Assert.Equal(1, harness.Tab.SelectedChartSessions.Sessions);

            harness.Tab.Dispose();
        }
    }

    [Fact]
    public async Task TwoConstructorsInParallel_EachRoundTripsIndependently()
    {
        // The same race from the other direction: several tabs constructing at once over an inline
        // post seam, which is what a suite running these cases back to back produces. Each tab
        // owns one background pass, so none of them can leave another's guard set.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var tabs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CreateAsync()));

            foreach (var harness in tabs)
            {
                harness.Tab.SelectedTextSize =
                    DisplayTabViewModel.TextSizes.Single(option => option.Id == "large");
                await Settle(harness);
                Assert.Equal("large", harness.Store.General.TextSize);
                harness.Tab.Dispose();
            }
        }
    }
}
