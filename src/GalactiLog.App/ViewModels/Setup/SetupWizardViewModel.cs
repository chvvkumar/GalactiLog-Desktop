using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Wizard;
using GalactiLog.Core.Io;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Setup;

/// <summary>
/// Design-spec 12.1's five-step setup wizard, shown when <c>general.setup_complete</c> is false and
/// reachable afterwards from the Settings Library tab as "Run setup again".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Next persists, then advances.</strong> Each step's own settings are written through
/// <c>SettingsStore.MutateGeneral</c> before <see cref="StepIndex"/> moves, so a failed save keeps
/// the user on the step with the message and changes nothing on disk. That is spec 12.1's own
/// sentence and the roadmap's named assertion for this row. Back does not save: the previous
/// step's values were already persisted when the user left it.
/// </para>
/// <para>
/// <strong>Finish writes <c>setup_complete</c> and only then closes.</strong> This is a deliberate
/// divergence from <c>SetupWizard.tsx</c>, whose <c>finish()</c> marks setup complete locally and
/// closes the dialog even when the write failed. A wizard that closes without having written the
/// flag reopens on the next start with every field to re-enter, which is worse than the four other
/// steps' behaviour, so a failed write keeps the user on the last step exactly as they do.
/// </para>
/// <para>
/// <strong>Filter seeding is first-run only</strong> (questions.md Q32). When
/// <c>setup_complete</c> was false at open, Finish also writes the five default folder-exclude
/// rules with the ids <c>setup-exclude-&lt;name&gt;</c>. On a re-run it writes nothing to the
/// filter document, because a user who tuned their exclude rules and then opened "Run setup again"
/// to change their latitude must not lose them.
/// </para>
/// <para>
/// Delegates, never the store and never the coordinator (spec 18.3). Every write goes through the
/// one <c>MutateGeneral</c> choke point, which reads, mutates, validates and writes under the
/// store's own gate, so the wizard and a Settings tab writing the same document cannot lose one
/// another's update.
/// </para>
/// </remarks>
public sealed partial class SetupWizardViewModel : WizardViewModel<SetupStepViewModel>
{
    /// <summary>The message shown for anything that is not a
    /// <c>SettingsValidationException</c>. The web's string, verbatim.</summary>
    public const string CouldNotSaveMessage = "Could not save this step";

    /// <summary>The message shown when the final write fails.</summary>
    public const string CouldNotFinishMessage = "Could not record setup completion";

    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> _mutate;
    private readonly Action? _navigateToDashboard;
    private readonly bool _setupWasComplete;

    /// <param name="load">Normally <c>SettingsStore.GetGeneral</c>. Read once, synchronously, as
    /// the wizard is built: the modal is opened on the UI thread from a click or from startup, the
    /// read is the same one <c>AppHost</c> already performs for <c>initialGeneral</c>, and a
    /// second background publish is exactly the interleaving the Settings tabs' shared spine had
    /// to be hardened against.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>. Not a
    /// <c>GetGeneral</c> plus <c>SaveGeneral</c> pair: the read, the mutation, the validation and
    /// the write happen under the store's gate in one critical section (Task 5 review
    /// escalation).</param>
    /// <param name="probe">The shared shallow probe, normally <c>SupportedFileProbe.Count</c> on a
    /// background thread.</param>
    /// <param name="volumeSpace">Normally <c>StorageTabViewModel.ReadVolumeSpace</c>.</param>
    /// <param name="defaultCacheRoot">The app data default,
    /// <c>Path.Combine(AppWriter.AppDataRoot, "thumbnails")</c>, which is what an empty
    /// <c>thumbnail_cache_dir</c> resolves to. Not the effective cache root.</param>
    /// <param name="runFirstScan">Normally
    /// <c>ScanCoordinator.RunAsync(ScanTrigger.FirstRun, roots, token)</c>, with the roots
    /// override.</param>
    /// <param name="cancelScan">Normally <c>ScanCoordinator.Cancel</c>.</param>
    /// <param name="navigateToDashboard">Spec 12.1's "navigates to the Dashboard". Normally the
    /// shell's own rail selection. Null in a test that is not about navigation.</param>
    /// <param name="scanStatus">The shared progress marshaller (spec 10.4).</param>
    /// <param name="systemTimezones">The observer timezone list. Defaults to the Location tab's
    /// own.</param>
    /// <param name="localTimezoneId">The system zone the combo defaults to.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="dataRoot">Where the app data root came from (spec 17.2). Passed straight into
    /// step 2; null leaves its data location block hidden.</param>
    /// <param name="requestDataRootMove">The one recorder of a picked data location.</param>
    /// <param name="cancelDataRootMove">Drops a requested move.</param>
    public SetupWizardViewModel(
        Func<GeneralSettings> load,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Func<string, CancellationToken, Task<int>> probe,
        Func<string, (long Total, long Free)?> volumeSpace,
        Func<string> defaultCacheRoot,
        Func<IReadOnlyList<string>, CancellationToken, Task<ScanRunOutcome>> runFirstScan,
        Action cancelScan,
        Action? navigateToDashboard = null,
        ScanStatusService? scanStatus = null,
        Func<IReadOnlyList<string>>? systemTimezones = null,
        Func<string>? localTimezoneId = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<AppDataRootResolution>? dataRoot = null,
        Func<string, string?>? requestDataRootMove = null,
        Action? cancelDataRootMove = null)
        : base(
            CreateSteps(
                probe, volumeSpace, defaultCacheRoot, runFirstScan, cancelScan, scanStatus,
                systemTimezones, localTimezoneId, post, logger ?? NullLogger.Instance,
                dataRoot, requestDataRootMove, cancelDataRootMove),
            logger)
    {
        _mutate = mutateGeneral;
        _navigateToDashboard = navigateToDashboard;

        // One read, one publish, on this thread, before anything can observe the steps. A failed
        // read leaves the defaults in place and reports: the wizard's whole job is to populate an
        // empty document, so there is nothing here it could overwrite badly.
        GeneralSettings document;
        try
        {
            document = load();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The setup wizard could not read the stored settings");
            document = new GeneralSettings();
            StepError = "The stored settings could not be read. The wizard is starting from the defaults.";
        }

        _setupWasComplete = document.SetupComplete;

        foreach (var step in Steps)
        {
            step.Load(document);
        }

        Steps[0].OnEntered();
    }

