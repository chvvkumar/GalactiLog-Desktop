using System.Text.RegularExpressions;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Tests.ViewModels.Wbpp;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Io;
using GalactiLog.Core.Survey;
using GalactiLog.Core.Wbpp;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Maintenance;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Spec 12's census rule for the job registry (PAR-015, ruling D1): "a test enumerates every action
// that can run longer than a second without blocking the window and asserts each one registers".
// Registration is a rule of the specification, not a convention each action remembers, which is
// design-lesson 2 applied to visibility instead of security: the choke point is the registry and
// this is what keeps a new pass from quietly skipping it.
//
// The set is declared here, as a list, and every member is proved BEHAVIOURALLY: each action is run
// against a real JobRegistry and the job it opened is what the assertion reads. No member needs the
// source-scan mechanism, because every one of them is reachable from a view-model a headless test
// can build and press.
//
// Phase 14B Task 4 added smart rebuild and catalogue identity backfill to the Maintenance tab,
// taking this set from seven members to nine. It could not do so silently:
// TheDeclaredMaintenanceSet_IsEveryCardOnTheTab fails the moment a card exists on the tab and is
// not named below.
//
// Phase 15A Task 4b added the guide-log pass and the correlation (ruling F5), taking it to eleven.
// Neither is a Maintenance card, so TheDeclaredMaintenanceSet_IsEveryCardOnTheTab is untouched by
// them; both are proved through the envelope seam, like the scan.
//
// Phase 15A Task 6b added TheCorrelationReRun_RegistersFromASettingsSaveToo, because member eleven
// has two entry points and the envelope seam proves only the in-scan one.
//
// Phase 21's fixer added the target page's two sends, taking the set to thirteen (phase review
// P2). They are the first members that are neither a scan phase nor a Maintenance card, and they
// are proved the same way: a real page, a real registry, the job the pressed item opened.
//
// Phase 22 added the Sky view image fetch as member fourteen, proved by running the service.
//
// The export wizard added the staging copy as member fifteen, proved by committing a copy over a
// fake disk.
//
// Phase 18 Task 4 added the Mosaics page's four kinds (spec 12.17), members sixteen to nineteen,
// proved by pressing Run Detection, Accept all, Dismiss all and Delete selected on a real page.
// Detection has a second entry point, a scan's pass, proved by its own case below.
public class JobRegistryCensusTest
{
    // snake_case, the same shape spec 10.9's action tokens already have. There is no second token
    // vocabulary: the scan's token is the scan's, and each Maintenance action's is its own spec
    // 10.9 action value.
    private static readonly Regex SnakeCase = new(
        @"^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>The scan (spec 10.3), registered through <c>ScanStatusService</c>.</summary>
    private const string TheScan = ScanStatusService.ScanJobKind;

    /// <summary>The guide-log pass of spec 10.3 step 5, census member ten. Its own job rather
    /// than a phase of the scan job, which is what spec 10.3 step 5 and spec 12's job monitor
    /// paragraph both say, and it registers through the same <c>ScanStatusService</c> envelope
    /// seam the scan does.</summary>
    private const string ThePhd2Ingest = ScanStatusService.Phd2IngestJobKind;

    /// <summary>The correlation of spec 7.6, census member eleven. The same token the out-of-scan
    /// re-run a profile map change dispatches registers under, so one activity has one kind
    /// however it was started.</summary>
    private const string ThePhd2Correlate = ScanStatusService.Phd2CorrelateJobKind;

    /// <summary>Every action on spec 12.7's Maintenance tab, by its own token.</summary>
    /// <remarks>
    /// The two thumbnail members of the spec's census sentence are already here:
    /// <c>reference_thumbnails</c> is <c>ReferenceThumbnailPass.Run</c> behind the tab and
    /// <c>frame_thumbnails</c> is <c>ThumbnailCache.Purge</c> behind it, so the sentence's "the
    /// eight Maintenance actions ... the reference thumbnail pass and the frame thumbnail
    /// regeneration" names them twice (questions.md Q3, built to the proposed answer: they count
    /// once, as Maintenance actions). <c>ThumbnailWorker</c> is not here and registers nothing: it
    /// is the on-demand LIFO render queue for thumbnails the UI asks for while browsing, it has no
    /// regenerate pass, and it reports no progress of any kind.
    /// </remarks>
    private static readonly string[] TheMaintenanceActions =
    [
        MaintenanceTabViewModel.RebuildTargetsAction,
        MaintenanceTabViewModel.RetryUnresolvedAction,
        MaintenanceTabViewModel.SmartRebuildAction,
        MaintenanceTabViewModel.CatalogIdentityBackfillAction,
        MaintenanceTabViewModel.ReferenceThumbnailsAction,
        MaintenanceTabViewModel.FrameThumbnailsAction,
        MaintenanceTabViewModel.PruneActivityAction,
        MaintenanceTabViewModel.ResetDatabaseAction,
    ];

    /// <summary>Spec 12.16's NINA send, census member twelve: a network call behind the page's
    /// overflow menu, which is a long-running action wherever it is started from.</summary>
    private const string TheNinaSend = TargetDetailViewModel.NinaSendJobKind;

    /// <summary>Spec 12.16's Stellarium slew, census member thirteen.</summary>
    private const string TheStellariumSend = TargetDetailViewModel.StellariumSendJobKind;

    /// <summary>Spec 11.3's Sky view image fetch, census member fourteen.</summary>
    private const string TheSurveyFetch = SurveyImageService.FetchJobKind;

    /// <summary>The export wizard's staging copy (wizard ruling R10), census member fifteen.</summary>
    private const string TheStackingCopy = WbppExportWizardViewModel.CopyJobKind;

    /// <summary>Spec 12.17's four Mosaics page kinds, census members sixteen to nineteen.</summary>
    private static readonly string[] TheMosaicActions =
    [
        MosaicsPageViewModel.DetectionJobKind,
        MosaicsPageViewModel.AcceptJobKind,
        MosaicsPageViewModel.DismissJobKind,
        MosaicsPageViewModel.DeleteJobKind,
    ];

    private static string[] TheCensusSet =>
        [
            TheScan,
            ThePhd2Ingest,
            ThePhd2Correlate,
            TheNinaSend,
            TheStellariumSend,
            TheSurveyFetch,
            TheStackingCopy,
            .. TheMaintenanceActions,
            .. TheMosaicActions,
        ];

    [Fact]
    public async Task EveryLongRunningAction_RegistersWithTheJobRegistry()
    {
        var observed = await ObserveEveryRegistrationAsync();

        Assert.Empty(MembersThatDidNotRegister(TheCensusSet, observed));
        Assert.Equal(TheCensusSet.Length, observed.Count);
    }

    // The other half of the rule: the declared list is only worth something if it cannot fall
    // behind the tab it describes. A ninth card with no entry here fails this case.
    [Fact]
    public void TheDeclaredMaintenanceSet_IsEveryCardOnTheTab()
    {
        using var tab = NewTab(jobs: null);

        Assert.Equal(
            TheMaintenanceActions.Order(StringComparer.Ordinal),
            tab.Actions.Select(action => action.Token).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryRegisteredKind_IsSnakeCase()
    {
        var observed = await ObserveEveryRegistrationAsync();

        Assert.NotEmpty(observed);
        Assert.All(observed, kind => Assert.Matches(SnakeCase, kind));
        Assert.All(TheCensusSet, kind => Assert.Matches(SnakeCase, kind));
    }

    // HelpPlacementCensusTest's own pattern: feed the census a member that does not register and
    // prove it reports it, so the rule is falsifiable rather than merely green.
    [Fact]
    public async Task TheCensus_FiresOnItsOwnOffender()
    {
        var observed = await ObserveEveryRegistrationAsync();

        // A declared action nothing registers is reported, not skipped.
        var withOffender = MembersThatDidNotRegister([.. TheCensusSet, "never_registers"], observed);
        Assert.Equal(["never_registers"], withOffender);

        // And a token that is not snake_case fails the second rule.
        Assert.DoesNotMatch(SnakeCase, "NeverRegisters");
        Assert.DoesNotMatch(SnakeCase, "never registers");

        // And the completeness half fires too: a declared list missing one of the tab's cards is
        // not the tab's card set.
        using var tab = NewTab(jobs: null);
        var shortList = TheMaintenanceActions.Take(TheMaintenanceActions.Length - 1);
        Assert.NotEqual(
            shortList.Order(StringComparer.Ordinal),
            tab.Actions.Select(action => action.Token).Order(StringComparer.Ordinal));
    }

    // Phase 15A Task 6b. Member eleven has two entry points, not one: the envelope seam above
    // proves the correlation that runs INSIDE a scan, and this proves the out-of-scan re-run a
    // settings save dispatches (spec 12.7). Without this case the census would be green while the
    // whole out-of-scan path registered nothing, because the token it would have skipped is
    // already in the observed set from the in-scan envelope. A real settings store over a real
    // database and a real registry: nothing here reaches into the registry to assert a kind it
    // put there itself.
    //
    // The subscription below is written the same way AppHost writes it, but it is this case's own
    // and not AppHost's (Task 6b review P2-3), so this case says nothing about whether AppHost
    // still makes that subscription. Phd2ReRunHostWiringTests is what covers that, through
    // AppHost.Build itself; this case covers the census member.
    [Fact]
    public async Task TheCorrelationReRun_RegistersFromASettingsSaveToo()
    {
        using var database = new TempDatabase("galactilog-census-phd2");
        var registry = new JobRegistry(action => action());
        var store = new SettingsStore(new SettingsRepository(database.ConnectionString));
        var runner = new Phd2CorrelationRunner(
            database.ConnectionString,
            store.GetGeneral,
            () => new AliasMap(store.GetFilters(), store.GetEquipment()),
            () => new NoOpLease(),
            jobs: registry);

        // The one subscription AppHost makes, written here the same way.
        store.Phd2GuidingInputsChanged += (_, _) => runner.Queue();

        // A real map change through the store's own write path, which is the only thing that
        // raises the event and which compares the normalised map rather than the reference.
        store.MutateGeneral(general => general with
        {
            Phd2ProfileMap = Phd2Profiles.ToJson(new Dictionary<string, Phd2ProfileEntry>
            {
                ["Rig A"] = new() { Telescope = "Askar 120" },
            }),
        });

        await runner.InFlight;

        var job = Assert.Single(registry.Recent);
        Assert.Equal(ThePhd2Correlate, job.Kind);
        Assert.Matches(SnakeCase, job.Kind);
    }

    // Phase 18 Task 4. Member sixteen's second entry point: a scan's detection pass, through the
    // envelope seam ScanStatusService wraps, the way the PHD2 phases are proved.
    [Fact]
    public void TheMosaicDetection_RegistersFromAScanToo()
    {
        var registry = new JobRegistry(action => action());
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action(), jobs: registry);

        coordinator.RaiseProgress(ScanTaskNames.MosaicDetection, 1, 4, "Relabelling frames", force: true);
        coordinator.RaiseProgress(ScanTaskNames.MosaicDetection, 4, 4, "2 suggestions", force: true);
        coordinator.RaiseProgress(ScanTaskNames.RefThumbnails, 0, 1, "Reference thumbnails", force: true);

        var job = Assert.Single(registry.Recent, candidate => candidate.Kind == MosaicsPageViewModel.DetectionJobKind);
        Assert.Equal("2 suggestions", job.Summary);
        Assert.Equal(JobResult.Succeeded, job.Result);
    }

    // And a failed pass ends its job failed, from the -1 terminal envelope.
    [Fact]
    public void AFailedScanDetectionPass_EndsItsJobFailed()
    {
        var registry = new JobRegistry(action => action());
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action(), jobs: registry);

        coordinator.RaiseProgress(ScanTaskNames.MosaicDetection, 1, 4, "Relabelling frames", force: true);
        coordinator.RaiseProgress(
            ScanTaskNames.MosaicDetection, 0, MosaicDetectionPass.FailedEnvelopeTotalSteps, "Mosaic detection failed: boom", force: true);

        var job = Assert.Single(registry.Recent, candidate => candidate.Kind == MosaicsPageViewModel.DetectionJobKind);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Equal("Mosaic detection failed: boom", job.Summary);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class NoOpLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>Runs every member of the census set against one real registry and returns the kind
    /// tokens the jobs it opened carry.</summary>
    private static async Task<HashSet<string>> ObserveEveryRegistrationAsync()
    {
        var registry = new JobRegistry(action => action());

        // Read after each group rather than once at the end: Recent is capped at RecentCap, and
        // the census now runs more actions than that, so a kind observed early would be evicted.
        var observed = new HashSet<string>(StringComparer.Ordinal);
        void Collect() =>
            observed.UnionWith(registry.Running.Concat(registry.Recent).Select(job => job.Kind));

        // The scan, through ScanStatusService, which is its registration site: a progress envelope
        // on a bare coordinator is what a real scan's first envelope does.
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using (var status = new ScanStatusService(coordinator, action => action(), jobs: registry))
        {
            coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);

            // The two PHD2 phases are proved the same way the scan is, through the envelopes a
            // real pass raises: nothing here reaches into the registry to assert a kind it put
            // there itself. Each opens its own sub-job because its task differs from the one
            // before it (Task 5 raises the second envelope for real; the seam is the same).
            coordinator.RaiseProgress(ScanTaskNames.Phd2Ingest, 1, 2, "Read 1/2 PHD2 guide logs", force: true);
            coordinator.RaiseProgress(
                ScanTaskNames.Phd2Correlate, 1, 3, "Matching guiding to frames", force: true);
        }

        Collect();

        // Phase 21's two page-level sends, by pressing the submenu item a configured instance puts
        // on a real target page. Built with neither client and with no socket: the handle opens
        // before the client call, so the page registers the job it then fails.
        using (var page = NewTargetPage(registry))
        {
            if (page.PendingLoad is { } load)
            {
                await load.WaitAsync(TimeSpan.FromSeconds(30));
            }

            await page.NinaSendItems.Single().SendCommand.ExecuteAsync(null);
            await page.StellariumSendItems.Single().SendCommand.ExecuteAsync(null);
        }

        Collect();

        // Phase 22's survey image fetch: a real service over a stub handler and a temp cache root,
        // on a miss, which is the only path that opens a job.
        var surveyRoot = Path.Combine(Path.GetTempPath(), $"galactilog-census-survey-{Guid.NewGuid():N}");
        try
        {
            using var http = new HttpClient(new NotFoundHandler());
            var survey = new SurveyImageService(
                new Hips2FitsClient(http),
                new AppWriter(surveyRoot, dataRootPointerPath: Path.Combine(surveyRoot, "datapath.json")),
                registry,
                () => new GeneralSettings { SurveyDownloadsEnabled = true });
            await survey.GetAsync(
                Guid.NewGuid(), new SurveyView(Surveys.DefaultId, 10, 20, 1), false, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(surveyRoot))
            {
                Directory.Delete(surveyRoot, recursive: true);
            }
        }

        Collect();

        // The export wizard's staging copy: a real wizard over the export harness, committed with
        // Copy now over a fake disk, so nothing is copied and the handle still opens.
        using (var harness = ExportHarness.Create(
                   new Library().Frame(new DateOnly(2025, 3, 20), Path.Combine(Library.Root, "M31", "a.fits")),
                   [new DateOnly(2025, 3, 20)]))
        using (var wizard = new WbppExportWizardViewModel(
                   harness.Page, _ => Task.CompletedTask, registry, post: action => action()))
        {
            harness.Page.StagingIoFor = _ => new StagingIo(
                _ => new MemoryStream(), _ => [], _ => null, _ => { }, _ => new MemoryStream());
            harness.Page.StagingPath = harness.TempFolder("staging");
            harness.Page.CommitStagingCommand.Execute(null);
            for (var i = 0; i < 4; i++)
            {
                await wizard.NextCommand.ExecuteAsync(null);
            }

            await wizard.CommitCommand.ExecuteAsync(null);
        }

        Collect();

        // The Mosaics page's four kinds, by pressing each on a real page over delegate stubs.
        using (var mosaics = new MosaicsPageHarness(
                   new MosaicsBackend
                   {
                       RunDetection = (_, _) => Task.FromResult<MosaicDetectionResult?>(new MosaicDetectionResult(0, 0, 0, 0, 0)),
                       ListPending = () => [new GalactiLog.Data.Repositories.MosaicSuggestionRow(
                           Guid.NewGuid(), "M 31", "M 31", [], "high", "name", null, [], "sig", DateTime.UtcNow)],
                       ListMosaics = () => [new GalactiLog.Data.Queries.MosaicListRow(Guid.NewGuid(), "M 33", 0, 0, 0, null, null, [])],
                   },
                   jobs: registry))
        {
            var page = mosaics.Page;
            await page.PendingLoad;
            await page.RunDetectionCommand.ExecuteAsync(null);
            await page.PendingLoad;
            await page.AcceptAllCommand.ExecuteAsync(null);
            await page.PendingLoad;
            Collect();
            await page.DismissAllCommand.ExecuteAsync(null);
            await page.DismissAllCommand.ExecuteAsync(null);
            await page.PendingLoad;
            page.Table.Mosaics[0].IsSelected = true;
            await page.Table.DeleteSelectedCommand.ExecuteAsync(null);
            await page.Table.DeleteSelectedCommand.ExecuteAsync(null);
            await page.PendingLoad;
        }

        Collect();

        // Every Maintenance action, by pressing its own button on a real tab.
        using (var tab = NewTab(registry))
        {
            foreach (var token in TheMaintenanceActions)
            {
                var action = tab.Action(token);
                var command = action.Buttons[0].Command;

                if (action.RequiresConfirm && !action.ConfirmPending)
                {
                    command.Execute(null);
                    if (command.ExecutionTask is { } arm)
                    {
                        await arm;
                    }
                }

                command.Execute(null);
                if (command.ExecutionTask is { } run)
                {
                    await run;
                }
            }
        }

        Collect();
        return observed;
    }

    private static string[] MembersThatDidNotRegister(
        IEnumerable<string> declared, IReadOnlySet<string> observed)
        => [.. declared.Where(kind => !observed.Contains(kind)).Order(StringComparer.Ordinal)];

    // One target page with one offered instance per kind, over the public constructor: the page
    // takes no clients here, which is the seam that lets the census press both sends with no
    // network at all.
    private static TargetDetailViewModel NewTargetPage(JobRegistry jobs)
    {
        var instances = IntegrationSettings.WriteInstances(
            [new IntegrationInstance("Obsy1", "http://a.local", true)]);

        return new TargetDetailViewModel(
            TargetDetailViewModelTestFactory.ResolvedGroupKey,
            _ => TargetDetailViewModelTestFactory.PopulatedDetail(sessions: []),
            (_, _, _) => throw new InvalidOperationException("The census page carries no session."),
            (_, _) => RenameOutcome.Renamed,
            (_, _) => { },
            (_, _, _) => Task.FromResult((false, "")),
            new ShellIntegration(_ => Task.CompletedTask, _ => null),
            new ChartSelectionViewModel(
                new GraphSettings(),
                new GraphSettingsWriter(() => new GraphSettings(), _ => { }),
                () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings())),
            post: action => action(),
            jobs: jobs,
            getGeneral: () => new GeneralSettings
            {
                NinaInstancesDocument = instances,
                StellariumInstancesDocument = instances,
            });
    }

    // Delegates everywhere, so nothing here opens a database, a cache root or a window
    // (design-spec 18.3). The same shape MaintenanceTabViewTests.NewTab uses.
    private static MaintenanceTabViewModel NewTab(JobRegistry? jobs) => new(
        (_, _) => new TargetRebuild.RebuildOutcome(0, 0, 0, 0, 0, 0),
        (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
        (_, _) => new SmartRebuild.SmartRebuildOutcome(0, 0, 0, 0, 0, 0, 0),
        (_, _) => new CatalogIdentityBackfill.BackfillOutcome(0, 0, 0),
        (_, _, _) => Task.FromResult<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>(
            new ReferenceThumbnailPass.ReferenceThumbnailOutcome(0, 0, 0, false)),
        _ => 0,
        () => 90,
        _ => 0,
        () => Task.FromResult<(string?, bool)>(("Database reset.", false)),
        post: action => action(),
        jobs: jobs);
}
