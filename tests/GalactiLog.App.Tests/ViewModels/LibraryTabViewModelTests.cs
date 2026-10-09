using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.LibraryTabViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.7's Library tab. The roadmap's Verify line for this row asks that "each control
// round-trips through SettingsStore, that an include path outside every root surfaces as a
// configuration error rather than being silently dropped, and that the test-a-path box returns
// each of the four verdicts". The first two clauses are here; the third is in
// TestPathViewModelTests.
//
// The harness builds a real SettingsStore over a temp database, so "round-trips through
// SettingsStore" means what it says: the assertion reads the document back out of SQLite.
public class LibraryTabViewModelTests
{
    // ---- scan roots (spec 10.1) --------------------------------------------------------------

    [Fact]
    public async Task ScanRoots_AddAndRemove_RoundTripThroughSettingsStore()
    {
        using var harness = Factory.Create().Settle();
        var tab = harness.ViewModel;

        Assert.True(tab.AddScanRoot(Factory.Root));
        Assert.True(tab.AddScanRoot(Factory.SecondRoot));
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Equal([Factory.Root, Factory.SecondRoot], harness.Stored.ScanRoots);

        tab.RemoveScanRootCommand.Execute(tab.ScanRoots[0]);
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Equal([Factory.SecondRoot], harness.Stored.ScanRoots);
    }

    [Fact]
    public void ScanRoots_AddingADuplicate_IsRefusedWithAMessage()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        var tab = harness.ViewModel;

        Assert.False(tab.AddScanRoot(Factory.Root));

