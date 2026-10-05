using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Tray;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.11 behaviour 4. Plain xunit, no window: the tray view-model owns no behaviour of
// its own, so every case here is about what it delegates to and how the tooltip is composed.
//
// A synchronous post throughout, so every mutation is visible immediately without a dispatcher.
// ScanStatusService's properties are private-set, so "running" is reached through the same public
// seam production uses, ScanCoordinator.RaiseProgress, exactly as StatusBarViewModelTests does.
public class TrayIconViewModelTests
{
    private sealed class Harness : IDisposable
    {
        public ScanCoordinator Coordinator { get; }

        public ScanStatusService Status { get; }

        public StatusBarViewModel StatusBar { get; }

        public WindowResidencyService Residency { get; }

        public TrayIconViewModel ViewModel { get; }

        public RecordingUpdateChecker Checker { get; } = new();

        public UpdateService? Updates { get; }

        public TempDatabase? Database { get; }

        public int ShutdownRequests { get; private set; }

        public Harness(bool withUpdateService = true, bool isInstalled = true)
        {
            Coordinator = ScanCoordinatorTestFactory.CreateBare();
            Status = new ScanStatusService(Coordinator, action => action());
            StatusBar = new StatusBarViewModel(Status, () => { }, _ => Task.CompletedTask);
            Residency = new WindowResidencyService(
                () => new GeneralSettings(),
                post: action => action(),
                requestShutdown: () => { ShutdownRequests++; return true; });

            if (withUpdateService)
            {
                Checker.IsInstalled = isInstalled;
                Database = new TempDatabase("galactilog-trayicon");
                Updates = new UpdateService(
                    new BuildInfo("1.4.2.0", "0b1d3f5", "alpha", isInstalled),
                    () => Checker,
                    Status,
                    new ActivityRepository(Database.ConnectionString),
                    showPrompt: null,
                    post: action => action());
            }

            ViewModel = new TrayIconViewModel(
                StatusBar, Residency, Updates, post: action => action());
        }

        // Reaches IsRunning == true without a real database, the way StatusBarViewModelTests does.
        public void RaiseRunning(string taskName = ScanTaskNames.Discovery, string message = "Discovering...")
            => Coordinator.RaiseProgress(taskName, 1, 1, message, force: true);

        // Drops back to idle the way UpdateServiceTests' harness does: the in-memory connection is
        // never migrated, so the pipeline throws once it reads settings, but not before its
        // finally block has raised ScanFinished, which is what puts IsRunning back to false.
        public async Task RaiseFinishedAsync()
            => await Assert.ThrowsAnyAsync<Exception>(
                () => Coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        public void Dispose()
        {
            ViewModel.Dispose();
            StatusBar.Dispose();
            Updates?.Dispose();
            Status.Dispose();
            Database?.Dispose();
        }
    }

    // ---- the menu's commands ------------------------------------------------------------------

    [Fact]
    public void ScanNowCommand_IsTheStatusBarsOwnRunScanCommand()
    {
        // Design-lessons rule 1, as one assertion. A second run-scan command here would be a tray
        // menu that can start a scan the status bar's guard would have refused.
        using var harness = new Harness();

        Assert.Same(harness.StatusBar.RunScanCommand, harness.ViewModel.ScanNowCommand);
    }

    // Spec 12.11 behaviour 3's only automated path. An AvaloniaFact with a real window, because
    // the behaviour under test is that the window comes back: a case built on a service with no
    // window attached passes with an empty Open() body (review finding 2).
    [AvaloniaFact]
    public void Open_CallsTheResidencyServiceShowAndActivate()
    {
        using var harness = new Harness();
        var window = new Window();
        window.Show();
        harness.Residency.Attach(window);

        // close_to_tray defaults to on, so this is the hide a user performs before reaching for
        // the tray menu.
        Assert.True(harness.Residency.HandleUserClose());
        Assert.False(window.IsVisible);

        harness.ViewModel.OpenCommand.Execute(null);

        Assert.True(window.IsVisible);
        Assert.True(harness.Residency.IsWindowVisible);

        window.Close();
    }

    [Fact]
    public void Exit_CallsTheResidencyServiceRequestExit()
    {
        using var harness = new Harness();

        harness.ViewModel.ExitCommand.Execute(null);

        Assert.Equal(1, harness.ShutdownRequests);
        Assert.True(harness.Residency.IsExiting);
    }

    [Fact]
    public void Exit_WhileExiting_DoesNothing()
    {
        using var harness = new Harness();
        harness.Residency.OnShutdownRequested();

        harness.ViewModel.ExitCommand.Execute(null);

        Assert.Equal(0, harness.ShutdownRequests);
    }

