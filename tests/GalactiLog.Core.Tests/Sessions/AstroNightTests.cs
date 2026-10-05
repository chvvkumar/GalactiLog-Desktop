using System;
using System.Collections.Generic;
using System.Globalization;
using GalactiLog.Core.Sessions;
using Xunit;

namespace GalactiLog.Core.Tests.Sessions;

public class AstroNightTests
{
    // The three USNO reference rows from spec 18.1. Latitude, longitude and date are hardcoded
    // exactly as the spec prints them. Dusk/dawn are given as full UTC instants (the "(Jun 22)"
    // markers in the spec table are folded into the date here) so both the crossing tests and
    // the interval test share one source of truth.
    //
    // DEVIATION (spec 18.1, non-observable: test data only, does not change AstroNight.cs).
    // Two of the six printed times do not survive a faithful, line-by-line transcription of
    // spec 8.4's pseudocode, and were corrected here rather than silently left as printed. The
    // pseudocode transcription was checked three ways before concluding the table, not the
    // code, was wrong: (1) every line of SunAltitudeDegrees matches spec 8.4 side by side; (2)
    // internal consistency: for the Greenwich row the computed local solar transit lands at
    // 12:07 UTC, matching the US Naval Observatory's own published "Upper Transit" time for
    // that date and coordinates to within a minute, and the sidereal-vs-solar rate the formula
    // produces between the two crossings matches the theoretical ~1.002738 ratio; (3) an
    // independent third-party astronomical twilight table (stjerneskinn.com, which cites the
    // same -18 degree definition spec 8.4 uses) gives Greenwich astronomical dawn on 2025-03-20
    // as 04:09 UTC and Sydney astronomical dusk on 2025-12-21 as 10:50 UTC, both within a few
    // minutes of this implementation's output and both far outside spec 18.1's 10 minute
    // tolerance from the printed 04:26 and 10:20. The other four of six printed times (Greenwich
    // dusk, Sydney dawn, and both Flagstaff times) matched this implementation within a few
    // minutes and are left exactly as printed. Ruled on: docs/superpowers/progress.md, the "P9
    // Task 1" ledger entry, records both corrections as ACCEPTED (independently reverified with
    // a NOAA-formula computation); design-spec.md 18.1's table is corrected at phase close by
    // the fixer, so this is not an open question.
    public static IEnumerable<object[]> UsnoRows()
    {
        yield return new object[]
        {
            51.4778, -0.0015, new DateOnly(2025, 3, 20),
            new DateTime(2025, 3, 20, 20, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 3, 21, 4, 9, 0, DateTimeKind.Utc), // corrected from spec's 04:26; see DEVIATION above
        };
        yield return new object[]
        {
            35.1983, -111.6513, new DateOnly(2025, 6, 21),
            new DateTime(2025, 6, 22, 4, 34, 0, DateTimeKind.Utc),
            new DateTime(2025, 6, 22, 10, 20, 0, DateTimeKind.Utc),
        };
        yield return new object[]
        {
            -33.8688, 151.2093, new DateOnly(2025, 12, 21),
            new DateTime(2025, 12, 21, 10, 50, 0, DateTimeKind.Utc), // corrected from spec's 10:20; see DEVIATION above
            new DateTime(2025, 12, 21, 17, 0, 0, DateTimeKind.Utc),
        };
    }

    // Walks one-minute samples across a window around the expected time and returns the
    // instant the sign of (altitude + 18) changes. Spec 18.1's wording is "crosses -18 degrees
    // within tolerance of each listed time", so the test asserts the crossing, not the
    // altitude at the listed time.
    private static DateTime FindCrossingUtc(DateTime windowStart, DateTime windowEnd, double latitudeDeg, double longitudeDeg)
    {
        var previousSign = Math.Sign(AstroNight.SunAltitudeDegrees(windowStart, latitudeDeg, longitudeDeg) - AstroNight.AstronomicalTwilightDegrees);
        for (var t = windowStart.AddMinutes(1); t <= windowEnd; t = t.AddMinutes(1))
        {
            var sign = Math.Sign(AstroNight.SunAltitudeDegrees(t, latitudeDeg, longitudeDeg) - AstroNight.AstronomicalTwilightDegrees);
            if (sign != previousSign)
            {
                return t;
            }

            previousSign = sign;
        }

        throw new InvalidOperationException("No -18 degree crossing found in the search window.");
    }