        Assert.Single(tab.ScanRoots);
        Assert.NotNull(tab.ErrorMessage);
        Assert.Contains("already a library folder", tab.ErrorMessage);
    }

    [Fact]
    public void ScanRoots_AddingANestedRoot_IsRefusedWithAMessage()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        var tab = harness.ViewModel;

        // A child of an existing root, and then a parent of it. Each root carries its own
        // confinement boundary, and nesting would ingest the same file twice.
        Assert.False(tab.AddScanRoot(Path.Combine(Factory.Root, "2025")));
        Assert.Contains("Library folders may not be nested", tab.ErrorMessage);

        Assert.False(tab.AddScanRoot(@"C:\Astro"));
        Assert.Contains("Library folders may not be nested", tab.ErrorMessage);
        Assert.Single(tab.ScanRoots);
    }

    [Fact]
    public void ScanRoots_ARelativePath_IsRefused()
    {
        using var harness = Factory.Create().Settle();

        Assert.False(harness.ViewModel.AddScanRoot(@"Astro\Captures"));

        Assert.Empty(harness.ViewModel.ScanRoots);
        Assert.Contains("absolute path", harness.ViewModel.ErrorMessage);
    }

    // ---- include and exclude paths (spec 10.2) -----------------------------------------------

    [Fact]
    public async Task IncludePaths_AddAndRemove_RoundTrip()
    {
        var inside = Path.Combine(Factory.Root, "2025");
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        var tab = harness.ViewModel;

        tab.AddIncludePath(inside);
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Equal([inside], harness.Stored.ScanFilters.IncludePaths);

        tab.RemoveFilterPathCommand.Execute(tab.IncludePaths[0]);
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Empty(harness.Stored.ScanFilters.IncludePaths);
    }

    [Fact]
    public async Task ExcludePaths_AddAndRemove_RoundTrip()
    {
        var inside = Path.Combine(Factory.Root, "rejected");
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        var tab = harness.ViewModel;

        tab.AddExcludePath(inside);
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Equal([inside], harness.Stored.ScanFilters.ExcludePaths);

        tab.RemoveFilterPathCommand.Execute(tab.ExcludePaths[0]);
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Empty(harness.Stored.ScanFilters.ExcludePaths);
    }

    [Fact]
    public void IncludePath_OutsideEveryRoot_IsShownAsAConfigurationError()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        var tab = harness.ViewModel;

        tab.AddIncludePath(@"E:\Somewhere\Else");

        // Spec 10.2: surfaced in Settings, never silently dropped. The row is present, it carries
        // an error, and the error names the roots the path has to be inside.
        var row = Assert.Single(tab.IncludePaths);
        Assert.Equal(@"E:\Somewhere\Else", row.Path);
        Assert.True(row.HasError);
        Assert.Contains("must be inside one of the library folders", row.ErrorText);
        Assert.Contains(Factory.Root, row.ErrorText);
        Assert.True(tab.HasInvalidPath);
    }

    [Fact]
    public async Task IncludePath_OutsideEveryRoot_RefusesSave_AndIsNotSilentlyDropped()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        var tab = harness.ViewModel;

        tab.AddIncludePath(@"E:\Somewhere\Else");

        Assert.False(tab.SaveCommand.CanExecute(null));

        // TRACKING item 13: a direct Execute ignores CanExecute, so the guard is in the body too.
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Empty(harness.Saves);
        Assert.Empty(harness.Stored.ScanFilters.IncludePaths);
        Assert.Single(tab.IncludePaths);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void ExcludePath_OutsideEveryRoot_IsShownAsAConfigurationError()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        var tab = harness.ViewModel;

        tab.AddExcludePath(@"E:\Somewhere\Else");

        var row = Assert.Single(tab.ExcludePaths);
        Assert.True(row.HasError);
        Assert.Contains("must be inside one of the library folders", row.ErrorText);
        Assert.True(tab.HasInvalidPath);
        Assert.False(tab.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_ThatThrowsSettingsValidationException_ShowsTheMessage_AndKeepsTheEdits()
    {
        using var harness = Factory.Create().Settle();
        var tab = harness.ViewModel;

        // The store is the enforcement point; the inline pass in front of it is a usability
        // layer, so the tab still has to survive a refusal it did not predict.
        harness.SaveThrows = new SettingsValidationException("general.scan_roots entry is not valid.");
        Assert.True(tab.AddScanRoot(Factory.Root));

        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Equal("general.scan_roots entry is not valid.", tab.ErrorMessage);
        Assert.Single(tab.ScanRoots);
        Assert.True(tab.IsDirty);
        Assert.False(tab.IsSaving);
    }

    // ---- name rules (spec 10.2) --------------------------------------------------------------

    [Fact]
    public void NameRule_AddedWithTheWebDefaults()
    {
        using var harness = Factory.Create().Settle();
        var tab = harness.ViewModel;

        tab.AddNameRuleCommand.Execute(null);

        var row = Assert.Single(tab.NameRules);
        Assert.Equal("exclude", row.Action);
        Assert.Equal("glob", row.Type);
        Assert.Equal("file", row.Target);
        Assert.Equal("", row.Pattern);
        Assert.True(row.Enabled);
        Assert.True(Guid.TryParse(row.Id, out _));
    }

    [Fact]
    public async Task NameRule_EmptyPattern_IsAnInlineError_AndRefusesSave()
    {
        using var harness = Factory.Create().Settle();
        var tab = harness.ViewModel;

        tab.AddNameRuleCommand.Execute(null);

        Assert.Equal("empty pattern", Assert.Single(tab.NameRules).ErrorText);
        Assert.True(tab.HasInvalidRule);
        Assert.False(tab.SaveCommand.CanExecute(null));

        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Empty(harness.Saves);
    }

    [Fact]
    public async Task NameRule_UncompilableRegex_IsAnInlineError_AndRefusesSave()
    {
        using var harness = Factory.Create().Settle();
        var tab = harness.ViewModel;

        tab.AddNameRuleCommand.Execute(null);
        var row = Assert.Single(tab.NameRules);
        row.Type = "regex";
        row.Pattern = "^M(\\d+";

        Assert.True(row.HasError);
        Assert.NotEqual("empty pattern", row.ErrorText);
        Assert.True(tab.HasInvalidRule);

        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Empty(harness.Saves);
    }

    [Fact]
    public void NameRule_ValidatedLocally_WithNoRoundTrip()
    {
        // Assert by construction: the row validates with no collaborator at all, so there is
        // nothing for a round trip to travel over. The web posts each regex to
        // /api/scan/filters/validate-regex because its scanner is Python's re; this port's
        // scanner is the very Regex engine running in this process.
        var row = NameRuleRowViewModel.NewRule();
        row.Type = "regex";
        row.Pattern = "^M(\\d+";

        Assert.NotNull(row.ComputeError());

        row.Pattern = @"^M\d+$";
        Assert.Null(row.ComputeError());
    }

    [Fact]
    public async Task NameRules_RoundTripThroughSettingsStore()
    {
        using var harness = Factory.Create().Settle();
        var tab = harness.ViewModel;

        tab.AddNameRuleCommand.Execute(null);
        var row = Assert.Single(tab.NameRules);
        row.Action = "include";
        row.Type = "regex";
        row.Target = "folder";
        row.Pattern = @"^M\d+$";
        row.Enabled = false;

        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        var stored = Assert.Single(harness.Stored.ScanFilters.NameRules);
        Assert.Equal(row.Id, stored.Id);
        Assert.Equal("include", stored.Action);
        Assert.Equal("regex", stored.Type);
        Assert.Equal("folder", stored.Target);
        Assert.Equal(@"^M\d+$", stored.Pattern);
        Assert.False(stored.Enabled);
    }

    [Fact]
    public async Task NameRule_Remove_RoundTrips()
    {
        using var harness = Factory
            .Create(general => general with
            {
                ScanFilters = general.ScanFilters with { NameRules = [Factory.Rule()] },
            })
            .Settle();
        var tab = harness.ViewModel;

        tab.RemoveNameRuleCommand.Execute(Assert.Single(tab.NameRules));
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Empty(harness.Stored.ScanFilters.NameRules);
    }

    [Fact]
    public void Revert_RestoresTheLastSavedFilterBlock()
    {
        using var harness = Factory
            .Create(general => general with
            {
                ScanRoots = [Factory.Root],
                ScanFilters = general.ScanFilters with
                {
                    ExcludePaths = [Path.Combine(Factory.Root, "rejected")],
                    NameRules = [Factory.Rule()],
                },
            })
            .Settle();
        var tab = harness.ViewModel;

        tab.AddScanRoot(Factory.SecondRoot);
        tab.AddNameRuleCommand.Execute(null);
        tab.RemoveFilterPathCommand.Execute(tab.ExcludePaths[0]);
        Assert.True(tab.IsDirty);

        tab.RevertCommand.Execute(null);

        Assert.Equal([Factory.Root], tab.ScanRoots.Select(row => row.Path));
        Assert.Equal([Path.Combine(Factory.Root, "rejected")], tab.ExcludePaths.Select(row => row.Path));
        Assert.Equal(["rule-1"], tab.NameRules.Select(row => row.Id));
        Assert.False(tab.IsDirty);
    }

    // ---- the four immediate, optimistic saves -------------------------------------------------

    [Fact]
    public void IncludeCalibration_RoundTrips()
    {
        using var harness = Factory.Create().Settle();

        Assert.False(harness.ViewModel.IncludeCalibration);
        harness.ViewModel.IncludeCalibration = true;
        harness.SettleWrites();

        Assert.True(harness.Stored.IncludeCalibration);
        Assert.Equal("All frames", harness.ViewModel.StatusMessage);
    }

    [Fact]
    public void AutoScanEnabled_RoundTrips()
    {
        using var harness = Factory.Create().Settle();

        Assert.True(harness.ViewModel.AutoScanEnabled);
        harness.ViewModel.AutoScanEnabled = false;
        harness.SettleWrites();

        Assert.False(harness.Stored.AutoScanEnabled);
        Assert.Equal("Auto-scan disabled", harness.ViewModel.StatusMessage);
    }

    [Fact]
    public void AutoScanInterval_OffersTheSevenPresets()
    {
        using var harness = Factory.Create().Settle();

        // ScanManager.tsx's INTERVALS, verbatim.
        Assert.Equal(
            [60, 120, 240, 360, 480, 720, 1440],
            harness.ViewModel.IntervalOptions.Select(option => option.Minutes));
        Assert.Equal(
            ["1 hour", "2 hours", "4 hours", "6 hours", "8 hours", "12 hours", "24 hours"],
            harness.ViewModel.IntervalOptions.Select(option => option.Label));
    }

    [Fact]
    public void AutoScanInterval_AStoredNonPresetValue_IsShownRatherThanSnapped()
    {
        using var harness = Factory
            .Create(general => general with { AutoScanIntervalMinutes = 90 })
            .Settle();

        Assert.Equal(8, harness.ViewModel.IntervalOptions.Count);
        Assert.Equal(90, harness.ViewModel.SelectedInterval!.Minutes);
        Assert.Equal("90 minutes", harness.ViewModel.SelectedInterval.Label);
        Assert.Equal(90, harness.Stored.AutoScanIntervalMinutes);
    }

    [Fact]
    public void AutoScanInterval_RoundTrips()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.SelectedInterval =
            harness.ViewModel.IntervalOptions.Single(option => option.Minutes == 720);
        harness.SettleWrites();

        Assert.Equal(720, harness.Stored.AutoScanIntervalMinutes);
        Assert.Equal("Scan interval updated", harness.ViewModel.StatusMessage);
    }

    // Phase 15B fixer pass, task5c-review.md P3-6, found on a launched application: on a FIRST
    // visit to the Library tab the "Scan interval" select renders empty and stays empty, and reads
    // "4 hours" on a second visit in the same session. The defect is here rather than in the
    // markup, and it is the rig ComboBox's family: a bound list replaced under a two-way
    // selection. The two cases below are its two halves.
    [Fact]
    public async Task AutoScanInterval_ThePublish_AnnouncesTheSelection_WhateverTheTabWasSeededWith()
    {
        // The first visit in the order a launched application produces it: the view binds while
        // the read is still in flight, so the select's list is empty and there is nothing for it
        // to show. The publish is the only thing that can tell it to select, and it has to say so
        // even when the value it lands on is the one the tab was constructed with.
        var published = new List<Action>();
        using var harness = Factory.Create(post: published.Add);
        var tab = harness.ViewModel;

        Assert.Empty(tab.IntervalOptions);

        var announced = new List<string?>();
        tab.PropertyChanged += (_, args) => announced.Add(args.PropertyName);

        await tab.PendingLoad!;
        foreach (var publish in published.ToArray())
        {
            publish();
        }

        Assert.Equal(7, tab.IntervalOptions.Count);
        Assert.Equal(240, tab.SelectedInterval!.Minutes);
        Assert.Contains(nameof(LibraryTabViewModel.SelectedInterval), announced);
    }

    [Fact]
    public void AutoScanInterval_ALaterPublish_LeavesTheOptionsAndTheSelectionWhereTheyAre()
    {
        // Every save made anywhere in the application reaches this tab through GeneralChanged, and
        // a publish that clears the options collection takes any bound selector's own selection
        // with it. The presets never change, so nothing about them is republished.
        using var harness = Factory.Create(followGeneralChanged: true).Settle();
        var tab = harness.ViewModel;
        var options = tab.IntervalOptions.ToArray();
        var selected = tab.SelectedInterval;

        var resets = 0;
        ((INotifyCollectionChanged)tab.IntervalOptions).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        harness.SaveElsewhere(general => general with { WatcherEnabled = false });
        harness.SettleWrites();

        Assert.Equal(0, resets);
        Assert.Equal(options.Length, tab.IntervalOptions.Count);
        for (var i = 0; i < options.Length; i++)
        {
            Assert.Same(options[i], tab.IntervalOptions[i]);
        }

        Assert.Same(selected, tab.SelectedInterval);
    }

    [Fact]
    public void WatcherEnabled_RoundTrips()
    {
        using var harness = Factory.Create().Settle();

        Assert.True(harness.ViewModel.WatcherEnabled);
        harness.ViewModel.WatcherEnabled = false;
        harness.SettleWrites();

        Assert.False(harness.Stored.WatcherEnabled);
        Assert.Equal("File watching disabled", harness.ViewModel.StatusMessage);
    }

    [Fact]
    public void AFailedImmediateSave_RollsTheControlBack()
    {
        using var harness = Factory.Create().Settle();
        harness.SaveThrows = new SettingsValidationException("general.watcher_enabled refused.");

        harness.ViewModel.WatcherEnabled = false;
        harness.SettleWrites();

        // A checkbox that stays ticked after a failed save is a lie.
        Assert.True(harness.ViewModel.WatcherEnabled);
        Assert.Equal("general.watcher_enabled refused.", harness.ViewModel.ErrorMessage);
        Assert.Null(harness.ViewModel.StatusMessage);

        // And the roll-back did not itself queue a second save.
        Assert.Single(harness.Saves);
    }

    // ---- the manual scan (spec 10.4, 10.5) ----------------------------------------------------

    [Fact]
    public void RunScan_IsDisabledWhileAScanIsRunning()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Factory.Create(scanStatus: status).Settle();

        Assert.True(harness.ViewModel.RunScanCommand.CanExecute(null));

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 1, "Discovering", force: true);

        Assert.False(harness.ViewModel.RunScanCommand.CanExecute(null));
        Assert.True(harness.ViewModel.CancelScanCommand.CanExecute(null));
    }

    [Fact]
    public void RunScan_IsDisabledWhileResolutionIsInProgress()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Factory.Create(scanStatus: status).Settle();

        // Phase 7 fixer item 1: the unresolved-name retry holds the coordinator's resolution
        // lease, and a scan cannot start while it is held. The same rule StatusBarViewModel uses.
        using var lease = coordinator.TryBeginResolution();

        Assert.NotNull(lease);
        Assert.False(harness.ViewModel.RunScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunScan_WhileRunning_IsRefusedInTheCommandBody()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Factory.Create(scanStatus: status).Settle();
        coordinator.RaiseProgress(ScanTaskNames.Ingest, 1, 1, "Ingesting", force: true);

        // RelayCommand.Execute ignores CanExecute (TRACKING section 6 item 13).
        await harness.ViewModel.RunScanCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.ScanRuns);
    }

    // FIXER LIST F19, the first concrete hit of the F16 audit: the command used to be built from a
    // Func<CancellationToken, Task>, so a second Execute cancelled the in-flight command token and
    // the running manual scan was aborted rather than the second press being refused.
    [Fact]
    public async Task RunScan_ExecutedTwice_RefusesTheSecondPress_AndDoesNotAbortTheFirst()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var release = new ManualResetEventSlim(false);
        using var harness = Factory.Create(scanStatus: status).Settle();
        harness.ScanRelease = release;

        harness.ViewModel.RunScanCommand.Execute(null);
        var first = harness.ViewModel.RunScanCommand.ExecutionTask!;
        Assert.True(harness.ScanEntered.Wait(TimeSpan.FromSeconds(30)));

        // The state the body guard reads, reached through the same seam production uses.
        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 1, "Discovering", force: true);
        harness.ViewModel.RunScanCommand.Execute(null);

        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, harness.ScanRuns);
        Assert.Equal(0, harness.Cancels);
    }

    [AvaloniaFact]
    public async Task RunScan_RunsOffTheUiThread()
    {
        // ScanCoordinator.RunAsync validates the configured roots synchronously before it returns
        // a task, so it must never be entered on the dispatcher. Awaited, never blocked on
        // (TRACKING section 2 item 8).
        Assert.True(Dispatcher.UIThread.CheckAccess(), "The test itself must run on the UI thread.");

        using var harness = Factory.Create(onUiThread: () => Dispatcher.UIThread.CheckAccess());
        await harness.ViewModel.PendingLoad!;

        await harness.ViewModel.RunScanCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.ScanRuns);
        Assert.NotEmpty(harness.ScanOnUiThread);
        Assert.All(harness.ScanOnUiThread, Assert.False);
    }

    [Fact]
    public void Cancel_CallsTheCancelDelegate()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Factory.Create(scanStatus: status).Settle();
        coordinator.RaiseProgress(ScanTaskNames.Ingest, 1, 1, "Ingesting", force: true);

        harness.ViewModel.CancelScanCommand.Execute(null);

        Assert.Equal(1, harness.Cancels);
    }

    [Fact]
    public void Cancel_WhenNoScanIsRunning_IsRefusedInTheCommandBody()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Factory.Create(scanStatus: status).Settle();

        harness.ViewModel.CancelScanCommand.Execute(null);

        Assert.Equal(0, harness.Cancels);
    }

    [Fact]
    public void Progress_MirrorsScanStatusService()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Factory.Create(scanStatus: status).Settle();

        coordinator.RaiseProgress(ScanTaskNames.Ingest, 3, 4, "Ingested 3/4 files", force: true);

        Assert.Same(status, harness.ViewModel.ScanStatus);
        Assert.True(harness.ViewModel.IsScanRunning);
        Assert.Equal("Ingested 3/4 files", harness.ViewModel.ScanMessage);
        Assert.Equal(75d, harness.ViewModel.ScanPercent);
        Assert.False(harness.ViewModel.IsScanIndeterminate);

        coordinator.RaiseProgress(ScanTaskNames.Dedup, 0, 0, "Finding duplicates", force: true);

        Assert.True(harness.ViewModel.IsScanIndeterminate);
    }

    [Fact]
    public void Dispose_UnsubscribesFromScanStatusService()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        var harness = Factory.Create(scanStatus: status).Settle();
        var tab = harness.ViewModel;

        var changes = 0;
        tab.PropertyChanged += (_, _) => changes++;

        harness.Dispose();
        coordinator.RaiseProgress(ScanTaskNames.Ingest, 1, 2, "Ingesting", force: true);

        Assert.Equal(0, changes);
        Assert.True(tab.IsDisposed);
    }

    // ---- reads that fail (spec 12.10) ---------------------------------------------------------

    [Fact]
    public void ALoadThatThrows_RendersTheFailedState_AndDoesNotSave()
    {
        using var harness = Factory
            .Create(loadThrows: new InvalidOperationException("no database"))
            .Settle();

        Assert.True(harness.ViewModel.LoadFailed);
        Assert.False(harness.ViewModel.IsLoading);
        Assert.False(harness.ViewModel.IsReady);
        Assert.Empty(harness.Saves);
        Assert.Contains(harness.Logger.Entries, entry => entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task AfterAFailedRead_ASaveIsRefused_AndTheStoredListsSurvive()
    {
        // Review finding I1. Before the fix, one edit after a failed read enabled Save, and the
        // write then re-read the document successfully and applied the tab's empty lists over
        // every include path, exclude path and name rule the user had.
        using var harness = Factory
            .Create(
                general => general with
                {
                    ScanRoots = [Factory.Root],
                    ScanFilters = general.ScanFilters with
                    {
                        IncludePaths = [Path.Combine(Factory.Root, "2025")],
                        NameRules = [Factory.Rule()],
                    },
                },
                loadThrows: new InvalidOperationException("no database"))
            .Settle();
        var tab = harness.ViewModel;
        harness.LoadThrows = null;

        Assert.True(tab.AddScanRoot(Factory.SecondRoot));
        Assert.True(tab.IsDirty);
        Assert.False(tab.SaveCommand.CanExecute(null));

        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Empty(harness.Saves);
        Assert.Equal([Path.Combine(Factory.Root, "2025")], harness.Stored.ScanFilters.IncludePaths);
        Assert.Single(harness.Stored.ScanFilters.NameRules);
        Assert.Equal([Factory.Root], harness.Stored.ScanRoots);
    }

    [Fact]
    public async Task BeforeTheFirstLoadPublishes_ASaveIsRefused()
    {
        // The other half of I1: _ready. A tab whose load has not landed does not know what its
        // four lists are, so nothing may be queued over them.
        var released = new ManualResetEventSlim(false);
        using var harness = Factory.Create(post: action =>
        {
            released.Wait(TimeSpan.FromSeconds(30));
            action();
        });
        var tab = harness.ViewModel;

        Assert.True(tab.AddScanRoot(Factory.Root));
        Assert.False(tab.SaveCommand.CanExecute(null));
        await tab.SaveCommand.ExecuteAsync(null);

        Assert.Empty(harness.Saves);

        released.Set();
        harness.Settle();
    }

    // ---- a concurrent writer of the same document (review finding I3) ------------------------

    [Fact]
    public void AGeneralChangeElsewhere_WhileNotDirty_ReloadsTheLists()
    {
        using var harness = Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .Settle();
        var tab = harness.ViewModel;

        // What Task 9's wizard does from a link on this very tab.
        harness.SaveElsewhere(general => general with
        {
            ScanRoots = [Factory.Root, Factory.SecondRoot],
            ScanFilters = general.ScanFilters with { NameRules = [Factory.Rule("wizard-rule")] },
        });

        Assert.Equal([Factory.Root, Factory.SecondRoot], tab.ScanRoots.Select(row => row.Path));
        Assert.Equal(["wizard-rule"], tab.NameRules.Select(row => row.Id));
        Assert.False(tab.IsDirty);
        Assert.False(tab.ChangedElsewhere);
    }

    [Fact]
    public async Task AGeneralChangeElsewhere_WhileDirty_IsReportedAndDoesNotOverwriteTheEdit()
    {
        using var harness = Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .Settle();
        var tab = harness.ViewModel;

        tab.AddNameRuleCommand.Execute(null);
        var edited = Assert.Single(tab.NameRules);
        edited.Pattern = "*_mine.fits";
        Assert.True(tab.IsDirty);

        harness.SaveElsewhere(general => general with
        {
            ScanFilters = general.ScanFilters with { NameRules = [Factory.Rule("wizard-rule")] },
        });

        Assert.True(tab.ChangedElsewhere);
        Assert.Equal([edited.Id], tab.NameRules.Select(row => row.Id));
        Assert.Equal("*_mine.fits", Assert.Single(tab.NameRules).Pattern);

        // Revert is how the stored values are taken, and it clears the notice.
        tab.RevertCommand.Execute(null);
        Assert.Equal(["wizard-rule"], tab.NameRules.Select(row => row.Id));
        Assert.False(tab.ChangedElsewhere);

        // And a save after the reload writes the reloaded lists, not the stale ones.
        tab.AddScanRoot(Factory.SecondRoot);
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();
        Assert.Equal(["wizard-rule"], harness.Stored.ScanFilters.NameRules.Select(rule => rule.Id));
    }

    [Fact]
    public void TheTabsOwnWrite_DoesNotReportItselfAsAChangeElsewhere()
    {
        using var harness = Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .Settle();
        var tab = harness.ViewModel;

        // A filter edit pending, then an immediate scalar save. The store raises GeneralChanged
        // for the tab's own write; treating that as a change made elsewhere would be a lie.
        tab.AddScanRoot(Factory.SecondRoot);
        tab.WatcherEnabled = false;
        harness.SettleWrites();

        Assert.False(tab.ChangedElsewhere);
        Assert.Equal(2, tab.ScanRoots.Count);
        Assert.False(harness.Stored.WatcherEnabled);
    }

    [Fact]
    public void AGeneralChangeElsewhere_MovesTheTestPathBoxOntoTheNewConfiguration()
    {
        using var harness = Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .Settle();
        var tab = harness.ViewModel;
        tab.TestPath.Path = Path.Combine(Factory.Root, "rejected", "M31.fits");
        tab.TestPath.TestCommand.Execute(null);
        Assert.Equal("included", tab.TestPath.Verdict);

        harness.SaveElsewhere(general => general with
        {
            ScanFilters = general.ScanFilters with { ExcludePaths = [Path.Combine(Factory.Root, "rejected")] },
        });
        tab.TestPath.TestCommand.Execute(null);

        Assert.Equal("excluded_by_path", tab.TestPath.Verdict);
    }

    [Fact]
    public void Dispose_UnsubscribesFromGeneralChanged()
    {
        var harness = Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .Settle();
        var tab = harness.ViewModel;

        tab.Dispose();
        harness.SaveElsewhere(general => general with { ScanRoots = [Factory.Root, Factory.SecondRoot] });

        Assert.Single(tab.ScanRoots);
        harness.Dispose();
    }

    // ---- nested roots from a document rather than from the add box (review finding M1) -------

    [Fact]
    public async Task NestedScanRoots_InTheStoredDocument_AreFlagged_AndRefuseSave()
    {
        // AddScanRoot refuses a nested pair, but that is one UI entry point. A hand-edited
        // document, the wizard, or a remove-and-re-add in a different order all reach one.
        using var harness = Factory
            .Create(seedRaw: general => general with { ScanRoots = [Factory.Root, Path.Combine(Factory.Root, "2025")] })
            .Settle();
        var tab = harness.ViewModel;

        Assert.True(tab.HasInvalidPath);
        Assert.Null(tab.ScanRoots[0].ErrorText);
        Assert.Contains("Library folders may not be nested", tab.ScanRoots[1].ErrorText);

        // Dirty through a change that is itself valid, so the refusal can only be the overlap.
        tab.AddIncludePath(Path.Combine(Factory.Root, "2025"));
        Assert.False(tab.HasInvalidRule);
        Assert.False(tab.SaveCommand.CanExecute(null));
        await tab.SaveCommand.ExecuteAsync(null);
        harness.SettleWrites();

        Assert.Empty(harness.Saves);
    }

    [Fact]
    public void RemovingTheOverlappingRoot_ClearsTheError()
    {
        using var harness = Factory
            .Create(seedRaw: general => general with { ScanRoots = [Factory.Root, Path.Combine(Factory.Root, "2025")] })
            .Settle();
        var tab = harness.ViewModel;

        tab.RemoveScanRootCommand.Execute(tab.ScanRoots[1]);

        Assert.False(tab.HasInvalidPath);
        Assert.Null(Assert.Single(tab.ScanRoots).ErrorText);
    }

    // ---- an edit typed before the first load published (review finding M6) --------------------

    [Fact]
    public void AnEditMadeBeforeTheFirstLoadPublishes_IsKept()
    {
        var released = new ManualResetEventSlim(false);
        using var harness = Factory
            .Create(
                general => general with { ScanRoots = [Factory.Root] },
                post: action =>
                {
                    released.Wait(TimeSpan.FromSeconds(30));
                    action();
                });
        var tab = harness.ViewModel;

        Assert.True(tab.AddScanRoot(Factory.SecondRoot));

        released.Set();
        harness.Settle();

        // The typed root survives the publish, and the tab says the stored values are the ones
        // that are now out of date.
        Assert.Contains(tab.ScanRoots, row => row.Path == Factory.SecondRoot);
        Assert.True(tab.IsDirty);

        // FIXER LIST F13: nothing changed elsewhere here. The first read simply landed after the
        // edit, which is its own flag with its own sentence.
        Assert.True(tab.EditedBeforeFirstLoad);
        Assert.False(tab.ChangedElsewhere);
        Assert.True(tab.StoredValuesNotShown);
        Assert.DoesNotContain("changed elsewhere", tab.StoredValuesNotice, StringComparison.Ordinal);

        // And Revert clears it, the same way it clears the concurrent-writer notice.
        tab.RevertCommand.Execute(null);
        Assert.False(tab.EditedBeforeFirstLoad);
        Assert.False(tab.StoredValuesNotShown);
    }

    // ---- dismissing the stored-values notice (polish wave 5, ruling 4) ------------------------

    [Fact]
    public void Dismiss_WhenChangedElsewhere_ClearsTheNotice()
    {
        using var harness = Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .Settle();
        var tab = harness.ViewModel;

        tab.AddScanRoot(Factory.SecondRoot);
        harness.SaveElsewhere(general => general with { WatcherEnabled = false });
        Assert.True(tab.ChangedElsewhere);

        tab.DismissStoredValuesNoticeCommand.Execute(null);

        Assert.False(tab.ChangedElsewhere);
        Assert.False(tab.StoredValuesNotShown);
    }

    [Fact]
    public void Dismiss_WhenEditedBeforeFirstLoad_ClearsTheNotice()
    {
        var released = new ManualResetEventSlim(false);
        using var harness = Factory
            .Create(
                general => general with { ScanRoots = [Factory.Root] },
                post: action =>
                {
                    released.Wait(TimeSpan.FromSeconds(30));
                    action();
                });
        var tab = harness.ViewModel;

        Assert.True(tab.AddScanRoot(Factory.SecondRoot));
        released.Set();
        harness.Settle();
        Assert.True(tab.EditedBeforeFirstLoad);

        tab.DismissStoredValuesNoticeCommand.Execute(null);

        Assert.False(tab.EditedBeforeFirstLoad);
        Assert.False(tab.StoredValuesNotShown);
    }

    [Fact]
    public void Dismiss_ThenAFurtherChangeElsewhere_RaisesTheNoticeAgain()
    {
        using var harness = Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .Settle();
        var tab = harness.ViewModel;

        tab.AddScanRoot(Factory.SecondRoot);
        harness.SaveElsewhere(general => general with { WatcherEnabled = false });
        tab.DismissStoredValuesNoticeCommand.Execute(null);
        Assert.False(tab.ChangedElsewhere);

        harness.SaveElsewhere(general => general with { WatcherEnabled = true });

        Assert.True(tab.ChangedElsewhere);
        Assert.True(tab.StoredValuesNotShown);
    }

    // ---- Phase 9 Task 9's seam: spec 12.1's "Run setup again" --------------------------------

    // Built directly rather than through the factory: the link is an optional last constructor
    // parameter, so every existing caller of the factory is unaffected and this is the one case
    // that supplies it.
    private static LibraryTabViewModel WithSetupWizard(
        Func<Task>? runSetupAgain, RecordingLogger? logger = null)
        => new(
            () => new GeneralSettings(),
            mutate => mutate(new GeneralSettings()),
            (_, _) => Task.FromResult(Factory.Outcome()),
            () => { },
            logger: logger,
            post: action => action(),
            runSetupAgain: runSetupAgain);

    [Fact]
    public async Task RunSetupAgain_OpensTheWizard()
    {
        var opened = 0;
        using var tab = WithSetupWizard(() =>
        {
            opened++;
            return Task.CompletedTask;
        });

        Assert.True(tab.CanRunSetupAgain);
        Assert.True(tab.RunSetupAgainCommand.CanExecute(null));

        await tab.RunSetupAgainCommand.ExecuteAsync(null);

        Assert.Equal(1, opened);
    }

    [Fact]
    public void RunSetupAgain_WithNoWizard_IsNotOffered()
    {
        using var tab = WithSetupWizard(null);

        Assert.False(tab.CanRunSetupAgain);
        Assert.False(tab.RunSetupAgainCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunSetupAgain_WhenTheWizardThrows_IsLogged_AndDoesNotEscape()
    {
        var logger = new RecordingLogger();
        var failure = new InvalidOperationException("no window");
        using var tab = WithSetupWizard(() => throw failure, logger);

        await tab.RunSetupAgainCommand.ExecuteAsync(null);

        // The guard actually ran, rather than the exception having been swallowed silently.
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Contains("setup wizard", entry.Message, StringComparison.OrdinalIgnoreCase);

        // The command completed; the failure did not reach the dispatcher.
        Assert.True(tab.RunSetupAgainCommand.CanExecute(null));
    }

    // ---- Phase 15B Task 5c: the guide log switch request ---------------------------------------

    [Fact]
    public void TheGuideLogSwitchRequest_IsPendingUntilItIsConsumed_AndThenOnce()
    {
        using var harness = Factory.Create().Settle();
        var tab = harness.ViewModel;

        // Nothing has asked, so a visit to the tab scrolls nowhere.
        Assert.False(tab.ConsumeGuideLogSwitchInViewRequest());

        tab.RequestGuideLogSwitchInView();

        Assert.True(tab.ConsumeGuideLogSwitchInViewRequest());

        // One route scrolls once: the second visit is an ordinary visit.
        Assert.False(tab.ConsumeGuideLogSwitchInViewRequest());
    }

    [Fact]
    public void TheGuideLogSwitchRequest_AndTheRuleEditorRequest_AreTwoIndependentFlags()
    {
        using var harness = Factory.Create().Settle();
        var tab = harness.ViewModel;

        tab.RequestGuideLogSwitchInView();

        // The Dashboard's Review route and the Statistics notice's route land on the same tab at
        // two different sections, so consuming one must not answer the other.
        Assert.False(tab.ConsumeNameRulesInViewRequest());
        Assert.True(tab.ConsumeGuideLogSwitchInViewRequest());
    }

    // ---- pending-edits spine -------------------------------------------------------------------

    [Fact]
    public void PendingEdits_FollowsIsDirty_AndRaisesForIt()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        IPendingEdits tab = harness.ViewModel;
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("Library folders, paths and rules", tab.Label);
        Assert.Equal("library", tab.NavigationKey);
        Assert.False(tab.HasPendingEdits);
        Assert.Null(tab.SaveRefusal);

        harness.ViewModel.AddNameRuleCommand.Execute(null);

        Assert.True(tab.HasPendingEdits);
        Assert.Contains("HasPendingEdits", raised);
        Assert.Contains("SaveRefusal", raised);
    }

    [Fact]
    public void PendingEdits_SaveRefusal_IsTheTooltipTextWhileDirtyAndUnsavable()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        IPendingEdits tab = harness.ViewModel;

        harness.ViewModel.AddIncludePath(@"E:\Somewhere\Else");

        Assert.NotNull(tab.SaveRefusal);
        Assert.Equal(harness.ViewModel.SaveRefusalReason, tab.SaveRefusal);
    }

    [Fact]
    public async Task PendingEdits_SaveAsync_PersistsAndClearsTheDirtyFlag()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        IPendingEdits tab = harness.ViewModel;

        harness.ViewModel.AddScanRoot(Factory.SecondRoot);
        Assert.Null(tab.SaveRefusal);

        await tab.SaveAsync();
        harness.SettleWrites();

        Assert.Contains(Factory.SecondRoot, harness.Stored.ScanRoots);
        Assert.False(harness.ViewModel.IsDirty);
        Assert.False(tab.HasPendingEdits);
    }

    [Fact]
    public void PendingEdits_Discard_RestoresTheStoredRoots()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();
        IPendingEdits tab = harness.ViewModel;

        harness.ViewModel.AddScanRoot(Factory.SecondRoot);
        tab.Discard();

        Assert.Equal([Factory.Root], harness.ViewModel.ScanRoots.Select(row => row.Path));
        Assert.False(tab.HasPendingEdits);
    }
}
