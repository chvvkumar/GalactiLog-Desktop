using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Tray;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// The one shipped path from spec 12.11 behaviour 10's seam to the notification-area tooltip,
/// which is the mechanism coordinator ruling Q6 (c) selected.
/// </summary>
/// <remarks>
/// Added in fix round 1 (review minor 4). The watcher's cases stop at
/// <see cref="RecordingScanCompletionNotifier"/> and the tray cases assign
/// <c>LastCompletion</c> by hand, so without this file the only production implementation of
/// <see cref="IScanCompletionNotifier"/> was constructed by nothing but <c>AppHost</c>.
/// </remarks>
public class TrayTooltipScanNotifierTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Coordinator = ScanCoordinatorTestFactory.CreateBare();
            Status = new ScanStatusService(Coordinator, action => action());
            StatusBar = new StatusBarViewModel(Status, () => { }, _ => Task.CompletedTask);
            Residency = new WindowResidencyService(() => new GeneralSettings(), post: action => action());
            Tray = new TrayIconViewModel(StatusBar, Residency, updates: null, post: action => action());
            Notifier = new TrayTooltipScanNotifier(Tray, Logger);
        }

        public ScanCoordinator Coordinator { get; }

        public ScanStatusService Status { get; }

        public StatusBarViewModel StatusBar { get; }

        public WindowResidencyService Residency { get; }

        public TrayIconViewModel Tray { get; }

        public RecordingLogger Logger { get; } = new();

        public TrayTooltipScanNotifier Notifier { get; }

        public void RaiseRunning()
            => Coordinator.RaiseProgress(ScanTaskNames.Ingest, 1, 1, "Ingesting...", force: true);

        public void Dispose()
        {
            Tray.Dispose();
            StatusBar.Dispose();
            Status.Dispose();
        }
    }

    private static ScanCompletionNotice Notice(string title = "Scan complete", string body = "4 new files")
        => new(title == ScanCompletionNotice.CompleteTitle, title, body);

    [Fact]
    public void IsAvailable_IsTrue_OnEveryBuild()
    {
        using var harness = new Harness();

        // No package, no P/Invoke and no Application User Model ID, so this notifier behaves the
        // same on an installed build and on a dotnet run. That is the whole reason ruling Q6 took
        // route (c).
        Assert.True(harness.Notifier.IsAvailable);
    }

    [Fact]
    public void Show_PutsTheOutcomeAndTheCountsOnTheTooltip()
    {
        using var harness = new Harness();

        harness.Notifier.Show(Notice());

        Assert.Equal("Scan complete - 4 new files", harness.Tray.ToolTipText);
    }

    [Fact]
    public void Show_Twice_KeepsTheLatestOutcome()
    {
        using var harness = new Harness();

        harness.Notifier.Show(Notice());
        harness.Notifier.Show(Notice("Scan failed", "1 failure. the catalogue is locked"));

        Assert.Equal("Scan failed - 1 failure. the catalogue is locked", harness.Tray.ToolTipText);
    }

    [Fact]
    public void Show_ThenAScanStarts_LeavesTheScanStateOnTheTooltip()
    {
        using var harness = new Harness();
        harness.Notifier.Show(Notice());

        harness.RaiseRunning();

        // Spec 12.11 behaviour 10: the notice stands until the next scan starts. One property,
        // one composer, and the running scan wins.
        Assert.Equal("GalactiLog - Ingesting", harness.Tray.ToolTipText);
        Assert.Null(harness.Tray.LastCompletion);
    }

    [Fact]
    public void Show_WritesNothingElse_AndLogsNothing()
    {
        using var harness = new Harness();

        harness.Notifier.Show(Notice());

        // It assigns the composer's one input and nothing else: no second writer of ToolTipText,
        // and no log line on the path that worked.
        Assert.Equal(Notice(), harness.Tray.LastCompletion);
        Assert.Empty(harness.Logger.Entries);
    }
}
