using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using GalactiLog.Data.Tests.Phd2;
using GalactiLog.Data.Tests.TestSupport;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// <see cref="GuidingStatsQuery"/>, the port of <c>services/phd2_stats.py</c>: the rig rows, the
/// altitude band rows, the unmapped count and the cross-rig baselines.
/// </summary>
/// <remarks>
/// Every case builds its own <c>phd2_sessions</c> rows, the idiom <c>Phd2RepositoryTests</c> uses.
/// No case reads the guide-log fixture under <c>C:\tmp</c>: an eight-rig baseline, a zero RA RMS
/// and two raw spellings of one scope are shapes no generated log carries, and a figure asserted
/// against a file this class cannot see is a figure a reader cannot check.
/// </remarks>
public class GuidingStatsQueryTests
{
    // ---- 6.1 the empty database --------------------------------------------------------

    [Fact]
    public void EmptyDatabase_ReturnsEmptyRowsAndEmptyBaselines_AndNeverNull()
    {
        using var library = new Library();

        var result = library.Query.Get();

        // Each of the four members explicitly, not one Assert.NotNull: the failure this guards
        // against is Rigs coming back null, which a view binds silently and then never renders an
        // empty notice for, and a baseline builder that assumed a median over an empty sequence
        // has a value.
        Assert.Equal(0, result.UnmappedSessionCount);
        Assert.Empty(result.Rigs);
        Assert.Empty(result.AltitudeBands);
        Assert.Equal(new MetricBaseline(null, null, 0), result.Baselines.RmsTotal);
        Assert.Equal(new MetricBaseline(null, null, 0), result.Baselines.RmsRa);
        Assert.Equal(new MetricBaseline(null, null, 0), result.Baselines.RmsDec);
    }

    // ---- 6.2 the unmapped count --------------------------------------------------------

    [Fact]
    public void ASessionWithNoTelescope_CountsAsUnmapped_AndJoinsNoRig()
    {
        using var library = new Library();
        library.AddSession(session => session.Telescope = null);
        library.AddSession(session => session.Telescope = "Rig A");
        library.AddSession(session => session.Telescope = "Rig A");

        var result = library.Query.Get();

        // The failure guarded against is the unmapped session folding into a rig row under an
        // empty-string key, which puts a nameless row on the scorecard and understates the notice
        // that tells the user to map the profile.
        Assert.Equal(1, result.UnmappedSessionCount);
        Assert.Equal("Rig A", Assert.Single(result.Rigs).Telescope);
        Assert.Equal(2, result.Rigs[0].SessionCount);
    }

    [Fact]
    public void ALibraryWhoseEveryProfileIsUnmapped_ReturnsTheCountAndNoRig()
    {
        using var library = new Library();
        library.AddSession(session => session.Telescope = null);

        var result = library.Query.Get();

        // The state every fresh install and every bare CLI scan is in, and the state the page's
        // empty notice renders for.
        Assert.Equal(1, result.UnmappedSessionCount);
        Assert.Empty(result.Rigs);
        Assert.Empty(result.AltitudeBands);
    }

    // ---- 6.2b the live profile map (task5b-review.md P2-1) ------------------------------
    //
    // Every case here catalogues the session FIRST and maps the profile SECOND, which is the order
    // the defect lives in and the order the empty notice's own "Map profiles" link produces. The
    // defect: the query read phd2_sessions.telescope, which only the ingest writes, so a mapping
    // made after the scan reached the page never, and a re-scan did not repair it either because
    // the guide-log pass skips an unchanged log.

    [Fact]
    public void ASessionWithNoStoredTelescope_WhoseProfileIsMappedAfterTheScan_JoinsThatRig()
    {
        using var library = new Library();
        library.AddSession(session =>
        {
            session.EquipmentProfile = "TestScope_TestCam";
            session.Telescope = null;
        });

        // The state the scan left: catalogued, unmapped, and the empty notice showing.
        Assert.Equal(1, library.Query.Get().UnmappedSessionCount);

        library.MapProfile("TestScope_TestCam", "Askar 120");

        var result = library.Query.Get();

        // Against the stored column this reads 1 and no rig, which is the user following the
        // notice's own link, mapping the profile, coming back and seeing the same notice.
        Assert.Equal(0, result.UnmappedSessionCount);
        Assert.Equal("Askar 120", Assert.Single(result.Rigs).Telescope);
        Assert.Equal(1, result.Rigs[0].SessionCount);
    }

