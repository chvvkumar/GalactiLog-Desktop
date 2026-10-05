using System;
using System.Collections.Concurrent;

namespace GalactiLog.Core.Sessions;

// spec 8.4, port of backend/app/services/astro_night.py.
//
// Spec 18.1, verbatim: "A failure here is almost always a degrees-versus-radians error in
// section 8.4." Every trig call in SunAltitudeDegrees below takes a Rad(...) result and every
// comparison against a degree threshold takes a Deg(...) result, per spec 8.4's unit
// convention paragraph, which states there are no implicit conversions anywhere in this
// pseudocode.
/// <summary>
/// Spec 8.4's astronomical night hours: the interval during which the sun's altitude is below
/// -18 degrees, sampled every 10 minutes across a 24 hour window starting at 12:00 UTC.
/// </summary>
public static class AstroNight
{
    /// <summary>Spec 8.4's sample count: one every 10 minutes across 24 hours.</summary>
    public const int SamplesPerDay = 144;

    /// <summary>24.0 / <see cref="SamplesPerDay"/>, so one sample is 1/6 hour.</summary>
    public const double SampleIntervalHours = 24.0 / SamplesPerDay;

    /// <summary>Spec 8.4's definition of astronomical night.</summary>
    public const double AstronomicalTwilightDegrees = -18.0;

    // Q2 (P9 RULINGS): the per-night value is memoized too, in the same unbounded style as
    // the two aggregates. The Python source only caches the aggregates because it precomputes
    // every night into a site_dark_hours table (spec 5.14 lists that table as deliberately
    // absent from this port), so the per-night function here is the one that actually gets
    // called per date and is worth memoizing.
    //
    // Three separate dictionaries, one per granularity, rather than one shared key space with
    // a discriminator: a month and a week can carry the same "period" number (month 3, week 3)
    // and a shared key space would invite a collision bug no test would catch.
    //
    // Spec 8.4: "a plain unbounded dictionary is fine here because the key space is bounded by
    // the user's catalogue span" -- so no eviction and no size cap on any of the three.
    //
    // Latitude and longitude are part of each key by exact double equality. That is correct
    // here: both values come from one settings document and are the same two doubles on every
    // call within a process, so the key is never rounded.
    private static readonly ConcurrentDictionary<NightKey, double> NightCache = new();
    private static readonly ConcurrentDictionary<MonthKey, double> MonthCache = new();
    private static readonly ConcurrentDictionary<WeekKey, double> WeekCache = new();

    private readonly record struct NightKey(DateOnly Date, double LatitudeDeg, double LongitudeDeg);
    private readonly record struct MonthKey(int Year, int Month, double LatitudeDeg, double LongitudeDeg);
    private readonly record struct WeekKey(int Year, int IsoWeek, double LatitudeDeg, double LongitudeDeg);

    /// <summary>
    /// Hours of astronomical night for one date at one observer position. Memoized on
    /// (date, latitude, longitude); see the class remarks on the memo dictionaries.
    /// </summary>
    public static double DarkHoursForNight(DateOnly date, double latitudeDeg, double longitudeDeg)
    {
        var key = new NightKey(date, latitudeDeg, longitudeDeg);
        return NightCache.GetOrAdd(key, static k => ComputeDarkHoursForNight(k.Date, k.LatitudeDeg, k.LongitudeDeg));
    }

    private static double ComputeDarkHoursForNight(DateOnly date, double latitudeDeg, double longitudeDeg)
    {
        var start = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Utc);
        var darkCount = 0;
        for (var i = 0; i < SamplesPerDay; i++)
        {
            var t = start.AddHours(i * SampleIntervalHours);
            // Strictly less than, matching Python's np.sum(chunk < -18.0). No rounding here:
            // the per-night value is already quantized to 1/6 hour and the aggregates below
            // are where spec 8.4 applies round(..., 1).
            if (SunAltitudeDegrees(t, latitudeDeg, longitudeDeg) < AstronomicalTwilightDegrees) darkCount++;
        }

