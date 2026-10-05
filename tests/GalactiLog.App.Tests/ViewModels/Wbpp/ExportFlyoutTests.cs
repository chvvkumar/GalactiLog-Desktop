using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Data.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// Spec 12.13's Export for stacking command on the Target detail page (Phase 16 Task 5b): the
/// guard in both places, and the nights arriving in the ledger's own order as a copy. The same
/// three shapes <c>NightSelectionTests</c> carries for Copy frame list, with the new command's
/// name, because the two entries of one flyout have to answer the same rule.
/// </summary>
public class ExportFlyoutTests
{
    [Fact]
    public void ExportForStacking_IsDisabledWithNothingChecked()
    {
        using var harness = Factory.Create().Settle();

        Assert.False(harness.ViewModel.ExportForStackingCommand.CanExecute(null));

        harness.ViewModel.Sessions[0].IsChecked = true;

        Assert.True(harness.ViewModel.ExportForStackingCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExportForStacking_ExecutedDirectlyWithNothingChecked_OpensNothing()
    {
        // TRACKING section 6 item 13 and HANDOFF rule 8: RelayCommand.Execute ignores CanExecute,
        // so the guard is in the body as well. Without it a keyboard path to an enabled-looking
        // entry opens the page over no night at all.
        using var harness = Factory.Create().Settle();

        await harness.ViewModel.ExportForStackingCommand.ExecuteAsync(null);

        Assert.Empty(harness.OpenedWbppExports);
    }

    [Fact]
    public async Task ExportForStacking_WithNoDialogHost_StillReportsTheRuleAndDoesNothing()
    {
        // The page built with no modal host: CanExecute answers the spec's own rule, which is
        // about the selection and not about the host, and the body is where the missing delegate
        // is handled.
        using var harness = Factory.Create(withWbppExport: false).Settle();
        harness.ViewModel.Sessions[0].IsChecked = true;

        Assert.True(harness.ViewModel.ExportForStackingCommand.CanExecute(null));

        await harness.ViewModel.ExportForStackingCommand.ExecuteAsync(null);

        Assert.Empty(harness.OpenedWbppExports);
    }

    [Fact]
    public async Task ExportForStacking_HandsOverTheCheckedNights_InLedgerOrder()
    {
        using var harness = Factory.Create().Settle();
        harness.ViewModel.Sessions[1].IsChecked = true;
        harness.ViewModel.Sessions[0].IsChecked = true;

        await harness.ViewModel.ExportForStackingCommand.ExecuteAsync(null);

        var (groupKey, targetName, nights) = Assert.Single(harness.OpenedWbppExports);
        Assert.Equal(Factory.ResolvedGroupKey, groupKey);
        Assert.Equal(harness.ViewModel.Title, targetName);
        Assert.Equal([Factory.LastSession, Factory.FirstSession], nights);
    }

    [Fact]
    public async Task ExportForStacking_HandsOverACopy_NotTheLedgersOwnCollection()
    {
        // A failure here is the live ObservableCollection being passed through, so a check made
        // while the page is open changes the export's night set underneath a computation that has
        // already run.
        using var harness = Factory.Create().Settle();
        harness.ViewModel.Sessions[0].IsChecked = true;

        await harness.ViewModel.ExportForStackingCommand.ExecuteAsync(null);

        var nights = Assert.Single(harness.OpenedWbppExports).Nights;
        Assert.Equal([Factory.LastSession], nights);

        harness.ViewModel.Sessions[1].IsChecked = true;

        Assert.Equal([Factory.LastSession], nights);
        Assert.Equal(2, harness.ViewModel.SelectedNights.Count);
    }

    [Fact]
    public void TheSentenceOnTheDisabledEntries_IsCarriedWhileNothingIsChecked()
    {
        // Spec 12.13: both entries carry "Select one or more nights first" while nothing is
        // checked, and nothing once one is, so an entry that will run has no tooltip at all.
        using var harness = Factory.Create().Settle();

        Assert.Equal("Select one or more nights first", harness.ViewModel.NoNightCheckedHint);

        harness.ViewModel.Sessions[0].IsChecked = true;

        Assert.Null(harness.ViewModel.NoNightCheckedHint);
    }

    // ---- section 8.3: the registration builds and resolves ------------------------------

    /// <summary>
    /// The same shape <c>AppHostTests.AppHostFixture</c> has, which is private to that class. A
    /// real host over a temp application-data root, with Serilog's process-global logger closed
    /// first because <c>AppHost.Build</c> overwrites it.
    /// </summary>
    private sealed class HostFixture : IDisposable
    {
        public string Root { get; }

        public IServiceProvider Services { get; }

        private readonly IDisposable _host;

        public HostFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "GalactiLogExportFlyoutTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Serilog.Log.CloseAndFlush();
            var host = AppHost.Build(Root, cliMode: false);
            _host = host;
            Services = host.Services;
        }

        public void Dispose()
        {
            _host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A file still held open by a read this fixture did not start leaves a temp
                // directory behind, which is not this case's subject.
            }
        }
    }

    /// <summary>An <c>AvaloniaFact</c> rather than a <c>Fact</c>, and the only case in this file
    /// that needs one: the page's first read posts its Publish onto the dispatcher, and the quality
    /// panel is built there. A plain case has no dispatcher loop to run it on.</summary>
    [AvaloniaFact]
    public async Task TheHost_ResolvesTheExportRegistrations()
    {
        // A failure looks like a factory whose captured serviceProvider resolves a service
        // registered later in the file, which throws only on the first real opening and which no
        // view case reaches.
        using var fixture = new HostFixture();

        Assert.NotNull(fixture.Services.GetRequiredService<WbppPathsQuery>());
        Assert.NotNull(fixture.Services.GetRequiredService<WbppExportDialogService>());

        var createPage = fixture.Services
            .GetRequiredService<Func<string, string, IReadOnlyList<DateOnly>, WbppExportViewModel>>();
        using var page = createPage("obj:M 31", "M 31", [new DateOnly(2026, 9, 8)]);

        // Task 5b review P2: the read is settled on PendingLoad, which exists for exactly this, and
        // the posted Publish is then run. Without the wait the case proves only that the
        // constructor did not throw, the createQualityPanel lambda never runs, and the background
        // read races the fixture's delete of its temp root.
        Assert.NotNull(page.PendingLoad);
        await page.PendingLoad!.WaitAsync(TimeSpan.FromSeconds(30));
        Dispatcher.UIThread.RunJobs();

        // The quality panel factory lambda ran with the shipped seam on both sides, which is the
        // half of the wiring no other case reaches.
        Assert.IsType<QualityPanelViewModel>(page.QualityPanel);
        Assert.False(page.IsLoading);
    }
}