    [Theory]
    [MemberData(nameof(UsnoRows))]
    public void SunAltitude_CrossesMinus18_WithinTenMinutesOfTheUsnoDusk(
        double latitudeDeg, double longitudeDeg, DateOnly nightDate, DateTime dusk, DateTime dawn)
    {
        _ = nightDate;
        _ = dawn;
        var crossing = FindCrossingUtc(dusk.AddMinutes(-30), dusk.AddMinutes(30), latitudeDeg, longitudeDeg);
        Assert.True(Math.Abs((crossing - dusk).TotalMinutes) <= 10.0,
            $"expected crossing within 10 minutes of {dusk:O}, found {crossing:O}");
    }

    [Theory]
    [MemberData(nameof(UsnoRows))]
    public void SunAltitude_CrossesMinus18_WithinTenMinutesOfTheUsnoDawn(
        double latitudeDeg, double longitudeDeg, DateOnly nightDate, DateTime dusk, DateTime dawn)
    {
        _ = nightDate;
        _ = dusk;
        var crossing = FindCrossingUtc(dawn.AddMinutes(-30), dawn.AddMinutes(30), latitudeDeg, longitudeDeg);
        Assert.True(Math.Abs((crossing - dawn).TotalMinutes) <= 10.0,
            $"expected crossing within 10 minutes of {dawn:O}, found {crossing:O}");
    }

    [Theory]
    [MemberData(nameof(UsnoRows))]
    public void DarkHoursForNight_MatchesTheUsnoInterval_WithinTwentyMinutes(
        double latitudeDeg, double longitudeDeg, DateOnly nightDate, DateTime dusk, DateTime dawn)
    {
        var expectedHours = (dawn - dusk).TotalHours;
        var actualHours = AstroNight.DarkHoursForNight(nightDate, latitudeDeg, longitudeDeg);

        // 20 minutes (0.334 h): the function counts 10 minute samples and can carry up to one
        // sample of error at each end, exactly as spec 18.1 states and explains.
        Assert.True(Math.Abs(actualHours - expectedHours) <= 20.0 / 60.0,
            $"expected {expectedHours:F3}h, got {actualHours:F3}h");
    }

    [Fact]
    public void Mod360_IsNonNegative_ForANegativeOperand()
    {
        // A raw % returns a negative value here; the plain operator is not sufficient.
        Assert.True(AstroNight.Mod360(-10.0) >= 0.0);
        Assert.Equal(350.0, AstroNight.Mod360(-10.0), 9);
    }

    [Fact]
    public void Mod24_IsNonNegative_ForANegativeOperand()
    {
        Assert.True(AstroNight.Mod24(-1.0) >= 0.0);
        Assert.Equal(23.0, AstroNight.Mod24(-1.0), 9);
    }

    [Fact]
    public void SunAltitude_ForADateBefore2000_IsFinite_AndInRange()
    {
        // n = julianDate(utc) - 2451545.0 is negative here, exercising Mod360/Mod24's
        // negative-operand path, and guards against NaN or an out-of-range altitude for a
        // pre-2000 date. It does not by itself catch a raw % in Mod360/Mod24: trig is
        // 360-degree periodic and the 24 hour sidereal wrap is exactly a 360 degree wrap, so a
        // raw % leaves every SunAltitudeDegrees value unchanged. That defect is caught only by
        // Mod360_IsNonNegative_ForANegativeOperand and Mod24_IsNonNegative_ForANegativeOperand.
        var utc = new DateTime(1990, 1, 15, 3, 0, 0, DateTimeKind.Utc);
        var altitude = AstroNight.SunAltitudeDegrees(utc, 51.4778, -0.0015);

        Assert.False(double.IsNaN(altitude));
        Assert.InRange(altitude, -90.0, 90.0);
    }

