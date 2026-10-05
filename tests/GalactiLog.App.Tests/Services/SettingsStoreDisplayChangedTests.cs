using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Phase 9 FIXER item 7, App side. The Data-side cases (that SaveDisplay raises, that it raises
// outside the write gate, and that a refused write raises nothing) are in
// GalactiLog.Data.Tests/SettingsStoreTests.cs; these are about what the App layer does with the
// event, over a real SettingsStore and a real DisplayColumnWriter.
public class SettingsStoreDisplayChangedTests
{
    private static (SettingsStore Store, TempDatabase Database) CreateStore()
    {
        var database = new TempDatabase("galactilog-display-changed");
        return (new SettingsStore(new SettingsRepository(database.ConnectionString)), database);
    }

    private static FrameTableViewModel CreateTable(SettingsStore store, DisplayColumnWriter columns)
        => new(
            frames: [],
            display: store.GetDisplay(),
            columns: columns,
            shell: new ShellIntegration(),
            openPreview: null,
            general: new GeneralSettings(),
            getHeaders: _ => null,
            logger: null,
            subscribeDisplayChanged: handler => store.DisplayChanged += handler,
            unsubscribeDisplayChanged: handler => store.DisplayChanged -= handler,

            // Synchronous, so the test asserts the applied state rather than a dispatcher queue.
            post: action => action());

    [Fact]
    public void AGroupTurnedOff_ReGatesAnOpenFrameTable()
    {
        var (store, database) = CreateStore();
        using var _ = database;
        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        using var table = CreateTable(store, columns);

        // quality ships enabled, so HFR starts ungated and rendered.
        var hfr = table.Columns.Single(column => column.Key == "median_hfr");
        Assert.True(hfr.IsGroupEnabled);
        Assert.True(hfr.IsShown);

        var display = store.GetDisplay();
        store.SaveDisplay(display with
        {
            Groups = new Dictionary<string, MetricGroupSettings>(display.Groups)
            {
                ["quality"] = new MetricGroupSettings(false, display.Groups["quality"].Fields),
            },
        });

        // Phase 6 read display.groups once by value (ruling Q13); the Settings Display tab is the
        // first writer, so without DisplayChanged this column would stay visible until a restart.
        Assert.False(hfr.IsGroupEnabled);
        Assert.False(hfr.IsShown);

        // Design-spec 5.8.2: the persisted visible list is a separate gate and keeps the key.
        Assert.True(hfr.IsVisible);
    }

    [Fact]
    public async Task OneColumnClick_RaisesBothNotifications_AndIsAppliedOnce()
    {
        // Reviewer focus 1: DisplayColumnWriter.Write raises its own Changed and its queued
        // SaveDisplay now raises DisplayChanged too. That is two notifications for one click and
        // it is correct, because they carry different halves of design-spec 5.8.2's document. A
        // subscriber to both must not apply the click twice.
        var (store, database) = CreateStore();
        using var _ = database;
        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        using var table = CreateTable(store, columns);

        var columnsChanged = 0;
        var displayChanged = 0;
        columns.Changed += (_, _) => columnsChanged++;
        store.DisplayChanged += (_, _) => displayChanged++;

        var fwhm = table.Columns.Single(column => column.Key == "fwhm");
        Assert.True(fwhm.IsVisible);

        table.ToggleColumnCommand.Execute(fwhm);
        await columns.Pending;

        Assert.Equal(1, columnsChanged);
        Assert.Equal(1, displayChanged);

        // Hidden once, and still hidden: the DisplayChanged handler touches only IsGroupEnabled,
        // so it cannot undo or repeat the columns half.
        Assert.False(fwhm.IsVisible);
        Assert.True(fwhm.IsGroupEnabled);
        Assert.DoesNotContain("fwhm", store.GetDisplay().Columns[DisplaySettings.FramesTableId]);
    }

    [Fact]
    public void ADisposedFrameTable_IsDetachedFromTheStore()
    {
        var (store, database) = CreateStore();
        using var _ = database;
        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        var table = CreateTable(store, columns);
        var hfr = table.Columns.Single(column => column.Key == "median_hfr");

        table.Dispose();

        var display = store.GetDisplay();
        store.SaveDisplay(display with
        {
            Groups = new Dictionary<string, MetricGroupSettings>(display.Groups)
            {
                ["quality"] = new MetricGroupSettings(false, display.Groups["quality"].Fields),
            },
        });

        // Frame tables are transient, one per expanded session card, so a collapsed card's table
        // must not stay reachable from the store for the life of the process.
        Assert.True(hfr.IsGroupEnabled);
    }

    [Fact]
    public void ApplyGroupGates_TouchesOnlyTheGroupHalf()
    {
        var (store, database) = CreateStore();
        using var _ = database;
        var columns = new DisplayColumnWriter(store.GetDisplay, store.SaveDisplay);
        using var table = CreateTable(store, columns);

        var eccentricity = table.Columns.Single(column => column.Key == "eccentricity");
        var aduMean = table.Columns.Single(column => column.Key == "adu_mean");
        Assert.True(eccentricity.IsVisible);
        Assert.False(aduMean.IsVisible);

        var display = store.GetDisplay();
        table.ApplyGroupGates(display with
        {
            Groups = new Dictionary<string, MetricGroupSettings>(display.Groups)
            {
                ["adu"] = new MetricGroupSettings(true, display.Groups["adu"].Fields),
            },
        });

        Assert.True(aduMean.IsGroupEnabled);

        // Ungating a group does not put its column in the persisted visible list.
        Assert.False(aduMean.IsVisible);
        Assert.True(eccentricity.IsVisible);
    }
}
