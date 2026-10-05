using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;
using LibraryFactory = GalactiLog.App.Tests.TestSupport.LibraryTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's headless smoke tests for the Filters and Equipment tabs (Phase 9 Task 7):
// they parse, lay out, and bind against a populated view-model. Every view-model here is built
// with plain lambdas over in-memory state, no database, the rule every Settings tab test in this
// phase follows.
public class AliasTabViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static FiltersTabViewModel BuildFilters(
        Dictionary<string, FilterSetting>? filters = null,
        IReadOnlyList<(string Name, int Count)>? discovered = null)
    {
        var stored = filters ?? [];
        var dismissed = new List<List<string>>();
        var vm = new FiltersTabViewModel(
            () => stored,
            value => stored = value,
            () => dismissed,
            value => dismissed = value,
            () => discovered ?? [],
            post: action => action());
        vm.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        return vm;
    }

    private static EquipmentTabViewModel BuildEquipment(
        EquipmentSettings? equipment = null,
        IReadOnlyList<(string Name, int Count)>? cameras = null,
        IReadOnlyList<(string Name, int Count)>? telescopes = null)
    {
        var stored = equipment ?? new EquipmentSettings();
        var dismissed = new List<List<string>>();
        var vm = new EquipmentTabViewModel(
            () => stored,
            value => stored = value,
            () => dismissed,
            value => dismissed = value,
            () => cameras ?? [],
            () => telescopes ?? [],
            post: action => action());
        vm.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        return vm;
    }

    [AvaloniaFact]
    public void FiltersTabView_Constructs_AndLaysOut()
    {
        using var vm = BuildFilters(
            filters: new Dictionary<string, FilterSetting> { ["Ha"] = new FilterSetting { Color = "#ff0000" } },
            discovered: [("Ha", 5), ("OIII", 3)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        Assert.Contains(view.GetVisualDescendants(), visual => visual is GroupingEditorView);
    }

    [AvaloniaFact]
    public void FiltersTabView_RendersTheCanonicalFilterTable_WithTheStoredSwatchColour()
    {
        using var vm = BuildFilters(
            filters: new Dictionary<string, FilterSetting> { ["Ha"] = new FilterSetting { Color = "#ff0000" } },
            discovered: [("Ha", 5)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains("Ha", texts);

        var swatchColour = Color.Parse("#ff0000");
        Assert.Contains(
            view.GetVisualDescendants().OfType<Border>(),
            border => border.Background is ISolidColorBrush brush && brush.Color == swatchColour);
    }

    [AvaloniaFact]
    public void FiltersTabView_RendersSuggestionsAndAddRow()
    {
        using var vm = BuildFilters(discovered: [("Ha", 5), ("ha", 3)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains("Found 1 possible duplicate", texts);

        var buttons = view.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string ?? "").ToList();
        Assert.Contains("Merge", buttons);
        Assert.Contains("Dismiss", buttons);
        Assert.Contains("Add Filter", buttons);
    }

    [AvaloniaFact]
    public void FiltersTabView_TheSelectedPill_HighlightsDifferentlyFromAnUnselectedOne()
    {
        // Fix pass I1: an inline Background attribute on the pill Button outranked the
        // "Button.pill.selected" style setter, so the chosen canonical pill never actually
        // highlighted. This proves the two pills' rendered backgrounds now genuinely differ.
        //
        // P12 Task 5 lifted Button.pill out of this view and two others into
        // Theme/Controls.axaml, where the selected fill sits on the presenter rather than on the
        // Button: the flat default fills every Button's presenter with Transparent, which would
        // beat a TemplateBinding from a Button level setter. The rendered fill is therefore read
        // off the presenter here. The hazard the case exists for is gone at the root, because no
        // pill carries an inline Background attribute anywhere any more, and the assertion is
        // still that the two pills paint differently.
        using var vm = BuildFilters(discovered: [("Ha", 5), ("ha", 3)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var pills = view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("pill"))
            .ToList();
        Assert.Equal(2, pills.Count);

        var colours = pills
            .Select(button => button.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .Select(presenter => presenter.Background)
                .OfType<ISolidColorBrush>()
                .Select(brush => (Color?)brush.Color)
                .FirstOrDefault())
            .ToList();
        Assert.All(colours, colour => Assert.NotNull(colour));
        Assert.NotEqual(colours[0], colours[1]);

        // And the lit pill reads in the primary ink over the emphasis outline, which is the
        // direction's rule that light carries state while colour carries data.
        Assert.NotEqual(pills[0].Foreground, pills[1].Foreground);
    }

    [AvaloniaFact]
    public void EquipmentTabView_Constructs_AndLaysOut()
    {
        using var vm = BuildEquipment(
            equipment: new EquipmentSettings
            {
                Cameras = new Dictionary<string, EquipmentItemSettings> { ["ASI2600MM"] = new EquipmentItemSettings() },
            },
            cameras: [("ASI2600MM", 10)],
            telescopes: [("RC8", 4)]);
        var view = new EquipmentTabView { DataContext = vm };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        // Two independent GroupingEditorView regions, cameras and telescopes.
        Assert.Equal(2, view.GetVisualDescendants().OfType<GroupingEditorView>().Count());

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains("Cameras", texts);
        Assert.Contains("Telescopes", texts);
        Assert.Contains("ASI2600MM", texts);
        Assert.Contains("RC8", texts);
    }

    [AvaloniaFact]
    public void SettingsPage_SelectingFiltersAndEquipment_ShowsTheRightView()
    {
        // SettingsViewModel's primary constructor takes one factory per real tab; Library and
        // Targets need real view-models to build it at all, so this uses the same two test
        // factories SettingsViewTests.cs already uses for the same reason.
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var filtersVm = BuildFilters();
        using var equipmentVm = BuildEquipment();

        // Phase 9 Task 8: the primary constructor takes a Maintenance factory too. Delegates that
        // do nothing; this case is about the filters and equipment templates.
        using var maintenanceVm = new MaintenanceTabViewModel(
            (_, _) => new TargetRebuild.RebuildOutcome(0, 0, 0, 0, 0, 0),
            (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
            (_, _) => new SmartRebuild.SmartRebuildOutcome(0, 0, 0, 0, 0, 0, 0),
            (_, _) => new CatalogIdentityBackfill.BackfillOutcome(0, 0, 0),
            (_, _, _) => Task.FromResult<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>(
                new ReferenceThumbnailPass.ReferenceThumbnailOutcome(0, 0, 0, false)),
            _ => 0,
            () => 90,
            _ => 0,
            post: action => action());

        // Phase 9 Task 6: the primary constructor takes one factory per real tab, so the three
        // preference tabs are supplied from the shared factory even though this file asserts
        // nothing about them.
        var page = new SettingsViewModel(
            () => library.ViewModel,
            () => targets.ViewModel,
            () => filtersVm,
            () => equipmentVm,
            () => maintenanceVm,
            () => TestSupport.PreferenceTabViewModelTestFactory.NewLocationTab(),
            () => TestSupport.PreferenceTabViewModelTestFactory.NewDisplayTab(),
            () => TestSupport.PreferenceTabViewModelTestFactory.NewStorageTab());
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");

        page.Selected = page.Tabs.Single(tab => tab.Key == "filters");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(filtersVm, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is FiltersTabView);

        page.Selected = page.Tabs.Single(tab => tab.Key == "equipment");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(equipmentVm, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is EquipmentTabView);
    }

    // ---- P13 R2(b) and R2(c): the swatch opens a picker, on both kinds of row -------------------

    private static List<Button> Named(Control view, string name)
        => [.. view.GetVisualDescendants().OfType<Button>().Where(button => button.Name == name)];

    [AvaloniaFact]
    public void GroupingEditorView_TheGroupSwatch_IsAButtonWithAPickerFlyout()
    {
        using var vm = BuildFilters(
            filters: new Dictionary<string, FilterSetting> { ["Ha"] = new FilterSetting { Color = "#ff0000" } },
            discovered: [("Ha", 5)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var button = Assert.Single(Named(view, "ColorSwatchButton"));
        Assert.True(button.IsVisible);

        // Ruling Q25: the colour is in the button's content, never in a view-declared button
        // style, so the chrome stays Theme/Controls.axaml's shared vocabulary.
        var swatch = Assert.IsType<Border>(button.Content);
        Assert.Equal(Color.Parse("#ff0000"), Assert.IsAssignableFrom<ISolidColorBrush>(swatch.Background).Color);

        var flyout = Assert.IsType<Flyout>(button.Flyout);
        Assert.IsType<ColorView>(flyout.Content);
    }

    [AvaloniaFact]
    public void GroupingEditorView_AnUngroupedRow_ShowsItsSwatchButton()
    {
        using var vm = BuildFilters(discovered: [("OIII", 3)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var button = Assert.Single(Named(view, "UngroupedColorSwatchButton"));
        Assert.True(button.IsVisible);

        // Seeded through the fold (ruling Q4), not left as the placeholder elevation fill.
        var swatch = Assert.IsType<Border>(button.Content);
        Assert.Equal(Color.Parse("#3a8fd4"), Assert.IsAssignableFrom<ISolidColorBrush>(swatch.Background).Color);
        Assert.IsType<ColorView>(Assert.IsType<Flyout>(button.Flyout).Content);
    }

    [AvaloniaFact]
    public void GroupingEditorView_TheCamerasEditor_ShowsNoSwatch()
    {
        using var vm = BuildEquipment(
            equipment: new EquipmentSettings
            {
                Cameras = new Dictionary<string, EquipmentItemSettings> { ["ASI2600MM"] = new EquipmentItemSettings() },
            },
            cameras: [("ASI2600MM", 10), ("ASI294MC", 4)],
            telescopes: [("RC8", 4)]);
        var view = new EquipmentTabView { DataContext = vm };
        Show(view);

        // ShowColorPicker is false for both equipment editors, so every swatch button is present
        // in the template and none of them is shown. Review P3-2: the NotEmpty comes first,
        // because Assert.All over an empty list succeeds and would make this case pass whether
        // the swatches are hidden or the scan simply found none.
        var grouped = Named(view, "ColorSwatchButton");
        var ungrouped = Named(view, "UngroupedColorSwatchButton");
        Assert.NotEmpty(grouped);
        Assert.NotEmpty(ungrouped);
        Assert.All(grouped, button => Assert.False(button.IsVisible));
        Assert.All(ungrouped, button => Assert.False(button.IsVisible));
    }

    [AvaloniaFact]
    public void GroupingEditorView_ThePicker_RoundTripsAColour()
    {
        using var vm = BuildFilters(
            filters: new Dictionary<string, FilterSetting> { ["Ha"] = new FilterSetting { Color = "#ff0000" } },
            discovered: [("Ha", 5)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var group = Assert.Single(vm.Editor.Groups);
        var button = Assert.Single(Named(view, "ColorSwatchButton"));
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        var picker = Assert.IsType<ColorView>(flyout.Content);
        // The picker opens on what the swatch shows, and opening it stores nothing.
        Assert.Equal(Color.Parse("#ff0000"), picker.Color);
        Assert.Equal("#ff0000", group.Color);

        picker.Color = Color.Parse("#123456");
        Dispatcher.UIThread.RunJobs();
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#123456", group.Color);
        Assert.Equal(Color.Parse("#123456"), group.SwatchBrush.Color);
        var swatch = Assert.IsType<Border>(button.Content);
        Assert.Equal(Color.Parse("#123456"), Assert.IsAssignableFrom<ISolidColorBrush>(swatch.Background).Color);
    }

    // Review P2-2: the ungrouped pick commits once, on close, with the colour the user released
    // on. Committing per ColorChanged promoted the row on the first pixel of a drag, after which
    // every later change hit SetUngroupedColor's coverage guard and was dropped.
    [AvaloniaFact]
    public void GroupingEditorView_TheUngroupedPicker_CommitsOnceOnCloseWithTheLastColour()
    {
        using var vm = BuildFilters(discovered: [("OIII", 3), ("Duoband", 1)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var button = Named(view, "UngroupedColorSwatchButton")
            .Single(candidate => candidate.DataContext is DiscoveredNameViewModel { Name: "OIII" });
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        var picker = Assert.IsType<ColorView>(flyout.Content);
        picker.Color = Color.Parse("#111111");
        Dispatcher.UIThread.RunJobs();

        // Mid-drag: the row's own swatch tracks the colour and nothing has been promoted, so the
        // flyout is still attached to a row that is still in the collection.
        Assert.Empty(vm.Editor.Groups);
        Assert.Equal(2, vm.Editor.Ungrouped.Count);
        Assert.Equal("#111111", vm.Editor.Ungrouped.Single(row => row.Name == "OIII").Color);

        picker.Color = Color.Parse("#222222");
        Dispatcher.UIThread.RunJobs();
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        var group = Assert.Single(vm.Editor.Groups);
        Assert.Equal("OIII", group.Canonical);
        Assert.Equal("#222222", group.Color);
        Assert.Empty(group.Aliases);
        Assert.Equal(["Duoband"], vm.Editor.Ungrouped.Select(row => row.Name));
    }

    [AvaloniaFact]
    public void GroupingEditorView_TheUngroupedPicker_OpenedAndClosedUnchanged_PromotesNothing()
    {
        using var vm = BuildFilters(discovered: [("OIII", 3)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var button = Assert.Single(Named(view, "UngroupedColorSwatchButton"));
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        // Looking at a seeded colour is not choosing one.
        Assert.Empty(vm.Editor.Groups);
        Assert.Equal(["OIII"], vm.Editor.Ungrouped.Select(row => row.Name));
    }

    [AvaloniaFact]
    public void GroupingEditorView_AFreshHaGroup_RendersTheSeededRed()
    {
        // No stored colour anywhere in the document: R2(a)'s whole point is that this still
        // reads red rather than grey.
        using var vm = BuildFilters(
            filters: new Dictionary<string, FilterSetting> { ["Ha"] = new FilterSetting() },
            discovered: [("Ha", 5)]);
        var view = new FiltersTabView { DataContext = vm };
        Show(view);

        var group = Assert.Single(vm.Editor.Groups);
        Assert.Null(group.Color);

        var button = Assert.Single(Named(view, "ColorSwatchButton"));
        var swatch = Assert.IsType<Border>(button.Content);
        Assert.Equal(Color.Parse("#c44040"), Assert.IsAssignableFrom<ISolidColorBrush>(swatch.Background).Color);
    }
}
