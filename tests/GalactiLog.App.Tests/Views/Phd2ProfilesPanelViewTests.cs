using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Globalization;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Phase 15A Task 6 brief section 8.5: spec 12.7's PHD2 profiles panel, hosted on EquipmentTabView
// (the panel is markup inside that view, not a separate UserControl). Design-spec 18.3's view
// smoke tests: the view parses, lays out, and binds against a populated panel and an empty one.
public class Phd2ProfilesPanelViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    private static Phd2ProfilesViewModel BuildPanel(
        GeneralSettings? seed = null, IReadOnlyList<Phd2ProfileRow>? rows = null)
    {
        var general = seed ?? new GeneralSettings();
        return new Phd2ProfilesViewModel(
            () => general,
            () => rows ?? [],
            mutate => general = mutate(general),
            () => ["Askar FMA180"],
            systemTimezones: () => ["UTC"],
            post: action => action());
    }

    private static EquipmentTabViewModel BuildTab(
        Phd2ProfilesViewModel? panel,
        Action<Action>? post = null,
        IReadOnlyList<(string Name, int Count)>? discovered = null)
        => new(
            () => new EquipmentSettings(),
            _ => { },
            () => [],
            _ => { },
            () => discovered ?? [],
            () => discovered ?? [],
            post: post ?? (action => action()),
            phd2Profiles: panel);

    private static Phd2ProfileRow Row(string profile = "Rig A")
        => new(profile, "ZWO ASI294MM Mini", 500, 1.23, 3, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

    [AvaloniaFact]
    public void EquipmentTabView_WithAPopulatedPanel_ConstructsAndLaysOut()
    {
        var panel = BuildPanel(rows: [Row("Rig A")]);
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        var vm = BuildTab(panel);
        var view = new EquipmentTabView { DataContext = vm };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        var section = view.GetControl<Border>("Phd2ProfilesSection");
        Assert.True(section.IsEffectivelyVisible);

        var texts = VisibleTexts(view);
        Assert.Contains("PHD2 profiles", texts);
        Assert.Contains("Rig A", texts);

        // The row's editors are inside the ItemsControl's DataTemplate, realized per item, so
        // they are found by descendant type rather than by x:Name (which only resolves against
        // the view's own compiled name scope, not a templated item's).
        Assert.Contains(view.GetVisualDescendants().OfType<ComboBox>(), c => c.Name == "TelescopeBox" && c.IsEffectivelyVisible);
        Assert.Contains(view.GetVisualDescendants().OfType<ComboBox>(), c => c.Name == "TimezoneBox" && c.IsEffectivelyVisible);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(), c => c.Name == "LatitudeBox" && c.IsEffectivelyVisible);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(), c => c.Name == "LongitudeBox" && c.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void EquipmentTabView_WithAnEmptyPanel_NamesTheLibraryTabsCheckbox()
    {
        var panel = BuildPanel();
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        var vm = BuildTab(panel);
        var view = new EquipmentTabView { DataContext = vm };
        Show(view);

        Assert.True(view.GetControl<Border>("Phd2ProfilesSection").IsEffectivelyVisible);
        var emptyText = view.GetControl<TextBlock>("Phd2ProfilesEmptyText");
        Assert.True(emptyText.IsEffectivelyVisible);
        Assert.Contains("Read PHD2 guide logs", emptyText.Text);
        Assert.Contains("Library", emptyText.Text);
    }

    // Phase 15B Task 5c: the Statistics page's Guiding empty notice offers "Map profiles", and the
    // shell asks this tab for the panel before the view exists, so the request is consumed on
    // attach and the posted BringIntoView runs against a measured panel.
    [AvaloniaFact]
    public void EquipmentTabView_OnAttach_ConsumesThePendingPhd2ProfilesRequest()
    {
        var panel = BuildPanel(rows: [Row("Rig A")]);
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        var vm = BuildTab(panel);
        vm.RequestPhd2ProfilesInView();
        var view = new EquipmentTabView { DataContext = vm };
        Show(view);
        Dispatcher.UIThread.RunJobs();

        // Consumed by the attach, so the next visit to the tab is an ordinary visit rather than a
        // second scroll the user did not ask for.
        Assert.False(vm.ConsumePhd2ProfilesInViewRequest());
        Assert.True(view.GetControl<Border>("Phd2ProfilesSection").IsEffectivelyVisible);
    }

    // ---- Phase 15B Task 5c, review P2-2: the first visit -------------------------------------
    //
    // The case above settles the panel and the tab before the view is built, so IsLoading is
    // already false at attach and the waiting branch never runs. A first visit is the opposite:
    // the tab's read is still on the pool, the two grouping editors are empty, and the PHD2 panel
    // sits high in the page. A scroll issued then lands on a real offset and the editors, once
    // they publish, push the panel straight past it, which is why this side waits on the load
    // rather than on visibility. Held open by a post seam that queues instead of running.

    private static readonly IReadOnlyList<(string Name, int Count)> ManyNames =
        [.. Enumerable.Range(1, 24).Select(index => ($"Discovered equipment {index:00}", index))];

    private static (EquipmentTabViewModel Tab, List<Action> Queued) HeldOpenTab(Phd2ProfilesViewModel panel)
    {
        var queued = new List<Action>();
        var tab = BuildTab(panel, post: action => queued.Add(action), discovered: ManyNames);

        // The read itself has run; what is held is the publish it posted.
        tab.PendingLoad?.Wait(TimeSpan.FromSeconds(30));
        return (tab, queued);
    }

    private static void ReleaseLoad(List<Action> queued)
    {
        foreach (var action in queued.ToArray())
        {
            action();
        }

        queued.Clear();

        // Twice: the first pass lets the layout run against the rows the publish added, the second
        // runs the scroll this view posts at Loaded priority behind it.
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void EquipmentTabView_OnAFirstVisit_ScrollsToThePanelOnceTheTabHasLoaded()
    {
        var panel = BuildPanel(rows: [Row("Rig A"), Row("Rig B"), Row("Rig C")]);
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        var (tab, queued) = HeldOpenTab(panel);
        tab.RequestPhd2ProfilesInView();
        var view = new EquipmentTabView { DataContext = tab };
        Show(view);
        Dispatcher.UIThread.RunJobs();

        Assert.True(tab.IsLoading);

        ReleaseLoad(queued);

        Assert.False(tab.IsLoading);
        Assert.True(
            ScrollAssertions.IsInView(
                view.GetControl<Border>("Phd2ProfilesSection"), ScrollAssertions.Scroller(view)),
            "the two editors above the panel fill when the tab publishes, so a scroll issued "
            + "before that leaves the panel below the fold");
    }

    [AvaloniaFact]
    public void EquipmentTabView_DetachedBeforeTheTabHasLoaded_LeavesNoWaitBehind()
    {
        var panel = BuildPanel(rows: [Row("Rig A")]);
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        var (tab, queued) = HeldOpenTab(panel);
        tab.RequestPhd2ProfilesInView();
        var view = new EquipmentTabView { DataContext = tab };
        var window = Show(view);
        Dispatcher.UIThread.RunJobs();

        // Away and back. The second attach consumes nothing, because the first consumed the
        // request.
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        window.Content = view;
        Dispatcher.UIThread.RunJobs();

        ReleaseLoad(queued);

        // A wait the detach failed to drop would fire here and scroll a visit that asked for
        // nothing.
        Assert.Equal(0d, ScrollAssertions.Scroller(view).Offset.Y);
    }

    [AvaloniaFact]
    public void EquipmentTabView_WithNoPanelConstructed_HidesTheWholeSection()
    {
        var vm = BuildTab(panel: null);
        var view = new EquipmentTabView { DataContext = vm };
        Show(view);

        Assert.False(view.GetControl<Border>("Phd2ProfilesSection").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void EquipmentTabView_DeclaresXDataTypeOnTheRowTemplate()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "GalactiLog.App", "Views", "Settings", "EquipmentTabView.axaml"));

        Assert.Contains("x:DataType=\"settings:EquipmentTabViewModel\"", source);
        Assert.Contains("x:DataType=\"settings:Phd2ProfilesViewModel\"", source);
        Assert.Contains("DataType=\"settings:Phd2ProfileRowViewModel\"", source);
    }

    // Review P2-4: the message CommitRow sets reached no markup at all, so a refused write reverted
    // the user's box in front of them with no explanation.
    [AvaloniaFact]
    public void EquipmentTabView_BindsThePanelsErrorMessage_AndItsReRunSentence()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "GalactiLog.App", "Views", "Settings", "EquipmentTabView.axaml"));

        Assert.Contains("x:Name=\"Phd2ProfilesErrorText\"", source);
        Assert.Contains("x:Name=\"Phd2ProfilesStatusText\"", source);

        // Review P1-2, first half: the property-changed default commits a write per keystroke.
        Assert.Contains(
            "Text=\"{Binding LatitudeText, Mode=TwoWay, UpdateSourceTrigger=LostFocus}\"", source);
        Assert.Contains(
            "Text=\"{Binding LongitudeText, Mode=TwoWay, UpdateSourceTrigger=LostFocus}\"", source);
    }

    // Review P2-3: a watermark is gone the moment a field holds a value, and a screen reader
    // announced nothing at all for any of the four editors.
    [AvaloniaFact]
    public void EveryEditor_CarriesAVisibleLabelAndAnAutomationName()
    {
        var panel = BuildPanel(rows: [Row("Rig A")]);
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        var view = new EquipmentTabView { DataContext = BuildTab(panel) };
        Show(view);

        var texts = VisibleTexts(view);
        foreach (var label in new[] { "Telescope", "Timezone", "Latitude", "Longitude" })
        {
            Assert.Contains(label, texts);
        }

        foreach (var name in new[] { "TelescopeBox", "TimezoneBox", "LatitudeBox", "LongitudeBox" })
        {
            var editor = view.GetVisualDescendants().OfType<Control>().Single(control => control.Name == name);
            Assert.False(
                string.IsNullOrEmpty(AutomationProperties.GetName(editor)),
                $"{name} carries no AutomationProperties.Name");
        }

        // The three facts that used to render as bare values. A TextBlock composed of Runs carries
        // no Text of its own, so the caption is read off the inlines the way the row builds it.
        var captioned = view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && block.Inlines is { Count: > 0 })
            .Select(block => string.Concat(block.Inlines!.OfType<Run>().Select(run => run.Text)))
            .ToList();

        foreach (var caption in new[] { "Guide camera", "Focal length", "Pixel scale" })
        {
            Assert.Contains(captioned, text => text.StartsWith(caption, StringComparison.Ordinal));
        }
    }

    // Review P2-2 and the shape TRACKING section 5 names: the Phase 14B Clear log crash was an
    // [ObservableProperty] written from a continuation that had left the dispatcher. IsSaving is
    // written only in SaveAsync's body and its finally, never inside _post, so it isolates that
    // continuation exactly.
    [AvaloniaFact]
    public async Task TheSaveCommandsContinuation_NotifiesOnTheDispatcher()
    {
        var general = new GeneralSettings();
        var panel = new Phd2ProfilesViewModel(
            () => general,
            () => [Row("Rig A")],
            mutate => general = mutate(general),
            () => ["Askar FMA180"],
            systemTimezones: () => ["UTC"],
            post: action => action());
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));

        var offDispatcher = new List<string>();
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Phd2ProfilesViewModel.IsSaving) && !Dispatcher.UIThread.CheckAccess())
            {
                offDispatcher.Add(e.PropertyName);
            }
        };

        var row = panel.Rows.Single();
        await panel.SaveCommand.ExecuteAsync(
            new Phd2RowEdit(row, entry => entry with { Latitude = 10 }, Revert: () => { }));

        Assert.Empty(offDispatcher);
    }

    // Review P3-5. The Settings tab's own allotment inside the shell, never the whole window: the
    // shipped 1280 by 800 window (MainWindow.axaml) less the 200 pixel expanded navigation rail and
    // the 32 pixel status bar leaves 1080 by 768 for the page, and SettingsView gives its content
    // region that less the 180 pixel tab strip and its own Margin="16" on both sides.
    private const double SettingsTabStripWidth = 180d;
    private const double SettingsTabRegionMargin = 32d;
    private const double StatusBarHeight = 32d;

    [AvaloniaTheory]
    [InlineData(18d)]
    [InlineData(20d)]
    public void EveryEditor_FitsTheSettingsTabsAllotment(double rootFontSize)
    {
        var panel = BuildPanel(rows: [Row("Rig A")]);
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        var view = new EquipmentTabView { DataContext = BuildTab(panel) };

        var allotment = 1280d - MainWindowViewModel.NavRailExpandedWidth
            - SettingsTabStripWidth - SettingsTabRegionMargin;
        var host = new Border
        {
            Width = allotment,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = view,
        };
        var window = new Window
        {
            FontSize = rootFontSize,
            Width = 1280,
            Height = 800 - StatusBarHeight,
            Content = host,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "TelescopeBox", "TimezoneBox", "LatitudeBox", "LongitudeBox" })
        {
            var editor = view.GetVisualDescendants().OfType<Control>().Single(control => control.Name == name);
            var trailing = editor.TranslatePoint(new Point(editor.Bounds.Width, 0), view);
            Assert.NotNull(trailing);
            Assert.True(
                trailing!.Value.X <= view.Bounds.Width,
                $"{name} ends at {trailing.Value.X} past the {view.Bounds.Width} the Settings tab is given "
                + $"at root text size {rootFontSize}");
        }
    }

    // Fix-wave review P2-2: spec 12.7's "the panel says a re-run is still owed" sentence had no
    // markup at all, so the flag the store, the scan and the host all maintain reached no reader.
    [AvaloniaFact]
    public void EquipmentTabView_WithAReRunOwed_ShowsTheOwedSentence_AndHidesItBehindTheQueuedOne()
    {
        var panel = BuildPanel(
            seed: new GeneralSettings { Phd2CorrelationPending = true }, rows: [Row("Rig A")]);
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        var view = new EquipmentTabView { DataContext = BuildTab(panel) };
        Show(view);

        var owed = view.GetControl<TextBlock>("Phd2ProfilesPendingText");
        Assert.True(owed.IsEffectivelyVisible);
        Assert.Equal(Phd2ProfilesViewModel.ReRunOwedMessage, owed.Text);

        // A save of this panel's own puts the queued sentence up; the owed one leaves the screen
        // rather than the two stacking as though there were two re-runs.
        panel.Rows.Single().LatitudeText = "30.1";
        panel.PendingWrite.Wait(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.GetControl<TextBlock>("Phd2ProfilesStatusText").IsEffectivelyVisible);
        Assert.False(owed.IsEffectivelyVisible);
    }

    /// <summary>Phase review set D item 3, read off the Task 6b capture: the picker drew
    /// "GMT-07:00 La Paz, Maz" and the two watermarks drew "-90 to 9" and "-180 to 1". Each
    /// control's own content is measured in its own face and size, at the default and the Extra
    /// Large root, inside the same allotment
    /// <see cref="EveryEditor_FitsTheSettingsTabsAllotment"/> pins, which stays green
    /// unedited.</summary>
    [AvaloniaTheory]
    [InlineData(18d)]
    [InlineData(20d)]
    public void TheTimezonePickerAndBothWatermarks_AreDrawnWhole(double rootFontSize)
    {
        // The zone of the capture itself, so the case is red against exactly what was clipped.
        const string zoneId = "Mountain Standard Time (Mexico)";
        var general = new GeneralSettings
        {
            Timezone = "UTC",
            Phd2ProfileMap = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                """{"Rig A": {"telescope": null, "timezone": "Mountain Standard Time (Mexico)", "latitude": null, "longitude": null}}"""),
        };
        var panel = new Phd2ProfilesViewModel(
            () => general,
            () => [Row("Rig A")],
            mutate => general = mutate(general),
            () => [],
            systemTimezones: () => [zoneId, "UTC"],
            resolveTimezone: TimeZoneInfo.FindSystemTimeZoneById,
            post: action => action());
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));

        var view = new EquipmentTabView { DataContext = BuildTab(panel) };
        var allotment = 1280d - MainWindowViewModel.NavRailExpandedWidth
            - SettingsTabStripWidth - SettingsTabRegionMargin;
        var window = new Window
        {
            FontSize = rootFontSize,
            Width = 1280,
            Height = 800 - StatusBarHeight,
            Content = new Border
            {
                Width = allotment,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = view,
            },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var label = panel.Rows.Single().TimezoneOptions.Single(option => option.Id == zoneId).Label;
        Assert.StartsWith("GMT-07:00 ", label, StringComparison.Ordinal);
        Assert.True(label.Length >= 20, $"the machine labels this zone \"{label}\", too short to pin anything");

        // Scoped to the owning editor and to what is actually on screen: the picker's closed
        // dropdown carries a second copy of the selected label in its popup.
        foreach (var (owner, text) in new (string Owner, string Text)[]
                 {
                     ("TimezoneBox", label),
                     ("LatitudeBox", "-90 to 90"),
                     ("LongitudeBox", "-180 to 180"),
                 })
        {
            var editor = view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == owner);
            var block = editor.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(candidate => candidate.Text == text && candidate.IsEffectivelyVisible);
            var measured = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight),
                block.FontSize,
                Brushes.White).Width;

            Assert.True(
                block.Bounds.Width >= measured - 0.5d,
                $"\"{text}\" is drawn into {block.Bounds.Width:F1} px for text measuring "
                + $"{measured:F1} px at root text size {rootFontSize}");
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
