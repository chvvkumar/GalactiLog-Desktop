using System.Text.Json;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 15A Task 6 brief, sections 8.1 to 8.4. The panel takes delegates, never a store, so every
// case here runs with lambdas and no database (design-spec 18.3).
public class Phd2ProfilesViewModelTests
{
    // The general document, in memory, mirroring LocationTabViewModelTests' FakeStore.
    private sealed class FakeStore
    {
        public FakeStore(GeneralSettings? seed = null) => Current = seed ?? new GeneralSettings();

        public GeneralSettings Current { get; private set; }

        public int Saves { get; private set; }

        public string? RefuseWith { get; set; }

        public GeneralSettings Get() => Current;

        /// <summary>How many times this store raised the real store's
        /// <c>Phd2GuidingInputsChanged</c>, which is the one trigger of the correlation re-run.
        /// </summary>
        public int GuidingInputsChanges { get; private set; }

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate)
        {
            var current = Current;
            var next = mutate(current);

            if (RefuseWith is { } message)
            {
                throw new SettingsValidationException(message);
            }

            // As the real SettingsStore.MutateGeneral behaves, and the review's P3-2: it
            // serializes and saves unconditionally, so a callback that returns the document it was
            // given still writes. What is gated is the guiding-inputs event below, on the same
            // structural comparison the store makes (SameMap over Normalize of both sides, plus
            // the three observer fields), never on reference identity.
            Current = next;
            Saves++;
            if (GuidingInputsMoved(current, next))
            {
                GuidingInputsChanges++;
            }

            return next;
        }