    [Fact]
    public void ASessionWhoseProfileTheMapDoesNotCarry_JoinsTheUnmappedTally()
    {
        using var library = new Library();
        library.AddSession(session =>
        {
            session.EquipmentProfile = "Retired_Cam";
            session.Telescope = "Rig A";
        });
        library.MapProfile("SomeOtherProfile", "Askar 120");

        var result = library.Query.Get();

        // REWRITTEN for phase-review.md F3. It used to expect the stored "Rig A" to survive, and
        // a tally of zero, on the strength of a fallback that made the scorecard unable to show
        // an unmapping at all: the Equipment tab unmaps by DROPPING the entry, so "the map says
        // nothing about this profile" is precisely the unmapped state. A reader who unmapped a
        // rig went on seeing its row, with its figures, on every visit.
        Assert.Equal(1, result.UnmappedSessionCount);
        Assert.Empty(result.Rigs);
    }

    [Fact]
    public void AProfileTheReaderUnmaps_LeavesItsRigAndJoinsTheUnmappedTally()
    {
        // F3 end to end through the members the Equipment tab writes with, and the mirror of the
        // mapping case above: the empty notice's round trip has to work in both directions or
        // "Map profiles" is the only settings change the page can ever see.
        using var library = new Library();
        library.AddSession(session =>
        {
            session.EquipmentProfile = "TestScope_TestCam";
            session.Telescope = "Rig A";
        });
        library.MapProfile("TestScope_TestCam", "Rig A");

        Assert.Equal("Rig A", Assert.Single(library.Query.Get().Rigs).Telescope);

        library.MapProfile("TestScope_TestCam", null);

        var result = library.Query.Get();

        Assert.Empty(result.Rigs);
        Assert.Equal(1, result.UnmappedSessionCount);
    }

    [Fact]
    public void ARemapFromOneRigToAnother_MovesTheSession()
    {
        using var library = new Library();
        library.AddSession(session =>
        {
            session.EquipmentProfile = "TestScope_TestCam";
            session.Telescope = "Rig A";
        });
        library.MapProfile("TestScope_TestCam", "Rig B");

        var rig = Assert.Single(library.Query.Get().Rigs);

        // Spec 12.5's Query paragraph in as many words: a remapped profile moves a session from
        // one rig to another. Against the stored column this row still reads "Rig A".
        Assert.Equal("Rig B", rig.Telescope);
    }

    // ---- 6.3 alias folding -------------------------------------------------------------

    [Fact]
    public void TwoRawSpellingsOfOneScope_FoldOntoOneCanonicalRow()
    {
        using var library = new Library();
        library.MapTelescope("RC8", "GSO RC8");
        library.AddSession(session => session.Telescope = "RC8");
        library.AddSession(session => session.Telescope = "GSO RC8");

        var rig = Assert.Single(library.Query.Get().Rigs);

        // The failure guarded against is two rows, one per spelling, each with half the sessions
        // and each graded against the other.
        Assert.Equal("RC8", rig.Telescope);
        Assert.Equal(2, rig.SessionCount);
    }

    // ---- 6.4 the gate, and what it does and does not gate -------------------------------

    [Fact]
    public void TheHundredFrameGate_ExcludesASessionFromTheRmsAndFromNothingElse()
    {
        using var library = new Library();
        library.AddSession(session => Measured(session, frames: 99, rmsTotal: 1.500000,
            rmsTotalFiltered: 1.200000, durationS: 100, exposureMs: 500, settleMedianS: 4.0));
        library.AddSession(session => Measured(session, frames: 400, rmsTotal: 0.750000,
            rmsTotalFiltered: 0.600000, durationS: 300, exposureMs: 1000, settleMedianS: 2.0));

        var rig = Assert.Single(library.Query.Get().Rigs);

        Assert.Equal(2, rig.SessionCount);
        Assert.Equal(1, rig.GatedSessionCount);

        // If the gate were missing from the RMS this would read 0.947256 rather than 0.750000:
        // the numerator is 99 x 1.5^2 + 400 x 0.75^2 = 447.75 over a denominator of 499, and the
        // square root of 0.897294 is 0.947256 at six decimals.
        Assert.Equal(0.750000, rig.RmsTotalArcsec!.Value, 6);

        // The fourth weighted figure, over the filtered column rather than the total one, and the
        // only member of the record the rest of the solution never reads. The seed values are
        // deliberately distinct from the totals in both rows: a projection or an accessor that
        // reached for the unfiltered member would read 0.750000 here, and a gate that leaked into
        // this figure as well would read 0.757805 over both rows.
        Assert.Equal(0.600000, rig.RmsTotalFilteredArcsec!.Value, 6);

        // And if the gate had leaked into the sums, these three would read 0.08, [1000] and 2.0,
        // each of which is a real number that no assertion on the RMS alone would catch. The
        // figure is (100 + 300) / 3600 rounded to 2.
        Assert.Equal(0.11, rig.GuidedHours);
        Assert.Equal(new[] { 500, 1000 }, rig.ExposureMsValues);
        Assert.Equal(3.0, rig.SettleMedianS!.Value, 6);
    }