    [Fact]
    public void DarkHoursForNight_ForADateBefore2000_IsBetweenZeroAndTwentyFour()
    {
        var hours = AstroNight.DarkHoursForNight(new DateOnly(1990, 1, 15), 51.4778, -0.0015);
        Assert.InRange(hours, 0.0, 24.0);
    }

    [Fact]
    public void DarkHoursForNight_AtTheEquator_IsWithinASanityBand()
    {
        // DEVIATION (task1.md's Tests section named a ten-to-twelve-hour band; corrected here,
        // test data only). The true value near an equinox at the equator is 9.6667 h (58
        // samples), which sits half an hour below the brief's stated lower bound: the brief's
        // figure was itself an astronomy error of the same class as the polar-winter latitude
        // below. The band is widened to 9.5..13.0 and the test is named for the band it
        // actually asserts, not for the brief's incorrect one. Still a sanity band, not an
        // exactness assertion.
        var hours = AstroNight.DarkHoursForNight(new DateOnly(2025, 3, 20), 0.0, 0.0);
        Assert.InRange(hours, 9.5, 13.0);
    }

    [Fact]
    public void DarkHoursForNight_InPolarSummer_IsZero()
    {
        // Latitude +78, 2025-06-21: no sample is below -18 degrees.
        var hours = AstroNight.DarkHoursForNight(new DateOnly(2025, 6, 21), 78.0, 0.0);
        Assert.Equal(0.0, hours, 9);
    }

    [Fact]
    public void DarkHoursForNight_InPolarWinter_IsTheWholeDay()
    {
        // DEVIATION (spec 18.1's task brief named latitude +78; corrected here, test data
        // only). At the December solstice the sun's peak (culmination) altitude at latitude L
        // is 90 - |L - (-23.44)|. At L=78 that is 90 - 101.44 = -11.44 degrees, which is above
        // -18: latitude 78 has a multi-hour astronomical-twilight window around local noon and
        // is not continuously dark, so it cannot assert 24.0 within one sample. The threshold
        // for a fully dark day is L >= 90 - 18 - 23.44 = 84.56 degrees. Latitude 88 clears that
        // threshold with margin (peak altitude -21.44 degrees, well below -18).
        var hours = AstroNight.DarkHoursForNight(new DateOnly(2025, 12, 21), 88.0, 0.0);
        Assert.True(Math.Abs(hours - 24.0) <= AstroNight.SampleIntervalHours);
    }

    [Theory]
    [InlineData(2025, 1, 1)]
    [InlineData(2025, 6, 15)]
    [InlineData(2025, 9, 30)]
    [InlineData(1998, 11, 3)]
    public void DarkHoursForNight_IsAlwaysAMultipleOfTheSampleInterval(int year, int month, int day)
    {
        var hours = AstroNight.DarkHoursForNight(new DateOnly(year, month, day), 35.1983, -111.6513);
        var samples = hours / AstroNight.SampleIntervalHours;
        Assert.Equal(Math.Round(samples), samples, 6);
    }

    [Fact]
    public void DarkHoursForMonth_EqualsTheSumOfItsNights_RoundedToOneDecimal()
    {
        const int year = 2025;
        const int month = 6;
        const double lat = 35.1983;
        const double lon = -111.6513;

        var expected = 0.0;
        for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
        {
            expected += AstroNight.DarkHoursForNight(new DateOnly(year, month, day), lat, lon);
        }

        expected = Math.Round(expected, 1);
        var actual = AstroNight.DarkHoursForMonth(year, month, lat, lon);

        Assert.Equal(expected, actual, 9);
    }

    [Fact]
    public void DarkHoursForNight_IsMemoized_AndReturnsTheSameValue()
    {
        // Q2's NightCache (P9 RULINGS): the per-night value is memoized in the same style as
        // the two aggregates, so it gets its own repeat-call pin here.
        var first = AstroNight.DarkHoursForNight(new DateOnly(2025, 7, 15), -33.8688, 151.2093);
        var second = AstroNight.DarkHoursForNight(new DateOnly(2025, 7, 15), -33.8688, 151.2093);

        Assert.Equal(first, second, 12);
    }

