using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.LibraryTabViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 12.7's scan control, Phase 14B Task 5: the run's scope as a radio pair, and the orphan
// cleanup checkbox. Neither is a stored key (spec 5.8.1's closing paragraph).
public class LibraryTabScanScopeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRadioPair_StartsFromTheStoredIncludeCalibration(bool stored)
    {
        using var harness = Factory
            .Create(general => general with { IncludeCalibration = stored })
            .Settle();

        Assert.Equal(stored, harness.ViewModel.ScanScopeAllFrames);
        Assert.Equal(!stored, harness.ViewModel.ScanScopeLightOnly);

        // The stored key's own editor is unchanged and still reads the key.
        Assert.Equal(stored, harness.ViewModel.IncludeCalibration);
    }

    [Fact]
    public void ChoosingLightOnly_DoesNotWriteTheStoredKey()
    {
        using var harness = Factory
            .Create(general => general with { IncludeCalibration = true })
            .Settle();

        harness.ViewModel.ScanScopeLightOnly = true;
        harness.SettleWrites();

        Assert.False(harness.ViewModel.ScanScopeAllFrames);

        // Spec 12.7: the pair is a per-run control. IncludeCalibrationCheckBox stays the key's
        // editor, and nothing was written.
        Assert.True(harness.Stored.IncludeCalibration);
        Assert.True(harness.ViewModel.IncludeCalibration);
        Assert.Empty(harness.Saves);
    }

    [Fact]
    public async Task TheRunPassesTheChosenScope()
    {
        using var harness = Factory
            .Create(general => general with { IncludeCalibration = true })
            .Settle();

        harness.ViewModel.ScanScopeLightOnly = true;
        await harness.ViewModel.RunScanCommand.ExecuteAsync(null);

        var options = Assert.Single(harness.ScanOptions);
        Assert.False(options.IncludeCalibration);
    }

    // Spec 12.7: "cleared on every visit to the tab rather than remembered". The tab is a lazily
    // constructed singleton, so the visit signal is the view re-attaching, which calls this.
    [Fact]
    public void TheCheckBox_IsClearedOnEveryVisit()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.ForceOrphanCleanup = true;
        harness.ViewModel.ClearPerRunOptions();

        Assert.False(harness.ViewModel.ForceOrphanCleanup);

        // And again, because "every visit" is not "the first one".
        harness.ViewModel.ForceOrphanCleanup = true;
        harness.ViewModel.ClearPerRunOptions();
        Assert.False(harness.ViewModel.ForceOrphanCleanup);
    }

    [Fact]
    public async Task TheCheckBox_IsClearedAfterARun()
    {
        using var harness = Factory.Create().Settle();

        harness.ViewModel.ForceOrphanCleanup = true;
        await harness.ViewModel.RunScanCommand.ExecuteAsync(null);

        // The run carried it, and the next run will not.
        Assert.True(Assert.Single(harness.ScanOptions).ForceOrphanCleanup);
        Assert.False(harness.ViewModel.ForceOrphanCleanup);
    }

    [Fact]
    public async Task TheRunPassesTheOverride()
    {
        using var harness = Factory.Create().Settle();

        await harness.ViewModel.RunScanCommand.ExecuteAsync(null);
        Assert.False(Assert.Single(harness.ScanOptions).ForceOrphanCleanup);

        harness.ViewModel.ForceOrphanCleanup = true;
        await harness.ViewModel.RunScanCommand.ExecuteAsync(null);
        Assert.True(harness.ScanOptions[^1].ForceOrphanCleanup);
    }

    // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so the body is the
    // guard. Widening the delegate must not have moved that guard.
    [Fact]
    public async Task RunScanCommand_ExecutedPastCanExecute_StillRefusesWhileAScanRuns()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var status = new ScanStatusService(coordinator, action => action());
        using var harness = Factory.Create(scanStatus: status).Settle();

        // The state the body guard reads, through the same seam production uses.
        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 1, "Discovering", force: true);
        Assert.False(harness.ViewModel.RunScanCommand.CanExecute(null));

        await harness.ViewModel.RunScanCommand.ExecuteAsync(null);

        Assert.Empty(harness.ScanOptions);
        Assert.Equal(0, harness.ScanRuns);
    }

    // Web parity, ScanFiltersPanel.tsx's applyNow: an unsaved rule is exactly what the user expects
    // the scan to apply, and the scan reads the stored document, so the press is refused with the
    // reason on the tab. A direct Execute ignores CanExecute (TRACKING section 6 item 13), so the
    // guard is in the body.
    [Fact]
    public async Task RunScanCommand_RefusesWhileTheFilterBlockIsDirty()
    {
        using var harness = Factory.Create(general => general with { ScanRoots = [Factory.Root] }).Settle();

        harness.ViewModel.AddNameRuleCommand.Execute(null);
        Assert.True(harness.ViewModel.IsDirty);

        await harness.ViewModel.RunScanCommand.ExecuteAsync(null);

        Assert.Empty(harness.ScanOptions);
        Assert.Equal(0, harness.ScanRuns);
        Assert.Equal(GalactiLog.App.ViewModels.Settings.LibraryTabViewModel.UnsavedFiltersRefusal, harness.ViewModel.ErrorMessage);
    }
}