    [Fact]
    public void ASessionAtExactlyOneHundredFrames_IsNotGated_AndItsRmsCounts()
    {
        using var library = new Library();
        library.AddSession(session => Measured(session, frames: Phd2Metrics.MinFrames,
            rmsTotal: 0.500000, durationS: 100, exposureMs: 500, settleMedianS: 1.0));

        var rig = Assert.Single(library.Query.Get().Rigs);

        // The comparison is strictly less than MinFrames, so the boundary session is kept.
        Assert.Equal(0, rig.GatedSessionCount);
        Assert.Equal(0.500000, rig.RmsTotalArcsec!.Value, 6);
    }

    // ---- 6.5 the Dec-to-RA ratio, including the zero -------------------------------------

    [Fact]
    public void TheRatio_IsNull_ForAZeroRaRms_AndForANullDecRms()
    {
        using var library = new Library();
        library.AddSession(session =>
        {
            Measured(session, frames: 400, rmsTotal: 0.750000, durationS: 300, exposureMs: 500);
            session.Telescope = "Rig A";
            session.RmsRaArcsec = 0.600000;
            session.RmsDecArcsec = 0.450000;
        });
        library.AddSession(session =>
        {
            Measured(session, frames: 400, rmsTotal: 0.450000, durationS: 300, exposureMs: 500);
            session.Telescope = "Rig B";
            session.RmsRaArcsec = 0.0;
            session.RmsDecArcsec = 0.450000;
        });
        library.AddSession(session =>
        {
            Measured(session, frames: 400, rmsTotal: 0.600000, durationS: 300, exposureMs: 500);
            session.Telescope = "Rig C";
            session.RmsRaArcsec = 0.600000;
            session.RmsDecArcsec = null;
        });

        var rigs = library.Query.Get().Rigs;

        Assert.Equal(0.75, rigs[0].RaDecRatio!.Value, 6);

        // The failure guarded against is the second rig reading double.PositiveInfinity, which is
        // what a null check alone produces and what renders as a garbage cell rather than the
        // missing glyph. Python's "if ra" is false for a zero as well as for a null.
        Assert.Equal(0.0, rigs[1].RmsRaArcsec!.Value, 6);
        Assert.Null(rigs[1].RaDecRatio);

        Assert.Null(rigs[2].RaDecRatio);
    }

    // ---- 6.6 the altitude bands -----------------------------------------------------------

    [Fact]
    public void TheBandBoundaries_AreExact_AndANullAltitudeJoinsNoBand()
    {
        using var library = new Library();
        library.AddSession(session => Banded(session, altDeg: 29.9, rmsTotal: 1.100000));
        library.AddSession(session => Banded(session, altDeg: 30.0, rmsTotal: 1.200000));
        library.AddSession(session => Banded(session, altDeg: 59.9, rmsTotal: 1.300000));
        library.AddSession(session => Banded(session, altDeg: 60.0, rmsTotal: 1.400000));

        var bands = library.Query.Get().AltitudeBands;

        // A boundary written as <= in either place moves 30.0 into Below30 or 60.0 into
        // From30To60, and these three counts distinguish both.
        Assert.Equal(3, bands.Count);
        Assert.Equal(GuidingAltitudeBand.Below30, bands[0].Band);
        Assert.Equal(1, bands[0].SessionCount);
        Assert.Equal(GuidingAltitudeBand.From30To60, bands[1].Band);
        Assert.Equal(2, bands[1].SessionCount);
        Assert.Equal(GuidingAltitudeBand.Above60, bands[2].Band);
        Assert.Equal(1, bands[2].SessionCount);

        library.AddSession(session => Banded(session, altDeg: null, rmsTotal: 1.500000));
        var after = library.Query.Get();

        // A null altitude bucketed as 0 would land in Below30 and silently claim the rig guided
        // below the horizon.
        Assert.Equal(5, Assert.Single(after.Rigs).SessionCount);
        Assert.Equal(
            bands.Select(band => (band.Band, band.SessionCount)),
            after.AltitudeBands.Select(band => (band.Band, band.SessionCount)));
    }

