using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Setup;

/// <summary>
/// Design-spec 12.1's step 5: the progress envelope display, live counters, a cancel button, and
/// on completion a summary. Running the scan is optional; Finish works without it, which is the
/// web's "Run the first scan now, or finish and start it later from Settings."
/// </summary>
/// <remarks>
/// <para>
/// The scan runs through <c>ScanCoordinator.RunAsync(ScanTrigger.FirstRun, roots, ct)</c> with the
/// <strong>roots override</strong> (HANDOFF.md section 5), so it walks the folders just chosen even
/// if the settings save has not propagated to whatever else reads them. The trigger is
/// <c>ScanTrigger.FirstRun</c>, which spec 5.13 records in <c>scan_runs.trigger</c> and which
/// existed with no caller until this step.
/// </para>
/// <para>
/// Progress comes from <see cref="ScanStatusService"/>, the one App-layer subscriber to the
/// coordinator's progress events, exactly as the status bar and the Library tab bind it. This step
/// never subscribes to <c>ScanCoordinator</c>.
/// </para>
/// <para>
/// <c>StartScanAsync</c> takes no <see cref="CancellationToken"/> on purpose. An
/// <c>AsyncRelayCommand</c> built from a <c>Func&lt;CancellationToken, Task&gt;</c> cancels the
/// in-flight execution's token the moment a second Execute arrives, so a caller that skipped
/// <c>CanExecute</c> would cancel the running scan instead of being turned away by the body guard
/// (Phase 9 Task 8, deviation D10). Cancellation here is the user's Cancel button, which goes
/// through the coordinator's own <c>Cancel()</c>.
/// </para>
/// </remarks>
public sealed partial class FirstScanStepViewModel : SetupStepViewModel
{
    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ScanRunOutcome>> _runFirstScan;
    private readonly Action _cancelScan;
    private readonly ScanStatusService? _scanStatus;