    [Fact]
    public void DarkHoursForMonth_IsMemoized_AndReturnsTheSameValue()
    {
        var first = AstroNight.DarkHoursForMonth(2025, 7, -33.8688, 151.2093);
        var second = AstroNight.DarkHoursForMonth(2025, 7, -33.8688, 151.2093);

        Assert.Equal(first, second, 12);
    }

    [Fact]
    public void DarkHoursForWeek_EqualsTheSumOfItsSevenNights_RoundedToOneDecimal()
    {
        const int year = 2025;
        const int isoWeek = 20;
        const double lat = 51.4778;
        const double lon = -0.0015;

        var monday = AstroNight.IsoWeekMonday(year, isoWeek);
        var expected = 0.0;
        for (var i = 0; i < 7; i++)
        {
            expected += AstroNight.DarkHoursForNight(monday.AddDays(i), lat, lon);
        }

        expected = Math.Round(expected, 1);
        var actual = AstroNight.DarkHoursForWeek(year, isoWeek, lat, lon);

        Assert.Equal(expected, actual, 9);
    }

    [Fact]
    public void IsoWeekMonday_Week1Of2026_IsTheMondayContainingJanuary4()
    {
        var monday = AstroNight.IsoWeekMonday(2026, 1);
        var jan4 = new DateOnly(2026, 1, 4);

        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        Assert.InRange(jan4.DayNumber - monday.DayNumber, 0, 6);
    }

    [Theory]
    [InlineData(2021)] // Jan 4 is a Monday
    [InlineData(2025)] // Jan 4 is a Saturday
    [InlineData(2027)] // Jan 4 is a Monday
    public void IsoWeekMonday_MatchesIsoWeekToDateTime_AcrossSeveralWeeks(int year)
    {
        foreach (var week in new[] { 1, 10, 26, 40, 52 })
        {
            var expected = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday));
            var actual = AstroNight.IsoWeekMonday(year, week);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void DarkHoursForWeek_AcrossAYearBoundary_UsesTheIsoRule()
    {
        // Find a year whose ISO week 1 begins in December of the previous year: this happens
        // whenever 1 January falls on a Friday, Saturday or Sunday. Searched rather than
        // hardcoded so the test does not depend on a manually worked out calendar fact.
        int? boundaryYear = null;
        for (var year = 2020; year <= 2035; year++)
        {
            if (AstroNight.IsoWeekMonday(year, 1).Year == year - 1)
            {
                boundaryYear = year;
                break;
            }
        }

        Assert.True(boundaryYear.HasValue, "expected at least one ISO year-boundary case in 2020..2035");

        var monday = AstroNight.IsoWeekMonday(boundaryYear!.Value, 1);
        Assert.Equal(12, monday.Month);
        Assert.Equal(boundaryYear.Value - 1, monday.Year);

        // Cross-check against the framework's independent ISO week implementation, which
        // catches the weekday() offset defect the way spec 18.1 intends.
        var expected = DateOnly.FromDateTime(ISOWeek.ToDateTime(boundaryYear.Value, 1, DayOfWeek.Monday));
        Assert.Equal(expected, monday);

        var hours = AstroNight.DarkHoursForWeek(boundaryYear.Value, 1, 51.4778, -0.0015);
        Assert.InRange(hours, 0.0, 24.0 * 7);
    }

    // DarkHoursForNight_IsIndependentOfLongitudeSign_AtTheSameAbsoluteLatitude is deliberately
    // not written: spec 18.1's window is a 24 hour span at 12:00 UTC, which makes the total
    // nearly longitude-independent at moderate latitudes but not exactly so, so that assertion
    // would be invalid.

    // ---- NightBounds (P12 Task 2, ruling Q7) -------------------------------------------
    //
    // The single-site cases below keep the Flagstaff pair the dark-hours cases already use, so
    // the suite never has to explain why the bounds and the duration were measured at two
    // different places. The five-row block that follows them is the P12 review's own site and
    // date list, which exists because one site cannot see the defect ruling P1-1 fixed: a window
    // anchored on 12:00 UTC splits the local night in two at most sites and most dates, and
    // first-dark to last-dark then spans almost the whole 24 hours.

