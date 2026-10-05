using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using GalactiLog.Data.Tests.TestSupport;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// <see cref="Phd2NightQuery"/>: the night read, the rig rule's truth table, the rollup figures
/// and the hundred-frame gate as the query reports it.
/// </summary>
/// <remarks>
/// Rows are built in the database directly rather than ingested. These are query cases, and a
/// query case that depends on a parser is a parser case wearing the wrong name; building the row
/// is also the only way to reach a calibration issue, a second pixel scale or an alias spelling,
/// none of which any guide-log fixture carries.
/// </remarks>
public class Phd2NightQueryTests
{
    private static readonly DateOnly Night = new(2025, 3, 19);
    private const string Profile = "TestScope_TestCam";

    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;
        private readonly Guid _logId = Guid.NewGuid();
        private int _added;

        public Library()
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            Settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
            _aliases = new AliasMapCache(Settings);
            Query = new Phd2NightQuery(
                new DatabaseConnectionString(_db.ConnectionString),
                _aliases,
                () => Settings.GetGeneral().Phd2ProfileMap);

            using var context = Open();
            context.Phd2Logs.Add(new Phd2Log
            {
                Id = _logId,
                FilePath = @"C:\Astro\guide\PHD2_GuideLog_2025-03-19_213000.txt",
                FileSize = 1024,
                FileMtime = 1_700_000_000d,
                ParseStatus = "ok",
                RunCount = 1,
                SessionCount = 1,
                CalibrationCount = 0,
                ParsedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            });
            context.SaveChanges();
        }

        public SettingsStore Settings { get; }

        public Phd2NightQuery Query { get; }

        public GalactiLogContext Open()
            => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

        /// <summary>One <c>phd2_sessions</c> row on <paramref name="night"/>, started one minute
        /// after the previous one so the ascending order is unambiguous.</summary>
        public Guid AddSession(DateOnly night, Action<Phd2Session> configure)
        {
            var id = Guid.NewGuid();
            var started = new DateTime(night.Year, night.Month, night.Day, 21, 0, 0, DateTimeKind.Utc)
                .AddMinutes(_added++);
            var session = new Phd2Session
            {
                Id = id,
                LogId = _logId,
                RunIndex = 0,
                SectionIndex = _added,
                // Five hours off the UTC start, so a projection reading the wrong column is a
                // value an assertion can see rather than the same wall clock twice.
                StartedAtLocal = DateTime.SpecifyKind(started.AddHours(-5), DateTimeKind.Unspecified),
                StartedAtUtc = started,
                EndedAtUtc = started.AddMinutes(5),
                SessionDate = night,
                DurationS = 300,
                EquipmentProfile = Profile,
                PixelScaleArcsec = 1.50,
                FrameCount = 400,
            };
            configure(session);

            using var context = Open();
            context.Phd2Sessions.Add(session);
            context.SaveChanges();
            return id;
        }

        /// <summary>
        /// A session whose rig is <paramref name="telescope"/>, expressed the only way a reader
        /// can express it since phase-review.md F3: the section names an equipment profile of its
        /// own and the live map points that profile at the telescope. Setting
        /// <c>Phd2Session.Telescope</c> in a seed no longer says anything, because no reader
        /// consults the stored column; it is still written here because the ingest writes it, so
        /// a reader that started consulting it again would not pass under this seed by accident.
        /// </summary>
        public Guid AddSessionOnRig(DateOnly night, string telescope, Action<Phd2Session>? configure = null)
        {
            var profile = $"{telescope}_Cam";
            var id = AddSession(night, session =>
            {
                session.EquipmentProfile = profile;
                session.Telescope = telescope;
                configure?.Invoke(session);
            });
            MapProfile(profile, telescope);
            return id;
        }

        /// <summary>Maps one PHD2 equipment profile to a telescope in
        /// <c>general.phd2_profile_map</c>, the way the Equipment tab's panel writes it. Called
        /// after the sessions are added, deliberately: that is the order the defect lives in, a
        /// library catalogued first and mapped afterwards.</summary>
        public void MapProfile(string profile, string? telescope)
            => Settings.MutateGeneral(general => general with
            {
                Phd2ProfileMap = Phd2Profiles.ToJson(
                    Phd2Profiles.SetTelescope(general.Phd2ProfileMap, profile, telescope)),
            });

        public void MapTelescopeAlias(string canonical, string alias)
            => Settings.SaveEquipment(new EquipmentSettings
            {
                Telescopes = new Dictionary<string, EquipmentItemSettings>
                {
                    [canonical] = new() { Aliases = [alias] },
                },
            });

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    // ---- AnyGuideLogs, spec 12.4's "is this band drawn at all" (Phase 15B Task 3) -----------

    /// <summary>A query over a database carrying exactly the <c>phd2_logs</c> rows
    /// <paramref name="parseStatuses"/> names, and nothing else.</summary>
    private static (Phd2NightQuery Query, IDisposable Scope) LibraryOfLogs(params string[] parseStatuses)
    {
        var db = TestDatabaseFactory.CreateMigratedDatabase();
        var settings = new SettingsStore(new SettingsRepository(db.ConnectionString));
        var aliases = new AliasMapCache(settings);

        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(db.ConnectionString, tracking: true)))
        {
            var index = 0;
            foreach (var status in parseStatuses)
            {
                context.Phd2Logs.Add(new Phd2Log
                {
                    Id = Guid.NewGuid(),
                    FilePath = $@"C:\Astro\guide\PHD2_GuideLog_2025-03-19_21300{index++}.txt",
                    FileSize = 1024,
                    FileMtime = 1_700_000_000d,
                    ParseStatus = status,
                    RunCount = 0,
                    SessionCount = 0,
                    CalibrationCount = 0,
                    ParsedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                });
            }

            context.SaveChanges();
        }

        return (
            new Phd2NightQuery(
                new DatabaseConnectionString(db.ConnectionString),
                aliases,
                () => settings.GetGeneral().Phd2ProfileMap),
            new Scope(aliases, db));
    }

    private sealed class Scope(AliasMapCache aliases, TestDatabaseHandle db) : IDisposable
    {
        public void Dispose()
        {
            aliases.Dispose();
            db.Dispose();
        }
    }

    /// <summary>Removes every <c>phd2_logs</c> row through the TEST's own connection, which is how
    /// the memo cases count statements rather than timing them: a call that really issues its
    /// <c>EXISTS</c> after this answers false, and a call answering from the memo answers true.
    /// </summary>
    private static void ClearGuideLogs(Library library)
    {
        using var context = library.Open();
        context.Phd2Logs.RemoveRange(context.Phd2Logs);
        context.SaveChanges();
    }

    [Fact]
    public void AnyGuideLogs_AnswersFromAMemoAcrossRepeatedCalls()
    {
        // Spec 12.4 promises one EXISTS per page and the page asks once per night card, so on a
        // target with two hundred nights this was two hundred statements (task3-review.md P2-3).
        // Red against the unmemoised member: the second call reads the emptied table and answers
        // false.
        using var library = new Library();

        Assert.True(library.Query.AnyGuideLogs());

        ClearGuideLogs(library);

        Assert.True(library.Query.AnyGuideLogs());
    }

    [Fact]
    public void AnyGuideLogs_AfterInvalidateGuideLogMemo_AsksTheDatabaseAgain()
    {
        // The other half, and the one that makes the memo safe to hold on a DI singleton: the
        // reset AppHost.InvalidateDerivedCaches calls is what reveals the band after the scan
        // that catalogues a library's first guide log. Red against a memo with no reset, and red
        // against a reset that sets the memo to a value instead of clearing it.
        using var library = new Library();

        Assert.True(library.Query.AnyGuideLogs());
        ClearGuideLogs(library);
        library.Query.InvalidateGuideLogMemo();

        Assert.False(library.Query.AnyGuideLogs());
    }

    [Fact]
    public void AnyGuideLogs_OnALibraryWithNoLogRow_IsFalse()
    {
        var (query, scope) = LibraryOfLogs();
        using var _ = scope;

        Assert.False(query.AnyGuideLogs());
    }

    [Fact]
    public void AnyGuideLogs_OnALibraryWithOneParsedLog_IsTrue()
    {
        var (query, scope) = LibraryOfLogs("ok");
        using var _ = scope;

        Assert.True(query.AnyGuideLogs());
    }

    [Fact]
    public void AnyGuideLogs_OnALibraryWhoseOnlyLogFailedToParse_IsStillTrue()
    {
        // The band's question is whether the library has ever seen a guide log, not whether one
        // parsed. A failure looks like a Where(l => l.ParseStatus == "ok").Any() that hides the
        // band on the library that most needs the section to say something.
        var (query, scope) = LibraryOfLogs("failed");
        using var _ = scope;

        Assert.True(query.AnyGuideLogs());
    }

    // Section 0 of the desktop guide log's shape: 400 frames at pixel scale 1.50, four drops in
    // two runs of two, one dither, two settles of which one failed.
    private static void LongSection(Phd2Session session)
    {
        session.FrameCount = 400;
        session.RmsRaArcsec = 0.600000;
        session.RmsDecArcsec = 0.450000;
        session.RmsTotalArcsec = 0.750000;
        session.DropCount = 4;
        session.MaxDropRun = 2;
        session.UnguidedSeconds = 3.0;
        // Three dithers against one failed settle: two same-typed neighbours in a twenty-four
        // argument constructor carrying the same value could be exchanged and stay green.
        session.DitherCount = 3;
        session.SettleCount = 2;
        session.SettleFailedCount = 1;
        session.SettleMedianS = 3.0;
    }

    // Section 1's shape: 60 frames, under the gate, and clean.
    private static void ShortSection(Phd2Session session)
    {
        session.FrameCount = 60;
        session.RmsRaArcsec = 0.600000;
        session.RmsDecArcsec = 0.450000;
        session.RmsTotalArcsec = 0.750000;
    }

    // ---- the live profile map (task5b-review.md P2-1) ---------------------------------------
    //
    // Every case here catalogues the session FIRST and maps the profile SECOND, the order the
    // defect lives in. The defect: the band labelled its rigs and picked its sessions from
    // phd2_sessions.telescope, which only the ingest writes, so a mapping made after the scan
    // never reached the pane and a re-scan did not repair the column either.

    [Fact]
    public void Get_ASessionWithNoStoredTelescope_TakesTheRigItsProfileIsMappedToAfterTheScan()
    {
        using var library = new Library();
        library.AddSession(Night, session =>
        {
            LongSection(session);
            session.Telescope = null;
        });

        Assert.Null(Assert.Single(library.Query.Get(Night).Sessions).Telescope);

        library.MapProfile(Profile, "Askar 120");

        // The label the band and the graph both read, and the rig the filter has to select by.
        Assert.Equal("Askar 120", Assert.Single(library.Query.Get(Night).Sessions).Telescope);
        Assert.Single(library.Query.Get(Night, "Askar 120").Sessions);
    }

    [Fact]
    public void Get_ASessionWhoseProfileTheMapDoesNotCarry_ResolvesToNoRig()
    {
        using var library = new Library();
        library.AddSession(Night, session =>
        {
            LongSection(session);
            session.Telescope = "Rig A";
        });
        library.MapProfile("SomeOtherProfile", "Askar 120");

        // REWRITTEN for phase-review.md F3. It used to expect "Rig A", the stored column, on the
        // strength of a fallback that made an unmapping impossible to perform: the Equipment tab
        // clears a telescope by dropping the entry, so "the map does not carry this profile" IS
        // the unmapped state and answering the stored column there means the rig a reader removed
        // speaks forever.
        Assert.Null(Assert.Single(library.Query.Get(Night).Sessions).Telescope);

        // The session is still reachable under that rig's filter, and for a different reason:
        // with no session on the night carrying a rig at all, spec 7.6 step 2's
        // sole-unmapped-profile branch attributes the night's one profile. That is the rig rule
        // speaking, not the stored column.
        Assert.Single(library.Query.Get(Night, "Rig A").Sessions);
    }

    [Fact]
    public void Get_AProfileTheReaderUnmaps_StopsAnsweringToThatRig()
    {
        // F3's own case, end to end through the members the Equipment tab writes with. Red
        // against the stored-column fallback: the session went on answering to "Rig B" after the
        // reader cleared the mapping, on every call, forever, because no rescan rewrites the
        // column either.
        using var library = new Library();
        library.AddSession(Night, session =>
        {
            LongSection(session);
            session.Telescope = "Rig B";
        });
        library.MapProfile(Profile, "Rig B");

        Assert.Equal("Rig B", Assert.Single(library.Query.Get(Night).Sessions).Telescope);

        // Clearing the telescope is exactly what the panel does, and SetTelescope drops an entry
        // that carries nothing else, so the profile leaves the map entirely.
        library.MapProfile(Profile, null);

        Assert.Null(Assert.Single(library.Query.Get(Night).Sessions).Telescope);
    }

    [Fact]
    public void Get_ASessionWhoseMapEntryCarriesANullTelescope_ResolvesToNoRig()
    {
        // The other unmapped shape: an entry kept because it still carries a zone. The map
        // carries the profile, so the lookup succeeds and answers null, which must mean no rig
        // rather than "fall through to something else".
        using var library = new Library();
        library.AddSession(Night, session =>
        {
            LongSection(session);
            session.Telescope = "Rig A";
        });
        library.Settings.MutateGeneral(general => general with
        {
            Phd2ProfileMap = Phd2Profiles.ToJson(new Dictionary<string, Phd2ProfileEntry>
            {
                [Profile] = new() { Timezone = "America/Chicago" },
            }),
        });

        Assert.Null(Assert.Single(library.Query.Get(Night).Sessions).Telescope);
    }

    [Fact]
    public void Get_ARemapMovesTheSession_AndTheFilterSelectsByTheResolvedRig()
    {
        using var library = new Library();
        library.AddSession(Night, session =>
        {
            LongSection(session);
            session.Telescope = "Rig A";
        });
        library.MapProfile(Profile, "Rig B");

        Assert.Equal("Rig B", Assert.Single(library.Query.Get(Night).Sessions).Telescope);
        Assert.Single(library.Query.Get(Night, "Rig B").Sessions);

        // The other direction, which is what a filter reading the stored column gets wrong while
        // still passing the assertion above: the session must no longer answer to its old rig.
        Assert.Empty(library.Query.Get(Night, "Rig A").Sessions);
    }

    [Fact]
    public void Get_RollsTheNightUpOverEverySessionItReturned()
    {
        using var library = new Library();
        library.AddSession(Night, LongSection);
        library.AddSession(Night, ShortSection);

        var night = library.Query.Get(Night);

        Assert.Equal(2, night.Sessions.Count);
        var summary = night.Summary;
        Assert.Equal(2, summary.SessionCount);
        Assert.Equal(1, summary.GatedSessionCount);
        Assert.Equal(460L, summary.FrameCount);
        Assert.Equal(0.600000, summary.RmsRaArcsec);
        Assert.Equal(0.450000, summary.RmsDecArcsec);
        Assert.Equal(0.750000, summary.RmsTotalArcsec);
        Assert.Equal(4, summary.DropCount);
        Assert.Equal(2, summary.MaxDropRun);
        Assert.Equal(3.0, summary.UnguidedSeconds);
        Assert.Equal(3, summary.DitherCount);
        Assert.Equal(1, summary.SettleFailedCount);
        Assert.Equal(3.0, summary.SettleMedianS);
        Assert.Empty(summary.CalIssues);
        Assert.Equal([Profile], summary.Profiles);
    }

    /// <summary>
    /// A failure looks like: the gated session's RMS counted into the rollup. The case above
    /// cannot see that, because both of its sessions read 0.750000 and an unweighted average of
    /// two equal numbers is the same number. Here the short session reads 1.500000, so a rollup
    /// that counted it reads sqrt((400 * 0.75^2 + 60 * 1.5^2) / 460), that is sqrt(360 / 460), that
    /// is 0.884651, and this case is red for its own reason.
    /// </summary>
    [Fact]
    public void Get_RollupExcludesTheRmsOfASessionUnderTheGate()
    {
        using var library = new Library();
        library.AddSession(Night, LongSection);
        library.AddSession(Night, session =>
        {
            ShortSection(session);
            session.RmsRaArcsec = 1.500000;
            session.RmsDecArcsec = 1.500000;
            session.RmsTotalArcsec = 1.500000;
        });

        var summary = library.Query.Get(Night).Summary;

        Assert.Equal(0.750000, summary.RmsTotalArcsec);
        Assert.Equal(0.600000, summary.RmsRaArcsec);
        Assert.Equal(0.450000, summary.RmsDecArcsec);
        Assert.Equal(460L, summary.FrameCount);
    }

    /// <summary>
    /// A failure looks like: a <c>&lt;=</c> in place of a <c>&lt;</c>, which makes the exactly
    /// hundred-frame session gated and drops its RMS out of every rollup.
    /// </summary>
    [Fact]
    public void Get_GatesBelowAHundredFramesAndNotAtIt()
    {
        using var library = new Library();
        library.AddSession(Night, session => session.FrameCount = 99);
        library.AddSession(Night, session => session.FrameCount = 100);
        library.AddSession(Night, session => session.FrameCount = 101);

        var night = library.Query.Get(Night);

        Assert.Equal([true, false, false], night.Sessions.Select(s => s.Gated));
        Assert.Equal(1, night.Summary.GatedSessionCount);
    }

    [Fact]
    public void Get_OnANightWithNoSession_IsTheEmptyRollupAndNotNull()
    {
        using var library = new Library();
        library.AddSession(new DateOnly(2025, 3, 18), LongSection);

        var night = library.Query.Get(Night, "Any Rig");

        Assert.Empty(night.Sessions);
        Assert.Equal(0, night.Summary.SessionCount);
        Assert.Equal(0L, night.Summary.FrameCount);
        Assert.Null(night.Summary.RmsTotalArcsec);
        Assert.Null(night.Summary.SettleMedianS);
        Assert.Empty(night.Summary.CalIssues);
        Assert.Empty(night.Summary.Profiles);
    }

    [Fact]
    public void Get_WithNoTelescope_ReturnsEverySessionOnTheNightIncludingOneMappedElsewhere()
    {
        using var library = new Library();
        var mine = library.AddSessionOnRig(Night, "RC8");
        var theirs = library.AddSessionOnRig(Night, "SVBony 80ED");

        Assert.Equal([mine, theirs], library.Query.Get(Night).Sessions.Select(s => s.Id));
        Assert.Equal([mine, theirs], library.Query.Get(Night, "").Sessions.Select(s => s.Id));
    }

    [Fact]
    public void Get_WithATelescope_ReturnsOnlyTheSessionsMappedToIt()
    {
        using var library = new Library();
        var mine = library.AddSessionOnRig(Night, "RC8");
        library.AddSessionOnRig(Night, "SVBony 80ED");

        Assert.Equal([mine], library.Query.Get(Night, "RC8").Sessions.Select(s => s.Id));
    }

    /// <summary>
    /// A failure looks like: the unmapped session returned for the rig nobody mapped it to, which
    /// is the sole-unmapped fallback firing although a mapped row exists, and which puts the
    /// guided rig's numbers on the other rig's card.
    /// </summary>
    [Fact]
    public void Get_WithOneMappedAndOneUnmappedSession_AttributesNothingToADifferentRig()
    {
        using var library = new Library();
        library.AddSessionOnRig(Night, "RC8");
        library.AddSession(Night, session => session.Telescope = null);

        Assert.Empty(library.Query.Get(Night, "SVBony 80ED").Sessions);
    }

    [Fact]
    public void Get_WithEverySessionUnmappedAndOneProfile_AttributesThemAllToTheRigAsked()
    {
        using var library = new Library();
        var first = library.AddSession(Night, session => session.Telescope = null);
        var second = library.AddSession(Night, session => session.Telescope = null);

        Assert.Equal([first, second], library.Query.Get(Night, "RC8").Sessions.Select(s => s.Id));
    }

    [Fact]
    public void Get_WithEverySessionUnmappedAndTwoProfiles_AttributesNothing()
    {
        using var library = new Library();
        library.AddSession(Night, session => session.Telescope = null);
        library.AddSession(Night, session =>
        {
            session.Telescope = null;
            session.EquipmentProfile = "RC8_ASI2600MM";
        });

        Assert.Empty(library.Query.Get(Night, "RC8").Sessions);
    }

    /// <summary>
    /// A null profile and an empty profile are the same profile, so a named profile beside a
    /// section with no profile line is two distinct profiles and attributes nothing. A failure
    /// looks like the null profile being dropped from the distinct count, which would attribute
    /// both sections to whichever rig was asked for.
    /// </summary>
    [Fact]
    public void Get_WithOneNamedProfileAndOneSectionWithNoProfileLine_AttributesNothing()
    {
        using var library = new Library();
        library.AddSession(Night, session => session.Telescope = null);
        library.AddSession(Night, session =>
        {
            session.Telescope = null;
            session.EquipmentProfile = null;
        });

        Assert.Empty(library.Query.Get(Night, "RC8").Sessions);
    }

    /// <summary>
    /// Phase 15B real-data finding 1: the picker now offers the library's discovered telescope
    /// names as well as the canonical group names, so a profile can be mapped to a raw name that
    /// belongs to no alias group at all. This is what the rig rule then has to do with such a name,
    /// and it is the query half of that fix.
    /// </summary>
    /// <remarks>
    /// Nothing here configures an equipment document, which is the state of the library the finding
    /// was raised on. <c>AliasMap.TelescopeMatchSet</c> folds with <c>CanonicalTelescope(raw) ?? raw</c>
    /// and expands an unconfigured canonical name to itself, so the wanted set is the raw name
    /// alone and it matches the frames spelled that way, which is the spelling the card asks with.
    /// </remarks>
    [Fact]
    public void Get_MatchesASessionMappedToATelescopeThatBelongsToNoAliasGroup()
    {
        using var library = new Library();
        var stored = library.AddSessionOnRig(Night, "SVBony 80ED");
        library.AddSessionOnRig(Night, "RC8");

        Assert.Equal([stored], library.Query.Get(Night, "SVBony 80ED").Sessions.Select(s => s.Id));
    }

    /// <summary>
    /// A failure looks like: nothing returned, because the comparison was a raw string equality
    /// against the stored spelling. Both sides are user data written at different times, and the
    /// wanted set carries the canonical name and every alias for exactly this reason.
    /// </summary>
    [Fact]
    public void Get_MatchesASessionStoredUnderAnAliasSpellingOfTheRigAsked()
    {
        using var library = new Library();
        library.MapTelescopeAlias("RC8", "RC8 Astrograph");
        var stored = library.AddSessionOnRig(Night, "RC8 Astrograph");
        library.AddSessionOnRig(Night, "SVBony 80ED");

        Assert.Equal([stored], library.Query.Get(Night, "RC8").Sessions.Select(s => s.Id));
    }

    [Fact]
    public void Get_RollsUpOnlyTheSessionsTheRigRuleReturned()
    {
        using var library = new Library();
        library.AddSessionOnRig(Night, "RC8", LongSection);
        library.AddSession(Night, session =>
        {
            LongSection(session);
            session.Telescope = "SVBony 80ED";
            session.EquipmentProfile = "RC8_ASI2600MM";
            session.RmsRaArcsec = 0.360000;
            session.RmsDecArcsec = 0.270000;
            session.RmsTotalArcsec = 0.450000;
        });

        var summary = library.Query.Get(Night, "RC8").Summary;

        Assert.Equal(1, summary.SessionCount);
        Assert.Equal(400L, summary.FrameCount);
        Assert.Equal(0.750000, summary.RmsTotalArcsec);

        // The rolled-up session's own profile, which its seed names after its rig now that the
        // rig can only be stated through the map (phase-review.md F3). The figure above is what
        // the case is about and is unchanged.
        Assert.Equal(["RC8_Cam"], summary.Profiles);
    }

    [Fact]
    public void Get_ReportsTheStoredRowWholeAndOrdersByStartTime()
    {
        using var library = new Library();
        var first = library.AddSession(Night, session =>
        {
            LongSection(session);
            session.LastCalIssue = "Star did not move enough";
            session.PierSide = "West";
            session.PeakRaArcsec = 1.200000;
            session.PeakDecArcsec = 0.900000;
            session.SnrMean = 24.5000;
            session.StarMassMean = 12345.6789;
        });
        var second = library.AddSession(Night, ShortSection);

        var night = library.Query.Get(Night);

        Assert.Equal([first, second], night.Sessions.Select(s => s.Id));
        var row = night.Sessions[0];

        // The UTC start, not the local wall clock five hours below it: spec 12.4 prints this value
        // in the session selector's label and the band reads it as the night's clock. A failure
        // looks like the assertion reading 16:00, which is a projection off the wrong column.
        Assert.Equal(new DateTime(2025, 3, 19, 21, 0, 0, DateTimeKind.Utc), row.StartedAtUtc);
        Assert.Equal(Profile, row.EquipmentProfile);
        Assert.Equal(1.50, row.PixelScaleArcsec);
        Assert.Equal(1.200000, row.PeakRaArcsec);
        Assert.Equal(0.900000, row.PeakDecArcsec);
        Assert.Equal(24.5000, row.SnrMean);
        Assert.Equal(12345.6789, row.StarMassMean);
        Assert.Equal("West", row.PierSide);
        Assert.Equal("Star did not move enough", row.LastCalIssue);
        Assert.Equal(2, row.SettleCount);
        Assert.False(row.Gated);
        Assert.True(night.Sessions[1].Gated);

        // The literal "None" is filtered by the rollup and a real issue is not, which is the only
        // fixture-free proof the calibration chip has data to show at all.
        Assert.Equal(["Star did not move enough"], night.Summary.CalIssues);
    }

    /// <summary>
    /// A failure looks like: a helpful local weighted RMS or gate added during the work because a
    /// projection felt awkward, which passes every figure case on the day it is written and drifts
    /// the first time the Core one is tuned. That is the duplication design lesson 1 names, and
    /// <c>Phd2OrphanGuardTests</c> is the house precedent for pinning it this way.
    /// </summary>
    [Fact]
    public void TheSourceScan_FindsNoSecondCopyOfTheRigRuleOrTheRollup()
    {
        var code = SourceScan.Read("src/GalactiLog.Data/Queries/Phd2NightQuery.cs");

        Assert.Contains("Phd2Metrics.SelectNightRows", code, StringComparison.Ordinal);
        Assert.Contains("Phd2Metrics.AggregateNight", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Sqrt", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Round", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MinFrames", code, StringComparison.Ordinal);

        // Reads only (spec 5.1, single writer), and no file system.
        Assert.DoesNotContain("ExecuteUpdate", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteDelete", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO", code, StringComparison.Ordinal);

        // phd2_frames is the volume table and the band shows stored aggregates only.
        Assert.DoesNotContain("Phd2Frames", code, StringComparison.Ordinal);

        // phase-review.md F7: the telescope match set has ONE home, AliasMap.TelescopeMatchSet,
        // which folds a raw name to canonical inside itself. Neither of the two callers may build
        // the set again by hand, which is what a second ExpandTelescope( call here would be.
        Assert.Contains("TelescopeMatchSet(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandTelescope(", code, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ExpandTelescope(",
            SourceScan.Read("src/GalactiLog.Data/Ingest/Phd2Correlation.cs"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="AliasMap.TelescopeMatchSet"/>, the one implementation of spec 7.6 step 1's
    /// expansion, lives here beside its first caller (phase-review.md F7).
    /// </summary>
    [Fact]
    public void TelescopeMatchSet_FoldsToCanonicalFirst_ThenExpandsToEverySpelling()
    {
        var map = new AliasMap(
            new Dictionary<string, FilterSetting>(),
            new EquipmentSettings
            {
                Telescopes = new Dictionary<string, EquipmentItemSettings>
                {
                    ["SVBony 80ED"] = new() { Aliases = ["SVBony SV503 80mm"] },
                },
            });

        // The canonical name and an alias spelling answer the SAME set. That is the whole reason
        // the fold happens inside the member: the raw comparison this replaces missed a night
        // shot on "SVBony SV503 80mm" whose profile was mapped to "SVBony 80ED".
        var fromCanonical = map.TelescopeMatchSet("SVBony 80ED");
        var fromAlias = map.TelescopeMatchSet("SVBony SV503 80mm");

        Assert.Equal(
            new[] { "SVBony 80ED", "SVBony SV503 80mm" }.Order(StringComparer.Ordinal),
            fromCanonical.Order(StringComparer.Ordinal));
        Assert.Equal(
            fromCanonical.Order(StringComparer.Ordinal),
            fromAlias.Order(StringComparer.Ordinal));

        // Case-insensitive, which is what both call sites compare with.
        Assert.Contains("svbony sv503 80mm", fromCanonical);

        // An unconfigured name answers to itself alone, never to the empty set: a night shot on a
        // rig the user never listed in Settings must still match its own sessions.
        Assert.Equal("Askar 120", Assert.Single(map.TelescopeMatchSet("Askar 120")));
    }

}
