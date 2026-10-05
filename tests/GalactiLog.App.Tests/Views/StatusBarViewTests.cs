using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// The status bar's first dedicated view test (design-spec 18.3). Phase 14B Task 2 adds spec 12's
// job monitor to this view, which until now was reached only through ShellNavigationTests, so the
// three existing scan controls get their own pinning here too: they are the controls the spec says
// are unchanged.
public class StatusBarViewTests
{
    private sealed record Harness(
        Window Window, StatusBarView View, StatusBarViewModel ViewModel, JobRegistry Jobs, ScanStatusService Status)
        : IDisposable
    {
        public void Dispose()
        {
            ViewModel.Dispose();
            Status.Dispose();
        }
    }

    private static Harness Show()
    {
        var status = new ScanStatusService(ScanCoordinatorTestFactory.CreateBare(), action => action());
        var jobs = new JobRegistry(action => action());
        var viewModel = new StatusBarViewModel(status, () => { }, jobs: jobs);
        var view = new StatusBarView { DataContext = viewModel };
        var window = new Window { Width = 1280, Height = 64, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Harness(window, view, viewModel, jobs, status);
    }

    private static IReadOnlyList<string> VisibleTextsIn(Visual root)
        => [.. root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    private static Button ButtonNamed(Visual root, string name)
        => root.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

    // Opens the monitor's flyout and returns its content, attached and laid out.
    private static Control OpenFlyout(Harness harness)
    {
        var button = ButtonNamed(harness.View, "JobMonitorButton");
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        return Assert.IsAssignableFrom<Control>(flyout.Content);
    }

    [AvaloniaFact]
    public void View_Constructs_AndLaysOutNonZero()
    {
        using var harness = Show();

        Assert.True(harness.View.Bounds.Width > 0);
        Assert.True(harness.View.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void View_TheJobMonitorButton_IsHiddenWithNoRunningJobs()
    {
        using var harness = Show();

        var button = ButtonNamed(harness.View, "JobMonitorButton");
        Assert.False(button.IsVisible);
    }

    [AvaloniaFact]
    public void View_TheJobMonitorButton_ShowsTheCount()
    {
        using var harness = Show();

        harness.Jobs.Begin("scan", "Library scan");
        Dispatcher.UIThread.RunJobs();

        var button = ButtonNamed(harness.View, "JobMonitorButton");
        Assert.True(button.IsVisible);
        Assert.Contains("1 job", VisibleTextsIn(button));

        harness.Jobs.Begin("prune_activity", "Prune activity log");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("2 jobs", VisibleTextsIn(button));
    }

    // Fix pass, review escalation 1: the recent list is reachable only through this button, so the
    // button outlives the last running job. The count itself is what hides at zero.
    [AvaloniaFact]
    public void View_TheJobMonitorButton_StaysVisibleForTheRecentList()
    {
        using var harness = Show();

        var handle = harness.Jobs.Begin("scan", "Library scan");
        handle.Report("Ingesting 20/20 files", 100d);
        Dispatcher.UIThread.RunJobs();

        var button = ButtonNamed(harness.View, "JobMonitorButton");
        Assert.True(button.IsVisible);

        handle.Finish(JobResult.Succeeded, "The scan finished.");
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(harness.Jobs.Running);
        Assert.True(button.IsVisible, "the monitor must survive the last job, or the outcome is unreachable");
        Assert.DoesNotContain("1 job", VisibleTextsIn(button));
        Assert.Contains("Jobs", VisibleTextsIn(button));

        var content = OpenFlyout(harness);
        var recent = content.GetVisualDescendants()
            .OfType<ItemsControl>()
            .Single(list => list.Name == "RecentJobList");
        var texts = recent.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "");
        Assert.Contains("Library scan", texts);
        Assert.Contains("The scan finished.", texts);
        Assert.Contains("Succeeded", texts);
    }

    [AvaloniaFact]
    public void View_TheFlyout_ListsRunningJobsAboveRecentOnes()
    {
        using var harness = Show();

        harness.Jobs.Begin("prune_activity", "Prune activity log").Finish(JobResult.Succeeded, "Pruned 12 entries.");
        var scan = harness.Jobs.Begin("scan", "Library scan");
        scan.Report("Classifying 3/10 files", 30d);
        Dispatcher.UIThread.RunJobs();

        var content = OpenFlyout(harness);
        var panel = Assert.IsType<StackPanel>(content);

        var running = panel.Children.OfType<ItemsControl>().Single(list => list.Name == "RunningJobList");
        var recent = panel.Children.OfType<ItemsControl>().Single(list => list.Name == "RecentJobList");
        Assert.True(panel.Children.IndexOf(running) < panel.Children.IndexOf(recent));

        Assert.Equal(harness.Jobs.Running, running.ItemsSource);
        Assert.Equal(harness.Jobs.Recent, recent.ItemsSource);

        var texts = content.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text ?? "")
            .ToList();
        Assert.Contains("Library scan", texts);
        Assert.Contains("Classifying 3/10 files", texts);
        Assert.Contains("Prune activity log", texts);
        Assert.Contains("Pruned 12 entries.", texts);
    }

    [AvaloniaFact]
    public void View_ARunningJobWithNoCancel_RendersNoCancelButton()
    {
        using var harness = Show();

        harness.Jobs.Begin("prune_activity", "Prune activity log");
        Dispatcher.UIThread.RunJobs();

        var content = OpenFlyout(harness);
        var running = content.GetVisualDescendants()
            .OfType<ItemsControl>()
            .Single(list => list.Name == "RunningJobList");

        Assert.DoesNotContain(
            running.GetVisualDescendants().OfType<Button>(),
            button => button.IsVisible);

        // And a job that DID register one renders it, so the case above is about the delegate and
        // not about the template never drawing a button at all.
        harness.Jobs.Begin("scan", "Library scan", () => { });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(
            running.GetVisualDescendants().OfType<Button>(),
            button => button.IsVisible && (button.Content as string) == "Cancel");
    }

    [AvaloniaFact]
    public void View_TheExistingScanControls_AreUnchanged()
    {
        using var harness = Show();

        // Idle: the run button is offered, the cancel button and the bar are not.
        var run = ButtonNamed(harness.View, "RunScanButton");
        var cancel = ButtonNamed(harness.View, "CancelButton");
        Assert.True(run.IsVisible);
        Assert.False(cancel.IsVisible);
        Assert.Equal("Run scan", run.Content);
        Assert.Equal("Cancel", cancel.Content);
        Assert.Same(harness.ViewModel.RunScanCommand, run.Command);
        Assert.Same(harness.ViewModel.CancelCommand, cancel.Command);

        // And the update indicator is still its own column, untouched by the monitor beside it.
        Assert.Single(
            harness.View.GetVisualDescendants().OfType<StackPanel>(),
            panel => panel.Name == "UpdateIndicator");
    }

    [AvaloniaFact]
    public void View_DeclaresNoStyleOfItsOwn()
    {
        // Theme/Controls.axaml is the whole shared control vocabulary (HANDOFF section 4 rule 5);
        // a view that declares its own button or badge style is a build failure by
        // ControlStyleScanTest, and this view has never had a Style block at all.
        var path = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "StatusBarView.axaml");
        Assert.True(File.Exists(path), $"Missing {path}");

        var markup = File.ReadAllText(path);
        Assert.DoesNotContain("<Style", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Styles>", markup, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void View_TheJobMonitorButton_IsNotFilled()
    {
        using var harness = Show();

        harness.Jobs.Begin("scan", "Library scan");
        Dispatcher.UIThread.RunJobs();

        // Spec 14.5: exactly one filled button exists in the application, and it is RunScanButton
        // in this very view. A second one is a review finding, so it is asserted where it would
        // happen.
        var monitor = ButtonNamed(harness.View, "JobMonitorButton");
        Assert.DoesNotContain("primary", monitor.Classes);
        Assert.Contains("primary", ButtonNamed(harness.View, "RunScanButton").Classes);
        Assert.Single(
            harness.View.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("primary"));
    }

    [AvaloniaFact]
    public void View_EveryTextBlock_RendersAtAReadableSize()
    {
        // FontSizeTokenTest bans the ratio keys statically; this is the rendered half of it, on the
        // markup this task added.
        using var harness = Show();

        harness.Jobs.Begin("scan", "Library scan", () => { }).Report("Classifying 3/10 files", 30d);
        harness.Jobs.Begin("prune_activity", "Prune").Finish(JobResult.Failed, "It failed.");
        Dispatcher.UIThread.RunJobs();

        var content = OpenFlyout(harness);
        var blocks = harness.View.GetVisualDescendants()
            .Concat(content.GetVisualDescendants())
            .OfType<TextBlock>()
            .ToList();

        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"A TextBlock rendered at {block.FontSize}, which is a ratio key bound to FontSize."));
    }
}