    private const double BoundsLatitude = 35.1983;
    private const double BoundsLongitude = -111.6513;
    private static readonly DateOnly BoundsDate = new(2025, 6, 21);

    // A zone with a fixed offset and no daylight rule, so the wall-clock assertions below read
    // the same on any machine and in any year. FindSystemTimeZoneById would bring the host's own
    // time zone database and its daylight rules into a test about twilight.
    private static TimeZoneInfo FixedZone(double offsetHours)
        => TimeZoneInfo.CreateCustomTimeZone(
            $"p12-t2-{offsetHours:0.##}",
            TimeSpan.FromHours(offsetHours),
            $"p12-t2-{offsetHours:0.##}",
            $"p12-t2-{offsetHours:0.##}");

    // The zone used below is UTC, so an Unspecified wall-clock value from NightBounds is the UTC
    // instant itself and can be handed straight to SunAltitudeDegrees.
    private static double Altitude(DateTime wallClockUtc)
        => AstroNight.SunAltitudeDegrees(
            DateTime.SpecifyKind(wallClockUtc, DateTimeKind.Utc),
            BoundsLatitude,
            BoundsLongitude);

    [Fact]
    public void NightBounds_ForAKnownDateAndSite_MatchesTheSpecEightFourSamples()
    {
        // The roadmap's bar is "within one minute of spec 8.4". Asserted as a property rather
        // than as two hardcoded clock times: both returned instants are below -18 degrees, and
        // one minute outside each of them the sun is not, which is what "resolved to one minute"
        // means and what a future bisection tweak must keep true.
        var bounds = AstroNight.NightBounds(BoundsDate, BoundsLatitude, BoundsLongitude, TimeZoneInfo.Utc);

        Assert.NotNull(bounds);
        var (dusk, dawn) = bounds.Value;
        Assert.True(dusk < dawn);

        Assert.True(Altitude(dusk) < AstroNight.AstronomicalTwilightDegrees, $"dusk {dusk:O} is not dark");
        Assert.True(Altitude(dawn) < AstroNight.AstronomicalTwilightDegrees, $"dawn {dawn:O} is not dark");
        Assert.True(
            Altitude(dusk.AddMinutes(-1)) >= AstroNight.AstronomicalTwilightDegrees,
            $"one minute before dusk {dusk:O} is already dark");
        Assert.True(
            Altitude(dawn.AddMinutes(1)) >= AstroNight.AstronomicalTwilightDegrees,
            $"one minute after dawn {dawn:O} is still dark");
    }

    // The P12 review's five site and date rows, the list ruling P1-1 requires the fix to be
    // proved against. Two of them (Berlin in January, Flagstaff at midsummer) are the rows where
    // the night already fell in one piece inside a 12:00 UTC window; the other three are rows
    // where it did not, and where the pre-fix method returned a band of 23.83 hours.
    //
    // The offsets are the fixed offsets those zones were on for those dates: America/Phoenix is
    // UTC-7 all year, Australia/Sydney is UTC+11 on 2025-01-15 and Europe/Berlin is UTC+1 on
    // 2025-01-15. FixedZone rather than FindSystemTimeZoneById for the reason stated there: a
    // twilight test must not depend on the host's time zone database, and none of these five
    // dates is near a daylight transition, so the fixed offset is the zone's own offset.
    public static IEnumerable<object[]> BoundsRows()
    {
        // label, latitude, longitude, UTC offset in hours, session date
        yield return new object[] { "Flagstaff 2025-06-21", 35.20, -111.65, -7.0, new DateOnly(2025, 6, 21) };
        yield return new object[] { "Flagstaff 2025-01-15", 35.20, -111.65, -7.0, new DateOnly(2025, 1, 15) };
        yield return new object[] { "Flagstaff 2025-10-10", 35.20, -111.65, -7.0, new DateOnly(2025, 10, 10) };
        yield return new object[] { "Sydney 2025-01-15", -33.87, 151.20, 11.0, new DateOnly(2025, 1, 15) };
        yield return new object[] { "Berlin 2025-01-15", 52.52, 13.40, 1.0, new DateOnly(2025, 1, 15) };
    }

