using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.7's General tab and design-spec 12.11's five residency controls. Built through
// PreferenceTabViewModelTestFactory.NewGeneralTab over an in-memory document and awaited through
// its SettleAsync: no database, no filesystem and no window.
//
// The tab is the sixth surface on GeneralSettingsTabViewModel, so these cases assert what this tab
// adds (five keys, five immediate saves, one availability rule) and not the base class's write
// chain, which has its own suites.
public class GeneralTabViewModelTests
{
    private static GeneralSettings AllOn() => new()
    {
        CloseToTray = true,
        MinimizeToTray = true,
        StartWithWindows = true,
        StartMinimized = true,
        NotifyOnScanComplete = true,
    };

    [Fact]
    public async Task Load_SeedsAllFiveControlsFromTheStoredDocument()
    {
        using var tab = await PreferenceTabViewModelTestFactory.NewGeneralTab(AllOn()).SettleAsync();

        Assert.True(tab.CloseToTray);
        Assert.True(tab.MinimizeToTray);
        Assert.True(tab.StartWithWindows);
        Assert.True(tab.StartMinimized);
        Assert.True(tab.NotifyOnScanComplete);
        Assert.False(tab.IsLoading);
        Assert.False(tab.LoadFailed);
        Assert.True(tab.IsReady);
    }

    [Fact]
    public async Task Load_ShipsCloseToTrayOn_AndTheOtherFourOff()
    {
        // The defaults ruling Q2 and spec 12.11's key table name, read through the tab rather than
        // asserted on the record: this is the surface a user sees on a first run.
        using var tab = await PreferenceTabViewModelTestFactory.NewGeneralTab().SettleAsync();

        Assert.True(tab.CloseToTray);
        Assert.False(tab.MinimizeToTray);
        Assert.False(tab.StartWithWindows);
        Assert.False(tab.StartMinimized);
        Assert.False(tab.NotifyOnScanComplete);
    }

    [Fact]
    public async Task Seeding_DoesNotSave()
    {
        // The base class's IsApplying contract: a publish that copies the stored document into the
        // controls must not be mistaken for a user gesture by any of the five handlers.
        var written = new List<GeneralSettings>();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(AllOn(), written: written)
            .SettleAsync();

        Assert.Empty(written);
    }

    [Theory]
    [InlineData(nameof(GeneralTabViewModel.CloseToTray))]
    [InlineData(nameof(GeneralTabViewModel.MinimizeToTray))]
    [InlineData(nameof(GeneralTabViewModel.StartWithWindows))]
    [InlineData(nameof(GeneralTabViewModel.StartMinimized))]
    [InlineData(nameof(GeneralTabViewModel.NotifyOnScanComplete))]
    public async Task TogglingAControl_SavesThatKeyOnly(string key)
    {
        // Every key starts false, so one toggle sets exactly one of the five and the other four
        // must still read false in the written document.
        var written = new List<GeneralSettings>();
        var seed = new GeneralSettings { CloseToTray = false };
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(seed, written: written)
            .SettleAsync();

        Toggle(tab, key, true);
        await tab.PendingWrite;

        var document = Assert.Single(written);
        Assert.Equal(key == nameof(GeneralTabViewModel.CloseToTray), document.CloseToTray);
        Assert.Equal(key == nameof(GeneralTabViewModel.MinimizeToTray), document.MinimizeToTray);
        Assert.Equal(key == nameof(GeneralTabViewModel.StartWithWindows), document.StartWithWindows);
        Assert.Equal(key == nameof(GeneralTabViewModel.StartMinimized), document.StartMinimized);
        Assert.Equal(
            key == nameof(GeneralTabViewModel.NotifyOnScanComplete), document.NotifyOnScanComplete);
        Assert.NotNull(tab.StatusMessage);
        Assert.Null(tab.ErrorMessage);
    }

    [Fact]
    public async Task AFailedSave_RollsTheControlBack_AndShowsTheReason()
    {
        // A control that stays changed after a refused save is a lie.
        var current = new GeneralSettings { CloseToTray = true };
        using var tab = new GeneralTabViewModel(
            () => current,
            _ => throw new SettingsValidationException("close_to_tray was refused"),
            post: action => action());
        await tab.SettleAsync();

        tab.CloseToTray = false;
        await tab.PendingWrite;

        Assert.True(tab.CloseToTray);
        Assert.Equal("close_to_tray was refused", tab.ErrorMessage);
        Assert.Null(tab.StatusMessage);
    }

