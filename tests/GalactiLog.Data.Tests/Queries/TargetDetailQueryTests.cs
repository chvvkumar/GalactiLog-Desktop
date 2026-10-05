using System.Diagnostics;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Data.Tests.Queries;

public class TargetDetailQueryTests(ITestOutputHelper output)
{
    private static readonly DateOnly Day = new(2025, 3, 1);

    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;
        private int _guidingSessions;

        private Library(TestDatabaseHandle db)
        {
            _db = db;
            Settings = new SettingsStore(new SettingsRepository(db.ConnectionString));
            _aliases = new AliasMapCache(Settings);
            Query = new TargetDetailQuery(
                new DatabaseConnectionString(db.ConnectionString),
                _aliases,
                // The live profile map, read per call, which is how the guiding session count
                // resolves each session's rig (phase-review.md F3).
                () => Settings.GetGeneral().Phd2ProfileMap);
            Listing = new TargetListingQuery(new DatabaseConnectionString(db.ConnectionString), _aliases);
            Guiding = new Phd2NightQuery(
                new DatabaseConnectionString(db.ConnectionString),
                _aliases,
                () => Settings.GetGeneral().Phd2ProfileMap);
        }

        public string ConnectionString => _db.ConnectionString;
        public SettingsStore Settings { get; }
        public TargetDetailQuery Query { get; }

        /// <summary>The dashboard listing, so a group-key assertion can name the exact string a
        /// row click would hand to <see cref="Query"/> rather than a hand-built one.</summary>
        public TargetListingQuery Listing { get; }

        /// <summary>The Guiding band's own query, so the closed band's count can be asserted
        /// against what the opened band would list rather than against a hand-counted figure.
        /// </summary>
        public Phd2NightQuery Guiding { get; }

        /// <summary>One <c>phd2_sessions</c> row on <paramref name="night"/> under
        /// <paramref name="profile"/>, with a <c>phd2_logs</c> parent, seeded the way the band's
        /// own cases seed one.</summary>
        public void AddGuidingSession(DateOnly night, string profile)
        {
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(ConnectionString, tracking: true));

            var logId = Guid.NewGuid();
            context.Phd2Logs.Add(new Phd2Log
            {
                Id = logId,
                FilePath = $@"C:\Astro\guide\PHD2_GuideLog_{logId:N}.txt",
                ParseStatus = "ok",
                ParsedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            });