    private static SetupStepViewModel[] CreateSteps(
        Func<string, CancellationToken, Task<int>> probe,
        Func<string, (long Total, long Free)?> volumeSpace,
        Func<string> defaultCacheRoot,
        Func<IReadOnlyList<string>, CancellationToken, Task<ScanRunOutcome>> runFirstScan,
        Action cancelScan,
        ScanStatusService? scanStatus,
        Func<IReadOnlyList<string>>? systemTimezones,
        Func<string>? localTimezoneId,
        Action<Action>? post,
        ILogger logger,
        Func<AppDataRootResolution>? dataRoot,
        Func<string, string?>? requestDataRootMove,
        Action? cancelDataRootMove)
    {
        var folders = new ScanFoldersStepViewModel(probe, post, logger);
        var cache = new ThumbnailCacheStepViewModel(
            volumeSpace, defaultCacheRoot, post, logger, dataRoot, requestDataRootMove, cancelDataRootMove);
        var observer = new ObserverLocationStepViewModel(systemTimezones, localTimezoneId, post, logger);
        var options = new ScanOptionsStepViewModel(
            () => folders.Folders.Select(row => row.Path).ToArray(), post, logger);
        var firstScan = new FirstScanStepViewModel(
            () => folders.Folders.Select(row => row.Path).ToArray(),
            runFirstScan,
            cancelScan,
            scanStatus,
            post,
            logger);

        return [folders, cache, observer, options, firstScan];
    }

    /// <summary>True while a step's save or the final write is in flight.</summary>
    public bool IsSaving => IsBusy;

    /// <summary>Whether the wizard has already finished. Set before the close is requested, so a
    /// second Finish cannot write twice.</summary>
    public bool IsFinished => IsCommitted;

    /// <summary>Whether <c>setup_complete</c> was already true when the wizard opened, which is
    /// what makes this a re-run (questions.md Q32).</summary>
    internal bool SetupWasComplete => _setupWasComplete;

    /// <summary>
    /// Persists the current step before Next moves. Never the other way round: the roadmap's named
    /// assertion for this row. Back saves nothing, because the previous step's values were
    /// persisted when the user left it.
    /// </summary>
    protected override Task<bool> OnAdvancingAsync(SetupStepViewModel step)
        => PersistAsync(step, CouldNotSaveMessage);

    /// <summary>
    /// Spec 12.1's Finish: seeds the first run's scan filters, writes
    /// <c>setup_complete = true</c>, then closes and navigates to the Dashboard.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private Task FinishAsync()
    {
        if (!CanFinish())
        {
            return Task.CompletedTask;
        }

        return CompleteAsync();
    }