    [Fact]
    public async Task AnExternalChange_RepublishesTheControls()
    {
        // Another tab, or Task 9's wizard, writing the same document while this tab is alive.
        var current = new GeneralSettings();
        EventHandler<GeneralSettings>? subscriber = null;
        using var tab = new GeneralTabViewModel(
            () => current,
            mutate => current = mutate(current),
            post: action => action(),
            subscribeGeneralChanged: handler => subscriber += handler,
            unsubscribeGeneralChanged: handler => subscriber -= handler);
        await tab.SettleAsync();

        Assert.False(tab.MinimizeToTray);

        subscriber!.Invoke(this, new GeneralSettings { MinimizeToTray = true, StartMinimized = true });

        Assert.True(tab.MinimizeToTray);
        Assert.True(tab.StartMinimized);
    }

    [Fact]
    public async Task AnExternalChangeFromThisTabsOwnWrite_DoesNotRepublish()
    {
        // The base class's _writingThreadId contract: the store raises GeneralChanged
        // synchronously on the thread that wrote, so the tab's own write coming back must not be
        // treated as a change made elsewhere. The echo below deliberately carries a document
        // nothing on this tab asked for, so a tab that republished on its own write would seed
        // every control from it and the four untouched assertions below would fail.
        var current = new GeneralSettings { CloseToTray = false };
        EventHandler<GeneralSettings>? subscriber = null;
        using var tab = new GeneralTabViewModel(
            () => current,
            mutate =>
            {
                current = mutate(current);
                subscriber?.Invoke(null, AllOn());
                return current;
            },
            post: action => action(),
            subscribeGeneralChanged: handler => subscriber += handler,
            unsubscribeGeneralChanged: handler => subscriber -= handler);
        await tab.SettleAsync();

        tab.MinimizeToTray = true;
        await tab.PendingWrite;

        Assert.True(tab.MinimizeToTray);
        Assert.False(tab.CloseToTray);
        Assert.False(tab.StartWithWindows);
        Assert.False(tab.StartMinimized);
        Assert.False(tab.NotifyOnScanComplete);
    }

    [Fact]
    public async Task StartWithWindows_WithNoSeam_IsDisabledAndCarriesTheReason()
    {
        // Spec 12.11 behaviour 8: the control is offered only on a build the updater installed,
        // and elsewhere the reason is on screen rather than a silent grey (spec 12.10).
        using var tab = await PreferenceTabViewModelTestFactory.NewGeneralTab().SettleAsync();

        Assert.False(tab.CanSetStartWithWindows);
        Assert.Equal(
            GeneralTabViewModel.StartWithWindowsUnavailableMessage,
            tab.StartWithWindowsUnavailableReason);
        Assert.True(tab.HasStartWithWindowsUnavailableReason);
    }

    [Fact]
    public async Task StartWithWindows_WithASupportedSeam_IsEnabledAndCarriesNoReason()
    {
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: new RecordingStartupShortcut())
            .SettleAsync();

