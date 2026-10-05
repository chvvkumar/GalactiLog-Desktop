using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace GalactiLog.Core.Sessions;

// Session date derivation (spec 8.1-8.3). Port of backend/app/services/session_date.py.
public static class SessionDate
{
    // Order matters: SITELONG, then OBSLONG, then LONG-OBS. First parseable FINITE value wins.
    private static readonly string[] LongitudeHeaderKeys = { "SITELONG", "OBSLONG", "LONG-OBS" };

    // spec 8.2, verbatim port of compute_session_date. captureUtc is null whenever DATE-OBS
    // was missing or unparseable (spec 7.1.2) -- this function does not itself decide that,
    // it only propagates null through.
    public static DateOnly? Compute(DateTime? captureUtc, bool useImagingNight, double? longitude)
    {
        if (captureUtc is null) return null;
        if (!useImagingNight) return DateOnly.FromDateTime(captureUtc.Value);
        if (longitude is null) return DateOnly.FromDateTime(captureUtc.Value); // UTC-midnight fallback
        var offsetHours = 12.0 - longitude.Value / 15.0;
        var shifted = captureUtc.Value - TimeSpan.FromHours(offsetHours);
        return DateOnly.FromDateTime(shifted);
    }

    public readonly record struct LongitudeResolution(double? Longitude, bool UsedFallback);

    // spec 8.3's four-step order. rawHeaders is the frame's own ExtractedMetadata.RawHeaders
    // (JsonObject); observerLongitude/observerTimezone come from GeneralSettings; captureUtc is
    // the frame's own capture instant (needed for step 3's UTC-offset-at-that-instant lookup,
    // not "now" -- a zone's offset can differ by DST between the frame's date and today).
    // UsedFallback is true only when step 4 is reached (no longitude resolvable at all); it
    // says nothing about which of steps 1-3 actually produced a value.
    public static LongitudeResolution ResolveLongitude(
        JsonObject? rawHeaders, double? observerLongitude, string? observerTimezone, DateTime captureUtc)
    {
        // Step 1: frame's own headers, first parseable FINITE value wins. A header reading NaN,
        // Infinity or -Infinity parses, because double.TryParse accepts those three literals under
        // NumberStyles.Float, and it used to win the step and then raise ArgumentException or
        // OverflowException inside Compute's TimeSpan arithmetic, which aborts the whole scan run
        // over one file the user did not write and cannot repair. Such a value is skipped like an
        // unparseable one: the next header key is tried, then step 2.
        //
        // A FINITE value outside -180 to 180 is deliberately NOT skipped and keeps exactly the
        // behaviour it has always had. Some capture software writes site longitude in the 0 to 360
        // east convention, so reading 250 as unset would silently move real session dates.
        if (rawHeaders is not null)
        {
            foreach (var key in LongitudeHeaderKeys)
            {
                if (rawHeaders.TryGetPropertyValue(key, out var node) && node is not null
                    && double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    && double.IsFinite(v))
                {
                    return new LongitudeResolution(v, false);
                }
            }
        }

        // Step 2: configured observer longitude, through the one rule both sides read.
        if (UsableLongitude(observerLongitude) is { } configured)
        {
            return new LongitudeResolution(configured, false);
        }

        // Step 3: configured IANA timezone -> approximate longitude at captureUtc. One
        // implementation, shared with the guide side (spec 8.3, Phase 17 ruling U4).
        if (LongitudeFromTimezone(observerTimezone, captureUtc) is { } fromZone)
        {
            return new LongitudeResolution(fromZone, false);
        }

        // Step 4: nothing resolvable. Caller is responsible for the once-per-run warning via
        // OnceGate; this function is pure and carries no rate-limiting state itself.
        return new LongitudeResolution(null, true);
    }