    // A local wall-clock value from a fixed-offset zone is that instant minus the offset, which
    // is all SunAltitudeDegrees needs.
    private static double AltitudeAt(
        DateTime localWallClock, double offsetHours, double latitudeDeg, double longitudeDeg)
        => AstroNight.SunAltitudeDegrees(
            DateTime.SpecifyKind(localWallClock.AddHours(-offsetHours), DateTimeKind.Utc),
            latitudeDeg,
            longitudeDeg);

    [Theory]
    [MemberData(nameof(BoundsRows))]
    public void NightBounds_ForEveryReviewSiteAndDate_IsTheOneDarkRunInsideTheLocalWindow(
        string label, double latitude, double longitude, double offsetHours, DateOnly date)
    {
        // Ruling P1-1: the window is anchored on 12:00 local, so on every site and date the band
        // lies inside that window, local noon is outside it, and the dark samples of the window
        // are exactly the samples the band covers. The last of those three is the property the
        // pre-fix method broke: its band swallowed the lit middle of the day between two dark
        // runs, and the sample equality below fails on the first lit sample it contains.
        var bounds = AstroNight.NightBounds(date, latitude, longitude, FixedZone(offsetHours));
        Assert.NotNull(bounds);

        var (dusk, dawn) = bounds.Value;
        var windowStart = date.ToDateTime(new TimeOnly(12, 0));
        var windowEnd = windowStart.AddHours(24.0);

        Assert.True(dusk < dawn, $"{label}: dusk {dusk:O} is not before dawn {dawn:O}");
        Assert.True(dusk >= windowStart, $"{label}: dusk {dusk:O} is before the window");
        Assert.True(dawn <= windowEnd, $"{label}: dawn {dawn:O} is after the window");
        Assert.True(dusk > windowStart, $"{label}: local noon {windowStart:O} is inside the band");
        Assert.True(dawn < windowEnd, $"{label}: the next local noon {windowEnd:O} is inside the band");

        for (var i = 0; i < AstroNight.SamplesPerDay; i++)
        {
            var sample = windowStart.AddHours(i * AstroNight.SampleIntervalHours);
            var isDark = AltitudeAt(sample, offsetHours, latitude, longitude)
                < AstroNight.AstronomicalTwilightDegrees;
            Assert.True(
                isDark == (sample >= dusk && sample <= dawn),
                $"{label}: sample {sample:O} is {(isDark ? "dark" : "lit")} and the band is "
                    + $"{dusk:O} to {dawn:O}");
        }

        // Both edges resolved to the minute, on the dark side, which is the roadmap's bar.
        Assert.True(
            AltitudeAt(dusk, offsetHours, latitude, longitude) < AstroNight.AstronomicalTwilightDegrees,
            $"{label}: dusk {dusk:O} is not dark");
        Assert.True(
            AltitudeAt(dawn, offsetHours, latitude, longitude) < AstroNight.AstronomicalTwilightDegrees,
            $"{label}: dawn {dawn:O} is not dark");
        Assert.True(
            AltitudeAt(dusk.AddMinutes(-1), offsetHours, latitude, longitude)
                >= AstroNight.AstronomicalTwilightDegrees,
            $"{label}: one minute before dusk {dusk:O} is already dark");
        Assert.True(
            AltitudeAt(dawn.AddMinutes(1), offsetHours, latitude, longitude)
                >= AstroNight.AstronomicalTwilightDegrees,
            $"{label}: one minute after dawn {dawn:O} is still dark");
    }

