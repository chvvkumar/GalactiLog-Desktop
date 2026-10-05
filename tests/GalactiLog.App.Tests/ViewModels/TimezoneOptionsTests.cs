using System.Security;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// User ruling U4: every timezone selector shows its GMT offset first and a display name second,
// and the list runs negative offsets, then GMT, then positive offsets. TimezoneOptions.Build is
// the one place a zone id becomes an option (design-lessons rule 1); LocationTabViewModelTests
// and SetupWizardViewModelTests pin the three view-model lists that call it.
//
// Coordinator ruling (review task8-review.md escalation): TimeZoneInfo.GetSystemTimeZones()
// returns Windows zone ids on Windows, not IANA ids, so the id alone ("Pacific Standard Time")
// would name standard time even in summer, which is the confusing naming the user complained
// about. The name half of the label is therefore TimeZoneInfo.DisplayName with its leading
// "(UTC...)" prefix stripped, falling back to the bare id when that leaves nothing. Provisional
// pending the user.
public class TimezoneOptionsTests
{
    // A custom zone with a fixed standard offset, an optional display name distinct from its id
    // (Windows-style, with the "(UTC...)" prefix the builder strips), and an optional daylight
    // rule in force on every date of the year, so a case can prove the label uses the standard
    // offset even when daylight saving is active right now.
    private static TimeZoneInfo Zone(
        string id,
        double standardOffsetHours,
        string? displayName = null,
        bool daylight = false)
    {
        var standardOffset = TimeSpan.FromHours(standardOffsetHours);
        var name = displayName ?? id;
        if (!daylight)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, standardOffset, name, name);
        }

        // 1 January 00:00 to 31 December 23:00: in force on every date of the year, so the case
        // that depends on it cannot fail on the calendar (review P2 finding: a March-to-November
        // rule left the case failing between 1 November and 1 March).
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 1, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 0, 0), 12, 31));
        return TimeZoneInfo.CreateCustomTimeZone(id, standardOffset, name, name, name, [rule]);
    }

    // Pins the resolver to a fixed set, so no case depends on the machine's zone database. An id
    // outside the dictionary throws the way TimeZoneInfo.FindSystemTimeZoneById does for an
    // unknown id.
    private static Func<string, TimeZoneInfo> Resolver(IReadOnlyDictionary<string, TimeZoneInfo> zones)
        => id => zones.TryGetValue(id, out var zone) ? zone : throw new TimeZoneNotFoundException(id);

    [Fact]
    public void Build_LabelsANegativeAZeroAPositiveAndANegativeHalfHourZone_WithTheDisplayedName()
    {
        var zones = new Dictionary<string, TimeZoneInfo>
        {
            ["Pacific Standard Time"] = Zone(
                "Pacific Standard Time", -8, displayName: "(UTC-08:00) Pacific Time (US & Canada)"),
            ["GMT Standard Time"] = Zone(
                "GMT Standard Time", 0, displayName: "(UTC) Dublin, Edinburgh, Lisbon, London"),
            ["India Standard Time"] = Zone(
                "India Standard Time", 5.5, displayName: "(UTC+05:30) Chennai, Kolkata, Mumbai, New Delhi"),
            // The negative half hour: a formatter that takes the absolute value of Hours but
            // leaves Minutes signed passes every other case here and still renders
            // "GMT-03:-30 Newfoundland" for this one (review P2 finding).
            ["Newfoundland Standard Time"] = Zone(
                "Newfoundland Standard Time", -3.5, displayName: "(UTC-03:30) Newfoundland"),
        };

        var options = TimezoneOptions.Build(zones.Keys, Resolver(zones)).ToDictionary(o => o.Id, o => o.Label);

        // Fails on a missing plus for zero, a single-digit hour, a signed minutes field, the id
        // used instead of the displayed name, or the name before the offset.
        Assert.Equal("GMT-08:00 Pacific Time (US & Canada)", options["Pacific Standard Time"]);
        Assert.Equal("GMT+00:00 Dublin, Edinburgh, Lisbon, London", options["GMT Standard Time"]);
        Assert.Equal("GMT+05:30 Chennai, Kolkata, Mumbai, New Delhi", options["India Standard Time"]);
        Assert.Equal("GMT-03:30 Newfoundland", options["Newfoundland Standard Time"]);
    }

    [Fact]
    public void Build_FallsBackToTheBareId_WhenTheDisplayNameHasNothingAfterThePrefix()
    {
        var zones = new Dictionary<string, TimeZoneInfo>
        {
            ["Prefix Only"] = Zone("Prefix Only", 0, displayName: "(UTC)"),
        };

        var option = TimezoneOptions.Build(zones.Keys, Resolver(zones)).Single();

        Assert.Equal("GMT+00:00 Prefix Only", option.Label);
    }

    [Fact]
    public void Build_OrdersNegativeBeforeZeroBeforePositive()
    {
        // Ids whose alphabetical order differs from their offset order, so an alphabetically
        // sorted list fails this case.
        var zones = new Dictionary<string, TimeZoneInfo>
        {
            ["Asia/Kolkata"] = Zone("Asia/Kolkata", 5.5),
            ["America/Los_Angeles"] = Zone("America/Los_Angeles", -8),
            ["Etc/UTC"] = Zone("Etc/UTC", 0),
        };

        var order = TimezoneOptions.Build(zones.Keys, Resolver(zones)).Select(o => o.Id).ToList();

        Assert.Equal(["America/Los_Angeles", "Etc/UTC", "Asia/Kolkata"], order);
    }

    [Fact]
    public void Build_TiesOnOffsetAreOrderedByTheDisplayedName_NotTheId()
    {
        // If the tie-break used the id, "AAA/EarlyId" would sort first; the displayed names
        // invert that, so this fails unless the tie-break reads Name, not Id.
        var zones = new Dictionary<string, TimeZoneInfo>
        {
            ["AAA/EarlyId"] = Zone("AAA/EarlyId", 0, displayName: "(UTC) Zebra Zone"),
            ["ZZZ/LateId"] = Zone("ZZZ/LateId", 0, displayName: "(UTC) Apple Zone"),
        };

        var order = TimezoneOptions.Build(zones.Keys, Resolver(zones)).Select(o => o.Id).ToList();

        Assert.Equal(["ZZZ/LateId", "AAA/EarlyId"], order);
    }

    [Fact]
    public void Build_UsesTheStandardOffset_NotTodaysActualOffset()
    {
        var zones = new Dictionary<string, TimeZoneInfo>
        {
            ["Zone/Daylight"] = Zone("Zone/Daylight", -5, daylight: true),
        };

        // Guards that the case is still meaningful: if this ever failed, the assertion below
        // would prove nothing about labels, only that the fixture zone is not currently
        // observing daylight saving.
        Assert.True(zones["Zone/Daylight"].IsDaylightSavingTime(DateTime.Now));

        var option = TimezoneOptions.Build(zones.Keys, Resolver(zones)).Single();

        // Fails if the label were built from GetUtcOffset(now) (-4) instead of BaseUtcOffset (-5).
        Assert.Equal("GMT-05:00 Zone/Daylight", option.Label);
    }

    [Fact]
    public void Build_KeepsAnUnresolvableId_UnlabelledWithAnOffset_SortedLast()
    {
        var zones = new Dictionary<string, TimeZoneInfo> { ["Etc/UTC"] = Zone("Etc/UTC", 0) };

        var options = TimezoneOptions.Build(["Etc/UTC", "Pacific/Chatham"], Resolver(zones)).ToList();

        Assert.Equal(["Etc/UTC", "Pacific/Chatham"], options.Select(o => o.Id));
        Assert.Equal("Pacific/Chatham", options[1].Label);
    }

    [Theory]
    [MemberData(nameof(ResolverFailures))]
    public void Build_TreatsAnyResolverFailure_AsAnUnresolvedId_NeverThrowing(Exception failure)
    {
        // Not only TimeZoneNotFoundException and InvalidTimeZoneException:
        // FindSystemTimeZoneById also documents SecurityException, and any other failure the
        // resolver raises must not reach the caller either (review P2 finding: the builder used
        // to be a lazy iterator whose resolver calls ran outside the wizard step's own
        // try/catch).
        Func<string, TimeZoneInfo> resolve = _ => throw failure;

        var options = TimezoneOptions.Build(["Some/Zone"], resolve);

        var option = Assert.Single(options);
        Assert.Equal("Some/Zone", option.Id);
        Assert.Equal("Some/Zone", option.Label);
    }

    public static TheoryData<Exception> ResolverFailures => new()
    {
        new SecurityException("no permission to read the zone registry key"),
        new InvalidOperationException("boom"),
    };

    [Fact]
    public void Build_LogsAResolverFailureOnceAtDebug_WhenALoggerIsInReach()
    {
        var logger = new RecordingLogger();
        Func<string, TimeZoneInfo> resolve = _ => throw new InvalidOperationException("boom");

        var options = TimezoneOptions.Build(["Some/Zone"], resolve, logger);

        Assert.Single(options);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
    }

    [Fact]
    public void Build_DoesNotLog_WithoutALogger()
    {
        // The logger parameter is optional; a resolver failure with no logger in reach must still
        // be swallowed rather than thrown, just silently.
        Func<string, TimeZoneInfo> resolve = _ => throw new InvalidOperationException("boom");

        var options = TimezoneOptions.Build(["Some/Zone"], resolve);

        Assert.Single(options);
    }

    [Fact]
    public void TimezoneOptionConstructionSites_AreExactlyTheBuilderAndTheThreeViewModels()
    {
        // The house absence-or-exact-count census (TRACKING section 5), replacing a narrower
        // regex that only caught the exact old shape "new TimezoneOption(zone, zone)" (review P3
        // finding): a fifth selector writing its own label in the new vocabulary, for instance
        // new TimezoneOption(zone, $"GMT... {zone}"), still fails this because it is a fourth
        // construction site, not because of what it constructs.
        //
        // Phase 15A adds Phd2ProfilesViewModel.cs, the per-profile timezone picker of spec 7.6.
        // It is not a U4 violation: every ordinary zone entry in its list comes from
        // TimezoneOptions.Build, and the two entries it constructs itself are the same two
        // LocationTabViewModel.cs constructs, neither of which is a zone label at all -- the
        // empty-id sentinel ("Inherit the observer timezone", the profile-level counterpart of
        // the location tab's "not configured") and the stored-but-unlisted id carried as its own
        // entry so a value chosen on another machine is not silently rewritten.
        Assert.Equal(
            ["LocationTabViewModel.cs", "ObserverLocationStepViewModel.cs", "Phd2ProfilesViewModel.cs"],
            SourceScan.FilesMatching(@"new\s+TimezoneOption\s*\("));
    }
}
