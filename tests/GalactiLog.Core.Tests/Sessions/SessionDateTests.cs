using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Sessions;
using Xunit;

namespace GalactiLog.Core.Tests.Sessions;

public class SessionDateTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0, int s = 0) =>
        new(y, m, d, h, min, s, DateTimeKind.Utc);

    // ---- Compute ----

    [Fact]
    public void Compute_NullCaptureUtc_ReturnsNull()
    {
        Assert.Null(SessionDate.Compute(null, true, 10.0));
    }

    [Fact]
    public void Compute_UseImagingNightFalse_ReturnsUtcDate()
    {
        var captureUtc = Utc(2025, 6, 15, 3, 0, 0);
        var result = SessionDate.Compute(captureUtc, false, 75.0);
        Assert.Equal(new DateOnly(2025, 6, 15), result);
    }

    [Fact]
    public void Compute_NullLongitude_FallsBackToUtcDate()
    {
        var captureUtc = Utc(2025, 6, 15, 3, 0, 0);
        var result = SessionDate.Compute(captureUtc, true, null);
        Assert.Equal(new DateOnly(2025, 6, 15), result);
    }

    [Fact]
    public void Compute_PositiveLongitude_ShiftsBeforeLocalNoon()
    {
        // longitude +75 -> offset 12 - 75/15 = 7h. A capture at 03:00 UTC shifts back to
        // 20:00 the previous day.
        var captureUtc = Utc(2025, 6, 15, 3, 0, 0);
        var result = SessionDate.Compute(captureUtc, true, 75.0);
        Assert.Equal(new DateOnly(2025, 6, 14), result);
    }

    [Fact]
    public void Compute_NegativeLongitude_ShiftsAfterUtcNoon()
    {
        // longitude -75 -> offset 12 - (-75/15) = 17h. A capture at 18:00 UTC (after UTC
        // noon) shifts to 01:00 the same day, so the session date is unchanged.
        var captureUtc = Utc(2025, 6, 15, 18, 0, 0);
        var result = SessionDate.Compute(captureUtc, true, -75.0);
        Assert.Equal(new DateOnly(2025, 6, 15), result);
    }

    [Fact]
    public void Compute_ExactlyAtSolarNoonBoundary_PicksTheDayThatStarts()
    {
        // longitude +75 -> offset 7h. A capture at exactly 07:00:00 UTC shifts to exactly
        // midnight, landing on the day that boundary instant starts (not the previous day).
        var captureUtc = Utc(2025, 6, 15, 7, 0, 0);
        var result = SessionDate.Compute(captureUtc, true, 75.0);
        Assert.Equal(new DateOnly(2025, 6, 15), result);

        // One second earlier still belongs to the previous day.
        var justBefore = Utc(2025, 6, 15, 6, 59, 59);
        var resultBefore = SessionDate.Compute(justBefore, true, 75.0);
        Assert.Equal(new DateOnly(2025, 6, 14), resultBefore);
    }

    // ---- ResolveLongitude ----

    private static readonly DateTime SampleCaptureUtc = Utc(2025, 6, 15, 3, 0, 0);

    [Fact]
    public void ResolveLongitude_Step1_SitelongWins()
    {
        var headers = new JsonObject
        {
            ["SITELONG"] = 10.0,
            ["OBSLONG"] = 20.0,
            ["LONG-OBS"] = 30.0,
        };

        var result = SessionDate.ResolveLongitude(headers, 99.0, null, SampleCaptureUtc);

        Assert.Equal(10.0, result.Longitude);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void ResolveLongitude_Step1_FallsThroughToObslongThenLongObs()
    {
        var obslongOnly = new JsonObject { ["OBSLONG"] = 20.0, ["LONG-OBS"] = 30.0 };
        var obslongResult = SessionDate.ResolveLongitude(obslongOnly, null, null, SampleCaptureUtc);
        Assert.Equal(20.0, obslongResult.Longitude);

        var longObsOnly = new JsonObject { ["LONG-OBS"] = 30.0 };
        var longObsResult = SessionDate.ResolveLongitude(longObsOnly, null, null, SampleCaptureUtc);
        Assert.Equal(30.0, longObsResult.Longitude);
    }

    [Fact]
    public void ResolveLongitude_Step1_UnparseableHeaderValueSkipped()
    {
        var headers = new JsonObject { ["SITELONG"] = "not-a-number" };

        var result = SessionDate.ResolveLongitude(headers, 55.0, null, SampleCaptureUtc);

        Assert.Equal(55.0, result.Longitude);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void ResolveLongitude_Step2_ObserverLongitudeWins_WhenNoHeaders()
    {
        var result = SessionDate.ResolveLongitude(null, 42.0, null, SampleCaptureUtc);

        Assert.Equal(42.0, result.Longitude);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void ResolveLongitude_Step3_TimezoneOffsetApproximation()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        var expected = tz.GetUtcOffset(SampleCaptureUtc).TotalHours * 15.0;

        var result = SessionDate.ResolveLongitude(null, null, "America/Denver", SampleCaptureUtc);

        Assert.Equal(expected, result.Longitude);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void ResolveLongitude_Step3_UsesOffsetAtCaptureInstant_NotNow()
    {
        // America/Denver observes DST (MDT, UTC-6) in July and not in January (MST, UTC-7):
        // exactly a 1 hour difference, so resolved longitudes must differ by exactly 15 degrees.
        var duringDst = Utc(2025, 7, 1, 12, 0, 0);
        var outsideDst = Utc(2025, 1, 1, 12, 0, 0);

        var dstResult = SessionDate.ResolveLongitude(null, null, "America/Denver", duringDst);
        var standardResult = SessionDate.ResolveLongitude(null, null, "America/Denver", outsideDst);

        Assert.NotNull(dstResult.Longitude);
        Assert.NotNull(standardResult.Longitude);
        Assert.Equal(15.0, dstResult.Longitude!.Value - standardResult.Longitude!.Value, 5);
    }

    [Fact]
    public void ResolveLongitude_Step3_UnknownTimezoneId_FallsThroughToStep4()
    {
        var result = SessionDate.ResolveLongitude(null, null, "Not/AZone", SampleCaptureUtc);

        Assert.Null(result.Longitude);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void ResolveLongitude_Step4_NothingResolvable_UsedFallbackTrue()
    {
        var result = SessionDate.ResolveLongitude(null, null, null, SampleCaptureUtc);

        Assert.Null(result.Longitude);
        Assert.True(result.UsedFallback);
    }

    [Theory]
    [InlineData(1)] // step 1: headers
    [InlineData(2)] // step 2: observer longitude
    [InlineData(3)] // step 3: timezone approximation
    public void ResolveLongitude_UsedFallbackFalse_WheneverAnyEarlierStepResolves(int step)
    {
        JsonObject? headers = step == 1 ? new JsonObject { ["SITELONG"] = 10.0 } : null;
        double? observerLongitude = step == 2 ? 42.0 : null;
        string? observerTimezone = step == 3 ? "America/Denver" : null;

        var result = SessionDate.ResolveLongitude(headers, observerLongitude, observerTimezone, SampleCaptureUtc);

        Assert.False(result.UsedFallback);
        Assert.NotNull(result.Longitude);
    }

    // ---- Step 1, a header value that is not finite ----

    /// <summary>
    /// The three literals <c>double.TryParse</c> accepts under <c>NumberStyles.Float</c> besides a
    /// number. Each of them used to win step 1 and then raise inside
    /// <see cref="SessionDate.Compute"/>: NaN gives <c>ArgumentException</c> from
    /// <c>TimeSpan.FromHours</c> and either infinity gives <c>OverflowException</c>. The frame is a
    /// file the user did not write and cannot repair, so the scan must not fail on it.
    /// </summary>
    [Theory]
    [InlineData("SITELONG", "NaN")]
    [InlineData("SITELONG", "Infinity")]
    [InlineData("SITELONG", "-Infinity")]
    [InlineData("OBSLONG", "NaN")]
    [InlineData("OBSLONG", "Infinity")]
    [InlineData("OBSLONG", "-Infinity")]
    [InlineData("LONG-OBS", "NaN")]
    [InlineData("LONG-OBS", "Infinity")]
    [InlineData("LONG-OBS", "-Infinity")]
    public void ResolveLongitude_Step1_AHeaderValueThatIsNotFinite_IsSkipped(string key, string stored)
    {
        var headers = new JsonObject { [key] = stored };

        // Step 2 answers instead, exactly as it does for an unparseable header value.
        var toStep2 = SessionDate.ResolveLongitude(headers, 42.0, FallbackZone, SampleCaptureUtc);
        Assert.Equal(42.0, toStep2.Longitude);
        Assert.False(toStep2.UsedFallback);

        // Then step 3 where no longitude is configured.
        var toStep3 = SessionDate.ResolveLongitude(headers, null, FallbackZone, SampleCaptureUtc);
        Assert.Equal(
            SessionDate.LongitudeFromTimezone(FallbackZone, SampleCaptureUtc), toStep3.Longitude);
        Assert.False(toStep3.UsedFallback);

        // Then step 4, the UTC-midnight fallback, where nothing is configured at all.
        var toStep4 = SessionDate.ResolveLongitude(headers, null, null, SampleCaptureUtc);
        Assert.Null(toStep4.Longitude);
        Assert.True(toStep4.UsedFallback);

        // And the whole point: the session date of that frame is computed rather than thrown.
        Assert.Equal(
            new DateOnly(2025, 6, 15), SessionDate.Compute(SampleCaptureUtc, true, toStep4.Longitude));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void ResolveLongitude_Step1_ASkippedValue_LetsALaterHeaderKeyWin(string stored)
    {
        // The skip is per key and not per frame: a header carrying one unusable value and one
        // usable one resolves on the usable one, at the priority that key already has.
        var sitelongUnusable = new JsonObject { ["SITELONG"] = stored, ["OBSLONG"] = 20.0, ["LONG-OBS"] = 30.0 };
        Assert.Equal(20.0, SessionDate.ResolveLongitude(sitelongUnusable, 42.0, null, SampleCaptureUtc).Longitude);

        var twoUnusable = new JsonObject { ["SITELONG"] = stored, ["OBSLONG"] = stored, ["LONG-OBS"] = 30.0 };
        Assert.Equal(30.0, SessionDate.ResolveLongitude(twoUnusable, 42.0, null, SampleCaptureUtc).Longitude);
    }

    /// <summary>
    /// Characterisation, pinning PRESENT behaviour unchanged, not a ruling. A FINITE header value
    /// outside the legal range still wins step 1 and still reaches spec 8.2's arithmetic as
    /// written. Some capture software writes site longitude in the 0 to 360 east convention, so
    /// 250 is a real site at 110 west and reading it as unset would silently move the session
    /// dates of frames already in a library. A later phase may rule on the convention; these are
    /// the numbers it would be ruling against.
    /// </summary>
    [Theory]
    [InlineData(250.0, 2025, 6, 15)]   // offset 12 - 250/15 = -4h40m, so the capture shifts forward
    [InlineData(-200.0, 2025, 6, 14)]  // offset 12 + 200/15 = 25h20m back, past the previous day
    public void ResolveLongitude_Step1_AFiniteOutOfRangeHeaderValue_IsUnchanged(
        double stored, int year, int month, int day)
    {
        var headers = new JsonObject { ["SITELONG"] = stored };

        var result = SessionDate.ResolveLongitude(headers, 42.0, FallbackZone, SampleCaptureUtc);

        Assert.Equal(stored, result.Longitude);
        Assert.False(result.UsedFallback);
        Assert.Equal(
            new DateOnly(year, month, day),
            SessionDate.Compute(SampleCaptureUtc, true, result.Longitude));
    }

    // ---- UsableLongitude, spec 8.3 step 2's one implementation ----

    // The zone every case below falls through to, and the evening the two sides are compared on.
    private const string FallbackZone = "America/Denver";

    /// <summary>
    /// A stored <c>general.observer_longitude</c> that is not a finite number inside the legal
    /// range is UNSET and the rule falls through to step 3, exactly as it does for a null. A
    /// failure looks like the raw read this side used to carry: the value reaches spec 8.2's
    /// arithmetic, a NaN longitude gives a NaN shift and no session date at all, and 180.0001
    /// gives a date the guide side of the same evening never computes.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(180.0001)]
    [InlineData(-180.0001)]
    public void ResolveLongitude_Step2_AStoredValueThatCannotDescribeASite_IsUnset(double stored)
    {
        var withZone = SessionDate.ResolveLongitude(null, stored, FallbackZone, SampleCaptureUtc);

        // Step 3's answer, and never the value itself and never the limit it sits outside.
        Assert.Equal(
            SessionDate.LongitudeFromTimezone(FallbackZone, SampleCaptureUtc), withZone.Longitude);
        Assert.False(withZone.UsedFallback);

        // And step 4 where there is no zone either, which is what a null stored value does today.
        var withoutZone = SessionDate.ResolveLongitude(null, stored, null, SampleCaptureUtc);
        Assert.Null(withoutZone.Longitude);
        Assert.True(withoutZone.UsedFallback);
    }

    [Theory]
    [InlineData(180.0)]   // the legal boundary, a real place: the rule keeps it
    [InlineData(-180.0)]
    [InlineData(0.0)]     // and the prime meridian is an answer, not the null signal
    [InlineData(42.0)]
    public void ResolveLongitude_Step2_ALegalValue_StillWinsOverTheZone(double stored)
    {
        var result = SessionDate.ResolveLongitude(null, stored, FallbackZone, SampleCaptureUtc);

        Assert.Equal(stored, result.Longitude);
        Assert.False(result.UsedFallback);
    }

    /// <summary>
    /// Design lesson 1 again, and the defect this rule exists to close. One stored key, read once:
    /// a frame of an evening and the guiding session of the same evening land on the SAME
    /// <c>session_date</c> whatever the stored value is, because both sides pass it through
    /// <see cref="SessionDate.UsableLongitude"/>. A failure is silent. Every consumer of a guiding
    /// night joins on strict <c>session_date</c> equality, so two dates for one evening is an empty
    /// guiding chart with no error anywhere.
    /// </summary>
    /// <remarks>
    /// The hour of the evening is a row of its own because two wrong longitudes can still round to
    /// one calendar date. A raw 180.0001 shifts a capture by almost nothing while the zone shifts
    /// it by 19 hours, so the two dates part before 19:00 UTC and agree by coincidence after it;
    /// a raw -180.0001 shifts by just over 24 hours and does the opposite. Each row therefore
    /// names an hour at which the divergence is actually visible, or the case would pass against
    /// the defect it is written for.
    /// </remarks>
    [Theory]
    [InlineData(double.NaN, 3)]
    [InlineData(double.PositiveInfinity, 3)]
    [InlineData(double.NegativeInfinity, 3)]
    [InlineData(180.0001, 3)]
    [InlineData(-180.0001, 20)]
    [InlineData(180.0, 3)]    // the legal boundary, where both sides take the stored value
    [InlineData(-180.0, 20)]
    public void AStoredLongitude_GivesTheFrameAndTheGuidingSessionOfOneEveningOneSessionDate(
        double stored, int hourUtc)
    {
        // One evening: a frame captured at this instant and a guiding session started at the same
        // instant, on a library whose only other configuration is the zone.
        var evening = Utc(2026, 3, 2, hourUtc, 0, 0);

        var frameSide = SessionDate.ResolveLongitude(null, stored, FallbackZone, evening).Longitude;
        var guideSide = Phd2Profiles.LongitudeResolver(null, stored, FallbackZone)("Rig A", evening);

        var frameDate = SessionDate.Compute(evening, true, frameSide);
        var guideDate = SessionDate.Compute(evening, true, guideSide);

        Assert.NotNull(frameDate);
        Assert.Equal(frameDate, guideDate);
    }

    // ---- LongitudeFromTimezone, spec 8.3 step 3's one implementation (Phase 17 ruling U4) ----

    [Fact]
    public void LongitudeFromTimezone_NullOrEmptyId_ReturnsNull()
    {
        Assert.Null(SessionDate.LongitudeFromTimezone(null, SampleCaptureUtc));
        Assert.Null(SessionDate.LongitudeFromTimezone("", SampleCaptureUtc));
    }

    [Fact]
    public void LongitudeFromTimezone_AZoneTheLoaderRefuses_ReturnsNull()
    {
        // The platform path and the injected path have to agree on this, or a pass that hands its
        // own memo would crash where the frame side quietly falls through to step 4.
        Assert.Null(SessionDate.LongitudeFromTimezone("Not/AZone", SampleCaptureUtc));
        Assert.Null(SessionDate.LongitudeFromTimezone("Not/AZone", SampleCaptureUtc, _ => null));
    }

    [Fact]
    public void LongitudeFromTimezone_IsFifteenDegreesPerOffsetHour_PositiveEast()
    {
        var minusFive = TimeZoneInfo.CreateCustomTimeZone(
            "Stub/Minus5", TimeSpan.FromHours(-5), "Stub/Minus5", "Stub/Minus5");
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone(
            "Stub/Plus2", TimeSpan.FromHours(2), "Stub/Plus2", "Stub/Plus2");

        Assert.Equal(-75.0, SessionDate.LongitudeFromTimezone("x", SampleCaptureUtc, _ => minusFive)!.Value, 6);
        Assert.Equal(30.0, SessionDate.LongitudeFromTimezone("x", SampleCaptureUtc, _ => plusTwo)!.Value, 6);
        Assert.Equal(0.0, SessionDate.LongitudeFromTimezone("x", SampleCaptureUtc, _ => TimeZoneInfo.Utc)!.Value, 6);
    }

    /// <summary>
    /// Design lesson 1, and the case a reviewer reads first. The rule "a timezone id plus an
    /// instant gives an approximate longitude of <c>utcOffsetHours * 15</c>" exists once, in
    /// <c>SessionDate</c>, and the guide side calls it rather than carrying a second copy. A
    /// failure looks like six lines pasted into <c>Phd2Profiles.LongitudeResolver</c>, which
    /// compiles, passes every behavioural case on the day it is written, and drifts the first time
    /// one side is tuned.
    /// </summary>
    [Fact]
    public void LongitudeFromTimezone_IsTheOneImplementationOfTheOffsetRule()
    {
        var guideSide = ReadSource("src/GalactiLog.Core/Phd2/Phd2Profiles.cs");

        // The two needles that are unique to the rule. FindSystemTimeZoneById is deliberately NOT
        // one of them: Phd2Profiles.ZoneLoader has carried it since Phase 15A as the default zone
        // resolver, which is a different job from converting an offset to a longitude.
        Assert.DoesNotContain("GetUtcOffset", guideSide, StringComparison.Ordinal);
        Assert.DoesNotContain("15.0", guideSide, StringComparison.Ordinal);
        Assert.Contains("SessionDate.LongitudeFromTimezone(", guideSide, StringComparison.Ordinal);

        // And the frame side holds exactly one, inside LongitudeFromTimezone itself, so the
        // extraction left no copy behind either.
        var frameSide = ReadSource("src/GalactiLog.Core/Sessions/SessionDate.cs");
        Assert.Single(Regex.Matches(frameSide, "GetUtcOffset"));
        Assert.Single(Regex.Matches(frameSide, "FindSystemTimeZoneById"));
    }

    // The comment-stripped text of one repository-relative source file. Written here rather than
    // shared because this project's structural cases each carry their own root walk already
    // (FileSafetyTest, NoSurveyImageFetchTest, VersionDerivationTests, WorkflowFileTests) and
    // folding those four is a refactor of files this task may not touch.
    private static string ReadSource(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above the test output directory.");
        var text = File.ReadAllText(
            Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        // Comments carry the rule in prose deliberately, so an absence assertion must not be
        // satisfied or broken by a sentence about the thing it forbids.
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\r\n]*", "");
    }
}
