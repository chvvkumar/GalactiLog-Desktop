using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalactiLog.Core.Phd2;

// The single definition of what a PHD2 equipment-profile mapping is (spec 7.6, "Timezone
// resolution"). Port of backend/app/services/phd2_profiles.py.
//
// A PHD2 guide log names the equipment profile that produced it and nothing else about the rig.
// Which telescope the profile belongs to, which zone its wall-clock timestamps are in and where
// on Earth the rig stands are all user configuration, stored in general.phd2_profile_map.
//
// Pure shape handling over JSON and plain values: no file IO, no database, no settings object.

/// <summary>One profile's stored configuration, in canonical form. Mirrors
/// <c>phd2_profiles._EMPTY_ENTRY</c>.</summary>
public sealed record Phd2ProfileEntry
{
    /// <summary>Null means the profile is not mapped to a telescope.</summary>
    [JsonPropertyName("telescope")] public string? Telescope { get; init; }

    /// <summary>The empty string means inherit <c>general.observer_timezone</c>.</summary>
    [JsonPropertyName("timezone")] public string Timezone { get; init; } = "";

    // The inherit marker for the two numeric fields is null, NEVER 0. Zero is a legal longitude
    // (Greenwich) and a legal latitude (the equator), so a falsy or == 0 test against either is a
    // defect: it would silently move a rig on the prime meridian to the user's own site and file
    // its nights under the wrong date. Every test against these two fields is "is null" or
    // "is not null".
    [JsonPropertyName("latitude")] public double? Latitude { get; init; }
    [JsonPropertyName("longitude")] public double? Longitude { get; init; }
}

/// <summary>The vocabulary the two resolvers and their callers share.</summary>
public static class Phd2ResolutionSource
{
    public const string Profile = "profile";
    public const string Global = "global";
    public const string Unset = "unset";
}

public static class Phd2Profiles
{
    private const double LatitudeLimit = 90.0;
    private const double LongitudeLimit = 180.0;

    /// <summary>Every stored form of the profile map in one canonical form: the current object
    /// form, the legacy <c>{"Rig A": "Askar 120"}</c> string form, an entry carrying only some
    /// keys, and hand-edited junk. Returns a fresh map whose every value carries all four
    /// canonical fields, freshly allocated on every call. Idempotent, and it never mutates its
    /// argument.
    /// <para>
    /// Every public entry point here takes the raw <see cref="JsonElement"/> and normalizes
    /// internally, so no caller carries its own tolerance; none of them accepts an already
    /// normalized map. A pass that resolves once per guiding section uses
    /// <see cref="ZoneResolver"/> and <see cref="LongitudeResolver"/>, which normalize once at
    /// construction and answer from a precomputed table thereafter.
    /// </para></summary>
    public static IReadOnlyDictionary<string, Phd2ProfileEntry> Normalize(JsonElement? raw)
    {
        var map = new Dictionary<string, Phd2ProfileEntry>(StringComparer.Ordinal);
        if (raw is not { ValueKind: JsonValueKind.Object })
        {
            return map;
        }

        foreach (var property in raw.Value.EnumerateObject())
        {
            // A non-string key is unreachable in JSON, so unlike the Python there is nothing to
            // guard against here.
            var entry = CoerceEntry(property.Value);
            if (entry is not null)
            {
                map[property.Name] = entry;
            }
        }

        return map;
    }

    /// <summary>The document a writer stores: the canonical four-field object form for every
    /// entry, including entries whose telescope is null.</summary>
    public static JsonElement ToJson(IReadOnlyDictionary<string, Phd2ProfileEntry> map)
        => JsonSerializer.SerializeToElement(map);