    [Theory]
    [MemberData(nameof(BoundsRows))]
    public void NightBounds_TotalMatchesDarkHoursForNight_WithinOneSample(
        string label, double latitude, double longitude, double offsetHours, DateOnly date)
    {
        // The duration counts 10 minute samples over a 12:00 UTC window and the interval resolves
        // its two edges over a 12:00 local one, so the two can differ by up to one sample and no
        // more. A bisection that walked towards the lit side instead of the dark one lands
        // outside this band, and so does the pre-fix 23.83 hour band on the three split rows.
        //
        // One sample is the tightest bound that holds on all five rows. The observed differences
        // are +0.0417 h (Flagstaff 2025-06-21), -0.0417 h (Flagstaff 2025-01-15), +0.0521 h
        // (Flagstaff 2025-10-10), -0.1458 h (Sydney 2025-01-15) and -0.0208 h (Berlin
        // 2025-01-15), against a sample of 0.1667 h. The three split rows stay inside it because
        // a 24 hour window at any anchor collects about one night's worth of dark samples
        // whichever nights they are drawn from, which is spec 8.4's own phase-independence
        // argument for the duration.
        var bounds = AstroNight.NightBounds(date, latitude, longitude, FixedZone(offsetHours));
        Assert.NotNull(bounds);

        var interval = (bounds.Value.Dawn - bounds.Value.Dusk).TotalHours;
        var duration = AstroNight.DarkHoursForNight(date, latitude, longitude);

        Assert.True(
            Math.Abs(interval - duration) <= AstroNight.SampleIntervalHours + 1e-9,
            $"{label}: interval {interval:F4}h, duration {duration:F4}h");
    }

    [Fact]
    public void NightBounds_InAPolarWinter_FillsTheWholeWindow()
    {
        // The two window-edge branches. At 85 degrees on the solstice every sample is dark, so
        // there is no crossing to refine on either side and the band is the window itself. This
        // is the only case that reaches those branches once the window is anchored on local noon,
        // and it is a correct answer: the sun does not rise that day.
        var date = new DateOnly(2025, 12, 21);
        var bounds = AstroNight.NightBounds(date, 85.0, 0.0, FixedZone(0));

        Assert.NotNull(bounds);
        Assert.Equal(date.ToDateTime(new TimeOnly(12, 0)), bounds.Value.Dusk);
        Assert.Equal(date.ToDateTime(new TimeOnly(12, 0)).AddHours(24.0), bounds.Value.Dawn);
        Assert.Equal(24.0, AstroNight.DarkHoursForNight(date, 85.0, 0.0), 9);
    }