    [AvaloniaFact]
    public void Open_WhileExiting_DoesNothing()
    {
        using var harness = new Harness();
        var window = new Window();
        window.Show();
        harness.Residency.Attach(window);
        Assert.True(harness.Residency.HandleUserClose());
        Assert.False(window.IsVisible);

        // The positive control, first (fixer list code item 1, phase review finding P3). Without
        // it this case passed on an empty Open() body, because the window is already hidden and
        // "still hidden" is what an unrouted command and a correctly refused one both leave
        // behind. Deleting the body of TrayIconViewModel.Open now fails here, on the activation
        // the service really did perform.
        harness.ViewModel.OpenCommand.Execute(null);
        Assert.True(window.IsVisible);
        Assert.True(harness.Residency.IsWindowVisible);

        Assert.True(harness.Residency.HandleUserClose());
        Assert.False(window.IsVisible);
        harness.Residency.OnShutdownRequested();

        // TRACKING item 13: the body carries the guard, and a native menu item is not a button
        // whose enablement this application controls. Bringing the window back while the drain is
        // running would put a window on screen that the shutdown is about to destroy.
        harness.ViewModel.OpenCommand.Execute(null);

        Assert.False(window.IsVisible);
        Assert.False(harness.Residency.IsWindowVisible);

        window.Close();
    }

    // ---- the tooltip --------------------------------------------------------------------------

    [Fact]
    public void ToolTipText_IsTheApplicationName_WhenIdle()
    {
        using var harness = new Harness();

        Assert.Equal(TrayIconViewModel.IdleToolTipText, harness.ViewModel.ToolTipText);
    }