    /// <summary>Spec 8.3 step 2's whole rule, and the only implementation of it: a stored
    /// <c>general.observer_longitude</c> counts only when it is a finite number inside the legal
    /// range of -180 to 180 degrees, the bounds included. Anything else is UNSET and is never
    /// clamped to the limit, so the rule falls through to step 3's timezone guess exactly as a null
    /// does. A value that cannot describe a site is not evidence about where the site is, and a
    /// clamp would invent one.
    /// <para>
    /// Both sides read it, the frame side in <see cref="ResolveLongitude"/> and the guide side in
    /// <c>Phd2Profiles.LongitudeResolver</c>. Two readings of the one stored key is how a frame and
    /// the guiding session of the same evening land on different <c>session_date</c> values, and
    /// every consumer of a guiding night joins on strict <c>session_date</c> equality, so that
    /// disagreement is an empty chart with no error.
    /// </para>
    /// <para>
    /// Steps 1 and 3 are deliberately NOT range coerced. A <c>SITELONG</c> header belongs to the
    /// frame and has no counterpart on the guide side, so it cannot make the two disagree (it does
    /// skip a value that is not finite, which is a narrower rule: see step 1 itself), and a
    /// zone-derived value is legitimately past a legal longitude (a UTC+14 zone answers 210, see
    /// <see cref="LongitudeFromTimezone"/>). <c>general.observer_latitude</c> needs no rule at all:
    /// latitude plays no part in the imaging-night arithmetic of spec 8.2 and this side never reads
    /// it.
    /// </para>
    /// <para>
    /// <c>ValidateGeneral</c> refuses a longitude outside the range on every save, so only a
    /// hand-edited settings document reaches this member with anything to reject.
    /// </para></summary>
    public static double? UsableLongitude(double? longitude)
        => longitude is { } value && double.IsFinite(value) && value >= -180.0 && value <= 180.0
            ? value
            : null;

    /// <summary>Spec 8.3 step 3's whole rule, and the only implementation of it: a timezone id
    /// plus an instant gives an approximate longitude of <c>utcOffsetHours * 15</c>, positive east
    /// matching <c>SITELONG</c>'s convention. The offset is taken AT <paramref name="instantUtc"/>
    /// and never at <c>DateTime.UtcNow</c>, because a zone's offset can differ by DST between that
    /// instant's date and today. Deliberately coarse (spec 8.3): a whole-hour zone offset places
    /// the noon boundary within 30 minutes of true local solar noon.
    /// <para>
    /// <paramref name="instantUtc"/> is expected to carry <see cref="DateTimeKind.Utc"/>, which
    /// every production caller supplies: the frame side from <c>AdjustToUniversal</c> and the guide
    /// side from <c>Phd2Metrics.LocalToUtc</c>. The Kind is load bearing and not a formality.
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> reads an <c>Unspecified</c> value as a
    /// wall clock IN that zone and a <c>Utc</c> value as an instant, so the same numbers give two
    /// different offsets near a transition. A value read back from SQLite arrives
    /// <c>Unspecified</c> and must be given the Kind before it reaches here.
    /// </para>
    /// <para>
    /// A null or empty id answers null, and so does a zone the platform will not load. The answer
    /// is NOT range coerced: a UTC+14 zone answers 210, which is past a legal longitude and is
    /// still the right number to hand to section 8.2's arithmetic, and coercing it here would make
    /// the frame side and the guide side disagree on exactly one class of library.
    /// </para>
    /// <para>
    /// <paramref name="load"/> exists so a caller that resolves hundreds of sections in one pass
    /// can hand its own memo (<c>Phd2Profiles.ZoneLoader</c>) instead of hitting the platform zone
    /// database once per section. The frame side passes nothing and keeps the per-call lookup it
    /// has always had: it resolves a longitude once per frame and nobody has measured it.
    /// </para></summary>
    public static double? LongitudeFromTimezone(
        string? timezoneId, DateTime instantUtc, Func<string, TimeZoneInfo?>? load = null)
    {
        if (string.IsNullOrEmpty(timezoneId))
        {
            return null;
        }

        var zone = (load ?? LoadSystemZone)(timezoneId);
        return zone is null ? null : zone.GetUtcOffset(instantUtc).TotalHours * 15.0;
    }

    // The two catches step 3 has always carried: a misspelled or unknown id and a corrupt zone
    // entry both read as "no longitude", never as a crash in the middle of a scan.
    private static TimeZoneInfo? LoadSystemZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    // Exact text ported from session_date.warn_imaging_night_fallback's docstring/message
    // (spec 8.3 step 4). Callers (Task 4's ScanWriter/reader) log this via ILogger.LogWarning
    // at most once per scan run, gated by their own OnceGate instance.
    public const string ImagingNightFallbackWarning =
        "use_imaging_night is enabled but no longitude is resolvable (no SITELONG/OBSLONG/" +
        "LONG-OBS header and no observer_longitude configured); falling back to UTC-midnight " +
        "session date grouping. Set observer_longitude in Settings for imaging-night grouping " +
        "to take effect.";
}