    [Fact]
    public void NightBounds_WithNoCoordinates_IsNull()
    {
        // Spec 5.8 leaves the observer location unset by default, and either half missing is the
        // same answer: no band.
        Assert.Null(AstroNight.NightBounds(BoundsDate, null, BoundsLongitude, TimeZoneInfo.Utc));
        Assert.Null(AstroNight.NightBounds(BoundsDate, BoundsLatitude, null, TimeZoneInfo.Utc));
        Assert.Null(AstroNight.NightBounds(BoundsDate, null, null, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NightBounds_InAPolarSummer_IsNull()
    {
        // Latitude 78 in late June: no sample of the window is below -18 degrees. A correct
        // answer, not a failure, and the same null the caller renders as "no band".
        Assert.Null(AstroNight.NightBounds(new DateOnly(2025, 6, 21), 78.0, 0.0, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NightBounds_WithAZoneFarFromTheSite_TakesTheLongestDarkRun()
    {
        // Spec 5.8 keeps the display zone and the observer location as two independent settings,
        // so a Flagstaff observer who displays UTC is a real configuration, and on this date that
        // window opens in the middle of one night and closes in the middle of the next. The band
        // is the longer of the two dark runs, never the span across the lit day between them,
        // which is the 23.83 hour answer the pre-fix method gave. Short by the part of the night
        // that falls outside the window, and every minute of it dark.
        var date = new DateOnly(2025, 1, 15);
        var bounds = AstroNight.NightBounds(date, 35.20, -111.65, TimeZoneInfo.Utc);

        Assert.NotNull(bounds);
        var (dusk, dawn) = bounds.Value;
        var duration = AstroNight.DarkHoursForNight(date, 35.20, -111.65);

        Assert.InRange((dawn - dusk).TotalHours, 1.0, duration);
        for (var sample = dusk; sample <= dawn; sample = sample.AddMinutes(10))
        {
            Assert.True(
                AltitudeAt(sample, 0.0, 35.20, -111.65) < AstroNight.AstronomicalTwilightDegrees,
                $"{sample:O} is inside the band and is not dark");
        }
    }

    [Fact]
    public void NightBounds_ReturnsLocalWallClockTimes()
    {
        // Two zones one hour apart, both plausible for this site, so both windows hold the whole
        // of the same night: the band is the same pair of instants and only the clock differs.
        // Zones hours away from the site's longitude sample a different window and are a
        // different question, covered by the band-inside-the-window assertions above.
        var west = AstroNight.NightBounds(BoundsDate, BoundsLatitude, BoundsLongitude, FixedZone(-7));
        var east = AstroNight.NightBounds(BoundsDate, BoundsLatitude, BoundsLongitude, FixedZone(-6));

        Assert.NotNull(west);
        Assert.NotNull(east);

        // Unspecified, never Utc or Local: the caller compares these with a capture time already
        // converted through the same zone and must not be able to mix an offset in.
        Assert.Equal(DateTimeKind.Unspecified, west.Value.Dusk.Kind);
        Assert.Equal(DateTimeKind.Unspecified, west.Value.Dawn.Kind);
        Assert.Equal(DateTimeKind.Unspecified, east.Value.Dusk.Kind);
        Assert.Equal(DateTimeKind.Unspecified, east.Value.Dawn.Kind);

        Assert.Equal(1.0, (east.Value.Dusk - west.Value.Dusk).TotalHours, 9);
        Assert.Equal(1.0, (east.Value.Dawn - west.Value.Dawn).TotalHours, 9);

        // P12 review P2-2: ConvertTimeFromUtc hands back Utc for TimeZoneInfo.Utc and Local for
        // TimeZoneInfo.Local, so those two zones are the cases the fixed-offset pair above cannot
        // see. The contract is Unspecified for every zone.
        foreach (var zone in new[] { TimeZoneInfo.Utc, TimeZoneInfo.Local })
        {
            var bounds = AstroNight.NightBounds(BoundsDate, BoundsLatitude, BoundsLongitude, zone);
            Assert.NotNull(bounds);
            Assert.Equal(DateTimeKind.Unspecified, bounds.Value.Dusk.Kind);
            Assert.Equal(DateTimeKind.Unspecified, bounds.Value.Dawn.Kind);
        }
    }

    [Fact]
    public void NightBounds_DoesNotPerturbDarkHoursForNight()
    {
        // NightBounds samples the same grid and deliberately does not memoize. This pins that it
        // neither writes to nor invalidates the caches the three neighbouring methods share.
        var before = AstroNight.DarkHoursForNight(BoundsDate, BoundsLatitude, BoundsLongitude);
        _ = AstroNight.NightBounds(BoundsDate, BoundsLatitude, BoundsLongitude, TimeZoneInfo.Utc);
        var after = AstroNight.DarkHoursForNight(BoundsDate, BoundsLatitude, BoundsLongitude);

        Assert.Equal(before, after, 12);
    }

    [Fact]
    public void SunAltitude_AtAFixedInstant_MatchesTheTranscribedFormulaToNineDecimals()
    {
        // Regression pin, not an external reference: the expected value is this
        // implementation's own output at a fixed instant and position, captured once and
        // hardcoded here. Every USNO-row assertion above tolerates several minutes of slack, so
        // none of them can see the 0.020 * sin(2 * gMeanDeg) second-harmonic term in lamDeg:
        // dropping its Rad conversion perturbs the altitude by at most 0.04 degrees, which no
        // crossing or interval assertion resolves. This pin asserts to about 1e-9 degrees, so a
        // future edit to any Rad/Deg conversion in SunAltitudeDegrees, including that term,
        // moves this value and fails the test even though it cannot move a crossing test.
        var utc = new DateTime(2025, 7, 4, 6, 30, 0, DateTimeKind.Utc);
        var altitude = AstroNight.SunAltitudeDegrees(utc, 40.7128, -74.0060);

        Assert.Equal(-23.123814871447372, altitude, 9);
    }
}
