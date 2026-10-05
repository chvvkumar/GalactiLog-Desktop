using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one place App.Tests builds design-spec 12.7's Location, Display and Storage tabs over
/// in-memory documents: no database, no filesystem and no window.
/// </summary>
/// <remarks>
/// Four suites need these three tabs only so that <c>SettingsViewModel</c>'s primary constructor,
/// which takes one factory per real tab, has something to invoke:
/// <c>SettingsTabLazyConstructionTests</c>, <c>SettingsViewTests</c>, <c>AliasTabViewTests</c> and
/// <c>SettingsPreferenceTabViewTests</c>. One factory rather than four near-identical private
/// helpers (design-lessons rule 1). The tabs' own behaviour is asserted in
/// <c>LocationTabViewModelTests</c>, <c>DisplayTabViewModelTests</c> and
/// <c>StorageTabViewModelTests</c>, which build them directly with the seams each case needs.
/// </remarks>
internal static class PreferenceTabViewModelTestFactory
{
    /// <summary>
    /// Design-spec 12.7's General tab and design-spec 12.11's five residency controls, over an
    /// in-memory general document. The fourth tab in a file built for exactly this.
    /// </summary>
    /// <param name="seed">The stored document. Defaults to the shipped defaults, which is
    /// <c>close_to_tray</c> on and the other four off.</param>
    /// <param name="startupShortcut">Design-spec 12.11 behaviour 8's seam. Null, the default,
    /// leaves the Start with Windows control disabled with its reason on screen, which is what a
    /// build the updater did not install gets.</param>
    /// <param name="written">Receives every document the tab wrote, in order, so a case can
    /// assert which key a control changed and that it changed no other.</param>
    public static GeneralTabViewModel NewGeneralTab(
        GeneralSettings? seed = null,
        IStartupShortcut? startupShortcut = null,
        List<GeneralSettings>? written = null)
    {
        var current = seed ?? new GeneralSettings();
        return new GeneralTabViewModel(
            () => current,
            mutate =>
            {
                current = mutate(current);
                written?.Add(current);
                return current;
            },
            startupShortcut,
            post: action => action());
    }

    /// <summary>Design-spec 12.7's Location tab over an in-memory general document.</summary>
    public static LocationTabViewModel NewLocationTab(GeneralSettings? seed = null)
    {
        var current = seed ?? new GeneralSettings();
        return new LocationTabViewModel(
            () => current,
            mutate =>
            {
                current = mutate(current);
                return current;
            },

            // A fixed list, so no case depends on which zone database the machine carries.
            systemTimezones: () => ["UTC", "Europe/London", "America/New_York"],
            post: action => action());
    }

    /// <summary>
    /// Design-spec 12.7's Display tab over in-memory general, display and graph documents, with a
    /// real <c>DisplayColumnWriter</c> and <c>GraphSettingsWriter</c> over them (both are small
    /// concrete types with delegate seams, so there is nothing to fake).
    /// </summary>
    public static DisplayTabViewModel NewDisplayTab(
        GeneralSettings? general = null,
        DisplaySettings? display = null,
        Action<string>? applyTheme = null)
    {
        var currentGeneral = general ?? new GeneralSettings();
        var currentDisplay = display ?? new DisplaySettings();
        var currentGraph = new GraphSettings();

        var columns = new DisplayColumnWriter(() => currentDisplay, value => currentDisplay = value);
        var dashboard = new TargetListViewModel(currentDisplay, columns, currentGeneral.DefaultPageSize);

        return new DisplayTabViewModel(
            () => currentGeneral,
            mutate =>
            {
                currentGeneral = mutate(currentGeneral);
                return currentGeneral;
            },
            () => currentDisplay,
            value => currentDisplay = value,
            () => currentGraph,
            new GraphSettingsWriter(() => currentGraph, value => currentGraph = value),
            columns,
            () => ThemeManager.Available,

            // Never the real ThemeManager.Apply by default: it merges a resource dictionary and
            // sets the application theme variant, which is UI-thread work no tab test wants as a
            // side effect.
            applyTheme ?? (_ => { }),
            dashboardColumns: dashboard.Columns,
            toggleDashboardColumn: column => dashboard.ToggleColumnCommand.Execute(column),
            post: action => action());
    }

    /// <summary>
    /// Design-spec 12.7's Storage tab over an in-memory general document and a fixed free-space
    /// answer, so no case touches a real volume.
    /// </summary>
    public static StorageTabViewModel NewStorageTab(
        GeneralSettings? seed = null,
        (long Total, long Free)? volume = null)
    {
        var current = seed ?? new GeneralSettings();
        return new StorageTabViewModel(
            () => current,
            mutate =>
            {
                current = mutate(current);
                return current;
            },
            volumeSpace: _ => volume ?? (2_000_000_000L, 1_500_000_000L),
            defaultCacheRoot: () => @"D:\Cache\GalactiLog",
            post: action => action());
    }

    /// <summary>Joins the General tab's background read and its write chain, so a caller awaits
    /// them instead of blocking. It reads one document and overrides no companion read, so there
    /// is exactly one task to await besides the write chain.</summary>
    public static async Task<GeneralTabViewModel> SettleAsync(this GeneralTabViewModel tab)
    {
        if (tab.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        await tab.PendingWrite.ConfigureAwait(false);
        return tab;
    }

    /// <summary>Joins the tab's background read and its write chain, so a caller awaits them
    /// instead of blocking.</summary>
    public static async Task<LocationTabViewModel> SettleAsync(this LocationTabViewModel tab)
    {
        if (tab.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        await tab.PendingWrite.ConfigureAwait(false);
        return tab;
    }

    /// <summary>
    /// The same for the Display tab. It reads three documents and there is still one task to
    /// await: the general, display and graph reads share one background pass and publish once
    /// (Task 6 review finding 1).
    /// </summary>
    public static async Task<DisplayTabViewModel> SettleAsync(this DisplayTabViewModel tab)
    {
        if (tab.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        await tab.PendingWrite.ConfigureAwait(false);
        return tab;
    }

    /// <summary>The same for the Storage tab, which also probes a volume.</summary>
    public static async Task<StorageTabViewModel> SettleAsync(this StorageTabViewModel tab)
    {
        if (tab.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        if (tab.PendingFreeSpace is { } freeSpace)
        {
            await freeSpace.ConfigureAwait(false);
        }

        await tab.PendingWrite.ConfigureAwait(false);
        return tab;
    }
}