    [Fact]
    public void ABandWhoseOnlySessionIsGated_KeepsItsRow_WithThreeNullRmsFigures()
    {
        using var library = new Library();
        library.AddSession(session =>
        {
            Banded(session, altDeg: 10.0, rmsTotal: 1.000000);
            session.FrameCount = 50;
        });

        var band = Assert.Single(library.Query.Get().AltitudeBands);

        // The row is a real shape and must be returned rather than dropped: the arc has to be able
        // to say "one session, no figure" instead of "no session".
        Assert.Equal(GuidingAltitudeBand.Below30, band.Band);
        Assert.Equal(1, band.SessionCount);
        Assert.Null(band.RmsTotalArcsec);
        Assert.Null(band.RmsRaArcsec);
        Assert.Null(band.RmsDecArcsec);
    }

    // ---- 6.7 ordering ----------------------------------------------------------------------

    [Fact]
    public void RigsSortOrdinal_AndBandsSortByTelescopeThenByBandDeclarationOrder()
    {
        using var library = new Library();

        // Inserted in the order that passes for the wrong reason if either sort is missing: the
        // second rig first, and that rig's highest band first.
        library.AddSession(session => Banded(session, altDeg: 70.0, rmsTotal: 1.000000, telescope: "B Rig"));
        library.AddSession(session => Banded(session, altDeg: 10.0, rmsTotal: 1.000000, telescope: "B Rig"));
        library.AddSession(session => Banded(session, altDeg: 40.0, rmsTotal: 1.000000, telescope: "B Rig"));
        library.AddSession(session => Banded(session, altDeg: 40.0, rmsTotal: 1.000000, telescope: "A Rig"));

        var result = library.Query.Get();

        Assert.Equal(new[] { "A Rig", "B Rig" }, result.Rigs.Select(rig => rig.Telescope));
        Assert.Equal(
            new[]
            {
                ("A Rig", GuidingAltitudeBand.From30To60),
                ("B Rig", GuidingAltitudeBand.Below30),
                ("B Rig", GuidingAltitudeBand.From30To60),
                ("B Rig", GuidingAltitudeBand.Above60),
            },
            result.AltitudeBands.Select(band => (band.Telescope, band.Band)));
    }

    // ---- 6.8 the baselines, and one-rig neutrality -------------------------------------------

    [Fact]
    public void TheBaselines_AreTheMedianAndMadAcrossRigs_AndGradeNothingBelowMinGroup()
    {
        using var library = new Library();
        library.AddSession(session => Banded(session, altDeg: 40.0, rmsTotal: 0.500000, telescope: "A Rig"));
        library.AddSession(session => Banded(session, altDeg: 40.0, rmsTotal: 1.500000, telescope: "B Rig"));

        var result = library.Query.Get();

        Assert.Equal(2, result.Baselines.RmsTotal.N);
        Assert.Equal(1.0, result.Baselines.RmsTotal.Median!.Value, 6);
        Assert.Equal(0.5, result.Baselines.RmsTotal.Mad!.Value, 6);

        // Ruling G4, proved in the Data layer because it needs no view and will still be true when
        // the cells are built: N is below FrameQuality.MinGroup, so every cell is neutral. The
        // gate itself is not pinned here; its home is Core.Tests/Metrics/FrameQualityTests.cs:141,
        // which asserts MinGroup is 8 and drives the gate directly. What this line demonstrates is
        // that a two-rig library reaches that gate with an N the gate refuses.
        Assert.Null(FrameQuality.MadZ(result.Rigs[0].RmsTotalArcsec, result.Baselines.RmsTotal));
        Assert.Null(FrameQuality.MadZ(result.Rigs[1].RmsTotalArcsec, result.Baselines.RmsTotal));
    }

    [Fact]
    public void ARigWithNoRms_DoesNotRaiseTheBaselineCount()
    {
        using var library = new Library();
        library.AddSession(session => Banded(session, altDeg: 40.0, rmsTotal: 0.500000, telescope: "A Rig"));
        library.AddSession(session => Banded(session, altDeg: 40.0, rmsTotal: 1.500000, telescope: "B Rig"));
        library.AddSession(session =>
        {
            Banded(session, altDeg: 40.0, rmsTotal: 9.000000, telescope: "C Rig");
            session.FrameCount = 50;
        });

        var result = library.Query.Get();

        Assert.Equal(3, result.Rigs.Count);
        Assert.Null(result.Rigs[2].RmsTotalArcsec);
        Assert.Equal(2, result.Baselines.RmsTotal.N);
    }