            var started = new DateTime(night.Year, night.Month, night.Day, 21, 0, 0, DateTimeKind.Utc)
                .AddMinutes(_guidingSessions++);
            context.Phd2Sessions.Add(new Phd2Session
            {
                Id = Guid.NewGuid(),
                LogId = logId,
                StartedAtLocal = DateTime.SpecifyKind(started, DateTimeKind.Unspecified),
                StartedAtUtc = started,
                EndedAtUtc = started.AddMinutes(5),
                SessionDate = night,
                DurationS = 300,
                EquipmentProfile = profile,
                PixelScaleArcsec = 1.50,
                FrameCount = 400,
            });
            context.SaveChanges();
        }

        /// <summary>Maps a PHD2 equipment profile to a telescope, the way the Equipment tab's
        /// panel writes it.</summary>
        public void MapProfile(string profile, string? telescope)
            => Settings.MutateGeneral(general => general with
            {
                Phd2ProfileMap = Phd2Profiles.ToJson(
                    Phd2Profiles.SetTelescope(general.Phd2ProfileMap, profile, telescope)),
            });

        public static Library Empty() => new(TestDatabaseFactory.CreateMigratedDatabase());

        public static Library Seeded()
        {
            var library = Empty();
            LibrarySeeder.Seed(library.ConnectionString);
            return library;
        }

        public Target AddTarget(string primaryName, Action<Target>? configure = null)
            => LibrarySeeder.AddTarget(ConnectionString, primaryName, configure);

        public Image AddFrame(Guid? targetId, DateOnly sessionDate, Action<Image>? configure = null)
            => LibrarySeeder.AddFrame(ConnectionString, targetId, sessionDate, configure);

        /// <summary>A resolved target plus one frame per supplied shape, all on the same date.</summary>
        public Guid AddGroup(string primaryName, params Action<Image>[] frames)
        {
            var target = AddTarget(primaryName);
            foreach (var frame in frames)
            {
                AddFrame(target.Id, Day, frame);
            }

            return target.Id;
        }

        public void AddNote(Guid targetId, DateOnly sessionDate)
        {
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(ConnectionString, tracking: true));
            context.SessionNotes.Add(new SessionNote
            {
                Id = Guid.NewGuid(),
                TargetId = targetId,
                SessionDate = sessionDate,
                Notes = "clouds after 02:00",
                UpdatedAt = DateTime.UtcNow,
            });
            context.SaveChanges();
        }

        public void AddMembership(Guid targetId, string catalogName, string catalogNumber, string? metadata = null)
        {
            using var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(ConnectionString, tracking: true));
            context.TargetCatalogMemberships.Add(new TargetCatalogMembership
            {
                TargetId = targetId,
                CatalogName = catalogName,
                CatalogNumber = catalogNumber,
                Metadata = metadata,
            });
            context.SaveChanges();
        }

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    private static TargetDetail Detail(Library library, Guid targetId)
    {
        var detail = library.Query.Get(targetId.ToString());
        Assert.NotNull(detail);
        return detail;
    }

    // ---- the closed Guiding band's session count (spec 12.4, task3-review.md P2-2) ----------

    /// <summary>
    /// A closed Guiding band states how many guiding sessions the night holds for THIS card, and
    /// the figure is read with the page's own night list rather than with the band's query, so
    /// nothing is issued until the band opens.
    /// </summary>
    /// <remarks>
    /// The assertion is against <see cref="Phd2NightQuery.Get"/>'s own answer and not against a
    /// hand-counted number, because the two disagreeing is the defect: the closed band and the
    /// opened band must be one rule read twice. On a single-rig card the band narrows to that
    /// card's rig, which is where the web narrows it (phase-review.md F1).
    /// </remarks>
    [Fact]
    public void ASingleRigNight_CountsOnlyThatRigsGuidingSessions()
    {
        using var library = Library.Empty();
        var targetId = library.AddGroup(
            "M42",
            frame => { frame.Telescope = "Rig A"; frame.Camera = "Cam A"; });

        library.AddGuidingSession(Day, "P_RigA");
        library.AddGuidingSession(Day, "P_RigA");
        library.AddGuidingSession(Day, "P_RigB");
        library.MapProfile("P_RigA", "Rig A");
        library.MapProfile("P_RigB", "Rig B");

        var session = Assert.Single(Detail(library, targetId).Sessions);

        Assert.Equal(1, session.RigCount);
        Assert.Equal(2, session.GuidingSessionCount);
        Assert.Equal(
            library.Guiding.Get(Day, "Rig A").Sessions.Count,
            session.GuidingSessionCount);
    }

    [Fact]
    public void AMultiRigNight_CountsTheWholeNight()
    {
        // A card whose own night carries several rigs is the case the band does not narrow, so
        // the count must not narrow either or the closed and the opened band disagree.
        using var library = Library.Empty();
        var targetId = library.AddGroup(
            "M42",
            frame => { frame.Telescope = "Rig A"; frame.Camera = "Cam A"; },
            frame => { frame.Telescope = "Rig B"; frame.Camera = "Cam B"; });

        library.AddGuidingSession(Day, "P_RigA");
        library.AddGuidingSession(Day, "P_RigB");
        library.MapProfile("P_RigA", "Rig A");
        library.MapProfile("P_RigB", "Rig B");

        var session = Assert.Single(Detail(library, targetId).Sessions);

        Assert.Equal(2, session.RigCount);
        Assert.Equal(2, session.GuidingSessionCount);
        Assert.Equal(library.Guiding.Get(Day).Sessions.Count, session.GuidingSessionCount);
    }

    [Fact]
    public void ANightWithNoGuidingSession_CountsZero()
    {
        // Zero, never a null and never a guess, and it is also what a library with no guide log
        // at all reports, which is the library the band is hidden on.
        using var library = Library.Empty();
        var targetId = library.AddGroup(
            "M42",
            frame => { frame.Telescope = "Rig A"; frame.Camera = "Cam A"; });

        Assert.Equal(0, Assert.Single(Detail(library, targetId).Sessions).GuidingSessionCount);
    }

    [Fact]
    public void AProfileTheReaderUnmaps_DropsOutOfTheCount()
    {
        // The count resolves the rig through the LIVE map, exactly as the band does
        // (phase-review.md F3). Red against a count taken from the stored telescope column, and
        // red against a count that ignores the rig altogether.
        using var library = Library.Empty();
        var targetId = library.AddGroup(
            "M42",
            frame => { frame.Telescope = "Rig A"; frame.Camera = "Cam A"; });

        library.AddGuidingSession(Day, "P_RigA");
        library.AddGuidingSession(Day, "P_Other");
        library.MapProfile("P_RigA", "Rig A");
        library.MapProfile("P_Other", "Rig B");

        Assert.Equal(1, Assert.Single(Detail(library, targetId).Sessions).GuidingSessionCount);

        library.MapProfile("P_RigA", null);

        var after = Assert.Single(Detail(library, targetId).Sessions);

        Assert.Equal(0, after.GuidingSessionCount);
        Assert.Equal(library.Guiding.Get(Day, "Rig A").Sessions.Count, after.GuidingSessionCount);
    }

    // ---- the seeded library ------------------------------------------------------------

    [Fact]
    public void Get_SeededTarget_ReportsTheTotalsTheSeederDeclares()
    {
        using var library = Library.Seeded();

        foreach (var seeded in LibrarySeeder.Targets)
        {
            var detail = Detail(library, seeded.Id);

            Assert.Equal(seeded.PrimaryName, detail.Header.PrimaryName);
            Assert.Equal(seeded.ObjectType, detail.Header.ObjectType);
            Assert.Equal(seeded.ObjectCategory, detail.Header.ObjectCategory);
            Assert.Equal(seeded.Aliases, detail.Header.Aliases);
            Assert.Equal(seeded.IntegrationSeconds, detail.Totals.IntegrationSeconds);
            Assert.Equal(seeded.FrameCount, detail.Totals.FrameCount);
            Assert.Equal(seeded.SessionCount, detail.Totals.SessionCount);
            Assert.Equal(seeded.FirstSession, detail.Totals.FirstSessionDate);
            Assert.Equal(seeded.LastSession, detail.Totals.LastSessionDate);
            Assert.Equal(seeded.SessionCount, detail.Sessions.Count);
            Assert.Equal(seeded.FrameCount, detail.FramePaths.Count);
        }
    }

    // ---- the header block --------------------------------------------------------------

    [Fact]
    public void Get_HeaderBlock_BindsEveryTargetsColumn()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("NGC 7000", t =>
        {
            t.CatalogId = "NGC 7000";
            t.CommonName = "North America Nebula";
            t.Aliases = """["Caldwell 20","Sh2-117"]""";
            t.Ra = 314.7;
            t.Dec = 44.31;
            t.ObjectType = "HII";
            t.Constellation = "Cyg";
            t.SizeMajor = 120.0;
            t.SizeMinor = 100.0;
            t.PositionAngle = 35.5;
            t.VMag = 4.0;
            t.SurfaceBrightness = 22.5;
            t.SacDescription = "large emission nebula";
            t.SacNotes = "needs a narrowband filter";
            t.Notes = "revisit in Ha";
            t.ReferenceThumbnailPath = @"C:\GalactiLogFixture\thumbs\ngc7000.jpg";
            t.NameLocked = true;
            t.UserDefined = true;
        });
        library.AddFrame(target.Id, Day);
        library.AddMembership(target.Id, "Caldwell", "20", """{"source":"static"}""");
        library.AddMembership(target.Id, "Sharpless", "117");

        var detail = Detail(library, target.Id);
        var header = detail.Header;

        Assert.Equal(target.Id, Guid.Parse(header.GroupKey));
        Assert.Equal(target.Id, header.TargetId);
        Assert.Equal("NGC 7000", header.PrimaryName);
        Assert.Equal(["Caldwell 20", "Sh2-117"], header.Aliases);
        Assert.Equal("HII", header.ObjectType);
        Assert.Equal("Emission Nebula", header.ObjectCategory);
        Assert.Equal("Cyg", header.Constellation);
        Assert.Equal(314.7, header.Ra);
        Assert.Equal(44.31, header.Dec);
        Assert.Equal(120.0, header.SizeMajor);
        Assert.Equal(100.0, header.SizeMinor);
        Assert.Equal(35.5, header.PositionAngle);
        Assert.Equal(4.0, header.VMag);
        Assert.Equal(22.5, header.SurfaceBrightness);
        Assert.Equal("large emission nebula", header.SacDescription);
        Assert.Equal("needs a narrowband filter", header.SacNotes);
        Assert.Equal("revisit in Ha", header.Notes);
        Assert.Equal(@"C:\GalactiLogFixture\thumbs\ngc7000.jpg", header.ReferenceThumbnailPath);
        Assert.True(header.NameLocked);
        Assert.True(header.UserDefined);
        Assert.Equal(
            [("Caldwell", "20", """{"source":"static"}"""), ("Sharpless", "117", null)],
            header.CatalogMemberships.Select(badge =>
                (badge.CatalogName, badge.CatalogNumber, badge.Metadata)));
    }

    [Fact]
    public void Get_HeaderBlock_MalformedAliasesJson_YieldsAnEmptyList()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 81", t => t.Aliases = "{not json at all");
        library.AddFrame(target.Id, Day);

        var detail = Detail(library, target.Id);

        Assert.Empty(detail.Header.Aliases);
    }

    [Fact]
    public void Get_HeaderBlock_GroupKey_IsTheStorageForm()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 101");
        library.AddFrame(target.Id, Day);
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Abell 39"));

        // Review ruling: the header echoes the key the dashboard row carries, whatever text form
        // the caller used. Guid.ToString() is lower-case, Microsoft.Data.Sqlite stores TEXT
        // upper-case, and "B" adds braces, so all three inputs differ from each other and from
        // the storage form; all three must come back as the dashboard's key.
        var rowKey = Assert.Single(
            library.Listing.List(new TargetListingCriteria { TargetId = target.Id }).Rows).GroupKey;

        foreach (var form in new[]
                 {
                     target.Id.ToString(),
                     target.Id.ToString().ToUpperInvariant(),
                     target.Id.ToString("B"),
                     target.Id.ToString("N"),
                 })
        {
            var detail = library.Query.Get(form);
            Assert.NotNull(detail);
            Assert.Equal(rowKey, detail.Header.GroupKey);
        }

        // An obj: key is compared as text, so its storage form is the string itself, and it is
        // the same string the dashboard row carries.
        var unresolvedRowKey = Assert.Single(
            library.Listing.List(new TargetListingCriteria { UnresolvedObject = "Abell 39" }).Rows).GroupKey;
        var unresolved = library.Query.Get("obj:Abell 39");
        Assert.NotNull(unresolved);
        Assert.Equal("obj:Abell 39", unresolvedRowKey);
        Assert.Equal(unresolvedRowKey, unresolved.Header.GroupKey);
    }

    // ---- the reference frame's field (P12 ruling Q6) -----------------------------------
    //
    // The scale bar over the reference thumbnail measures that frame's field, so both figures
    // must come from the same frame: the most recent LIGHT frame by capture date, which is the
    // frame the reference thumbnail pass prefers.

    /// <summary>A raw_headers document carrying a frame width, which is the only card these cases
    /// read besides OBJECT. <c>images</c> has no width column, so spec 6.1.2's stored header is
    /// the source.</summary>
    private static string RawHeadersWithWidth(int naxis1)
        => $$"""{"OBJECT":"NGC 7000","NAXIS1":{{naxis1}}}""";

    [Fact]
    public void Get_HeaderBlock_CarriesTheNewestFramesPlateScaleAndWidth()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("NGC 7000");

        // Oldest first, so a query that forgot its ORDER BY or reversed it picks a decoy.
        library.AddFrame(target.Id, Day, frame =>
        {
            frame.CaptureDate = Day.ToDateTime(new TimeOnly(21, 0));
            frame.ArcsecPerPixel = 9.99;
            frame.RawHeaders = RawHeadersWithWidth(100);
        });
        library.AddFrame(target.Id, Day.AddDays(1), frame =>
        {
            frame.CaptureDate = Day.AddDays(1).ToDateTime(new TimeOnly(21, 0));
            frame.ArcsecPerPixel = 0.62;
            frame.RawHeaders = RawHeadersWithWidth(9576);
        });
        // A frame with no capture date is not the newest frame: it is a frame with no place in
        // the ordering at all, and the subselect excludes it.
        library.AddFrame(target.Id, Day.AddDays(2), frame =>
        {
            frame.CaptureDate = null;
            frame.ArcsecPerPixel = 5.55;
            frame.RawHeaders = RawHeadersWithWidth(1);
        });

        var header = Detail(library, target.Id).Header;

        Assert.Equal(0.62, header.ReferenceArcsecPerPixel);
        Assert.Equal(9576, header.ReferenceFrameWidthPixels);
    }

    [Fact]
    public void Get_HeaderBlock_WithTwoFramesOnTheNewestCaptureDate_ReadsBothFieldsFromOneFrame()
    {
        // P12 review P2-3. Two LIGHT frames share the newest capture_date and carry different
        // plate scales and different widths. The scale bar measures one frame's field, so the
        // pair must come from one frame; two independent LIMIT 1 subselects were free to answer
        // from two different rows here. Which of the tied frames wins is the id tiebreaker's
        // business and is not asserted: that it is one frame and not half of each is.
        using var library = Library.Empty();
        var target = library.AddTarget("NGC 7331");

        var tie = Day.AddDays(1).ToDateTime(new TimeOnly(22, 0));
        library.AddFrame(target.Id, Day, frame =>
        {
            frame.CaptureDate = Day.ToDateTime(new TimeOnly(21, 0));
            frame.ArcsecPerPixel = 9.99;
            frame.RawHeaders = RawHeadersWithWidth(100);
        });
        library.AddFrame(target.Id, Day.AddDays(1), frame =>
        {
            frame.CaptureDate = tie;
            frame.ArcsecPerPixel = 0.62;
            frame.RawHeaders = RawHeadersWithWidth(9576);
        });
        library.AddFrame(target.Id, Day.AddDays(1), frame =>
        {
            frame.CaptureDate = tie;
            frame.ArcsecPerPixel = 1.31;
            frame.RawHeaders = RawHeadersWithWidth(4144);
        });

        var header = Detail(library, target.Id).Header;

        Assert.NotNull(header.ReferenceArcsecPerPixel);
        Assert.NotNull(header.ReferenceFrameWidthPixels);
        var pair = (header.ReferenceArcsecPerPixel, header.ReferenceFrameWidthPixels);
        Assert.True(
            pair == (0.62, 9576) || pair == (1.31, 4144),
            $"plate scale {header.ReferenceArcsecPerPixel} and width {header.ReferenceFrameWidthPixels} "
                + "are not the same frame's");
    }

    [Fact]
    public void Get_HeaderBlock_WithNoPlateScale_LeavesBothNull()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 101");
        library.AddFrame(target.Id, Day, frame =>
        {
            frame.ArcsecPerPixel = null;
            frame.RawHeaders = null;
        });
        // A stored header with no NAXIS1 card is the same answer as no header at all: null, not
        // zero, so the caller draws no bar rather than a bar of no length.
        library.AddFrame(target.Id, Day, frame =>
        {
            frame.ArcsecPerPixel = null;
            frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("M 101");
        });

        var header = Detail(library, target.Id).Header;

        Assert.Null(header.ReferenceArcsecPerPixel);
        Assert.Null(header.ReferenceFrameWidthPixels);
    }

    [Fact]
    public void Get_HeaderBlock_ForAnUnresolvedGroup_LeavesTheReferenceFieldsNull()
    {
        using var library = Library.Empty();
        library.AddFrame(null, Day, frame =>
        {
            frame.ArcsecPerPixel = 0.62;
            frame.RawHeaders = RawHeadersWithWidth(9576);
        });

        var detail = library.Query.Get("obj:NGC 7000");

        Assert.NotNull(detail);
        // An obj: group has no targets row, so it has no reference thumbnail either and there is
        // nothing for a scale bar to sit over.
        Assert.Null(detail.Header.ReferenceThumbnailPath);
        Assert.Null(detail.Header.ReferenceArcsecPerPixel);
        Assert.Null(detail.Header.ReferenceFrameWidthPixels);
    }

    // ---- the metric columns ------------------------------------------------------------

    [Fact]
    public void Get_BindsFwhmGuidingRmsAndDetectedStars()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Distinct metrics",
            frame =>
            {
                frame.Fwhm = 3.10;
                frame.GuidingRmsArcsec = 0.41;
                frame.DetectedStars = 1100;
            },
            frame =>
            {
                frame.Fwhm = 4.90;
                frame.GuidingRmsArcsec = 0.83;
                frame.DetectedStars = 1900;
            });

        var detail = Detail(library, target);

        // Three different magnitudes on purpose: swapping any two of statement 1's ordinals for
        // fwhm, guiding_rms_arcsec and detected_stars fails this test rather than passing
        // silently.
        Assert.Equal(4.0, detail.Totals.AvgFwhm!.Value, 10);
        Assert.Equal(0.62, detail.Totals.AvgGuidingRmsArcsec!.Value, 10);
        Assert.Equal(1500d, detail.Totals.AvgDetectedStars!.Value, 10);

        var session = Assert.Single(detail.Sessions);
        Assert.Equal(4.0, session.MedianFwhm!.Value, 10);
        Assert.Equal(0.62, session.MedianGuidingRmsArcsec!.Value, 10);
        Assert.Equal(1500d, session.MedianDetectedStars!.Value, 10);
    }

    // ---- Phase 15A: the guiding provenance summary, computed once by this query and read by
    // both the session pane's facts line and the ledger cell's mark (task7.md section 7.3) -------

    [Fact]
    public void Get_ANightWithBothCsvAndPhd2Sources_ReportsGuidingProvenanceMixed()
    {
        // task7.md 7.3 case 9: a night that mixes the two sources is the case a facts line or a
        // ledger cell that derived its own answer, instead of reading this one summary, would get
        // wrong first. No fixture library carries a phd2 source yet (Task 5 has not landed), so
        // this seeds the column directly rather than running a correlation, which is the right
        // shape for a view-model case anyway. Both frames carry an RMS: the review ruling counts a
        // source only on an RMS-bearing frame.
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Mixed guiding sources",
            frame => { frame.GuidingRmsArcsec = 0.41; frame.GuidingRmsSource = "csv"; },
            frame => { frame.GuidingRmsArcsec = 0.52; frame.GuidingRmsSource = "phd2"; });

        var session = Assert.Single(Detail(library, target).Sessions);

        Assert.Equal(GuidingRmsProvenance.Mixed, session.GuidingProvenance);
    }

    [Fact]
    public void Get_ANightWithOnlyCsvSources_ReportsGuidingProvenanceCsv()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Csv-only guiding source",
            frame => { frame.GuidingRmsArcsec = 0.41; frame.GuidingRmsSource = "csv"; },
            frame => { frame.GuidingRmsArcsec = 0.44; frame.GuidingRmsSource = "csv"; });

        var session = Assert.Single(Detail(library, target).Sessions);

        Assert.Equal(GuidingRmsProvenance.Csv, session.GuidingProvenance);
    }

    [Fact]
    public void Get_ANightWithOnlyPhd2Sources_ReportsGuidingProvenancePhd2()
    {
        // Review ruling: the unqualified "from a PHD2 guide log" sentence requires every
        // RMS-bearing frame of the night to be phd2, which this case is the positive proof of.
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Phd2-only guiding source",
            frame => { frame.GuidingRmsArcsec = 0.52; frame.GuidingRmsSource = "phd2"; },
            frame => { frame.GuidingRmsArcsec = 0.58; frame.GuidingRmsSource = "phd2"; });

        var session = Assert.Single(Detail(library, target).Sessions);

        Assert.Equal(GuidingRmsProvenance.Phd2, session.GuidingProvenance);
    }

    [Fact]
    public void Get_ANightWhoseFramesAllCarryANullSource_ReportsGuidingProvenanceNoneNotNull()
    {
        // task7.md 7.3 case 10: the summary is a value, never a null a caller has to guard
        // against, which is the whole reason it is a four-valued token and not two booleans. No
        // RMS is set either, so this is also the "no RMS at all" arm of the review ruling: a frame
        // with a null source AND no RMS must not be confused with one that has an RMS and a null
        // source (the next case).
        using var library = Library.Empty();
        var target = library.AddGroup(
            "No guiding source at all",
            frame => frame.GuidingRmsSource = null,
            frame => frame.GuidingRmsSource = null);

        var session = Assert.Single(Detail(library, target).Sessions);

        Assert.Equal(GuidingRmsProvenance.None, session.GuidingProvenance);
    }

    [Fact]
    public void Get_ANightWithARealRmsAndANullSourceBesideAPhd2Frame_ReportsMixedNotPhd2()
    {
        // Review ruling on the reviewer's null-source escalation: only NinaCsvReader stamps "csv",
        // and only when the CSV row supplied a guiding field, so a header-derived RMS, and every
        // row written before that stamp existed, carries a real guiding_rms_arcsec with a null
        // source. Such a frame is not from a guide log, so it must pull the night to Mixed beside
        // a real phd2 frame, not let the phd2 frame alone read as "unqualified".
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Real RMS, null source, beside a phd2 frame",
            frame => { frame.GuidingRmsArcsec = 0.37; frame.GuidingRmsSource = null; },
            frame => { frame.GuidingRmsArcsec = 0.52; frame.GuidingRmsSource = "phd2"; });

        var session = Assert.Single(Detail(library, target).Sessions);

        Assert.Equal(GuidingRmsProvenance.Mixed, session.GuidingProvenance);
    }

    [Fact]
    public void Get_ANightWithAnUnrecognizedSourceString_CountsAsNotFromAGuideLog()
    {
        // The review's P3-3: an unrecognized token must not be silently dropped from the count (it
        // used to read as if the frame carried no source at all). It is not phd2, so it is "other"
        // exactly like csv or null, and pulls a night that also has a phd2 frame to Mixed.
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Unrecognized source string",
            frame => { frame.GuidingRmsArcsec = 0.29; frame.GuidingRmsSource = "guidemaster"; },
            frame => { frame.GuidingRmsArcsec = 0.52; frame.GuidingRmsSource = "phd2"; });

        var session = Assert.Single(Detail(library, target).Sessions);

        Assert.Equal(GuidingRmsProvenance.Mixed, session.GuidingProvenance);
    }

    [Fact]
    public void Get_ANightWithGuidingRmsButNoSourceAndNoOtherFrame_ReportsCsvNotNone()
    {
        // The other half of the ruling: an RMS-bearing frame with a null source, alone on the
        // night, is "not from a guide log" (Csv, no mark) rather than "nothing measured" (None, no
        // figure at all). The two must stay distinguishable, because Csv still draws the figure.
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Real RMS, null source, alone",
            frame => { frame.GuidingRmsArcsec = 0.37; frame.GuidingRmsSource = null; });

        var session = Assert.Single(Detail(library, target).Sessions);

        Assert.Equal(GuidingRmsProvenance.Csv, session.GuidingProvenance);
        Assert.NotNull(session.MedianGuidingRmsArcsec);
    }

    [Fact]
    public void Get_OnTheSeededLibrary_ReportsTheGuidingRmsTheSeederDeclares()
    {
        using var library = Library.Seeded();

        var detail = Detail(library, LibrarySeeder.Targets[0].Id);

        // The seeder cycles GuidingRmsBase + (j % 4) * 0.05 within every session, so both middle
        // values of every session land on the second step whatever the session's frame parity.
        var expectedMedian = Math.Round(LibrarySeeder.GuidingRmsBase + 0.05, 2);
        foreach (var session in detail.Sessions)
        {
            Assert.Equal(expectedMedian, session.MedianGuidingRmsArcsec!.Value, 10);
        }

        Assert.InRange(
            detail.Totals.AvgGuidingRmsArcsec!.Value,
            LibrarySeeder.GuidingRmsBase,
            LibrarySeeder.GuidingRmsBase + 0.15);
    }

    // ---- the arcsecond conversion (spec 18.1 "Detail queries") -------------------------

    [Fact]
    public void Get_HfrArcsec_PoolsOnlyPlateScaledFrames()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Scaled",
            frame => { frame.MedianHfr = 2.0; frame.ArcsecPerPixel = 0.5; },
            frame => { frame.MedianHfr = 3.0; frame.ArcsecPerPixel = null; });

        var detail = Detail(library, target);

        Assert.Equal(1.0, detail.Totals.AvgHfrArcsec);
        Assert.Equal(2.5, detail.Totals.AvgHfr);
    }

    [Fact]
    public void Get_HfrArcsec_ReportsTheExcludedFrameCount()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Partly scaled",
            frame => { frame.MedianHfr = 2.0; frame.ArcsecPerPixel = 0.5; },
            frame => { frame.MedianHfr = 3.0; frame.ArcsecPerPixel = null; },
            frame => { frame.MedianHfr = 4.0; frame.ArcsecPerPixel = null; });

        var detail = Detail(library, target);

        Assert.Equal(2, detail.Totals.HfrArcsecExcludedCount);
    }

    [Fact]
    public void Get_HfrArcsec_OnTheSeededLibrary_ExcludesTheSessionWithoutAPlateScale()
    {
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[1];

        var detail = Detail(library, seeded.Id);

        // The seeder leaves exactly one session without a plate scale; every other frame of the
        // target converts. This is the assertion that catches a forgotten CloneImage column.
        Assert.Equal(LibrarySeeder.FramesWithoutPlateScale, detail.Totals.HfrArcsecExcludedCount);
        var withoutScale = Assert.Single(
            detail.Sessions,
            session => session.SessionDate == LibrarySeeder.SessionWithoutPlateScale);
        Assert.Equal(LibrarySeeder.FramesWithoutPlateScale, withoutScale.HfrArcsecExcludedCount);
        Assert.Null(withoutScale.MedianHfrArcsec);
        foreach (var session in detail.Sessions.Where(s => s.SessionDate != LibrarySeeder.SessionWithoutPlateScale))
        {
            Assert.Equal(0, session.HfrArcsecExcludedCount);
            Assert.NotNull(session.MedianHfrArcsec);
        }
    }

    [Fact]
    public void Get_HfrArcsec_AFrameWithNoHfrIsNotCountedAsExcluded()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "No HFR",
            frame => { frame.MedianHfr = null; frame.ArcsecPerPixel = null; },
            frame => { frame.MedianHfr = 2.0; frame.ArcsecPerPixel = 0.5; });

        var detail = Detail(library, target);

        Assert.Equal(0, detail.Totals.HfrArcsecExcludedCount);
        Assert.Equal(1.0, detail.Totals.AvgHfrArcsec);
        Assert.Equal(2.0, detail.Totals.AvgHfr);
    }

    [Fact]
    public void Get_HfrArcsec_NonPositivePlateScale_IsExcluded()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Bad plate scale",
            frame => { frame.MedianHfr = 2.0; frame.ArcsecPerPixel = 0d; },
            frame => { frame.MedianHfr = 2.0; frame.ArcsecPerPixel = -1.5; });

        var detail = Detail(library, target);

        Assert.Equal(2, detail.Totals.HfrArcsecExcludedCount);
        Assert.Null(detail.Totals.AvgHfrArcsec);
    }

    // ---- the eccentricity modal source (spec 7.2) --------------------------------------

    [Fact]
    public void Get_Eccentricity_PoolsOnlyTheModalSource()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Mixed sources",
            frame => { frame.Eccentricity = 0.30; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.40; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.50; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.90; frame.EccentricitySource = "csv"; },
            frame => { frame.Eccentricity = 0.95; frame.EccentricitySource = "csv"; });

        var detail = Detail(library, target);

        Assert.Equal(0.40, detail.Totals.AvgEccentricity!.Value, 10);
    }

    [Fact]
    public void Get_Eccentricity_ReportsTheExcludedCountAndTheModalSource()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Mixed sources",
            frame => { frame.Eccentricity = 0.30; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.40; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.50; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.90; frame.EccentricitySource = "csv"; },
            frame => { frame.Eccentricity = 0.95; frame.EccentricitySource = "csv"; });

        var detail = Detail(library, target);

        Assert.Equal("header", detail.Totals.EccentricityModalSource);
        Assert.Equal(2, detail.Totals.EccentricityExcludedCount);
    }

    [Fact]
    public void Get_Eccentricity_OnTheSeededLibrary_ExcludesTheMinoritySource()
    {
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[3];

        var detail = Detail(library, seeded.Id);

        Assert.Equal(LibrarySeeder.EccentricitySourceHeader, detail.Totals.EccentricityModalSource);
        Assert.Equal(
            LibrarySeeder.FramesWithMinorityEccentricitySource,
            detail.Totals.EccentricityExcludedCount);
    }

    [Fact]
    public void Get_Eccentricity_TieOnCount_BreaksOnNullLastThenOrdinal()
    {
        using var nullVersusNamed = Library.Empty();
        var tieWithNull = nullVersusNamed.AddGroup(
            "Tie with a null source",
            frame => { frame.Eccentricity = 0.90; frame.EccentricitySource = null; },
            frame => { frame.Eccentricity = 0.20; frame.EccentricitySource = "csv"; });

        var withNull = Detail(nullVersusNamed, tieWithNull);

        Assert.Equal("csv", withNull.Totals.EccentricityModalSource);
        Assert.Equal(0.20, withNull.Totals.AvgEccentricity!.Value, 10);
        Assert.Equal(1, withNull.Totals.EccentricityExcludedCount);

        using var namedVersusNamed = Library.Empty();
        var tieOnOrdinal = namedVersusNamed.AddGroup(
            "Tie between two named sources",
            frame => { frame.Eccentricity = 0.80; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.20; frame.EccentricitySource = "csv"; });

        var onOrdinal = Detail(namedVersusNamed, tieOnOrdinal);

        Assert.Equal("csv", onOrdinal.Totals.EccentricityModalSource);
        Assert.Equal(0.20, onOrdinal.Totals.AvgEccentricity!.Value, 10);
        Assert.Equal(1, onOrdinal.Totals.EccentricityExcludedCount);
    }

    [Fact]
    public void Get_Eccentricity_NoFrameCarriesOne_ReportsNullAndZeroExcluded()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "No eccentricity",
            frame => { frame.Eccentricity = null; frame.EccentricitySource = "header"; },
            frame => frame.Eccentricity = null);

        var detail = Detail(library, target);

        Assert.Null(detail.Totals.AvgEccentricity);
        Assert.Null(detail.Totals.EccentricityModalSource);
        Assert.Equal(0, detail.Totals.EccentricityExcludedCount);
    }

    [Fact]
    public void Get_Sessions_MedianEccentricity_PoolsOnlyTheSessionModalSource()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Mixed sources in one night",
            frame => { frame.Eccentricity = 0.30; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.31; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.32; frame.EccentricitySource = "header"; },
            frame => { frame.Eccentricity = 0.90; frame.EccentricitySource = "csv"; },
            frame => { frame.Eccentricity = 0.95; frame.EccentricitySource = "csv"; });

        var session = Assert.Single(Detail(library, target).Sessions);

        // Review ruling on spec 7.2: the card's median pools the session's modal source only.
        // Pooling all five would put the median at 0.32.
        Assert.Equal(0.31, session.MedianEccentricity!.Value, 10);
        Assert.Equal("header", session.EccentricitySource);
    }

    [Fact]
    public void Get_Sessions_MedianEccentricity_OnTheSeededLibrary_NamesTheModalSource()
    {
        using var library = Library.Seeded();

        var detail = Detail(library, LibrarySeeder.Targets[3].Id);
        var minoritySession = Assert.Single(
            detail.Sessions,
            session => session.SessionDate == LibrarySeeder.SessionWithMinorityEccentricitySource);

        // The minority frames are outnumbered inside their own session too, so the card pools and
        // labels "header" even on the one night that carries "ellipticity" frames.
        Assert.Equal(LibrarySeeder.EccentricitySourceHeader, minoritySession.EccentricitySource);
        foreach (var session in detail.Sessions)
        {
            Assert.Equal(LibrarySeeder.EccentricitySourceHeader, session.EccentricitySource);
        }
    }

    // ---- spec 7.1.1 -------------------------------------------------------------------

    [Fact]
    public void Get_NeverReadsMedianFwhm()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Header FWHM only",
            frame => { frame.MedianFwhm = 9.9; frame.Fwhm = null; },
            frame => { frame.MedianFwhm = 8.8; frame.Fwhm = null; });

        var detail = Detail(library, target);

        Assert.Null(detail.Totals.AvgFwhm);
        Assert.Null(Assert.Single(detail.Sessions).MedianFwhm);
    }

    // ---- the session overview list -----------------------------------------------------

    [Fact]
    public void Get_Sessions_AreNewestFirst()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 51");
        library.AddFrame(target.Id, new DateOnly(2025, 1, 10));
        library.AddFrame(target.Id, new DateOnly(2025, 3, 20));
        library.AddFrame(target.Id, new DateOnly(2025, 2, 14));

        var detail = Detail(library, target.Id);

        Assert.Equal(
            [new DateOnly(2025, 3, 20), new DateOnly(2025, 2, 14), new DateOnly(2025, 1, 10)],
            detail.Sessions.Select(session => session.SessionDate));
    }

    [Fact]
    public void Get_Sessions_CollapseBySessionDate()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 51");
        library.AddFrame(target.Id, Day);
        library.AddFrame(target.Id, Day);
        library.AddFrame(target.Id, Day.AddDays(1));

        var detail = Detail(library, target.Id);

        Assert.Equal(2, detail.Sessions.Count);
        Assert.Equal([1, 2], detail.Sessions.Select(session => session.FrameCount));
        Assert.Equal(
            [LibrarySeeder.ExposureSeconds, 2 * LibrarySeeder.ExposureSeconds],
            detail.Sessions.Select(session => session.IntegrationSeconds));
    }

    [Fact]
    public void Get_Sessions_FrameWithNullSessionDate_CountsInTotalsButInNoSession()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 51");
        library.AddFrame(target.Id, Day);
        library.AddFrame(target.Id, Day, frame =>
        {
            frame.SessionDate = null;
            frame.CaptureDate = null;
        });

        var detail = Detail(library, target.Id);

        // Ruling Q5: the frame is real, so it counts in the totals; it is not a session, so the
        // cards' frame counts can sum to less than the totals row's.
        Assert.Equal(2, detail.Totals.FrameCount);
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, detail.Totals.IntegrationSeconds);
        Assert.Equal(1, detail.Totals.SessionCount);
        Assert.Equal(Day, detail.Totals.FirstSessionDate);
        Assert.Equal(Day, detail.Totals.LastSessionDate);
        Assert.Equal(1, Assert.Single(detail.Sessions).FrameCount);
    }

    [Fact]
    public void Get_Sessions_RigCount_IsOneForASingleRigNight()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "One rig",
            frame => { frame.Telescope = "RC8"; frame.Camera = "ASI2600MM"; },
            frame => { frame.Telescope = "RC8"; frame.Camera = "ASI2600MM"; });

        var detail = Detail(library, target);

        var session = Assert.Single(detail.Sessions);
        Assert.Equal(1, session.RigCount);
        Assert.Equal("RC8", session.Telescope);
        Assert.Equal("ASI2600MM", session.Camera);
    }

    [Fact]
    public void Get_Sessions_RigCount_CountsDistinctCanonicalRigs()
    {
        using var library = Library.Empty();
        library.Settings.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM Pro"] = new() { Aliases = ["ZWO ASI2600MM"] },
            },
        });
        var target = library.AddGroup(
            "Two raw camera names, one canonical",
            frame => { frame.Telescope = "RC8"; frame.Camera = "zwo asi2600mm"; },
            frame => { frame.Telescope = "RC8"; frame.Camera = "ASI2600MM Pro"; },
            frame => { frame.Telescope = "FRA600"; frame.Camera = "ASI294MC"; });

        var detail = Detail(library, target);

        // The fold is load-bearing: without it the two raw camera names would count as two rigs
        // and the night would report three.
        var session = Assert.Single(detail.Sessions);
        Assert.Equal(2, session.RigCount);
    }

    [Fact]
    public void Get_Sessions_CameraAndTelescope_ComeFromTheFirstFrameByCaptureTime()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("Two rigs in one night");

        // Inserted latest first, so a query that trusted insertion order would report the wrong
        // rig.
        library.AddFrame(target.Id, Day, frame =>
        {
            frame.CaptureDate = Day.ToDateTime(new TimeOnly(23, 0));
            frame.Telescope = "FRA600";
            frame.Camera = "ASI294MC";
        });
        library.AddFrame(target.Id, Day, frame =>
        {
            frame.CaptureDate = Day.ToDateTime(new TimeOnly(20, 0));
            frame.Telescope = "RC8";
            frame.Camera = "ASI2600MM";
        });

        var detail = Detail(library, target.Id);

        var session = Assert.Single(detail.Sessions);
        Assert.Equal("RC8", session.Telescope);
        Assert.Equal("ASI2600MM", session.Camera);
        Assert.Equal(2, session.RigCount);
    }

    [Fact]
    public void Get_Sessions_HasNotes_IsTrueOnlyForADateWithASessionNoteRow()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 51");
        library.AddFrame(target.Id, Day);
        library.AddFrame(target.Id, Day.AddDays(1));
        library.AddNote(target.Id, Day.AddDays(1));

        var detail = Detail(library, target.Id);

        Assert.Equal([true, false], detail.Sessions.Select(session => session.HasNotes));
    }

    [Fact]
    public void Get_Sessions_MediansNotMeans()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Skewed",
            frame => frame.MedianHfr = 1.0,
            frame => frame.MedianHfr = 1.0,
            frame => frame.MedianHfr = 1.0,
            frame => frame.MedianHfr = 10.0);

        var detail = Detail(library, target);

        // Spec 12.4: the totals row is means, the session cards are medians. One outlier is what
        // separates them.
        Assert.Equal(1.0, Assert.Single(detail.Sessions).MedianHfr);
        Assert.Equal(3.25, detail.Totals.AvgHfr);
    }

    [Fact]
    public void Get_FiltersUsed_AreCanonicalSortedAndNullFree()
    {
        using var library = Library.Empty();
        library.Settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new() { Color = "#3ba7ff", Aliases = ["O3"] },
        });
        var target = library.AddGroup(
            "Filters",
            frame => frame.FilterUsed = "Ha",
            frame => frame.FilterUsed = "o3",
            frame => frame.FilterUsed = "OIII",
            frame => frame.FilterUsed = null,
            frame => frame.FilterUsed = "");

        var detail = Detail(library, target);

        // No "Unknown" bucket here, unlike the dashboard palette: a frame with no FILTER card
        // used no filter.
        Assert.Equal(["Ha", "OIII"], detail.Totals.FiltersUsed);
        Assert.Equal(["Ha", "OIII"], Assert.Single(detail.Sessions).FiltersUsed);
    }

    [Fact]
    public void Get_Equipment_IsTheDistinctCanonicalTelescopeAndCameraNames()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "Two rigs",
            frame => { frame.Telescope = "RC8"; frame.Camera = "ASI2600MM"; },
            frame => { frame.Telescope = "RC8"; frame.Camera = "ASI2600MM"; },
            frame => { frame.Telescope = "FRA600"; frame.Camera = "ASI294MC"; },
            frame => { frame.Telescope = null; frame.Camera = null; });

        var detail = Detail(library, target);

        // A flat set of names, each as its own entry, not the dashboard's "telescope / camera"
        // rig strings.
        Assert.Equal(["ASI2600MM", "ASI294MC", "FRA600", "RC8"], detail.Totals.Equipment);
    }

    // ---- group keys --------------------------------------------------------------------

    [Fact]
    public void Get_UnresolvedObjectGroup_ResolvesByGroupKey()
    {
        using var library = Library.Empty();
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Sh2-155"));
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject("Sh2-155"));

        var detail = library.Query.Get("obj:Sh2-155");

        Assert.NotNull(detail);
        Assert.Equal("obj:Sh2-155", detail.Header.GroupKey);
        Assert.Null(detail.Header.TargetId);
        Assert.Equal("Sh2-155", detail.Header.PrimaryName);
        Assert.Equal("Unresolved", detail.Header.ObjectCategory);
        Assert.Null(detail.Header.ObjectType);
        Assert.Empty(detail.Header.Aliases);
        Assert.Empty(detail.Header.CatalogMemberships);
        Assert.Equal(2, detail.Totals.FrameCount);
        Assert.False(Assert.Single(detail.Sessions).HasNotes);
    }

    [Fact]
    public void Get_UncategorizedGroup_ResolvesByGroupKey()
    {
        using var library = Library.Empty();
        library.AddFrame(null, Day, frame => frame.RawHeaders = LibrarySeeder.RawHeadersWithObject(""));
        library.AddFrame(null, Day, frame => frame.RawHeaders = """{"EXPTIME": 300}""");

        var detail = library.Query.Get("obj:__uncategorized__");

        Assert.NotNull(detail);
        Assert.Equal("Uncategorized", detail.Header.PrimaryName);
        Assert.Equal(2, detail.Totals.FrameCount);
    }

    [Fact]
    public void Get_NumericObjectCard_MatchesItsGroupKey()
    {
        using var library = Library.Empty();
        library.AddFrame(null, Day, frame => frame.RawHeaders = """{"OBJECT": 7331}""");

        // Phase 5 fix F1 at this query: json_extract hands back a NUMBER, and only comparing the
        // concatenated group-key expression forces both sides to text.
        var detail = library.Query.Get("obj:7331");

        Assert.NotNull(detail);
        Assert.Equal("7331", detail.Header.PrimaryName);
        Assert.Equal(1, detail.Totals.FrameCount);
    }

    [Fact]
    public void Get_UnknownGroupKey_ReturnsNull()
    {
        using var library = Library.Seeded();

        Assert.Null(library.Query.Get(Guid.NewGuid().ToString()));
        Assert.Null(library.Query.Get("obj:never imaged"));
        Assert.Null(library.Query.Get("not a key at all"));
    }

    [Fact]
    public void Get_MergedTarget_ReturnsNull()
    {
        using var library = Library.Empty();
        var winner = library.AddTarget("M 81");
        var loser = library.AddTarget("M 81 dup", target => target.MergedIntoId = winner.Id);
        library.AddFrame(loser.Id, Day);

        // Ruling Q4: the caller renders an error state rather than silently following the merge.
        Assert.Null(library.Query.Get(loser.Id.ToString()));
    }

    // ---- MergedInto: the merged-away companion (FIXER LIST item 14, ruling Q12) ---------

    [Fact]
    public void MergedInto_MergedAwayTarget_NamesTheWinner()
    {
        using var library = Library.Empty();
        var winner = library.AddTarget("M 81");
        var loser = library.AddTarget("M 81 dup", target => target.MergedIntoId = winner.Id);
        library.AddFrame(loser.Id, Day);

        var key = loser.Id.ToString();

        // Get is unchanged: the page is never silently redirected to the winner (spec 12.10).
        Assert.Null(library.Query.Get(key));

        var mergedInto = library.Query.MergedInto(key);
        Assert.NotNull(mergedInto);
        Assert.Equal(winner.Id, mergedInto!.Value.TargetId);
        Assert.Equal("M 81", mergedInto.Value.PrimaryName);

        // Phase 7 FIXER item 18: the merged-away target's own name, because Get returns no header
        // for it and the page's title line has nothing else to show.
        Assert.Equal("M 81 dup", mergedInto.Value.LoserName);
    }

    [Fact]
    public void MergedInto_ActiveTarget_ReturnsNull()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("M 81");
        library.AddFrame(target.Id, Day);

        Assert.Null(library.Query.MergedInto(target.Id.ToString()));
    }

    [Fact]
    public void MergedInto_UnresolvedKey_ReturnsNull()
    {
        using var library = Library.Empty();

        Assert.Null(library.Query.MergedInto("obj:NGC 7331 field"));
        Assert.Null(library.Query.MergedInto("not a key at all"));
    }

    [Fact]
    public void MergedInto_UnknownKey_ReturnsNull()
    {
        using var library = Library.Empty();

        Assert.Null(library.Query.MergedInto(Guid.NewGuid().ToString()));
    }

    // ---- frame scope -------------------------------------------------------------------

    [Fact]
    public void Get_CalibrationFrames_AreExcluded()
    {
        using var library = Library.Empty();
        var target = library.AddGroup(
            "With calibration",
            frame => frame.MedianHfr = 2.0,
            frame => { frame.ImageType = "DARK"; frame.MedianHfr = 9.0; },
            frame => { frame.ImageType = "FLAT"; frame.MedianHfr = 9.0; },
            frame => { frame.ImageType = null; frame.MedianHfr = 9.0; });

        var detail = Detail(library, target);

        Assert.Equal(1, detail.Totals.FrameCount);
        Assert.Equal(LibrarySeeder.ExposureSeconds, detail.Totals.IntegrationSeconds);
        Assert.Equal(2.0, detail.Totals.AvgHfr);
        Assert.Single(detail.FramePaths);
    }

    [Fact]
    public void Get_FramePaths_AreInCaptureOrder()
    {
        using var library = Library.Empty();
        var target = library.AddTarget("Ordered");
        var late = library.AddFrame(target.Id, Day, frame =>
            frame.CaptureDate = Day.ToDateTime(new TimeOnly(23, 30)));
        var early = library.AddFrame(target.Id, Day, frame =>
            frame.CaptureDate = Day.ToDateTime(new TimeOnly(20, 15)));
        var middle = library.AddFrame(target.Id, Day.AddDays(1), frame =>
            frame.CaptureDate = Day.ToDateTime(new TimeOnly(22, 0)));

        var detail = Detail(library, target.Id);

        Assert.Equal([early.FilePath, middle.FilePath, late.FilePath], detail.FramePaths);
    }

    // ---- budget ------------------------------------------------------------------------

    [Fact]
    public void Get_OnTheSeededLibrary_CompletesUnderABudget()
    {
        using var library = Library.Seeded();
        var seeded = LibrarySeeder.Targets[0];
        var key = seeded.Id.ToString();

        // Warm the connection pool and the alias map so the figure measures the query.
        library.Query.Get(key);

        var best = double.MaxValue;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            library.Query.Get(key);
            stopwatch.Stop();
            best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
        }

        output.WriteLine(
            $"seeded {seeded.FrameCount} frames / {seeded.SessionCount} sessions: detail {best:F1} ms");

        // Deliberately loose, like the listing query's budget: this catches an accidental N+1 or
        // a cartesian join, not a slow build agent.
        Assert.True(best < 500, $"target detail took {best:F1} ms");
    }
}