        Assert.True(tab.CanSetStartWithWindows);
        Assert.Null(tab.StartWithWindowsUnavailableReason);
        Assert.False(tab.HasStartWithWindowsUnavailableReason);
    }

    [Fact]
    public async Task StartWithWindows_WritesTheSettingThroughTheOneDoor()
    {
        // Task 2's half of spec 12.11 behaviour 8: the key is written through ImmediateSave, which
        // reaches SettingsStore.MutateGeneral, exactly like the other four controls. Phase 11 Task
        // 3 landed the shortcut half in the same handler and in this same file: see the
        // TogglingStartWithWindows* cases below for "the tab applies the shortcut", which is the
        // roadmap's own Verify clause for this file rather than for VelopackStartupShortcut's own
        // suite (StartupShortcutTests.cs, which never constructs the tab at all).
        var shortcut = new RecordingStartupShortcut();
        var written = new List<GeneralSettings>();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: shortcut, written: written)
            .SettleAsync();

        tab.StartWithWindows = true;
        await tab.PendingWrite;

        Assert.True(Assert.Single(written).StartWithWindows);
    }

    // ---- Phase 11 Task 3: the shortcut half of spec 12.11 behaviour 8 ------------------------
    //
    // The roadmap's Verify clause, by name: "asserts the toggle calls the shortcut seam once per
    // change and never when not installed". Every case builds the tab through
    // PreferenceTabViewModelTestFactory.NewGeneralTab with a RecordingStartupShortcut and awaits
    // SettleAsync and PendingWrite; none of them blocks (TRACKING section 2 item 8).

    [Fact]
    public async Task TogglingStartWithWindowsOn_CallsTheSeamOnce_WithTrue()
    {
        var shortcut = new RecordingStartupShortcut();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: shortcut)
            .SettleAsync();

        tab.StartWithWindows = true;
        await tab.PendingWrite;

        Assert.Equal(new[] { true }, shortcut.Applied);
    }

    [Fact]
    public async Task TogglingStartWithWindowsOff_CallsTheSeamOnce_WithFalse()
    {
        var shortcut = new RecordingStartupShortcut();
        var seed = new GeneralSettings { StartWithWindows = true };
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(seed, startupShortcut: shortcut)
            .SettleAsync();

        tab.StartWithWindows = false;
        await tab.PendingWrite;

        Assert.Equal(new[] { false }, shortcut.Applied);
    }

    [Fact]
    public async Task TogglingStartWithWindowsTwice_CallsTheSeamTwice_NotFourTimes()
    {
        var shortcut = new RecordingStartupShortcut();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: shortcut)
            .SettleAsync();

        tab.StartWithWindows = true;
        await tab.PendingWrite;
        tab.StartWithWindows = false;
        await tab.PendingWrite;

        Assert.Equal(new[] { true, false }, shortcut.Applied);
    }

    [Fact]
    public async Task SeedingStartWithWindowsFromTheStoredDocument_CallsTheSeamNever()
    {
        // The IsApplying contract: publishing a stored true must not rewrite the shortcut on
        // every application start.
        var shortcut = new RecordingStartupShortcut();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(AllOn(), startupShortcut: shortcut)
            .SettleAsync();

        Assert.True(tab.StartWithWindows);
        Assert.Empty(shortcut.Applied);
    }

    [Fact]
    public async Task WithAnUnsupportedSeam_TogglingSavesTheKeyAndReportsNoFailure()
    {
        // Phase review finding P5. This tab no longer re-tests IsSupported beside Apply: the seam
        // owns that check, and Apply reports success for a build that had nothing to do, so a
        // false from it means the one thing it should. VelopackStartupShortcut still guards every
        // member it has on IsSupported, which is where the defense in depth actually lives
        // (StartupShortcutTests.NotInstalled_Apply_DoesNothingAndReportsNoFailure), so even a
        // direct assignment that bypassed the control's own IsEnabled binding reaches neither
        // Velopack's locator nor the Startup folder.
        //
        // What the user sees is what this pins: the key carries the intent, the checkbox stays
        // where it was put, and no error is shown.
        var shortcut = new RecordingStartupShortcut { IsSupported = false };
        var written = new List<GeneralSettings>();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: shortcut, written: written)
            .SettleAsync();

        tab.StartWithWindows = true;
        await tab.PendingWrite;

        Assert.True(tab.StartWithWindows);
        Assert.Null(tab.ErrorMessage);
        Assert.True(Assert.Single(written).StartWithWindows);
    }

    [Fact]
    public async Task WithNoSeam_TogglingCallsNothingAndSavesTheKey()
    {
        // An absent seam has nothing to call, but the key still carries the user's intent even
        // where nothing can act on it yet (a dotnet run, or a build the updater did not install).
        var written = new List<GeneralSettings>();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(written: written)
            .SettleAsync();

        tab.StartWithWindows = true;
        await tab.PendingWrite;

        Assert.True(Assert.Single(written).StartWithWindows);
    }

    [Fact]
    public async Task AFailedApply_ShowsTheReason_AndLeavesTheControlAgreeingWithTheFilesystem()
    {
        var shortcut = new RecordingStartupShortcut { Result = false };
        var written = new List<GeneralSettings>();
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: shortcut, written: written)
            .SettleAsync();

        tab.StartWithWindows = true;
        await tab.PendingWrite;

        // Nothing was persisted (the preWrite step throws before SettingsStore.MutateGeneral is
        // even called), so the checkbox rolls back to false, which is what the filesystem still
        // is.
        Assert.False(tab.StartWithWindows);
        Assert.NotNull(tab.ErrorMessage);
        Assert.Empty(written);
    }

    [Fact]
    public async Task TogglingStartWithWindows_CallsTheSeam_NeverWhileTheMutateDelegateIsRunning()
    {
        // Phase 11 Task 3 review, Important 1: Apply, a COM plus filesystem call, must never run
        // while SettingsStore.MutateGeneral holds its process-wide write gate. The mutateGeneral
        // delegate below stands in for that whole call, gate included, so proving the seam is
        // never invoked while this delegate is on the stack is the unit-level proof that the fix
        // moved Apply outside it.
        var current = new GeneralSettings();
        var insideMutate = false;
        var seenInsideMutate = false;
        var shortcut = new RecordingStartupShortcut
        {
            OnApply = () => seenInsideMutate |= insideMutate,
        };
        using var tab = new GeneralTabViewModel(
            () => current,
            mutate =>
            {
                insideMutate = true;
                try
                {
                    current = mutate(current);
                    return current;
                }
                finally
                {
                    insideMutate = false;
                }
            },
            shortcut,
            post: action => action());
        await tab.SettleAsync();

        tab.StartWithWindows = true;
        await tab.PendingWrite;

        Assert.False(seenInsideMutate);
        Assert.Equal(new[] { true }, shortcut.Applied);
    }

    [Fact]
    public async Task CanSetStartWithWindows_FollowsTheSeamsIsSupported()
    {
        using var supported = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: new RecordingStartupShortcut { IsSupported = true })
            .SettleAsync();
        using var unsupported = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: new RecordingStartupShortcut { IsSupported = false })
            .SettleAsync();

        Assert.True(supported.CanSetStartWithWindows);
        Assert.False(unsupported.CanSetStartWithWindows);
    }

    [Fact]
    public async Task StartWithWindowsUnavailableReason_IsNull_WhenItCanBeSet()
    {
        using var tab = await PreferenceTabViewModelTestFactory
            .NewGeneralTab(startupShortcut: new RecordingStartupShortcut())
            .SettleAsync();

        Assert.Null(tab.StartWithWindowsUnavailableReason);
        Assert.False(tab.HasStartWithWindowsUnavailableReason);
    }

    [Fact]
    public async Task Constructor_ReadsNothingOnTheConstructingThread()
    {
        // TRACKING section 6 item 16: the read happens inside Load's Task.Run, so resolving the
        // tab costs no SQLite read on the thread that constructed it.
        //
        // The thread is asserted rather than a count taken immediately after the constructor
        // returns (review finding 3): the pool thread can win that race and increment a counter
        // before the assertion runs, so a count-based case can fail with no defect present. The
        // read is awaited through PendingLoad and then the thread it ran on is checked, which is
        // the property the case is actually about (TRACKING section 2 item 8: awaited, never
        // blocked on).
        var constructingThread = Environment.CurrentManagedThreadId;
        var readThreads = new System.Collections.Concurrent.ConcurrentQueue<int>();
        using var tab = new GeneralTabViewModel(
            () =>
            {
                readThreads.Enqueue(Environment.CurrentManagedThreadId);
                return new GeneralSettings();
            },
            mutate => mutate(new GeneralSettings()),
            post: action => action());

        await tab.SettleAsync();

        Assert.NotEmpty(readThreads);
        Assert.All(readThreads, thread => Assert.NotEqual(constructingThread, thread));
    }

    [Fact]
    public async Task Dispose_DetachesTheGeneralChangedSubscription()
    {
        var current = new GeneralSettings();
        EventHandler<GeneralSettings>? subscriber = null;
        var tab = new GeneralTabViewModel(
            () => current,
            mutate => current = mutate(current),
            post: action => action(),
            subscribeGeneralChanged: handler => subscriber += handler,
            unsubscribeGeneralChanged: handler => subscriber -= handler);
        await tab.SettleAsync();

        Assert.NotNull(subscriber);
        tab.Dispose();

        Assert.Null(subscriber);
        Assert.True(tab.IsDisposed);
    }

    private static void Toggle(GeneralTabViewModel tab, string key, bool value)
    {
        switch (key)
        {
            case nameof(GeneralTabViewModel.CloseToTray):
                tab.CloseToTray = value;
                break;
            case nameof(GeneralTabViewModel.MinimizeToTray):
                tab.MinimizeToTray = value;
                break;
            case nameof(GeneralTabViewModel.StartWithWindows):
                tab.StartWithWindows = value;
                break;
            case nameof(GeneralTabViewModel.StartMinimized):
                tab.StartMinimized = value;
                break;
            case nameof(GeneralTabViewModel.NotifyOnScanComplete):
                tab.NotifyOnScanComplete = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Not one of the five keys.");
        }
    }
}
