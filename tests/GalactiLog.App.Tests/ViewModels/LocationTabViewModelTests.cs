using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.7's Location tab and design-spec 18.3: the tab takes delegates, so every case
// here runs with lambdas and no database. The one case that asserts the store is still the
// enforcement point uses a real migrated database and a real SettingsStore, because that is the
// claim it is making.
public class LocationTabViewModelTests
{
    // The general document, in memory, with the two failure shapes the tab has to survive.
    private sealed class FakeStore
    {
        public FakeStore(GeneralSettings? seed = null) => Current = seed ?? new GeneralSettings();

        public GeneralSettings Current { get; private set; }

        public int Saves { get; private set; }

        public bool ReadThrows { get; set; }

        public string? RefuseWith { get; set; }

        public GeneralSettings Get()
            => ReadThrows ? throw new InvalidOperationException("the database is gone") : Current;

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate)
        {
            var next = mutate(Current);
            if (RefuseWith is { } message)
            {
                throw new SettingsValidationException(message);
            }

            Current = next;
            Saves++;
            return next;
        }
    }

    // The three zones every case uses, so no test depends on which zone database the machine has.
    private static readonly string[] Zones = ["America/New_York", "Europe/London", "UTC"];

    private static async Task<(LocationTabViewModel Tab, FakeStore Store)> CreateAsync(GeneralSettings? seed = null)
    {
        var store = new FakeStore(seed);
        var tab = new LocationTabViewModel(
            store.Get,
            store.Mutate,
            systemTimezones: () => Zones,
            post: action => action());

        await Settle(tab).ConfigureAwait(false);
        return (tab, store);
    }

    // The tab's read and its write chain are both off the calling thread; a test awaits them
    // instead of blocking (TRACKING.md section 2 item 8).
    private static async Task Settle(LocationTabViewModel tab)
    {
        if (tab.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        await tab.PendingWrite.ConfigureAwait(false);
    }

    [Fact]
    public async Task ObserverName_RoundTrips()
    {
        var (tab, store) = await CreateAsync();

        tab.ObserverName = "Backyard";
        await Settle(tab);

        Assert.Equal("Backyard", store.Current.ObserverName);
    }

    [Fact]
    public async Task ObserverName_BlankClearsToNull()
    {
        var (tab, store) = await CreateAsync(new GeneralSettings { ObserverName = "Backyard" });

        tab.ObserverName = "   ";
        await Settle(tab);

        Assert.Null(store.Current.ObserverName);
    }

    [Fact]
    public async Task ObserverLatitude_RoundTrips()
    {
        var (tab, store) = await CreateAsync();

        tab.LatitudeText = "35.1983";
        await Settle(tab);

        Assert.Equal(35.1983, store.Current.ObserverLatitude);
        Assert.Null(tab.LatitudeError);
    }

    [Theory]
    [InlineData("-90.1")]
    [InlineData("90.1")]
    public async Task ObserverLatitude_OutOfRange_IsAnInlineError_AndDoesNotSave(string typed)
    {
        var (tab, store) = await CreateAsync();

        tab.LatitudeText = typed;
        await Settle(tab);

        // The web's message, verbatim.
        Assert.Equal("Must be between -90 and 90", tab.LatitudeError);
        Assert.Equal(0, store.Saves);
        Assert.Null(store.Current.ObserverLatitude);
    }

    [Theory]
    [InlineData("-90", -90d)]
    [InlineData("90", 90d)]
    public async Task ObserverLatitude_AtTheBounds_IsAccepted(string typed, double expected)
    {
        var (tab, store) = await CreateAsync();

        tab.LatitudeText = typed;
        await Settle(tab);

        Assert.Null(tab.LatitudeError);
        Assert.Equal(expected, store.Current.ObserverLatitude);
    }

    [Fact]
    public async Task ObserverLongitude_RoundTrips()
    {
        var (tab, store) = await CreateAsync();

        tab.LongitudeText = "-111.6513";
        await Settle(tab);

        Assert.Equal(-111.6513, store.Current.ObserverLongitude);
    }

    [Theory]
    [InlineData("-180.1")]
    [InlineData("180.1")]
    public async Task ObserverLongitude_OutOfRange_IsAnInlineError_AndDoesNotSave(string typed)
    {
        var (tab, store) = await CreateAsync();

        tab.LongitudeText = typed;
        await Settle(tab);

        Assert.Equal("Must be between -180 and 180", tab.LongitudeError);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData("-180", -180d)]
    [InlineData("180", 180d)]
    public async Task ObserverLongitude_AtTheBounds_IsAccepted(string typed, double expected)
    {
        var (tab, store) = await CreateAsync();

        tab.LongitudeText = typed;
        await Settle(tab);

        Assert.Null(tab.LongitudeError);
        Assert.Equal(expected, store.Current.ObserverLongitude);
    }

    [Fact]
    public async Task ObserverCoordinates_BlankClearsToNull()
    {
        var (tab, store) = await CreateAsync(new GeneralSettings
        {
            ObserverLatitude = 35.2,
            ObserverLongitude = -111.7,
        });

        tab.LatitudeText = "";
        await Settle(tab);
        tab.LongitudeText = "";
        await Settle(tab);

        Assert.Null(store.Current.ObserverLatitude);
        Assert.Null(store.Current.ObserverLongitude);

        // Spec 8.3's UTC-midnight fallback is now in force, and the tab says so.
        Assert.True(tab.LongitudeIsBlank);
    }

    [Fact]
    public async Task ObserverTimezone_RoundTrips()
    {
        var (tab, store) = await CreateAsync();

        tab.SelectedObserverTimezone = tab.ObserverTimezones.First(option => option.Id == "Europe/London");
        await Settle(tab);

        Assert.Equal("Europe/London", store.Current.ObserverTimezone);
    }

    [Fact]
    public async Task ObserverTimezone_Empty_IsAccepted_AndMeansNotConfigured()
    {
        var (tab, store) = await CreateAsync(new GeneralSettings { ObserverTimezone = "Europe/London" });

        var notConfigured = tab.ObserverTimezones.First(option => option.Id.Length == 0);
        Assert.Equal("Select a timezone", notConfigured.Label);

        tab.SelectedObserverTimezone = notConfigured;
        await Settle(tab);

        Assert.Equal("", store.Current.ObserverTimezone);
        Assert.Null(tab.TimezoneError);

        // Spec 5.8.1: the empty string is what "not configured" is stored as, and the store
        // accepts it.
        Assert.Null(LocationTabViewModel.ValidateTimezone(""));
    }

    [Fact]
    public void ObserverTimezone_UnknownId_IsRejectedWithTheWebMessage()
    {
        // The backend's own message, from schemas/settings.py::_validated_zone_name.
        Assert.Equal(
            "'Mars/Olympus_Mons' is not a known IANA time zone",
            LocationTabViewModel.ValidateTimezone("Mars/Olympus_Mons"));
    }

    // Unlike every other case in this file, the next two run the production resolver
    // (TimeZoneInfo.FindSystemTimeZoneById, hard-wired inside RebuildTimezones: neither
    // LocationTabViewModel's constructor nor ObserverLocationStepViewModel's exposes a resolver
    // seam, only the zone id list), so they depend on this machine resolving these three ids at
    // all, which is why they check only the GMT offset prefix rather than the full label
    // (review task8-review.md P3 finding: the fixture's stated machine-independence invariant,
    // line 46 above, does not extend to a case that reaches the real resolver). Neither assumes
    // an order between Europe/London and UTC, which share an offset: their relative order is
    // this machine's display names to decide, and TimezoneOptionsTests pins the resolver and
    // asserts the exact label text, displayed name included, deterministically.

    [Fact]
    public async Task ObserverTimezones_AreBuiltByTheSharedBuilder_OffsetOrderedWithGmtPrefixes()
    {
        // Timezone left blank so no "Same as display timezone" entry sits between the special
        // first entry and the zone entries the builder produces (user ruling U4).
        var (tab, _) = await CreateAsync(new GeneralSettings { Timezone = "" });

        // The special first entry keeps its place and its wording, ahead of every zone entry.
        Assert.Equal("", tab.ObserverTimezones[0].Id);
        Assert.Equal(LocationTabViewModel.NotConfiguredLabel, tab.ObserverTimezones[0].Label);

        var zoneOptions = tab.ObserverTimezones.Skip(1).ToList();
        Assert.Equal(3, zoneOptions.Count);

        // America/New_York's offset (-05:00) is the only one of the three that is not shared, so
        // it must lead.
        Assert.Equal("America/New_York", zoneOptions[0].Id);
        Assert.StartsWith(TimezoneLabels.GmtPrefix("America/New_York"), zoneOptions[0].Label);

        Assert.Equal(
            new HashSet<string> { "Europe/London", "UTC" },
            zoneOptions.Skip(1).Select(option => option.Id).ToHashSet());
        Assert.All(
            zoneOptions.Skip(1),
            option => Assert.StartsWith(TimezoneLabels.GmtPrefix(option.Id), option.Label));
    }

    [Fact]
    public async Task DisplayTimezones_AreBuiltByTheSharedBuilder_OffsetOrderedWithGmtPrefixes()
    {
        var (tab, _) = await CreateAsync(new GeneralSettings { Timezone = "" });

        // Polish wave 3 ruling 1: the follow-the-observer entry leads, then the builder's zones.
        Assert.Equal(4, tab.DisplayTimezones.Count);
        Assert.Equal("", tab.DisplayTimezones[0].Id);
        Assert.Equal("America/New_York", tab.DisplayTimezones[1].Id);
        Assert.StartsWith(TimezoneLabels.GmtPrefix("America/New_York"), tab.DisplayTimezones[1].Label);

        Assert.Equal(
            new HashSet<string> { "Europe/London", "UTC" },
            tab.DisplayTimezones.Skip(2).Select(option => option.Id).ToHashSet());
        Assert.All(
            tab.DisplayTimezones.Skip(2),
            option => Assert.StartsWith(TimezoneLabels.GmtPrefix(option.Id), option.Label));
    }

    // Polish wave 3 ruling 1: the display timezone follows the observer timezone until picked.

    [Fact]
    public async Task DisplayTimezones_FollowTheObserver_IsFirst_AndSelectedForAnEmptyStore()
    {
        // Failure looks like a zone id in the first slot, or a stored "" selecting a zone.
        var (tab, _) = await CreateAsync(new GeneralSettings { Timezone = "", ObserverTimezone = "Europe/London" });

        Assert.Equal("", tab.DisplayTimezones[0].Id);
        Assert.Equal(LocationTabViewModel.FollowObserverLabel, tab.DisplayTimezones[0].Label);
        Assert.Same(tab.DisplayTimezones[0], tab.SelectedDisplayTimezone);
    }

    [Fact]
    public async Task DisplayTimezones_FollowTheObserver_SaysWhenTheObserverZoneIsBlank()
    {
        // Failure looks like the plain follow label, which would promise a zone nobody has set.
        var (tab, _) = await CreateAsync(new GeneralSettings { Timezone = "", ObserverTimezone = "" });

        Assert.Equal(LocationTabViewModel.FollowObserverUnsetLabel, tab.DisplayTimezones[0].Label);
    }

    [Fact]
    public async Task DisplayTimezone_PickingFollowTheObserver_SavesTheEmptyValue()
    {
        // Failure looks like the store keeping "UTC", or the observer zone being rewritten.
        var (tab, store) = await CreateAsync(new GeneralSettings { Timezone = "UTC", ObserverTimezone = "Europe/London" });
        Assert.Equal("UTC", tab.SelectedDisplayTimezone?.Id);

        tab.SelectedDisplayTimezone = tab.DisplayTimezones.First(option => option.Id == "");
        await Settle(tab);

        Assert.Equal("", store.Current.Timezone);
        Assert.Equal("Europe/London", store.Current.ObserverTimezone);
    }

    [Fact]
    public async Task ObserverTimezone_StoredButUnlisted_IsOfferedRatherThanDropped()
    {
        var (tab, _) = await CreateAsync(new GeneralSettings { ObserverTimezone = "Pacific/Chatham" });

        var stored = tab.ObserverTimezones.Single(option => option.Id == "Pacific/Chatham");
        Assert.Same(stored, tab.SelectedObserverTimezone);
    }

    [Fact]
    public async Task UseImagingNight_RoundTrips()
    {
        var (tab, store) = await CreateAsync();
        Assert.True(tab.UseImagingNight);

        tab.UseImagingNight = false;
        await Settle(tab);

        Assert.False(store.Current.UseImagingNight);
    }

    [Fact]
    public async Task DisplayTimezone_AndObserverTimezone_AreSeparateKeys()
    {
        var (tab, store) = await CreateAsync(new GeneralSettings
        {
            Timezone = "UTC",
            ObserverTimezone = "",
        });

        tab.SelectedObserverTimezone = tab.ObserverTimezones.First(option => option.Id == "America/New_York");
        await Settle(tab);
        tab.SelectedDisplayTimezone = tab.DisplayTimezones.First(option => option.Id == "Europe/London");
        await Settle(tab);

        // Spec 5.8.1: observer_timezone feeds session derivation, timezone is display formatting
        // only. Writing one must never write the other.
        Assert.Equal("America/New_York", store.Current.ObserverTimezone);
        Assert.Equal("Europe/London", store.Current.Timezone);
    }

    [Fact]
    public async Task Use24HTime_RoundTrips()
    {
        var (tab, store) = await CreateAsync();
        Assert.False(tab.Use24HTime);

        tab.Use24HTime = true;
        await Settle(tab);

        Assert.True(store.Current.Use24HTime);
    }

    [Fact]
    public async Task AFailedSave_RollsTheFieldBack()
    {
        var (tab, store) = await CreateAsync(new GeneralSettings { ObserverName = "Backyard" });
        store.RefuseWith = "general.observer_name is refused by this test";

        tab.ObserverName = "Roof";
        await Settle(tab);

        Assert.Equal("Backyard", tab.ObserverName);
        Assert.Equal("general.observer_name is refused by this test", tab.ErrorMessage);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task AFailedRead_LeavesNoEditor_AndSavesNothing()
    {
        var store = new FakeStore { ReadThrows = true };
        var tab = new LocationTabViewModel(store.Get, store.Mutate, () => Zones, post: action => action());
        await Settle(tab);

        // Spec 12.10, and the Task 5 review's finding I1: a live editor over a document that
        // failed to read would write its defaults over whatever the user actually had.
        Assert.True(tab.LoadFailed);
        Assert.False(tab.IsReady);

        tab.ObserverName = "Roof";
        await Settle(tab);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task AGeneralSaveElsewhere_ReloadsTheFields()
    {
        var store = new FakeStore();
        EventHandler<GeneralSettings>? subscriber = null;
        var tab = new LocationTabViewModel(
            store.Get,
            store.Mutate,
            () => Zones,
            post: action => action(),
            subscribeGeneralChanged: handler => subscriber += handler,
            unsubscribeGeneralChanged: handler => subscriber -= handler);
        await Settle(tab);

        // Another writer of the same document: the setup wizard, or another tab.
        subscriber!.Invoke(this, new GeneralSettings { ObserverName = "Observatory", ObserverLatitude = 12.5 });

        Assert.Equal("Observatory", tab.ObserverName);
        Assert.Equal("12.5", tab.LatitudeText);
        Assert.Equal(0, store.Saves);

        tab.Dispose();
        Assert.Null(subscriber);
    }

    [Fact]
    public void ValidateLatitudeAndLongitude_AreTheOneDefinition_AndTheWizardCallsThem()
    {
        // The collision map's designated-owner row: Task 9's wizard calls these rather than
        // growing a latitude rule the Settings tab would then disagree with.
        Assert.Null(LocationTabViewModel.ValidateLatitude("0", out var zero));
        Assert.Equal(0d, zero);

        Assert.Null(LocationTabViewModel.ValidateLatitude("", out var blank));
        Assert.Null(blank);

        Assert.Equal("Must be between -90 and 90", LocationTabViewModel.ValidateLatitude("91", out _));
        Assert.Equal("Must be between -180 and 180", LocationTabViewModel.ValidateLongitude("181", out _));
        Assert.Equal(LocationTabViewModel.NotANumberMessage, LocationTabViewModel.ValidateLatitude("north", out _));
    }

    [Fact]
    public void ValidationIsStillEnforcedInSettingsStore()
    {
        // Design-lessons rule 2: the inline messages above are a usability layer, and this is the
        // enforcement point behind them. A value the tab would refuse is also refused by the
        // store, so a write that reaches it another way still cannot land.
        using var database = new TempDatabase("galactilog-location-tab");
        var store = new SettingsStore(new SettingsRepository(database.ConnectionString));

        Assert.Throws<SettingsValidationException>(
            () => store.SaveGeneral(store.GetGeneral() with { ObserverLatitude = 91d }));
        Assert.Throws<SettingsValidationException>(
            () => store.SaveGeneral(store.GetGeneral() with { ObserverLongitude = -181d }));

        Assert.Null(store.GetGeneral().ObserverLatitude);
        Assert.Null(store.GetGeneral().ObserverLongitude);
    }
}
