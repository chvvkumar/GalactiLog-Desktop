using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Setup;
using GalactiLog.Core.Scanning;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.1's setup wizard. The roadmap's four named assertions live in
// SetupStepPersistenceTests so they cannot be lost among the rest; everything else about the five
// steps, their validation and their commands is here.
public class SetupWizardViewModelTests
{
    [Fact]
    public void Wizard_HasFiveSteps_InTheSpecOrder()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.Equal(5, harness.ViewModel.Steps.Count);
        Assert.Equal(
            ["Scan folders", "Storage locations", "Observer location", "Scan options", "First scan"],
            harness.ViewModel.Steps.Select(step => step.Title));
    }

    [Fact]
    public async Task StepHeader_ReadsStepNOfFive()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        Assert.Equal("Step 1 of 5: Scan folders", harness.ViewModel.StepHeader);

        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal("Step 2 of 5: Storage locations", harness.ViewModel.StepHeader);
    }

    // The roadmap's first named assertion.
    [Fact]
    public void Step1_NextIsDisabledUntilAFolderIsChosen()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.Empty(harness.Folders.Folders);
        Assert.False(harness.Folders.CanAdvance);
        Assert.False(harness.ViewModel.CanGoNext);
        Assert.False(harness.ViewModel.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Step1_NextIsEnabledOnceOneFolderIsChosen()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.True(harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder));

        Assert.True(harness.ViewModel.CanGoNext);
        Assert.True(harness.ViewModel.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Step1_RemovingTheLastFolder_DisablesNextAgain()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);

        harness.Folders.RemoveFolderCommand.Execute(harness.Folders.Folders[0]);

        Assert.Empty(harness.Folders.Folders);
        Assert.False(harness.ViewModel.CanGoNext);
    }

    [Fact]
    public void Step1_AddingADuplicateRoot_IsRefused()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);

        Assert.False(harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder));

        Assert.Single(harness.Folders.Folders);

        // FIXER LIST F10 and the Task 9 review's minor 2: one rule, one sentence. This step and
        // the Settings Library tab both show ScanFilterConfig.RefuseScanRoot's wording, which is
        // also what ScanFilterConfig.Validate throws on the write path.
        Assert.Contains("already a library folder", harness.Folders.ErrorMessage);
    }

    [Theory]
    [InlineData(@"C:\Astro\Captures\2025")]
    [InlineData(@"C:\Astro")]
    public void Step1_AddingANestedRoot_IsRefused(string nested)
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);

        Assert.False(harness.Folders.AddFolder(nested));

        Assert.Single(harness.Folders.Folders);
        Assert.Contains("may not be nested", harness.Folders.ErrorMessage);
    }

    [Fact]
    public void Step1_ARelativeFolder_IsRefused()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.False(harness.Folders.AddFolder(@"Astro\Captures"));

        Assert.Empty(harness.Folders.Folders);
        Assert.Contains("absolute path", harness.Folders.ErrorMessage);
    }

    [Fact]
    public async Task Step1_ShowsAPerFolderCountAndATotal()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.ProbeCounts[SetupWizardViewModelTestFactory.Folder] = 42;
        harness.ProbeCounts[SetupWizardViewModelTestFactory.SecondFolder] = 8;

        // FIXER LIST (Phase 10 code item 1), TRACKING section 2 item 8's family. One folder at a
        // time, each probe awaited before the next folder is added.
        //
        // The two adds used to run back to back with one await after them, and that is a race
        // rather than a sequence. The factory's post seam runs its closure inline, so the probe's
        // publish (row.Count, row.CountText and RefreshTotal) runs on the pool thread the probe
        // finished on, while the test thread is inside the next AddFolder raising
        // CollectionChanged, which calls RefreshTotal too. Two RefreshTotal runs can then
        // interleave: the one that read "a folder still has no count" can write its
        // "Counting supported files..." line after the one that read both counts wrote the total,
        // and the assertion below sees the stale line. Under a starved thread pool the window is
        // wide, which is why this failed once in five full runs and never when filtered.
        //
        // In production there is no race to fix: the post seam is UiPost.Default and AddFolder is
        // called from the UI thread, so RefreshTotal only ever runs on that one thread. Awaiting
        // each probe is what gives the test the same single-threaded ordering.
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        await harness.Folders.PendingProbes;
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.SecondFolder);
        await harness.Folders.PendingProbes;

        // Spec 12.1, fixer-list item 40: the label reads as an estimate, not a bare count.
        Assert.Equal("~42 supported files", harness.Folders.Folders[0].CountText);
        Assert.Equal("~8 supported files", harness.Folders.Folders[1].CountText);
        Assert.Contains("~50 supported files found in 2 folders", harness.Folders.TotalText);
        Assert.EndsWith("(estimate).", harness.Folders.TotalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step1_AFolderAtTheProbeCeiling_ReadsOrMore()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.ProbeCounts[SetupWizardViewModelTestFactory.Folder] = SupportedFileProbe.MaxCounted;

        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        await harness.Folders.PendingProbes;

        // The ceiling path keeps SupportedFileProbe's own wording and simply gains the tilde like
        // every other count: "~1000 or more" tells a reader the truth twice, which is acceptable.
        Assert.Equal("~1000 or more supported files", harness.Folders.Folders[0].CountText);
        Assert.Contains("~1000 or more supported files found", harness.Folders.TotalText);
    }

    // Carried fixer-list item 40 (spec 12.1, questions.md Q15): a shallow probe undercounts a
    // folder that nests its support files more than three levels down, so the step must not read
    // as a promise the first real scan then contradicts. Both strings read as an estimate.
    [Fact]
    public async Task TheFolderCount_ReadsAsAnEstimate()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.ProbeCounts[SetupWizardViewModelTestFactory.Folder] = 254;

        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        await harness.Folders.PendingProbes;

        Assert.StartsWith("~", harness.Folders.Folders[0].CountText, StringComparison.Ordinal);
        Assert.StartsWith("~", harness.Folders.TotalText, StringComparison.Ordinal);
        Assert.Contains("(estimate)", harness.Folders.TotalText, StringComparison.Ordinal);
    }

    // ---- step 2's data location (spec 12.1, 17.2, Phase 10 Task 9) ----------------------------
    //
    // Every case below runs on lambdas and constants: no pointer file is read or written, no
    // directory is created, and no path used has to exist.

    [Fact]
    public void StorageStep_TitleIsStorageLocations()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.Equal("Storage locations", harness.Cache.Title);
    }

    [Fact]
    public void Wizard_StillHasFiveSteps()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.Equal(5, harness.ViewModel.Steps.Count);
    }

    [Fact]
    public void StorageStep_ShowsTheDataLocationAndTheThumbnailCache()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.True(harness.Cache.HasDataLocation);
        Assert.Equal(SetupWizardViewModelTestFactory.DataRoot, harness.Cache.DataRootPath);
        Assert.False(string.IsNullOrWhiteSpace(harness.Cache.DataRootSourceText));
        Assert.False(string.IsNullOrWhiteSpace(harness.Cache.CachePath));
    }

    [Fact]
    public void StorageStep_SetDataRoot_RecordsAPendingMoveAndDoesNotBlockNext()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        harness.Cache.SetDataRoot(@"D:\Astro\GalactiLogData");

        Assert.Equal([@"D:\Astro\GalactiLogData"], harness.MoveRequests);
        Assert.Equal(@"D:\Astro\GalactiLogData", harness.Cache.PendingDataRoot);
        Assert.True(harness.Cache.HasPendingMove);
        Assert.Null(harness.Cache.DataRootError);
        Assert.True(harness.Cache.CanAdvance);
    }

    [Fact]
    public void StorageStep_SetDataRoot_ARefusal_ShowsAtTheFieldAndStillAllowsNext()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        harness.Cache.SetDataRoot("Astro");

        Assert.Equal(StorageTabViewModel.DataRootMustBeAbsolute, harness.Cache.DataRootError);
        Assert.Empty(harness.MoveRequests);
        Assert.Null(harness.Cache.PendingDataRoot);
        Assert.True(harness.Cache.CanAdvance);

        harness.MoveRefusal = StorageTabViewModel.DataRootHasDatabase;
        harness.Cache.SetDataRoot(@"D:\Astro\GalactiLogData");

        Assert.Equal(StorageTabViewModel.DataRootHasDatabase, harness.Cache.DataRootError);
        Assert.Null(harness.Cache.PendingDataRoot);
        Assert.True(harness.Cache.CanAdvance);
    }

    [Fact]
    public void StorageStep_CancelDataRootMove_ClearsThePendingRootAndCallsTheDelegate()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Cache.SetDataRoot(@"D:\Astro\GalactiLogData");
        Assert.True(harness.Cache.CancelDataRootMoveCommand.CanExecute(null));

        harness.Cache.CancelDataRootMoveCommand.Execute(null);

        Assert.Equal(1, harness.MoveCancels);
        Assert.Null(harness.Cache.PendingDataRoot);
        Assert.False(harness.Cache.CancelDataRootMoveCommand.CanExecute(null));
    }

    [Fact]
    public void StorageStep_Apply_WritesNoDataRootKeyIntoTheGeneralDocument()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Cache.SetDataRoot(@"D:\Astro\GalactiLogData");

        var before = harness.Stored;
        var after = harness.Cache.Apply(before);

        Assert.Equal(before with { ThumbnailCacheDir = after.ThumbnailCacheDir }, after);
    }

    [Fact]
    public void StorageStep_NotesComeFromTheStorageTabConstants()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Cache.SetDataRoot(@"D:\Astro\GalactiLogData");

        Assert.Same(StorageTabViewModel.RelocationNoteText, harness.Cache.RelocationNote);
        Assert.Same(StorageTabViewModel.DataRootNoteText, harness.Cache.DataRootNote);
        Assert.Contains(@"D:\Astro\GalactiLogData", harness.Cache.PendingMoveNote, StringComparison.Ordinal);
        Assert.Contains(SetupWizardViewModelTestFactory.DataRoot, harness.Cache.PendingMoveNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Step2_DefaultsToTheResolvedAppDataThumbnailPath()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.Equal(SetupWizardViewModelTestFactory.DefaultCacheRoot, harness.Cache.CachePath);
        Assert.Null(harness.Cache.PathError);
        Assert.True(harness.Cache.CanAdvance);
    }

    // Review Important finding 1, the step's half: a re-run seeds the box with the stored custom
    // path, and "is this the default" is asked against the app data default rather than against
    // the effective cache root, so a user who re-runs setup and changes nothing on step 2 keeps
    // the volume they chose instead of being moved back to %LOCALAPPDATA%.
    [Fact]
    public async Task Step2_OnAReRun_AnUntouchedCustomPath_IsStoredVerbatim()
    {
        const string Custom = @"E:\Astro\thumbnails";
        using var harness = SetupWizardViewModelTestFactory.Create(
            seed: general => general with { SetupComplete = true, ThumbnailCacheDir = Custom });

        Assert.Equal(Custom, harness.Cache.CachePath);

        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        await harness.ViewModel.NextCommand.ExecuteAsync(null);
        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal(2, harness.ViewModel.StepIndex);
        Assert.Equal(Custom, harness.Stored.ThumbnailCacheDir);
    }

    // The other half of the same finding, which only the AppHost binding can hold: the wizard's
    // defaultCacheRoot is the app data default, never appWriter.ThumbnailCacheRoot, which is the
    // effective root and equals whatever custom path is stored. Read as text, the way
    // Step5_StartScan_UsesTheFirstRunTrigger reads the same registration.
    [Fact]
    public void Step2_DefaultCacheRoot_IsBoundToTheAppDataDefault_NotTheEffectiveRoot()
    {
        var registration = ReadWizardRegistration();

        Assert.Contains("appWriter.AppDataRoot", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("() => appWriter.ThumbnailCacheRoot", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void Step2_RelativePath_IsAnInlineError()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        harness.Cache.CachePath = @"thumbnails\here";

        Assert.Equal(StorageTabViewModel.ValidateCacheDir(@"thumbnails\here"), harness.Cache.PathError);
        Assert.Contains("absolute path", harness.Cache.PathError);
        Assert.False(harness.Cache.CanAdvance);
    }

    [Fact]
    public void Step2_BareDriveRoot_IsAnInlineError()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        harness.Cache.CachePath = @"D:\";

        Assert.Contains("bare drive or UNC share root", harness.Cache.PathError);
        Assert.False(harness.Cache.CanAdvance);
    }

    // TRACKING section 2 item 8: a test asserting off-UI-thread work awaits it, never blocks.
    [Fact]
    public async Task Step2_FreeSpaceIsReadOffTheUiThread()
    {
        var readingThread = 0;
        using var harness = SetupWizardViewModelTestFactory.Create(
            volumeSpace: _ =>
            {
                readingThread = Environment.CurrentManagedThreadId;
                return (100L, 25L);
            });

        var callingThread = Environment.CurrentManagedThreadId;
        harness.Cache.CachePath = @"E:\cache\thumbnails";
        await harness.Cache.PendingFreeSpace!;

        Assert.NotEqual(callingThread, readingThread);
        Assert.Equal(StorageTabViewModel.FormatVolumeSpace((100L, 25L)), harness.Cache.FreeSpaceText);
    }

    [Fact]
    public async Task Step2_AnUnreadableVolume_DegradesToOneLine()
    {
        using var harness = SetupWizardViewModelTestFactory.Create(volumeSpace: _ => null);

        harness.Cache.CachePath = @"\\nas\astro\cache";
        await harness.Cache.PendingFreeSpace!;

        Assert.Equal(StorageTabViewModel.FreeSpaceUnavailable, harness.Cache.FreeSpaceText);
    }

    [Theory]
    [InlineData("-90.1")]
    [InlineData("90.1")]
    public async Task Step3_LatitudeOutOfRange_IsAnInlineError_AndBlocksNext(string latitude)
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await Advance(harness, 2);

        harness.Observer.LatitudeText = latitude;

        Assert.Equal(LocationTabViewModel.LatitudeRangeMessage, harness.Observer.LatitudeError);
        Assert.False(harness.Observer.CanAdvance);
        Assert.False(harness.ViewModel.CanGoNext);
    }

    [Theory]
    [InlineData("-180.1")]
    [InlineData("180.1")]
    public async Task Step3_LongitudeOutOfRange_IsAnInlineError_AndBlocksNext(string longitude)
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await Advance(harness, 2);

        harness.Observer.LongitudeText = longitude;

        Assert.Equal(LocationTabViewModel.LongitudeRangeMessage, harness.Observer.LongitudeError);
        Assert.False(harness.Observer.CanAdvance);
        Assert.False(harness.ViewModel.CanGoNext);
    }

    [Fact]
    public async Task Step3_BlankCoordinates_AreAllowed_AndStoreNull()
    {
        using var harness = SetupWizardViewModelTestFactory.Create(
            seed: general => general with { ObserverLatitude = 12d, ObserverLongitude = 34d });
        await Advance(harness, 2);

        harness.Observer.LatitudeText = "";
        harness.Observer.LongitudeText = "";

        Assert.True(harness.Observer.CanAdvance);

        // Two Next presses: the first is consumed by the blank-longitude nudge (questions.md Q34).
        await harness.ViewModel.NextCommand.ExecuteAsync(null);
        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Null(harness.Stored.ObserverLatitude);
        Assert.Null(harness.Stored.ObserverLongitude);
    }

    [Fact]
    public async Task Step3_BlankLongitude_NudgesOnceThenAccepts()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await Advance(harness, 2);
        harness.Observer.LongitudeText = "";

        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal(2, harness.ViewModel.StepIndex);
        Assert.Equal(ObserverLocationStepViewModel.BlankLongitudeNudge, harness.ViewModel.StepError);

        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal(3, harness.ViewModel.StepIndex);
        Assert.Null(harness.ViewModel.StepError);
    }

    [Fact]
    public void Step3_TimezoneDefaultsToTheSystemZone()
    {
        using var harness = SetupWizardViewModelTestFactory.Create(
            timezones: () => ["Etc/UTC", "Europe/London", "America/New_York"],
            localTimezoneId: () => "Europe/London");

        Assert.Equal("Europe/London", harness.Observer.SelectedTimezone?.Id);
        Assert.Equal(LocationTabViewModel.NotConfiguredLabel, harness.Observer.Timezones[0].Label);
        Assert.Equal("", harness.Observer.Timezones[0].Id);
    }

    [Fact]
    public void Step3_TimezonesAreBuiltByTheSharedBuilder_OffsetOrderedWithGmtPrefixes()
    {
        // Runs the production resolver (TimeZoneInfo.FindSystemTimeZoneById; neither
        // SetupWizardViewModel nor ObserverLocationStepViewModel exposes a resolver seam, only
        // the zone id list), so, like LocationTabViewModelTests' matching cases, this checks only
        // the GMT offset prefix and does not assume an order between Etc/UTC and Europe/London,
        // which share an offset and whose relative order is this machine's display names to
        // decide (review task8-review.md P3 finding, extended here to this case for the same
        // reason).
        using var harness = SetupWizardViewModelTestFactory.Create(
            timezones: () => ["Etc/UTC", "Europe/London", "America/New_York"],
            localTimezoneId: () => "Europe/London");

        // The special first entry keeps its place and its wording, ahead of every zone entry
        // (user ruling U4).
        Assert.Equal("", harness.Observer.Timezones[0].Id);
        Assert.Equal(LocationTabViewModel.NotConfiguredLabel, harness.Observer.Timezones[0].Label);

        var zoneOptions = harness.Observer.Timezones.Skip(1).ToList();
        Assert.Equal(3, zoneOptions.Count);

        Assert.Equal("America/New_York", zoneOptions[0].Id);
        Assert.StartsWith(TimezoneLabels.GmtPrefix("America/New_York"), zoneOptions[0].Label);

        Assert.Equal(
            new HashSet<string> { "Etc/UTC", "Europe/London" },
            zoneOptions.Skip(1).Select(option => option.Id).ToHashSet());
        Assert.All(
            zoneOptions.Skip(1),
            option => Assert.StartsWith(TimezoneLabels.GmtPrefix(option.Id), option.Label));
    }

    [Fact]
    public void Step3_AStoredZoneWins_OverTheSystemZone()
    {
        using var harness = SetupWizardViewModelTestFactory.Create(
            seed: general => general with { ObserverTimezone = "Etc/UTC" },
            timezones: () => ["Etc/UTC", "Europe/London"],
            localTimezoneId: () => "Europe/London");

        Assert.Equal("Etc/UTC", harness.Observer.SelectedTimezone?.Id);
    }

    [Fact]
    public void Step4_IntervalPresets_AreTheSeven_AndDefaultToFourHours()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.Equal(
            [60, 120, 240, 360, 480, 720, 1440],
            harness.Options.IntervalOptions.Select(option => option.Minutes));

        // Spec 12.1's "default 4 hours", not the web wizard's local 360.
        Assert.Equal(240, harness.Options.SelectedInterval?.Minutes);
        Assert.Equal(240, ScanOptionsStepViewModel.DefaultIntervalMinutes);
    }

    [Fact]
    public void Step4_WatcherDefaultsToOn()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.True(harness.Options.WatcherEnabled);
    }

    [Fact]
    public void Step4_IncludeCalibrationDefaultsToOff()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.False(harness.Options.IncludeCalibration);
    }

    [Fact]
    public async Task Step4_NamesTheFiveDefaultExclusions_AndTheChosenFolders()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await Advance(harness, 3);

        foreach (var name in ScanOptionsStepViewModel.ExcludeDefaults)
        {
            Assert.Contains(name, harness.Options.Intro);
        }

        Assert.Contains(SetupWizardViewModelTestFactory.Folder, harness.Options.Intro);
    }

    [Fact]
    public async Task Step5_StartScan_UsesTheRootsOverride()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await Advance(harness, 4);

        await harness.FirstScan.StartScanCommand.ExecuteAsync(null);

        var roots = Assert.Single(harness.ScanRoots);
        Assert.Equal([SetupWizardViewModelTestFactory.Folder], roots);
    }

    // The trigger is baked into the AppHost binding, which is the only place it can be. Read as
    // text, the way Task 8's AppHost binding assertions are, because App.Tests cannot resolve a
    // real host against the true %LOCALAPPDATA%.
    [Fact]
    public void Step5_StartScan_UsesTheFirstRunTrigger()
        => Assert.Contains("ScanTrigger.FirstRun", ReadWizardRegistration(), StringComparison.Ordinal);

    [Fact]
    public async Task Step5_Cancel_CallsTheCancelDelegate()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = SetupWizardViewModelTestFactory.Create(scanStatus: status);
        await Advance(harness, 4);

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);

        Assert.True(harness.FirstScan.CancelScanCommand.CanExecute(null));
        harness.FirstScan.CancelScanCommand.Execute(null);

        Assert.Equal(1, harness.Cancels);
    }

    [Fact]
    public async Task Step5_ProgressMirrorsScanStatusService()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = SetupWizardViewModelTestFactory.Create(scanStatus: status);
        await Advance(harness, 4);

        coordinator.RaiseProgress(ScanTaskNames.Classify, 3, 10, "Classifying 3/10 files", force: true);

        Assert.True(harness.FirstScan.IsScanRunning);
        Assert.Equal("Classifying 3/10 files", harness.FirstScan.ScanMessage);
        Assert.Equal(30d, harness.FirstScan.ScanPercent);
        Assert.False(harness.FirstScan.IsScanIndeterminate);
    }

    [Fact]
    public async Task Step5_StartScan_IsRefusedWhileAScanIsRunning()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = SetupWizardViewModelTestFactory.Create(scanStatus: status);
        await Advance(harness, 4);

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);

        Assert.False(harness.FirstScan.StartScanCommand.CanExecute(null));
        await harness.FirstScan.StartScanCommand.ExecuteAsync(null);

        Assert.Empty(harness.ScanRoots);
    }

    [Fact]
    public async Task Step5_AFinishedScan_ShowsASummary()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await Advance(harness, 4);

        await harness.FirstScan.StartScanCommand.ExecuteAsync(null);

        Assert.Equal(
            FirstScanStepViewModel.Describe(SetupWizardViewModelTestFactory.Outcome()),
            harness.FirstScan.Summary);
    }

    // Review Important finding 2: finishing the wizard disposes the page through ModalHost's
    // cleanup, which cancels every step's lifetime. A scan already running must survive that, so
    // it is started on CancellationToken.None and cancelled only through the Cancel button.
    [Fact]
    public async Task Step5_AScanInFlight_IsNotCancelledByFinishing()
    {
        using var release = new ManualResetEventSlim(false);
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.ScanRelease = release;
        await Advance(harness, 4);

        var scan = harness.FirstScan.StartScanCommand.ExecuteAsync(null);
        await WaitForTheScanToStart(harness);

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        // What ModalHost's cleanup does once the wizard has closed itself.
        harness.ViewModel.Dispose();

        var token = Assert.Single(harness.ScanTokens);
        Assert.False(token.IsCancellationRequested);
        Assert.False(token.CanBeCanceled);

        release.Set();
        await scan;

        Assert.True(harness.Stored.SetupComplete);
    }

    [Fact]
    public async Task Step5_Finish_WorksWithoutRunningAScan()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        await Advance(harness, 4);

        var closed = false;
        harness.ViewModel.CloseRequested += (_, finished) => closed = finished;

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);

        Assert.Empty(harness.ScanRoots);
        Assert.True(harness.Stored.SetupComplete);
        Assert.True(closed);
    }

    [Fact]
    public async Task Back_DoesNotSave()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        var savesAfterNext = harness.Saves.Count;
        harness.Cache.CachePath = @"E:\somewhere\else";
        harness.ViewModel.BackCommand.Execute(null);

        Assert.Equal(0, harness.ViewModel.StepIndex);
        Assert.Equal(savesAfterNext, harness.Saves.Count);
        Assert.Equal("", harness.Stored.ThumbnailCacheDir);
    }

    [Fact]
    public void Back_IsDisabledOnTheFirstStep()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();

        Assert.False(harness.ViewModel.CanGoBack);
        Assert.False(harness.ViewModel.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task SkipSetup_FinishesWithoutVisitingTheRemainingSteps()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var closed = false;
        harness.ViewModel.CloseRequested += (_, finished) => closed = finished;

        await harness.ViewModel.SkipSetupCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.ViewModel.StepIndex);
        Assert.True(harness.Stored.SetupComplete);
        Assert.True(harness.ViewModel.IsFinished);
        Assert.True(closed);
        Assert.Equal(1, harness.Navigations);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromScanStatusService()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var harness = SetupWizardViewModelTestFactory.Create(scanStatus: status);
        try
        {
            await Advance(harness, 4);
            coordinator.RaiseProgress(ScanTaskNames.Classify, 3, 10, "Classifying 3/10 files", force: true);
            Assert.Equal("Classifying 3/10 files", harness.FirstScan.ScanMessage);

            var firstScan = harness.FirstScan;
            var raised = 0;
            firstScan.PropertyChanged += (_, _) => raised++;

            harness.ViewModel.Dispose();
            coordinator.RaiseProgress(ScanTaskNames.Ingest, 9, 10, "Ingesting 9/10 files", force: true);

            // The service still moved; the step no longer hears it. ScanMessage is a live read of
            // the service, so the assertion is on the notification, not on the value.
            Assert.Equal("Ingesting 9/10 files", status.Message);
            Assert.Equal(0, raised);
            Assert.True(firstScan.IsDisposed);
        }
        finally
        {
            harness.Dispose();
        }
    }

    // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so every command whose
    // CanExecute carries a correctness rule repeats the guard in its body.
    [Theory]
    [InlineData("next")]
    [InlineData("finish")]
    [InlineData("startScan")]
    public async Task Commands_RepeatTheirGuardsInTheirBodies(string command)
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = SetupWizardViewModelTestFactory.Create(scanStatus: status);

        switch (command)
        {
            case "next":
                // No folder chosen, so Next must do nothing at all.
                Assert.False(harness.ViewModel.NextCommand.CanExecute(null));
                harness.ViewModel.NextCommand.Execute(null);
                await AwaitExecution(harness.ViewModel.NextCommand.ExecutionTask);
                Assert.Equal(0, harness.ViewModel.StepIndex);
                Assert.Empty(harness.Saves);
                break;

            case "finish":
                // Not on the last step, so Finish must write nothing.
                Assert.False(harness.ViewModel.FinishCommand.CanExecute(null));
                harness.ViewModel.FinishCommand.Execute(null);
                await AwaitExecution(harness.ViewModel.FinishCommand.ExecutionTask);
                Assert.False(harness.Stored.SetupComplete);
                Assert.False(harness.ViewModel.IsFinished);
                break;

            default:
                await Advance(harness, 4);
                coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);
                Assert.False(harness.FirstScan.StartScanCommand.CanExecute(null));
                harness.FirstScan.StartScanCommand.Execute(null);
                await AwaitExecution(harness.FirstScan.StartScanCommand.ExecutionTask);
                Assert.Empty(harness.ScanRoots);
                break;
        }
    }

    // Walks the wizard from step 0 to `target`, choosing one folder so step 1's Next is enabled
    // and consuming the blank-longitude nudge on the way past step 3.
    private static async Task Advance(SetupWizardViewModelTestFactory.Harness harness, int target)
    {
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);

        var guard = 0;
        while (harness.ViewModel.StepIndex < target && guard++ < 12)
        {
            await harness.ViewModel.NextCommand.ExecuteAsync(null);
        }

        Assert.Equal(target, harness.ViewModel.StepIndex);
    }

    private static Task AwaitExecution(Task? execution)
        => execution ?? Task.CompletedTask;

    // Awaited, never blocked on: the scan delegate runs on the pool and records its roots before
    // it parks (TRACKING section 2 item 8).
    private static async Task WaitForTheScanToStart(SetupWizardViewModelTestFactory.Harness harness)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && Started() == 0)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, Started());

        int Started()
        {
            lock (harness.ScanRoots)
            {
                return harness.ScanRoots.Count;
            }
        }
    }

    // The wizard's own registration in AppHost.cs, as text. App.Tests cannot resolve a real host
    // against the true %LOCALAPPDATA%, so a binding is asserted by reading it, which is the
    // precedent Task 8 set.
    private static string ReadWizardRegistration()
    {
        var source = ReadAppHostSource();
        var start = source.IndexOf("new SetupWizardViewModel(", StringComparison.Ordinal);

        Assert.True(start > 0, "The SetupWizardViewModel registration was not found in AppHost.cs.");

        var body = source[start..];
        var end = body.IndexOf("SetupWizardService", StringComparison.Ordinal);
        Assert.True(end > 0, "The SetupWizardService registration was not found after the wizard's.");

        return body[..end];
    }

    private static string ReadAppHostSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "GalactiLog.App", "AppHost.cs");
        Assert.True(File.Exists(path), $"AppHost.cs was not found at {path}.");
        return File.ReadAllText(path);
    }
}