    [Fact]
    public void WithEightRigsOfWhichThreeCarryNoRms_TheBaselineCountIsFive()
    {
        using var library = new Library();
        for (var index = 0; index < 8; index++)
        {
            var graded = index < 5;
            library.AddSession(session =>
            {
                Banded(session, altDeg: 40.0, rmsTotal: 0.500000 + index, telescope: $"Rig {index}");
                if (!graded)
                {
                    session.FrameCount = 50;
                }
            });
        }

        var baseline = library.Query.Get().Baselines.RmsTotal;

        // The failure guarded against is N being set to Rigs.Count, which MetricBaseline's own
        // summary warns about: an N of 8 here would grade three ungraded rigs against a five-value
        // median and call it robust.
        Assert.Equal(5, baseline.N);
    }

    // ---- 6.10 the guided hours accumulate as the Python's sum() does ---------------------------

    [Fact]
    public void GuidedHours_AccumulateCompensated_AsTheBuiltinSumDoes()
    {
        // phd2_stats.py:57 is round(sum(r.duration_s or 0.0 for r in rows) / 3600, 2), and CPython
        // 3.12, which the backend requires, gives the builtin sum() Neumaier compensation over
        // floats. Enumerable.Sum accumulates left to right and is a different double.
        //
        // These six durations are ordinary session lengths, two minutes to thirty-five minutes,
        // whose exact decimal total is 5382.000 seconds, which is 1.495 hours exactly and
        // therefore sits on the boundary the second decimal rounds at. Compensated they total
        // 5382.0 in every one of the 720 orders and the row reads 1.5 hours. Left to right they
        // total 5381.999999999999 in this order, and in 308 of the 720, and the row reads 1.49.
        double[] durations = [728.445, 2102.892, 293.522, 1958.394, 177.199, 121.548];

        using var library = new Library();
        foreach (var duration in durations)
        {
            library.AddSession(session => Measured(
                session, frames: 400, rmsTotal: 0.750000, durationS: duration, exposureMs: 500));
        }

        var rig = Assert.Single(library.Query.Get().Rigs);

        Assert.Equal(6, rig.SessionCount);
        Assert.Equal(1.5, rig.GuidedHours);
    }

    // ---- 6.9 no second copy -------------------------------------------------------------------