    /// <summary>Whether two canonical maps carry the same entries. This is the comparison a change
    /// test needs, and the only one available: every map this module returns is a freshly
    /// allocated dictionary, and <c>IReadOnlyDictionary</c> declares no <c>op_Inequality</c> while
    /// <c>Dictionary</c> does not override <c>Equals</c>, so both <c>!=</c> and <c>Equals</c> are
    /// reference tests that report a change on every call whatever the contents.
    /// <para>
    /// Both arguments are non-nullable. Nullable reference types are on across the solution with
    /// warnings as errors, so a caller holding a nullable map is told to settle it at the call
    /// site rather than having a null quietly answered here, where either answer would be a
    /// guess: two absent maps are not obviously the same map, and an absent map against an empty
    /// one is the shape a first-ever save takes.
    /// </para></summary>
    public static bool SameMap(
        IReadOnlyDictionary<string, Phd2ProfileEntry> a,
        IReadOnlyDictionary<string, Phd2ProfileEntry> b)
        => a.Count == b.Count
            && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);

    /// <summary>The profile-name to telescope-name projection. An unmapped profile is absent from
    /// the result rather than present with a null value, which preserves the lookup semantics
    /// every caller is written against.</summary>
    public static IReadOnlyDictionary<string, string> TelescopeMap(JsonElement? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (profile, entry) in Normalize(raw))
        {
            if (entry.Telescope is not null)
            {
                map[profile] = entry.Telescope;
            }
        }

        return map;
    }

    /// <summary>Which rig a stored PHD2 session describes, and the ONE implementation of that
    /// rule. <b>The live map handed here is the sole authority.</b> A profile the map carries
    /// resolves to that entry's telescope, which may itself be null; a profile the map does not
    /// carry, and a section whose header named no profile at all, resolve to no rig.
    /// <para>
    /// The stored <c>phd2_sessions.telescope</c> column is deliberately NOT consulted, and the
    /// parameter for it is gone. The column is the map's own answer as it stood at ingest, so
    /// falling back to it made a mapping take effect and an unmapping never take effect
    /// (phase-review.md F3): <see cref="SetTelescope"/> drops an entry that carries nothing else
    /// when its telescope is cleared, which is exactly what the Equipment tab does when a reader
    /// unmaps a profile, so the entry leaves the map, the fallback fires and the removed rig speaks
    /// again forever. There is no repair path either: the column's only writer is the ingest and
    /// the guide-log pass skips an unchanged log, so no rescan rewrites it.
    /// </para>
    /// <para>
    /// It lives in Core because three callers need it and a copy in any of them would drift: the
    /// correlation pass resolves it to pick a night's samples, and both read paths resolve it at
    /// query time because nothing ever writes the newer answer back to the column
    /// (task5b-review.md P2-1).
    /// </para></summary>
    public static string? EffectiveTelescope(
        string? equipmentProfile,
        IReadOnlyDictionary<string, Phd2ProfileEntry> profileMap)
        => !string.IsNullOrEmpty(equipmentProfile)
            && profileMap.TryGetValue(equipmentProfile, out var entry)
                ? entry.Telescope
                : null;

    /// <summary>The map with one profile's telescope changed and everything else intact. Clearing
    /// the telescope (null, empty or whitespace) drops an entry that carries nothing else, as the
    /// settings UI has always done, and keeps an entry that carries a timezone or either
    /// coordinate with <c>Telescope</c> null, so unmapping a rig does not silently discard its
    /// zone and site.</summary>
    public static IReadOnlyDictionary<string, Phd2ProfileEntry> SetTelescope(
        JsonElement? raw, string profile, string? telescope)
    {
        var map = new Dictionary<string, Phd2ProfileEntry>(Normalize(raw), StringComparer.Ordinal);
        var name = TrimmedOrNull(telescope);
        var entry = (map.TryGetValue(profile, out var existing) ? existing : new Phd2ProfileEntry())
            with { Telescope = name };

        if (name is null && !CarriesConfiguration(entry))
        {
            map.Remove(profile);
            return map;
        }

        map[profile] = entry;
        return map;
    }

    /// <summary>The map with every telescope name put through <paramref name="rename"/>. Grouping
    /// two telescope names turns one into an alias, which strands the map pointing at the alias,
    /// so the stored names are folded onto the new canonical ones. A name the rename does not
    /// recognise, signalled by a null or empty return, is left alone, so callers write no
    /// <c>?? name</c> fallback of their own. Entries with no telescope are left untouched and
    /// <paramref name="rename"/> is not called for them. Only the telescope moves: the timezone
    /// and the site of every entry survive.
    /// <para>
    /// Test for a real change with <c>!SameMap(result, Normalize(raw))</c>, never against
    /// <c>raw</c> and never with <c>!=</c> or <c>Equals</c>. Two reasons, and a caller that misses
    /// either one writes the profile map back on every save. Against <c>raw</c>: a legacy map
    /// always differs from its canonical form, so the shape upgrade alone reads as a change. With
    /// <c>!=</c> or <c>Equals</c>: both sides are freshly allocated dictionaries and neither
    /// operator is structural here, so the test is a reference comparison that is true even when
    /// nothing moved. <see cref="SameMap"/> is the structural comparison, and it lives here rather
    /// than in each caller so there is one of it.
    /// </para></summary>
    public static IReadOnlyDictionary<string, Phd2ProfileEntry> RewriteTelescopes(
        JsonElement? raw, Func<string, string?> rename)
    {
        var map = new Dictionary<string, Phd2ProfileEntry>(StringComparer.Ordinal);
        foreach (var (profile, entry) in Normalize(raw))
        {
            var replacement = entry.Telescope is null ? null : TrimmedOrNull(rename(entry.Telescope));
            map[profile] = replacement is null ? entry : entry with { Telescope = replacement };
        }

        return map;
    }

    /// <summary>The zone a section's timestamps are in, and where it came from. The order is the
    /// profile's own zone, then <c>general.observer_timezone</c>, then nothing. An unloadable
    /// per-profile zone degrades to the global one and reports <c>global</c>, because from the
    /// caller's point of view the global value is what will be used.</summary>
    public static (string Zone, string Source) ResolveTimezone(
        JsonElement? raw, string? profile, string? globalTimezone, Func<string, TimeZoneInfo>? resolver = null)
        => ResolveZone(Lookup(Normalize(raw), profile), globalTimezone, ZoneLoader(resolver));

    /// <summary>A <see cref="ResolveTimezone"/> bound to one map and built once per pass.
    /// Resolving a zone costs a <see cref="TimeZoneInfo"/> construction and a pass over a library
    /// resolves once per guiding section, so the answer for every configured profile plus the one
    /// fallback is precomputed and a lookup during the pass is a dictionary hit.</summary>
    public static Func<string?, (string Zone, string Source)> ZoneResolver(
        JsonElement? raw, string? globalTimezone, Func<string, TimeZoneInfo>? resolver = null)
    {
        // One loader for the whole table, so a corpus whose profiles all name one zone asks the
        // platform once rather than once per profile and once more for the fallback.
        var load = ZoneLoader(resolver);
        var fallback = ResolveZone(null, globalTimezone, load);
        var answers = new Dictionary<string, (string Zone, string Source)>(StringComparer.Ordinal);
        foreach (var (profile, entry) in Normalize(raw))
        {
            answers[profile] = ResolveZone(entry, globalTimezone, load);
        }

        return profile => answers.TryGetValue(profile ?? "", out var answer) ? answer : fallback;
    }

    /// <summary>The <see cref="TimeZoneInfo"/> behind a resolved zone name, memoised for one pass.
    /// The empty name, which is <see cref="ZoneResolver"/>'s own <c>unset</c> answer, returns null
    /// without a lookup, and so does a name the platform will not load.
    /// <para>
    /// The memo is why this exists: a corpus with hundreds of guiding sections would otherwise hit
    /// the platform zone database once per section. <see cref="ZoneResolver"/> has already tested
    /// loadability, so a non-empty name that reaches here normally resolves; the catch is here
    /// because a zone database can change under a running process and a warning must never break a
    /// pass. The null it produces is memoised too, so a misspelled zone costs one throw for the
    /// pass rather than one per section.
    /// </para>
    /// <para>
    /// The memo is per returned instance and NEVER static. A per-pass memo keeps one pass
    /// self-consistent without pinning a stale answer for the life of the application. The
    /// returned delegate is not thread safe and needs no locking, because each caller builds its
    /// own for its own pass.
    /// </para></summary>
    public static Func<string, TimeZoneInfo?> ZoneLoader(Func<string, TimeZoneInfo>? resolver = null)
    {
        var load = resolver ?? TimeZoneInfo.FindSystemTimeZoneById;
        var zones = new Dictionary<string, TimeZoneInfo?>(StringComparer.Ordinal);

        return name =>
        {
            if (name.Length == 0)
            {
                return null;
            }

            if (zones.TryGetValue(name, out var cached))
            {
                return cached;
            }

            TimeZoneInfo? zone;
            try
            {
                zone = load(name);
            }
            // ArgumentException, and with it ArgumentNullException, came from the private
            // Loadable this member absorbed. It is unreachable behind the empty-name guard above
            // with the platform resolver, and it is kept rather than dropped so the fold changed
            // no caller's tolerance. Anything else still propagates, which is the rule
            // ZoneLoader_AnyOtherException_Propagates pins.
            catch (Exception exception) when (
                exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                zone = null;
            }

            zones[name] = zone;
            return zone;
        };
    }

    /// <summary>Where a section's rig stands, and where those coordinates came from. Each
    /// coordinate resolves independently: the profile's own value when it is not null, otherwise
    /// the global observer value, otherwise null.
    /// <para>
    /// <c>Source</c> describes the PAIR and is presentation only. NEVER BRANCH BEHAVIOUR ON IT. A
    /// profile carrying a latitude and no longitude reports <c>profile</c> while its longitude is
    /// the home site's, so a decision made here would apply the tight tolerance to an inherited
    /// value and accuse the user of a misconfiguration they never made. Ask
    /// <see cref="HasOwnLongitude"/> or <see cref="HasOwnLatitude"/> for the one field a decision
    /// actually turns on.
    /// </para></summary>
    public static (double? Latitude, double? Longitude, string Source) ResolveSite(
        JsonElement? raw, string? profile, double? globalLatitude, double? globalLongitude)
    {
        var entry = Lookup(Normalize(raw), profile);
        return ResolveSiteCore(
            entry,
            CoerceCoordinate(globalLatitude, LatitudeLimit),
            CoerceCoordinate(globalLongitude, LongitudeLimit));
    }

    /// <summary>Whether this profile stores a latitude of its own. True for a stored 0.0, a rig on
    /// the equator. False for an unknown profile, a null profile, a legacy string entry and a
    /// value that failed range coercion.</summary>
    public static bool HasOwnLatitude(JsonElement? raw, string? profile)
        => Lookup(Normalize(raw), profile)?.Latitude is not null;

    /// <summary>Whether this profile stores a longitude of its own. True for a stored 0.0, a rig
    /// on the prime meridian.</summary>
    public static bool HasOwnLongitude(JsonElement? raw, string? profile)
        => Lookup(Normalize(raw), profile)?.Longitude is not null;

    /// <summary>The per-profile longitude lookup the session-date arithmetic needs, built once per
    /// pass. Spec 8.3's four steps in order: the profile's own <c>longitude</c>, then
    /// <c>general.observer_longitude</c>, then the configured <c>general.observer_timezone</c> at
    /// the session's own start instant, then null. A resolved 0.0 is a site on the prime meridian
    /// and is an answer, not the null signal.
    /// <para>
    /// Step 3 reads the GLOBAL zone and never the profile's own (Phase 17 ruling U4, question Q8).
    /// That is what makes this side agree with <see cref="Sessions.SessionDate.ResolveLongitude"/>,
    /// whose own step 3 reads the same global key: a profile zone here would give a guiding session
    /// a longitude the frame of the same evening cannot reach, and the two would disagree on the
    /// calendar date of one night again. Every consumer of a guiding night joins on strict
    /// <c>session_date</c> equality, so that disagreement is an empty chart with no error.
    /// </para>
    /// <para>
    /// Step 2 reads the stored longitude through
    /// <see cref="Sessions.SessionDate.UsableLongitude"/>, which is where that rule now lives and
    /// where the frame side reads it too. A hand-edited value that cannot describe a site is
    /// therefore unset on both sides, where it used to be unset here and raw there.
    /// </para>
    /// <para>
    /// Steps 1 and 2 do not depend on the instant and stay precomputed at construction. Only step 3
    /// is evaluated per call, through <paramref name="load"/>, so a corpus of hundreds of sections
    /// costs one platform lookup. A null instant skips step 3 and answers null: a section whose
    /// zone does not resolve has no UTC start, so there is no instant at which to ask for an
    /// offset, and its session date is null anyway.
    /// </para>
    /// <para>
    /// <c>general.observer_latitude</c> and the profile's own <c>latitude</c> gain nothing: latitude
    /// plays no part in the imaging-night arithmetic of spec 8.2, which is why
    /// <see cref="ResolveSite"/>, <see cref="HasOwnLatitude"/> and <see cref="HasOwnLongitude"/>
    /// are untouched.
    /// </para></summary>
    public static Func<string?, DateTime?, double?> LongitudeResolver(
        JsonElement? raw,
        double? globalLongitude,
        string? globalTimezone,
        Func<string, TimeZoneInfo?>? load = null)
    {
        // Step 2 through the frame side's own member, for the same reason step 3 goes there: one
        // rule for the one stored key, so a hand-edited value cannot be read two ways.
        var fallback = Sessions.SessionDate.UsableLongitude(globalLongitude);
        var answers = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var (profile, entry) in Normalize(raw))
        {
            answers[profile] = entry.Longitude ?? fallback;
        }

        return (profile, instantUtc) =>
        {
            // Steps 1 and 2, from the table built above.
            var configured = answers.TryGetValue(profile ?? "", out var answer) ? answer : fallback;
            if (configured is not null)
            {
                return configured;
            }

            // Step 3, through the frame side's own member rather than a second copy of it: one
            // rule, so the two sides cannot drift the first time either is tuned. Step 4 is the
            // null this falls through to.
            return instantUtc is { } instant
                ? Sessions.SessionDate.LongitudeFromTimezone(globalTimezone, instant, load)
                : null;
        };
    }

    // A section whose header names no equipment profile passes null and resolves through the
    // global value and the unset answer only. A null profile and an empty profile are the same
    // lookup: the ASIAIR writes the profile line with nothing but a trailing space.
    private static Phd2ProfileEntry? Lookup(IReadOnlyDictionary<string, Phd2ProfileEntry> map, string? profile)
        => map.TryGetValue(profile ?? "", out var entry) ? entry : null;

    // FindSystemTimeZoneById accepts both IANA and Windows ids on Windows, so a stored
    // America/Chicago and a stored Central Standard Time both resolve. Loadability is a non-null
    // answer from ZoneLoader and nothing else: the private Loadable that used to sit a hundred
    // lines below this was the same load-and-swallow shape a second time, which is the duplication
    // design lesson 1 names (task2c-review.md finding 6).
    private static (string Zone, string Source) ResolveZone(
        Phd2ProfileEntry? entry, string? globalTimezone, Func<string, TimeZoneInfo?> load)
    {
        var own = entry?.Timezone ?? "";
        if (own.Length > 0 && load(own) is not null)
        {
            return (own, Phd2ResolutionSource.Profile);
        }

        var global = (globalTimezone ?? "").Trim();
        return global.Length > 0 && load(global) is not null
            ? (global, Phd2ResolutionSource.Global)
            : ("", Phd2ResolutionSource.Unset);
    }

    private static (double? Latitude, double? Longitude, string Source) ResolveSiteCore(
        Phd2ProfileEntry? entry, double? globalLatitude, double? globalLongitude)
    {
        var ownLatitude = entry?.Latitude;
        var ownLongitude = entry?.Longitude;
        var latitude = ownLatitude ?? globalLatitude;
        var longitude = ownLongitude ?? globalLongitude;

        var source = ownLatitude is not null || ownLongitude is not null
            ? Phd2ResolutionSource.Profile
            : latitude is not null || longitude is not null
                ? Phd2ResolutionSource.Global
                : Phd2ResolutionSource.Unset;

        return (latitude, longitude, source);
    }

    private static Phd2ProfileEntry? CoerceEntry(JsonElement value) => value.ValueKind switch
    {
        // Legacy form: the whole value is the telescope name.
        JsonValueKind.String => new Phd2ProfileEntry { Telescope = CoerceTelescope(value) },
        JsonValueKind.Object => new Phd2ProfileEntry
        {
            Telescope = CoerceTelescope(Member(value, "telescope")),
            Timezone = CoerceText(Member(value, "timezone")),
            Latitude = CoerceCoordinate(Member(value, "latitude"), LatitudeLimit),
            Longitude = CoerceCoordinate(Member(value, "longitude"), LongitudeLimit),
        },
        // A number, a bool, an array or null as the entry value is not a mapping of any kind, so
        // the entry is dropped rather than read as an empty one.
        _ => null,
    };

    private static JsonElement? Member(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) ? value : null;

    private static string CoerceText(JsonElement? value)
        => value is { ValueKind: JsonValueKind.String } ? (value.Value.GetString() ?? "").Trim() : "";

    // An empty or whitespace-only name means "not mapped", never "mapped to a telescope with no
    // name": the entry has to survive an unmapping in order to keep carrying the timezone and the
    // site, so the emptiness lives on this field.
    private static string? CoerceTelescope(JsonElement? value) => TrimmedOrNull(CoerceText(value));

    // Whether an entry holds anything besides its telescope. The coordinate tests are
    // "is not null", never truth tests: a rig at Greenwich stores longitude 0.0, and a falsy test
    // here would read its entry as empty and delete the site the user configured.
    private static bool CarriesConfiguration(Phd2ProfileEntry entry)
        => entry.Timezone.Length > 0 || entry.Latitude is not null || entry.Longitude is not null;

    private static string? TrimmedOrNull(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static double? CoerceCoordinate(JsonElement? value, double limit)
    {
        if (value is not { } element)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out var number) ? InRange(number, limit) : null;
            case JsonValueKind.String:
                var text = (element.GetString() ?? "").Trim();
                return text.Length > 0
                    && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        ? InRange(parsed, limit)
                        : null;
            default:
                // A JSON true or false is simply not a number, which is the guard the Python has to
                // write out by hand because bool subclasses int there.
                return null;
        }
    }

    private static double? CoerceCoordinate(double? value, double limit)
        => value is { } number ? InRange(number, limit) : null;

    // Out-of-range values, NaN and infinity read as unset rather than propagating: they cannot
    // describe a site, and letting one reach the sidereal coherence check would produce a
    // confident wrong verdict instead of no verdict. The bounds are inclusive: -90, 90, -180 and
    // 180 are all real places. This member covers the per-profile JSON entries and the latitude;
    // the same rule for the stored general.observer_longitude of spec 8.3 step 2 lives on
    // SessionDate.UsableLongitude, because the frame side reads that key as well.
    private static double? InRange(double number, double limit)
        => double.IsFinite(number) && number >= -limit && number <= limit ? number : null;
}