        private static bool GuidingInputsMoved(GeneralSettings before, GeneralSettings after)
            => !Phd2Profiles.SameMap(
                   Phd2Profiles.Normalize(before.Phd2ProfileMap),
                   Phd2Profiles.Normalize(after.Phd2ProfileMap))
                || !string.Equals(before.ObserverTimezone, after.ObserverTimezone, StringComparison.Ordinal)
                || before.ObserverLatitude != after.ObserverLatitude
                || before.ObserverLongitude != after.ObserverLongitude;
    }

    private static readonly DateTime Seen = new(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc);

    // A profile every one of whose sessions resolved a zone: both "seen" values are real instants.
    private static Phd2ProfileRow Row(string profile = "Rig A")
        => new(profile, "ZWO ASI294MM Mini", 500, 1.23, 5, Seen.AddDays(-10), Seen);

    // The same profile as the query returns it while no zone has resolved for it: both values are
    // the guide log's own wall clock, and the row says so (fix-wave review P2-1).
    private static Phd2ProfileRow LogClockRow(string profile = "Rig A")
        => new(
            profile, "ZWO ASI294MM Mini", 500, 1.23, 5, Seen.AddDays(-10), Seen,
            FirstSeenIsLogClock: true, LastSeenIsLogClock: true);

    private static Phd2ProfilesViewModel Create(
        FakeStore store,
        IReadOnlyList<Phd2ProfileRow>? rows = null,
        IReadOnlyList<string>? telescopes = null)
        => new(
            store.Get,
            () => rows ?? [Row()],
            store.Mutate,
            () => telescopes ?? ["Askar FMA180", "Celestron EdgeHD 8"],
            systemTimezones: () => ["America/New_York", "Europe/London", "UTC"],
            resolveTimezone: TimeZoneInfo.FindSystemTimeZoneById,
            post: action => action());

    // The read and every write are off the calling thread; a test awaits them instead of
    // blocking (TRACKING.md section 2 item 8, and this task's own rule 5: the query runs off the
    // UI thread and a test that asserts it awaits the work).
    private static async Task Settle(Phd2ProfilesViewModel panel)
    {
        if (panel.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        await panel.PendingWrite.ConfigureAwait(false);
    }

    private static async Task<(Phd2ProfilesViewModel Panel, FakeStore Store)> CreateAndSettleAsync(
        FakeStore? store = null,
        IReadOnlyList<Phd2ProfileRow>? rows = null,
        IReadOnlyList<string>? telescopes = null)
    {
        store ??= new FakeStore();
        var panel = Create(store, rows, telescopes);
        await Settle(panel).ConfigureAwait(false);
        return (panel, store);
    }

    private static JsonElement Map(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    // ---- 8.1 the map write ---------------------------------------------------------------------

    [Fact]
    public async Task SettingATelescope_LeavesTheOtherThreeFieldsOfThatEntryAndEveryOtherEntryIntact()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""
                {
                  "Rig A": { "telescope": null, "timezone": "America/New_York", "latitude": 30.1, "longitude": -97.5 },
                  "Rig B": { "telescope": "Celestron EdgeHD 8", "timezone": "", "latitude": null, "longitude": null }
                }
                """),
        };
        var (panel, store) = await CreateAndSettleAsync(
            new FakeStore(seed), rows: [Row("Rig A"), Row("Rig B")]);

        var row = panel.Rows.Single(r => r.Profile == "Rig A");
        row.SelectedTelescope = row.TelescopeOptions.Single(o => o.Value == "Askar FMA180");
        await Settle(panel);

        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        Assert.Equal("Askar FMA180", map["Rig A"].Telescope);
        Assert.Equal("America/New_York", map["Rig A"].Timezone);
        Assert.Equal(30.1, map["Rig A"].Latitude);
        Assert.Equal(-97.5, map["Rig A"].Longitude);

        // Every other entry, byte-identical.
        Assert.Equal("Celestron EdgeHD 8", map["Rig B"].Telescope);
        Assert.Equal("", map["Rig B"].Timezone);
        Assert.Null(map["Rig B"].Latitude);
    }

    [Fact]
    public async Task SettingAZoneOnAProfileWithNoEntry_CreatesTheEntryWithTheOtherThreeFieldsAtTheirDefaults()
    {
        var (panel, store) = await CreateAndSettleAsync(rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        row.SelectedTimezone = row.TimezoneOptions.Single(o => o.Id == "UTC");
        await Settle(panel);

        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        Assert.Equal("UTC", map["Rig A"].Timezone);
        Assert.Null(map["Rig A"].Telescope);
        Assert.Null(map["Rig A"].Latitude);
        Assert.Null(map["Rig A"].Longitude);
    }

    [Fact]
    public async Task ClearingTheTelescope_KeepsAnEntryThatStillCarriesAZone_AndRemovesAnEmptyOne()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""
                {
                  "Rig A": { "telescope": "Askar FMA180", "timezone": "UTC", "latitude": null, "longitude": null },
                  "Rig B": { "telescope": "Askar FMA180", "timezone": "", "latitude": null, "longitude": null }
                }
                """),
        };
        var (panel, store) = await CreateAndSettleAsync(
            new FakeStore(seed), rows: [Row("Rig A"), Row("Rig B")]);

        panel.Rows.Single(r => r.Profile == "Rig A").SelectedTelescope
            = panel.Rows.Single(r => r.Profile == "Rig A").TelescopeOptions.Single(o => o.Value == "");
        await Settle(panel);
        panel.Rows.Single(r => r.Profile == "Rig B").SelectedTelescope
            = panel.Rows.Single(r => r.Profile == "Rig B").TelescopeOptions.Single(o => o.Value == "");
        await Settle(panel);

        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        Assert.True(map.ContainsKey("Rig A"));
        Assert.Null(map["Rig A"].Telescope);
        Assert.Equal("UTC", map["Rig A"].Timezone);
        Assert.False(map.ContainsKey("Rig B"));
    }

    [Fact]
    public async Task LatitudeAndLongitudeZero_AreStoredAndReadBackAsSet()
    {
        var (panel, store) = await CreateAndSettleAsync(rows: [Row("Rig A")]);
        var row = panel.Rows.Single();

        row.LatitudeText = "0";
        await Settle(panel);
        row.LongitudeText = "0";
        await Settle(panel);

        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        Assert.NotNull(map["Rig A"].Latitude);
        Assert.Equal(0d, map["Rig A"].Latitude);
        Assert.NotNull(map["Rig A"].Longitude);
        Assert.Equal(0d, map["Rig A"].Longitude);
    }

    [Theory]
    [InlineData("91")]
    [InlineData("-91")]
    [InlineData("not a number")]
    public async Task AnOutOfRangeOrUnparseableLatitude_IsRefusedAndTheBoxReverts(string text)
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": null, "timezone": "", "latitude": 12.5, "longitude": null}}"""),
        };
        var (panel, store) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);
        var row = panel.Rows.Single();
        var savesBefore = store.Saves;

        row.LatitudeText = text;
        await Settle(panel);

        Assert.NotNull(row.LatitudeError);
        Assert.Equal(savesBefore, store.Saves);
        Assert.Equal(12.5, Phd2Profiles.Normalize(store.Current.Phd2ProfileMap)["Rig A"].Latitude);
    }

    [Fact]
    public async Task ALegacyStringMap_RendersAMappedRow_AndAnEditRewritesTheObjectForm()
    {
        var seed = new GeneralSettings { Phd2ProfileMap = Map("""{"Rig A": "Askar FMA180"}""") };
        var (panel, store) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.Equal("Askar FMA180", row.SelectedTelescope?.Value);

        row.LatitudeText = "10";
        await Settle(panel);

        var raw = store.Current.Phd2ProfileMap!.Value;
        Assert.Equal(JsonValueKind.Object, raw.GetProperty("Rig A").ValueKind);
        Assert.Equal("Askar FMA180", raw.GetProperty("Rig A").GetProperty("telescope").GetString());
    }

    // ---- 8.2 the resolution display -------------------------------------------------------------

    [Fact]
    public async Task AProfileWithItsOwnZone_ReportsTheProfileSource()
    {
        var seed = new GeneralSettings
        {
            ObserverTimezone = "UTC",
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": null, "timezone": "America/New_York", "latitude": null, "longitude": null}}"""),
        };
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.False(row.ZoneIsUnset);
        // Ruling U4: the offset-first label, never the stored id. America/New_York's standard
        // offset is minus five whatever the date, because the builder reads BaseUtcOffset.
        Assert.Contains("GMT-05:00", row.ZoneResolutionText);
        Assert.DoesNotContain("America/New_York", row.ZoneResolutionText);
        Assert.Contains("this profile's own value", row.ZoneResolutionText);
    }

    [Fact]
    public async Task AProfileWithNoZoneAndAGlobalOne_ReportsTheObserverSource()
    {
        var seed = new GeneralSettings { ObserverTimezone = "UTC" };
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.False(row.ZoneIsUnset);
        Assert.Contains("GMT+00:00", row.ZoneResolutionText);
        Assert.Contains("the observer's value", row.ZoneResolutionText);
    }

    // Phase review set D item 1, Task 6b review P2-4. The line read
    // "Timezone: Mountain Standard Time (Mexico) (this profile's own value)" six lines under a
    // picker on the same row reading "GMT-07:00 La Paz, Mazatlan": two names for one zone on one
    // screen, against Phase 14C user ruling U4 and spec 12.7's Timezone row.
    [Fact]
    public async Task TheResolutionLine_ReadsTheOffsetFirstLabel_AndNeverTheRawZoneId()
    {
        const string windowsId = "SA Western Standard Time";
        var seed = new GeneralSettings
        {
            Timezone = "UTC",
            Phd2ProfileMap = Map(
                """{"Rig A": {"telescope": null, "timezone": "SA Western Standard Time", "latitude": null, "longitude": null}}"""),
        };
        var store = new FakeStore(seed);
        var panel = new Phd2ProfilesViewModel(
            store.Get,
            () => [Row("Rig A")],
            store.Mutate,
            () => [],
            systemTimezones: () => [windowsId, "UTC"],
            resolveTimezone: TimeZoneInfo.FindSystemTimeZoneById,
            post: action => action());
        await Settle(panel);

        var row = panel.Rows.Single();
        var pickerLabel = row.TimezoneOptions.Single(option => option.Id == windowsId).Label;

        Assert.StartsWith("GMT-04:00 ", pickerLabel, StringComparison.Ordinal);
        Assert.Contains(pickerLabel, row.ZoneResolutionText, StringComparison.Ordinal);
        Assert.DoesNotContain(windowsId, row.ZoneResolutionText, StringComparison.Ordinal);
    }

    // The one zone the picker cannot label by offset: a value stored on another machine. The row
    // keeps whatever its own picker says rather than falling back to a third wording.
    [Fact]
    public async Task AStoredZoneThisMachineDoesNotList_KeepsThePickersOwnLabel()
    {
        const string unlisted = "Olympus Mons Standard Time";
        var seed = new GeneralSettings
        {
            Timezone = "UTC",
            Phd2ProfileMap = Map(
                """{"Rig A": {"telescope": null, "timezone": "Olympus Mons Standard Time", "latitude": null, "longitude": null}}"""),
        };
        var store = new FakeStore(seed);
        var panel = new Phd2ProfilesViewModel(
            store.Get,
            () => [Row("Rig A")],
            store.Mutate,
            () => [],
            systemTimezones: () => ["UTC"],
            // A resolver that answers for any id: the stored zone loads, it is simply not one of
            // the ids this machine's picker offers, which is the case WithStoredTimezone exists
            // for.
            resolveTimezone: _ => TimeZoneInfo.Utc,
            post: action => action());
        await Settle(panel);

        var row = panel.Rows.Single();
        Assert.Contains(
            unlisted + Phd2ProfilesViewModel.NotOnThisMachineSuffix,
            row.ZoneResolutionText,
            StringComparison.Ordinal);
    }

    // Phase review set D item 2, Task 6b review P3-6. The column rendered a bare UTC instant with
    // no zone said, one line above a sentence naming the profile's zone, so the same session read
    // 21:31 before the zone was set and 04:31 after: a silent change of basis that invites the
    // reading that the zone was applied to it. It now reads in the display timezone, through the
    // one formatter the frame table and the Activity feed use.
    [Fact]
    public async Task FirstAndLastSeen_RenderInTheDisplayTimezone_WhenAZoneResolved()
    {
        var seed = new GeneralSettings
        {
            // Four hours behind UTC, so the conversion rolls the date back rather than only
            // moving the clock: a failure that ignored the display zone cannot pass this.
            Timezone = "SA Western Standard Time",
            ObserverTimezone = "UTC",
        };
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.False(row.ZoneIsUnset);
        Assert.Equal("2026-02-28 10:00 PM", row.LastSeenText);
        Assert.Equal("2026-02-18 10:00 PM", row.FirstSeenText);
        Assert.DoesNotContain(
            Phd2ProfilesViewModel.UnzonedSeenSuffix, row.LastSeenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstAndLastSeen_FollowTheObserverZone_WhenTheDisplayTimezoneIsEmpty()
    {
        // Polish wave 3 ruling 1. Failure looks like this machine's zone on the stamp: 7:30 AM
        // here, where the observer's Tokyo zone says 11:00 AM.
        var seed = new GeneralSettings { Timezone = "", ObserverTimezone = "Tokyo Standard Time" };
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.Equal("2026-03-01 11:00 AM", row.LastSeenText);
        Assert.Equal("2026-02-19 11:00 AM", row.FirstSeenText);
    }

    [Fact]
    public async Task FirstAndLastSeen_ShowTheGuideLogsOwnClockAndSaySo_WhenNoZoneResolved()
    {
        // No profile zone and no observer zone: the stored value is the wall clock PHD2 wrote,
        // which belongs to no zone, so it is shown as written and marked. Never blank, and never
        // shifted by the display zone, which would invent an instant the session never had.
        var seed = new GeneralSettings { Timezone = "SA Western Standard Time" };
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(seed), rows: [LogClockRow("Rig A")]);

        var row = panel.Rows.Single();
        Assert.True(row.ZoneIsUnset);
        Assert.Equal(
            "2026-03-01 2:00 AM" + Phd2ProfilesViewModel.UnzonedSeenSuffix, row.LastSeenText);
        Assert.Equal(
            "2026-02-19 2:00 AM" + Phd2ProfilesViewModel.UnzonedSeenSuffix, row.FirstSeenText);
    }

    // ---- fix-wave review P2-1: the basis travels with the value ----------------------------------
    //
    // Deriving it from the CURRENT resolution made the panel lie the instant a zone was set: the
    // commit path calls RefreshResolution and never Reload, so the row still held the guide log's
    // naive wall clock while the formatter had already begun treating it as a UTC instant.

    private static Phd2ProfilesViewModel Panel(
        FakeStore store, Func<IReadOnlyList<Phd2ProfileRow>> rows)
        => new(
            store.Get,
            rows,
            store.Mutate,
            () => [],
            systemTimezones: () => ["UTC"],
            resolveTimezone: TimeZoneInfo.FindSystemTimeZoneById,
            post: action => action());

    [Fact]
    public async Task SettingAZoneOnAnUnzonedProfile_LeavesTheSeenValuesOnTheLogClock_UntilAReloadDeliversReDerivedInstants()
    {
        var store = new FakeStore(new GeneralSettings { Timezone = "SA Western Standard Time" });
        IReadOnlyList<Phd2ProfileRow> rows = [LogClockRow("Rig A")];
        var panel = Panel(store, () => rows);
        await Settle(panel);

        var row = panel.Rows.Single();
        var before = row.LastSeenText;
        Assert.Equal("2026-03-01 2:00 AM" + Phd2ProfilesViewModel.UnzonedSeenSuffix, before);

        // The user picks a zone. The stored session times have NOT been re-derived yet: the re-run
        // is asynchronous and can wait on the scan's resolution lease for up to thirty minutes.
        row.SelectedTimezone = row.TimezoneOptions.Single(option => option.Id == "UTC");
        await Settle(panel);

        Assert.False(row.ZoneIsUnset);
        Assert.Equal(before, row.LastSeenText);

        // The re-run lands, and the next read returns re-derived instants.
        rows = [Row("Rig A")];
        panel.Reload();
        await Settle(panel);

        var reloaded = panel.Rows.Single();
        Assert.Equal("2026-02-28 10:00 PM", reloaded.LastSeenText);
        Assert.DoesNotContain(
            Phd2ProfilesViewModel.UnzonedSeenSuffix, reloaded.LastSeenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearingAZone_DoesNotStampAStoredInstantAsALogClock()
    {
        var seed = new GeneralSettings
        {
            Timezone = "SA Western Standard Time",
            Phd2ProfileMap = Map(
                """{"Rig A": {"telescope": null, "timezone": "UTC", "latitude": null, "longitude": null}}"""),
        };
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.Equal("2026-02-28 10:00 PM", row.LastSeenText);

        // Back to "Use the observer timezone", with no observer zone set. The row resolves to
        // nothing now, but what it HOLDS is still the instant the read gave it.
        row.SelectedTimezone = row.TimezoneOptions.Single(option => option.Id.Length == 0);
        await Settle(panel);

        Assert.True(row.ZoneIsUnset);
        Assert.Equal("2026-02-28 10:00 PM", row.LastSeenText);
        Assert.DoesNotContain(
            Phd2ProfilesViewModel.UnzonedSeenSuffix, row.LastSeenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithGuideLogReadingOff_SettingAZone_LeavesTheSeenValuesOnTheLogClockForGood()
    {
        // The worst form of the defect: with the key off no correlation pass ever runs, so the
        // stored value is never re-derived and a basis taken from the current resolution would be
        // wrong for as long as the key stayed off, not merely until the next pass.
        var store = new FakeStore(new GeneralSettings
        {
            Timezone = "SA Western Standard Time",
            Phd2ScanEnabled = false,
        });
        var panel = Panel(store, () => [LogClockRow("Rig A")]);
        await Settle(panel);

        var row = panel.Rows.Single();
        row.SelectedTimezone = row.TimezoneOptions.Single(option => option.Id == "UTC");
        await Settle(panel);

        Assert.False(row.ZoneIsUnset);
        Assert.Equal("2026-03-01 2:00 AM" + Phd2ProfilesViewModel.UnzonedSeenSuffix, row.LastSeenText);
        Assert.Equal("2026-02-19 2:00 AM" + Phd2ProfilesViewModel.UnzonedSeenSuffix, row.FirstSeenText);
    }

    // ---- fix-wave review P2-2: the panel says a re-run is still owed -----------------------------

    [Fact]
    public async Task AnOwedReRun_IsSaidOnThePanelAtLoad()
    {
        var (panel, _) = await CreateAndSettleAsync(
            new FakeStore(new GeneralSettings { Phd2CorrelationPending = true }));

        Assert.Equal(Phd2ProfilesViewModel.ReRunOwedMessage, panel.PendingMessage);
        Assert.True(panel.HasPendingMessage);
    }

    [Fact]
    public async Task NothingOwed_SaysNothing()
    {
        var (panel, _) = await CreateAndSettleAsync();

        Assert.Null(panel.PendingMessage);
        Assert.False(panel.HasPendingMessage);
    }

    [Fact]
    public async Task AnOwedReRunWithGuideLogReadingOff_NamesTheLibraryTabsSwitch_AndPromisesNoBackgroundWork()
    {
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(new GeneralSettings
        {
            Phd2CorrelationPending = true,
            Phd2ScanEnabled = false,
        }));

        Assert.Equal(Phd2ProfilesViewModel.ReRunOwedButDisabledMessage, panel.PendingMessage);
        Assert.True(panel.HasPendingMessage);
        Assert.Contains("Library tab", panel.PendingMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("background", panel.PendingMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheQueuedSentence_HidesTheOwedSentence_WhileItIsUp()
    {
        var (panel, _) = await CreateAndSettleAsync(
            new FakeStore(new GeneralSettings { Phd2CorrelationPending = true }),
            rows: [Row("Rig A")]);
        Assert.True(panel.HasPendingMessage);

        panel.Rows.Single().LatitudeText = "30.1";
        await Settle(panel);

        // Both are true of the world at this moment, and the newer, more specific one wins: the
        // owed sentence is still held, and hidden.
        Assert.Equal(Phd2ProfilesViewModel.ReRunQueuedMessage, panel.StatusMessage);
        Assert.NotNull(panel.PendingMessage);
        Assert.False(panel.HasPendingMessage);
    }

    [Fact]
    public async Task AProfileWithNeitherZone_CarriesTheUnsetWarning()
    {
        var (panel, _) = await CreateAndSettleAsync(rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.True(row.ZoneIsUnset);
        Assert.Equal(Phd2ProfilesViewModel.UnsetZoneWarning, row.ZoneResolutionText);
    }

    [Fact]
    public async Task LatitudeAndLongitude_ResolveIndependently()
    {
        var seed = new GeneralSettings
        {
            ObserverLongitude = -97.5,
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": null, "timezone": "", "latitude": 30.1, "longitude": null}}"""),
        };
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.Contains("this profile's own value", row.SiteResolutionText);
        Assert.Contains("the observer's value", row.SiteResolutionText);
    }

    // ---- 8.3 the rename rewrite -------------------------------------------------------------------

    [Fact]
    public async Task RewriteTelescopes_MovesEveryEntrysTelescopeOntoTheNewName_AndTimezoneAndSiteSurvive()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""
                {"Rig A": {"telescope": "Askar 120", "timezone": "UTC", "latitude": 5.0, "longitude": null}}
                """),
        };
        var store = new FakeStore(seed);
        var panel = Create(store);
        await Settle(panel);

        var changed = panel.RewriteTelescopes(name => name == "Askar 120" ? "Askar FMA180" : null);
        await Settle(panel);

        Assert.True(changed);
        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        Assert.Equal("Askar FMA180", map["Rig A"].Telescope);
        Assert.Equal("UTC", map["Rig A"].Timezone);
        Assert.Equal(5.0, map["Rig A"].Latitude);
        Assert.Equal(1, store.GuidingInputsChanges);
        Assert.Equal(Phd2ProfilesViewModel.ReRunQueuedMessage, panel.StatusMessage);
    }

    [Fact]
    public async Task RewriteTelescopes_ARenameThatMatchesNoEntry_WritesNoChangeAndQueuesNoReRun()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": "Askar 120", "timezone": "", "latitude": null, "longitude": null}}"""),
        };
        var store = new FakeStore(seed);
        var panel = Create(store);
        await Settle(panel);

        var changed = panel.RewriteTelescopes(_ => null);

        Assert.False(changed);
        // The real store writes the document it is handed on every MutateGeneral call, so what a
        // no-change save must leave alone is the map itself and the guiding-inputs event that
        // queues the re-run, never the save count (review P3-2).
        Assert.Equal("Askar 120", Phd2Profiles.Normalize(store.Current.Phd2ProfileMap)["Rig A"].Telescope);
        Assert.Equal(0, store.GuidingInputsChanges);
        Assert.Null(panel.StatusMessage);
    }

    // ---- 8.4 the re-run seam and the save command's guard ----------------------------------------

    [Fact]
    public async Task AChangedSave_RaisesTheStoresGuidingInputsSignalOnce_AndSaysAReRunIsQueued()
    {
        var (panel, store) = await CreateAndSettleAsync(rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        row.LatitudeText = "10";
        await Settle(panel);

        Assert.Equal(1, store.GuidingInputsChanges);
        Assert.Equal(Phd2ProfilesViewModel.ReRunQueuedMessage, panel.StatusMessage);
    }

    [Fact]
    public async Task AnUnchangedSave_MovesNoMapAndQueuesNothing_AndClaimsNoReRun()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": "Askar FMA180", "timezone": "", "latitude": null, "longitude": null}}"""),
        };
        var store = new FakeStore(seed);
        var (panel, _) = await CreateAndSettleAsync(store, rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        // Re-selecting the already-stored telescope resolves to the same entry. SameMap must
        // refuse the write; with SameMap swapped for != the panel would write the document and
        // claim a re-run, because both sides are freshly allocated dictionaries and != is a
        // reference test (case 13, the "no job per keystroke on an autosaving field" rule).
        await panel.SaveCommand.ExecuteAsync(
            new Phd2RowEdit(row, entry => entry with { Telescope = "Askar FMA180" }, Revert: () => { }));

        Assert.Equal(0, store.GuidingInputsChanges);
        Assert.Null(panel.StatusMessage);
        Assert.Equal("Askar FMA180", Phd2Profiles.Normalize(store.Current.Phd2ProfileMap)["Rig A"].Telescope);
    }

    [Fact]
    public async Task TwoCommitsBackToBack_BothReachTheStoreInOrder_AndNeitherOverlaps()
    {
        // Review P1-2, second half. The save command's body guard refuses a concurrent write
        // (TRACKING item 13) and used to drop the refused edit on the floor with no write, no
        // revert and no message, so a coordinate the user had committed was lost while the box on
        // screen still showed it. The refused edit is chained instead: both writes land, in order.
        var store = new FakeStore();
        var gate = new SemaphoreSlim(0);
        var inFlight = 0;
        var overlapped = false;
        var order = new List<double?>();
        var panel = new Phd2ProfilesViewModel(
            store.Get,
            () => [Row("Rig A")],
            mutate =>
            {
                if (Interlocked.Increment(ref inFlight) > 1)
                {
                    overlapped = true;
                }

                gate.Wait(TimeSpan.FromSeconds(5));
                var written = store.Mutate(mutate);
                order.Add(Phd2Profiles.Normalize(written.Phd2ProfileMap)["Rig A"].Latitude);
                Interlocked.Decrement(ref inFlight);
                return written;
            },
            () => ["Askar FMA180"],
            post: action => action());
        await Settle(panel);
        var row = panel.Rows.Single();

        var first = panel.SaveCommand.ExecuteAsync(
            new Phd2RowEdit(row, entry => entry with { Latitude = 1 }, Revert: () => { }));
        var second = panel.SaveCommand.ExecuteAsync(
            new Phd2RowEdit(row, entry => entry with { Latitude = 2 }, Revert: () => { }));
        gate.Release(2);
        await Task.WhenAll(first, second);
        await Settle(panel);

        Assert.False(overlapped, "two writes of the same document overlapped");
        Assert.Equal(new double?[] { 1d, 2d }, order);
        Assert.Equal(2d, Phd2Profiles.Normalize(store.Current.Phd2ProfileMap)["Rig A"].Latitude);
    }

    // ---- the pickers: a stored value neither list offers -----------------------------------------

    [Fact]
    public async Task AStoredZoneThisMachineDoesNotList_IsOfferedAndSelected_AndSurvivesAnUnrelatedEdit()
    {
        // The web application stores IANA ids; this machine lists Windows ids. Dropping the stored
        // entry showed the row as inheriting the observer zone while the resolution line directly
        // below it named the stored zone, and the user had no way to see or restore what they set
        // (review P2-1).
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": null, "timezone": "America/La_Paz", "latitude": null, "longitude": null}}"""),
        };
        var (panel, store) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.NotNull(row.SelectedTimezone);
        Assert.Equal("America/La_Paz", row.SelectedTimezone.Id);
        Assert.Contains("America/La_Paz", row.SelectedTimezone.Label);

        row.LatitudeText = "10";
        await Settle(panel);

        Assert.Equal("America/La_Paz", Phd2Profiles.Normalize(store.Current.Phd2ProfileMap)["Rig A"].Timezone);
    }

    [Fact]
    public async Task AStoredTelescopeTheEquipmentListDoesNotCarry_IsOfferedAndSelected_NotShownAsUnmapped()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": "Retired refractor", "timezone": "", "latitude": null, "longitude": null}}"""),
        };
        var (panel, _) = await CreateAndSettleAsync(new FakeStore(seed), rows: [Row("Rig A")]);

        var row = panel.Rows.Single();
        Assert.Equal("Retired refractor", row.SelectedTelescope?.Value);
        Assert.NotEqual(Phd2ProfilesViewModel.NotMappedLabel, row.SelectedTelescope?.Label);
    }

    // ---- the rename rewrite through the tab's own save path ---------------------------------------

    private static (EquipmentTabViewModel Tab, Phd2ProfilesViewModel Panel, FakeStore Store) BuildTab(
        GeneralSettings seed,
        IReadOnlyDictionary<string, string[]> telescopes,
        params string[] discoveredTelescopes)
    {
        var store = new FakeStore(seed);
        var equipment = new EquipmentSettings
        {
            Telescopes = telescopes.ToDictionary(
                entry => entry.Key,
                entry => new EquipmentItemSettings { Aliases = [.. entry.Value] },
                StringComparer.Ordinal),
        };

        EquipmentTabViewModel tab = null!;
        var panel = new Phd2ProfilesViewModel(
            store.Get,
            () => [Row("Rig A"), Row("Rig B")],
            store.Mutate,
            // Exactly what AppHost hands it, so a delegate narrowed back to the groups alone
            // fails here as well as on the real host.
            () => tab.KnownTelescopes(),
            systemTimezones: () => ["UTC"],
            post: action => action());

        tab = new EquipmentTabViewModel(
            () => equipment,
            document => equipment = document,
            () => [],
            _ => { },
            () => [],
            () => [.. discoveredTelescopes.Select(name => (name, 1))],
            post: action => action(),
            phd2Profiles: panel);

        tab.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));

        // The panel's first load can race the assignment of `tab` above (the same two-phase
        // construction AppHost uses), so its telescope list is re-read once the tab exists.
        panel.Reload();
        panel.PendingLoad?.Wait(TimeSpan.FromSeconds(5));
        return (tab, panel, store);
    }

    // The rename the editor itself performs: BeginRename, RenameText, CommitRename, which raises
    // RenameRequested and lands in GroupingEditorViewModel.OnRenameRequested, which assigns
    // group.Canonical and adds no alias.
    private static void Rename(AliasGroupViewModel group, string newName)
    {
        group.BeginRenameCommand.Execute(null);
        group.RenameText = newName;
        group.CommitRenameCommand.Execute(null);
    }

    private static async Task SaveTabAsync(EquipmentTabViewModel tab, Phd2ProfilesViewModel panel)
    {
        await tab.SaveCommand.ExecuteAsync(null);
        if (tab.PendingSave is { } save)
        {
            await save;
        }

        await Settle(panel);
    }

    [Fact]
    public async Task APlainTelescopeRename_MovesEveryMappedEntryOntoTheNewName_ThroughTheTabsOwnSave()
    {
        // Review P1-1. A plain rename touches no alias, so the saved document holds no trace of
        // "Askar 120"; a rename function built from the alias table alone returns null for it, the
        // map keeps the dead name, the picker falls to "Not mapped" and the correlation stops
        // matching that rig.
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""
                {
                  "Rig A": {"telescope": "Askar 120", "timezone": "UTC", "latitude": 5.0, "longitude": null},
                  "Rig B": {"telescope": "Celestron EdgeHD 8", "timezone": "", "latitude": null, "longitude": null}
                }
                """),
        };
        var (tab, panel, store) = BuildTab(
            seed,
            new Dictionary<string, string[]>
            {
                ["Askar 120"] = [],
                ["Celestron EdgeHD 8"] = [],
            });

        Rename(tab.TelescopesEditor.Groups.Single(group => group.Canonical == "Askar 120"), "Askar FMA180");
        await SaveTabAsync(tab, panel);

        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        Assert.Equal("Askar FMA180", map["Rig A"].Telescope);
        Assert.Equal("UTC", map["Rig A"].Timezone);
        Assert.Equal(5.0, map["Rig A"].Latitude);
        Assert.Equal("Celestron EdgeHD 8", map["Rig B"].Telescope);
        Assert.Equal("Askar FMA180", panel.Rows.Single(row => row.Profile == "Rig A").SelectedTelescope?.Value);
    }

    [Fact]
    public async Task ARenameThatMergesTwoGroups_LandsBothProfilesOnTheSurvivingCanonicalName()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""
                {
                  "Rig A": {"telescope": "Askar 120", "timezone": "UTC", "latitude": null, "longitude": null},
                  "Rig B": {"telescope": "Askar FMA180", "timezone": "", "latitude": null, "longitude": null}
                }
                """),
        };
        var (tab, panel, store) = BuildTab(
            seed,
            new Dictionary<string, string[]>
            {
                ["Askar 120"] = [],
                ["Askar FMA180"] = [],
            });

        // The merge the tab performs: the absorbed group is dropped and its raw name becomes an
        // alias of the survivor (EquipmentTabViewModel.AcceptSuggestion's own two steps).
        var absorbed = tab.TelescopesEditor.Groups.Single(group => group.Canonical == "Askar 120");
        var survivor = tab.TelescopesEditor.Groups.Single(group => group.Canonical == "Askar FMA180");
        tab.TelescopesEditor.RemoveGroup(absorbed);
        survivor.Aliases.Add("Askar 120");
        await SaveTabAsync(tab, panel);

        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        Assert.Equal("Askar FMA180", map["Rig A"].Telescope);
        Assert.Equal("UTC", map["Rig A"].Timezone);
        Assert.Equal("Askar FMA180", map["Rig B"].Telescope);
    }

    [Fact]
    public async Task ADeletedTelescopeGroup_UnmapsTheEntry_AndNeverLeavesADanglingName()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""
                {
                  "Rig A": {"telescope": "Askar 120", "timezone": "UTC", "latitude": null, "longitude": null},
                  "Rig B": {"telescope": "Askar 120", "timezone": "", "latitude": null, "longitude": null}
                }
                """),
        };
        var (tab, panel, store) = BuildTab(seed, new Dictionary<string, string[]> { ["Askar 120"] = [] });

        tab.TelescopesEditor.RemoveGroup(tab.TelescopesEditor.Groups.Single());
        await SaveTabAsync(tab, panel);

        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        // The entry that still carries a zone survives, unmapped; the one carrying nothing else is
        // removed outright, which is the keep-or-remove rule an explicit clear takes.
        Assert.True(map.ContainsKey("Rig A"));
        Assert.Null(map["Rig A"].Telescope);
        Assert.Equal("UTC", map["Rig A"].Timezone);
        Assert.False(map.ContainsKey("Rig B"));
    }

    [Fact]
    public async Task ARenameOfATelescopeNoProfileMapsTo_MovesNoMapAndQueuesNothing()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": "Askar 120", "timezone": "", "latitude": null, "longitude": null}}"""),
        };
        var (tab, panel, store) = BuildTab(
            seed,
            new Dictionary<string, string[]>
            {
                ["Askar 120"] = [],
                ["Celestron EdgeHD 8"] = [],
            });
        var changesBefore = store.GuidingInputsChanges;

        Rename(tab.TelescopesEditor.Groups.Single(group => group.Canonical == "Celestron EdgeHD 8"), "Celestron 8");
        await SaveTabAsync(tab, panel);

        Assert.Equal("Askar 120", Phd2Profiles.Normalize(store.Current.Phd2ProfileMap)["Rig A"].Telescope);
        Assert.Equal(changesBefore, store.GuidingInputsChanges);
        Assert.Null(panel.StatusMessage);
    }

    // ---- the picker's source: groups AND the library's own discovered telescope names ------------

    // Phase 15B real-data finding 1, the blocker. The picker read the grouping editor's canonical
    // names alone, and a library whose reader has never created a telescope alias group has none:
    // every row offered "Not mapped" and nothing else, "Group Selected" is disabled at one selected
    // item, so no profile could ever be mapped and every guiding surface built on that mapping
    // stayed empty for ever. The web reads the distinct telescope values of the images table for
    // this same picker.
    [Fact]
    public void TwoDiscoveredTelescopesAndNoAliasGroup_AreBothOfferedByThePicker()
    {
        var (_, panel, _) = BuildTab(
            new GeneralSettings(),
            new Dictionary<string, string[]>(),
            "AM5n_OAG_ASI174M",
            "ASI220-AM5-30F5");

        var offered = panel.Rows[0].TelescopeOptions.Select(option => option.Value).ToList();

        Assert.Equal("", offered[0]);
        Assert.Equal(Phd2ProfilesViewModel.NotMappedLabel, panel.Rows[0].TelescopeOptions[0].Label);
        Assert.Contains("AM5n_OAG_ASI174M", offered);
        Assert.Contains("ASI220-AM5-30F5", offered);
    }

    // One scope, one entry. The ungrouped list is exactly the discovered names no group covers, so
    // the union cannot offer a member spelling beside the canonical name it folds to.
    [Fact]
    public void AGroupedTelescope_IsOfferedOnceByItsCanonicalName_AndItsMemberSpellingsAreNot()
    {
        var (_, panel, _) = BuildTab(
            new GeneralSettings(),
            new Dictionary<string, string[]> { ["Askar FMA180"] = ["Askar 120"] },
            "Askar 120",
            "Askar FMA180",
            "SVBony 80ED");

        var offered = panel.Rows[0].TelescopeOptions.Select(option => option.Value).ToList();

        Assert.Equal(["", "Askar FMA180", "SVBony 80ED"], offered);
    }

    // The storing half of the real-data blocker: the raw discovered name is what goes into the map,
    // and AliasMap.TelescopeMatchSet folds it with CanonicalTelescope(raw) ?? raw, so it matches
    // the frames spelled that way. The query half is Phd2NightQueryTests and GuidingStatsQueryTests.
    [Fact]
    public async Task MappingARowToAnUngroupedDiscoveredName_StoresThatRawName()
    {
        var (_, panel, store) = BuildTab(
            new GeneralSettings(), new Dictionary<string, string[]>(), "SVBony 80ED");

        var row = panel.Rows.Single(r => r.Profile == "Rig A");
        row.SelectedTelescope = row.TelescopeOptions.Single(option => option.Value == "SVBony 80ED");
        await Settle(panel);

        Assert.Equal("SVBony 80ED", Phd2Profiles.Normalize(store.Current.Phd2ProfileMap)["Rig A"].Telescope);
    }

    // The rewrite unmaps every name it does not recognise, so an equipment save that leaves an
    // ungrouped mapped telescope alone must still recognise it. Tested against the canonical
    // dictionary alone, this mapping was wiped by the next save of any unrelated equipment edit.
    [Fact]
    public async Task AnUngroupedMappedTelescope_SurvivesAnUnrelatedEquipmentSave()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": "SVBony 80ED", "timezone": "UTC", "latitude": null, "longitude": null}}"""),
        };
        var (tab, panel, store) = BuildTab(
            seed, new Dictionary<string, string[]>(), "SVBony 80ED", "Askar 120");

        tab.TelescopesEditor.AddGroup(new AliasGroupViewModel("Askar FMA180", color: null, ["Askar 120"]));
        await SaveTabAsync(tab, panel);

        Assert.Equal("SVBony 80ED", Phd2Profiles.Normalize(store.Current.Phd2ProfileMap)["Rig A"].Telescope);
    }

    // Phase 15A's rename rule, kept true for the shape this fix adds: a profile mapped to a raw
    // name the reader groups LATER must follow the name onto its new canonical rather than strand
    // on a spelling the picker no longer offers.
    [Fact]
    public async Task GroupingAnUngroupedMappedTelescopeAfterwards_MovesTheMappingOntoTheCanonicalName()
    {
        var seed = new GeneralSettings
        {
            Phd2ProfileMap = Map("""{"Rig A": {"telescope": "SVBony 80ED", "timezone": "UTC", "latitude": null, "longitude": null}}"""),
        };
        var (tab, panel, store) = BuildTab(
            seed, new Dictionary<string, string[]>(), "SVBony 80ED", "SVBony SV503 80mm");

        tab.TelescopesEditor.AddGroup(
            new AliasGroupViewModel("SVBony SV503 80mm", color: null, ["SVBony 80ED"]));
        await SaveTabAsync(tab, panel);

        var map = Phd2Profiles.Normalize(store.Current.Phd2ProfileMap);
        Assert.Equal("SVBony SV503 80mm", map["Rig A"].Telescope);
        Assert.Equal("UTC", map["Rig A"].Timezone);
        Assert.Equal(
            "SVBony SV503 80mm",
            panel.Rows.Single(row => row.Profile == "Rig A").SelectedTelescope?.Value);
    }
}