    [Fact]
    public void TheQuerySource_HoldsNoSecondWeightedRms_NoFrameRead_AndNoWrite()
    {
        var code = SourceScan.Read("src/GalactiLog.Data/Queries/GuidingStatsQuery.cs");

        // A helpful inline weighted RMS written because the Core member did not fit on the day
        // passes every figure case above and drifts the first time the Core one is tuned.
        Assert.DoesNotContain("Math.Sqrt", code, StringComparison.Ordinal);

        // Rule G6: every parity rounding goes through RoundLikePython and nothing else.
        Assert.DoesNotContain("Math.Round", code, StringComparison.Ordinal);
        Assert.Contains("RoundLikePython", code, StringComparison.Ordinal);

        // Every figure here is already an aggregate on the session row. A later change that
        // reaches for a frame would be correct and would cost a scan of 1.2 million rows on every
        // Statistics load.
        Assert.DoesNotContain("phd2_frames", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Phd2Frames", code, StringComparison.Ordinal);

        // The Statistics page reads and never writes.
        Assert.DoesNotContain("ExecuteUpdate", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteDelete", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveChanges", code, StringComparison.Ordinal);
    }

    // ---- shared shapes -------------------------------------------------------------------------

    /// <summary>A session carrying the figures the rig row sums and weights. Leaves the RA and Dec
    /// columns alone; the cases that read them set them.</summary>
    /// <param name="rmsTotalFiltered">Set it to something other than <paramref name="rmsTotal"/> in
    /// any case that asserts it: the two columns sit next to each other in the projection and in
    /// the four weighted calls, so equal values would hide a swap.</param>
    private static void Measured(
        Phd2Session session,
        int frames,
        double? rmsTotal,
        double durationS,
        double? exposureMs,
        double? settleMedianS = null,
        double? rmsTotalFiltered = null)
    {
        session.Telescope = "Rig A";
        session.FrameCount = frames;
        session.RmsTotalArcsec = rmsTotal;
        session.RmsTotalFilteredArcsec = rmsTotalFiltered;
        session.DurationS = durationS;
        session.ExposureMs = exposureMs;
        session.SettleMedianS = settleMedianS;
    }

    /// <summary>A session over the gate at one altitude, for the band and baseline cases.</summary>
    private static void Banded(
        Phd2Session session,
        double? altDeg,
        double rmsTotal,
        string telescope = "Rig A")
    {
        session.Telescope = telescope;
        session.AltDeg = altDeg;
        session.FrameCount = 400;
        session.RmsTotalArcsec = rmsTotal;
        session.RmsRaArcsec = rmsTotal;
        session.RmsDecArcsec = rmsTotal;
    }

    /// <summary>A migrated database with one guide-log row to hang sessions off, the alias map
    /// cache and the query under test. Mirrors <c>SessionDetailRigTests.Library</c>.</summary>
    private sealed class Library : IDisposable
    {
        private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
        private readonly SettingsStore _settings;
        private readonly AliasMapCache _aliases;
        private readonly Guid _logId = Guid.NewGuid();
        private int _sectionIndex;

        public Library()
        {
            _settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
            _aliases = new AliasMapCache(_settings);
            Query = new GuidingStatsQuery(
                new DatabaseConnectionString(_db.ConnectionString),
                _aliases,
                () => _settings.GetGeneral().Phd2ProfileMap);

            using var context = Phd2SchemaTests.Open(_db);
            context.Phd2Logs.Add(Phd2SchemaTests.Log(_logId, @"C:\Astro\guide\PHD2_GuideLog_0001.txt"));
            context.SaveChanges();
        }

        public GuidingStatsQuery Query { get; }

        /// <summary>
        /// One <c>phd2_sessions</c> row. A seed that names a <c>Telescope</c> and leaves the
        /// profile alone means "this session's rig is that telescope", and since
        /// phase-review.md F3 the LIVE MAP is the only thing that can say so: the stored column
        /// is the ingest's own record and no reader consults it. Such a seed is therefore given
        /// an equipment profile of its own and the map is pointed at it, which is the state an
        /// ingest under that map really leaves behind.
        /// </summary>
        /// <remarks>
        /// A case that sets <c>EquipmentProfile</c> itself is exercising the map and is left
        /// exactly as written, which is what section 6.2b's cases do. The stored column is still
        /// written, deliberately: it is what the ingest writes, and a reader that started
        /// consulting it again would pass under this seed and fail its own cases.
        /// </remarks>
        public void AddSession(Action<Phd2Session> configure)
        {
            using var context = Phd2SchemaTests.Open(_db);
            var session = Phd2SchemaTests.Session(Guid.NewGuid(), _logId);
            session.SectionIndex = _sectionIndex++;
            var seeded = session.EquipmentProfile;
            configure(session);

            var rig = session.Telescope;
            var mapIt = !string.IsNullOrEmpty(rig)
                && string.Equals(session.EquipmentProfile, seeded, StringComparison.Ordinal);
            if (mapIt)
            {
                session.EquipmentProfile = $"{rig}_Cam";
            }

            context.Phd2Sessions.Add(session);
            context.SaveChanges();

            if (mapIt)
            {
                MapProfile(session.EquipmentProfile!, rig!);
            }
        }

        /// <summary>Configures one canonical telescope with its raw spellings and lets
        /// <see cref="AliasMapCache"/> rebuild through the event it already subscribes to.</summary>
        public void MapTelescope(string canonical, params string[] rawSpellings)
            => _settings.SaveEquipment(new EquipmentSettings
            {
                Telescopes = new Dictionary<string, EquipmentItemSettings>
                {
                    [canonical] = new() { Aliases = rawSpellings },
                },
            });

        /// <summary>Maps one PHD2 equipment profile to a telescope in
        /// <c>general.phd2_profile_map</c>, the way the Equipment tab's panel writes it. Called
        /// after the sessions are added, deliberately: that is the order the defect lives in, a
        /// library catalogued first and mapped afterwards.</summary>
        public void MapProfile(string profile, string? telescope)
            => _settings.MutateGeneral(general => general with
            {
                Phd2ProfileMap = Phd2Profiles.ToJson(
                    Phd2Profiles.SetTelescope(general.Phd2ProfileMap, profile, telescope)),
            });

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }
}
