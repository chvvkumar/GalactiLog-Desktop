using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.Views;
using GalactiLog.Core.Settings;
using Xunit;
using TabFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// ux-flow F8: the window's keyboard shortcuts. Each case presses the real key on the real window,
// so a binding missing from the markup, or one whose gesture is spelled wrong, fails here.
public class ShellShortcutTests : IDisposable
{
    private readonly List<IDisposable> _pages = [];

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private T Track<T>(T page) where T : IDisposable
    {
        _pages.Add(page);
        return page;
    }

    private (MainWindow Window, MainWindowViewModel ViewModel) ShowShell(
        Func<ActivityViewModel>? activity = null,
        PendingEditsRegistry? pendingEdits = null,
        DashboardViewModel? dashboard = null,
        Func<AnalysisViewModel>? analysis = null)
    {
        var viewModel = CreateShell(activity, pendingEdits, dashboard, analysis);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    private MainWindowViewModel CreateShell(
        Func<ActivityViewModel>? activity = null,
        PendingEditsRegistry? pendingEdits = null,
        DashboardViewModel? dashboard = null,
        Func<AnalysisViewModel>? analysis = null,
        Func<MosaicsPageViewModel>? mosaics = null)
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var viewModel = new MainWindowViewModel(
            new GeneralSettings(),
            dashboard ?? DashboardViewModelTestFactory.Create(),
            new StatusBarViewModel(new ScanStatusService(coordinator, action => action()), coordinator.Cancel),
            new GalactiLog.App.ViewModels.Settings.SettingsViewModel(
                TabFactory.Create(createTarget: TabFactory.CreateForm()).Settle().ViewModel),
            () => Track(StatisticsViewModelTestFactory.Create()),
            analysis ?? (() => Track(AnalysisViewModelTestFactory.Create())),
            activity ?? (() => Track(ActivityViewModelTestFactory.Create())),
            mosaics: mosaics,
            pendingEdits: pendingEdits);
        return Track(viewModel);
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void CtrlDigit_SelectsTheRailEntryInOrder()
    {
        var (window, viewModel) = ShowShell();

        Press(window, PhysicalKey.Digit3, RawInputModifiers.Control);
        Assert.Same(viewModel.Items[2], viewModel.Selected);

        Press(window, PhysicalKey.Digit7, RawInputModifiers.Control);
        Assert.Equal("settings", viewModel.Selected.Key);

        Press(window, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.Equal("dashboard", viewModel.Selected.Key);
        window.Close();
    }

    [AvaloniaFact]
    public void TheRailTips_NameTheShortcut()
    {
        var (window, viewModel) = ShowShell();

        Assert.Equal(
            ["Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7"],
            viewModel.Items.Select(item => item.Shortcut));
        Assert.Equal("Statistics  Ctrl+3", viewModel.Items[2].Tip);
        window.Close();
    }

    [AvaloniaFact]
    public async Task F5_RunsTheShowingPagesRefresh()
    {
        var reads = 0;
        var activity = await ActivityViewModelTestFactory.CreateAsync(page: (_, _, _) =>
        {
            Interlocked.Increment(ref reads);
            return ActivityViewModelTestFactory.Page();
        });
        Track(activity);
        var (window, viewModel) = ShowShell(activity: () => activity);
        viewModel.Selected = viewModel.Items.First(item => item.Key == "activity");
        Dispatcher.UIThread.RunJobs();
        Assert.IsAssignableFrom<IRefreshable>(viewModel.CurrentPage);
        var before = Volatile.Read(ref reads);

        Press(window, PhysicalKey.F5);

        // The refresh starts its read synchronously, so the page's pending load is the signal.
        Dispatcher.UIThread.RunJobs();
        await activity.PendingLoad!;
        Dispatcher.UIThread.RunJobs();

        Assert.True(Volatile.Read(ref reads) > before, "F5 did not re-read the Activity page");
        window.Close();
    }

    [AvaloniaFact]
    public void CtrlS_ReachesTheSaveBarOnlyWhileItShows()
    {
        var pending = new PendingEditsRegistry(post: action => action());
        var source = new Source();
        pending.Register(source);
        var (window, _) = ShowShell(pendingEdits: pending);

        // Nothing pending: the key passes through and touches nothing.
        Press(window, PhysicalKey.S, RawInputModifiers.Control);
        Assert.Equal(0, source.Saves);

        source.Dirty = true;
        Press(window, PhysicalKey.S, RawInputModifiers.Control);
        Assert.Equal(1, source.Saves);
        window.Close();
    }

    // Review C1: a window-level Escape ran before every control and threw away all pending
    // settings on an Escape meant for a dropdown or an editor. Escape discards only from inside
    // the bar.
    [AvaloniaFact]
    public void Escape_DiscardsOnlyWithFocusInTheSaveBar()
    {
        var pending = new PendingEditsRegistry(post: action => action());
        var source = new Source { Dirty = true };
        pending.Register(source);
        var (window, viewModel) = ShowShell(pendingEdits: pending);
        var settings = Assert.IsType<GalactiLog.App.ViewModels.Settings.SettingsViewModel>(
            viewModel.Items.First(item => item.Key == "settings").Page);
        settings.Selected = settings.Tabs.First(tab => tab.Key == "targets");
        var targets = Assert.IsType<GalactiLog.App.ViewModels.Settings.TargetsTabViewModel>(settings.Selected.Page);
        targets.CreateTarget!.OpenCommand.Execute(null);
        viewModel.Selected = viewModel.Items.First(item => item.Key == "settings");
        Dispatcher.UIThread.RunJobs();

        var field = window.GetControl<ContentControl>("ContentRegion").GetVisualDescendants()
            .OfType<TextBox>().Single(box => box.Name == "CreateTargetNameBox");
        Assert.True(field.Focus());
        Press(window, PhysicalKey.Escape);
        Assert.Equal(0, source.Discards);

        var discard = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiscardAllButton");
        Assert.True(discard.Focus());
        Press(window, PhysicalKey.Escape);
        Assert.Equal(1, source.Discards);
        window.Close();
    }

    [AvaloniaFact]
    public void F5_OnTheDashboard_ReReadsItOnce()
    {
        var probes = 0;
        var dashboard = DashboardViewModelTestFactory.Create(probeRoots: () =>
        {
            Interlocked.Increment(ref probes);
            return [];
        });
        var (window, viewModel) = ShowShell(dashboard: dashboard);
        DashboardViewModelTestFactory.Settle(dashboard);
        var before = Volatile.Read(ref probes);

        Press(window, PhysicalKey.F5);
        DashboardViewModelTestFactory.Settle(dashboard);

        Assert.Same(dashboard, viewModel.CurrentPage);
        Assert.Equal(before + 1, Volatile.Read(ref probes));
        window.Close();
    }

    [AvaloniaFact]
    public void F5_OnAnalysis_ReReadsItOnce()
    {
        var reads = 0;
        var analysis = Track(AnalysisViewModelTestFactory.Create(loadFilters: () =>
        {
            Interlocked.Increment(ref reads);
            return ["Ha", "OIII"];
        }));
        var (window, viewModel) = ShowShell(analysis: () => analysis);
        viewModel.Selected = viewModel.Items.First(item => item.Key == "analysis");
        Dispatcher.UIThread.RunJobs();
        AnalysisViewModelTestFactory.Settle(analysis);
        var before = Volatile.Read(ref reads);

        Press(window, PhysicalKey.F5);
        AnalysisViewModelTestFactory.Settle(analysis);

        Assert.Equal(before + 1, Volatile.Read(ref reads));
        window.Close();
    }

    // The F5 key to RefreshPageCommand route is pressed for real in the three cases above. The
    // Mosaics harness posts its reload from the thread pool, which a shown window's bindings
    // refuse, so this case runs the shell's command with no window.
    [AvaloniaFact]
    public void F5_OnMosaics_ReReadsItOnce()
    {
        var reads = 0;
        var harness = Track(new MosaicsPageHarness(new MosaicsBackend
        {
            ListPending = () =>
            {
                Interlocked.Increment(ref reads);
                return [];
            },
        }));
        var viewModel = CreateShell(mosaics: () => harness.Page);
        viewModel.Selected = viewModel.Items.First(item => item.Key == "mosaics");
        Assert.True(MosaicsPageSettle.Settle(harness.Page));
        var before = Volatile.Read(ref reads);

        viewModel.RefreshPageCommand.Execute(null);
        Assert.True(MosaicsPageSettle.Settle(harness.Page));

        Assert.Equal(before + 1, Volatile.Read(ref reads));
    }

    [AvaloniaFact]
    public void CtrlF_OpensTheDashboardSearchAndFocusesIt()
    {
        var dashboard = DashboardViewModelTestFactory.Create();
        var (window, viewModel) = ShowShell(dashboard: dashboard);
        viewModel.Selected = viewModel.Items.First(item => item.Key == "settings");
        Dispatcher.UIThread.RunJobs();

        Press(window, PhysicalKey.F, RawInputModifiers.Control);

        // Returning to the dashboard starts its quiet re-query, which the factory's post seam
        // publishes from the pool thread. Wait it out and run the layout pass it may invalidate
        // before reading the tree, as the F5 case above does; without this the search box can be
        // missing from the tree on a loaded runner.
        DashboardViewModelTestFactory.Settle(dashboard);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("dashboard", viewModel.Selected.Key);
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "DashboardSearchBox");
        Assert.True(box.IsFocused);
        window.Close();
    }

    // Task 9 review m1: a field bound on LostFocus (latitude, longitude, tool paths) has not
    // written its value when Ctrl+S arrives. The shortcut commits the focused box first, so the
    // save takes the value just typed. A failure is Saves at 0 (the key passed through with
    // nothing pending yet) or the holder still empty.
    [AvaloniaFact]
    public void CtrlS_CommitsTheFocusedLostFocusTextBox_ThenSaves()
    {
        var pending = new PendingEditsRegistry(post: action => action());
        var source = new Source();
        pending.Register(source);
        var (window, _) = ShowShell(pendingEdits: pending);

        var holder = new Holder(source);
        var box = new TextBox();
        box.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(Holder.Value))
        {
            Source = holder,
            Mode = Avalonia.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = Avalonia.Data.UpdateSourceTrigger.LostFocus,
        });
        ((Grid)window.Content!).Children.Add(box);
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.Focus());
        box.Text = "51.5";
        Assert.Null(holder.Value);

        Press(window, PhysicalKey.S, RawInputModifiers.Control);

        Assert.Equal("51.5", holder.Value);
        Assert.Equal(1, source.Saves);
        window.Close();
    }

    // ux-flow F3 with final review minor 3: focus moves to the content region on the switch, so
    // an Enter typed on the same frame has nothing on the old page to act on.
    [AvaloniaFact]
    public void APageSwitch_MovesFocusOffTheOutgoingPage_ToTheContentRegion()
    {
        var (window, viewModel) = ShowShell();
        var region = window.GetControl<TransitioningContentControl>("ContentRegion");
        var inside = region.GetVisualDescendants().OfType<Button>()
            .First(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        Assert.True(inside.Focus());

        viewModel.Selected = viewModel.Items.First(item => item.Key == "activity");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(region, window.FocusManager!.GetFocusedElement());
        window.Close();
    }

    private sealed class Holder(Source source)
    {
        private string? _value;

        public string? Value
        {
            get => _value;
            set
            {
                _value = value;
                source.Dirty = true;
            }
        }
    }

    private sealed class Source : IPendingEdits
    {
        private bool _dirty;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public int Saves { get; private set; }

        public int Discards { get; private set; }

        public string Label => "Library";

        public string NavigationKey => "library";

        public string? SaveRefusal => null;

        public bool HasPendingEdits => _dirty;

        public bool Dirty
        {
            set
            {
                _dirty = value;
                PropertyChanged?.Invoke(this, new(nameof(HasPendingEdits)));
            }
        }

        public Task SaveAsync()
        {
            Saves++;
            return Task.CompletedTask;
        }

        public void Discard() => Discards++;
    }
}