    private bool CanFinish() => !IsBusy && !IsCommitted && IsLastStep;

    /// <summary>
    /// The footer's "Skip setup" link, which calls the same completion path the web's does: a user
    /// who skips has still decided, and a wizard that reopens on every start after being skipped
    /// is not a skip.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSkip))]
    private Task SkipSetupAsync()
    {
        if (!CanSkip())
        {
            return Task.CompletedTask;
        }

        return CompleteAsync();
    }

    private bool CanSkip() => !IsBusy && !IsCommitted;

    // The one completion path, so Finish and Skip cannot drift apart.
    private async Task CompleteAsync()
    {
        // Captured before the mutation: it runs inside the store's write gate and must read
        // nothing observable.
        var seedFilters = !_setupWasComplete;

        // One mutation, not two. The web writes the filters and then the flag, which can leave an
        // install with seeded rules and setup_complete still false; one MutateGeneral makes the
        // pair atomic, so a refused write leaves the document exactly as it was.
        var saved = await PersistAsync(
            general =>
            {
                var next = general with { SetupComplete = true };
                return seedFilters
                    ? next with
                    {
                        ScanFilters = next.ScanFilters with
                        {
                            IncludePaths = [],
                            ExcludePaths = [],
                            NameRules = DefaultExcludeRules(),
                        },
                    }
                    : next;
            },
            CouldNotFinishMessage).ConfigureAwait(true);

        if (!saved)
        {
            // Divergence from the web, stated in this type's remarks: a failed write keeps the
            // user on the last step instead of closing anyway.
            return;
        }

        StepError = null;
        IsCommitted = true;

        try
        {
            _navigateToDashboard?.Invoke();
        }
        catch (Exception ex)
        {
            // Setup is recorded either way; a shell that could not be navigated is not a reason to
            // leave the wizard open over it.
            Logger.LogWarning(ex, "The setup wizard could not navigate to the Dashboard");
        }

        RequestClose(true);
    }

    /// <summary>
    /// The five default folder-exclude rules the web seeds on a first run, with the ids
    /// <c>setup-exclude-&lt;name&gt;</c> so they are identifiable and removable in the Library
    /// tab's rule list (questions.md Q32).
    /// </summary>
    /// <remarks>
    /// Phase 14B fixer, fixer list item 3 (Task 5 review escalation 1, ruled to the fixer). This
    /// used to be a second construction of the same five rules beside
    /// <see cref="ScanFilterConfig.SeededRules"/>, spelling the id prefix inline where Core
    /// publishes <see cref="ScanFilterConfig.SeededExcludeIdPrefix"/>. Either projection drifting
    /// would have made <c>ScanFilterConfig.IsOnlyTheSeededRules</c> stop recognising the wizard's
    /// own output, so spec 12.2's notice would never go down. There is one projection now and the
    /// wizard reaches it (design-lessons rule 1).
    /// </remarks>
    internal static IReadOnlyList<NameRule> DefaultExcludeRules() => ScanFilterConfig.SeededRules();

    private Task<bool> PersistAsync(SetupStepViewModel step, string genericMessage)
        => PersistAsync(step.Apply, genericMessage);

    // The one write path. The mutation is built on the UI thread from the step's controls and then
    // handed to the store off the UI thread, where it runs inside the store's own critical
    // section.
    private async Task<bool> PersistAsync(
        Func<GeneralSettings, GeneralSettings> mutate, string genericMessage)
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => _mutate(mutate)).ConfigureAwait(true);
            StepError = null;
            return true;
        }
        catch (SettingsValidationException ex)
        {
            // The store refused it. The inline validation on each step is a usability layer in
            // front of this, never a replacement for it (design-lessons rule 2).
            StepError = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The setup wizard could not save a step");
            StepError = genericMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Finish and Skip are this wizard's own commands, so the base's flags reach them here, and the
    // two names the setup tests and bindings read follow the base flags they forward.
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(IsBusy):
                OnPropertyChanged(nameof(IsSaving));
                FinishCommand.NotifyCanExecuteChanged();
                SkipSetupCommand.NotifyCanExecuteChanged();
                break;
            case nameof(IsCommitted):
                OnPropertyChanged(nameof(IsFinished));
                FinishCommand.NotifyCanExecuteChanged();
                SkipSetupCommand.NotifyCanExecuteChanged();
                break;
            case nameof(StepIndex):
                FinishCommand.NotifyCanExecuteChanged();
                break;
        }
    }
}