    /// <param name="roots">The folders chosen on step 1. Read at click time, not captured, so a
    /// folder added after the wizard was built is included.</param>
    /// <param name="runFirstScan">Normally
    /// <c>ScanCoordinator.RunAsync(ScanTrigger.FirstRun, roots, token)</c>.</param>
    /// <param name="cancelScan">Normally <c>ScanCoordinator.Cancel</c>.</param>
    /// <param name="scanStatus">The shared progress marshaller (spec 10.4). Null leaves the
    /// readout empty and the button enabled, which is what a unit test that is not about progress
    /// wants.</param>
    public FirstScanStepViewModel(
        Func<IReadOnlyList<string>> roots,
        Func<IReadOnlyList<string>, CancellationToken, Task<ScanRunOutcome>> runFirstScan,
        Action cancelScan,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(post, logger)
    {
        _roots = roots;
        _runFirstScan = runFirstScan;
        _cancelScan = cancelScan;
        _scanStatus = scanStatus;

        if (scanStatus is not null)
        {
            // ScanStatusService has already marshalled onto the UI thread; do not post again.
            scanStatus.PropertyChanged += OnScanStatusPropertyChanged;
        }
    }

    /// <inheritdoc />
    public override string Title => "First scan";

    /// <summary>The web wizard's step 4 copy, verbatim.</summary>
    public string Intro { get; } = "Run the first scan now, or finish and start it later from Settings.";

    /// <summary>The shared progress marshaller, for the view's own bindings.</summary>
    public ScanStatusService? ScanStatus => _scanStatus;

    /// <summary>Whether a scan is running, mirrored so the view needs no null checks.</summary>
    public bool IsScanRunning => _scanStatus?.IsRunning ?? false;

    /// <summary>The live progress line (spec 10.4's envelope).</summary>
    public string ScanMessage => _scanStatus?.Message ?? "";

    /// <summary>The live percentage, 0 to 100.</summary>
    public double ScanPercent => _scanStatus?.Percent ?? 0d;

    /// <summary>True while a scan is running with no determinate percentage.</summary>
    public bool IsScanIndeterminate => _scanStatus?.IsIndeterminate ?? false;

    /// <summary>The completion summary, or null before the first run finishes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial string? Summary { get; private set; }

    public bool HasSummary => Summary is not null;

    /// <summary>Whether a scan started from this step has been requested and not yet returned.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    public partial bool IsStarting { get; private set; }

    /// <inheritdoc />
    /// <remarks>This step writes no settings of its own. Finishing writes
    /// <c>setup_complete</c>, and the wizard owns that, so Next never reaches here: it is the last
    /// step.</remarks>
    public override GeneralSettings Apply(GeneralSettings general) => general;

    /// <inheritdoc />
    public override void Load(GeneralSettings general)
    {
    }

    /// <summary>Spec 12.1's "Start scan", disabled while a scan is active.</summary>
    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        // TRACKING item 13: CanExecute is the affordance, the body is the guard. RelayCommand's
        // Execute ignores CanExecute, and a click that lands between a state change and the
        // command being notified arrives here too.
        if (!CanStartScan())
        {
            return;
        }

        IsStarting = true;
        Summary = null;
        var roots = _roots();

        try
        {
            // Off the dispatcher: RunAsync validates the configured roots synchronously before it
            // returns a task, which is why the status bar's own button does the same Task.Run.
            //
            // CancellationToken.None, not the step's wizard lifetime (Task 9 review, Important
            // finding 2): Finish and Skip dispose the page through ModalHost's cleanup, which
            // cancels that lifetime, and a first scan aborted by finishing the wizard would stop
            // with nothing on screen saying so. Spec 12.1 gives Cancel its own button, which goes
            // through the coordinator's own Cancel(); the coordinator owns cancellation and is
            // drained at shutdown, and the completion Post below is already guarded by IsDisposed.
            var outcome = await Task.Run(() => _runFirstScan(roots, CancellationToken.None))
                .ConfigureAwait(false);
            Post(() =>
            {
                if (IsDisposed)
                {
                    return;
                }

                Summary = Describe(outcome);
            });
        }
        catch (OperationCanceledException)
        {
            // The user cancelled the scan, or the wizard closed under it. Not a failure.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The setup wizard could not run the first scan");
            Post(() =>
            {
                if (!IsDisposed)
                {
                    Summary = "The first scan could not be started. See the log for details.";
                }
            });
        }
        finally
        {
            Post(() =>
            {
                if (!IsDisposed)
                {
                    IsStarting = false;
                }
            });
        }
    }

    private bool CanStartScan()
        => !IsStarting && !(_scanStatus?.IsRunning ?? false) && !(_scanStatus?.ResolutionInProgress ?? false);

    /// <summary>Spec 12.1's cancel. The coordinator's own <c>Cancel()</c>, never a token this
    /// command owns.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        if (!CanCancelScan())
        {
            return;
        }

        _cancelScan();
    }

    private bool CanCancelScan() => _scanStatus?.IsRunning ?? false;

    /// <summary>The completion line, in the CLI's own order so the two report the same run the
    /// same way (spec 15).</summary>
    internal static string Describe(ScanRunOutcome outcome)
        => $"Scan {outcome.State}: {outcome.Discovered} discovered, {outcome.NewFiles} new, "
            + $"{outcome.ChangedFiles} changed, {outcome.Completed} completed, {outcome.Failed} failed, "
            + $"{outcome.SkippedCalibration} calibration skipped, {outcome.Removed} removed.";

    private void OnScanStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScanStatusService.IsRunning):
                OnPropertyChanged(nameof(IsScanRunning));
                OnPropertyChanged(nameof(IsScanIndeterminate));
                StartScanCommand.NotifyCanExecuteChanged();
                CancelScanCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ScanStatusService.ResolutionInProgress):
                StartScanCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ScanStatusService.Message):
                OnPropertyChanged(nameof(ScanMessage));
                break;
            case nameof(ScanStatusService.Percent):
                OnPropertyChanged(nameof(ScanPercent));
                break;
            case nameof(ScanStatusService.HasDeterminatePercent):
            case nameof(ScanStatusService.IsIndeterminate):
                OnPropertyChanged(nameof(IsScanIndeterminate));
                break;
        }
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        if (_scanStatus is not null)
        {
            _scanStatus.PropertyChanged -= OnScanStatusPropertyChanged;
        }
    }
}
