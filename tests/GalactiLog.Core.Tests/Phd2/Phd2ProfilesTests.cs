using System.Text.Json;
using GalactiLog.Core.Phd2;
using Xunit;

namespace GalactiLog.Core.Tests.Phd2;

// Spec 7.6, "Timezone resolution". One case per row of the normalisation table and of the
// resolution order table in task3.md section 4.6, named after the rule it proves.
public class Phd2ProfilesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // A zone loader that answers without touching the host's zone database, so the resolution
    // order is pinned by the order alone.
    private static TimeZoneInfo StubZones(string id) => id switch
    {
        "Zone/Profile" or "Zone/Global" => TimeZoneInfo.Utc,
        "Zone/Minus5" => Minus5,
        "Zone/Dst" => Minus5WithDst,
        _ => throw new TimeZoneNotFoundException(id),
    };

    // Spec 8.3 step 3's loader shape: the memo Phd2Profiles.ZoneLoader builds, over the stub above
    // rather than over the platform. Built fresh per access, so no case inherits another's memo.
    private static Func<string, TimeZoneInfo?> StubLoad => Phd2Profiles.ZoneLoader(StubZones);

    // The instant step 3 takes its offset at, for the cases where the date of it does not matter.
    private static readonly DateTime Instant = new(2026, 3, 2, 7, 0, 0, DateTimeKind.Utc);

    // Fixed UTC-05:00, five hours west of Greenwich, which is -75 degrees at 15 degrees per hour.
    private static readonly TimeZoneInfo Minus5 = TimeZoneInfo.CreateCustomTimeZone(
        "Stub/Minus5", TimeSpan.FromHours(-5), "Stub/Minus5", "Stub/Minus5");

    // The same zone with a northern-summer saving, built here rather than read from the host so
    // the two offsets a case asserts do not depend on which zone database the machine ships.
    private static readonly TimeZoneInfo Minus5WithDst = TimeZoneInfo.CreateCustomTimeZone(
        "Stub/Minus5Dst", TimeSpan.FromHours(-5), "Stub/Minus5Dst", "Stub/Minus5Dst", "Stub/Minus4Dst",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        ]);

    // ---- Normalisation table ----

    [Fact]
    public void ANullMap_IsEmpty() => Assert.Empty(Phd2Profiles.Normalize(null));

    [Theory]
    [InlineData("5")]
    [InlineData("\"nonsense\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    public void ANonObjectDocument_IsEmpty(string stored)
        => Assert.Empty(Phd2Profiles.Normalize(Json(stored)));

    [Fact]
    public void ALegacyStringMap_BecomesCanonicalEntries()
    {
        // A failure here is an empty map, which silently unmaps every profile on every install
        // that predates per-rig timezones.
        var map = Phd2Profiles.Normalize(Json("""{"Rig A": "Askar 120"}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.Equal("Askar 120", entry.Telescope);
        Assert.Equal("", entry.Timezone);
        Assert.Null(entry.Latitude);
        Assert.Null(entry.Longitude);
    }

    [Fact]
    public void AFullObjectEntry_KeepsEveryField()
    {
        var map = Phd2Profiles.Normalize(Json(
            """{"Rig A": {"telescope": "Askar 120", "timezone": "America/Chicago", "latitude": 30.27, "longitude": -97.74}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.Equal("Askar 120", entry.Telescope);
        Assert.Equal("America/Chicago", entry.Timezone);
        Assert.Equal(30.27, entry.Latitude!.Value, 6);
        Assert.Equal(-97.74, entry.Longitude!.Value, 6);
    }

    [Fact]
    public void AnEntryMissingKeys_TakesTheCanonicalDefaults()
    {
        var map = Phd2Profiles.Normalize(Json("""{"Rig A": {"timezone": "America/Chicago"}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.Null(entry.Telescope);
        Assert.Equal("America/Chicago", entry.Timezone);
        Assert.Null(entry.Latitude);
        Assert.Null(entry.Longitude);
    }

    [Fact]
    public void ATelescopeName_IsTrimmed()
    {
        var map = Phd2Profiles.Normalize(Json("""{"Rig A": {"telescope": "  Askar 120  "}}"""));

        Assert.Equal("Askar 120", Assert.Contains("Rig A", map).Telescope);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void AnEmptyTelescopeName_MeansNotMapped(string stored)
    {
        // An empty name means "not mapped", never "mapped to a telescope with no name": the entry
        // has to survive an unmapping in order to keep carrying its zone and site.
        var map = Phd2Profiles.Normalize(Json($$$"""{"Rig A": {"telescope": {{{stored}}}}}"""));

        Assert.Null(Assert.Contains("Rig A", map).Telescope);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ANonStringTelescope_MeansNotMapped(string stored)
    {
        var map = Phd2Profiles.Normalize(Json($$$"""{"Rig A": {"telescope": {{{stored}}}}}"""));

        Assert.Null(Assert.Contains("Rig A", map).Telescope);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ANonStringTimezone_ReadsAsInherit(string stored)
    {
        var map = Phd2Profiles.Normalize(Json($$$"""{"Rig A": {"timezone": {{{stored}}}}}"""));

        Assert.Equal("", Assert.Contains("Rig A", map).Timezone);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    public void AJunkEntryValue_DropsTheEntry_AndKeepsTheOthers(string junk)
    {
        // A failure is the whole map dropped because one entry was junk.
        var map = Phd2Profiles.Normalize(Json(
            $$$"""{"Rig A": {{{junk}}}, "Rig B": {"telescope": "Askar 120"}}"""));

        Assert.DoesNotContain("Rig A", map);
        Assert.Equal("Askar 120", Assert.Contains("Rig B", map).Telescope);
    }

    [Fact]
    public void AUnicodeProfileName_IsPreserved()
    {
        var map = Phd2Profiles.Normalize(Json("""{"Sternwarte Überlingen": "Askar 120"}"""));

        Assert.Equal("Askar 120", Assert.Contains("Sternwarte Überlingen", map).Telescope);
    }

    [Theory]
    [InlineData("30.27", "-97.74", 30.27, -97.74)]
    [InlineData("\"30.27\"", "\"-97.74\"", 30.27, -97.74)]
    public void ANumericCoordinate_IsRead(string latitude, string longitude, double expectedLatitude, double expectedLongitude)
    {
        var map = Phd2Profiles.Normalize(Json(
            $$$"""{"Rig A": {"latitude": {{{latitude}}}, "longitude": {{{longitude}}}}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.Equal(expectedLatitude, entry.Latitude!.Value, 6);
        Assert.Equal(expectedLongitude, entry.Longitude!.Value, 6);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("\"0\"")]
    public void AZeroLatitudeAndAZeroLongitude_SurviveNormalization(string zero)
    {
        // The single most consequential defect in this module: null here moves a rig on the prime
        // meridian or the equator to the user's own site and files its nights under the wrong
        // date. Pinned from both directions, here and in HasOwnLongitude_IsTrueForAStoredZero.
        var map = Phd2Profiles.Normalize(Json(
            $$$"""{"Rig A": {"latitude": {{{zero}}}, "longitude": {{{zero}}}}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.NotNull(entry.Latitude);
        Assert.NotNull(entry.Longitude);
        Assert.Equal(0.0, entry.Latitude!.Value, 6);
        Assert.Equal(0.0, entry.Longitude!.Value, 6);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"north\"")]
    public void AnEmptyOrUnparseableCoordinateString_MeansInherit(string stored)
    {
        var map = Phd2Profiles.Normalize(Json(
            $$$"""{"Rig A": {"latitude": {{{stored}}}, "longitude": {{{stored}}}, "timezone": "x"}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.Null(entry.Latitude);
        Assert.Null(entry.Longitude);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void ABooleanCoordinate_IsNotACoordinate(string stored)
    {
        // A failure is latitude 1.0 from a stored true.
        var map = Phd2Profiles.Normalize(Json(
            $$$"""{"Rig A": {"latitude": {{{stored}}}, "longitude": {{{stored}}}, "timezone": "x"}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.Null(entry.Latitude);
        Assert.Null(entry.Longitude);
    }

    // The expected pairs are written out rather than recomputed from the bounds in the test body,
    // which would be a second implementation of the rule under test.
    [Theory]
    [InlineData("500", "-97.74", false, true)]
    [InlineData("-90.5", "0", false, true)]
    [InlineData("30.27", "181", true, false)]
    [InlineData("30.27", "-180.5", true, false)]
    public void AnOutOfRangeCoordinate_MeansInherit(
        string latitude, string longitude, bool keepsLatitude, bool keepsLongitude)
    {
        // A latitude of 500 propagating into the sidereal check produces a confident wrong verdict
        // instead of no verdict. Each row keeps the in-range axis, so the rejection is per
        // coordinate and does not take the whole entry with it.
        var map = Phd2Profiles.Normalize(Json(
            $$$"""{"Rig A": {"latitude": {{{latitude}}}, "longitude": {{{longitude}}}}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.Equal(keepsLatitude, entry.Latitude is not null);
        Assert.Equal(keepsLongitude, entry.Longitude is not null);
    }

    [Theory]
    [InlineData("-90", "-180")]
    [InlineData("90", "180")]
    public void TheRangeBounds_AreInclusive(string latitude, string longitude)
    {
        // A failure drops two legal sites.
        var map = Phd2Profiles.Normalize(Json(
            $$$"""{"Rig A": {"latitude": {{{latitude}}}, "longitude": {{{longitude}}}}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.NotNull(entry.Latitude);
        Assert.NotNull(entry.Longitude);
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    [InlineData("\"1e400\"")]
    public void ANonFiniteCoordinateString_MeansInherit(string stored)
    {
        var map = Phd2Profiles.Normalize(Json(
            $$$"""{"Rig A": {"latitude": {{{stored}}}, "longitude": {{{stored}}}, "timezone": "x"}}"""));

        var entry = Assert.Contains("Rig A", map);
        Assert.Null(entry.Latitude);
        Assert.Null(entry.Longitude);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        // A second pass that changes something makes every save report a change.
        var raw = Json(
            """{"Rig A": "Askar 120", "Rig B": {"telescope": "  SVBony 80ED  ", "timezone": "America/Chicago", "longitude": "0"}}""");

        var once = Phd2Profiles.Normalize(raw);
        var twice = Phd2Profiles.Normalize(Phd2Profiles.ToJson(once));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Normalize_ReturnsAFreshMapOnEveryCall()
    {
        // Renamed from Normalize_DoesNotMutateItsArgument, which could not fail: the argument is a
        // JsonElement, an immutable reader over a JsonDocument buffer with no mutating API at all,
        // so "the argument is unchanged" was true by construction whatever the method did. What
        // can fail is Normalize handing out a shared instance, which would let one caller's edit
        // reach another caller's map.
        var raw = Json("""{"Rig A": "Askar 120"}""");

        var first = Phd2Profiles.Normalize(raw);
        Assert.NotSame(first, Phd2Profiles.Normalize(raw));

        ((IDictionary<string, Phd2ProfileEntry>)first).Remove("Rig A");

        Assert.Contains("Rig A", Phd2Profiles.Normalize(raw));
    }

    [Fact]
    public void TelescopeMap_OmitsUnmappedEntries()
    {
        // A null value present under the key changes what a TryGetValue caller sees.
        var map = Phd2Profiles.TelescopeMap(Json(
            """{"Rig A": {"telescope": "Askar 120"}, "Rig B": {"timezone": "America/Chicago"}}"""));

        Assert.Equal("Askar 120", Assert.Contains("Rig A", map));
        Assert.DoesNotContain("Rig B", map);
    }

    // ---- EffectiveTelescope, the one rule for which rig a stored session describes ----
    //
    // The live map is the sole authority (phase-review.md F3). The member used to fall back to the
    // stored phd2_sessions.telescope column for a profile the map did not carry, which made a
    // mapping take effect and an unmapping never take effect: clearing a telescope on the
    // Equipment tab drops the entry from the map, the fallback fired, and the removed rig spoke
    // again forever.

    [Theory]
    // The map carries the profile: its telescope wins, whatever the ingest once stored.
    [InlineData("Rig A", "Askar 120")]
    // The map carries the profile with a null telescope, which is an entry kept for its zone or
    // its site after an unmapping: still no rig.
    [InlineData("Rig B", null)]
    // The map does not carry the profile at all. This is the unmap case and the one that changed.
    [InlineData("Rig Z", null)]
    // A section whose header named no equipment profile, and the ASIAIR's empty spelling of it.
    [InlineData("", null)]
    [InlineData(null, null)]
    public void EffectiveTelescope_AnswersTheLiveMapAndNothingElse(string? profile, string? expected)
    {
        var map = Phd2Profiles.Normalize(Json(
            """{"Rig A": {"telescope": "Askar 120"}, "Rig B": {"timezone": "America/Chicago"}}"""));

        Assert.Equal(expected, Phd2Profiles.EffectiveTelescope(profile, map));
    }

    [Fact]
    public void EffectiveTelescope_AgainstAnEmptyMap_ResolvesEveryProfileToNoRig()
    {
        // Red against the fallback: with the stored column restored as a second argument this
        // answered whatever the ingest wrote, so a library whose profiles a reader had unmapped
        // went on grouping, labelling and correlating every session under the removed rig.
        var empty = Phd2Profiles.Normalize(null);

        Assert.Null(Phd2Profiles.EffectiveTelescope("Rig A", empty));
        Assert.Null(Phd2Profiles.EffectiveTelescope("Rig Z", empty));
    }

    [Fact]
    public void EffectiveTelescope_TakesNoStoredTelescopeArgumentAtAll()
    {
        // The structural half of F3, and the reason the parameter went rather than being passed
        // null at every call site: a signature that still accepted the stored column would let a
        // fifth reader added later re-acquire the defect with an argument that looks helpful.
        var parameters = typeof(Phd2Profiles)
            .GetMethod(nameof(Phd2Profiles.EffectiveTelescope))!
            .GetParameters();

        Assert.Equal(["equipmentProfile", "profileMap"], parameters.Select(p => p.Name));
    }

    // ---- Timezone resolution order table ----

    [Fact]
    public void ALoadableProfileZone_Wins()
    {
        var raw = Json("""{"Rig A": {"timezone": "Zone/Profile"}}""");

        Assert.Equal(("Zone/Profile", Phd2ResolutionSource.Profile),
            Phd2Profiles.ResolveTimezone(raw, "Rig A", "Zone/Global", StubZones));
    }

    [Fact]
    public void AnUnloadableProfileZone_DegradesToTheGlobalAndReportsGlobal()
    {
        // Source "profile" with an unusable zone tells the user the wrong thing about where the
        // number came from.
        var raw = Json("""{"Rig A": {"timezone": "Zone/Nonsense"}}""");

        Assert.Equal(("Zone/Global", Phd2ResolutionSource.Global),
            Phd2Profiles.ResolveTimezone(raw, "Rig A", "Zone/Global", StubZones));
    }

    [Fact]
    public void AnUnloadableProfileZone_WithNoUsableGlobal_IsUnset()
    {
        var raw = Json("""{"Rig A": {"timezone": "Zone/Nonsense"}}""");

        Assert.Equal(("", Phd2ResolutionSource.Unset),
            Phd2Profiles.ResolveTimezone(raw, "Rig A", "", StubZones));
    }

    [Fact]
    public void AnEmptyProfileZone_TakesTheGlobal()
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        Assert.Equal(("Zone/Global", Phd2ResolutionSource.Global),
            Phd2Profiles.ResolveTimezone(raw, "Rig A", "Zone/Global", StubZones));
    }

    [Fact]
    public void AnEmptyProfileZone_WithNoUsableGlobal_IsUnset()
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        Assert.Equal(("", Phd2ResolutionSource.Unset),
            Phd2Profiles.ResolveTimezone(raw, "Rig A", "", StubZones));
    }

    [Theory]
    [InlineData("Rig Z")]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnknownOrAbsentProfile_TakesTheGlobal(string? profile)
    {
        // A section whose header names no equipment profile resolves through the global value and
        // the unset answer only. An ASIAIR writes the profile line with nothing but a trailing
        // space, so null and "" have to answer identically.
        var raw = Json("""{"Rig A": {"timezone": "Zone/Profile"}}""");

        Assert.Equal(("Zone/Global", Phd2ResolutionSource.Global),
            Phd2Profiles.ResolveTimezone(raw, profile, "Zone/Global", StubZones));
    }

    [Theory]
    [InlineData("Rig Z")]
    [InlineData(null)]
    public void AnUnknownOrAbsentProfile_WithNoUsableGlobal_IsUnset(string? profile)
    {
        var raw = Json("""{"Rig A": {"timezone": "Zone/Profile"}}""");

        Assert.Equal(("", Phd2ResolutionSource.Unset),
            Phd2Profiles.ResolveTimezone(raw, profile, "", StubZones));
    }

    [Fact]
    public void ANullProfileAndAnEmptyProfile_ResolveIdentically()
    {
        // Task 2 confirmed Phd2Header.EquipmentProfile is null for every ASIAIR section, never the
        // empty string, because the header line is trimmed before matching. Both forms exist in
        // the wild all the same and PHD2 names no profile either way, so the lookup keys on
        // profile ?? "" and the two must never diverge. A divergence would resolve one ASIAIR log
        // to a zone and the next to nothing.
        var raw = Json("""{"Rig A": {"timezone": "Zone/Profile"}, "": {"longitude": 0, "timezone": "Zone/Profile"}}""");

        Assert.Equal(
            Phd2Profiles.ResolveTimezone(raw, null, "Zone/Global", StubZones),
            Phd2Profiles.ResolveTimezone(raw, "", "Zone/Global", StubZones));
        Assert.Equal(Phd2Profiles.ResolveSite(raw, null, 30.27, -97.74), Phd2Profiles.ResolveSite(raw, "", 30.27, -97.74));
        Assert.Equal(Phd2Profiles.HasOwnLongitude(raw, null), Phd2Profiles.HasOwnLongitude(raw, ""));
        Assert.Equal(Phd2Profiles.HasOwnLatitude(raw, null), Phd2Profiles.HasOwnLatitude(raw, ""));

        var resolveZone = Phd2Profiles.ZoneResolver(raw, "Zone/Global", StubZones);
        Assert.Equal(resolveZone(null), resolveZone(""));

        var resolveLongitude = Phd2Profiles.LongitudeResolver(raw, -97.74, "Zone/Global", StubLoad);
        Assert.Equal(resolveLongitude(null, Instant), resolveLongitude("", Instant));
    }

    [Fact]
    public void ALegacyStringEntry_TakesTheGlobal()
    {
        var raw = Json("""{"Rig A": "Askar 120"}""");

        Assert.Equal(("Zone/Global", Phd2ResolutionSource.Global),
            Phd2Profiles.ResolveTimezone(raw, "Rig A", "Zone/Global", StubZones));
    }

    [Fact]
    public void AnUnloadableGlobalZone_IsUnset()
    {
        // A failure is a thrown exception, or the machine's own zone.
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        Assert.Equal(("", Phd2ResolutionSource.Unset),
            Phd2Profiles.ResolveTimezone(raw, "Rig A", "Zone/Nonsense", StubZones));
    }

    [Fact]
    public void ZoneResolver_AgreesWithResolveTimezoneForEveryInput()
    {
        // A disagreement means the precomputed table and the direct call are two implementations
        // of one rule.
        var raw = Json(
            """{"Rig A": {"timezone": "Zone/Profile"}, "Rig B": {"timezone": "Zone/Nonsense"}, "Rig C": "Askar 120", "": {"timezone": "Zone/Profile"}}""");
        string?[] profiles = ["Rig A", "Rig B", "Rig C", "Rig Z", "", null];

        foreach (var globalZone in new[] { "Zone/Global", "", "Zone/Nonsense" })
        {
            var resolve = Phd2Profiles.ZoneResolver(raw, globalZone, StubZones);
            foreach (var profile in profiles)
            {
                Assert.Equal(Phd2Profiles.ResolveTimezone(raw, profile, globalZone, StubZones), resolve(profile));
            }
        }
    }

    [Fact]
    public void ZoneResolver_IsReusableAcrossCalls()
    {
        var resolve = Phd2Profiles.ZoneResolver(
            Json("""{"Rig A": {"timezone": "Zone/Profile"}}"""), "Zone/Global", StubZones);

        Assert.Equal(("Zone/Profile", Phd2ResolutionSource.Profile), resolve("Rig A"));
        Assert.Equal(("Zone/Profile", Phd2ResolutionSource.Profile), resolve("Rig A"));
        Assert.Equal(("Zone/Global", Phd2ResolutionSource.Global), resolve("Rig Z"));
        Assert.Equal(("Zone/Profile", Phd2ResolutionSource.Profile), resolve("Rig A"));
    }

    // ---- ZoneLoader, the one memoised name-to-zone loader (carried item 51) ----

    // A resolver that counts its calls, so the memo is provable rather than merely plausible.
    private sealed class CountingResolver(Func<string, TimeZoneInfo> answer)
    {
        public int Calls { get; private set; }

        public TimeZoneInfo Load(string id)
        {
            Calls++;
            return answer(id);
        }
    }

    [Fact]
    public void ZoneLoader_LoadsAKnownName()
    {
        // Red when the empty-name test is written against the wrong thing and every name answers
        // null: every zone in the application would read as unset and every guiding session would
        // drop out of the correlation with no warning, because a null zone is a legitimate outcome
        // everywhere else.
        var zone = Phd2Profiles.ZoneLoader()("UTC");

        Assert.NotNull(zone);
        Assert.Equal("UTC", zone.Id);
    }

    [Fact]
    public void ZoneLoader_AnEmptyName_IsNullWithNoLookup()
    {
        // The platform is never asked about "": it would throw, the catch would swallow the throw
        // into the same null, and the answer would look right while costing one throw per section
        // on every ASIAIR corpus. The throwing resolver is what makes the difference visible.
        var resolver = new CountingResolver(id => throw new InvalidOperationException(id));
        var load = Phd2Profiles.ZoneLoader(resolver.Load);

        Assert.Null(load(""));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public void ZoneLoader_AsksThePlatformOncePerDistinctName()
    {
        // Red when the dictionary is built inside the returned lambda rather than in the closure,
        // which compiles, answers correctly and memoises nothing: the count reads 5.
        var resolver = new CountingResolver(_ => TimeZoneInfo.Utc);
        var load = Phd2Profiles.ZoneLoader(resolver.Load);

        Assert.NotNull(load("Zone/Profile"));
        Assert.NotNull(load("Zone/Profile"));
        Assert.NotNull(load("Zone/Profile"));
        Assert.NotNull(load("Zone/Global"));
        Assert.NotNull(load("Zone/Global"));

        Assert.Equal(2, resolver.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZoneLoader_ABadName_IsMemoisedAsNull(bool notFound)
    {
        // The null is written to the memo, not merely returned. Red when the catch returns without
        // storing: a corpus whose profiles all name one misspelled zone pays a throw per section.
        var resolver = new CountingResolver(id => notFound
            ? throw new TimeZoneNotFoundException(id)
            : throw new InvalidTimeZoneException(id));
        var load = Phd2Profiles.ZoneLoader(resolver.Load);

        Assert.Null(load("Zone/Nonsense"));
        Assert.Null(load("Zone/Nonsense"));
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void ZoneLoader_AnyOtherException_Propagates()
    {
        // The catch is by name and stays that way. Widened to Exception it would swallow a
        // programming error into a null zone and the pass would carry on storing unzoned sessions
        // with no warning anywhere.
        var load = Phd2Profiles.ZoneLoader(id => throw new InvalidOperationException(id));

        Assert.Throws<InvalidOperationException>(() => load("Zone/Profile"));
    }

    [Fact]
    public void ZoneLoader_InstancesDoNotShareAMemo()
    {
        // Red when the dictionary is promoted to a static field during an extraction, which looks
        // like a free win and pins the platform's answer for the life of the process. A zone
        // database can change under a running process; a per-pass memo is the deliberate scope.
        var first = new CountingResolver(_ => TimeZoneInfo.Utc);
        var second = new CountingResolver(_ => TimeZoneInfo.Utc);

        Assert.NotNull(Phd2Profiles.ZoneLoader(first.Load)("Zone/Profile"));
        Assert.NotNull(Phd2Profiles.ZoneLoader(second.Load)("Zone/Profile"));

        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    // ---- Site resolution ----

    [Fact]
    public void EachCoordinate_ResolvesIndependently()
    {
        var raw = Json("""{"Rig A": {"latitude": 51.48}}""");

        var (latitude, longitude, _) = Phd2Profiles.ResolveSite(raw, "Rig A", 30.27, -97.74);

        Assert.Equal(51.48, latitude!.Value, 6);
        Assert.Equal(-97.74, longitude!.Value, 6);
    }

    [Fact]
    public void OneProfileCoordinateAndOneInherited_ReportsProfile()
    {
        // Source "global" here is the mis-read that would apply the tight tolerance to an
        // inherited value and accuse the user of a misconfiguration they never made.
        var raw = Json("""{"Rig A": {"latitude": 51.48}}""");

        Assert.Equal(Phd2ResolutionSource.Profile, Phd2Profiles.ResolveSite(raw, "Rig A", 30.27, -97.74).Source);
    }

    [Fact]
    public void NoProfileCoordinates_ReportGlobal()
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        var (latitude, longitude, source) = Phd2Profiles.ResolveSite(raw, "Rig A", 30.27, -97.74);

        Assert.Equal(30.27, latitude!.Value, 6);
        Assert.Equal(-97.74, longitude!.Value, 6);
        Assert.Equal(Phd2ResolutionSource.Global, source);
    }

    [Fact]
    public void NothingConfigured_ReportsUnset()
    {
        var (latitude, longitude, source) = Phd2Profiles.ResolveSite(null, "Rig A", null, null);

        Assert.Null(latitude);
        Assert.Null(longitude);
        Assert.Equal(Phd2ResolutionSource.Unset, source);
    }

    [Fact]
    public void AGlobalCoordinate_GoesThroughTheSameRangeCoercion()
    {
        var (latitude, longitude, source) = Phd2Profiles.ResolveSite(null, "Rig A", 500.0, double.NaN);

        Assert.Null(latitude);
        Assert.Null(longitude);
        Assert.Equal(Phd2ResolutionSource.Unset, source);
    }

    [Fact]
    public void HasOwnLongitude_IsTrueForAStoredZero()
    {
        // False here widens the tolerance for a rig whose site is precisely known.
        Assert.True(Phd2Profiles.HasOwnLongitude(Json("""{"Rig A": {"longitude": 0}}"""), "Rig A"));
        Assert.True(Phd2Profiles.HasOwnLatitude(Json("""{"Rig A": {"latitude": 0}}"""), "Rig A"));
    }

    [Fact]
    public void HasOwnLongitude_IsFalseForALegacyStringEntry()
    {
        // True here asserts a site that was never stored.
        var raw = Json("""{"Rig A": "Askar 120"}""");

        Assert.False(Phd2Profiles.HasOwnLongitude(raw, "Rig A"));
        Assert.False(Phd2Profiles.HasOwnLatitude(raw, "Rig A"));
    }

    [Fact]
    public void HasOwnLatitude_IsFalseForAnUnknownProfileAndForAFailedCoercion()
    {
        var raw = Json("""{"Rig A": {"latitude": 500, "longitude": true}}""");

        Assert.False(Phd2Profiles.HasOwnLatitude(raw, "Rig A"));
        Assert.False(Phd2Profiles.HasOwnLongitude(raw, "Rig A"));
        Assert.False(Phd2Profiles.HasOwnLatitude(raw, "Rig Z"));
        Assert.False(Phd2Profiles.HasOwnLatitude(raw, null));
    }

    [Fact]
    public void LongitudeResolver_ReturnsNullOnlyWhenNothingIsConfigured()
    {
        // 0.0 returned as "nothing configured" is the prime meridian again. Since Phase 17's
        // ruling U4 "nothing configured" includes the zone: a library with a zone set and no
        // longitude anywhere resolves at step 3, and only a library with neither resolves to null.
        var raw = Json("""{"Rig A": {"longitude": 0}, "Rig B": {"telescope": "Askar 120"}}""");

        var withGlobal = Phd2Profiles.LongitudeResolver(raw, -97.74, "", StubLoad);
        Assert.Equal(0.0, withGlobal("Rig A", Instant)!.Value, 6);
        Assert.Equal(-97.74, withGlobal("Rig B", Instant)!.Value, 6);
        Assert.Equal(-97.74, withGlobal("Rig Z", Instant)!.Value, 6);

        // No global longitude and no zone: the four original null arms, unchanged.
        var withoutGlobal = Phd2Profiles.LongitudeResolver(raw, null, "", StubLoad);
        Assert.Equal(0.0, withoutGlobal("Rig A", Instant)!.Value, 6);
        Assert.Null(withoutGlobal("Rig B", Instant));
        Assert.Null(withoutGlobal("Rig Z", Instant));
        Assert.Null(withoutGlobal(null, Instant));

        // A configured zone and no longitude anywhere: step 3 answers, which is the whole of
        // ruling U4. Zone/Minus5 is UTC-05:00, so 5 hours west of Greenwich is -75 degrees.
        var withZone = Phd2Profiles.LongitudeResolver(raw, null, "Zone/Minus5", StubLoad);
        Assert.Equal(0.0, withZone("Rig A", Instant)!.Value, 6);
        Assert.Equal(-75.0, withZone("Rig B", Instant)!.Value, 6);
        Assert.Equal(-75.0, withZone("Rig Z", Instant)!.Value, 6);
        Assert.Equal(-75.0, withZone(null, Instant)!.Value, 6);

        // A zone the loader refuses falls through to step 4, exactly as an unset one does.
        var withRefusedZone = Phd2Profiles.LongitudeResolver(raw, null, "Zone/Nonsense", StubLoad);
        Assert.Equal(0.0, withRefusedZone("Rig A", Instant)!.Value, 6);
        Assert.Null(withRefusedZone("Rig B", Instant));
        Assert.Null(withRefusedZone(null, Instant));
    }

    /// <summary>
    /// Spec 8.3's four steps in order, one row each (ruling U4, brief case 2). A failure looks
    /// like a resolver that tests the profile longitude for truthiness rather than for null: the
    /// stored 0.0 row falls through to the global value and a rig on the prime meridian silently
    /// moves to the user's own site.
    /// </summary>
    [Theory]
    // Step 1 wins over a global longitude and over a zone.
    [InlineData("""{"Rig A": {"longitude": 12.5}}""", -97.74, "Zone/Minus5", "Rig A", 12.5)]
    // Step 1's stored 0.0 is the prime meridian and is an answer, not a signal.
    [InlineData("""{"Rig A": {"longitude": 0}}""", -97.74, "Zone/Minus5", "Rig A", 0.0)]
    // Step 2 wins over a zone.
    [InlineData("""{"Rig A": {"telescope": "Askar 120"}}""", -97.74, "Zone/Minus5", "Rig A", -97.74)]
    // Step 3 alone: offsetHours * 15, positive east.
    [InlineData("""{"Rig A": {"telescope": "Askar 120"}}""", null, "Zone/Minus5", "Rig A", -75.0)]
    // Step 3 with a zone at Greenwich answers 0.0, which is an answer and not the null signal.
    [InlineData("""{"Rig A": {"telescope": "Askar 120"}}""", null, "Zone/Global", "Rig A", 0.0)]
    public void LongitudeResolver_TakesSpec83sFourStepsInOrder(
        string stored, double? globalLongitude, string globalZone, string profile, double expected)
    {
        var resolve = Phd2Profiles.LongitudeResolver(
            Json(stored), globalLongitude, globalZone, StubLoad);

        Assert.Equal(expected, resolve(profile, Instant)!.Value, 6);
    }

    [Fact]
    public void LongitudeResolver_AnsweringNothing_IsStep4()
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        // Nothing configured at all.
        Assert.Null(Phd2Profiles.LongitudeResolver(raw, null, "", StubLoad)("Rig A", Instant));

        // A zone the loader refuses is not a zone.
        Assert.Null(
            Phd2Profiles.LongitudeResolver(raw, null, "Zone/Nonsense", StubLoad)("Rig A", Instant));

        // A null instant skips step 3: a section whose zone does not resolve has no UTC start, so
        // there is no instant at which to ask for an offset, and its session date is null anyway.
        Assert.Null(
            Phd2Profiles.LongitudeResolver(raw, null, "Zone/Minus5", StubLoad)("Rig A", null));
    }

    /// <summary>
    /// Spec 8.3 step 2's rule, now read from <c>SessionDate.UsableLongitude</c> rather than from
    /// this file's own coercion: a stored <c>general.observer_longitude</c> that is not a finite
    /// number inside the legal range is UNSET, never the limit, and the resolver falls through to
    /// step 3. This side has always behaved this way. The case is here so that a later change
    /// which moves the rule to a clamp, or which gives this side its own copy again, fails on both
    /// sides at once rather than on the frame side alone.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(180.0001)]
    [InlineData(-180.0001)]
    public void LongitudeResolver_Step2_AStoredValueThatCannotDescribeASite_IsUnset(double stored)
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}, "Rig B": {"longitude": 12.5}}""");

        // Step 3 answers instead, for a mapped profile and for an unknown one alike.
        var withZone = Phd2Profiles.LongitudeResolver(raw, stored, "Zone/Minus5", StubLoad);
        Assert.Equal(-75.0, withZone("Rig A", Instant)!.Value, 6);
        Assert.Equal(-75.0, withZone("Rig Z", Instant)!.Value, 6);
        Assert.Equal(-75.0, withZone(null, Instant)!.Value, 6);

        // Step 1 is untouched: a profile with a longitude of its own keeps it.
        Assert.Equal(12.5, withZone("Rig B", Instant)!.Value, 6);

        // And step 4 where no zone resolves either, which is what a null stored value does today.
        Assert.Null(Phd2Profiles.LongitudeResolver(raw, stored, "", StubLoad)("Rig A", Instant));
    }

    [Theory]
    [InlineData(180.0)]   // the legal boundary, a real place: the rule keeps it
    [InlineData(-180.0)]
    [InlineData(0.0)]     // and the prime meridian is an answer, not the null signal
    [InlineData(-97.74)]
    public void LongitudeResolver_Step2_ALegalValue_StillWinsOverTheZone(double stored)
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        var resolve = Phd2Profiles.LongitudeResolver(raw, stored, "Zone/Minus5", StubLoad);

        Assert.Equal(stored, resolve("Rig A", Instant)!.Value, 6);
        Assert.Equal(stored, resolve(null, Instant)!.Value, 6);
    }

    /// <summary>
    /// The offset is taken at the session's own instant and never at <c>DateTime.UtcNow</c> (brief
    /// case 3). A failure looks like a resolver that passes the current time: both sessions answer
    /// the same longitude, and every summer session in a DST zone is filed against a winter
    /// offset.
    /// </summary>
    [Fact]
    public void LongitudeResolver_TakesTheOffsetAtTheSessionsOwnInstant()
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");
        var resolve = Phd2Profiles.LongitudeResolver(raw, null, "Zone/Dst", StubLoad);

        var winter = resolve("Rig A", new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc));
        var summer = resolve("Rig A", new DateTime(2026, 7, 15, 3, 0, 0, DateTimeKind.Utc));

        // Zone/Dst is UTC-05:00 standard with a one hour saving in northern summer.
        Assert.Equal(-75.0, winter!.Value, 6);
        Assert.Equal(-60.0, summer!.Value, 6);
        Assert.NotEqual(winter.Value, summer.Value);
    }

    /// <summary>
    /// Latitude gained no step (brief case 8). A failure looks like a symmetric "improvement" that
    /// gives latitude a zone fallback, which spec 8.3 refuses: a zone offset says nothing about how
    /// far north a rig stands, and latitude plays no part in spec 8.2's arithmetic at all.
    /// </summary>
    [Fact]
    public void AConfiguredZone_GivesTheLatitudeNothing()
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        // ResolveSite takes no zone and no instant, which is the structural half of the proof.
        Assert.Equal((null, null, Phd2ResolutionSource.Unset), Phd2Profiles.ResolveSite(raw, "Rig A", null, null));
        Assert.False(Phd2Profiles.HasOwnLatitude(raw, "Rig A"));
        Assert.False(Phd2Profiles.HasOwnLongitude(raw, "Rig A"));

        // And the longitude resolver of the same library, with the same zone, does answer.
        Assert.Equal(
            -75.0,
            Phd2Profiles.LongitudeResolver(raw, null, "Zone/Minus5", StubLoad)("Rig A", Instant)!.Value,
            6);
    }

    // ---- The rename rewrite ----

    [Fact]
    public void RewriteTelescopes_PreservesTimezoneAndCoordinates()
    {
        // A rewritten entry flattened to a legacy string discards the zone and the site the user
        // configured, which is exactly what the two rewriters the web replaced did.
        var raw = Json(
            """{"Rig A": {"telescope": "SVBony SV503 80mm", "timezone": "America/Chicago", "latitude": 30.27, "longitude": -97.74}}""");

        var map = Phd2Profiles.RewriteTelescopes(raw, _ => "SVBony 80ED");

        var entry = Assert.Contains("Rig A", map);
        Assert.Equal("SVBony 80ED", entry.Telescope);
        Assert.Equal("America/Chicago", entry.Timezone);
        Assert.Equal(30.27, entry.Latitude!.Value, 6);
        Assert.Equal(-97.74, entry.Longitude!.Value, 6);
    }

    [Fact]
    public void RewriteTelescopes_PreservesAZeroLongitude()
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120", "longitude": 0, "latitude": 0}}""");

        var entry = Assert.Contains("Rig A", Phd2Profiles.RewriteTelescopes(raw, _ => "Askar 120ED"));

        Assert.NotNull(entry.Longitude);
        Assert.Equal(0.0, entry.Longitude!.Value, 6);
        Assert.NotNull(entry.Latitude);
    }

    [Fact]
    public void RewriteTelescopes_LeavesUnlistedNamesAlone()
    {
        // A name replaced with null or the empty string unmaps a rig on an unrelated rename.
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        Assert.Equal("Askar 120", Assert.Contains("Rig A", Phd2Profiles.RewriteTelescopes(raw, _ => null)).Telescope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RewriteTelescopes_TreatsAnEmptyResultAsNoChange(string replacement)
    {
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        Assert.Equal("Askar 120",
            Assert.Contains("Rig A", Phd2Profiles.RewriteTelescopes(raw, _ => replacement)).Telescope);
    }

    [Fact]
    public void RewriteTelescopes_SkipsUnmappedEntries()
    {
        // A failure calls the rename delegate with null.
        var raw = Json("""{"Rig A": {"timezone": "America/Chicago"}}""");
        var calls = new List<string>();

        var map = Phd2Profiles.RewriteTelescopes(raw, name =>
        {
            calls.Add(name);
            return "Askar 120";
        });

        Assert.Empty(calls);
        Assert.Null(Assert.Contains("Rig A", map).Telescope);
        Assert.Equal("America/Chicago", Assert.Contains("Rig A", map).Timezone);
    }

    [Fact]
    public void RewriteTelescopes_ChangeDetectionIgnoresTheShapeUpgrade()
    {
        // Comparing the result against the stored value reports a change on every save of a legacy
        // map, which is a write loop. The comparison is SameMap against Normalize(raw).
        var raw = Json("""{"Rig A": "Askar 120"}""");

        var rewritten = Phd2Profiles.RewriteTelescopes(raw, _ => null);

        Assert.True(Phd2Profiles.SameMap(rewritten, Phd2Profiles.Normalize(raw)));
        Assert.NotEqual(raw.GetRawText(), Phd2Profiles.ToJson(rewritten).GetRawText());
    }

    [Fact]
    public void SameMap_IsStructuralWhereReferenceEqualityIsNot()
    {
        // This is the case that fails if anyone re-points the change-detection sentence in
        // RewriteTelescopes at "!=" or Equals. Neither is structural on IReadOnlyDictionary or on
        // Dictionary, so both report a change here although the two maps are element for element
        // identical, and Task 6 following that advice would write the profile map on every save.
        var raw = Json("""{"Rig A": "Askar 120", "Rig B": {"timezone": "America/Chicago", "longitude": 0}}""");

        var rewritten = Phd2Profiles.RewriteTelescopes(raw, _ => null);
        var normalized = Phd2Profiles.Normalize(raw);

        Assert.True(Phd2Profiles.SameMap(rewritten, normalized));
        Assert.NotSame(rewritten, normalized);
        Assert.False(ReferenceEquals(rewritten, normalized));
    }

    [Fact]
    public void SameMap_IsFalseForAChangedTelescopeZoneOrCoordinate()
    {
        var baseMap = Phd2Profiles.Normalize(Json(
            """{"Rig A": {"telescope": "Askar 120", "timezone": "America/Chicago", "latitude": 0, "longitude": -97.74}}"""));

        Assert.False(Phd2Profiles.SameMap(baseMap, Phd2Profiles.Normalize(Json(
            """{"Rig A": {"telescope": "Askar 120ED", "timezone": "America/Chicago", "latitude": 0, "longitude": -97.74}}"""))));
        Assert.False(Phd2Profiles.SameMap(baseMap, Phd2Profiles.Normalize(Json(
            """{"Rig A": {"telescope": "Askar 120", "timezone": "Europe/Berlin", "latitude": 0, "longitude": -97.74}}"""))));
        Assert.False(Phd2Profiles.SameMap(baseMap, Phd2Profiles.Normalize(Json(
            """{"Rig A": {"telescope": "Askar 120", "timezone": "America/Chicago", "latitude": 0, "longitude": -97.75}}"""))));
    }

    [Fact]
    public void SameMap_IsFalseForAnAddedOrRemovedKey()
    {
        var baseMap = Phd2Profiles.Normalize(Json("""{"Rig A": {"telescope": "Askar 120"}}"""));

        Assert.False(Phd2Profiles.SameMap(baseMap, Phd2Profiles.Normalize(Json(
            """{"Rig A": {"telescope": "Askar 120"}, "Rig B": "SVBony 80ED"}"""))));
        Assert.False(Phd2Profiles.SameMap(baseMap, Phd2Profiles.Normalize(Json("{}"))));

        // Same count, different keys: a count-only comparison would call these the same map.
        Assert.False(Phd2Profiles.SameMap(baseMap, Phd2Profiles.Normalize(Json(
            """{"Rig B": {"telescope": "Askar 120"}}"""))));
    }

    [Fact]
    public void RewriteTelescopes_ReturnsAFreshMapTheCallerCanEditFreely()
    {
        // The GetRawText half this replaces could not fail, for the reason given on
        // Normalize_ReturnsAFreshMapOnEveryCall. Emptying the returned map must leave the next
        // read of the stored value intact.
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}}""");

        var first = Phd2Profiles.RewriteTelescopes(raw, _ => "Askar 120ED");
        var second = Phd2Profiles.RewriteTelescopes(raw, _ => "Askar 120ED");

        Assert.NotSame(first, second);
        Assert.Equal("Askar 120ED", Assert.Contains("Rig A", first).Telescope);

        ((IDictionary<string, Phd2ProfileEntry>)first).Clear();

        Assert.Equal("Askar 120ED", Assert.Contains("Rig A", second).Telescope);
        Assert.Equal("Askar 120", Assert.Contains("Rig A", Phd2Profiles.Normalize(raw)).Telescope);
    }

    // ---- SetTelescope ----

    [Fact]
    public void SetTelescope_PreservesTheRestOfTheEntry()
    {
        var raw = Json(
            """{"Rig A": {"telescope": "Askar 120", "timezone": "America/Chicago", "latitude": 30.27, "longitude": -97.74}}""");

        var entry = Assert.Contains("Rig A", Phd2Profiles.SetTelescope(raw, "Rig A", "SVBony 80ED"));

        Assert.Equal("SVBony 80ED", entry.Telescope);
        Assert.Equal("America/Chicago", entry.Timezone);
        Assert.Equal(30.27, entry.Latitude!.Value, 6);
        Assert.Equal(-97.74, entry.Longitude!.Value, 6);
    }

    [Fact]
    public void SetTelescope_AddsAProfileTheMapDidNotCarry()
    {
        var map = Phd2Profiles.SetTelescope(Json("{}"), "Rig A", "  Askar 120  ");

        Assert.Equal("Askar 120", Assert.Contains("Rig A", map).Telescope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClearingTheTelescope_DropsAnEntryThatCarriesNothingElse(string? cleared)
    {
        // A dead entry left in the document stays there forever.
        var raw = Json("""{"Rig A": {"telescope": "Askar 120"}, "Rig B": "SVBony 80ED"}""");

        var map = Phd2Profiles.SetTelescope(raw, "Rig A", cleared);

        Assert.DoesNotContain("Rig A", map);
        Assert.Contains("Rig B", map);
    }

    [Fact]
    public void ClearingTheTelescope_KeepsAnEntryThatCarriesATimezone()
    {
        // The zone discarded here means unmapping and re-mapping a rig loses its configuration.
        var raw = Json("""{"Rig A": {"telescope": "Askar 120", "timezone": "America/Chicago"}}""");

        var entry = Assert.Contains("Rig A", Phd2Profiles.SetTelescope(raw, "Rig A", null));

        Assert.Null(entry.Telescope);
        Assert.Equal("America/Chicago", entry.Timezone);
    }

    [Theory]
    [InlineData("""{"telescope": "Askar 120", "longitude": 0}""")]
    [InlineData("""{"telescope": "Askar 120", "latitude": 0}""")]
    public void ClearingTheTelescope_KeepsAnEntrySitedOnThePrimeMeridian(string stored)
    {
        // The entry dropped here means 0.0 read as absent. CarriesConfiguration tests the two
        // coordinates with "is not null", never truthiness.
        var raw = Json($$$"""{"Rig A": {{{stored}}}}""");

        var entry = Assert.Contains("Rig A", Phd2Profiles.SetTelescope(raw, "Rig A", ""));

        Assert.Null(entry.Telescope);
        Assert.True(entry.Latitude is not null || entry.Longitude is not null);
    }

    // ---- ToJson ----

    [Fact]
    public void ToJson_RoundTripsThroughNormalize()
    {
        // A written document that Normalize reads back differently is a slow corruption across
        // saves.
        var map = Phd2Profiles.Normalize(Json(
            """{"Rig A": {"telescope": "Askar 120", "timezone": "America/Chicago", "latitude": 0, "longitude": -97.74}, "Rig B": "SVBony 80ED", "Rig C": {"timezone": "Europe/Berlin"}}"""));

        Assert.Equal(map, Phd2Profiles.Normalize(Phd2Profiles.ToJson(map)));
    }

    [Fact]
    public void ToJson_WritesTheCanonicalFourFieldFormForAnUnmappedEntry()
    {
        var map = Phd2Profiles.Normalize(Json("""{"Rig A": {"timezone": "America/Chicago"}}"""));

        var written = Phd2Profiles.ToJson(map).GetRawText();

        Assert.Contains("\"telescope\"", written, StringComparison.Ordinal);
        Assert.Contains("\"timezone\"", written, StringComparison.Ordinal);
        Assert.Contains("\"latitude\"", written, StringComparison.Ordinal);
        Assert.Contains("\"longitude\"", written, StringComparison.Ordinal);
    }
}