        return darkCount * SampleIntervalHours;
    }

    /// <summary>How finely <see cref="NightBounds"/> resolves each twilight crossing. Spec 8.4's
    /// 10 minute grid decides the duration; this decides the two edges.</summary>
    public static readonly TimeSpan NightBoundsResolution = TimeSpan.FromMinutes(1);

    /// <summary>Spec 8.4's astronomical night as an interval rather than a duration: the first and
    /// last instants of the 24 hour window from 12:00 local in <paramref name="zone"/> on
    /// <paramref name="sessionDate"/> at which the sun is below -18 degrees, as local wall-clock
    /// times in that same zone.</summary>
    /// <remarks>
    /// <para>
    /// Spec 8.4's threshold and 10 minute grid are unchanged, and <see cref="DarkHoursForNight"/>
    /// is untouched: this method answers a different question over the same altitude function.
    /// The one deliberate difference is the window's anchor. The duration counts dark samples, so
    /// a 24 hour window at any phase yields the same total and spec 8.4's 12:00 UTC anchor is
    /// correct there. An interval is not phase-independent: a 12:00 UTC window cuts the local
    /// night in two at every site whose local midnight falls near the window's edge, which is
    /// most of the Americas outside midsummer and all of Australia and east Asia year round, and
    /// the first dark sample to the last dark sample then spans almost the whole 24 hours.
    /// Anchoring the window on 12:00 local in <paramref name="zone"/> puts the night in the
    /// middle of the window at every site whose zone is its own, so the dark samples are one
    /// contiguous run and the two edges are the night's own dusk and dawn.
    /// </para>
    /// <para>
    /// <paramref name="zone"/> is expected to be the observer's own zone, which is what the
    /// application passes. It need not be: spec 5.8 keeps the display zone and the observer
    /// location as two independent settings, and a zone hours away from the site's longitude can
    /// still split the night across the window's edge. The band is then the longest dark run
    /// inside the window rather than the span of all of them, which costs the part of the night
    /// that falls outside the window and never paints a lit noon as night.
    /// </para>
    /// <para>
    /// The coarse pass is spec 8.4's own 10 minute grid; each of the two crossings is then
    /// bisected against its lit neighbour to one minute, because a 10 minute edge is about 14
    /// pixels of band on the night strip and the roadmap's verification bar is one minute.
    /// </para>
    /// <para>
    /// Null in two cases the caller must render identically as "no band": either coordinate is
    /// null (spec 5.8 leaves the observer location unset by default), or no sample of the window
    /// is dark (a polar summer, which is a correct answer and not a failure).
    /// </para>
    /// <para>
    /// The returned values carry <see cref="DateTimeKind.Unspecified"/> and are local wall-clock
    /// times in <paramref name="zone"/>, so the caller compares them with a frame's capture time
    /// converted through the same zone by <c>SessionTimeFormat</c> and never mixes an offset in.
    /// </para>
    /// <para>
    /// Deliberately not memoized, which is the one place this method departs from its three
    /// neighbours. It is called once per selected session, at most a few dozen times in a session
    /// of use, and each call is 144 altitude evaluations plus about 20 bisection steps. A fourth
    /// cache would buy nothing and add a fourth key space to keep correct.
    /// </para>
    /// </remarks>
    public static (DateTime Dusk, DateTime Dawn)? NightBounds(
        DateOnly sessionDate, double? latitudeDeg, double? longitudeDeg, TimeZoneInfo zone)
    {
        if (latitudeDeg is not { } lat || longitudeDeg is not { } lon)
        {
            return null;
        }

        // 12:00 local in zone, expressed as the UTC instant the sampling walks from. See the
        // remarks above for why the anchor is local here and stays UTC in DarkHoursForNight.
        var localNoon = new DateTime(
            sessionDate.Year, sessionDate.Month, sessionDate.Day, 12, 0, 0, DateTimeKind.Unspecified);
        // No real zone moves its clocks at noon, so this branch is unreachable for every zone in
        // the host's database; a hand-built custom zone could, and ConvertTimeToUtc throws on a
        // local time that does not exist rather than returning a band.
        var start = zone.IsInvalidTime(localNoon)
            ? TimeZoneInfo.ConvertTimeToUtc(localNoon.AddHours(1.0), zone).AddHours(-1.0)
            : TimeZoneInfo.ConvertTimeToUtc(localNoon, zone);
        // The longest contiguous run of dark samples, not the span from the first dark sample to
        // the last. With the local anchor above the two are the same thing at every site whose
        // zone is its own: there is one run and it sits in the middle of the window. They differ
        // only when the zone is hours away from the site's longitude, which spec 5.8 permits
        // because the display zone and the observer location are two independent settings and
        // "UTC" is a reasonable thing for an observer to display. Taking the run rather than the
        // span makes that case a band that is short by the part of the night outside the window
        // instead of a band that covers the whole day. Ties go to the earlier run, so the result
        // is deterministic.
        var bestStart = -1;
        var bestLength = 0;
        var runStart = -1;
        for (var i = 0; i < SamplesPerDay; i++)
        {
            // Strictly less than, matching ComputeDarkHoursForNight, so the interval and the
            // duration count the same samples as dark.
            if (SunAltitudeDegrees(SampleAt(start, i), lat, lon) >= AstronomicalTwilightDegrees)
            {
                runStart = -1;
                continue;
            }

            if (runStart < 0)
            {
                runStart = i;
            }

            if (i - runStart + 1 > bestLength)
            {
                bestLength = i - runStart + 1;
                bestStart = runStart;
            }
        }

        if (bestLength == 0)
        {
            return null;
        }

        var first = bestStart;
        var last = bestStart + bestLength - 1;

        // A run that touches an edge of the window has no crossing to refine on that side: the
        // band simply runs to the edge. With a local anchor and the site's own zone the sun is up
        // at both edges, so these two branches are reached only by a polar winter, where every
        // sample is dark and both are taken at once, and by the mismatched-zone case above.
        // NightBounds_InAPolarWinter_FillsTheWholeWindow is the case that reaches them.
        var duskUtc = first == 0
            ? start
            : RefineCrossing(SampleAt(start, first - 1), SampleAt(start, first), lat, lon);
        var dawnUtc = last == SamplesPerDay - 1
            ? start.AddHours(24.0)
            : RefineCrossing(SampleAt(start, last + 1), SampleAt(start, last), lat, lon);

        // ConvertTimeFromUtc returns Utc for TimeZoneInfo.Utc and Local for TimeZoneInfo.Local,
        // so the Kind is forced here: the contract above is a wall-clock value with no offset of
        // its own, and Tasks 3 and 5 compare it with a capture time already converted through the
        // same zone.
        return (
            DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(duskUtc, zone), DateTimeKind.Unspecified),
            DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(dawnUtc, zone), DateTimeKind.Unspecified));
    }

    private static DateTime SampleAt(DateTime start, int index) => start.AddHours(index * SampleIntervalHours);

    // Both ends are UTC instants; darkAt is the end whose altitude is below the threshold. Halves
    // the bracket until it is under NightBoundsResolution, then returns the dark end, so the band
    // never claims a minute that is not dark. litAt may be either before or after darkAt: dusk
    // brackets forwards and dawn backwards, and the midpoint arithmetic is the same both ways.
    private static DateTime RefineCrossing(
        DateTime litAt, DateTime darkAt, double latitudeDeg, double longitudeDeg)
    {
        while ((darkAt - litAt).Duration() >= NightBoundsResolution)
        {
            var middle = litAt + ((darkAt - litAt) / 2);
            if (SunAltitudeDegrees(middle, latitudeDeg, longitudeDeg) < AstronomicalTwilightDegrees)
            {
                darkAt = middle;
            }
            else
            {
                litAt = middle;
            }
        }

        return darkAt;
    }

    /// <summary>Sum of the per-night values for every day of the month, rounded to one
    /// decimal. Memoized on (year, month, latitude, longitude).</summary>
    public static double DarkHoursForMonth(int year, int month, double latitudeDeg, double longitudeDeg)
    {
        var key = new MonthKey(year, month, latitudeDeg, longitudeDeg);
        return MonthCache.GetOrAdd(key, static k => ComputeDarkHoursForMonth(k.Year, k.Month, k.LatitudeDeg, k.LongitudeDeg));
    }

    private static double ComputeDarkHoursForMonth(int year, int month, double latitudeDeg, double longitudeDeg)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var sum = 0.0;
        for (var day = 1; day <= daysInMonth; day++)
        {
            sum += DarkHoursForNight(new DateOnly(year, month, day), latitudeDeg, longitudeDeg);
        }

        // Python's round(x, 1) is banker's rounding on a tie, and so is Math.Round's default
        // MidpointRounding.ToEven, so the default overload is the matching one. Do not pass
        // MidpointRounding.AwayFromZero.
        return Math.Round(sum, 1);
    }

    /// <summary>Sum of the per-night values for the seven days of the ISO week, rounded to one
    /// decimal. Memoized on (year, week, latitude, longitude).</summary>
    public static double DarkHoursForWeek(int year, int isoWeek, double latitudeDeg, double longitudeDeg)
    {
        var key = new WeekKey(year, isoWeek, latitudeDeg, longitudeDeg);
        return WeekCache.GetOrAdd(key, static k => ComputeDarkHoursForWeek(k.Year, k.IsoWeek, k.LatitudeDeg, k.LongitudeDeg));
    }

    private static double ComputeDarkHoursForWeek(int year, int isoWeek, double latitudeDeg, double longitudeDeg)
    {
        var monday = IsoWeekMonday(year, isoWeek);
        var sum = 0.0;
        for (var i = 0; i < 7; i++)
        {
            sum += DarkHoursForNight(monday.AddDays(i), latitudeDeg, longitudeDeg);
        }

        return Math.Round(sum, 1);
    }

    /// <summary>The sun's altitude in degrees at a UTC instant. Public because the spec 18.1
    /// test asserts the -18 degree crossing directly against the USNO rows.</summary>
    public static double SunAltitudeDegrees(DateTime utc, double latitudeDeg, double longitudeDeg)
    {
        var n = JulianDate(utc) - 2451545.0; // days since J2000.0

        var lMeanDeg = Mod360(280.460 + 0.9856474 * n); // mean longitude, degrees
        var gMeanDeg = Mod360(357.528 + 0.9856003 * n); // mean anomaly, degrees

        var lamDeg = lMeanDeg
            + 1.915 * Math.Sin(Rad(gMeanDeg))
            + 0.020 * Math.Sin(Rad(2.0 * gMeanDeg)); // ecliptic longitude, degrees
        var epsDeg = 23.439 - 0.0000004 * n; // obliquity, degrees

        var lamRad = Rad(lamDeg);
        var epsRad = Rad(epsDeg);

        var raRad = Math.Atan2(Math.Cos(epsRad) * Math.Sin(lamRad), Math.Cos(lamRad)); // right ascension, radians
        var decRad = Math.Asin(Math.Sin(epsRad) * Math.Sin(lamRad)); // declination, radians

        var gmstHours = Mod24(18.697374558 + 24.06570982441908 * n); // Greenwich sidereal, hours
        var lstDeg = Mod360(gmstHours * 15.0 + longitudeDeg); // local sidereal, degrees
        var haDeg = Mod360(lstDeg - Deg(raRad)); // hour angle, degrees

        var latRad = Rad(latitudeDeg);
        var haRad = Rad(haDeg);

        var altRad = Math.Asin(Math.Sin(latRad) * Math.Sin(decRad)
            + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(haRad)); // altitude, radians
        return Deg(altRad); // altitude, degrees
    }

    /// <summary>The Monday of the given ISO week, by the arithmetic in
    /// <c>astro_night.dark_hours_for_week</c>. Internal so the test can pin it.</summary>
    internal static DateOnly IsoWeekMonday(int year, int isoWeek)
    {
        var jan4 = new DateOnly(year, 1, 4);
        // DayOfWeek.Monday is 1 in .NET and 0 in Python's date.weekday(); the offset below is
        // Python's: Monday -> 0, Sunday -> 6.
        var offset = ((int)jan4.DayOfWeek + 6) % 7;
        var week1 = jan4.AddDays(-offset);
        return week1.AddDays(7 * (isoWeek - 1));
    }

    // ((x % m) + m) % m. C#'s % on a negative operand returns a negative value, and
    // n = julianDate(utc) - 2451545.0 is negative for any date before 2000, so the plain
    // operator is not sufficient. Covered by Mod360_IsNonNegative_ForANegativeOperand and
    // Mod24_IsNonNegative_ForANegativeOperand: trig is 360-degree periodic, so a raw % here
    // does not perturb any SunAltitudeDegrees result and cannot be caught by an altitude or
    // dark-hours assertion, only by these two non-negativity checks directly on Mod360/Mod24.
    internal static double Mod360(double x) => ((x % 360.0) + 360.0) % 360.0;

    // Same reasoning as Mod360, for the 24 hour sidereal-time modulus.
    internal static double Mod24(double x) => ((x % 24.0) + 24.0) % 24.0;

    // utc.ToOADate() + 2415018.5, per spec 8.4, exact for the DateTime range this application
    // sees. ToOADate is defined in terms of the instant's own value, so the argument must be a
    // UTC DateTime. Q3 (P9 RULINGS): a Local value is converted with ToUniversalTime();
    // Unspecified is treated as UTC, matching spec 7.1.2's convention for an offsetless
    // DATE-OBS. This does not throw on a non-UTC Kind, because every caller in this
    // application already holds UTC.
    internal static double JulianDate(DateTime utc)
    {
        var value = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        return value.ToOADate() + 2415018.5;
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;
    private static double Deg(double rad) => rad * 180.0 / Math.PI;
}
