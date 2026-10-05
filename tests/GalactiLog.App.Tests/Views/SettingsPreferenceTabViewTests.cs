using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for design-spec 12.7's Location, Display and Storage tabs:
// they parse, lay out and bind against a populated view-model. Compiled bindings already turn a
// binding-path typo into a build error; these catch the rest (a missing resource, a template that
// cannot realize, a note that was quietly deleted).
public class SettingsPreferenceTabViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// What <c>ToggleButton.OnClick</c> does, in order: <c>Toggle()</c> sets <c>IsChecked</c>,
    /// which writes back through the binding if that binding is two way, and then <c>Click</c>
    /// runs the command. Both halves, which is the whole of what verification B1 found.
    /// </summary>
    /// <remarks>
    /// Not a pointer click through <c>window.MouseDown</c>: this tab is a tall scrolling page and
    /// the column pickers arrange around y 2000 in a 900 pixel window, so a pointer at the box's
    /// translated centre lands outside the viewport and hits nothing. Measured, not assumed.
    /// </remarks>
    private static void ClickCheckBox(CheckBox box)
    {
        box.IsChecked = box.IsChecked != true;
        if (box.Command is { } command && command.CanExecute(box.CommandParameter))
        {
            command.Execute(box.CommandParameter);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    private static async Task<LocationTabViewModel> NewLocationAsync()
    {
        var current = new GeneralSettings { ObserverName = "Backyard", ObserverLatitude = 35.2 };
        var tab = new LocationTabViewModel(
            () => current,
            mutate =>
            {
                current = mutate(current);
                return current;
            },
            systemTimezones: () => ["UTC", "Europe/London"],
            post: action => action());

        return await tab.SettleAsync().ConfigureAwait(true);
    }

    private static async Task<DisplayTabViewModel> NewDisplayAsync()
    {
        var general = new GeneralSettings();
        var display = new DisplaySettings();
        var graph = new GraphSettings();
        var columns = new DisplayColumnWriter(() => display, value => display = value);
        var dashboard = new TargetListViewModel(display, columns, general.DefaultPageSize);

        var tab = new DisplayTabViewModel(
            () => general,
            mutate =>
            {
                general = mutate(general);
                return general;
            },
            () => display,
            value => display = value,
            () => graph,
            new GraphSettingsWriter(() => graph, value => graph = value),
            columns,
            () => ThemeManager.Available,
            _ => { },
            dashboardColumns: dashboard.Columns,
            toggleDashboardColumn: column => dashboard.ToggleColumnCommand.Execute(column),
            post: action => action());

        return await tab.SettleAsync().ConfigureAwait(true);
    }

    private static async Task<StorageTabViewModel> NewStorageAsync()
    {
        var current = new GeneralSettings { ThumbnailCacheDir = @"D:\Cache\GalactiLog" };
        var tab = new StorageTabViewModel(
            () => current,
            mutate =>
            {
                current = mutate(current);
                return current;
            },
            volumeSpace: _ => (2_000_000_000L, 1_500_000_000L),
            defaultCacheRoot: () => @"D:\Cache\GalactiLog",
            post: action => action());

        return await tab.SettleAsync().ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task LocationTabView_Constructs_AndRendersItsFields()
    {
        using var tab = await NewLocationAsync();
        var view = new LocationTabView { DataContext = tab };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        Assert.Equal("Backyard", view.GetControl<TextBox>("ObserverNameBox").Text);
        Assert.Equal("35.2", view.GetControl<TextBox>("LatitudeBox").Text);
        Assert.True(view.GetControl<CheckBox>("UseImagingNightBox").IsChecked);

        var texts = VisibleTexts(view);
        Assert.Contains("Observer location", texts);
        Assert.Contains("Coordinates group frames into imaging nights and drive darkness calculations.", texts);

        // Spec 8.3's fallback, on screen because the longitude is unset.
        Assert.Contains(
            "Without a longitude, sessions group on UTC midnight instead of on local solar midnight.",
            texts);

        // Spec 8.2: the setting takes effect on the next scan; nothing is recomputed.
        Assert.Contains(texts, text => text.Contains("takes effect on the next scan"));
    }

    // Polish wave 1 ruling 3: both timezone pickers hold a fixed width, so the section stops
    // resizing as the selected label changes length. A failure reads as a Width of NaN.
    [AvaloniaFact]
    public async Task LocationTabView_TimezonePickers_HoldAFixedWidth()
    {
        using var tab = await NewLocationAsync();
        var view = new LocationTabView { DataContext = tab };
        Show(view);

        Assert.Equal(320, view.GetControl<ComboBox>("ObserverTimezoneBox").Width);
        Assert.Equal(320, view.GetControl<ComboBox>("DisplayTimezoneBox").Width);
    }

    [AvaloniaFact]
    public async Task LocationTabView_AnOutOfRangeLatitude_RendersTheInlineMessage()
    {
        using var tab = await NewLocationAsync();
        var view = new LocationTabView { DataContext = tab };
        Show(view);

        tab.LatitudeText = "91";
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Must be between -90 and 90", VisibleTexts(view));
    }

    [AvaloniaFact]
    public async Task DisplayTabView_Constructs_AndRendersEverySection()
    {
        using var tab = await NewDisplayAsync();
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        Assert.True(view.Bounds.Width > 0);

        var texts = VisibleTexts(view);
        Assert.Contains("Appearance", texts);
        Assert.Contains("Defaults", texts);
        Assert.Contains("Metric visibility", texts);
        Assert.Contains("Dashboard columns", texts);
        Assert.Contains("Frame table columns", texts);

        // The six groups and their 27 field checkboxes all realize.
        var checkBoxes = view.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Contains(checkBoxes, box => (box.Content as string) == "Quality Metrics");
        Assert.Contains(checkBoxes, box => (box.Content as string) == "HFR StDev");
        Assert.Contains(checkBoxes, box => (box.Content as string) == "Rotator Position");

        // The theme picker renders the six shipped themes (P12 R7).
        Assert.Equal(6, view.GetControl<ComboBox>("ThemeBox").ItemCount);
    }

    [AvaloniaFact]
    public async Task DisplayTabView_FieldCheckboxes_UseTheLabelTierOverTheWindowsRootSize()
    {
        // Questions.md Q24's form: a FontSize bound through MultiplyConverter over the window's
        // inherited size, never a FontSize* ratio key bound straight to FontSize, which
        // FontSizeTokenTest fails the build on.
        using var tab = await NewDisplayAsync();
        var view = new DisplayTabView { DataContext = tab };
        var window = Show(view);
        window.FontSize = 18d;
        Dispatcher.UIThread.RunJobs();

        var field = view.GetVisualDescendants()
            .OfType<CheckBox>()
            .First(box => (box.Content as string) == "HFR StDev");

        Assert.Equal(18d * 0.786d, field.FontSize, 3);
    }

    [AvaloniaFact]
    public async Task DisplayTabView_AGatedFrameColumn_RendersDisabled()
    {
        using var tab = await NewDisplayAsync();
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        // adu ships disabled, so its columns are gated off and the picker says so rather than
        // offering a click that would change nothing visible.
        var list = view.GetControl<ItemsControl>("FrameColumnList");
        var gated = list.GetVisualDescendants()
            .OfType<CheckBox>()
            .First(box => (box.Content as string) == "ADU Mean");

        Assert.False(gated.IsEffectivelyEnabled);
    }

    // Verification B1 (step 30). Both pickers on this tab bound CheckBox.IsChecked without a mode,
    // and CheckBox.IsChecked binds TWO WAY by default: the click wrote IsVisible through the
    // binding and the Click command then toggled it back, so the two cancelled and no box on this
    // tab ever changed. A real click, not a Command.Execute: executing the command alone never
    // exercised the binding write and is why the existing cases passed against a broken tab.
    [AvaloniaFact]
    public async Task DisplayTabView_ClickingADashboardColumn_ChangesThePersistedList()
    {
        using var tab = await NewDisplayAsync();
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        var picker = tab.DashboardColumns!;
        var column = picker.Columns.First(candidate => candidate.CanHide && candidate.IsVisible);
        var box = view.GetVisualDescendants().OfType<CheckBox>()
            .First(candidate => (candidate.Content as string) == column.Title);

        ClickCheckBox(box);

        Assert.False(column.IsVisible);
        Assert.False(box.IsChecked);

        ClickCheckBox(box);

        Assert.True(column.IsVisible);
        Assert.True(box.IsChecked);
    }

    [AvaloniaFact]
    public async Task DisplayTabView_ClickingAFrameColumn_ChangesThePersistedList()
    {
        using var tab = await NewDisplayAsync();
        var view = new DisplayTabView { DataContext = tab };
        Show(view);

        var picker = tab.FramesColumns;
        var column = picker.Columns.First(
            candidate => candidate.CanHide && candidate.IsGroupEnabled && candidate.IsVisible);
        var list = view.GetControl<ItemsControl>("FrameColumnList");
        var box = list.GetVisualDescendants().OfType<CheckBox>()
            .First(candidate => (candidate.Content as string) == column.Title);

        ClickCheckBox(box);

        Assert.False(column.IsVisible);
        Assert.False(box.IsChecked);

        ClickCheckBox(box);

        Assert.True(column.IsVisible);
        Assert.True(box.IsChecked);
    }

    [AvaloniaFact]
    public async Task StorageTabView_Constructs_AndRendersTheThreeSpecNotes()
    {
        using var tab = await NewStorageAsync();
        var view = new StorageTabView { DataContext = tab };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.Equal(@"D:\Cache\GalactiLog", view.GetControl<TextBox>("CacheDirBox").Text);
        Assert.Equal("1.5 GB free of 2.0 GB", view.GetControl<TextBlock>("FreeSpaceText").Text);

        var texts = VisibleTexts(view);

        // Spec 11.3 and FIXER item 20, on screen rather than in a comment.
        Assert.Contains(texts, text => text.Contains("does not move existing files"));
        Assert.Contains(texts, text => text.Contains("changes the cache key"));
        Assert.Contains(texts, text => text.Contains("below 100 MB are treated as 100 MB"));
    }

    [AvaloniaFact]
    public async Task StorageTabView_AClampedCacheSize_RendersTheNote()
    {
        using var tab = await NewStorageAsync();
        var view = new StorageTabView { DataContext = tab };
        Show(view);

        tab.PreviewCacheMbText = "5";
        await tab.PendingWrite;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Raised to the 100 MB minimum.", VisibleTexts(view));
        Assert.Equal("100", view.GetControl<TextBox>("PreviewCacheMbBox").Text);
    }

    // Spec 12.7's data location row (Phase 10 Task 9). Delegates and constants: the move recorder
    // is a lambda, no pointer file is touched and no directory is created.
    private static async Task<StorageTabViewModel> NewStorageWithDataRootAsync(string? pendingRoot = null)
    {
        var current = new GeneralSettings { ThumbnailCacheDir = @"D:\Cache\GalactiLog" };
        var tab = new StorageTabViewModel(
            () => current,
            mutate =>
            {
                current = mutate(current);
                return current;
            },
            volumeSpace: _ => (2_000_000_000L, 1_500_000_000L),
            defaultCacheRoot: () => @"D:\Cache\GalactiLog",
            post: action => action(),
            dataRoot: () => new AppDataRootResolution(
                @"C:\AppData\GalactiLogData", AppDataRootSource.Pointer, pendingRoot, null, null),
            lastRelocation: () => RelocationOutcome.None,
            requestDataRootMove: _ => null,
            cancelDataRootMove: () => { });

        return await tab.SettleAsync().ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task StorageTabView_RendersTheDataLocationRowAndItsBrowseButton()
    {
        using var tab = await NewStorageWithDataRootAsync();
        var view = new StorageTabView { DataContext = tab };
        Show(view);

        Assert.True(view.GetControl<Border>("DataLocationSection").IsVisible);
        Assert.Equal(@"C:\AppData\GalactiLogData", view.GetControl<TextBox>("DataRootBox").Text);
        Assert.True(view.GetControl<Button>("BrowseDataRootButton").IsEffectivelyVisible);
        Assert.Equal("Chosen location", view.GetControl<TextBlock>("DataRootSourceText").Text);
        Assert.Contains(VisibleTexts(view), text => text.Contains("uninstalling GalactiLog does not remove it"));
    }

    [AvaloniaFact]
    public async Task StorageTabView_PendingMoveRow_IsHiddenWhenNoMoveIsPending()
    {
        using var idle = await NewStorageWithDataRootAsync();
        var idleView = new StorageTabView { DataContext = idle };
        Show(idleView);
        Assert.False(idleView.GetControl<Grid>("PendingMoveRow").IsVisible);

        using var pending = await NewStorageWithDataRootAsync(@"E:\Moved\GalactiLogData");
        var pendingView = new StorageTabView { DataContext = pending };
        Show(pendingView);
        Assert.True(pendingView.GetControl<Grid>("PendingMoveRow").IsVisible);
        Assert.Contains(VisibleTexts(pendingView), text => text.Contains(@"E:\Moved\GalactiLogData"));
    }

    [AvaloniaFact]
    public async Task AFailedRead_RendersOneLineAndNoEditor()
    {
        var tab = new StorageTabViewModel(
            () => throw new InvalidOperationException("the database is gone"),
            mutate => throw new InvalidOperationException("unreachable"),
            volumeSpace: _ => null,
            defaultCacheRoot: () => "",
            post: action => action());
        await tab.SettleAsync();

        using var _ = tab;
        var view = new StorageTabView { DataContext = tab };
        Show(view);

        // Spec 12.10 and the Task 5 review's finding I1: one neutral line in place of the
        // controls, never a live editor over defaults it would then save.
        Assert.Contains("The storage settings could not be read.", VisibleTexts(view));
        Assert.False(view.GetControl<Border>("CacheLocationSection").IsVisible);
        Assert.False(view.GetControl<Border>("PreviewSection").IsVisible);
        Assert.False(view.GetControl<Border>("ThumbnailSection").IsVisible);
    }
}
