using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Dashboard;
using GalactiLog.Core.Scanning;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for spec 12.10: each empty state parses, lays out and
// renders its own text and button, and the unreachable-root banner names the path. Compiled
// bindings already turn a binding-path typo into a build error; these catch a missing resource
// or a template that cannot realize.
public class DashboardEmptyStateViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string?> TextsOf(Control view) =>
        [.. view.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text)];

    private static DashboardViewModel EmptyDashboard(DashboardContentState state)
    {
        var dashboard = DashboardViewModelTestFactory.Create(startScan: _ => Task.CompletedTask);

        if (state == DashboardContentState.NoMatches)
        {
            // Reached the way the user reaches it: a filter that excludes everything.
            dashboard.Filters.ObjectTypes.First(pill => pill.Key == "Galaxy").IsSelected = true;
            DashboardViewModelTestFactory.Settle(dashboard);
        }

        Assert.Equal(state, dashboard.ContentState);
        return dashboard;
    }

    [AvaloniaTheory]
    [InlineData(DashboardContentState.NoFramesYet, "No frames catalogued yet", "Run Scan")]
    [InlineData(DashboardContentState.NoMatches, "No targets match these filters", "Reset Filters")]
    public void DashboardEmptyStateView_RendersEachState(DashboardContentState state, string message, string buttonText)
    {
        var view = new DashboardEmptyStateView { DataContext = EmptyDashboard(state) };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.Contains(message, TextsOf(view));

        var buttons = view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .ToList();
        Assert.Single(buttons);
        Assert.Equal(buttonText, buttons[0].Content);
        Assert.True(buttons[0].IsEffectivelyEnabled);

        // Review item 2. Scales.axaml's FontSize* keys are ratios, not point sizes; the sub-line
        // used to bind FontSizeCaption and rendered at 0.714px. FontSizeTokenTest is the
        // repo-wide guard; this is the rendered check for this view.
        var blocks = view.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }

    [AvaloniaFact]
    public void NoFramesYet_WhileAScanRuns_RendersTheProgressNotTheInstruction()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var status = new ScanStatusService(coordinator, action => action());
        var dashboard = DashboardViewModelTestFactory.Create(startScan: _ => Task.CompletedTask, scanStatus: status);
        var view = new DashboardEmptyStateView { DataContext = dashboard };
        Show(view);

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 4, "Discovering files...", force: true);
        Dispatcher.UIThread.RunJobs();

        var texts = TextsOf(view);
        Assert.Contains("Discovering files...", texts);
        Assert.DoesNotContain(texts, text => text!.Contains("Add a library folder"));
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible);
        var bar = Assert.Single(view.GetVisualDescendants().OfType<ProgressBar>(), bar => bar.IsEffectivelyVisible);
        Assert.Equal(25d, bar.Value);
    }

    [AvaloniaFact]
    public void UnreachableRootBanner_RendersThePathText()
    {
        const string root = @"Z:\Astro\Lights";
        var dashboard = DashboardViewModelTestFactory.Create(probeRoots: () => [root]);
        var view = new DashboardView { DataContext = dashboard };
        Show(view);

        var banner = view.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "UnreachableRootBanner");
        Assert.True(banner.IsEffectivelyVisible);
        Assert.Contains(root, TextsOf(banner));
        Assert.Contains("Library folder not reachable", TextsOf(banner));
    }

    [AvaloniaFact]
    public void UnreachableRootBanner_IsHiddenWhenEveryRootIsReachable()
    {
        var view = new DashboardView { DataContext = DashboardViewModelTestFactory.Create() };
        Show(view);

        var banner = view.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "UnreachableRootBanner");
        Assert.False(banner.IsEffectivelyVisible);
    }
}
