using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views.Settings;
using Xunit;

namespace GalactiLog.App.Tests.Views.Settings;

// Design-spec 18.3's view smoke tests for spec 12.15's Custom Columns settings tab (Phase 20
// Task 4). No database: the tab is built directly with fakes, the same shape every other Settings
// tab's own view test file uses (SettingsViewTests.cs's NewEquipmentTab and its siblings).
public class CustomColumnsTabViewTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static string ViewPath =>
        Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Settings", "CustomColumnsTabView.axaml");

    private static Window Show(Control view)
    {
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static CustomColumnsTabViewModel NewTab() => new(
        load: () => [],
        create: (_, _, _, _) => CustomColumnTestFactory.Written,
        update: (_, _, _) => CustomColumnTestFactory.Written,
        reorder: (_, _) => CustomColumnTestFactory.Written,
        delete: _ => CustomColumnTestFactory.Written,
        post: action => action());

    [AvaloniaFact]
    public void TheTab_ConstructsAndLaysOutNonZero()
    {
        var view = new CustomColumnsTabView { DataContext = NewTab() };
        var window = Show(view);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
    }

    [Fact]
    public void TheTab_CarriesTheTwoHelpGlyphsAndNoOthers()
    {
        var source = File.ReadAllText(ViewPath);
        var topics = Regex.Matches(source, @"<controls:HelpButton\s+Topic=""([^""]*)""")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(
            new[] { "settings.custom-columns.add", "settings.custom-columns.table" },
            topics);
    }

    [Fact]
    public void TheTab_DeclaresNoStyleOfItsOwn()
    {
        // The same source check ControlStyleScanTest itself uses over every view under src/, and
        // the shape TargetListViewTests.View_DeclaresNoStyleOfItsOwn already takes.
        var source = File.ReadAllText(ViewPath);
        Assert.DoesNotContain("<Style ", source);
    }

    // Review P3-2: no case attached the same view instance twice to confirm RefreshAsync is not
    // called twice within one attach. A held-publish seam over `load` proves it directly: the
    // first call blocks, so a re-attach while it is still in flight either starts a second call
    // (red, without the guard) or does not (green, with it).
    [AvaloniaFact]
    public void ReAttachingWhileALoadIsInFlight_DoesNotStartASecondLoad()
    {
        var gate = new SemaphoreSlim(0);
        var loadCallCount = 0;
        var tab = new CustomColumnsTabViewModel(
            load: () =>
            {
                Interlocked.Increment(ref loadCallCount);
                gate.Wait(Budget);
                return [];
            },
            create: (_, _, _, _) => CustomColumnTestFactory.Written,
            update: (_, _, _) => CustomColumnTestFactory.Written,
            reorder: (_, _) => CustomColumnTestFactory.Written,
            delete: _ => CustomColumnTestFactory.Written,
            post: action => action());

        var view = new CustomColumnsTabView { DataContext = tab };
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, loadCallCount);
        Assert.True(tab.IsLoading);

        // Detach and reattach the same instance while the first load is still blocked inside its
        // delegate, the shape Avalonia's own reparenting during a measure pass can trigger.
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        window.Content = view;
        Dispatcher.UIThread.RunJobs();

        // Red without the guard: the second attach would start a second background load here,
        // before the first has ever been released.
        Assert.Equal(1, loadCallCount);

        gate.Release();
        tab.PendingLoad?.Wait(Budget);

        // A load that completes (or was never started) still refreshes on the next attach, which
        // is what a genuine later revisit needs; this case is only about the in-flight window.
        Assert.Equal(1, loadCallCount);
    }
}