    [Fact]
    public void ToolTipText_CarriesTheScanState_WhileAScanRuns()
    {
        using var harness = new Harness();

        harness.RaiseRunning(ScanTaskNames.Ingest);

        Assert.Equal("GalactiLog - Ingesting", harness.ViewModel.ToolTipText);

        // The same text the status bar shows, from the same property, so the two cannot disagree.
        Assert.EndsWith(harness.StatusBar.StateText, harness.ViewModel.ToolTipText, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolTipText_RaisesPropertyChanged_WhenTheScanStateChanges()
    {
        using var harness = new Harness();
        var raised = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        harness.RaiseRunning(ScanTaskNames.Discovery);
        harness.RaiseRunning(ScanTaskNames.Ingest);

        // Both sources matter: IsRunning toggles once, and TaskName changes several times within
        // one run without IsRunning ever toggling again.
        Assert.Contains(nameof(TrayIconViewModel.ToolTipText), raised);
        Assert.True(
            raised.Count(name => name == nameof(TrayIconViewModel.ToolTipText)) >= 2,
            "the tooltip must follow the task name as well as the running flag");
    }

    [Fact]
    public void ToolTipText_IsTruncated_AtTheWindowsTrayTooltipCap()
    {
        using var harness = new Harness();

        // An unknown task token falls back to the token itself, which is how a long state string
        // reaches the tooltip at all. Windows truncates at 127 characters; truncating here means a
        // long message can never produce a silently empty tooltip.
        harness.RaiseRunning(new string('x', 400));

        Assert.Equal(TrayIconViewModel.ToolTipMaxLength, harness.ViewModel.ToolTipText.Length);
        Assert.StartsWith("GalactiLog - ", harness.ViewModel.ToolTipText, StringComparison.Ordinal);
    }

    // ---- the scan completion notice (spec 12.11 behaviour 10, Phase 11 Task 5) -----------------
    //
    // One property, one composer, two inputs. A second writer of ToolTipText would be the finding
    // here; what these assert is that the notice is an input to the composer the scan state
    // already goes through.

    [Fact]
    public void ToolTipText_AfterACompletion_CarriesTheOutcome()
    {
        using var harness = new Harness();

        harness.ViewModel.LastCompletion = new ScanCompletionNotice(true, "Scan complete", "4 new files");

        Assert.Equal("Scan complete - 4 new files", harness.ViewModel.ToolTipText);
    }

    [Fact]
    public void ToolTipText_AfterACompletion_RaisesPropertyChanged()
    {
        using var harness = new Harness();
        var raised = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        harness.ViewModel.LastCompletion = new ScanCompletionNotice(true, "Scan complete", "4 new files");

        Assert.Contains(nameof(TrayIconViewModel.ToolTipText), raised);
    }

    [Fact]
    public void ToolTipText_WhileAScanRuns_PrefersTheScanState()
    {
        using var harness = new Harness();

        harness.RaiseRunning(ScanTaskNames.Ingest);
        harness.ViewModel.LastCompletion = new ScanCompletionNotice(true, "Scan complete", "4 new files");

        // The coordinator's pending follow-up run means a notice can land while a scan is already
        // running again. What the application is doing now wins.
        Assert.Equal("GalactiLog - Ingesting", harness.ViewModel.ToolTipText);
    }

    [Fact]
    public async Task ToolTipText_WhenAScanStarts_ClearsTheLastCompletion()
    {
        using var harness = new Harness();
        harness.ViewModel.LastCompletion = new ScanCompletionNotice(true, "Scan complete", "4 new files");

        harness.RaiseRunning(ScanTaskNames.Discovery);

        // Spec 12.11 behaviour 10: the notice stands until the next scan starts, so a finished
        // run is never reported over a running one and never survives it.
        Assert.Null(harness.ViewModel.LastCompletion);

        await harness.RaiseFinishedAsync();

        Assert.Equal(TrayIconViewModel.IdleToolTipText, harness.ViewModel.ToolTipText);
    }

    [Fact]
    public void ToolTipText_WithALongCompletion_IsTruncatedAtTheSameCap()
    {
        using var harness = new Harness();

        harness.ViewModel.LastCompletion =
            new ScanCompletionNotice(false, "Scan failed", new string('x', 400));

        Assert.Equal(TrayIconViewModel.ToolTipMaxLength, harness.ViewModel.ToolTipText.Length);
        Assert.StartsWith("Scan failed - ", harness.ViewModel.ToolTipText, StringComparison.Ordinal);
    }

    // ---- the update check ---------------------------------------------------------------------

    [Fact]
    public async Task CheckForUpdates_CallsTheUpdateServiceOnce()
    {
        using var harness = new Harness();

        harness.ViewModel.CheckForUpdatesCommand.Execute(null);
        await harness.ViewModel.CheckForUpdatesCommand.ExecutionTask!;

        Assert.Equal(1, harness.Checker.Checks);
    }

    [Fact]
    public async Task CheckForUpdates_PressedTwice_ChecksOnce()
    {
        using var harness = new Harness();
        using var gate = new TaskCompletionSourceGate();
        harness.Checker.CheckGate = gate.Source;

        harness.ViewModel.CheckForUpdatesCommand.Execute(null);

        // Captured BEFORE the second Execute (TRACKING item 13, the Phase 9 flake shape): a second
        // press trips the guard and overwrites ExecutionTask with a completed no-op.
        var first = harness.ViewModel.CheckForUpdatesCommand.ExecutionTask!;

        harness.ViewModel.CheckForUpdatesCommand.Execute(null);
        gate.Source.TrySetResult();
        await first;

        Assert.Equal(1, harness.Checker.Checks);
    }

    [Fact]
    public async Task CheckForUpdates_IsGuardedInTheCommandBody_NotOnlyByCanExecute()
    {
        using var harness = new Harness(withUpdateService: false);

        // RelayCommand.Execute ignores CanExecute, and a native menu item is not a button this
        // application greys out itself.
        harness.ViewModel.CheckForUpdatesCommand.Execute(null);
        if (harness.ViewModel.CheckForUpdatesCommand.ExecutionTask is { } task)
        {
            await task;
        }

        Assert.Equal(0, harness.Checker.Checks);
    }

    [Fact]
    public async Task CheckForUpdates_OnANonInstalledBuild_CannotExecuteAndDoesNothing()
    {
        using var harness = new Harness(isInstalled: false);

        Assert.False(harness.ViewModel.CheckForUpdatesCommand.CanExecute(null));

        harness.ViewModel.CheckForUpdatesCommand.Execute(null);
        if (harness.ViewModel.CheckForUpdatesCommand.ExecutionTask is { } task)
        {
            await task;
        }

        Assert.Equal(0, harness.Checker.Checks);
    }

    [Fact]
    public void CheckForUpdates_ReadsTheOneSharedPredicate()
    {
        // Ruling Q4: the tray and spec 12.7's About tab both read UpdateService.CanCheckNow, so
        // neither carries its own copy of "not disposed, installed, not already checking".
        using var harness = new Harness();

        Assert.True(harness.Updates!.CanCheckNow);
        Assert.True(harness.ViewModel.CheckForUpdatesCommand.CanExecute(null));

        harness.Updates.Dispose();

        Assert.False(harness.Updates.CanCheckNow);
        Assert.False(harness.ViewModel.CheckForUpdatesCommand.CanExecute(null));
    }

    // ---- teardown -----------------------------------------------------------------------------

    [Fact]
    public void Dispose_DetachesBothStatusSubscriptions()
    {
        using var harness = new Harness();
        var raised = 0;
        PropertyChangedEventHandler handler = (_, _) => raised++;
        harness.ViewModel.PropertyChanged += handler;

        harness.ViewModel.Dispose();
        harness.RaiseRunning(ScanTaskNames.Ingest);

        Assert.Equal(0, raised);
        harness.ViewModel.PropertyChanged -= handler;
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var harness = new Harness();

        harness.ViewModel.Dispose();
        harness.ViewModel.Dispose();
    }

    /// <summary>A completion source with a using-friendly wrapper, so a parked check is always
    /// released even when an assertion fails first.</summary>
    private sealed class TaskCompletionSourceGate : IDisposable
    {
        public TaskCompletionSource Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => Source.TrySetResult();
    }
}
